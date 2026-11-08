using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryBuildEditTests
{
    private sealed class FakeClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-08T00:00:00Z"); }
    private static readonly P3 A = new(3, 64, 3), B = new(4, 64, 3), C = new(8, 64, 3);
    private static JsonArray Cells(params int[][] rows) => new(rows.Select(r => (JsonNode)new JsonArray(r.Select(n => (JsonNode)n).ToArray())).ToArray());
    private static JsonObject Item(int id, params P3[] positions) => new()
    {
        ["kind"] = "block", ["material"] = new JsonArray(id, 0), ["blocks"] = new JsonArray(positions.Select(p => (JsonNode)p.Json()).ToArray()),
    };
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig()
    {
        var rig = StoryRig.Make(data: Release1122GameData.Instance, time: new FakeClock());
        rig.C.DoneGrace = TimeSpan.Zero;
        foreach (var t in WorkTypes.All) rig.C.Pawns[rig.C.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        rig.H.Lives[rig.C.P()] = FakeStoryHost.Live(3, 64, 1, inv: [new Stack(5, 0, 32), new Stack(4, 0, 32), new Stack(53, 0, 12)]);
        return rig;
    }
    private static string Id((JsonNode? Result, string? Error) edit)
    {
        Assert.Null(edit.Error);
        return edit.Result!["build_id"]!.GetValue<string>();
    }

    [Fact]
    public void New_sparse_edits_keep_exact_states_and_reuse_one_plan_without_changing_server_blocks()
    {
        var (c, _, w) = Rig();
        var result = c.EditBuildCells(null, Cells([3, 64, 3, 53, 5], [8, 64, 3, 17, 4]));
        string id = Id(result);
        var edit = Assert.Single(c.Builds);
        Assert.Equal(id, edit.Id);
        Assert.Equal(2, edit.Ghosts.Count);
        Assert.Contains(edit.Ghosts, g => g.At == A && g.Id == 53 && g.Meta == 5);
        Assert.Contains(edit.Ghosts, g => g.At == C && g.Id == 17 && g.Meta == 4);
        Assert.Equal((0, 0), w.Block(A.X, A.Y, A.Z));
        Assert.True(result.Result!["rev"]!.GetValue<long>() > 0);
        Assert.Equal(id, Id(c.EditBuildCells(id, Cells([4, 64, 3, 4, 0]))));
        Assert.Single(c.Builds);
        Assert.Equal(3, edit.Ghosts.Count);
        var state = c.State()["builds"]![0]!;
        Assert.Equal(edit.Spec!["group"]!.GetValue<string>(), state["group"]!.GetValue<string>());
        Assert.Equal("Freecam edits", state["name"]!.GetValue<string>());
    }

    [Fact]
    public void Editing_an_inflight_plan_releases_the_whole_old_batch_but_changes_only_selected_cells()
    {
        var (c, h, w) = Rig();
        var source = Item(5, A, B, C);
        source["name"] = "Storehouse";
        string original = source.ToJsonString();
        Assert.Null(c.BuildsOp("add", new JsonArray(source), null).Error);
        var before = Assert.Single(c.Builds);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        string id = Id(c.EditBuildCells(null, Cells([3, 64, 3, 4, 0])));
        Assert.NotEqual(before.Id, id);
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Empty(c.Leases());
        Assert.Single(h.Interrupted);
        Assert.False(c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null));
        Assert.Equal([B, C], before.Ghosts.Select(g => g.At));
        Assert.DoesNotContain(before.Ghosts, g => g.State == "leased");
        Assert.Equal(before.Spec!["group"]!.GetValue<string>(), c.Builds.Single(b => b.Id == id).Spec!["group"]!.GetValue<string>());
        Assert.Equal(original, source.ToJsonString()); // reusable input/template was never mutated
        var expanded = StoryConstruction.Expand(w.Data, before.Spec!, w);
        Assert.Null(expanded.Error);
        Assert.Equal([B, C], expanded.Ghosts.Select(g => g.At));
        Assert.Null(before.Spec!["replace_area"]);
    }

    [Fact]
    public void Explicit_group_anchor_attaches_unowned_extensions_and_preserves_paused_work()
    {
        var (c, _, _) = Rig();
        var source = Item(5, A); source["group"] = "house"; source["name"] = "House";
        c.BuildsOp("add", new JsonArray(source), null);
        var old = c.Builds.Single();
        c.BuildsOp("pause", null, [old.Id]);
        var id = Id(c.EditBuildCells(old.Id, Cells([8, 64, 3, 4, 0])));
        var edit = c.Builds.Single(b => b.Id == id);
        Assert.Equal("house", edit.Spec!["group"]!.GetValue<string>());
        Assert.Equal("paused", edit.Status);
        Assert.Equal(A, Assert.Single(old.Ghosts).At);
        Assert.Equal(id, Id(c.EditBuildCells(old.Id, Cells([4, 64, 3, 4, 0]))));
    }

    [Fact]
    public void Delete_and_replace_interleaved_keep_air_owned_and_publish_correct_delta_keys()
    {
        var (c, _, w) = Rig();
        var id = Id(c.EditBuildCells(null, Cells([3, 64, 3, 4, 0])));
        long first = c.State()["rev"]!.GetValue<long>();
        Assert.Equal(id, Id(c.EditBuildCells(id, Cells([3, 64, 3, 0, 0]))));
        var air = Assert.Single(c.Builds.Single().Ghosts);
        Assert.True(air.Remove);
        Assert.Equal("done", air.State);
        Assert.Contains(id + "|" + A, c.State(first)["removed"]!["ghosts"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(c.State()["ghosts"]!["blocks"]!.AsArray());
        w.Set(A.X, A.Y, A.Z, 5); // an already-sent old placement lands after cancellation
        c.Reconcile(w); c.Reconcile(w);
        Assert.Equal("todo", air.State);
        long second = c.State()["rev"]!.GetValue<long>();
        Assert.Equal(id, Id(c.EditBuildCells(id, Cells([3, 64, 3, 53, 5]))));
        var delta = c.State(second);
        Assert.Contains(delta["ghosts"]!["blocks"]!.AsArray(), row => row![5]!.GetValue<string>() == id);
        Assert.False(delta["removed"]?["ghosts"]?.AsArray().Any(n => n!.GetValue<string>() == id + "|" + A) == true);
    }

    [Fact]
    public void Completed_overlaps_and_preserve_repairs_cannot_restore_the_old_target()
    {
        var (c, _, w) = Rig();
        w.Set(A.X, A.Y, A.Z, 5); w.Set(B.X, B.Y, B.Z, 5);
        c.BuildsOp("add", new JsonArray(Item(5, A, B)), null);
        c.BuildsOp("add", new JsonArray(Item(4, A, C)), null);
        c.ZoneOp("add", "home", Area.Of(3, 64, 3, 4, 64, 3));
        var zone = c.Zones.Single();
        c.ZoneOp("preserve", null, null, id: zone.Id, grow: new JsonObject { ["on"] = true });
        var snap = zone.Preserve!.Data!;
        int? outside = snap.At(B.X, B.Y, B.Z);
        string id = Id(c.EditBuildCells(null, Cells([3, 64, 3, 0, 0])));
        Assert.Single(c.Builds.SelectMany(b => b.Ghosts).Where(g => g.At == A));
        Assert.Equal(PreserveSnap.Held, snap.At(A.X, A.Y, A.Z));
        Assert.Equal(outside, snap.At(B.X, B.Y, B.Z));
        foreach (var old in c.Builds.Where(b => b.Id != id && b.Kind != "preserve"))
            Assert.DoesNotContain(StoryConstruction.Expand(w.Data, old.Spec!, w).Ghosts, g => g.At == A);
        w.Set(A.X, A.Y, A.Z, 0);
        c.Reconcile(w); c.Reconcile(w);
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.At == A && !g.Remove);
        c.BuildsOp("remove", null, [id]);
        c.Reconcile(w); c.Reconcile(w);
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.At == A);
    }

    [Theory]
    [InlineData("[[3,64,3,4,0],[4,64,3,53,16]]")]
    [InlineData("[[3,64,3,4,0],[4,64,3,99999,0]]")]
    [InlineData("[[3,64,3,4,0],[3,64,3,5,0]]")]
    [InlineData("[[3,64,3,4,0],[4,0,3,4,0]]")]
    [InlineData("[[3,64,3,4,0],[4.5,64,3,4,0]]")]
    [InlineData("[[3,64,3,4,0],[4,64,3,64,0]]")]
    public void Invalid_batches_are_atomic_even_when_a_valid_first_cell_is_leased(string json)
    {
        var (c, h, _) = Rig();
        c.BuildsOp("add", new JsonArray(Item(5, A, B)), null);
        var job = c.Next();
        string before = c.State().ToJsonString();
        Assert.NotNull(c.EditBuildCells(null, JsonNode.Parse(json)!.AsArray()).Error);
        Assert.Equal(before, c.State().ToJsonString());
        Assert.Equal(job["id"]!.GetValue<string>(), c.CurrentOf(c.P())!.Id);
        Assert.Empty(h.Interrupted);
    }

    [Fact]
    public void Exact_placement_jobs_include_all_metas_and_actual_block_id_including_zero_orientation()
    {
        var (c, _, _) = Rig();
        Id(c.EditBuildCells(null, Cells([3, 64, 3, 53, 0], [4, 64, 3, 53, 5])));
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.True(job["exact_state"]!.GetValue<bool>());
        Assert.Equal(53, job["want_id"]!.GetValue<int>());
        Assert.Equal(job["cells"]!.AsArray().Count, job["metas"]!.AsArray().Count);
        Assert.Contains(0, job["metas"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    public void Edited_sparse_specs_and_air_ownership_round_trip_without_reexpanding_the_old_box()
    {
        var (c, h, w) = Rig();
        var id = Id(c.EditBuildCells(null, Cells([3, 64, 3, 0, 0], [8, 64, 3, 17, 4])));
        var saved = JsonSerializer.Deserialize<BuildState>(JsonSerializer.Serialize(c.Builds.Single()))!;
        var loaded = new StoryColony(c.Meta, h); loaded.Builds.Add(saved);
        Assert.Equal(id, saved.Id);
        var expansion = StoryConstruction.Expand(w.Data, saved.Spec!, w);
        Assert.Null(expansion.Error);
        Assert.Equal(2, expansion.Ghosts.Count);
        Assert.Contains(expansion.Ghosts, g => g.At == A && g.Remove);
        Assert.Contains(expansion.Ghosts, g => g.At == C && g.Id == 17 && g.Meta == 4);
        w.Set(A.X, A.Y, A.Z, 5);
        loaded.Reconcile(w); loaded.Reconcile(w);
        Assert.Equal("todo", saved.Ghosts.Single(g => g.At == A).State);
    }
}
