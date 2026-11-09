using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryBillPriorityTests
{
    private static readonly P3 Chest = new(6, 64, 3);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(params Stack[] stock)
    {
        var t = new Clock();
        var (c, h, w) = StoryRig.Make(pawns: 2, data: Release1122GameData.Instance, time: t);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var p in c.Pawns.Values)
            foreach (var work in WorkTypes.All) p.Priorities[work] = work is WorkTypes.Craft or WorkTypes.Cook ? 1 : 0;
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var item in stock) c.AddContents(c.StorageAt(Chest)!, item);
        foreach (int x in new[] { 3, 11 }) { w.Set(x, 64, 0, 61); Assert.Null(c.WorkbenchOp("designate", new(x, 64, 0)).Error); }
        return (c, h, w, t);
    }
    private static BillState Bill(StoryColony c, int bench, string recipe, int priority = 3, string mode = "count")
    {
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = c.Benches[bench].Pos.Json(), ["bill"] = new JsonObject
        { ["recipe"] = recipe, ["mode"] = mode, ["target"] = 8, ["priority"] = priority, ["fuel"] = "coal" } }).Error);
        return c.Benches[bench].Bills.Last();
    }

    [Fact]
    public void Farther_high_priority_bill_gets_the_last_coal_before_nearer_bill()
    {
        var (c, _, _, _) = Rig(new(15, 0, 8), new(14, 0, 8), new(263, 0, 1));
        Bill(c, 0, "smelt:iron_ore", 4);
        var urgent = Bill(c, 1, "smelt:gold_ore", 1);
        var job = c.Next();
        Assert.Equal("smelt", job["kind"]!.GetValue<string>());
        Assert.Equal(c.Benches[1].Pos.ToString(), job["at"]!.GetValue<string>());
        Assert.Equal(urgent.Id, c.CurrentOf(c.P())!.SourceId);
        Assert.Equal(1, job["fuel_units"]!.GetValue<int>());
        Assert.NotEqual("smelt", c.Next(1)["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Crafting_priority_compares_tables_but_keeps_manual_bills_at_their_selected_table()
    {
        var (c, _, w, _) = Rig(new Stack(5, 0, 32));
        foreach (var b in c.Benches) { b.Kind = "crafting_table"; w.Set(b.Pos.X, b.Pos.Y, b.Pos.Z, 58); }
        foreach (int i in new[] { 0, 1 }) Assert.Null(c.BillsOp("add", new JsonObject
        {
            ["bench"] = c.Benches[i].Pos.Json(), ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["target"] = 8, ["priority"] = i == 0 ? 4 : 1 },
        }).Error);
        var job = c.Next();
        Assert.Equal("craft", job["kind"]!.GetValue<string>());
        Assert.Equal(c.Benches[1].Pos.ToString(), job["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_lower_cooking_bill_cannot_burn_the_urgent_crafting_bills_fuel()
    {
        var (c, _, _, _) = Rig(new(319, 0, 8), new(15, 0, 8), new(263, 0, 1));
        var food = Bill(c, 0, "smelt:porkchop", 4, "forever");
        var iron = Bill(c, 1, "smelt:iron_ore", 1);
        // Cook appears before Craft in the pawn's work columns, even when both are enabled at priority 1.
        var job = c.Next();
        Assert.Equal(iron.Id, c.CurrentOf(c.P())!.SourceId);
        Assert.Contains("reserved for higher-priority bills", food.WaitingReason);
        Assert.Equal("smelt", job["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_or_paused_high_priority_inputs_do_not_hold_fuel_and_existing_refuels_keep_priority()
    {
        var (c, h, _, _) = Rig(new(15, 0, 8), new(263, 0, 1));
        var low = Bill(c, 0, "smelt:iron_ore", 4);
        var high = Bill(c, 1, "smelt:gold_ore", 1);
        Assert.Null(c.PickFuel(c.Benches[0], low, 8, h.Live(c.P())).Missing);
        var b = c.Benches[1];
        b.LoadedBill = high.Id; b.LoadedRecipe = high.Recipe; b.LoadedCount = b.RefuelCount = 8; b.RefuelItem = "gold ore";
        var refuel = c.Next();
        Assert.Equal(high.Id, c.CurrentOf(c.P())!.SourceId);
        Assert.Equal(0, refuel["n"]!.GetValue<int>());
    }

    [Fact]
    public void Priority_updates_validate_persist_and_reorder_without_rebinding_the_bill()
    {
        var (c, _, _, _) = Rig(new(15, 0, 16), new(263, 0, 2));
        var first = Bill(c, 0, "smelt:iron_ore");
        var second = Bill(c, 0, "smelt:iron_ore");
        Assert.Equal(first, c.BenchQueue(c.Benches[0]).First());
        Assert.Null(c.BillsOp("update", new JsonObject { ["id"] = second.Id, ["priority"] = 1 }).Error);
        Assert.Equal(second, c.BenchQueue(c.Benches[0]).First());
        Assert.NotNull(c.BillsOp("update", new JsonObject { ["id"] = second.Id, ["priority"] = 0 }).Error);
        Assert.Equal(1, StoryJson.Read<BillState>(StoryJson.Text(second))!.Priority);
        Assert.Equal(3, StoryJson.Read<BillState>("{\"id\":\"old-save\"}")!.Priority);
        Assert.Equal(1, c.State()["workbenches"]![0]!["bills"]![1]!["priority"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("offline")]
    [InlineData("skill")]
    [InlineData("disabled")]
    public void Unrunnable_urgent_bills_do_not_reserve_fuel(string reason)
    {
        var (c, h, _, _) = Rig(new(319, 0, 8), new(15, 0, 8), new(263, 0, 1));
        var low = Bill(c, 0, "smelt:porkchop", 4);
        var high = Bill(c, 1, "smelt:iron_ore", 1);
        if (reason == "paused") high.Paused = true;
        if (reason == "offline") { high.Pawn = c.P(1); h.Lives[c.P(1)] = PawnLive.Offline; }
        if (reason == "skill") high.MinSkill = 99;
        if (reason == "disabled") foreach (var pawn in c.Pawns.Values) pawn.Priorities[WorkTypes.Craft] = 0;
        Assert.Null(c.PickFuel(c.Benches[0], low, 8, h.Live(c.P())).Missing);
    }

    [Fact]
    public void Two_urgent_bills_cannot_reserve_fuel_for_the_same_input_batch()
    {
        var (c, h, w, _) = Rig(new(319, 0, 8), new(15, 0, 8), new(263, 0, 2));
        var low = Bill(c, 0, "smelt:porkchop", 4);
        Bill(c, 1, "smelt:iron_ore", 1);
        w.Set(16, 64, 0, 61);
        c.WorkbenchOp("designate", new(16, 64, 0));
        Bill(c, 2, "smelt:iron_ore", 1);
        Assert.Null(c.PickFuel(c.Benches[0], low, 8, h.Live(c.P())).Missing);
    }

    [Fact]
    public void Urgent_bill_cannot_reserve_fuel_using_an_ineligible_pawns_carried_inputs()
    {
        var (c, h, _, _) = Rig(new(319, 0, 8), new(263, 0, 1));
        var food = Bill(c, 0, "smelt:porkchop", 4);
        var iron = Bill(c, 1, "smelt:iron_ore", 1);
        iron.Pawn = c.P(1);
        c.Pawns[c.P()].Priorities[WorkTypes.Craft] = 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(15, 0, 8));
        Assert.Equal("smelt", c.Next()["kind"]!.GetValue<string>());
        Assert.Equal(food.Id, c.CurrentOf(c.P())!.SourceId);
    }

    [Theory]
    [InlineData("no iron ore: checked 1 chest within 32 of the bench")]
    [InlineData("no iron ore within 32 blocks")]
    [InlineData("needs fuel: no coal in storage")]
    [InlineData("need 9 more wheat for 16 breads; inventory + the 24 known chests have only 39 wheat")]
    public void Saved_forever_shortages_retry_after_migration_but_real_failures_stay_suspended(string shortage)
    {
        var (c, _, _, _) = Rig(new(15, 0, 8), new(263, 0, 1));
        var waiting = Bill(c, 0, "smelt:iron_ore", mode: "forever");
        waiting.SuspendedReason = shortage;
        var broken = Bill(c, 1, "smelt:gold_ore", mode: "forever");
        broken.SuspendedReason = "no path to furnace";
        Assert.True(c.MigrateCraftTasks());
        Assert.Null(waiting.SuspendedReason);
        Assert.Equal("no path to furnace", broken.SuspendedReason);
        Assert.Equal("smelt", c.Next()["kind"]!.GetValue<string>());
        Assert.DoesNotContain(c.Alerts, a => a.Text.StartsWith("Bill ", StringComparison.Ordinal));
    }

    [Fact]
    public void Changing_furnace_fuel_retries_a_waiting_forever_bill_immediately()
    {
        var (c, _, _, _) = Rig(new(15, 0, 8), new(263, 1, 1));
        var bill = Bill(c, 0, "smelt:iron_ore", mode: "forever");
        bill.Fuel = null;
        c.WorkbenchOp("fuel", c.Benches[0].Pos, "coal");
        c.Next();
        Assert.StartsWith("needs fuel:", bill.WaitingReason);
        c.WorkbenchOp("fuel", c.Benches[0].Pos, "charcoal");
        Assert.Null(bill.WaitingReason);
        if (c.CurrentOf(c.P()) is { } current) c.Report(c.P(), current.Id, "done", null, null);
        Assert.Equal("smelt", c.Next()["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("need 9 more wheat for 16 breads; inventory + the 24 known chests have only 39 wheat")]
    [InlineData("need 48 more wheat for 16 breads; none in inventory or the 24 known chests")]
    [InlineData("no fuel")]
    [InlineData("no fuel (coal, charcoal, planks, logs, sticks)")]
    [InlineData("not enough fuel (0 coal)")]
    [InlineData("no iron ore to smelt")]
    public void Forever_execution_shortfalls_wait_without_failure_penalties_and_retry_on_stock(string shortage)
    {
        var (c, _, _, _) = Rig(new(15, 0, 8), new(263, 0, 1));
        var bill = Bill(c, 0, "smelt:iron_ore", mode: "forever");
        var job = c.Next();
        Assert.Equal("smelt", job["kind"]!.GetValue<string>());
        c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", shortage, null);
        Assert.Equal(shortage, bill.WaitingReason);
        Assert.Null(bill.SuspendedReason);
        Assert.DoesNotContain(c.Alerts, a => a.Level is "warn" or "error");
        c.AddContents(c.StorageAt(Chest)!, new Stack(263, 0, 1));
        Assert.Equal("smelt", c.Next()["kind"]!.GetValue<string>());
        Assert.Null(bill.WaitingReason);
    }

    [Theory]
    [InlineData("not enough fuel (64 coal)")]
    [InlineData("couldn't load the furnace")]
    [InlineData("inventory full")]
    public void Real_execution_failures_are_not_converted_to_quiet_shortages(string failure)
    {
        var (c, _, _, t) = Rig(new(15, 0, 8), new(263, 0, 1));
        var bill = Bill(c, 0, "smelt:iron_ore", mode: "forever");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            t.Now += TimeSpan.FromMinutes(5);
            var job = c.Next();
            Assert.Equal("smelt", job["kind"]!.GetValue<string>());
            c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", failure, null);
        }
        Assert.Null(bill.WaitingReason);
        Assert.Equal(failure, bill.SuspendedReason);
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("Bill ", StringComparison.Ordinal) && a.Level == "warn");
    }

    [Fact]
    public void Loaded_forever_furnace_waits_for_fuel_without_idle_blockage_notice()
    {
        var (c, _, _, _) = Rig();
        var bill = Bill(c, 0, "smelt:iron_ore", mode: "forever");
        var bench = c.Benches[0];
        bench.LoadedBill = bill.Id; bench.LoadedRecipe = bill.Recipe; bench.LoadedCount = bench.RefuelCount = 8;
        c.Next();
        Assert.StartsWith("needs fuel:", bill.WaitingReason);
        Assert.Null(bill.SuspendedReason);
        c.Tick();
        Assert.Null(c.IdleReasonOf(c.P()));
        Assert.Empty(c.State()["blocked_work"]!.AsArray());
        Assert.DoesNotContain(c.Alerts, a => a.Level is "warn" or "error");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Forever_bills_wait_quietly_for_inputs_or_fuel_and_resume_on_new_stock(bool missingFuel)
    {
        var (c, _, _, t) = Rig(missingFuel ? new Stack(15, 0, 8) : new Stack(263, 0, 1));
        var bill = Bill(c, 0, "smelt:iron_ore", mode: "forever");
        c.Next();
        Assert.NotNull(bill.WaitingReason);
        Assert.Null(bill.SuspendedReason);
        Assert.DoesNotContain(c.Alerts, a => a.Text.StartsWith("Bill ", StringComparison.Ordinal) && a.Level is "warn" or "error");
        c.Tick();
        Assert.Null(c.IdleReasonOf(c.P()));
        Assert.Empty(c.State()["blocked_work"]!.AsArray());
        if (c.CurrentOf(c.P()) is { } current) c.Report(c.P(), current.Id, "done", null, null);
        c.AddContents(c.StorageAt(Chest)!, missingFuel ? new Stack(263, 0, 1) : new Stack(15, 0, 8));
        t.Now += TimeSpan.FromMinutes(1);
        Assert.Equal("smelt", c.Next()["kind"]!.GetValue<string>());
        Assert.Null(bill.WaitingReason);
    }
}
