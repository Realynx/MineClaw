using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-04 (release 20261103-204549, story-1): maid_chan_114 stuck in a hole two deep ("no path … it's 2 blocks up; unreachable even
/// by pillaring/bridging" ×5) failing grass after grass for everyone while the other pawns stood idle; "attack failed: no skeleton
/// within 16" ×15 (cave mobs, mobs another body saw); pawns in caves and waterfalls. docs/web/story-mode.md "Stuck pawns", "Defending".
/// </summary>
public sealed class StoryStuckPawnTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock NewClock() => new(DateTimeOffset.Parse("2026-11-04T10:00:00Z"));

    private static void Only(StoryColony c, params string[] types)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    private static P3 At(JsonObject j)
    {
        var a = j["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        return new P3(a[0], a[1], a[2]);
    }

    private const string NoPath = "no path to within 2 of (218, 64, 400) (no route); it's 2 blocks up; unreachable even by pillaring/bridging";

    /// <summary>A field of tall grass (the live case) east of home, cut-designated.</summary>
    private static int Field(StoryColony c, FakeStoryWorld w, int n = 20)
    {
        int made = 0;
        for (int x = 2; x < 12 && made < n; x++)
            for (int z = 2; z < 4 && made < n; z++, made++)
                w.Set(x, 64, z, 31, 1);
        c.Designate("add", "cut", Area.Of(2, 64, 2, 11, 64, 3), null, null);
        return made;
    }

    // ================================================================ one stuck pawn never stalls the colony

    [Fact]
    public void A_stuck_pawn_fails_only_for_itself_and_the_others_cut_the_whole_field()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(3, time: clock);
        Only(c, WorkTypes.Chop);
        int grass = Field(c, w);
        Assert.Equal(grass, c.Designations.Count);
        string stuck = c.P(0);
        h.Lives[stuck] = FakeStoryHost.Live(40, 64, 40); // stuck somewhere else (its spot keeps the others away from work by it)
        int idleWhileWork = 0, stuckFails = 0, rounds = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (c.Designations.Count > 0 && rounds++ < 200)
        {
            for (int i = 0; i < 3; i++)
            {
                var j = c.Next(i);
                if (c.P(i) == stuck)
                {
                    c.Report(stuck, Id(j), "failed", Kind(j) == "escape" ? "stuck in a pit at 0,62,0: help is on its way" : NoPath, null);
                    stuckFails++;
                    continue;
                }
                if (Kind(j) != "dig") { if (c.Designations.Count > 0 && Kind(j) is "idle" or "hold") idleWhileWork++; c.Report(c.P(i), Id(j), "done", null, null); continue; }
                var at = At(j);
                w.Set(at.X, at.Y, at.Z, 0);
                c.Report(c.P(i), Id(j), "done", null, null);
            }
            clock.Now += TimeSpan.FromSeconds(2);
        }
        Assert.Empty(c.Designations); // every blade cut by the two pawns that could
        Assert.Equal(0, idleWhileWork); // nobody stood idle in front of the field
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn" && a.Text.Contains("failed"));
        Assert.True(c.IsStuck(stuck), "the colony sees it is stuck");
        Assert.True(stuckFails > 0);
        Assert.True(watch.ElapsedMilliseconds < 5000, "picks stay cheap: " + watch.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void Unreachable_for_one_pawn_rests_for_that_pawn_and_counts_only_after_three_pawns_could_not_reach_it()
    {
        var clock = NewClock();
        var (c, _, w) = StoryRig.Make(3, time: clock);
        Only(c, WorkTypes.Chop);
        w.Set(5, 64, 5, 31, 1);
        c.Designate("add", "cut", null, [new P3(5, 64, 5)], null);
        var d = Assert.Single(c.Designations);
        for (int i = 0; i < 3; i++)
        {
            var j = c.Next(i);
            Assert.Equal("dig", Kind(j)); // the others still get it after one (or two) couldn't reach it
            c.Report(c.P(i), Id(j), "failed", NoPath, null);
            if (i < 2)
            {
                Assert.Equal(0, d.Fails);
                Assert.Null(d.Reason);
                Assert.NotEqual("dig", Kind(c.Next(i))); // not again for the pawn that couldn't
                c.Report(c.P(i), Id(c.Next(i)), "done", null, null);
            }
        }
        Assert.Equal(1, d.Fails); // three different pawns: now it is the target's failure
        Assert.Contains("no path", d.Reason);
    }

    [Fact]
    public void Can_t_reach_rests_the_target_for_that_pawn_only()
    {
        var clock = NewClock();
        var (c, _, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Chop);
        w.Set(5, 64, 5, 31, 1);
        c.Designate("add", "cut", null, [new P3(5, 64, 5)], null);
        var j = c.Next(0);
        c.Report(c.P(0), Id(j), "failed", "can't reach grass at 5,64,5", null);
        Assert.Equal("dig", Kind(c.Next(1))); // maid_chan_67's "can't reach grass" ×3 no longer rests it for the colony
        Assert.Equal(0, c.Designations[0].Fails);
    }

    // ================================================================ help: blocks from another pawn, then the teleport

    [Fact]
    public void A_stuck_pawn_gets_blocks_from_another_pawn_and_after_that_a_teleport_home()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Chop);
        h.Lives[c.P(0)] = FakeStoryHost.Live(10, 62, 10);
        var chest = new P3(2, 64, 0);
        c.StorageOp("set", [chest], null, null, null);
        c.AddContents(c.StorageAt(chest)!, new Stack(4, 0, 32));
        Assert.StartsWith("help is on its way", c.Stuck(c.P(0), "stuck in a pit at 10,62,10"));
        Assert.True(c.IsStuck(c.P(0)));
        Assert.Equal("escape", Kind(c.Next(0)));
        var give = c.Next(1);
        Assert.Equal("give", Kind(give));
        Assert.Equal(c.P(0), give["to"]!.GetValue<string>());
        Assert.Equal(chest.ToString(), give["fetch"]![0]!["from"]!.GetValue<string>());
        c.Report(c.P(1), Id(give), "done", null, null);
        Assert.Empty(h.Teleported);
        // The blocks came and it still can't get out: the last resort.
        Assert.Equal("teleported home", c.Stuck(c.P(0), "stuck in a pit at 10,62,10"));
        Assert.Equal(c.P(0), Assert.Single(h.Teleported).Account);
        Assert.True(c.IsStuck(c.P(0))); // out once it shows up at home
        c.Report(c.P(0), c.CurrentOf(c.P(0))!.Id, "failed", "teleported", null);
        h.Lives[c.P(0)] = FakeStoryHost.Live(1, 64, 1);
        Assert.NotEqual("escape", Kind(c.Next(0)));
        Assert.False(c.IsStuck(c.P(0)));
        Assert.Contains(c.Alerts, a => a.Text.Contains("teleported home"));
    }

    [Fact]
    public void With_nothing_to_bring_the_teleport_comes_at_once_and_only_when_the_option_and_op_allow_it()
    {
        var (c, h, _) = StoryRig.Make(2, time: NewClock());
        h.Lives[c.P(0)] = FakeStoryHost.Live(10, 62, 10);
        c.SetHaulOptions(false, null, null);
        Assert.StartsWith("nobody can help", c.Stuck(c.P(0), "stuck in a pit at 10,62,10 (obsidian)"));
        Assert.Empty(h.Teleported);
        Assert.Contains(c.Alerts, a => a.Level == "warn" && a.Text.Contains("rescue teleport is off"));
        c.SetHaulOptions(true, null, null);
        h.TeleportError = "the gate refused /tp";
        Assert.Contains("no teleport", c.Stuck(c.P(0), "stuck"));
        Assert.Empty(h.Teleported);
        h.TeleportError = null;
        Assert.Equal("teleported home", c.Stuck(c.P(0), "stuck"));
        Assert.Single(h.Teleported);
        Assert.True(c.State()["options"]!["rescue_teleport"]!.GetValue<bool>());
    }

    [Fact]
    public void A_pawn_under_ground_with_no_mining_near_heads_back_up_and_one_mining_stays()
    {
        var (c, h, w) = StoryRig.Make(1, time: NewClock());
        Only(c, WorkTypes.Chop);
        w.Set(30, 50, 30, 0); w.Set(30, 51, 30, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(30, 50, 30);
        var j = c.Next();
        Assert.Equal("escape", Kind(j));
        Assert.Contains("under ground", j["label"]!.GetValue<string>());
        // Back up top (it moved away): free again.
        h.Lives[c.P()] = FakeStoryHost.Live(3, 64, 3);
        c.Report(c.P(), Id(j), "done", null, null);
        Assert.False(c.IsStuck(c.P()));
        // Mining down there: being under ground is the job.
        Only(c, WorkTypes.Mine);
        c.Designate("add", "mine", null, [new P3(31, 50, 30)], "all");
        h.Lives[c.P()] = FakeStoryHost.Live(30, 50, 30);
        Assert.NotEqual("escape", Kind(c.Next()));
    }

    [Fact]
    public void Idle_pawns_never_wander_into_a_cave_and_cave_plants_are_not_cut()
    {
        var (c, h, w) = StoryRig.Make(1, time: NewClock());
        Only(c); // nothing to do: it wanders
        // A hill east of home with a cave in it: floor 64, rock 66..69, the hilltop at 70.
        for (int x = 3; x <= 12; x++)
            for (int z = -12; z <= 12; z++)
            {
                w.Set(x, 64, z, 0); w.Set(x, 65, z, 0);
                for (int y = 66; y <= 69; y++) w.Set(x, y, z, 1);
            }
        for (int i = 0; i < 40; i++)
        {
            var j = c.Next();
            if (j["at"] != null)
            {
                var at = At(j);
                Assert.False(at.X >= 3 && at.X <= 12 && at.Y < 66, $"wandered into the cave at {at}");
            }
            c.Report(c.P(), Id(j), "done", null, null);
        }
        Only(c, WorkTypes.Chop);
        w.Set(5, 64, 5, 31, 1);
        c.Designate("add", "cut", null, [new P3(5, 64, 5)], null);
        // A cave plant the colony marked itself is left alone; one the owner marked is work (live 11-04: five pawns idle on
        // "cut 91,55,463: in a cave").
        c.Designations.Single().Auto = true;
        Assert.NotEqual("dig", Kind(c.Next()));
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        c.Designations.Single().Auto = false;
        Assert.Equal("dig", Kind(c.Next()));
    }

    // ================================================================ ravines: stuck is sticky until it is out

    /// <summary>Live 11-04: pawns in a ravine (floor y 49, home y 63) "looks stuck … 3 targets out of reach in a row" ×6, then the
    /// next task and the next failure (~100 failures each in 20 min): walking a few blocks along the ravine floor read as "got out".</summary>
    [Fact]
    public void A_stuck_pawn_gets_only_escape_work_until_it_walks_home_however_far_it_wanders_meanwhile()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Chop);
        c.SetHaulOptions(false, null, null); // no teleport here: it keeps escaping (the budget has its own test)
        Field(c, w, 6);
        h.Lives[c.P()] = FakeStoryHost.Live(30, 64, 30);
        for (int i = 0; i < 3; i++)
        {
            var j = c.Next();
            Assert.Equal("dig", Kind(j));
            c.Report(c.P(), Id(j), "failed", NoPath, null);
        }
        Assert.True(c.IsStuck(c.P()));
        for (int i = 0; i < 12; i++)
        {
            h.Lives[c.P()] = FakeStoryHost.Live(30 + i * 3, 64, 30); // along the ravine floor, far from where it got stuck
            var j = c.Next();
            Assert.Equal("escape", Kind(j)); // never a regular task while stuck: no cycling
            c.Report(c.P(), Id(j), "progress", "no way home (no path): trying a staircase toward home", null);
            c.Report(c.P(), Id(j), "failed", "no way home (couldn't climb out below home at 45,49,30): teleported home", null);
            clock.Now += TimeSpan.FromSeconds(10);
        }
        var stuck = c.State()["pawns"]![0]!["stuck"]!;
        Assert.Equal(12, stuck["attempts"]!.GetValue<int>());
        Assert.Contains("couldn't climb out", stuck["step"]!.GetValue<string>());
        // Its escape walks it home: out, and back to work.
        var esc = c.Next();
        Assert.Equal("escape", Kind(esc));
        h.Lives[c.P()] = FakeStoryHost.Live(1, 64, 1);
        c.Report(c.P(), Id(esc), "done", null, null);
        Assert.False(c.IsStuck(c.P()));
        Assert.Equal("dig", Kind(c.Next()));
    }

    [Fact]
    public void A_pawn_with_no_way_home_is_teleported_at_once_and_a_teleport_that_never_shows_is_said_once()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Chop);
        var chest = new P3(2, 64, 0);
        c.StorageOp("set", [chest], null, null, null);
        c.AddContents(c.StorageAt(chest)!, new Stack(4, 0, 32)); // blocks to bring: no use in a ravine
        h.Lives[c.P(0)] = FakeStoryHost.Live(40, 49, 40);
        Assert.Equal("teleported home", c.Stuck(c.P(0), "no way home: couldn't climb out below home at 40,49,40 (obsidian)"));
        Assert.Single(h.Teleported);
        Assert.NotEqual("give", Kind(c.Next(1))); // no rescuer walks into a ravine after it
        // The server never moved it (a refused /tp, or one dropped in a backed-up chat queue — live 11-04): tried again
        // TeleportRetry apart, MaxTeleportTries in all, and said once when the last one didn't take either.
        for (int miss = 1; miss <= StoryColony.MaxTeleportTries; miss++)
        {
            var wait = c.Next(0);
            Assert.Equal("hold", Kind(wait));
            clock.Now += StoryColony.TeleportCheck + TimeSpan.FromSeconds(1);
            c.Report(c.P(0), Id(wait), "done", null, null);
            Assert.True(c.IsStuck(c.P(0)));
            var esc = c.Next(0);
            Assert.Equal("escape", Kind(esc));
            Assert.Equal(miss, h.Teleported.Count);
            if (miss == StoryColony.MaxTeleportTries) break;
            Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("teleport home didn't take"));
            clock.Now += StoryColony.TeleportRetry;
            c.Report(c.P(0), Id(esc), "failed", "no way home: still", null); // the escape fails again: past the retry, sent again
        }
        Assert.Single(c.Alerts, a => a.Text.Contains("teleport home didn't take"));
        Assert.StartsWith("nobody can help", c.Stuck(c.P(0), "no way home: still"));
        Assert.Equal(StoryColony.MaxTeleportTries, h.Teleported.Count);
    }

    // ================================================================ the escape budget (Fox 11-04: "they should teleport sooner")

    /// <summary>Stuck after three targets out of reach; the escape job it gets.</summary>
    private static JsonObject StuckPawn(StoryColony c, FakeStoryHost h)
    {
        h.Lives[c.P()] = FakeStoryHost.Live(30, 64, 30);
        for (int i = 0; i < 3; i++) c.Report(c.P(), Id(c.Next()), "failed", NoPath, null);
        Assert.True(c.IsStuck(c.P()));
        var esc = c.Next();
        Assert.Equal("escape", Kind(esc));
        return esc;
    }

    [Fact]
    public void Two_failed_escape_steps_and_the_pawn_is_teleported_home()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Chop);
        Field(c, w, 6);
        var esc = StuckPawn(c, h);
        c.Report(c.P(), Id(esc), "failed", "no way home (couldn't climb out below home at 30,49,30)", null);
        esc = c.Next();
        Assert.Equal("escape", Kind(esc));
        Assert.Empty(h.Teleported);
        c.Report(c.P(), Id(esc), "failed", "no way home (couldn't climb out below home at 30,49,30)", null);
        var wait = c.Next();
        Assert.Equal("hold", Kind(wait)); // teleported: arriving
        Assert.Equal(c.P(), Assert.Single(h.Teleported).Account);
        // It shows up at home: out, and to work.
        h.Lives[c.P()] = FakeStoryHost.Live(1, 64, 1);
        c.Report(c.P(), Id(wait), "done", null, null);
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal("dig", Kind(c.Next()));
        Assert.False(c.IsStuck(c.P()));
    }

    [Fact]
    public void Twenty_seconds_of_escaping_and_the_pawn_is_teleported_home_and_the_budget_is_an_option()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Chop);
        Field(c, w, 6);
        var esc = StuckPawn(c, h);
        clock.Now += TimeSpan.FromSeconds(12);
        c.Report(c.P(), Id(esc), "progress", "trying a staircase toward home", null);
        Assert.Empty(h.Teleported);
        clock.Now += TimeSpan.FromSeconds(9); // 21 s since the colony saw it stuck
        c.Report(c.P(), Id(esc), "failed", "no way home (still climbing)", null);
        Assert.Equal("hold", Kind(c.Next()));
        Assert.Single(h.Teleported);
        Assert.Equal(20, c.State()["options"]!["stuck_teleport_after_s"]!.GetValue<int>());
        c.SetHaulOptions(null, null, null, stuckTeleportAfterS: 2);
        Assert.Equal(5, c.State()["options"]!["stuck_teleport_after_s"]!.GetValue<int>()); // at least 5 s
    }

    [Fact]
    public void A_far_or_busy_colleague_never_goes_to_toss_blocks_the_teleport_comes_instead()
    {
        var (c, h, w) = StoryRig.Make(2, time: NewClock());
        Only(c, WorkTypes.Chop);
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        c.AddContents(c.StorageAt(new P3(2, 64, 0))!, new Stack(4, 0, 32));
        h.Lives[c.P(0)] = FakeStoryHost.Live(40, 62, 40); // 56 blocks from its idle colleague
        Assert.Equal("teleported home", c.Stuck(c.P(0), "stuck in a pit at 40,62,40"));
        Assert.NotEqual("give", Kind(c.Next(1)));
    }

    /// <summary>Live 11-04: maid_chan_126, _118 and _105 all stuck in one hole at 243,53,427: the work by it sent one after another.</summary>
    [Fact]
    public void Work_by_a_spot_where_a_pawn_got_stuck_goes_to_no_other_pawn_for_a_while()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Chop);
        w.Set(20, 64, 20, 31, 1); // by the hole
        w.Set(30, 64, 30, 31, 1); // elsewhere
        c.Designate("add", "cut", null, [new P3(20, 64, 20), new P3(30, 64, 30)], null);
        h.Lives[c.P(0)] = FakeStoryHost.Live(21, 62, 20);
        h.Lives[c.P(1)] = FakeStoryHost.Live(18, 64, 18);
        c.Stuck(c.P(0), "stuck in a pit at 21,62,20");
        var j = c.Next(1);
        Assert.Equal("30,64,30", j["at"]!.GetValue<string>()); // the nearer one is by the stuck spot: not for now
        c.Report(c.P(1), Id(j), "failed", "whatever", null);
        clock.Now += StoryColony.HazardFor + TimeSpan.FromSeconds(1);
        Assert.Equal("20,64,20", c.Next(1)["at"]!.GetValue<string>());
    }

    // ================================================================ never idle with work about (Fox 11-04)

    [Fact]
    public void A_pawn_whose_top_work_is_all_leased_or_waiting_for_a_tool_takes_its_next_work_type()
    {
        var (c, h, w) = StoryRig.Make(2, time: NewClock());
        foreach (var p in c.Pawns.Values) { foreach (var t in WorkTypes.All) p.Priorities[t] = 0; p.Priorities[WorkTypes.Mine] = 1; p.Priorities[WorkTypes.Chop] = 2; }
        w.Set(20, 64, 20, 49); // obsidian: nobody has a diamond pickaxe, it waits for one
        w.Set(21, 64, 20, 3);  // dirt: one target, the first pawn takes it
        c.Designate("add", "mine", null, [new P3(20, 64, 20), new P3(21, 64, 20)], "all");
        Field(c, w, 4);
        var a = c.Next(0);
        Assert.Equal("21,64,20", a["at"]!.GetValue<string>());
        var b = c.Next(1);
        Assert.Equal("dig", Kind(b)); // its mine targets are leased or waiting: it cuts grass instead of idling
        Assert.Equal(3, b["at"]!.GetValue<string>().Split(',').Length);
        Assert.Equal(64, int.Parse(b["at"]!.GetValue<string>().Split(',')[1]));
        Assert.NotEqual("21,64,20", b["at"]!.GetValue<string>());
        Assert.NotEqual("20,64,20", b["at"]!.GetValue<string>());
    }

    [Fact]
    public void Ten_pawns_with_a_mixed_queue_none_idles_while_work_is_left_for_it()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(10, time: clock);
        foreach (var p in c.Pawns.Values) { foreach (var t in WorkTypes.All) p.Priorities[t] = 0; p.Priorities[WorkTypes.Mine] = 1; p.Priorities[WorkTypes.Chop] = 2; }
        for (int x = 20; x < 23; x++) w.Set(x, 64, 20, 3);
        c.Designate("add", "mine", null, [new P3(20, 64, 20), new P3(21, 64, 20), new P3(22, 64, 20)], "all");
        Field(c, w, 20);
        int idle = 0;
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < 10; i++)
            {
                var j = c.Next(i);
                if (Kind(j) != "dig") { if (c.Designations.Count > 10) idle++; c.Report(c.P(i), Id(j), "done", null, null); continue; }
                // Pawn 0 fails everything it touches (a bad spot): it backs off those targets, never the work.
                if (i == 0) { c.Report(c.P(i), Id(j), "failed", "too hard to dig", null); continue; }
                var at = At(j);
                w.Set(at.X, at.Y, at.Z, 0);
                c.Report(c.P(i), Id(j), "done", null, null);
            }
            clock.Now += TimeSpan.FromSeconds(2);
            c.Tick();
        }
        Assert.Equal(0, idle);
    }

    [Fact]
    public void The_idle_audit_says_why_a_pawn_is_idle_with_work_about()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        foreach (var p in c.Pawns.Values) { foreach (var t in WorkTypes.All) p.Priorities[t] = 0; p.Priorities[WorkTypes.Mine] = 1; }
        w.Set(21, 64, 20, 3);
        c.Designate("add", "mine", null, [new P3(21, 64, 20)], "all");
        Assert.Equal("dig", Kind(c.Next(0)));
        Assert.NotEqual("dig", Kind(c.Next(1))); // its only target is the other pawn's
        c.Tick();
        Assert.Equal("all targets leased", c.IdleReasonOf(c.P(1)));
        Assert.Null(c.IdleReasonOf(c.P(0)));
        Assert.Equal("all targets leased", c.State()["pawns"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == c.P(1))!["idle_reason"]!.GetValue<string>());
        Assert.Contains("idle-with-work", StoryPerf.TakeIdle());
        // An ore it has no pickaxe for: "tool waiting".
        w.Set(21, 64, 20, 0); // dug
        c.Report(c.P(0), c.CurrentOf(c.P(0))!.Id, "done", null, null);
        w.Set(22, 64, 20, 56); // diamond ore: no iron pickaxe anywhere
        c.Designate("add", "mine", null, [new P3(22, 64, 20)], "all");
        c.Report(c.P(1), c.CurrentOf(c.P(1))!.Id, "done", null, null);
        Assert.NotEqual("dig", Kind(c.Next(1)));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.True(c.IdleReasonOf(c.P(1)) == "tool waiting", $"{c.IdleReasonOf(c.P(1))} | {c.CurrentOf(c.P(1))?.Source} {c.CurrentOf(c.P(1))?.Label} | " + string.Join(", ", c.Designations.Select(d => $"{d.Kind}:{d.Status}:{d.Wait}")));
        // Work only of kinds it doesn't do: "priority 0".
        w.Set(5, 64, 5, 31, 1);
        c.Designate("add", "cut", null, [new P3(5, 64, 5)], null);
        c.Designations.RemoveAll(d => d.Kind == "mine");
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Equal("priority 0", c.IdleReasonOf(c.P(1)));
    }

    // ================================================================ defending: fresh, reachable monsters only

    [Fact]
    public void A_guard_requires_sight_and_stops_when_the_target_disappears_behind_a_wall()
    {
        var (c, h, w) = StoryRig.Make();
        Only(c, WorkTypes.Guard);
        h.Lives[c.P()] = FakeStoryHost.Live(0.5, 64, 0.5);
        w.Ents.Add(new StoryEntity(77, "mob", "zombie", 4.5, 64, 0.5, Hostile: true));
        w.Set(2, 64, 0, 1); w.Set(2, 65, 0, 1);
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P())));
        w.Set(2, 64, 0, 0); w.Set(2, 65, 0, 0);
        var job = c.Next();
        Assert.Equal("attack", Kind(job));
        w.Set(2, 64, 0, 1); w.Set(2, 65, 0, 1);
        c.Tick();
        Assert.Contains(h.Interrupted, s => s.Contains("guard target out of sight"));
        Assert.False(c.Report(c.P(), Id(job), "done", null, null));
        Assert.DoesNotContain("e:77", c.Leases().Keys);
    }

    [Fact]
    public void Guards_ignore_cave_mobs_and_stale_ones_and_a_mob_not_found_rests_for_that_pawn_only()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Guard);
        h.Lives[c.P(1)] = FakeStoryHost.Live(-10, 64, -10);
        w.Ents.Add(new StoryEntity(1, "mob", "zombie", 3.5, 55, 3.5, Hostile: true, SeenAt: clock.Now));               // in a cave under home
        w.Ents.Add(new StoryEntity(2, "mob", "skeleton", 4.5, 64, 4.5, Hostile: true, SeenAt: clock.Now - TimeSpan.FromMinutes(2))); // stale
        w.Ents.Add(new StoryEntity(3, "mob", "zombie", 3.5, 85, 3.5, Hostile: true, SeenAt: clock.Now));               // far above home
        Assert.NotEqual("attack", Kind(c.Next(0)));
        c.Report(c.P(0), Id(c.Next(0)), "done", null, null);
        w.Ents.Add(new StoryEntity(4, "mob", "creeper", 6.5, 64, 6.5, Hostile: true, SeenAt: clock.Now));
        var j = c.Next(0);
        Assert.Equal("attack", Kind(j));
        Assert.Equal("creeper", j["mob"]!.GetValue<string>());
        c.Report(c.P(0), Id(j), "failed", "no creeper within 16", null);
        Assert.NotEqual("attack", Kind(c.Next(0))); // it rests for this pawn…
        Assert.Equal("attack", Kind(c.Next(1)));    // …and another pawn may still go
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
        Assert.DoesNotContain("resting", c.Next(0)["label"]!.GetValue<string>());
    }
}
