using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>The Story Mode director's job picker, leases, characteristics and persistence (docs/web/story-mode.md).</summary>
public sealed class StoryDirectorTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Prio(StoryColony c, int i, params (string Type, int P)[] ps)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = 0;
        foreach (var (t, v) in ps) p.Priorities[t] = v;
    }

    [Fact]
    public void Characteristics_are_seeded_by_the_name_skills_0_to_20_one_to_three_traits_never_brave_and_skittish()
    {
        var a = StoryCharacter.Generate("maid_chan_101");
        var b = StoryCharacter.Generate("maid_chan_101");
        Assert.Equal(a.Display, b.Display);
        Assert.Equal(a.Traits, b.Traits);
        Assert.Equal(a.Skills.Select(s => s.Value.Level), b.Skills.Select(s => s.Value.Level));
        for (int i = 100; i < 160; i++)
        {
            var p = StoryCharacter.Generate("maid_chan_" + i);
            Assert.Equal(WorkTypes.All.Count, p.Skills.Count);
            Assert.All(p.Skills.Values, s => Assert.InRange(s.Level, 0, 20));
            Assert.InRange(p.Traits.Count, 1, 3);
            Assert.False(p.Traits.Contains("brave") && p.Traits.Contains("skittish"));
            Assert.All(p.Priorities.Values, v => Assert.InRange(v, 1, 4)); // suggested from skills, never off
            Assert.Equal(24, p.Schedule.Length);
        }
        Assert.NotEqual(StoryCharacter.Generate("maid_chan_101").Skills.Select(s => s.Value.Level), StoryCharacter.Generate("maid_chan_102").Skills.Select(s => s.Value.Level));
    }

    [Fact]
    public void Skills_grow_with_experience_faster_with_passion_and_traits()
    {
        var p = StoryCharacter.Generate("x");
        p.Skills[WorkTypes.Farm] = new SkillState { Level = 0 };
        p.Traits = ["green_thumb"];
        Assert.Equal(1, StoryCharacter.Learn(p, WorkTypes.Farm, 50, 12)); // 50 × 2 = 100 = level 0's need
        p.Skills[WorkTypes.Mine] = new SkillState { Level = 0, Passion = true };
        Assert.Equal(0, StoryCharacter.Learn(p, WorkTypes.Mine, 50, 12));
        Assert.Equal(75, p.Skills[WorkTypes.Mine].Xp);
        Assert.Equal(6, StoryCharacter.Hour(0));      // tick 0 = 06:00
        Assert.Equal(18, StoryCharacter.Hour(12000));
        Assert.Equal(0, StoryCharacter.Hour(18000));
    }

    [Fact]
    public void Lower_priority_number_first_ties_by_column_order_or_by_skill()
    {
        var (c, h, w) = StoryRig.Make();
        w.Tree(10, 0);
        w.Set(2, 64, 0, 4); // a cobblestone block (mine designation) much nearer than the tree
        Assert.Equal(1, c.Designate("add", "chop", null, [new P3(10, 66, 0)], null).Result!["count"]!.GetValue<int>());
        Assert.Equal(1, c.Designate("add", "mine", null, [new P3(2, 64, 0)], null).Result!["count"]!.GetValue<int>());
        Prio(c, 0, (WorkTypes.Chop, 1), (WorkTypes.Mine, 2));
        Assert.Equal("chop", c.Next()["kind"]!.GetValue<string>()); // priority 1 beats the nearer priority-2 job
        Assert.True(c.Report(c.P(), c.CurrentOf(c.P())!.Id, "failed", "test", null));

        Prio(c, 0, (WorkTypes.Chop, 2), (WorkTypes.Mine, 2));
        Assert.Equal(["mine", "chop"], c.WorkOrder(c.Pawns.Values.First())); // equal: column order (mine before chop)
        var p = c.Pawns.Values.First();
        p.Skills[WorkTypes.Chop].Level = 15; p.Skills[WorkTypes.Mine].Level = 2;
        c.Meta.TieBreak = "skill";
        Assert.Equal(["chop", "mine"], c.WorkOrder(p)); // skill first when asked
        Prio(c, 0, (WorkTypes.Chop, 0), (WorkTypes.Mine, 4));
        Assert.Equal(["mine"], c.WorkOrder(p)); // off is never done
    }

    [Fact]
    public void The_schedule_hour_comes_from_the_world_time()
    {
        var (c, h, w) = StoryRig.Make();
        w.Tree(5, 5);
        c.Designate("add", "chop", Area.Of(0, 60, 0, 10, 80, 10), null, null);
        var p = c.Pawns.Values.First();
        p.Schedule[2] = ScheduleBlocks.Sleep; p.Schedule[14] = ScheduleBlocks.Free;
        w.TimeOfDay = 20000; // 02:00
        var j = c.Next();
        Assert.Equal("sleep", j["kind"]!.GetValue<string>());
        Assert.Equal("0,64,0", j["at"]!.GetValue<string>()); // home
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        w.TimeOfDay = 8000; // 14:00 free
        j = c.Next();
        Assert.Equal("idle", j["kind"]!.GetValue<string>());
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        w.TimeOfDay = 4000; // 10:00 anything: work
        Assert.Equal("chop", c.Next()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Drafted_pawns_ignore_work_and_needs_and_only_follow_orders()
    {
        var (c, h, w) = StoryRig.Make();
        w.Tree(3, 3);
        c.Designate("add", "chop", Area.Of(0, 60, 0, 10, 80, 10), null, null);
        var (ps, _) = c.Resolve([c.P()]);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, food: 2, inv: new Stack(297, 0, 3)); // starving, has bread
        c.Draft(ps, true);
        var j = c.Next();
        Assert.Equal("hold", j["kind"]!.GetValue<string>());
        Assert.Equal("order", j["type"]!.GetValue<string>());
        Assert.Equal("drafted", c.State()["pawns"]![0]!["state"]!.GetValue<string>());
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        Assert.Null(c.GiveOrder(ps, new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["x"] = 9, ["y"] = 64, ["z"] = 9 } }, false).Error);
        j = c.Next();
        Assert.Equal("go", j["kind"]!.GetValue<string>());
        Assert.Equal("9,64,9", j["at"]!.GetValue<string>());
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        Assert.Empty(c.Pawns.Values.First().Queue);
        // Queued orders run in turn.
        c.GiveOrder(ps, new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["x"] = 1, ["y"] = 64, ["z"] = 1 } }, false);
        c.GiveOrder(ps, new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["x"] = 2, ["y"] = 64, ["z"] = 2 } }, true);
        Assert.Equal(2, c.Pawns.Values.First().Queue.Count);
        Assert.Equal("1,64,1", c.Next()["at"]!.GetValue<string>()); // the first, then the queued one
        // Undrafted: back to the picker, and urgent needs come first (eat).
        c.Draft(ps, false);
        Assert.Contains(h.Interrupted, i => i.EndsWith("undrafted"));
        Assert.Equal("eat", c.Next()["kind"]!.GetValue<string>());
        Assert.StartsWith("order.kind must be", c.GiveOrder(ps, new JsonObject { ["kind"] = "dance" }, false).Error);
    }

    [Fact]
    public void Urgent_needs_win_and_traits_shape_the_thresholds()
    {
        var (c, h, w) = StoryRig.Make();
        w.Tree(3, 3);
        c.Designate("add", "chop", Area.Of(0, 60, 0, 10, 80, 10), null, null);
        var p = c.Pawns.Values.First();
        Prio(c, 0, (WorkTypes.Chop, 1)); // not a guard: the zombie is only a danger here
        w.Ents.Add(new StoryEntity(77, "mob", "zombie", 3, 64, 0, Hostile: true));
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, hp: 10);
        Assert.Equal("chop", c.Next()["kind"]!.GetValue<string>()); // 10 hp: fine for a plain pawn
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "failed", "x", null);
        p.Traits = ["skittish"];
        var j = c.Next();
        Assert.Equal("go", j["kind"]!.GetValue<string>());
        Assert.StartsWith("fleeing", j["label"]!.GetValue<string>()); // skittish flees at 12
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        p.Traits = ["brave"];
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, hp: 6);
        Assert.NotEqual("fleeing", c.Next()["label"]!.GetValue<string>()[..7]); // brave stays at 6
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "failed", "x", null);
        p.Traits = [];
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, food: 5);
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        c.AddContents(c.StorageAt(new P3(0, 64, 4))!, new Stack(297, 0, 10));
        j = c.Next();
        Assert.Equal("eat", j["kind"]!.GetValue<string>()); // no food on it: fetch from storage, then eat
        Assert.Equal("0,64,4", j["fetch"]![0]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void Two_pawns_never_take_the_same_target_and_leases_go_back()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var (c, h, w) = StoryRig.Make(pawns: 3, time: clock);
        w.Tree(4, 0); w.Tree(8, 0);
        Assert.Equal(2, c.Designate("add", "chop", Area.Of(0, 60, -2, 10, 80, 2), null, null).Result!["count"]!.GetValue<int>());
        foreach (var i in Enumerable.Range(0, 3)) Prio(c, i, (WorkTypes.Chop, 1));
        var a = c.Next(0);
        var b = c.Next(1);
        var third = c.Next(2);
        Assert.Equal("chop", a["kind"]!.GetValue<string>());
        Assert.Equal("chop", b["kind"]!.GetValue<string>());
        Assert.NotEqual(a["at"]!.GetValue<string>(), b["at"]!.GetValue<string>());
        Assert.NotEqual("chop", third["kind"]!.GetValue<string>()); // both trees leased: the third idles
        Assert.Equal(2, c.Leases().Count);
        // Asking again returns the job it already holds (a script restart), not a new target.
        Assert.Equal(a["id"]!.GetValue<string>(), c.Next(0)["id"]!.GetValue<string>());
        // A stale lease (the pawn vanished: its script isn't running any more) runs out and the target goes back. A pawn whose script
        // still runs keeps its lease (see StoryMiningFixTests).
        c.Report(c.P(2), third["id"]!.GetValue<string>(), "done", null, null);
        foreach (var i in new[] { 0, 1 }) h.Lives[c.P(i)] = h.Lives[c.P(i)] with { ScriptRunning = false };
        clock.Now += StoryColony.LeaseTtl + TimeSpan.FromSeconds(1);
        c.Tick();
        Assert.Empty(c.Leases());
        Assert.Equal("chop", c.Next(2)["kind"]!.GetValue<string>());
        // A report on a job it no longer holds is refused.
        Assert.False(c.Report(c.P(0), a["id"]!.GetValue<string>(), "done", null, null));
    }

    [Fact]
    public void A_restarted_pawn_script_is_started_again_by_the_watchdog()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var (c, h, _) = StoryRig.Make(time: clock);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0) with { ScriptRunning = false };
        c.Tick();
        Assert.Equal([c.P()], h.Started);
        c.Tick();
        Assert.Single(h.Started); // not twice within 5 s
        clock.Now += TimeSpan.FromSeconds(6);
        c.Tick();
        Assert.Equal(2, h.Started.Count);
        c.Pause(true);
        Assert.Equal("hold", c.Next()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void The_colony_survives_a_restart_in_the_agent_db()
    {
        using var db = AgentDb.Open(null);
        var store = new StoryStore(db);
        var w = new FakeStoryWorld();
        var h = new FakeStoryHost(w);
        var colonies = new StoryColonies(h, store);
        var (c, err) = colonies.Create("Riverside", 2, new P3(1, 64, 1));
        Assert.Null(err);
        w.Tree(5, 5);
        c!.Designate("add", "chop", Area.Of(0, 60, 0, 9, 80, 9), null, null);
        c.ZoneOp("add", "farm", Area.Of(-5, 63, -5, -1, 64, -1));
        c.StorageOp("set", [new P3(2, 64, 2)], new StorageFilter { Allow = ["wood"] }, "critical", "Wood");
        c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "wall", ["area"] = new JsonArray(10, 64, 10, 12, 64, 12), ["height"] = 2 }), null);
        var (ps, _) = c.Resolve([c.P(0)]);
        c.SetPriorities(ps, new Dictionary<string, int> { ["chop"] = 1, ["haul"] = 0 });
        c.SetSchedule(ps, new Dictionary<string, string> { ["3"] = "work" });
        c.Draft(ps, true);
        c.Save(force: true);
        long rev = c.Rev;

        var again = new StoryColonies(new FakeStoryHost(w), store);
        Assert.Equal(1, again.Load());
        var r = again.Get("story-1")!;
        Assert.Equal("Riverside", r.Meta.Name);
        Assert.Equal(c.Pawns.Keys.Order(), r.Pawns.Keys.Order());
        var p = r.Pawns[c.P(0)];
        Assert.Equal(1, p.Priorities["chop"]);
        Assert.Equal(0, p.Priorities["haul"]);
        Assert.Equal("work", p.Schedule[3]);
        Assert.True(p.Drafted);
        Assert.Equal(c.Pawns[c.P(0)].Display, p.Display);
        Assert.Single(r.Designations);
        Assert.Single(r.Zones);
        Assert.Equal("critical", r.Storages.Single().Priority);
        Assert.Equal(["wood"], r.Storages.Single().Filter.Allow);
        Assert.Equal(8 * 2, r.Builds.Single().BlocksTotal);
        Assert.True(r.Rev >= rev);
        // It resumed: its pawns came back on their old accounts.
        Assert.All(c.Pawns.Keys, k => Assert.True(((FakeStoryHost)again.Host).Lives.ContainsKey(k)));
    }

    [Fact]
    public void The_state_delta_carries_only_what_changed_and_what_went()
    {
        var (c, h, w) = StoryRig.Make();
        var full = c.State();
        Assert.True(full["full"]!.GetValue<bool>());
        Assert.NotNull(full["categories"]);
        long rev = full["rev"]!.GetValue<long>();
        var z = c.ZoneOp("add", "home", Area.Of(-8, 60, -8, 8, 70, 8)).Result!["id"]!.GetValue<string>();
        var d1 = c.State(rev);
        Assert.Single(d1["zones"]!.AsArray());
        Assert.Null(d1["categories"]);
        rev = d1["rev"]!.GetValue<long>();
        Assert.Empty(c.State(rev)["zones"]!.AsArray());
        c.ZoneOp("remove", null, Area.Of(0, 64, 0, 0, 64, 0));
        var d2 = c.State(rev);
        Assert.Equal(z, d2["removed"]!["zones"]![0]!.GetValue<string>());
        Assert.Single(d2["pawns"]!.AsArray()); // pawns always (they move)
        var pawn = d2["pawns"]![0]!.AsObject();
        foreach (var k in new[] { "name", "display", "kind", "online", "hp", "food", "pos", "dim", "drafted", "state", "job", "queue", "skills", "traits", "priorities", "schedule", "held", "inventory" })
            Assert.True(pawn.ContainsKey(k), k);
        Assert.Equal(24, pawn["schedule"]!.AsArray().Count);
    }
}
