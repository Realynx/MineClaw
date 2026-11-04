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
        Assert.Contains(down.Steps, s => s.X is >= 3 and <= 7 && s.Z <= 1);
    }

    /// <summary>Ground: dirt y 54..62, grass 63 over x −4..58, z 0..6; a tunnel 2 high at z 3, feet y 57, from x 2 to 42, whose only
    /// exit is a stair up to the surface at x 43..48.</summary>
    private static WorldState Tunnel()
    {
        var w = TestWorld.Create(-1, 3);
        w.Fill(-4, 54, 0, 58, 62, 6, Dirt);
        w.Fill(-4, 63, 0, 58, 63, 6, Grass);
        w.Fill(2, 57, 3, 42, 58, 3, Air);
        w.Set(42, 59, 3, Air);                                                    // head room for the first step up
        for (int k = 1; k <= 6; k++) w.Fill(42 + k, 57 + k, 3, 42 + k, 59 + k, 3, Air);
        return w;
    }

    /// <summary>Live 11-04 story-1: "within 3 of" a tree's base was met in a cave under it ("chopped 0/39"). A cell below the spot with
    /// ground between it and the spot doesn't count; the same distance up top does.</summary>
    [Fact]
    public void Within_n_of_a_spot_is_never_met_from_the_cave_under_it()
    {
        var w = Tunnel();
        var target = new BlockPos(5, 64, 3);
        Assert.True(new GoalNear(target, 7).IsReached(5, 57, 3));                 // plain: the cave cell 7 under it
        var g = GoalFiltered.NearNotFromBelow(w, target, 7);
        Assert.False(g.IsReached(5, 57, 3));                                      // dirt between its eyes and the spot
        Assert.True(g.IsReached(9, 64, 3));                                       // up top beside it
        Assert.True(g.IsReached(12, 64, 3));
        // A cell below with an open line (a hollow beside the spot) still counts.
        w.Fill(5, 61, 3, 5, 63, 3, Air); // a shaft straight under it
        Assert.True(g.IsReached(5, 61, 3));
    }

    /// <summary>Fox 11-04: "it makes the pawn go way out of the way underground." From the tunnel under a spot on the surface, a plain
    /// search walks the 90-odd blocks out by the far exit and back; a way up to the surface (digging, <see cref="PathOptions.Covered"/>)
    /// digs a short stair up instead.</summary>
    [Fact]
    public void A_way_up_digs_a_short_stair_instead_of_walking_far_round_through_the_tunnel()
    {
        var w = Tunnel();
        bool Covered(int x, int y, int z)
        {
            int n = 0;
            for (int yy = y + 2; yy <= y + 10; yy++) if (w.GetBlock(x, yy, z) != 0 && ++n >= 2) return true;
            return false;
        }
        var start = new BlockPos(4, 57, 3);
        var goal = new GoalNear(new BlockPos(5, 64, 3), 1);
        var opts = Strict with { Timeout = TimeSpan.FromSeconds(10), MaxNodes = 200_000 };
        var walk = Pathfinder.FindPath(w, start, goal, opts);
        Assert.Equal(PathStatus.Found, walk.Status);
        Assert.True(walk.Steps.Count > 60, walk.ToString());                      // the walk out and back: a detour
        var up = Pathfinder.FindPath(w, start, goal, opts with { AllowBreak = true, Covered = Covered });
        Assert.Equal(PathStatus.Found, up.Status);
        Assert.Contains(up.Steps, s => s.Break.Count > 0);
        Assert.True(up.Steps.Count <= 16, up.ToString());
        Assert.All(up.Steps, s => Assert.InRange(s.X, 0, 10));                    // never out along the tunnel
    }
}
