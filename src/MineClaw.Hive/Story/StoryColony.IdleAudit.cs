namespace MineClaw.Hive.Story;

/// <summary>
/// The idle audit (docs/web/story-mode.md "Idle with work"). Fox 11-04: "We want most pawns doing things, and never idle on a work
/// schedule if there are things to be done." Every tick, a pawn in its work hours that holds, wanders or rests while the colony has
/// work of a kind it does gets a reason: <c>stuck</c>, <c>back-off rest</c>, <c>work not taken yet</c> (an idle leg the next wake hands
/// over), <c>all targets leased</c>, <c>tool waiting</c>, <c>targets backing off</c>, <c>no reachable target</c>, <c>build or bill
/// waiting</c>, or <c>priority 0</c> (there is work, of kinds it doesn't do). The pawn's row carries <c>idle_reason</c>; the
/// supervisor's per-minute perf line counts them (<c>idle-with-work N: …</c>).
/// </summary>
public sealed partial class StoryColony
{
    private readonly Dictionary<string, string> _idleReason = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _idleAudit = DateTimeOffset.MinValue;

    /// <summary>Why the pawn is idle with work about (null = it works, or there is none for it). The browser's <c>idle_reason</c>.</summary>
    public string? IdleReasonOf(string pawn) { lock (_lock) return _idleReason.GetValueOrDefault(pawn); }

    /// <summary>Every tick (at most once a second): each pawn's idle reason, counted for the perf line.</summary>
    private void AuditIdle()
    {
        if (Now - _idleAudit < TimeSpan.FromSeconds(1)) return;
        _idleAudit = Now;
        foreach (var p in Pawns.Values)
        {
            string? why = null;
            try { why = IdleWithWork(p); } catch (Exception) { /* the audit never breaks the tick */ }
            if (why == null) { if (_idleReason.Remove(p.Name)) p.Rev = Bump(); continue; }
            if (!_idleReason.TryGetValue(p.Name, out var old) || old != why) { _idleReason[p.Name] = why; p.Rev = Bump(); }
            StoryPerf.NoteIdle(why);
        }
    }

    private string? IdleWithWork(PawnState p)
    {
        var live = Host.Live(p.Name);
        if (!live.Online || p.Drafted || _leaving.ContainsKey(p.Name)) return null;
        int hour = HourNow;
        var block = p.Schedule.Length == 24 && ScheduleBlocks.Known(p.Schedule[hour]) ? p.Schedule[hour] : ScheduleBlocks.Anything;
        if (block is not (ScheduleBlocks.Work or ScheduleBlocks.Anything)) return null;
        if (_current.TryGetValue(p.Name, out var cur) && cur.Source is not ("none" or "wander" or "escape")) return null; // working
        if (_stuck.ContainsKey(p.Name)) return "stuck";
        var mine = WorkTypes.All.Where(t => p.Priorities.GetValueOrDefault(t) is >= 1 and <= 4).ToHashSet();
        bool any = false, others = false;
        foreach (var t in WorkKindsWithWork())
        {
            if (mine.Contains(t)) any = true;
            else others = true;
        }
        if (!any) return others ? "priority 0" : null;
        if (RestLeft(p) != null) return "back-off rest";
        if (Work(p, live) != null) return "work not taken yet";
        // Why no target of its kinds is for it.
        bool leased = false, waiting = false, backingOff = false, open = false;
        foreach (var d in Designations)
        {
            if (WorkOf(d.Kind) is not { Length: > 0 } wt || !mine.Contains(wt)) continue;
            var key = DesignationKey(d);
            if (d.Wait != null) { waiting = true; continue; }
            if (!Free(key, p.Name)) { leased = true; continue; }
            if (Skipped(key) || Skipped(PawnKey(key, p.Name))) { backingOff = true; continue; }
            open = true;
        }
        if (open) return "no reachable target";
        if (backingOff) return "targets backing off";
        if (waiting) return "tool waiting";
        if (leased) return "all targets leased";
        return "build or bill waiting";
    }

    /// <summary>The work types the colony has something open for now: designations, builds with cells to do, bills that run.</summary>
    private HashSet<string> WorkKindsWithWork()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Designations)
            if (d.Status is null or "open" && WorkOf(d.Kind) is { Length: > 0 } t) set.Add(t);
        if (Builds.Any(b => b.Status is "active" or "queued" && b.Ghosts.Any(g => g.Job && g.State is "todo"))) set.Add(WorkTypes.Build);
        foreach (var bench in Benches)
            foreach (var bill in bench.Bills)
                if (!bill.Paused && bill.SuspendedReason == null && StoryRecipes.For(Data!, bench.Kind).FirstOrDefault(r => r.Id == bill.Recipe) is { } rec)
                    set.Add(rec.WorkType);
        return set;
    }
}
