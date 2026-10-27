using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-27 21:12-21:17 at the real three-cell gen (tests/fixtures/live-gen/gen.txt, the live db's remembered blocks):
/// "harvest yields 62% at 4,64,6 (8 dug, 5 cobble); 6,64,8 unreachable from stand 3,64,4", then "4,64,6 isn't regenerating
/// cobble for me (yield 0 %)" while the player said "the gen looks fine" and was mining the same cell. Evidence in the log:
/// "collect: not chasing item 4:0 x1 at 4.1,63.6,5.7 / 5.1,64.1,6.1: in lava/water (hazard)", i.e. drops lying on the slab
/// beside the cell or washed into the water were never counted, and the yield guard retracted all three real cells.
/// </summary>
public class LiveGenHarvestTests
{
    internal static readonly BlockPos West = new(4, 64, 6), Middle = new(5, 64, 7), East = new(6, 64, 8);
    /// <summary>Fox's demo stand: "(relative to 4,63,6) stood at -1,+1,-1" = on the slab at 3,64,5.</summary>
    internal static readonly BlockPos FoxStand = new(3, 64, 5);

    internal static string Fixture()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "live-gen", "gen.txt");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tests/fixtures/live-gen/gen.txt");
    }

    /// <summary>The live gen from the db: cobble floor y63, slabs around, lava 4,64,7/4,64,8/5,64,8, water 5..6,64..65,6..7.</summary>
    internal static void LiveGen(FakeServer srv)
    {
        srv.Blocks.Clear();
        srv.Metas.Clear();
        foreach (var line in File.ReadLines(Fixture()))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            srv.Blocks[(f[0], f[1], f[2])] = f[3];
            if (f[4] != 0) srv.Metas[(f[0], f[1], f[2])] = f[4];
        }
        foreach (var c in new[] { West, Middle, East }) srv.Regenerate[(c.X, c.Y, c.Z)] = Blocks.Cobblestone;
        srv.SpawnAt = (FoxStand.X + 0.5, FoxStand.Y + 0.5, FoxStand.Z + 0.5);
        srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        srv.DropOnDig = true;
    }

    [Fact]
    public void Fixture_is_the_live_gen()
    {
        using var srv = new FakeServer();
        LiveGen(srv);
        Assert.Equal(Blocks.Cobblestone, srv.Blocks[(4, 64, 6)]);
        Assert.Equal(Blocks.Cobblestone, srv.Blocks[(5, 64, 7)]);
        Assert.Equal(Blocks.Cobblestone, srv.Blocks[(6, 64, 8)]);
        Assert.True(BlockShapes.IsLava(srv.Blocks[(4, 64, 7)]));
        Assert.True(BlockShapes.IsWater(srv.Blocks[(5, 64, 6)]));
        Assert.Equal(Blocks.Slab, srv.Blocks[(3, 64, 5)]);
        Assert.Equal(Blocks.Slab, srv.Blocks[(4, 63, 5)]);
    }

    [Fact]
    public async Task Drops_washed_into_the_water_never_blame_the_cell()
    {
        // Every drop lands deep in the water channel (live 21:41:21 "6,64.1,6.9"), out of reach: the old guard saw 0 cobble from
        // 8 digs and "proved" the real cell wrong. The drops did appear, so the cell is regenerating.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            LiveGen(srv);
            srv.DigDrop = (_, _, _) => { srv.SpawnDrop(6.0, 64.1, 6.9, Harness.Stack(Blocks.Cobblestone, 1)); return null; };
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.HarvestGeneratorAsync(West.X, West.Y, West.Z, MinecraftClient.YieldWindow, FoxStand);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(MinecraftClient.YieldWindow, r.Count);
        Assert.DoesNotContain("isn't regenerating", r.Message);
        Assert.Contains("washed into the gen's water", r.Message);
    }

    [Fact]
    public async Task A_drop_lying_on_the_slab_beside_the_cell_is_picked_up()
    {
        // Live 21:13 "not chasing item 4:0 x1 at 4.1,63.6,5.7: in lava/water (hazard)": it lay on the slab at 4,63,5.
        var (c, s) = await Harness.StartAsync(LiveGen);
        await using var _ = c;
        using var __ = s;
        // 21:40:53-58 (the 0 % run at 5,64,7): "not chasing item 4:0 x1 at 4.8,63.6,5.7 / 4.1,63.6,5.7: in lava/water (hazard)".
        s.SpawnDrop(4.1, 63.6, 5.7, Harness.Stack(Blocks.Cobblestone, 1));
        s.SpawnDrop(4.8, 63.6, 5.7, Harness.Stack(Blocks.Cobblestone, 1));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count >= 2));
        var r = await c.CollectDropsAsync(6);
        Assert.True(r.Ok, r.Message);
        Assert.DoesNotContain("hazard", r.Message);
        Assert.Equal(2, c.World.Inventory.CountItem(Blocks.Cobblestone));
    }

    [Fact]
    public async Task Harvest_rotates_through_the_cells_in_reach_instead_of_hammering_one()
    {
        var (c, s) = await Harness.StartAsync(LiveGen);
        await using var _ = c;
        using var __ = s;
        var r = await c.HarvestGeneratorAsync([West, Middle, East], 9, FoxStand);
        Assert.True(r.Ok, r.Message);
        var dug = s.Dug.Select(d => new BlockPos(d.X, d.Y, d.Z)).ToList();
        Assert.All(dug, d => Assert.Contains(d, new[] { West, Middle, East }));
        Assert.True(dug.Distinct().Count() >= 2, string.Join(" ", dug));
        // Never twice in a row on one cell while another cell is ready.
        Assert.True(dug.Zip(dug.Skip(1)).Count(t => t.First == t.Second) <= 1, string.Join(" ", dug));
        Assert.Contains("across", r.Message);
    }

    [Fact]
    public async Task A_player_mining_the_same_cell_is_shared_use_not_a_dead_gen()
    {
        // Live 21:16 "you were mining it too, we were probably racing for the same block": Fox digs the cell (block changes we
        // didn't cause) and our digs give nothing. That never proves the cell wrong.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            LiveGen(srv);
            srv.DigDrop = (_, _, _) => null; // Fox grabs everything
        });
        await using var _ = c;
        using var __ = s;
        s.Send(new NamedEntitySpawnPacket { EntityId = 77, PlayerName = "PoofimaFox", X = FixedPoint.ToAbsolute(4.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(4.5) });
        using var stop = new CancellationTokenSource();
        var fox = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(40);
                if (s.Blocks.GetValueOrDefault((4, 64, 6)) != Blocks.Cobblestone) continue;
                s.Send(new AnimationPacket { EntityId = 77, Animation = 1 }); // Fox swings at the cell
                s.Blocks.Remove((4, 64, 6));
                s.Send(new BlockChangePacket { X = 4, Y = 64, Z = 6, BlockId = 0 });
                await Task.Delay(60);
                s.Blocks[(4, 64, 6)] = Blocks.Cobblestone;
                s.Send(new BlockChangePacket { X = 4, Y = 64, Z = 6, BlockId = (byte)Blocks.Cobblestone });
            }
        });
        var r = await c.HarvestGeneratorAsync(West.X, West.Y, West.Z, MinecraftClient.YieldWindow, FoxStand);
        stop.Cancel();
        await fox;
        Assert.DoesNotContain("isn't regenerating", r.Message);
        Assert.Contains("PoofimaFox is mining this gen too", r.Message);
    }

    [Fact]
    public async Task A_cell_that_really_stops_names_the_cause()
    {
        // The lava next to the cell turned to obsidian: the cell stays air. Say exactly that.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            LiveGen(srv);
            srv.Regenerate.Remove((4, 64, 6));
            srv.Blocks[(4, 64, 7)] = Blocks.Obsidian;
            srv.Blocks[(4, 65, 7)] = Blocks.Obsidian;
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.HarvestGeneratorAsync([West], 3, FoxStand);
        Assert.Contains("stopped producing", r.Message);
        Assert.Contains("obsidian at 4,64,7", r.Message);
        Assert.Equal(1, s.Dug.Count);
    }

    [Fact]
    public async Task An_enclosed_cell_gets_an_exact_reason()
    {
        // A cell walled in on every side with no standable spot in reach: say so (and how far the nearest spot is).
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks.Clear();
            for (int x = 0; x <= 24; x++)
            for (int z = 0; z <= 24; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Cobblestone;
            for (int x = 6; x <= 18; x++)
            for (int y = 64; y <= 70; y++)
            for (int z = 6; z <= 18; z++)
                srv.Blocks[(x, y, z)] = Blocks.Cobblestone;
            srv.Blocks[(12, 67, 12)] = Blocks.Cobblestone; // the cell, deep inside
            srv.Blocks[(13, 67, 12)] = Blocks.Lava;
            srv.Blocks[(12, 67, 13)] = Blocks.Water;
            srv.SpawnAt = (3.5, 64, 3.5);
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.HarvestGeneratorAsync(12, 67, 12, 4);
        Assert.False(r.Ok);
        Assert.Contains("12,67,12 is enclosed", r.Message);
        Assert.Contains("nearest standable spot", r.Message);
        Assert.Empty(s.Dug);
    }

    /// <summary>After a dig at <paramref name="cell"/> the server shows flowing water there; cobble only after <paramref name="cobbleAfterMs"/>
    /// (negative = never: the lava side is blocked).</summary>
    private static void WaterAfterDig(FakeServer srv, BlockPos cell, int cobbleAfterMs)
    {
        srv.Regenerate.Remove((cell.X, cell.Y, cell.Z));
        srv.Hook = pkt =>
        {
            if (pkt is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusFinished } d && d.X == cell.X && d.Y == cell.Y && d.Z == cell.Z)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(30);
                    srv.Blocks[(cell.X, cell.Y, cell.Z)] = Blocks.Water;
                    srv.Send(new BlockChangePacket { X = cell.X, Y = cell.Y, Z = cell.Z, BlockId = (byte)Blocks.Water, Metadata = 1 });
                    if (cobbleAfterMs < 0) return;
                    await Task.Delay(cobbleAfterMs);
                    srv.Blocks[(cell.X, cell.Y, cell.Z)] = Blocks.Cobblestone;
                    srv.Send(new BlockChangePacket { X = cell.X, Y = cell.Y, Z = cell.Z, BlockId = (byte)Blocks.Cobblestone });
                });
            return false;
        };
    }

    [Fact]
    public async Task Water_in_a_just_dug_cell_is_the_regen_transient_not_a_failure()
    {
        // Live (420): "the cell at 4,64,6 turned to water and 5,64,7 gave nothing": flowing water fills the dug cell before the
        // lava meets it. Wait for the cobble.
        var (c, s) = await Harness.StartAsync(srv => { LiveGen(srv); WaterAfterDig(srv, West, 1500); });
        await using var _ = c;
        using var __ = s;
        c.GeneratorRegenWait = TimeSpan.FromMilliseconds(300); // shorter than the transient: the liquid rule must carry it
        var r = await c.HarvestGeneratorAsync([West], 3, FoxStand);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, r.Count);
        Assert.DoesNotContain("stopped producing", r.Message);
    }

    [Fact]
    public async Task A_cell_that_stays_water_is_reported_precisely_and_left_out_of_the_rotation()
    {
        var (c, s) = await Harness.StartAsync(srv => { LiveGen(srv); WaterAfterDig(srv, West, -1); });
        await using var _ = c;
        using var __ = s;
        c.GeneratorRegenWait = TimeSpan.FromMilliseconds(200);
        c.GeneratorLiquidTimeout = TimeSpan.FromSeconds(1);
        // Alone: stop and say exactly what's wrong.
        var r = await c.HarvestGeneratorAsync([West], 3, FoxStand);
        Assert.False(r.Ok && r.Count == 3, r.Message);
        Assert.Contains("4,64,6 has been water for 1 s: the lava side may be blocked", r.Message);
        Assert.Contains("stopped producing", r.Message);
        // With the middle cell working: it's left out, the job goes on.
        c.GeneratorLiquidTimeout = TimeSpan.FromMilliseconds(300);
        var r2 = await c.HarvestGeneratorAsync([West, Middle], 6, FoxStand);
        Assert.True(r2.Ok, r2.Message);
        Assert.Equal(6, r2.Count);
        Assert.Contains("4,64,6 has been water", r2.Message);
        Assert.Contains("(left it out)", r2.Message);
    }
}
