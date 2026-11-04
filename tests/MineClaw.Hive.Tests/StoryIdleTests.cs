using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Idle behaviour (docs/web/story-mode.md "Idle behaviour"). Live 11-03 (Fox): pawns with nothing to do all walked to the home cell
/// and stood in one pile. Now an idle pawn wanders the home area one leg at a time, each on its own reserved cell with room around it,
/// real work takes the leg back at once, and resting or brought-home pawns are spread the same way.
/// </summary>
public sealed class StoryIdleTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock NewClock() => new(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));

    private static P3 At(JsonObject j) => P3.Parse(new JsonArray(j["at"]!.GetValue<string>().Split(',').Select(n => (JsonNode)int.Parse(n)).ToArray()))!.Value;

    private static void Arrive(FakeStoryHost h, string pawn, P3 at) => h.Lives[pawn] = FakeStoryHost.Live(at.X, at.Y, at.Z);

    [Fact]
    public void An_idle_pawn_gets_a_wander_leg_to_a_safe_cell_of_the_home_area()
    {
        var (c, _, w) = StoryRig.Make();
        var j = c.Next();
        Assert.Equal("idle", j["kind"]!.GetValue<string>());
        Assert.Equal("Idle (wandering)", j["label"]!.GetValue<string>());
        Assert.True(j["wander"]!.GetValue<bool>());
        Assert.Equal(0, j["dist"]!.GetValue<int>()); // onto its cell, not "somewhere near it"
        Assert.InRange(j["s"]!.GetValue<int>(), 4, 12);
        var to = At(j);
        Assert.NotEqual(new P3(0, 64, 0), to); // a stroll: somewhere else than where it stands
        Assert.True(StoryWander.Gap(to, new P3(0, 64, 0)) <= StoryColony.WanderRadius);
        Assert.True(StoryWander.Safe(w, to));
        Assert.Equal(to, c.Spots()[c.P()]);
        var pawn = c.State()["pawns"]![0]!;
        Assert.Equal("idle", pawn["state"]!.GetValue<string>());
        Assert.Equal("Idle (wandering)", pawn["job"]!["label"]!.GetValue<string>());

        // Home zones: inside them only.
        c.ZoneOp("add", "home", Area.Of(40, 63, 40, 49, 63, 49), name: "Barn");
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        for (int i = 0; i < 20; i++)
        {
            var leg = c.Next();
            var p = At(leg);
            Assert.True(p.X is >= 40 and <= 49 && p.Z is >= 40 and <= 49 && p.Y == 64, p.ToString());
            c.Report(c.P(), leg["id"]!.GetValue<string>(), "done", null, null);
        }
    }

    [Fact]
    public void Eight_wandering_pawns_never_share_a_cell_and_keep_their_distance()
    {
        var clock = NewClock();
        var (c, h, _) = StoryRig.Make(pawns: 8, time: clock);
        c.Rng = new Random(7);
        var rng = new Random(11);
        var legs = new Dictionary<string, JsonObject>();
        int handed = 0;
        for (int step = 0; step < 400; step++)
        {
            clock.Now += TimeSpan.FromMilliseconds(500);
            var pawn = c.P(rng.Next(8));
            if (legs.TryGetValue(pawn, out var leg))
            {
                // It got there (most of the time) and lingered: the leg ends.
                if (rng.NextDouble() < 0.8) Arrive(h, pawn, At(leg));
                c.Report(pawn, leg["id"]!.GetValue<string>(), rng.NextDouble() < 0.1 ? "failed" : "done", null, null);
                legs.Remove(pawn);
                continue;
            }
            var j = c.Next(pawn)!;
            Assert.Equal("idle", j["kind"]!.GetValue<string>());
            legs[pawn] = j;
            handed++;
            var to = At(j);
            // Never on, or right next to, where another pawn stands or is headed.
            foreach (var other in c.Pawns.Keys.Where(o => o != pawn))
            {
                Assert.True(StoryWander.Gap(to, h.Live(other).Block) >= 2, $"{to} by {other} standing at {h.Live(other).Block}");
                if (legs.TryGetValue(other, out var ol)) Assert.True(StoryWander.Gap(to, At(ol)) >= 2, $"{to} by {other} headed to {At(ol)}");
            }
            var spots = c.Spots().Values.ToList();
            Assert.Equal(spots.Count, spots.Distinct().Count());
        }
        Assert.True(handed > 100);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void New_work_takes_a_wander_leg_back_at_once_and_a_cancelled_or_failed_leg_is_no_failure()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(pawns: 2, time: clock);
        var leg0 = c.Next(0);
        var leg1 = c.Next(1);
        Assert.True(c.Holds(c.P(0), leg0["id"]!.GetValue<string>()));
        // Fox designates a tree mid-walk: the nearest idle pawn has it at once (leased), the other keeps strolling.
        w.Tree(6, 0);
        c.Designate("add", "chop", Area.Of(0, 60, -2, 10, 80, 2), null, null);
        c.Tick();
        var chopper = c.Pawns.Keys.Single(p => c.CurrentOf(p)!.Kind == "chop");
        var stroller = c.Pawns.Keys.Single(p => p != chopper);
        Assert.Equal("idle", c.CurrentOf(stroller)!.Kind);
        var oldLeg = chopper == c.P(0) ? leg0 : leg1;
        Assert.False(c.Holds(chopper, oldLeg["id"]!.GetValue<string>())); // story.during cuts the walk now
        Assert.Contains(c.Leases(), l => l.Value == chopper);
        Assert.Empty(h.Interrupted); // nothing stopped or restarted
        // The cut leg's report is stale: ignored, not a failure.
        Assert.False(c.Report(chopper, oldLeg["id"]!.GetValue<string>(), "failed", "cancelled: the colony has other work for it", null));
        Assert.Equal("chop", c.Next(chopper)["kind"]!.GetValue<string>());

        // Legs that fail (an unreachable cell) never make a pawn rest or raise an alert.
        for (int i = 0; i < 5; i++)
        {
            var j = c.Next(stroller);
            Assert.Equal("idle", j["kind"]!.GetValue<string>());
            Assert.True(c.Report(stroller, j["id"]!.GetValue<string>(), "failed", "no path", null));
            clock.Now += TimeSpan.FromSeconds(1);
        }
        Assert.NotEqual("resting: the last jobs failed", c.Next(stroller)["label"]!.GetValue<string>());
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed"));
    }

    [Fact]
    public void A_free_hour_leg_ends_when_the_hour_turns_to_sleep()
    {
        var clock = NewClock();
        var (c, _, w) = StoryRig.Make(time: clock);
        var p = c.Pawns.Values.First();
        p.Schedule[8] = ScheduleBlocks.Free; p.Schedule[9] = ScheduleBlocks.Sleep;
        w.TimeOfDay = 2500; // 08:30
        var j = c.Next();
        Assert.Equal("Free time (wandering)", j["label"]!.GetValue<string>());
        w.TimeOfDay = 3100; // 09:06: bed time
        clock.Now += TimeSpan.FromSeconds(3);
        c.Tick();
        Assert.False(c.Holds(c.P(), j["id"]!.GetValue<string>()));
        Assert.Equal("sleep", c.Next()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Pawns_resting_without_a_bed_each_get_their_own_spot_at_home_and_keep_it()
    {
        var (c, h, _) = StoryRig.Make(pawns: 5);
        foreach (var p in c.Pawns.Values) p.Schedule = Enumerable.Repeat(ScheduleBlocks.Sleep, 24).ToArray();
        var spots = new List<P3>();
        for (int i = 0; i < 5; i++)
        {
            var j = c.Next(i);
            Assert.Equal("sleep", j["kind"]!.GetValue<string>());
            Assert.Equal(0, j["dist"]!.GetValue<int>());
            spots.Add(At(j));
        }
        for (int a = 0; a < 5; a++)
            for (int b = a + 1; b < 5; b++)
                Assert.True(StoryWander.Gap(spots[a], spots[b]) >= 2, $"{spots[a]} and {spots[b]}");
        Assert.All(spots, s => Assert.True(StoryWander.Gap(s, new P3(0, 64, 0)) <= 5, s.ToString())); // close by home
        // They walk there; the next rest keeps each on its own spot (no shuffling every 30 s).
        for (int i = 0; i < 5; i++)
        {
            Arrive(h, c.P(i), spots[i]);
            c.Report(c.P(i), c.CurrentOf(c.P(i))!.Id, "done", null, null);
        }
        for (int i = 0; i < 5; i++) Assert.Equal(spots[i], At(c.Next(i)));
    }

    [Fact]
    public void Wander_cells_are_never_unsafe_busy_or_by_the_colonys_chests()
    {
        var (c, h, w) = StoryRig.Make(pawns: 3);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = 0; // nothing they'd do: all idle
        c.Rng = new Random(3);
        // Hazards around home: a lava pool, water, cactus, farmland with wheat, a pit, a door, a chest and a designated block.
        for (int x = 3; x <= 5; x++) for (int z = 3; z <= 5; z++) w.Set(x, 63, z, 11);
        for (int x = -5; x <= -3; x++) for (int z = 3; z <= 5; z++) w.Set(x, 63, z, 9);
        w.Set(-2, 64, -6, 81);
        for (int x = 6; x <= 9; x++) for (int z = -6; z <= -3; z++) { w.Set(x, 63, z, 60); w.Set(x, 64, z, 59, 3); }
        for (int x = -9; x <= -6; x++) for (int z = -9; z <= -6; z++) for (int y = 57; y <= 63; y++) w.Set(x, y, z, 0);
        w.Set(0, 64, 8, 64); w.Set(0, 65, 8, 64, 8);
        c.StorageOp("set", [new P3(8, 64, 8)], null, null, null);
        c.Designate("add", "mine", null, [new P3(-8, 63, 0)], null);
        var busy = new HashSet<P3> { new(-8, 64, 0), new(-8, 63, 0) };
        var seen = new HashSet<P3>();
        for (int k = 0; k < 300; k++)
        {
            int i = k % 3;
            var j = c.Next(i);
            var to = At(j);
            seen.Add(to);
            Assert.True(StoryWander.Safe(w, to), to.ToString());
            int floor = w.Block(to.X, to.Y - 1, to.Z).Id;
            Assert.True(floor == 1, $"{to} stands on {floor}"); // stone only here: never water, lava, farmland, crops
            Assert.False(Math.Abs(to.X - 8) <= 1 && Math.Abs(to.Z - 8) <= 1, $"{to} blocks the chest");
            Assert.False(busy.Contains(to), $"{to} is designated");
            Assert.False(Math.Abs(to.X) + Math.Abs(to.Z - 8) <= 1, $"{to} is a doorway");
            Assert.False(to.X is >= 2 and <= 6 && to.Z is >= 3 and <= 5 || to.X is >= 3 and <= 5 && to.Z is >= 2 and <= 6, $"{to} is by the lava");
            Assert.False(to.X is >= -10 and <= -5 && to.Z is >= -9 and <= -6 || to.X is >= -9 and <= -6 && to.Z is >= -10 and <= -5, $"{to} is at the pit's edge");
            Arrive(h, c.P(i), to);
            c.Report(c.P(i), j["id"]!.GetValue<string>(), "done", null, null);
        }
        Assert.True(seen.Count > 40, "they get about: " + seen.Count);
        // Forbidden areas too.
        c.ZoneOp("add", "forbidden", Area.Of(-14, 50, -14, 14, 80, -1));
        for (int k = 0; k < 30; k++)
        {
            var j = c.Next(0);
            Assert.True(At(j).Z >= 0, At(j).ToString());
            c.Report(c.P(0), j["id"]!.GetValue<string>(), "done", null, null);
        }
    }

    [Fact]
    public void At_night_idle_pawns_stay_home_on_their_own_spots_and_never_leave_the_dimension()
    {
        var (c, h, w) = StoryRig.Make(pawns: 3);
        w.TimeOfDay = 15000; // 21:00
        var spots = new List<P3>();
        for (int i = 0; i < 3; i++)
        {
            var j = c.Next(i);
            Assert.Equal("Idle (at home)", j["label"]!.GetValue<string>());
            spots.Add(At(j));
        }
        Assert.All(spots, s => Assert.True(StoryWander.Gap(s, new P3(0, 64, 0)) <= 5, s.ToString()));
        Assert.Equal(3, spots.Distinct().Count());
        // Inside a roofed house zone it may move about at night, within the house only.
        for (int x = 20; x <= 26; x++) for (int z = 20; z <= 26; z++) w.Set(x, 68, z, 5);
        c.ZoneOp("add", "home", Area.Of(20, 63, 20, 26, 63, 26), name: "House");
        c.Report(c.P(0), c.CurrentOf(c.P(0))!.Id, "done", null, null);
        var leg = c.Next(0);
        Assert.Equal("Idle (wandering)", leg["label"]!.GetValue<string>());
        Assert.True(At(leg).X is >= 20 and <= 26 && At(leg).Z is >= 20 and <= 26, At(leg).ToString());
        // A pawn in another dimension doesn't wander off there.
        h.Lives[c.P(1)] = h.Live(c.P(1)) with { Dim = -1 };
        c.Report(c.P(1), c.CurrentOf(c.P(1))!.Id, "done", null, null);
        Assert.Equal(StoryColony.NothingToDo, c.Next(1)["label"]!.GetValue<string>());
        Assert.Null(c.Next(1)["at"]);
    }
}
