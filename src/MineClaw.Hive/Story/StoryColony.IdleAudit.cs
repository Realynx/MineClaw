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

    /// <summary>What a pawn's last work search saw: when, whether it found a job, and what held work of its kinds back.</summary>
    private sealed record Look(DateTimeOffset At, bool Found, List<string> Blocked);

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
        if (p.Drafted || _leaving.ContainsKey(p.Name)) return null;
        // Working, or between two jobs (it asks for the next one at once): not idle.
        if (!_current.TryGetValue(p.Name, out var cur) || cur.Source is not ("none" or "wander" or "escape")) return null;
        int hour = HourNow;
        var block = p.Schedule.Length == 24 && ScheduleBlocks.Known(p.Schedule[hour]) ? p.Schedule[hour] : ScheduleBlocks.Anything;
        if (block is not (ScheduleBlocks.Work or ScheduleBlocks.Anything)) return null;
        if (!Host.Live(p.Name).Online) return null;
        if (_stuck.ContainsKey(p.Name)) return "stuck";
        var look = _looks.GetValueOrDefault(p.Name);
        if (look != null && Now - look.At > LookFresh) look = null;
        if (look is { Found: true }) return "work not taken yet";
        // Its designations: why none of them is for it.
        bool leased = false, waiting = false, backingOff = false, open = false;
        foreach (var d in Designations)
        {
            if (d.Status is not (null or "open" or "waiting") || WorkOf(d.Kind) is not { Length: > 0 } wt || p.Priorities.GetValueOrDefault(wt) is < 1 or > 4) continue;
            var key = DesignationKey(d);
            if (d.Wait != null) { waiting = true; continue; }
            if (!Free(key, p.Name)) { leased = true; continue; }
            if (Skipped(key) || Skipped(PawnKey(key, p.Name))) { backingOff = true; continue; }
            open = true;
        }
        if (open) return RestLeft(p) != null ? "back-off rest" : "no reachable target";
        if (backingOff) return "targets backing off";
        if (waiting) return "tool waiting";
        if (leased) return "all targets leased";
        // What its last search found held back: a build short of something, a suspended bill, a busy bench.
        if (look is { Blocked: [var first, ..] }) return "waiting: " + first;
        kinds ??= WorkKindsWithWork();
        foreach (var t in kinds) if (p.Priorities.GetValueOrDefault(t) is >= 1 and <= 4) return null; // its own kinds: nothing it could start (searched)
        return kinds.Count > 0 ? "priority 0" : null;
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
        if (_probed.TryGetValue(p.Name, out var last) && (Now - last.At < TimeSpan.FromSeconds(2) || Now - last.At < TimeSpan.FromSeconds(10) && last.Stamp == stamp))
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
