using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Building fast (docs/web/story-mode.md "Building fast"; live 11-03: builders ~90% waiting, "busy: … is using chest" failing build
/// jobs, "too far" and "occupied by planks" for a built cell counted as failures): a build job is a batch of cells with one stand
/// and one fetch, builders split a build, each cell is reported, and "not now" is no failure.
/// </summary>
public sealed class StoryBuildFastTests
{
    private static readonly P3 ChestA = new(0, 64, 9), ChestB = new(2, 64, 9);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(int pawns, int planksA = 64, int planksB = 0)
    {
        var (c, h, w) = StoryRig.Make(pawns);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        int i = 0;
        foreach (var p in c.Pawns.Keys.ToList()) h.Lives[p] = FakeStoryHost.Live(i++, 64, 4);
        w.Set(ChestA.X, ChestA.Y, ChestA.Z, 54);
        w.Set(ChestB.X, ChestB.Y, ChestB.Z, 54);
        c.StorageOp("set", [ChestA, ChestB], null, null, null);
        if (planksA > 0) c.AddContents(c.StorageAt(ChestA)!, new Stack(5, 0, planksA));
        if (planksB > 0) c.AddContents(c.StorageAt(ChestB)!, new Stack(5, 0, planksB));
        return (c, h, w);
    }

    private static BuildState Floor(StoryColony c, int x1 = 4, int z1 = 12, int x2 = 9, int z2 = 17)
    {
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "floor", ["area"] = new JsonArray(x1, 64, z1, x2, 64, z2), ["material"] = "planks",
        }), null).Error);
        return c.Builds.Last();
    }

    private static List<string> Cells(JsonObject j) => j["cells"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
    private static P3 At(string s) { var a = s.Split(',').Select(int.Parse).ToArray(); return new P3(a[0], a[1], a[2]); }
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    [Fact]
    public void Three_builders_split_a_floor_into_disjoint_batches_each_with_a_stand_in_reach_and_one_fetch()
    {
        var (c, _, _) = Rig(3);
        var b = Floor(c);
        var jobs = Enumerable.Range(0, 3).Select(i => c.Next(i)).ToList();
        Assert.All(jobs, j => Assert.Equal("place", j["kind"]!.GetValue<string>()));
        var cells = jobs.Select(Cells).ToList();
        Assert.All(cells, cs => Assert.InRange(cs.Count, 1, 12)); // at most a fair share: 36 cells, 3 builders (what one stand reaches)
        Assert.True(cells.Sum(x => x.Count) >= 24, "most of the floor is out: " + string.Join(" / ", cells.Select(x => x.Count)));
        Assert.Equal(cells.Sum(x => x.Count), cells.SelectMany(x => x).Distinct().Count()); // disjoint
        foreach (var j in jobs)
        {
            Assert.Equal(j["at"]!.GetValue<string>(), Cells(j)[0]);
            Assert.Equal("planks", j["want"]!.GetValue<string>());
            // The cells in reach of the stand come first (the seed among them), then the ones a few steps on.
            var stand = At(j["stand"]!.GetValue<string>());
            var near = Cells(j).Select(At).Select(cell =>
            {
                Assert.NotEqual(cell, stand);
                return Math.Sqrt(Math.Pow(cell.X - stand.X, 2) + Math.Pow(cell.Y + 0.5 - (stand.Y + 1.62), 2) + Math.Pow(cell.Z - stand.Z, 2)) <= StoryColony.BuildReach;
            }).ToList();
            Assert.True(near[0], "the seed is in reach of the stand");
            Assert.Equal(near.OrderByDescending(x => x), near);
            Assert.True(near.Count(x => x) * 2 >= near.Count, "most of the batch is laid from the stand");
            Assert.Equal(Cells(j).Count, j["fetch"]!.AsArray().Sum(f => f!["n"]!.GetValue<int>())); // one fetch for the batch, never a whole stack
        }
        // Every cell is leased to its builder, and shows as being built.
        var leases = c.Leases();
        foreach (var (j, i) in jobs.Select((j, i) => (j, i)))
            foreach (var cell in Cells(j)) Assert.Equal(c.P(i), leases[$"g:{b.Id}|{cell}"]);
        Assert.Equal(cells.Sum(x => x.Count), b.Ghosts.Count(g => g.State == "leased"));
        Assert.Equal("active", b.Status);
    }

    [Fact]
    public void Fetches_spread_over_the_chests_and_count_what_others_are_still_on_their_way_to_take()
    {
        var (c, _, _) = Rig(2, planksA: 20, planksB: 20);
        Floor(c);
        var a = c.Next(0);
        var b = c.Next(1);
        string FromOf(JsonObject j) => j["fetch"]![0]!["from"]!.GetValue<string>();
        Assert.NotEqual(FromOf(a), FromOf(b)); // nobody queues at one chest while the other has them
        Assert.Equal(Cells(a).Count, a["fetch"]!.AsArray().Sum(f => f!["n"]!.GetValue<int>()));
        Assert.InRange(Cells(a).Count, 9, 18);
        // A third pawn wanting more than is left: it takes what remains, never what the first two are on their way to take.
        var (c3, _, _) = Rig(3, planksA: 14, planksB: 0);
        Floor(c3);
        var j1 = c3.Next(0);
        var j2 = c3.Next(1);
        int n1 = Cells(j1).Count;
        Assert.InRange(n1, 4, 12);
        Assert.Equal(n1, j1["fetch"]!.AsArray().Sum(f => f!["n"]!.GetValue<int>()));
        Assert.Equal(14 - n1, j2["fetch"]!.AsArray().Sum(f => f!["n"]!.GetValue<int>())); // only what is left after the first's fetch
        Assert.Equal(14 - n1, Cells(j2).Count);
        Assert.NotEqual("place", c3.Next(2)["kind"]!.GetValue<string>()); // nothing left to fetch: no job, and nothing blocked
        Assert.Equal("active", c3.Builds.Single().Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_builder_leaves_a_failed_chest_out_of_its_next_fetch(bool didntOpen, bool alternateStock)
    {
        var (c, _, _) = Rig(2, planksB: alternateStock ? 64 : 0);
        var build = Floor(c, 4, 12, 5, 13);
        var first = c.Next(0);
        Assert.Equal("place", first["kind"]!.GetValue<string>());
        Assert.Equal(ChestA.ToString(), first["fetch"]![0]!["from"]!.GetValue<string>());

        var why = didntOpen ? $"chest at {ChestA} didn't open (blocked above, or protected?)"
            : $"can't reach chest at {ChestA}: no walkable spot that sees it (no route)";
        Assert.True(c.Report(c.P(0), Id(first), "failed", why, null));
        Assert.All(build.Ghosts, g => Assert.Equal("todo", g.State));
        Assert.Empty(c.Leases());

        var next = c.Next(0); // the same instant: chest cooldown, not a target failure, must break the retry loop
        if (alternateStock)
        {
            Assert.Equal("place", next["kind"]!.GetValue<string>());
            Assert.Equal(first["at"]!.GetValue<string>(), next["at"]!.GetValue<string>());
            Assert.All(next["fetch"]!.AsArray(), f => Assert.Equal(ChestB.ToString(), f!["from"]!.GetValue<string>()));
        }
        else
        {
            Assert.NotEqual("place", next["kind"]!.GetValue<string>());
            Assert.All(build.Ghosts, g => Assert.Equal("todo", g.State));
        }

        // One pawn's failed route does not hide that chest from another builder.
        var other = c.Next(1);
        Assert.Equal("place", other["kind"]!.GetValue<string>());
        Assert.Equal(ChestA.ToString(), other["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.DoesNotContain(build.Ghosts, g => g.State == "blocked");
    }

    [Fact]
    public void Cells_are_reported_one_by_one_and_the_ledger_follows_the_fetch_not_the_end_of_the_batch()
    {
        var (c, _, w) = Rig(1);
        var b = Floor(c, 4, 12, 5, 13); // 4 cells
        var j = c.Next(0);
        var cells = Cells(j);
        Assert.Equal(4, cells.Count);
        string p = c.P();
        Assert.True(c.Report(p, Id(j), "progress", "fetched", null));
        Assert.Equal(60, c.StorageAt(ChestA)!.Contents.Single().Count); // taken now
        var first = At(cells[0]);
        w.Set(first.X, first.Y, first.Z, 5);
        c.Report(p, Id(j), "progress", $"cell {cells[0]} done", null);
        Assert.Equal("done", b.Ghosts.Single(g => g.At == first).State);
        Assert.True(c.Leases().ContainsKey($"g:{b.Id}|{cells[1]}")); // the rest are still its own
        // Out of reach from where it got to: not now, not a failure (no block, no alert); the cell rests a little.
        c.Report(p, Id(j), "progress", $"cell {cells[1]} failed: can't place at {cells[1]}: its nearest support face is 8.7 blocks from my eyes at 1,64,4 (reach 4.5) and I couldn't get closer", null);
        var second = b.Ghosts.Single(g => g.At == At(cells[1]));
        Assert.Equal("todo", second.State);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("can't place"));
        // The batch ends with two cells unreported: one the world shows built (done), one not (back to the queue, no failure).
        var third = At(cells[2]);
        w.Set(third.X, third.Y, third.Z, 5);
        c.Report(p, Id(j), "done", "batch: 2/4 placed", null);
        Assert.Equal("done", b.Ghosts.Single(g => g.At == third).State);
        Assert.Equal("todo", b.Ghosts.Single(g => g.At == At(cells[3])).State);
        Assert.Empty(c.Leases().Keys.Where(k => k.StartsWith("g:")));
        Assert.Equal(60, c.StorageAt(ChestA)!.Contents.Single().Count); // not taken twice at the end
        var next = c.Next(0);
        Assert.Contains(cells[3], Cells(next)); // the open one comes again; the resting one later
        Assert.DoesNotContain(cells[1], Cells(next));
    }

    [Fact]
    public void A_cell_that_is_already_built_is_done_never_a_failure()
    {
        var (c, _, w) = Rig(1);
        var b = Floor(c, 4, 12, 5, 12); // 2 cells
        w.Set(5, 64, 12, 5); // built before anyone got to it
        var j = c.Next(0);
        Assert.Equal(["4,64,12"], Cells(j));
        Assert.Equal("done", b.Ghosts.Single(g => g.At == new P3(5, 64, 12)).State);
        // The pawn found its cell taken by the right block (the colony's view was a moment behind).
        w.Set(4, 64, 12, 5);
        c.Report(c.P(), Id(j), "progress", "cell 4,64,12 failed: 4,64,12 is occupied by planks", null);
        Assert.Equal("done", b.Status);
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
    }

    [Fact]
    public void A_busy_chest_is_not_a_failure_and_a_probe_for_work_claims_no_cell()
    {
        var (c, h, _) = Rig(1);
        var b = Floor(c, 4, 12, 5, 12);
        var p = c.Pawns.Values.Single();
        // The idle-wake check asks the picker whether there is work: that must not mark cells leased (live: a build's last cells hung).
        Assert.NotNull(c.Candidate(WorkTypes.Build, p, h.Live(p.Name)));
        Assert.All(b.Ghosts, g => Assert.Equal("todo", g.State));
        Assert.Empty(c.Leases());
        for (int i = 0; i < 2; i++)
        {
            var j = c.Next(0);
            Assert.Equal("place", j["kind"]!.GetValue<string>());
            c.Report(p.Name, Id(j), "failed", "busy: maid_chan_102 is using chest 0,64,9; waited 12s", null);
            Assert.All(b.Ghosts, g => Assert.Equal("todo", g.State));
            // The seed rests a little (no fail count); the next pick has the other cell.
        }
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
        Assert.NotEqual("resting: the last jobs failed", c.Next(0)["label"]?.GetValue<string>()); // the pawn isn't sent to rest either
    }

    [Fact]
    public void A_wall_batch_is_one_level_and_its_builder_stands_outside()
    {
        var (c, h, _) = Rig(1);
        h.Lives[c.P()] = FakeStoryHost.Live(12, 64, 12);
        c.AddContents(c.StorageAt(ChestA)!, new Stack(4, 0, 64));
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "wall", ["area"] = new JsonArray(10, 64, 10, 13, 64, 13), ["material"] = "cobblestone", ["height"] = 3,
        }), null).Error);
        var j = c.Next(0);
        var cells = Cells(j).Select(At).ToList();
        Assert.InRange(cells.Count, 4, 12);
        Assert.All(cells, x => Assert.Equal(64, x.Y)); // one level at a time: the next goes up once this one stands
        var stand = At(j["stand"]!.GetValue<string>());
        Assert.False(stand.X > 10 && stand.X < 13 && stand.Z > 10 && stand.Z < 13, $"stand {stand} is inside the walls");
    }
}
