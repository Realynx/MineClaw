using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Fox, 11-04: "When pawns place special blocks like furnaces that have rotation, they don't take into consideration the direction
/// they're facing when they place it to replicate the same rotation that was specified in the hologram." <see cref="BlockOrientation"/>
/// maps a wanted meta to the clicks that give it (face, yaw, cursor; the stand for pistons), per version, and its forward model agrees
/// with the fake server's own transcription of the vanilla rules.
/// </summary>
public class BlockOrientationTests
{
    private static readonly BlockPos At = new(10, 64, 10);
    /// <summary>A player three blocks south of the cell, on its level (no piston turns up or down from here).</summary>
    private static readonly Vec3d Level = new(10.5, 64, 13.5);

    private static List<Turn> Solve(int id, int meta, bool modern, Vec3d? feet = null) =>
        BlockOrientation.Solve(id, meta, At, feet ?? Level, modern, _ => true).ToList();

    private static void AllYaw(List<Turn> turns, float yaw)
    {
        Assert.NotEmpty(turns);
        Assert.All(turns, t => Assert.Equal(yaw, t.Yaw));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_furnace_faces_the_player_so_the_yaw_looks_the_other_way(bool modern)
    {
        AllYaw(Solve(61, 2, modern), 0);   // front north: the player stands north, looking south
        AllYaw(Solve(61, 3, modern), 180); // front south: looking north
        AllYaw(Solve(61, 4, modern), 270); // front west: looking east
        AllYaw(Solve(61, 5, modern), 90);  // front east: looking west
        Assert.Equal(6, Solve(61, 5, modern).Select(t => t.Face).Distinct().Count()); // any face: the yaw alone decides
        Assert.False(BlockOrientation.Turns(61, 0, modern)); // meta 0 can't be clicked: any placement does
    }

    [Fact]
    public void Stairs_take_the_look_and_on_1122_their_half_from_the_face_and_cursor()
    {
        var yawOf = new Dictionary<int, float> { [0] = 270, [1] = 90, [2] = 0, [3] = 180 }; // rising E W S N = looking that way
        foreach (var (m, yaw) in yawOf)
        {
            var bottom = Solve(53, m, true);
            AllYaw(bottom, yaw);
            Assert.Contains(bottom, t => t.Face == BlockFace.Top);
            Assert.DoesNotContain(bottom, t => t.Face == BlockFace.Bottom);
            Assert.All(bottom.Where(t => t.Face is not (BlockFace.Top or BlockFace.Bottom)), t => Assert.True(t.CursorY < 0.5f));

            var top = Solve(53, m | 4, true);
            AllYaw(top, yaw);
            Assert.Contains(top, t => t.Face == BlockFace.Bottom);
            Assert.DoesNotContain(top, t => t.Face == BlockFace.Top);
            Assert.All(top.Where(t => t.Face is not (BlockFace.Top or BlockFace.Bottom)), t => Assert.True(t.CursorY > 0.5f));

            AllYaw(Solve(67, m, false), yaw); // b1.8.1 BlockStairs.postPlace: the same quarters
        }
    }

    [Fact]
    public void A_log_lies_along_the_clicked_faces_axis_on_1122_and_needs_no_yaw()
    {
        Assert.Equal([BlockFace.Bottom, BlockFace.Top], Solve(17, 1, true).Select(t => t.Face).Order().ToArray());          // spruce, upright
        Assert.Equal([BlockFace.West, BlockFace.East], Solve(17, 1 | 4, true).Select(t => t.Face).Distinct().Order().ToArray());  // along x
        Assert.Equal([BlockFace.North, BlockFace.South], Solve(17, 2 | 8, true).Select(t => t.Face).Distinct().Order().ToArray()); // along z
        Assert.All(Solve(17, 4, true), t => Assert.Null(t.Yaw));
        Assert.False(BlockOrientation.Turns(17, 12, true)); // bark all round: no click makes it
        Assert.False(BlockOrientation.Turns(17, 1, false)); // b1.8.1 logs have no axis
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_torch_hangs_off_the_face_clicked(bool modern)
    {
        Assert.Equal([BlockFace.East], Solve(50, 1, modern).Select(t => t.Face).Distinct()); // on the west wall: its east face
        Assert.Equal([BlockFace.West], Solve(50, 2, modern).Select(t => t.Face).Distinct());
        Assert.Equal([BlockFace.South], Solve(50, 3, modern).Select(t => t.Face).Distinct());
        Assert.Equal([BlockFace.North], Solve(50, 4, modern).Select(t => t.Face).Distinct());
        Assert.Equal([BlockFace.Top], Solve(50, 5, modern).Select(t => t.Face).Distinct());
        Assert.All(Solve(50, 3, modern), t => Assert.Null(t.Yaw));
        // Only faces with something behind them: a torch on the south wall needs the block south of the cell.
        Assert.Empty(BlockOrientation.Solve(50, 4, At, Level, modern, f => f != BlockFace.North));
    }

    [Fact]
    public void A_slab_is_a_top_half_from_the_underside_or_high_on_a_side_on_1122()
    {
        var top = Solve(126, 8 | 2, true); // a birch top slab
        Assert.Contains(top, t => t.Face == BlockFace.Bottom);
        Assert.DoesNotContain(top, t => t.Face == BlockFace.Top);
        Assert.All(top.Where(t => t.Face >= BlockFace.North), t => Assert.Equal(0.75f, t.CursorY));
        var bottom = Solve(126, 2, true);
        Assert.Contains(bottom, t => t.Face == BlockFace.Top);
        Assert.All(bottom.Where(t => t.Face >= BlockFace.North), t => Assert.Equal(0.25f, t.CursorY));
        Assert.False(BlockOrientation.Turns(44, 3, false)); // b1.8.1: slabs are always the bottom half
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_piston_points_at_the_player_in_3D_so_up_and_down_need_a_stand_over_or_under_it(bool modern)
    {
        // Horizontal from the yaw (toward the player) when the eyes are level with it.
        AllYaw(Solve(33, 2, modern), 0);
        AllYaw(Solve(33, 3, modern), 180);
        AllYaw(Solve(33, 4, modern), 270);
        AllYaw(Solve(33, 5, modern), 90);
        // Up: the eyes more than 2 over its base, within 2 blocks: one cell over and beside it. Not from the level stand.
        Assert.Empty(Solve(33, 1, modern));
        var over = new Vec3d(11.5, 65, 10.5);
        Assert.NotEmpty(Solve(33, 1, modern, over));
        Assert.All(Solve(33, 1, modern, over), t => Assert.Null(t.Yaw));
        // Down: the eyes under its base: two below, beside it.
        Assert.Empty(Solve(33, 0, modern));
        Assert.NotEmpty(Solve(33, 0, modern, new Vec3d(11.5, 62, 10.5)));
        // Close and level still turns horizontally (feet on its level, or one below).
        AllYaw(Solve(33, 5, modern, new Vec3d(11.5, 64, 10.5)), 90);
        AllYaw(Solve(33, 5, modern, new Vec3d(11.5, 63, 10.5)), 90);
        // An observer looks away from the player: up from below.
        if (modern) Assert.NotEmpty(Solve(218, 1, modern, new Vec3d(11.5, 62, 10.5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_door_and_a_bed_go_on_the_top_face_along_the_look(bool modern)
    {
        var door = new Dictionary<int, float> { [0] = 270, [1] = 0, [2] = 90, [3] = 180 }; // E S W N
        foreach (var (m, yaw) in door)
        {
            var t = Solve(64, m, modern);
            AllYaw(t, yaw);
            Assert.All(t, x => Assert.Equal(BlockFace.Top, x.Face));
        }
        // 1.12.2 ItemDoor: looking east (i > 0) the hinge is on the right when hitZ > 0.5: the left hinge asks for z 0.25.
        Assert.Equal(0.25f, Solve(64, 0, true).Single().CursorZ);
        var bed = new Dictionary<int, float> { [0] = 0, [1] = 90, [2] = 180, [3] = 270 }; // foot → head S W N E
        foreach (var (m, yaw) in bed)
        {
            var t = Solve(26, m, modern);
            AllYaw(t, yaw);
            Assert.All(t, x => Assert.Equal(BlockFace.Top, x.Face));
        }
        Assert.False(BlockOrientation.Turns(64, 8, modern)); // the upper half comes with the lower
    }

    [Fact]
    public void A_trapdoor_takes_the_side_clicked_and_on_1122_the_cursor_height_or_the_look()
    {
        Assert.Equal([BlockFace.North], Solve(96, 0, false).Select(t => t.Face).Distinct()); // b1.8.1: sides only
        var top = Solve(96, 8, true); // against the south side, top half
        Assert.Contains(top, t => t.Face == BlockFace.North && t.CursorY > 0.5f);
        Assert.Contains(top, t => t.Face == BlockFace.Bottom && t.Yaw == 0); // from the underside: toward the player
        Assert.DoesNotContain(top, t => t.Face == BlockFace.Top);
    }

    [Fact]
    public void Same_compares_only_what_the_click_decides()
    {
        Assert.True(BlockOrientation.Same(61, 5, 5 | 8, true));   // the furnace's front, not its runtime bits
        Assert.False(BlockOrientation.Same(61, 5, 4, true));
        Assert.True(BlockOrientation.Same(64, 1, (0) | 4, false)); // b1.8.1 flipped door (f − 1) | 4 looks like f
        Assert.True(BlockOrientation.Same(69, 5, 6, false));       // b1.8.1 floor lever: 5 or 6 at random
        Assert.False(BlockOrientation.Same(69, 5, 6, true));
        Assert.True(BlockOrientation.Same(5, 0, 3, true));         // planks: nothing to turn
    }

    /// <summary>The faces a click can use to get a meta (bit = 1 &lt;&lt; face: D1 U2 N4 S8 W16 E32), which the colony director asks
    /// before it offers a turned cell: a wall torch needs its wall, never just the floor; a chest any neighbour.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlaceFaces_names_the_faces_a_meta_can_come_from(bool modern)
    {
        const int down = 1, up = 2, north = 4, south = 8, west = 16, east = 32, all = 63, sides = north | south | west | east;
        Assert.Equal(east, BlockOrientation.PlaceFaces(50, 1, modern));  // on the west wall: the east face of the block west of it
        Assert.Equal(west, BlockOrientation.PlaceFaces(50, 2, modern));
        Assert.Equal(south, BlockOrientation.PlaceFaces(50, 3, modern));
        Assert.Equal(north, BlockOrientation.PlaceFaces(50, 4, modern));
        Assert.Equal(up, BlockOrientation.PlaceFaces(50, 5, modern));    // on the floor: the top face of the block under it
        Assert.Equal(north, BlockOrientation.PlaceFaces(65, 2, modern));
        Assert.Equal(south, BlockOrientation.PlaceFaces(65, 3, modern));
        Assert.Equal(west, BlockOrientation.PlaceFaces(65, 4, modern));
        Assert.Equal(east, BlockOrientation.PlaceFaces(65, 5, modern));
        Assert.Equal(all, BlockOrientation.PlaceFaces(54, 2, modern));   // a chest: the yaw alone decides
        Assert.Equal(all, BlockOrientation.PlaceFaces(61, 5, modern));   // a furnace
        Assert.Equal(all, BlockOrientation.PlaceFaces(5, 0, modern));    // planks don't turn
        Assert.Equal(up, BlockOrientation.PlaceFaces(64, 0, modern));    // a door stands on the block under it
        if (modern)
        {
            Assert.Equal(sides | down, BlockOrientation.PlaceFaces(44, 8, modern)); // a top slab: a side high, or the underside
            Assert.Equal(south, BlockOrientation.PlaceFaces(154, 2, modern));       // a hopper feeding north: that block's south face
            Assert.Equal(sides | up, BlockOrientation.PlaceFaces(53, 0, modern));   // upright stairs: the underside would turn them over
            Assert.Equal(sides | down, BlockOrientation.PlaceFaces(53, 4, modern)); // upside-down stairs
        }
        else Assert.Equal(all, BlockOrientation.PlaceFaces(53, 0, modern));         // b1.8.1 stairs: the yaw alone decides
    }

    /// <summary>The client's forward model against the fake server's separate transcription of the vanilla rules, over every face,
    /// every quarter, both halves and a ring of stands (pistons' 3D rule), both versions.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_model_agrees_with_the_servers_rules(bool modern)
    {
        int[] ids = modern ? [61, 54, 23, 29, 33, 86, 53, 134, 126, 17, 170, 50, 65, 96, 107] : [61, 54, 23, 29, 33, 86, 53, 67, 50, 65, 96, 107];
        var stands = new List<Vec3d>();
        for (int dx = -3; dx <= 3; dx++) for (int dy = -3; dy <= 2; dy++) stands.Add(new Vec3d(At.X + 0.5 + dx, At.Y + dy, At.Z + 1.5));
        int checkedCount = 0;
        foreach (var id in ids)
        foreach (var face in BlockFaceExtensions.All)
        foreach (var hitY in new[] { 0.25f, 0.75f })
        for (int q = 0; q < 4; q++)
        foreach (var feet in stands)
        {
            int damage = id == 17 ? 1 : 0;
            var want = MineClaw.Sim.FakeServer.VanillaMeta(modern, id, damage, (int)face, hitY, q * 90f, (feet.X, feet.Y, feet.Z), At.X, At.Y, At.Z);
            var got = BlockOrientation.Predict(id, damage, At, new PlaceInput(face, q * 90f, feet, 0.5f, hitY, 0.5f), modern);
            if (want is not { } w || got is not { } g) continue; // the server leaves it alone / the click can't place it
            Assert.True(BlockOrientation.Same(id, w, g, modern), $"{id} face {face} y {hitY} q {q} feet {feet}: server {w}, model {g}");
            checkedCount++;
        }
        Assert.True(checkedCount > 1000, $"{checkedCount}");
    }
}
