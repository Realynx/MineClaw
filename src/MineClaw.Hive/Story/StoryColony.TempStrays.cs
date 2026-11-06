namespace MineClaw.Hive.Story;

/// <summary>
/// The one-time look for strays (docs/web/story-mode.md "Temporary blocks"): blocks the colony's own pawns placed near home before the
/// registry existed (live 11-04/11-05: shelter boxes of oak planks, 2-high plank tree pillars, walks' cobblestone and dirt pillars) that
/// still stand. Only what the hive's placed-block log says a pawn of this colony placed counts (never a player's block, never main's),
/// only scaffold kinds (dirt, cobblestone, planks, stone kinds, logs, sand, gravel), never a build's cell or a quarry's own blocks, and
/// only small clusters (a box, a stub, a lone block) or a single column (a pillar): a wall or a floor is somebody's build.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>Blocks pawns build scaffolds with: dirt (grass: grown over), cobblestone, planks, stone, netherrack, sandstone, stone
    /// bricks, logs, gravel, sand.</summary>
    internal static readonly HashSet<int> TempKinds = [3, 2, 4, 5, 1, 87, 24, 98, 17, 162, 13, 12];
    /// <summary>How far from home strays are looked for (horizontally): the colony's woods and quarries too (live: 2-high plank stubs from
    /// the 11-03 tree runs stood 130-190 blocks from home).</summary>
    public const int StrayRange = 200;
    /// <summary>A cluster bigger than this (that isn't one column) is somebody's build, never a stray.</summary>
    public const int StrayClusterMax = 12;
    /// <summary>At most this many strays are queued in one look.</summary>
    public const int StrayMax = 256;

    /// <summary>Once per load, with home in view: the pawns' stray blocks near home become deconstruct designations, said in one alert.</summary>
    private void SweepStrays(IStoryWorld w)
    {
        if (Home() is not { } home || !w.Loaded(home.X, home.Z)) return;
        _straysSwept = true;
        IReadOnlyList<(P3 At, int Id, string Bot)> placed;
        try { placed = Host.PlacedByBots(); } catch (Exception) { return; }
        var cand = StrayCandidates(w, home, placed);
        if (cand.Count == 0) return;
        var builds = BuildCellSet();
        var found = Clusters(cand.Keys).Where(g => LooksTemporary(g, builds)).SelectMany(g => g).Take(StrayMax).Select(at => cand[at]).ToList();
        if (found.Count == 0) return;
        foreach (var t in found) { Temps.Add(t); QueueTemp(t, said: true); }
        var kinds = found.GroupBy(t => BlockLabel(t.Id)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
        Alert("info", $"Found {found.Count} stray block(s) the pawns left near home ({string.Join(", ", kinds)}; at "
            + $"{string.Join(", ", found.Select(t => t.At.ToString()).Take(5))}{(found.Count > 5 ? ", …" : "")}): queued for removal");
    }

    /// <summary>The colony's pawns' standing scaffold-kind blocks near home that nothing else claims (not registered, not designated, not
    /// in a quarry).</summary>
    private Dictionary<P3, TempBlockState> StrayCandidates(IStoryWorld w, P3 home, IReadOnlyList<(P3 At, int Id, string Bot)> placed)
    {
        var known = (Meta.TempBlocks ?? []).Select(t => t.At).Concat(Designations.Where(d => d.At != null).Select(d => d.At!.Value)).ToHashSet();
        var cand = new Dictionary<P3, TempBlockState>();
        foreach (var (at, id, bot) in placed)
        {
            if (!Pawns.ContainsKey(bot) || !TempKinds.Contains(id) || known.Contains(at) || !NearHome(home, at) || InQuarry(at)) continue;
            if (!w.Loaded(at.X, at.Z) || !SameTempBlock(id, w.Block(at.X, at.Y, at.Z).Id)) continue;
            cand[at] = new TempBlockState { At = at, Id = id, Pawn = bot, Kind = "stray", Since = Now };
        }
        return cand;
    }

    private static bool NearHome(P3 home, P3 at) => Math.Abs(at.X - home.X) <= StrayRange && Math.Abs(at.Z - home.Z) <= StrayRange;

    /// <summary>In a quarry's columns or right beside them (its stair steps and hazard seals are the quarry's own).</summary>
    private bool InQuarry(P3 at) => Zones.Any(z => IsQuarry(z) && (z.Contains(at.X, at.Z) || z.Contains(at.X + 1, at.Z) || z.Contains(at.X - 1, at.Z)
        || z.Contains(at.X, at.Z + 1) || z.Contains(at.X, at.Z - 1)));

    /// <summary>A small cluster or one column, and none of it a build's cell.</summary>
    private static bool LooksTemporary(List<P3> group, HashSet<P3> buildCells)
    {
        if (group.Any(buildCells.Contains)) return false;
        if (group.Count <= StrayClusterMax) return true;
        return group.All(c => c.X == group[0].X && c.Z == group[0].Z) && group.Count <= Scripting.Story.ReachScan.MaxPillar + 2;
    }

    /// <summary>The cells grouped by touch (faces, edges or corners).</summary>
    internal static List<List<P3>> Clusters(IEnumerable<P3> cells)
    {
        var left = cells.ToHashSet();
        var groups = new List<List<P3>>();
        while (left.Count > 0)
        {
            var start = left.First();
            left.Remove(start);
            var group = new List<P3> { start };
            for (int i = 0; i < group.Count; i++)
                foreach (var n in Around(group[i]))
                    if (left.Remove(n)) group.Add(n);
            groups.Add(group);
        }
        return groups;
    }

    private static IEnumerable<P3> Around(P3 c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (dx != 0 || dy != 0 || dz != 0) yield return c.Add(dx, dy, dz);
    }
}
