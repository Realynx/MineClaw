using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using System.Reflection;

namespace MineClaw.Client.Tests;

public sealed class ScaffoldReserveTests
{
    private static void Floor(FakeServer s)
    {
        for (int x = 2; x <= 14; x++)
            for (int z = 2; z <= 14; z++) s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.SpawnAt = (4.5, 64, 8.5);
        s.ConsumeOnPlace = true;
        s.CraftingEnabled = true;
    }

    private static GotoOptions Options(params (int Id, int Count)[] keep) => new()
    {
        AllowPlace = true, AllowBreak = false, BreakOnly = (_, _) => false,
        ScaffoldBudget = 2, ScaffoldReserve = keep.ToDictionary(k => k.Id, k => k.Count),
        AllowPartial = false, Parkour = ParkourMode.Off, Timeout = TimeSpan.FromSeconds(15),
    };

    private static void NoExcavation(FakeServer s) => Assert.DoesNotContain(s.Received,
        p => p is PlayerDiggingPacket { Status: 0 or 2 });

    [Theory]
    [InlineData(Blocks.Dirt)]
    [InlineData(Blocks.Planks)]
    public async Task A_two_block_escape_uses_spare_material_and_keeps_the_permanent_build_stock(int permanent)
    {
        using var normal = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.Inventory[36] = Harness.Stack(permanent, 8);
            srv.Inventory[37] = Harness.Stack(Blocks.Cobblestone, 2);
        }, release1122: true);
        await using var _ = c;
        using var __ = s;
        c.DigThroughDetours = true; // the route's explicit veto also applies to automatic detour shortcuts
        var goal = new GoalBlock(8, 66, 8);
        var options = Options((permanent, 8));
        var walking = await c.GotoAsync(goal, options with { AllowPlace = false });
        Assert.False(walking.Ok);
        Assert.Empty(c.TemporaryBlocks());
        var result = await c.GotoAsync(goal, options);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(8, c.World.Inventory.CountItem(permanent));
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Cobblestone));
        var placed = c.TemporaryBlocks();
        Assert.Equal(2, placed.Count);
        Assert.All(placed, p => Assert.Equal(Blocks.Cobblestone, s.Blocks[(p.Pos.X, p.Pos.Y, p.Pos.Z)]));
        NoExcavation(s);
    }

    [Fact]
    public async Task A_route_can_spend_the_surplus_of_a_reserved_material_and_stops_at_the_reserve()
    {
        using var normal = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv => { Floor(srv); srv.Inventory[36] = Harness.Stack(Blocks.Dirt, 10); }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var result = await c.GotoAsync(new GoalBlock(8, 66, 8), Options((Blocks.Dirt, 8)));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(8, c.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Equal(2, c.TemporaryBlocks().Count);
        NoExcavation(s);
    }

    [Fact]
    public async Task Releasing_a_reserve_retries_a_previously_unaffordable_route()
    {
        using var normal = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv => { Floor(srv); srv.Inventory[36] = Harness.Stack(Blocks.Planks, 2); }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var goal = new GoalBlock(8, 66, 8);
        var refused = await c.GotoAsync(goal, Options((Blocks.Planks, 2)));
        Assert.False(refused.Ok);
        Assert.Equal(2, c.World.Inventory.CountItem(Blocks.Planks));
        Assert.Empty(c.TemporaryBlocks());
        var retried = await c.GotoAsync(goal, Options());
        Assert.True(retried.Ok, retried.Message);
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Planks));
        NoExcavation(s);
    }

    [Fact]
    public async Task Reserved_logs_are_not_crafted_to_make_scaffolding()
    {
        using var normal = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv => { Floor(srv); srv.Inventory[36] = Harness.Stack(Blocks.Log, 2); }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var result = await c.GotoAsync(new GoalBlock(8, 66, 8), Options((Blocks.Log, 2)));
        Assert.False(result.Ok);
        Assert.Equal(2, c.World.Inventory.CountItem(Blocks.Log));
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Planks));
        Assert.DoesNotContain(s.Received, p => p is WindowClickPacket or PlayerBlockPlacementPacket);
        NoExcavation(s);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stuck_recovery_respects_the_routes_explicit_dig_veto(bool denyDig)
    {
        using var normal = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.Blocks[(5, 64, 8)] = Blocks.Dirt;
        }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var obstruction = new BlockPos(5, 64, 8);
        c.World.Placed.Note(obstruction, c.World.Dimension, Blocks.Dirt, "maid_chan", DateTimeOffset.UtcNow);
        // Exercise the same fallback the second stuck replan invokes, without timing a simulated collision race.
        var fallback = typeof(MinecraftClient).GetMethod("MineOwnObstructionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<StuckProbe.Obstruction> hits = [new(obstruction, false)];
        Func<int, BlockPos, bool>? veto = denyDig ? (_, _) => false : null;
        var result = await (Task<string?>)fallback.Invoke(c, [hits, veto, CancellationToken.None])!;
        if (denyDig)
        {
            Assert.Null(result);
            Assert.Equal(Blocks.Dirt, c.World.GetBlock(obstruction));
            NoExcavation(s);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(0, c.World.GetBlock(obstruction));
            Assert.Contains(s.Received, p => p is PlayerDiggingPacket { Status: 0 or 2 });
        }
    }

    [Fact]
    public async Task Converting_spare_logs_keeps_both_the_log_and_new_plank_reserves()
    {
        using var normal = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv => { Floor(srv); srv.Inventory[36] = Harness.Stack(Blocks.Log, 2); }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var result = await c.GotoAsync(new GoalBlock(8, 66, 8), Options((Blocks.Log, 1), (Blocks.Planks, 2)));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, c.World.Inventory.CountItem(Blocks.Log));
        Assert.Equal(2, c.World.Inventory.CountItem(Blocks.Planks));
        Assert.Equal(2, c.TemporaryBlocks().Count);
        Assert.All(c.TemporaryBlocks(), p => Assert.Equal(Blocks.Planks, s.Blocks[(p.Pos.X, p.Pos.Y, p.Pos.Z)]));
        NoExcavation(s);
    }
}
