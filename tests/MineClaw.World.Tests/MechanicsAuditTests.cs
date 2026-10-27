using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>Regression tests for the mc-dev (Bukkit mc-dev 218a2a71, b1.8) mechanics audit — docs/mechanics-audit.md.</summary>
public class MechanicsAuditTests
{
    private const int SignPost = 63, Farmland = 60, SoulSand = 88, WoodStairs = 53, Cake = 92, Piston = 33,
        PistonHead = 34, GlassPane = 102, IronBars = 101, Glass = 20, Flower = 37, Torch = 50;

    private static List<AABB> Collision(WorldState w, int x, int y, int z)
    {
        var list = new List<AABB>();
        BlockShapes.AddCollisionBoxes(w, x, y, z, list);
        return list;
    }

    private static List<AABB> Selection(WorldState w, int x, int y, int z)
    {
        var list = new List<AABB>();
        BlockShapes.AddSelectionBoxes(w, x, y, z, list);
        return list;
    }

    private static void Near(AABB expected, List<AABB> actual)
    {
        var a = Assert.Single(actual);
        Assert.Equal(expected.MinX, a.MinX, 9); Assert.Equal(expected.MinY, a.MinY, 9); Assert.Equal(expected.MinZ, a.MinZ, 9);
        Assert.Equal(expected.MaxX, a.MaxX, 9); Assert.Equal(expected.MaxY, a.MaxY, 9); Assert.Equal(expected.MaxZ, a.MaxZ, 9);
    }

    // ---- climbing: EntityLiving.p() is ladder-only in b1.8

    [Fact]
    public void Vines_are_not_climbable_in_b18()
    {
        var w = TestWorld.Create();
        w.Fill(-5, 63, -5, 5, 63, 5, Stone);
        w.Fill(0, 64, 1, 0, 70, 1, Stone);      // wall at z = 1
        w.Fill(0, 64, 0, 0, 69, 0, Vine, 1);    // vines on it
        Assert.False(BlockShapes.IsClimbable(w, Vine));
        Assert.True(BlockShapes.IsClimbable(w, Ladder));
        var s = TestWorld.Standing(0.5, 64, 0.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Yaw: 0), 30); // pushing into the wall
        Assert.True(s.Position.Y < 64.01, $"climbed vines to y={s.Position.Y}");
    }

    // ---- shapes

    [Fact]
    public void Glass_pane_alone_is_a_plus_and_connects_like_BlockThin()
    {
        var w = TestWorld.Create();
        w.Set(0, 64, 0, GlassPane);
        var alone = Collision(w, 0, 64, 0);
        Assert.Equal(2, alone.Count);
        Assert.Contains(new AABB(0, 64, 0.4375, 1, 65, 0.5625), alone);
        Assert.Contains(new AABB(0.4375, 64, 0, 0.5625, 65, 1), alone);

        w.Set(-1, 64, 0, Stone); // opaque neighbour west only: half bar towards it, no Z bar
        var west = Collision(w, 0, 64, 0);
        Assert.Equal([new AABB(0, 64, 0.4375, 0.5, 65, 0.5625)], west);
        Assert.Equal([new AABB(0, 64, 0.4375, 0.5625, 65, 0.5625)], Selection(w, 0, 64, 0)); // bounds keep the centre post

        w.Set(1, 64, 0, Glass); // glass east too: full X bar
        Assert.Equal([new AABB(0, 64, 0.4375, 1, 65, 0.5625)], Collision(w, 0, 64, 0));

        w.Set(5, 64, 5, IronBars);
        w.Set(5, 64, 6, IronBars); // bars connect to bars
        Assert.Equal([new AABB(5.4375, 64, 5.5, 5.5625, 65, 6)], Collision(w, 5, 64, 5));
        Assert.False(BlockShapes.IsFullCube(w, 0, 64, 0));
    }

    [Fact]
    public void Piston_head_collides_as_plate_plus_arm()
    {
        var w = TestWorld.Create();
        w.Set(0, 64, 0, PistonHead, 1); // facing up: plate at the top
        var boxes = Collision(w, 0, 64, 0);
        Assert.Equal(2, boxes.Count);
        Assert.Contains(new AABB(0, 64.75, 0, 1, 65, 1), boxes);
        Assert.Contains(new AABB(0.375, 64, 0.375, 0.625, 64.75, 0.625), boxes);
        w.Set(1, 64, 0, Piston, 8 | 1); // extended base facing up: full collision, shortened selection
        Assert.Equal([new AABB(1, 64, 0, 2, 65, 1)], Collision(w, 1, 64, 0));
        Assert.Equal([new AABB(1, 64, 0, 2, 64.75, 1)], Selection(w, 1, 64, 0));
    }

    [Fact]
    public void Selection_boxes_differ_from_collision_where_mcdev_does()
    {
        var w = TestWorld.Create();
        w.Set(0, 64, 0, Farmland);
        Assert.Equal([new AABB(0, 64, 0, 1, 65, 1)], Collision(w, 0, 64, 0));        // BlockSoil.e(): full
        Assert.Equal([new AABB(0, 64, 0, 1, 64.9375, 1)], Selection(w, 0, 64, 0));   // bounds 0.9375
        w.Set(1, 64, 0, SoulSand);
        Assert.Equal([new AABB(1, 64, 0, 2, 64.875, 1)], Collision(w, 1, 64, 0));
        Assert.Equal([new AABB(1, 64, 0, 2, 65, 1)], Selection(w, 1, 64, 0));
        w.Set(2, 64, 0, WoodStairs, 0);
        Assert.Equal(2, Collision(w, 2, 64, 0).Count);
        Assert.Equal([new AABB(2, 64, 0, 3, 65, 1)], Selection(w, 2, 64, 0));
        w.Set(3, 64, 0, Cake, 0);
        Assert.Equal(64.4375, Collision(w, 3, 64, 0)[0].MaxY, 9);
        Assert.Equal(64.5, Selection(w, 3, 64, 0)[0].MaxY, 9);
        w.Set(4, 64, 0, Flower);
        Near(new AABB(4.3, 64, 0.3, 4.7, 64.6, 0.7), Selection(w, 4, 64, 0));
        w.Set(5, 64, 0, Torch, 1); // on a west wall
        Near(new AABB(5, 64.2, 0.35, 5.3, 64.8, 0.65), Selection(w, 5, 64, 0));
        w.Set(6, 64, 0, Vine, 2); // west plate
        Assert.Equal([new AABB(6, 64, 0, 6.0625, 65, 1)], Selection(w, 6, 64, 0));
        Assert.Empty(Collision(w, 6, 64, 0));
    }

    // ---- water: BlockFluids flow vector uses materials, not collision boxes

    [Fact]
    public void Flow_vector_treats_signs_as_walls_and_ladders_as_open()
    {
        var w = TestWorld.Create();
        w.Set(0, 64, 0, Water, 3);
        w.Set(1, 63, 0, Water, 0);        // water one block down to the east → pull east
        Assert.True(PlayerPhysics.FlowVector(w, 0, 64, 0).X > 0.99);
        w.Set(1, 64, 0, SignPost);         // no collision, but WOOD material: blocks the flow
        Assert.Equal(0, PlayerPhysics.FlowVector(w, 0, 64, 0).X, 9);
        w.Set(1, 64, 0, Ladder, 4);       // collision box, but ORIENTABLE material: flow passes
        Assert.True(PlayerPhysics.FlowVector(w, 0, 64, 0).X > 0.99);
    }

    [Fact]
    public void Falling_water_next_to_a_slab_pushes_down()
    {
        var w = TestWorld.Create();
        w.Set(0, 64, 0, Water, 8);        // falling water
        w.Set(1, 64, 0, Slab);             // buildable STONE material (not a full cube)
        Assert.True(PlayerPhysics.FlowVector(w, 0, 64, 0).Y < -0.99);
    }

    // ---- fall damage into water: only a sampled tick inside the water resets the fall distance

    [Fact]
    public void Shallow_water_does_not_break_a_long_fall()
    {
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Stone);
        w.Set(0, 64, 0, StillWater);
        Assert.False(Safety.WaterLanding(w, 0, 0, 74, 64).BreaksFall);   // 10 blocks into 1-deep water
        Assert.True(Safety.IsDeadlyColumn(w, 0, 74, 0, 3));
        Assert.True(Safety.WaterLanding(w, 0, 0, 67, 64).BreaksFall);    // 3 blocks: no damage anyway
        w.Fill(0, 64, 0, 0, 66, 0, StillWater);                         // 3 deep: window 2.6 > step 1.2
        Assert.True(Safety.WaterLanding(w, 0, 0, 76, 66).BreaksFall);
        Assert.False(Safety.IsDeadlyColumn(w, 0, 76, 0, 3));
        Assert.Equal(0.6516, Safety.MaxFallStep(3.3), 3);                 // the step that skips 1-deep water
    }

    [Fact]
    public void Pathfinder_drops_only_into_deep_enough_water()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 0, 73, 0, Stone); // pillar, top at 74
        w.Set(1, 63, 0, Stone);
        w.Set(1, 64, 0, StillWater);
        var opts = new PathOptions { AllowPartial = false };
        Assert.Equal(PathStatus.NoPath, Pathfinder.FindPath(w, new BlockPos(0, 74, 0), new GoalBlock(1, 64, 0), opts).Status);
        w.Fill(1, 64, 0, 1, 66, 0, StillWater);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 74, 0), new GoalBlock(1, 66, 0), opts);
        Assert.Equal(PathStatus.Found, r.Status);
    }

    // ---- cobweb: Entity.move ×0.25 / ×0.05 and motion zeroed, but the fall distance is kept (no reset in b1.8)

    [Fact]
    public void Web_slows_but_does_not_reset_fall_distance()
    {
        var w = TestWorld.Create();
        w.Set(0, 60, 0, Stone);
        w.Set(0, 61, 0, 30);
        var s = TestWorld.Standing(0.5, 70, 0.5);
        s.OnGround = false;
        for (int i = 0; i < 60 && s.Position.Y > 62.2; i++) PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
        float before = (float)s.FallDistance;
        Assert.True(before > 7);
        for (int i = 0; i < 5; i++) PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
        Assert.True(s.FallDistance >= before, "web must not reset the fall distance");
    }

    // ---- items

    [Fact]
    public void Item_pickup_box_matches_EntityHuman()
    {
        var feet = new Vec3d(0.5, 64, 0.5);
        Assert.True(ItemEntityRules.InPickupRange(feet, new Vec3d(1.9, 64.125, 0.5)));   // 1.4 away
        Assert.False(ItemEntityRules.InPickupRange(feet, new Vec3d(2.0, 64.125, 0.5)));  // 1.5 away
        Assert.False(ItemEntityRules.InPickupRange(feet, new Vec3d(0.5, 63.125, 0.5)));  // lying a block below
        Assert.True(ItemEntityRules.InPickupRange(feet, new Vec3d(0.5, 65.8, 0.5)));     // at head height
        Assert.Equal(TimeSpan.FromSeconds(1.5), ItemEntityRules.RemainingPickupDelay(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(6000, ItemEntityRules.DespawnTicks);
    }
}
