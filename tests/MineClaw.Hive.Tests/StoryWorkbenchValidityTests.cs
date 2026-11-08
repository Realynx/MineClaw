using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryWorkbenchValidityTests
{
    private static readonly P3 First = new(3, 64, 0), Second = new(6, 64, 0), Chest = new(0, 64, 9);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(bool modern, int benchId, bool alternate = true)
    {
        var t = new Clock();
        var (c, h, w) = StoryRig.Make(data: modern ? Release1122GameData.Instance : Beta181GameData.Instance, time: t);
        foreach (var p in c.Pawns.Values)
            foreach (var work in WorkTypes.All) p.Priorities[work] = work is WorkTypes.Craft or WorkTypes.Cook ? 1 : 0;
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        c.StorageOp("set", [Chest], null, null, null);
        c.AddContents(c.StorageAt(Chest)!, new Stack(12, 0, 16));
        c.AddContents(c.StorageAt(Chest)!, new Stack(4, 0, 16));
        c.AddContents(c.StorageAt(Chest)!, new Stack(263, 0, 4));
        foreach (var at in alternate ? new[] { First, Second } : [First])
        {
            w.Set(at.X, at.Y, at.Z, benchId);
            Assert.Null(c.WorkbenchOp("designate", at).Error);
        }
        return (c, h, w, t);
    }

    private static void Build(StoryColony c, int id)
    {
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["area"] = new JsonArray(0, 64, 5, 3, 64, 5), ["material"] = new JsonArray(id, 0),
        }), null).Error);
        c.EnsureMaterials();
    }

    private static string Id(JsonObject job) => job["id"]!.GetValue<string>();
    private static string Kind(JsonObject job) => job["kind"]!.GetValue<string>();

    [Theory]
    [InlineData(false, 61, 20)]
    [InlineData(true, 61, 20)]
    [InlineData(false, 58, 67)]
    [InlineData(true, 58, 67)]
    public void A_suspended_automatic_task_moves_from_a_removed_bench_and_runs_at_an_existing_one(bool modern, int benchId, int output)
    {
        var (c, _, w, t) = Rig(modern, benchId, alternate: false);
        Build(c, output);
        var old = c.Benches.Single(b => b.Pos == First);
        var task = Assert.Single(old.Tasks);
        for (int i = 0; i < 3; i++)
        {
            var job = c.Next();
            Assert.Equal(First.ToString(), job["at"]!.GetValue<string>());
            Assert.True(c.Report(c.P(), Id(job), "failed", $"air at {First} is not a container", null));
            t.Now += TimeSpan.FromMinutes(11);
        }
        Assert.NotNull(task.SuspendedReason);
        // Introduce the replacement after the task exists: furnace work now spreads across all registered
        // stations, while this regression specifically checks migration of the original task identity.
        w.Set(Second.X, Second.Y, Second.Z, benchId);
        Assert.Null(c.WorkbenchOp("designate", Second).Error);
        w.Set(First.X, First.Y, First.Z, 0);
        c.EnsureMaterials(); // no stock or queue mutation: block validity itself must invalidate the cached plan
        var next = c.Next();
        Assert.Equal(benchId == 61 ? "smelt" : "craft", Kind(next));
        Assert.Equal(Second.ToString(), next["at"]!.GetValue<string>());
        Assert.Same(task, Assert.Single(c.Benches.Single(b => b.Pos == Second).Tasks));
        Assert.Null(task.SuspendedReason);
        Assert.Empty(old.Tasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removing_a_manual_furnace_cancels_its_job_but_keeps_its_bill_and_fuel_settings(bool modern)
    {
        var (c, h, w, _) = Rig(modern, 61);
        Assert.Null(c.WorkbenchOp("fuel", First, "coal").Error);
        Assert.Null(c.BillsOp("add", new JsonObject
        {
            ["bench"] = First.Json(), ["bill"] = new JsonObject { ["recipe"] = "smelt:sand", ["mode"] = "count", ["target"] = 4 },
        }).Error);
        var bench = c.Benches.Single(b => b.Pos == First);
        var bill = Assert.Single(bench.Bills);
        var job = c.Next();
        Assert.Equal("smelt", Kind(job));
        w.Set(First.X, First.Y, First.Z, 0);
        c.EnsureMaterials();
        Assert.False(c.Holds(c.P(), Id(job)));
        Assert.Contains(h.Interrupted, text => text.StartsWith(c.P() + ":", StringComparison.Ordinal));
        Assert.False(c.Report(c.P(), Id(job), "done", null, null));
        Assert.Same(bill, Assert.Single(bench.Bills));
        Assert.Equal("coal", bench.FuelSetting);
        Assert.Contains("is gone", bill.SuspendedReason);
        Assert.Empty(c.Benches.Single(b => b.Pos == Second).Bills);
        var wait = c.Next();
        Assert.NotEqual("smelt", Kind(wait));
        c.Report(c.P(), Id(wait), "done", null, null);
        w.Set(First.X, First.Y, First.Z, 61);
        c.EnsureMaterials();
        var restored = c.Next();
        Assert.Equal("smelt", Kind(restored));
        Assert.Equal(First.ToString(), restored["at"]!.GetValue<string>());
        Assert.Null(bill.SuspendedReason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_real_lit_or_unloaded_furnace_keeps_its_loaded_output_and_task(bool modern, bool unloaded)
    {
        var (c, _, w, t) = Rig(modern, 61, alternate: false);
        Build(c, 20);
        var job = c.Next();
        Assert.Equal("smelt", Kind(job));
        Assert.True(c.Report(c.P(), Id(job), "done", "smelting", null));
        // Once the whole request is loaded, adding another furnace must not duplicate that committed output.
        w.Set(Second.X, Second.Y, Second.Z, 61);
        Assert.Null(c.WorkbenchOp("designate", Second).Error);
        var first = c.Benches.Single(b => b.Pos == First);
        var loaded = first.LoadedBill;
        int count = first.LoadedCount;
        if (unloaded) w.Unloaded.Add((First.X, First.Z)); else w.Set(First.X, First.Y, First.Z, 62);
        c.RefreshMaterials();
        Assert.Equal(loaded, first.LoadedBill);
        Assert.Equal(count, first.LoadedCount);
        Assert.Contains(first.Tasks, x => x.Id == loaded);
        Assert.Empty(c.Benches.Single(b => b.Pos == Second).Tasks);
        Assert.Equal(4, c.Builds.Single().Materials.Single().Have);
        t.Now += TimeSpan.FromMinutes(2);
        var collect = c.Next();
        Assert.Equal("collect_furnace", Kind(collect));
        Assert.Equal(First.ToString(), collect["at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_furnace_cannot_supply_imaginary_loaded_output_or_unlock_smelting(bool modern)
    {
        var (c, _, w, _) = Rig(modern, 61, alternate: false);
        Build(c, 20);
        var job = c.Next();
        Assert.True(c.Report(c.P(), Id(job), "done", "smelting", null));
        var bench = c.Benches.Single();
        Assert.NotNull(bench.LoadedBill);
        w.Set(First.X, First.Y, First.Z, 0);
        c.EnsureMaterials();
        Assert.Null(bench.LoadedBill);
        Assert.Equal(0, bench.LoadedCount);
        Assert.Equal(0, bench.FuelCredit);
        Assert.Equal(0, c.Builds.Single().Materials.Single().Have);
        Assert.Equal(0, c.Architect()["benches"]!["furnace"]!.GetValue<int>());
        Assert.NotEqual("collect_furnace", Kind(c.Next()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_confirmed_missing_bench_stays_missing_after_reload_until_its_block_is_seen_again(bool modern)
    {
        var (c, h, w, t) = Rig(modern, 61);
        Build(c, 20);
        w.Set(First.X, First.Y, First.Z, 0);
        c.EnsureMaterials();
        Assert.True(c.Benches.Single(b => b.Pos == First).Missing);
        using var db = AgentDb.Open(null);
        var store = new StoryStore(db);
        store.Save(c);

        w.Unloaded.Add((First.X, First.Z));
        var again = Assert.Single(store.Load(meta => new StoryColony(meta, h, t)));
        again.EnsureMaterials();
        var missing = again.Benches.Single(b => b.Pos == First);
        Assert.True(missing.Missing);
        Assert.Empty(missing.Tasks);
        Assert.Equal(1, again.Architect()["benches"]!["furnace"]!.GetValue<int>());
        var job = again.Next();
        Assert.Equal("smelt", Kind(job));
        Assert.Equal(Second.ToString(), job["at"]!.GetValue<string>());

        w.Set(First.X, First.Y, First.Z, 61);
        again.EnsureMaterials(); // still unseen: the saved absence is not discarded
        Assert.True(missing.Missing);
        w.Unloaded.Remove((First.X, First.Z));
        again.EnsureMaterials();
        Assert.False(missing.Missing);
        Assert.Equal(2, again.Architect()["benches"]!["furnace"]!.GetValue<int>());
    }
}
