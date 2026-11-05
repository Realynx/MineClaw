using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Protection rework, client side (live 2026-10-27): own pillars are always minable (dug down from the top, one safe step at a
/// time), autonomous <c>mine</c> only takes resource sources (15:55: "mine cobble" dug the island floor next to the dual gen),
/// hard safety never yields to an override (void support, lava, another player's footing), high falls aren't "the void"
/// (15:42 clone 67), and a hole the bot stands in can be refilled before climbing out.
/// </summary>
public class ProtectionReworkClientTests
{
    private static Task<bool> Until(Func<bool> c, int ms = 4000) => Harness.WaitAsync(c, ms);

    // ------------------------------------------------------------------ own pillar

    [Fact]
    public async Task Own_log_pillar_is_recorded_and_mined_down_from_the_top_inside_a_protected_box()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.ConsumeOnPlace = true;
            srv.Inventory[36] = Harness.Stack(Blocks.Log, 10);
            srv.Inventory[37] = Harness.Stack(Items.StoneAxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        // The live "main house" box (a guessed build, "don't break"): everything in it is soft-protected, except my own blocks.
        c.IsProtected = (p, id) => p.Y >= 63 && !c.World.Placed.IsPlaced(p, c.World.Dimension, id);
        var g = await c.GotoAsync(new GoalBlock(8, 68, 8), GotoOptions.Default with { AllowPlace = true, ScaffoldBudget = 8 });
        Assert.True(g.Ok, g.Message);
        for (int y = 64; y <= 67; y++)
        {
            Assert.Equal(Blocks.Log, c.World.GetBlock(8, y, 8));
            Assert.True(c.World.Placed.IsPlaced(new BlockPos(8, y, 8), 0, Blocks.Log), $"8,{y},8 not recorded as mine");
        }
        // Mine down: each dig is the block under my feet, a 1-block step onto the next pillar block.
        for (int y = 67; y >= 64; y--)
        {
            var r = await c.DigBlockAsync(8, y, 8);
            Assert.True(r.Ok, r.Message);
            Assert.True(await Until(() => c.World.Player.OnGround && c.World.Player.Position.Y < y + 0.5), $"didn't drop below {y}");
            Assert.False(c.World.Placed.IsPlaced(new BlockPos(8, y, 8), 0, Blocks.Log)); // dug: no longer mine
        }
        Assert.Equal(64, (int)Math.Floor(c.World.Player.Position.Y + 1e-6));
    }

    [Fact]
    public async Task A_protected_block_refusal_says_why_and_how_to_unlock_it()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Blocks[(9, 64, 8)] = Blocks.Planks);
        await using var _ = c;
        using var __ = s;
        c.IsProtected = (p, _) => p == new BlockPos(9, 64, 8);
        c.ExplainProtection = (_, _) => new ProtectionVerdict(ProtectionLevel.Soft, "structure", "part of the main house build");
        var r = await c.DigBlockAsync(9, 64, 8);
        Assert.False(r.Ok);
        Assert.Contains("is protected (part of the main house build)", r.Message);
        Assert.Contains("say 'you can break it'", r.Message);
    }

    /// <summary>
    /// A Story build's ordered clear (clear_dig; docs/web/story-mode.md "Builds reconcile with the world"): live 11-05 natural stone and
    /// dirt in a house's cells were refused as "a placed stone (someone's floor, wall or path)" 581 times an hour, and each refusal
    /// became an "occupied by stone" placement. The owner ordered those cells built: soft protection on natural ground yields to the
    /// ordered dig; a block someone placed (planks), a container and a never-break mark still refuse, and a plain dig still refuses.
    /// </summary>
    [Fact]
    public async Task A_builds_ordered_clear_digs_natural_ground_through_soft_protection_but_never_a_placed_block_or_a_mark()
    {
        using var mode = MineClaw.Data.Versioning.WorldMode.Override(MineClaw.Data.Versioning.WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Dirt;
            srv.Blocks[(9, 64, 9)] = Blocks.Planks;
            srv.Blocks[(9, 64, 7)] = Blocks.Dirt;
            srv.Blocks[(7, 64, 8)] = Blocks.Chest;
        });
        await using var _ = c;
        using var __ = s;
        c.IsProtected = (p, _) => p.Y == 64; // the "someone's floor" guess over the whole spot
        c.ExplainProtection = (_, _) => new ProtectionVerdict(ProtectionLevel.Soft, "floor", "a placed stone (someone's floor, wall or path)");
        c.NeverBreak = p => p == new BlockPos(9, 64, 7);
        var plain = await c.DigBlockAsync(9, 64, 8);
        Assert.False(plain.Ok); // the bot's own dig keeps every protection
        Assert.Contains("someone's floor", plain.Message);
        var r = await c.DigForBuildAsync(new BlockPos(9, 64, 8));
        Assert.True(r.Ok, r.Message);
        Assert.True(await Until(() => c.World.GetBlock(9, 64, 8) == 0));
        Assert.False((await c.DigForBuildAsync(new BlockPos(9, 64, 9))).Ok); // planks: someone placed them
        Assert.False((await c.DigForBuildAsync(new BlockPos(9, 64, 7))).Ok); // a never-break mark
        Assert.False((await c.DigForBuildAsync(new BlockPos(7, 64, 8))).Ok); // a chest
        Assert.Equal(Blocks.Planks, c.World.GetBlock(9, 64, 9));
    }

    // ------------------------------------------------------------------ hard safety

    [Fact]
    public async Task Hard_safety_never_yields_to_an_override()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks.Clear();
            for (int x = 6; x <= 10; x++) srv.Blocks[(x, 63, 8)] = Blocks.Stone; // a one-wide bridge over the void
            srv.Blocks[(10, 64, 8)] = Blocks.Leaves;
            srv.Blocks[(6, 64, 8)] = Blocks.Cobblestone;
            srv.Blocks[(5, 64, 8)] = Blocks.Lava;
            srv.SpawnAt = (8.5, 64, 8.5);
        });
        await using var _ = c;
        using var __ = s;
        // Everything permitted / "placed by me": soft protection is gone entirely.
        c.IsProtected = (_, _) => false;
        c.SoftOverride = (_, _) => true;
        var under = await c.DigBlockAsync(8, 63, 8);
        Assert.False(under.Ok);
        Assert.Contains("void", under.Message);
        var lava = await c.DigBlockAsync(6, 64, 8);
        Assert.False(lava.Ok);
        Assert.Contains("lava", lava.Message);
        // Another player standing on the leaves: never dug out from under them.
        c.World.Entities.SpawnPlayer(77, "kiwi", new Vec3d(10.5, 65, 8.5), 0, 0);
        var leaf = await c.DigBlockAsync(10, 64, 8);
        Assert.False(leaf.Ok);
        Assert.Contains("someone is standing on it", leaf.Message);
        Assert.Equal(Blocks.Stone, c.World.GetBlock(8, 63, 8));
    }

    [Fact]
    public async Task A_high_fall_onto_ground_is_not_called_the_void()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(8, 70, 8)] = Blocks.Leaves; // up on a tree crown, ground 6 below
            srv.SpawnAt = (8.5, 71, 8.5);
        }, o => o with { ClutchEnabled = true });
        await using var _ = c;
        using var __ = s;
        var dangers = new List<DangerEvent>();
        c.Danger += d => { lock (dangers) dangers.Add(d); };
        Assert.True(await Until(() => c.World.Player.OnGround && c.World.Player.Position.Y > 70));
        s.Blocks.Remove((8, 70, 8));
        s.Send(new MineClaw.Protocol.Packets.BlockChangePacket { X = 8, Y = 70, Z = 8, BlockId = 0, Metadata = 0 });
        Assert.True(await Until(() => c.World.Player.OnGround && c.World.Player.Position.Y < 65, 6000));
        await Until(() => { lock (dangers) return dangers.Count > 0; }, 1000);
        lock (dangers)
        {
            Assert.DoesNotContain(dangers, d => d.Kind == DangerKind.FallingIntoVoid);
            var fall = Assert.Single(dangers, d => d.Kind == DangerKind.HighFall);
            Assert.Contains("not the void", fall.Message);
        }
    }

    // ------------------------------------------------------------------ autonomous mine: resource sources only

    /// <summary>The live dual gen: lava | cell | water | cell | lava along z=7, walls at z=6/8, a cobble floor all around.</summary>
    internal static void DualGenWithFloor(FakeServer srv)
    {
        srv.Blocks.Clear();
        for (int x = 1; x <= 11; x++)
        for (int z = 3; z <= 11; z++)
            srv.Blocks[(x, 63, z)] = Blocks.Cobblestone; // the island floor around the gen
        for (int x = 4; x <= 8; x++)
        {
            srv.Blocks[(x, 64, 6)] = Blocks.Cobblestone;
            srv.Blocks[(x, 64, 8)] = Blocks.Cobblestone;
        }
        srv.Blocks[(4, 64, 7)] = Blocks.Lava;
        srv.Blocks[(5, 64, 7)] = Blocks.Cobblestone; // cell
        srv.Blocks[(6, 64, 7)] = Blocks.Water;
        srv.Blocks[(7, 64, 7)] = Blocks.Cobblestone; // cell
        srv.Blocks[(8, 64, 7)] = Blocks.Lava;
        srv.Regenerate[(5, 64, 7)] = Blocks.Cobblestone;
        srv.Regenerate[(7, 64, 7)] = Blocks.Cobblestone;
        srv.SpawnAt = (6.5, 64, 4.5);
        srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        srv.DropOnDig = true;
    }

    internal static readonly HashSet<(int, int, int)> GenCells = [(5, 64, 7), (7, 64, 7)];

    [Fact]
    public async Task Mine_cobble_at_the_dual_gen_digs_only_the_regen_cells_never_the_floor()
    {
        var (c, s) = await Harness.StartAsync(DualGenWithFloor);
        await using var _ = c;
        using var __ = s;
        // The client's mine never digs next to lava (the generator loop does that, from a lava-safe stand), and never the
        // placed floor: so it digs nothing here and says to use the generator (the Bot's mine tool routes there itself).
        var r = await c.MineAsync("cobblestone", 20, 8);
        var dug = s.Dug.ToList();
        Assert.All(dug, d => Assert.Contains((d.X, d.Y, d.Z), GenCells));
        for (int x = 1; x <= 11; x++)
        for (int z = 3; z <= 11; z++)
            Assert.Equal(Blocks.Cobblestone, s.Blocks.GetValueOrDefault((x, 63, z)));
        // Never silent: the floor it left alone is named, with the way to get cobble.
        Assert.Contains("left", r.Message);
        Assert.Contains("harvest_generator", r.Message);
    }

    [Fact]
    public async Task Mine_takes_my_own_placed_cobble_and_ores_but_not_someone_elses_floor()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 9)] = Blocks.Cobblestone;
            srv.Blocks[(7, 64, 7)] = Blocks.CoalOre;
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
            srv.Inventory[37] = Harness.Stack(Blocks.Cobblestone, 4);
        });
        await using var _ = c;
        using var __ = s;
        var placed = await c.PlaceBlockAsync(8, 64, 10, Blocks.Cobblestone);
        Assert.True(placed.Ok, placed.Message);
        Assert.True(c.World.Placed.IsPlaced(new BlockPos(8, 64, 10), 0, Blocks.Cobblestone));
        var r = await c.MineAsync("cobblestone", 5, 6);
        Assert.Equal(1, r.Count);
        Assert.Equal(0, c.World.GetBlock(8, 64, 10));                    // mine
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(9, 64, 9));    // not mine: a placed block, left alone
        var ore = await c.MineAsync("coal_ore", 1, 6);
        Assert.True(ore.Ok, ore.Message);
    }

    // ------------------------------------------------------------------ holes

    /// <summary>Live 15:55: in its own hole at 7,63,8, the worker couldn't refill 7,62,9 ("no solid neighbour face in reach").</summary>
    private static void Holes(FakeServer srv)
    {
        srv.Blocks.Clear();
        for (int x = 3; x <= 11; x++)
        for (int z = 4; z <= 12; z++)
        for (int y = 61; y <= 63; y++)
            srv.Blocks[(x, y, z)] = Blocks.Cobblestone;
        srv.Blocks.Remove((7, 63, 8)); // the hole I stand in
        srv.Blocks.Remove((7, 63, 9)); // the holes beside it
        srv.Blocks.Remove((7, 62, 9));
        srv.SpawnAt = (7.5, 63, 8.5);
        srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 16);
        srv.ConsumeOnPlace = true;
    }

    [Fact]
    public async Task Refills_the_diagonal_hole_below_from_inside_its_own_hole()
    {
        var (c, s) = await Harness.StartAsync(Holes);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Until(() => c.World.Player.OnGround));
        var r = await c.PlaceBlockAsync(7, 62, 9, Blocks.Cobblestone);
        Assert.True(r.Ok, r.Message);
        var r2 = await c.PlaceBlockAsync(7, 63, 9, Blocks.Cobblestone);
        Assert.True(r2.Ok, r2.Message);
    }

    [Fact]
    public async Task Refill_the_hole_im_standing_in_then_climb_out()
    {
        var (c, s) = await Harness.StartAsync(Holes);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Until(() => c.World.Player.OnGround));
        // Pillar up 1 with cobble: the hole under me is filled by the jump-place.
        var up = await c.GotoAsync(new GoalBlock(7, 64, 8), GotoOptions.Default with { AllowPlace = true, ScaffoldBudget = 2 });
        Assert.True(up.Ok, up.Message);
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(7, 63, 8));
        // Then fill the holes beside it, bottom first.
        Assert.True((await c.PlaceBlockAsync(7, 62, 9, Blocks.Cobblestone)).Ok);
        Assert.True((await c.PlaceBlockAsync(7, 63, 9, Blocks.Cobblestone)).Ok);
        Assert.Equal(Blocks.Cobblestone, s.Blocks.GetValueOrDefault((7, 62, 9)));
        Assert.Equal(Blocks.Cobblestone, s.Blocks.GetValueOrDefault((7, 63, 9)));
    }
}

/// <summary>Rule 2 (pure): a tree is logs with leaves on a dirt/grass base; bare log pillars and log builds are not.</summary>
public class TreeRuleTests
{
    private static WorldState World()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -6; x <= 6; x++)
        for (int z = -6; z <= 6; z++)
            w.SetBlock(x, 63, z, Blocks.Grass, 0);
        return w;
    }

    private static void Tree(WorldState w, int x, int z)
    {
        for (int y = 64; y <= 68; y++) w.SetBlock(x, y, z, Blocks.Log, 0);
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        for (int y = 67; y <= 69; y++)
            if ((dx != 0 || dz != 0 || y == 69) && w.GetBlock(x + dx, y, z + dz) == 0) w.SetBlock(x + dx, y, z + dz, Blocks.Leaves, 0);
    }

    [Fact]
    public void A_bare_log_pillar_on_grass_is_not_a_tree_and_never_stewarded()
    {
        var w = World();
        for (int y = 64; y <= 69; y++) w.SetBlock(4, y, 4, Blocks.Log, 0);
        var census = TreeStewardship.Census(w, new Vec3d(0, 64, 0));
        Assert.Empty(census.Trees);
        Assert.Empty(TreeStewardship.GuardedLogs(census, 2));
    }

    [Fact]
    public void A_real_tree_with_leaves_still_respects_stewardship_and_a_pillar_touching_it_is_not_its()
    {
        var w = World();
        Tree(w, 0, 0);
        for (int y = 64; y <= 65; y++) w.SetBlock(1, y, 1, Blocks.Log, 0); // a bare pillar beside the trunk (diagonal: same cluster)
        var census = TreeStewardship.Census(w, new Vec3d(0, 64, 0));
        var tree = Assert.Single(census.Trees);
        Assert.DoesNotContain(new BlockPos(1, 65, 1), tree.Logs);
        Assert.Equal(5, tree.Logs.Count);
        var guarded = TreeStewardship.GuardedLogs(census, 2); // the island's only tree: guarded
        Assert.Contains(new BlockPos(0, 65, 0), guarded);
        Assert.DoesNotContain(new BlockPos(1, 65, 1), guarded);
    }

    [Fact]
    public void My_own_placed_logs_next_to_a_tree_are_never_part_of_it()
    {
        var w = World();
        Tree(w, 0, 0);
        for (int y = 67; y <= 70; y++)
        {
            w.SetBlock(1, y, 0, Blocks.Log, 0); // a nerd pole up the trunk side, in the crown (touches leaves)
            w.Placed.Note(new BlockPos(1, y, 0), 0, Blocks.Log, "maid_chan", DateTimeOffset.UtcNow);
        }
        var tree = TreeScan.Analyze(w, new BlockPos(0, 65, 0))!;
        Assert.DoesNotContain(new BlockPos(1, 68, 0), tree.Logs);
        Assert.Null(TreeScan.Analyze(w, new BlockPos(1, 68, 0)));
    }
}
