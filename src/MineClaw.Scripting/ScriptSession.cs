using System.Diagnostics;
using System.Text.RegularExpressions;
using Lua;
using MineClaw.Data;

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
    public string? CarryJson { get; init; }
    public string? ArgsJson { get; init; }
    /// <summary>Fast-forward (actions skipped, stages not run) until <c>cp"label"</c>.</summary>
    public string? ResumeFrom { get; init; }
}

/// <summary>
/// One Lua script run on its own thread with its own sandboxed <see cref="LuaState"/>. Host calls are the only safe points:
/// a pause, cancel or replace takes effect between two host calls, never inside one. Interruptible actions (walking,
/// mining, waits) are aborted through their own cancellation on danger and re-run after resume; atomic ones (click
/// sequences, digs) always finish. Lua code between host calls is bounded by a count hook (instructions, busy loop, call
/// depth, memory, stop requests) — the script can't remove it (no debug library) or catch it (no pcall).
/// </summary>
public sealed partial class ScriptSession
{
    private const int HookEvery = 10;

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
    private LuaState? _lua;
    private long _instructions, _sinceAction, _allocBase;
    private string[] _sourceLines = [];
    private readonly Dictionary<(int Id, int Dmg), int> _invStart;
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private HashSet<string> _baseGlobals = new(StringComparer.Ordinal);
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
    }

    public int Id { get; }
    public string Name { get; }
    public string Code { get; }
    public ScriptRequest Request { get; }
    public DateTimeOffset Started { get; }
    public DateTimeOffset? Finished { get; private set; }
    public ScriptStatus Status { get; private set; } = ScriptStatus.Starting;
    public bool IsLive => Status is ScriptStatus.Starting or ScriptStatus.Running or ScriptStatus.Paused;
    /// <summary>Paused, or a pause was requested and takes effect at the next safe point.</summary>
    public bool PausePending { get { lock (_lock) return _pauseReq != null || Status == ScriptStatus.Paused; } }
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
    /// <summary>Why it failed/was cancelled (null while running or when done).</summary>
    public string? Reason { get; private set; }
    /// <summary>The terse result line once ended.</summary>
    public string? Outcome { get; private set; }
    public bool Ok => Status == ScriptStatus.Done;
    public string? Vars { get; private set; }
    /// <summary>JSON of <c>carry</c> when the script ended (replace hands it to the new script).</summary>
    public string? CarryJson { get; private set; }
    /// <summary>carry snapshot per checkpoint label (resume_from).</summary>
    public Dictionary<string, string> CheckpointCarry { get; } = new(StringComparer.Ordinal);
    public List<string> Logs { get; } = new();
    /// <summary>Active (unpaused) run time.</summary>
    public TimeSpan Elapsed => _active.Elapsed;
    /// <summary>Set by the host adapter: non-null = this run lost the bot's body (another activity took over) → cancel.</summary>
    public Func<string?>? OwnerLost { get; set; }

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
            if (_pauseReq is not { } cur || kind >= cur) { _pauseReq = kind; _pauseReqReason = reason; }
            if (abort && !_currentAtomic && _abortCts != null) { _abortedForPause = true; cut = _abortCts; }
        }
        try { cut?.Cancel(); } catch (ObjectDisposedException) { }
        return true;
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
        if (Status is ScriptStatus.Running && _active.Elapsed > _o.MaxWall)
            Stop($"time limit {ScriptText.Dur(_o.MaxWall)}", limit: true);
    }

    // ------------------------------------------------------------------ the script thread

    private void Body()
    {
        ScriptStatus final;
        string? reason = null;
        LuaValue[] result = [];
        try
        {
            _sourceLines = Code.Split('\n');
            _lua = BuildState();
            lock (_lock) if (Status == ScriptStatus.Starting) Status = ScriptStatus.Running;
            _active.Start();
            TouchSegment();
            result = _lua.DoStringAsync(Code, "script", _hardCt).AsTask().GetAwaiter().GetResult();
            ThrowIfStopping();
            if (_resumeTarget != null) throw new ScriptFailException($"checkpoint '{_resumeTarget}' not reached");
            final = ScriptStatus.Done;
            if (UnhandledFailure(result) is { } unhandled) { final = ScriptStatus.Failed; reason = unhandled; }
        }
        catch (Exception ex)
        {
            (final, reason) = Classify(ex);
        }
        try { CarryJson = SerializeCarry(); } catch (Exception) { }
        _active.Stop();
        try { _watchdog?.Dispose(); } catch (Exception) { }
        TaskCompletionSource settled;
        string outcome;
        lock (_lock)
        {
            Status = final;
            Reason = reason;
            Finished = _time.GetUtcNow();
            outcome = Outcome = ScriptText.Outcome(this, final == ScriptStatus.Done ? Completed(result) : null, InventoryDelta(), _o.ResultChars);
            settled = _settled;
        }
        _owner.OnEnded(this); // stats, saves, promotion before anyone waiting sees the end
        settled.TrySetResult();
        _done.TrySetResult(outcome);
        _abortCts?.Dispose();
    }

    private static readonly Regex LuaLoc = new(@"\[string ""script""\]:(\d+):\s*(.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    private (ScriptStatus, string) Classify(Exception ex)
    {
        string? stop, limit;
        bool stopIsLimit;
        lock (_lock) { stop = _stopReason; stopIsLimit = _stopIsLimit; limit = _limitHit; }
        // Our own exceptions arrive wrapped by the VM (whose message carries the line); the innermost one decides.
        if (Line == 0 && ex is LuaRuntimeException && LuaLoc.Match(ex.Message) is { Success: true } where)
        {
            Line = int.Parse(where.Groups[1].Value);
            Snippet = SourceLine(Line);
        }
        for (var e = ex; e != null; e = e.InnerException)
            switch (e)
            {
                case ScriptStopException s: return (ScriptStatus.Cancelled, s.Message);
                case ScriptLimitException l: return (ScriptStatus.Failed, l.Message);
                case ScriptFailException f: return (ScriptStatus.Failed, f.Message);
            }
        if (limit != null) return (ScriptStatus.Failed, limit);
        if (stop != null) return (stopIsLimit ? ScriptStatus.Failed : ScriptStatus.Cancelled, stop);
        switch (ex)
        {
            case LuaCompileException ce: return (ScriptStatus.Failed, "syntax: " + LuaMessage(ce.Message));
            case LuaRuntimeException re: return (ScriptStatus.Failed, "error: " + LuaMessage(re.Message));
            case OperationCanceledException: return (ScriptStatus.Cancelled, "shutdown");
            default:
                _host.Log?.Invoke($"script {Name} host error: {ex}");
                return (ScriptStatus.Failed, $"{ex.GetType().Name}: {OneLine(ex.Message)}");
        }
    }

    /// <summary>"Lua-CSharp: [string "script"]:2: attempt to..." → line 2, "attempt to...".</summary>
    private string LuaMessage(string msg)
    {
        var m = LuaLoc.Match(msg);
        if (!m.Success) return Short(OneLine(msg.Replace("Lua-CSharp: ", "")), 120);
        Line = int.Parse(m.Groups[1].Value);
        Snippet = SourceLine(Line);
        return Short(OneLine(m.Groups[2].Value), 120);
    }

    private void ThrowIfStopping()
    {
        string? why, limit;
        bool isLimit;
        lock (_lock) { why = _stopReason; isLimit = _stopIsLimit; limit = _limitHit; }
        if (limit != null) throw new ScriptLimitException(limit);
        if (why == null) return;
        if (isLimit) throw new ScriptLimitException(why);
        throw new ScriptStopException(why);
    }

    private void Limit(string what)
    {
        lock (_lock) _limitHit ??= what;
        throw new ScriptLimitException(what);
    }

    /// <summary>Count hook (every <see cref="HookEvery"/> instructions): the only thing that runs while Lua code runs.</summary>
    private void Hook()
    {
        long total = Interlocked.Add(ref _instructions, HookEvery);
        _sinceAction += HookEvery;
        ThrowIfStopping();
        if (total > _o.MaxInstructions) Limit($"instruction limit ({_o.MaxInstructions})");
        if (_sinceAction > _o.MaxInstructionsWithoutAction) Limit($"busy loop ({_o.MaxInstructionsWithoutAction} instructions without an action; add wait())");
        if (_lua!.CallStackFrameCount > _o.MaxCallDepth) Limit($"call depth limit ({_o.MaxCallDepth})");
        if (GC.GetAllocatedBytesForCurrentThread() - _allocBase > _o.MaxSegmentAllocBytes)
            Limit($"memory limit ({_o.MaxSegmentAllocBytes / (1024 * 1024)} MB between actions)");
    }

    private void TouchSegment()
    {
        _sinceAction = 0;
        _allocBase = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>The boundary between host calls: stop, lost body, handlers, pauses — in that order.</summary>
    private void SafePoint()
    {
        if (_inSafePoint) { ThrowIfStopping(); return; }
        _inSafePoint = true;
        try
        {
            while (true)
            {
                ThrowIfStopping();
                if (OwnerLost?.Invoke() is { } lost) { Stop(lost); ThrowIfStopping(); }
                if (TakeHandler(out var h, out var ev)) { RunHandler(h!, ev!); continue; }
                PauseKind kind;
                string reason;
                lock (_lock)
                {
                    if (_pauseReq is not { } k) return;
                    kind = k;
                    reason = _pauseReqReason ?? kind.ToString().ToLowerInvariant();
                    _pauseReq = null;
                }
                PauseHere(kind, reason);
            }
        }
        finally { _inSafePoint = false; }
    }

    /// <summary>Blocks the script thread until resume/cancel/timeout. Returns the resume answer (ask"brain").</summary>
    private string? PauseHere(PauseKind kind, string reason)
    {
        var at = _time.GetUtcNow();
        TaskCompletionSource settled;
        lock (_lock)
        {
            Status = ScriptStatus.Paused;
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
        while (true)
        {
            _wake.Wait(TimeSpan.FromMilliseconds(100));
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                if (_stopReason != null || _hardCt.IsCancellationRequested) break;
                if (_resumeReq) break;
                if (PausedFor == PauseKind.Chat && now - at >= TimeSpan.FromSeconds(_o.ChatAutoResumeSeconds)) { auto = true; break; }
            }
            if (now - at >= _o.MaxPause) { Stop($"paused over {ScriptText.Dur(_o.MaxPause)}"); break; }
        }
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

    // ------------------------------------------------------------------ helpers

    private static readonly Regex CheckpointRx = new(@"\bcp\s*\(?\s*(['""])([^'""\n]{1,40})\1", RegexOptions.Compiled);

    private static int CountCheckpoints(string code) =>
        CheckpointRx.Matches(code).Select(m => m.Groups[2].Value).Distinct(StringComparer.Ordinal).Count();

    /// <summary>True if <paramref name="code"/> contains <c>cp"label"</c> / <c>cp("label", fn)</c>.</summary>
    public static bool HasCheckpoint(string code, string label) =>
        CheckpointRx.Matches(code).Any(m => m.Groups[2].Value == label);

    private static readonly Regex TraceLoc = new(@"\[string ""script""\]:(\d+):", RegexOptions.Compiled);

    /// <summary>Current script line from the Lua traceback (the host call's call site).</summary>
    private void Locate()
    {
        try
        {
            var tb = _lua!.GetTraceback().ToString();
            var m = TraceLoc.Match(tb);
            if (!m.Success) return;
            Line = int.Parse(m.Groups[1].Value);
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

    private static string? Completed(LuaValue[] r)
    {
        if (r.Length == 0) return null;
        // `return ok, msg` (an action's own result): the run's status says ok/failed, so a leading boolean is not text.
        var vals = r.Take(3).ToList();
        if (vals.Count > 0 && vals[0].Type == LuaValueType.Boolean) vals.RemoveAt(0);
        var parts = vals.Where(v => v.Type is LuaValueType.String or LuaValueType.Number or LuaValueType.Boolean).Select(Str)
            .Where(t => t.Length > 0).ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// The script ended right after a failed action without saying how it went: reported as failed ("goto: no route").
    /// Handled = a later action succeeded, or the script returned its own result (a string, number or true) instead of
    /// nothing / false / the failure itself.
    /// </summary>
    private string? UnhandledFailure(LuaValue[] result)
    {
        if (!LastActionFailed || LastFailure == null) return null;
        if (result.Length == 0 || result[0].Type == LuaValueType.Nil) return LastFailure;
        var first = result[0];
        if (first.Type == LuaValueType.Boolean) return first.Read<bool>() ? null : LastFailure;
        if (first.Type == LuaValueType.String)
        {
            // `return ok .. msg` / `return tostring(ok) .. msg` ("falseno skill 'toss'..") or just the failure text again.
            var text = OneLine(first.Read<string>());
            var fm = OneLine(_lastFailureMsg);
            if (fm.Length > 0)
            {
                foreach (var head in new[] { "false", "nil" })
                    if (text.StartsWith(head, StringComparison.OrdinalIgnoreCase) && text[head.Length..].TrimStart(' ', ':', ',', '-').StartsWith(fm, StringComparison.Ordinal))
                        return LastFailure;
                if (string.Equals(text, fm, StringComparison.Ordinal)) return LastFailure;
            }
        }
        return null;
    }

    internal static string OneLine(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
