namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private const string BuildConflictWords = "superseded by build ";

    private static bool BuildConflicted(GhostState g) =>
        g.State == "blocked" && g.Reason?.StartsWith(BuildConflictWords, StringComparison.Ordinal) == true;

    /// <summary>A newer placement design owns an overlapping cell, even when paused or finished.
    /// Queue priority never changes that ownership. Removing the newer design releases the older one.</summary>
    private void BuildConflictsPass()
    {
        var owners = new Dictionary<P3, (BuildState Build, GhostState Ghost)>();
        foreach (var b in Builds.Where(b => b.Kind != "preserve").OrderBy(b => b.Created))
            foreach (var g in b.Ghosts.Where(g => !g.Remove)) owners[g.At] = (b, g);
        var held = new List<(BuildState Build, GhostState Ghost, string Why)>();
        foreach (var b in Builds.Where(b => b.Kind != "preserve"))
            foreach (var g in b.Ghosts.Where(g => !g.Remove))
            {
                var owner = owners[g.At];
                if (owner.Build != b && (g.Id != owner.Ghost.Id || g.Meta != owner.Ghost.Meta))
                    held.Add((b, g, BuildConflictWords + owner.Build.Id + ": newer design owns this cell"));
                else if (BuildConflicted(g))
                {
                    ForgetCellFailures(b, g);
                    g.Rev = Touch();
                    if (b.Status == "blocked") { b.Status = "queued"; b.Reason = null; }
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
