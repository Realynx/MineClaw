using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Regressions from the 2026-10-27 night log review: B12 (generator drops/stand), B21 (floor digs), L6 (chop reports).</summary>
public class NightLogHarvestTests
{
    private static Func<ClientOptions, ClientOptions> Capture(ConcurrentQueue<string> log) => o => o with { Log = log.Enqueue };

    [Fact]
    public async Task B12_generator_drops_next_to_lava_are_a_hazard_not_the_void_and_logged_once()
    {
        // Live 10-27: 35 "collect: not chasing item 4:0 x1 at 6.3,64.1,7.7: over or next to the void" in main for drops lying
        // in the generator's lava/water channel; the same drop was logged on every pass.
        var log = new ConcurrentQueue<string>();
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int x = 11; x <= 14; x++)
            for (int z = 6; z <= 10; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Stone;
            srv.Blocks[(12, 64, 7)] = Blocks.Lava;
            srv.Blocks[(12, 64, 10)] = Blocks.Lava;
        }, Capture(log));
        await using var _ = c;
        using var __ = s;
        s.SpawnDrop(12.5, 64.1, 8.5, new ItemStack(Blocks.Cobblestone, 1, 0));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count > 0));

        var r = await c.CollectDropsAsync(8);
        var r2 = await c.CollectDropsAsync(8);
        Assert.False(r.Ok);
        Assert.Contains("left in lava/water", r.Message);
        Assert.DoesNotContain("void", r.Message);
        Assert.False(r2.Ok);
        var lines = log.Where(l => l.StartsWith("collect: not chasing", StringComparison.Ordinal)).ToList();
        Assert.Single(lines);
        Assert.Contains("in lava/water (hazard)", lines[0]);
    }

    [Fact]
    public async Task B12_harvest_from_the_taught_stand_spot_stays_on_it()
    {
        // Live 10-27 demo: "stood at 7,63,8 facing cobblestone; punched cobblestone at 6,64,8": the stand is in the hole next
        // to the cell, closer to the lava than the bot's own rule allows. The harvester walked off to its own spot every dig.
        var log = new ConcurrentQueue<string>();
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks.Remove((9, 63, 8));          // the hole the player stood in
            srv.Blocks[(9, 62, 8)] = Blocks.Stone;
            srv.Blocks[(10, 64, 8)] = Blocks.Cobblestone; // the cell
            srv.Regenerate[(10, 64, 8)] = Blocks.Cobblestone;
            srv.Blocks[(11, 64, 8)] = Blocks.Lava;
            srv.Blocks[(10, 64, 9)] = Blocks.Water;
            srv.Blocks[(11, 63, 8)] = Blocks.Stone;
            srv.Blocks[(10, 63, 8)] = Blocks.Stone;
            srv.DropOnDig = true;
            srv.Inventory[36] = Harness.Stack(Items.WoodenPickaxe, 1);
        }, Capture(log));
        await using var _ = c;
        using var __ = s;
        var stand = new BlockPos(9, 63, 8);
        var r = await c.HarvestGeneratorAsync(10, 64, 8, 3, stand);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(3, s.Got<PlayerDiggingPacket>().Count(d => d.Status == PlayerDiggingPacket.StatusFinished && d.X == 10 && d.Y == 64 && d.Z == 8));
        var p = c.World.Player.Position;
        Assert.Equal(stand, new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y + 1e-6), (int)Math.Floor(p.Z)));
        Assert.DoesNotContain(log, l => l.Contains("re-planning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task B21_harvest_refuses_when_the_taught_stand_spot_is_unreachable()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(10, 64, 8)] = Blocks.Cobblestone;
            srv.Regenerate[(10, 64, 8)] = Blocks.Cobblestone;
            srv.Inventory[36] = Harness.Stack(Items.WoodenPickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.HarvestGeneratorAsync(10, 64, 8, 3, new BlockPos(12, 70, 8)); // floating: nothing to stand on
        Assert.False(r.Ok);
        Assert.Contains("can't get onto the taught stand spot 12,70,8", r.Message);
        Assert.DoesNotContain(s.Got<PlayerDiggingPacket>(), d => d.Status == PlayerDiggingPacket.StatusStarted);
    }

    [Fact]
    public async Task B21_free_standing_digs_never_take_the_walkway_floor_next_to_the_void()
    {
        // Live 10-27 07:21: script cobble_b dug the floor (drops at 9.4,63.5,7.5 and 7.4,62.5,8.6), then `look` said
        // "void E2 S1 (EDGE)". The harness platform is the same: a 5x5 floor with the void 2 away.
        var (c, s) = await Harness.StartAsync(srv => srv.Blocks[(10, 64, 7)] = Blocks.Cobblestone);
        await using var _ = c;
        using var __ = s;
        var floor = await c.DigBlockAsync(9, 63, 7);
        Assert.False(floor.Ok);
        Assert.Contains("floor of a walkway with the void", floor.Message);
        var mine = await c.MineAsync("stone", 2);
        Assert.False(mine.Ok);
        Assert.Contains("no more reachable stone", mine.Message);
        Assert.DoesNotContain(s.Got<PlayerDiggingPacket>(), d => d.Y == 63);
        Assert.True((await c.DigBlockAsync(10, 64, 7)).Ok); // a block standing on the floor is fine
    }
}

public class NightLogChopTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private Func<ClientOptions, ClientOptions> Logged => o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } };

    [Fact]
    public async Task L6_replant_blocked_with_no_sapling_does_not_say_sapling_kept()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 4);
            TreeWorld.Drops(srv, saplingEvery: 0);
            srv.SpawnAt = (5.5, 64, 5.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        c.World.BlockChanged += ch =>
        {
            if (ch.Pos == new BlockPos(8, 67, 8) && ch.NewId == 0) ThreadPool.QueueUserWorkItem(__ => s.SetBlock(8, 64, 8, Blocks.Cobblestone));
        };
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Assert.False(r.Ok);
        Assert.StartsWith("replant blocked: ", r.Message);
        Assert.DoesNotContain("sapling kept", r.Message);
        Assert.Contains("0 saplings, no sapling to plant either)", r.Message);
    }

    [Fact]
    public async Task L6_chopping_stops_after_two_trees_in_a_row_were_not_replanted()
    {
        // Live2 01:03: the worker chopped 10 trees and replanted none without stopping.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 6);
            TreeWorld.Tree(srv, 4, 4, 4);
            TreeWorld.Tree(srv, 12, 12, 4);
            TreeWorld.Tree(srv, 4, 12, 4);
            TreeWorld.Drops(srv, saplingEvery: 0);
            srv.SpawnAt = (8.5, 64, 8.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var first = await c.ChopTreeAsync(new BlockPos(4, 64, 4), options: TreeWorld.Fast);
        Assert.Contains("NOT replanted", first.Message);
        var second = await c.ChopTreeAsync(new BlockPos(12, 64, 12), options: TreeWorld.Fast);
        Assert.Contains("NOT replanted", second.Message);
        int digs = s.Dug.Count;
        var third = await c.ChopTreeAsync(new BlockPos(4, 64, 12), options: TreeWorld.Fast);
        Assert.False(third.Ok);
        Assert.StartsWith("stopping: the last 2 trees I chopped weren't replanted", third.Message);
        Assert.Equal(digs, s.Dug.Count);
        Assert.Equal(Blocks.Log, s.Blocks.GetValueOrDefault((4, 64, 12)));
    }
}
