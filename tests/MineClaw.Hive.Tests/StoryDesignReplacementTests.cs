using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryDesignReplacementTests
{
    private static JsonObject Block(int id, params int[][] cells) => new()
    {
        ["kind"] = "block", ["material"] = new JsonArray(id, 0),
        ["blocks"] = new JsonArray(cells.Select(p => (JsonNode)new JsonArray(p[0], p[1], p[2])).ToArray()),
    };

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Palette_volume_clears_interior_and_permanently_releases_only_overlapping_old_cells(string version)
    {
        IGameData data = version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;
        var (c, h, w) = StoryRig.Make(1, data);
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(5, 0, 20), new Stack(4, 0, 20)]);
        w.Set(5, 64, 12, 54); // a chest inside the new room, absent from its sparse palette data
        Assert.Null(c.BuildsOp("add", new JsonArray(Block(5, [4, 64, 12], [5, 64, 12], [8, 64, 12])), null).Error);
        var old = c.Builds.Single();
        var active = c.Next();
        Assert.Equal("place", active["kind"]!.GetValue<string>());
        var replacement = Block(4, [4, 64, 12], [6, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 6, 65, 12);
        var added = c.BuildsOp("add", new JsonArray(replacement), null);
        Assert.Null(added.Error);
        Assert.Single(old.Ghosts);
        Assert.Equal(new P3(8, 64, 12), old.Ghosts[0].At);
        Assert.Contains(h.Interrupted, s => s.Contains("replaced by a new design"));
        var dig = c.Builds.Single(b => b.Kind == "remove");
        Assert.Equal(new P3(5, 64, 12), Assert.Single(dig.Ghosts.Where(g => g.State != "done")).At);
        Assert.True(dig.Spec!["replacement_clear"]!.GetValue<bool>());
        Assert.Equal(dig.Spec["group"]!.ToString(), c.Builds.Last().Spec!["group"]!.ToString());
        var ids = added.Result!["ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Null(c.BuildsOp("remove", null, ids).Error);
        for (int i = 0; i < 3; i++) c.Reconcile(w);
        Assert.Single(old.Ghosts); // removing the new design never resurrects the old room
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.At.X is 4 or 5 or 6);
    }

    [Fact]
    public void A_canceled_builders_late_atomic_placement_is_removed_from_intentional_air_and_hidden_again_when_done()
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(5, 0, 20), new Stack(4, 0, 20)]);
        Assert.Null(c.BuildsOp("add", new JsonArray(Block(5, [5, 64, 12])), null).Error);
        var old = c.Next();
        Assert.Equal("place", old["kind"]!.GetValue<string>());
        var replacement = Block(4, [4, 64, 12], [6, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 6, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        var airBuild = c.Builds.Single(b => b.Kind == "remove");
        var air = Assert.Single(airBuild.Ghosts);
        Assert.Equal("done", air.State);
        Assert.False(c.Holds(c.P(), old["id"]!.GetValue<string>()));
        Assert.DoesNotContain(c.State()["ghosts"]!["blocks"]!.AsArray(), g => g![5]!.GetValue<string>() == airBuild.Id);

        // The already-sent block packet lands after superseding the old job. Its final report is stale. The client logs the block
        // that appeared where it just clicked, cut or not (MinecraftClient.cs): the hive's placed-block log puts it on the pawn.
        w.Set(5, 64, 12, 5);
        h.Placed.Add((new P3(5, 64, 12), 5, c.P()));
        Assert.False(c.Report(c.P(), old["id"]!.GetValue<string>(), "done", null, null));
        c.Reconcile(w);
        Assert.Equal("done", air.State);
        c.Reconcile(w);
        Assert.Equal("todo", air.State);
        Assert.Equal(5, air.Id);
        var state = c.State();
        Assert.Contains(state["ghosts"]!["blocks"]!.AsArray(), g => g![5]!.GetValue<string>() == airBuild.Id);
        long since = state["rev"]!.GetValue<long>();
        var clear = c.Next();
        Assert.Equal("dig", clear["kind"]!.GetValue<string>());
        Assert.Equal("5,64,12", clear["at"]!.GetValue<string>());
        Assert.True(clear["replace"]!.GetValue<bool>());
        w.Set(5, 64, 12, 0);
        Assert.True(c.Report(c.P(), clear["id"]!.GetValue<string>(), "done", null, null));
        Assert.Equal("done", air.State);
        Assert.Contains(c.State(since)["removed"]!["ghosts"]!.AsArray(), g => g!.GetValue<string>() == $"{airBuild.Id}|5,64,12");
        Assert.DoesNotContain(c.State()["ghosts"]!["blocks"]!.AsArray(), g => g![5]!.GetValue<string>() == airBuild.Id);
        Assert.Single(airBuild.Ghosts); // owned internally after its map marker disappears
    }

    /// <summary>A finished design's room (1.12.2) with a chest at 4,64,12 and empty space at 5 and 6: one pawn that only builds.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, GhostState Air) FinishedRoom(ManualTime? clock = null)
    {
        var (c, h, w) = StoryRig.Make(1, Release1122GameData.Instance, clock);
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(4, 0, 20)]);
        w.Set(4, 64, 12, 54, 4);
        var room = Block(54, [4, 64, 12]);
        room["material"] = new JsonArray(54, 4);
        room["replace_area"] = new JsonArray(4, 64, 12, 6, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(room), null).Error);
        c.Reconcile(w);
        Assert.All(c.Builds, b => Assert.Equal("done", b.Status));
        var air = c.Builds.Single(b => b.Kind == "remove").Ghosts.Single(g => g.At == new P3(5, 64, 12));
        Assert.Equal("done", air.State);
        return (c, h, w, air);
    }

    /// <summary>Every block that appeared in a finished design's empty space was dug out, for good, whoever put it there: a storage
    /// sign the owner hung on a chest of the storehouse too (C11). Only what the hive's own bots placed is removed now.</summary>
    [Fact]
    public void A_block_the_owner_put_into_a_designs_room_is_left_alone()
    {
        var (c, _, w, air) = FinishedRoom();
        w.Set(5, 64, 12, 68, 5); // a wall sign on the chest's east side
        for (int i = 0; i < 3; i++) c.Reconcile(w);
        Assert.Equal("done", air.State);
        Assert.NotEqual("dig", c.Next()["kind"]?.GetValue<string>());
        Assert.Equal(68, w.Block(5, 64, 12).Id);
    }

    /// <summary>The owner's block stays for good, so every 5 s pass read the whole placed-block log again (every body's, thousands of
    /// rows) under the colony lock. The same block in the same cell is answered from the last read for a minute; a new block reads it.</summary>
    [Fact]
    public void The_owners_block_in_a_designs_room_reads_the_placed_block_log_once_a_minute()
    {
        var clock = new ManualTime();
        var (c, h, w, air) = FinishedRoom(clock);
        w.Set(5, 64, 12, 68, 5); // the owner's sign
        int before = h.PlacedReads;
        for (int i = 0; i < 11; i++)
        {
            c.Reconcile(w);
            clock.Now += TimeSpan.FromSeconds(5);
        }
        Assert.Equal(before + 1, h.PlacedReads);
        clock.Now += TimeSpan.FromSeconds(5); // a minute since that read
        c.Reconcile(w);
        Assert.Equal(before + 2, h.PlacedReads);
        Assert.Equal("done", air.State);
        // A pawn's block in the next cell is new to the colony: the log is read at once, and the block is taken out.
        w.Set(6, 64, 12, 4);
        h.Placed.Add((new P3(6, 64, 12), 4, c.P()));
        c.Reconcile(w);
        Assert.Equal(before + 3, h.PlacedReads);
        c.Reconcile(w);
        Assert.Equal(before + 3, h.PlacedReads);
        Assert.Equal("todo", c.Builds.Single(b => b.Kind == "remove").Ghosts.Single(g => g.At == new P3(6, 64, 12)).State);
        Assert.Equal("done", air.State); // the owner's sign stays
    }

    [Fact]
    public void A_pawns_late_block_in_a_designs_room_is_still_removed()
    {
        var (c, h, w, air) = FinishedRoom();
        w.Set(5, 64, 12, 4);
        h.Placed.Add((new P3(5, 64, 12), 4, c.P()));
        c.Reconcile(w);
        Assert.Equal("done", air.State); // seen once: it may be a moment's view
        c.Reconcile(w);
        Assert.Equal("todo", air.State);
        var dig = c.Next();
        Assert.Equal("dig", dig["kind"]!.GetValue<string>());
        Assert.Equal("5,64,12", dig["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_later_ordinary_build_owns_the_cell_instead_of_fighting_the_older_air_claim()
    {
        var (c, _, w) = StoryRig.Make();
        var replacement = Block(4, [4, 64, 12], [6, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 6, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        var air = Assert.Single(c.Builds.Single(b => b.Kind == "remove").Ghosts);
        Assert.Null(c.BuildsOp("add", new JsonArray(Block(5, [5, 64, 12])), null).Error);
        var later = c.Builds.Last();
        Assert.Equal("blocked", air.State);
        Assert.Contains("superseded by build " + later.Id, air.Reason);
        w.Set(5, 64, 12, 5);
        for (int i = 0; i < 3; i++) c.Reconcile(w);
        Assert.Equal("blocked", air.State);
        Assert.Equal("done", Assert.Single(later.Ghosts).State);

        Assert.Null(c.BuildsOp("remove", null, [later.Id]).Error);
        c.Reconcile(w);
        Assert.Equal("todo", air.State); // removing the newer directive restores the intentionally empty room
    }

    /// <summary>A placed design's empty-space cell that a later build took over, with that build's block standing in it.</summary>
    private static (StoryColony C, FakeStoryWorld W, BuildState AirBuild, GhostState Air, BuildState Later) SupersededAir()
    {
        var (c, _, w) = StoryRig.Make();
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        var replacement = Block(4, [4, 64, 12], [6, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 6, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        var airBuild = c.Builds.Single(b => b.Kind == "remove");
        var air = Assert.Single(airBuild.Ghosts);
        Assert.Null(c.BuildsOp("add", new JsonArray(Block(5, [5, 64, 12])), null).Error);
        var later = c.Builds.Last();
        w.Set(5, 64, 12, 5);
        Assert.Equal("blocked", air.State);
        return (c, w, airBuild, air, later);
    }

    /// <summary>Retry reopened every cell that was not done, superseded ones too, and ran no conflict pass: until the next reconcile a
    /// builder was handed the older design's empty-space cell as a dig that ignores protection, and removed the newer build's block.</summary>
    [Fact]
    public void Retry_never_reopens_a_superseded_cell()
    {
        var (c, w, airBuild, air, later) = SupersededAir();
        Assert.Equal(0, c.FailedCells()); // superseded is not failed: "Retry failed cells (N)" has nothing to count

        Assert.Equal(0, c.RetryBuilds("retry_all", null, null).Result!["reopened"]!.GetValue<int>());
        Assert.Equal(0, c.RetryBuilds("retry", [airBuild.Id], [air.At]).Result!["reopened"]!.GetValue<int>());
        Assert.Equal("blocked", air.State);
        Assert.Contains("superseded by build " + later.Id, air.Reason);
        var job = c.Next();
        Assert.NotEqual("dig", job["kind"]?.GetValue<string>());
        Assert.Equal(5, w.Block(5, 64, 12).Id);
        Assert.Equal(0, c.FailedCells());

        // The same once a pass has run (the older build then counts as done) and the retry names the build and the cell.
        c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null);
        c.Reconcile(w);
        Assert.Equal(0, c.RetryBuilds("retry", [airBuild.Id], [air.At]).Result!["reopened"]!.GetValue<int>());
        Assert.Equal("blocked", air.State);
        Assert.NotEqual("dig", c.Next()["kind"]?.GetValue<string>());
    }

    [Fact]
    public void Removing_the_newer_build_gives_the_older_cell_back_as_work()
    {
        var (c, w, airBuild, air, later) = SupersededAir();
        c.Reconcile(w);
        Assert.Equal("done", airBuild.Status); // only a superseded cell is left of it: no failure, nothing to do
        Assert.Equal("blocked", air.State);

        Assert.Null(c.BuildsOp("remove", null, [later.Id]).Error);
        c.Reconcile(w);
        Assert.Equal("todo", air.State);
        Assert.Equal("queued", airBuild.Status); // a done build is scanned by no pawn: the cell would wait for good
        var dig = c.Next();
        Assert.Equal("dig", dig["kind"]!.GetValue<string>());
        Assert.Equal("5,64,12", dig["at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Intentional_air_keeps_active_temporary_access_registered_until_its_owner_releases_it(bool beforeReplacement)
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(time: clock);
        var at = new P3(5, 64, 12);
        h.Lives[c.P()] = FakeStoryHost.Live(5.5, 65, 12.5);
        void AddPole()
        {
            w.Set(at.X, at.Y, at.Z, 3);
            h.Placed.Add((at, 3, c.P())); // the pawn's own block: the placed-block log has it
            h.Temps[c.P()] = [(at, 3)];
            c.Temporary(c.P(), at.ToString(), true, "reach pillar");
        }
        if (beforeReplacement) AddPole();
        var replacement = Block(4, [4, 64, 12], [6, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 6, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        var air = Assert.Single(c.Builds.Single(b => b.Kind == "remove").Ghosts);
        if (!beforeReplacement) AddPole();
        for (int i = 0; i < 3; i++) { clock.Now += TimeSpan.FromSeconds(6); c.Tick(); }
        Assert.Equal("done", air.State);
        Assert.Contains(c.Meta.TempBlocks!, t => t.At == at);
        Assert.Contains(h.Temps[c.P()], t => t.At == at);
        Assert.DoesNotContain(c.Designations, d => d.At == at);

        h.Temps[c.P()] = [];
        c.Temporary(c.P(), at.ToString(), false, "reach pillar");
        // If ownership ends but a block remains, the room's air claim is responsible again.
        c.Reconcile(w);
        c.Reconcile(w);
        Assert.Equal("todo", air.State);
    }

    [Fact]
    public void A_replacement_releases_abandoned_canceled_and_deferred_temp_blocks_from_both_ledgers()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(time: clock);
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(4, 0, 20)]);
        for (int x = 5; x <= 7; x++)
        {
            var at = new P3(x, 64, 12);
            w.Set(x, 64, 12, 3);
            c.Temporary(c.P(), at.ToString(), true, "reach pillar");
            h.Placed.Add((at, 3, c.P())); // the startup stray scan must not recreate ownership either
        }
        h.Temps[c.P()] = c.Meta.TempBlocks!.Select(t => (t.At, t.Id)).ToList();
        c.Meta.TempBlocks![1].Cancelled = true;
        c.Meta.TempBlocks[2].DeferredReason = "out of reach";
        c.Meta.TempBlocks[2].Misses = 2;
        var replacement = Block(4, [4, 64, 12], [8, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 8, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        Assert.Null(c.Meta.TempBlocks);
        Assert.Empty(h.Temps[c.P()]);
        var air = c.Builds.Single(b => b.Kind == "remove").Ghosts;
        Assert.Equal(3, air.Count);
        Assert.All(air, g => Assert.Equal("todo", g.State));
        clock.Now += TimeSpan.FromSeconds(6);
        c.Tick();
        Assert.Null(c.Meta.TempBlocks);
        Assert.DoesNotContain(c.Designations, d => d.Auto && d.At is { X: >= 5 and <= 7, Y: 64, Z: 12 });
        var clear = c.Next();
        Assert.Equal("dig", clear["kind"]!.GetValue<string>());
        Assert.True(clear["replace"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("reach pillar", false)]
    [InlineData("scaffold", false)]
    [InlineData("scaffold", true)]
    public void A_prior_keep_flag_yields_to_replacement_but_the_pawn_can_first_leave_its_active_pillar(string kind, bool borrowed)
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(borrowed ? 2 : 1, time: clock);
        var at = new P3(5, 64, 12);
        for (int y = 64; y <= 73; y++) w.Set(at.X, y, at.Z, 3);
        var standing = borrowed ? c.P(1) : c.P();
        h.Lives[standing] = FakeStoryHost.Live(5.5, 74, 12.5);
        c.Temporary(c.P(), at.ToString(), true, kind);
        var temp = Assert.Single(c.Meta.TempBlocks!);
        temp.Cancelled = true;
        temp.DeferredReason = "out of reach";
        temp.Misses = 2;
        temp.RetryAfter = clock.Now + TimeSpan.FromHours(1);
        var replacement = Block(4, [4, 64, 12], [6, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 6, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        var air = Assert.Single(c.Builds.Single(b => b.Kind == "remove").Ghosts);
        Assert.Equal("done", air.State);
        Assert.Same(temp, Assert.Single(c.Meta.TempBlocks!));
        Assert.Equal(standing, temp.Pawn);
        Assert.False(temp.Cancelled);
        Assert.Null(temp.DeferredReason);
        Assert.Null(temp.RetryAfter);
        Assert.Equal(0, temp.Misses);
        c.Reconcile(w);
        c.Reconcile(w);
        Assert.Equal("done", air.State);
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        Assert.DoesNotContain(c.Designations, d => d.Auto && d.At == at);

        h.Lives[standing] = FakeStoryHost.Live(40, 64, 40);
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        Assert.Contains(c.Designations, d => d.Auto && d.Kind == "deconstruct" && d.At == at);
    }

    [Fact]
    public void Solid_replacement_waits_for_active_access_to_be_recovered_even_when_the_material_matches()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(time: clock);
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        var at = new P3(5, 64, 12);
        w.Set(at.X, at.Y, at.Z, 3);
        h.Lives[c.P()] = FakeStoryHost.Live(5.5, 65, 12.5, inv: [new Stack(3, 0, 20)]);
        h.Temps[c.P()] = [(at, 3)];
        c.Temporary(c.P(), at.ToString(), true, "reach pillar");
        var replacement = Block(3, [at.X, at.Y, at.Z]);
        replacement["replace_area"] = new JsonArray(at.X, at.Y, at.Z, at.X, at.Y, at.Z);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        var ghost = Assert.Single(c.Builds.Single().Ghosts);
        Assert.Equal("todo", ghost.State); // a temporary block is not yet the permanent design, even with the same id
        for (int i = 0; i < 3; i++) { clock.Now += TimeSpan.FromSeconds(6); c.Tick(); }
        Assert.Equal("todo", ghost.State);
        Assert.Contains(c.Meta.TempBlocks!, t => t.At == at);
        Assert.Contains(h.Temps[c.P()], t => t.At == at);
        var waiting = c.Next();
        Assert.DoesNotContain(waiting["kind"]!.GetValue<string>(), new[] { "place", "dig" });
        c.Report(c.P(), waiting["id"]!.GetValue<string>(), "done", null, null);

        w.Set(at.X, at.Y, at.Z, 0);
        h.Temps[c.P()] = [];
        c.Temporary(c.P(), at.ToString(), false, "reach pillar");
        h.Lives[c.P()] = FakeStoryHost.Live(5.5, 64, 12.5, inv: [new Stack(3, 0, 20)]);
        c.Reconcile(w);
        var placing = c.Next();
        Assert.Equal("place", placing["kind"]!.GetValue<string>());
        Assert.Equal(at.ToString(), placing["at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void A_neighboring_replacement_seed_cannot_adopt_or_clear_an_occupied_access_cell(int wanted)
    {
        var (c, h, w) = StoryRig.Make(2);
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        var held = new P3(5, 64, 12);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(3, 0, 20), new Stack(4, 0, 20)]);
        h.Lives[c.P(1)] = FakeStoryHost.Live(5.5, 65, 12.5);
        w.Set(held.X, held.Y, held.Z, 3);
        if (wanted == 4) w.Set(4, 64, 12, 3); // both cells need the same clear; an unguarded batch would include both
        c.Temporary(c.P(1), held.ToString(), true, "reach pillar");
        var replacement = Block(wanted, [4, 64, 12], [5, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 5, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal("4,64,12", Assert.Single(job["cells"]!.AsArray())!.GetValue<string>());
        var access = c.Builds.Single().Ghosts.Single(g => g.At == held);
        Assert.Equal("todo", access.State); // matching temporary dirt is not prematurely accepted as the permanent design
        Assert.Contains(c.Meta.TempBlocks!, t => t.At == held);
    }

    /// <summary>Inside a placed design every cell is a ghost (its blocks or its empty space), and no scaffold went on any build's cell:
    /// a block with nothing under it but the design's own cleared empty space waited for good.</summary>
    [Fact]
    public void A_scaffold_may_stand_in_the_designs_own_empty_space()
    {
        var (c, h, _) = StoryRig.Make();
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(5, 0, 20)]);
        var design = Block(5, [4, 65, 12]);
        design["replace_area"] = new JsonArray(4, 64, 12, 4, 65, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(design), null).Error);
        var air = Assert.Single(c.Builds.Single(b => b.Kind == "remove").Ghosts);
        Assert.Equal(("done", new P3(4, 64, 12)), (air.State, air.At)); // the empty space under the block, already empty
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal("4,64,12", job["scaffold"]!["4,65,12"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_replacement_is_atomic_and_unloaded_interior_is_not_treated_as_empty()
    {
        var (c, _, w) = StoryRig.Make();
        Assert.Null(c.BuildsOp("add", new JsonArray(Block(5, [4, 64, 12])), null).Error);
        var old = c.Builds.Single();
        var bad = Block(4, [4, 64, 12]);
        bad["replace_area"] = new JsonArray(0, 1, 0, 100000, 100, 100000);
        Assert.NotNull(c.BuildsOp("add", new JsonArray(bad), null).Error);
        Assert.Same(old, Assert.Single(c.Builds));
        Assert.Single(old.Ghosts);
        w.Unloaded.Add((5, 12));
        var next = Block(4, [4, 64, 12], [6, 64, 12]);
        next["replace_area"] = new JsonArray(4, 64, 12, 6, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(next), null).Error);
        var clear = Assert.Single(c.Builds.Single(b => b.Kind == "remove").Ghosts);
        Assert.Equal(new P3(5, 64, 12), clear.At);
        Assert.Equal("todo", clear.State);
    }

    [Fact]
    public void Ordinary_upper_wall_job_enables_shared_temporary_access()
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(4, 0, 24), new Stack(3, 0, 24)]);
        for (int y = 64; y < 70; y++) w.Set(4, y, 12, 4);
        Assert.Null(c.BuildsOp("add", new JsonArray(Block(4, [4, 70, 12], [5, 70, 12])), null).Error);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.True(job["reach"]!.GetValue<bool>());
        Assert.Equal(2, job["cells"]!.AsArray().Count);
    }

    [Fact]
    public void Building_material_does_not_hide_the_need_for_a_climbing_supply_hint()
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(4, 0, 8)]);
        var chest = new P3(0, 64, 9);
        c.StorageOp("set", [chest], null, null, null);
        c.AddContents(c.StorageAt(chest)!, new Stack(3, 0, 32));
        for (int y = 64; y < 70; y++) w.Set(4, y, 12, 4);
        Assert.Null(c.BuildsOp("add", new JsonArray(Block(4, Enumerable.Range(4, 8).Select(x => new[] { x, 70, 12 }).ToArray())), null).Error);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal("dirt", job["pillar"]!["item"]!.GetValue<string>());
        Assert.True(job["pillar"]!["n"]!.GetValue<int>() >= 8);
    }

    [Fact]
    public void Replacement_cancels_inflight_automatic_cleanup_in_its_volume()
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        w.Set(4, 64, 12, 4);
        c.Designations.Add(new DesignationState { Id = "old-cleanup", Auto = true, Kind = "deconstruct", At = new P3(4, 64, 12) });
        var active = c.Next();
        Assert.Equal("dig", active["kind"]!.GetValue<string>());
        var replacement = Block(4, [4, 64, 12]);
        replacement["replace_area"] = new JsonArray(4, 64, 12, 4, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(replacement), null).Error);
        Assert.Empty(c.Designations);
        Assert.False(c.Holds(c.P(), active["id"]!.GetValue<string>()));
        Assert.Contains(h.Interrupted, s => s.Contains("replaced by a new design"));
        Assert.Equal("done", c.Builds.Single().Ghosts.Single().State);
    }
}
