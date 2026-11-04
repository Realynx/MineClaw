using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Planting bills (docs/web/story-mode.md "Farming"). Fox 11-03: "I should be able to add harvest bills, or maybe planting bills, to
/// zones — as a way to choose which types of seeds to plant there. Those zones need to be hoed, then sown, and harvested automatically."
/// </summary>
public sealed class StoryGrowBillsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private void Later() => _clock.Now += TimeSpan.FromSeconds(6);

    private (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Farm(params Stack[] inv)
    {
        var (c, h, w) = StoryRig.Make(time: _clock);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Farm ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: inv);
        // Farmland at x 10 and 12 (pumpkins skip every other row: x 11), water near.
        for (int x = 10; x <= 12; x++) w.Set(x, 63, 0, 60);
        w.Set(10, 63, 2, 9);
        return (c, h, w);
    }

    private static JsonObject Zone(StoryColony c, string id) =>
        c.State()["zones"]!.AsArray().Select(z => z!.AsObject()).Single(z => z["id"]!.GetValue<string>() == id);

    private static JsonObject Bill(StoryColony c, string op, string zone, JsonObject body)
    {
        var (res, err) = c.ZoneOp(op, null, null, zone, grow: body);
        Assert.True(err == null, err);
        return res!.AsObject();
    }

    private static string AddZone(StoryColony c, string plant) =>
        c.ZoneOp("add", "farm", Area.Of(10, 63, 0, 12, 63, 0), grow: new JsonObject { ["plant"] = plant }).Result!["id"]!.GetValue<string>();

    private void Finish(StoryColony c)
    {
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        Later();
    }

    [Fact]
    public void The_first_unmet_bill_decides_what_is_sown_and_until_switches_on_storage()
    {
        var (c, _, w) = Farm(new Stack(295, 0, 8), new Stack(361, 0, 8));
        var id = AddZone(c, "wheat");
        var first = Zone(c, id)["bills"]!.AsArray().Single()!;
        Assert.Equal("wheat", first["plant"]!.GetValue<string>()); // the plant is a one-bill list
        var wheatBill = first["id"]!.GetValue<string>();
        Bill(c, "bill_update", id, new JsonObject { ["bill"] = wheatBill, ["mode"] = "until", ["target"] = 10 });
        var pumpkinBill = Bill(c, "bill_add", id, new JsonObject { ["plant"] = "pumpkin" })["bill"]!.GetValue<string>();
        Assert.Equal(wheatBill, Zone(c, id)["active_bill"]!.GetValue<string>());
        var sow = c.Next();
        Assert.Equal("place", sow["kind"]!.GetValue<string>());
        Assert.Equal(w.Data.Item(295).Name, sow["block"]!.GetValue<string>());
        Finish(c);

        // 10 wheat in storage: the wheat bill is met, sowing moves to the pumpkins.
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        c.AddContents(c.StorageAt(new P3(0, 64, 4))!, new Stack(296, 0, 10));
        var z = Zone(c, id);
        Assert.Equal("pumpkin", z["active_plant"]!.GetValue<string>());
        Assert.Equal(pumpkinBill, z["active_bill"]!.GetValue<string>());
        var wb = z["bills"]!.AsArray().First()!;
        Assert.True(wb["met"]!.GetValue<bool>());
        Assert.Equal(10, wb["stored"]!.GetValue<int>());
        var next = c.Next();
        Assert.Equal("place", next["kind"]!.GetValue<string>());
        Assert.Equal(w.Data.Item(361).Name, next["block"]!.GetValue<string>());
        Finish(c);

        // Reorder: the pumpkins first (the zone's plant follows the first bill); pause them: every bill met, nothing sown.
        var ro = Bill(c, "bill_reorder", id, new JsonObject { ["ids"] = new JsonArray(pumpkinBill, wheatBill) });
        Assert.Equal([pumpkinBill, wheatBill], ro["ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal("pumpkin", c.Zones.Single().Plant);
        Bill(c, "bill_pause", id, new JsonObject { ["bill"] = pumpkinBill });
        z = Zone(c, id);
        Assert.Null(z["active_bill"]);
        Assert.StartsWith("all bills met", z["status"]!.GetValue<string>());
        Assert.NotEqual("place", c.Next()["kind"]!.GetValue<string>());
        Finish(c);
        Bill(c, "bill_resume", id, new JsonObject { ["bill"] = pumpkinBill });
        Assert.Equal(pumpkinBill, Zone(c, id)["active_bill"]!.GetValue<string>());
        Bill(c, "bill_remove", id, new JsonObject { ["bill"] = pumpkinBill });
        Assert.Single(c.Zones.Single().Bills!);
        Assert.Equal("wheat", c.Zones.Single().Plant);
    }

    [Fact]
    public void Count_bills_count_harvests_and_a_crop_in_the_ground_is_harvested_not_ripped_out()
    {
        var (c, _, w) = Farm(new Stack(295, 0, 8), new Stack(361, 0, 8));
        var id = AddZone(c, "wheat");
        var wheatBill = c.Zones.Single().Bills!.Single().Id;
        Bill(c, "bill_update", id, new JsonObject { ["bill"] = wheatBill, ["mode"] = "count", ["target"] = 1 });
        Bill(c, "bill_add", id, new JsonObject { ["plant"] = "pumpkin" });
        w.Set(10, 64, 0, 59, 7); // ripe wheat
        w.Set(12, 64, 0, 59, 7);
        var harvest = c.Next();
        Assert.Equal("harvest", harvest["kind"]!.GetValue<string>());
        Assert.Equal(w.Data.Item(295).Name, harvest["replant"]!.GetValue<string>()); // still sowing wheat: sown again
        var at = harvest["at"]!.GetValue<string>();
        w.Set(at == "10,64,0" ? 10 : 12, 64, 0, 0);
        Later();
        c.Report(c.P(), harvest["id"]!.GetValue<string>(), "done", null, null);
        var bill = c.Zones.Single().Bills!.First();
        Assert.Equal(1, bill.Done);
        Assert.Equal("pumpkin", Zone(c, id)["active_plant"]!.GetValue<string>());
        // The other ripe wheat is harvested as wheat, and not sown again (the zone sows pumpkins now).
        Later();
        var second = c.Next();
        Assert.Equal("harvest", second["kind"]!.GetValue<string>());
        Assert.Null(second["replant"]);
    }

    [Fact]
    public void Bills_validate_the_plant_for_this_version_and_the_soil()
    {
        var (c, _, w) = Farm();
        var id = AddZone(c, "wheat");
        Assert.Contains("plant must be one of", c.ZoneOp("bill_add", null, null, id, grow: new JsonObject { ["plant"] = "carrots" }).Error); // not in b1.8.1
        Assert.Contains("grows on sand", c.ZoneOp("bill_add", null, null, id, grow: new JsonObject { ["plant"] = "cactus" }).Error);
        Assert.Contains("mode must be", c.ZoneOp("bill_add", null, null, id, grow: new JsonObject { ["plant"] = "melon", ["mode"] = "sometimes" }).Error);
        Assert.Contains("no such bill", c.ZoneOp("bill_pause", null, null, id, grow: new JsonObject { ["bill"] = "gb999" }).Error);
        var chop = c.ZoneOp("add", "chop", Area.Of(30, 63, 0, 31, 63, 1)).Result!["id"]!.GetValue<string>();
        Assert.Contains("only growing and fishing zones", c.ZoneOp("bill_add", null, null, chop, grow: new JsonObject { ["plant"] = "wheat" }).Error);
        // Per bill: its seeds and where they come from.
        Bill(c, "bill_add", id, new JsonObject { ["plant"] = "melon", ["mode"] = "count", ["target"] = 5 });
        var bills = Zone(c, id)["bills"]!.AsArray();
        Assert.Equal(2, bills.Count);
        Assert.Contains("cut tall grass", bills[0]!["seeds"]!["missing"]!.GetValue<string>());
        Assert.Equal("seek_seeds", bills[0]!["seeds"]!["action"]!.GetValue<string>());
        Assert.Equal("craft_seeds", bills[1]!["seeds"]!["action"]!.GetValue<string>());
        // update {plant} (the Growing zones window's plant picker before bills) changes the first bill.
        c.ZoneOp("update", null, null, id, grow: new JsonObject { ["plant"] = "pumpkin" });
        Assert.Equal(["pumpkin", "melon"], c.Zones.First(z => z.Id == id).Bills!.Select(b => b.Plant).ToArray());
    }
}
