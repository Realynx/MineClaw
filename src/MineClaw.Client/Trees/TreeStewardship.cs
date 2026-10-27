using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Trees;

/// <summary>The living trees and planted saplings (future trees) around a spot: on Skyblock the only renewable wood.</summary>
public sealed record TreeCensus(IReadOnlyList<TreeInfo> Trees, IReadOnlyList<BlockPos> Saplings)
{
    /// <summary>Trees plus saplings planted on dirt/grass (each one grows into a tree).</summary>
    public int Future => Trees.Count + Saplings.Count;

    public override string ToString() => $"{Trees.Count} tree(s), {Saplings.Count} planted sapling(s)";
}

/// <summary>
/// Hard tree-stewardship rule (pure, unit-testable): never fell a tree when the island would be left with fewer than
/// <c>MinTrees</c> trees (planted saplings count as future trees), unless the bot holds a sapling and can replant that
/// very spot. Applies to chop_tree (and so to every script/worker path that calls it); plain digs/mines of a living
/// tree's logs never replant, so for them the rule holds even with a sapling in hand.
/// </summary>
public static class TreeStewardship
{
    public const int DefaultMinTrees = 2;
    /// <summary>Census radius around the tree: a Skyblock island (and its neighbours) fits comfortably.</summary>
    public const double CensusRadius = 32;

    /// <summary>A real, growing tree: logs rising from dirt/grass with a crown (not a log build, not a hanging remnant).</summary>
    public static bool IsLiving(TreeInfo t) => t.OnSoil && t.Leaves.Count >= 3;

    public static TreeCensus Census(WorldState w, Vec3d center, double radius = CensusRadius)
    {
        var trees = TreeScan.FindTrees(w, center, radius).Where(IsLiving).ToList();
        var saplings = w.FindBlocks(center, radius, (id, _) => id == Blocks.Sapling, 512)
            .Select(f => f.Pos)
            .Where(p => w.GetBlock(p.X, p.Y - 1, p.Z) is Blocks.Dirt or Blocks.Grass)
            .ToList();
        return new TreeCensus(trees, saplings);
    }

    /// <summary>Trees + planted saplings left after <paramref name="target"/> is felled and not replanted.</summary>
    public static int LeftAfter(TreeCensus census, TreeInfo target) =>
        census.Trees.Count(t => t.Base != target.Base) + census.Saplings.Count(s => s != target.Base);

    /// <summary>
    /// Null when felling <paramref name="target"/> is allowed, else the refusal. <paramref name="replantProblem"/> is why
    /// the bot couldn't replant that spot even with a sapling (null = it can); <paramref name="saplingsHeld"/> = saplings in
    /// the inventory.
    /// </summary>
    public static string? Refusal(TreeCensus census, TreeInfo target, int saplingsHeld, string? replantProblem, int minTrees)
    {
        if (minTrees <= 0 || !IsLiving(target)) return null;   // a remnant/build isn't one of the island's trees
        if (saplingsHeld > 0 && replantProblem is null) return null; // the replant is certain: the tree count holds
        if (LeftAfter(census, target) >= minTrees) return null;
        return saplingsHeld <= 0
            ? $"that's one of the last {minTrees} trees and I have no sapling to replant"
            : $"that's one of the last {minTrees} trees and I can't replant it ({replantProblem})";
    }

    /// <summary>
    /// Logs of every living tree that a non-replanting dig/mine must leave alone: all of them while felling any one would
    /// drop the island below <paramref name="minTrees"/>, else none.
    /// </summary>
    public static HashSet<BlockPos> GuardedLogs(TreeCensus census, int minTrees)
    {
        var set = new HashSet<BlockPos>();
        if (minTrees <= 0 || census.Trees.Count == 0 || census.Future - 1 >= minTrees) return set;
        foreach (var t in census.Trees) set.UnionWith(t.Logs);
        return set;
    }
}
