using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-27 (14:17, 14:33, 15:0x): clones outside the plank house (x 7..16, z -12..-4, door pair 12/13,64,-4) stopped at
/// 14.6,64,-2.5 / 15.5,64,-2.4 "within reach" of chest 14,64,-5 behind the wall and failed "can't reach chest at 14,64,-5";
/// the worker reported furnace 8,64,-8 "walled in" and chest 15,68,-8 (upper floor) unreachable. The reach goal was distance
/// only, so it was satisfied outside the wall; the open now walks to a spot the block is visible from (through the doors).
/// World: the remembered island from the live db (tests/fixtures/house-exit/island.txt, same house as the live 10-27 db).
/// </summary>
public class ContainerReachTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    private Func<ClientOptions, ClientOptions> Logged => o => o with
    {
        Log = m => { _lines.Enqueue(m); try { log.WriteLine(m); } catch (InvalidOperationException) { } },
        IsProtected = HouseExitTests.HouseProtected,
    };

    /// <summary>Opened with a real line of sight from where the bot stands (inside, or in/at the doorway looking through
    /// it), never the unseen last-resort click.</summary>
    private void AssertSeen(MinecraftClient c, BlockPos target)
    {
        Assert.DoesNotContain(_lines, l => l.Contains("clicking it unseen"));
        Assert.True(Aiming.FindDigAim(c.World, c.World.Player.EyePosition, target, c.Options.Reach) is not null,
            $"no line of sight to {target} from {c.World.Player.Position}");
    }

    private static ItemStack[] Slots(int n) => Enumerable.Repeat(ItemStack.Empty, n).ToArray();

    private static void Setup(FakeServer s, (double X, double Z) spawn, int? door12Meta = null, int? door13Meta = null)
    {
        HouseExitTests.LoadIsland(s);
        if (door12Meta is { } m12) { s.Metas[(12, 64, -4)] = m12; s.Metas[(12, 65, -4)] = m12 | 8; }
        if (door13Meta is { } m13) { s.Metas[(13, 64, -4)] = m13; s.Metas[(13, 65, -4)] = m13 | 8; }
        s.SpawnAt = (spawn.X, 64, spawn.Z);
        var chest = Slots(27);
        chest[0] = Harness.Stack(Items.StoneAxe, 1);
        s.Chests[(14, 64, -5)] = chest;
        s.Chests[(15, 64, -5)] = Slots(27);
        s.Chests[(15, 68, -8)] = Slots(27);
        s.Furnaces[(8, 64, -8)] = Slots(3);
    }

    private static bool Inside(Vec3d p) => p.X > 8 && p.X < 16 && p.Z > -12 && p.Z < -4;

    // Door states seen live: fixture snapshot (12 closed=3, 13 paired=6), live db now (12 open=7), and both swung the other way.
    public static TheoryData<double, double, int?, int?> OutsideStarts => new()
    {
        { 9.5, 2.5, null, null },   // 67's spawn, doors as in the fixture
        { 14.6, -2.5, null, null }, // where 67 stopped
        { 15.5, -2.4, 7, null },    // where 69 stopped, door 12 open as in the live db
        { 15.5, -2.4, 3, 2 },       // both shut, panels the other way (13 unpaired: open bit clear)
        { 14.6, -2.5, 7, 2 },       // 12 open, 13 shut
    };

    [Theory]
    [MemberData(nameof(OutsideStarts))]
    public async Task Chest_behind_the_house_wall_opens_from_outside_via_the_doors(double x, double z, int? m12, int? m13)
    {
        var (c, s) = await Harness.StartAsync(srv => Setup(srv, (x, z), m12, m13), Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.WithdrawAsync(Items.StoneAxe, 1, new BlockPos(14, 64, -5));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(1, c.World.Inventory.CountItem(Items.StoneAxe));
        // Walked to a spot that sees the chest (through the door pair); no click through the wall, nothing dug.
        AssertSeen(c, new BlockPos(14, 64, -5));
        Assert.Empty(s.Dug);
    }

    [Fact]
    public async Task Goto_then_withdraw_chain_from_67s_spawn_ends_inside()
    {
        // The `do goto>withdraw` chain: goto stopped next to the chest's coordinates (outside), then withdraw.
        var (c, s) = await Harness.StartAsync(srv => Setup(srv, (9.5, 2.5)), Logged);
        await using var _ = c;
        using var __ = s;
        var g = await c.GotoAsync(new GoalNear(14, 64, -5, 3), GotoOptions.Default);
        Assert.True(g.Ok, g.ToString());
        var r = await c.WithdrawAsync(Items.StoneAxe, 1, new BlockPos(14, 64, -5));
        Assert.True(r.Ok, r.ToString());
        AssertSeen(c, new BlockPos(14, 64, -5));
    }

    [Theory]
    [InlineData(3.5, 4.5)] // the generator stand the worker was mining from
    [InlineData(6.5, -7.5)] // right outside the west wall, "in reach" of the furnace through it
    public async Task Furnace_by_the_west_wall_opens_from_inside(double x, double z)
    {
        var (c, s) = await Harness.StartAsync(srv => Setup(srv, (x, z)), Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.OpenContainerAsync(8, 64, -8);
        Assert.True(r.Ok, r.ToString());
        Assert.True(Inside(c.World.Player.Position), $"opened from {c.World.Player.Position}");
        AssertSeen(c, new BlockPos(8, 64, -8));
    }

    [Fact]
    public async Task Upper_floor_chest_opens()
    {
        var (c, s) = await Harness.StartAsync(srv => Setup(srv, (13.5, -2.5)), Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.OpenContainerAsync(15, 68, -8);
        Assert.True(r.Ok, r.ToString());
        Assert.Contains(s.Got<PlayerBlockPlacementPacket>(), p => p is { X: 15, Y: 68, Z: -8 });
        AssertSeen(c, new BlockPos(15, 68, -8));
    }

    [Fact]
    public async Task Truly_walled_in_chest_says_what_is_in_the_way()
    {
        // A chest sealed in a cobblestone block, farther than reach from every standable cell: the line names the wall.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int x = 0; x <= 15; x++)
            for (int z = 0; z <= 15; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Stone;
            for (int x = 7; x <= 15; x++)
            for (int z = 7; z <= 15; z++)
            for (int y = 64; y <= 67; y++)
                srv.Blocks[(x, y, z)] = Blocks.Cobblestone;
            srv.Blocks[(11, 64, 11)] = Blocks.Chest;
            srv.Chests[(11, 64, 11)] = Slots(27);
            srv.SpawnAt = (2.5, 64, 2.5);
        }, o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } });
        await using var _ = c;
        using var __ = s;
        var r = await c.OpenContainerAsync(11, 64, 11);
        Assert.False(r.Ok);
        Assert.Contains("can't reach chest at 11,64,11", r.Message);
        Assert.Contains("cobblestone at", r.Message);
        Assert.Contains("blocks the view", r.Message);
    }
}
