using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Regressions from the 10-27 live session: saplings left lying up in the crown (written off as "lost near the void"),
/// stewardship of the island's last trees, the hanging log remnant reported as "the dirt is gone", and one stray failed
/// leaf dig learning spawn protection that then blocked the replant.
/// </summary>
public class TreeHelpersTests
{
    private static WorldState World(Action<FakeServer> build)
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 48, 128, 48, new byte[48 * 128 * 48], null, null, null);
        var s = new FakeServer();
        s.Blocks.Clear();
        build(s);
        foreach (var ((x, y, z), id) in s.Blocks) w.SetBlock(x, y, z, id, s.Metas.GetValueOrDefault((x, y, z)));
        s.Dispose();
        return w;
    }

    [Fact]
    public void A_drop_resting_on_a_leaf_is_found_but_not_one_on_grass_or_in_the_air()
    {
        var w = World(s => { TreeWorld.Square(s, 8, 8, 4); TreeWorld.Tree(s, 8, 8, 4); });
        Assert.Equal(new BlockPos(9, 65, 8), TreeScan.SupportingLeaf(w, new Vec3d(9.5, 66.125, 8.5)));
        Assert.Null(TreeScan.SupportingLeaf(w, new Vec3d(5.5, 64.125, 5.5)));  // on grass
        Assert.Null(TreeScan.SupportingLeaf(w, new Vec3d(9.5, 67.6, 8.5)));    // falling past a leaf
    }

    [Fact]
    public void Leaves_next_to_another_standing_trunk_are_not_expected_to_decay()
    {
        var w = World(s => { TreeWorld.Square(s, 8, 8, 6); TreeWorld.Tree(s, 6, 8, 4); TreeWorld.Tree(s, 10, 8, 4); });
        var a = TreeScan.Analyze(w, new BlockPos(6, 64, 8))!;
        foreach (var l in a.Logs) w.SetBlock(l.X, l.Y, l.Z, 0, 0); // tree A felled, B stands
        var alive = TreeScan.SupportedLeaves(w, a.Leaves.ToList());
        Assert.Contains(new BlockPos(7, 66, 8), alive);    // A's leaf, 3 leaves from B's trunk: B keeps it alive
        Assert.DoesNotContain(new BlockPos(4, 66, 8), alive); // only A held it: it decays
    }

    [Fact]
    public void Column_under_a_hanging_remnant_finds_the_planted_sapling_and_its_dirt()
    {
        var w = World(s =>
        {
            TreeWorld.Square(s, 8, 8, 3);
            s.Blocks[(8, 64, 8)] = Blocks.Sapling;
        });
        var (sapling, dirt, other) = TreeScan.ColumnUnder(w, new BlockPos(8, 65, 8));
        Assert.Equal(new BlockPos(8, 64, 8), sapling);
        Assert.Equal(new BlockPos(8, 63, 8), dirt);
        Assert.Null(other);
    }

    [Fact]
    public void Stewardship_counts_planted_saplings_and_allows_a_certain_replant()
    {
        var w = World(s => { TreeWorld.Square(s, 8, 8, 6); TreeWorld.Tree(s, 5, 8, 4); TreeWorld.Tree(s, 11, 8, 4); });
        var census = TreeStewardship.Census(w, new Vec3d(8, 64, 8));
        Assert.Equal(2, census.Trees.Count);
        var a = census.Trees[0];
        Assert.Equal("that's one of the last 2 trees and I have no sapling to replant", TreeStewardship.Refusal(census, a, 0, null, 2));
        Assert.Null(TreeStewardship.Refusal(census, a, 1, null, 2));                       // sapling in hand, spot fine
        Assert.StartsWith("that's one of the last 2 trees and I can't replant it", TreeStewardship.Refusal(census, a, 1, "too dark", 2));
        Assert.Equal(census.Trees.SelectMany(t => t.Logs).ToHashSet(), TreeStewardship.GuardedLogs(census, 2));

        w.SetBlock(8, 64, 3, Blocks.Sapling, 0); // a planted sapling is a future tree
        var more = TreeStewardship.Census(w, new Vec3d(8, 64, 8));
        Assert.Single(more.Saplings);
        Assert.Null(TreeStewardship.Refusal(more, a, 0, null, 2));
        Assert.Empty(TreeStewardship.GuardedLogs(more, 2));
    }
}

public class TreeStewardshipTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private Func<ClientOptions, ClientOptions> Logged => o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } };

    private static void TwoTrees(FakeServer srv)
    {
        TreeWorld.Square(srv, 8, 8, 6);
        TreeWorld.Tree(srv, 5, 8, 4);
        TreeWorld.Tree(srv, 11, 8, 4);
        srv.SpawnAt = (8.5, 64, 4.5);
    }

    [Fact]
    public async Task Sapling_lying_on_a_neighbours_leaf_is_knocked_down_collected_and_replanted()
    {
        // Live 10-27: "not chasing item 6:0 at 12,70.1,1.9: over or next to the void" for 30 s while it lay on a leaf.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 6);
            TreeWorld.Tree(srv, 6, 8, 4);
            TreeWorld.Tree(srv, 10, 8, 4);
            TreeWorld.Drops(srv, saplingEvery: 0);   // the crown's own leaves give nothing...
            srv.SpawnAt = (6.5, 64, 4.5);
            srv.SpawnDrop(9.5, 66.125, 8.5, Harness.Stack(Blocks.Sapling, 1)); // ...the only sapling lies on B's leaf
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(at: new BlockPos(6, 64, 8), options: TreeWorld.Fast);
        log.WriteLine(r.ToString());
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("replanted at 6,64,8", r.Message);
        Assert.DoesNotContain("void", r.Message);
        Assert.Contains(s.Dug, d => d is (9, 65, 8, Blocks.Leaves));
        Assert.Equal(Blocks.Sapling, c.World.GetBlock(6, 64, 8));
    }

    [Fact]
    public async Task Collect_drops_reports_a_drop_on_the_leaves_as_out_of_reach_not_lost()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 4);
            srv.SpawnAt = (5.5, 64, 5.5);
            srv.SpawnDrop(9.5, 66.125, 8.5, Harness.Stack(Blocks.Sapling, 1));
        }, Logged);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.All().Any(e => e.Kind == EntityKind.Item)));
        var r = await c.CollectDropsAsync(8);
        Assert.Contains("lying up on leaves", r.Message);
        Assert.DoesNotContain("void", r.Message);
    }

    [Fact]
    public async Task One_of_the_last_two_trees_without_a_sapling_is_refused_and_left_alone()
    {
        var (c, s) = await Harness.StartAsync(srv => { TwoTrees(srv); TreeWorld.Drops(srv, saplingEvery: 0); }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast with { MinTrees = null });
        Assert.False(r.Ok);
        Assert.Equal("that's one of the last 2 trees and I have no sapling to replant", r.Message);
        Assert.Empty(s.Dug);

        // Every other path (dig, mine) refuses those logs too: they never replant.
        var dig = await c.DigBlockAsync(5, 64, 8);
        Assert.False(dig.Ok);
        Assert.Contains("one of the island's last 2", dig.Message);
        var mine = await c.MineAsync("log", 2);
        Assert.False(mine.Ok);
        Assert.Empty(s.Dug);
    }

    [Fact]
    public async Task With_a_sapling_in_hand_the_last_trees_may_be_felled_because_the_replant_is_certain()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TwoTrees(srv);
            TreeWorld.Drops(srv, saplingEvery: 0);
            srv.Inventory[37] = Harness.Stack(Blocks.Sapling, 1);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast with { MinTrees = null });
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("replanted at", r.Message);
        Assert.Single(c.World.FindBlocks(new Vec3d(8, 64, 8), 8, Blocks.Sapling));
    }

    [Fact]
    public async Task A_planted_sapling_counts_as_a_future_tree()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TwoTrees(srv);
            srv.Blocks[(8, 64, 12)] = Blocks.Sapling;
            TreeWorld.Drops(srv, saplingEvery: 2);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast with { MinTrees = null });
        log.WriteLine(r.ToString());
        Assert.DoesNotContain("last 2 trees", r.Message);
        Assert.Contains(s.Dug, d => d.Id == Blocks.Log);
    }

    [Fact]
    public async Task Hanging_remnant_over_a_planted_sapling_is_not_reported_as_missing_dirt()
    {
        // Live 10-27: 2 logs left at 19,67-68,3 above Fox's sapling at 19,65,3 → "the dirt under the tree at 19,66,3 is gone".
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            srv.Blocks[(8, 64, 8)] = Blocks.Sapling;
            srv.Blocks[(8, 66, 8)] = Blocks.Log;
            srv.Blocks[(8, 67, 8)] = Blocks.Log;
            foreach (var p in new[] { (7, 67, 8), (9, 67, 8), (8, 67, 7), (8, 67, 9), (8, 68, 8) }) srv.Blocks[p] = Blocks.Leaves;
            TreeWorld.Drops(srv, saplingEvery: 0);
            srv.SpawnAt = (5.5, 64, 5.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast with { MinTrees = null });
        log.WriteLine(r.ToString());
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("its spot 8,64,8 already has a sapling (dirt intact)", r.Message);
        Assert.DoesNotContain("gone", r.Message);
        Assert.All(s.Dug, d => Assert.True(d.Id is Blocks.Log or Blocks.Leaves, $"dug {d}"));
        Assert.Equal(Blocks.Sapling, s.Blocks[(8, 64, 8)]);
        Assert.Equal(Blocks.Grass, s.Blocks[(8, 63, 8)]);
    }

    [Fact]
    public async Task Pillar_dig_back_leaves_a_block_it_did_not_place()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 10);
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
            srv.Inventory[38] = Harness.Stack(Blocks.Cobblestone, 4);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        // Someone swaps the bot's cobblestone pillar block for dirt while it stands on it.
        bool swapped = false;
        c.World.BlockChanged += ch =>
        {
            if (swapped || ch.NewId != Blocks.Cobblestone || ch.Pos.Y < 64) return;
            swapped = true;
            var p = ch.Pos;
            ThreadPool.QueueUserWorkItem(__ => { Thread.Sleep(300); s.SetBlock(p.X, p.Y, p.Z, Blocks.Dirt); });
        };
        var r = await c.ChopTreeAsync(options: TreeWorld.Fast);
        log.WriteLine(r.ToString());
        Assert.True(swapped);
        Assert.DoesNotContain(s.Dug, d => d.Id is Blocks.Dirt or Blocks.Grass);
    }

    [Fact]
    public async Task One_failed_edit_after_edits_near_spawn_worked_does_not_learn_spawn_protection()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int x = 2; x <= 14; x++)
            for (int z = 2; z <= 14; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Stone;
            srv.SpawnAt = (4.5, 64, 8.5);
            srv.Blocks[(9, 64, 10)] = Blocks.Cobblestone;
            srv.Blocks[(10, 64, 10)] = Blocks.Cobblestone;
            srv.Blocks[(11, 64, 10)] = Blocks.Cobblestone;
        }, Logged);
        await using var _ = c;
        using var __ = s;
        Assert.True((await c.DigBlockAsync(9, 64, 10)).Ok);
        s.BreakOnFinish = false; // one dig the server doesn't take (lag, a race...)
        Assert.False((await c.DigBlockAsync(10, 64, 10)).Ok);
        Assert.Null(c.ProtectedRegion);
        s.BreakOnFinish = true;
        var again = await c.DigBlockAsync(11, 64, 10);
        Assert.True(again.Ok, again.ToString());   // not refused up front as "spawn protection"
    }
}
