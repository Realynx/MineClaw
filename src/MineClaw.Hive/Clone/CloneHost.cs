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
    /// <summary>
    /// Fetch the task's loadout ("pickaxe", "axe", "food"...) from storage before the brain starts (WorldIndexer chest
    /// data). Returns a short honest line ("took stone_pickaxe from 14,64,-5" / "no pickaxe in storage"), or null when
    /// nothing was needed. Default: unsupported (null).
    /// </summary>
    Task<LoadoutReport?> PrepareLoadoutAsync(IReadOnlyList<string> loadout, CancellationToken ct) => Task.FromResult<LoadoutReport?>(null);
    /// <summary>A TUI console line for this clone.</summary>
    Task<bool> ExecuteAsync(string line, CancellationToken ct);
    /// <summary>A console /as test line forwarded by main (same line every process arbitrates).</summary>
    void InjectChat(string raw);
    /// <summary>An update from main for the running task (a partner joined or changed): context for the brain, the task stays.</summary>
    void Note(string text) { }
    /// <summary>Public chat from C# (not the brain): the void-spawn help request. Default: can't speak.</summary>
    Task SayAsync(string text) => Task.CompletedTask;
    /// <summary>True while the client holds still after a login/teleport, waiting for verified ground (or over the void).</summary>
    bool HoldingStill => false;
    Task DisconnectAsync(string reason);
}

/// <summary>Outcome of a loadout fetch: <see cref="Missing"/> lists what neither the inventory nor storage had.</summary>
public sealed record LoadoutReport(string Text, IReadOnlyList<string> Missing);

/// <summary>
/// Outcome of <see cref="CloneHost.RequestHandoffAsync"/>: <see cref="Received"/> items arrived in this clone's inventory
/// (counted, not assumed). Not ok: <see cref="Text"/> says why; <see cref="Holders"/> = other hive bots carrying a spare.
/// </summary>
public sealed record HandoffResult(bool Ok, int Received, string Text, IReadOnlyList<string> Holders)
{
    public static HandoffResult Fail(string why, IReadOnlyList<string>? holders = null) => new(false, 0, why, holders ?? []);
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
    private (double X, double Y, double Z, DateTimeOffset Since)? _voidHold;
    private TaskMsg? _voidDeferred;
    private DateTimeOffset _connectedAt;
    private readonly object _voidLock = new();
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
        Arbiter = new ChatArbiter(name, "main", cfg.Clones(), sticky: TimeSpan.FromSeconds(cfg.StickySeconds),
            cloneWindow: TimeSpan.FromSeconds(cfg.CloneThreadSeconds));
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

    /// <summary>Trusted players the void-spawn help request is addressed to (the clone's Self.TrustedPlayers).</summary>
    public Func<IReadOnlyList<string>>? TrustedPlayers { get; set; }

    /// <summary>Where the clone holds still over the void waiting for a /tp (null = not holding).</summary>
    public (double X, double Y, double Z)? HoldingOverVoid { get { lock (_voidLock) return _voidHold is { } v ? (v.X, v.Y, v.Z) : null; } }

    /// <summary>How long a void hold waits for a /tp before the clone logs out (its saved spot stays marked: main won't log
    /// it in there again without a player's confirmation).</summary>
    public TimeSpan VoidHoldTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Records a shared experience once (key, text): set by the body (hive_events + a RAG doc in the shared db).</summary>
    public Func<string, string, bool>? RecordExperience { get; set; }

    /// <summary>"Send me your view" (main's hive vision): flush this clone's world memory to the shared db, return its pose.</summary>
    public Func<CancellationToken, Task<CloneViewMsg?>>? View { get; set; }

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
        link.StartHeartbeat(_cfg.Heartbeat); // dedicated thread: never stalled by the brain, the db or the thread pool
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
        _connectedAt = DateTimeOffset.UtcNow;
        _ = Task.Run(() => ClearVoidMarkOnGroundAsync(ct));

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
            if (_task != null) await StartTaskAsync(_task, ct).ConfigureAwait(false);
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
                if (!LoggingOut) _ = StartTaskAsync(t, _cts.Token); // the loadout walk never blocks the message queue
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
            case Msg.Note when m.As<NoteMsg>() is { } note:
                _body.Note(note.Text);
                return null;
            case Msg.View:
                return View == null ? null : await View(ct).ConfigureAwait(false);
            default: return null;
        }
    }

    /// <summary>
    /// Loadout first (a pickaxe for mining, an axe for trees, food), then the task goes to the brain with an honest note.
    /// Anything storage didn't have is reported to main as progress ("no pickaxe in storage"), so main can tell the player.
    /// </summary>
    internal async Task StartTaskAsync(TaskMsg t, CancellationToken ct)
    {
        // Just logged in / teleported: the client holds still until the ground is verified. Over the void that hold lasts until a
        // /tp (it is detected after ~2 s): the task waits for the landing instead of failing at once (live 10-27 07:49: 420 said
        // "no path up to island", reported failed and logged out, twice).
        for (int i = 0; i < 40 && _body.HoldingStill && HoldingOverVoid is null && !LoggingOut; i++)
        {
            try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
        lock (_voidLock)
            if (_voidHold is not null) { _voidDeferred = t; return; }
        string? note = null;
        if (t.Loadout is { Length: > 0 } want)
        {
            try
            {
                var r = await _body.PrepareLoadoutAsync(want, ct).ConfigureAwait(false);
                if (r != null)
                {
                    note = "loadout: " + r.Text;
                    // Storage lacks a tool: the hive's inventory next (main hands over a spare; live 3, 14:16:58).
                    var missing = r.Missing.ToList();
                    var holders = new List<string>();
                    foreach (var kind in r.Missing.Where(k => !k.Equals(TaskLoadout.Food, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (LoggingOut) break;
                        var h = await RequestHandoffAsync(kind, 1, ct).ConfigureAwait(false);
                        if (h.Ok && h.Received > 0) { missing.Remove(kind); note += "; then " + h.Text; }
                        else holders.AddRange(h.Holders);
                    }
                    _log.LogInformation("clone {Name}: {Note}", Name, note);
                    if (missing.Count > 0)
                        await ReportAsync("progress", $"loadout short: no {string.Join(", no ", missing)} in my inventory or storage" +
                            (holders.Count > 0 ? $"; spare with {string.Join(", ", holders.Distinct())}" : "")).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "clone {Name}: loadout fetch failed", Name); note = "loadout: fetch failed (" + ex.Message + ")"; }
        }
        if (LoggingOut || !ReferenceEquals(_task, t) && _task?.Id != t.Id) return; // replaced meanwhile
        _body.AssignTask(note == null ? t : t with { Text = $"({note}) {t.Text}" });
    }

    // ---------------------------------------------------------------- to main

    /// <summary>hive_report tool: progress / done / failed. done/failed → result to main, then a planned logout.</summary>
    public async Task<string> ReportAsync(string status, string text, double? pct = null)
    {
        status = (status ?? "progress").Trim().ToLowerInvariant();
        var id = _task?.Id ?? "-";
        if (status == "failed" && HoldingOverVoid is { } v)
        {
            // Stuck over the void is not a failed task: the help request is out, the body is safe, the task resumes on landing.
            await Send(Msg.Progress, new ProgressMsg(id, "holding over the void, task paused: " + text, null)).ConfigureAwait(false);
            return $"not ended: you are holding still over the void at {Fmt(v)} and already asked for a /tp; wait, the task resumes " +
                   "when you land (walking or building is impossible from here)";
        }
        if (status == "failed" && _cfg.FailureGraceSeconds > 0 && _link is { IsOpen: true })
        {
            // A failure is main's to handle while this clone stays available: one stalled cell, a transient, a missing tool. Main
            // gets it as a wake ("still online, awaiting orders") and reassigns or retries with a fix; no new orders within the
            // grace period → the normal logout (deposit first).
            var grace = TimeSpan.FromSeconds(_cfg.FailureGraceSeconds);
            await Send(Msg.Result, new ResultMsg(id, false, text, Online: true)).ConfigureAwait(false);
            _ = GraceAsync(id, grace, _cts.Token);
            return $"ok: reported to main; you stay online, idle and safe, awaiting new orders for {grace.TotalMinutes:0.#} min " +
                   "(then deposit and log out). Don't start anything else meanwhile";
        }
        if (status is "done" or "failed")
        {
            await Send(Msg.Result, new ResultMsg(id, status == "done", text)).ConfigureAwait(false);
            _ = LogoutAsync("task " + status, planned: true);
            return "ok: reported; depositing everything and logging out";
        }
        await Send(Msg.Progress, new ProgressMsg(id, text, pct)).ConfigureAwait(false);
        return "ok: reported";
    }

    /// <summary>A hive script ended on its own: one result line to main, then logout (deposit first) or stay asleep online.</summary>
    public async Task ReportResultAsync(bool ok, string text, bool logout)
    {
        await Send(Msg.Result, new ResultMsg(_task?.Id ?? "-", ok, text)).ConfigureAwait(false);
        if (logout) _ = LogoutAsync("script " + (ok ? "done" : "failed"), planned: true);
    }

    /// <summary>This clone accepted a player's message (sticky partner).</summary>
    public void ChatAccepted(string player) => _ = Send(Msg.ChatAccepted, new ChatAcceptedMsg(player));

    /// <summary>This clone spoke to a player: their unnamed follow-ups continue with it for the arbiter's clone window.</summary>
    public void ChatSpoke(string player) => _ = Send(Msg.ChatAccepted, new ChatAcceptedMsg(player, Spoke: true));

    private Task<bool> Send(string type, object? payload) => _link is { IsOpen: true } l ? l.SendAsync(type, payload) : Task.FromResult(false);

    private void OnUsage(PromptUsageRecord r) => _ = Send(Msg.Usage, JsonSerializer.Serialize(r, BotSnapshot.Json));

    /// <summary>Death cause of a deliberate void step (into_void): an intentional reset, never a failure or a reason to retire.</summary>
    public const string IntentionalResetCause = "intentional reset";

    private void OnDied(string cause)
    {
        if (cause.StartsWith(IntentionalResetCause, StringComparison.Ordinal))
        {
            // The clone respawns and carries on: no lost-items record, no logout (whatever OnCloneDeath says), main is told.
            _log.LogInformation("clone {Name}: {Cause}; respawning and continuing", Name, cause);
            _ = Send(Msg.Death, new DeathMsg(cause, []));
            return;
        }
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
        var lastInvCheck = DateTimeOffset.MinValue;
        var voxelGate = new VoxelSendGate();
        while (!ct.IsCancellationRequested && _link is { IsOpen: true })
        {
            try
            {
                await Send(Msg.Snapshot, voxelGate.Thin(_body.Snapshot(full: true)).ToJson()).ConfigureAwait(false); // unchanged voxels: Seq only
                var now = DateTimeOffset.UtcNow; // heartbeats: the link's own thread (HiveLink.StartHeartbeat)
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

    /// <summary>
    /// Main alive check: any frame counts; after <see cref="HiveConfig.HeartbeatTimeout"/> of total silence a ping/pong
    /// probe decides (the pong comes from main's link threads, so a busy main still answers). Only a failed probe = lost.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_cfg.Heartbeat, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            if (_link is not { IsOpen: true } l || DateTimeOffset.UtcNow - l.LastReceived <= _cfg.HeartbeatTimeout) continue;
            var silent = DateTimeOffset.UtcNow - l.LastReceived;
            _log.LogInformation("clone {Name}: main silent for {S:0}s; probing", Name, silent.TotalSeconds);
            if (await l.ProbeAsync(_cfg.PingTimeout, ct).ConfigureAwait(false))
            {
                _log.LogInformation("clone {Name}: main answered the probe; staying", Name);
                continue;
            }
            if (!l.IsOpen) return; // closed meanwhile: OnParentLostAsync runs
            _log.LogWarning("clone {Name}: main silent for {S:0}s and no answer to a ping in {P:0}s", Name,
                (DateTimeOffset.UtcNow - l.LastReceived).TotalSeconds, _cfg.PingTimeout.TotalSeconds);
            await l.DisposeAsync().ConfigureAwait(false); // → OnParentLostAsync
            return;
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
    /// <summary>The failure grace period: still on the failed task (no new orders from main) when it ends → deposit, log out.</summary>
    private async Task GraceAsync(string failedId, TimeSpan grace, CancellationToken ct)
    {
        try { await Task.Delay(grace, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        if (LoggingOut || (_task?.Id ?? "-") != failedId) return; // main reassigned (or recalled) meanwhile
        _log.LogInformation("clone {Name}: no new orders {S:0}s after the failure; logging out", Name, grace.TotalSeconds);
        await LogoutAsync("task failed (no new orders)", planned: true).ConfigureAwait(false);
    }

    /// <summary>A logout because the clone's own job ended (not main's recall, the budget, a shutdown or a lost parent).</summary>
    private static bool EndsTask(string reason) =>
        reason.StartsWith("task ", StringComparison.Ordinal) || reason.StartsWith("script ", StringComparison.Ordinal);

    public async Task LogoutAsync(string reason, bool planned, TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _loggingOut, 1) != 0) return;
        LogoutReason = reason;
        bool stayOnline = false;
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
            if (!report.Empty && planned && _body.IsPlaying && _link is { IsOpen: true } && EndsTask(reason))
            {
                // The user's rule: the WHOLE inventory is put away before any logout. Storage, a step-out, main's hands and a new
                // chest all failed: stay online holding it and ask main (free a chest, take the items, or recall me to retry).
                _log.LogWarning("clone {Name}: {N} item(s) not put away; staying online instead of logging out ({Reason})", Name, report.Remaining, reason);
                await Send(Msg.NeedsInput, new NeedsInputMsg(_task?.Id ?? "-",
                    $"couldn't put away {report.Remaining} item(s) ({report.Text}); staying online holding them: free a chest, take them, or recall me")).ConfigureAwait(false);
                LogoutReason = null;
                stayOnline = true;
                try { _body.StartAgent(_cts.Token); } catch (Exception ex) { _log.LogDebug(ex, "restart agent failed"); }
                return;
            }
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
            if (stayOnline) Volatile.Write(ref _loggingOut, 0); // a later recall/logout retries the deposit
        }
        if (stayOnline) return;
        try { await _body.DisconnectAsync("Bye!").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        _cts.Cancel();
        if (_link is { } link) { try { await link.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
        _exit.TrySetResult(code);
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ---------------------------------------------------------------- void spawn: hold still, ask for a /tp, resume

    private static string Fmt((double X, double Y, double Z) p) =>
        $"{(int)Math.Floor(p.X)},{(int)Math.Floor(p.Y)},{(int)Math.Floor(p.Z)}";

    /// <summary>
    /// The client is holding still over the void (loaded chunks, nothing below; it never falls and is never kicked: see
    /// MinecraftClient.DeferEcho). Once per hold: marks the spot in the shared db (main won't log this clone in there again
    /// without a player's confirmation), asks the trusted players in chat for a /tp, tells main (which relays it to its brain),
    /// and pauses the task until the landing.
    /// </summary>
    public async Task OnVoidHoldAsync(double x, double y, double z)
    {
        if (LoggingOut) return;
        lock (_voidLock)
        {
            if (_voidHold is not null) return;
            _voidHold = (x, y, z, DateTimeOffset.UtcNow);
        }
        var at = Fmt((x, y, z));
        string target = Main is { Length: > 0 } m ? m : "main";
        var who = (TrustedPlayers?.Invoke() ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool spawned = _connectedAt == default || DateTimeOffset.UtcNow - _connectedAt < TimeSpan.FromMinutes(2);
        var line = (who.Count > 0 ? string.Join(", ", who) + ": " : "") +
                   $"I {(spawned ? "spawned" : "ended up")} over the void at {at}, can someone /tp {Name} to me or to {target}?";
        _log.LogWarning("clone {Name}: holding over the void at {At}; asking for a /tp", Name, at);
        try { _store?.MarkVoidSpawn(Name, x, y, z); } catch (Exception ex) { _log.LogDebug(ex, "void spawn record failed"); }
        try { await _body.SayAsync(line).ConfigureAwait(false); } catch (Exception ex) { _log.LogDebug(ex, "void help request failed"); }
        await Send(Msg.VoidHold, new VoidHoldMsg(true, x, y, z, $"/tp {Name} {target}")).ConfigureAwait(false);
        _ = VoidTimeoutAsync(_cts.Token);
    }

    /// <summary>A /tp (or a block below) put the clone on verified ground: clear the mark, tell main, run the paused task.</summary>
    public async Task OnVoidHoldEndedAsync(double x, double y, double z)
    {
        TaskMsg? resume;
        lock (_voidLock)
        {
            if (_voidHold is null) return;
            _voidHold = null;
            resume = _voidDeferred;
            _voidDeferred = null;
        }
        _log.LogInformation("clone {Name}: on ground again at {At} after the void hold; resuming", Name, Fmt((x, y, z)));
        try { _store?.ClearVoidSpawn(Name); } catch (Exception ex) { _log.LogDebug(ex, "void spawn clear failed"); }
        await Send(Msg.VoidHold, new VoidHoldMsg(false, x, y, z)).ConfigureAwait(false);
        if (resume != null && !LoggingOut && (ReferenceEquals(_task, resume) || _task?.Id == resume.Id))
            await StartTaskAsync(resume, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Logged in onto verified ground (a player built under the saved spot, or it was never the void): drop the mark.</summary>
    private async Task ClearVoidMarkOnGroundAsync(CancellationToken ct)
    {
        for (int i = 0; i < 60 && _body.HoldingStill && HoldingOverVoid is null; i++)
        {
            try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
        if (_body.IsPlaying && !_body.HoldingStill && HoldingOverVoid is null)
            try { _store?.ClearVoidSpawn(Name); } catch (Exception ex) { _log.LogDebug(ex, "void spawn clear failed"); }
    }

    private async Task VoidTimeoutAsync(CancellationToken ct)
    {
        DateTimeOffset since;
        lock (_voidLock) { if (_voidHold is not { } v) return; since = v.Since; }
        try { await Task.Delay(VoidHoldTimeout, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        (double X, double Y, double Z) where;
        lock (_voidLock)
        {
            if (_voidHold is not { } v || v.Since != since) return; // landed (or a newer hold) meanwhile
            where = (v.X, v.Y, v.Z);
        }
        await Send(Msg.Result, new ResultMsg(_task?.Id ?? "-", false,
            $"stuck over the void at {Fmt(where)} for {VoidHoldTimeout.TotalMinutes:0} min, nobody /tp'd me; logging out " +
            "(that spot is saved: I'd spawn there again)")).ConfigureAwait(false);
        await LogoutAsync("stuck over the void", planned: true).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- handoff from main (lend)

    /// <summary>
    /// Ask the hive for an item this clone lacks: a loadout kind ("pickaxe", "axe", "shovel", "sword") or an item name
    /// ("stone_pickaxe", "torch"). Main tosses it at this clone's feet when it has one spare (it keeps the one it is using):
    /// main stands still, this clone walks next to it, main tosses, and the result counts what actually arrived in the
    /// inventory. When main has none spare, <see cref="HandoffResult.Holders"/> names other live clones carrying a spare.
    /// For the loadout and any other caller (the acquisition planner).
    /// </summary>
    /// <param name="ids">Acceptable item ids, best first (e.g. a minimum tool tier); null = any of the kind/item.</param>
    public async Task<HandoffResult> RequestHandoffAsync(string item, int count, CancellationToken ct, IReadOnlyList<int>? ids = null)
    {
        if (_link is not { IsOpen: true } l) return HandoffResult.Fail("no link to main");
        if (_bodyRef is not { IsPlaying: true } body) return HandoffResult.Fail("not in game");
        LendReplyMsg? offer;
        try { offer = await l.RequestAsync<LendReplyMsg>(Msg.Lend, new LendMsg(item, Math.Max(1, count), ids?.ToArray()), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (HiveIpcException ex) { return HandoffResult.Fail("main did not answer: " + ex.Message); }
        if (offer is not { Ok: true }) return HandoffResult.Fail(offer?.Reason ?? "main did not answer", offer?.Holders);
        int before = Count(body, offer.Id);
        bool near;
        try { near = await body.Inventory.GoNearAsync(new Vec3d(offer.X, offer.Y, offer.Z), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "walk to main failed"); near = false; }
        var me = body.Snapshot(false).Pos ?? new BotPos(offer.X, offer.Y, offer.Z);
        // Go even when the walk fell short: the toss request is what releases main (it stands still until then).
        LendDoneMsg? done;
        try { done = await l.RequestAsync<LendDoneMsg>(Msg.LendGo, new LendGoMsg(me.X, me.Y, me.Z), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (HiveIpcException ex) { return HandoffResult.Fail("main did not toss: " + ex.Message); }
        if (done is not { Tossed: > 0 }) return HandoffResult.Fail("main could not toss: " + (done?.Reason ?? "no answer"));
        // Confirmed by this clone's own inventory (the pick-up), never by the toss alone.
        var until = DateTimeOffset.UtcNow + HandoffPickupWait;
        while (DateTimeOffset.UtcNow < until && !ct.IsCancellationRequested && Count(body, offer.Id) - before < done.Tossed)
        {
            try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        int got = Math.Max(0, Count(body, offer.Id) - before);
        _log.LogInformation("clone {Name}: main tossed {T} {Item}, picked up {Got}{Near}", Name, done.Tossed, offer.Name, got, near ? "" : " (walk to main fell short)");
        return got > 0
            ? new HandoffResult(true, got, $"main handed me {got} {offer.Name}", [])
            : HandoffResult.Fail($"main tossed {done.Tossed} {offer.Name} but I did not pick it up");
    }

    /// <summary>How long a clone waits to pick up what main tossed.</summary>
    public static TimeSpan HandoffPickupWait { get; set; } = TimeSpan.FromSeconds(4);

    private static int Count(ICloneBody body, int id) => body.Inventory.Items().Where(i => i.Id == id).Sum(i => i.Count);

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
