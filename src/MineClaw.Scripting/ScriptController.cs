using System.Text.RegularExpressions;

namespace MineClaw.Scripting;

/// <summary>Telemetry row for one finished run.</summary>
public sealed record ScriptRunRecord(string Name, ScriptStatus Status, int Actions, TimeSpan Duration, DateTimeOffset At, string Outcome,
    string? Author = null, int? Version = null, int ItemsGained = 0, string? Params = null, string? Timings = null, string? Language = null,
    string? Body = null);

/// <summary>
/// One per bot: at most one script runs at a time. Brain/worker ops (run, status, pause, resume, cancel, replace), the
/// interrupt policy entry point (<see cref="Deliver"/>), checkpoint carries for <c>resume_from</c>, and run telemetry.
/// Agent-agnostic: the Bot layer adapts it to tools, the activity manager and the event queue.
/// </summary>
public sealed class ScriptController : IDisposable
{
    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Dictionary<string, string>> _checkpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScriptRunRecord> _runs = new();
    private ScriptSession? _current, _last;
    private int _nextId;

    public ScriptController(ScriptHost host, ScriptOptions? options = null, TimeProvider? time = null)
    {
        Host = host;
        Options = options ?? new ScriptOptions();
        Time = time ?? TimeProvider.System;
    }

    public ScriptHost Host { get; }
    public ScriptOptions Options { get; }
    public TimeProvider Time { get; }

    /// <summary>
    /// One brain, many bodies: this controller's body scope (the Bot layer's <c>BodyScope.Push(body)</c>), pushed on the script
    /// thread for the whole run and again around every host action, so script actions act on this lane's body. Null = none.
    /// </summary>
    public Func<IDisposable?>? StepScope { get; set; }

    /// <summary>The running or paused script, or null.</summary>
    public ScriptSession? Current { get { lock (_lock) return _current is { IsLive: true } c ? c : null; } }
    public ScriptSession? Last { get { lock (_lock) return _last; } }
    public IReadOnlyList<ScriptRunRecord> Runs { get { lock (_lock) return _runs.ToArray(); } }

    /// <summary>Raised on the script thread when a run ends (stats, persistence, promotion, telemetry).</summary>
    public event Action<ScriptSession>? Ended;
    /// <summary>Raised on the script thread once a pause took effect (the adapter wakes the brain with <see cref="ScriptSession.State"/>).</summary>
    public event Action<ScriptSession>? Paused;
    /// <summary>(session, automatic) — automatic = chat pause timed out.</summary>
    public event Action<ScriptSession, bool>? Resumed;
    /// <summary>Raised when a run starts (before its thread runs).</summary>
    public event Action<ScriptSession>? Started;

    public static string CleanName(string? raw)
    {
        var s = Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (s.Length > 40) s = s[..40];
        return s.Length > 0 ? s : "script";
    }

    /// <summary>Starts a run. Error text instead when one is already live or the request is invalid.</summary>
    public (ScriptSession? Session, string? Error) Start(ScriptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return (null, "no code");
        if (request.Code.Length > Options.MaxCodeChars) return (null, $"script too long ({request.Code.Length} > {Options.MaxCodeChars} chars)");
        if (Host.ActionVeto() is { } veto) return (null, "can't run scripts now: " + veto);
        var req = request with { Name = CleanName(request.Name) };
        if (req.ResumeFrom is { Length: > 0 } label)
        {
            label = label.Trim();
            if (!ScriptSession.HasCheckpoint(req.Code, label)) return (null, $"no checkpoint('{label}') in script {req.Name}");
            // A restart: the snapshot comes back from the durable store (cp carry; a machine's state, history and blackboard).
            var snap = CheckpointCarry(req.Name, label) ?? SafeLoad(req.Name, label);
            if (label.StartsWith('@'))
            {
                req = req with { ResumeState = req.ResumeState ?? snap };
                if (req.CarryJson == null && snap != null)
                    try { if (System.Text.Json.Nodes.JsonNode.Parse(snap)?["carry"] is System.Text.Json.Nodes.JsonObject c) req = req with { CarryJson = c.ToJsonString() }; }
                    catch (System.Text.Json.JsonException) { }
            }
            else if (req.CarryJson == null) req = req with { CarryJson = snap };
            req = req with { ResumeFrom = label };
        }
        ScriptSession s;
        lock (_lock)
        {
            if (_current is { IsLive: true } live) return (null, "already running: " + live.State());
            s = new ScriptSession(this, ++_nextId, req, _shutdown.Token);
            _current = s;
        }
        Safe(() => Started?.Invoke(s));
        s.Start();
        return (s, null);
    }

    /// <summary>
    /// Re-script: the live script stops at its next safe point (an atomic action finishes first), then <paramref name="request"/>
    /// starts with the old script's <c>carry</c> (unless it brings its own).
    /// </summary>
    public async Task<(ScriptSession? Session, string? Error)> ReplaceAsync(ScriptRequest request, CancellationToken ct = default)
    {
        var old = Current;
        string? carry = request.CarryJson;
        if (old != null)
        {
            old.Stop("replaced");
            try { await old.Completion.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (TimeoutException) { return (null, "old script did not stop (atomic action still running); try again"); }
            carry ??= old.CarryJson;
            if (string.IsNullOrWhiteSpace(request.Name)) request = request with { Name = old.Name };
        }
        return Start(request with { CarryJson = carry });
    }

    public string Pause(string reason = "paused by planner")
    {
        var s = Current;
        if (s == null) return "no script running";
        if (s.Status == ScriptStatus.Paused) return "already paused: " + s.State();
        s.RequestPause(PauseKind.Brain, reason, abort: false);
        return "pausing at next safe point: " + s.State();
    }

    public string Resume()
    {
        var s = Current;
        if (s == null) return "no script to resume";
        return s.Resume() ? "resumed: " + s.State() : "not paused: " + s.State();
    }

    public string Cancel(string reason = "cancelled")
    {
        var s = Current;
        if (s == null) return "no script running";
        s.Stop(reason);
        return $"cancelling script {s.Name} at next safe point";
    }

    public string Status()
    {
        if (Current is { } s) return s.State();
        return Last is { } l ? "no script running; last: " + l.Outcome : "no script running";
    }

    /// <summary>Routes an event to the live script (wait_for, on() handlers, interrupt policy).</summary>
    public void Deliver(ScriptEvent e) => Current?.Deliver(e);

    private string? SafeLoad(string name, string label)
    {
        try { return Host.Checkpoints?.Load(CleanName(name), Host.Body ?? Host.Username, label); }
        catch (Exception ex) { Host.Log?.Invoke("checkpoint load failed: " + ex.Message); return null; }
    }

    /// <summary>carry snapshot taken at <c>checkpoint(label)</c> in the last run of <paramref name="name"/>.</summary>
    public string? CheckpointCarry(string name, string label)
    {
        lock (_lock)
            return _checkpoints.TryGetValue(CleanName(name), out var d) && d.TryGetValue(label, out var c) ? c : null;
    }

    internal void OnEnded(ScriptSession s)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, s)) _current = null;
            _last = s;
            if (s.CheckpointCarry.Count > 0) _checkpoints[s.Name] = new Dictionary<string, string>(s.CheckpointCarry, StringComparer.Ordinal);
            _runs.Add(new ScriptRunRecord(s.Name, s.Status, s.Actions, s.Elapsed, s.Finished ?? Time.GetUtcNow(), s.Outcome ?? "",
                s.Request.Author, s.Request.Version, s.ItemsGained, s.ParamsJson, TimingText(s), ScriptLanguages.Tag(s.Language), Host.Body));
            if (_runs.Count > 50) _runs.RemoveAt(0);
        }
        Safe(() => Ended?.Invoke(s));
    }

    /// <summary>"gen 12x 31.2s, deposit 2x 4.0s" (slowest first, ≤ 6).</summary>
    public static string? TimingText(ScriptSession s)
    {
        var t = s.Timings.OrderByDescending(kv => kv.Value.Total).Take(6).Select(kv => $"{kv.Key} {kv.Value}").ToList();
        return t.Count > 0 ? string.Join(", ", t) : null;
    }

    internal void OnPaused(ScriptSession s) => Safe(() => Paused?.Invoke(s));
    internal void OnResumed(ScriptSession s, bool auto) => Safe(() => Resumed?.Invoke(s, auto));

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Host.Log?.Invoke("script listener failed: " + ex.Message); }
    }

    public void Dispose()
    {
        Current?.Stop("shutdown");
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
    }
}
