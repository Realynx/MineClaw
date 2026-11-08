using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Scripting.Tests;

public sealed class ReachScanTests
{
    private static WorldState Floor(int y = 64)
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -8; x <= 8; x++)
            for (int z = -8; z <= 8; z++) w.SetBlock(x, y, z, Blocks.Stone, 0);
        return w;
    }

    private static BlockPos Stand(System.Text.Json.Nodes.JsonObject scan)
    {
        var xyz = scan["stand"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        return new BlockPos(xyz[0], xyz[1], xyz[2]);
    }

    [Fact]
    public void A_nearby_block_hidden_behind_a_wall_requires_a_visible_walkable_stand()
    {
        var w = Floor();
        var target = new BlockPos(2, 66, 0);
        w.SetBlock(target.X, target.Y, target.Z, Blocks.Cobblestone, 0);
        for (int y = 65; y <= 68; y++)
            for (int z = -1; z <= 1; z++) w.SetBlock(1, y, z, Blocks.Stone, 0);
        var scan = ReachScan.Scan(w, Beta181GameData.Instance, new Vec3d(0.5, 65, 0.5), target, walkable: true);
        Assert.False(scan["within"]!.GetValue<bool>());
        Assert.Equal(0, scan["steps"]!.GetValue<int>());
        var stand = Stand(scan);
        Assert.NotNull(Aiming.FindDigAim(w, new Vec3d(stand.X + 0.5, stand.Y + PlayerPhysics.EyeHeight, stand.Z + 0.5), target, ReachScan.Reach));
    }

    [Fact]
    public void A_cell_under_a_quarry_step_is_not_considered_reachable_through_the_step()
    {
        var w = Floor(61);
        var target = new BlockPos(0, 63, 0);
        w.SetBlock(0, 63, 0, Blocks.Cobblestone, 0);
        w.SetBlock(0, 64, 0, Blocks.Stone, 0);
        w.SetBlock(1, 62, 0, Blocks.Stone, 0); // the next step down
        w.SetBlock(1, 63, 0, Blocks.Stone, 0);
        var scan = ReachScan.Scan(w, Beta181GameData.Instance, new Vec3d(0.5, 65, 0.5), target, walkable: true);
        Assert.False(scan["within"]!.GetValue<bool>());
        Assert.Equal(0, scan["steps"]!.GetValue<int>());
        var stand = Stand(scan);
        Assert.NotNull(Aiming.FindDigAim(w, new Vec3d(stand.X + 0.5, stand.Y + PlayerPhysics.EyeHeight, stand.Z + 0.5), target, ReachScan.Reach));
    }

    [Fact]
    public void A_pillar_top_must_see_the_target_instead_of_stopping_at_the_first_close_height()
    {
        var w = Floor();
        var target = new BlockPos(2, 72, 0);
        w.SetBlock(2, 72, 0, Blocks.Cobblestone, 0);
        for (int x = 0; x <= 4; x++)
            for (int z = -2; z <= 2; z++) w.SetBlock(x, 70, z, Blocks.Stone, 0); // opaque ceiling below target
        var scan = ReachScan.Scan(w, Beta181GameData.Instance, new Vec3d(0.5, 65, 0.5), target, walkable: true);
        Assert.False(scan["within"]!.GetValue<bool>());
        Assert.Null(scan["stand"]); // the ceiling has no walkable access from below
    }

    [Fact]
    public void A_cluster_chooses_one_reachable_column_that_covers_both_adjacent_blocks()
    {
        var w = Floor();
        var targets = new[] { new BlockPos(2, 71, 0), new BlockPos(3, 71, 0) };
        foreach (var target in targets) w.SetBlock(target.X, target.Y, target.Z, Blocks.Cobblestone, 0);
        w.SetBlock(4, 68, 0, Blocks.Stone, 0); // tempting zero-pillar ledge, but disconnected from the floor
        var scan = ReachScan.Scan(w, Beta181GameData.Instance, new Vec3d(0.5, 65, 0.5), targets[0], walkable: true, targets);
        var stand = Stand(scan);
        int steps = scan["steps"]!.GetValue<int>();
        Assert.Equal(65, stand.Y);
        Assert.Equal(1, steps);
        var top = new Vec3d(stand.X + 0.5, stand.Y + steps, stand.Z + 0.5);
        Assert.All(targets, target => Assert.True(ReachScan.Within(w, top, target)));
    }

    [Fact]
    public void A_pair_of_remnants_against_a_quarry_wall_share_one_visible_pillar_top()
    {
        var w = Floor(34);
        var target = new BlockPos(2, 57, 0);
        var lower = new BlockPos(2, 56, 0);
        w.SetBlock(2, 57, 0, Blocks.Cobblestone, 0);
        w.SetBlock(2, 56, 0, Blocks.Cobblestone, 0);
        w.SetBlock(2, 40, 0, Blocks.Stone, 0); // an isolated step below the remnants
        for (int y = 35; y <= 63; y++)
            for (int z = -8; z <= 8; z++) w.SetBlock(3, y, z, Blocks.Stone, 0);
        var scan = ReachScan.Scan(w, Beta181GameData.Instance, new Vec3d(0.5, 35, 0.5), target, walkable: true);
        var stand = Stand(scan);
        int steps = scan["steps"]!.GetValue<int>();
        Assert.Equal(35, stand.Y);
        Assert.InRange(steps, 17, ReachScan.MaxPillar);
        var top = new Vec3d(stand.X + 0.5, stand.Y + steps, stand.Z + 0.5);
        Assert.True(ReachScan.Within(w, top, target));
        Assert.True(ReachScan.Within(w, top, lower));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deep_quarry_remnants_are_reached_via_the_wraparound_stair(bool fromRim)
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        for (int x = -8; x <= 27; x++)
            for (int z = -8; z <= 17; z++)
                for (int y = 34; y <= 63; y++)
                    if (y == 34 || x < 0 || x > 19 || z < 0 || z > 9) w.SetBlock(x, y, z, Blocks.Stone, 0);
        var ring = new List<(int X, int Z)>();
        for (int x = 0; x <= 19; x++) ring.Add((x, 0));
        for (int z = 1; z <= 9; z++) ring.Add((19, z));
        for (int x = 18; x >= 0; x--) ring.Add((x, 9));
        for (int z = 8; z > 0; z--) ring.Add((0, z));
        for (int k = 0; k <= 29; k++) w.SetBlock(ring[k].X, 63 - k, ring[k].Z, Blocks.Stone, 0);
        var targets = new[] { new BlockPos(19, 57, 4), new BlockPos(19, 56, 4) };
        foreach (var target in targets) w.SetBlock(target.X, target.Y, target.Z, Blocks.Cobblestone, 0);
        var from = fromRim ? new Vec3d(-1.5, 64, -0.5) : new Vec3d(6.5, 58, 0.5);
        var scan = ReachScan.Scan(w, Beta181GameData.Instance, from, targets[0], walkable: true, targets);
        Assert.NotNull(scan["stand"]);
        var stand = Stand(scan);
        int steps = scan["steps"]!.GetValue<int>();
        var top = new Vec3d(stand.X + 0.5, stand.Y + steps, stand.Z + 0.5);
        Assert.All(targets, target => Assert.True(ReachScan.Within(w, top, target)));
        var path = Pathfinder.FindPath(w, from, new GoalBlock(stand), new PathOptions
        {
            AllowPartial = false, AllowParkour = false, MaxNodes = 12000, Timeout = TimeSpan.FromMilliseconds(500),
        });
        Assert.Equal(PathStatus.Found, path.Status);
        Assert.DoesNotContain(path.Steps, step => step.Kind is MoveKind.Pillar or MoveKind.Bridge);
    }

    [Fact]
    public void A_visible_ceiling_block_is_reached_from_below_and_not_stood_on()
    {
        var w = Floor();
        var target = new BlockPos(2, 71, 0);
        w.SetBlock(2, 71, 0, Blocks.Cobblestone, 0);
        var scan = ReachScan.Scan(w, Beta181GameData.Instance, new Vec3d(0.5, 65, 0.5), target, walkable: true);
        Assert.False(scan["within"]!.GetValue<bool>());
        var stand = Stand(scan);
        int steps = scan["steps"]!.GetValue<int>();
        Assert.True(steps > 0);
        Assert.Equal(65, stand.Y);
        Assert.NotNull(Aiming.FindDigAim(w, new Vec3d(stand.X + 0.5, stand.Y + steps + PlayerPhysics.EyeHeight, stand.Z + 0.5), target, ReachScan.Reach));
    }
}
