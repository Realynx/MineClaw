using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Fail-fast on edits the server undoes (spawn protection), body-language moves, the playful punch's consent
/// rules, and the goto "no route" reason.</summary>
public class BodyAndProtectionTests
{
    private static void Floor(FakeServer s)
    {
        for (int x = 2; x <= 14; x++)
        for (int z = 2; z <= 14; z++)
            s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.SpawnAt = (4.5, 64, 8.5);
    }

    [Fact]
    public async Task Undone_digs_near_spawn_abort_the_macro_and_two_independent_ones_learn_the_region()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.BreakOnFinish = false; // the server ignores the digs (spawn protection, not op)
            foreach (var x in new[] { 9, 10, 11 }) srv.Blocks[(x, 64, 10)] = Blocks.Gravel;
        });
        await using var _ = c;
        using var __ = s;
        LearnedProtectedRegion? seen = null;
        c.ProtectedRegionChanged += r => seen = r;
        var r = await c.MineAsync("gravel", 3);
        Assert.False(r.Ok);
        Assert.Equal("server undid my digs within 16 of spawn (spawn protection? need op)", r.Message);
        Assert.Equal(1, s.Got<PlayerDiggingPacket>().Count(d => d.Status == PlayerDiggingPacket.StatusStarted));
        await Task.Delay(100);
        Assert.Null(c.ProtectedRegion); // one undone dig is lag/a race, not yet spawn protection
        Assert.False((await c.MineAsync("gravel", 3)).Ok); // a second, independent attempt (separate action)
        Assert.True(await Harness.WaitAsync(() => seen is not null));
        Assert.Equal(new BlockPos(8, 64, 8), seen!.Center);
        Assert.Equal(16, seen.Radius);
        Assert.Same(seen, c.ProtectedRegion);

        // Up front now: no dig is even tried, and the reason says why.
        int before = s.Got<PlayerDiggingPacket>().Count();
        var again = await c.MineAsync("gravel", 3);
        Assert.False(again.Ok);
        Assert.Contains("spawn protection", again.Message);
        var dig = await c.DigBlockAsync(10, 64, 10);
        Assert.Contains("spawn protection", dig.Message);
        Assert.Equal(before, s.Got<PlayerDiggingPacket>().Count());
    }

    [Fact]
    public async Task A_later_success_inside_the_region_clears_it()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.BreakOnFinish = false;
            srv.Blocks[(9, 64, 10)] = Blocks.Dirt;
            srv.Blocks[(10, 64, 10)] = Blocks.Dirt;
        }, o => o with { ProtectedRegionRetry = TimeSpan.Zero });
        await using var _ = c;
        using var __ = s;
        int events = 0;
        LearnedProtectedRegion? last = null;
        c.ProtectedRegionChanged += r => { events++; last = r; };
        Assert.False((await c.DigBlockAsync(9, 64, 10)).Ok);
        Assert.Null(c.ProtectedRegion);
        Assert.False((await c.DigBlockAsync(9, 64, 10)).Ok); // second, separate attempt
        Assert.NotNull(c.ProtectedRegion);
        s.BreakOnFinish = true; // e.g. the account was opped
        var ok = await c.DigBlockAsync(10, 64, 10);
        Assert.True(ok.Ok, ok.ToString());
        Assert.Null(c.ProtectedRegion);
        Assert.True(await Harness.WaitAsync(() => events == 2));
        Assert.Null(last);
    }

    [Fact]
    public async Task Undone_dig_far_from_spawn_still_fails_fast_without_learning_spawn_protection()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.BreakOnFinish = false;
            srv.Blocks[(10, 64, 10)] = Blocks.Gravel;
            srv.Blocks[(11, 64, 10)] = Blocks.Gravel;
        });
        await using var _ = c;
        using var __ = s;
        s.Send(new SpawnPositionPacket { X = 200, Y = 64, Z = 200 });
        Assert.True(await Harness.WaitAsync(() => c.World.Spawn.X == 200));
        var r = await c.MineAsync("gravel", 2);
        Assert.False(r.Ok);
        Assert.Contains("protected area?", r.Message);
        Assert.Null(c.ProtectedRegion);
    }

    [Fact]
    public async Task Jump_spin_wave_crouch()
    {
        var (c, s) = await Harness.StartAsync(Floor);
        await using var _ = c;
        using var __ = s;
        var j = await c.JumpAsync(2);
        Assert.True(j.Ok, j.ToString());
        Assert.Equal(2, j.Count);
        Assert.True((await c.SpinAsync(1)).Ok);
        Assert.True((await c.ActAsync("wave", times: 2)).Ok);
        Assert.Equal(2, s.Got<AnimationPacket>().Count());
        Assert.True((await c.ActAsync("crouch")).Ok);
        Assert.True(c.IsCrouching);
        Assert.True(await Harness.WaitAsync(() => s.Got<EntityActionPacket>().Any(a => a.Action == 1)));
        Assert.True((await c.ActAsync("crouch", times: 0)).Ok);
        Assert.True(await Harness.WaitAsync(() => s.Got<EntityActionPacket>().Any(a => a.Action == 2)));
        Assert.False((await c.ActAsync("moonwalk")).Ok);
        Assert.InRange(c.World.Player.Position.X, 4, 5); // jumped and spun in place
    }

    private static void SpawnTester(FakeServer s, double x, double z) =>
        s.Send(new NamedEntitySpawnPacket { EntityId = 77, PlayerName = "tester", X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(z) });

    [Fact]
    public async Task Punch_needs_that_players_request_and_stops_at_three()
    {
        var (c, s) = await Harness.StartAsync(Floor);
        await using var _ = c;
        using var __ = s;
        SpawnTester(s, 6.5, 8.5);
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.FindPlayer("tester") is not null));
        var no = await c.PlayfulPunchAsync("tester");
        Assert.False(no.Ok);
        Assert.Contains("asks me", no.Message);
        c.NotePunchRequest("someone_else");
        Assert.False((await c.PlayfulPunchAsync("tester")).Ok, "another player's request doesn't count");

        s.Send(new ChatPacket("<tester> haha punch me!"));
        Assert.True(await Harness.WaitAsync(() => c.PunchHealthEstimate("tester") > 0));
        await Task.Delay(50);
        for (int i = 0; i < 3; i++)
        {
            var r = await c.PlayfulPunchAsync("tester");
            Assert.True(r.Ok, r.ToString());
        }
        var fourth = await c.PlayfulPunchAsync("tester");
        Assert.False(fourth.Ok);
        Assert.Contains("3 punches", fourth.Message);
        var hits = s.Got<UseEntityPacket>().ToList();
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.True(h.LeftClick && h.TargetEntityId == 77));
        Assert.Equal(0, c.RefusedAttacks);
        Assert.True(c.World.Inventory.HeldItem(c.World.Player.HeldSlot).IsEmpty, "bare-handed");
    }

    [Fact]
    public async Task Punch_refused_when_hurt_or_near_an_edge()
    {
        var (c, s) = await Harness.StartAsync(Floor);
        await using var _ = c;
        using var __ = s;
        SpawnTester(s, 6.5, 8.5);
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.FindPlayer("tester") is not null));
        for (int i = 0; i < 3; i++) s.Send(new EntityStatusPacket { EntityId = 77, Status = 2 }); // hurt by something else
        Assert.True(await Harness.WaitAsync(() => c.PunchHealthEstimate("tester") <= 10));
        c.NotePunchRequest("tester");
        var hurt = await c.PlayfulPunchAsync("tester");
        Assert.False(hurt.Ok);
        Assert.Contains("looks hurt", hurt.Message);

        s.Send(new EntityTeleportPacket { EntityId = 77, X = FixedPoint.ToAbsolute(2.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(8.5) });
        s.Send(new NamedEntitySpawnPacket { EntityId = 78, PlayerName = "rim", X = FixedPoint.ToAbsolute(2.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(9.5) });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.FindPlayer("rim") is not null));
        c.NotePunchRequest("rim");
        var edge = await c.PlayfulPunchAsync("rim");
        Assert.False(edge.Ok);
        Assert.Contains("edge", edge.Message);
        Assert.Empty(s.Got<UseEntityPacket>());
    }

    [Fact]
    public async Task Goto_above_with_no_route_says_how_many_blocks_a_pillar_needs()
    {
        var (c, s) = await Harness.StartAsync(Floor);
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(new GoalBlock(8, 67, 8), GotoOptions.Default with { AllowPartial = false });
        Assert.False(r.Ok);
        Assert.Contains("3 blocks up", r.Message);
        Assert.Contains("have 0", r.Message);
        Assert.Matches(@"need \d+ blocks to pillar", r.Message);
    }

    [Fact]
    public async Task Goto_above_with_blocks_suggests_build_mode()
    {
        var (c, s) = await Harness.StartAsync(srv => { Floor(srv); srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 16); });
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(new GoalBlock(8, 67, 8), GotoOptions.Default with { AllowPartial = false });
        Assert.False(r.Ok);
        Assert.Contains("build mode", r.Message);
    }
}
