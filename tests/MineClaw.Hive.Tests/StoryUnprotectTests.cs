using System.Text.Json.Nodes;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

public sealed class StoryUnprotectTests
{
    [Fact]
    public void Release_interrupts_a_pawns_placement_batch_and_rejects_its_late_report()
    {
        var (c, h, _) = StoryRig.Make();
        foreach (var type in WorkTypes.All) c.Pawns[c.P()].Priorities[type] = type == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(4, 0, 8)]);
        var at = new P3(2, 64, 2);
        var neighbour = at.Add(1, 0, 0);
        c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["material"] = "cobblestone",
            ["blocks"] = new JsonArray(at.Json(), neighbour.Json()) }), null);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal(2, job["cells"]!.AsArray().Count);
        Assert.Null(c.UnprotectCells([at]).Error);
        Assert.Null(c.CurrentOf(c.P()));
        Assert.NotEmpty(h.Interrupted);
        Assert.False(c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null));
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.At == at);
        Assert.Contains(c.Builds.SelectMany(b => b.Ghosts), g => g.At == neighbour);
    }

    [Fact]
    public void Releasing_a_cell_stops_every_overlapping_build_and_only_that_preserved_cell()
    {
        var (c, _, w) = StoryRig.Make();
        var at = new P3(2, 64, 2);
        var neighbour = at.Add(1, 0, 0);
        w.Set(at.X, at.Y, at.Z, 4);
        w.Set(neighbour.X, neighbour.Y, neighbour.Z, 4);
        Assert.Null(c.ZoneOp("add", "preserve", Area.Of(2, 64, 2, 3, 64, 2), grow: new JsonObject { ["below"] = 0, ["above"] = 0 }).Error);
        for (int i = 0; i < 2; i++)
            Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["material"] = "cobblestone",
                ["blocks"] = new JsonArray(at.Json(), neighbour.Json()) }), null).Error);
        c.Reconcile(w);
        Assert.Null(c.UnprotectCells([at]).Error);
        w.Set(at.X, at.Y, at.Z, 0);
        w.Set(neighbour.X, neighbour.Y, neighbour.Z, 0);
        c.Reconcile(w);
        c.Reconcile(w); // completed build cells require two agreeing drift observations before reopening
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.At == at);
        Assert.Contains(c.Builds.SelectMany(b => b.Ghosts), g => g.At == neighbour && g.State != "done");
        var preserve = c.Zones.Single().Preserve!;
        Assert.Equal(PreserveSnap.Unprotected, preserve.Data!.At(at.X, at.Y, at.Z));
        Assert.False(c.PreserveFilter()!(new BlockPos(at.X, at.Y, at.Z), 4));
        var decoded = PreserveCodec.Decode(preserve.Snap!);
        Assert.Equal(PreserveSnap.Unprotected, decoded!.At(at.X, at.Y, at.Z));
        c.Reconcile(w);
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.At == at);
    }

    [Fact]
    public void Release_does_not_dig_a_block_or_remove_other_work_and_rejects_missing_selection()
    {
        var (c, _, w) = StoryRig.Make();
        w.Set(2, 64, 2, 54);
        Assert.NotNull(c.UnprotectCells([]).Error);
        Assert.Null(c.UnprotectCells([new(2, 64, 2)]).Error);
        Assert.Equal(54, w.Block(2, 64, 2).Id);
        Assert.Empty(c.Designations);
    }
}
