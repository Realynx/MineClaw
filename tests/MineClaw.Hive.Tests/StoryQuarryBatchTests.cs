using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryQuarryBatchTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly P3 Top = new(14, 60, 14);
    private static readonly P3 Lower = new(14, 59, 14);
    private static readonly P3 Beside = new(16, 60, 14);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(int pawns = 1, TimeProvider? time = null)
    {
        var (c, h, w) = StoryRig.Make(pawns, time: time);
        c.DoneGrace = TimeSpan.Zero;
        c.SetHaulOptions(null, null, null, pillarStack: 0);
        foreach (var p in c.Pawns.Values)
        {
            foreach (var type in WorkTypes.All) p.Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
            h.Lives[p.Name] = FakeStoryHost.Live(14, 64, 14);
        }
        c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 19, 63, 19));
        var zone = c.Zones.Single();
        zone.QuarryTop = 63;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int x = 10; x <= 19; x++) for (int z = 10; z <= 19; z++) for (int y = 45; y <= 63; y++) w.Set(x, y, z, 0);
        for (int k = 0; k <= 18; k++)
        {
            var step = StoryQuarry.Step(ring, 63, k);
            w.Set(step.X, step.Y, step.Z, 1);
        }
        foreach (var at in new[] { Top, Lower, Beside }) w.Set(at.X, at.Y, at.Z, 4);
        return (c, h, w);
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static string[] Cells(JsonObject j) => j["dig_cells"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public void Nearby_remnants_share_a_job_including_the_lower_pole_but_never_the_next_solid_floor()
    {
        var (c, _, w) = Rig();
        // A thin remnant extends down several levels; a surviving floor caps how far this trip may work.
        for (int y = 52; y <= 58; y++) w.Set(14, y, 14, 4);
        for (int x = 11; x <= 18; x++) for (int z = 11; z <= 18; z++) w.Set(x, 54, z, 1);
        var job = c.Next();
        var cells = Cells(job);
        Assert.Equal(Top.ToString(), job["at"]!.GetValue<string>());
        Assert.Contains(Beside.ToString(), cells);
        Assert.Contains(Lower.ToString(), cells);
        Assert.Contains("14,55,14", cells);
        Assert.DoesNotContain(cells, cell => int.Parse(cell.Split(',')[1]) <= 54);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        Assert.DoesNotContain(cells, cell => Enumerable.Range(0, 63).Any(k => StoryQuarry.Step(ring, 63, k).ToString() == cell));
        Assert.InRange(cells.Length, 2, StoryColony.QuarryBatchMax);
    }

    [Fact]
    public void Nearby_steps_and_liquid_seals_are_never_extra_dig_cells()
    {
        var (c, _, w) = Rig();
        foreach (var at in new[] { Top, Lower, Beside }) w.Set(at.X, at.Y, at.Z, 0);
        w.Set(14, 60, 11, 4);
        w.Set(14, 59, 11, 4);
        w.Set(14, 58, 11, 4);
        w.Set(13, 58, 11, 11); // the lower remnant is sealing lava
        var cells = Cells(c.Next());
        Assert.Contains("14,59,11", cells);
        Assert.DoesNotContain("14,59,10", cells); // adjacent quarry stair step: as thin/exposed as the remnant
        Assert.DoesNotContain("14,58,11", cells);
    }

    [Fact]
    public void Every_reserved_cell_stays_exclusive_across_a_long_climb_and_returns_when_interrupted()
    {
        var clock = new Clock();
        var (c, _, _) = Rig(pawns: 2, time: clock);
        var job = c.Next();
        clock.Now += TimeSpan.FromSeconds(65);
        Assert.True(c.Report(c.P(), Id(job), "progress", "dig batch working", null));
        clock.Now += TimeSpan.FromSeconds(40);
        c.Tick();
        var other = c.Next(1);
        Assert.NotEqual("dig", other["kind"]!.GetValue<string>());
        c.Report(c.P(1), Id(other), "done", null, null);
        c.Report(c.P(), Id(job), "failed", "dig batch interrupted", null);
        Assert.Equal(Cells(job), Cells(c.Next(1)));
    }

    [Fact]
    public void A_partial_batch_rests_only_its_failed_cell_and_does_not_credit_it_as_done()
    {
        var (c, _, w) = Rig();
        var job = c.Next();
        c.Report(c.P(), Id(job), "progress", $"digcell {Top} failed: can't reach cobblestone", null);
        w.Set(Lower.X, Lower.Y, Lower.Z, 0);
        c.Report(c.P(), Id(job), "progress", $"digcell {Lower} done", null);
        // Beside was never attempted: finishing the partial batch must release it with no failure.
        c.Report(c.P(), Id(job), "done", "batch: 1/3 dug", null);
        var next = c.Next();
        Assert.Equal("dig", next["kind"]!.GetValue<string>());
        Assert.Equal(Beside.ToString(), next["at"]!.GetValue<string>());
        w.Set(Beside.X, Beside.Y, Beside.Z, 0);
        c.Report(c.P(), Id(next), "done", null, null);
        Assert.Contains("can't reach cobblestone", c.State()["zones"]![0]!["quarry"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Cancelling_an_extra_cell_stops_the_whole_batch_and_releases_the_untouched_work()
    {
        var (c, h, _) = Rig();
        var job = c.Next();
        var (result, error) = c.Designate("cancel", null, Area.Of(Lower.X, Lower.Y, Lower.Z, Lower.X, Lower.Y, Lower.Z), null, null);
        Assert.Null(error);
        Assert.Equal(1, result!["count"]!.GetValue<int>());
        Assert.Single(h.Interrupted, message => message.EndsWith("task cancelled by owner"));
        Assert.False(c.Report(c.P(), Id(job), "progress", $"digcell {Lower} done", null));
        var next = c.Next();
        Assert.NotEqual(Id(job), Id(next));
        Assert.Equal(Cells(job), Cells(next));
    }

    [Fact]
    public void Erasing_the_quarry_interrupts_its_reserved_batch()
    {
        var (c, h, _) = Rig();
        c.Next();
        c.ZoneOp("remove", null, null, id: c.Zones.Single().Id);
        Assert.Single(h.Interrupted, message => message.EndsWith("quarry changed"));
        Assert.NotEqual("dig", c.Next()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Batch_excludes_occupied_cancelled_and_harder_tool_cells()
    {
        var (c, h, w) = Rig(pawns: 2);
        h.Lives[c.P(1)] = FakeStoryHost.Live(Beside.X, Beside.Y + 1, Beside.Z);
        c.Meta.TempBlocks = [new TempBlockState { At = Lower, Id = 4, Cancelled = true }];
        w.Set(14, 58, 14, 56); // diamond ore needs iron, not the carried stone pickaxe
        w.Set(15, 60, 14, 4);
        var cells = Cells(c.Next());
        Assert.Contains("15,60,14", cells);
        Assert.DoesNotContain(Beside.ToString(), cells);
        Assert.DoesNotContain(Lower.ToString(), cells);
        Assert.DoesNotContain("14,58,14", cells);
    }

    [Theory]
    [InlineData("reach pillar")]
    [InlineData("escape pillar")]
    public void A_long_climb_keeps_the_entire_access_pillar_out_of_cleanup_and_quarry_jobs_until_its_owner_leaves(string kind)
    {
        var clock = new Clock();
        var (c, h, w) = Rig(pawns: 2, time: clock);
        w.Set(Beside.X, Beside.Y, Beside.Z, 0);
        for (int y = 45; y <= 62; y++)
        {
            w.Set(14, y, 14, 4);
            c.Temporary(c.P(), $"14,{y},14", true, kind);
        }
        // Mid-jump atop an 18-block tower: its bottom is well beyond the old eight-block abandonment radius.
        h.Lives[c.P()] = FakeStoryHost.Live(14, 64, 14);
        h.Lives[c.P(1)] = FakeStoryHost.Live(0, 64, 0);
        clock.Now += TimeSpan.FromMinutes(2);
        c.Tick();
        Assert.DoesNotContain(c.Designations, d => d.Auto && d.Kind == "deconstruct");
        var wait = c.Next(1);
        Assert.NotEqual("dig", wait["kind"]!.GetValue<string>());
        c.Report(c.P(1), Id(wait), "done", null, null);

        h.Lives[c.P()] = FakeStoryHost.Live(40, 64, 40);
        clock.Now += TimeSpan.FromSeconds(5);
        c.Tick();
        Assert.Equal(18, c.Designations.Count(d => d.Auto && d.Kind == "deconstruct"));
        var cleanup = c.Next(1);
        Assert.Equal("dig", cleanup["kind"]!.GetValue<string>());
        Assert.Equal("14,62,14", cleanup["at"]!.GetValue<string>());
        Assert.Contains("14,61,14", Cells(cleanup));
    }
}
