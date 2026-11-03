using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>cast_portal: the frame made of lava and water buckets, no obsidian carried, no diamond pickaxe.</summary>
public class CastPortalTests
{
    private static readonly BlockPos Origin = new(-2, 64, -4);   // frame along x in the plane z = -4, plate at z = -3, me in front at z = -5

    /// <summary>The 10 frame cells in casting order.</summary>
    private static readonly BlockPos[] Cells =
        [new(-1, 64, -4), new(0, 64, -4), new(-2, 65, -4), new(-2, 66, -4), new(-2, 67, -4), new(1, 65, -4), new(1, 66, -4), new(1, 67, -4), new(-1, 68, -4), new(0, 68, -4)];

    /// <summary>A 17x17 island with a pool of lava (12 sources) in its south-east corner.</summary>
    private static FakeGame World(int dirt = 40, bool lava = true, int waterBuckets = 1, int emptyBuckets = 1)
    {
        var g = LiquidTests.Island(8);
        if (lava) for (int x = 3; x <= 8; x++) for (int z = 6; z <= 7; z++) g.Set(x, 63, z, Blocks.Lava);
        g.Give(Items.WaterBucket, waterBuckets);
        g.Give(Items.Bucket, emptyBuckets);
        g.Give(Blocks.Cobblestone, dirt);
        g.Stand(0.5, 64, 4.5);
        return g;
    }

    private static List<BlockPos> AllOf(FakeGame g, int id, BlockPos from, BlockPos to) =>
        BuildOpsBox(from, to).Where(p => g.World.GetBlock(p) == id).ToList();

    private static IEnumerable<BlockPos> BuildOpsBox(BlockPos a, BlockPos b)
    {
        for (int x = a.X; x <= b.X; x++) for (int y = a.Y; y <= b.Y; y++) for (int z = a.Z; z <= b.Z; z++) yield return new BlockPos(x, y, z);
    }

    [Fact]
    public async Task Casts_the_ten_frame_cells_from_lava_and_water_and_leaves_nothing_else_behind()
    {
        var g = World();
        var r = await g.Run("cast_portal", new { pos = "-2,64,-4" });
        Assert.True(r.Ok, r.Text);
        var all = BuildOpsBox(new BlockPos(-8, 64, -8), new BlockPos(8, 72, 8)).ToList();
        Assert.Equal(Cells.OrderBy(c => c.Y).ThenBy(c => c.X).ToList(), all.Where(p => g.World.GetBlock(p) == Blocks.Obsidian).OrderBy(c => c.Y).ThenBy(c => c.X).ToList());
        Assert.Equal(Cells, g.Mixed);                                                   // cast bottom row, left column, right column, top row
        Assert.All(all, p => Assert.False(BlockShapes.IsLiquid(g.World.GetBlock(p)) && p.Z < 5, $"liquid left at {p}"));   // no water or lava loose at the frame
        Assert.True(PortalFramesProbe.IsFrame(g.World, new BlockPos(-1, 65, -4)), "the frame is a valid portal frame");
        Assert.Empty(AllOf(g, Blocks.Cobblestone, new BlockPos(-2, 64, -3), new BlockPos(1, 69, -3)));                          // the back plate is dug out again
        Assert.All(BuildOpsBox(new BlockPos(-1, 65, -4), new BlockPos(0, 67, -4)), p => Assert.Equal(0, g.World.GetBlock(p)));  // the inside is clear
        Assert.Equal(1, g.World.Inventory.CountItem(Items.WaterBucket));
        Assert.Equal(0, g.World.Inventory.CountItem(Items.LavaBucket));
        Assert.Equal(1, g.World.Inventory.CountItem(Items.Bucket));
        Assert.Equal(12 - 10, g.World.FindBlocks(new Vec3d(5, 63, 6), 8, (id, _) => BlockShapes.IsLava(id), 64).Count);   // exactly one source per cell was taken
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Refuses_without_the_buckets_the_lava_or_the_blocks_and_places_nothing_first()
    {
        var noWater = World(waterBuckets: 0);
        Assert.Contains("needs", (await noWater.Run("cast_portal")).Text);

        var noLava = World(lava: false);
        var r = await noLava.Run("cast_portal", new { pos = "-2,64,-4" });
        Assert.False(r.Ok);
        Assert.Contains("lava", r.Text);
        Assert.Equal(40, noLava.World.Inventory.CountItem(Blocks.Cobblestone));                // no plate built before the lava was found
        Assert.Equal(0, noLava.World.GetBlock(-2, 64, -3));

        var noDirt = World(dirt: 5);
        var d = await noDirt.Run("cast_portal", new { pos = "-2,64,-4" });
        Assert.False(d.Ok);
        Assert.Contains("back plate", d.Text);
        Assert.Equal(1, noDirt.World.Inventory.CountItem(Items.Bucket));               // and no lava bucket filled either
    }

    [Fact]
    public async Task Refuses_a_site_with_something_in_the_way_wood_near_or_no_floor()
    {
        var g = World();
        g.Set(0, 66, -4, Blocks.Stone);                                                  // inside the frame
        var r = await g.Run("cast_portal", new { pos = "-2,64,-4" });
        Assert.False(r.Ok);
        Assert.Contains("in the way", r.Text);

        var wood = World();
        wood.Set(3, 66, -4, Blocks.Planks);                                              // burns
        Assert.Contains("would catch fire", (await wood.Run("cast_portal", new { pos = "-2,64,-4" })).Text);

        var hole = World();
        hole.Set(-2, 63, -3, 0);                                                         // no floor under the plate
        Assert.Contains("no solid floor", (await hole.Run("cast_portal", new { pos = "-2,64,-4" })).Text);
    }

    [Fact]
    public async Task Finds_its_own_spot_when_given_none_and_works_along_z_too()
    {
        var g = World();
        var r = await g.Run("cast_portal", new { axis = "z" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("z axis", r.Text);
        Assert.Equal(10, BuildOpsBox(new BlockPos(-8, 64, -8), new BlockPos(8, 72, 8)).Count(p => g.World.GetBlock(p) == Blocks.Obsidian));
    }

    [Fact]
    public async Task When_the_water_cannot_go_in_the_lava_is_taken_back_and_the_cast_stops_honestly()
    {
        var g = World();
        // As soon as the 3rd cell's lava is placed something lands in the cell above it, where the water goes.
        g.Changed = (p, id, _) => { if (BlockShapes.IsLava(id) && p == Cells[2]) g.World.SetBlock(p.X, p.Y + 1, p.Z, Blocks.Cobblestone, 0); };
        var r = await g.Run("cast_portal", new { pos = "-2,64,-4" });
        Assert.False(r.Ok);
        Assert.Contains("cast 2/10", r.Text);
        Assert.False(BlockShapes.IsLava(g.World.GetBlock(Cells[2])), "the loose lava source was scooped up again");
        Assert.True(g.World.Inventory.CountItem(Items.LavaBucket) >= 1, "and is in the bucket");
        Assert.Equal(2, g.Mixed.Count);
    }

    [Fact]
    public async Task Lights_the_portal_after_casting_when_asked_and_flint_and_steel_is_carried()
    {
        var g = World();
        g.Give(Items.FlintAndSteel, 1);
        var r = await g.Run("cast_portal", new { pos = "-2,64,-4", light = true });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("portal lit", r.Text);
        Assert.Equal(Blocks.Portal, g.World.GetBlock(-1, 65, -4));
    }
}

/// <summary>The frame check written out again (PortalFrames is internal to the skills assembly): 10 obsidian around a 2x3 inside.</summary>
internal static class PortalFramesProbe
{
    public static bool IsFrame(IBlockAccess w, BlockPos i) => FrameCells(i).All(c => w.GetBlock(c.X, c.Y, c.Z) == Blocks.Obsidian);

    private static IEnumerable<BlockPos> FrameCells(BlockPos i)
    {
        for (int u = 0; u <= 1; u++) { yield return i.Offset(u, -1, 0); yield return i.Offset(u, 3, 0); }
        for (int v = 0; v <= 2; v++) { yield return i.Offset(-1, v, 0); yield return i.Offset(2, v, 0); }
    }
}
