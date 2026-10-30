using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MineClaw.Data;
using MineClaw.Scripting.Runtime;

namespace MineClaw.Scripting;

public enum ScriptStatus { Starting, Running, Paused, Done, Failed, Cancelled }

/// <summary>Why a script is paused. Higher = stickier: a danger pause is never turned back into an auto-resuming chat pause.</summary>
public enum PauseKind
{
    /// <summary>Addressed chat (auto-resumes after <see cref="ScriptOptions.ChatAutoResumeSeconds"/>).</summary>
    Chat = 0,
    /// <summary>The script itself asked: an on() handler returned "pause", or ask("brain", q) (wakes the brain).</summary>
    Script = 1,
    /// <summary>script {op:'pause'}.</summary>
    Brain = 2,
    Danger = 3,
    /// <summary>A trusted player said stop.</summary>
    Stop = 4,
}

/// <summary>What to run. <see cref="CarryJson"/> seeds the <c>carry</c> table (replace / resume_from); <see cref="ArgsJson"/> the <c>args</c> table.</summary>
public sealed record ScriptRequest
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    /// <summary>JS for every new script; Lua only for legacy saved versions. Null = detected from the code (direct callers, tests).</summary>
    public ScriptLanguage? Language { get; init; }
    /// <summary>The language this request runs in.</summary>
    public ScriptLanguage Lang => Language ?? ScriptLanguages.Detect(Code);
    public string? CarryJson { get; init; }
    public string? ArgsJson { get; init; }
    /// <summary>This run's tunables (<c>script{name, params:{...}}</c>), merged over the saved ones and the code's defaults by <c>params()</c>.</summary>
    public string? ParamsJson { get; init; }
    /// <summary>Who wrote the code ("main/claude-opus-5-5"; null = C#/saved script run by name).</summary>
    public string? Author { get; init; }
    /// <summary>Saved-script version this run executes (stats are kept per version), or null for an unsaved run.</summary>
    public int? Version { get; init; }
    /// <summary>Lint warnings found before the run (attached to the result).</summary>
    public string? LintWarnings { get; init; }
    /// <summary>Fast-forward (actions skipped, stages not run) until <c>cp"label"</c> (or a machine's "@id").</summary>
    public string? ResumeFrom { get; init; }
    /// <summary>The machine snapshot to resume (state, history, blackboard) when <see cref="ResumeFrom"/> is a machine label.</summary>
    public string? ResumeState { get; init; }
    /// <summary>Crew lane: the role function this lane runs after loading the script's top level.</summary>
    public string? Role { get; init; }
    /// <summary>Crew lane / coordinator: the shared board's name.</summary>
    public string? CrewId { get; init; }
    /// <summary>Crew lane: who runs its crew ("maid_chan_101", the hive whose bodiless coordinator handed out the lane); debugger only.</summary>
    public string? Coordinator { get; init; }
    /// <summary>
    /// No brain watches this run (a hive script's coordinator; every crew lane is unattended too): a danger pause resumes by itself
    /// once the body's reflexes let go (<see cref="ScriptOptions.LaneSafeSeconds"/>), and gives up after
    /// <see cref="ScriptOptions.LaneDangerMaxSeconds"/> (the run fails, so its coordinator re-runs the role and its leases go back).
    /// </summary>
    public bool Unattended { get; init; }
}

/// <summary>
/// One script run on its own thread with its own sandboxed engine (JS via Jint; legacy Lua). Host calls are the only safe
/// points: a pause, cancel or replace takes effect between two host calls, never inside one. Interruptible actions (walking,
/// mining, waits) are aborted through their own cancellation on danger and re-run after resume; atomic ones (click
/// sequences, digs) always finish. Code between host calls is bounded by the runtime's budget hook (statements, busy loop,
/// call depth, memory, stop requests) — the script can't remove it or catch it (host signals are CLR exceptions that no
/// script try/catch or pcall sees).
/// </summary>
public sealed partial class ScriptSession
{
    private readonly ScriptController _owner;
    private readonly ScriptHost _host;
    private readonly ScriptOptions _o;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly CancellationToken _hardCt;
    private CancellationTokenSource? _abortCts;
    private bool _abortedForPause, _currentAtomic, _inSafePoint;
    private string? _stopReason;
    private bool _stopIsLimit;
    private string? _limitHit;
    private PauseKind? _pauseReq;
    private string? _pauseReqReason;
    private bool _resumeReq;
    private string? _resumeAnswer;
    private readonly ManualResetEventSlim _wake = new(false);
    private TaskCompletionSource _settled = NewTcs();
    private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _active = new();
    private ITimer? _watchdog;
    private IScriptRuntime? _runtime;
    private long _instructions, _sinceAction, _allocBase;
    private string[] _sourceLines = [];
    private readonly Dictionary<(int Id, int Dmg), int> _invStart;
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private int _skipped;
    private string? _resumeTarget;

    internal ScriptSession(ScriptController owner, int id, ScriptRequest request, CancellationToken hardCt)
    {
        _owner = owner;
        _host = owner.Host;
        _o = owner.Options;
        _time = owner.Time;
        _hardCt = hardCt;
        Id = id;
        Name = request.Name;
        Code = request.Code;
        Request = request;
        _resumeTarget = string.IsNullOrWhiteSpace(request.ResumeFrom) ? null : request.ResumeFrom.Trim();
        Started = _time.GetUtcNow();
        CheckpointTotal = CountCheckpoints(Code);
        _invStart = InventoryCounts();
        Signals = new Sdk.SignalSet(this);
    }

    public int Id { get; }
    public string Name { get; }
    public string Code { get; }
    public ScriptRequest Request { get; private set; }
    public ScriptLanguage Language => Request.Lang;
    public DateTimeOffset Started { get; }
    public DateTimeOffset? Finished { get; private set; }
    public ScriptStatus Status { get; private set; } = ScriptStatus.Starting;
    public bool IsLive => Status is ScriptStatus.Starting or ScriptStatus.Running or ScriptStatus.Paused;
    /// <summary>Paused, or a pause was requested and takes effect at the next safe point.</summary>
    public bool PausePending { get { lock (_lock) return _pauseReq != null || Status == ScriptStatus.Paused || _pauseInFlight; } }
    public int Actions { get; private set; }
    public long Instructions => Interlocked.Read(ref _instructions);
    public int Line { get; private set; }
    public string? Snippet { get; private set; }
    public string? Checkpoint { get; private set; }
    public int CheckpointTotal { get; }
    public int CheckpointsPassed { get { lock (_lock) return _passed.Count; } }
    public double? ProgressPct { get; private set; }
    public string? ProgressNote { get; private set; }
    public PauseKind? PausedFor { get; private set; }
    public string? PauseReason { get; private set; }
    public DateTimeOffset? PausedAt { get; private set; }
    /// <summary>"go: no route" — the most recent failed action.</summary>
    public string? LastFailure { get; private set; }
    /// <summary>True while the most recent action (say/emote included) failed: a script ending right after it without
    /// returning its own result is reported as failed, never "ok".</summary>
    public bool LastActionFailed { get; private set; }
    private string? _lastFailureMsg;
    /// <summary>A crew lane or a hive script's coordinator: nobody resumes its danger pauses but itself (see <see cref="ScriptRequest.Unattended"/>).</summary>
    public bool Unattended => Request.Role != null || Request.Unattended;
    /// <summary>Paused for a danger it will resume from by itself (the host doesn't wake the brain for it).</summary>
    public bool AutoResumes { get { lock (_lock) return Status == ScriptStatus.Paused && PausedFor == PauseKind.Danger && Unattended; } }
    private DateTimeOffset _lastDanger;
    /// <summary>Why it failed/was cancelled (null while running or when done).</summary>
    public string? Reason { get; private set; }
    /// <summary>The terse result line once ended.</summary>
    public string? Outcome { get; private set; }
    /// <summary>What the script itself returned (its text), when it ended normally — a crew lane's result for <c>parallel()</c>.</summary>
    public string? Result { get; private set; }
    /// <summary>A lane's answer to its coordinator: the role's own result when done, else the outcome line.</summary>
    public string LaneResult => Ok ? Result ?? Outcome ?? "ok" : Outcome ?? Reason ?? "failed";
    public bool Ok => Status == ScriptStatus.Done;
    public string? Vars { get; private set; }
    /// <summary>JSON of <c>carry</c> when the script ended (replace hands it to the new script).</summary>
    public string? CarryJson { get; private set; }
    /// <summary>carry snapshot per checkpoint label (resume_from).</summary>
    public Dictionary<string, string> CheckpointCarry { get; } = new(StringComparer.Ordinal);
    public List<string> Logs { get; } = new();
    /// <summary>What say/emote would have said while chat is muted (hive clone): returned to the brain in the result.</summary>
    public List<string> Reports { get; } = new();
    /// <summary>Total items gained during the run (all kinds): the run's "yield" for reliability stats.</summary>
    public int ItemsGained { get; private set; }
    /// <summary>Active (unpaused) run time.</summary>
    public TimeSpan Elapsed => _active.Elapsed;
    /// <summary>Set by the host adapter: non-null = this run lost the bot's body (another activity took over) → cancel.</summary>
    public Func<string?>? OwnerLost { get; set; }
    /// <summary>The effective tunables (<c>params()</c>) as JSON, for telemetry and self-tuning; null = the script declared none.</summary>
    public string? ParamsJson { get; internal set; }
    /// <summary>Per action: calls, failures and total time — the run's timing telemetry.</summary>
    public IReadOnlyDictionary<string, ActionTiming> Timings { get { lock (_lock) return new Dictionary<string, ActionTiming>(_timings); } }
    private readonly Dictionary<string, ActionTiming> _timings = new(StringComparer.Ordinal);
    /// <summary>The state machine running in this session ("mine", 4 transitions), or null.</summary>
    public string? MachineState { get; internal set; }
    public int Transitions { get; internal set; }
    /// <summary>Edge-triggered conditions the script listens for (hungry, invFull, night...; machine timers; when()).</summary>
    internal Sdk.SignalSet Signals { get; private set; }

    internal ScriptHost Host => _host;
    internal ScriptOptions Options => _o;
    internal TimeProvider Time => _time;
    internal ScriptController Owner => _owner;
    internal CancellationToken HardToken => _hardCt;
    internal CancellationToken StopToken => _stopCts.Token;
    internal string? ResumeTarget => _resumeTarget;

    /// <summary>Completes with <see cref="Outcome"/> when the run ends.</summary>
    public Task<string> Completion => _done.Task;

    /// <summary>Completes when the script is paused or has ended (already true → completed task).</summary>
    public Task WhenSettled(CancellationToken ct = default)
    {
        lock (_lock)
            return Status is ScriptStatus.Starting or ScriptStatus.Running ? _settled.Task.WaitAsync(ct) : Task.CompletedTask;
    }

    public string State() => ScriptText.State(this, _o.StateChars);

    // ------------------------------------------------------------------ control (any thread)

    internal void Start()
    {
        _watchdog = _time.CreateTimer(_ => Watch(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        var t = new Thread(Body, 16 * 1024 * 1024) { IsBackground = true, Name = "script:" + Name };
        t.Start();
    }

    /// <summary>Stops at the next safe point (an in-flight interruptible action is aborted; atomic ones finish).</summary>
    public bool Stop(string reason, bool limit = false)
    {
        CancellationTokenSource? abort;
        lock (_lock)
        {
            if (!IsLive || _stopReason != null) return false;
            _stopReason = reason;
            _stopIsLimit = limit;
            abort = _currentAtomic ? null : _abortCts;
        }
        try { _stopCts.Cancel(); } catch (ObjectDisposedException) { }
        try { abort?.Cancel(); } catch (ObjectDisposedException) { }
        _wake.Set();
        return true;
    }

    /// <summary>Asks for a pause at the next safe point. <paramref name="abort"/> also cuts an interruptible action short (re-run on resume).</summary>
    public bool RequestPause(PauseKind kind, string reason, bool abort)
    {
        CancellationTokenSource? cut = null;
        lock (_lock)
        {
            if (!IsLive || _stopReason != null) return false;
            if (Status == ScriptStatus.Paused)
            {
                if (PausedFor is { } k && kind > k) { PausedFor = kind; PauseReason = reason; }
                return true;
            }
            if (_pauseReq is not { } cur || kind >= cur) { _pauseReq = kind; _pauseReqReason = reason; _pauseReqEvent = _nextPauseEvent; }
            if (abort && !_currentAtomic && _abortCts != null) { _abortedForPause = true; cut = _abortCts; }
        }
        try { cut?.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    [ThreadStatic] private static ScriptEvent? _nextPauseEvent;

    /// <summary>A danger pause that remembers its event (the patch path reads its topic and text).</summary>
    internal bool RequestPause(PauseKind kind, string reason, bool abort, ScriptEvent cause)
    {
        _nextPauseEvent = cause;
        try { return RequestPause(kind, reason, abort); }
        finally { _nextPauseEvent = null; }
    }

    /// <summary>Resumes a paused script (or withdraws a pending pause). <paramref name="answer"/> is what a pending ask("brain", q) returns.</summary>
    public bool Resume(string? answer = null)
    {
        lock (_lock)
        {
            if (Status == ScriptStatus.Paused) { _resumeReq = true; _resumeAnswer = answer; _wake.Set(); return true; }
            if (_pauseReq != null) { _pauseReq = null; return true; }
            return false;
        }
    }

    private void Watch()
    {
        try
        {
            if (Status is ScriptStatus.Running && _active.Elapsed > _o.MaxWall)
                Stop($"time limit {ScriptText.Dur(_o.MaxWall)}", limit: true);
            if (Status is ScriptStatus.Running) Signals.Poll(onScriptThread: false);
        }
        catch (Exception) { /* a timer callback: throwing would end the process (every in-process hive with it); next tick retries */ }
    }

    /// <summary>Cuts the in-flight interruptible action so the next safe point runs a handler (it re-runs afterwards unless the handler unwinds).</summary>
    internal void CutForHandler()
    {
        CancellationTokenSource? cut = null;
        lock (_lock)
            if (!_currentAtomic && _abortCts != null) { _abortedForPause = true; cut = _abortCts; }
        try { cut?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ the script thread

    private void Body()
    {
        using var scope = _owner.StepScope?.Invoke(); // this lane's body, for the whole thread
        ScriptStatus final;
        string? reason = null;
        IReadOnlyList<JsonNode?> result = [];
        try
        {
            _sourceLines = Code.Split('\n');
            _runtime = ScriptRuntimes.Create(this);
            lock (_lock) if (Status == ScriptStatus.Starting) Status = ScriptStatus.Running;
            _active.Start();
            TouchSegment();
            while (true)
            {
                try { result = _runtime.Run(Code, _hardCt); break; }
                catch (Exception ex) when (FindRestart(ex) is { } restart && _stopReason == null)
                {
                    Restart(restart.Label);
                }
            }
            ThrowIfStopping();
            if (_resumeTarget != null) throw new ScriptFailException($"checkpoint '{_resumeTarget}' not reached");
            final = ScriptStatus.Done;
            if (UnhandledFailure(result) is { } unhandled) { final = ScriptStatus.Failed; reason = unhandled; }
        }
        catch (Exception ex)
        {
            (final, reason) = Classify(ex);
        }
        try { CarryJson = _runtime?.CarryJson(); } catch (Exception) { }
        try { VerifyAtEnd(final); } catch (Exception) { }
        _active.Stop();
        try { _watchdog?.Dispose(); } catch (Exception) { }
        TaskCompletionSource settled;
        string outcome;
        lock (_lock)
        {
            Status = final;
            Reason = reason;
            Finished = _time.GetUtcNow();
            Result = final == ScriptStatus.Done ? Completed(result) : null;
            // The script thread's last words: nothing here may throw out of the thread (an exception escaping a thread ends the
            // process, and with in-process hives every hive in it).
            try { outcome = Outcome = ScriptText.Outcome(this, Result, InventoryDelta(), _o.ResultChars); }
            catch (Exception ex) { outcome = Outcome = $"{final}: {reason ?? "-"} (outcome unavailable: {ex.Message})"; }
            try
            {
                var now = InventoryCounts();
                ItemsGained = now.Sum(kv => Math.Max(0, kv.Value - _invStart.GetValueOrDefault(kv.Key)));
            }
            catch (Exception) { }
            settled = _settled;
        }
        try { _runtime?.Dispose(); } catch (Exception) { }
        try { _owner.OnEnded(this); } // stats, saves, promotion before anyone waiting sees the end
        catch (Exception) { /* the end still settles below; the owner's own failure is not the script's */ }
        settled.TrySetResult();
        _done.TrySetResult(outcome);
        _abortCts?.Dispose();
    }

    private (ScriptStatus, string) Classify(Exception ex)
    {
        string? stop, limit;
        bool stopIsLimit;
        lock (_lock) { stop = _stopReason; stopIsLimit = _stopIsLimit; limit = _limitHit; }
        (string? Reason, int Line)? desc = null;
        try { desc = _runtime?.Describe(ex); } catch (Exception) { }
        // Our own exceptions may arrive wrapped by the engine (whose message carries the line); the innermost one decides.
        if (Line == 0 && desc is { Line: > 0 } at)
        {
            Line = at.Line;
            Snippet = SourceLine(Line);
        }
        for (var e = ex; e != null; e = e.InnerException)
            switch (e)
            {
                case ScriptStopException s: return (ScriptStatus.Cancelled, s.Message);
                case ScriptLimitException l: return (ScriptStatus.Failed, l.Message);
                case ScriptFailException f: return (ScriptStatus.Failed, f.Message);
                case Sdk.ScriptUnwind u: return (ScriptStatus.Failed, "error: " + u.Message + " outside its machine/timeout");
            }
        if (limit != null) return (ScriptStatus.Failed, limit);
        if (stop != null) return (stopIsLimit ? ScriptStatus.Failed : ScriptStatus.Cancelled, stop);
        if (desc is { Reason: { } why } d2)
        {
            if (d2.Line > 0) { Line = d2.Line; Snippet = SourceLine(Line); }
            return (ScriptStatus.Failed, why);
        }
        switch (ex)
        {
            case OperationCanceledException: return (ScriptStatus.Cancelled, "shutdown");
            default:
                _host.Log?.Invoke($"script {Name} host error: {ex}");
                return (ScriptStatus.Failed, $"{ex.GetType().Name}: {OneLine(ex.Message)}");
        }
    }

    internal void ThrowIfStopping()
    {
        string? why, limit;
        bool isLimit;
        lock (_lock) { why = _stopReason; isLimit = _stopIsLimit; limit = _limitHit; }
        if (limit != null) throw new ScriptLimitException(limit);
        if (why == null) return;
        if (isLimit) throw new ScriptLimitException(why);
        throw new ScriptStopException(why);
    }

    private static ScriptRestartException? FindRestart(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException) if (e is ScriptRestartException r) return r;
        return null;
    }

    /// <summary>restart_step without a machine: a fresh engine fast-forwards to the last checkpoint, carry and patched params kept.</summary>
    private void Restart(string? label)
    {
        string? carry = null;
        try { carry = _runtime?.CarryJson(); } catch (Exception) { }
        try { _runtime?.Dispose(); } catch (Exception) { }
        lock (_lock)
        {
            _handlers.Clear();
            _pendingHandlers.Clear();
            _declared.Clear();
        }
        Signals = new Sdk.SignalSet(this);
        Request = Request with
        {
            CarryJson = carry ?? Request.CarryJson, ResumeFrom = label, ResumeState = label != null && CheckpointCarry.TryGetValue(label, out var snap) && label.StartsWith('@') ? snap : null,
            ParamsJson = ParamsJson ?? Request.ParamsJson,
        };
        _resumeTarget = label;
        _skipped = 0;
        Log("restarting " + (label != null ? $"at checkpoint '{label}'" : "from the top") + " (patch)");
        _runtime = ScriptRuntimes.Create(this);
        TouchSegment();
    }

    internal void Limit(string what)
    {
        lock (_lock) _limitHit ??= what;
        throw new ScriptLimitException(what);
    }

    /// <summary>
    /// The runtime's budget hook (Lua: every 10 VM instructions; JS: every few statements): the only thing that runs while
    /// script code runs. Stop requests, the instruction limit, the busy-loop guard and the memory guard.
    /// </summary>
    internal void Budget(int units)
    {
        long total = Interlocked.Add(ref _instructions, units);
        _sinceAction += units;
        ThrowIfStopping();
        if (_patchRun is { } pr) PatchBudget(pr, units);
        if (total > _o.MaxInstructions) Limit($"instruction limit ({_o.MaxInstructions})");
        if (_sinceAction > _o.MaxInstructionsWithoutAction) Limit($"busy loop ({_o.MaxInstructionsWithoutAction} instructions without an action; add wait())");
        if (GC.GetAllocatedBytesForCurrentThread() - _allocBase > _o.MaxSegmentAllocBytes)
            Limit($"memory limit ({_o.MaxSegmentAllocBytes / (1024 * 1024)} MB between actions)");
    }

    internal void DepthLimit() => Limit($"call depth limit ({_o.MaxCallDepth})");

    internal void TouchSegment()
    {
        _sinceAction = 0;
        _allocBase = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>The boundary between host calls: stop, lost body, signals, handlers, pauses — in that order.</summary>
    internal void SafePoint()
    {
        if (_inSafePoint || _inPatch) { ThrowIfStopping(); return; }
        _inSafePoint = true;
        try
        {
            while (true)
            {
                ThrowIfStopping();
                if (OwnerLost?.Invoke() is { } lost) { Stop(lost); ThrowIfStopping(); }
                Signals.Poll(onScriptThread: true);
                if (TakeHandler(out var h, out var ev)) { RunHandler(h!, ev!); continue; }
                PauseKind kind;
                string reason;
                ScriptEvent? cause;
                lock (_lock)
                {
                    if (_pauseReq is not { } k)
                    {
                        if (_interruptReqs.Count == 0) return;
                        kind = default;
                        reason = "";
                        cause = null;
                    }
                    else
                    {
                        kind = k;
                        reason = _pauseReqReason ?? kind.ToString().ToLowerInvariant();
                        cause = _pauseReqEvent;
                        _pauseReq = null;
                        _pauseReqEvent = null;
                    }
                }
                if (reason.Length == 0) { TakeInterruptRequest(); continue; }
                // Inline fix first (onInterrupt → learned handler → patch turn); escalation pauses and wakes the brain as before.
                if (PatchPause(kind, cause)) continue;
                PauseHere(kind, reason);
            }
        }
        finally { _inSafePoint = false; }
    }

    /// <summary>Blocks the script thread until resume/cancel/timeout. Returns the resume answer (ask"brain").</summary>
    internal string? PauseHere(PauseKind kind, string reason)
    {
        var at = _time.GetUtcNow();
        TaskCompletionSource settled;
        lock (_lock)
        {
            Status = ScriptStatus.Paused;
            _pauseInFlight = false;
            PausedFor = kind;
            PauseReason = reason;
            PausedAt = at;
            _resumeReq = false;
            _resumeAnswer = null;
            _wake.Reset();
            settled = _settled;
            _settled = NewTcs();
        }
        _active.Stop();
        SnapshotVars();
        _owner.OnPaused(this);
        settled.TrySetResult();
        bool auto = false;
        DateTimeOffset? safeSince = null;
        _blocked = true; // parked: the debugger may read the engine's locals
        while (true)
        {
            _wake.Wait(TimeSpan.FromMilliseconds(100));
            var now = _time.GetUtcNow();
            bool dangerPause;
            lock (_lock)
            {
                if (_stopReason != null || _hardCt.IsCancellationRequested) break;
                if (_resumeReq) break;
                if (PausedFor == PauseKind.Chat && now - at >= TimeSpan.FromSeconds(_o.ChatAutoResumeSeconds)) { auto = true; break; }
                dangerPause = PausedFor == PauseKind.Danger && Unattended;
            }
            if (dangerPause)
            {
                // Unattended (live 10-29: 20 hive bodies' lanes sat paused on a zombie each until the brain resumed them one by one):
                // the body's reflexes fight/shelter/eat; once they let go and no new danger came for a moment, carry on (the cut
                // action re-runs). Too long in danger: fail, so the coordinator re-runs the role and the leases go back.
                bool busy = _host.SafetyBusy is { } sb && SafeBusyNow(sb);
                var calm = TimeSpan.FromSeconds(Math.Max(0.2, _o.LaneSafeSeconds));
                if (busy) safeSince = null; else safeSince ??= now;
                DateTimeOffset last;
                lock (_lock) last = _lastDanger;
                if (safeSince is { } since && now - since >= calm && now - last >= calm) { auto = true; break; }
                if (now - at >= TimeSpan.FromSeconds(Math.Max(1, _o.LaneDangerMaxSeconds)))
                {
                    Stop($"interrupted: {reason} for over {ScriptText.Dur(TimeSpan.FromSeconds(_o.LaneDangerMaxSeconds))}", limit: true);
                    break;
                }
            }
            if (now - at >= _o.MaxPause) { Stop($"paused over {ScriptText.Dur(_o.MaxPause)}"); break; }
        }
        WaitProbe();
        string? answer;
        lock (_lock)
        {
            if (_hardCt.IsCancellationRequested && _stopReason == null) _stopReason = "shutdown";
            Status = ScriptStatus.Running;
            PausedFor = null;
            PauseReason = null;
            PausedAt = null;
            _resumeReq = false;
            answer = _resumeAnswer;
            _resumeAnswer = null;
        }
        ThrowIfStopping();
        _active.Start();
        TouchSegment();
        _owner.OnResumed(this, auto);
        return answer;
    }

    private static bool SafeBusyNow(Func<bool> busy)
    {
        try { return busy(); } catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ helpers

    private static readonly Regex CheckpointRx = new(@"\bcp\s*\(?\s*(['""`])([^'""`\n]{1,40})\1", RegexOptions.Compiled);

    private static int CountCheckpoints(string code) =>
        CheckpointRx.Matches(code).Select(m => m.Groups[2].Value).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// True if <paramref name="code"/> contains <c>cp"label"</c> / <c>cp("label", fn)</c>, or <paramref name="label"/> is a machine
    /// checkpoint ("@id", written on every transition) and the code builds a machine.
    /// </summary>
    public static bool HasCheckpoint(string code, string label) =>
        label.StartsWith('@') ? code.Contains("machine(", StringComparison.Ordinal)
        : CheckpointRx.Matches(code).Any(m => m.Groups[2].Value == label);

    /// <summary>Current script line (the host call's call site).</summary>
    internal void Locate()
    {
        try
        {
            int line = _runtime?.CurrentLine() ?? 0;
            if (line <= 0) return;
            Line = line;
            Snippet = SourceLine(Line);
        }
        catch (Exception) { }
    }

    private string? SourceLine(int line)
    {
        if (line < 1 || line > _sourceLines.Length) return null;
        var s = _sourceLines[line - 1].Trim();
        return s.Length <= 48 ? s : s[..45] + "...";
    }

    private Dictionary<(int, int), int> InventoryCounts()
    {
        var d = new Dictionary<(int, int), int>();
        try
        {
            var slots = _host.World.Inventory.PlayerSlots();
            for (int i = 9; i < slots.Length && i < 45; i++)
            {
                var s = slots[i];
                if (s.IsEmpty) continue;
                var k = (s.Id, Items.Get(s.Id).MaxDamage > 0 ? 0 : s.Damage);
                d[k] = d.GetValueOrDefault(k) + s.Count;
            }
        }
        catch (Exception) { }
        return d;
    }

    /// <summary>Items gained during the run, biggest first ("+64 cobblestone").</summary>
    private List<string> InventoryDelta()
    {
        var now = InventoryCounts();
        return now.Select(kv => (kv.Key, Gain: kv.Value - _invStart.GetValueOrDefault(kv.Key))).Where(x => x.Gain > 0)
            .OrderByDescending(x => x.Gain).Take(2)
            .Select(x => $"+{x.Gain} {SafeName(x.Key.Item1, x.Key.Item2)}").ToList();
    }

    private static string SafeName(int id, int dmg)
    {
        try { return Items.DisplayName(id, dmg).ToLowerInvariant(); } catch (Exception) { return "#" + id; }
    }

    private enum ValueKind { Nil, Str, Num, Bool, Other }

    private static ValueKind KindOf(JsonNode? v) => v switch
    {
        null => ValueKind.Nil,
        JsonValue jv => jv.GetValueKind() switch
        {
            JsonValueKind.String => ValueKind.Str,
            JsonValueKind.Number => ValueKind.Num,
            JsonValueKind.True or JsonValueKind.False => ValueKind.Bool,
            JsonValueKind.Null or JsonValueKind.Undefined => ValueKind.Nil,
            _ => ValueKind.Other,
        },
        _ => ValueKind.Other,
    };

    /// <summary>A neutral scalar as text ("12", "1.5", "true", "x").</summary>
    internal static string Str(JsonNode? v) => KindOf(v) switch
    {
        ValueKind.Nil => "",
        ValueKind.Str => v!.GetValue<string>(),
        ValueKind.Num => Fmt(AsNumber(v) ?? 0),
        ValueKind.Bool => v!.GetValue<bool>() ? "true" : "false",
        _ => v!.ToJsonString(),
    };

    internal static string Fmt(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? Completed(IReadOnlyList<JsonNode?> r)
    {
        if (r.Count == 0) return null;
        // `return ok, msg` (an action's own result): the run's status says ok/failed, so a leading boolean is not text.
        var vals = r.Take(3).ToList();
        if (vals.Count > 0 && KindOf(vals[0]) == ValueKind.Bool) vals.RemoveAt(0);
        var parts = vals.Where(v => KindOf(v) is ValueKind.Str or ValueKind.Num or ValueKind.Bool).Select(Str)
            .Where(t => t.Length > 0).ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// The script ended right after a failed action without saying how it went: reported as failed ("goto: no route").
    /// Handled = a later action succeeded, or the script returned its own result (a string, number or true) instead of
    /// nothing / false / the failure itself.
    /// </summary>
    private string? UnhandledFailure(IReadOnlyList<JsonNode?> result)
    {
        if (!LastActionFailed || LastFailure == null) return null;
        if (result.Count == 0 || KindOf(result[0]) == ValueKind.Nil) return LastFailure;
        var first = result[0];
        if (KindOf(first) == ValueKind.Bool) return first!.GetValue<bool>() ? null : LastFailure;
        if (KindOf(first) == ValueKind.Str)
        {
            // `return ok .. msg` / `return tostring(ok) .. msg` ("falseno skill 'toss'..") or just the failure text again.
            var text = OneLine(first!.GetValue<string>());
            var fm = OneLine(_lastFailureMsg);
            if (fm.Length > 0)
            {
                foreach (var head in new[] { "false", "nil", "undefined" })
                    if (text.StartsWith(head, StringComparison.OrdinalIgnoreCase) && text[head.Length..].TrimStart(' ', ':', ',', '-').StartsWith(fm, StringComparison.Ordinal))
                        return LastFailure;
                if (string.Equals(text, fm, StringComparison.Ordinal)) return LastFailure;
            }
        }
        return null;
    }

    internal static string OneLine(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    internal static string Short(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 3)] + "...";

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The state line's variables (runtime snapshot).</summary>
    internal void SnapshotVars()
    {
        try { Vars = _runtime?.Vars(); } catch (Exception) { }
    }
}

/// <summary>One action's timing over a run: calls, failures, total wall time.</summary>
public readonly record struct ActionTiming(int Calls, int Fails, TimeSpan Total)
{
    public override string ToString() => $"{Calls}x {Total.TotalSeconds:0.#}s" + (Fails > 0 ? $" {Fails} failed" : "");
}
