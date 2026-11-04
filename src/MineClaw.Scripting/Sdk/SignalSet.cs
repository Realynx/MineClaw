using System.Globalization;
using System.Text.Json.Nodes;
using MineClaw.Data;

namespace MineClaw.Scripting.Sdk;

/// <summary>
/// Thrown at a safe point to leave a JS frame for the C# frame that owns it: a machine transition (<see cref="Target"/> = the
/// runner, <see cref="To"/> = the next state) or a <c>timeout()</c> expiring. A CLR exception, so no script try/catch sees it.
/// </summary>
internal sealed class ScriptUnwind(object target, string to, string reason) : Exception(reason)
{
    public object Target { get; } = target;
    public string To { get; } = to;
}

/// <summary>
/// Edge-triggered conditions a script listens for (zero tokens, C#): built-ins over the world state (hungry, invFull, toolBroke,
/// hurt, night, day), timers (<c>after</c>, <c>every</c>, timeouts) and <c>when(fn)</c> predicates. A signal fires once when its
/// condition becomes true (also right away if it is already true when registered) and re-arms when it turns false; firing posts
/// a <see cref="ScriptEventKind.Signal"/> event with the signal's topic through the session's normal event path. Built-ins and
/// timers are polled by the session's watchdog (so they cut a long action) and at every safe point; <c>when</c> predicates run
/// on the script thread at safe points only (every 100 ms, or every 25 safe points).
/// </summary>
internal sealed class SignalSet(ScriptSession s)
{
    private sealed class Signal
    {
        public required string Topic;
        public required Func<bool> Cond;
        public bool ScriptThread;
        public bool Armed = true;
        public bool Active = true;
        public object? Owner;
        public DateTimeOffset? Due;
        public TimeSpan? Period;
        public string Text = "";
    }

    /// <summary>Built-in topics and their default thresholds.</summary>
    public static readonly IReadOnlyList<string> BuiltIns = ["hungry", "invFull", "toolBroke", "hurt", "night", "day"];

    private static readonly string[] ToolKinds = ["pickaxe", "axe", "shovel", "sword", "hoe"];

    private readonly object _lock = new();
    private readonly List<Signal> _list = new();
    private int _nextWhen;
    private DateTimeOffset _lastScriptPoll = DateTimeOffset.MinValue;
    private float _lastHp = -1;
    private readonly Dictionary<string, int> _lastTools = new(StringComparer.Ordinal);

    public int Count { get { lock (_lock) return _list.Count; } }
    private int _sincePoll;

    /// <summary>"hungry on, when:2 off, after:300 due" (diagnostics, the status of every signal).</summary>
    public string Describe()
    {
        lock (_lock) return string.Join(", ", _list.Select(x => $"{x.Topic} {(x.Active ? "on" : "off")}{(x.Due != null ? " due" : "")}{(x.Armed ? "" : " fired")}"));
    }

    public bool Has(string topic)
    {
        lock (_lock) return _list.Any(x => string.Equals(x.Topic, topic, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A built-in signal (idempotent per topic; a later call updates its threshold): hungry (food &lt; below, 14), invFull (free
    /// slots &lt; free, 1), toolBroke / toolBroke:pickaxe (the last tool of a kind is gone), hurt (hp dropped), night, day.
    /// False when the topic isn't a built-in.
    /// </summary>
    public bool Ensure(string topic, JsonNode? opts = null)
    {
        var t = (topic ?? "").Trim();
        var (head, tail) = t.IndexOf(':') is var c and > 0 ? (t[..c], t[(c + 1)..]) : (t, "");
        Func<bool>? cond = head switch
        {
            "hungry" => () => s.Host.World.Player.Food < Num(opts, "below", 14),
            "invFull" => () => s.Host.World.Inventory.EmptySlotCount() < Num(opts, "free", 1),
            "night" => () => s.Host.World.IsNight,
            "day" => () => !s.Host.World.IsNight,
            "hurt" => Hurt,
            "toolBroke" => tail.Length > 0 ? () => Broke(tail) : () => ToolKinds.Any(Broke),
            _ => null,
        };
        if (cond == null) return false;
        lock (_lock)
        {
            if (opts == null && _list.Any(x => x.Topic == t && x.Owner == null && x.Due == null)) return true; // keep its threshold
            _list.RemoveAll(x => x.Topic == t && x.Owner == null && x.Due == null);
            if (head == "toolBroke") foreach (var k in ToolKinds) _lastTools[k] = ToolCount(k);
            if (head == "hurt") _lastHp = s.Host.World.Player.Health;
            _list.Add(new Signal { Topic = t, Cond = cond, Text = Describe(head) });
        }
        return true;
    }

    /// <summary><c>when(fn)</c>: a predicate signal ("when:3"), polled on the script thread. Inactive until a handler or a machine state uses it.</summary>
    public string AddWhen(Func<bool> pred, object? owner = null)
    {
        lock (_lock)
        {
            var topic = "when:" + (++_nextWhen).ToString(CultureInfo.InvariantCulture);
            _list.Add(new Signal { Topic = topic, Cond = pred, ScriptThread = true, Active = false, Owner = owner, Text = "condition met" });
            return topic;
        }
    }

    /// <summary>A timer topic ("after:5000" once, "every:20" every 20 game ticks) starting now, owned by a machine state / handler.</summary>
    public void AddTimer(string topic, object? owner)
    {
        var (once, span) = ParseTimer(topic);
        if (span is not { } d) return;
        var now = s.Time.GetUtcNow();
        lock (_lock)
        {
            _list.RemoveAll(x => x.Topic == topic && ReferenceEquals(x.Owner, owner));
            _list.Add(new Signal { Topic = topic, Cond = () => true, Owner = owner, Due = now + d, Period = once ? null : d, Text = topic });
        }
    }

    /// <summary>A one-shot deadline under its own topic (<c>timeout()</c>: never confused with a machine's "after:" keys).</summary>
    public void AddDeadline(string topic, TimeSpan after, object owner)
    {
        var now = s.Time.GetUtcNow();
        lock (_lock) _list.Add(new Signal { Topic = topic, Cond = () => true, Owner = owner, Due = now + after, Text = topic });
    }

    /// <summary>A one-shot condition under its own topic, polled off the script thread (so it fires while an action runs):
    /// <c>story.during()</c>'s "the director took the job back". Remove it with <see cref="Remove"/>.</summary>
    public void AddCond(string topic, Func<bool> cond, object owner)
    {
        lock (_lock) _list.Add(new Signal { Topic = topic, Cond = cond, Owner = owner, Text = topic });
    }

    /// <summary>"after:5000" → (once, 5 s); "every:20" → (repeating, 1 s = 20 game ticks); else (false, null).</summary>
    public static (bool Once, TimeSpan? Span) ParseTimer(string topic)
    {
        int c = topic.IndexOf(':');
        if (c <= 0 || !double.TryParse(topic[(c + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return (false, null);
        return topic[..c] switch
        {
            "after" => (true, TimeSpan.FromMilliseconds(Math.Max(10, v))),
            "every" => (false, TimeSpan.FromMilliseconds(Math.Max(50, v * 50))),
            _ => (false, null),
        };
    }

    public void Activate(string topic, bool on)
    {
        lock (_lock)
            foreach (var x in _list)
                if (string.Equals(x.Topic, topic, StringComparison.OrdinalIgnoreCase)) { x.Active = on; if (!on) x.Armed = true; }
    }

    public void RemoveOwner(object owner)
    {
        lock (_lock) _list.RemoveAll(x => ReferenceEquals(x.Owner, owner) && x.Due != null);
    }

    public void Remove(string topic, object? owner)
    {
        lock (_lock) _list.RemoveAll(x => x.Topic == topic && ReferenceEquals(x.Owner, owner));
    }

    /// <summary>Evaluates the signals this thread may evaluate and posts the ones that fired.</summary>
    public void Poll(bool onScriptThread)
    {
        List<Signal> check;
        var now = s.Time.GetUtcNow();
        lock (_lock)
        {
            if (_list.Count == 0) return;
            // when() predicates: every 100 ms, or every 25 safe points when actions are quicker than that.
            bool scriptSignals = onScriptThread && (now - _lastScriptPoll >= TimeSpan.FromMilliseconds(100) || ++_sincePoll >= 25);
            if (scriptSignals) { _lastScriptPoll = now; _sincePoll = 0; }
            check = _list.Where(x => x.Active && (!x.ScriptThread || scriptSignals)).ToList();
        }
        var fired = new List<ScriptEvent>();
        foreach (var sig in check)
        {
            bool hit;
            if (sig.Due is { } due) hit = now >= due;
            else
            {
                try { hit = sig.Cond(); }
                catch (ScriptUnwind) { throw; }
                catch (ScriptStopException) { throw; }
                catch (ScriptLimitException) { throw; }
                catch (Exception ex) { hit = false; s.Host.Log?.Invoke($"signal {sig.Topic} failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            lock (_lock)
            {
                if (!_list.Contains(sig)) continue;
                if (sig.Due != null)
                {
                    if (!hit) continue;
                    if (sig.Period is { } p) sig.Due = now + p; else _list.Remove(sig);
                    fired.Add(new ScriptEvent(ScriptEventKind.Signal, sig.Text, Topic: sig.Topic));
                    continue;
                }
                if (hit && sig.Armed) { sig.Armed = false; fired.Add(new ScriptEvent(ScriptEventKind.Signal, sig.Text + Detail(sig.Topic), Topic: FiredTopic(sig))); }
                else if (!hit) sig.Armed = true;
            }
        }
        if (!onScriptThread) UpdateBaselines();
        foreach (var e in fired) s.Deliver(e);
        if (onScriptThread) UpdateBaselines();
    }

    // ------------------------------------------------------------------ built-in conditions

    private bool Hurt() => _lastHp >= 0 && s.Host.World.Player.Health < _lastHp;

    private bool Broke(string kind) => _lastTools.GetValueOrDefault(kind) > 0 && ToolCount(kind) == 0;

    private void UpdateBaselines()
    {
        lock (_lock)
        {
            if (_list.Any(x => x.Topic == "hurt")) _lastHp = s.Host.World.Player.Health;
            if (_list.Any(x => x.Topic.StartsWith("toolBroke", StringComparison.Ordinal)))
                foreach (var k in ToolKinds) _lastTools[k] = ToolCount(k);
        }
    }

    /// <summary>toolBroke fires with the kind that broke ("toolBroke:pickaxe").</summary>
    private string FiredTopic(Signal sig)
    {
        if (sig.Topic != "toolBroke") return sig.Topic;
        foreach (var k in ToolKinds) if (Broke(k)) return "toolBroke:" + k;
        return sig.Topic;
    }

    private string Detail(string topic) => topic switch
    {
        "hungry" => $" (food {s.Host.World.Player.Food})",
        "hurt" => $" (hp {s.Host.World.Player.Health})",
        "invFull" => $" ({s.Host.World.Inventory.EmptySlotCount()} free)",
        _ => "",
    };

    private int ToolCount(string kind)
    {
        int n = 0;
        try
        {
            var slots = s.Host.World.Inventory.PlayerSlots();
            for (int i = 9; i < slots.Length && i < 45; i++)
            {
                var st = slots[i];
                if (st.IsEmpty) continue;
                var name = Items.Get(st.Id).Name;
                if (name.EndsWith("_" + kind, StringComparison.Ordinal) || name == kind) n += st.Count;
            }
        }
        catch (Exception) { }
        return n;
    }

    private static string Describe(string head) => head switch
    {
        "hungry" => "hungry",
        "invFull" => "inventory full",
        "toolBroke" => "tool broke",
        "hurt" => "hurt",
        "night" => "night fell",
        "day" => "day broke",
        _ => head,
    };

    private static double Num(JsonNode? opts, string key, double def) =>
        opts is JsonObject o && ScriptSession.AsNumber(o[key]) is { } v ? v : def;
}
