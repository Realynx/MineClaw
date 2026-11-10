using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Builds reconcile with the world (docs/web/story-mode.md "Builds reconcile with the world"; Fox 11-05: wrongly turned stairs went
/// unnoticed, "no way to place" where there was one, blocks in the way). Each case runs on both versions: a wrong-turned stair in a
/// finished house is found and fixed, a stone in a wall's cell is cleared (and a refused clear is said once, not tried a hundred
/// times), a cell beside only a stair, slab or pane is offered, a cell over another build's unplaced block waits for it and a truly
/// floating one goes on a scaffold, and a finished build whose block changed reopens.
/// </summary>
public sealed class StoryReconcileTests
{
    private static readonly P3 Chest = new(0, 64, 9);

    public static TheoryData<string> Versions => new() { "1.12.2", "b1.8.1" };

    private sealed class BuildClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("1.12.2", false)]
    [InlineData("1.12.2", true)]
    [InlineData("b1.8.1", false)]
    [InlineData("b1.8.1", true)]
    public void Missing_support_never_exhausts_retries_and_unattempted_cells_return_to_the_queue(string v, bool wholeJob)
    {
        var clock = new BuildClock();
        var (c, h, _) = StoryRig.Make(1, Data(v), clock);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 4, new Stack(5, 0, 64));
        var b = Add(c, 5, 0, [4, 64, 12], [5, 64, 12]);
        for (int i = 0; i < StoryColony.SoftLimit * 4; i++)
        {
            var j = Pick(c);
            Assert.Equal("place", j["kind"]!.GetValue<string>());
            Assert.Equal(2, Strs(j["cells"]).Count);
            var at = j["at"]!.GetValue<string>();
            if (wholeJob) c.Report(c.P(), Id(j), "failed", $"{at} has nothing solid on any side to place against", null);
            else
            {
                c.Report(c.P(), Id(j), "progress", $"cell {at} waiting: {at} has nothing solid on any side to place against", null);
                c.Report(c.P(), Id(j), "done", "batch: 0/2 placed", null);
            }
            Assert.All(b.Ghosts, g => { Assert.Equal("todo", g.State); Assert.Null(g.Reason); });
            Assert.Empty(c.Leases());
            clock.Now += TimeSpan.FromMinutes(1);
        }
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("nothing solid"));
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_second_builder_waits_until_the_first_builders_support_is_in_the_world(string v)
    {
        var (c, h, w) = StoryRig.Make(2, Data(v));
        foreach (var p in c.Pawns.Values)
        {
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
            h.Lives[p.Name] = FakeStoryHost.Bare(0, 64, 4, new Stack(4, 0, 8), new Stack(5, 0, 8));
        }
        Add(c, 4, 0, [4, 64, 12]);
        var upper = Add(c, 5, 0, [4, 65, 12]);
        var footing = c.Next(0);
        Assert.Equal(["4,64,12"], Strs(footing["cells"]));
        var waiting = c.Next(1);
        Assert.NotEqual("place", waiting["kind"]?.GetValue<string>());
        Assert.Equal("todo", upper.Ghosts.Single().State);
        c.Report(c.P(1), Id(waiting), "done", null, null);
        w.Set(4, 64, 12, 4);
        c.Report(c.P(0), Id(footing), "done", "batch: 1/1 placed", null);
        Assert.Equal(["4,65,12"], Strs(c.Next(1)["cells"]));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Saved_missing_support_failures_reopen_and_wait_for_the_footing(string v)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        Add(c, 4, 0, [4, 64, 12]);
        var upper = Add(c, 5, 0, [4, 65, 12]);
        var cell = upper.Ghosts.Single();
        cell.State = "blocked";
        cell.Reason = "4,65,12 has nothing solid on any side to place against";
        upper.Status = "blocked";
        c.Reconcile(w);
        Assert.Equal("todo", cell.State);
        Assert.Null(cell.Reason);
        Assert.NotEqual("blocked", upper.Status);
        Assert.NotEqual("place", Pick(c)["kind"]?.GetValue<string>());
        w.Set(4, 64, 12, 4);
        Assert.Equal(["4,65,12"], Strs(Pick(c)["cells"]));
    }

    private static IGameData Data(string v) => v == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(string v, params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(1, Data(v));
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 4, new Stack(274, 0, 1)); // a stone pickaxe: stone in the way can be dug
        w.Set(Chest.X, Chest.Y, Chest.Z, 54, 3);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        return (c, h, w);
    }

    private static BuildState Add(StoryColony c, int id, int meta, params int[][] cells)
    {
        var item = new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(cells.Select(p => (JsonNode)new JsonArray(p[0], p[1], p[2])).ToArray()),
            ["material"] = new JsonArray(id, meta),
        };
        Assert.Null(c.BuildsOp("add", new JsonArray(item), null).Error);
        return c.Builds.Last();
    }

    private static List<string> Strs(JsonNode? a) => a?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    private static JsonArray GhostOf(StoryColony c, P3 at) =>
        ((JsonArray)((JsonObject)c.State()["ghosts"]!)["blocks"]!).Select(x => (JsonArray)x!)
        .Single(g => g[0]!.GetValue<int>() == at.X && g[1]!.GetValue<int>() == at.Y && g[2]!.GetValue<int>() == at.Z);

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    [Theory, MemberData(nameof(Versions))]
    public void Newer_pavement_supersedes_an_old_floor_without_repairing_each_other(string v)
    {
        var (c, _, w) = Rig(v, new Stack(3, 0, 8), new Stack(1, 0, 8));
        w.Set(4, 64, 12, 3, 0);
        var floor = Add(c, 3, 0, [4, 64, 12], [5, 64, 12]);
        var pavement = Add(c, 1, 0, [4, 64, 12]);
        Assert.Null(c.BuildsOp("reorder", null, [pavement.Id, floor.Id]).Error);
        w.Set(4, 64, 12, 1, 0);
        for (int i = 0; i < 4; i++) c.Reconcile(w);
        Assert.Equal("blocked", floor.Ghosts[0].State);
        Assert.Contains("superseded by build " + pavement.Id, floor.Ghosts[0].Reason);
        Assert.Equal("todo", floor.Ghosts[1].State);
        Assert.Equal("done", pavement.Ghosts[0].State);
        Assert.Null(c.BuildsOp("remove", null, [pavement.Id]).Error);
        c.Reconcile(w);
        Assert.Equal("todo", floor.Ghosts[0].State);
        Assert.Null(floor.Ghosts[0].Reason);
    }

    /// <summary>An older build whose last unfinished cells a newer build took over showed as blocked for good, with no reason: a
    /// finished floor the owner later paved a part of. Nothing of it failed and nothing can be done about it: it is done.</summary>
    [Theory, MemberData(nameof(Versions))]
    public void A_build_left_with_only_superseded_cells_is_done(string v)
    {
        var (c, _, w) = Rig(v, new Stack(1, 0, 8));
        w.Set(4, 64, 12, 3, 0);
        w.Set(5, 64, 12, 3, 0);
        var floor = Add(c, 3, 0, [4, 64, 12], [5, 64, 12]);
        c.Reconcile(w);
        Assert.Equal("done", floor.Status);
        var pavement = Add(c, 1, 0, [4, 64, 12]);
        Assert.Equal("blocked", floor.Ghosts[0].State);
        Assert.Contains("superseded by build " + pavement.Id, floor.Ghosts[0].Reason);
        for (int i = 0; i < 3; i++) c.Reconcile(w);
        Assert.Equal("done", floor.Status);
        Assert.Null(floor.Reason);
        Assert.Equal(0, c.FailedCells());
        var shown = c.State()["builds"]!.AsArray().Single(b => b!["id"]!.GetValue<string>() == floor.Id)!;
        Assert.Equal("done", shown["status"]!.GetValue<string>());

        // Another cell of it that cannot be built for a reason of its own still shows the build as blocked.
        var (c2, _, w2) = Rig(v, new Stack(1, 0, 8));
        w2.Set(5, 64, 12, 1, 0); // stone where the floor's other cell goes, and the pawn's dig of it was refused for good
        var held = Add(c2, 3, 0, [4, 64, 12], [5, 64, 12]);
        Add(c2, 1, 0, [4, 64, 12]);
        held.Ghosts[1].State = "blocked";
        held.Ghosts[1].Reason = "can't clear the cell 5,64,12: won't dig stone at 5,64,12: a placed stone (someone's floor, wall or path)";
        for (int i = 0; i < 3; i++) c2.Reconcile(w2);
        Assert.Equal("blocked", held.Status);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_new_design_interrupts_a_leased_old_floor_batch(string v)
    {
        var (c, h, w) = Rig(v, new Stack(3, 0, 8), new Stack(1, 0, 8));
        var floor = Add(c, 3, 0, [4, 64, 12]);
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal("leased", floor.Ghosts[0].State);
        var pavement = Add(c, 1, 0, [4, 64, 12]);
        Assert.Equal("blocked", floor.Ghosts[0].State);
        Assert.Contains(pavement.Id, floor.Ghosts[0].Reason);
        Assert.False(c.Report(c.P(), Id(j), "done", null, null));
    }

    /// <summary>The pawn's next job; anything but a build job (idle, wander) is ended at once so the next pick is a fresh one.</summary>
    private static JsonObject Pick(StoryColony c)
    {
        var j = c.Next();
        if (j["kind"]?.GetValue<string>() is not ("place" or "dig") && j["id"] is not null) c.Report(c.P(), Id(j), "done", null, null);
        return j;
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_stair_turned_wrong_in_a_finished_build_is_found_shown_and_placed_again(string v)
    {
        var (c, _, w) = Rig(v, new Stack(53, 0, 4));
        w.Set(4, 64, 12, 53, 2); // built as drawn: rising south
        var b = Add(c, 53, 2, [4, 64, 12]);
        Assert.Equal("done", b.Ghosts.Single().State);
        c.Reconcile(w); // every cell shows the design: the build is done
        Assert.Equal("done", b.Status);
        w.Set(4, 64, 12, 53, 0); // now rising east (live: stairs placed before the rotation fix, never looked at again)
        c.Reconcile(w);
        Assert.Equal("done", b.Ghosts.Single().State); // once could be the view lagging a fresh placement
        c.Reconcile(w);
        var g = b.Ghosts.Single();
        Assert.Equal("todo", g.State);
        Assert.Equal("queued", b.Status);
        Assert.Contains(c.Alerts, a => a.Text.Contains("no longer match the design") && a.Text.Contains("turned the wrong way"));
        Assert.Equal("misrotated", GhostOf(c, g.At)[6]!.GetValue<string>()); // the map shows it turned wrong
        // Fixed: taken back (cleared) and placed again with the design's turn.
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Contains("4,64,12", Strs(j["clear"]));
        Assert.Equal([2], j["metas"]!.AsArray().Select(m => m!.GetValue<int>()));
        w.Set(4, 64, 12, 53, 2);
        c.Report(c.P(), Id(j), "progress", "cell 4,64,12 done", null);
        Assert.Equal("done", g.State);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Stone_in_a_walls_cells_is_cleared_first_and_a_refused_clear_is_blocked_and_said_once(string v)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        foreach (int x in new[] { 4, 5, 6 }) w.Set(x, 64, 12, 1, 0); // natural stone where the wall goes
        var b = Add(c, 5, 0, [4, 64, 12], [5, 64, 12], [6, 64, 12], [7, 64, 12]);
        c.Reconcile(w);
        var shown = GhostOf(c, new P3(4, 64, 12));
        Assert.Equal("clear", shown[6]!.GetValue<string>());
        Assert.Contains("holds stone", shown[8]!.GetValue<string>());
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal(["4,64,12", "5,64,12", "6,64,12"], Strs(j["clear"]).Order().ToList());
        // The pawn's dig was refused for good (protection): blocked now, said once for all such cells; the free cell still goes in.
        const string Refused = "can't clear the cell {0}: won't dig stone at {0}: a placed stone (someone's floor, wall or path)";
        c.Report(c.P(), Id(j), "progress", "cell 4,64,12 failed: " + string.Format(Refused, "4,64,12"), null);
        c.Report(c.P(), Id(j), "progress", "cell 5,64,12 failed: " + string.Format(Refused, "5,64,12"), null);
        Assert.Equal("blocked", b.Ghosts[0].State);
        Assert.Equal("blocked", b.Ghosts[1].State);
        Assert.Single(c.Alerts, a => a.Text.Contains("can't clear the cell"));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("is occupied by"));
    }

    [Theory]
    [InlineData("1.12.2", 102, 0)] // a glass pane
    [InlineData("1.12.2", 44, 0)]  // a bottom slab
    [InlineData("1.12.2", 53, 0)]  // stairs
    [InlineData("b1.8.1", 102, 0)]
    [InlineData("b1.8.1", 44, 0)]
    [InlineData("b1.8.1", 53, 0)]
    public void A_cell_whose_only_neighbour_is_a_stair_slab_or_pane_is_offered(string v, int under, int meta)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        w.Set(4, 64, 12, under, meta);
        Add(c, 5, 0, [4, 65, 12]);
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal(["4,65,12"], Strs(j["cells"]));
        Assert.Null(j["scaffold"]);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_cell_beside_nothing_clickable_is_never_offered_as_if_it_had_support(string v)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        w.Set(4, 64, 12, 50, 5); // a torch under it: nothing to click against (live: "nothing solid on any side (all air)")
        var b = Add(c, 5, 0, [4, 65, 12]);
        Assert.NotEqual("place", Pick(c)?["kind"]?.GetValue<string>());
        Assert.Equal("todo", b.Ghosts.Single().State);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_cell_over_another_builds_unplaced_block_waits_for_it_and_a_floating_one_gets_a_scaffold(string v)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        Add(c, 4, 0, [4, 64, 12]);                 // the footing: cobblestone, none anywhere (live: stone bricks short)
        var upper = Add(c, 5, 0, [4, 65, 12]);      // planks on it
        Assert.NotEqual("place", Pick(c)?["kind"]?.GetValue<string>()); // never "all air": it waits for the footing
        Assert.Equal("todo", upper.Ghosts.Single().State);
        w.Set(4, 64, 12, 4, 0);                     // the footing is in
        var j = Pick(c);
        Assert.Equal(["4,65,12"], Strs(j["cells"]));
        c.Report(c.P(), Id(j), "done", "batch: 0/1 placed", null);

        // A cell with nothing beside it and no build to wait for: a scaffold block under it, dug again once it stands.
        var (c2, _, _) = Rig(v, new Stack(5, 0, 8));
        Add(c2, 5, 0, [4, 65, 12]);
        var s = Pick(c2);
        Assert.Equal("place", s["kind"]!.GetValue<string>());
        Assert.Equal("4,64,12", s["scaffold"]!["4,65,12"]!.GetValue<string>());
        Assert.Equal(2, s["fetch"]!.AsArray().Sum(f => f!["n"]!.GetValue<int>())); // the block and its scaffold
    }

    /// <summary>A build's only cell on its lowest level is blocked: that level used to stay the build's level, so a higher cell with
    /// nothing to place against was never open and never got its scaffold ("the level below is still being built", for good).</summary>
    [Theory, MemberData(nameof(Versions))]
    public void A_floating_cell_above_a_blocked_lowest_level_gets_its_scaffold(string v)
    {
        var (c, _, _) = Rig(v, new Stack(5, 0, 8));
        var b = Add(c, 5, 0, [4, 64, 12], [8, 65, 12]);
        var low = b.Ghosts.Single(g => g.At.Y == 64);
        low.State = "blocked";
        low.Reason = "server undid my placement at 4,64,12 (protected area?)";
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal(["8,65,12"], Strs(j["cells"]));
        Assert.Equal("8,64,12", j["scaffold"]!["8,65,12"]!.GetValue<string>());
        Assert.Equal("blocked", low.State);
    }

    /// <summary>Another build's cell that no try is spent on (here a chest the game refuses beside a double chest) was waited for as
    /// support to come: a cell resting on it waited for good. It is now no support to come, so the cell gets a scaffold.</summary>
    [Theory, MemberData(nameof(Versions))]
    public void A_cell_that_leans_on_an_unbuildable_neighbour_gets_a_scaffold(string v)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        w.Set(3, 64, 12, 54, 2);
        w.Set(4, 64, 12, 54, 2); // a double chest
        w.Set(6, 64, 12, 1);     // something a scaffold beside the planks can stand on
        var chest = Add(c, 54, 0, [5, 64, 12]).Ghosts.Single();
        Assert.Equal("blocked", chest.State);
        Assert.True(StoryConstruction.IsChestRule(chest.Reason));
        Add(c, 5, 0, [5, 65, 12]); // planks over the chest cell: nothing else beside them
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal(["5,65,12"], Strs(j["cells"]));
        Assert.Equal("6,65,12", j["scaffold"]!["5,65,12"]!.GetValue<string>());
    }

    /// <summary>Live 11-05 after the reconcile went out: dirt floors reopened ×22 in 7 minutes as grass spread over them and pawns
    /// tilled them, and their clears were refused as a farm. Ground that drifted within its family is the design's block.</summary>
    [Theory, MemberData(nameof(Versions))]
    public void A_dirt_floor_that_grew_grass_or_was_tilled_stays_done_and_a_stone_in_it_still_reopens(string v)
    {
        var (c, _, w) = Rig(v, new Stack(3, 0, 8));
        int[][] cells = [[4, 64, 12], [5, 64, 12], [6, 64, 12], [7, 64, 12]];
        foreach (var p in cells) w.Set(p[0], p[1], p[2], 3, 0);
        var b = Add(c, 3, 0, cells);
        c.Reconcile(w);
        Assert.Equal("done", b.Status);
        w.Set(4, 64, 12, 2, 0);   // grass spread over it
        w.Set(5, 64, 12, 60, 7);  // tilled, wet
        if (v == "1.12.2") { w.Set(6, 64, 12, 3, 2); w.Set(7, 64, 12, 208, 0); } // podzol, a grass path
        for (int i = 0; i < 3; i++) c.Reconcile(w);
        Assert.All(b.Ghosts, g => Assert.Equal("done", g.State));
        Assert.Equal("done", b.Status);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("no longer match"));
        // A truly wrong block still reopens, and is cleared.
        w.Set(7, 64, 12, 1, 0);
        c.Reconcile(w);
        c.Reconcile(w);
        Assert.Equal("todo", b.Ghosts[3].State);
        Assert.Equal("clear", GhostOf(c, new P3(7, 64, 12))[6]!.GetValue<string>());
        Assert.Equal("done", b.Ghosts[0].State);
        var j = Pick(c);
        Assert.Equal(["7,64,12"], Strs(j["clear"]));
        // A lit furnace is the furnace the design drew (its front still counts).
        var (c2, _, w2) = Rig(v, new Stack(61, 0, 1));
        w2.Set(4, 64, 12, 62, 5);
        var f = Add(c2, 61, 5, [4, 64, 12]);
        c2.Reconcile(w2);
        Assert.Equal("done", f.Ghosts.Single().State);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_builds_reopening_is_alerted_at_most_once_per_window_with_a_running_count(string v)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        w.Set(4, 64, 12, 5, 0);
        w.Set(5, 64, 12, 5, 0);
        var b = Add(c, 5, 0, [4, 64, 12], [5, 64, 12]);
        c.Reconcile(w);
        void Break(int x) { w.Set(x, 64, 12, 0, 0); c.Reconcile(w); c.Reconcile(w); w.Set(x, 64, 12, 5, 0); c.Reconcile(w); }
        Break(4);
        Break(5);
        Break(4);
        Assert.Single(c.Alerts, a => a.Text.Contains("no longer match"));
        Assert.Equal("done", b.Status);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_finished_build_whose_block_was_broken_or_swapped_reopens(string v)
    {
        var (c, _, w) = Rig(v, new Stack(5, 0, 8));
        w.Set(4, 64, 12, 5, 0);
        w.Set(5, 64, 12, 5, 0);
        var b = Add(c, 5, 0, [4, 64, 12], [5, 64, 12]);
        c.Reconcile(w);
        Assert.Equal("done", b.Status);
        w.Set(5, 64, 12, 0, 0);  // broken
        w.Set(4, 64, 12, 3, 0);  // swapped for dirt
        c.Reconcile(w);
        c.Reconcile(w);
        Assert.All(b.Ghosts, g => Assert.Equal("todo", g.State));
        Assert.Equal("queued", b.Status);
        Assert.Contains(c.Alerts, a => a.Text.Contains("2 blocks no longer match") && a.Text.Contains("1 hold another block") && a.Text.Contains("1 missing"));
        var j = Pick(c);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Contains("4,64,12", Strs(j["clear"]).Concat(Strs(j["cells"])));
        // Put back by hand: done again on the next pass, and the build with it.
        c.Report(c.P(), Id(j), "done", "batch: 0/2 placed", null);
        w.Set(4, 64, 12, 5, 0);
        w.Set(5, 64, 12, 5, 0);
        c.Reconcile(w);
        Assert.All(b.Ghosts, g => Assert.Equal("done", g.State));
        Assert.Equal("done", b.Status);
    }
}
