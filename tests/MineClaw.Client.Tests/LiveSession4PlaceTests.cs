using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 2026-10-27, placement failures that kept recurring with the same vague line ("no solid neighbour face in reach (or
/// I'm in the way)"). Both layouts are rebuilt from the live world memory (db snapshot, world_chunks around the spots):
/// 15:56 refilling 7,62,9 from inside its own hole (the only support was the south face of 7,62,8, hidden from every cell
/// with z &lt; 9; b1.8.1's server doesn't raytrace 0x0F, so it is clicked unseen), and 16:36 replanting a sapling at 15,65,1,
/// which has air under it (the real reason, said plainly).
/// </summary>
public class LiveSession4PlaceTests
{
    private static Task<bool> Until(Func<bool> c, int ms = 4000) => Harness.WaitAsync(c, ms);

    /// <summary>The gen floor at 15:56: y63 cobble floor with the hole the bot dug (7..9 × 7..9), y62 only 6,62,8 and 7,62,8 under
    /// it (15:51 snapshot), slabs at 7,64,6/7/9 beside the gen; the bot inside the hole on 7,62,8.</summary>
    private static void GenHole(FakeServer srv)
    {
        srv.Blocks.Clear();
        for (int x = 3; x <= 11; x++)
        for (int z = 4; z <= 12; z++)
            if (x is < 7 or > 9 || z is < 7 or > 9) srv.Blocks[(x, 63, z)] = Blocks.Cobblestone;
        srv.Blocks[(6, 62, 8)] = Blocks.Cobblestone;
        srv.Blocks[(7, 62, 8)] = Blocks.Cobblestone;
        srv.Blocks[(4, 62, 6)] = Blocks.Cobblestone;
        srv.Blocks[(5, 62, 6)] = Blocks.Cobblestone;
        foreach (var (x, z) in new[] { (7, 6), (7, 7), (7, 9) }) { srv.Blocks[(x, 64, z)] = Blocks.Slab; srv.Metas[(x, 64, z)] = 3; }
        foreach (var x in new[] { 4, 5, 6 }) srv.Blocks[(x, 64, 9)] = Blocks.Cobblestone;
        srv.Blocks[(6, 64, 8)] = Blocks.Cobblestone;
        srv.SpawnAt = (7.5, 63, 8.5);
        srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 7);
        srv.ConsumeOnPlace = true;
    }

    [Fact]
    public void Gen_hole_layout_has_no_visible_support_face_from_the_bots_cell_but_one_blind_click()
    {
        using var srv = new FakeServer();
        GenHole(srv);
        // Same blocks, pure check: from the eye in the hole the south face of 7,62,8 faces away (eye z 8.5 < 9).
        var world = new TestBlocks(srv.Blocks);
        var eye = new Vec3d(7.5, 63 + 1.62, 8.5);
        Assert.Null(Aiming.FindPlaceAim(world, eye, new BlockPos(7, 62, 9), 4.5, AABB.ForPlayer(new Vec3d(7.5, 63, 8.5))));
        var blind = PlaceRules.BlindAim(world, eye, new BlockPos(7, 62, 9), 4.5);
        Assert.NotNull(blind);
        Assert.Equal(new BlockPos(7, 62, 8), blind!.Value.Against);
        Assert.Equal(BlockFace.South, blind.Value.Face);
    }

    [Fact]
    public async Task Identical_refused_placements_stop_sending_after_three_attempts()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            GenHole(srv);
            srv.Hook = p => p is PlayerBlockPlacementPacket;
        });
        await using var _ = c;
        using var __ = s;
        for (int i = 0; i < 3; i++) Assert.False((await c.PlaceBlockAsync(7, 62, 9, Blocks.Cobblestone)).Ok);
        int before = s.Got<PlayerBlockPlacementPacket>().Count();
        var stopped = await c.PlaceBlockAsync(7, 62, 9, Blocks.Cobblestone);
        Assert.Contains("stopped after 3 identical server refusals", stopped.Message);
        Assert.Equal(before, s.Got<PlayerBlockPlacementPacket>().Count());
        // A changed support/target is new evidence, not a permanent ban on this coordinate.
        s.SetBlock(7, 62, 8, Blocks.Stone);
        Assert.True(await Until(() => c.World.GetBlock(7, 62, 8) == Blocks.Stone));
        await c.PlaceBlockAsync(7, 62, 9, Blocks.Cobblestone);
        Assert.True(s.Got<PlayerBlockPlacementPacket>().Count() > before);
    }

    [Fact]
    public async Task Hidden_face_refusals_near_spawn_never_teach_spawn_protection()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            GenHole(srv);
            srv.Hook = p => p is PlayerBlockPlacementPacket;
        });
        await using var _ = c;
        using var __ = s;
        for (int i = 0; i < 2; i++) Assert.False((await c.PlaceBlockAsync(7, 62, 9, Blocks.Cobblestone)).Ok);
        Assert.Null(c.ProtectedRegion);
    }

    [Fact]
    public async Task Refills_7_62_9_from_inside_the_hole_by_clicking_the_hidden_south_face_of_7_62_8()
    {
        var (c, s) = await Harness.StartAsync(GenHole);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Until(() => c.World.Player.OnGround));
        var r = await c.PlaceBlockAsync(7, 62, 9, Blocks.Cobblestone);
        Assert.True(r.Ok, r.Message);
        var click = s.Got<PlayerBlockPlacementPacket>().Last();
        Assert.Equal((7, 62, 8), (click.X, (int)click.Y, click.Z));
        Assert.Equal((byte)BlockFace.South, click.Direction);
        Assert.Equal(Blocks.Cobblestone, s.Blocks.GetValueOrDefault((7, 62, 9)));
    }

    [Fact]
    public async Task A_cell_with_nothing_solid_around_says_so_instead_of_in_reach_or_in_the_way()
    {
        var (c, s) = await Harness.StartAsync(GenHole);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Until(() => c.World.Player.OnGround));
        var r = await c.PlaceBlockAsync(9, 61, 10, Blocks.Cobblestone);
        Assert.False(r.Ok);
        Assert.Contains("9,61,10 has nothing solid on any side to place against", r.Message);
        Assert.DoesNotContain("in the way", r.Message);
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
    }

    /// <summary>The tree farm at 16:36 (snapshot): y63 cobble floor, dirt at 13/15/17 × 0/3 on y64, saplings at 15,65,0, 15,65,3
    /// and 17,65,3, logs at 13,65..67,0, 13,65..66,3 and the grown tree at 17,65..69,0; the bot at 16,64,-1.</summary>
    private static void TreeFarm(FakeServer srv)
    {
        srv.Blocks.Clear();
        for (int x = 11; x <= 19; x++)
        for (int z = -3; z <= 5; z++)
            srv.Blocks[(x, 63, z)] = Blocks.Cobblestone;
        foreach (var x in new[] { 13, 15, 17 })
        foreach (var z in new[] { 0, 3 })
            srv.Blocks[(x, 64, z)] = Blocks.Dirt;
        srv.Blocks[(15, 65, 0)] = Blocks.Sapling;
        srv.Blocks[(15, 65, 3)] = Blocks.Sapling;
        srv.Blocks[(17, 65, 3)] = Blocks.Sapling;
        for (int y = 65; y <= 67; y++) srv.Blocks[(13, y, 0)] = Blocks.Log;
        for (int y = 65; y <= 66; y++) srv.Blocks[(13, y, 3)] = Blocks.Log;
        for (int y = 65; y <= 69; y++) srv.Blocks[(17, y, 0)] = Blocks.Log;
        srv.SpawnAt = (16.5, 64, -0.5);
        srv.Inventory[36] = Harness.Stack(Blocks.Sapling, 64);
    }

    [Fact]
    public async Task Sapling_at_15_65_1_says_there_is_no_dirt_under_it()
    {
        var (c, s) = await Harness.StartAsync(TreeFarm);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Until(() => c.World.Player.OnGround));
        var r = await c.PlaceBlockAsync(15, 65, 1, Blocks.Sapling);
        Assert.False(r.Ok);
        Assert.Contains("15,65,1 has no dirt/grass under it for a sapling (15,64,1 is air)", r.Message);
        Assert.DoesNotContain("neighbour face", r.Message);
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
    }

    [Fact]
    public void Sapling_soil_rule_points_to_the_nearest_free_dirt()
    {
        var blocks = new Dictionary<(int, int, int), int>
        {
            [(15, 64, 0)] = Blocks.Dirt, [(15, 65, 0)] = Blocks.Sapling, [(17, 64, 0)] = Blocks.Dirt, [(15, 63, 1)] = Blocks.Cobblestone,
        };
        var w = new TestBlocks(blocks);
        Assert.Contains("no dirt/grass under it", PlaceRules.SoilProblem(w, new BlockPos(15, 65, 1), Blocks.Sapling));
        Assert.Null(PlaceRules.SoilProblem(w, new BlockPos(17, 65, 0), Blocks.Sapling));
        Assert.Equal(new BlockPos(17, 65, 0), PlaceRules.NearestSoil(w, new BlockPos(15, 65, 1), Blocks.Sapling));
        Assert.Null(PlaceRules.SoilProblem(w, new BlockPos(15, 64, 1), Blocks.Cobblestone)); // ordinary blocks have no soil rule
    }

    /// <summary>Minimal block access over a FakeServer-style dictionary (everything loaded).</summary>
    private sealed class TestBlocks(Dictionary<(int, int, int), int> blocks) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => blocks.GetValueOrDefault((x, y, z));
        public int GetMeta(int x, int y, int z) => blocks.GetValueOrDefault((x, y, z)) == Blocks.Slab ? 3 : 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }
}
