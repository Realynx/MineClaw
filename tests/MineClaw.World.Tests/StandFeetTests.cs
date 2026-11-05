using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

using static MineClaw.World.Tests.TestBlocks;

/// <summary>
/// <see cref="Safety.StandFeet"/>: anything that holds a player up in the version's collision boxes is somewhere to stand, not only a
/// full cube. Live 11-04: seeds on 1.12.2 farmland (15/16 high) were written off "near the void" by the client's collect while the
/// colony offered them. Every block stands on stone at y 63 with its own cell at y 64.
/// </summary>
public class StandFeetTests
{
    private const int Farmland = 60, Bed = 26, SnowLayer = 78, SoulSand = 88, Cake = 92, EnchantingTable = 116, DaylightSensor = 151, GrassPath = 208;

    private static WorldState World(bool release1122) => release1122 ? Physics1122Tests.World() : TestWorld.Create();

    private static WorldState On(bool release1122, int id, int meta = 0)
    {
        var w = World(release1122);
        w.Fill(-3, 63, -3, 3, 63, 3, Stone);
        w.Set(0, 64, 0, id, meta);
        return w;
    }

    [Theory]
    // 1.12.2 collision tops (minecraft-data shapes): farmland and path 15/16, soul sand 7/8, a bottom slab 1/2, four snow layers 3/8,
    // a bed 9/16, a chest 14/16, an enchanting table 12/16, a daylight sensor 6/16, a cake 1/2.
    [InlineData(true, Farmland, 0, 0.9375)]
    [InlineData(true, GrassPath, 0, 0.9375)]
    [InlineData(true, SoulSand, 0, 0.875)]
    [InlineData(true, Slab, 0, 0.5)]
    [InlineData(true, SnowLayer, 3, 0.375)]
    [InlineData(true, Bed, 0, 0.5625)]
    [InlineData(true, Chest, 0, 0.875)]
    [InlineData(true, EnchantingTable, 0, 0.75)]
    [InlineData(true, DaylightSensor, 0, 0.375)]
    [InlineData(true, Cake, 0, 0.5)]
    // b1.8.1 (mc-dev): soul sand 7/8, a slab 1/2, snow from 4 layers 1/2, a bed 9/16, a cake 7/16 (BlockCake: 1/2 less 1/16).
    [InlineData(false, SoulSand, 0, 0.875)]
    [InlineData(false, Slab, 0, 0.5)]
    [InlineData(false, SnowLayer, 3, 0.5)]
    [InlineData(false, Bed, 0, 0.5625)]
    [InlineData(false, Cake, 0, 0.4375)]
    public void A_block_lower_than_a_cube_holds_the_feet_at_its_top_seen_from_its_cell_and_the_one_over_it(bool release1122, int id, int meta, double top)
    {
        var w = On(release1122, id, meta);
        Assert.Equal(top, BlockShapes.CollisionTop(w, 0, 64, 0), 6);
        Assert.Equal(64 + top, Safety.StandFeet(w, 0, 64, 0)!.Value, 6); // the pathfinder's foot cell for it
        Assert.Equal(64 + top, Safety.StandFeet(w, 0, 65, 0)!.Value, 6); // the same spot one cell up (where a drop on it is)
        Assert.False(Safety.IsStandable(w, 0, 65, 0));                   // the full-ground rule alone never saw it
        // Room for the body: a block where the head goes takes it away.
        w.Set(0, 66, 0, Stone);
        Assert.Null(Safety.StandFeet(w, 0, 64, 0));
        Assert.Null(Safety.StandFeet(w, 0, 65, 0));
    }

    [Theory]
    [InlineData(false, Farmland)] // b1.8.1 BlockSoil's collision box is a full cube (only its outline is 15/16)
    [InlineData(false, Chest)]    // and so is a b1.8.1 chest's
    [InlineData(false, Stone)]
    [InlineData(true, Stone)]
    [InlineData(true, Dirt)]
    public void A_full_cube_is_stood_on_top_of_never_inside(bool release1122, int id)
    {
        var w = On(release1122, id);
        Assert.Equal(65.0, Safety.StandFeet(w, 0, 65, 0));
        Assert.True(Safety.IsStandable(w, 0, 65, 0));
        Assert.Null(Safety.StandFeet(w, 0, 64, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Air_liquids_the_void_and_hazards_hold_nobody(bool release1122)
    {
        var w = World(release1122);
        Assert.Null(Safety.StandFeet(w, 0, 64, 0)); // the void
        w.Fill(0, 60, 0, 0, 63, 0, StillWater);
        Assert.Null(Safety.StandFeet(w, 0, 64, 0)); // a water column over the void
        Assert.Null(Safety.StandFeet(w, 0, 63, 0));
        w.Fill(2, 63, 0, 2, 63, 0, Stone);
        w.Set(2, 64, 0, Lava);
        Assert.Null(Safety.StandFeet(w, 2, 64, 0)); // lava at the feet
        w.Set(4, 63, 0, Stone);
        w.Set(4, 64, 0, Cactus);                    // 15/16 high, but it hurts
        Assert.Null(Safety.StandFeet(w, 4, 64, 0));
        Assert.Null(Safety.StandFeet(w, 4, 65, 0));
        w.Set(6, 63, 0, Stone);
        w.Set(6, 64, 0, StillWater);                // wading on ground is fine
        Assert.Equal(64.0, Safety.StandFeet(w, 6, 64, 0));
    }

    [Fact]
    public void One_snow_layer_in_1122_has_no_collision_and_is_stood_in_on_the_ground()
    {
        var w = On(true, SnowLayer, 0);
        Assert.False(BlockShapes.HasCollision(w, 0, 64, 0));
        Assert.Equal(64.0, Safety.StandFeet(w, 0, 64, 0));
        Assert.Null(Safety.StandFeet(w, 0, 65, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_drop_inside_a_tilled_field_has_a_pickup_spot_on_the_farmland_next_to_it(bool release1122)
    {
        var w = World(release1122);
        w.Fill(-6, 62, -6, 6, 62, 6, Stone);
        w.Fill(-5, 63, -5, 5, 63, 5, Dirt);
        w.Fill(-4, 63, -4, 4, 63, 4, Farmland); // a 9×9 field, the surface at 63
        double rest = 63 + BlockShapes.CollisionTop(w, 0, 63, 0);
        var seeds = new Vec3d(0.5, rest, 0.5);
        Assert.True(ItemEntityRules.HasPickupSpot(w, seeds));
        var at = ItemEntityRules.PickupPoint(w, 1, release1122 ? 63 : 64, 0, seeds);
        Assert.NotNull(at);
        Assert.Equal(rest, at.Value.Y, 6); // the feet on the farmland next to it
        // Over the void there is nowhere at all.
        Assert.False(ItemEntityRules.HasPickupSpot(w, new Vec3d(20.5, 64, 20.5)));
    }

    [Fact]
    public void The_1122_pathfinder_stands_on_farmland_in_the_cell_stand_feet_names()
    {
        var w = World(true);
        w.Fill(-6, 62, -6, 6, 62, 6, Stone);
        w.Fill(-4, 63, -4, 4, 63, 4, Farmland);
        var r = Pathfinder.FindPath(w, new BlockPos(-3, 63, 0), new GoalBlock(3, 63, 0), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.NotEmpty(r.Steps);
        Assert.All(r.Steps, s => Assert.Equal(s.Floor, Safety.StandFeet(w, s.X, s.Y, s.Z)!.Value, 6)); // the planner's own feet height
        Assert.All(r.Steps, s => Assert.Equal(63 + 0.9375, s.Floor, 6));
        Assert.IsType<Release1122PhysicsRules>(w.Physics);
    }
}
