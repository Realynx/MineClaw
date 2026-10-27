using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Self;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests.WorldIndex;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class TreeToolTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static int Tokens(LlmToolDefinition d) => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6;

    [Fact]
    public void Chop_tree_is_on_both_the_brain_and_the_worker_and_stays_tiny()
    {
        var (h, _) = TestBot.Create();
        var brain = h.Runtime.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "chop_tree");
        Assert.Contains(h.Runtime.Tools.Definitions(ModelTier.Worker), d => d.Name == "chop_tree");
        Assert.DoesNotContain(h.Runtime.Tools.Definitions(ModelTier.Cheap), d => d.Name == "chop_tree");
        output.WriteLine($"chop_tree ≈{Tokens(brain)} tokens: {brain.Description} {brain.InputSchema.GetRawText()}");
        Assert.True(Tokens(brain) <= 60, $"≈{Tokens(brain)}");
        Assert.Contains("mine/chop a tree", brain.Description);
        Assert.Contains("replant", brain.Description);
        Assert.True(h.Runtime.Tools.TryGet("chop_tree", out var t) && t.IsLongRunning);
    }

    [Theory]
    [InlineData(null, "chop at= wood=")]
    [InlineData("nearest", "chop at= wood=")]
    [InlineData("birch", "chop at= wood=2")]
    [InlineData("3,64,-1", "chop at=3,64,-1 wood=")]
    public async Task Target_spec_maps_to_the_macro(string? at, string call)
    {
        var (h, c) = TestBot.Create();
        var r = at is null ? await h.Run("chop_tree", new { }) : await h.Run("chop_tree", new { at });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(call, c.CallList.Last());
    }

    [Fact]
    public async Task Saved_name_resolves_and_unknown_is_a_clean_error()
    {
        var (h, c) = TestBot.Create();
        h.Runtime.Memory.SetWaypoint("big_tree", 4, 64, 2);
        Assert.True((await h.Run("chop_tree", new { at = "big_tree" })).Ok);
        Assert.Equal("chop at=4,64,2 wood=", c.CallList.Last());
        var bad = await h.Run("chop_tree", new { at = "the moon" });
        Assert.False(bad.Ok);
        Assert.StartsWith("unknown tree 'the moon'", bad.Text);
    }

    [Fact]
    public void Report_event_rides_along_and_credits_wood_work()
    {
        var ok = new TreeChopReport("oak", new(3, 64, -1), new(3, 63, -1), 5, 5, 5, 2, 0, 0, new(3, 64, -1), null, true,
            "chopped oak tree (5 logs, 2 saplings), replanted at 3,64,-1");
        var e = TreeTools.ReportEvent(ok);
        Assert.Equal(EventKind.TaskCompleted, e.Kind);
        Assert.Equal(EventPriority.Background, e.Priority); // never wakes the brain by itself
        Assert.Equal("wood", TaskTypes.Classify(e.Text));
        var fail = TreeTools.ReportEvent(ok with { Ok = false, Replanted = null, ReplantProblem = "no sapling", Message = "no sapling dropped, NOT replanted (...)" });
        Assert.Equal(EventKind.TaskFailed, fail.Kind);
        Assert.False(fail.Wakes);
        Assert.Equal("wood", TaskTypes.Classify(fail.Text));
    }

    [Fact]
    public void Index_notes_the_replanted_sapling_and_protects_its_dirt()
    {
        using var f = new IndexFixture();
        f.Island(0, 0, 3);
        f.Set(1, 64, 1, Blocks.Sapling);
        f.Pump();
        f.Indexer.NoteTree(new BlockPos(1, 64, 1), "replanted oak sapling, growing");
        f.Pump();
        var tree = f.Indexer.Snapshot().Single(s => s.Kind == IndexKinds.Tree);
        output.WriteLine(tree.Text);
        Assert.Contains("tree spot at 1,64,1", tree.Text);
        Assert.Contains("replanted oak sapling, growing (by me)", tree.Text);
        Assert.True(f.Indexer.IsProtected(new BlockPos(1, 63, 1), Blocks.Grass));
        Assert.True(f.Indexer.IsProtected(new BlockPos(1, 64, 1), Blocks.Sapling));
        Assert.False(f.Indexer.IsProtected(new BlockPos(2, 63, 1), Blocks.Grass));
    }

    [Fact]
    public void Own_digs_are_never_a_players_even_with_them_facing_and_swinging()
    {
        using var a = new ActivityFixture();
        var log = new BlockPos(2, 64, 0);
        a.World.SetBlock(log.X, log.Y, log.Z, Blocks.Log, 0);
        a.Step();
        int robin = a.Player("kiwi", 1.5, 64, 1.5);
        a.Step();
        a.Look(robin, log);
        a.Events.Swing(robin);
        a.World.SelfEdits.Note(log);                 // our 0x0E for that cell went out
        a.World.SetBlock(log.X, log.Y, log.Z, 0, 0); // the server confirms the break
        a.Step();
        Assert.DoesNotContain(a.Recorded, r => r.Kind == ActivityKinds.Broke);
        Assert.Equal(1, a.Tracker.SelfEdits);

        // Control: the same dig without our packet is hers.
        var log2 = new BlockPos(2, 64, 2);
        a.World.SetBlock(log2.X, log2.Y, log2.Z, Blocks.Log, 0);
        a.Step();
        a.Look(robin, log2);
        a.Events.Swing(robin);
        a.World.SetBlock(log2.X, log2.Y, log2.Z, 0, 0);
        a.Step();
        Assert.Contains(a.Recorded, r => r.Kind == ActivityKinds.Broke && r.Player == "kiwi" && r.BlockId == Blocks.Log);
    }
}
