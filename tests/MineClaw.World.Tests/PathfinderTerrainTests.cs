using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Live 11-04 (Story Mode, Fox): "Pawns are getting stuck in waterfalls and going into caves and not being able to get back out."
/// A step through flowing water costs more (a stream is crossed, never followed), a waterfall column much more; a walk that starts
/// under the sky pays for every cell the sky doesn't reach, so the surface route wins when there is one.
/// </summary>
public class PathfinderTerrainTests
{
    private static readonly PathOptions Strict = new() { AllowPartial = false };

    [Fact]
    public void A_waterfall_column_is_walked_around_when_there_is_a_dry_way()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 10, 63, 4, Cobble);
        for (int z = 0; z <= 2; z++) { w.SetBlock(5, 64, z, 9, 8); w.SetBlock(5, 65, z, 9, 8); } // falling water, rows 0..2
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(9, 64, 1), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => s.X == 5 && s.Z <= 2); // around it, by the dry rows
    }

    [Fact]
    public void A_one_block_stream_with_no_way_around_is_still_crossed()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 10, 63, 4, Cobble);
        for (int z = 0; z <= 4; z++) w.SetBlock(5, 64, z, 9, 2); // a flowing stream across the whole floor
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(9, 64, 1), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Single(r.Steps, s => s.X == 5);
    }

    [Fact]
    public void A_walk_from_the_surface_keeps_out_of_a_cave_when_the_surface_way_exists()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 10, 63, 6, Cobble);
        // The straight row z 0..1 between x 2 and 8 is "under ground" (no sky light); rows 3+ are open.
        int Sky(int x, int y, int z) => x is >= 2 and <= 8 && z <= 1 ? 0 : 15;
        var plain = Pathfinder.FindPath(w, new BlockPos(1, 64, 0), new GoalBlock(9, 64, 0), Strict);
        Assert.Contains(plain.Steps, s => s.X is >= 2 and <= 8 && s.Z <= 1); // no light: straight through
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 0), new GoalBlock(9, 64, 0), Strict with { SkyLight = Sky, UndergroundPenalty = 6 });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => s.X is >= 3 and <= 7 && s.Z <= 1);
        // A walk that starts under ground (mining) pays nothing extra: it goes straight.
        int Dark(int x, int y, int z) => 0;
        var down = Pathfinder.FindPath(w, new BlockPos(1, 64, 0), new GoalBlock(9, 64, 0), Strict with { SkyLight = Dark, UndergroundPenalty = 6 });
        Assert.Contains(down.Steps, s => s.X is >= 3 and <= 7 && s.Z == 0);
    }
}
