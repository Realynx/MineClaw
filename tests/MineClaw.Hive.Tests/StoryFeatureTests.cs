using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>Story Mode's designate tools, the Architect's build queue, storage settings and workbench bills.</summary>
public sealed class StoryFeatureTests
{
    private static void Only(StoryColony c, int i, params string[] types)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static int Count((JsonNode? Result, string? Error) r)
    {
        Assert.Null(r.Error);
        return r.Result!["count"]!.GetValue<int>();
    }

    // ================================================================ designations

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void A_dragged_area_expands_to_the_matching_targets_per_kind(string version)
    {
        IGameData data = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var (c, h, w) = StoryRig.Make(data: data);
        var area = Area.Of(0, 60, 0, 15, 80, 15);
        w.Tree(2, 2, 5); w.Tree(6, 2, 3); w.Tree(12, 12, 4);            // three trees: three jobs (not 12 logs)
        w.Set(1, 64, 8, 31, 1); w.Set(2, 64, 8, 37); w.Set(3, 64, 8, 6); // tall grass, a flower, a sapling
        w.Set(5, 64, 8, 59, 7); w.Set(6, 64, 8, 59, 3);                  // ripe and unripe wheat
        w.Set(7, 64, 8, 83); w.Set(7, 65, 8, 83);                        // sugar cane: only the part above the base
        w.Set(9, 63, 9, 15); w.Set(10, 63, 9, 16);                       // iron and coal ore in the stone floor
        w.Set(13, 64, 1, 5); w.Set(13, 65, 1, 20);                       // planks and glass: built blocks
        if (version == "1.12.2") { w.Set(8, 64, 8, 141, 7); w.Set(8, 64, 9, 141, 4); } // ripe / unripe carrots
        w.Ents.Add(new StoryEntity(1, "item", "log", 4.5, 64, 4.5, ItemId: 17, Count: 3));
        w.Ents.Add(new StoryEntity(2, "item", "log", 40.5, 64, 4.5, ItemId: 17, Count: 3)); // outside
        w.Ents.Add(new StoryEntity(3, "mob", "cow", 5.5, 64, 5.5));
        w.Ents.Add(new StoryEntity(4, "mob", "zombie", 6.5, 64, 6.5, Hostile: true));

        Assert.Equal(3, Count(c.Designate("add", "chop", area, null, null)));
        var tree = c.Designations.First(d => d.At == new P3(2, 64, 2));
        Assert.Equal(5, tree.Blocks!.Count); // the tree's log column, one job
        Assert.Equal(3 + 3, Count(c.Designate("add", "cut", area, null, null))); // three plants + the three leaf caps
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Harvest_takes_only_ripe_crops_and_mine_follows_its_filter(string version)
    {
        IGameData data = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var (c, h, w) = StoryRig.Make(data: data);
        var area = Area.Of(0, 60, 0, 15, 80, 15);
        w.Set(5, 64, 8, 59, 7); w.Set(6, 64, 8, 59, 3);
        w.Set(7, 64, 8, 83); w.Set(7, 65, 8, 83);
        w.Set(9, 64, 9, 103);
        int ripe = 3;
        if (version == "1.12.2") { w.Set(8, 64, 8, 141, 7); w.Set(8, 64, 9, 141, 4); w.Set(3, 64, 3, 207, 3); ripe += 2; }
        Assert.Equal(ripe, Count(c.Designate("add", "harvest", area, null, null)));
        Assert.Equal(data.Item(295).Name, c.Designations.First(d => d.At == new P3(5, 64, 8)).Replant); // the version's seed name
        Assert.DoesNotContain(c.Designations, d => d.At == new P3(6, 64, 8) || d.At == new P3(7, 64, 8));

        var (c2, _, w2) = StoryRig.Make(data: data);
        w2.Set(9, 63, 9, 15); w2.Set(10, 63, 9, 16); w2.Set(11, 63, 9, 3); w2.Set(12, 63, 9, 7);
        var floor = Area.Of(9, 63, 9, 12, 63, 9);
        Assert.Equal(2, Count(c2.Designate("add", "mine", floor, null, "ores")));
        Assert.Equal(1, Count(c2.Designate("add", "mine", floor, null, "soil")));
        Assert.Equal(1, Count(c2.Designate("add", "mine", Area.Of(13, 63, 9, 13, 63, 9), null, "stone")));
        Assert.Equal(0, Count(c2.Designate("add", "mine", Area.Of(12, 63, 9, 12, 63, 9), null, "all"))); // bedrock never
        Assert.NotNull(c2.Designate("add", "mine", floor, null, "gems").Error);
    }

    [Fact]
    public void Haul_hunt_deconstruct_forbid_and_cancel_by_area()
    {
        var (c, h, w) = StoryRig.Make();
        var area = Area.Of(0, 60, 0, 15, 80, 15);
        w.Set(13, 64, 1, 5); w.Set(13, 65, 1, 20); w.Set(14, 64, 1, 2); w.Set(2, 64, 2, 64, 1); w.Set(2, 65, 2, 64, 8);
        w.Ents.Add(new StoryEntity(1, "item", "log", 4.5, 64, 4.5, ItemId: 17, Count: 3));
        w.Ents.Add(new StoryEntity(2, "item", "log", 40.5, 64, 4.5, ItemId: 17, Count: 3));
        w.Ents.Add(new StoryEntity(3, "mob", "cow", 5.5, 64, 5.5));
        w.Ents.Add(new StoryEntity(4, "mob", "zombie", 6.5, 64, 6.5, Hostile: true));
        var haul = c.Designate("add", "haul", area, null, null).Result!;
        Assert.Equal([1], haul["targets"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.Equal(1, Count(c.Designate("add", "hunt", area, null, null))); // the cow, not the zombie
        Assert.Equal(4, Count(c.Designate("add", "deconstruct", area, null, null))); // planks, glass, the door's halves; never grass
        Assert.Equal(0, Count(c.Designate("add", "haul", area, null, null))); // already designated
        Assert.Equal(3, Count(c.Designate("add", "forbid", Area.Of(0, 60, 0, 5, 80, 5), null, null))); // the drop and the door's two halves
        // A forbidden drop is never hauled.
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        Only(c, 0, WorkTypes.Haul);
        Assert.NotEqual("collect", c.Next()["kind"]!.GetValue<string>());
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        Assert.Equal(1, Count(c.Designate("add", "allow", null, [new P3(4, 64, 4)], null)));
        Assert.Equal("collect", c.Next()["kind"]!.GetValue<string>());
        // Cancel by area: the deconstruct marks in the box go, the rest stay.
        int before = c.Designations.Count;
        Assert.Equal(2, Count(c.Designate("cancel", "deconstruct", Area.Of(13, 60, 1, 13, 80, 1), null, null)));
        Assert.Equal(before - 2, c.Designations.Count);
        Assert.Equal("haul", StoryColony.WorkOf("haul"));
        Assert.Equal(WorkTypes.Chop, StoryColony.WorkOf("cut"));
        Assert.Equal(WorkTypes.Build, StoryColony.WorkOf("deconstruct"));
    }

    // ================================================================ construction

    private static List<GhostState> Expand(JsonObject item, IGameData? d = null)
    {
        var (g, _, err) = StoryConstruction.Expand(d ?? Beta181GameData.Instance, item);
        Assert.Null(err);
        return g;
    }

    private static GhostState At(List<GhostState> g, int x, int y, int z) => g.Single(b => b.At == new P3(x, y, z));

    [Fact]
    public void Walls_are_the_perimeter_times_height_doors_two_tall_and_a_gable_roof_meets_at_the_ridge()
    {
        var wall = Expand(new JsonObject { ["kind"] = "wall", ["area"] = new JsonArray(0, 64, 0, 4, 64, 4), ["material"] = "cobblestone", ["height"] = 3 });
        Assert.Equal(16 * 3, wall.Count);
        Assert.All(wall, b => Assert.Equal(4, b.Id));
        Assert.DoesNotContain(wall, b => b.At.X is > 0 and < 4 && b.At.Z is > 0 and < 4); // hollow
        Assert.Contains(wall, b => b.At == new P3(0, 66, 0)); // corners included

        var door = Expand(new JsonObject { ["kind"] = "door", ["blocks"] = new JsonArray(new JsonArray(2, 64, 0)), ["facing"] = "s" });
        Assert.Equal(2, door.Count);
        Assert.True(At(door, 2, 64, 0).Job);
        Assert.Equal(1, At(door, 2, 64, 0).Meta);
        Assert.Equal("wooden_door", At(door, 2, 64, 0).Item);
        Assert.False(At(door, 2, 65, 0).Job);
        Assert.Equal(8, At(door, 2, 65, 0).Meta);

        var house = Expand(new JsonObject
        {
            ["kind"] = "wall", ["area"] = new JsonArray(0, 64, 0, 4, 64, 6), ["material"] = "planks", ["height"] = 3,
            ["roof"] = new JsonObject { ["style"] = "gable" },
        });
        Assert.Equal(53, At(house, 0, 67, 3).Id);
        Assert.Equal(StoryConstruction.StairsMeta("e"), At(house, 0, 67, 3).Meta); // rises toward the ridge
        Assert.Equal(StoryConstruction.StairsMeta("w"), At(house, 4, 67, 3).Meta);
        Assert.Equal(StoryConstruction.StairsMeta("e"), At(house, 1, 68, 3).Meta);
        Assert.Equal((44, 2), (At(house, 2, 69, 3).Id, At(house, 2, 69, 3).Meta)); // the ridge: a wooden half slab (b1.8.1)
        Assert.Equal(5, At(house, 2, 67, 0).Id); // gable ends filled with the wall material
        Assert.Equal(5, At(house, 2, 68, 6).Id);
        var modern = Expand(new JsonObject { ["kind"] = "roof", ["area"] = new JsonArray(0, 67, 0, 2, 67, 2), ["material"] = "planks", ["roof"] = new JsonObject { ["style"] = "slab" } },
            Release1122GameData.Instance);
        Assert.All(modern, b => Assert.Equal(126, b.Id)); // 1.12.2 has wooden slabs of their own
        var flat = Expand(new JsonObject { ["kind"] = "roof", ["area"] = new JsonArray(0, 67, 0, 2, 67, 2), ["roof"] = new JsonObject { ["style"] = "flat", ["overhang"] = 1 } });
        Assert.Equal(25, flat.Count);
        var stairs = Expand(new JsonObject { ["kind"] = "stairs", ["area"] = new JsonArray(0, 64, 0, 3, 67, 0), ["material"] = "cobblestone", ["facing"] = "e" });
        Assert.Equal(3, stairs.Count);
        Assert.Equal(new P3(2, 66, 0), stairs.Single(s => s.At.Y == 66).At);
        Assert.All(stairs, s => Assert.Equal(67, s.Id));
    }

    [Fact]
    public void The_build_queue_goes_in_order_bottom_up_with_one_pawn_per_block()
    {
        var (c, h, w) = StoryRig.Make(pawns: 4);
        for (int i = 0; i < 4; i++)
        {
            Only(c, i, WorkTypes.Build);
            h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(4, 0, 64));
        }
        var a = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["area"] = new JsonArray(0, 64, 5, 2, 65, 5), ["material"] = "cobblestone" }), null);
        var b = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(9, 64, 9)), ["material"] = "cobblestone" }), null);
        string ia = a.Result!["ids"]![0]!.GetValue<string>(), ib = b.Result!["ids"]![0]!.GetValue<string>();
        var jobs = Enumerable.Range(0, 3).Select(i => c.Next(i)).ToList();
        Assert.All(jobs, j => Assert.Equal("place", j["kind"]!.GetValue<string>()));
        Assert.All(jobs, j => Assert.EndsWith(",64,5", j["at"]!.GetValue<string>())); // the bottom row first
        Assert.Equal(3, jobs.Select(j => j["at"]!.GetValue<string>()).Distinct().Count());
        Assert.Equal("9,64,9", c.Next(3)["at"]!.GetValue<string>()); // the upper row waits for its support: the next build goes on
        foreach (var (j, i) in jobs.Select((j, i) => (j, i))) { w.Set(int.Parse(j["at"]!.GetValue<string>().Split(',')[0]), 64, 5, 4); c.Report(c.P(i), j["id"]!.GetValue<string>(), "done", null, null); }
        Assert.EndsWith(",65,5", c.Next(0)["at"]!.GetValue<string>());
        var state = c.State()["builds"]!.AsArray();
        Assert.Equal(3, state.First(x => x!["id"]!.GetValue<string>() == ia)!["blocks_done"]!.GetValue<int>());
        Assert.Contains(c.State()["ghosts"]!["blocks"]!.AsArray(), g => g![5]!.GetValue<string>() == ia && g[6]!.GetValue<string>() == "leased");
        // Reorder, pause, remove.
        Assert.Equal([ib, ia], c.BuildsOp("reorder", null, [ib]).Result!["ids"]!.AsArray().Select(x => x!.GetValue<string>()));
        c.BuildsOp("pause", null, [ia]);
        Assert.Equal("paused", c.Builds.First(x => x.Id == ia).Status);
        c.BuildsOp("resume", null, [ia]);
        Assert.Equal("queued", c.Builds.First(x => x.Id == ia).Status);
        c.BuildsOp("remove", null, [ib]);
        Assert.DoesNotContain(c.Builds, x => x.Id == ib);
        Assert.NotNull(c.BuildsOp("remove", null, ["nope"]).Error);
    }

    [Fact]
    public void A_build_without_material_anywhere_is_blocked_with_an_alert_until_some_is_stored()
    {
        var (c, h, w) = StoryRig.Make();
        Only(c, 0, WorkTypes.Build);
        c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "floor", ["area"] = new JsonArray(0, 64, 5, 2, 64, 6), ["material"] = "planks" }), null);
        var j = c.Next();
        Assert.NotEqual("place", j["kind"]!.GetValue<string>());
        var b = c.Builds.Single();
        Assert.Equal("blocked", b.Status);
        Assert.Equal("need 6 wooden planks", b.Reason);
        Assert.Contains(c.Alerts, a => a.Text.Contains("need 6 wooden planks"));
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        c.AddContents(c.StorageAt(new P3(0, 64, 9))!, new Stack(5, 0, 32));
        j = c.Next();
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal("0,64,9", j["fetch"]![0]!["from"]!.GetValue<string>()); // fetched from storage first
        Assert.Equal("active", b.Status);
    }

    // ================================================================ storage

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Every_item_of_the_version_is_in_exactly_one_category_leaf(string version)
    {
        IGameData d = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var cats = StoryCategories.For(d);
        var leaves = cats.Roots.SelectMany(r => r.Children.Count == 0 ? [r] : r.Children).ToList();
        var seen = new Dictionary<(int, int), string>();
        foreach (var leaf in leaves)
            foreach (var k in leaf.Items) Assert.True(seen.TryAdd((k.Id, k.Meta), leaf.Id), $"{k} in {leaf.Id} and {seen.GetValueOrDefault((k.Id, k.Meta))}");
        for (int id = 1; id < 4096; id++)
        {
            if (!d.ItemExists(id) || d.Item(id).Name is "air" or "") continue;
            Assert.True(seen.ContainsKey((id, -1)) || seen.ContainsKey((id, 0)), $"{id} {d.Item(id).Name} has no category");
        }
        Assert.Equal("wood_logs", cats.LeafOf(17, 0));
        Assert.Equal("wood_planks", cats.LeafOf(5, 2));
        Assert.Equal("gems", cats.LeafOf(351, 4)); // lapis
        Assert.Equal("food_meals", cats.LeafOf(297, 0));
        Assert.Equal("ores", cats.LeafOf(15, 0));
        Assert.Equal("tools", cats.LeafOf(278, 0));
    }

    [Fact]
    public void Filters_take_nested_categories_items_and_deny()
    {
        var cats = StoryCategories.For(Beta181GameData.Instance);
        var wood = new StorageFilter { Allow = ["wood"], Deny = ["wood_sticks"] };
        Assert.True(cats.Accepts(wood, 17, 0));
        Assert.True(cats.Accepts(wood, 5, 0));
        Assert.False(cats.Accepts(wood, 280, 0));
        Assert.False(cats.Accepts(wood, 297, 0));
        Assert.True(cats.Accepts(new StorageFilter { Allow = ["*"], Deny = ["food"] }, 4, 0));
        Assert.False(cats.Accepts(new StorageFilter { Allow = ["*"], Deny = ["food"] }, 320, 0));
        Assert.True(cats.Accepts(new StorageFilter { Allow = ["351:4"] }, 351, 4));
        Assert.False(cats.Accepts(new StorageFilter { Allow = ["351:4"] }, 351, 1));
        Assert.True(cats.Accepts(new StorageFilter { Allow = ["280"] }, 280, 0));
        Assert.False(cats.Valid("nonsense"));
    }

    [Fact]
    public void Deliveries_go_by_priority_then_distance_skip_full_chests_and_higher_ones_pull_from_lower()
    {
        var (c, h, w) = StoryRig.Make();
        P3 near = new(2, 64, 0), far = new(30, 64, 0), food = new(1, 64, 0);
        c.StorageOp("set", [near], new StorageFilter { Allow = ["wood"] }, "normal", "Wood");
        c.StorageOp("set", [far], new StorageFilter { Allow = ["wood"] }, "critical", "Wood (main)");
        c.StorageOp("set", [food], new StorageFilter { Allow = ["food"] }, "critical", "Food");
        Assert.Equal(far, c.BestChest(17, 0, new P3(0, 64, 0))!.Chest);
        c.StorageAt(far)!.FreeSlots = 0;
        Assert.Equal(near, c.BestChest(17, 0, new P3(0, 64, 0))!.Chest); // full: next one
        Assert.Null(c.BestChest(4, 0, new P3(0, 64, 0))); // nothing accepts cobblestone
        c.StorageAt(far)!.FreeSlots = 27;
        Assert.NotNull(c.StorageOp("set", [near], null, "urgent", null).Error);
        Assert.NotNull(c.StorageOp("set", [near], new StorageFilter { Allow = ["nope"] }, null, null).Error);

        // A pawn that chopped: its logs go to the Wood chest (the critical one), not the Food chest.
        w.Tree(5, 5);
        c.Designate("add", "chop", Area.Of(0, 60, 0, 9, 80, 9), null, null);
        Only(c, 0, WorkTypes.Chop, WorkTypes.Haul);
        var chop = c.Next();
        Assert.Equal("chop", chop["kind"]!.GetValue<string>());
        h.Lives[c.P()] = FakeStoryHost.Live(5, 64, 4, inv: [new Stack(17, 0, 5), new Stack(6, 0, 1), new Stack(278, 0, 1)]);
        c.Report(c.P(), chop["id"]!.GetValue<string>(), "done", null, null);
        var stash = c.Next();
        Assert.Equal(far.ToString(), stash["stash"]![0]!["to"]!.GetValue<string>());
        Assert.Equal(["log"], stash["stash"]![0]!["items"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("No storage accepts")); // the sapling: kept, never dropped
        c.Report(c.P(), stash["id"]!.GetValue<string>(), "done", null, null);
        Assert.Equal(5, c.StorageAt(far)!.Contents.Single(s => s.Id == 17).Count);
        Assert.Empty(c.StorageAt(food)!.Contents);

        // Rebalance: logs sitting in the normal chest move up to the critical one (a background haul).
        c.AddContents(c.StorageAt(near)!, new Stack(17, 0, 20));
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0);
        c.Designations.Clear();
        var move = c.Next();
        Assert.Equal(near.ToString(), move["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.Equal(far.ToString(), move["stash"]![0]!["to"]!.GetValue<string>());
    }

    // ================================================================ bills

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Recipes_per_version_and_bench(string version)
    {
        IGameData d = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var table = StoryRecipes.For(d, "crafting_table");
        var stick = table.Single(r => r.Id == "craft:stick");
        Assert.Equal(new Stack(280, 0, 4), stick.Output);
        Assert.Equal(WorkTypes.Craft, stick.WorkType);
        var furnace = StoryRecipes.For(d, "furnace");
        Assert.Equal(WorkTypes.Craft, furnace.Single(r => r.Id == "smelt:iron_ore").WorkType);
        Assert.Equal(WorkTypes.Cook, furnace.Single(r => r.Id == "smelt:porkchop").WorkType);
        Assert.All(furnace, r => Assert.True(r.Fuel));
        Assert.Empty(StoryRecipes.For(d, "anvil")); // no primitive yet
        Assert.Equal(version == "1.12.2", table.Any(r => r.Output.Id == 145)); // anvils exist in 1.12.2 only
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Workshop()
    {
        var (c, h, w) = StoryRig.Make(pawns: 2);
        w.Set(3, 64, 0, 58);
        w.Set(6, 64, 0, 61);
        c.StorageOp("set", [new P3(3, 64, 2)], new StorageFilter { Allow = ["wood"] }, "normal", "Wood");
        c.StorageOp("set", [new P3(6, 64, 2)], new StorageFilter { Allow = ["*"], Deny = ["wood"] }, "normal", "Rest");
        c.AddContents(c.StorageAt(new P3(3, 64, 2))!, new Stack(5, 0, 16));
        for (int i = 0; i < 2; i++) Only(c, i, WorkTypes.Craft, WorkTypes.Cook);
        return (c, h, w);
    }

    private static string Bill(StoryColony c, int x, JsonObject bill)
    {
        var r = c.BillsOp("add", new JsonObject { ["bench"] = new JsonArray(x, 64, 0), ["bill"] = bill });
        Assert.Null(r.Error);
        return r.Result!["id"]!.GetValue<string>();
    }

    [Fact]
    public void A_count_bill_fetches_crafts_and_stores_until_it_is_done()
    {
        var (c, h, w) = Workshop();
        var id = Bill(c, 3, new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "count", ["target"] = 8 });
        var j = c.Next();
        Assert.Equal("craft", j["kind"]!.GetValue<string>());
        Assert.Equal("stick", j["item"]!.GetValue<string>());
        Assert.Equal(8, j["n"]!.GetValue<int>());
        Assert.Equal("3,64,2", j["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.Equal(4, j["fetch"]![0]!["n"]!.GetValue<int>()); // 2 crafts × 2 planks
        Assert.Equal("3,64,2", j["stash"]![0]!["to"]!.GetValue<string>()); // sticks are wood
        Assert.Equal("idle", c.Next(1)["kind"]!.GetValue<string>()); // one pawn per bench
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        Assert.Equal(8, c.Benches.Single(b => b.Pos == new P3(3, 64, 0)).Bills.Single().Done);
        Assert.Equal(12, c.StorageAt(new P3(3, 64, 2))!.Contents.Single(s => s.Id == 5).Count);
        Assert.NotEqual("craft", c.Next()["kind"]!.GetValue<string>()); // done
        Assert.Null(c.BillsOp("remove", new JsonObject { ["id"] = id }).Error);
    }

    [Fact]
    public void Until_counts_storage_forever_runs_on_and_restrictions_hold()
    {
        var (c, h, w) = Workshop();
        c.AddContents(c.StorageAt(new P3(3, 64, 2))!, new Stack(280, 0, 12));
        var until = Bill(c, 3, new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "until", ["target"] = 10 });
        Assert.NotEqual("craft", c.Next()["kind"]!.GetValue<string>()); // 12 stored ≥ 10
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        c.BillsOp("update", new JsonObject { ["id"] = until, ["mode"] = "forever" });
        Assert.Equal("craft", c.Next()["kind"]!.GetValue<string>());
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);

        // Only one pawn may take it; a skill floor keeps the other out too.
        c.BillsOp("update", new JsonObject { ["id"] = until, ["pawn"] = c.P(1) });
        Assert.NotEqual("craft", c.Next(0)["kind"]!.GetValue<string>());
        c.Report(c.P(0), c.CurrentOf(c.P(0))!.Id, "done", null, null);
        c.Pawns[c.P(1)].Skills[WorkTypes.Craft].Level = 3;
        c.BillsOp("update", new JsonObject { ["id"] = until, ["min_skill"] = 10 });
        Assert.NotEqual("craft", c.Next(1)["kind"]!.GetValue<string>());
        c.Report(c.P(1), c.CurrentOf(c.P(1))!.Id, "done", null, null);
        c.BillsOp("update", new JsonObject { ["id"] = until, ["min_skill"] = null });
        // An ingredient filter that denies planks: suspended, with the reason and an alert.
        c.BillsOp("update", new JsonObject { ["id"] = until, ["ingredients"] = new JsonObject { ["allow"] = new JsonArray("*"), ["deny"] = new JsonArray("wood_planks") } });
        Assert.NotEqual("craft", c.Next(1)["kind"]!.GetValue<string>());
        var bill = c.Benches.Single(b => b.Pos == new P3(3, 64, 0)).Bills.Single();
        Assert.StartsWith("no ", bill.SuspendedReason);
        Assert.Contains(c.Alerts, a => a.Text.Contains("suspended"));
        Assert.NotNull(c.BillsOp("add", new JsonObject { ["bench"] = new JsonArray(3, 64, 0), ["bill"] = new JsonObject { ["recipe"] = "smelt:porkchop" } }).Error);
    }

    [Fact]
    public void A_furnace_is_loaded_left_to_smelt_and_collected_later()
    {
        var clock = new FakeClock();
        var (c, h, w) = StoryRig.Make(time: clock);
        w.Set(6, 64, 0, 61);
        c.StorageOp("set", [new P3(6, 64, 2)], null, null, null);
        c.AddContents(c.StorageAt(new P3(6, 64, 2))!, new Stack(319, 0, 2));
        c.AddContents(c.StorageAt(new P3(6, 64, 2))!, new Stack(263, 0, 4));
        Only(c, 0, WorkTypes.Cook);
        Bill(c, 6, new JsonObject { ["recipe"] = "smelt:porkchop", ["mode"] = "count", ["target"] = 2 });
        var j = c.Next();
        Assert.Equal("smelt", j["kind"]!.GetValue<string>());
        Assert.Equal("porkchop", j["item"]!.GetValue<string>());
        Assert.Equal("coal", j["fuel"]!.GetValue<string>());
        Assert.Equal(2, j["fetch"]!.AsArray().Count); // the meat and the fuel
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        var bench = c.Benches.Single();
        Assert.NotNull(bench.LoadedBill);
        Assert.NotEqual("collect_furnace", c.Next()["kind"]!.GetValue<string>()); // still smelting: other work (here: idle)
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        clock.Now += TimeSpan.FromSeconds(25);
        var collect = c.Next();
        Assert.Equal("collect_furnace", collect["kind"]!.GetValue<string>());
        c.Report(c.P(), collect["id"]!.GetValue<string>(), "done", null, null);
        Assert.Equal(2, bench.Bills.Single().Done);
        Assert.Null(bench.LoadedBill);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
