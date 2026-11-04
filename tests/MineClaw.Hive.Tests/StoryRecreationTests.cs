using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fox 11-03: "If a pawn is idle or resting, it should do recreation or something else instead of sitting in the same place. Should
/// wander." Resting pawns take idle legs for as long as their back-off lasts; idle and free pawns pick pastimes with variety
/// (docs/web/story-mode.md "Idle behaviour").
/// </summary>
public sealed class StoryRecreationTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock NewClock() => new(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));

    private static P3 At(JsonObject j) => P3.Parse(new JsonArray(j["at"]!.GetValue<string>().Split(',').Select(n => (JsonNode)int.Parse(n)).ToArray()))!.Value;

    private static void Arrive(FakeStoryHost h, string pawn, P3 at) => h.Lives[pawn] = FakeStoryHost.Live(at.X, at.Y, at.Z);

    private static void NoWork(StoryColony c)
    {
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = 0;
    }

    [Fact]
    public void A_resting_pawn_wanders_for_its_whole_back_off_and_works_again_the_moment_it_ends()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(time: clock);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        c.Designate("add", "mine", null, [new P3(1, 63, 0), new P3(2, 63, 0), new P3(3, 63, 0), new P3(4, 63, 0)], null);
        for (int k = 0; k < 3; k++)
        {
            var j = c.Next();
            Assert.Equal("dig", j["kind"]!.GetValue<string>());
            // (A run of "no route" failures is a stuck pawn: an escape job, StoryStuckPawnTests. Any other failure: a rest.)
            c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "too hard to dig", null);
            clock.Now += TimeSpan.FromSeconds(1);
        }
        // Resting: on its feet about home, not standing still; the open mine job doesn't cut the rest short.
        var started = clock.Now;
        int legs = 0;
        while (clock.Now - started < StoryColony.FailRest - TimeSpan.FromSeconds(5)) // the rest runs from the last failure (a second before "started")
        {
            var leg = c.Next();
            Assert.Equal("idle", leg["kind"]!.GetValue<string>());
            Assert.StartsWith("Resting", leg["label"]!.GetValue<string>());
            Assert.NotNull(leg["at"]);
            legs++;
            clock.Now += TimeSpan.FromSeconds(3);
            c.Tick();
            if (c.Holds(c.P(), leg["id"]!.GetValue<string>())) c.Report(c.P(), leg["id"]!.GetValue<string>(), "done", null, null);
            else Assert.Fail("a resting leg was taken back before the rest was over");
            Arrive(h, c.P(), At(leg));
        }
        Assert.True(legs >= 3, $"{legs} legs");
        // The rest is over: the leg in progress is taken back in the same tick and the pawn works.
        var last = c.Next();
        Assert.Equal("idle", last["kind"]!.GetValue<string>());
        clock.Now = started + StoryColony.FailRest + TimeSpan.FromSeconds(3);
        c.Tick();
        Assert.False(c.Holds(c.P(), last["id"]!.GetValue<string>()));
        Assert.Equal("dig", c.Next()["kind"]!.GetValue<string>());
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("resting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pastimes_vary_and_never_repeat_back_to_back_when_another_is_possible()
    {
        var (c, h, w) = StoryRig.Make();
        NoWork(c);
        c.Rng = new Random(5);
        // A chair, a crafting table and a garden by home.
        w.Set(4, 64, 4, 53, 0);
        w.Set(-6, 64, 0, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(-6, 64, 0)).Error);
        for (int x = 5; x <= 7; x++) for (int z = -7; z <= -5; z++) w.Set(x, 63, z, 60);
        c.ZoneOp("add", "farm", Area.Of(5, 63, -7, 7, 63, -5));
        var acts = new List<string>();
        for (int k = 0; k < 30; k++)
        {
            var j = c.Next();
            var act = j["act"]!.GetValue<string>();
            acts.Add(act);
            if (act != "wander") Assert.StartsWith("Recreation: ", j["label"]!.GetValue<string>());
            Assert.NotNull(j["look"]); // where it looks while it lingers
            Arrive(h, c.P(), At(j));
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        }
        for (int k = 1; k < acts.Count; k++) Assert.NotEqual(acts[k - 1], acts[k]);
        Assert.Contains("sit", acts);
        Assert.Contains("garden", acts);
        Assert.Contains("wander", acts);
        // Wander only: strolls and nothing else; stay: it holds where it is.
        Assert.Null(c.SetIdle(StoryColony.IdleWander));
        for (int k = 0; k < 6; k++)
        {
            var j = c.Next();
            Assert.Equal("wander", j["act"]!.GetValue<string>());
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        }
        Assert.Null(c.SetIdle(StoryColony.IdleStay));
        var hold = c.Next();
        Assert.Equal(StoryColony.NothingToDo, hold["label"]!.GetValue<string>());
        Assert.Null(hold["at"]);
        Assert.NotNull(c.SetIdle("dance"));
        Assert.Equal("stay", c.State()["options"]!["idle"]!.GetValue<string>());
    }

    [Fact]
    public void Socialising_pawns_stand_two_to_three_blocks_apart_facing_each_other_and_keep_their_distance_from_everyone()
    {
        var (c, h, _) = StoryRig.Make(pawns: 5);
        NoWork(c);
        c.Rng = new Random(9);
        var legs = new Dictionary<string, JsonObject>();
        int social = 0;
        for (int k = 0; k < 120; k++)
        {
            var pawn = c.P(k % 5);
            if (legs.Remove(pawn, out var old))
            {
                Arrive(h, pawn, At(old));
                c.Report(pawn, old["id"]!.GetValue<string>(), "done", null, null);
            }
            var j = c.Next(pawn)!;
            legs[pawn] = j;
            var to = At(j);
            foreach (var other in c.Pawns.Keys.Where(o => o != pawn))
            {
                Assert.True(StoryWander.Gap(to, h.Live(other).Block) >= 2, $"{to} by {other}");
                if (legs.TryGetValue(other, out var ol)) Assert.True(StoryWander.Gap(to, At(ol)) >= 2, $"{to} by {other}'s {At(ol)}");
            }
            if (j["act"]!.GetValue<string>() != "socialise") continue;
            social++;
            var with = j["with"]!.GetValue<string>();
            Assert.Contains($"socialising with {c.Pawns[with].Display}", j["label"]!.GetValue<string>());
            var partner = legs.TryGetValue(with, out var pl) ? At(pl) : h.Live(with).Block;
            Assert.InRange(StoryWander.Gap(to, partner), 2, 3.3);
            var look = j["look"]![0]!.GetValue<string>().Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(partner.X + 0.5, look[0], 3); // it faces its partner
            Assert.Equal(partner.Z + 0.5, look[2], 3);
        }
        Assert.True(social >= 5, $"{social} socialising legs");
    }

    [Fact]
    public void Stargazing_is_only_at_night_and_only_under_open_sky()
    {
        var (c, h, w) = StoryRig.Make();
        NoWork(c);
        c.Rng = new Random(2);
        // A roof over most of home: open sky only on the east side.
        for (int x = -14; x <= 3; x++) for (int z = -14; z <= 14; z++) w.Set(x, 70, z, 5);
        var day = new List<string>();
        for (int k = 0; k < 20; k++)
        {
            var j = c.Next();
            day.Add(j["act"]!.GetValue<string>());
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        }
        Assert.DoesNotContain("stargaze", day);
        w.TimeOfDay = 16000; // 22:00
        int stars = 0;
        for (int k = 0; k < 30; k++)
        {
            var j = c.Next();
            if (j["act"]!.GetValue<string>() == "stargaze")
            {
                stars++;
                var at = At(j);
                Assert.True(at.X > 3, $"{at} is under the roof");
                Assert.True(j["look"]!.AsArray().All(l => double.Parse(l!.GetValue<string>().Split(',')[1], System.Globalization.CultureInfo.InvariantCulture) > at.Y + 10), "it looks up");
            }
            Arrive(h, c.P(), At(j));
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        }
        Assert.True(stars >= 3, $"{stars} stargazing legs");
    }

    [Fact]
    public void Every_pastime_is_cut_for_new_work_within_one_director_step()
    {
        var clock = NewClock();
        var (c, h, w) = StoryRig.Make(pawns: 4, time: clock);
        c.Rng = new Random(4);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Chop ? 1 : 0;
        w.Set(4, 64, 4, 53, 0);
        var legs = c.Pawns.Keys.ToDictionary(p => p, p => c.Next(p)!);
        clock.Now += TimeSpan.FromSeconds(1);
        // A grove: one tree for each pawn.
        foreach (var x in new[] { 8, 11, 14, 17 }) w.Tree(x, 0);
        c.Designate("add", "chop", Area.Of(6, 60, -2, 19, 80, 2), null, null);
        c.Tick();
        foreach (var (p, leg) in legs)
        {
            Assert.False(c.Holds(p, leg["id"]!.GetValue<string>()), $"{p}'s {leg["act"]} goes on");
            Assert.Equal("chop", c.CurrentOf(p)!.Kind);
        }
        Assert.Empty(h.Interrupted);
        Assert.Equal(4, c.Leases().Values.Distinct().Count());
    }
}
