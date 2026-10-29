using MineClaw.Agent.Events;

namespace MineClaw.Agent.Bodies;

/// <summary>
/// One brain, many bodies: the wake rules for events of non-main bodies (docs/design/one-brain-many-bodies.md section 2,
/// "Wake and triage rules"), applied at the queue's intake so N bodies never wake the brain N times:
/// <list type="bullet">
/// <item>danger (hp/void/death, High+) and MustWake (stall, failed detached job, kept promise) always pass, as they are;</item>
/// <item>a crew of bodies started together (one multi-body <c>do</c>): each member's task result is held (kept as
///   non-waking context) while another member is still busy; the last one wakes the brain, and the batch reads all of them
///   as one "[bodies] 67 ok: ...; 69 ok: ..." line (<see cref="EventBatch.Format"/>);</item>
/// <item>minor events (Low and below) of a non-focus body never wake: they ride along as context;</item>
/// <item>any other waking event of a non-focus body wakes the brain at most once per <see cref="MinWakeGap"/> per body.</item>
/// </list>
/// Events without a body (hive mode, main, brain-level) pass untouched.
/// </summary>
public sealed class BodyEventDigest
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastWake = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Group> _groups = new();

    private sealed class Group
    {
        public required HashSet<string> Members;
        public required DateTimeOffset Until;
    }

    /// <param name="focus">The brain's focus body (short name; null = main).</param>
    /// <param name="busy">Whether a body (short name) still has a job running.</param>
    public BodyEventDigest(Func<string?> focus, Func<string, bool> busy, TimeProvider? time = null)
    {
        Focus = focus;
        Busy = busy;
        _time = time ?? TimeProvider.System;
    }

    public Func<string?> Focus { get; }
    public Func<string, bool> Busy { get; }

    /// <summary>A non-focus body wakes the brain at most this often (danger and MustWake excepted).</summary>
    public TimeSpan MinWakeGap { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a crew group waits for its slowest member before its results wake the brain one by one again.</summary>
    public TimeSpan GroupTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Bodies a running crew owns (a lane of a coordinator that is still going: <c>everyBody</c>, a hive script): their minor and
    /// normal events never wake the brain (MustWake and typed chat still do), a lane's own end included (the coordinator re-runs it;
    /// the crew's one result wakes). Their danger passes here untouched and the lane's gate keeps it from waking the brain (the reflexes
    /// acted; the lane pauses and carries on once safe). Null = none. Live 10-29: a 20-body hive woke on every lane's zombie and every
    /// lane's end, then micro-managed them one by one.
    /// </summary>
    public Func<string, bool>? Unattended { get; set; }

    /// <summary>Events held back from waking (tests, telemetry).</summary>
    public int Held { get; private set; }

    /// <summary>Bodies started together by one brain call: their results are reported together.</summary>
    public void BeginGroup(IEnumerable<string> bodies)
    {
        var set = bodies.Select(Norm).Where(b => b != null).Select(b => b!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Count < 2) return;
        lock (_lock)
        {
            // A body belongs to its newest crew only.
            foreach (var g in _groups) g.Members.ExceptWith(set);
            _groups.RemoveAll(g => g.Members.Count == 0);
            _groups.Add(new Group { Members = set, Until = _time.GetUtcNow() + GroupTtl });
        }
    }

    /// <summary>The queue intake stage: returns the event (possibly non-waking now). Never drops one.</summary>
    public GameEvent? Apply(GameEvent e)
    {
        var body = Norm(e.Body);
        if (body == null) return e;
        // A person typing to a body (a whisper to its account) always wakes the brain, like any addressed chat.
        // Danger passes as it is: the body's lane gate must still read it as danger (it pauses the lane and, for a crew lane,
        // keeps the brain asleep itself).
        if (IsDanger(e) || e.MustWake || EventQueue.IsTyped(e)) return e;
        if (e.Wakes && Unattended is { } crewed && SafeCrewed(crewed, body)) { Held++; return e with { Wakes = false }; }
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _groups.RemoveAll(g => g.Until < now || g.Members.Count == 0);
            if (e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed && _groups.FirstOrDefault(g => g.Members.Contains(body)) is { } group)
            {
                group.Members.Remove(body);
                bool others = group.Members.Any(m => SafeBusy(m));
                if (!others) _groups.Remove(group);
                if (others && e.Wakes) { Held++; return e with { Wakes = false }; }
                if (!others) { _lastWake[body] = now; return e.Wakes ? e : e with { Wakes = e.Priority >= EventPriority.Normal }; }
            }
        }
        if (!e.Wakes) return e;
        var focus = Norm(Focus()) ?? "main";
        if (body.Equals(focus, StringComparison.OrdinalIgnoreCase)) return e;
        if (e.Priority <= EventPriority.Low) { Held++; return e with { Wakes = false }; }
        lock (_lock)
        {
            if (_lastWake.TryGetValue(body, out var last) && now - last < MinWakeGap) { Held++; return e with { Wakes = false }; }
            _lastWake[body] = now;
        }
        return e;
    }

    private static bool SafeCrewed(Func<string, bool> crewed, string body)
    {
        try { return crewed(body); } catch (Exception) { return false; }
    }

    private bool SafeBusy(string body)
    {
        try { return Busy(body); } catch (Exception) { return false; }
    }

    private static bool IsDanger(GameEvent e) =>
        e.Kind is EventKind.LowHealth or EventKind.VoidFall or EventKind.Death || e.Priority >= EventPriority.High;

    /// <summary>A body tag as the queue sees it: "67" / "maid_chan_67" → "67"; main / null → null.</summary>
    private static string? Norm(string? body) => GameEvent.BodyTag(body);
}
