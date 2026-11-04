using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fox 11-04: "they bring the block they mined back to the chest right away. They shouldn't. They can dump their inventory all at once
/// later while they keep mining until the job is done or their inventory is full." And: "I updated the rules for mining dirt, but it
/// didn't look like it updated while they were in the middle of the job." docs/web/story-mode.md "Keeping loot while gathering".
/// </summary>
public sealed class StoryLootBatchingTests(ITestOutputHelper output)
{
    private static readonly P3 Chest = new(0, 64, 9);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Only(StoryColony c, params string[] types)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static bool Stores(JsonObject j) => j["stash"] is JsonArray { Count: > 0 } || Kind(j) == "dump";

    private static P3 At(JsonObject j)
    {
        var a = j["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        return new P3(a[0], a[1], a[2]);
    }

    /// <summary>A row of dirt to mine, designated; a chest.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Field(int n, IGameData? data = null, TimeProvider? time = null)
    {
        var (c, h, w) = StoryRig.Make(1, data, time);
        Only(c, WorkTypes.Mine);
        Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        var cells = Enumerable.Range(0, n).Select(i => new P3(2 + i % 12, 64, 2 + i / 12)).ToList();
        foreach (var at in cells) w.Set(at.X, at.Y, at.Z, 3);
        Assert.Equal(n, c.Designate("add", "mine", null, cells, "all").Result!["count"]!.GetValue<int>());
        return (c, h, w);
    }

    /// <summary>
    /// Runs the pawn: a dig clears the cell and the dirt lands in its pockets (<paramref name="slots"/> free slots at most), a store
    /// trip empties them. Returns the order of what it did ("dig" / "store") until the field is done and nothing is carried.
    /// </summary>
    private static List<string> Work(StoryColony c, FakeStoryHost h, FakeStoryWorld w, int slots = 35, int max = 200, Action<int>? between = null)
    {
        var did = new List<string>();
        int dirt = 0;
        void Pockets() => h.Lives[c.P()] = new PawnLive(true, 20, 20, 1.5, 64, 1.5, 0, null, dirt > 0 ? [new Stack(3, 0, dirt)] : [], true,
            Math.Max(0, slots - Math.Max(0, (dirt + 63) / 64 - 1) - (dirt > 0 ? 1 : 0)));
        Pockets();
        for (int i = 0; i < max && (c.Designations.Count > 0 || dirt > 0); i++)
        {
            between?.Invoke(did.Count(d => d == "dig"));
            var j = c.Next();
            if (Kind(j) == "dig")
            {
                var at = At(j);
                w.Set(at.X, at.Y, at.Z, 0);
                dirt++;
                did.Add("dig");
            }
            else if (Stores(j)) { dirt = 0; did.Add("store"); }
            else did.Add(Kind(j));
            Pockets();
            c.Report(c.P(), Id(j), "done", null, null);
        }
        return did;
    }

    [Fact]
    public void A_pawn_mines_twelve_cells_in_a_row_and_stores_once_at_the_end()
    {
        var (c, h, w) = Field(12);
        var did = Work(c, h, w);
        Assert.Equal(12, did.Count(d => d == "dig"));
        Assert.Equal(1, did.Count(d => d == "store"));
        Assert.Equal("store", did.Last(d => d is "dig" or "store")); // after the last block: nothing left of that work
        Assert.Equal(12, c.StorageAt(Chest)!.Contents.Single(s => s.Id == 3).Count);
    }

    [Fact]
    public void A_pawn_whose_pockets_fill_stores_early_then_goes_on()
    {
        var (c, h, w) = Field(12);
        var did = Work(c, h, w, slots: 3); // one stack of dirt takes a slot: 2 free after the first block, so it unloads at once
        Assert.Equal(12, did.Count(d => d == "dig"));
        Assert.True(did.Count(d => d == "store") > 1, string.Join(" ", did));
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void Sleep_hours_and_a_draft_end_the_run_the_first_with_a_trip_to_storage_the_second_keeping_the_loot()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T10:00:00Z"));
        var (c, h, w) = Field(6, time: clock);
        var p = c.Pawns.Values.Single();
        var j = c.Next();
        Assert.Equal("dig", Kind(j));
        w.Set(At(j).X, 64, At(j).Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(1, 64, 1, inv: new Stack(3, 0, 1));
        c.Report(c.P(), Id(j), "done", null, null);
        // Its hours turn to sleep: it unloads first.
        p.Schedule = Enumerable.Repeat(ScheduleBlocks.Sleep, 24).ToArray();
        var next = c.Next();
        Assert.True(Stores(next), next.ToJsonString());
        c.Report(c.P(), Id(next), "failed", "whatever", null);
        // Drafted: orders only, the loot stays with it.
        p.Schedule = Enumerable.Repeat(ScheduleBlocks.Anything, 24).ToArray();
        c.Draft([p], true);
        Assert.False(Stores(c.Next()));
    }

    [Fact]
    public void A_build_that_needs_what_the_miner_carries_takes_it_from_its_pockets()
    {
        var (c, h, w) = Field(4);
        Only(c, WorkTypes.Build, WorkTypes.Mine);
        var j = c.Next();
        Assert.Equal("dig", Kind(j));
        w.Set(At(j).X, 64, At(j).Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(1, 64, 1, inv: new Stack(3, 0, 1));
        c.Report(c.P(), Id(j), "done", null, null);
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(5, 64, 6)), ["material"] = "dirt" }), null).Error);
        var place = c.Next();
        Assert.Equal("place", Kind(place));
        Assert.Null(place["fetch"]); // from its pockets, no trip to storage first
    }

    [Fact]
    public void Benchmark_blocks_per_minute_keeping_loot_versus_a_trip_after_every_block()
    {
        // Simulated time: a dig 2 s, a step to the next cell 1 s, a store trip 2 × 9 blocks to the chest at walking pace (4.3 b/s) + 2 s.
        double Minutes(bool eachJob)
        {
            var (c, h, w) = Field(24);
            c.DepositEachJob = eachJob;
            var did = Work(c, h, w, max: 400);
            double s = did.Count(d => d == "dig") * 3.0 + did.Count(d => d == "store") * (2 * 9 / 4.3 + 2);
            return s / 60;
        }
        double before = 24 / Minutes(true), after = 24 / Minutes(false);
        output.WriteLine($"blocks/min: a trip after every block {before:0.0}, keeping the loot {after:0.0}");
        Assert.True(after > before * 1.8, $"{before:0.0} → {after:0.0}");
    }

    // ================================================================ tool rules take effect between blocks

    [Fact]
    public void A_dirt_rule_changed_mid_run_takes_effect_at_the_next_block_without_restarting_the_work()
    {
        var (c, h, w) = Field(6);
        var first = c.Next();
        Assert.Equal("dig", Kind(first));
        Assert.Null(first["tool"]); // dirt by hand
        // The owner says: dirt with a shovel. The block under way finishes as it is (never cut mid-swing)…
        Assert.Null(c.MaterialRulesOp(new JsonObject { ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "dirt", ["tool"] = "shovel" } }).Error);
        Assert.Equal(Id(first), c.CurrentOf(c.P())!.Id);
        c.AddContents(c.StorageAt(Chest)!, new Stack(269, 0, 1)); // a wooden shovel in storage
        w.Set(At(first).X, 64, At(first).Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(1, 64, 1, inv: new Stack(3, 0, 1));
        c.Report(c.P(), Id(first), "done", null, null);
        // …and the next block gets the shovel from storage: fetched with the dig, or by the gear step just before it.
        var second = c.Next();
        Assert.False(Stores(second), second.ToJsonString()); // no trip to storage with the loot in between
        Assert.Contains("\"from\":\"" + Chest + "\"", second.ToJsonString());
        Assert.Contains(w.Data.Item(269).Name, second.ToJsonString());
        if (Kind(second) != "dig")
        {
            h.Lives[c.P()] = FakeStoryHost.Live(1, 64, 1, inv: [new Stack(3, 0, 1), new Stack(269, 0, 1)]);
            c.Report(c.P(), Id(second), "done", null, null);
            second = c.Next();
        }
        Assert.Equal("dig", Kind(second));
        Assert.Equal(w.Data.Item(269).Name, second["tool"]!.GetValue<string>());
        // Carrying it now: held, no more fetching; and still no store trip in between.
        w.Set(At(second).X, 64, At(second).Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(1, 64, 1, inv: [new Stack(3, 0, 2), new Stack(269, 0, 1)]);
        c.Report(c.P(), Id(second), "done", null, null);
        var third = c.Next();
        Assert.Equal("dig", Kind(third));
        Assert.Null(third["fetch"]);
    }

    [Fact]
    public void A_rule_that_now_waits_for_silk_touch_pauses_the_rest_of_the_field_with_its_reason()
    {
        var (c, h, w) = Field(4, Release1122GameData.Instance);
        var first = c.Next();
        Assert.Equal("dig", Kind(first));
        Assert.Null(c.MaterialRulesOp(new JsonObject
        {
            ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "dirt", ["tool"] = "shovel", ["enchant"] = "silk_touch", ["fallback"] = "wait" },
        }).Error);
        w.Set(At(first).X, 64, At(first).Z, 0);
        c.Report(c.P(), Id(first), "done", null, null);
        Assert.NotEqual("dig", Kind(c.Next()));
        Assert.All(c.Designations, d => Assert.StartsWith("waiting for", d.Wait));
        Assert.All(c.Designations, d => Assert.Equal(0, d.Fails));
    }
}
