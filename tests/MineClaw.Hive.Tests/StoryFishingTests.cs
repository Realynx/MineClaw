using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fishing zones (docs/web/story-mode.md "Fishing"). Fox 11-03: "Make me a fishing zone where pawns can fish using fishing poles;
/// it must be over water blocks."
/// </summary>
public sealed class StoryFishingTests
{
    /// <summary>A pond of still water at y 63, x 10..12, z 0..1 (6 cells), in the stone floor; the shore all around at y 64.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Pond(int pawns = 1, params Stack[] inv)
    {
        var (c, h, w) = StoryRig.Make(pawns);
        for (int x = 10; x <= 12; x++)
            for (int z = 0; z <= 1; z++)
                w.Set(x, 63, z, 9);
        w.Set(13, 63, 0, 8, 1); // flowing water beside it: not fished
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Fish ? 1 : 0;
        for (int i = 0; i < pawns; i++) h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 0, inv: inv);
        return (c, h, w);
    }

    private static JsonObject Zone(StoryColony c, string id) =>
        c.State()["zones"]!.AsArray().Select(z => z!.AsObject()).Single(z => z["id"]!.GetValue<string>() == id);

    [Fact]
    public void A_fishing_zone_keeps_only_still_water_and_needs_enough_of_it_and_a_shore()
    {
        var (c, _, w) = Pond();
        Assert.Contains("no still water", c.ZoneOp("add", "fish", Area.Of(0, 63, 0, 3, 63, 3)).Error);
        Assert.Contains("at least 4 water cells", c.ZoneOp("add", "fish", Area.Of(10, 63, 0, 11, 63, 0)).Error);
        // Over the pond and its banks: the 6 still-water cells, not the flowing one, not the land.
        c.ZoneOp("add", "farm", Area.Of(8, 63, 0, 9, 63, 1)); // a field on the bank stays: land isn't the fishing zone's
        var (res, err) = c.ZoneOp("add", "fish", Area.Of(8, 63, -1, 14, 63, 2), name: "Pond");
        Assert.True(err == null, err);
        var z = c.Zones.Single(x => x.Kind == "fish");
        Assert.Equal(6, z.Cells.Count);
        Assert.False(z.Contains(13, 0));
        Assert.Equal(4, c.Zones.Single(x => x.Kind == "farm").Cells.Count);
        Assert.Equal(6, Zone(c, res!["id"]!.GetValue<string>())["water_cells"]!.GetValue<int>());

        // A covered pool with walls all round: no shore to stand on.
        var (c2, _, w2) = Pond();
        for (int x = 6; x <= 16; x++)
            for (int zz = -4; zz <= 5; zz++)
                for (int y = 64; y <= 67; y++)
                    if (!(x is >= 10 and <= 12 && zz is >= 0 and <= 1)) w2.Set(x, y, zz, 1);
        Assert.Contains("no shore", c2.ZoneOp("add", "fish", Area.Of(10, 63, 0, 12, 63, 1)).Error);
    }

    [Fact]
    public void Fishers_stand_on_spread_shore_cells_with_a_rod_and_their_catch_counts_and_goes_to_storage()
    {
        var (c, h, w) = Pond(2, new Stack(StoryColony.FishingRod, 0, 1));
        var id = c.ZoneOp("add", "fish", Area.Of(10, 63, 0, 12, 63, 1)).Result!["id"]!.GetValue<string>();
        var a = c.Next(0);
        Assert.Equal("fish", a["kind"]!.GetValue<string>());
        Assert.Equal(WorkTypes.Fish, a["type"]!.GetValue<string>());
        var at = P3.Parse(JsonNode.Parse("[" + a["at"]!.GetValue<string>() + "]"))!.Value;
        Assert.Equal(64, at.Y);
        Assert.False(at.X is >= 10 and <= 12 && at.Z is >= 0 and <= 1); // on the shore, not in the water
        var water = P3.Parse(JsonNode.Parse("[" + a["water"]!.GetValue<string>() + "]"))!.Value;
        Assert.Equal(9, w.Block(water.X, water.Y, water.Z).Id);
        Assert.True(Math.Abs(water.X - at.X) <= StoryColony.ShoreReach && Math.Abs(water.Z - at.Z) <= StoryColony.ShoreReach);
        Assert.Equal(StoryColony.FishSessionSeconds, a["s"]!.GetValue<int>());
        var b = c.Next(1);
        Assert.Equal("fish", b["kind"]!.GetValue<string>());
        var at2 = P3.Parse(JsonNode.Parse("[" + b["at"]!.GetValue<string>() + "]"))!.Value;
        Assert.True(StoryWander.Gap(at, at2) >= 2, $"{at} and {at2} too close");
        Assert.Equal(2, Zone(c, id)["fishers"]!.AsArray().Count);

        // The session ends: its catch counts, and the fish go to storage.
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(StoryColony.FishingRod, 0, 1), new Stack(StoryColony.RawFish, 0, 3)]);
        c.Report(c.P(), a["id"]!.GetValue<string>(), "done", "fished: 4 cast(s), 3 bite(s), 3 fish caught", null);
        Assert.Equal(3, c.Zones.Single(z => z.Id == id).Catches);
        // It keeps its catch and fishes on (StoryLootBatchingTests); with its pockets nearly full it takes it all to storage.
        var more = c.Next(0);
        Assert.Equal("fish", more["kind"]!.GetValue<string>());
        h.Lives[c.P()] = h.Lives[c.P()] with { FreeSlots = 2 };
        c.Report(c.P(), more["id"]!.GetValue<string>(), "done", "fished: 1 cast(s), 0 bite(s), 0 fish caught", null);
        var next = c.Next(0);
        Assert.NotNull(next["stash"]);
        Assert.Contains(w.Data.Item(StoryColony.RawFish).Name, next["stash"]![0]!["items"]!.ToJsonString());
    }

    [Fact]
    public void The_rod_is_fetched_from_storage_or_a_bill_crafts_one_and_bills_stop_the_fishing()
    {
        var (c, h, w) = Pond();
        var id = c.ZoneOp("add", "fish", Area.Of(10, 63, 0, 12, 63, 1)).Result!["id"]!.GetValue<string>();
        // No rod, no string: the colony hears where string comes from; nothing to do.
        Assert.NotEqual("fish", c.Next()["kind"]!.GetValue<string>());
        Assert.Contains(c.Alerts, x => x.Text.Contains("no string"));
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        // String in storage and a crafting table: a bill for a rod.
        w.Set(3, 64, 0, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        c.AddContents(c.StorageAt(new P3(0, 64, 4))!, new Stack(StoryColony.StringItem, 0, 2));
        c.Next();
        Assert.Equal(StoryRecipes.For(w.Data, "crafting_table").First(r => r.Output.Id == StoryColony.FishingRod).Id,
            Assert.Single(c.Benches.Single().Bills).Recipe);
        if (c.CurrentOf(c.P()) is { } cur2) c.Report(c.P(), cur2.Id, "done", null, null);
        // A rod in storage: fetched for the session.
        c.AddContents(c.StorageAt(new P3(0, 64, 4))!, new Stack(StoryColony.FishingRod, 0, 1));
        var j = c.Next();
        Assert.Equal("fish", j["kind"]!.GetValue<string>());
        Assert.Equal("0,64,4", j["fetch"]![0]!["from"]!.GetValue<string>());
        if (c.CurrentOf(c.P()) is { } cur3) c.Report(c.P(), cur3.Id, "done", null, null);
        // A bill: fish until 5 raw fish are stored; with 5 in storage the zone rests.
        var (bill, berr) = c.ZoneOp("bill_add", null, null, id, grow: new JsonObject { ["mode"] = "until", ["target"] = 5 });
        Assert.True(berr == null, berr);
        Assert.Equal("fish", c.Zones.Single(z => z.Id == id).Bills!.Single().Plant);
        c.AddContents(c.StorageAt(new P3(0, 64, 4))!, new Stack(StoryColony.RawFish, 0, 5));
        Assert.StartsWith("all bills met", Zone(c, id)["status"]!.GetValue<string>());
        Assert.NotEqual("fish", c.Next()["kind"]!.GetValue<string>());
        Assert.Contains("plant must be fish", c.ZoneOp("bill_add", null, null, id, grow: new JsonObject { ["plant"] = "wheat" }).Error);
    }
}
