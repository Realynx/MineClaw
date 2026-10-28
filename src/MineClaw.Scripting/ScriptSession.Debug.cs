using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MineClaw.Scripting;

/// <summary>
/// The debugger's side of a session (the Explorer's live script panel, the Supervisor's <c>/api/bodies/{name}/script</c>).
/// <para>
/// Cost model. Nobody watching: every 16 statements one volatile read (inside the budget hook that already runs there), one
/// small object per host action (label, args, start time) and the stack text the engine already builds for the call site. A
/// reader <see cref="DebugSnapshot">asking</see> takes a lease (<see cref="WatchLease"/>); while it holds, the script thread
/// publishes a sample (stack, locals, blackboard) at most every <see cref="SampleInterval"/> (≈ 5 Hz) from the budget hook and
/// at each host action. The current line is read off-thread from the engine's last-statement pointer (a reference read: no
/// hook at all). Locals are read off-thread only while the script thread is parked in a host call or a pause.
/// </para>
/// </summary>
public sealed partial class ScriptSession
{
    /// <summary>How long one snapshot request keeps the sampling on (the client polls at 2-5 Hz, so a closed panel stops it).</summary>
    public static readonly TimeSpan WatchLease = TimeSpan.FromSeconds(3);
    /// <summary>Script-thread sampling period while watched (≈ 5 Hz).</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(200);

    private long _watchUntil;          // Environment.TickCount64 ms; 0 = nobody watching
    private long _lastSampleTick;
    private long _seq;
    private volatile DebugSample? _sample;
    private volatile ActionNow? _actionNow;
    private volatile bool _blocked;    // the script thread is parked in a host call or a pause (its engine is still)
    private volatile MachineTrace? _machineTrace;
    private readonly List<Sdk.MachineRunner> _machines = new();
    private readonly Dictionary<int, Crew.ILane> _traceLanes = new();
    private readonly object _probeLock = new();
    private long _probeWarned;

    /// <summary>True while a debugger holds a lease (script thread: sample now and then).</summary>
    public bool Watched
    {
        get
        {
            long until = Volatile.Read(ref _watchUntil);
            return until != 0 && Environment.TickCount64 <= until;
        }
    }

    /// <summary>Script thread, every 16 statements (the budget hook): sample while a debugger watches; otherwise one read.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DebugTick()
    {
        if (Volatile.Read(ref _watchUntil) != 0) DebugTickSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DebugTickSlow()
    {
        long until = Volatile.Read(ref _watchUntil), now = Environment.TickCount64;
        if (until == 0) return;
        if (now > until) { Interlocked.CompareExchange(ref _watchUntil, 0, until); return; }
        if (now - _lastSampleTick < (long)SampleInterval.TotalMilliseconds) return;
        SampleOnScriptThread(now, freshStack: true);
    }

    /// <summary>Script thread: stack, locals, the machine's blackboard, published as one immutable sample.</summary>
    private void SampleOnScriptThread(long now, bool freshStack)
    {
        _lastSampleTick = now;
        if (_runtime is not IScriptDebugProbe probe) return;
        try
        {
            var stack = freshStack ? probe.StackText() : probe.LastStackText();
            var (locals, note) = probe.Locals();
            JsonNode? bb = null;
            Sdk.MachineRunner? m;
            lock (_machines) m = _machines.Count > 0 ? _machines[^1] : null;
            if (m != null) try { bb = m.Blackboard()?.DeepClone(); } catch (Exception) { }
            _sample = new DebugSample(now, stack, locals, note, m != null ? bb : null, m != null);
            Interlocked.Increment(ref _seq);
        }
        catch (Exception ex) { ProbeFailed(ex); }
    }

    private void ProbeFailed(Exception ex)
    {
        if (Interlocked.Exchange(ref _probeWarned, 1) == 0) _host.Log?.Invoke($"script {Name}: debugger probe failed: {ex.GetType().Name}: {OneLine(ex.Message)}");
    }

    /// <summary>Script thread, at a host action's start (after <see cref="Locate"/>): what runs now, and a sample when watched.</summary>
    private void TraceActionStart(string label, bool atomic, JsonElement? args)
    {
        if (!_o.DebugProbe) return;
        _actionNow = new ActionNow(label, args, atomic, Stopwatch.GetTimestamp(), 0, null);
        if (Volatile.Read(ref _watchUntil) != 0 && Watched) SampleOnScriptThread(Environment.TickCount64, freshStack: false);
    }

    private void TraceActionEnd(bool ok)
    {
        if (!_o.DebugProbe || _actionNow is not { } a) return;
        _actionNow = a with { Ended = Stopwatch.GetTimestamp(), Ok = ok };
    }

    /// <summary>Script thread, leaving a host call or a pause: no off-thread probe may still be reading the engine after this.</summary>
    private void WaitProbe()
    {
        _blocked = false;
        lock (_probeLock) { }
    }

    // ------------------------------------------------------------------ machines and lanes (script thread, rare)

    internal void EnterMachine(Sdk.MachineRunner m) { lock (_machines) _machines.Add(m); }

    internal void ExitMachine(Sdk.MachineRunner m)
    {
        lock (_machines)
        {
            _machines.Remove(m);
            if (_machineTrace?.Runner == m) _machineTrace = null;
        }
    }

    /// <summary>A machine entered a state: its history and (on a transition) the checkpoint snapshot with the blackboard.</summary>
    internal void TraceMachine(Sdk.MachineRunner m, string state, string? snapshotJson)
    {
        if (!_o.DebugProbe) return;
        JsonNode? bb = _machineTrace?.Runner == m ? _machineTrace.Blackboard : null;
        if (snapshotJson != null)
            try { bb = JsonNode.Parse(snapshotJson)?["bb"]?.DeepClone(); } catch (JsonException) { }
        _machineTrace = new MachineTrace(m, m.Id, state, m.TransitionCount, m.History.ToArray(), bb, Environment.TickCount64);
    }

    internal void TraceLane(int id, Crew.ILane lane)
    {
        lock (_traceLanes) _traceLanes[id] = lane;
    }

    // ------------------------------------------------------------------ the reader (any thread)

    /// <summary>
    /// The debugger snapshot (any thread, never blocks the script): takes or renews the watch lease, reads what the script thread
    /// published, the engine's current line, and — while the script is parked in a host call or a pause — its locals.
    /// <paramref name="code"/> adds the source.
    /// </summary>
    public ScriptDebugSnapshot DebugSnapshot(bool code = false)
    {
        long now = Environment.TickCount64;
        bool live = IsLive;
        if (live && _o.DebugProbe) Volatile.Write(ref _watchUntil, now + (long)WatchLease.TotalMilliseconds);
        var probe = live ? _runtime as IScriptDebugProbe : null;
        var sample = _sample;
        var act = _actionNow;

        // Off-thread refresh while the engine is still (a long gen/goto, a pause): the script thread can't sample until it
        // runs again, so read the locals here. Only reads (no getters run); any race is caught and the sample kept.
        if (probe != null && _blocked && (sample == null || now - sample.Tick > 1000 || (act != null && sample.Tick < TickOf(act.Started))))
        {
            lock (_probeLock)
                try
                {
                    if (_blocked)
                    {
                        var (locals, note) = probe.Locals();
                        var stack = probe.LastStackText();
                        Sdk.MachineRunner? m;
                        lock (_machines) m = _machines.Count > 0 ? _machines[^1] : null;
                        JsonNode? bb = sample?.Blackboard;
                        if (m != null) try { bb = m.Blackboard(); } catch (Exception) { }
                        sample = new DebugSample(now, stack, locals, note, bb, m != null, sample?.Tick);
                        _sample = sample;
                        Interlocked.Increment(ref _seq);
                    }
                }
                catch (Exception ex) { ProbeFailed(ex); }
        }

        var frames = ParseStack(sample?.Stack ?? (probe?.LastStackText()));
        (string File, int Line, int Col)? at = null;
        try { at = probe?.LiveLocation(); } catch (Exception) { }
        int line = Line, col = 0;
        bool lineLive = false;
        string? inSdk = null;
        if (at is { Line: > 0 } a)
        {
            if (a.File == "script") { line = a.Line; col = a.Col; lineLive = true; }
            else
            {
                inSdk = $"{a.File}:{a.Line}";
                if (frames.FirstOrDefault(f => f.Script) is { } sf) line = sf.Line;
            }
        }
        else if (frames.FirstOrDefault(f => f.Script) is { } sf2) line = sf2.Line;

        ScriptMachineView? machine = null;
        if (_machineTrace is { } mt)
        {
            var bb = sample is { HasMachine: true } s0 && s0.Blackboard != null && s0.Tick >= mt.Tick ? s0.Blackboard : mt.Blackboard;
            double? bbAge = bb == null ? null : Math.Round((now - (ReferenceEquals(bb, mt.Blackboard) ? mt.Tick : sample!.Tick)) / 1000.0, 1);
            machine = new ScriptMachineView(mt.Id, mt.State, mt.Transitions, mt.History, bb?.DeepClone(), bbAge);
        }

        List<ScriptLaneView> lanes;
        lock (_traceLanes) lanes = _traceLanes.Select(kv => new ScriptLaneView(kv.Key, kv.Value.Body, SafeStatus(kv.Value))).ToList();

        List<string> logs;
        lock (_lock) logs = Logs.TakeLast(8).ToList();
        var paused = Status == ScriptStatus.Paused;
        return new ScriptDebugSnapshot
        {
            SessionId = Id, Name = Name, Version = Request.Version, Lang = ScriptLanguages.Tag(Language),
            Status = Status.ToString().ToLowerInvariant(), Live = live, Body = _host.Body ?? "main", Author = Request.Author,
            Role = Request.Role, Crew = Request.CrewId, At = _time.GetUtcNow(), Started = Started, Seq = Interlocked.Read(ref _seq),
            Line = line, Column = col, LineLive = lineLive && live, InSdk = inSdk, Snippet = SourceLine(line),
            Stack = frames, StackAgeSeconds = sample != null ? Math.Round((now - sample.Tick) / 1000.0, 1) : null,
            Action = act == null ? null : new ScriptActionView(act.Label, ArgsText(act.Args),
                Math.Round(Stopwatch.GetElapsedTime(act.Started, act.Ended != 0 ? act.Ended : Stopwatch.GetTimestamp()).TotalSeconds, 2),
                act.Atomic, act.Ended == 0 && live, act.Ok),
            Actions = Actions, ElapsedSeconds = Math.Round(Elapsed.TotalSeconds, 1),
            Timings = Timings.OrderByDescending(kv => kv.Value.Total).Select(kv => new ScriptTimingRow(kv.Key, kv.Value.Calls, kv.Value.Fails,
                Math.Round(kv.Value.Total.TotalSeconds, 2), kv.Value.Calls == 0 ? 0 : Math.Round(kv.Value.Total.TotalMilliseconds / kv.Value.Calls, 1))).ToList(),
            Params = ParseJson(ParamsJson), Args = ParseJson(Request.ArgsJson),
            Checkpoint = Checkpoint, CheckpointsPassed = CheckpointsPassed, CheckpointTotal = CheckpointTotal,
            Paused = paused, PausePending = PausePending && !paused, PauseKind = PausedFor?.ToString().ToLowerInvariant(), PauseReason = PauseReason,
            PausedSeconds = PausedAt is { } pa ? Math.Round((_time.GetUtcNow() - pa).TotalSeconds, 1) : null,
            Machine = machine, Patching = Patching, PatchNote = PatchNote, Patches = Patches,
            Locals = sample?.Locals,
            LocalsNote = probe == null ? (live ? "locals are shown for JavaScript only" : "the script ended") : sample?.LocalsNote ?? (sample == null ? "sampling starts with the next statement or action" : null),
            LocalsAgeSeconds = sample?.Locals != null ? Math.Round((now - sample.Tick) / 1000.0, 1) : null,
            Lanes = lanes, ProgressPct = ProgressPct, ProgressNote = ProgressNote, LastFailure = LastFailure, Outcome = Outcome, Logs = logs,
            Code = code ? Code : null,
        };
    }

    private static string SafeStatus(Crew.ILane l)
    {
        try { return l.Status; } catch (Exception) { return "?"; }
    }

    private static long TickOf(long stopwatchTs) =>
        Environment.TickCount64 - (long)Stopwatch.GetElapsedTime(stopwatchTs).TotalMilliseconds;

    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var n = JsonNode.Parse(json);
            if (n is JsonObject o) foreach (var k in o.Select(kv => kv.Key).Where(k => k.StartsWith("__", StringComparison.Ordinal)).ToList()) o.Remove(k);
            return n;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>"5,64,7" for a position, "cobblestone 16" for positional values, else compact JSON (≤ 120 chars).</summary>
    internal static string? ArgsText(JsonElement? args)
    {
        if (args is not { } a) return null;
        try
        {
            if (a.ValueKind != JsonValueKind.Object) return Short(a.GetRawText(), 120);
            var parts = new List<string>();
            foreach (var p in a.EnumerateObject())
            {
                var v = p.Value;
                string text = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                    JsonValueKind.Object when v.TryGetProperty("x", out var x) && v.TryGetProperty("y", out var y) && v.TryGetProperty("z", out var z) =>
                        $"{x.GetRawText()},{y.GetRawText()},{z.GetRawText()}",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => v.GetRawText(),
                };
                if (text.Length > 0) parts.Add(text);
            }
            return parts.Count == 0 ? null : Short(string.Join(" ", parts), 120);
        }
        catch (Exception) { return null; }
    }

    private static readonly Regex FrameRx = new(@"at\s+(?:(?<fn>[^\n]+?)\s+\()?(?<file>[\w.\-/]+):(?<line>\d+):(?<col>\d+)\)?", RegexOptions.Compiled);

    /// <summary>
    /// Jint's stack text → frames, innermost first: "at go (script:3:1)" is the host function <c>go</c> called from script
    /// line 3, "at mineGen (sdk/50-jobs.js:41:7)" an SDK helper, "at (anonymous) (script:3:1)" the script body (the async
    /// wrapper), and the last bare "at script:12:2" the wrapper's own invocation, which is dropped. Line-1 columns lose the
    /// wrapper's prefix.
    /// </summary>
    internal static IReadOnlyList<ScriptFrame> ParseStack(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var list = new List<ScriptFrame>();
        foreach (Match m in FrameRx.Matches(text))
        {
            var file = m.Groups["file"].Value;
            int line = int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int col = int.Parse(m.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture);
            bool script = file == "script";
            if (script && line == 1) col = Math.Max(1, col - Runtime.Js.JsRuntime.WrapPrefix.Length);
            var fn = m.Groups["fn"].Success ? m.Groups["fn"].Value.Trim() : "";
            if (fn.Length == 0 && script) continue; // the wrapper's invocation (global code), not a frame of the script
            if (fn.Length == 0 || fn is "anonymous" or "(anonymous)") fn = "(anonymous)";
            list.Add(new ScriptFrame(fn, file, line, col, script));
            if (list.Count >= 24) break;
        }
        // The outermost script frame is the script body itself (the async wrapper).
        int top = list.FindLastIndex(f => f.Script);
        if (top >= 0 && top == list.Count - 1 && list[top].Function == "(anonymous)") list[top] = list[top] with { Function = "(top level)" };
        return list;
    }

    private sealed record DebugSample(long Tick, string? Stack, IReadOnlyList<ScriptLocal>? Locals, string? LocalsNote, JsonNode? Blackboard,
        bool HasMachine, long? StackTick = null);

    private sealed record ActionNow(string Label, JsonElement? Args, bool Atomic, long Started, long Ended, bool? Ok);

    private sealed record MachineTrace(Sdk.MachineRunner Runner, string Id, string State, int Transitions, IReadOnlyList<Sdk.MachineStep> History,
        JsonNode? Blackboard, long Tick);
}

/// <summary>What a runtime can tell a debugger (JS only; the legacy Lua runtime has none).</summary>
internal interface IScriptDebugProbe
{
    /// <summary>Any thread: the engine's current statement (source file, line, column), or null.</summary>
    (string File, int Line, int Col)? LiveLocation();
    /// <summary>Script thread: the call stack now.</summary>
    string? StackText();
    /// <summary>Any thread: the stack text the last host call built (no work).</summary>
    string? LastStackText();
    /// <summary>The innermost script frame's locals (script thread, or off-thread while it is parked), or null with why.</summary>
    (IReadOnlyList<ScriptLocal>? Locals, string? Note) Locals();
}
