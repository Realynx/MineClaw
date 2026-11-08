using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

public sealed class StoryUnprotectTests
{
    [Theory]
    [InlineData("floor")]
    [InlineData("block")]
    public void Released_cells_stay_out_of_the_saved_shape_and_its_reimported_plan(string kind)
    {
        var (c, h, w) = StoryRig.Make();
        var released = new P3(2, 64, 2);
        var kept = released.Add(1, 0, 0);
        var input = new JsonObject { ["kind"] = kind, ["material"] = "cobblestone", ["group"] = "old-floor", ["name"] = "Old floor" };
        if (kind == "floor") input["area"] = Area.Of(2, 64, 2, 3, 64, 2).Json();
        else input["blocks"] = new JsonArray(released.Json(), kept.Json());
        string originalInput = input.ToJsonString();
        Assert.Null(c.BuildsOp("add", new JsonArray(input), null).Error);
        Assert.Null(c.UnprotectCells([released]).Error);
        var saved = JsonSerializer.Deserialize<BuildState>(JsonSerializer.Serialize(Assert.Single(c.Builds)))!;
        var loaded = new StoryColony(c.Meta, h);
        loaded.Builds.Add(saved);
        loaded.Reconcile(w); loaded.Reconcile(w);
        Assert.Equal(kept, Assert.Single(saved.Ghosts).At);
        Assert.Null(saved.Spec!["area"]);
        Assert.Null(saved.Spec["blocks"]);
        Assert.Equal("old-floor", saved.Spec["group"]!.GetValue<string>());
        Assert.Equal("Old floor", saved.Spec["name"]!.GetValue<string>());
        Assert.Equal(originalInput, input.ToJsonString());
        // Re-expanding a saved/exported spec is a fresh add, not a copy of the already-pruned runtime ghost list.
        var (imported, _, _) = StoryRig.Make();
        Assert.Null(imported.BuildsOp("add", new JsonArray(saved.Spec.DeepClone()), null).Error);
        var ghost = Assert.Single(Assert.Single(imported.Builds).Ghosts);
        Assert.Equal(kept, ghost.At);
        Assert.Equal(4, ghost.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Releasing_or_editing_a_mine_cut_keeps_its_natural_walking_floor_safe_from_vein_mining(bool freecam)
    {
        var (c, _, w) = StoryRig.Make();
        c.DoneGrace = TimeSpan.Zero;
        foreach (var type in WorkTypes.All) c.Pawns[c.P()].Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        var released = new P3(3, 65, 0);
        var kept = released.Add(1, 0, 0);
        w.Set(released.X, released.Y, released.Z, 1);
        w.Set(kept.X, kept.Y, kept.Z, 1);
        var source = new JsonObject { ["kind"] = "mine", ["group"] = "access" };
        if (freecam) source["blocks"] = new JsonArray(released.Json(), kept.Json());
        else source["area"] = Area.Of(3, 65, 0, 4, 65, 0).Json();
        Assert.Null(c.BuildsOp("add", new JsonArray(source), null).Error);
        var mine = Assert.Single(c.Builds);
        if (freecam) Assert.Null(c.EditBuildCells(mine.Id, new JsonArray(new JsonArray(3, 65, 0, 0, 0))).Error);
        else Assert.Null(c.UnprotectCells([released]).Error);
        Assert.NotNull(mine.Spec![freecam ? "blocks" : "area"]);
        Assert.Equal(kept, Assert.Single(StoryConstruction.Expand(w.Data, mine.Spec, w).Ghosts).At);
        w.Set(released.X, released.Y, released.Z, 0);
        w.Set(kept.X, kept.Y, kept.Z, 0);
        var floor = released.Add(0, -1, 0);
        var seed = floor.Add(-1, 0, 0);
        w.Set(floor.X, floor.Y, floor.Z, 15);
        w.Set(seed.X, seed.Y, seed.Z, 15);
        Assert.Null(c.Designate("add", "mine", null, [seed], null).Error);
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        w.Set(seed.X, seed.Y, seed.Z, 0);
        var exposed = c.DigExposed(c.P(), job["id"]!.GetValue<string>(), seed.ToString(), 15, 0);
        Assert.NotNull(exposed);
        Assert.Empty(exposed);
        Assert.DoesNotContain(c.Designations, d => d.At == floor);
        Assert.Equal(15, w.Block(floor.X, floor.Y, floor.Z).Id);
    }

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
