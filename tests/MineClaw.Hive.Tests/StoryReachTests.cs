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
        Assert.Equal(["log"], items); // the 12 dirt stay: blocks to nerd-pole with
        c.Report(c.P(), Id(j), "done", null, null);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(3, 0, 40), new Stack(17, 0, 5)]);
        var big = c.Next();
        Assert.Contains("dirt", big["stash"]!.ToJsonString()); // more than the stack: it all goes (fetched back when needed)
        Assert.Equal(16, c.State()["options"]!["pillar_stack"]!.GetValue<int>());
    }

    [Fact]
    public void A_job_up_high_with_nothing_to_climb_with_carries_a_stack_from_storage()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, WorkTypes.Mine);
        c.StorageOp("set", [Chest], null, null, null);
        c.AddContents(c.StorageAt(Chest)!, new Stack(4, 0, 64));
        // A block hanging 7 above the pawn (a cave roof, a ledge): reachable only from a pillar.
        w.Set(3, 71, 3, 3);
        c.Designate("add", "mine", null, [new P3(3, 71, 3)], "all");
        var j = c.Next();
        Assert.Equal("dig", Kind(j));
        Assert.Equal(Chest.ToString(), j["pillar"]!["from"]!.GetValue<string>());
        Assert.Equal("cobblestone", j["pillar"]!["item"]!.GetValue<string>());
        c.Report(c.P(), Id(j), "failed", "too hard", null);
        // Carrying enough: no detour to storage.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(3, 0, 16));
        w.Set(4, 71, 3, 3);
        c.Designate("add", "mine", null, [new P3(4, 71, 3)], "all");
        var next = c.Next();
        Assert.Equal("4,71,3", next["at"]!.GetValue<string>());
        Assert.Null(next["pillar"]);
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
}
