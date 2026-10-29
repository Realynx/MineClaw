using System.Globalization;
using MineClaw.Agent.Events;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.Bodies;

/// <summary>The answer to <see cref="BodyRegistry.Spawn"/>: the body (already online, or logging in) or why not.</summary>
public sealed record BodySpawnResult(bool Ok, IBody? Body, string Text)
{
    public static BodySpawnResult No(string text) => new(false, null, text);
}

/// <summary>
/// Step 2 of one brain, many bodies: clone bodies in main's process (<c>Bodies:Mode=inproc</c>). Spawn (slot policy over
/// main's tab list, the launch gap, someone else's login, the saved void spot), yield (the 7-player limit), despawn (pause,
/// deposit with main as the receiver, verify empty, log out; leftovers are stranded), the void hold, and shutdown. The rules
/// are the hive supervisor's and CloneHost's, ported; each body runs under its own <see cref="BodyRunner"/>.
/// </summary>
public sealed partial class BodyRegistry
{
    private sealed class Account(string name)
    {
        public string Name { get; } = name;
        public string Short { get; } = Bodies.Body.ShortName(name, false);
        public Body? Body;
        public BodyRunner? Runner;
        public DateTimeOffset Started, GoneAt = DateTimeOffset.MinValue, OccupiedUntil = DateTimeOffset.MinValue, ExternalSince;
        public DateTimeOffset VoidConfirmedUntil = DateTimeOffset.MinValue;
        public (Vec3d At, DateTimeOffset Since)? VoidHold;
        public bool Retired;
        public int Despawning;
        public string? LastExit;
        public long TicksSeen;
        public bool Live => Runner is { IsRunning: true };
    }

    private readonly object _life = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private IBodyHost? _host;
    private BodiesOptions _o = new();
    private ILogger _log = NullLogger.Instance;
    private TimeProvider _time = TimeProvider.System;
    private CancellationTokenSource _stop = new();
    private System.Threading.Timer? _yieldTimer, _tickTimer;
    private readonly object _joinGate = new();
    private DateTimeOffset _nextJoin;
    private volatile bool _shuttingDown;
    private DateTimeOffset _lastTickReport;

    /// <summary>Clone bodies can be spawned (<see cref="Configure"/> was called: <c>Bodies:Mode=inproc</c>).</summary>
    public bool HasLifecycle => _host != null;

    public BodiesOptions Options => _o;

    /// <summary>The clone accounts this registry may spawn, in config order.</summary>
    public IReadOnlyList<string> Accounts { get { lock (_life) return _accounts.Keys.ToArray(); } }

    /// <summary>Raised after a body was added (spawned) or removed (despawned, retired, occupied).</summary>
    public event Action<IBody, bool>? Changed;

    /// <summary>
    /// Turns on the in-process lifecycle: <paramref name="accounts"/> are the clone logins (<c>Bodies:Accounts</c>). With
    /// <paramref name="timers"/> the slot policy checks every <see cref="BodiesOptions.YieldCheckSeconds"/> and the per-body
    /// "tick behind" counts are logged (tests drive <see cref="YieldTick"/> / <see cref="ReportTicks"/> by hand).
    /// </summary>
    public void Configure(IBodyHost host, IEnumerable<string> accounts, BodiesOptions? options = null, ILogger? log = null,
        TimeProvider? time = null, bool timers = true)
    {
        lock (_life)
        {
            _host = host;
            _o = options ?? new BodiesOptions();
            _log = log ?? NullLogger.Instance;
            _time = time ?? TimeProvider.System;
            foreach (var a in accounts)
                if (!string.IsNullOrWhiteSpace(a) && !a.Trim().Equals(Main.Name, StringComparison.OrdinalIgnoreCase))
                    _accounts.TryAdd(a.Trim(), new Account(a.Trim()));
        }
        if (!timers) return;
        if (_o.YieldCheckSeconds > 0)
        {
            var every = TimeSpan.FromSeconds(_o.YieldCheckSeconds);
            _yieldTimer = new System.Threading.Timer(_ => { try { YieldTick(); } catch (Exception ex) { _log.LogWarning(ex, "bodies: slot check failed"); } },
                null, every, every);
        }
        if (_o.TickReportSeconds > 0)
        {
            var every = TimeSpan.FromSeconds(_o.TickReportSeconds);
            _tickTimer = new System.Threading.Timer(_ => { try { ReportTicks(); } catch (Exception) { } }, null, every, every);
        }
    }

    /// <summary>
    /// A dynamic account (a subhive body main just allocated): spawnable from now on. False when it is main, already known, or the
    /// lifecycle is off.
    /// </summary>
    public bool AddAccount(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals(Main.Name, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_life) return _host != null && _accounts.TryAdd(name.Trim(), new Account(name.Trim()));
    }

    /// <summary>Forgets an account that is offline (released for good: main reuses or retires it). False while it is live.</summary>
    public bool RemoveAccount(string name)
    {
        lock (_life)
        {
            var n = Resolve(name);
            if (n == null || _accounts[n].Live) return false;
            return _accounts.Remove(n);
        }
    }

    /// <summary>The body built for an account (online or not), or null before its first spawn.</summary>
    public Body? BodyOf(string name) => Acc(name)?.Body;

    private DateTimeOffset Now => _time.GetUtcNow();

    private IBodyHost Host => _host ?? throw new InvalidOperationException("in-process bodies are off (Bodies:Mode=hive)");

    /// <summary>A clone account by login or short name ("maid_chan_67" or "67"), or null.</summary>
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().TrimStart('@');
        if (n.StartsWith("clone ", StringComparison.OrdinalIgnoreCase)) n = n[6..].Trim();
        lock (_life)
            return _accounts.Values.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Name
                ?? _accounts.Values.FirstOrDefault(a => a.Short.Equals(n, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private Account? Acc(string? name) { var n = Resolve(name); lock (_life) return n != null ? _accounts[n] : null; }

    /// <summary>The live state of an account (tests, the list): the body's, or Offline/Occupied/Retired when it has none.</summary>
    public BodyState StateOf(string name)
    {
        var a = Acc(name);
        if (a == null) return Find(name)?.State ?? BodyState.Offline;
        if (a.Retired) return BodyState.Retired;
        if (Now < a.OccupiedUntil) return BodyState.Occupied;
        return a.Live && a.Body != null ? a.Body.State : BodyState.Offline;
    }

    /// <summary>The account's runner (tests, telemetry), or null before its first spawn.</summary>
    public BodyRunner? RunnerOf(string name) => Acc(name)?.Runner;

    /// <summary>When an occupied account may be used again (MinValue = it isn't).</summary>
    public DateTimeOffset OccupiedUntil(string name) => Acc(name)?.OccupiedUntil ?? DateTimeOffset.MinValue;

    // ================================================================ spawn

    /// <summary>
    /// Logs a clone body in (by name, or the first free account). Refused while the slot policy says no, someone else is on
    /// the account, it is retired, or its saved login spot is over the void (unless <paramref name="confirmVoid"/>). A body
    /// that is online already is returned as it is. Returns at once; <see cref="WaitOnlineAsync"/> waits for the join.
    /// </summary>
    public BodySpawnResult Spawn(string? name = null, bool confirmVoid = false)
    {
        if (_host == null) return BodySpawnResult.No("error: in-process bodies are off (Bodies:Mode=hive)");
        if (_shuttingDown) return BodySpawnResult.No("error: shutting down");
        Account acc;
        lock (_life)
        {
            if (name != null)
            {
                var a = Acc(name);
                if (a == null) return BodySpawnResult.No($"error: no body called {name} (bodies: {string.Join(", ", _accounts.Values.Select(x => x.Short))})");
                if (a.Live && a.Despawning == 0 && a.Body != null) return new BodySpawnResult(true, a.Body, $"ok: {a.Short} is already online");
                if (Blocker(a, confirmVoid) is { } why)
                {
                    var free = _accounts.Values.Where(x => x != a && !x.Live && Blocker(x, false) == null).Select(x => x.Short).ToList();
                    return BodySpawnResult.No($"no: {a.Short} {why}" + (free.Count > 0 ? $"; {string.Join(", ", free)} {(free.Count == 1 ? "is" : "are")} free" : ""));
                }
                acc = a;
            }
            else
            {
                var pick = _accounts.Values.FirstOrDefault(x => !x.Live && Blocker(x, confirmVoid) == null);
                if (pick == null)
                {
                    var why = string.Join("; ", _accounts.Values.Where(x => !x.Live).Select(x => $"{x.Short} {Blocker(x, confirmVoid)}"));
                    return BodySpawnResult.No("no: no free body" + (why.Length > 0 ? ": " + why : " (all online)"));
                }
                acc = pick;
            }
            // Pending: logging in, not in game (and not on the tab list) yet.
            var slot = Host.CanSpawn(_accounts.Values.Count(x => x.Live && x.Body is { } pb && !pb.Adapter.IsPlaying));
            if (!slot.Ok) return BodySpawnResult.No($"no: {slot.Reason}");
            try { acc.Body ??= Host.Create(acc.Name); }
            catch (Exception ex)
            {
                _log.LogError(ex, "bodies: couldn't build {Name}", acc.Name);
                return BodySpawnResult.No($"error: couldn't build {acc.Short}: {ex.Message}");
            }
            StartLocked(acc);
        }
        _log.LogInformation("bodies: spawning {Name}", acc.Name);
        return new BodySpawnResult(true, acc.Body, $"ok: {acc.Short} spawning");
    }

    /// <summary>Why an offline account can't be logged in now (null = free).</summary>
    private string? Blocker(Account a, bool confirmVoid)
    {
        var now = Now;
        if (a.Retired) return $"is retired for this session ({a.LastExit ?? "crashed too often"})";
        if (now < a.OccupiedUntil)
            return $"is in use by a player's viewer (someone else logged in as {a.Name}); left alone for {Math.Max(1, (int)Math.Ceiling((a.OccupiedUntil - now).TotalMinutes))} more min";
        if (!a.Live && now - a.GoneAt >= TimeSpan.FromSeconds(Math.Max(0, _o.OwnLogoutGraceSeconds)) && OnServer(a.Name))
        {
            if (a.ExternalSince == default)
            {
                a.ExternalSince = now;
                Post(GameEvent.Notice($"[bodies] {a.Short} is in use by a player's viewer ({a.Name} is online but isn't one of ours); using other bodies",
                    EventPriority.Background));
            }
            return $"is in use by a player's viewer ({a.Name} is online but isn't one of ours)";
        }
        a.ExternalSince = default;
        if (!confirmVoid && now >= a.VoidConfirmedUntil && SafeVoidSpot(a.Name) is { } spot)
            return $"would log in over the void at {spot} (its saved login spot): ask a trusted player to /tp {a.Name} once it's in, then spawn it again";
        return null;
    }

    private bool OnServer(string name)
    {
        try { return Host.Slots().Online.Contains(name, StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    private string? SafeVoidSpot(string name)
    {
        try { return Host.VoidSpot(name); }
        catch (Exception) { return null; }
    }

    /// <summary>A trusted player said they'll /tp this body: its void login spot doesn't block a spawn for a while.</summary>
    public bool ConfirmVoid(string name, TimeSpan? window = null)
    {
        var a = Acc(name);
        if (a == null) return false;
        a.VoidConfirmedUntil = Now + (window ?? TimeSpan.FromMinutes(10));
        return true;
    }

    // caller holds _life
    private void StartLocked(Account acc)
    {
        var body = acc.Body!;
        var host = Host;
        acc.Started = Now;
        acc.LastExit = null;
        acc.VoidHold = null;
        body.Started = acc.Started;
        body.StateOverride = BodyState.Connecting;
        if (body.Orders is { } orders) orders.Held = false;
        bool added = false;
        lock (_lock)
            if (!_bodies.Contains(body)) { _bodies.Add(body); added = true; }
        if (added) RaiseChanged(body, true);
        var runner = new BodyRunner(acc.Name, new BodySession(
                ct => WaitTurnAsync(ct),
                ct => host.ConnectAsync(body, ct),
                ct => host.RunAsync(body, ct),
                () => host.DisconnectAsync(body, "restarting")),
            _o, _log, _time);
        runner.StateChanged += s =>
        {
            if (Volatile.Read(ref acc.Despawning) != 0) return; // logging out: Yielding until it's gone
            body.StateOverride = s == BodyState.Online ? null : s;
        };
        runner.Crashed += (ex, retired) => OnCrashed(acc, ex, retired);
        runner.Ended += exit => { if (exit.Kind == BodyExitKind.ExternalLogin) _ = OnExternalLoginAsync(acc, exit.Reason); };
        acc.Runner = runner;
        runner.Start(_stop.Token);
    }

    /// <summary>
    /// The server's login throttle rejects joins from one IP less than ~4 s apart: logins (and reconnects) are spaced by
    /// <see cref="BodiesOptions.LaunchGapSeconds"/>. Returns how long this login waits for its turn.
    /// </summary>
    public TimeSpan JoinDelay()
    {
        var gap = TimeSpan.FromSeconds(Math.Max(0, _o.LaunchGapSeconds));
        lock (_joinGate)
        {
            var now = Now;
            var at = _nextJoin > now ? _nextJoin : now;
            _nextJoin = at + gap;
            return at - now;
        }
    }

    private async Task WaitTurnAsync(CancellationToken ct)
    {
        var wait = JoinDelay();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
    }

    /// <summary>Waits until the body is in game (true) or <paramref name="timeout"/> / it stopped trying (false).</summary>
    public async Task<bool> WaitOnlineAsync(IBody body, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            if (body.Adapter.IsPlaying && body.State == BodyState.Online) return true;
            if (Acc(body.Name) is { } a && !a.Live) return false;
            try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        return body.Adapter.IsPlaying;
    }

    // ================================================================ crashes and someone else's login

    private void OnCrashed(Account acc, Exception ex, bool retired)
    {
        var why = ex.Message.Length > 80 ? ex.Message[..79] + "…" : ex.Message;
        if (!retired)
        {
            Post(GameEvent.Notice($"[bodies] {acc.Short} crashed ({why}); restarting it", EventPriority.Background) with { Body = acc.Short });
            return;
        }
        acc.Retired = true;
        acc.LastExit = $"crashed {acc.Runner?.Crashes.Count ?? 0}x: {why}";
        if (acc.Body is { } b)
        {
            PauseJob(b, "retired");
            if (Remove(b)) RaiseChanged(b, false);
            b.StateOverride = BodyState.Retired;
        }
        Post(GameEvent.Notice($"[bodies] {acc.Short} crashed {acc.Runner?.Crashes.Count}x in {_o.CrashWindowMinutes:0} min ({why}): retired for this session",
            EventPriority.Low) with { Body = acc.Short });
    }

    private async Task OnExternalLoginAsync(Account acc, string reason)
    {
        var body = acc.Body;
        acc.OccupiedUntil = Now + TimeSpan.FromMinutes(Math.Max(0, _o.ExternalLoginCooldownMinutes));
        acc.GoneAt = Now;
        acc.LastExit = "someone else logged in";
        if (body == null) return;
        bool hadWork = body.Activities.Current != null || body.Scripts?.Current != null || body.Orders?.Current != null;
        PauseJob(body, "someone else logged in as " + body.Name);
        int items = ItemCount(body);
        try { Host.Stranded(body, "kicked: someone else logged in as " + body.Name, items); } catch (Exception ex) { _log.LogWarning(ex, "bodies: stranded record failed"); }
        // Never fight the login: no reconnect (a reconnect would kick the player back out).
        try { await Host.DisconnectAsync(body, "kicked").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        if (Remove(body)) RaiseChanged(body, false);
        body.StateOverride = BodyState.Occupied;
        _log.LogWarning("bodies: {Name} kicked, someone else logged in with it ({Reason}); left alone for {M:0} min", body.Name, reason, _o.ExternalLoginCooldownMinutes);
        Post(GameEvent.Notice($"[bodies] {acc.Short} is in use by a player's viewer: someone logged in as {acc.Name} and the server kicked ours; " +
            $"not using it for {_o.ExternalLoginCooldownMinutes:0} min" + (items > 0 ? $" ({items} item(s) still on it)" : "") +
            (hadWork ? "; its job is unfinished: give it to another body" : ""), EventPriority.Normal) with { MustWake = hadWork, Body = acc.Short });
    }

    // ================================================================ yield and despawn

    /// <summary>The slot check (timer): bodies that must make room for an offline regular log out, furthest from done first.</summary>
    public IReadOnlyList<string> YieldTick()
    {
        if (_host == null || _shuttingDown) return [];
        List<BodyProgress> running;
        lock (_life)
            running = _accounts.Values.Where(a => a.Live && a.Despawning == 0 && a.Body is { } b && b.Adapter.IsPlaying)
                .Select(a => new BodyProgress(a.Name, a.Body!.Progress, a.Started)).ToList();
        if (running.Count == 0) return [];
        var yield = Host.MustYield(running);
        foreach (var n in yield)
        {
            _log.LogInformation("bodies: {Name} logs out to free a slot for a regular", n);
            _ = DespawnAsync(n, "making room for a player");
        }
        return yield;
    }

    /// <summary>
    /// The logout gate: pause the job (its standing order and lane checkpoint stay), deposit everything (main takes what
    /// storage can't), check the inventory is empty, log out. Leftovers are recorded as stranded. Returns one line.
    /// </summary>
    public async Task<string> DespawnAsync(string name, string reason, TimeSpan? timeout = null)
    {
        var acc = Acc(name);
        if (acc?.Body is not { } body || !acc.Live) return $"{acc?.Short ?? name} isn't online";
        if (Interlocked.Exchange(ref acc.Despawning, 1) != 0) return $"{acc.Short} is already logging out";
        try
        {
            body.StateOverride = BodyState.Yielding;
            PauseJob(body, "logging out: " + reason);
            var limit = timeout ?? TimeSpan.FromSeconds(_o.DepositTimeoutSeconds);
            BodyDeposit report;
            if (!body.Adapter.IsPlaying) report = BodyDeposit.Nothing(ItemCount(body), "not in game");
            else
            {
                try
                {
                    using var dcts = new CancellationTokenSource(limit + TimeSpan.FromSeconds(5));
                    report = await Host.DepositAsync(body, limit, dcts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "bodies: {Name}: deposit failed", body.Name);
                    report = BodyDeposit.Nothing(ItemCount(body), "deposit failed: " + ex.Message);
                }
            }
            // Never log out on a stale "empty": the inventory itself decides.
            int left = ItemCount(body);
            if (left > 0 && report.Empty) report = report with { Empty = false, Remaining = left, Text = $"{left} item(s) still held" };
            try { Host.Stranded(body, "logout: " + reason, report.Empty ? 0 : report.Remaining); } catch (Exception ex) { _log.LogWarning(ex, "bodies: stranded record failed"); }
            _log.LogInformation("bodies: {Name}: deposit before logout: {Text}", body.Name, report.Text);
            if (acc.Runner is { } runner) await runner.StopAsync().ConfigureAwait(false);
            try { await Host.DisconnectAsync(body, "Bye!").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
            try { if (body.Scripts?.Current != null) body.Scripts.Cancel("logged out"); } catch (Exception) { } // its checkpoint stays
            acc.GoneAt = Now;
            acc.LastExit = reason;
            if (Remove(body)) RaiseChanged(body, false);
            body.StateOverride = BodyState.Offline;
            Post(GameEvent.Notice($"[bodies] {acc.Short} logged out ({reason}): {report.Text}",
                report.Empty ? EventPriority.Background : EventPriority.Low) with { Body = acc.Short });
            return $"ok: {acc.Short} logged out ({reason}): {report.Text}";
        }
        finally { Volatile.Write(ref acc.Despawning, 0); }
    }

    /// <summary>Main shutting down: every clone body deposits and logs out in parallel, within <paramref name="timeout"/>.</summary>
    public async Task DespawnAllAsync(string reason, TimeSpan timeout)
    {
        _shuttingDown = true;
        List<string> live;
        lock (_life) live = _accounts.Values.Where(a => a.Live).Select(a => a.Name).ToList();
        if (live.Count == 0) return;
        var each = TimeSpan.FromSeconds(Math.Max(1, timeout.TotalSeconds - 2));
        var all = Task.WhenAll(live.Select(n => DespawnAsync(n, reason, each)));
        try { await all.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { _log.LogWarning("bodies: shutdown recall timed out after {S:0}s", timeout.TotalSeconds); }
        catch (Exception ex) { _log.LogWarning(ex, "bodies: shutdown recall failed"); }
    }

    /// <summary>Stops every runner without a deposit (after <see cref="DespawnAllAsync"/>, or in tests) and the timers.</summary>
    public async Task StopAllAsync()
    {
        _shuttingDown = true;
        _yieldTimer?.Dispose();
        _tickTimer?.Dispose();
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        List<Account> live;
        lock (_life) live = _accounts.Values.Where(a => a.Runner != null).ToList();
        foreach (var a in live)
        {
            try { await a.Runner!.StopAsync().ConfigureAwait(false); } catch (Exception) { }
            if (a.Body is { } b && _host != null)
                try { await _host.DisconnectAsync(b, "Bye!").WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    /// <summary>Stops the job without forgetting it: the activity is cancelled, the script lane paused, the order held.</summary>
    internal static void PauseJob(IBody body, string why)
    {
        try { body.Activities.Cancel(why); } catch (Exception) { }
        try { if (body.Scripts?.Current is { } s && s.Status == MineClaw.Scripting.ScriptStatus.Running) body.Scripts.Pause(why); } catch (Exception) { }
        if (body.Orders is { } o) o.Held = true;
    }

    internal static void ResumeJob(IBody body)
    {
        if (body.Orders is { } o) o.Held = false;
        try { if (body.Scripts?.Current is { } s && s.Status == MineClaw.Scripting.ScriptStatus.Paused) body.Scripts.Resume(); } catch (Exception) { }
    }

    private static int ItemCount(IBody body)
    {
        try
        {
            var w = body.Adapter.World;
            int n = 0;
            var slots = w.Inventory.PlayerSlots();
            for (int i = 1; i < slots.Length; i++) if (!slots[i].IsEmpty) n += slots[i].Count; // 0 = crafting output
            if (!w.Inventory.Cursor.IsEmpty) n += w.Inventory.Cursor.Count;
            return n;
        }
        catch (Exception) { return 0; }
    }

    // ================================================================ the void hold (ported from CloneHost.OnVoidHoldAsync)

    /// <summary>
    /// The body's client is holding still over the void (it never falls, it's never kicked). Once per hold: the spot is saved
    /// (no login there again without a player's word), the trusted players are asked for a /tp on main's account, the brain is
    /// told, and the job waits for the landing. After <see cref="BodiesOptions.VoidHoldMinutes"/> it logs out.
    /// </summary>
    public void OnVoidHold(IBody body, Vec3d at)
    {
        var acc = Acc(body.Name);
        if (acc == null || _host == null || Volatile.Read(ref acc.Despawning) != 0) return;
        DateTimeOffset since;
        lock (acc)
        {
            if (acc.VoidHold is not null) return;
            since = Now;
            acc.VoidHold = (at, since);
        }
        var spot = Fmt(at);
        bool spawned = acc.Started == default || since - acc.Started < TimeSpan.FromMinutes(2);
        _log.LogWarning("bodies: {Name} is holding over the void at {At}; asking for a /tp", body.Name, spot);
        try { Host.MarkVoid(body.Name, at); } catch (Exception ex) { _log.LogDebug(ex, "void spot record failed"); }
        PauseJob(body, "holding over the void");
        var who = SafeTrusted();
        var line = (who.Count > 0 ? string.Join(", ", who) + ": " : "") +
                   $"{acc.Short} {(spawned ? "spawned" : "ended up")} over the void at {spot}, can someone /tp {body.Name} to me?";
        _ = SayAsync(line);
        Post(GameEvent.Notice($"[bodies] {acc.Short} {(spawned ? "spawned" : "ended up")} over the void at {spot} and is holding still (safe, can't move); " +
            $"asked for /tp {body.Name} {Main.Name}; its job waits for the landing", EventPriority.Normal) with { Body = acc.Short });
        _ = VoidTimeoutAsync(acc, since);
    }

    /// <summary>A /tp (or a block below) put the body on verified ground: the mark goes, the job resumes.</summary>
    public void OnVoidHoldEnded(IBody body, Vec3d at)
    {
        var acc = Acc(body.Name);
        if (acc == null) return;
        lock (acc)
        {
            if (acc.VoidHold is null) return;
            acc.VoidHold = null;
        }
        _log.LogInformation("bodies: {Name} on ground again at {At} after the void hold; resuming", body.Name, Fmt(at));
        try { _host?.ClearVoid(body.Name); } catch (Exception ex) { _log.LogDebug(ex, "void spot clear failed"); }
        ResumeJob(body);
        Post(GameEvent.Notice($"[bodies] {acc.Short} landed on ground at {Fmt(at)} after the void hold; its job resumes", EventPriority.Background) with { Body = acc.Short });
    }

    /// <summary>The body is holding over the void now (where), or null.</summary>
    public Vec3d? VoidHoldOf(string name) => Acc(name) is { } a ? a.VoidHold?.At : null;

    private async Task VoidTimeoutAsync(Account acc, DateTimeOffset since)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(0.01, _o.VoidHoldMinutes)), _time, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        Vec3d where;
        lock (acc)
        {
            if (acc.VoidHold is not { } v || v.Since != since) return; // landed (or a newer hold) meanwhile
            where = v.At;
        }
        Post(GameEvent.Notice($"[bodies] {acc.Short} stuck over the void at {Fmt(where)} for {_o.VoidHoldMinutes:0} min, nobody /tp'd it; logging it out " +
            "(that spot is saved: it would spawn there again)", EventPriority.Normal) with { MustWake = true, Body = acc.Short });
        await DespawnAsync(acc.Name, "stuck over the void").ConfigureAwait(false);
    }

    private IReadOnlyList<string> SafeTrusted()
    {
        try { return Host.Trusted().Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception) { return []; }
    }

    private async Task SayAsync(string line)
    {
        try { await Host.SayAsync(line).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "bodies: chat line failed"); }
    }

    private static string Fmt(Vec3d p) => string.Create(CultureInfo.InvariantCulture,
        $"{(int)Math.Floor(p.X)},{(int)Math.Floor(p.Y)},{(int)Math.Floor(p.Z)}");

    // ================================================================ status

    /// <summary>One line per account for the brain's <c>hive list</c>: online with its job and vitals, or why it's off.</summary>
    public string List()
    {
        List<Account> all;
        lock (_life) all = _accounts.Values.ToList();
        if (all.Count == 0) return "no clone bodies configured";
        var now = Now;
        var parts = new List<string>();
        foreach (var a in all)
        {
            if (a.Retired) { parts.Add($"{a.Short} retired ({a.LastExit})"); continue; }
            if (now < a.OccupiedUntil) { parts.Add($"{a.Short} in use by a player's viewer ({(int)Math.Ceiling((a.OccupiedUntil - now).TotalMinutes)} min)"); continue; }
            if (a.Live && a.Body is { } b)
            {
                if (b.State is BodyState.Connecting) { parts.Add($"{a.Short} logging in"); continue; }
                if (b.State is BodyState.Yielding) { parts.Add($"{a.Short} logging out"); continue; }
                var p = b.Adapter.World.Player;
                string job = b.Activities.Describe() ?? (b.Scripts?.Current is { } s ? "script " + s.Name : null) ?? "idle";
                string hold = a.VoidHold is { } v ? $" HOLDING over the void at {Fmt(v.At)}" : "";
                parts.Add($"{a.Short} {job} @{Fmt(p.Position)} hp{p.Health} f{p.Food}{hold}");
                continue;
            }
            parts.Add(SafeVoidSpot(a.Name) is { } spot ? $"{a.Short} off (login spot over the void at {spot})" : $"{a.Short} off");
        }
        return string.Join(" | ", parts);
    }

    /// <summary>Per-body "tick behind" counts (GC pauses / CPU starvation stall every body at once): logged when they grew.</summary>
    public IReadOnlyList<string> ReportTicks()
    {
        var lines = new List<string>();
        var now = Now;
        var span = _lastTickReport == default ? TimeSpan.Zero : now - _lastTickReport;
        _lastTickReport = now;
        foreach (var b in All)
        {
            if (b.Client is not { } c) continue;
            Account? a = Acc(b.Name);
            long total = c.TicksBehind;
            long seen = a?.TicksSeen ?? MainTicksSeen;
            if (a != null) a.TicksSeen = total; else MainTicksSeen = total;
            long worst = c.ResetMaxTickLate();
            if (total <= seen) continue;
            var line = $"{b.Short} {total - seen} tick(s) behind (worst {worst} ms)";
            lines.Add(line);
        }
        if (lines.Count > 0)
            _log.LogInformation("tick behind{Span}: {Lines}", span > TimeSpan.Zero ? $" in {span.TotalSeconds:0}s" : "", string.Join("; ", lines));
        return lines;
    }

    private long MainTicksSeen;

    private void Post(GameEvent e)
    {
        try { _host?.Post(e); } catch (Exception ex) { _log.LogDebug(ex, "bodies: notice failed"); }
    }

    private void RaiseChanged(IBody b, bool added)
    {
        try { Changed?.Invoke(b, added); } catch (Exception ex) { _log.LogDebug(ex, "bodies: change handler failed"); }
    }
}
