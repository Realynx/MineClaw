using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>Bucket source rules (ItemBucket b1.8.1: material water/lava + meta 0, raytrace with liquids on) and aiming.</summary>
public class BucketSourceTests
{
    [Theory]
    [InlineData(8, 0, true)]   // fresh source (flowing id)
    [InlineData(9, 0, true)]   // still source
    [InlineData(8, 1, false)]
    [InlineData(9, 3, false)]  // a settled flow keeps its level as meta
    [InlineData(9, 7, false)]
    [InlineData(8, 8, false)]  // falling water
    [InlineData(9, 9, false)]
    [InlineData(8, 15, false)]
    [InlineData(0, 0, false)]
    [InlineData(10, 0, false)] // lava is not water
    public void Water_source_is_any_water_id_with_meta_0(int id, int meta, bool source)
    {
        Assert.Equal(source, Blocks.IsWaterSource(id, meta));
        Assert.Equal(source, FluidRules.IsSource(id, meta, lava: false));
    }

    [Theory]
    [InlineData(10, 0, true)]
    [InlineData(11, 0, true)]
    [InlineData(10, 2, false)]
    [InlineData(11, 6, false)]
    [InlineData(10, 8, false)]
    [InlineData(9, 0, false)]
    public void Lava_source_is_any_lava_id_with_meta_0(int id, int meta, bool source) =>
        Assert.Equal(source, FluidRules.IsSource(id, meta, lava: true));

    /// <summary>The live trench (x=-1..2 one row, one bucket poured at the west end): a source (id 9 meta 0) then
    /// settled flow levels 1..3, all id 9.</summary>
    private static FakeGame Trench()
    {
        var g = LiquidTests.Island();
        g.Set(-1, 63, -3, Blocks.Water, 0);
        g.Set(0, 63, -3, Blocks.Water, 1);
        g.Set(1, 63, -3, Blocks.Water, 2);
        g.Set(2, 63, -3, Blocks.Water, 3);
        g.Give(Items.Bucket, 1);
        return g;
    }

    [Fact]
    public async Task Trench_flowing_cell_resolves_to_the_pool_source()
    {
        var g = Trench();
        var r = await g.Run("fill_bucket", new { pos = "1,63,-3" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("-1,63,-3", r.Text);
        Assert.Equal(1, g.World.Inventory.CountItem(Items.WaterBucket));
        Assert.Equal(0, g.World.GetBlock(-1, 63, -3)); // the source was the block the server raytrace hit
    }

    [Fact]
    public async Task Trench_floor_cell_and_nearby_search_find_the_source()
    {
        var g = Trench();
        var floor = await g.Run("fill_bucket", new { pos = "1,62,-3" });
        Assert.True(floor.Ok, floor.Text);

        var g2 = Trench();
        g2.Set(-1, 63, -3, Blocks.FlowingWater, 0); // a fresh source still has the flowing id
        var near = await g2.Run("fill_bucket");
        Assert.True(near.Ok, near.Text);
        Assert.Equal(1, g2.World.Inventory.CountItem(Items.WaterBucket));
    }

    [Fact]
    public void Trench_aim_hits_the_source_top_face()
    {
        var g = Trench();
        var src = new BlockPos(-1, 63, -3);
        var eye = g.World.Player.EyePosition;
        var aim = BucketOps.SourceAim(g.World, eye, src, 4.5);
        Assert.NotNull(aim);
        Assert.True(aim.Value.Y > 63.9 && aim.Value.Y < 64, $"top face, got {aim}");
        Assert.True(aim.Value.X >= -1 && aim.Value.X <= 0 && aim.Value.Z >= -3 && aim.Value.Z <= -2, $"inside the source's face, got {aim}");
        Assert.Equal(src, BucketOps.FirstHit(g.World, eye, aim.Value, liquids: true));
    }

    [Fact]
    public void Aim_never_goes_through_another_source_or_a_solid_block()
    {
        var g = LiquidTests.Island();
        // Two sources in a row, the far one is the target; a cobble lid covers the far one's top.
        g.Set(0, 63, -2, Blocks.Water, 0);
        g.Set(0, 63, -3, Blocks.Water, 0);
        g.Set(0, 64, -3, Blocks.Cobblestone);
        var eye = g.World.Player.EyePosition;
        var target = new BlockPos(0, 63, -3);
        var aim = BucketOps.SourceAim(g.World, eye, target, 4.5);
        if (aim is { } a) Assert.Equal(target, BucketOps.FirstHit(g.World, eye, a, liquids: true));
        // Fully boxed in (lid, near source in front, dirt around): no clear line at all.
        Assert.Null(aim);
        // The near one is open on top and aimable.
        var near = BucketOps.SourceAim(g.World, eye, new BlockPos(0, 63, -2), 4.5);
        Assert.NotNull(near);
        Assert.Equal(new BlockPos(0, 63, -2), BucketOps.FirstHit(g.World, eye, near.Value, liquids: true));
    }

    [Fact]
    public async Task Protected_nearest_source_does_not_hide_an_unprotected_one()
    {
        var g = LiquidTests.Island();
        g.Set(1, 63, 1, Blocks.Water, 0);  // nearest: protected irrigation
        g.ProtectedBlocks.Add(new BlockPos(1, 63, 1));
        g.Set(-3, 63, -3, Blocks.Water, 0); // farther, free
        g.Give(Items.Bucket, 1);
        var r = await g.Run("fill_bucket");
        Assert.True(r.Ok, r.Text);
        Assert.Contains("-3,63,-3", r.Text);
        Assert.Equal(Blocks.Water, g.World.GetBlock(1, 63, 1));
    }

    [Fact]
    public async Task Only_flowing_or_falling_water_says_so()
    {
        var g = LiquidTests.Island();
        g.Set(2, 63, 2, Blocks.Water, 8);
        g.Give(Items.Bucket, 1);
        var r = await g.Run("fill_bucket", new { pos = "2,63,2" });
        Assert.False(r.Ok);
        Assert.Contains("falling water", r.Text);
        var n = await g.Run("fill_bucket");
        Assert.False(n.Ok);
        Assert.Contains("can't be scooped", n.Text);
    }

    [Fact]
    public async Task Fill_walks_around_a_wall_to_a_visible_source()
    {
        var g = LiquidTests.Island();
        g.Stand(0.5, 64, 0.5);
        g.Set(0, 63, -3, Blocks.Water);
        // A distance-only reach goal is already satisfied on the wrong side of this wall.
        g.Fill(-2, 64, -2, 2, 66, -2, Blocks.Cobblestone);
        g.Give(Items.Bucket, 1);
        var r = await g.Run("fill_bucket", new { pos = "0,63,-3" });
        Assert.True(r.Ok, r.Text);
        Assert.NotEmpty(g.Visited);
        Assert.Equal(1, g.World.Inventory.CountItem(Items.WaterBucket));
    }

    [Fact]
    public async Task Fill_tries_another_source_in_the_same_pond_but_not_a_protected_one()
    {
        var g = LiquidTests.Island();
        g.Stand(0.5, 64, 0.5);
        g.Set(0, 63, -3, Blocks.Water);
        g.Set(0, 64, -3, Blocks.Cobblestone); // covered target
        g.Set(0, 63, -2, Blocks.Water); // the only exposed face hits this source first
        g.ProtectedBlocks.Add(new BlockPos(0, 63, -2));
        g.Set(1, 63, -2, Blocks.Water);
        g.Give(Items.Bucket, 1);
        var r = await g.Run("fill_bucket", new { pos = "0,63,-3" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("1,63,-2", r.Text);
        Assert.Equal(Blocks.Water, g.World.GetBlock(0, 63, -2));
        Assert.Equal(Blocks.Water, g.World.GetBlock(0, 63, -3));
    }

    [Fact]
    public async Task Empty_trench_pass_reports_zero_changes_and_the_reason()
    {
        var g = LiquidTests.Island();
        var r = await g.Run("excavate", new { box = "1,66,1,3,66,1" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("dug 0", r.Text);
        Assert.Contains("already clear", r.Text);
    }

    [Fact]
    public void Infinite_pool_cell_refills()
    {
        var g = LiquidTests.Island();
        g.Set(0, 63, -3, Blocks.Water, 0);
        g.Set(1, 63, -3, Blocks.Water, 0);
        g.Set(2, 63, -3, Blocks.Water, 0);
        Assert.True(FluidRules.Refills(g.World, new BlockPos(1, 63, -3)));
        Assert.False(FluidRules.Refills(g.World, new BlockPos(0, 63, -3)));
    }
}
