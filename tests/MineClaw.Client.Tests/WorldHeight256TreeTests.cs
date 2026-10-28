using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Tree analysis on a 1.12.2 (256-high) world: trunks and crowns above y 127 (multi-version P3).</summary>
public class WorldHeight256TreeTests
{
    private static WorldState Tree(int soilY, int trunk)
    {
        var w = new WorldState(height: 256);
        w.ApplyChunkRegion(-16, 0, -16, 32, 256, 32, new byte[32 * 256 * 32], null, null, null);
        w.SetBlock(0, soilY, 0, Blocks.Dirt, 0);
        int top = soilY + trunk;
        for (int y = soilY + 1; y <= top; y++) w.SetBlock(0, y, 0, Blocks.Log, 0);
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        for (int y = top - 1; y <= top + 1; y++)
            if (w.GetBlock(dx, y, dz) == 0) w.SetBlock(dx, y, dz, Blocks.Leaves, 0);
        return w;
    }

    [Fact]
    public void A_tree_crossing_y_128_is_analysed_whole()
    {
        var w = Tree(124, 6); // logs 125..130
        var t = TreeScan.Analyze(w, new BlockPos(0, 125, 0))!;
        Assert.Equal(6, t.Logs.Count);
        Assert.Equal(130, t.Top);
        Assert.Contains(new BlockPos(1, 131, 0), t.Leaves);
        Assert.True(t.OnSoil);
    }

    [Fact]
    public void A_tree_at_y_200_is_found_and_its_leaves_hold_drops()
    {
        var w = Tree(199, 4); // logs 200..203
        Assert.Equal(new BlockPos(0, 200, 0), TreeScan.LogNear(w, new BlockPos(1, 199, 1)));
        var t = Assert.Single(TreeScan.FindTrees(w, new Vec3d(0.5, 200, 0.5), 8));
        Assert.Equal(new BlockPos(0, 200, 0), t.Base);
        Assert.Equal(4, t.Logs.Count);
        Assert.Equal(new BlockPos(1, 204, 0), TreeScan.SupportingLeaf(w, new Vec3d(1.5, 205.125, 0.5)));
        Assert.Contains(new BlockPos(1, 204, 0), TreeScan.SupportedLeaves(w, t.Leaves.ToList()));
    }
}
