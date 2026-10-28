using MineClaw.World;

namespace MineClaw.World.Tests;

/// <summary>Water poured at an island edge (mc-dev BlockFlowing/ItemBucket) and riding it down and up (EntityLiving water physics).</summary>
public class WaterColumnTests
{
    /// <summary>A 3×3 stone island at y 63 over the void, one block thick; the column hangs off its east edge at x 2.</summary>
    private static WorldState Island(int depth = 1)
    {
        var w = TestWorld.Create();
        w.Fill(-1, 64 - depth, -1, 1, 63, 1, TestBlocks.Stone);
        return w;
    }

    private static void Pour(WorldState w, BlockPos source)
    {
        foreach (var (p, m) in WaterColumn.Cells(w, source)) w.Set(p.X, p.Y, p.Z, m == 0 ? TestBlocks.StillWater : TestBlocks.Water, m);
    }

    [Fact]
    public void A_source_over_the_void_only_falls_and_never_spreads_onto_the_island()
    {
        var w = Island();
        var src = new BlockPos(2, 63, 0);
        var cells = WaterColumn.Cells(w, src);
        Assert.All(cells, c => Assert.True(c.Pos.X == 2 && c.Pos.Z == 0, $"{c.Pos} spread"));
        Assert.Equal(0, cells.Single(c => c.Pos == src).Meta);
        Assert.All(cells.Where(c => c.Pos != src), c => Assert.Equal(8, c.Meta)); // falling
        Assert.Equal((0, false), WaterColumn.Extent(w, src)); // runs into the void
    }

    [Fact]
    public void A_column_landing_on_a_block_spreads_over_it()
    {
        var w = Island();
        w.Set(2, 57, 0, TestBlocks.Stone);
        var src = new BlockPos(2, 63, 0);
        Assert.Equal((58, true), WaterColumn.Extent(w, src));
        var cells = WaterColumn.Cells(w, src);
        Assert.Contains(cells, c => c.Pos == new BlockPos(2, 58, 0) && c.Meta == 8);
        Assert.Contains(cells, c => c.Pos == new BlockPos(3, 58, 0) && c.Meta == 1); // spread over the block, then falls off its sides
        Assert.Contains(cells, c => c.Pos == new BlockPos(3, 57, 0) && c.Meta == 8);
    }

    [Fact]
    public void Descends_into_a_void_column_hovers_at_depth_and_climbs_back_out()
    {
        var w = Island();
        var src = new BlockPos(2, 63, 0);
        Pour(w, src);
        var col = new SwimColumn(2, 63, 0);
        var stand = new BlockPos(1, 64, 0);
        var down = SwimSim.SimulateDescent(w, stand, col, 57);
        Assert.True(down.Ok, down.Why);
        Assert.True(down.MinFeetY > 57 - 1.0, $"sank to {down.MinFeetY}"); // never far past the depth
        var up = SwimSim.SimulateAscent(w, down.End, col, stand);
        Assert.True(up.Ok, up.Why);
        Assert.True(down.Ticks < 200 && up.Ticks < 600, $"down {down.Ticks} up {up.Ticks}");
    }
}
