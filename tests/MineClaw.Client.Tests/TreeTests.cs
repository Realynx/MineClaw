using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Synthetic Skyblock worlds for the chop_tree macro.</summary>
internal static class TreeWorld
{
    /// <summary>Classic L-shaped starter island: leg A x 0..5 / z 0..2, leg B x 0..2 / z 3..5; dirt y 61-62, grass y 63.</summary>
    public static void LIsland(FakeServer s)
    {
        s.Blocks.Clear();
        foreach (var (x, z) in LCells())
        {
            s.Blocks[(x, 61, z)] = Blocks.Dirt;
            s.Blocks[(x, 62, z)] = Blocks.Dirt;
            s.Blocks[(x, 63, z)] = Blocks.Grass;
        }
    }

    public static IEnumerable<(int X, int Z)> LCells()
    {
        for (int x = 0; x <= 5; x++) for (int z = 0; z <= 2; z++) yield return (x, z);
        for (int x = 0; x <= 2; x++) for (int z = 3; z <= 5; z++) yield return (x, z);
    }

    /// <summary>Square island (2r+1)² centred on (cx, cz), top (grass) at y 63.</summary>
    public static void Square(FakeServer s, int cx, int cz, int r)
    {
        s.Blocks.Clear();
        for (int x = cx - r; x <= cx + r; x++)
        for (int z = cz - r; z <= cz + r; z++)
        {
            s.Blocks[(x, 62, z)] = Blocks.Dirt;
            s.Blocks[(x, 63, z)] = Blocks.Grass;
        }
    }

    /// <summary>b1.8.1 WorldGenTrees shape: logs y 64..63+h, leaves 4 layers from the top (radius 2, 2, 1, 1), no corners.</summary>
    public static List<(int, int, int)> Tree(FakeServer s, int bx, int bz, int h, int wood = 0)
    {
        var logs = new List<(int, int, int)>();
        for (int y = 64; y < 64 + h; y++)
        {
            s.Blocks[(bx, y, bz)] = Blocks.Log;
            if (wood != 0) s.Metas[(bx, y, bz)] = wood;
            logs.Add((bx, y, bz));
        }
        int i = 64 + h;
        for (int y = i - 3; y <= i; y++)
        {
            int k = y - i, r = 1 - k / 2;
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Math.Abs(dx) == r && Math.Abs(dz) == r) continue;
                var key = (bx + dx, y, bz + dz);
                if (s.Blocks.ContainsKey(key)) continue;
                s.Blocks[key] = Blocks.Leaves;
                if (wood != 0) s.Metas[key] = wood;
            }
        }
        return logs;
    }

    /// <summary>Logs drop themselves; every <paramref name="saplingEvery"/>-th leaf drops a sapling (0 = never).</summary>
    public static void Drops(FakeServer s, int saplingEvery)
    {
        int leaves = 0;
        s.DropOnDig = true;
        s.ConsumeOnPlace = true;
        s.DigDrop = (_, id, meta) => id switch
        {
            Blocks.Log => new ItemStack(Blocks.Log, 1, (short)(meta & 3)),
            Blocks.Leaves => saplingEvery > 0 && Interlocked.Increment(ref leaves) % saplingEvery == 0 ? new ItemStack(Blocks.Sapling, 1, (short)(meta & 3)) : null,
            _ => new ItemStack((short)id, 1, 0),
        };
    }

    /// <summary>Short waits; stewardship off (single-tree test islands) unless a test turns it on.</summary>
    public static TreeChopOptions Fast => new()
    {
        DecayWait = TimeSpan.FromMilliseconds(200), LeafPhase = TimeSpan.FromSeconds(20), Timeout = TimeSpan.FromSeconds(50), MinTrees = 0,
    };

    /// <summary>Every position the client reported had ground under it (it never stood/walked over the void).</summary>
    public static void AssertNeverOverVoid(FakeServer s)
    {
        foreach (var p in s.Received)
        {
            (double x, double y, double z)? at = p switch
            {
                PlayerPositionPacket pp => (pp.X, pp.Y, pp.Z),
                PlayerPositionLookPacket pl => (pl.X, pl.Y, pl.Z),
                _ => null,
            };
            if (at is not { } a) continue;
            int bx = (int)Math.Floor(a.x), bz = (int)Math.Floor(a.z);
            bool ground = false;
            // The hitbox (±0.3) may straddle a column; any supported column under it counts.
            foreach (var (dx, dz) in new[] { (0.0, 0.0), (0.3, 0.3), (-0.3, 0.3), (0.3, -0.3), (-0.3, -0.3) })
            {
                int cx = (int)Math.Floor(a.x + dx), cz = (int)Math.Floor(a.z + dz);
                for (int y = (int)Math.Floor(a.y); y >= 0 && !ground; y--) ground |= s.Blocks.ContainsKey((cx, y, cz));
            }
            Assert.True(ground, $"over the void at {a.x:0.##},{a.y:0.##},{a.z:0.##} (column {bx},{bz})");
        }
    }
}

public class TreeScanTests
{
    [Fact]
    public void Analyze_splits_touching_crowns_by_nearest_trunk()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 48, 128, 48, new byte[48 * 128 * 48], null, null, null);
        var s = new FakeServer();
        s.Blocks.Clear();
        TreeWorld.Tree(s, 5, 1, 5, wood: 2);
        TreeWorld.Tree(s, 1, 1, 4);
        foreach (var ((x, y, z), id) in s.Blocks) w.SetBlock(x, y, z, id, s.Metas.GetValueOrDefault((x, y, z)));
        s.Dispose();
        for (int x = 0; x <= 5; x++) w.SetBlock(x, 63, 1, Blocks.Grass, 0);

        var birch = TreeScan.Analyze(w, new BlockPos(5, 66, 1))!;
        Assert.Equal("birch", birch.WoodName);
        Assert.Equal(new BlockPos(5, 64, 1), birch.Base);
        Assert.Equal(5, birch.Logs.Count);
        Assert.True(birch.OnSoil);
        Assert.DoesNotContain(birch.Leaves, l => l.X <= 3 && l.Y <= 67); // the oak's side of the overlap stays the oak's
        var oak = TreeScan.Analyze(w, new BlockPos(1, 64, 1))!;
        Assert.Equal("oak", oak.WoodName);
        Assert.Empty(oak.Leaves.Intersect(birch.Leaves));
        Assert.Equal(2, TreeScan.FindTrees(w, new Vec3d(3, 64, 1), 10).Count);
        Assert.Equal(2, TreeScan.ParseWood("birch"));
    }
}

public class ChopTreeTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private Func<ClientOptions, ClientOptions> Logged => o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } };

    [Fact]
    public async Task Oak_on_the_L_island_from_the_far_leg_chops_collects_and_replants()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.LIsland(srv);
            TreeWorld.Tree(srv, 5, 1, 5);          // tree on the rim: x = 6 is void
            TreeWorld.Drops(srv, saplingEvery: 4);
            srv.SpawnAt = (1.5, 64, 4.5);          // far leg
            srv.Inventory[36] = Harness.Stack(Items.StoneAxe, 1);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Assert.True(r.Ok, r.ToString());
        Assert.Matches(@"^chopped oak tree \(5 logs, [1-9]\d* saplings?\), replanted at 5,64,1", r.Message);
        Assert.Equal(Blocks.Sapling, c.World.GetBlock(5, 64, 1));
        Assert.All(Enumerable.Range(64, 5), y => Assert.NotEqual(Blocks.Log, c.World.GetBlock(5, y, 1)));
        Assert.Equal(5, c.World.Inventory.CountItem(Blocks.Log));
        // Only that tree's logs and leaves were dug: never the dirt/grass under or around it.
        Assert.All(s.Dug, d => Assert.True(d.Id is Blocks.Log or Blocks.Leaves, $"dug {d}"));
        Assert.Equal(Blocks.Grass, s.Blocks[(5, 63, 1)]);
        Assert.Contains(s.Got<PlayerDiggingPacket>(), d => d.Status == 0);
        TreeWorld.AssertNeverOverVoid(s);
        output(r);
    }

    [Fact]
    public async Task Birch_is_found_by_type_other_trees_untouched_and_replanted_as_birch()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 4, 4, 5);
            TreeWorld.Tree(srv, 7, 4, 5, wood: 2);
            var oak = TreeWorld.Tree(srv, 3, 4, 4);
            TreeWorld.Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (4.5, 64, 8.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(wood: 2, options: TreeWorld.Fast);
        Assert.True(r.Ok, r.ToString());
        Assert.StartsWith("chopped birch tree (5 logs,", r.Message);
        Assert.Equal(Blocks.Sapling, c.World.GetBlock(7, 64, 4));
        Assert.Equal(2, c.World.GetMeta(7, 64, 4) & 3);
        for (int y = 64; y < 68; y++) Assert.Equal(Blocks.Log, c.World.GetBlock(3, y, 4)); // the oak stands
        Assert.DoesNotContain(s.Dug, d => d.X <= 4);                                       // nothing of the oak's crown
        Assert.Equal(5, c.World.Inventory.CountItem(Blocks.Log, 2));
    }

    [Fact]
    public async Task Tall_tree_top_is_reached_by_jumping_inside_the_trunk()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 8);          // logs 64..71: the top two need a jump
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Assert.True(r.Ok, r.ToString());
        Assert.StartsWith("chopped oak tree (8 logs,", r.Message);
        Assert.DoesNotContain(s.Got<PlayerBlockPlacementPacket>(), p => p.HeldItem.Id is Blocks.Dirt or Blocks.Cobblestone);
    }

    [Fact]
    public async Task Tree_above_jump_reach_pillars_with_cobblestone_and_takes_it_back()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 10);         // logs 64..73
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
            srv.Inventory[37] = Harness.Stack(Blocks.Dirt, 10);        // must never be used to pillar
            srv.Inventory[38] = Harness.Stack(Blocks.Cobblestone, 4);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        // The crown is out of reach: once the last log is gone it decays by itself and drops two saplings.
        c.World.BlockChanged += ch =>
        {
            if (ch.Pos != new BlockPos(8, 73, 8) || ch.NewId != 0) return;
            ThreadPool.QueueUserWorkItem(__ =>
            {
                Thread.Sleep(100);
                foreach (var ((x, y, z), id) in s.Blocks.ToList()) if (id == Blocks.Leaves) s.SetBlock(x, y, z, 0);
                s.SpawnDrop(7.5, 64.1, 8.5, Harness.Stack(Blocks.Sapling, 1));
                s.SpawnDrop(9.5, 64.1, 9.5, Harness.Stack(Blocks.Sapling, 1));
            });
        };
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast with { DecayWait = TimeSpan.FromSeconds(5) });
        Assert.True(r.Ok, r.ToString());
        Assert.StartsWith("chopped oak tree (10 logs,", r.Message);
        Assert.DoesNotContain(s.Got<PlayerBlockPlacementPacket>(), p => p.HeldItem.Id == Blocks.Dirt);
        Assert.Contains(s.Got<PlayerBlockPlacementPacket>(), p => p.HeldItem.Id == Blocks.Cobblestone);
        Assert.Equal(Blocks.Sapling, c.World.GetBlock(8, 64, 8));    // pillar dug back, sapling on the dirt
        Assert.False(s.Blocks.ContainsKey((8, 65, 8)));
    }

    [Fact]
    public async Task Tree_beyond_reach_without_cobblestone_reports_honestly()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 10);
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
            srv.Inventory[37] = Harness.Stack(Blocks.Dirt, 10);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Assert.False(r.Ok);
        Assert.Matches(@"^2 log\(s\) out of reach \(up to y 73\); no cobble/planks to pillar; chopped 8/10 oak logs", r.Message);
        Assert.DoesNotContain(s.Got<PlayerBlockPlacementPacket>(), p => p.HeldItem.Id == Blocks.Dirt);
    }

    [Fact]
    public async Task Log_over_the_void_is_cut_from_inside_and_its_drop_written_off()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.LIsland(srv);
            TreeWorld.Tree(srv, 4, 1, 4);
            srv.Blocks[(5, 66, 1)] = Blocks.Log;   // branch toward the rim
            srv.Blocks[(6, 67, 1)] = Blocks.Log;   // branch hanging over the void
            srv.Blocks.Remove((6, 66, 1)); srv.Blocks.Remove((6, 67, 0)); srv.Blocks.Remove((6, 67, 2));
            TreeWorld.Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (1.5, 64, 4.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Assert.True(r.Ok, r.ToString());
        Assert.False(s.Blocks.ContainsKey((6, 67, 1)));
        Assert.Contains("replanted at 4,64,1", r.Message);
        log.WriteLine(r.ToString());
        Assert.Equal(5, c.World.Inventory.CountItem(Blocks.Log));    // the one over the void fell out of the world
        Assert.DoesNotContain(s.Dug, d => d.Y < 64);
        TreeWorld.AssertNeverOverVoid(s);
    }

    [Fact]
    public async Task No_sapling_dropped_is_reported_as_a_failure_not_a_replant()
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
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        Assert.False(r.Ok);
        Assert.Matches(@"^no sapling dropped, NOT replanted \(chopped oak tree: 4 logs, 0 saplings; dirt at 8,63,8 is free; broke \d+ leaves, \d+ decayed\)$", r.Message);
        Assert.False(s.Blocks.ContainsKey((8, 64, 8)));
    }

    [Fact]
    public async Task Replant_blocked_keeps_the_sapling_and_says_why()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 4);
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        // Someone puts a cobblestone on the stump the moment the last log goes.
        c.World.BlockChanged += ch =>
        {
            if (ch.Pos == new BlockPos(8, 67, 8) && ch.NewId == 0) ThreadPool.QueueUserWorkItem(__ => s.SetBlock(8, 64, 8, Blocks.Cobblestone));
        };
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        log.WriteLine(r.ToString());
        Assert.False(r.Ok);
        Assert.StartsWith("replant blocked: cobblestone on the dirt at 8,64,8 (chopped oak tree: 4 logs, ", r.Message);
        Assert.Contains("sapling kept)", r.Message);
        Assert.True(c.World.Inventory.CountItem(Blocks.Sapling) > 0);
        Assert.DoesNotContain(s.Dug, d => d.Id == Blocks.Cobblestone);
    }

    [Fact]
    public async Task Chopping_raises_a_report_and_marks_own_digs()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 4);
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        TreeChopReport? report = null;
        c.TreeChopped += rep => report = rep;
        bool ownDig = false;
        c.World.BlockChanged += ch => { if (ch.OldId == Blocks.Log) ownDig |= c.World.SelfEdits.IsRecent(ch.Pos); };
        var r = await c.ChopTreeAsync(at: new BlockPos(8, 63, 8), options: TreeWorld.Fast);
        Assert.True(r.Ok, r.ToString());
        Assert.True(await Harness.WaitAsync(() => report is not null));
        Assert.Equal(new BlockPos(8, 64, 8), report!.Replanted);
        Assert.Equal(4, report.Logs);
        Assert.True(ownDig);
    }

    [Fact]
    public async Task No_tree_nearby_fails_fast()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync();
        Assert.False(r.Ok);
        Assert.Equal("no tree within 24", r.Message);
    }

    private void output(ActionResult r) => log.WriteLine("chop_tree: " + r);
}
