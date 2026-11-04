using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 11-04 story-1 (Fox: "edge cases with the pathing, where it makes the pawn go way out of the way underground … when I'm trying to
/// make it farm trees"): chops failed "39 log(s) out of reach (up to y 76); chopped 0/39", "no clutch blocks on me … 4 log(s) out of
/// reach", "no path to within 3 of … it's 5 blocks up". In a normal world a tree is now chopped whole from nothing: dirt dug for a pillar
/// (dug back down, its holes filled), a way dug up out of a trench or a cave beside the trunk, and the spot replanted.
/// </summary>
public class NormalWorldChopTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private readonly List<string> _lines = new();
    private Func<ClientOptions, ClientOptions> Logged => o => o with
    {
        Log = m =>
        {
            lock (_lines) _lines.Add(m);
            try { log.WriteLine(m); } catch (InvalidOperationException) { }
        },
    };

    /// <summary>A grass field x/z −6..22 over dirt from y 54 (the ground), stone under it.</summary>
    private static void Field(FakeServer s, int x1 = -6, int x2 = 22, int z1 = -6, int z2 = 22)
    {
        s.Blocks.Clear();
        for (int x = x1; x <= x2; x++)
        for (int z = z1; z <= z2; z++)
        {
            for (int y = 50; y <= 53; y++) s.Blocks[(x, y, z)] = Blocks.Stone;
            for (int y = 54; y <= 62; y++) s.Blocks[(x, y, z)] = Blocks.Dirt;
            s.Blocks[(x, 63, z)] = Blocks.Grass;
        }
    }

    /// <summary>Logs and dirt drop themselves, grass drops dirt, every <paramref name="saplingEvery"/>-th leaf a sapling of its kind.</summary>
    private static void Drops(FakeServer s, int saplingEvery)
    {
        int leaves = 0;
        s.DropOnDig = true;
        s.ConsumeOnPlace = true;
        s.DigDrop = (_, id, meta) => id switch
        {
            Blocks.Log => new ItemStack(Blocks.Log, 1, (short)(meta & 3)),
            Blocks.Leaves => Interlocked.Increment(ref leaves) % saplingEvery == 0 ? new ItemStack(Blocks.Sapling, 1, (short)(meta & 3)) : null,
            Blocks.Grass => new ItemStack(Blocks.Dirt, 1, 0),
            _ => new ItemStack((short)id, 1, 0),
        };
    }

    private void Dump()
    {
        lock (_lines) foreach (var l in _lines.Where(l => l.StartsWith("chop", StringComparison.Ordinal) || l.StartsWith("goto", StringComparison.Ordinal)).TakeLast(60))
            try { log.WriteLine("> " + l); } catch (InvalidOperationException) { }
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(12, 0)]
    [InlineData(8, 2)]
    public async Task A_tree_is_chopped_whole_from_an_empty_inventory_on_a_dirt_pillar_and_replanted(int height, int wood)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Field(srv);
            TreeWorld.Tree(srv, 8, 8, height, wood);
            Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (3.5, 64, 8.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Dump();
        Assert.True(r.Ok, r.Message);
        Assert.StartsWith($"chopped {(wood == 2 ? "birch" : "oak")} tree ({height} logs,", r.Message);
        Assert.All(Enumerable.Range(64, height), y => Assert.NotEqual(Blocks.Log, s.Blocks.GetValueOrDefault((8, y, 8))));
        // Replanted with its own species on the same spot.
        Assert.Equal(Blocks.Sapling, s.Blocks.GetValueOrDefault((8, 64, 8)));
        Assert.Equal(wood, s.Metas.GetValueOrDefault((8, 64, 8)) & 3);
        // No pillar left standing by the trunk; the dirt it dug for one was put back.
        var left = s.Blocks.Where(b => b.Value == Blocks.Dirt && b.Key.Item2 >= 64 && Math.Abs(b.Key.Item1 - 8) <= 2 && Math.Abs(b.Key.Item3 - 8) <= 2).ToList();
        Assert.True(left.Count == 0, "pillar left: " + string.Join(" ", left.Select(b => b.Key)));
        var holes = Enumerable.Range(-6, 29).SelectMany(x => Enumerable.Range(-6, 29).Select(z => (x, z)))
            .Where(p => !s.Blocks.ContainsKey((p.x, 63, p.z))).ToList();
        Assert.True(holes.Count == 0, "holes left in the ground: " + string.Join(" ", holes));
        if (height >= 12) // it climbed on a pillar of the junk it had: the tree's own felled logs
            Assert.Contains(s.Got<PlayerBlockPlacementPacket>(), p => p.HeldItem.Id is Blocks.Log or Blocks.Dirt);
    }

    /// <summary>A 2x2 jungle trunk 12 high (1.12.2): all 48 logs come down from the pillar and the spot gets a jungle sapling.</summary>
    [Fact]
    public async Task A_two_by_two_jungle_tree_of_12_is_chopped_whole()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Field(srv);
            for (int y = 64; y <= 75; y++)
                foreach (var (x, z) in new[] { (8, 8), (9, 8), (8, 9), (9, 9) }) { srv.Blocks[(x, y, z)] = Blocks.Log; srv.Metas[(x, y, z)] = 3; }
            for (int y = 74; y <= 77; y++)
                for (int x = 6; x <= 11; x++)
                    for (int z = 6; z <= 11; z++)
                        if (!srv.Blocks.ContainsKey((x, y, z))) { srv.Blocks[(x, y, z)] = Blocks.Leaves; srv.Metas[(x, y, z)] = 3; }
            Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (3.5, 64, 8.5);
        }, Logged, release1122: true);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast with { Timeout = TimeSpan.FromSeconds(90) });
        Dump();
        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, s.Blocks.Count(b => b.Value == Blocks.Log));
        Assert.Contains(new[] { (8, 8), (9, 8), (8, 9), (9, 9) }, p => s.Blocks.GetValueOrDefault((p.Item1, 64, p.Item2)) == Blocks.Sapling);
        var left = s.Blocks.Where(b => b.Value == Blocks.Dirt && b.Key.Item2 >= 64).ToList();
        Assert.True(left.Count == 0, "pillar left: " + string.Join(" ", left.Select(b => b.Key)));
    }

    /// <summary>Nothing on it at all and nothing it can reach first (a remnant whose lowest log hangs 8 above the ground): it digs dirt
    /// for a pillar beside the tree, climbs, takes every log, digs the pillar back, fills the holes and plants the spot under it.</summary>
    [Fact]
    public async Task With_nothing_to_climb_on_it_digs_dirt_for_a_pillar_and_fills_the_holes_afterwards()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Field(srv);
            TreeWorld.Tree(srv, 8, 8, 15);                                   // logs 64..78, crown 76..79
            for (int y = 64; y <= 71; y++) srv.Blocks.Remove((8, y, 8));       // its lower trunk is gone: logs 72..78 hang
            Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (3.5, 64, 8.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(at: new BlockPos(8, 72, 8), options: TreeWorld.Fast);
        Dump();
        Assert.True(r.Ok, r.Message);
        Assert.Contains(s.Got<PlayerBlockPlacementPacket>(), p => p.HeldItem.Id == Blocks.Dirt);    // a dirt pillar
        Assert.Contains(s.Dug, d => d.Id is Blocks.Grass or Blocks.Dirt && d.Y == 63);              // dug from the ground
        Assert.All(Enumerable.Range(72, 7), y => Assert.NotEqual(Blocks.Log, s.Blocks.GetValueOrDefault((8, y, 8))));
        var left = s.Blocks.Where(b => b.Value == Blocks.Dirt && b.Key.Item2 >= 64 && Math.Abs(b.Key.Item1 - 8) <= 2 && Math.Abs(b.Key.Item3 - 8) <= 2).ToList();
        Assert.True(left.Count == 0, "pillar left: " + string.Join(" ", left.Select(b => b.Key)));
        var holes = Enumerable.Range(-6, 29).SelectMany(x => Enumerable.Range(-6, 29).Select(z => (x, z)))
            .Where(p => !s.Blocks.ContainsKey((p.x, 63, p.z))).ToList();
        Assert.True(holes.Count == 0, "holes left in the ground: " + string.Join(" ", holes));
        Assert.Equal(Blocks.Sapling, s.Blocks.GetValueOrDefault((8, 64, 8)));
    }

    /// <summary>Live 11-04: "chop … failed: no path … it's 5 blocks up". A pawn at the bottom of a 4-deep trench beside the tree, with
    /// nothing to pillar with, digs its way up beside the trunk and chops the tree.</summary>
    [Fact]
    public async Task A_pawn_in_a_four_deep_trench_by_the_tree_climbs_out_and_chops_it()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Field(srv);
            for (int z = 6; z <= 10; z++)
                for (int y = 60; y <= 63; y++) srv.Blocks.Remove((5, y, z)); // a trench 1 wide, 5 long, 4 deep, no way out
            TreeWorld.Tree(srv, 8, 8, 6);
            Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (5.5, 60, 8.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Dump();
        Assert.True(r.Ok, r.Message);
        Assert.All(Enumerable.Range(64, 6), y => Assert.NotEqual(Blocks.Log, s.Blocks.GetValueOrDefault((8, y, 8))));
        Assert.Equal(Blocks.Grass, s.Blocks.GetValueOrDefault((8, 63, 8))); // the tree's own soil is never dug
    }

    /// <summary>
    /// The detour regression: the pawn starts in a tunnel 6 blocks under the tree whose only exit is 14 blocks east. The walk out and
    /// back on top is ~40 blocks for ~8 straight; the chop digs a short stair up beside the trunk instead (the route stays near the
    /// tree), and the route's shape is logged.
    /// </summary>
    [Fact]
    public async Task A_pawn_in_a_cave_under_the_tree_digs_up_beside_it_instead_of_walking_far_round()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Field(srv, x2: 31); // the fake server sends chunks −1..1 (x −16..31)
            for (int x = 8; x <= 22; x++)
                for (int y = 57; y <= 58; y++) srv.Blocks.Remove((x, y, 8)); // the tunnel, 2 high
            srv.Blocks.Remove((22, 59, 8)); // head room for the first step up
            for (int k = 1; k <= 6; k++)
                for (int y = 57 + k; y <= 59 + k; y++) srv.Blocks.Remove((22 + k, y, 8)); // its stair up to the surface at x 29
            TreeWorld.Tree(srv, 8, 8, 6);
            Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (9.5, 57, 8.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        // There is a walk out (by the far exit and back over the top), ~40 blocks for ~8 straight.
        var walk = Pathfinder.FindPath(c.World, c.World.Player.Position, new GoalNear(new BlockPos(8, 64, 6), 1),
            PathOptions.Default with { AllowPartial = false, MaxNodes = 200_000, Timeout = TimeSpan.FromSeconds(10) });
        Assert.Equal(PathStatus.Found, walk.Status);
        Assert.True(RouteStats.Of(c.World.Player.Position, walk.Steps).IsDetour);
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Dump();
        Assert.True(r.Ok, r.Message);
        // Never more than a few blocks away from the tree: no walk out through the far exit.
        double far = s.Got<PlayerPositionPacket>().Select(p => Math.Abs(p.X - 8.5) + Math.Abs(p.Z - 8.5))
            .Concat(s.Got<PlayerPositionLookPacket>().Select(p => Math.Abs(p.X - 8.5) + Math.Abs(p.Z - 8.5))).DefaultIfEmpty(0).Max();
        Assert.True(far <= 12, $"went {far:0.#} blocks from the tree");
        lock (_lines) Assert.Contains(_lines, l => l.StartsWith("goto (chop dig): route", StringComparison.Ordinal));
    }

    [Fact]
    public void A_detour_is_three_times_the_straight_line_or_a_dip_below_both_ends()
    {
        static PathStep S(int x, int y, int z) => new(x, y, z, MoveKind.Walk, y, 4.6, []);
        var start = new Vec3d(0.5, 64, 0.5);
        var straight = RouteStats.Of(start, Enumerable.Range(1, 10).Select(x => S(x, 64, 0)).ToList());
        Assert.False(straight.IsDetour);
        Assert.Equal(10, straight.Length, 1);
        // Out 15 east, 3 south, 15 back west: 33 walked for 3 straight.
        var round = Enumerable.Range(1, 15).Select(x => S(x, 64, 0)).Concat(Enumerable.Range(1, 3).Select(z => S(15, 64, z)))
            .Concat(Enumerable.Range(0, 15).Select(i => S(14 - i, 64, 3))).ToList();
        Assert.True(RouteStats.Of(start, round).IsDetour);
        // Down 6 through a cave and back up: a dip below both ends.
        var dip = new List<PathStep>();
        for (int i = 1; i <= 6; i++) dip.Add(S(i, 64 - i, 0));
        for (int i = 1; i <= 6; i++) dip.Add(S(6 + i, 58 + i, 0));
        var shape = RouteStats.Of(start, dip);
        Assert.Equal(6, shape.Dip);
        Assert.True(shape.IsDetour);
    }
}
