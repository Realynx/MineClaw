using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-03 23:41+ (colony story-1, 1.12.2): "reported done, but the tallgrass is still there" ×54, a tall plant "busy: another hive
/// bot is working on that part" ×11, "can't reach dirt" ×46 while a 3-deep box was dug, "won't dig: someone is standing on it" ×6.
/// </summary>
public sealed class StoryMiningFixTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock NewClock() => new(DateTimeOffset.Parse("2026-11-03T23:45:00Z"));

    private static void Only(StoryColony c, params string[] types)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static P3 At(JsonObject j)
    {
        var a = j["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        return new P3(a[0], a[1], a[2]);
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    // ================================================================ done is checked where the dig happened, with a grace

    [Fact]
    public void A_block_change_that_arrives_a_moment_after_done_is_no_failure()
    {
        var (c, _, w) = StoryRig.Make();
        c.DoneGrace = TimeSpan.FromSeconds(2);
        Only(c, WorkTypes.Chop);
        w.Set(3, 64, 3, 31, 1); // tall grass
        c.Designate("add", "cut", null, [new P3(3, 64, 3)], null);
        var j = c.Next();
        // Main's copy of the block change lands 150 ms after the pawn said done.
        var late = Task.Run(async () => { await Task.Delay(150); w.Set(3, 64, 3, 0); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.Report(c.P(), Id(j), "done", null, null);
        late.Wait();
        Assert.Empty(c.Designations);
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.9), $"waited {sw.Elapsed}: should end as soon as the world agrees");
    }

    [Fact]
    public void A_block_that_really_stays_fails_after_the_grace_with_why()
    {
        var (c, _, w) = StoryRig.Make();
        c.DoneGrace = TimeSpan.FromMilliseconds(200);
        Only(c, WorkTypes.Chop);
        w.Set(3, 64, 3, 31, 1);
        c.Designate("add", "cut", null, [new P3(3, 64, 3)], null);
        var j = c.Next();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.Report(c.P(), Id(j), "done", null, null);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(180), "it waits the grace out");
        var d = Assert.Single(c.Designations);
        Assert.Equal(1, d.Fails);
        Assert.Contains("still there", d.Reason);
    }

    [Fact]
    public void Done_is_judged_by_the_pawns_own_view_when_it_holds_the_cell()
    {
        var (c, h, w) = StoryRig.Make();
        c.DoneGrace = TimeSpan.Zero;
        Only(c, WorkTypes.Mine);
        w.Set(3, 64, 3, 3); // dirt
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], "all");
        // The pawn's own connection saw the block go; main's view hasn't caught up yet.
        var own = new FakeStoryWorld(w.Data);
        h.Views[c.P()] = own;
        var j = c.Next();
        c.Report(c.P(), Id(j), "done", null, null);
        Assert.Empty(c.Designations);

        // The other way round: its own view still holds the block (the dig didn't take): a failure, said so.
        w.Set(4, 64, 4, 3);
        own.Set(4, 64, 4, 3);
        c.Designate("add", "mine", null, [new P3(4, 64, 4)], "all");
        w.Set(4, 64, 4, 0);
        var j2 = c.Next();
        c.Report(c.P(), Id(j2), "done", null, null);
        var d = Assert.Single(c.Designations);
        Assert.Contains("still there (in its own view)", d.Reason);
    }

    // ================================================================ a tall plant is one target, whatever box covers it

    [Fact]
    public void A_double_plant_under_a_cut_and_a_mine_box_is_one_target_for_one_pawn()
    {
        var (c, h, w) = StoryRig.Make(2, Release1122GameData.Instance);
        Only(c, WorkTypes.Chop, WorkTypes.Mine);
        w.Set(5, 64, 5, StoryBlocks.DoublePlant, 2);  // lower half (tall grass variant)
        w.Set(5, 65, 5, StoryBlocks.DoublePlant, 10); // upper half
        h.Lives[c.P(1)] = FakeStoryHost.Live(7, 64, 7); // a second pawn working nearby
        var box = Area.Of(5, 64, 5, 5, 65, 5);
        Assert.Equal(1, c.Designate("add", "cut", box, null, null).Result!["count"]!.GetValue<int>());
        Assert.Equal(1, c.Designate("add", "mine", box, null, "all").Result!["count"]!.GetValue<int>());
        Assert.All(c.Designations, d => Assert.Equal(new P3(5, 64, 5), d.At)); // both on the lower half

        var first = c.Next(0);
        Assert.Equal(new P3(5, 64, 5), At(first));
        Assert.Equal("b:5,64,5", c.CurrentOf(c.P())!.Lease);
        // The other pawn never gets either half of it while the first one works there.
        var second = c.Next(1);
        Assert.NotEqual("dig", second["kind"]?.GetValue<string>());
        Assert.Single(c.Leases(), l => l.Key.StartsWith("b:5,", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pawn_still_at_its_job_keeps_its_lease_past_90_seconds()
    {
        var clock = NewClock();
        var (c, _, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Chop);
        w.Set(3, 64, 3, 31, 1);
        c.Designate("add", "cut", null, [new P3(3, 64, 3)], null);
        var j = c.Next(0);
        for (int i = 0; i < 12; i++) { clock.Now += TimeSpan.FromSeconds(10); c.Tick(); }
        Assert.Equal(Id(j), Id(c.Next(0))); // still its job
        Assert.NotEqual("dig", c.Next(1)["kind"]?.GetValue<string>()); // and nobody else's
    }

    // ================================================================ exposed cells only, top-down; buried cells wait without failing

    [Fact]
    public void A_3x3x3_block_is_dug_out_completely_and_only_exposed_cells_are_handed_out()
    {
        var (c, h, w) = StoryRig.Make(3);
        Only(c, WorkTypes.Mine);
        h.Lives[c.P(0)] = FakeStoryHost.Live(-2, 64, 1);
        h.Lives[c.P(1)] = FakeStoryHost.Live(4, 64, 1);
        h.Lives[c.P(2)] = FakeStoryHost.Live(1, 64, 4);
        // The fake world is stone at y <= 63: a 3x3x3 block from y 61 to 63, buried on every side but the top.
        Assert.Equal(27, c.Designate("add", "mine", Area.Of(0, 61, 0, 2, 63, 2), null, "all").Result!["count"]!.GetValue<int>());
        bool Open(int x, int y, int z) => w.Block(x, y, z).Id == 0;
        int digs = 0;
        for (int round = 0; round < 40 && c.Designations.Count > 0; round++)
            for (int i = 0; i < 3; i++)
            {
                var j = c.Next(i);
                if (j["kind"]?.GetValue<string>() != "dig") { c.Report(c.P(i), Id(j), "done", null, null); continue; }
                var a = At(j);
                Assert.True(Open(a.X, a.Y + 1, a.Z) || Open(a.X + 1, a.Y, a.Z) || Open(a.X - 1, a.Y, a.Z) || Open(a.X, a.Y, a.Z + 1) || Open(a.X, a.Y, a.Z - 1),
                    $"{a} was handed out while still buried");
                w.Set(a.X, a.Y, a.Z, 0);
                c.Report(c.P(i), Id(j), "done", null, null);
                digs++;
            }
        Assert.Equal(27, digs);
        Assert.Empty(c.Designations);
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
    }

    [Fact]
    public void Cant_reach_is_not_a_failure_until_it_keeps_happening()
    {
        var clock = NewClock();
        var (c, _, w) = StoryRig.Make(time: clock);
        Only(c, WorkTypes.Mine);
        w.Set(3, 64, 3, 3);
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], "all");
        for (int i = 1; i < StoryColony.SoftLimit; i++)
        {
            var j = c.Next();
            Assert.Equal("dig", j["kind"]!.GetValue<string>());
            c.Report(c.P(), Id(j), "failed", "can't reach dirt at 3,64,3", null);
            var d = Assert.Single(c.Designations);
            Assert.Equal(0, d.Fails);
            Assert.Equal("open", d.Status);
            Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
            Assert.NotEqual("dig", c.Next()["kind"]?.GetValue<string>()); // it rests a little
            c.Report(c.P(), Id(c.Next()), "done", null, null);
            clock.Now += StoryColony.SoftRest + TimeSpan.FromSeconds(1);
        }
        // Again and again: now it counts (and says why).
        var last = c.Next();
        c.Report(c.P(), Id(last), "failed", "can't reach dirt at 3,64,3", null);
        Assert.Equal(1, Assert.Single(c.Designations).Fails);
        Assert.Contains(c.Alerts, a => a.Level == "warn" && a.Text.Contains("can't reach"));
    }

    // ================================================================ nobody digs a block someone stands on

    [Fact]
    public void A_block_another_pawn_stands_on_is_skipped_and_a_pawn_on_its_own_target_steps_off_first()
    {
        var (c, h, w) = StoryRig.Make(2);
        Only(c, WorkTypes.Mine);
        c.Designate("add", "mine", null, [new P3(1, 63, 1), new P3(6, 63, 6)], "all");
        h.Lives[c.P(0)] = FakeStoryHost.Live(1, 64, 2); // nearest to 1,63,1
        h.Lives[c.P(1)] = FakeStoryHost.Live(1, 64, 1); // standing on it
        var j = c.Next(0);
        Assert.Equal(new P3(6, 63, 6), At(j));

        // A pawn standing on its only target gets a cell beside it to dig from.
        var (c2, h2, _) = StoryRig.Make();
        Only(c2, WorkTypes.Mine);
        c2.Designate("add", "mine", null, [new P3(1, 63, 1)], "all");
        h2.Lives[c2.P()] = FakeStoryHost.Live(1, 64, 1);
        var own = c2.Next();
        Assert.Equal(new P3(1, 63, 1), At(own));
        Assert.NotNull(own["stand"]);
        Assert.NotEqual("1,64,1", own["stand"]!.GetValue<string>());
    }
}
