using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryHarvestGoneTests
{
    private static readonly P3 At = new(3, 64, 3);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void OnlyHarvest(StoryColony c)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var type in WorkTypes.All) p.Priorities[type] = type is WorkTypes.Chop or WorkTypes.Farm ? 1 : 0;
    }

    private static void Failed(StoryColony c, JsonObject job, string why = "no tree at 3,64,3") =>
        Assert.True(c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", why, null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_gone_tree_removes_manual_and_automatic_marks_and_publishes_the_removal(bool automatic)
    {
        var (c, h, w) = StoryRig.Make();
        OnlyHarvest(c);
        w.Tree(At.X, At.Z);
        Assert.Null(c.Designate("add", "chop", null, [At], null).Error);
        var mark = Assert.Single(c.Designations);
        mark.Auto = automatic;
        var job = c.Next();
        Assert.Equal("chop", job["kind"]!.GetValue<string>());
        long rev = c.State()["rev"]!.GetValue<long>();
        h.Views[c.P()] = new FakeStoryWorld(); // main still sees the old tree; the worker saw it disappear
        Failed(c, job);
        Assert.Empty(c.Designations);
        Assert.Empty(c.Leases());
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Contains(mark.Id, c.State(rev)["removed"]!["designations"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
        Assert.False(c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", "late no tree there", null));
    }

    [Theory]
    [InlineData("cut", 31, 1)]
    [InlineData("harvest", 59, 7)]
    public void Harvest_and_cut_marks_clear_when_another_worker_has_already_taken_the_target(string kind, int block, int meta)
    {
        var (c, _, w) = StoryRig.Make();
        OnlyHarvest(c);
        w.Set(At.X, At.Y, At.Z, block, meta);
        c.Designate("add", kind, null, [At], null);
        var job = c.Next();
        w.Set(At.X, At.Y, At.Z, 0);
        Failed(c, job, "no target there");
        Assert.Empty(c.Designations);
        Assert.Empty(c.Leases());
    }

    [Theory]
    [InlineData("can't reach tree: no route")]
    [InlineData("no tree there")]
    [InlineData("tree doesn't exist")]
    public void A_still_standing_tree_is_not_erased_by_a_failed_or_inaccurate_report(string why)
    {
        var (c, _, w) = StoryRig.Make();
        OnlyHarvest(c);
        w.Tree(At.X, At.Z);
        c.Designate("add", "chop", null, [At], null);
        Failed(c, c.Next(), why);
        Assert.Single(c.Designations);
        Assert.Empty(c.Leases());
    }

    [Fact]
    public void A_missing_base_does_not_erase_the_remaining_recorded_logs()
    {
        var (c, h, w) = StoryRig.Make();
        OnlyHarvest(c);
        w.Tree(At.X, At.Z);
        c.Designate("add", "chop", null, [At], null);
        var job = c.Next();
        var own = new FakeStoryWorld();
        own.Set(At.X, At.Y + 2, At.Z, 17);
        h.Views[c.P()] = own;
        Failed(c, job, "can't reach the remaining tree logs");
        Assert.Single(c.Designations);
    }

    [Theory]
    [InlineData("no tree at 3,64,3; nearest trees: spruce 8,64,3")]
    [InlineData("no tree there")]
    [InlineData("tree doesn't exist")]
    public void A_terminal_no_tree_report_cancels_the_removed_base_marker_even_with_stray_upper_logs(string why)
    {
        var (c, h, w) = StoryRig.Make();
        OnlyHarvest(c);
        w.Tree(At.X, At.Z);
        c.Designate("add", "chop", null, [At], null);
        var job = c.Next();
        var own = new FakeStoryWorld();
        own.Set(At.X, At.Y + 2, At.Z, 17);
        h.Views[c.P()] = own;
        Failed(c, job, why);
        Assert.Empty(c.Designations);
        Assert.Empty(c.Leases());
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
    }

    [Fact]
    public void An_unloaded_column_is_not_evidence_that_a_tree_disappeared()
    {
        var (c, h, w) = StoryRig.Make();
        OnlyHarvest(c);
        w.Tree(At.X, At.Z);
        c.Designate("add", "chop", null, [At], null);
        var job = c.Next();
        var own = new FakeStoryWorld();
        own.Unloaded.Add((At.X, At.Z));
        w.Unloaded.Add((At.X, At.Z));
        h.Views[c.P()] = own;
        Failed(c, job);
        Assert.Single(c.Designations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_no_tree_mark_with_31_failures_is_retired_on_tick_without_waiting_for_retry(bool active)
    {
        var (c, h, w) = StoryRig.Make();
        OnlyHarvest(c);
        w.Tree(At.X, At.Z);
        c.Designate("add", "chop", null, [At], null);
        var job = c.Next();
        if (!active) Failed(c, job); // the standing tree keeps this as an ordinary failure with a retry delay
        var mark = Assert.Single(c.Designations);
        mark.Fails = 31;
        mark.Status = "failed";
        mark.Reason = "no tree at 3,64,3; nearest trees: spruce 8,64,3";
        var own = new FakeStoryWorld();
        own.Set(At.X, At.Y + 2, At.Z, 17); // terminal no-tree result also clears old bases with upper remnants
        h.Views[c.P()] = own;
        long rev = c.State()["rev"]!.GetValue<long>();
        c.Tick();
        Assert.Empty(c.Designations);
        Assert.Empty(c.Leases());
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Equal(active ? 1 : 0, h.Interrupted.Count);
        Assert.Contains(mark.Id, c.State(rev)["removed"]!["designations"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_standing_zone_keeps_other_trees_and_does_not_repeat_a_stale_main_target()
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(time: clock);
        OnlyHarvest(c);
        w.Tree(At.X, At.Z);
        w.Tree(8, 3);
        Assert.Null(c.ZoneOp("add", "chop", Area.Of(3, 63, 3, 8, 69, 3)).Error);
        var first = c.Next();
        Assert.Equal(At.ToString(), first["at"]!.GetValue<string>());
        var own = new FakeStoryWorld();
        own.Tree(8, 3);
        h.Views[c.P()] = own;
        Failed(c, first);
        Assert.Single(c.Zones);
        clock.Now += TimeSpan.FromSeconds(11); // also past the short view-lag rest
        var next = c.Next();
        Assert.Equal("8,64,3", next["at"]!.GetValue<string>());
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
    }
}
