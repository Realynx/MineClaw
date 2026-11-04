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
        Assert.NotEqual("dig", Kind(c.Next()));
    }

    // ================================================================ defending: fresh, reachable monsters only

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
