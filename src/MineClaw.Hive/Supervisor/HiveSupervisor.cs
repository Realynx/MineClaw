using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Db;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Supervisor;

public enum CloneState { Idle, Starting, Running, Stopping, Backoff, Retired }

/// <summary>Main-side receiver for item handoffs (the main bot's position + its collect events).</summary>
public interface IHandoffReceiver
{
    Vec3d? Position { get; }
    /// <summary>Starts counting items this bot picks up; returns a counter read by <see cref="Collected"/>.</summary>
    void BeginCounting(string from);
    int Collected(string from);
}

/// <summary>One configured clone: its process, pipe, task and health.</summary>
public sealed class CloneSlot(string name)
{
    internal readonly object Lock = new();
    public string Name { get; } = name;
    public string Number => ChatArbiter.NumberOf(Name) ?? Name;
    public CloneState State { get; internal set; } = CloneState.Idle;
    public TaskMsg? Task { get; internal set; }
    public double Progress { get; internal set; }
    public string? LastProgress { get; internal set; }
    public DateTimeOffset Started { get; internal set; }
    public DateTimeOffset LaunchedAt { get; internal set; }
    public bool Connected { get; internal set; }
    public bool Recalled { get; internal set; }
    public string? RecallReason { get; internal set; }
    public bool ByeReceived { get; internal set; }
    public string? LastExit { get; internal set; }
    public int Deaths { get; internal set; }
    public List<DateTimeOffset> Crashes { get; } = new();
    public BotSnapshot? Snapshot { get; internal set; }
    public List<ItemMsg>? Inventory { get; internal set; }
    internal ICloneProcess? Process;
    internal HiveLink? Link;
    internal int Generation;
    internal TaskCompletionSource? Gone;
    internal event Action<PromptUsageRecord>? Usage;
    internal void RaiseUsage(PromptUsageRecord r) => Usage?.Invoke(r);
    public bool IsLive => State is CloneState.Running or CloneState.Stopping && Connected;
}

/// <summary>
/// The hive supervisor (main process only). Spawns clones as separate OS processes (<c>--clone name --parent-pipe pipe</c>),
/// one named pipe each; watches heartbeats (both ways) and process exit; restarts crashed clones with exponential backoff
/// and retires one after <see cref="HiveConfig.MaxCrashes"/> crashes in the window. Hosts the claim board, chat arbiter
/// state and budget ledger for the clones. When a pipe closes the clone is treated as gone: its claims and grants are
/// released at once, an unplanned exit records its last inventory as stranded, and the main bot gets a quiet event.
/// A dying clone never touches the main process: everything here is message passing and process handles.
/// </summary>
public sealed class HiveSupervisor : IAsyncDisposable
{
    private readonly HiveConfig _cfg;
    private readonly string _main;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, CloneSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _tick;
    private readonly Timer _policy;
    private int _rosterPush, _claimsPush;
    private volatile bool _shuttingDown;

    public HiveSupervisor(HiveConfig cfg, string main, IProcessLauncher launcher, ClaimBoard claims, BudgetLedger ledger, ChatArbiter arbiter,
        HiveStore? store, Func<SlotFacts> slots, IReadOnlyList<string> regulars, ILogger? log = null, TimeProvider? time = null)
    {
        _cfg = cfg;
        _main = main;
        _launcher = launcher;
        Claims = claims;
        Ledger = ledger;
        Arbiter = arbiter;
        Store = store;
        SlotFacts = slots;
        Regulars = regulars;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        foreach (var c in cfg.Clones()) _slots[c] = new CloneSlot(c);
        claims.Changed += () => _ = PushClaimsAsync();
        arbiter.Changed += () => _ = PushRosterAsync();
        ledger.Spent += () => _ = CheckBudgetAsync();
        _tick = new Timer(_ => Tick(), null, cfg.Heartbeat, cfg.Heartbeat);
        _policy = new Timer(_ => _ = PolicyAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public ClaimBoard Claims { get; }
    public BudgetLedger Ledger { get; }
    public ChatArbiter Arbiter { get; }
    public HiveStore? Store { get; }
    public Func<SlotFacts> SlotFacts { get; }
    public IReadOnlyList<string> Regulars { get; }
    public IHandoffReceiver? Handoff { get; set; }

    /// <summary>Events for the main bot's queue (quiet ones are Background: context only, never a wake-up).</summary>
    public event Action<GameEvent>? Notice;
    /// <summary>Console /as lines etc. are forwarded to live clones so every process arbitrates the same message.</summary>
    public IReadOnlyCollection<CloneSlot> Slots => _slots.Values;
    public CloneSlot? Slot(string name) => _slots.GetValueOrDefault(name);

    private DateTimeOffset Now => _time.GetUtcNow();

    private void Post(GameEvent e)
    {
        _log.LogInformation("hive: {Event}", e.Format());
        try { Notice?.Invoke(e); } catch (Exception ex) { _log.LogWarning(ex, "hive notice handler failed"); }
    }

    /// <summary>"67" / "maid_chan_67" / "maid chan 67" → the configured clone login.</summary>
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().TrimStart('#');
        if (_slots.ContainsKey(n)) return _slots[n].Name;
        var squashed = new string(n.Where(char.IsLetterOrDigit).ToArray());
        return _slots.Values.FirstOrDefault(s => s.Number.Equals(n, StringComparison.OrdinalIgnoreCase)
            || new string(s.Name.Where(char.IsLetterOrDigit).ToArray()).Equals(squashed, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    // ================================================================ spawn / recall / list

    public string Spawn(string task, string? name = null)
    {
        if (!_cfg.Enabled) return "error: hive is off";
        if (_shuttingDown) return "error: shutting down";
        task = (task ?? "").Trim();
        if (task.Length == 0) return "error: task required";
        if (task.Length > 400) task = task[..400];
        if (Ledger.AtRecallLine(out var why)) return "no: budget (" + why + ")";

        CloneSlot? slot;
        if (name != null)
        {
            var r = Resolve(name);
            if (r == null) return $"error: no clone '{name}' (have {string.Join(", ", _slots.Values.Select(s => s.Number))})";
            slot = _slots[r];
        }
        else slot = _slots.Values.FirstOrDefault(s => s.State == CloneState.Idle);
        if (slot == null) return "no: all clones busy or retired";

        var t = new TaskMsg(Guid.NewGuid().ToString("N")[..8], task);
        lock (slot.Lock)
        {
            switch (slot.State)
            {
                case CloneState.Retired: return $"no: {slot.Number} is retired ({slot.Crashes.Count} crashes)";
                case CloneState.Stopping: return $"no: {slot.Number} is logging out";
                case CloneState.Running or CloneState.Starting or CloneState.Backoff:
                    slot.Task = t;
                    slot.Progress = 0;
                    slot.LastProgress = null;
                    if (slot.Link is { IsOpen: true } l) _ = l.SendAsync(Msg.Task, t);
                    return $"ok: {slot.Number} got the new task";
            }
            var facts = SlotFacts();
            int pending = _slots.Values.Count(s => s.State is CloneState.Starting && !facts.Online.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
            var d = SlotPolicy.CanSpawn(facts, Regulars, pending, _cfg.MaxPlayersFallback);
            if (!d.Ok) return "no: " + d.Reason;
            slot.Task = t;
            slot.Progress = 0;
            slot.LastProgress = null;
            slot.Recalled = false;
            slot.RecallReason = null;
            slot.Crashes.Clear();
            slot.Started = Now;
        }
        Launch(slot);
        return $"ok: {slot.Number} spawning";
    }

    public string Recall(string? nameOrAll, string reason = "recalled")
    {
        var targets = string.IsNullOrWhiteSpace(nameOrAll) || nameOrAll.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? _slots.Values.Where(s => s.State is not (CloneState.Idle or CloneState.Retired)).ToList()
            : Resolve(nameOrAll) is { } n ? [_slots[n]] : null;
        if (targets == null) return $"error: no clone '{nameOrAll}'";
        if (targets.Count == 0) return "ok: no clones out";
        foreach (var s in targets) RecallSlot(s, reason);
        return "ok: recalling " + string.Join(", ", targets.Select(s => s.Number));
    }

    private void RecallSlot(CloneSlot s, string reason, double? deadlineSeconds = null)
    {
        HiveLink? link;
        lock (s.Lock)
        {
            s.Recalled = true;
            s.RecallReason = reason;
            if (s.State is CloneState.Idle or CloneState.Retired) return;
            if (s.State == CloneState.Backoff) { s.State = CloneState.Idle; return; }
            s.State = CloneState.Stopping;
            link = s.Link;
        }
        if (link is { IsOpen: true }) _ = link.SendAsync(Msg.Recall, new RecallMsg(reason, deadlineSeconds));
        else if (!s.Connected) s.Process?.Kill(); // never connected: nothing to deposit
    }

    /// <summary>Main shutting down: recall everyone, wait for them to deposit and log out, kill stragglers (recorded as stranded).</summary>
    public async Task RecallAllAsync(string reason, TimeSpan timeout)
    {
        _shuttingDown = true;
        var live = _slots.Values.Where(s => s.State is not (CloneState.Idle or CloneState.Retired)).ToList();
        if (live.Count == 0) return;
        var waits = live.Select(s => s.Gone?.Task ?? Task.CompletedTask).ToList();
        foreach (var s in live) RecallSlot(s, reason, Math.Max(5, timeout.TotalSeconds - 5));
        try { await Task.WhenAll(waits).WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            foreach (var s in live.Where(s => s.State is not (CloneState.Idle or CloneState.Retired)))
            {
                _log.LogWarning("hive: {Clone} didn't log out in time; killing it", s.Name);
                s.Process?.Kill();
            }
            try { await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { }
        }
    }

    /// <summary>Terse status line for <c>list_clones</c>.</summary>
    public string List()
    {
        var parts = _slots.Values.Select(s =>
        {
            string st = s.State switch
            {
                CloneState.Running when s.Connected => "out",
                CloneState.Running or CloneState.Starting => "starting",
                CloneState.Stopping => "logging out",
                CloneState.Backoff => "restarting",
                CloneState.Retired => $"retired ({s.Crashes.Count} crashes)",
                _ => "idle",
            };
            var line = $"{s.Number} {st}";
            if (s.State is not (CloneState.Idle or CloneState.Retired) && s.Task != null)
            {
                line += $": {Trunc(s.Task.Text, 50)}";
                if (s.Progress > 0) line += $" {s.Progress:P0}";
                if (s.Snapshot?.Pos is { } p) line += $" @{p}";
            }
            if (s.Deaths > 0) line += $" died x{s.Deaths}";
            return line;
        });
        var (d, _) = Ledger.ClonesSpend();
        return string.Join(" | ", parts) + $" | clones today ${d:0.00}";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>
    /// The main brain's per-turn hive line (uncached tail), only while clones are online:
    /// "[hive: 1 clone online: maid_chan_67 (idle)]", plus a split reminder when the turn holds a group ask.
    /// </summary>
    public string? HintLine(bool groupAsk = false)
    {
        var live = _slots.Values.Where(s => s.IsLive).ToList();
        if (live.Count == 0) return null;
        var parts = live.Select(s => $"{s.Name} ({CloneActivity(s)})");
        var line = $"[hive: {live.Count} clone{(live.Count == 1 ? "" : "s")} online: {string.Join(", ", parts)}]";
        if (groupAsk) line += " group ask: split it, hive spawn name+task for the clone's part; reply once";
        return line;
    }

    private static string CloneActivity(CloneSlot s)
    {
        if (s.State == CloneState.Stopping) return "logging out";
        var task = s.Snapshot?.Task;
        if (string.IsNullOrWhiteSpace(task) || task.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return "idle";
        return "busy: " + Trunc(task, 30);
    }

    // ================================================================ process lifecycle

    private void Launch(CloneSlot slot)
    {
        var pipe = PipeNames.For(slot.Name, _cfg.PipeDirectory);
        System.IO.Pipes.NamedPipeServerStream server;
        int gen;
        lock (slot.Lock)
        {
            gen = ++slot.Generation;
            slot.State = CloneState.Starting;
            slot.Connected = false;
            slot.ByeReceived = false;
            slot.LaunchedAt = Now;
            slot.Gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server = PipeNames.Server(pipe);
        }
        ICloneProcess proc;
        try { proc = _launcher.Start(slot.Name, pipe); }
        catch (Exception ex)
        {
            server.Dispose();
            _log.LogWarning(ex, "hive: can't start {Clone}", slot.Name);
            OnGone(slot, gen, clean: false, "start failed: " + ex.Message);
            return;
        }
        lock (slot.Lock) slot.Process = proc;
        _log.LogInformation("hive: launched {Clone} (pid {Pid}, pipe {Pipe})", slot.Name, proc.Id, pipe);

        _ = Task.Run(async () =>
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            startCts.CancelAfter(TimeSpan.FromSeconds(_cfg.StartTimeoutSeconds));
            var exited = proc.WaitForExitAsync(_cts.Token);
            try
            {
                var connect = server.WaitForConnectionAsync(startCts.Token);
                if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited || !server.IsConnected)
                {
                    server.Dispose();
                    OnGone(slot, gen, clean: false, $"exited before connecting (code {proc.ExitCode})");
                    return;
                }
                await connect.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                proc.Kill();
                OnGone(slot, gen, clean: false, "didn't connect in time");
                return;
            }
            var link = new HiveLink(server, slot.Name, _log);
            link.Handler = (m, ct) => HandleAsync(slot, gen, m, ct);
            link.Closed += reason => OnGone(slot, gen, slot.ByeReceived, slot.ByeReceived ? "logged out" : reason);
            lock (slot.Lock) slot.Link = link;
            link.Start();
            try { await exited.ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            // Process gone: the pipe closes with it, but don't wait for the read loop to notice.
            OnGone(slot, gen, slot.ByeReceived, slot.ByeReceived ? "logged out" : $"exited (code {proc.ExitCode})");
        });
    }

    private void OnGone(CloneSlot slot, int gen, bool clean, string reason)
    {
        bool restart = false;
        TimeSpan backoff = default;
        TaskCompletionSource? gone;
        List<ItemMsg>? inv;
        lock (slot.Lock)
        {
            if (gen != slot.Generation || slot.State is CloneState.Idle or CloneState.Retired or CloneState.Backoff) return; // already handled
            gone = slot.Gone;
            inv = slot.Inventory;
            slot.Connected = false;
            slot.LastExit = reason;
            var link = slot.Link;
            slot.Link = null;
            if (link != null) _ = link.DisposeAsync().AsTask();
            slot.Process?.Kill();
            slot.Process?.Dispose();
            slot.Process = null;
            if (clean || slot.Recalled)
                slot.State = CloneState.Idle;
            else
            {
                var now = Now;
                slot.Crashes.Add(now);
                slot.Crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(_cfg.CrashWindowMinutes));
                if (slot.Crashes.Count >= _cfg.MaxCrashes) slot.State = CloneState.Retired;
                else if (slot.Task != null && !_shuttingDown)
                {
                    slot.State = CloneState.Backoff;
                    restart = true;
                    backoff = TimeSpan.FromSeconds(Math.Min(_cfg.BackoffMaxSeconds, _cfg.BackoffBaseSeconds * Math.Pow(2, slot.Crashes.Count - 1)));
                }
                else slot.State = CloneState.Idle;
            }
        }
        // Immediately: the dead clone's leases, grants and conversations go.
        int released = Claims.ReleaseAll(slot.Name);
        Ledger.Close(slot.Name);
        Arbiter.Forget(slot.Name);
        UpdateLive();
        if (clean && !(slot.Recalled && !slot.ByeReceived))
        {
            Post(GameEvent.Notice($"[hive] {slot.Number} logged out ({slot.RecallReason ?? reason})", EventPriority.Background));
        }
        else
        {
            int stranded = 0;
            try { stranded = Store?.MarkStranded(slot.Name, reason, inv) ?? 0; }
            catch (Exception ex) { _log.LogWarning(ex, "hive: couldn't record {Clone}'s stranded inventory", slot.Name); }
            var what = slot.State == CloneState.Retired ? $"; retired after {slot.Crashes.Count} crashes" : restart ? $"; restarting in {backoff.TotalSeconds:0}s" : "";
            Post(GameEvent.Notice($"[hive] {slot.Number} lost ({reason}): {released} claim(s) released" +
                (stranded > 0 ? $", {stranded} item(s) stranded" : "") + what,
                slot.State == CloneState.Retired ? EventPriority.Low : EventPriority.Background));
        }
        gone?.TrySetResult();
        if (restart) _ = RestartAfterAsync(slot, backoff);
    }

    private async Task RestartAfterAsync(CloneSlot slot, TimeSpan backoff)
    {
        try { await Task.Delay(backoff, _time, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        lock (slot.Lock)
        {
            if (slot.State != CloneState.Backoff || slot.Recalled || _shuttingDown) { if (slot.State == CloneState.Backoff) slot.State = CloneState.Idle; return; }
            if (slot.Task != null) slot.Task = slot.Task with { Restarted = true };
            if (!SlotPolicy.CanSpawn(SlotFacts(), Regulars, 0, _cfg.MaxPlayersFallback).Ok || Ledger.AtRecallLine(out _))
            {
                slot.State = CloneState.Idle;
                Post(GameEvent.Notice($"[hive] {slot.Number} not restarted (no slot/budget)", EventPriority.Background));
                return;
            }
        }
        _log.LogInformation("hive: restarting {Clone} after {S:0}s backoff", slot.Name, backoff.TotalSeconds);
        Launch(slot);
    }

    private void UpdateLive() => Arbiter.SetLive(_slots.Values.Where(s => s.IsLive).Select(s => s.Name));

    // ================================================================ messages from a clone

    private async Task<object?> HandleAsync(CloneSlot slot, int gen, HiveMessage m, CancellationToken ct)
    {
        if (gen != slot.Generation) return null;
        switch (m.T)
        {
            case Msg.Hello:
                {
                    var stranded = Store?.Stranded(slot.Name);
                    lock (slot.Lock)
                    {
                        slot.Connected = true;
                        if (slot.State == CloneState.Starting) slot.State = CloneState.Running;
                    }
                    UpdateLive();
                    _log.LogInformation("hive: {Clone} connected", slot.Name);
                    return new WelcomeMsg(_main, slot.Task, Arbiter.ToRoster(), Claims.Mirror(), stranded != null, stranded?.Count ?? 0);
                }
            case Msg.Heartbeat:
                return null;
            case Msg.Snapshot:
                if (m.D is { ValueKind: JsonValueKind.String } sj && BotSnapshot.FromJson(sj.GetString()!) is { } snap) slot.Snapshot = snap;
                return null;
            case Msg.Usage:
                if (m.D is { ValueKind: JsonValueKind.String } uj && JsonSerializer.Deserialize<PromptUsageRecord>(uj.GetString()!, BotSnapshot.Json) is { } rec)
                    slot.RaiseUsage(rec);
                return null;
            case Msg.Progress when m.As<ProgressMsg>() is { } p:
                slot.LastProgress = p.Text;
                if (p.Pct is { } pct) slot.Progress = Math.Clamp(pct, 0, 1);
                Post(GameEvent.Notice($"[hive] {slot.Number}: {Trunc(p.Text, 160)}", EventPriority.Background));
                return null;
            case Msg.Result when m.As<ResultMsg>() is { } r:
                slot.Progress = 1;
                Post(GameEvent.Notice($"[hive] {slot.Number} {(r.Ok ? "done" : "failed")}: {Trunc(r.Text, 200)}", EventPriority.Low));
                return null;
            case Msg.Claim when m.As<ClaimMsg>() is { } c:
                {
                    if (!ClaimKey.TryParse(c.Key, out var key)) throw new InvalidOperationException("bad claim key " + c.Key);
                    var res = Claims.TryClaim(slot.Name, key, c.TtlSeconds > 0 ? TimeSpan.FromSeconds(c.TtlSeconds) : null, c.Note);
                    return new ClaimReplyMsg(res.Ok, res.Holder, res.Expires.ToUnixTimeMilliseconds());
                }
            case Msg.Release when m.As<ReleaseMsg>() is { } rl:
                if (ClaimKey.TryParse(rl.Key, out var rk)) Claims.Release(slot.Name, rk);
                return null;
            case Msg.SpendAuth when m.As<SpendAuthMsg>() is { } sa:
                {
                    var g = Ledger.Authorize(slot.Name, sa.EstimateUsd);
                    if (!g.Ok) _log.LogInformation("hive: refused spend for {Clone}: {Why}", slot.Name, g.Reason);
                    return g.ToMsg();
                }
            case Msg.Spend when m.As<SpendMsg>() is { } sp:
                Ledger.Record(slot.Name, sp.Usd);
                return null;
            case Msg.ChatAccepted when m.As<ChatAcceptedMsg>() is { } ca:
                Arbiter.Accept(ca.Player, slot.Name);
                return null;
            case Msg.Inventory when m.As<InventoryMsg>() is { } inv:
                slot.Inventory = inv.Items;
                return null;
            case Msg.Death when m.As<DeathMsg>() is { } dm:
                {
                    slot.Deaths++;
                    int n = Claims.ReleaseAll(slot.Name);
                    slot.Inventory = [];
                    // Main's context and state stay untouched: logged and counted only (the clone records the shared experience).
                    _log.LogWarning("hive: {Clone} died ({Cause}); lost {Lost}; {N} claim(s) released", slot.Name, dm.Cause, HiveStore.FormatItems(dm.Lost), n);
                    return null;
                }
            case Msg.Handoff when m.As<HandoffMsg>() is { } ho:
                {
                    if (Handoff is not { Position: { } pos } h) return new HandoffReplyMsg(false, 0, 0, 0, "main can't take items");
                    h.BeginCounting(slot.Name);
                    return new HandoffReplyMsg(true, pos.X, pos.Y, pos.Z, null);
                }
            case Msg.HandoffAck:
                return new HandoffAckMsg(Handoff?.Collected(slot.Name) ?? 0);
            case Msg.Bye when m.As<ByeMsg>() is { } bye:
                lock (slot.Lock) { slot.ByeReceived = true; slot.RecallReason ??= bye.Reason; }
                _log.LogInformation("hive: {Clone} bye ({Reason}, deposited {Dep}, stranded {N})", slot.Name, bye.Reason, bye.Deposited, bye.Stranded);
                if (!bye.Deposited && bye.Stranded > 0)
                    Post(GameEvent.Notice($"[hive] {slot.Number} logged out holding {bye.Stranded} item(s): no storage space", EventPriority.Low));
                return null;
            default:
                await Task.CompletedTask.ConfigureAwait(false);
                return null;
        }
    }

    // ================================================================ broadcasts, heartbeats, policies

    /// <summary>Sends a notification to every connected clone.</summary>
    public async Task BroadcastAsync(string type, object? payload)
    {
        foreach (var s in _slots.Values)
            if (s.Link is { IsOpen: true } l && s.Connected) await l.SendAsync(type, payload).ConfigureAwait(false);
    }

    private async Task PushClaimsAsync()
    {
        if (Interlocked.Exchange(ref _claimsPush, 1) == 1) return;
        try { await Task.Delay(50).ConfigureAwait(false); Volatile.Write(ref _claimsPush, 0); await BroadcastAsync(Msg.Claims, Claims.Mirror()).ConfigureAwait(false); }
        finally { Volatile.Write(ref _claimsPush, 0); }
    }

    private async Task PushRosterAsync()
    {
        if (Interlocked.Exchange(ref _rosterPush, 1) == 1) return;
        try { await Task.Delay(20).ConfigureAwait(false); Volatile.Write(ref _rosterPush, 0); await BroadcastAsync(Msg.Roster, Arbiter.ToRoster()).ConfigureAwait(false); }
        finally { Volatile.Write(ref _rosterPush, 0); }
    }

    /// <summary>Forward a console /as line so every live clone's arbiter sees the same message.</summary>
    public Task InjectAsync(string raw) => BroadcastAsync(Msg.Inject, new InjectMsg(raw));

    private void Tick()
    {
        var now = Now;
        foreach (var s in _slots.Values)
        {
            HiveLink? l;
            ICloneProcess? p;
            CloneState st;
            lock (s.Lock) { l = s.Link; p = s.Process; st = s.State; }
            if (st is CloneState.Running or CloneState.Stopping && l is { IsOpen: true })
            {
                _ = l.SendAsync(Msg.Heartbeat);
                if (now - l.LastReceived > _cfg.HeartbeatTimeout)
                {
                    _log.LogWarning("hive: {Clone} missed heartbeats for {S:0}s; killing it", s.Name, (now - l.LastReceived).TotalSeconds);
                    p?.Kill();
                }
            }
        }
    }

    private async Task PolicyAsync()
    {
        try
        {
            await CheckBudgetAsync().ConfigureAwait(false);
            var running = _slots.Values.Where(s => s.State == CloneState.Running && s.Connected).ToList();
            if (running.Count == 0) return;
            var yield = SlotPolicy.MustYield(SlotFacts(), Regulars, running.Select(s => new CloneProgress(s.Name, s.Progress, s.Started)), _cfg.MaxPlayersFallback);
            foreach (var n in yield)
            {
                _log.LogInformation("hive: {Clone} logs out to free a slot for a regular", n);
                RecallSlot(_slots[n], "making room for a player");
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "hive policy check failed"); }
    }

    private Task CheckBudgetAsync()
    {
        if (!_slots.Values.Any(s => s.State is CloneState.Running or CloneState.Starting or CloneState.Backoff)) return Task.CompletedTask;
        if (Ledger.AtRecallLine(out var why)) RecallBudget(why);
        return Task.CompletedTask;
    }

    /// <summary>Budget mode dropped (or the recall line was hit): clones go first, furthest from finishing first.</summary>
    public void RecallBudget(string why)
    {
        var order = SlotPolicy.YieldOrder(_slots.Values.Where(s => s.State is CloneState.Running or CloneState.Starting or CloneState.Backoff)
            .Select(s => new CloneProgress(s.Name, s.Progress, s.Started))).ToList();
        if (order.Count == 0) return;
        _log.LogWarning("hive: budget ({Why}): recalling {Clones}", why, string.Join(", ", order.Select(o => o.Name)));
        foreach (var c in order) RecallSlot(_slots[c.Name], "budget");
        Post(GameEvent.Notice($"[hive] budget: recalling {string.Join(", ", order.Select(o => _slots[o.Name].Number))}", EventPriority.Background));
    }

    /// <summary>Order in which clones are recalled when the budget drops (exposed for tests and list output).</summary>
    public IReadOnlyList<string> BudgetRecallOrder() =>
        SlotPolicy.YieldOrder(_slots.Values.Where(s => s.State is CloneState.Running or CloneState.Starting)
            .Select(s => new CloneProgress(s.Name, s.Progress, s.Started))).Select(c => c.Name).ToList();

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;
        _tick.Dispose();
        _policy.Dispose();
        _cts.Cancel();
        foreach (var s in _slots.Values)
        {
            s.Process?.Kill();
            if (s.Link is { } l) await l.DisposeAsync().ConfigureAwait(false);
        }
    }
}
