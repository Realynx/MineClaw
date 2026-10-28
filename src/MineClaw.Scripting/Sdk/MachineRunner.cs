using System.Text.Json.Nodes;

namespace MineClaw.Scripting.Sdk;

/// <summary>
/// One <c>on</c> entry: the event key ("danger", "chat:come", "hungry", "toolBroke:pickaxe", "after:5000", "every:20", "when:3"),
/// a fixed target state, a handler (runtime id, ≥ 0) that may return the target, and a guard (name or runtime id).
/// </summary>
internal sealed record TransitionSpec(string Key, string? Target, int Handler = -1, string? Guard = null, int GuardFn = -1);

internal sealed record StateSpec(string Name, bool HasEnter, bool HasTick, bool HasExit, IReadOnlyList<TransitionSpec> On, bool Final, double? IdleMs);

/// <summary>A machine's static shape (read once from the script's spec); behaviour stays in the runtime's callbacks.</summary>
internal sealed record MachineSpec(string Id, string Initial, IReadOnlyDictionary<string, StateSpec> States, IReadOnlyList<TransitionSpec> On,
    JsonObject? Thresholds = null);

/// <summary>One transition in a machine's history: from → to, why ("on danger", "tick", "after:5000"), seconds into the run.</summary>
public sealed record MachineStep(string From, string To, string Why, double At);

/// <summary>The runtime side of a machine: runs the script's callbacks and holds its blackboard.</summary>
internal interface IMachineHost
{
    string? Enter(string state);
    string? Tick(string state);
    void Exit(string state);
    /// <summary>
    /// The transition's guard and handler for <paramref name="e"/>: Taken = false when the guard said no (the next entry may take
    /// it); Target = the next state, or null (the event is consumed, the machine stays).
    /// </summary>
    (bool Taken, string? Target) Handle(TransitionSpec t, ScriptEvent e);
    JsonNode? Blackboard();
    void Restore(JsonNode? bb);
    /// <summary>The state changed (the script's <c>m.state</c> / <c>m.history</c>).</summary>
    void Changed(string state, IReadOnlyList<MachineStep> history);
}

/// <summary>
/// The state-machine core, host-side because it is hot and must own the interrupt semantics: enter → tick… → exit per state,
/// transitions on events (the session's event stream: danger and its topics, chat, signals), timers (<c>after</c>, <c>every</c>)
/// and conditions (<c>when</c>) — an event a state listens for cuts the in-flight interruptible action and unwinds the tick at the
/// next safe point. The current state, the history and the blackboard are written to the session's checkpoint "@id" on every
/// transition, so a restart (<c>resume_from</c>) resumes in the same state with the same blackboard. Deterministic: the same
/// events in the same order give the same transitions; transitions requested while exiting a state are deferred until it has
/// exited. A tick that did no action idles <see cref="ScriptOptions.MachineIdleMs"/> (not counted as an action).
/// </summary>
internal sealed class MachineRunner
{
    public const int MaxHistory = 20;
    public const int MaxTransitions = 5000;

    private readonly ScriptSession _s;
    private readonly MachineSpec _spec;
    private readonly IMachineHost _host;
    private readonly List<MachineStep> _history = new();
    private volatile StateSpec _current;
    private object _stateToken = new();
    private bool _inExit;
    private string? _deferred;
    private int _transitions;

    public MachineRunner(ScriptSession s, MachineSpec spec, IMachineHost host)
    {
        _s = s;
        _spec = spec;
        _host = host;
        if (!spec.States.TryGetValue(spec.Initial, out var init)) throw ScriptSession.Bug($"machine '{spec.Id}': initial state '{spec.Initial}' isn't in states");
        foreach (var t in spec.On.Concat(spec.States.Values.SelectMany(x => x.On)))
            if (t.Target is { } to && to != "done" && !spec.States.ContainsKey(to))
                throw ScriptSession.Bug($"machine '{spec.Id}': '{t.Key}' goes to unknown state '{to}'");
        _current = init;
    }

    public string Label => "@" + _spec.Id;
    public string State => _current.Name;
    public IReadOnlyList<MachineStep> History => _history;

    /// <summary>Runs until a final state (or "done"); returns its name.</summary>
    public string Run()
    {
        string state = _spec.Initial;
        if (_s.ResumeTarget == Label)
        {
            state = Restore() ?? state;
            _s.ReachedResumeTarget(Label);
        }
        _current = _spec.States.TryGetValue(state, out var st) ? st : _spec.States[_spec.Initial];
        state = _current.Name;
        foreach (var key in AllKeys()) if (!IsTimer(key) && !key.StartsWith("when:", StringComparison.Ordinal)) _s.Signals.Ensure(key, Threshold(key));
        _s.AddHandler(new ScriptSession.Handler($"machine '{_spec.Id}'", Match, Handle, Cuts: true, Owner: this));
        StartTimers(_spec.On, this);
        try
        {
            bool entered = false;
            string? pending = null;
            string why = "start";
            while (true)
            {
                try
                {
                    if (!entered)
                    {
                        entered = true;
                        Publish(state);
                        StartTimers(_current.On, _stateToken);
                        pending = Enter(state);
                        why = "enter";
                    }
                    while (pending != null)
                    {
                        if (pending == "done" && !_spec.States.ContainsKey("done")) { Step(state, "done", why); return "done"; }
                        state = Switch(state, pending, why);
                        pending = Enter(state);
                        why = "enter";
                    }
                    if (_current.Final) return state;
                    _s.SafePoint();
                    int before = _s.Actions;
                    var next = _current.HasTick ? _host.Tick(state) : null;
                    if (next != null) { pending = next; why = "tick"; continue; }
                    if (_s.Actions == before) _s.Wait((_current.IdleMs ?? _s.Options.MachineIdleMs) / 1000.0, counts: false);
                }
                catch (ScriptUnwind u) when (ReferenceEquals(u.Target, this))
                {
                    pending = u.To;
                    why = u.Message;
                }
            }
        }
        finally
        {
            _s.RemoveHandlers(this);
            _s.Signals.RemoveOwner(this);
            _s.Signals.RemoveOwner(_stateToken);
            foreach (var key in AllKeys().Where(k => k.StartsWith("when:", StringComparison.Ordinal))) _s.Signals.Activate(key, false);
        }
    }

    private string? Enter(string state)
    {
        var p = _current.HasEnter ? _host.Enter(state) : null;
        if (_deferred != null) { p ??= _deferred; _deferred = null; }
        return p;
    }

    /// <summary>exit(from) → history, checkpoint → the new state's timers and conditions. Returns the new state.</summary>
    private string Switch(string from, string to, string why)
    {
        if (!_spec.States.TryGetValue(to, out var next)) throw ScriptSession.Bug($"machine '{_spec.Id}': no state '{to}' (from {from})");
        if (++_transitions > MaxTransitions) _s.Limit($"machine '{_spec.Id}': {MaxTransitions} transitions");
        if (_current.HasExit)
        {
            _inExit = true;
            try { _host.Exit(from); }
            finally { _inExit = false; }
        }
        StopTimers();
        _current = next;
        Step(from, to, why);
        StartTimers(next.On, _stateToken);
        return to;
    }

    private void Step(string from, string to, string why)
    {
        _history.Add(new MachineStep(from, to, why, Math.Round(_s.Elapsed.TotalSeconds, 1)));
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
        _s.Transitions++;
        _s.Log($"machine {_spec.Id}: {from} -> {to} ({why})");
        Publish(to);
        _s.Mark(Label, Snapshot(to));
    }

    private void Publish(string state)
    {
        _s.MachineState = _spec.Id == "m" ? state : $"{_spec.Id}:{state}";
        _host.Changed(state, _history);
    }

    private void StartTimers(IReadOnlyList<TransitionSpec> on, object owner)
    {
        foreach (var t in on)
        {
            if (IsTimer(t.Key)) _s.Signals.AddTimer(t.Key, owner);
            else if (t.Key.StartsWith("when:", StringComparison.Ordinal)) _s.Signals.Activate(t.Key, true);
        }
    }

    private void StopTimers()
    {
        _s.Signals.RemoveOwner(_stateToken);
        _stateToken = new object();
        var global = _spec.On.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var t in _current.On)
            if (t.Key.StartsWith("when:", StringComparison.Ordinal) && !global.Contains(t.Key)) _s.Signals.Activate(t.Key, false);
    }

    private static bool IsTimer(string key) => key.StartsWith("after:", StringComparison.Ordinal) || key.StartsWith("every:", StringComparison.Ordinal);

    private IEnumerable<string> AllKeys() => _spec.On.Concat(_spec.States.Values.SelectMany(x => x.On)).Select(t => t.Key).Distinct(StringComparer.Ordinal);

    private JsonNode? Threshold(string key)
    {
        var head = key.Split(':')[0];
        return _spec.Thresholds?[head] switch
        {
            JsonValue v when ScriptSession.AsNumber(v) is { } n => head == "invFull" ? new JsonObject { ["free"] = n } : new JsonObject { ["below"] = n },
            JsonObject o => o.DeepClone(),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ events

    /// <summary>Any thread: does the current state (or the machine) listen for this event?</summary>
    private bool Match(ScriptEvent e)
    {
        var cur = _current;
        return cur.On.Any(t => e.Matches(t.Key)) || _spec.On.Any(t => e.Matches(t.Key));
    }

    /// <summary>Script thread, at a safe point: the first matching transition whose guard passes moves the machine.</summary>
    private string? Handle(ScriptEvent e)
    {
        foreach (var t in _current.On.Concat(_spec.On))
        {
            if (!e.Matches(t.Key)) continue;
            var (taken, target) = _host.Handle(t, e);
            if (!taken) continue;
            if (target == null) return null;
            if (_inExit) { _deferred ??= target; return null; }
            throw new ScriptUnwind(this, target, "on " + e.Name);
        }
        // A danger nobody here took keeps the default policy: pause and wake the brain.
        return e.Kind == ScriptEventKind.Danger ? "pause" : null;
    }

    // ------------------------------------------------------------------ persistence

    private string Snapshot(string state)
    {
        var o = new JsonObject
        {
            ["state"] = state,
            ["transitions"] = _transitions,
            ["history"] = new JsonArray(_history.TakeLast(8).Select(h => (JsonNode)new JsonObject
            {
                ["from"] = h.From, ["to"] = h.To, ["why"] = h.Why, ["at"] = h.At,
            }).ToArray()),
        };
        try { o["bb"] = _host.Blackboard()?.DeepClone(); } catch (Exception) { }
        try { if (_s.CurrentCarry() is { } c) o["carry"] = JsonNode.Parse(c); } catch (Exception) { }
        var json = o.ToJsonString();
        if (json.Length > 4000) { o.Remove("history"); json = o.ToJsonString(); }
        return json;
    }

    private string? Restore()
    {
        if (string.IsNullOrWhiteSpace(_s.Request.ResumeState)) return null;
        try
        {
            if (JsonNode.Parse(_s.Request.ResumeState) is not JsonObject o) return null;
            if (o["history"] is JsonArray h)
                foreach (var x in h.OfType<JsonObject>())
                    _history.Add(new MachineStep(ScriptSession.Str(x["from"]), ScriptSession.Str(x["to"]), ScriptSession.Str(x["why"]),
                        ScriptSession.AsNumber(x["at"]) ?? 0));
            _transitions = (int)(ScriptSession.AsNumber(o["transitions"]) ?? 0);
            _host.Restore(o["bb"]);
            var state = ScriptSession.AsString(o["state"]);
            _s.Log($"machine {_spec.Id}: resumed in {state}");
            return state != null && _spec.States.ContainsKey(state) ? state : null;
        }
        catch (Exception) { return null; }
    }
}
