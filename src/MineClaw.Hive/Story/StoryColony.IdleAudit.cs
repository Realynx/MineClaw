using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// The idle audit (docs/web/story-mode.md "Idle with work"). Fox 11-04: "We want most pawns doing things, and never idle on a work
/// schedule if there are things to be done." Once a second, a pawn in its work hours that holds, wanders or rests while there is work
/// of a kind it does gets a reason: <c>stuck</c>, <c>work not taken yet</c> (its last search found work an idle leg still holds),
/// <c>all targets leased</c>, <c>tool waiting</c>, <c>targets backing off</c>, <c>no reachable target</c>, <c>back-off rest</c>,
/// <c>waiting: …</c> (what held its kinds of work back in its last search: <c>build b3: need 24 block of iron</c>, <c>bill Iron Ingot
/// suspended: no iron ore …</c>), or <c>priority 0</c> (there is work, of kinds it doesn't do). The pawn's row carries
/// <c>idle_reason</c>; the colony's <c>blocked_work</c> groups them ("4 pawns idle: build b3 needs iron ore"); the supervisor's
/// per-minute perf line counts them (<c>idle-with-work N: …</c>).
/// <para>
/// Cheap by design (live 11-04: "60 ticks 6717 ms" with the audit running a full job search per idle pawn every second): the audit
/// never searches. It reads what the pawn's own last search saw (<see cref="Look"/>, kept by every <see cref="Work"/> call: the
/// picks, the wanderers' wake check) and one pass over the designations; the colony-wide part is built once per audit.
/// </para>
/// </summary>
public sealed partial class StoryColony
{
    private readonly Dictionary<string, string> _idleReason = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _idleAudit = DateTimeOffset.MinValue;

    /// <summary>What a pawn's last work search saw: when, whether it found a job, what held work of its kinds back, and why it passed
    /// over designations that are free and not resting (<see cref="NoReach"/>: the audit's "no reachable target: …").</summary>
    private sealed record Look(DateTimeOffset At, bool Found, List<string> Blocked, string? NoReach = null);

    private readonly Dictionary<string, Look> _looks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>While a work search runs: where its "held back" notes go (null = nobody listens).</summary>
    private List<string>? _blockedNotes;

    /// <summary>Work of the searching pawn's kinds is there but held back: a build short of a material, a suspended bill, a busy bench…</summary>
    private void Blocked(string why)
    {
        if (_blockedNotes is not { } l || l.Count >= 6) return;
        if (why.Length > 90) why = why[..87] + "...";
        if (!l.Contains(why)) l.Add(why);
    }

    /// <summary>" (short: 216 iron ore)": the raw shortfall behind a build's missing item (the materials pass), else "".</summary>
    private static string ShortSource(BuildMaterial? mat) =>
        mat is { Short: > 0, Raw.Count: > 0 } ? " (short: " + string.Join(", ", mat.Raw.Take(2)) + ")" : "";

    /// <summary>A look is about now for this long (an idle pawn searches again every few seconds: a pick, the wake check).</summary>
    private static readonly TimeSpan LookFresh = TimeSpan.FromSeconds(30);

    /// <summary>Why the pawn is idle with work about (null = it works, or there is none for it). The browser's <c>idle_reason</c>.</summary>
    public string? IdleReasonOf(string pawn) { lock (_lock) return _idleReason.GetValueOrDefault(pawn); }

    /// <summary>Every tick (at most once a second): each pawn's idle reason, counted for the perf line.</summary>
    private void AuditIdle()
    {
        if (Now - _idleAudit < TimeSpan.FromSeconds(1)) return;
        _idleAudit = Now;
        HashSet<string>? kinds = null; // the work types with something open: built once per audit, only when a pawn is idle
        foreach (var p in Pawns.Values)
        {
            string? why = null;
            try { why = IdleWithWork(p, ref kinds); } catch (Exception) { /* the audit never breaks the tick */ }
            if (why == null) { if (_idleReason.Remove(p.Name)) p.Rev = Bump(); continue; }
            if (!_idleReason.TryGetValue(p.Name, out var old) || old != why) { _idleReason[p.Name] = why; p.Rev = Bump(); }
            StoryPerf.NoteIdle(why);
        }
    }

    private string? IdleWithWork(PawnState p, ref HashSet<string>? kinds)
    {
        if (!IdleInWorkHours(p)) return null;
        if (_stuck.ContainsKey(p.Name)) return "stuck";
        var look = FreshLook(p.Name);
        if (look is { Found: true }) return "work not taken yet";
        if (MarksIdleReason(p, look) is { } why) return why;
        // What its last search found held back: a build short of something, a suspended bill, a busy bench.
        if (look is { Blocked: [var first, ..] }) return "waiting: " + first;
        kinds ??= WorkKindsWithWork();
        if (DoesAnyOf(p, kinds)) return null; // its own kinds: nothing it could start (searched)
        return kinds.Count > 0 ? "priority 0" : null;
    }

    /// <summary>
    /// Holding, wandering or resting (not working, nor between two jobs: it asks for the next one at once) in its work hours, online,
    /// neither drafted nor leaving.
    /// </summary>
    private bool IdleInWorkHours(PawnState p)
    {
        if (p.Drafted || _leaving.ContainsKey(p.Name)) return false;
        if (!_current.TryGetValue(p.Name, out var cur) || cur.Source is not ("none" or "wander" or "escape")) return false;
        if (ScheduleBlockAt(p, HourNow) is not (ScheduleBlocks.Work or ScheduleBlocks.Anything)) return false;
        return Host.Live(p.Name).Online;
    }

    /// <summary>The pawn's last work search, while it is still about now (null = none, or too old).</summary>
    private Look? FreshLook(string pawn)
    {
        var look = _looks.GetValueOrDefault(pawn);
        return look != null && Now - look.At > LookFresh ? null : look;
    }

    private static bool DoesAnyOf(PawnState p, HashSet<string> kinds)
    {
        foreach (var t in kinds) if (p.Priorities.GetValueOrDefault(t) is >= 1 and <= 4) return true;
        return false;
    }

    /// <summary>What the open designations of a pawn's kinds of work are to it (any one of each is enough).</summary>
    [Flags]
    private enum MarkSeen { None = 0, Waiting = 1, Leased = 2, BackingOff = 4, Open = 8 }

    /// <summary>Its designations: why none of them is for it (null = it has none to do). Free, not resting, yet not taken: what its
    /// own last search passed them over for (<see cref="MarkPass"/>), else plain "no reachable target".</summary>
    private string? MarksIdleReason(PawnState p, Look? look)
    {
        var seen = MarkSeen.None;
        foreach (var d in Designations) seen |= MarkSeenBy(p, d);
        if ((seen & MarkSeen.Open) != 0) return RestLeft(p) != null ? "back-off rest" : look?.NoReach ?? "no reachable target";
        if ((seen & MarkSeen.BackingOff) != 0) return "targets backing off";
        if ((seen & MarkSeen.Waiting) != 0) return "tool waiting";
        if ((seen & MarkSeen.Leased) != 0) return "all targets leased";
        return null;
    }

    private MarkSeen MarkSeenBy(PawnState p, DesignationState d)
    {
        if (!MarkOfItsWork(p, d)) return MarkSeen.None;
        var key = DesignationKey(d);
        if (d.Wait != null) return MarkSeen.Waiting;
        if (!Free(key, p.Name)) return MarkSeen.Leased;
        if (Skipped(key) || Skipped(PawnKey(key, p.Name))) return MarkSeen.BackingOff;
        return MarkSeen.Open;
    }

    /// <summary>An open (or tool-waiting) designation of a work type the pawn does.</summary>
    private static bool MarkOfItsWork(PawnState p, DesignationState d) =>
        d.Status is null or "open" or "waiting" && WorkOf(d.Kind) is { Length: > 0 } wt && p.Priorities.GetValueOrDefault(wt) is >= 1 and <= 4;

    // ---------------------------------------------------------------- why a search passed over open marks

    /// <summary>
    /// Why a work search passed over a designation that is free and not resting: what the audit's one pass over the designations
    /// can't see (live 11-04 story-1: "no reachable target" 150-340 pawn-seconds a minute, all evening, with nothing to say which marks
    /// or why). Counted per search without allocating; the most common one becomes the look's <see cref="Look.NoReach"/>, with the
    /// first such mark's kind and cell.
    /// </summary>
    internal enum MarkPass { Buried, Underfoot, Cave, StuckSpot, Forbidden, LeftToSurface, OutOfSight }

    private static readonly string[] MarkPassText =
    [
        "buried (dug once a cell over or beside it is gone)", "under someone's feet", "in a cave (surface work)",
        "by a spot where a pawn got stuck", "forbidden", "left to a pawn up top (this one is under ground)", "out of sight",
    ];

    private readonly int[] _passed = new int[MarkPassText.Length];
    private readonly (string Kind, P3? At)[] _passedFirst = new (string, P3?)[MarkPassText.Length];
    private readonly Dictionary<(MarkPass, string, P3?), string> _noReachText = new();

    /// <summary>A search passed over an open mark of <paramref name="kind"/> (at <paramref name="at"/>, when it has a cell).</summary>
    private void PassedMark(MarkPass why, string kind, P3? at)
    {
        int i = (int)why;
        if (_passed[i]++ == 0) _passedFirst[i] = (kind, at);
    }

    /// <summary>"no reachable target: mine 4,60,2: buried (…)": the search's most common pass (null = it passed over none).</summary>
    private string? NoReachNote()
    {
        int best = -1;
        for (int i = 0; i < _passed.Length; i++) if (_passed[i] > 0 && (best < 0 || _passed[i] > _passed[best])) best = i;
        if (best < 0) return null;
        var (kind, at) = _passedFirst[best];
        var key = ((MarkPass)best, kind, at);
        if (_noReachText.TryGetValue(key, out var text)) return text;
        if (_noReachText.Count > 256) _noReachText.Clear();
        return _noReachText[key] = $"no reachable target: {kind}{(at is { } c ? " " + c : "")}: {MarkPassText[best]}";
    }

    /// <summary>Entity marks of <paramref name="kind"/> (haul, hunt) whose entity isn't in <paramref name="inView"/> (null: none is) or is
    /// forbidden: the audit counts them open, the search never sees them.</summary>
    private void PassedEntityMarks(string kind, IReadOnlyDictionary<int, StoryEntity>? inView)
    {
        foreach (var d in Designations)
            if (d.Kind == kind && d.Entity is { } id && (inView == null || !inView.ContainsKey(id) || ForbiddenEntity(id)))
                PassedMark(ForbiddenEntity(id) ? MarkPass.Forbidden : MarkPass.OutOfSight, kind, d.At);
    }

    /// <summary>The work types the colony has something to do for now: designations, builds with cells to do, bills that are wanted.</summary>
    private HashSet<string> WorkKindsWithWork()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Designations)
            if (d.Status is null or "open" or "waiting" && WorkOf(d.Kind) is { Length: > 0 } t) set.Add(t); // waiting: for a tool
        if (Builds.Any(b => b.Status is "active" or "queued" && b.Ghosts.Any(g => g.Job && g.State is "todo"))) set.Add(WorkTypes.Build);
        if (World is { } w)
            foreach (var bench in Benches)
            {
                if (bench.LoadedBill != null) continue; // smelting: nothing to do there until it's done
                foreach (var bill in bench.Bills)
                    if (!bill.Paused && bill.SuspendedReason == null && StoryRecipes.Get(w.Data, bill.Recipe) is { } rec && Wanted(bill, rec))
                        set.Add(rec.WorkType); // a bill whose count is reached (or stock is there) is no work
            }
        return set;
    }

    /// <summary>
    /// The colony's blocked work for the browser: idle pawns grouped by why, most first (<c>blocked_work</c>: "4 pawns idle: build b3:
    /// need 24 block of iron"). Empty when no pawn is idle with work about.
    /// </summary>
    private JsonArray BlockedWorkJson()
    {
        var arr = new JsonArray();
        foreach (var g in _idleReason.GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(6))
            arr.Add(new JsonObject
            {
                ["reason"] = g.Key, ["count"] = g.Count(),
                ["pawns"] = new JsonArray(g.Select(kv => (JsonNode)kv.Key).ToArray()),
            });
        return arr;
    }

    // ---------------------------------------------------------------- the wanderers' wake check, cheap

    /// <summary>Everything a work search reads that changes when work appears: designations and zones, builds, benches and their bills,
    /// storages, materials, leases (an integer pass; no allocation).</summary>
    private long WorkStamp()
    {
        long s = IndexStamp();
        foreach (var b in Builds) s = s * 31 + b.Rev;
        foreach (var b in Benches) s = s * 31 + b.Rev;
        foreach (var st in Storages) s = s * 31 + st.Rev;
        return (s * 31 + _workEvents) * 31 + _leases.Count;
    }

    private long _workEvents;

    /// <summary>Full work searches so far (picks, the wake checks): tests hold the tick to a few.</summary>
    internal long Searches;
    private readonly Dictionary<string, (DateTimeOffset At, long Stamp)> _probed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A wanderer's wake check now? Only when something changed since its last empty search (or a while passed: a back-off
    /// ran out), and at most <paramref name="budget"/> searches this tick (live 11-04: each idle pawn searched every 2 s).</summary>
    private bool ProbeDue(PawnState p, long stamp, ref int budget)
    {
        if (_probed.TryGetValue(p.Name, out var last) && (Now - last.At < TimeSpan.FromSeconds(3) || Now - last.At < TimeSpan.FromSeconds(10) && last.Stamp == stamp))
            return false;
        if (budget <= 0) return false;
        budget--;
        return true;
    }

    /// <summary>A work search from the tick (not a pick): the per-search caches set up as a pick does (tool plans are per pawn).</summary>
    private ActiveJob? ProbeWork(PawnState p, PawnLive live, long stamp)
    {
        _toolPlans.Clear();
        _pickPawn = p.Name; _pickClaims = null;
        try
        {
            var job = Work(p, live);
            if (job == null) _probed[p.Name] = (Now, stamp);
            else _probed.Remove(p.Name);
            return job;
        }
        finally { _pickPawn = null; }
    }
}
