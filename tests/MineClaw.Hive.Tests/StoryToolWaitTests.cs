using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fox, 11-04: "If a pawn wants to mine something and doesn't have the tools to do it, it must stop acquiring a lock on the block and
/// then airing it out for 10 minutes because it couldn't mine it. Obviously it shouldn't be touching it." A target is offered (and
/// leased) only to a pawn that holds the tool or can fetch a verified one; otherwise it waits with why, no lease, no failure, no rest,
/// and it is offered again the moment a tool exists.
/// </summary>
public sealed class StoryToolWaitTests
{
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, -3), Ore = new(3, 64, 3);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(bool table, params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(1);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0);
        Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        if (table)
        {
            w.Set(Table.X, Table.Y, Table.Z, 58);
            Assert.Null(c.WorkbenchOp("designate", Table).Error);
        }
        w.Set(Ore.X, Ore.Y, Ore.Z, 15); // iron ore: a stone pickaxe at least
        Assert.Equal(1, c.Designate("add", "mine", null, [Ore], null).Result!["count"]!.GetValue<int>());
        return (c, h, w);
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    private static void Idle(StoryColony c)
    {
        var j = c.Next();
        Assert.NotEqual("dig", Kind(j));
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
    }

    [Fact]
    public void Without_a_pickaxe_anywhere_the_ore_is_never_leased_it_waits_with_why_and_nothing_fails()
    {
        var (c, _, _) = Rig(table: false);
        for (int i = 0; i < 5; i++) Idle(c);
        var d = c.Designations.Single();
        Assert.DoesNotContain(c.Leases(), l => l.Key == "b:" + Ore);
        Assert.Equal("waiting", d.Status);
        Assert.Equal(0, d.Fails);
        Assert.Null(d.Reason);
        Assert.Null(c.State()["designations"]![0]!["retry_at"]); // no back-off
        c.State(); // the materials pass says what the colony does about it
        Idle(c);
        Assert.Equal("needs a stone pickaxe — no crafting table for it", d.Wait);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed"));
    }

    [Fact]
    public void The_wait_says_whether_a_pickaxe_is_being_crafted_or_what_it_is_short_of()
    {
        var (c, _, _) = Rig(table: true, new Stack(4, 0, 3), new Stack(280, 0, 2));
        Idle(c);
        c.State();
        Idle(c);
        Assert.Equal("needs a stone pickaxe — crafting one", c.Designations.Single().Wait);

        var (c2, _, _) = Rig(table: true, new Stack(280, 0, 2)); // sticks, no cobblestone
        Idle(c2);
        c2.State();
        Idle(c2);
        Assert.Equal("needs a stone pickaxe — no cobblestone for it", c2.Designations.Single().Wait);
    }

    [Fact]
    public void A_pickaxe_in_storage_gets_the_ore_offered_at_the_next_pick()
    {
        var (c, _, w) = Rig(table: false);
        Idle(c);
        c.AddContents(c.StorageAt(Chest)!, new Stack(274, 0, 1));
        var j = c.Next();
        Assert.True(Kind(j) is "dig" or "equip", Kind(j)); // the dig with its fetch, or its policy fetching the pickaxe first
        Assert.Equal(w.Data.Item(274).Name, j["fetch"]![0]!["item"]!.GetValue<string>());
        if (Kind(j) == "dig") Assert.Equal("open", c.Designations.Single().Status);
    }

    [Fact]
    public void A_tool_refusal_or_a_missing_fetch_is_never_the_targets_failure()
    {
        var (c, h, _) = Rig(table: false, new Stack(274, 0, 1));
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1)); // its own stone pickaxe
        var dig = c.Next();
        Assert.Equal("dig", Kind(dig));
        // The pickaxe broke on the way: the strict dig refuses. Not the ore's fault.
        c.Report(c.P(), dig["id"]!.GetValue<string>(), "failed", $"needs a stone pickaxe: digging iron ore at {Ore} without it would lose the drop", null);
        var d = c.Designations.Single();
        Assert.Equal(0, d.Fails);
        Assert.Null(c.State()["designations"]![0]!["retry_at"]);
        var again = c.Next();
        Assert.Equal("dig", Kind(again)); // offered again at once (no 30 s / 1 m / 10 m rest)
        Assert.DoesNotContain("resting", again["label"]!.GetValue<string>());
    }

    [Fact]
    public void Old_tool_failures_and_their_rests_are_cleared_on_load()
    {
        var (c, h, _) = Rig(table: false);
        var d = c.Designations.Single();
        d.Fails = 3;
        d.Status = "failed";
        d.Reason = $"needs a stone pickaxe: digging iron ore at {Ore} without it would lose the drop";
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1));
        var j = c.Next(); // the first pick after the load purges them
        Assert.Equal(0, d.Fails);
        Assert.Null(d.Reason);
        Assert.Equal("dig", Kind(j));
    }
}
