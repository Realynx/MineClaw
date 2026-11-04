using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fox 11-04: "Dropped items should be auto-hauled by pawns that have hauling with high priority", and "if I limit the types of items
/// allowed in a chest, pawns … should rearrange … Otherwise they end up dropped on the ground." docs/web/story-mode.md "Hauling".
/// </summary>
public sealed class StoryHaulingTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock NewClock() => new(DateTimeOffset.Parse("2026-11-04T10:00:00Z"));

    private static void Prio(StoryColony c, params (string Type, int P)[] ps)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = ps.FirstOrDefault(x => x.Type == t).P;
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Label(JsonObject j) => j["label"]!.GetValue<string>();
    private static StoryEntity Drop(int id, double x, double z, int item = 4, DateTimeOffset? first = null) =>
        new(id, "item", "cobblestone", x, 64, z, ItemId: item, Count: 3, FirstSeen: first);

    [Fact]
    public void Drops_by_colony_work_are_hauled_and_far_ones_are_not()
    {
        var (c, h, w) = StoryRig.Make(time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        w.Tree(60, 2);
        c.Designate("add", "chop", Area.Of(58, 60, 0, 62, 75, 4), null, null);
        w.Ents.Add(Drop(1, 200.5, 0.5));  // nowhere near the colony
        w.Ents.Add(Drop(2, 61.5, 0.5));   // by the chop designation, 60 blocks from home
        var j = c.Next();
        Assert.Equal("collect", Kind(j));
        Assert.Equal("61,64,0", j["at"]!.GetValue<string>());
        c.Report(c.P(), Id(j), "done", null, null);
        w.Ents.RemoveAll(e => e.Id == 2);
        Assert.NotEqual("collect", Kind(c.Next()));
    }

    [Fact]
    public void Haul_priority_decides_and_a_drop_about_to_despawn_goes_first_within_one_priority()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(time: clock);
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        w.Tree(6, 6);
        c.Designate("add", "chop", Area.Of(4, 60, 4, 8, 75, 8), null, null);
        w.Ents.Add(Drop(1, 3.5, -3.5));
        Prio(c, (WorkTypes.Haul, 1), (WorkTypes.Chop, 2));
        Assert.Equal("collect", Kind(c.Next())); // hauling first: it is priority 1
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "failed", "whatever", null);
        clock.Now += TimeSpan.FromMinutes(2);
        Prio(c, (WorkTypes.Haul, 2), (WorkTypes.Chop, 1));
        Assert.Equal("chop", Kind(c.Next())); // chopping first
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        // Same priority: chop comes first by column… unless a drop is about to despawn.
        Prio(c, (WorkTypes.Haul, 2), (WorkTypes.Chop, 2));
        w.Ents.Add(Drop(2, -3.5, 3.5, first: clock.Now - TimeSpan.FromMinutes(4)));
        var j = c.Next();
        Assert.Equal("collect", Kind(j));
        Assert.Equal("-4,64,3", j["at"]!.GetValue<string>());
        Assert.Matches(@"despawns in \d+s", Label(j));
    }

    [Fact]
    public void A_drop_no_filter_takes_goes_to_an_open_chest_unless_the_option_is_off_then_one_alert_names_it()
    {
        var (c, h, w) = StoryRig.Make(time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        c.StorageOp("set", [new P3(2, 64, 0)], new StorageFilter { Allow = ["wood"] }, null, "Wood");
        w.Ents.Add(Drop(1, 3.5, 3.5));
        Assert.Equal("collect", Kind(c.Next())); // "unfiltered storage takes anything" (default on): the wood chest has room
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        c.SetHaulOptions(null, false, null);
        Assert.NotEqual("collect", Kind(c.Next()));
        var kinds = c.State()["unstored_kinds"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("Cobblestone", kinds);
        Assert.Single(c.Alerts, a => a.Text.StartsWith("No storage for: Cobblestone"));
    }

    [Fact]
    public void With_no_storage_at_all_loot_goes_to_the_drop_spot_with_a_single_alert()
    {
        var (c, h, w) = StoryRig.Make(time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        c.SetHaulOptions(null, null, null, pillarStack: 0);
        // Its own loot (seeds to sow, blocks to build) stays with it…
        h.Lives[c.P()] = FakeStoryHost.Live(5, 64, 5, inv: [new Stack(4, 0, 10), new Stack(3, 0, 5)]);
        Assert.NotEqual("dump", Kind(c.Next()));
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        h.Lives[c.P()] = FakeStoryHost.Live(5, 64, 5);
        // …drops it hauls go to the drop spot.
        w.Ents.Add(Drop(1, 8.5, 8.5));
        w.Ents.Add(new StoryEntity(2, "item", "dirt", 9.5, 64, 8.5, ItemId: 3, Count: 5));
        var haul = c.Next();
        Assert.Equal("collect", Kind(haul));
        w.Ents.Clear();
        h.Lives[c.P()] = FakeStoryHost.Live(8, 64, 8, inv: [new Stack(4, 0, 3), new Stack(3, 0, 5)]);
        c.Report(c.P(), Id(haul), "done", null, null);
        var j = c.Next();
        Assert.Equal("dump", Kind(j));
        Assert.Equal(["cobblestone", "dirt"], j["drop"]!.AsArray().Select(d => d!["item"]!.GetValue<string>()));
        Assert.Single(c.Alerts, a => a.Text.StartsWith("No storage for:"));
        Assert.Equal(2, c.State()["unstored_kinds"]!.AsArray().Count);
        // What lies at the drop spot stays there.
        var spot = j["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        c.Report(c.P(), Id(j), "done", null, null);
        h.Lives[c.P()] = FakeStoryHost.Live(5, 64, 5);
        w.Ents.Add(Drop(9, spot[0] + 0.5, spot[2] + 0.5));
        Assert.NotEqual("collect", Kind(c.Next()));
        // A dump zone is the drop spot when there is one.
        c.ZoneOp("add", "dump", Area.Of(-8, 64, -8, -6, 64, -6));
        Assert.Equal(-7, c.DumpSpot()!.Value.X);
    }

    [Fact]
    public void One_trip_collects_the_drops_near_each_other_and_another_pawn_does_not_get_them()
    {
        var (c, h, w) = StoryRig.Make(2, time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        w.Ents.Add(Drop(1, 6.5, 6.5));
        w.Ents.Add(Drop(2, 8.5, 6.5));
        w.Ents.Add(Drop(3, 7.5, 9.5));
        var j = c.Next(0);
        Assert.Equal("collect", Kind(j));
        Assert.Contains("(+2 more)", Label(j));
        Assert.True(j["r"]!.GetValue<int>() >= 4);
        Assert.Equal(3, c.Leases().Count(l => l.Key.StartsWith("e:") && l.Value == c.P(0)));
        Assert.NotEqual("collect", Kind(c.Next(1)));
        c.Report(c.P(0), Id(j), "done", null, null);
        Assert.DoesNotContain(c.Leases(), l => l.Key.StartsWith("e:"));
    }

    [Fact]
    public void A_working_pawn_is_cut_only_for_a_drop_about_to_despawn_and_only_when_hauling_matters_as_much()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(time: clock);
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        w.Tree(6, 6);
        c.Designate("add", "chop", Area.Of(4, 60, 4, 8, 75, 8), null, null);
        Prio(c, (WorkTypes.Haul, 2), (WorkTypes.Chop, 1));
        Assert.Equal("chop", Kind(c.Next()));
        w.Ents.Add(Drop(1, 3.5, -3.5, first: clock.Now - TimeSpan.FromSeconds(285)));
        clock.Now += TimeSpan.FromSeconds(6);
        c.Tick();
        Assert.Empty(h.Interrupted); // hauling is priority 2, chopping 1: no churn
        Prio(c, (WorkTypes.Haul, 1), (WorkTypes.Chop, 1));
        clock.Now += TimeSpan.FromSeconds(6);
        c.Tick();
        Assert.Contains(h.Interrupted, i => i.Contains("about to despawn"));
        Assert.Equal("collect", Kind(c.Next()));
    }

    // ================================================================ haul marks follow their drop (Fox 11-04: "the hauling icons don't go away")

    [Fact]
    public void One_trip_takes_three_designated_drops_and_all_three_marks_go()
    {
        var (c, h, w) = StoryRig.Make(time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        w.Ents.Add(Drop(1, 6.5, 6.5));
        w.Ents.Add(Drop(2, 8.5, 6.5));
        w.Ents.Add(Drop(3, 7.5, 9.5));
        Assert.Equal(3, c.Designate("add", "haul", Area.Of(4, 60, 4, 10, 70, 10), null, null).Result!["count"]!.GetValue<int>());
        var rev = c.Rev;
        var j = c.Next();
        Assert.Equal("collect", Kind(j));
        Assert.Contains("(+2 more)", Label(j));
        w.Ents.Clear(); // all three picked up on the one trip
        c.Report(c.P(), Id(j), "done", null, null);
        Assert.DoesNotContain(c.Designations, d => d.Kind == "haul");
        Assert.Equal(3, c.State(rev)["removed"]!["designations"]!.AsArray().Count); // the web clears all three icons
    }

    [Fact]
    public void A_designated_drop_someone_else_picks_up_loses_its_mark_and_one_that_merges_passes_it_on()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(time: clock);
        Prio(c); // nobody hauls: only the world changes
        w.Ents.Add(Drop(1, 5.5, 5.5));
        w.Ents.Add(Drop(2, 12.5, 5.5));
        c.Designate("add", "haul", Area.Of(4, 60, 4, 14, 70, 7), null, null);
        Assert.Equal(2, c.Designations.Count);
        c.Tick();
        // A player picks up the first; the second merges into a stack that lands right beside it (a new entity).
        w.Ents.RemoveAll(e => e.Id is 1 or 2);
        w.Ents.Add(Drop(9, 12.9, 5.7));
        var rev = c.Rev;
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Equal(9, c.Designations.Single(d => d.Entity != 1).Entity); // the mark follows the stack at once
        Assert.Contains(c.Designations, d => d.Entity == 1);              // the picked-up one waits a moment (packets in flight)
        clock.Now += StoryColony.EntityGone + TimeSpan.FromSeconds(1);
        c.Tick();
        Assert.Equal(9, Assert.Single(c.Designations).Entity);
        Assert.Single(c.State(rev)["removed"]!["designations"]!.AsArray());
    }

    // ================================================================ re-sorting chests

    [Fact]
    public void A_tightened_filter_sends_what_it_no_longer_allows_to_chests_that_take_it_in_one_trip()
    {
        var (c, h, w) = StoryRig.Make(time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        P3 a = new(2, 64, 0), wood = new(4, 64, 0), stone = new(6, 64, 0);
        c.StorageOp("set", [a], null, null, "General");
        c.StorageOp("set", [wood], new StorageFilter { Allow = ["wood"] }, null, "Wood");
        c.StorageOp("set", [stone], new StorageFilter { Allow = ["stone_building"] }, null, "Stone");
        c.AddContents(c.StorageAt(a)!, new Stack(17, 0, 20));
        c.AddContents(c.StorageAt(a)!, new Stack(4, 0, 40));
        Assert.NotEqual("go", Kind(c.Next())); // all fine where they are
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        c.StorageOp("set", [a], new StorageFilter { Allow = ["food"] }, null, null);
        var j = c.Next();
        Assert.Equal("go", Kind(j));
        Assert.Equal(2, j["fetch"]!.AsArray().Count);
        Assert.All(j["fetch"]!.AsArray(), f => Assert.Equal(a.ToString(), f!["from"]!.GetValue<string>()));
        var to = j["stash"]!.AsArray().ToDictionary(s => s!["to"]!.GetValue<string>(), s => s!["items"]![0]!.GetValue<string>());
        Assert.Equal("log", to[wood.ToString()]);
        Assert.Equal("cobblestone", to[stone.ToString()]);
        c.Report(c.P(), Id(j), "done", null, null);
        Assert.Empty(c.StorageAt(a)!.Contents);
        Assert.Equal(40, c.StorageAt(stone)!.Contents.Single().Count);
    }

    [Fact]
    public void With_no_chest_for_it_a_disallowed_stack_is_dropped_beside_the_chest_then_hauled_once_a_chest_takes_it()
    {
        var (c, h, w) = StoryRig.Make(time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        c.SetHaulOptions(null, false, null);
        P3 a = new(2, 64, 0);
        c.StorageOp("set", [a], new StorageFilter { Allow = ["food"] }, null, "Food");
        c.AddContents(c.StorageAt(a)!, new Stack(4, 0, 30));
        var j = c.Next();
        Assert.Equal("dump", Kind(j));
        Assert.Equal("cobblestone", j["drop"]![0]!["item"]!.GetValue<string>());
        Assert.Equal(a.ToString(), j["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.Single(c.Alerts, x => x.Text.StartsWith("No storage for: Cobblestone"));
        c.Report(c.P(), Id(j), "done", null, null);
        Assert.Empty(c.StorageAt(a)!.Contents);
        // On the ground by the chest: nowhere to go, so it lies there…
        w.Ents.Add(Drop(5, 2.5, 1.5));
        Assert.NotEqual("collect", Kind(c.Next()));
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        // …until a chest takes it.
        c.StorageOp("set", [new P3(6, 64, 0)], new StorageFilter { Allow = ["stone_building"] }, null, "Stone");
        Assert.Equal("collect", Kind(c.Next()));
        Assert.Empty(c.State()["unstored_kinds"]!.AsArray());
    }

    [Fact]
    public void Keep_disallowed_leaves_the_stack_in_its_chest()
    {
        var (c, h, w) = StoryRig.Make(time: NewClock());
        Prio(c, (WorkTypes.Haul, 1));
        c.SetHaulOptions(null, false, true);
        P3 a = new(2, 64, 0);
        c.StorageOp("set", [a], new StorageFilter { Allow = ["food"] }, null, "Food");
        c.AddContents(c.StorageAt(a)!, new Stack(4, 0, 30));
        Assert.DoesNotContain(Kind(c.Next()), new[] { "dump", "go" });
        Assert.Equal(30, c.StorageAt(a)!.Contents.Single().Count);
        Assert.True(c.State()["options"]!["keep_disallowed"]!.GetValue<bool>());
    }
}
