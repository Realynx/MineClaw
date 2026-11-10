namespace MineClaw.Hive.Story;

/// <summary>
/// Trees on a quarry (docs/web/story-mode.md "Quarries"). A quarry's top is its highest ground and logs are no ground, so a trunk
/// standing on the zone was never a cell of it: the ground under it was dug and the tree was left floating over the pit, its leaves
/// never decaying while the logs stood. Now a tree whose trunk stands in the zone's columns and reaches over the top is felled first,
/// by a chop job of Mine work, and the quarry digs nothing while one stands (one resting after failures doesn't hold it: it is said).
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>
    /// The trunks standing on the quarry, found once per look: a log rooted on a block in one of the zone's columns, from 8 under the
    /// top to the top + 1 (a trunk on the top's ground), whose tree has a log over the top. Logs under the top are cells of the layers.
    /// </summary>
    private static List<P3> QuarryTrees(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        if (q.Trees != null) return q.Trees;
        var trees = new List<P3>();
        if (q.Top > 0)
            foreach (var (x, zz) in z.Cells.Sorted())
            {
                if (!w.Loaded(x, zz)) continue;
                for (int y = Math.Max(1, q.Top - 8); y <= Math.Min(w.Height - 1, q.Top + 1); y++)
                {
                    var at = new P3(x, y, zz);
                    if (!StoryBlocks.Logs.Contains(w.Block(x, y, zz).Id) || !StoryBlocks.Rooted(w, at)) continue;
                    if (y > q.Top || StoryBlocks.TreeFrom(w, at).Any(l => l.Y > q.Top)) trees.Add(at);
                }
            }
        return q.Trees = trees;
    }

    /// <summary>Trees on the quarry not resting after failures: they hold its digging.</summary>
    private int TreesStanding(IStoryWorld w, ZoneState z, QuarryScan q) => QuarryTrees(w, z, q).Count(t => !Skipped("b:" + t));

    /// <summary>The nearest tree on the quarry this pawn can fell now, as a chop job of Mine work, or null. It walks there on the surface:
    /// <c>quarry_access_y</c> is the top + 1, or its travel would send it down the stair first (StoryColony.QuarryTravel.cs).</summary>
    private ActiveJob? QuarryTreeJob(PawnState p, PawnLive live, IStoryWorld w, ZoneState z, QuarryScan q)
    {
        var trees = QuarryTrees(w, z, q);
        if (trees.Count == 0) return null;
        var from = live.Block;
        foreach (var t in trees.OrderBy(t => t.Dist(from)))
        {
            if (!Open("b:" + t, p.Name, t)) continue;
            var logs = StoryBlocks.TreeFrom(w, t);
            var tool = PlanTool(p, live, ChopBlockAt(w, t, logs));
            if (tool is { Wait: not null }) continue;
            return WithTool(tool, Job(WorkTypes.Mine, "chop", $"felling a tree on quarry {ZoneName(z)}", t, "b:" + t, "zone", z.Id, "working", f =>
            {
                ChopFill(f, logs);
                f["quarry_access_y"] = q.Top + 1;
            }, xp: 40));
        }
        return null;
    }

    /// <summary>"felling 2 tree(s) on it first" while trees hold the quarry, else null.</summary>
    private string? FellingWait(ZoneState z, QuarryScan q) =>
        World is { } w && TreesStanding(w, z, q) is > 0 and var n ? $"felling {n} tree(s) on it first" : null;
}
