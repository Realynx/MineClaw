using System.Collections.Concurrent;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Client;
using MineClaw.Client.Versioning;
using MineClaw.Data.Versioning;
using MineClaw.Hive;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Hosting;

/// <summary>Everything one clone run needs (<see cref="CloneMode.RunAsync(CloneRun)"/>): its own options, logs and scope.</summary>
public sealed class CloneRun
{
    public required string Clone { get; init; }
    /// <summary>Main's address: a pipe name (clone process) or an in-memory address (<see cref="Transport"/>).</summary>
    public required string Parent { get; init; }
    public required MinecraftSettings Mc { get; init; }
    public required AgentOptions Agent { get; init; }
    public required BotOptions Bot { get; init; }
    public required HostSettings Host { get; init; }
    public required HiveConfig Hive { get; init; }
    public required TelemetryOptions Telemetry { get; init; }
    public required string TelemetryDir { get; init; }
    public required ILoggerFactory Loggers { get; init; }
    public required ILogger Log { get; init; }
    /// <summary>The clone's own scope. Cancelled = its planned logout (deposit first): SIGTERM in a process, release or main's
    /// shutdown recall in process. A hive's <c>/quit</c> console command cancels this, never main's.</summary>
    public required CancellationTokenSource Stop { get; init; }
    public GameVersionProfile? Profile { get; init; }
    public MineClaw.Bot.Bodies.BodiesOptions? Bodies { get; init; }
    /// <summary>Null = named pipes.</summary>
    public IHiveTransport? Transport { get; init; }
    /// <summary>In main's process: the kill (no logout gate, straight to the teardown). A process has the OS kill instead.</summary>
    public CancellationToken Kill { get; init; }
    public Func<string?>? KillReason { get; init; }
    /// <summary>The in-process watchdog's view of this hive (null in a clone process: main's heartbeat probe covers it).</summary>
    public HiveHealth? Health { get; init; }
    /// <summary>Tests: every client this run creates (its own and its subhive bodies') goes through this (fake servers).</summary>
    public Func<ClientOptions, ClientOptions>? ClientSetup { get; init; }
    /// <summary>Tests: the brain's model client (default: <see cref="LlmClientFactory"/> on its profile).</summary>
    public Func<AgentOptions, ILlmClient>? Llm { get; init; }
}

/// <summary>What a hive in main's process starts from: a fresh binding of the config per hive (main's objects are never shared),
/// prepared as a clone process prepares itself, on the process's one version.</summary>
public sealed class InProcHiveSettings(AppCli cli, Func<AppSettings> bind, GameVersionProfile profile, string mainLogDir)
{
    public GameVersionProfile Profile => profile;
    public string MainLogDir => mainLogDir;
    public AppCli Cli => cli;

    public AppSettings For(string clone)
    {
        var s = bind();
        var (logDir, telemetryDir) = CloneMode.Prepare(clone, s.Mc, s.Agent, s.Hive, s.LogDir, s.TelemetryDir);
        s.ApplyVersion(profile, appliesWorldMode: false); // main's runtime set the process's world mode; a hive only reads it
        return s with { LogDir = logDir, TelemetryDir = telemetryDir };
    }
}

/// <summary>
/// The in-process watchdog's view of one hive (it has no process to kill): when its brain's last model call finished, whether a
/// turn is running, when its snapshot loop last ran, the errors its loggers wrote, and a flag C# raises (the budget gate refused).
/// </summary>
public sealed class HiveHealth
{
    private long _lastModelTicks;
    private string? _flag;

    public CloneHost? Host { get; private set; }
    public BotAgent? Agent { get; private set; }
    public DateTimeOffset AttachedAt { get; private set; }
    public HiveErrorCounter Errors { get; } = new();
    public DateTimeOffset LastModelCall => new(Interlocked.Read(ref _lastModelTicks), TimeSpan.Zero);
    /// <summary>Set once the hive's host runs (its runtime is built): the build gate is released then.</summary>
    public event Action? Attached;

    public void Attach(CloneHost host, BotAgent agent)
    {
        Host = host;
        Agent = agent;
        AttachedAt = DateTimeOffset.UtcNow;
        Attached?.Invoke();
    }

    public void ModelCallEnded() => Interlocked.Exchange(ref _lastModelTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>A reason to stop raised from inside the hive (e.g. "over budget: …"); the watchdog acts on it.</summary>
    public void Flag(string why) => Interlocked.CompareExchange(ref _flag, why, null);

    /// <summary>Null = healthy; else why it should be stopped.</summary>
    public string? Check(InProcHiveConfig cfg, DateTimeOffset now)
    {
        if (Volatile.Read(ref _flag) is { } flagged) return flagged;
        if (Host is not { } host || host.LoggingOut) return null; // not built yet (main's start timeout) / its logout gate runs (the stop grace)
        if (Agent?.TurnStartedAt is { } turn)
        {
            var since = LastModelCall > turn ? LastModelCall : turn;
            if (now - since > TimeSpan.FromSeconds(Math.Max(1, cfg.HangSeconds)))
                return $"hung: a brain turn {(now - since).TotalMinutes:0} min without a model call";
        }
        var streamed = host.LastStreamed > AttachedAt ? host.LastStreamed : AttachedAt;
        if (now - streamed > TimeSpan.FromSeconds(Math.Max(1, cfg.StallSeconds)))
            return $"stalled: its loops silent for {(now - streamed).TotalSeconds:0}s";
        var window = TimeSpan.FromMinutes(Math.Max(0.1, cfg.ErrorWindowMinutes));
        if (cfg.MaxErrors > 0 && Errors.Count(now, window) is var n && n >= cfg.MaxErrors)
            return $"{n} errors in {window.TotalMinutes:0.#} min (last: {Errors.Last ?? "-"})";
        return null;
    }
}

/// <summary>Counts Error/Critical lines a hive's loggers write (its "throws repeatedly" signal); the last one's text for the report.</summary>
public sealed class HiveErrorCounter : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<long> _at = new();
    public string? Last { get; private set; }

    public int Count(DateTimeOffset now, TimeSpan window)
    {
        long from = (now - window).UtcTicks;
        while (_at.TryPeek(out var t) && t < from) _at.TryDequeue(out _);
        return _at.Count;
    }

    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        _at.Enqueue(DateTimeOffset.UtcNow.UtcTicks);
        while (_at.Count > 4096) _at.TryDequeue(out _);
        try { var m = formatter(state, exception); Last = m.Length > 120 ? m[..120] : m; } catch (Exception) { }
    }

    public void Dispose() { }
}

/// <summary>A hive's Error+ lines also go to main's log, prefixed with the hive (a clone process's stderr went there too).</summary>
internal sealed class ForwardLoggerProvider(ILoggerFactory main, string clone, LogLevel min) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Fwd(main.CreateLogger(categoryName), clone, min);
    public void Dispose() { }

    private sealed class Fwd(ILogger inner, string clone, LogLevel min) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= min && inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            inner.Log(logLevel, eventId, "[{Clone}] {Line}", exception, clone, formatter(state, exception));
        }
    }
}

/// <summary>Stamps <see cref="HiveHealth.ModelCallEnded"/> on every finished model call (success or not).</summary>
internal sealed class ProgressLlmClient(ILlmClient inner, HiveHealth health) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        try { return await inner.CompleteAsync(request, ct).ConfigureAwait(false); }
        finally { health.ModelCallEnded(); }
    }
}

/// <summary>
/// One hive running inside main's process: what the supervisor sees as its "process". Its life is one task on the thread pool
/// (<see cref="CloneMode.RunAsync(CloneRun)"/>), started with a clean execution context (nothing ambient of the spawning brain turn
/// flows in: no <c>TurnContext</c>, <c>ToolCaller</c>, <c>BodyScope</c>) and the process's version and world mode scoped explicitly.
/// <see cref="Kill"/> cancels its kill scope (teardown without the logout gate); a task that won't unwind within
/// <see cref="InProcHiveConfig.AbandonSeconds"/> is reported exited and abandoned (logged; it can't take main down, every boundary
/// catches).
/// </summary>
public sealed class InProcHive : ICloneProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _kill = new();
    private readonly InProcHiveConfig _cfg;
    private readonly ILogger _log;
    private int _stopping;
    private int _killed;

    internal InProcHive(int id, string clone, InProcHiveConfig cfg, ILogger log)
    {
        Id = id;
        Name = clone;
        _cfg = cfg;
        _log = log;
    }

    public int Id { get; }
    public string Name { get; }
    public bool InProcess => true;
    public HiveHealth Health { get; } = new();
    public CancellationTokenSource Stop { get; } = new();
    internal CancellationToken KillToken => _kill.Token;
    /// <summary>Why the watchdog stopped it (null = it wasn't).</summary>
    public string? StopReason { get; private set; }
    public DateTimeOffset StopAt { get; private set; }
    public string? KillReason { get; private set; }
    /// <summary>Its task outlived the kill and was abandoned.</summary>
    public bool Abandoned { get; private set; }
    public Task<int>? Run { get; private set; }

    public bool HasExited => _exit.Task.IsCompleted;
    public int? ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : null;
    public Task WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

    internal void Started(Task<int> run)
    {
        Run = run;
        _ = run.ContinueWith(t =>
        {
            if (t.IsFaulted) _log.LogError(t.Exception?.GetBaseException(), "hive {Clone}: crashed (contained in main's process)", Name);
            _exit.TrySetResult(t.IsCompletedSuccessfully ? t.Result : 1);
        }, TaskScheduler.Default);
    }

    /// <summary>The watchdog's stop: the release path (deposit, bodies first, bye, log out) within the grace, then <see cref="Kill"/>.</summary>
    public bool RequestStop(string reason)
    {
        if (HasExited || Interlocked.Exchange(ref _stopping, 1) != 0) return false;
        StopReason = reason;
        StopAt = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromSeconds(Math.Max(1, _cfg.StopGraceSeconds));
        if (Health.Host is { } host)
            _ = Task.Run(() => host.LogoutAsync("watchdog: " + reason, planned: true, TimeSpan.FromSeconds(Math.Max(1, grace.TotalSeconds - 60)) /* its bodies recall first */));
        else Kill("watchdog: " + reason);
        return true;
    }

    /// <summary>Past its stop grace (the watchdog then kills it).</summary>
    public bool StopOverdue(DateTimeOffset now) => Volatile.Read(ref _stopping) != 0 && !HasExited
        && now - StopAt > TimeSpan.FromSeconds(Math.Max(1, _cfg.StopGraceSeconds));

    public void Kill() => Kill("killed by the supervisor");

    /// <summary>
    /// Hard stop: no logout gate. A hive already past its bye (logout done, link closed) is only given its teardown time; else its
    /// kill scope is cancelled and <see cref="CloneMode"/> tears it down at once. Either way the exit is reported within
    /// <see cref="InProcHiveConfig.AbandonSeconds"/>. Never throws.
    /// </summary>
    public void Kill(string reason)
    {
        if (HasExited || Interlocked.Exchange(ref _killed, 1) != 0) return;
        KillReason = reason;
        bool pastBye = Health.Host is { LoggingOut: true } h && h.Link is not { IsOpen: true };
        if (!pastBye)
        {
            _log.LogWarning("hive {Clone}: {Reason}: stopping it hard", Name, reason);
            try { _kill.Cancel(); } catch (Exception) { }
        }
        _ = Task.Run(async () =>
        {
            try { await _exit.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _cfg.AbandonSeconds))).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Abandoned = true;
                _log.LogError("hive {Clone}: its task didn't unwind {S:0}s after the stop; abandoned (it runs on unobserved until it ends)",
                    Name, _cfg.AbandonSeconds);
                _exit.TrySetResult(CloneExitCodes.Watchdog);
            }
            catch (Exception) { }
        });
    }

    public void Dispose() { }
}

/// <summary>
/// <c>Hive:Isolation=inproc</c> (the default): a hive-kind or script clone becomes another agent inside main's process instead of a
/// <c>--clone</c> process. It is the same <see cref="CloneMode.RunAsync(CloneRun)"/> wiring a clone process runs (its own brain
/// loop and model profile, its own client and account, its own subhive bodies, the same <see cref="CloneHost"/> logout gate) on the
/// same shared memory (one SQLite file, writes through main's groomer over the bus), linked to the supervisor over
/// <see cref="MemoryTransport"/> with the same frames, so spawn, task, result, recall, release, vitals, the bus, the arbiter, budget
/// grants, claims, hive scripts, the Supervisor viewer and the Explorer panels all work unchanged. Per hive: its own option objects
/// (<see cref="InProcHiveSettings"/>), its own log files (<c>logs/hive/&lt;account&gt;/</c>), its own telemetry and usage file, its
/// own cancellation scope; brains are async tasks on the thread pool, each client keeps its own reader and tick threads.
/// <para>A watchdog stands in for the process kill (<see cref="WatchAsync"/>): a hang, an error storm or a budget flag stops the
/// hive the way a release does, then hard if that overruns, and main's brain hears why (<see cref="Notice"/>).</para>
/// </summary>
public sealed class InProcHiveLauncher : IProcessLauncher, IAsyncDisposable
{
    private readonly HiveConfig _cfg;
    private readonly Func<string, AppSettings> _settings;
    private readonly GameVersionProfile _profile;
    private readonly ILoggerFactory _mainLoggers;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _builds;
    private readonly ConcurrentDictionary<int, InProcHive> _hives = new();
    private readonly ConcurrentDictionary<string, InProcHive> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watch;
    private int _ids = 1000;

    public InProcHiveLauncher(HiveConfig cfg, Func<string, AppSettings> settings, GameVersionProfile profile, ILoggerFactory mainLoggers, ILogger? log = null)
    {
        _cfg = cfg;
        _settings = settings;
        _profile = profile;
        _mainLoggers = mainLoggers;
        _log = log ?? NullLogger.Instance;
        _builds = new SemaphoreSlim(Math.Max(1, cfg.InProc.MaxConcurrentStarts));
        _watch = Task.Run(() => WatchAsync(_cts.Token));
    }

    /// <summary>The one in-memory transport of this process (main listens, each hive connects).</summary>
    public MemoryTransport Transport { get; } = new();
    public IHiveTransport TransportFor(string clone) => Transport;

    /// <summary>A watchdog stop, for main's brain ("[hive] watchdog stopped 101: hung …").</summary>
    public Action<string, string>? Notice { get; set; }
    /// <summary>Tests: every client a hive creates goes through this (fake servers).</summary>
    public Func<ClientOptions, ClientOptions>? ClientSetup { get; set; }
    /// <summary>Tests: each hive's brain model client.</summary>
    public Func<string, AgentOptions, ILlmClient>? Llm { get; set; }

    /// <summary>Hives started by this launcher that haven't exited.</summary>
    public IReadOnlyList<InProcHive> Live => _hives.Values.Where(h => !h.HasExited).OrderBy(h => h.Id).ToList();
    /// <summary>The latest start of <paramref name="clone"/> (kept after it exits: its exit code, stop and kill reasons).</summary>
    public InProcHive? Find(string clone) => _last.GetValueOrDefault(clone);

    public ICloneProcess Start(string clone, string pipeName)
    {
        // One version per process: every hive in it talks to the same server with the process's tables (GameData.Current).
        var data = GameData.Current;
        if (!string.Equals(data.VersionId, _profile.Data.VersionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"one version per process: this process plays {data.VersionId}, the hive profile is {_profile.Data.VersionId}");
        var world = WorldMode.Current;
        var hive = new InProcHive(Interlocked.Increment(ref _ids), clone, _cfg.InProc, _log);
        _hives[hive.Id] = hive;
        _last[clone] = hive;
        Task<int> run;
        // A clean execution context: the spawning brain turn's ambient state (TurnContext, ToolCaller, BodyScope…) stays with main.
        using (ExecutionContext.SuppressFlow())
            run = Task.Run(() => RunHiveAsync(hive, pipeName, data, world));
        hive.Started(run);
        _ = run.ContinueWith(_ => _hives.TryRemove(hive.Id, out InProcHive? _), TaskScheduler.Default);
        return hive;
    }

    private async Task<int> RunHiveAsync(InProcHive hive, string pipe, IGameData data, WorldMode world)
    {
        using var scopedData = GameData.Override(data);
        using var scopedWorld = WorldMode.Override(world);
        var clone = hive.Name;
        ILoggerFactory? loggers = null;
        FileLoggerProvider? file = null;
        ILogger? log = null;
        int built = 1; // 0 = this hive holds a build slot
        void Release() { if (Interlocked.Exchange(ref built, 1) == 0) _builds.Release(); }
        try
        {
            var s = _settings(clone);
            file = new FileLoggerProvider(s.LogDir);
            loggers = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Debug);
                b.AddProvider(file);                                                   // logs/hive/<account>/: separable as before
                b.AddProvider(hive.Health.Errors);                                     // the watchdog's error storm count
                b.AddProvider(new ForwardLoggerProvider(_mainLoggers, clone, LogLevel.Error)); // like a clone process's stderr
            });
            log = loggers.CreateLogger("MineClaw.App");
            Directory.CreateDirectory(s.TelemetryDir);
            log.LogInformation("hive {Clone} starting in main's process (hive #{Id}, {Version}, log {Log})", clone, hive.Id, data.VersionId, s.LogDir);
            // The runtime build (db, knowledge sync, RAG load) is CPU-heavy: a few at a time; released once the host runs.
            hive.Health.Attached += Release;
            await _builds.WaitAsync(hive.KillToken).ConfigureAwait(false);
            built = 0;
            var run = new CloneRun
            {
                Clone = clone, Parent = pipe, Mc = s.Mc, Agent = s.Agent, Bot = s.Bot, Host = s.Host, Hive = s.Hive, Telemetry = s.Telemetry,
                TelemetryDir = s.TelemetryDir, Loggers = loggers, Log = log, Stop = hive.Stop, Profile = _profile, Bodies = s.Bodies,
                Transport = Transport, Kill = hive.KillToken, KillReason = () => hive.KillReason, Health = hive.Health,
                ClientSetup = ClientSetup, Llm = Llm is { } llm ? o => llm(clone, o) : null,
            };
            return await CloneMode.RunAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (hive.KillToken.IsCancellationRequested) { return CloneExitCodes.Watchdog; }
        catch (Exception ex)
        {
            // Contained: one hive's unhandled exception is that hive's crash (the supervisor's restart policy), never main's. Its own
            // log gets it (and main's, through the Error+ forward); before its loggers exist, main's.
            (log ?? _log).LogError(ex, "hive {Clone}: crashed (contained in main's process)", clone);
            return 1;
        }
        finally
        {
            Release();
            try { loggers?.Dispose(); } catch (Exception) { }
            try { file?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// The watchdog: every <see cref="InProcHiveConfig.CheckSeconds"/>, each live hive's health (<see cref="HiveHealth.Check"/>). A
    /// sick one is stopped as a release would stop it (deposit, its bodies first, bye) and main's brain is told; one still running
    /// <see cref="InProcHiveConfig.StopGraceSeconds"/> later is killed (teardown without the gate). The supervisor sees the exit like a
    /// process exit: a clean bye = logged out, else a crash (restart policy, <c>MaxCrashes</c> retire it).
    /// </summary>
    internal async Task WatchAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromSeconds(Math.Clamp(_cfg.InProc.CheckSeconds, 0.05, 60));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(every, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            try { CheckOnce(DateTimeOffset.UtcNow); }
            catch (Exception ex) { _log.LogWarning(ex, "hive watchdog: check failed"); }
        }
    }

    internal void CheckOnce(DateTimeOffset now)
    {
        foreach (var h in _hives.Values)
        {
            if (h.HasExited) continue;
            if (h.StopOverdue(now)) { h.Kill($"watchdog: still running {_cfg.InProc.StopGraceSeconds:0}s after its stop ({h.StopReason})"); continue; }
            if (h.StopReason != null || h.Health.Check(_cfg.InProc, now) is not { } why) continue;
            if (!h.RequestStop(why)) continue;
            _log.LogWarning("hive watchdog: stopping {Clone}: {Why}", h.Name, why);
            try { Notice?.Invoke(h.Name, why); } catch (Exception ex) { _log.LogDebug(ex, "hive watchdog: notice failed"); }
        }
    }

    /// <summary>Main's last shutdown step: hives the recall left running are killed and their teardown is awaited (bounded), so
    /// their db connections close before main's.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        var live = _hives.Values.ToList();
        foreach (var h in live) h.Kill("main shutting down");
        try { await Task.WhenAll(live.Select(h => h.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(Math.Max(2, _cfg.InProc.AbandonSeconds + 2))).ConfigureAwait(false); }
        catch (Exception) { }
        try { await _watch.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
    }
}

/// <summary>
/// Picks the launcher per clone (<c>Hive:Isolation</c>, <c>Hive:ProcessProfiles</c>, a spawn's <c>Isolation</c> override): the
/// in-process host or a clone process. The decision made for the transport is the one the start uses.
/// </summary>
public sealed class IsolationLauncher(Func<string, bool> inProcess, IProcessLauncher inproc, IProcessLauncher process) : IProcessLauncher
{
    private readonly ConcurrentDictionary<string, bool> _next = new(StringComparer.OrdinalIgnoreCase);

    public IHiveTransport TransportFor(string clone)
    {
        bool ip = inProcess(clone);
        _next[clone] = ip;
        return (ip ? inproc : process).TransportFor(clone);
    }

    public ICloneProcess Start(string clone, string pipeName) =>
        (_next.TryRemove(clone, out var ip) ? ip : inProcess(clone)) ? inproc.Start(clone, pipeName) : process.Start(clone, pipeName);
}
