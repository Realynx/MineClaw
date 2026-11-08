using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A furnace load left without fuel (docs/web/story-mode.md "Fuel"). Live 11-04 story-1 (release 20261104-181056): "collect_furnace
/// 218,64,399 failed: nothing to collect; furnace (3 slots, 2 free) progress 0%; in=oak wood x1 fuel=- out=-: oak wood x1" ×108, a
/// collect every 20 s that rested the pawns and the bench and never smelted anything. The credit counted the leftovers of lit logs as if
/// they kept, and the one log a pawn carried was both the input and the fuel.
/// </summary>
public class StoryFurnaceRefuelTests
{
    private static readonly P3 Furnace = new(6, 64, 0), Chest = new(6, 64, 2);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeClock Clock) Rig(params Stack[] stock)
    {
        var clock = new FakeClock();
        var (c, h, w) = StoryRig.Make(pawns: 1, data: Release1122GameData.Instance, time: clock);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t is WorkTypes.Craft or WorkTypes.Cook ? 1 : 0;
        w.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        Assert.Null(c.WorkbenchOp("designate", Furnace).Error);
        return (c, h, clock);
    }

    private static void Bill(StoryColony c, string recipe, int target, string? fuel = null)
    {
        var bill = new JsonObject { ["recipe"] = recipe, ["mode"] = "count", ["target"] = target };
        if (fuel != null) Assert.Null(c.WorkbenchOp("fuel", Furnace, fuel).Error);
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = Furnace.Json(), ["bill"] = bill }).Error);
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private static List<(string Item, int N)> Fetched(JsonObject j) =>
        (j["fetch"] as JsonArray ?? []).Select(f => (f!["item"]!.GetValue<string>(), f["n"]!.GetValue<int>())).ToList();

    /// <summary>Loads the furnace (the bill's smelt job, done) and lets its time pass.</summary>
    private static JsonObject Load(StoryColony c, FakeClock clock, int seconds)
    {
        var j = c.Next();
        Assert.Equal("smelt", Kind(j));
        c.Report(c.P(), Id(j), "done", "smelting", null);
        clock.Now += TimeSpan.FromSeconds(seconds);
        return j;
    }

    [Fact]
    public void A_collect_that_finds_the_input_with_no_fuel_becomes_a_refuel_and_no_failure()
    {
        var (c, _, clock) = Rig(new Stack(15, 0, 4), new Stack(263, 0, 2));
        Bill(c, "smelt:iron_ore", 4);
        Load(c, clock, 41);
        var collect = c.Next();
        Assert.Equal("collect_furnace", Kind(collect));
        c.Report(c.P(), Id(collect), "failed", "nothing to collect; furnace (3 slots, 2 free) progress 0%; in=iron ore x4 fuel=- out=-: iron ore x4", null);
        var bench = c.Benches.Single();
        Assert.Equal(4, bench.RefuelCount);
        Assert.Null(bench.Bills.Single().SuspendedReason);

        // The same pawn (not rested: nothing failed) brings fuel for the 4 ore at once.
        var refuel = c.Next();
        Assert.Equal("smelt", Kind(refuel));
        Assert.StartsWith("fuelling the furnace (4 iron ore waiting)", refuel["label"]!.GetValue<string>());
        Assert.Equal(0, refuel["n"]!.GetValue<int>()); // fuel only
        Assert.Equal("coal", refuel["fuel"]!.GetValue<string>());
        Assert.Equal([("coal", 1)], Fetched(refuel));

        c.Report(c.P(), Id(refuel), "done", "fuelled the furnace at 6,64,0: 1 coal for 4 iron ore, ready in ~40s", null);
        Assert.Equal(0, bench.RefuelCount);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(40), bench.ReadyAt);
        Assert.NotEqual("collect_furnace", Kind(c.Next())); // smelting now
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        clock.Now += TimeSpan.FromSeconds(41);
        Assert.Equal("collect_furnace", Kind(c.Next()));
    }

    [Fact]
    public void The_live_message_with_a_log_waiting_brings_the_furnace_its_own_fuel_choice()
    {
        var (c, _, clock) = Rig(new Stack(17, 0, 6));
        Bill(c, "smelt:charcoal_any", 1, fuel: "logs");
        Load(c, clock, 30);
        var collect = c.Next();
        Assert.Equal("collect_furnace", Kind(collect));
        c.Report(c.P(), Id(collect), "failed", "nothing to collect; furnace (3 slots, 2 free) progress 0%; in=oak wood x1 fuel=- out=-: oak wood x1", null);
        var refuel = c.Next();
        Assert.Equal("smelt", Kind(refuel));
        Assert.Contains("1 oak wood waiting", refuel["label"]!.GetValue<string>());
        Assert.StartsWith("log", refuel["fuel"]!.GetValue<string>());
        Assert.Equal(1, refuel["fuel_units"]!.GetValue<int>());
    }

    [Fact]
    public void A_furnace_found_empty_forgets_its_load_and_a_burning_one_is_looked_at_later()
    {
        var (c, _, clock) = Rig(new Stack(15, 0, 8), new Stack(263, 0, 2));
        Bill(c, "smelt:iron_ore", 4);
        Load(c, clock, 41);
        var bench = c.Benches.Single();

        var still = c.Next();
        c.Report(c.P(), Id(still), "failed", "nothing to collect; furnace (3 slots, 1 free) progress 40% burning; in=iron ore x2 fuel=coal x1 out=-: iron ore x2, coal x1", null);
        Assert.NotNull(bench.LoadedBill);
        Assert.Equal(0, bench.RefuelCount);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(20), bench.ReadyAt); // its 2 items' time

        clock.Now += TimeSpan.FromSeconds(21);
        var empty = c.Next();
        Assert.Equal("collect_furnace", Kind(empty));
        c.Report(c.P(), Id(empty), "failed", "nothing to collect; furnace (3 slots, 3 free) progress 0%; in=- fuel=- out=-: empty", null);
        Assert.Null(bench.LoadedBill);
        Assert.Equal("smelt", Kind(c.Next())); // the bill loads again
    }

    [Fact]
    public void Logs_for_charcoal_bring_their_own_fuel_beyond_the_log_the_pawn_carries()
    {
        var (c, h, _) = Rig(new Stack(17, 0, 4));
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(17, 0, 1)); // the one log it carries is the input
        Bill(c, "smelt:charcoal_any", 1, fuel: "logs");
        var j = c.Next();
        Assert.Equal("smelt", Kind(j));
        Assert.Equal(1, j["fuel_units"]!.GetValue<int>());
        Assert.Contains(Fetched(j), f => h.World!.Data.ItemByNameWithDamage(f.Item) is { } item
            && item.Item.Id == 17 && item.Damage == 0 && f.N == 1); // and one more of the exact selected wood to burn
    }

    [Fact]
    public void Lit_logs_burn_out_so_one_log_batches_each_bring_their_fuel()
    {
        var (c, _, clock) = Rig(new Stack(17, 0, 16));
        for (int i = 0; i < 3; i++)
        {
            Bill(c, "smelt:charcoal_any", 1, fuel: "logs"); // one log a batch
            var j = c.Next();
            Assert.Equal("smelt", Kind(j));
            Assert.Equal(1, j["n"]!.GetValue<int>());
            Assert.True(j["fuel_units"]!.GetValue<int>() >= 1, $"batch {i + 1} brought no fuel (credit {c.FuelCreditNow(c.Benches.Single())})");
            c.Report(c.P(), Id(j), "done", "smelting", null);
            clock.Now += TimeSpan.FromSeconds(60); // collected a minute later: the lit log's leftover burnt away meanwhile
            var collect = c.Next();
            Assert.Equal("collect_furnace", Kind(collect));
            int n = c.Benches.Single().LoadedCount;
            c.Report(c.P(), Id(collect), "done", $"collected {n} charcoal; still smelting 0", null);
        }
    }

    [Fact]
    public void A_lit_coal_counts_while_it_burns_and_not_after()
    {
        var (c, _, clock) = Rig(new Stack(15, 0, 8), new Stack(263, 0, 4));
        Bill(c, "smelt:iron_ore", 2);
        Load(c, clock, 0);
        var bench = c.Benches.Single();
        Assert.Equal(6, c.FuelCreditNow(bench), 3); // one coal smelts 8: 6 left after 2, while it burns
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Equal(5, c.FuelCreditNow(bench), 3);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(0, c.FuelCreditNow(bench), 3); // burnt out with nothing to smelt
    }
}
