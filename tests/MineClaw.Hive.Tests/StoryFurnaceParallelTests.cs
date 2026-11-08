using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryFurnaceParallelTests
{
    private static readonly P3 Chest = new(0, 64, 9);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(int furnaces, params Stack[] stock)
    {
        var time = new Clock();
        var (c, h, w) = StoryRig.Make(pawns: furnaces + 1, data: Release1122GameData.Instance, time: time);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var pawn in c.Pawns.Values)
            foreach (var work in WorkTypes.All) pawn.Priorities[work] = work is WorkTypes.Craft or WorkTypes.Cook ? 1 : 0;
        c.StorageOp("set", [Chest], null, null, null);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        foreach (var stack in stock) c.AddContents(c.StorageAt(Chest)!, stack);
        for (int i = 0; i < furnaces; i++)
        {
            var at = new P3(3 + i * 2, 64, 0);
            w.Set(at.X, at.Y, at.Z, 61);
            Assert.Null(c.WorkbenchOp("designate", at).Error);
        }
        return (c, h, w, time);
    }
    private static void Glass(StoryColony c, int count)
    {
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["material"] = "glass",
            ["blocks"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonArray(i, 64, 20)).ToArray()),
        }), null).Error);
    }
    private static BillState Manual(StoryColony c, int bench, string recipe = "smelt:sand", int target = 8)
    {
        var state = c.Benches[bench];
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = state.Pos.Json(), ["bill"] = new JsonObject
            { ["recipe"] = recipe, ["target"] = target } }).Error);
        return state.Bills.Last();
    }
    private static void Done(StoryColony c, int pawn, JsonObject job, string? note = null) =>
        Assert.True(c.Report(c.P(pawn), job["id"]!.GetValue<string>(), "done", note, null));

    [Fact]
    public void Paused_finished_suspended_and_uncompletable_manual_bills_leave_all_furnaces_available()
    {
        var (c, _, _, _) = Rig(4, new Stack(12, 0, 24), new Stack(263, 0, 8));
        Manual(c, 0).Paused = true;
        var finished = Manual(c, 1); finished.Done = finished.Target;
        Manual(c, 2, "smelt:gold_ore").SuspendedReason = "owner ingredient unavailable";
        Manual(c, 3, "smelt:iron_ore"); // no ore: the furnace can still make the build's glass
        Glass(c, 24);
        var jobs = Enumerable.Range(0, 4).Select(c.Next).ToArray();
        Assert.All(jobs, j => Assert.Equal("smelt", j["kind"]!.GetValue<string>()));
        Assert.Equal(4, jobs.Select(j => j["at"]!.GetValue<string>()).Distinct().Count());
        Assert.Equal(24, jobs.Sum(j => j["n"]!.GetValue<int>()));
        Assert.All(jobs, j => Assert.NotNull(c.Benches.SelectMany(b => b.Tasks).Single(t => t.Id == c.CurrentOf(c.P(Array.IndexOf(jobs, j)))!.SourceId).ForBuild));
        Assert.NotEqual("smelt", c.Next(4)["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Concurrent_loads_reserve_inputs_and_outputs_then_collect_each_furnace_once()
    {
        var (c, h, _, time) = Rig(3, new Stack(12, 0, 10), new Stack(263, 0, 3));
        Glass(c, 10);
        var jobs = Enumerable.Range(0, 3).Select(c.Next).ToArray();
        Assert.Equal(new[] { 4, 3, 3 }, jobs.Select(j => j["n"]!.GetValue<int>()).ToArray());
        Assert.Equal(10, Enumerable.Range(0, 3).SelectMany(i => c.CurrentOf(c.P(i))!.Takes).Where(t => t.Stack.Id == 12).Sum(t => t.Stack.Count));
        Assert.Equal(3, Enumerable.Range(0, 3).SelectMany(i => c.CurrentOf(c.P(i))!.Takes).Where(t => t.Stack.Id == 263).Sum(t => t.Stack.Count));
        for (int i = 0; i < 3; i++) Done(c, i, jobs[i]);
        Assert.All(c.Benches, b => Assert.NotNull(b.LoadedBill));
        Assert.Equal(10, c.Benches.Sum(b => b.LoadedCount));
        time.Now += TimeSpan.FromMinutes(2);
        for (int i = 0; i < 3; i++)
        {
            var collect = c.Next(i); Assert.Equal("collect_furnace", collect["kind"]!.GetValue<string>());
            int count = c.Benches.Single(b => b.Pos.ToString() == collect["at"]!.GetValue<string>()).LoadedCount;
            h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(20, 0, count)]);
            Done(c, i, collect);
        }
        Assert.All(c.Benches, b => Assert.Null(b.LoadedBill));
        c.EnsureMaterials();
        Assert.Empty(c.Benches.SelectMany(b => b.Tasks));
        Assert.Equal(10, c.CountItem(20, 0));
    }

    [Fact]
    public void A_small_manual_batch_does_not_monopolize_the_remaining_build_demand()
    {
        var (c, _, _, _) = Rig(3, new Stack(12, 0, 16), new Stack(263, 0, 8));
        var manual = Manual(c, 0, target: 2);
        Glass(c, 16);
        var first = c.Next(0);
        Assert.Equal(manual.Id, c.CurrentOf(c.P(0))!.SourceId);
        Assert.Equal(2, first["n"]!.GetValue<int>());
        var other = new[] { c.Next(1), c.Next(2) };
        Assert.All(other, j => Assert.Equal("smelt", j["kind"]!.GetValue<string>()));
        Assert.Equal(14, other.Sum(j => j["n"]!.GetValue<int>()));
        Assert.Equal(16, Enumerable.Range(0, 3).Sum(i => c.CurrentOf(c.P(i))!.Count));
    }

    [Fact]
    public void The_last_fuel_item_is_not_promised_to_two_furnaces_and_failure_releases_it()
    {
        var (c, _, _, time) = Rig(2, new Stack(12, 0, 8), new Stack(263, 0, 1));
        Glass(c, 8);
        var first = c.Next(0); Assert.Equal("smelt", first["kind"]!.GetValue<string>());
        var waiting = c.Next(1); Assert.NotEqual("smelt", waiting["kind"]!.GetValue<string>());
        Assert.True(c.Report(c.P(0), first["id"]!.GetValue<string>(), "failed", "test interrupted before fetch", null));
        Done(c, 1, waiting);
        time.Now += TimeSpan.FromMinutes(1);
        var retry = c.Next(1); Assert.Equal("smelt", retry["kind"]!.GetValue<string>());
        Assert.Single(c.CurrentOf(c.P(1))!.Takes.Where(t => t.Stack.Id == 263));
    }

    [Fact]
    public void Missing_and_released_furnaces_are_excluded_and_existing_loads_are_not_moved()
    {
        var (c, _, w, _) = Rig(3, new Stack(12, 0, 16), new Stack(263, 0, 8));
        var missing = c.Benches[1].Pos;
        w.Set(missing.X, missing.Y, missing.Z, 0);
        Assert.Null(c.WorkbenchOp("release", c.Benches[2].Pos).Error);
        Glass(c, 16);
        var first = c.Next(0); Assert.Equal("smelt", first["kind"]!.GetValue<string>());
        Done(c, 0, first);
        var loaded = c.Benches.Single(b => b.LoadedBill != null);
        string bill = loaded.LoadedBill!;
        c.RefreshMaterials();
        Assert.Equal(bill, loaded.LoadedBill);
        Assert.NotEqual("smelt", c.Next(1)["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Family_smelts_select_the_actual_carried_input_id_and_exact_metadata()
    {
        var (c, h, _, _) = Rig(1, new Stack(263, 0, 2));
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(162, 1, 4)]);
        Manual(c, 0, "smelt:charcoal_any", target: 4);
        var job = c.Next();
        Assert.Equal("smelt", job["kind"]!.GetValue<string>());
        Assert.Equal(1, job["input_meta"]!.GetValue<int>());
        var selected = Release1122GameData.Instance.ItemByNameWithDamage(job["item"]!.GetValue<string>());
        Assert.NotNull(selected);
        Assert.Equal(162, selected.Value.Item.Id);
        Assert.Equal(4, job["n"]!.GetValue<int>());
        Assert.DoesNotContain(c.CurrentOf(c.P())!.Takes, t => t.Stack.Id == 162);
    }

    [Fact]
    public void A_nearest_furnace_with_missing_named_fuel_does_not_hold_back_other_furnaces()
    {
        var (c, _, _, _) = Rig(3, new Stack(12, 0, 12), new Stack(263, 0, 2));
        Assert.Null(c.WorkbenchOp("fuel", c.Benches[0].Pos, "lava_bucket").Error);
        Glass(c, 12);
        var first = c.Next(0); var second = c.Next(1);
        Assert.Equal("smelt", first["kind"]!.GetValue<string>());
        Assert.Equal("smelt", second["kind"]!.GetValue<string>());
        Assert.Equal(12, first["n"]!.GetValue<int>() + second["n"]!.GetValue<int>());
        Assert.DoesNotContain(c.Benches[0].Pos.ToString(), new[] { first["at"]!.GetValue<string>(), second["at"]!.GetValue<string>() });
        Assert.Empty(c.Benches[0].Tasks);
    }
}
