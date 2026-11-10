using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Rotation (docs/web/story-mode.md "Rotation"; Fox 11-04: furnaces placed without the hologram's facing): a design's or the
/// Architect's turned block keeps its meta from the queue to the pawn (the job's <c>metas</c>), a cell counts as built only turned
/// the designed way, a wrong one is taken back once and then shown as <c>misrotated</c>, and a block that holds items is never broken
/// for its rotation.
/// </summary>
public sealed class StoryRotationTests
{
    private static readonly P3 Chest = new(0, 64, 9);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(IGameData data, params Stack[] stock) => Rig(data, (TimeProvider?)null, stock);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(IGameData data, TimeProvider? time, params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(1, data, time);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54, 3);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        return (c, h, w);
    }

    private static BuildState Add(StoryColony c, JsonObject item)
    {
        Assert.Null(c.BuildsOp("add", new JsonArray(item), null).Error);
        return c.Builds.Last();
    }

    private static JsonObject Block(int id, int meta, params int[][] cells) => new()
    {
        ["kind"] = "block", ["blocks"] = new JsonArray(cells.Select(p => (JsonNode)new JsonArray(p[0], p[1], p[2])).ToArray()),
        ["material"] = new JsonArray(id, meta),
    };

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    [Theory]
    [InlineData("no oak_stairs in the chest at 0,64,9: it is empty")]
    [InlineData("can't reach chest at 0,64,9")]
    public void A_failed_fetch_or_approach_does_not_spend_any_cells_rotation_repair(string failure)
    {
        var (c, h, w) = Rig(Release1122GameData.Instance, new Stack(53, 0, 8));
        var b = Add(c, Block(53, 3, [4, 64, 12], [5, 64, 12]));
        w.Set(4, 64, 12, 53, 2);
        w.Set(5, 64, 12, 53, 2);
        var first = c.Next();
        Assert.Equal(2, first["clear"]!.AsArray().Count);
        Assert.NotNull(first["fetch"]);
        Assert.True(c.Report(c.P(), Id(first), "failed", failure, null));
        // Another supply is now carried; the missing or inaccessible chest is no longer needed.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(53, 0, 8)]);
        var retry = c.Next();
        Assert.Equal("place", retry["kind"]!.GetValue<string>());
        Assert.Equal(2, retry["clear"]!.AsArray().Count);
        Assert.All(b.Ghosts, g => Assert.Equal("leased", g.State));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("was placed with the wrong rotation"));
    }

    [Fact]
    public void A_verified_wrong_placement_stops_only_that_cell_and_returns_its_untouched_batch_tail()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance, new Stack(53, 0, 8));
        var b = Add(c, Block(53, 3, [4, 64, 12], [5, 64, 12]));
        w.Set(4, 64, 12, 53, 2);
        w.Set(5, 64, 12, 53, 2);
        var first = c.Next();
        var attempted = first["cells"]![0]!.GetValue<string>();
        var untouched = first["cells"]![1]!.GetValue<string>();
        c.Report(c.P(), Id(first), "progress", $"cell {attempted} failed: placed with wrong rotation at {attempted}", null);
        c.Report(c.P(), Id(first), "done", "batch: 0/2 placed", null);
        var retry = c.Next();
        Assert.Equal("place", retry["kind"]!.GetValue<string>());
        Assert.Equal([untouched], retry["cells"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("misrotated", b.Ghosts.Single(g => g.At.ToString() == attempted).State);
        Assert.Equal("leased", b.Ghosts.Single(g => g.At.ToString() == untouched).State);
    }

    [Fact]
    public void The_Architects_turn_becomes_the_ghosts_meta_and_a_designs_meta_is_kept()
    {
        var d = Release1122GameData.Instance;
        List<GhostState> Expand(JsonObject item) { var (g, _, err) = StoryConstruction.Expand(d, item); Assert.Null(err); return g; }
        JsonObject Furniture(string kind, string facing) => new() { ["kind"] = kind, ["blocks"] = new JsonArray(new JsonArray(4, 64, 4)), ["facing"] = facing };
        Assert.Equal(5, Expand(Furniture("furnace", "e")).Single().Meta);  // the front east
        Assert.Equal(2, Expand(Furniture("chest", "n")).Single().Meta);    // the front north
        Assert.Equal(0, Expand(Furniture("torch", "e")).Single().Meta);    // a torch: as it is (wherever it can hang)
        var stairs = Block(53, 4, [4, 64, 4]);
        stairs["facing"] = "w";
        Assert.Equal(1 | 4, Expand(stairs).Single().Meta);                 // rising west, still upside down
        Assert.Equal(6, Expand(Block(53, 6, [4, 64, 4])).Single().Meta);    // a design's own meta (no facing word): kept
        Assert.Equal(5, StoryConstruction.Faced(33, 0, "e"));               // a piston from the palette (meta 0 = down) turned east
    }

    [Fact]
    public void A_designs_turned_blocks_go_to_the_pawn_with_each_cells_meta()
    {
        var (c, _, _) = Rig(Release1122GameData.Instance, new Stack(53, 0, 8), new Stack(5, 0, 8));
        Add(c, Block(53, 0, [4, 64, 12], [5, 64, 12]));
        var j = c.Next();
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal([0, 0], j["metas"]!.AsArray().Select(m => m!.GetValue<int>()));
        c.Report(c.P(), Id(j), "done", "batch: 0/2 placed", null);
        // Planks don't turn: no metas, the plain place.
        var (c2, _, _) = Rig(Release1122GameData.Instance, new Stack(5, 0, 8));
        Add(c2, Block(5, 0, [4, 64, 12]));
        Assert.Null(c2.Next()["metas"]);
    }

    [Fact]
    public void A_cell_is_built_only_turned_the_designed_way_and_a_wrong_one_is_taken_back_once_then_shown()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance, new Stack(53, 0, 8));
        var b = Add(c, Block(53, 2, [4, 64, 12])); // rising south
        // Already there, turned east: not built. The first time it is cleared and placed again.
        w.Set(4, 64, 12, 53, 0);
        var j = c.Next();
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Contains("4,64,12", j["clear"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal([2], j["metas"]!.AsArray().Select(m => m!.GetValue<int>()));
        // The pawn's client couldn't turn it right (it already tried once): the cell is misrotated, said, and the build finishes.
        c.Report(c.P(), Id(j), "progress", "cell 4,64,12 failed: placed with wrong rotation at 4,64,12: rising east, wants rising south", null);
        var g = b.Ghosts.Single();
        Assert.Equal("misrotated", g.State);
        Assert.Contains("rising east", g.Reason);
        Assert.Contains(c.Alerts, a => a.Text.Contains("wrong rotation"));
        c.Report(c.P(), Id(j), "done", "batch: 0/1 placed", null);
        Assert.Equal("done", b.Status);
        Assert.Equal("misrotated", ((JsonArray)((JsonObject)c.State()["ghosts"]!)["blocks"]!).Select(x => x![6]!.GetValue<string>()).Single());
        // Retry (the Retry tool / "Retry failed cells"): the misrotated cell counts as failed, starts over and gets its take-back again.
        Assert.Equal(1, c.FailedCells());
        Assert.Null(c.RetryBuilds("retry_all", null, null).Error);
        Assert.Equal("todo", g.State);
        Assert.Equal("queued", b.Status);
        var again = c.Next();
        Assert.Contains("4,64,12", again["clear"]!.AsArray().Select(x => x!.GetValue<string>()));
        c.Report(c.P(), Id(again), "done", "batch: 0/1 placed", null);
        // Turned right later (by hand): a new build there is done at once.
        w.Set(4, 64, 12, 53, 2);
        var b2 = Add(c, Block(53, 2, [4, 64, 12]));
        Assert.Equal("done", b2.Ghosts.Single().State);
    }

    [Fact]
    public void A_wrong_furnace_is_never_broken_for_its_rotation_and_runtime_bits_dont_count()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance, new Stack(61, 0, 2));
        var b = Add(c, Block(61, 5, [4, 64, 12])); // front east
        w.Set(4, 64, 12, 61, 2);                  // there, front north
        Assert.NotEqual("place", c.Next()?["kind"]?.GetValue<string>());
        Assert.Equal("misrotated", b.Ghosts.Single().State);
        Assert.Contains("facing north", b.Ghosts.Single().Reason);
        // An open trapdoor's open bit isn't the placement's: built.
        var (c2, _, w2) = Rig(Release1122GameData.Instance, new Stack(96, 0, 2));
        var b2 = Add(c2, Block(96, 1, [6, 64, 12]));
        w2.Set(6, 64, 12, 96, 1 | 4);
        c2.Next();
        Assert.Equal("done", b2.Ghosts.Single().State);
    }

    /// <summary>The pawn's next job; anything but a build job (idle, a walk) is ended at once so the pick after it is a fresh one.</summary>
    private static JsonObject Pick(StoryColony c)
    {
        var j = c.Next();
        if (j["kind"]?.GetValue<string>() is not ("place" or "dig") && j["id"] is not null) c.Report(c.P(), Id(j), "done", null, null);
        return j;
    }

    private static List<string> Clears(JsonObject j) => j["clear"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    /// <summary>A placed design (palette placement: it replaces its volume) with one chest drawn facing south at <paramref name="at"/>.</summary>
    private static GhostState PlaceChestDesign(StoryColony c, P3 at)
    {
        var item = Block(54, 3, [at.X, at.Y, at.Z]);
        item["replace_area"] = new JsonArray(at.X, at.Y, at.Z, at.X, at.Y, at.Z);
        return Add(c, item).Ghosts.Single();
    }

    /// <summary>A cell changed with the plan editor is checked for its exact meta, and the client words a wrong one "wrong exact state".
    /// That was read as an ordinary failure: a strike, and on the next pick the chest was dug out and placed again, twice, then the
    /// cell was blocked (a chest's facing is the game's to decide beside another chest).</summary>
    [Fact]
    public void A_wrong_exact_state_that_is_a_turn_is_misrotated_not_a_strike()
    {
        var clock = new ManualTime();
        var (c, _, w) = Rig(Release1122GameData.Instance, clock, new Stack(54, 0, 4));
        Assert.Null(c.EditBuildCells(null, new JsonArray(new JsonArray(4, 64, 12, 54, 3))).Error);
        var b = c.Builds.Single();
        var g = b.Ghosts.Single();
        var j = c.Next();
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.True(j["exact_state"]!.GetValue<bool>());
        w.Set(4, 64, 12, 54, 2); // placed, and the game turned it north
        c.Report(c.P(), Id(j), "progress", "cell 4,64,12 failed: placed with wrong exact state at 4,64,12: wants 54:3, got 54:2", null);
        Assert.Equal("misrotated", g.State);
        Assert.Contains("facing north", g.Reason);
        c.Report(c.P(), Id(j), "done", "batch: 0/1 placed", null);
        Assert.Equal("done", b.Status); // finished for the build, like any wrong turn
        // No strike and no rest to wait out: no later job digs the chest out to place it again, and a retry only looks at it again.
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.NotEqual("place", Pick(c)["kind"]?.GetValue<string>());
        Assert.Null(c.RetryBuilds("retry_all", null, null).Error);
        Assert.DoesNotContain("4,64,12", Clears(Pick(c)));
        Assert.Equal("misrotated", g.State);
    }

    /// <summary>Live risk named in the diagnosis: a placed design's chest that stands turned wrong was dug out for its rotation, its
    /// contents on the floor (the override for palette placements ignored that it holds items).</summary>
    [Fact]
    public void Retry_never_digs_out_a_chest_that_holds_items()
    {
        var clock = new ManualTime();
        var (c, _, w) = Rig(Release1122GameData.Instance, clock, new Stack(54, 0, 4));
        var at = new P3(4, 64, 12);
        w.Set(at.X, at.Y, at.Z, 54, 2); // a chest facing north, the colony's storage, looked into a moment ago: it holds diamonds
        c.StorageOp("set", [at], null, null, null);
        c.AddContents(c.StorageAt(at)!, new Stack(264, 0, 5));
        c.StorageAt(at)!.SeenAt = clock.Now;
        var g = PlaceChestDesign(c, at);
        Assert.DoesNotContain(at.ToString(), Clears(Pick(c)));
        Assert.Equal("misrotated", g.State);
        Assert.Contains("facing north", g.Reason);

        Assert.Null(c.RetryBuilds("retry_all", null, null).Error);
        Assert.Equal("todo", g.State);
        Assert.DoesNotContain(at.ToString(), Clears(Pick(c)));
        Assert.Equal("misrotated", g.State);
    }

    [Theory]
    [InlineData("none")]   // not a storage: the colony has no record of what it holds
    [InlineData("unseen")] // a storage nobody has looked into: its empty record is not knowledge
    [InlineData("stale")]  // looked into long ago: a player may have used it since
    [InlineData("half")]   // a double chest whose other half the record does not cover
    public void A_chest_not_known_empty_is_never_taken_back_for_its_rotation(string record)
    {
        var clock = new ManualTime();
        var (c, _, w) = Rig(Release1122GameData.Instance, clock, new Stack(54, 0, 4));
        var at = new P3(4, 64, 12);
        w.Set(at.X, at.Y, at.Z, 54, 2);
        if (record != "none") c.StorageOp("set", [at], null, null, null);
        if (record is "stale" or "half") c.StorageAt(at)!.SeenAt = clock.Now;
        if (record == "stale") clock.Now += StoryColony.SeenStale + TimeSpan.FromMinutes(1);
        if (record == "half") w.Set(at.X + 1, at.Y, at.Z, 54, 2); // joined since the record was made
        var g = PlaceChestDesign(c, at);
        Assert.DoesNotContain(at.ToString(), Clears(Pick(c)));
        Assert.Equal("misrotated", g.State);
    }

    /// <summary>The one case a container is taken back: a placed design, and the colony's record of the chest was looked into lately
    /// and is empty (nothing can spill). One take-back, as for any turned block; a retry grants one more.</summary>
    [Fact]
    public void Retry_may_take_back_a_chest_known_empty_once()
    {
        var clock = new ManualTime();
        var (c, _, w) = Rig(Release1122GameData.Instance, clock, new Stack(54, 0, 4));
        var at = new P3(4, 64, 12);
        w.Set(at.X, at.Y, at.Z, 54, 2);
        c.StorageOp("set", [at], null, null, null);
        c.StorageAt(at)!.SeenAt = clock.Now;
        var g = PlaceChestDesign(c, at);
        const string Wrong = "cell 4,64,12 failed: placed with wrong rotation at 4,64,12: facing north, wants facing south";
        for (int round = 0; round < 2; round++)
        {
            var j = Pick(c);
            Assert.Equal("place", j["kind"]!.GetValue<string>());
            Assert.Contains(at.ToString(), Clears(j));
            c.Report(c.P(), Id(j), "progress", Wrong, null); // the game turned it north again
            Assert.Equal("misrotated", g.State);
            c.Report(c.P(), Id(j), "done", "batch: 0/1 placed", null);
            Assert.NotEqual("place", Pick(c)["kind"]?.GetValue<string>()); // its one take-back is spent
            Assert.Null(c.RetryBuilds("retry_all", null, null).Error);
        }
    }

    [Fact]
    public void B181_counts_a_flipped_door_and_either_floor_lever_as_built()
    {
        var (c, _, w) = Rig(Beta181GameData.Instance);
        var door = Add(c, Block(64, 1, [4, 64, 12]));     // facing south
        w.Set(4, 64, 12, 64, 0 | 4);                     // ItemDoor's flipped form of it
        var lever = Add(c, Block(69, 5, [6, 64, 12]));
        w.Set(6, 64, 12, 69, 6);
        c.Next();
        Assert.Equal("done", door.Ghosts.First(g => g.Job).State);
        Assert.Equal("done", lever.Ghosts.Single().State);
    }
}
