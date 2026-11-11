using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fox 11-04: "With the blocks-out-of-reach prompt for mining, cutting or harvesting, it should nerd-pole up to reach those blocks on its
/// own and destroy the nerd pole on the way back down. Not many blocks should truly be out of reach." The colony's side: a pillar stack
/// kept out of store trips, a stack from storage for a job up high, and "out of reach even from a pillar" as a wait, not a failure.
/// </summary>
public sealed class StoryReachTests
{
    private static readonly P3 Chest = new(0, 64, 9);

    private static void Only(StoryColony c, params string[] types)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    [Fact]
    public void The_store_trip_keeps_a_small_pillar_stack_and_takes_a_big_one()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, WorkTypes.Haul);
        c.StorageOp("set", [Chest], null, null, null);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(3, 0, 12), new Stack(17, 0, 5)]);
        var j = c.Next();
        var items = j["stash"]!.AsArray().SelectMany(s => s!["items"]!.AsArray().Select(x => x!.GetValue<string>())).ToList();
        Assert.Equal(["oak_wood"], items); // the 12 dirt stay: blocks to nerd-pole with
        c.Report(c.P(), Id(j), "done", null, null);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(3, 0, 40), new Stack(17, 0, 5)]);
        var big = c.Next();
        Assert.Contains("3:0", big["stash"]!.ToJsonString()); // more than the stack: it all goes (fetched back when needed)
        Assert.Equal(16, c.State()["options"]!["pillar_stack"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(71)]
    [InlineData(59)]
    public void A_dig_with_nothing_to_climb_with_carries_a_stack_from_storage_even_below_the_pawn(int targetY)
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, WorkTypes.Mine);
        c.StorageOp("set", [Chest], null, null, null);
        c.AddContents(c.StorageAt(Chest)!, new Stack(4, 0, 64));
        // The target may be above the pawn, or below it but hanging above the quarry floor.
        w.Set(3, targetY, 3, 3);
        w.Set(3, targetY + 1, 3, 0);
        c.Designate("add", "mine", null, [new P3(3, targetY, 3)], "all");
        var j = c.Next();
        Assert.Equal("dig", Kind(j));
        Assert.Equal(Chest.ToString(), j["pillar"]!["from"]!.GetValue<string>());
        Assert.Equal("cobblestone", j["pillar"]!["item"]!.GetValue<string>());
        c.Report(c.P(), Id(j), "failed", "too hard", null);
        // Sixteen blocks cover ordinary work; a deeper climb has an optional top-up, with no mandatory fetch.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(3, 0, 16));
        w.Set(4, 71, 3, 3);
        c.Designate("add", "mine", null, [new P3(4, 71, 3)], "all");
        var next = c.Next();
        Assert.Equal("4,71,3", next["at"]!.GetValue<string>());
        Assert.Equal(8, next["pillar"]!["n"]!.GetValue<int>());
        Assert.Null(next["fetch"]);
    }

    [Fact]
    public void A_remnant_below_the_rim_keeps_a_supply_hint_for_a_climb_taller_than_the_retained_stack()
    {
        var (c, h, w) = StoryRig.Make();
        Only(c, WorkTypes.Mine);
        var chest = new P3(196, 64, 368);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        c.StorageOp("set", [chest], null, null, null);
        c.AddContents(c.StorageAt(chest)!, new Stack(4, 0, 64));
        h.Lives[c.P()] = FakeStoryHost.Live(195, 64, 370, inv: new Stack(4, 0, 16));
        var at = new P3(195, 57, 370);
        for (int y = 35; y <= 63; y++) w.Set(at.X, y, at.Z, 0);
        w.Set(at.X, at.Y, at.Z, 4);
        c.Designate("add", "mine", null, [at], "all");
        var job = c.Next();
        Assert.Equal("dig", Kind(job));
        Assert.Equal(at.ToString(), job["at"]!.GetValue<string>());
        Assert.Equal(chest.ToString(), job["pillar"]!["from"]!.GetValue<string>());
        Assert.Equal(8, job["pillar"]!["n"]!.GetValue<int>());
        Assert.Null(job["fetch"]);
    }

    [Fact]
    public void A_block_out_of_reach_even_from_a_pillar_waits_with_the_reason_instead_of_failing()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, WorkTypes.Chop);
        w.Set(3, 64, 3, 31, 1);
        c.Designate("add", "cut", null, [new P3(3, 64, 3)], null);
        var j = c.Next();
        c.Report(c.P(), Id(j), "failed", "out of reach even from a pillar: only liquid to stand on under it", null);
        var d = c.Designations.Single();
        Assert.Equal(0, d.Fails);
        Assert.Contains("only liquid to stand on", d.Wait);
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
    }

    /// <summary>Live 11-11: about 17,000 "out of reach even from a pillar" a day: once every pawn had tried, only each pawn's own rest
    /// (5 minutes at most) held such a mark, so they walked at it again and again (and a walk that fails from a pit sends a pawn up an
    /// escape pillar). It now rests for everyone, longer each round, and says when it is tried again.</summary>
    [Fact]
    public void A_mark_every_pawn_found_out_of_reach_even_from_a_pillar_rests_for_everyone_longer_each_round()
    {
        var clock = new ManualTime();
        var (c, _, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Chop);
        w.Set(3, 64, 3, 31, 1);
        c.Designate("add", "cut", null, [new P3(3, 64, 3)], null);
        var d = c.Designations.Single();
        foreach (var (wait, rest) in new[] { (0, 10), (11, 20), (21, 40) })
        {
            clock.Now += TimeSpan.FromMinutes(wait);
            var j = c.Next();
            Assert.Equal("3,64,3", j["at"]!.GetValue<string>());
            c.Report(c.P(), Id(j), "failed", "out of reach even from a pillar: only liquid to stand on under it", null);
            Assert.EndsWith($"(every pawn tried; again in {rest} min)", d.Wait);
            clock.Now += TimeSpan.FromMinutes(rest - 1);
            var idle = c.Next();
            Assert.NotEqual("3,64,3", idle["at"]?.GetValue<string>());
            c.Report(c.P(), Id(idle), "done", null, null);
        }
        Assert.Equal(0, d.Fails);
    }
}
