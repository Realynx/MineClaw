using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Blocked cells are tried again (docs/web/story-mode.md "Construction", "Retrying failed build cells"). Fox: "Even now, there's some
/// blocked blocks on the storehouse build that we've been making." A cell that failed three times stayed <c>blocked</c> until the
/// owner pressed Retry, across restarts and across the deploys that fixed what failed it. Now each reconcile pass reopens a few
/// cells per reason: four, after 10, 30, then every 60 minutes, and at once for cells blocked before the process started. One that
/// goes in reopens every cell blocked with that reason; one that fails again is blocked at once, with no second alert.
/// </summary>
public sealed class StoryBlockedCellRecoveryTests
{
    /// <summary>A failure that counts against the cell (not "not now", not a missing support).</summary>
    private const string Why = "out of reach even from a pillar: no ground to stand on below or beside it within 24 blocks";

    private const string Refused = "can't clear the cell {0}: won't dig stone at {0}: a placed stone (someone's floor, wall or path)";

    /// <summary>1.12.2, one pawn that only builds and carries planks (and the fixture's stone pickaxe).</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, ManualTime T) Rig()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, Release1122GameData.Instance, clock);
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: new Stack(5, 0, 64));
        return (c, h, w, clock);
    }

    private static JsonObject Block(int id, int meta, params int[][] cells) => new()
    {
        ["kind"] = "block", ["blocks"] = new JsonArray(cells.Select(p => (JsonNode)new JsonArray(p[0], p[1], p[2])).ToArray()),
        ["material"] = new JsonArray(id, meta),
    };

    private static BuildState Add(StoryColony c, JsonObject item)
    {
        Assert.Null(c.BuildsOp("add", new JsonArray(item), null).Error);
        return c.Builds.Last();
    }

    private static BuildState Add(StoryColony c, int id, int meta, params int[][] cells) => Add(c, Block(id, meta, cells));

    /// <summary>A cell as a save holds it once it has failed three times: blocked, with its last reason; no count or rest in memory.</summary>
    private static void Blocked(GhostState g, string why)
    {
        g.State = "blocked";
        g.Reason = why;
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private static List<string> Strs(JsonNode? a) => a?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    /// <summary>The pawn's next job; anything but a build job (idle, a walk) is ended at once so the pick after it is a fresh one.</summary>
    private static JsonObject Pick(StoryColony c)
    {
        var j = c.Next();
        if (j["kind"]?.GetValue<string>() is not ("place" or "dig") && j["id"] is not null) c.Report(c.P(), Id(j), "done", null, null);
        return j;
    }

    /// <summary>The pawn's place job fails at one cell, as the pawn script reports it: that cell, then the batch's end.</summary>
    private static void Fail(StoryColony c, JsonObject job, string at, string why)
    {
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Contains(at, Strs(job["cells"]));
        c.Report(c.P(), Id(job), "progress", $"cell {at} failed: {why}", null);
        c.Report(c.P(), Id(job), "done", $"batch: 0/{Strs(job["cells"]).Count} placed", null);
    }

    [Fact]
    public void A_cell_saved_as_blocked_is_tried_again_on_the_first_pass_after_a_load()
    {
        var (c, _, w, _) = Rig();
        var b = Add(c, 5, 0, [4, 64, 12]);
        var cell = b.Ghosts.Single();
        Blocked(cell, "out of reach: no path to block"); // blocked before the 11-08 fixes; they changed the picker, not the save
        Assert.NotEqual("place", Pick(c)["kind"]?.GetValue<string>());

        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
        Assert.Equal("out of reach: no path to block", cell.Reason); // the map still says why it was blocked
        Assert.Equal(1, c.FailedCells());
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal("4,64,12", j["at"]!.GetValue<string>());
        w.Set(4, 64, 12, 5);
        c.Report(c.P(), Id(j), "progress", "cell 4,64,12 done", null);
        Assert.Equal("done", cell.State);
        Assert.Equal("done", b.Status);
        Assert.Equal(0, c.FailedCells());
    }

    /// <summary>A plain remove build's dig cell takes neither of the pass's branches for placed blocks and empty space: it is tried
    /// again all the same.</summary>
    [Fact]
    public void A_blocked_dig_cell_of_a_remove_build_is_tried_again_too()
    {
        var (c, _, w, _) = Rig();
        var cell = Add(c, new JsonObject { ["kind"] = "remove", ["blocks"] = new JsonArray(new JsonArray(4, 63, 12)) }).Ghosts.Single();
        Assert.True(cell.Remove);
        Blocked(cell, "out of reach: no path to block");
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
        var j = Pick(c);
        Assert.Equal("dig", j["kind"]!.GetValue<string>());
        Assert.Equal("4,63,12", j["at"]!.GetValue<string>());
        w.Set(4, 63, 12, 0);
        Assert.True(c.Report(c.P(), Id(j), "done", null, null));
        Assert.Equal("done", cell.State);
    }

    [Fact]
    public void Only_four_cells_of_a_reason_are_tried_and_a_success_reopens_the_rest()
    {
        var (c, _, w, t) = Rig();
        var b = Add(c, 5, 0, Enumerable.Range(4, 10).Select(x => new[] { x, 64, 12 }).ToArray());
        // One reason, said with each cell's own coordinates.
        foreach (var g in b.Ghosts) Blocked(g, $"server undid my placement at {g.At} (protected area?)");
        List<string> Open() => b.Ghosts.Where(g => g.State is "todo" or "leased").Select(g => g.At.ToString()).Order().ToList();

        c.Reconcile(w);
        Assert.Equal(["4,64,12", "5,64,12", "6,64,12", "7,64,12"], Open()); // the lowest: y, then x, z
        Assert.Equal(6, b.Ghosts.Count(g => g.State == "blocked"));
        // No more while those four are out, also once the reason's wait is over.
        t.Now += TimeSpan.FromMinutes(11);
        for (int i = 0; i < 3; i++) c.Reconcile(w);
        Assert.Equal(4, Open().Count);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("can't place at"));

        // One of them goes in: what blocked this reason is gone, so the other nine start over.
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        var first = Strs(j["cells"])[0];
        var at = first.Split(',').Select(int.Parse).ToArray();
        w.Set(at[0], at[1], at[2], 5);
        c.Report(c.P(), Id(j), "progress", $"cell {first} done", null);
        Assert.Equal(9, Open().Count);
        Assert.DoesNotContain(b.Ghosts, g => g.State == "blocked");
        Assert.All(b.Ghosts.Where(g => g.State != "done"), g => Assert.Null(g.Reason));
        Assert.Equal(0, c.FailedCells()); // a fresh start: no failure on record
    }

    [Fact]
    public void A_failed_sample_blocks_at_once_and_its_reason_waits_ten_then_thirty_then_sixty_minutes()
    {
        var (c, _, w, t) = Rig();
        var cell = Add(c, 5, 0, [4, 64, 12]).Ghosts.Single();
        Blocked(cell, Why);
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
        foreach (int wait in new[] { 10, 30, 60, 60 })
        {
            Fail(c, Pick(c), "4,64,12", Why); // one failure, not three more trips
            Assert.Equal("blocked", cell.State);
            Assert.Equal(Why, cell.Reason);
            t.Now += TimeSpan.FromMinutes(wait - 1);
            c.Reconcile(w);
            Assert.Equal("blocked", cell.State);
            t.Now += TimeSpan.FromMinutes(1);
            c.Reconcile(w);
            Assert.Equal("todo", cell.State);
        }
    }

    [Fact]
    public void Superseded_quarry_undiggable_refused_clear_and_pour_cells_are_never_sampled()
    {
        var (c, _, w, t) = Rig();
        // A cell a newer build took over.
        var superseded = Add(c, 3, 0, [4, 64, 12]).Ghosts.Single();
        Add(c, 1, 0, [4, 64, 12]);
        // A cell in a quarry's columns.
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(20, 63, 20, 23, 63, 23)).Error);
        var quarried = Add(c, 5, 0, [21, 63, 21]).Ghosts.Single();
        // Water in a placed design's empty space.
        w.Set(31, 64, 30, 9);
        var design = Block(4, 0, [30, 64, 30], [32, 64, 30]);
        design["replace_area"] = new JsonArray(30, 64, 30, 32, 64, 30);
        Add(c, design);
        var undiggable = c.Builds.Single(b => b.Kind == "remove").Ghosts.Single();
        // A clear the pawn's dig refused for good, the stone still in the cell.
        w.Set(40, 64, 12, 1);
        var refused = Add(c, 5, 0, [40, 64, 12]).Ghosts.Single();
        Blocked(refused, string.Format(Refused, "40,64,12"));
        // A pour with nothing to pour against.
        var pour = new GhostState { At = new P3(50, 64, 12), Id = 9, Item = "water_bucket" };
        Blocked(pour, "nothing beside or under 50,64,12 to pour against");
        c.Builds.Add(new BuildState { Id = "pond", Kind = "water", Label = "water", Ghosts = [pour] });
        // Its own block standing in the cell, turned another way (the rotation rules decide those).
        w.Set(60, 64, 12, 53, 0);
        var turned = Add(c, 53, 2, [60, 64, 12]).Ghosts.Single();
        Blocked(turned, "placed with wrong exact state at 60,64,12: wants 53:2, got 53:0");
        // And an ordinary failure beside them, which is tried.
        var plain = Add(c, 5, 0, [70, 64, 12]).Ghosts.Single();
        Blocked(plain, Why);

        GhostState[] held = [superseded, quarried, undiggable, refused, pour, turned];
        Assert.All(held, g => Assert.Equal("blocked", g.State));
        Assert.StartsWith("superseded by build ", superseded.Reason);
        Assert.StartsWith("conflicts with quarry ", quarried.Reason);
        Assert.StartsWith("cannot clear ", undiggable.Reason);
        var reasons = held.Select(g => g.Reason).ToList();
        for (int i = 0; i < 30; i++)
        {
            c.Reconcile(w);
            t.Now += TimeSpan.FromMinutes(5);
        }
        Assert.All(held, g => Assert.Equal("blocked", g.State));
        Assert.Equal(reasons, held.Select(g => g.Reason).ToList());
        Assert.Equal("todo", plain.State);
    }

    [Fact]
    public void A_refused_clear_reopens_when_the_block_in_the_way_is_gone()
    {
        var (c, _, w, t) = Rig();
        w.Set(4, 64, 12, 1); // stone where the planks go
        var cell = Add(c, 5, 0, [4, 64, 12]).Ghosts.Single();
        var j = Pick(c);
        Assert.Equal(["4,64,12"], Strs(j["clear"]));
        Fail(c, j, "4,64,12", string.Format(Refused, "4,64,12"));
        Assert.Equal("blocked", cell.State); // at once: no try will move a protected block
        for (int i = 0; i < 6; i++)
        {
            t.Now += TimeSpan.FromMinutes(30);
            c.Reconcile(w);
        }
        Assert.Equal("blocked", cell.State);

        w.Set(4, 64, 12, 0); // the owner took the stone out: nothing is left to clear
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
        Assert.Null(cell.Reason);
        var again = Pick(c);
        Assert.Equal("place", again["kind"]!.GetValue<string>());
        Assert.Empty(Strs(again["clear"]));
    }

    [Fact]
    public void A_finished_neighbour_makes_the_blocked_cell_beside_it_due()
    {
        var (c, h, w, t) = Rig();
        var wall = Add(c, 4, 0, [4, 64, 12]).Ghosts.Single(); // cobblestone: none yet, so it waits
        var cell = Add(c, 5, 0, [5, 64, 12]).Ghosts.Single();
        Blocked(cell, Why);
        c.Reconcile(w);
        Fail(c, Pick(c), "5,64,12", Why);
        Assert.Equal("blocked", cell.State);
        t.Now += TimeSpan.FromMinutes(1);
        c.Reconcile(w);
        Assert.Equal("blocked", cell.State); // its reason waits ten minutes

        // The block beside it goes in (the wall a torch hangs on, the support a block leans on).
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(5, 0, 64), new Stack(4, 0, 8)]);
        var j = Pick(c);
        Assert.Equal(["4,64,12"], Strs(j["cells"]));
        w.Set(4, 64, 12, 4);
        c.Report(c.P(), Id(j), "progress", "cell 4,64,12 done", null);
        c.Report(c.P(), Id(j), "done", "batch: 1/1 placed", null);
        Assert.Equal("done", wall.State);
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
        Assert.Equal(Why, cell.Reason);
        // That try did not move the reason's own wait: failed again, the cell is tried when the ten minutes are over.
        Fail(c, Pick(c), "5,64,12", Why);
        Assert.Equal("blocked", cell.State);
        t.Now += TimeSpan.FromMinutes(8);
        c.Reconcile(w);
        Assert.Equal("blocked", cell.State);
        t.Now += TimeSpan.FromMinutes(1);
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
    }

    /// <summary>Tries no pawn is ever handed (torches whose walls are not up) stayed out for good, and while they were out every other
    /// cell of their reason stayed blocked, also one whose wall had just gone in.</summary>
    [Fact]
    public void A_cell_beside_a_finished_one_is_tried_while_its_reasons_tries_are_still_out()
    {
        var (c, _, w, _) = Rig();
        var b = Add(c, 5, 0, Enumerable.Range(4, 10).Select(x => new[] { x, 64, 12 }).ToArray());
        foreach (var g in b.Ghosts) Blocked(g, Why);
        c.Reconcile(w);
        GhostState At(int x) => b.Ghosts.Single(g => g.At.X == x);
        Assert.All(Enumerable.Range(4, 4), x => Assert.Equal("todo", At(x).State)); // four tries out, never handed to a pawn
        Assert.Equal("blocked", At(12).State);

        // Something beside x=12 is finished (here another build's block, found standing by the pass).
        var wall = Add(c, 4, 0, [12, 64, 13]).Ghosts.Single();
        w.Set(12, 64, 13, 4);
        c.Reconcile(w);
        Assert.Equal("done", wall.State);
        c.Reconcile(w);
        Assert.Equal("todo", At(12).State);
        Assert.Equal(Why, At(12).Reason);
        Assert.All(new[] { 8, 9, 10, 11, 13 }, x => Assert.Equal("blocked", At(x).State)); // the rest still wait
        Assert.All(Enumerable.Range(4, 4), x => Assert.Equal("todo", At(x).State));
    }

    [Fact]
    public void A_sample_raises_no_second_alert_for_the_same_reason()
    {
        var (c, _, w, t) = Rig();
        var cell = Add(c, 5, 0, [4, 64, 12]).Ghosts.Single();
        for (int i = 0; i < 3; i++)
        {
            Fail(c, Pick(c), "4,64,12", Why);
            t.Now += TimeSpan.FromMinutes(2); // past the rest after a counted failure
        }
        Assert.Equal("blocked", cell.State);
        Assert.Single(c.Alerts, a => a.Text.Contains("can't place at"));
        // Blocked just now: its reason's first wait runs from then, so the next pass leaves it.
        c.Reconcile(w);
        Assert.Equal("blocked", cell.State);

        t.Now += TimeSpan.FromMinutes(10);
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
        Fail(c, Pick(c), "4,64,12", Why);
        Assert.Equal("blocked", cell.State);
        Assert.Single(c.Alerts, a => a.Text.Contains("can't place at"));
    }

    /// <summary>A try that never gets an outcome is not walked to for good: the batch ends before the pawn gets to it (no report of
    /// the cell). Handed out four times and still open, the cell is blocked again until its reason's next try. A miss that is not the
    /// cell's (here the pawn died on the way) is no try: it never blocks the cell again (StoryCellFailureKindsTests).</summary>
    [Fact]
    public void A_try_handed_out_four_times_without_an_outcome_waits_for_its_reasons_next_try()
    {
        var (c, _, w, clock) = Rig();
        var cell = Add(c, 5, 0, [4, 64, 12]).Ghosts.Single();
        Blocked(cell, Why);
        c.Reconcile(w);
        foreach (var (dead, rest) in new[] { (true, 1), (false, 1), (true, 1), (false, 2), (false, 4), (false, 0) }) // minutes
        {
            Assert.Equal("todo", cell.State);
            var j = Pick(c);
            Assert.Equal("place", j["kind"]!.GetValue<string>());
            Assert.True(dead ? c.Report(c.P(), Id(j), "failed", "dead", null) : c.Report(c.P(), Id(j), "done", "batch: 0/1 placed", null));
            clock.Now += TimeSpan.FromMinutes(rest);
            c.Reconcile(w);
        }
        Assert.Equal("blocked", cell.State);
        Assert.Equal(Why, cell.Reason); // the reason it was blocked with, not the miss's text: that reason's wait holds it
        Assert.NotEqual("place", Pick(c)["kind"]?.GetValue<string>());
        // Nine minutes in: its reason's ten-minute wait has not moved.
        clock.Now += TimeSpan.FromMinutes(1);
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
    }

    /// <summary>A build whose remaining cells a quarry digs has nothing a retry can reopen: it keeps its status and the reason that
    /// says which quarry, and its cells are not counted as failed.</summary>
    [Fact]
    public void Retry_leaves_a_build_a_quarry_holds_as_it_is()
    {
        var (c, _, w, _) = Rig();
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(20, 63, 20, 23, 63, 23)).Error);
        var b = Add(c, 5, 0, [21, 63, 21], [22, 63, 21]);
        c.Reconcile(w);
        Assert.Equal("blocked", b.Status);
        var reason = b.Reason;
        Assert.Contains("lie in quarry", reason);
        Assert.Equal(0, c.FailedCells());

        var r = c.RetryBuilds("retry_all", null, null);
        Assert.Equal(0, r.Result!["reopened"]!.GetValue<int>());
        Assert.All(b.Ghosts, g => Assert.StartsWith("conflicts with quarry ", g.Reason));
        Assert.Equal("blocked", b.Status);
        Assert.Equal(reason, b.Reason);
    }

    /// <summary>The owner's retry of a whole build is a fresh start for its reasons too: a cell that blocks again waits the first wait
    /// (ten minutes), not the one its reason had reached.</summary>
    [Fact]
    public void A_whole_builds_retry_starts_its_reasons_waits_over()
    {
        var (c, _, w, t) = Rig();
        var b = Add(c, 5, 0, [4, 64, 12]);
        var cell = b.Ghosts.Single();
        Blocked(cell, Why);
        c.Reconcile(w);
        Fail(c, Pick(c), "4,64,12", Why);
        t.Now += TimeSpan.FromMinutes(10);
        c.Reconcile(w);
        Fail(c, Pick(c), "4,64,12", Why); // tried twice: its reason now waits thirty minutes
        Assert.Equal("blocked", cell.State);

        Assert.Equal(1, c.RetryBuilds("retry", [b.Id], null).Result!["reopened"]!.GetValue<int>());
        for (int i = 0; i < 3; i++)
        {
            Fail(c, Pick(c), "4,64,12", Why);
            t.Now += TimeSpan.FromMinutes(2);
        }
        Assert.Equal("blocked", cell.State);
        t.Now += TimeSpan.FromMinutes(3);
        c.Reconcile(w);
        Assert.Equal("blocked", cell.State);
        t.Now += TimeSpan.FromMinutes(6);
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
    }
}
