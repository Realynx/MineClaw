using System.Text.Json;
using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Db;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Clone;

/// <summary>The game/agent side of a clone process (real: the App's clone body; tests: a fake).</summary>
public interface ICloneBody
{
    string Name { get; }
    bool IsPlaying { get; }
    IInventoryPort Inventory { get; }
    BotSnapshot Snapshot(bool full);
    event Action<PromptUsageRecord>? UsageRecorded;
    /// <summary>Raised with the cause when the bot dies in game.</summary>
    event Action<string>? Died;

    /// <summary>Connect to the server (retrying) until in game or cancelled.</summary>
    Task ConnectAsync(CancellationToken ct);
    /// <summary>Start the brain loop (after any stranded-inventory deposit).</summary>
    void StartAgent(CancellationToken ct);
    /// <summary>Stop the brain and any running activity (before depositing / logging out).</summary>
    Task StopAgentAsync();
    /// <summary>Hand a task to the brain (wakes it).</summary>
    void AssignTask(TaskMsg task);
    /// <summary>A TUI console line for this clone.</summary>
    Task<bool> ExecuteAsync(string line, CancellationToken ct);
    /// <summary>A console /as test line forwarded by main (same line every process arbitrates).</summary>
    void InjectChat(string raw);
    Task DisconnectAsync(string reason);
}

public static class CloneExitCodes
{
    public const int Ok = 0;
    /// <summary>No parent pipe to connect to: a clone never runs unsupervised.</summary>
    public const int NoParent = 75;
}

/// <summary>
/// Runs one clone process under the main process's supervision: connects the parent pipe (or exits — never unsupervised),
/// streams snapshots/usage/inventory, heartbeats both ways, applies roster/claims pushes, and owns the logout gate:
/// every planned logout (task done, recall, budget, making room, main shutting down) first deposits the ENTIRE inventory
/// and logs out only once it's verified empty; if the main process vanishes it deposits only if it can within
/// <see cref="HiveConfig.ParentLossDepositSeconds"/>, records anything left as stranded, and logs out.
/// </summary>
public sealed class CloneHost : IHandoffTarget
{
    private readonly string _pipe;
    private readonly HiveConfig _cfg;
    private readonly ILogger _log;
    private ICloneBody? _bodyRef;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private HiveLink? _link;
    private int _loggingOut;
    private TaskMsg? _task;
    private string _lastInv = "";
    private List<ItemMsg> _inventory = [];

    /// <summary>The body and store can be attached after construction (the bot is built with this host's claim board and arbiter).</summary>
    public CloneHost(string name, string pipe, HiveConfig cfg, ICloneBody? body = null, HiveStore? store = null, ILogger? log = null)
    {
        Name = name;
        _pipe = pipe;
        _bodyRef = body;
        _cfg = cfg;
        Store = store;
        _log = log ?? NullLogger.Instance;
        Claims = new RemoteClaimBoard(name, () => _link);
        Arbiter = new ChatArbiter(name, "main", cfg.Clones(), sticky: TimeSpan.FromSeconds(cfg.StickySeconds));
        // This clone asked a player something / watches their demo: main (the arbiter authority) and the other clones must know.
        Arbiter.LocalThreadChanged += (player, open, ttl) => _ = Send(Msg.Thread, new ThreadMsg(player, open, ttl.TotalSeconds));
    }

    public string Name { get; }
    public ICloneBody Body { get => _bodyRef ?? throw new InvalidOperationException("clone body not attached"); set => _bodyRef = value; }
    private ICloneBody _body => Body;
    public HiveStore? Store { get; set; }
    private HiveStore? _store => Store;
    public RemoteClaimBoard Claims { get; }
    public ChatArbiter Arbiter { get; }
    public HiveLink? Link => _link;
    public string? Main { get; private set; }
    public TaskMsg? CurrentTask => _task;
    public bool LoggingOut => Volatile.Read(ref _loggingOut) != 0;
    public string? LogoutReason { get; private set; }
    public DepositReport? LastDeposit { get; private set; }

    /// <summary>Records a shared experience once (key, text): set by the body (hive_events + a RAG doc in the shared db).</summary>
    public Func<string, string, bool>? RecordExperience { get; set; }

    /// <summary>Connects the pipe and says hello; false = no parent (exit <see cref="CloneExitCodes.NoParent"/>).</summary>
    public async Task<WelcomeMsg?> ConnectParentAsync(TimeSpan timeout, CancellationToken ct)
    {
        var client = PipeNames.Client(_pipe);
        try { await client.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            _log.LogError("clone {Name}: no parent pipe {Pipe} ({Error}); a clone never runs unsupervised", Name, _pipe, ex.Message);
            client.Dispose();
            return null;
        }
        var link = new HiveLink(client, "main", _log) { Handler = HandleAsync };
        link.Closed += reason => _ = OnParentLostAsync(reason);
        _link = link;
        link.Start();
        try
        {
            var w = await link.RequestAsync<WelcomeMsg>(Msg.Hello, new HelloMsg(Name, Environment.ProcessId), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (w == null) return null;
            Main = w.Main;
            Arbiter.Apply(w.Roster);
            Claims.SetMirror(w.Claims);
            _task = w.Task;
            return w;
        }
        catch (HiveIpcException ex)
        {
            _log.LogError("clone {Name}: hello failed: {Error}", Name, ex.Message);
            return null;
        }
    }

    /// <summary>Whole clone lifetime. <paramref name="stop"/> (e.g. SIGTERM to the clone itself) = planned logout.</summary>
    /// <param name="welcome">Already connected via <see cref="ConnectParentAsync"/> (the host checks the parent before building the bot).</param>
    public async Task<int> RunAsync(CancellationToken stop, WelcomeMsg? welcome = null)
    {
        welcome ??= await ConnectParentAsync(TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
        if (welcome == null) return CloneExitCodes.NoParent;
        using var reg = stop.Register(() => _ = LogoutAsync("signal", planned: true));
        var ct = _cts.Token;
        _ = Task.Run(() => StreamLoopAsync(ct));
        _ = Task.Run(() => WatchdogAsync(ct));
        _body.UsageRecorded += OnUsage;
        _body.Died += OnDied;

        try { await _body.ConnectAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return await _exit.Task.ConfigureAwait(false); }

        if (welcome.Stranded && !LoggingOut)
        {
            // This clone crashed holding items last time: put them away before doing anything else.
            _log.LogInformation("clone {Name}: depositing {N} stranded item(s) first", Name, welcome.StrandedItems);
            var r = await new DepositProcedure(_body.Inventory, Claims, "hive_storage_" + Name, this, _log)
                .RunAsync(TimeSpan.FromSeconds(_cfg.DepositTimeoutSeconds), ct).ConfigureAwait(false);
            LastDeposit = r;
            if (r.Empty) _store?.ClearStranded(Name);
            await ReportAsync("progress", "stranded items: " + r.Text).ConfigureAwait(false);
        }
        if (!LoggingOut)
        {
            _body.StartAgent(ct);
            if (_task != null) _body.AssignTask(_task);
        }
        return await _exit.Task.ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- from main

    private async Task<object?> HandleAsync(HiveMessage m, CancellationToken ct)
    {
        switch (m.T)
        {
            case Msg.Heartbeat: return null;
            case Msg.Task when m.As<TaskMsg>() is { } t:
                _task = t;
                if (!LoggingOut) _body.AssignTask(t);
                return null;
            case Msg.Recall when m.As<RecallMsg>() is { } r:
                _ = LogoutAsync(r.Reason, planned: true, r.DeadlineSeconds is { } d ? TimeSpan.FromSeconds(d) : null);
                return null;
            case Msg.Roster when m.As<RosterMsg>() is { } ro:
                Arbiter.Apply(ro);
                return null;
            case Msg.Claims when m.As<ClaimsMsg>() is { } c:
                Claims.SetMirror(c);
                return null;
            case Msg.Cmd when m.As<CmdMsg>() is { } cmd:
                return await _body.ExecuteAsync(cmd.Line, ct).ConfigureAwait(false);
            case Msg.Inject when m.As<InjectMsg>() is { } inj:
                _body.InjectChat(inj.Raw);
                return null;
            default: return null;
        }
    }

    // ---------------------------------------------------------------- to main

    /// <summary>hive_report tool: progress / done / failed. done/failed → result to main, then a planned logout.</summary>
    public async Task<string> ReportAsync(string status, string text, double? pct = null)
    {
        status = (status ?? "progress").Trim().ToLowerInvariant();
        var id = _task?.Id ?? "-";
        if (status is "done" or "failed")
        {
            await Send(Msg.Result, new ResultMsg(id, status == "done", text)).ConfigureAwait(false);
            _ = LogoutAsync("task " + status, planned: true);
            return "ok: reported; depositing everything and logging out";
        }
        await Send(Msg.Progress, new ProgressMsg(id, text, pct)).ConfigureAwait(false);
        return "ok: reported";
    }

    /// <summary>This clone accepted a player's message (sticky partner).</summary>
    public void ChatAccepted(string player) => _ = Send(Msg.ChatAccepted, new ChatAcceptedMsg(player));

    private Task<bool> Send(string type, object? payload) => _link is { IsOpen: true } l ? l.SendAsync(type, payload) : Task.FromResult(false);

    private void OnUsage(PromptUsageRecord r) => _ = Send(Msg.Usage, JsonSerializer.Serialize(r, BotSnapshot.Json));

    private void OnDied(string cause)
    {
        var lost = _inventory;
        _ = Send(Msg.Death, new DeathMsg(cause, lost));
        var at = DateTimeOffset.UtcNow;
        var text = $"{Name} died ({cause}); lost {HiveStore.FormatItems(lost)}";
        try { RecordExperience?.Invoke($"death:{Name}:{at:yyyyMMddHHmmss}", text); } catch (Exception ex) { _log.LogWarning(ex, "death record failed"); }
        _inventory = [];
        _lastInv = "";
        try { _store?.SaveInventory(Name, []); } catch (Exception) { }
        if (_cfg.OnCloneDeath.Equals("logout", StringComparison.OrdinalIgnoreCase)) _ = LogoutAsync("died", planned: true);
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        var snapEvery = TimeSpan.FromSeconds(Math.Clamp(_cfg.SnapshotSeconds, 0.2, 10));
        var hbEvery = _cfg.Heartbeat;
        var lastHb = DateTimeOffset.MinValue;
        var lastInvCheck = DateTimeOffset.MinValue;
        var voxelGate = new VoxelSendGate();
        while (!ct.IsCancellationRequested && _link is { IsOpen: true })
        {
            try
            {
                await Send(Msg.Snapshot, voxelGate.Thin(_body.Snapshot(full: true)).ToJson()).ConfigureAwait(false); // unchanged voxels: Seq only
                var now = DateTimeOffset.UtcNow;
                if (now - lastHb >= hbEvery) { lastHb = now; await Send(Msg.Heartbeat, null).ConfigureAwait(false); }
                if (now - lastInvCheck >= TimeSpan.FromSeconds(2)) { lastInvCheck = now; await SyncInventoryAsync().ConfigureAwait(false); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "stream tick failed"); }
            try { await Task.Delay(snapEvery, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Live per-clone inventory for the hive ledger (shared db + main), on change only.</summary>
    public async Task SyncInventoryAsync()
    {
        if (!_body.IsPlaying) return;
        var items = _body.Inventory.Items().ToList();
        var key = string.Join(";", items.OrderBy(i => i.Id).ThenBy(i => i.Damage).Select(i => $"{i.Id}:{i.Damage}:{i.Count}"));
        if (key == _lastInv) return;
        _lastInv = key;
        _inventory = items;
        try { _store?.SaveInventory(Name, items, _body.Snapshot(false).Dimension); } catch (Exception ex) { _log.LogDebug(ex, "inventory save failed"); }
        await Send(Msg.Inventory, new InventoryMsg(items)).ConfigureAwait(false);
    }

    /// <summary>Main alive check: no message for the heartbeat timeout = parent lost (even if the pipe looks open).</summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_cfg.Heartbeat, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            if (_link is { IsOpen: true } l && DateTimeOffset.UtcNow - l.LastReceived > _cfg.HeartbeatTimeout)
            {
                _log.LogWarning("clone {Name}: main silent for {S:0}s", Name, (DateTimeOffset.UtcNow - l.LastReceived).TotalSeconds);
                await l.DisposeAsync().ConfigureAwait(false); // → OnParentLostAsync
                return;
            }
        }
    }

    private Task OnParentLostAsync(string reason)
    {
        if (LoggingOut) return Task.CompletedTask; // a planned logout is already closing the pipe
        _log.LogWarning("clone {Name}: lost the main process ({Reason}); logging out", Name, reason);
        return LogoutAsync("main process lost", planned: false, TimeSpan.FromSeconds(_cfg.ParentLossDepositSeconds));
    }

    // ---------------------------------------------------------------- the logout gate

    /// <summary>
    /// Stops the brain, deposits everything, and only then logs out. Planned: waits up to <paramref name="timeout"/> (default
    /// <see cref="HiveConfig.DepositTimeoutSeconds"/>) for an empty inventory; parent lost: deposits only if it's safe and
    /// quick. Anything left is recorded as stranded so the next spawn deposits first.
    /// </summary>
    public async Task LogoutAsync(string reason, bool planned, TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _loggingOut, 1) != 0) return;
        LogoutReason = reason;
        int code = CloneExitCodes.Ok;
        try
        {
            _log.LogInformation("clone {Name}: logout ({Reason}); depositing first", Name, reason);
            try { await _body.StopAgentAsync().ConfigureAwait(false); } catch (Exception ex) { _log.LogWarning(ex, "stop agent failed"); }
            var limit = timeout ?? TimeSpan.FromSeconds(planned ? _cfg.DepositTimeoutSeconds : _cfg.ParentLossDepositSeconds);
            DepositReport report;
            if (!_body.IsPlaying || (!planned && !_body.Inventory.CanActSafely))
                report = new DepositReport(DepositProcedure.CountOf(_body.Inventory.Items()) == 0, DepositProcedure.CountOf(_body.Inventory.Items()), 0, "not in a state to deposit");
            else
            {
                using var dcts = new CancellationTokenSource(limit + TimeSpan.FromSeconds(5));
                report = await new DepositProcedure(_body.Inventory, Claims, "hive_storage_" + Name, planned && _link is { IsOpen: true } ? this : null, _log)
                    .RunAsync(limit, dcts.Token).ConfigureAwait(false);
            }
            LastDeposit = report;
            _log.LogInformation("clone {Name}: deposit before logout: {Text}", Name, report.Text);
            await SyncInventoryAsync().ConfigureAwait(false);
            if (!report.Empty)
            {
                try { _store?.MarkStranded(Name, (planned ? "logout: " : "") + reason, _body.Inventory.Items().ToList()); } catch (Exception ex) { _log.LogWarning(ex, "stranded record failed"); }
            }
            else
            {
                try { _store?.ClearStranded(Name); } catch (Exception) { }
            }
            if (_link is { IsOpen: true } l) await l.SendAsync(Msg.Bye, new ByeMsg(reason, report.Empty, report.Remaining)).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogError(ex, "clone {Name}: logout failed", Name); code = 1; }
        finally
        {
            try { await _body.DisconnectAsync("Bye!").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
            _cts.Cancel();
            if (_link is { } l) { try { await l.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
            _exit.TrySetResult(code);
        }
    }

    // ---------------------------------------------------------------- handoff to main (IHandoffTarget)

    async Task<Vec3d?> IHandoffTarget.OfferAsync(int items, CancellationToken ct)
    {
        if (_link is not { IsOpen: true } l) return null;
        try
        {
            var r = await l.RequestAsync<HandoffReplyMsg>(Msg.Handoff, new HandoffMsg(items), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return r is { Ok: true } ? new Vec3d(r.X, r.Y, r.Z) : null;
        }
        catch (HiveIpcException) { return null; }
    }

    async Task<int> IHandoffTarget.ConfirmAsync(CancellationToken ct)
    {
        if (_link is not { IsOpen: true } l) return 0;
        try { return (await l.RequestAsync<HandoffAckMsg>(Msg.HandoffAck, null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))?.Collected ?? 0; }
        catch (HiveIpcException) { return 0; }
    }
}
