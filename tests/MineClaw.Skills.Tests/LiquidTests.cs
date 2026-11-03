using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

public class LiquidTests
{
    /// <summary>A 9×9 island, 3 deep (y 61-63), me on top at the centre.</summary>
    internal static FakeGame Island(int r = 4)
    {
        var g = new FakeGame();
        g.Fill(-r, 61, -r, r, 63, r, Blocks.Dirt);
        g.Stand(0.5, 64, 0.5);
        return g;
    }

    [Fact]
    public async Task Infinite_water_2x2_makes_four_sources()
    {
        var g = Island();
        g.Give(Items.WaterBucket, 2);
        var r = await g.Run("infinite_water", new { pos = "2,63,2" });
        Assert.True(r.Ok, r.Text);
        foreach (var p in new[] { new BlockPos(2, 63, 2), new BlockPos(3, 63, 2), new BlockPos(2, 63, 3), new BlockPos(3, 63, 3) })
            Assert.True(FluidRules.IsSource(g.World, p, false), $"{p} not a source");
        Assert.Equal(2, g.World.Inventory.CountItem(Items.Bucket));
        // And it really is infinite: scoop one, it refills from its two neighbours.
        var f = await g.Run("fill_bucket", new { pos = "2,63,2" });
        Assert.True(f.Ok, f.Text);
    }

    [Fact]
    public async Task Infinite_water_line_of_three()
    {
        var g = Island();
        g.Give(Items.WaterBucket, 2);
        var r = await g.Run("infinite_water", new { pos = "-3,63,2", shape = "line" });
        Assert.True(r.Ok, r.Text);
        Assert.True(FluidRules.IsSource(g.World, new BlockPos(-2, 63, 2), false), "middle of the line becomes a source");
    }

    [Fact]
    public async Task Infinite_water_needs_two_buckets_and_refuses_lava_nearby()
    {
        var g = Island();
        g.Give(Items.WaterBucket, 1);
        Assert.Contains("2 water buckets", (await g.Run("infinite_water")).Text);
        g.Give(Items.WaterBucket, 1);
        g.Set(4, 63, 2, Blocks.Lava);
        var r = await g.Run("infinite_water", new { pos = "2,63,2" });
        Assert.False(r.Ok, "lava next to the basin: refused (it would leak into it)");
        var pour = await g.Run("empty_bucket", new { pos = "2,64,2" }); // surface water would run to the lava
        Assert.False(pour.Ok);
        Assert.Contains("lava", pour.Text);
        Assert.Empty(g.Mixed);
        Assert.Equal(2, g.World.Inventory.CountItem(Items.WaterBucket));
    }

    [Fact]
    public async Task Lava_is_never_placed_where_it_could_touch_water()
    {
        var g = Island();
        g.Give(Items.LavaBucket, 1);
        g.Set(3, 63, 0, Blocks.Water); // pool on the island
        var r = await g.Run("move_lava", new { to = "3,64,2" });
        Assert.False(r.Ok);
        Assert.Contains("water", r.Text);
        Assert.Equal(1, g.World.Inventory.CountItem(Items.LavaBucket));
        Assert.Empty(g.Mixed);
        // Far from water and from me: allowed.
        g.Fill(-4, 64, -4, -4, 64, 4, Blocks.Cobblestone); // a rim so it doesn't spill toward me
        var ok = await g.Run("move_lava", new { to = "-3,64,-3" });
        Assert.True(ok.Ok, ok.Text);
        Assert.Empty(g.Mixed);
    }

    [Fact]
    public void Lava_contact_names_what_would_form()
    {
        var g = Island();
        g.Set(2, 64, 0, Blocks.Water);
        Assert.Contains("obsidian", FluidRules.LavaContact(g.World, new BlockPos(1, 64, 0), false));
        Assert.Contains("cobblestone", FluidRules.LavaContact(g.World, new BlockPos(-1, 64, 0), false));
        Assert.Null(FluidRules.LavaContact(g.World, new BlockPos(-4, 64, -4), false));
    }

    [Fact]
    public async Task Make_obsidian_pours_water_on_lava_and_takes_it_back()
    {
        var g = Island();
        g.Set(2, 63, 2, Blocks.Lava, 0);
        g.Give(Items.WaterBucket, 1);
        var r = await g.Run("make_obsidian", new { pos = "2,63,2" });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(Blocks.Obsidian, g.World.GetBlock(2, 63, 2));
        Assert.Equal(1, g.World.Inventory.CountItem(Items.WaterBucket));
        Assert.Equal(0, g.World.GetBlock(2, 64, 2));
    }

    [Fact]
    public async Task Make_obsidian_skips_protected_generator_lava()
    {
        var g = Island();
        g.Set(2, 63, 2, Blocks.Lava, 0);
        g.ProtectedBlocks.Add(new BlockPos(2, 63, 2));
        g.Give(Items.WaterBucket, 1);
        var r = await g.Run("make_obsidian");
        Assert.False(r.Ok);
        Assert.Equal(Blocks.Lava, g.World.GetBlock(2, 63, 2));
    }

    [Fact]
    public async Task Water_poured_into_a_dug_hole_lands_in_it_even_from_the_sneaking_eye()
    {
        // From a stand two cells off, the nearest visible point of the hole's far wall is just over the near rim:
        // the 0.08-lower sneaking ray
        // hits the rim's top and the server fills the cell above it.
        var g = Island();
        var hole = new BlockPos(2, 63, 0);
        g.Set(hole, 0);
        g.ServerEyeDrop = Aiming.SneakEyeDrop;
        g.Give(Items.WaterBucket, 1);
        var r = await g.Run("empty_bucket", new { pos = "2,63,0" });
        Assert.True(r.Ok, r.Text);
        Assert.True(BlockShapes.IsWater(g.World.GetBlock(hole)), $"hole holds {g.World.GetBlock(hole)}");
        for (int x = -4; x <= 4; x++)
        for (int z = -4; z <= 4; z++)
            Assert.False(BlockShapes.IsWater(g.World.GetBlock(x, 64, z)), $"water on top of the grass at {x},64,{z}");
    }

    [Fact]
    public async Task Water_cant_be_placed_in_the_nether()
    {
        var g = Island();
        g.World.Dimension = -1;
        g.Give(Items.WaterBucket, 2);
        var r = await g.Run("empty_bucket", new { pos = "2,64,2" });
        Assert.False(r.Ok);
        Assert.Contains("overworld", r.Text);
    }
}
