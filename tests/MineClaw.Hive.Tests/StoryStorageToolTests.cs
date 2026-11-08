using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryStorageToolTests
{
    private static readonly P3 Lower = new(3, 64, 2), Upper = new(3, 68, 2), Outside = new(20, 64, 2);

    private static (StoryColonies Colonies, StoryColony C, FakeStoryWorld W) Rig()
    {
        var world = new FakeStoryWorld(Release1122GameData.Instance);
        var colonies = new StoryColonies(new FakeStoryHost(world));
        var (colony, error) = colonies.Create("Storehouse colony", 1, new P3(0, 64, 0));
        Assert.Null(error);
        foreach (var at in new[] { Lower, Upper, Outside }) world.Set(at.X, at.Y, at.Z, 54);
        colony!.Builds.Add(new BuildState { Id = "storehouse", Label = "Storehouse", Kind = "design", Status = "done",
            Ghosts = [new GhostState { At = new P3(0, 63, 0), Id = 1 }, new GhostState { At = new P3(10, 72, 10), Id = 1 }] });
        return (colonies, colony, world);
    }

    private static JsonObject Query(StoryColony colony, string json)
    {
        var (result, error) = colony.StorageManage(JsonNode.Parse(json)!.AsObject());
        Assert.Null(error);
        return result!.AsObject();
    }

    [Fact]
    public void Discovery_exposes_build_bounds_and_chest_heights_without_registering_storage()
    {
        var (colonies, colony, _) = Rig();
        using (colonies)
        {
            var result = Query(colony, """{"op":"list","build_id":"storehouse"}""");
            Assert.Equal(2, result["total_chests"]!.GetValue<int>());
            Assert.Empty(colony.Storages);
            Assert.Equal("[0,63,0,10,72,10]", result["builds"]![0]!["bounds"]!.ToJsonString());
            Assert.Equal("[64,68]", result["builds"]![0]!["chest_y_levels"]!.ToJsonString());
            Assert.All(result["storages"]!.AsArray(), row => Assert.False(row!["registered"]!.GetValue<bool>()));
        }
    }

    [Fact]
    public void Building_floor_selection_updates_only_its_chests_and_deduplicates_double_halves()
    {
        var (colonies, colony, world) = Rig();
        using (colonies)
        {
            world.Set(4, 64, 2, 54);
            var result = Query(colony, """{"op":"set","build_id":"storehouse","y_min":64,"y_max":67,"allow":["wood"]}""");
            Assert.Equal(1, result["total_chests"]!.GetValue<int>());
            var storage = Assert.Single(colony.Storages);
            Assert.Equal(Lower, storage.Chest);
            Assert.Equal(new P3(4, 64, 2), storage.DoubleWith);
            Assert.Equal(["wood"], storage.Filter.Allow);
            Assert.Null(colony.StorageAt(Upper));
            Assert.Null(colony.StorageAt(Outside));
        }
    }

    [Fact]
    public void A_saved_build_group_exposes_its_name_and_selects_the_whole_building_footprint()
    {
        var (colonies, colony, _) = Rig();
        using (colonies)
        {
            var first = colony.Builds.Single();
            first.Label = "block (1 blocks, stonebrick)";
            first.Spec = new JsonObject { ["name"] = "StoreHouse", ["group"] = "saved-building" };
            first.Ghosts = [new GhostState { At = new P3(0, 63, 0), Id = 1 }];
            colony.Builds.Add(new BuildState { Id = "roof", Spec = new JsonObject { ["group"] = "saved-building" },
                Ghosts = [new GhostState { At = new P3(10, 72, 10), Id = 1 }] });
            var listing = Query(colony, """{"op":"list"}""");
            Assert.Equal(1, listing["total_builds"]!.GetValue<int>());
            Assert.Equal("StoreHouse", listing["builds"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("[0,63,0,10,72,10]", listing["builds"]![0]!["bounds"]!.ToJsonString());
            Query(colony, """{"op":"set","build_id":"storehouse","y_min":64,"y_max":67,"allow":["food"]}""");
            Assert.Equal(Lower, Assert.Single(colony.Storages).Chest);
        }
    }

    [Fact]
    public void A_sign_on_a_new_double_half_refreshes_the_old_record_before_registration_and_listing()
    {
        var (colonies, colony, world) = Rig();
        using (colonies)
        {
            colony.StorageOp("set", [Lower], null, null, null);
            var half = Lower.Add(1, 0, 0);
            var sign = half.Add(0, 1, 0);
            world.Set(half.X, half.Y, half.Z, 54);
            world.Set(sign.X, sign.Y, sign.Z, 63);
            world.SignRows.Add(new StorySign(sign, ["food", "", "", ""]));
            var listing = Query(colony, """{"op":"list","chests":[[4,64,2]]}""");
            var storage = Assert.Single(colony.Storages);
            Assert.Equal(half, storage.DoubleWith);
            Assert.Same(storage, colony.StorageAt(half));
            Assert.Equal("food", listing["storages"]![0]!["filter"]!["allow"]![0]!.GetValue<string>());
            Assert.Equal("critical", listing["storages"]![0]!["priority"]!.GetValue<string>());
            Assert.Null(colony.BestChest(17, 0, Lower));
        }
    }

    [Fact]
    public void Reattaching_an_unchanged_sign_to_another_chest_cancels_its_old_pending_edit()
    {
        var (colonies, colony, world) = Rig();
        using (colonies)
        {
            colony.StorageOp("set", [Lower], null, null, null);
            var sign = Lower.Add(0, 0, -1);
            var other = sign.Add(-1, 0, 0);
            world.Set(sign.X, sign.Y, sign.Z, 68, 2);
            world.Set(other.X, other.Y, other.Z, 54);
            world.SignRows.Add(new StorySign(sign, ["food", "", "", ""]));
            Query(colony, """{"op":"set","chests":[[3,64,2]],"allow":["wood"]}""");
            var mark = Assert.Single(colony.Designations, d => d.At == sign);
            world.Set(sign.X, sign.Y, sign.Z, 68, 5); // same label now faces a different supporting chest
            colony.ReconcileStorageSigns(true);
            Assert.Null(colony.StorageAt(Lower)!.PendingFilter);
            Assert.DoesNotContain(colony.Designations, d => d.Id == mark.Id);
            Assert.Empty(colony.StorageAt(Lower)!.SignRemovals);
            Assert.Equal(["food"], colony.StorageAt(other)!.EffectiveFilter.Allow);
            Assert.Equal(68, world.Block(sign.X, sign.Y, sign.Z).Id);
        }
    }

    [Fact]
    public void Remove_item_excludes_it_from_a_category_and_add_restores_it_without_unregistering()
    {
        var (colonies, colony, _) = Rig();
        using (colonies)
        {
            Query(colony, """{"op":"set","chests":[[3,64,2]],"allow":["food"]}""");
            Query(colony, """{"op":"remove","chests":[[3,64,2]],"allow":["apple"]}""");
            var storage = Assert.Single(colony.Storages);
            Assert.Equal(["food"], storage.Filter.Allow);
            Assert.Contains("260", storage.Filter.Deny);
            Assert.False(StoryCategories.For(Release1122GameData.Instance).Accepts(storage.EffectiveFilter, 260, 0));
            Query(colony, """{"op":"add","chests":[[3,64,2]],"allow":["apple"]}""");
            Assert.DoesNotContain("260", storage.Filter.Deny);
            Assert.True(StoryCategories.For(Release1122GameData.Instance).Accepts(storage.EffectiveFilter, 260, 0));
        }
    }

    [Fact]
    public void Unknown_entries_and_unloaded_bulk_selections_fail_before_any_storage_change()
    {
        var (colonies, colony, world) = Rig();
        using (colonies)
        {
            var invalid = colony.StorageManage(JsonNode.Parse("""{"op":"set","area":[0,64,0,10,70,10],"allow":["food","not_an_item_or_category"]}""")!.AsObject());
            Assert.Contains("unknown", invalid.Error);
            Assert.Empty(colony.Storages);
            world.Unloaded.Add((7, 7));
            var list = Query(colony, """{"op":"list","build_id":"storehouse"}""");
            Assert.False(list["complete"]!.GetValue<bool>());
            var incomplete = colony.StorageManage(JsonNode.Parse("""{"op":"set","build_id":"storehouse","allow":["food"]}""")!.AsObject());
            Assert.Contains("not loaded", incomplete.Error);
            Assert.Empty(colony.Storages);
        }
    }

    [Fact]
    public void Signed_storage_tool_edit_returns_effective_and_pending_filters_and_queues_sign_removal()
    {
        var (colonies, colony, world) = Rig();
        using (colonies)
        {
            colony.StorageOp("set", [Lower], null, null, null);
            var sign = Lower.Add(0, 1, 0);
            world.Set(sign.X, sign.Y, sign.Z, 63);
            colony.StorageAt(Lower)!.Signs.Add(new StorageSignState(sign, ["food"], ["food"], []));
            var changed = Query(colony, """{"op":"set","chests":[[3,64,2]],"allow":["wood"]}""");
            var row = changed["storages"]![0]!;
            Assert.Equal("food", row["filter"]!["allow"]![0]!.GetValue<string>());
            Assert.Equal("wood", row["pending_filter"]!["allow"]![0]!.GetValue<string>());
            Assert.Equal("critical", row["priority"]!.GetValue<string>());
            Assert.Contains(colony.Designations, d => d.At == sign && d.Kind == "deconstruct");
        }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("important")]
    public void Cancelling_a_tool_priority_change_preserves_the_previous_priority(string previous)
    {
        var (colonies, colony, world) = Rig();
        using (colonies)
        {
            colony.StorageOp("set", [Lower], null, previous, null);
            var sign = Lower.Add(0, 1, 0);
            world.Set(sign.X, sign.Y, sign.Z, 63);
            world.SignRows.Add(new StorySign(sign, ["food", "", "", ""]));
            var changed = Query(colony, """{"op":"set","chests":[[3,64,2]],"priority":"low"}""");
            var row = changed["storages"]![0]!;
            Assert.Equal("critical", row["priority"]!.GetValue<string>());
            var mark = Assert.Single(colony.Designations, d => d.At == sign);
            Assert.Null(colony.Designate("cancel", null, null, null, null, [mark.Id]).Error);
            Assert.Equal(previous, colony.StorageAt(Lower)!.Priority);
            Assert.Null(colony.StorageAt(Lower)!.PendingPriority);
            world.Set(sign.X, sign.Y, sign.Z, 0);
            colony.ReconcileStorageSigns(true);
            Assert.Equal(previous, Query(colony, """{"op":"list","chests":[[3,64,2]]}""")["storages"]![0]!["priority"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("""{"op":"set","allow":["food"]}""", "choose chests")]
    [InlineData("""{"op":"set","chests":[[3,64,2]],"area":[0,64,0,10,70,10],"allow":["food"]}""", "one selector")]
    [InlineData("""{"op":"list","area":[0,0,0,1000,1000,1000]}""", "too large")]
    [InlineData("""{"op":"list","build_id":"storehouse","y_min":70,"y_max":64}""", "ordered")]
    public void Ambiguous_or_unbounded_requests_do_not_mutate_storage(string request, string expected)
    {
        var (colonies, colony, _) = Rig();
        using (colonies)
        {
            var result = colony.StorageManage(JsonNode.Parse(request)!.AsObject());
            Assert.Contains(expected, result.Error);
            Assert.Empty(colony.Storages);
        }
    }

    [Fact]
    public async Task Main_and_authenticated_hive_brains_use_the_same_storage_handler_and_bus_keeps_other_handlers()
    {
        var (colonies, colony, _) = Rig();
        using (colonies)
        {
            var main = new ToolRegistry();
            StoryStorageTool.RegisterMain(main, colonies);
            Assert.Single(main.Definitions(ModelTier.Main));
            Assert.Empty(main.Definitions(ModelTier.Worker));
            var bus = new HiveBus("maid_chan") { MainHandler = (_, _) => Task.FromResult(BusReply.Of("old handler")) };
            var token = bus.Register("maid_chan_201", (_, _) => Task.FromResult(BusReply.Of("inbox")));
            StoryStorageTool.AttachBus(bus, colonies);
            var hive = new ToolRegistry();
            StoryStorageTool.RegisterRemote(hive, async (input, ct) => await bus.RouteAsync(
                BusEnvelope.Make(BusTypes.StoryStorage, "maid_chan_201", "main", input, token), "maid_chan_201", ct));
            Assert.Equal(main.Definitions(ModelTier.Main)[0].InputSchema.GetRawText(), hive.Definitions(ModelTier.Main)[0].InputSchema.GetRawText());
            var changed = await hive.ExecuteAsync(StoryStorageTool.Name, JsonSerializer.SerializeToElement(new
            { op = "set", chests = new[] { new[] { 3, 64, 2 } }, allow = new[] { "food" } }), default);
            Assert.True(changed.Ok, changed.Text);
            Assert.Equal(["food"], colony.StorageAt(Lower)!.Filter.Allow);
            var catalog = await main.ExecuteAsync(StoryStorageTool.Name, JsonSerializer.SerializeToElement(new { op = "catalog" }), default);
            Assert.True(catalog.Ok, catalog.Text);
            Assert.Contains("\"id\":\"wood\"", catalog.Text);
            var previous = await bus.RouteAsync(BusEnvelope.Make(BusTypes.List, "maid_chan_201", "main", null, token), "maid_chan_201");
            Assert.Equal("old handler", previous.As<string>());
            var denied = await bus.RouteAsync(BusEnvelope.Make(BusTypes.StoryStorage, "maid_chan_201", "main",
                new { op = "set", chests = new[] { new[] { 20, 64, 2 } }, allow = new[] { "wood" } }, "incorrect"), "maid_chan_201");
            Assert.False(denied.Ok);
            Assert.Null(colony.StorageAt(Outside));
        }
    }

    [Fact]
    public async Task Multiple_colonies_require_an_explicit_colony_id()
    {
        var (colonies, colony, _) = Rig();
        using (colonies)
        {
            Assert.Null(colonies.Create("Second", 1, new P3(30, 64, 0)).Error);
            var tools = new ToolRegistry();
            StoryStorageTool.RegisterMain(tools, colonies);
            var result = await tools.ExecuteAsync(StoryStorageTool.Name, JsonSerializer.SerializeToElement(new
            { op = "set", chests = new[] { new[] { 3, 64, 2 } }, allow = new[] { "food" } }), default);
            Assert.False(result.Ok);
            Assert.Contains("colony is required", result.Text);
            Assert.Empty(colony.Storages);
        }
    }
}
