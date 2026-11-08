namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    /// <summary>A partial tree keeps its log's tool policy after its original base is gone. Use observed logs, never air or an
    /// assumed species. Orders have no recorded log list, so use the tree macro's bounded nearest-log search.</summary>
    private static int ChopBlockAt(IStoryWorld w, P3 at, IReadOnlyList<P3>? logs = null)
    {
        int Log(P3 p) => p.Y >= 0 && p.Y < w.Height && w.Loaded(p.X, p.Z)
            && w.Block(p.X, p.Y, p.Z).Id is var id && StoryBlocks.Logs.Contains(id) ? id : 0;
        if (Log(at) is > 0 and var target) return target;
        foreach (var p in logs ?? []) if (Log(p) is > 0 and var remaining) return remaining;
        int nearest = 0;
        double distance = double.MaxValue;
        // TreeScan.LogNear: three cells sideways/down, six above, with vertical distance weighted by one half.
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 6; dy++)
                for (int dz = -3; dz <= 3; dz++)
                {
                    double d = dx * dx + dz * dz + .5 * dy * dy;
                    if (d >= distance || Log(at.Add(dx, dy, dz)) is not (> 0 and var id)) continue;
                    nearest = id;
                    distance = d;
                }
        return nearest;
    }
}
