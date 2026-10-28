using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-28 01:52 (task #55): Fox's "pause harvesting" stopped a chop at 3/5 birch logs; "finish that last birch tree" then
/// found "only an unverified log column at 9,68,0" (no leaves/dirt under it) and the worker asked Fox to punch it. A log column
/// next to cells our own chop felled is our unfinished tree: chop_tree finishes it and replants the tree's own species.
/// </summary>
public class UnfinishedTreeTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private Func<ClientOptions, ClientOptions> Logged => o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } };

    /// <summary>The two top birch logs of a trunk at 7,64..68,4 whose lower three were chopped; no crown (leaves broken).</summary>
    private static void Remnant(FakeServer srv)
    {
        TreeWorld.Square(srv, 4, 4, 5);
        foreach (var y in new[] { 67, 68 })
        {
            srv.Blocks[(7, y, 4)] = Blocks.Log;
            srv.Metas[(7, y, 4)] = 2;
        }
        TreeWorld.Drops(srv, saplingEvery: 0);
        srv.SpawnAt = (4.5, 64, 4.5);
    }

    private static void OurStoppedChop(MinecraftClient c) =>
        c.World.Chops.Note(new ChopRecord(new BlockPos(7, 64, 4), new BlockPos(7, 63, 4), c.World.Dimension, 2,
            new HashSet<BlockPos> { new(7, 64, 4), new(7, 65, 4), new(7, 66, 4) }, new HashSet<BlockPos>(), "maid_chan", DateTimeOffset.UtcNow));

    [Fact]
    public async Task Our_stopped_birch_is_finished_and_replanted_with_a_birch_sapling()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Remnant(srv);
            srv.Inventory[36] = Harness.Stack(Blocks.Sapling, 30);    // oak x30, as live
            srv.Inventory[37] = Harness.Stack(Blocks.Sapling, 2, 2);  // and two birch
        }, Logged);
        await using var _ = c;
        using var __ = s;
        OurStoppedChop(c);
        var r = await c.ChopTreeAsync(wood: 2, options: TreeWorld.Fast);
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("replanted at 7,64,4", r.Message);
        Assert.NotEqual(Blocks.Log, c.World.GetBlock(7, 67, 4));
        Assert.NotEqual(Blocks.Log, c.World.GetBlock(7, 68, 4));
        Assert.Equal(Blocks.Sapling, c.World.GetBlock(7, 64, 4));
        Assert.Equal(2, c.World.GetMeta(7, 64, 4) & 3); // birch, not the oak it holds 30 of
        Assert.All(s.Dug, d => Assert.True(d.Id is Blocks.Log or Blocks.Leaves, $"dug {d}"));
    }

    [Fact]
    public async Task Without_a_birch_sapling_it_asks_instead_of_planting_oak()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Remnant(srv);
            srv.Inventory[36] = Harness.Stack(Blocks.Sapling, 30);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        OurStoppedChop(c);
        var r = await c.ChopTreeAsync(at: new BlockPos(7, 67, 4), options: TreeWorld.Fast);
        Assert.Contains("no birch sapling; plant oak?", r.Message);
        Assert.NotEqual(Blocks.Log, c.World.GetBlock(7, 67, 4));
        Assert.NotEqual(Blocks.Sapling, c.World.GetBlock(7, 64, 4));
        Assert.Equal(30, c.World.Inventory.CountItem(Blocks.Sapling, 0));
    }

    [Fact]
    public async Task A_bare_log_column_nobody_of_ours_chopped_is_still_a_build()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Remnant(srv);
            srv.Inventory[37] = Harness.Stack(Blocks.Sapling, 2, 2);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(at: new BlockPos(7, 67, 4), options: TreeWorld.Fast);
        Assert.False(r.Ok);
        Assert.Contains("a build, not a tree", r.Message);
        Assert.Equal(Blocks.Log, c.World.GetBlock(7, 67, 4));
    }

    [Fact]
    public async Task A_finished_chop_is_remembered_for_its_remnants()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 4, 4, 5);
            TreeWorld.Tree(srv, 7, 4, 5, wood: 2);
            TreeWorld.Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (4.5, 64, 8.5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(wood: 2, options: TreeWorld.Fast);
        Assert.True(r.Ok, r.ToString());
        var rec = c.World.Chops.Owner(new BlockPos(7, 69, 4), c.World.Dimension); // right above the felled top
        Assert.NotNull(rec);
        Assert.Equal(2, rec!.Wood);
        Assert.Equal(5, rec.Felled.Count);
        Assert.False(rec.Unfinished);
    }
}

public class ChopLogTests
{
    private static ChopRecord Rec(DateTimeOffset at, params BlockPos[] left) =>
        new(new BlockPos(9, 65, 0), new BlockPos(9, 64, 0), 0, 2,
            new HashSet<BlockPos> { new(9, 65, 0), new(9, 66, 0), new(9, 67, 0) }, left.ToHashSet(), "maid_chan", at);

    [Fact]
    public void Remnant_logs_touching_or_above_a_felled_cell_or_left_standing_are_ours_for_a_while()
    {
        var now = new DateTimeOffset(2026, 10, 28, 1, 52, 27, TimeSpan.Zero);
        var log = new ChopLog();
        log.Note(Rec(now, new BlockPos(9, 68, 0), new BlockPos(9, 69, 0)));
        Assert.NotNull(log.Owner(new BlockPos(9, 68, 0), 0, now));          // the live 9,68,0 column
        Assert.NotNull(log.Owner(new BlockPos(9, 69, 0), 0, now));          // left standing
        Assert.NotNull(log.Owner(new BlockPos(10, 67, 1), 0, now));         // a branch touching a felled cell
        Assert.Null(log.Owner(new BlockPos(12, 67, 0), 0, now));            // another tree two blocks over
        Assert.Null(log.Owner(new BlockPos(9, 68, 0), -1, now));            // the nether
        Assert.Null(log.Owner(new BlockPos(9, 68, 0), 0, now + ChopLog.Window + TimeSpan.FromMinutes(1)));
        Assert.True(Rec(now, new BlockPos(9, 68, 0)).Unfinished);
    }
}
