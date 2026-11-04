using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-03 (colony story-1, 1.12.2): home left at world spawn, pawns that never moved, chop designations on the canopy top that
/// resolved to no tree, scripts cut by "new work" several times a second, and designations that vanished after three failures.
/// </summary>
public sealed class StoryLiveFixTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock NewClock() => new(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));

    private static void Only(StoryColony c, int i, params string[] types)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    /// <summary>A tall tree: a trunk of <paramref name="h"/> logs from y 64 and a 5x5 crown over its top three logs, capped above.</summary>
    private static void TallTree(FakeStoryWorld w, int x, int z, int h)
    {
        int top = 64 + h - 1;
        for (int y = top - 2; y <= top + 1; y++)
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    w.Set(x + dx, y, z + dz, 18);
        for (int y = 64; y <= top; y++) w.Set(x, y, z, 17);
    }

    // ================================================================ chop designations resolve to the tree

    [Fact]
    public void A_chop_designation_on_the_canopy_top_or_a_branch_resolves_to_the_trunk_base()
    {
        var (c, _, w) = StoryRig.Make();
        TallTree(w, 10, 10, 12);                    // logs 64..75, crown 73..76
        w.Set(11, 74, 10, 17); w.Set(12, 75, 10, 17); // a branch off the crown (leaves and air under it, never ground)

        // A top-down click lands on the canopy's top leaf: the tree is its trunk.
        var r = c.Designate("add", "chop", null, [new P3(9, 76, 11)], null);
        Assert.Null(r.Error);
        Assert.Equal(1, r.Result!["count"]!.GetValue<int>());
        var d = Assert.Single(c.Designations);
        Assert.Equal(new P3(10, 64, 10), d.At);
        Assert.Contains(new P3(10, 75, 10), d.Blocks!);
        // The branch log and the top log are the same tree: nothing new.
        Assert.Equal(0, c.Designate("add", "chop", null, [new P3(12, 75, 10), new P3(10, 75, 10)], null).Result!["count"]!.GetValue<int>());
        // The air just over the canopy (the clicked block's top face) also means the tree.
        c.Designate("cancel", "chop", null, null, null, [d.Id]);
        Assert.Equal(1, c.Designate("add", "chop", null, [new P3(10, 77, 10)], null).Result!["count"]!.GetValue<int>());
        // Not a tree at all: nothing.
        Assert.Equal(0, c.Designate("add", "chop", null, [new P3(30, 64, 30)], null).Result!["count"]!.GetValue<int>());
    }

    [Fact]
    public void A_box_drawn_over_the_canopy_only_finds_each_tree_once_by_its_base()
    {
        var (c, _, w) = StoryRig.Make();
        TallTree(w, 4, 4, 12);
        TallTree(w, 12, 4, 10);
        w.Set(5, 74, 4, 17); // a branch: not a tree of its own
        // The box covers only the crowns' top layer (what a top-down drag sees).
        var r = c.Designate("add", "chop", Area.Of(0, 76, 0, 16, 76, 8), null, null);
        Assert.Null(r.Error);
        Assert.Equal(2, r.Result!["count"]!.GetValue<int>());
        Assert.Equal([new P3(4, 64, 4), new P3(12, 64, 4)], c.Designations.Select(x => x.At!.Value).OrderBy(p => p.X).ToList());
        // A box at the ground still works the old way.
        var (c2, _, w2) = StoryRig.Make();
        w2.Tree(2, 2, 5);
        Assert.Equal(1, c2.Designate("add", "chop", Area.Of(0, 64, 0, 4, 64, 4), null, null).Result!["count"]!.GetValue<int>());
    }

    [Fact]
    public void A_chop_job_carries_what_the_script_needs_to_finish_a_tree_from_an_empty_inventory()
    {
        var (c, _, w) = StoryRig.Make();
        Only(c, 0, WorkTypes.Chop);
        TallTree(w, 6, 0, 12);
        c.Designate("add", "chop", null, [new P3(6, 76, 0)], null);
        var j = c.Next();
        Assert.Equal("chop", j["kind"]!.GetValue<string>());
        Assert.Equal("6,64,0", j["at"]!.GetValue<string>());
        Assert.Contains(j["wood"]!.AsArray(), n => n!.GetValue<string>() == w.Data.Item(17).Name);
        Assert.Equal(w.Data.Item(5).Name, j["planks"]!.GetValue<string>());
        var logs = j["logs"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal("6,64,0", logs[0]);   // lowest first
        Assert.Contains("6,75,0", logs);
    }

    [Fact]
    public void A_partial_chop_is_done_with_an_alert_and_the_tree_leaves_the_map()
    {
        var (c, _, w) = StoryRig.Make();
        Only(c, 0, WorkTypes.Chop);
        TallTree(w, 6, 0, 12);
        c.Designate("add", "chop", null, [new P3(6, 64, 0)], null);
        var j = c.Next();
        for (int y = 64; y < 69; y++) w.Set(6, y, 0, 0); // the reachable logs came down
        Assert.True(c.Report(c.P(), j["id"]!.GetValue<string>(), "done", "partial: 5 log(s) taken; 7 log(s) out of reach (up to y 75)", null));
        Assert.Empty(c.Designations);
        Assert.Contains(c.Alerts, a => a.Text.Contains("took what it could reach") && a.Text.Contains("out of reach"));
    }

    [Fact]
    public void The_shipped_pawn_script_finishes_a_tree_from_planks_and_reports_partial_success()
    {
        var code = StoryColonies.PawnCode();
        Assert.Contains("function chopTree(j)", code);
        Assert.Contains("case 'chop': return chopTree(j)", code);
        Assert.Contains("craft(j.planks", code);
        Assert.Contains("partial: ", code);
    }

    // ================================================================ no job thrash

    [Fact]
    public void Idle_pawns_are_not_cut_over_and_over_when_work_or_priorities_change()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(pawns: 3, time: clock);
        c.Meta.Home = null; // no home: nowhere to wander, so the plain idle hold (idle legs: StoryIdleTests)
        for (int i = 0; i < 3; i++) Only(c, i, WorkTypes.Chop);
        for (int i = 0; i < 3; i++)
        {
            c.Next(i);
            Assert.Equal(StoryColony.NothingToDo, c.CurrentOf(c.P(i))!.Label);
        }

        // What Fox did live: work-panel clicks (a POST each) and a designation, while every pawn held a short idle.
        w.Tree(6, 0);
        c.Designate("add", "chop", Area.Of(0, 60, -2, 10, 80, 2), null, null);
        var pawns = c.Pawns.Values.ToList();
        for (int k = 0; k < 10; k++)
        {
            Assert.Null(c.SetPriorities(pawns, new Dictionary<string, int> { [WorkTypes.Chop] = 1 + k % 2 }));
            c.Tick();
            clock.Now += TimeSpan.FromMilliseconds(300);
        }
        // Before: 10 clicks x 3 idle pawns = 30 cut scripts ("cancelled ... (new work)"). A short idle ends by itself.
        Assert.Empty(h.Interrupted);

        // A pawn stuck in a long idle (walking home from far) is cut once for the new work, not once per click.
        clock.Now += StoryColony.WakeAfter;
        for (int k = 0; k < 10; k++)
        {
            c.SetPriorities(pawns, new Dictionary<string, int> { [WorkTypes.Chop] = 1 + k % 2 });
            c.Tick();
            clock.Now += TimeSpan.FromMilliseconds(500);
        }
        Assert.InRange(h.Interrupted.Count, 1, 3);
        Assert.Equal(h.Interrupted.Count, h.Interrupted.Distinct().Count());
        // The same values again are no new work at all.
        int before = h.Interrupted.Count;
        clock.Now += StoryColony.WakeCooldown + StoryColony.WakeAfter;
        for (int i = 0; i < 3; i++) c.Next(i);
        clock.Now += StoryColony.WakeAfter;
        c.SetPriorities(pawns, new Dictionary<string, int> { [WorkTypes.Chop] = 2 }); // what they already are
        c.SetPriorities(pawns, new Dictionary<string, int> { [WorkTypes.Chop] = 2 });
        c.Tick();
        Assert.Equal(before, h.Interrupted.Count);
    }

    [Fact]
    public void A_pawn_keeps_its_job_until_it_ends_and_new_work_never_cuts_a_working_pawn()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(pawns: 1, time: clock);
        Only(c, 0, WorkTypes.Chop);
        w.Tree(4, 0);
        c.Designate("add", "chop", Area.Of(0, 60, -2, 10, 80, 2), null, null);
        var j = c.Next();
        Assert.Equal("chop", j["kind"]!.GetValue<string>());
        for (int k = 0; k < 5; k++)
        {
            w.Tree(8 + 2 * k, 0);
            c.Designate("add", "chop", Area.Of(0, 60, -2, 20, 80, 2), null, null);
            c.SetPriorities(c.Pawns.Values.ToList(), new Dictionary<string, int> { [WorkTypes.Chop] = 1 + k % 2 });
            clock.Now += TimeSpan.FromSeconds(10);
            c.Tick();
            Assert.Equal(j["id"]!.GetValue<string>(), c.Next()["id"]!.GetValue<string>()); // the same job, still
        }
        Assert.Empty(h.Interrupted);
    }

    [Fact]
    public void A_pawn_whose_jobs_keep_failing_rests_instead_of_looping()
    {
        var clock = NewClock();
        var (c, _, _) = StoryRig.Make(time: clock);
        Only(c, 0, WorkTypes.Mine);
        c.Designate("add", "mine", null, [new P3(1, 63, 0), new P3(2, 63, 0), new P3(3, 63, 0), new P3(4, 63, 0)], null);
        var tried = new List<string>();
        for (int k = 0; k < 3; k++)
        {
            var j = c.Next();
            Assert.Equal("dig", j["kind"]!.GetValue<string>());
            tried.Add(j["at"]!.GetValue<string>());
            // (A run of "no route" failures is a stuck pawn now: an escape job, StoryStuckPawnTests. Any other failure: a rest.)
            c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "too hard to dig", null);
            clock.Now += TimeSpan.FromSeconds(1);
        }
        Assert.Equal(3, tried.Distinct().Count()); // never the one that just failed
        var rest = c.Next();
        Assert.Equal("hold", rest["kind"]!.GetValue<string>());
        Assert.StartsWith("resting", rest["label"]!.GetValue<string>());
        c.Report(c.P(), rest["id"]!.GetValue<string>(), "done", null, null);
        clock.Now += StoryColony.FailRest;
        Assert.Equal("dig", c.Next()["kind"]!.GetValue<string>()); // rested: back to work (the fourth block)
    }

    [Fact]
    public void A_failed_designation_stays_on_the_map_with_its_reason_and_backs_off()
    {
        var clock = NewClock();
        var (c, _, w) = StoryRig.Make(time: clock);
        Only(c, 0, WorkTypes.Chop);
        w.Tree(6, 0);
        c.Designate("add", "chop", Area.Of(0, 60, -2, 10, 80, 2), null, null);
        var d = Assert.Single(c.Designations);
        for (int k = 1; k <= 3; k++)
        {
            var j = c.Next();
            Assert.Equal("chop", j["kind"]!.GetValue<string>());
            c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "no tree at 6,64,0", null);
            Assert.Same(d, Assert.Single(c.Designations)); // kept
            Assert.Equal(k, d.Fails);
            var again = c.Next();
            Assert.NotEqual("chop", again["kind"]!.GetValue<string>()); // backed off: not re-picked at once
            c.Report(c.P(), again["id"]!.GetValue<string>(), "done", null, null);
            clock.Now += TimeSpan.FromSeconds(30 * k + 1);
        }
        Assert.Equal("failed", d.Status);
        Assert.Equal("no tree at 6,64,0", d.Reason);
        Assert.Contains(c.Alerts, a => a.Text.Contains("failed 1 times"));
        Assert.Contains(c.Alerts, a => a.Text.Contains("failed 3 times") && a.Text.Contains("kept on the map"));
        var shown = c.State()["designations"]!.AsArray().Single()!;
        Assert.Equal("failed", shown["status"]!.GetValue<string>());
        Assert.Equal("no tree at 6,64,0", shown["reason"]!.GetValue<string>());
        // Retried after the long rest; done → gone.
        clock.Now += TimeSpan.FromMinutes(10);
        var last = c.Next();
        Assert.Equal("chop", last["kind"]!.GetValue<string>());
        for (int y = 64; y < 68; y++) w.Set(6, y, 0, 0); // felled
        c.Report(c.P(), last["id"]!.GetValue<string>(), "done", null, null);
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void A_failed_delivery_is_not_retried_in_a_tight_loop()
    {
        var clock = NewClock();
        var (c, h, _) = StoryRig.Make(time: clock);
        Only(c, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(4, 0, 10));
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        var j = c.Next();
        Assert.Contains("storing", j["label"]!.GetValue<string>());
        c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "no route to the chest", null);
        var next = c.Next();
        Assert.DoesNotContain("storing", next["label"]!.GetValue<string>());
        c.Report(c.P(), next["id"]!.GetValue<string>(), "done", null, null);
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Contains("storing", c.Next()["label"]!.GetValue<string>());
    }

    // ================================================================ home

    [Fact]
    public void Moving_home_brings_far_pawns_there_by_teleport_to_a_standable_spot()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(pawns: 4, time: clock);
        h.Lives[c.P(1)] = FakeStoryHost.Live(194, 18, 340, hp: 1);                       // far, same dimension
        h.Lives[c.P(2)] = FakeStoryHost.Live(90, 64, 95);                                // already near the new home
        h.Lives[c.P(3)] = FakeStoryHost.Live(100, 64, 100) with { Dim = -1 };            // the nether
        c.Next(1); // it holds a job over there: cut when it's brought home
        c.SetHome(new P3(100, 60, 100)); // a click on the ground's top face, inside the stone: the spot is the surface above
        Assert.Equal(new P3(100, 60, 100), c.Meta.Home);
        Assert.Equal(new[] { c.P(0), c.P(1) }, h.Teleported.Select(t => t.Account).OrderBy(x => x).ToArray());
        // Each to its own standable spot by home, never one cell for all (the first gets the home column itself).
        Assert.Equal(new P3(100, 64, 100), h.Teleported[0].To);
        Assert.All(h.Teleported, t => Assert.True(t.To.Y == 64 && Math.Abs(t.To.X - 100) <= 4 && Math.Abs(t.To.Z - 100) <= 4, t.To.ToString()));
        Assert.True(StoryWander.Gap(h.Teleported[0].To, h.Teleported[1].To) >= 2, "spread out");
        Assert.Contains(c.Alerts, a => a.Text.Contains("another dimension"));
        Assert.Equal(2, c.Alerts.Count(a => a.Text.Contains("brought home to")));
        Assert.Contains(h.Interrupted, i => i.StartsWith(c.P(1)) && i.EndsWith("brought home"));
        // Not again right away (the teleport takes a moment to show in its position).
        c.BringHome("again");
        Assert.Equal(2, h.Teleported.Count);
        Assert.Equal(new[] { 100, 60, 100 }, c.State()["home"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void A_pawn_that_joins_far_from_home_is_brought_there_and_one_that_cant_be_teleported_walks()
    {
        var clock = NewClock();
        var (c, h, _) = StoryRig.Make(pawns: 2, time: clock);
        c.Tick(); // both at home: nothing
        Assert.Empty(h.Teleported);
        h.Lives[c.P(1)] = PawnLive.Offline;
        c.Tick();
        h.Lives[c.P(1)] = FakeStoryHost.Live(250, 70, -40); // reconnects far away
        c.Tick();
        var tp = Assert.Single(h.Teleported);
        Assert.Equal(c.P(1), tp.Account);
        Assert.True(StoryWander.Gap(tp.To, new P3(0, 64, 0)) is >= 2 and <= 4, tp.To.ToString()); // by home, not on the pawn standing there

        // b1.8.1's /tp takes no coordinates (or the gate refused): a move order home, before any work.
        h.TeleportError = "b1.8.1's /tp takes no coordinates";
        h.Lives[c.P(0)] = PawnLive.Offline;
        c.Tick();
        h.Lives[c.P(0)] = FakeStoryHost.Live(-300, 64, 10);
        c.Tick();
        var p0 = c.Pawns[c.P(0)];
        var order = Assert.Single(p0.Queue);
        Assert.Equal("move", order.Kind);
        Assert.True(order.At is { Y: 64 } at && Math.Abs(at.X) <= 4 && Math.Abs(at.Z) <= 4, order.At.ToString());
        var j = c.Next(0);
        Assert.Equal("go", j["kind"]!.GetValue<string>());
        Assert.Contains(c.Alerts, a => a.Text.Contains("walks home"));
    }

    [Fact]
    public void The_safe_spot_is_ground_under_two_free_cells_never_leaves_water_or_an_unloaded_column()
    {
        var w = new FakeStoryWorld();
        // A tree's crown over home's column: the spot is beside it on the ground, never on the leaves.
        w.Set(0, 64, 0, 17); w.Set(0, 65, 0, 17); w.Set(0, 66, 0, 18);
        Assert.Equal(new P3(-1, 64, -1), StoryColony.SafeSpot(w, new P3(0, 70, 0)));
        // Standing water at the ground: the next column.
        var w2 = new FakeStoryWorld();
        w2.Set(5, 64, 5, 9);
        var s = StoryColony.SafeSpot(w2, new P3(5, 64, 5))!.Value;
        Assert.NotEqual((5, 5), (s.X, s.Z));
        Assert.Equal(64, s.Y);
        // A cave home with a floor: the home cell itself.
        var w3 = new FakeStoryWorld();
        for (int y = 64; y <= 90; y++) w3.Set(9, y, 9, 1);
        w3.Set(9, 70, 9, 0); w3.Set(9, 71, 9, 0);
        Assert.Equal(new P3(9, 70, 9), StoryColony.SafeSpot(w3, new P3(9, 70, 9), radius: 0));
        Assert.Null(StoryColony.SafeSpot(new UnloadedWorld(), new P3(0, 64, 0)));
    }

    [Theory]
    [InlineData("cut")]
    [InlineData("harvest")]
    [InlineData("mine")]
    [InlineData("deconstruct")]
    public void A_designation_said_done_while_its_block_is_still_there_is_a_logged_failure_not_a_silent_drop(string kind)
    {
        var clock = NewClock();
        var (c, _, w) = StoryRig.Make(time: clock);
        var at = new P3(3, 64, 3);
        switch (kind)
        {
            case "cut": w.Set(3, 64, 3, 31, 1); break;    // tall grass
            case "harvest": w.Set(3, 64, 3, 59, 7); break; // ripe wheat
            case "mine": w.Set(3, 64, 3, 1); break;
            case "deconstruct": w.Set(3, 64, 3, 5); break; // planks
        }
        Assert.Equal(1, c.Designate("add", kind, null, [at], null).Result!["count"]!.GetValue<int>());
        var j = c.Next();
        Assert.Equal(at.ToString(), j["at"]!.GetValue<string>());
        // The live failure: the pawn's dig on a block out of its view did nothing and said ok.
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        var d = Assert.Single(c.Designations);
        Assert.Equal(1, d.Fails);
        Assert.Contains("still there", d.Reason);
        Assert.Contains(c.Alerts, a => a.Level == "warn" && a.Text.StartsWith(kind + " at 3,64,3 failed 1 times"));
        // Really done: gone.
        clock.Now += TimeSpan.FromSeconds(31);
        var again = c.Next();
        Assert.Equal(at.ToString(), again["at"]!.GetValue<string>());
        if (kind == "harvest") w.Set(3, 64, 3, 59, 0); else w.Set(3, 64, 3, 0);
        c.Report(c.P(), again["id"]!.GetValue<string>(), "done", null, null);
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void A_hungry_pawn_with_no_food_anywhere_and_no_storage_keeps_working()
    {
        var (c, h, w) = StoryRig.Make();
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, food: 2, inv: new Stack(17, 0, 5));
        w.Tree(4, 0);
        c.Designate("add", "chop", Area.Of(0, 60, -2, 10, 80, 2), null, null);
        var j = c.Next();
        Assert.Equal("chop", j["kind"]!.GetValue<string>());
        Assert.Contains(c.Alerts, a => a.Text.Contains("hungry and there is no food"));
    }

    private sealed class UnloadedWorld : IStoryWorld
    {
        public MineClaw.Data.Versioning.IGameData Data => MineClaw.Data.Versioning.Beta181GameData.Instance;
        public long TimeOfDay => 6000;
        public int Height => 128;
        public (int Id, int Meta) Block(int x, int y, int z) => (0, 0);
        public IReadOnlyList<StoryEntity> Entities() => [];
        public bool Loaded(int x, int z) => false;
    }
}
