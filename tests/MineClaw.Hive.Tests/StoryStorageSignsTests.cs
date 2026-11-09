using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryStorageSignsTests
{
    private static readonly P3 Chest = new(3, 64, 2), Sign = new(3, 64, 1), Other = new(9, 64, 2);
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(params string[] text)
    {
        var (c, h, w) = StoryRig.Make();
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        w.Set(Other.X, Other.Y, Other.Z, 54);
        c.StorageOp("set", [Chest, Other], new StorageFilter(), "normal", null);
        foreach (var p in c.Pawns.Values) foreach (var type in WorkTypes.All) p.Priorities[type] = type == WorkTypes.Build ? 1 : 0;
        if (text.Length > 0) Label(w, text);
        c.ReconcileStorageSigns(true);
        return (c, h, w);
    }

    private static void Label(FakeStoryWorld world, params string[] text)
    {
        world.Set(Sign.X, Sign.Y, Sign.Z, 68, 2);
        world.SignRows.RemoveAll(s => s.At == Sign);
        world.SignRows.Add(new(Sign, Enumerable.Range(0, 4).Select(i => i < text.Length ? text[i] : "").ToArray()));
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Keywords_categories_exact_items_and_exclusions_are_version_aware(string version)
    {
        IGameData data = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var filter = StoryStorageKeywords.Parse(["Food, Seeds", "-apple", "wood", "Storehouse"], data)!;
        Assert.Contains("food", filter.Allow);
        Assert.Contains("plants_seeds", filter.Allow);
        Assert.Contains("wood", filter.Allow);
        Assert.Contains("260", filter.Deny);
        Assert.True(StoryCategories.For(data).Accepts(filter, 297, 0));
        Assert.True(StoryCategories.For(data).Accepts(filter, 295, 0));
        Assert.False(StoryCategories.For(data).Accepts(filter, 260, 0));
        Assert.Null(StoryStorageKeywords.Parse(["Storehouse", "First floor"], data));
        Assert.Null(StoryStorageKeywords.Normalize("999999", data));
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Freehand_wood_keeps_its_category_and_matches_wooden_items_of_any_durability(string version)
    {
        IGameData data = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var filter = StoryStorageKeywords.Parse(["§a[WoOd]"], data)!;
        var categories = StoryCategories.For(data);
        Assert.Contains("wood", filter.Allow);
        Assert.True(categories.Accepts(filter, 17, 0)); // logs
        Assert.True(categories.Accepts(filter, 280, 0)); // sticks: retained category behavior
        Assert.True(categories.Accepts(filter, 270, 40)); // worn wooden pickaxe
        Assert.True(categories.Accepts(filter, 268, 30)); // wooden sword
        Assert.True(categories.Accepts(filter, 324, 0)); // wooden door
        Assert.False(categories.Accepts(filter, 257, 0)); // iron pickaxe
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Freehand_searches_use_phrases_substrings_and_exclusions_without_broadening_metadata(string version)
    {
        IGameData data = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var categories = StoryCategories.For(data);
        var iron = StoryStorageKeywords.Parse(["iRoN", "no axe; !sword"], data)!;
        Assert.True(categories.Accepts(iron, 265, 0)); // iron ingot
        Assert.True(categories.Accepts(iron, 309, 0)); // iron boots
        Assert.False(categories.Accepts(iron, 257, 0)); // pickaxe also contains axe
        Assert.False(categories.Accepts(iron, 267, 0));
        var birch = StoryStorageKeywords.Parse(["BiRcH   WoOd"], data)!;
        Assert.True(categories.Accepts(birch, 17, 2));
        Assert.False(categories.Accepts(birch, 17, 0));
        if (version == "1.12.2") Assert.True(categories.Accepts(birch, 5, 2));
        Assert.Equal(birch.Allow, StoryStorageKeywords.Parse(["birch_wood"], data)!.Allow);
        var exact = StoryStorageKeywords.Parse(["351:4"], data)!;
        Assert.True(categories.Accepts(exact, 351, 4));
        Assert.False(categories.Accepts(exact, 351, 1));
        var fallback = StoryStorageKeywords.Parse(["food seeds"], data)!;
        Assert.True(categories.Accepts(fallback, 297, 0));
        Assert.True(categories.Accepts(fallback, 295, 0));
    }

    [Fact]
    public void Freehand_signs_preserve_text_priority_and_authority_until_physical_removal()
    {
        var (c, _, _) = Rig("WoOd", "!sword", "Storehouse");
        var storage = c.StorageAt(Chest)!;
        Assert.Equal(["WoOd", "!sword", "Storehouse", ""], storage.Signs.Single().Lines);
        Assert.Equal(Chest, c.BestChest(270, 20, Other)!.Chest);
        Assert.Equal(Other, c.BestChest(268, 20, Chest)!.Chest);
        Assert.Null(c.StorageOp("set", [Chest], new StorageFilter { Allow = ["food"] }, null, null).Error);
        Assert.NotNull(storage.PendingFilter);
        Assert.Equal(Chest, c.BestChest(270, 20, Other)!.Chest);
        Assert.NotEmpty(storage.SignRemovals);
    }

    [Fact]
    public void Existing_unchanged_sign_text_recompiles_legacy_filters_after_parser_upgrade()
    {
        var (c, _, _) = Rig("wood");
        var storage = c.StorageAt(Chest)!;
        var original = storage.Signs.Single();
        storage.Signs = [new(original.At, original.Lines, ["wood"], [])];
        c.ReconcileStorageSigns(true);
        Assert.Equal(Chest, c.BestChest(270, 20, Other)!.Chest);
        Assert.Equal(original.Lines, storage.Signs.Single().Lines);
        Assert.Empty(storage.SignRemovals);
    }

    [Fact]
    public void Attached_sign_autoregisters_and_has_priority_above_ordinary_critical_storage()
    {
        var (c, _, w) = Rig();
        c.StorageOp("remove", [Chest], null, null, null);
        c.StorageOp("set", [Other], null, "critical", null);
        Label(w, "food");
        c.ReconcileStorageSigns(true);
        Assert.Equal(Chest, c.BestChest(297, 0, Other)!.Chest);
        Assert.Equal(Other, c.BestChest(4, 0, Chest)!.Chest);
        Assert.Equal("critical", c.State()["storages"]!.AsArray().Single(s => P3.Parse(s!["chest"]) == Chest)!["priority"]!.GetValue<string>());
    }

    [Fact]
    public void Sign_priority_causes_existing_matching_stock_to_be_hauled_in()
    {
        var (c, h, _) = Rig("food");
        c.AddContents(c.StorageAt(Other)!, new Stack(297, 0, 20));
        var job = c.RebalanceJob(c.Pawns[c.P()], h.Live(c.P()));
        Assert.NotNull(job);
        Assert.Equal(Chest.ToString(), job.Json["stash"]![0]!["to"]!.GetValue<string>());
        Assert.Equal(Other.ToString(), job.Json["fetch"]![0]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void Override_waits_for_physical_removal_and_uses_the_checked_sign_job()
    {
        var (c, _, w) = Rig("food");
        Assert.Null(c.StorageOp("set", [Chest], new StorageFilter { Allow = ["wood"] }, null, null).Error);
        var storage = c.StorageAt(Chest)!;
        Assert.Equal(["food"], storage.EffectiveFilter.Allow);
        Assert.Equal(["wood"], storage.PendingFilter!.Allow);
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.Equal(Chest.ToString(), job["storage_sign"]!["chest"]!.GetValue<string>());
        Assert.Equal(4, job["storage_sign"]!["lines"]!.AsArray().Count);
        w.Set(Sign.X, Sign.Y, Sign.Z, 0);
        c.ReconcileStorageSigns(true);
        Assert.Equal(["wood"], storage.EffectiveFilter.Allow);
        Assert.Null(storage.PendingFilter);
        Assert.Empty(storage.SignRemovals);
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void Cancelling_the_map_task_cancels_the_pending_edit_without_recreating_it()
    {
        var (c, _, _) = Rig("food");
        c.StorageOp("set", [Chest], new StorageFilter { Allow = ["wood"] }, null, null);
        var id = c.Designations.Single().Id;
        c.Designate("cancel", null, null, null, null, [id]);
        c.ReconcileStorageSigns(true);
        Assert.Empty(c.Designations);
        Assert.Null(c.StorageAt(Chest)!.PendingFilter);
        Assert.Equal(["food"], c.StorageAt(Chest)!.EffectiveFilter.Allow);
    }

    [Fact]
    public void Edited_sign_supersedes_pending_browser_instruction()
    {
        var (c, _, w) = Rig("food");
        c.StorageOp("set", [Chest], new StorageFilter { Allow = ["wood"] }, null, null);
        Label(w, "seeds");
        c.ReconcileStorageSigns(true);
        Assert.Equal(["plants_seeds"], c.StorageAt(Chest)!.EffectiveFilter.Allow);
        Assert.Null(c.StorageAt(Chest)!.PendingFilter);
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void Unloaded_sign_keeps_authority_and_pending_edit_survives_serialization()
    {
        var (c, _, w) = Rig("food");
        c.StorageOp("set", [Chest], new StorageFilter { Allow = ["wood"] }, null, null);
        var original = c.StorageAt(Chest)!;
        var saved = JsonSerializer.Deserialize<StorageState>(JsonSerializer.Serialize(original))!;
        c.Storages[c.Storages.IndexOf(original)] = saved;
        w.Unloaded.Add((Sign.X, Sign.Z));
        c.ReconcileStorageSigns(true);
        Assert.Equal(["food"], saved.EffectiveFilter.Allow);
        Assert.Equal(["wood"], saved.PendingFilter!.Allow);
        Assert.Single(saved.SignRemovals);
    }

    [Fact]
    public void Double_chest_halves_share_the_attached_sign_and_copy_uses_its_effective_rule()
    {
        var (c, _, w) = Rig();
        w.Set(4, 64, 2, 54);
        c.StorageAt(Chest)!.DoubleWith = new(4, 64, 2);
        w.Set(4, 64, 1, 68, 2);
        w.SignRows.Add(new(new(4, 64, 1), ["food", "", "", ""]));
        c.ReconcileStorageSigns(true);
        c.StorageOp("copy", [Other], null, null, null, Chest);
        Assert.Equal(["food"], c.StorageAt(Other)!.Filter.Allow);
        Assert.Single(c.StorageAt(Chest)!.Signs);
    }

    [Fact]
    public void Entry_removal_excludes_an_item_inside_a_broader_category_and_add_reverses_it()
    {
        var (c, _, _) = Rig();
        c.StorageOp("set", [Chest], new StorageFilter { Allow = ["food"] }, null, null);
        Assert.Null(c.StorageOp("remove_entries", [Chest], new StorageFilter { Allow = ["apple"] }, null, null).Error);
        Assert.Equal(["260"], c.StorageAt(Chest)!.Filter.Deny);
        c.StorageOp("add", [Chest], new StorageFilter { Allow = ["apple", "seeds"] }, null, null);
        Assert.Empty(c.StorageAt(Chest)!.Filter.Deny);
        Assert.Contains("plants_seeds", c.StorageAt(Chest)!.Filter.Allow);
    }

    [Fact]
    public void A_nearby_unattached_sign_does_not_claim_the_chest()
    {
        var (c, _, w) = Rig("food");
        w.Set(Sign.X, Sign.Y, Sign.Z, 68, 3);
        c.ReconcileStorageSigns(true);
        Assert.Empty(c.StorageAt(Chest)!.Signs);
    }

    [Fact]
    public void Signed_chest_never_uses_fallback_or_discards_disallowed_contents_without_a_destination()
    {
        var (c, h, _) = Rig("food");
        c.StorageOp("remove", [Other], null, null, null);
        c.SetHaulOptions(null, true, false);
        c.AddContents(c.StorageAt(Chest)!, new Stack(4, 0, 20));
        Assert.Null(c.RebalanceJob(c.Pawns[c.P()], h.Live(c.P())));
        Assert.Null(c.BestChest(4, 0, Other));
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(4, 0, 20)]);
        var job = c.Next();
        Assert.Null(job["stash"]);
    }
}
