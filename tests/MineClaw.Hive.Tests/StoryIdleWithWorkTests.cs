using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-04 story-1 (release 20261104-070104): "idle-with-work 191-309: no reachable target" pawn-seconds a minute, all evening and
/// across every release since the audit had that reason, with "collect … failed: no reachable drops nearby" ×36 in 4 minutes. The
/// audit counts a mark open when it is free and not resting; the picker also passes over marks that are buried, under someone's
/// feet, in a cave, by a stuck spot, forbidden, left to a pawn up top, or (haul, hunt) out of sight. docs/web/story-mode.md
/// "Idle with work".
/// </summary>
public sealed class StoryIdleWithWorkTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock NewClock() => new(DateTimeOffset.Parse("2026-11-04T12:00:00Z"));
    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private static void Only(StoryColony c, params (string Type, int P)[] ps)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = ps.FirstOrDefault(x => x.Type == t).P;
    }

    [Fact]
    public void No_reachable_target_says_which_mark_and_why_the_pawn_passed_it_over()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        Only(c, (WorkTypes.Mine, 1));
        w.Set(21, 64, 20, 3);
        c.Designate("add", "mine", null, [new P3(21, 64, 20), new P3(21, 63, 20)], "all"); // the stone under the dirt is buried
        Assert.Equal("dig", Kind(c.Next(0)));
        Assert.NotEqual("dig", Kind(c.Next(1))); // the dirt is the other pawn's, the stone is buried
        c.Tick();
        var why = c.IdleReasonOf(c.P(1));
        Assert.NotNull(why);
        Assert.StartsWith("no reachable target: mine 21,63,20: buried", why);
        Assert.Contains("idle-with-work", StoryPerf.TakeIdle());
    }

    /// <summary>A cave under stone at 5,55,5 (8 blocks of stone over it) with tall grass in it.</summary>
    private static void CaveGrass(FakeStoryWorld w)
    {
        w.Set(5, 55, 5, 31, 1);
        w.Set(5, 56, 5, 0);
    }

    /// <summary>Live 11-04 story-1 (release 20261104-074119): "no reachable target: cut 91,55,463: in a cave (surface work)", five pawns
    /// idle on it. A mark the owner placed is ordinary work wherever it is: the route, reach and stuck rules decide.</summary>
    [Fact]
    public void An_owner_cut_mark_in_a_cave_is_taken()
    {
        var (c, h, w) = StoryRig.Make(1, time: NewClock());
        Only(c, (WorkTypes.Chop, 1));
        CaveGrass(w);
        c.Designate("add", "cut", null, [new P3(5, 55, 5)], null);
        Assert.False(Assert.Single(c.Designations).Auto);
        var j = c.Next();
        Assert.Equal("dig", Kind(j));
        Assert.Equal("5,55,5", j["at"]!.GetValue<string>());
    }

    /// <summary>A cave plant the colony marked itself (seek_seeds' grass) is left alone by design, and the audit doesn't count a pawn
    /// with only that about as idle with work.</summary>
    [Fact]
    public void A_colony_marked_cave_plant_is_left_alone_and_is_no_blocked_work()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, (WorkTypes.Chop, 1));
        CaveGrass(w);
        c.Designate("add", "cut", null, [new P3(5, 55, 5)], null);
        Assert.Single(c.Designations).Auto = true;
        Assert.NotEqual("dig", Kind(c.Next()));
        c.Tick();
        Assert.Null(c.IdleReasonOf(c.P())); // no reason: not counted on the perf line nor in blocked_work
    }

    [Fact]
    public void A_haul_mark_whose_drop_is_out_of_sight_is_said_as_such()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, (WorkTypes.Haul, 1));
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        w.Ents.Add(new StoryEntity(1, "item", "log", 4.5, 64, 4.5, ItemId: 17, Count: 3));
        c.Designate("add", "haul", Area.Of(0, 60, 0, 15, 80, 15), null, null);
        w.Ents.Clear(); // the drop left every body's view (its mark goes only once the colony is sure it is gone)
        Assert.NotEqual("collect", Kind(c.Next()));
        c.Tick();
        var why = c.IdleReasonOf(c.P());
        Assert.NotNull(why);
        Assert.StartsWith("no reachable target: haul", why);
        Assert.EndsWith("out of sight", why);
    }

    /// <summary>The pawn up top that "takes the trees" must be one that will: idle, or chopping. One busy at other work may not come
    /// back to the trees for hours while the pawn under ground stands idle with the trees open.</summary>
    [Fact]
    public void A_pawn_under_ground_takes_the_trees_when_the_one_up_top_is_busy_at_other_work()
    {
        var (c, h, w) = StoryRig.Make(2, time: NewClock());
        Only(c);
        var deep = c.Pawns.Values.ElementAt(0);
        deep.Priorities[WorkTypes.Chop] = 1;
        deep.Priorities[WorkTypes.Mine] = 2;
        var top = c.Pawns.Values.ElementAt(1);
        top.Priorities[WorkTypes.Haul] = 1;
        top.Priorities[WorkTypes.Chop] = 2;
        for (int x = -3; x <= 3; x++)
            for (int z = -3; z <= 3; z++)
                for (int y = 54; y <= 62; y++) w.Set(x, y, z, 3);
        w.Set(0, 55, 0, 0); w.Set(0, 56, 0, 0);                        // its cell in the ground, 6 of dirt over its head
        h.Lives[c.P(0)] = FakeStoryHost.Live(0.5, 55, 0.5) with { ScriptRunning = false };
        h.Lives[c.P(1)] = FakeStoryHost.Live(10.5, 64, 10.5) with { ScriptRunning = false };
        c.StorageOp("set", [new P3(12, 64, 8)], null, null, null);
        w.Ents.Add(new StoryEntity(1, "item", "cobblestone", 12.5, 64, 12.5, ItemId: 4, Count: 3));
        w.Tree(6, 8, 5);
        c.Designate("add", "chop", null, [new P3(6, 64, 8)], null);
        c.Designate("add", "mine", null, [new P3(1, 55, 0)], null);       // work beside it down there
        Assert.Equal("collect", Kind(c.Next(1)));                         // the pawn up top is hauling (its priority 1)
        Assert.Equal("chop", Kind(c.Next(0)));                            // so the tree is this one's
    }

    /// <summary>A collect that found nothing while its drop is still in view rests the drop for that pawn only; another pawn may get
    /// to it (before: one pawn's miss rested it for the whole colony, 30 s, 1 min, then 10 min).</summary>
    [Fact]
    public void A_drop_one_pawn_could_not_collect_rests_for_that_pawn_first()
    {
        var (c, h, w) = StoryRig.Make(2, time: NewClock());
        Only(c, (WorkTypes.Haul, 1));
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        w.Ents.Add(new StoryEntity(1, "item", "cobblestone", 6.5, 64, 6.5, ItemId: 4, Count: 3));
        var j0 = c.Next(0);
        Assert.Equal("collect", Kind(j0));
        c.Report(c.P(0), Id(j0), "failed", "no reachable drops nearby", null);
        var j1 = c.Next(1);
        Assert.Equal("collect", Kind(j1));                               // the other pawn tries it
        Assert.Equal("6,64,6", j1["at"]!.GetValue<string>());
        Assert.NotEqual("collect", Kind(c.Next(0)));                     // not the one that missed it, while the other has it
    }

    /// <summary>A collect whose drops are gone from the view (another pawn or a player picked them up on the way) failed nothing: no
    /// failure on its haul designation, no alert.</summary>
    [Fact]
    public void A_collect_whose_drops_someone_else_picked_up_is_no_failure()
    {
        var (c, h, w) = StoryRig.Make(1, time: NewClock());
        Only(c, (WorkTypes.Haul, 1));
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        w.Ents.Add(new StoryEntity(1, "item", "log", 4.5, 64, 4.5, ItemId: 17, Count: 3));
        c.Designate("add", "haul", Area.Of(0, 60, 0, 15, 80, 15), null, null);
        var j = c.Next();
        Assert.Equal("collect", Kind(j));
        w.Ents.Clear(); // picked up by someone passing by
        c.Report(c.P(), Id(j), "failed", "no reachable drops nearby", null);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed", StringComparison.Ordinal));
        Assert.All(c.Designations, d => Assert.Equal(0, d.Fails));
    }
}
