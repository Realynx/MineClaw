using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Pouring and filling from dry land (live 11-04, Story canals: "no block face to pour against" cell after cell, pawns walking into
/// the channel and nearly drowning, the colony's own infinite pool refused as "protected").
/// </summary>
public class BucketPourTests
{
    /// <summary>The island with a 1-deep trench x=-1..2 at z=-3: a source at its west end that has run into the next cell.</summary>
    private static FakeGame Canal()
    {
        var g = LiquidTests.Island();
        for (int x = -1; x <= 2; x++) g.Set(x, 63, -3, 0);
        g.Set(-1, 63, -3, Blocks.Water, 0);
        g.Set(0, 63, -3, Blocks.Water, 1); // flowing, from the first pour
        return g;
    }

    private static bool Wet(FakeGame g)
    {
        var p = g.World.Player.Position;
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y + 0.01), z = (int)Math.Floor(p.Z);
        return BlockShapes.IsLiquid(g.World.GetBlock(x, y, z)) || BlockShapes.IsLiquid(g.World.GetBlock(x, y + 1, z));
    }

    [Fact]
    public async Task A_canal_cell_holding_flowing_water_is_poured_from_the_bank_never_from_the_water()
    {
        var g = Canal();
        g.Give(Items.WaterBucket, 1);
        var r = await g.Run("empty_bucket", new { pos = "0,63,-3" });
        Assert.True(r.Ok, r.Text);
        Assert.True(FluidRules.IsSource(g.World, new BlockPos(0, 63, -3), false), "the flowing cell is a source now");
        Assert.False(Wet(g), $"stood in water at {g.World.Player.Position}");
        Assert.DoesNotContain(g.Visited, v => BlockShapes.IsLiquid(g.World.GetBlock(v.ToBlockPos())));
        Assert.Equal(1, g.World.Inventory.CountItem(Items.Bucket));
    }

    [Fact]
    public async Task Every_cell_of_a_dug_canal_takes_its_pour_from_dry_ground_even_far_from_where_the_pawn_starts()
    {
        var g = Canal();
        g.Stand(3.5, 64, 3.5); // the far corner of the island
        foreach (var x in new[] { 1, 2 })
        {
            g.Give(Items.WaterBucket, 1);
            var r = await g.Run("empty_bucket", new { pos = $"{x},63,-3" });
            Assert.True(r.Ok, r.Text);
            Assert.False(Wet(g), $"stood in water at {g.World.Player.Position}");
        }
        Assert.True(FluidRules.IsSource(g.World, new BlockPos(2, 63, -3), false));
    }

    [Fact]
    public void A_fill_aim_reaches_the_source_from_the_sneaking_eye_too()
    {
        // A source in a 1-deep hole seen from stands around it, near and far: the server raytraces from the lower 1.12.2 sneaking eye
        // when the body sneaks at an edge; an aim only the standing eye clears hits the rim and the bucket stays empty.
        var g = LiquidTests.Island();
        var src = new BlockPos(0, 63, 0);
        g.Set(src, Blocks.Water, 0);
        var drop = new Vec3d(0, Aiming.SneakEyeDrop, 0);
        int aims = 0;
        foreach (var (x, z) in new[] { (2, 0), (3, 0), (0, 3), (-3, 1), (2, 2), (-2, -3) })
        {
            var eye = new Vec3d(x + 0.5, 64 + 1.62, z + 0.5);
            if (BucketOps.SourceAim(g.World, eye, src, 4.5) is not { } p) continue;
            aims++;
            Assert.Equal(src, BucketOps.FirstHit(g.World, eye, p, liquids: true));
            Assert.Equal(src, BucketOps.FirstHit(g.World, eye - drop, p - drop, liquids: true));
        }
        Assert.True(aims >= 3, $"{aims} stands had an aim");
    }

    [Fact]
    public async Task A_cell_with_nothing_beside_or_under_it_says_so_instead_of_searching_for_a_face()
    {
        var g = LiquidTests.Island();
        g.Give(Items.WaterBucket, 1);
        var r = await g.Run("empty_bucket", new { pos = "0,66,0" }); // in the air over the island
        Assert.False(r.Ok);
        Assert.Contains("nothing beside or under", r.Text);
    }

    [Fact]
    public async Task A_protected_pool_that_refills_gives_water_and_a_protected_lone_source_does_not()
    {
        var g = LiquidTests.Island();
        foreach (var (x, z) in new[] { (2, 2), (3, 2), (2, 3), (3, 3) })
        {
            g.Set(x, 63, z, Blocks.Water, 0);
            g.ProtectedBlocks.Add(new BlockPos(x, 63, z)); // the colony's own infinite pool, marked as farm water
        }
        g.Give(Items.Bucket, 1);
        var r = await g.Run("fill_bucket", new { pos = "2,63,2" });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(1, g.World.Inventory.CountItem(Items.WaterBucket));

        var lone = LiquidTests.Island();
        lone.Set(2, 63, 2, Blocks.Water, 0);
        lone.ProtectedBlocks.Add(new BlockPos(2, 63, 2)); // a generator's single source: used up if taken
        lone.Give(Items.Bucket, 1);
        var no = await lone.Run("fill_bucket", new { pos = "2,63,2" });
        Assert.False(no.Ok);
        Assert.Equal(Blocks.Water, lone.World.GetBlock(2, 63, 2));
    }
}
