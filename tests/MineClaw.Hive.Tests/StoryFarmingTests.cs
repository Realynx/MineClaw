using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Growing zones (docs/web/story-mode.md "Farming"). Fox 11-03: "I don't see the ability to plant a specific type of plant, or how
/// acquiring the seeds will work." A farm zone takes a plant; pawns till, sow, harvest and sow again; the zone says where its seeds
/// come from and what is missing, and can designate tall grass to cut (wheat) or queue a seed bill (pumpkin, melon).
/// </summary>
public sealed class StoryFarmingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new(); // one per test (xunit makes an instance per test)

    /// <summary>The world changed (a crop grew): past the zone scan's 5 s cache.</summary>
    private void Later() => _clock.Now += TimeSpan.FromSeconds(6);

    private (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Farm(IGameData? data = null, params Stack[] inv)
    {
        var (c, h, w) = StoryRig.Make(data: data, time: _clock);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Farm ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: inv);
        return (c, h, w);
    }

    private static JsonObject Zone(StoryColony c, string id) =>
        c.State()["zones"]!.AsArray().Select(z => z!.AsObject()).Single(z => z["id"]!.GetValue<string>() == id);

    private static string AddZone(StoryColony c, Area a, string plant, string? name = null)
    {
        var (res, err) = c.ZoneOp("add", "farm", a, name: name, grow: new JsonObject { ["plant"] = plant });
        Assert.True(err == null, err);
        return res!["id"]!.GetValue<string>();
    }

    [Fact]
    public void Each_pawn_body_gets_the_growing_zones_to_crouch_walk_through()
    {
        // Fox 11-04: "The pawns need to crouch-walk through growing zones": the bodies' planner gets the farm zones as a filter.
        var (c, h, w) = Farm();
        c.Tick();
        Assert.Null(h.GrowZones[c.P()]); // no growing zone yet
        for (int x = 10; x <= 12; x++) { w.Set(x, 62, 0, 3); w.Set(x, 63, 0, 60); w.Set(x, 63, 1, 9); }
        AddZone(c, Area.Of(10, 63, 0, 12, 63, 0), "wheat");
        c.Tick();
        var f = h.GrowZones[c.P()];
        Assert.NotNull(f);
        Assert.True(f!(11, 63, 0));
        Assert.True(f(11, 64, 0));
        Assert.False(f(13, 64, 0));
        Assert.False(f(11, 64, 1));
        Assert.False(f(11, 70, 0));
        c.Tick();
        Assert.Same(f, h.GrowZones[c.P()]); // unchanged zones: the same snapshot, no rebuild
    }

    [Fact]
    public void A_growing_zone_takes_a_plant_this_version_grows_on_soil_that_suits_it()
    {
        var (c, _, w) = Farm();
        for (int x = 10; x <= 12; x++) w.Set(x, 63, 0, 3); // dirt, no water anywhere
        var a = Area.Of(10, 63, 0, 12, 63, 0);
        Assert.Contains("plant must be one of", c.ZoneOp("add", "farm", a, grow: new JsonObject { ["plant"] = "nether_wart" }).Error); // not in b1.8.1
        Assert.Contains("plant must be one of", c.ZoneOp("add", "farm", a, grow: new JsonObject { ["plant"] = "tomatoes" }).Error);
        Assert.Contains("grows on sand", c.ZoneOp("add", "farm", a, grow: new JsonObject { ["plant"] = "cactus" }).Error);
        Assert.Contains("beside water", c.ZoneOp("add", "farm", a, grow: new JsonObject { ["plant"] = "sugar_cane" }).Error);
        Assert.Contains("only farm", c.ZoneOp("add", "chop", a, grow: new JsonObject { ["plant"] = "wheat" }).Error);
        // Wheat on dirt with no water near: made, with a warning.
        var (res, err) = c.ZoneOp("add", "farm", a, name: "Field", grow: new JsonObject { ["plant"] = "wheat" });
        Assert.Null(err);
        Assert.Contains("no water", res!["warnings"]![0]!.GetValue<string>());
        var id = res["id"]!.GetValue<string>();
        // Water beside it: sugar cane fits now; an update changes the plant and the switches.
        w.Set(11, 63, 1, 9);
        var (up, uerr) = c.ZoneOp("update", null, null, id, grow: new JsonObject { ["plant"] = "sugar_cane", ["harvest"] = false });
        Assert.True(uerr == null, uerr);
        Assert.Equal("sugar_cane", c.Zones.Single().Plant);
        Assert.False(c.Zones.Single().Harvest);
        var z = Zone(c, id);
        Assert.Equal("sugar_cane", z["plant"]!.GetValue<string>());
        Assert.False(z["harvest"]!.GetValue<bool>());
        Assert.True(z["sow"]!.GetValue<bool>());
        // The catalogue per version.
        var plants = c.State()["plants"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("wheat", plants);
        Assert.DoesNotContain("carrots", plants);
        var modern = StoryPlants.For(Release1122GameData.Instance).Select(p => p.Id).ToList();
        Assert.Contains("carrots", modern);
        Assert.Contains("nether_wart", modern);
        Assert.NotNull(c.ZoneOp("update", null, null, "z999", grow: new JsonObject { ["plant"] = "wheat" }).Error);
    }

    [Fact]
    public void Wheat_is_tilled_sown_harvested_and_sown_again()
    {
        var (c, h, w) = Farm(null, new Stack(290, 0, 1), new Stack(295, 0, 5));
        w.Set(10, 63, 0, 2);   // grass
        w.Set(10, 63, 2, 9);   // water two blocks off
        var id = AddZone(c, Area.Of(10, 63, 0, 10, 63, 0), "wheat", "Wheat field");
        Assert.Equal("0/1 sown, 0 ripe", Zone(c, id)["status"]!.GetValue<string>());

        var till = c.Next();
        Assert.Equal("till", till["kind"]!.GetValue<string>());
        Assert.Equal("10,63,0", till["at"]!.GetValue<string>());
        Assert.Equal(w.Data.Item(290).Name, till["hoe"]!.GetValue<string>());
        w.Set(10, 63, 0, 60); // the hoe made farmland
        Later();
        c.Report(c.P(), till["id"]!.GetValue<string>(), "done", null, null);

        var sow = c.Next();
        Assert.Equal("place", sow["kind"]!.GetValue<string>());
        Assert.Equal("10,64,0", sow["at"]!.GetValue<string>());
        Assert.Equal(w.Data.Item(295).Name, sow["block"]!.GetValue<string>());
        w.Set(10, 64, 0, 59, 0);
        Later();
        c.Report(c.P(), sow["id"]!.GetValue<string>(), "done", null, null);
        Assert.Equal("1/1 sown, 0 ripe", Zone(c, id)["status"]!.GetValue<string>());
        Assert.NotEqual("place", c.Next()["kind"]!.GetValue<string>()); // growing: nothing to do there
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);

        w.Set(10, 64, 0, 59, 7); // ripe
        Later();
        c.Report(c.P(), c.CurrentOf(c.P())?.Id ?? "", "done", null, null);
        var harvest = c.Next();
        Assert.Equal("harvest", harvest["kind"]!.GetValue<string>());
        Assert.Equal("10,64,0", harvest["at"]!.GetValue<string>());
        Assert.Equal(w.Data.Item(295).Name, harvest["replant"]!.GetValue<string>()); // sown again at once
        Assert.Equal(WorkTypes.Farm, harvest["type"]!.GetValue<string>());
        w.Set(10, 64, 0, 59, 0); // the script replanted
        Later();
        c.Report(c.P(), harvest["id"]!.GetValue<string>(), "done", null, null);
        Assert.Equal("1/1 sown, 0 ripe", Zone(c, id)["status"]!.GetValue<string>());
        // Harvest off: a ripe crop stays.
        c.ZoneOp("update", null, null, id, grow: new JsonObject { ["harvest"] = false });
        w.Set(10, 64, 0, 59, 7);
        Later();
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        Assert.NotEqual("harvest", c.Next()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void With_no_seeds_the_zone_says_where_they_come_from_and_seeking_them_designates_tall_grass()
    {
        var (c, h, w) = Farm();
        w.Set(10, 63, 0, 60); w.Set(11, 63, 0, 60);
        w.Set(10, 63, 1, 9);
        w.Set(15, 64, 2, 31, 1); w.Set(16, 64, 3, 31, 2); w.Set(17, 64, 3, 37); // tall grass, a fern, a flower
        var id = AddZone(c, Area.Of(10, 63, 0, 11, 63, 0), "wheat", "North field");
        var seeds = Zone(c, id)["seeds"]!;
        Assert.Equal(0, seeds["have"]!.GetValue<int>());
        Assert.Equal(2, seeds["needed_for_empty"]!.GetValue<int>());
        Assert.Contains("cut tall grass", seeds["missing"]!.GetValue<string>());
        Assert.Equal("seek_seeds", seeds["action"]!.GetValue<string>());
        // A farm pawn finds nothing to sow: the colony hears why, once.
        Assert.NotEqual("place", c.Next()["kind"]!.GetValue<string>());
        Assert.Single(c.Alerts, a => a.Text.Contains("No wheat seeds"));
        // The explicit action: cut the grass and ferns near the zone (never the flower).
        var (res, err) = c.ZoneOp("seek_seeds", null, null, id);
        Assert.True(err == null, err);
        Assert.Equal(2, res!["count"]!.GetValue<int>());
        Assert.Equal(new[] { new P3(15, 64, 2), new P3(16, 64, 3) }, c.Designations.Where(d => d.Kind == "cut").Select(d => d.At!.Value).OrderBy(p => p.X).ToArray());
        Assert.All(c.Designations, d => Assert.True(d.Auto)); // the colony chose these cells: a cave plant among them would be left alone
        // Seeds in storage: the zone has them, a pawn fetches them to sow.
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        c.AddContents(c.StorageAt(new P3(0, 64, 4))!, new Stack(295, 0, 6));
        Assert.Null(Zone(c, id)["seeds"]!["missing"]);
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Farm ? 1 : 0;
        var sow = c.Next();
        Assert.Equal("place", sow["kind"]!.GetValue<string>());
        Assert.Equal("0,64,4", sow["fetch"]![0]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void Pumpkin_seeds_get_a_bill_when_a_pumpkin_is_stored_and_stems_stay_when_the_fruit_is_picked()
    {
        var (c, h, w) = Farm(Release1122GameData.Instance);
        w.Set(3, 64, 0, 58); // a crafting table at home, the colony's
        Assert.Null(c.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        w.Set(10, 63, 0, 60); w.Set(12, 63, 0, 60);
        w.Set(10, 63, 2, 9);
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        c.AddContents(c.StorageAt(new P3(0, 64, 4))!, new Stack(86, 0, 1));
        var id = AddZone(c, Area.Of(10, 63, 0, 12, 63, 0), "pumpkin");
        Assert.Equal("craft_seeds", Zone(c, id)["seeds"]!["action"]!.GetValue<string>());
        c.Next(); // no seeds: the pumpkin in storage becomes a bill for seeds
        var bill = Assert.Single(c.Benches.Single().Tasks);
        Assert.Equal(StoryRecipes.For(w.Data, "crafting_table").Single(r => r.Output.Id == 361).Id, bill.Recipe);
        Assert.Contains("needs", bill.Reason); // a craft task with its reason, never a bill in the owner's list
        // A grown pumpkin beside a stem: the pumpkin is picked, the stem stays.
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        w.Set(10, 64, 0, 104, 7); w.Set(11, 64, 0, 86);
        Later();
        var pick = c.Next();
        Assert.Equal("dig", pick["kind"]!.GetValue<string>());
        Assert.Equal("11,64,0", pick["at"]!.GetValue<string>());
        Assert.Null(pick["replant"]);
    }

    [Fact]
    public void Sugar_cane_is_cut_above_its_bottom_block_and_a_missing_hoe_gets_a_bill()
    {
        var (c, h, w) = Farm();
        w.Set(10, 63, 0, 12); w.Set(11, 63, 0, 9);                                  // sand by water
        w.Set(10, 64, 0, 83); w.Set(10, 65, 0, 83); w.Set(10, 66, 0, 83);            // cane, three high
        AddZone(c, Area.Of(10, 63, 0, 10, 63, 0), "sugar_cane");
        var cut = c.Next();
        Assert.Equal("dig", cut["kind"]!.GetValue<string>());
        Assert.Equal("10,65,0", cut["at"]!.GetValue<string>()); // the bottom block stays and grows again

        // Wheat on grass with no hoe anywhere: a crafting table gets a hoe bill.
        var (c2, _, w2) = Farm(null, new Stack(295, 0, 3));
        w2.Set(3, 64, 0, 58);
        Assert.Null(c2.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        w2.Set(10, 63, 0, 2); w2.Set(10, 63, 2, 9);
        AddZone(c2, Area.Of(10, 63, 0, 10, 63, 0), "wheat");
        Assert.NotEqual("till", c2.Next()["kind"]!.GetValue<string>());
        Assert.Equal(StoryRecipes.For(w2.Data, "crafting_table").First(r => r.Output.Id == 290).Id, Assert.Single(c2.Benches.Single().Tasks).Recipe);
    }
}
