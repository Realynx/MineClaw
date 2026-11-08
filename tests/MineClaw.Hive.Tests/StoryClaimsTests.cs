using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Reservations that make sense (docs/web/story-mode.md "Reservations"): an owner's smelt bill may use the iron ore a water build's
/// bucket chain holds (it does that chain's step; no second, automatic smelt), a partial batch on what is free, a reason that names
/// the build and the amounts, the owner's release; and furnaces that are collected: done on collection, collected even when the bill is
/// gone, the expected progress in between.
/// </summary>
public class StoryClaimsTests
{
    private static readonly P3 Table = new(3, 64, 0), Furnace = new(6, 64, 0), Chest = new(6, 64, 2);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, FakeClock T) Rig(params Stack[] stock)
    {
        var clock = new FakeClock();
        var (c, h, w) = StoryRig.Make(pawns: 2, data: Release1122GameData.Instance, time: clock);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t is WorkTypes.Craft or WorkTypes.Cook ? 1 : 0;
        w.Set(Table.X, Table.Y, Table.Z, 58);
        w.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        Assert.Null(c.WorkbenchOp("designate", Furnace).Error);
        return (c, h, w, clock);
    }

    private static string Bill(StoryColony c, JsonObject bill)
    {
        var r = c.BillsOp("add", new JsonObject { ["bench"] = Furnace.Json(), ["bill"] = bill });
        Assert.Null(r.Error);
        return r.Result!["id"]!.GetValue<string>();
    }

    private static JsonArray Cells(params (int X, int Y, int Z)[] cells) => new(cells.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray());

    private static string Water(StoryColony c)
    {
        var r = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "water", ["blocks"] = Cells((10, 64, 10)) }), null);
        Assert.Null(r.Error);
        return r.Result!["ids"]![0]!.GetValue<string>();
    }

    private static WorkbenchState Fur(StoryColony c) => c.Benches.Single(b => b.Kind == "furnace");

    [Fact]
    public void An_owner_smelt_bill_uses_the_ore_a_bucket_chain_holds_and_the_chain_smelts_no_second_time()
    {
        var (c, h, w, _) = Rig(new Stack(15, 0, 3), new Stack(263, 0, 2));
        var build = Water(c);
        c.EnsureMaterials();
        // The water build's bucket: 3 iron ore claimed as its chain's input, an auto smelt bill for it.
        var ore = c.State()["reservations"]!.AsArray().Single(r => r!["item"]![0]!.GetValue<int>() == 15)!;
        Assert.Equal((3, build, "bucket", true), (ore["count"]!.GetValue<int>(), ore["build"]!.GetValue<string>(), ore["for_item"]!.GetValue<string>(), ore["chain"]!.GetValue<bool>()));
        Assert.Contains(Fur(c).Tasks, b => b.ForBuild == build);

        // The owner smelts iron: not "reserved for builds" — it is exactly the chain's step. The automatic task
        // remains as a fallback until a real load is committed; an idle manual bill cannot monopolize build demand.
        var id = Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 3 });
        c.EnsureMaterials();
        Assert.Contains(Fur(c).Tasks, b => b.ForBuild == build);
        var j = c.Next();
        Assert.Equal("smelt", j["kind"]!.GetValue<string>());
        Assert.Equal(id, c.CurrentOf(c.P())!.SourceId);
        Assert.Null(Fur(c).Bills.Single(b => b.Id == id).SuspendedReason);
        Assert.Equal(3, j["n"]!.GetValue<int>());

        // Loaded: the ingots on their way count for the build (its next step is the bucket at the table).
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        c.EnsureMaterials();
        Assert.Contains(c.Benches.Single(b => b.Kind == "crafting_table").Tasks, b => b.ForBuild == build && b.Recipe.Contains("bucket"));
        Assert.DoesNotContain(Fur(c).Tasks, b => b.ForBuild == build);
    }

    [Fact]
    public void A_partial_batch_runs_on_what_is_free_and_all_reserved_says_who_holds_it()
    {
        var (c, h, w, t) = Rig(new Stack(15, 0, 5), new Stack(263, 0, 2));
        var r = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["blocks"] = Cells((10, 64, 10), (11, 64, 10), (12, 64, 10)), ["material"] = "iron_ore" }), null);
        Assert.Null(r.Error);
        var build = r.Result!["ids"]![0]!.GetValue<string>();
        c.EnsureMaterials();
        var id = Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 5 });
        var j = c.Next();
        Assert.Equal("smelt", j["kind"]!.GetValue<string>());
        Assert.Equal(2, j["n"]!.GetValue<int>()); // 3 of the 5 are the build's own blocks
        c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "busy", null);

        // With only the build's 3 left: the bill waits and says who holds them.
        c.TakeContents(c.StorageAt(Chest)!, 15, 0, 2);
        t.Now += TimeSpan.FromMinutes(5); // the failed furnace rests a while
        c.EnsureMaterials();
        Assert.NotEqual("smelt", c.Next(1)["kind"]!.GetValue<string>());
        Assert.Equal($"3 iron ore reserved for build {build} (iron ore) — 0 free", Fur(c).Bills.Single(b => b.Id == id).SuspendedReason);

        // The owner releases the claim (the build's own item: the build pauses) and the bill can run.
        var rel = c.BillsOp("release_claim", new JsonObject { ["build"] = build, ["item"] = "iron_ore" });
        Assert.Null(rel.Error);
        Assert.Equal("build paused", rel.Result!["done"]!.GetValue<string>());
        Assert.Equal("paused", c.Builds.Single().Status);
        Assert.DoesNotContain(c.State()["reservations"]!.AsArray(), x => x!["build"]!.GetValue<string>() == build);
    }

    [Fact]
    public void A_reservation_of_something_else_never_holds_a_bill_back()
    {
        // Live 11-04: "the wheat in storage is reserved for builds" — nothing reserved wheat; a build held cobblestone.
        var (c, h, w, _) = Rig(new Stack(296, 0, 30), new Stack(4, 0, 3));
        var r = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["blocks"] = Cells((10, 64, 10), (11, 64, 10), (12, 64, 10)), ["material"] = "cobblestone" }), null);
        Assert.Null(r.Error);
        c.EnsureMaterials();
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = Table.Json(), ["bill"] = new JsonObject { ["recipe"] = "craft:bread", ["mode"] = "forever" } }).Error);
        var j = c.Next();
        Assert.Equal("craft", j["kind"]!.GetValue<string>());
        Assert.Null(c.Benches.Single(b => b.Kind == "crafting_table").Bills.Single().SuspendedReason);
    }

    [Fact]
    public void A_loaded_furnace_is_collected_when_done_and_the_bill_counts_on_collection()
    {
        var (c, h, w, t) = Rig(new Stack(15, 0, 3), new Stack(263, 0, 2));
        var id = Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 3 });
        var j = c.Next();
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        var bill = Fur(c).Bills.Single();
        Assert.Equal(0, bill.Done); // loading isn't making
        t.Now += TimeSpan.FromSeconds(10.5);
        var est = c.State()["workbenches"]!.AsArray().Single(b => b!["kind"]!.GetValue<string>() == "furnace")!["smelting"]!;
        Assert.Equal(1, est["output"]![2]!.GetValue<int>()); // one ingot by now, two ore left
        Assert.Equal(2, est["input"]![2]!.GetValue<int>());
        Assert.True(est["estimated"]!.GetValue<bool>());
        Assert.NotEqual("collect_furnace", c.Next(1)["kind"]!.GetValue<string>()); // not done yet
        c.Report(c.P(1), c.CurrentOf(c.P(1))?.Id ?? "", "done", null, null);
        t.Now += TimeSpan.FromSeconds(25);
        var k = c.Next(1);
        Assert.Equal("collect_furnace", k["kind"]!.GetValue<string>());
        Assert.Equal("6,64,0", k["at"]!.GetValue<string>());
        c.Report(c.P(1), k["id"]!.GetValue<string>(), "done", "collected 3 iron ingot; still smelting nothing", null);
        Assert.Equal(3, bill.Done);
        Assert.Null(Fur(c).LoadedBill);
        Assert.Null(c.State()["workbenches"]!.AsArray().Single(b => b!["kind"]!.GetValue<string>() == "furnace")!["smelting"]);
    }

    [Fact]
    public void A_furnace_whose_bill_is_gone_is_still_collected_and_a_partial_collect_keeps_the_rest_loaded()
    {
        var (c, h, w, t) = Rig(new Stack(15, 0, 3), new Stack(263, 0, 2));
        var id = Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 3 });
        var j = c.Next();
        Assert.Null(c.BillsOp("remove", new JsonObject { ["id"] = id }).Error); // gone while the pawn walks
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        Assert.NotNull(Fur(c).LoadedBill);
        t.Now += TimeSpan.FromSeconds(31);
        var k = c.Next(1);
        Assert.Equal("collect_furnace", k["kind"]!.GetValue<string>());
        c.Report(c.P(1), k["id"]!.GetValue<string>(), "done", "collected 2 iron ingot; still smelting 1 iron ore", null);
        Assert.Equal(1, Fur(c).LoadedCount); // one still in it: collected later
        Assert.NotNull(Fur(c).ReadyAt);
    }
}
