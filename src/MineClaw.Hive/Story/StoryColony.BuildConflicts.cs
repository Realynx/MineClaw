namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private const string BuildConflictWords = "superseded by build ";

    private static bool BuildConflicted(GhostState g) =>
        g.State == "blocked" && g.Reason?.StartsWith(BuildConflictWords, StringComparison.Ordinal) == true;

    private static bool SameBuildIntent(GhostState a, GhostState b) =>
        a.Remove || b.Remove ? a.Remove == b.Remove : a.Id == b.Id && a.Meta == b.Meta;

    /// <summary>
    /// Nothing is left of the build but cells a newer build took over: it counts as done (the owner's later order replaced them; they
    /// are no failure and nothing can be done about them). True = that is the case (the build is marked done here unless it is paused).
    /// Any other unfinished cell (open, blocked for another reason, in a quarry) keeps the build as it is. Removing the newer build
    /// gives the cells back, and <see cref="BuildConflictsPass"/> queues the build again.
    /// </summary>
    private bool SupersededOnly(BuildState b)
    {
        bool any = false;
        foreach (var g in b.Ghosts)
        {
            if (!g.Job || g.State is "done" or "misrotated") continue;
            if (!BuildConflicted(g)) return false;
            any = true;
        }
        if (!any) return false;
        if (b.Status is not ("done" or "paused")) { b.Status = "done"; b.Reason = null; b.Rev = Touch(); }
        return true;
    }

    /// <summary>A newer placement design owns an overlapping cell, even when paused or finished.
    /// Queue priority never changes that ownership. Removing the newer design releases the older one.</summary>
    /// <summary>Who owns each placed cell, made again every pass (kept between passes, so its room is not allocated again).</summary>
    private readonly Dictionary<P3, (BuildState Build, GhostState Ghost)> _cellOwners = new();

    private void BuildConflictsPass()
    {
        var owners = _cellOwners;
        owners.Clear();
        // A build owns its placed cells and, a replacement design, its empty space too; the spec read once a build, not per cell (a
        // strip mine's 47,000 digs own no cell: passed over at once).
        foreach (var b in Builds.Where(b => b.Kind != "preserve").OrderBy(b => b.Created))
        {
            bool replacement = ReplacementBuild(b);
            foreach (var g in b.Ghosts) if (!g.Remove || replacement) owners[g.At] = (b, g);
        }
        var held = new List<(BuildState Build, GhostState Ghost, string Why)>();
        foreach (var b in Builds.Where(b => b.Kind != "preserve"))
        {
            bool replacement = ReplacementBuild(b);
            foreach (var g in b.Ghosts)
            {
                if (g.Remove && !replacement) continue;
                var owner = owners[g.At];
                if (owner.Build != b && !SameBuildIntent(g, owner.Ghost))
                    held.Add((b, g, BuildConflictWords + owner.Build.Id + ": newer design owns this cell"));
                else if (BuildConflicted(g))
                {
                    ForgetCellFailures(b, g);
                    g.Rev = Touch();
                    // Done too: a build that counted as done with only superseded cells left has work again (SupersededOnly).
                    if (b.Status is "blocked" or "done") { b.Status = "queued"; b.Reason = null; }
                    b.Rev = g.Rev;
                }
            }
        }
        owners.Clear(); // no build or cell kept alive until the next pass
        var ids = held.Select(h => $"{h.Build.Id}|{h.Ghost.At}").ToHashSet();
        foreach (var (pawn, job) in _current.ToList())
            if (job.Source == "ghost" && JobCells(job).Any(ids.Contains) && Pawns.TryGetValue(pawn, out var p))
                Supersede(p, "newer build owns this cell");
        foreach (var (b, g, why) in held)
        {
            if (BuildConflicted(g) && g.Reason == why) continue;
            ForgetCellFailures(b, g);
            g.State = "blocked";
            g.Reason = why;
            g.Holds = g.HoldsWords = null;
            g.Rev = b.Rev = Touch();
            MaterialsChanged();
        }
    }
}
