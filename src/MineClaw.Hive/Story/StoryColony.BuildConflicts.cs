namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private const string BuildConflictWords = "superseded by build ";

    private static bool BuildConflicted(GhostState g) =>
        g.State == "blocked" && g.Reason?.StartsWith(BuildConflictWords, StringComparison.Ordinal) == true;

    private static bool OwnsBuildCell(BuildState b, GhostState g) => !g.Remove || ReplacementAir(b, g);

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
    private void BuildConflictsPass()
    {
        var owners = new Dictionary<P3, (BuildState Build, GhostState Ghost)>();
        foreach (var b in Builds.Where(b => b.Kind != "preserve").OrderBy(b => b.Created))
            foreach (var g in b.Ghosts.Where(g => OwnsBuildCell(b, g))) owners[g.At] = (b, g);
        var held = new List<(BuildState Build, GhostState Ghost, string Why)>();
        foreach (var b in Builds.Where(b => b.Kind != "preserve"))
            foreach (var g in b.Ghosts.Where(g => OwnsBuildCell(b, g)))
            {
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
