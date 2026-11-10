using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Temporary blocks (docs/web/story-mode.md "Temporary blocks"). Fox 11-05: "I'm seeing a lot of stray planks have been placed down as
/// well" and "Bots should delete whatever they put down unless it was a directive to build it. So, nerd poles, other blocks. If a bot
/// can't navigate something without deleting its nerd pole, then it should teleport." Live 11-04: maid_chan_67's box of 9 oak planks at
/// 193,37,430 stayed when it died beside it; 2-high plank stubs and walks' pillars stood around the colony.
/// </summary>
public sealed class StoryTempBlocksTests
{
    private static void Only(StoryColony c, params string[] types)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static List<DesignationState> Cleanups(StoryColony c) =>
        c.Designations.Where(d => d.Kind == "deconstruct" && d.Auto && (d.Label ?? "").Contains("temporary")).ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancelling_cleanup_interrupts_its_pawn_and_keeps_the_block_across_ticks_and_reload(bool byArea)
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Build);
        var pawn = c.P();
        var at = new P3(5, 64, 5);
        w.Set(at.X, at.Y, at.Z, 4);
        h.Temps[pawn] = [(at, 4)];
        h.Placed.Add((at, 4, pawn));
        c.Temporary(pawn, at.ToString(), true, "scaffold");
        h.Lives[pawn] = FakeStoryHost.Live(40, 64, 40);
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        var mark = Assert.Single(Cleanups(c));
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());

        var result = c.Designate("cancel", null, byArea ? Area.Of(5, 64, 5, 5, 64, 5) : null,
            null, null, byArea ? null : [mark.Id]);
        Assert.Null(result.Error);
        Assert.Equal(1, result.Result!["count"]!.GetValue<int>());
        Assert.Null(c.CurrentOf(pawn));
        Assert.Contains(h.Interrupted, s => s.StartsWith(pawn + ": task cancelled"));
        Assert.Empty(c.Leases());
        Assert.Empty(h.Temps[pawn]);
        Assert.True(Assert.Single(c.Meta.TempBlocks!).Cancelled);
        Assert.False(c.Report(pawn, job["id"]!.GetValue<string>(), "done", null, null));
        clock.Now += TimeSpan.FromMinutes(2);
        c.Tick();
        Assert.Empty(Cleanups(c));
        Assert.Equal(4, w.Block(at.X, at.Y, at.Z).Id);

        var saved = System.Text.Json.JsonSerializer.Serialize(c.Meta);
        var meta = System.Text.Json.JsonSerializer.Deserialize<ColonyMeta>(saved)!;
        var reloaded = new StoryColony(meta, h, clock);
        reloaded.Joined([pawn]);
        reloaded.Tick();
        Assert.Empty(Cleanups(reloaded)); // the placed-block scan cannot recreate the cancelled job
        Assert.True(Assert.Single(reloaded.Meta.TempBlocks!).Cancelled);
        // An explicit new order can still remove it.
        reloaded.Designate("add", "mine", null, [at], "all");
        Assert.Contains(reloaded.Designations, d => d.At == at && !d.Auto);
    }

    [Fact]
    public void Cancelling_manual_mining_stops_the_active_dig_and_leaves_other_tasks_alone()
    {
        var (c, h, w) = StoryRig.Make();
        Only(c, WorkTypes.Mine);
        w.Set(2, 64, 2, 3);
        w.Set(4, 64, 4, 3);
        c.Designate("add", "mine", null, [new P3(2, 64, 2), new P3(4, 64, 4)], "all");
        var job = c.Next();
        var current = c.CurrentOf(c.P())!;
        c.Designate("cancel", null, null, null, null, [current.SourceId!]);
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Single(c.Designations);
        Assert.NotEqual(job["at"]!.GetValue<string>(), c.Next()["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_pillar_left_by_a_cut_job_becomes_a_cleanup_job_said_once_and_goes_with_its_blocks()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Build);
        var pawn = c.P();
        for (int y = 64; y <= 66; y++) { w.Set(5, y, 5, 5); c.Temporary(pawn, $"5,{y},5", true, "reach pillar"); }
        c.Tick();
        Assert.Empty(Cleanups(c)); // its pawn is still by it: it takes it back itself
        // The job was cut and the pawn went off elsewhere: the pillar is left behind.
        h.Lives[pawn] = FakeStoryHost.Live(40, 64, 40);
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        var marks = Cleanups(c);
        Assert.Equal(3, marks.Count);
        Assert.All(marks, m => Assert.Contains("reach pillar", m.Label));
        Assert.Single(c.Alerts, a => a.Text.Contains("temporary block(s) the pawns left behind") && a.Text.Contains("3 "));
        // Any builder takes it: a dig at the pillar, top first.
        var j = c.Next();
        Assert.Equal("dig", j["kind"]!.GetValue<string>());
        Assert.Equal("5,66,5", j["at"]!.GetValue<string>());
        // Dug: the registry and the marks go.
        for (int y = 64; y <= 66; y++) w.Set(5, y, 5, 0);
        clock.Now += TimeSpan.FromSeconds(5);
        c.Tick();
        Assert.Empty(Cleanups(c));
        Assert.Null(c.Meta.TempBlocks);
        Assert.Single(c.Alerts, a => a.Text.Contains("temporary block(s)"));
    }

    [Fact]
    public void A_pawn_that_takes_its_pillar_back_leaves_nothing_queued()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        var pawn = c.P();
        w.Set(5, 64, 5, 3);
        c.Temporary(pawn, "5,64,5", true, "build scaffold");
        c.Temporary(pawn, "5,64,5", false, "build scaffold");
        w.Set(5, 64, 5, 0);
        h.Lives[pawn] = FakeStoryHost.Live(40, 64, 40);
        clock.Now += TimeSpan.FromSeconds(60);
        c.Tick();
        Assert.Empty(Cleanups(c));
        Assert.Null(c.Meta.TempBlocks);
    }

    [Fact]
    public void An_offline_pawns_shelter_box_from_its_client_is_queued_at_once_and_a_build_cell_never_is()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        var pawn = c.P();
        // The body's own client remembers a box it put up (oak planks) and a walk's pillar step that is a build's cell now.
        w.Set(7, 65, 7, 5);
        w.Set(9, 64, 9, 4);
        h.Temps[pawn] = [(new P3(7, 65, 7), 5), (new P3(9, 64, 9), 4)];
        c.Builds.Add(new BuildState { Id = "b1", Kind = "block", Status = "done", Ghosts = { new GhostState { At = new P3(9, 64, 9), Id = 4 } } });
        c.Tick();
        h.Lives[pawn] = PawnLive.Offline; // it died by its box, or logged out
        h.Temps.Remove(pawn);
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        var mark = Assert.Single(Cleanups(c));
        Assert.Equal(new P3(7, 65, 7), mark.At);
        Assert.DoesNotContain(c.Meta.TempBlocks ?? [], t => t.At == new P3(9, 64, 9)); // the owner's design: a directive, kept
    }

    [Fact]
    public void On_load_the_pawns_stray_boxes_and_stubs_near_home_are_queued_in_one_alert_never_a_wall_or_anyone_elses_block()
    {
        var (c, h, w) = StoryRig.Make(1);
        var pawn = c.P();
        void Placed(int x, int y, int z, int id, string bot) { w.Set(x, y, z, id); h.Placed.Add((new P3(x, y, z), id, bot)); }
        // A pawn's box of planks around 20,64..65,20 (feet sides, head sides, roof).
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) { Placed(20 + dx, 64, 20 + dz, 5, pawn); Placed(20 + dx, 65, 20 + dz, 5, pawn); }
        Placed(20, 66, 20, 5, pawn);
        // A 2-high stub of planks (a tree pillar) and a lone dirt pillar step.
        Placed(-30, 64, 12, 5, pawn); Placed(-30, 65, 12, 5, pawn);
        Placed(14, 64, -25, 3, pawn);
        // Not strays: a pawn's 20-block plank wall (somebody's build), main's own block, a block too far from home, a crop.
        for (int x = 40; x < 60; x++) Placed(x, 64, 40, 5, pawn);
        Placed(-10, 64, -10, 5, "maid_chan");
        Placed(400, 64, 400, 5, pawn);
        Placed(3, 64, 3, 59, pawn);
        c.Tick();
        var marks = Cleanups(c);
        Assert.Equal(12, marks.Count);
        Assert.DoesNotContain(marks, m => m.At!.Value.Z == 40 || m.At == new P3(-10, 64, -10) || m.At!.Value.X == 400 || m.At == new P3(3, 64, 3));
        var alert = Assert.Single(c.Alerts, a => a.Text.StartsWith("Found 12 stray block(s)"));
        Assert.Contains("11 ", alert.Text); // 11 planks, 1 dirt
        c.Tick();
        Assert.Single(c.Alerts, a => a.Text.Contains("stray block(s)")); // once
    }

    /// <summary>
    /// Every block in or beside a quarry was skipped by the stray look, because the quarry's seals were recorded nowhere: a pawn's
    /// pillar left on the rim or in the pit stood for good. Now only its stair's cells, the seals its finished place jobs put there and
    /// a block holding back lava or water are its own.
    /// </summary>
    [Fact]
    public void A_pawns_block_beside_a_quarry_that_is_no_step_or_seal_is_swept()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, WorkTypes.Mine);
        var pawn = c.P();
        c.Meta.QuarrySealsAdopted = true; // a colony already on this version: nothing to adopt
        h.Lives[pawn] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(4, 0, 64)]);
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 15, 63, 15)).Error);
        void Placed(int x, int y, int z, int id) { w.Set(x, y, z, id); h.Placed.Add((new P3(x, y, z), id, pawn)); }
        // A seal its place job put over water in the pit: the quarry's own from now on.
        var wet = new P3(12, 63, 12);
        w.Set(wet.X, wet.Y, wet.Z, 9);
        var seal = c.Next();
        Assert.Equal("place", seal["kind"]!.GetValue<string>());
        Assert.Equal(wet.ToString(), seal["at"]!.GetValue<string>());
        Placed(wet.X, wet.Y, wet.Z, 4);
        c.Report(pawn, seal["id"]!.GetValue<string>(), "done", null, null);
        var step = StoryQuarry.Step(StoryQuarry.Ring(c.Zones.Single().Cells)!, 63, 0);
        Placed(step.X, step.Y, step.Z, 4); // a stair step a pawn placed
        Placed(16, 60, 14, 4); // beside the quarry, holding back water
        w.Set(17, 60, 14, 9);
        Placed(16, 64, 12, 3); Placed(16, 65, 12, 3); // a pillar on the rim
        Placed(13, 64, 14, 3); // a stub on the quarry's top
        c.Tick();
        var swept = Cleanups(c).Select(d => d.At!.Value).ToHashSet();
        Assert.Equal(new HashSet<P3> { new(16, 64, 12), new(16, 65, 12), new(13, 64, 14) }, swept);
        Assert.Single(c.Alerts, a => a.Text.StartsWith("Found 3 stray block(s)"));
    }

    /// <summary>
    /// A seal walling a cave off from the pit whose place job reported failure after the block went in is recorded nowhere: the stray
    /// look took it for a pawn's leftover and dug it out, reopening the cave into the pit. A block with cave air beside it is kept.
    /// </summary>
    [Fact]
    public void A_pawns_block_walling_a_cave_off_from_a_quarry_is_never_a_stray()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, WorkTypes.Mine);
        var pawn = c.P();
        c.Meta.QuarrySealsAdopted = true;
        h.Lives[pawn] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(4, 0, 64)]);
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 15, 63, 15)).Error);
        void Placed(int x, int y, int z, int id) { w.Set(x, y, z, id); h.Placed.Add((new P3(x, y, z), id, pawn)); }
        for (int x = 17; x <= 60; x++) { w.Set(x, 58, 12, 0); w.Set(x, 59, 12, 0); } // a cave running east from the pit's side
        Placed(16, 58, 12, 4); // the seal in its mouth, recorded nowhere
        Placed(16, 64, 12, 3); // a stub on the rim: a stray
        c.Tick();
        Assert.Equal(new P3(16, 64, 12), Assert.Single(Cleanups(c)).At);
        Assert.Null(c.Zones.Single().QuarryPlaced);
    }

    /// <summary>
    /// A save from before quarries recorded their seals: the pawns' blocks already in or beside a quarry are taken as its own once, so the
    /// upgrade never sweeps a seal (a stray elsewhere is swept as before).
    /// </summary>
    [Fact]
    public void On_the_upgrade_the_pawns_blocks_already_in_or_beside_a_quarry_are_adopted_never_swept()
    {
        var (c, h, w) = StoryRig.Make(1);
        var pawn = c.P();
        Assert.False(c.Meta.QuarrySealsAdopted);
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 15, 63, 15)).Error);
        void Placed(int x, int y, int z, int id) { w.Set(x, y, z, id); h.Placed.Add((new P3(x, y, z), id, pawn)); }
        Placed(15, 50, 12, 4); // an old seal in the pit's wall
        Placed(16, 58, 12, 4); // and one beside it, in a cave's mouth
        Placed(30, 64, 30, 3); // a stray far from any quarry
        c.Tick();
        Assert.Equal(new P3(30, 64, 30), Assert.Single(Cleanups(c)).At);
        Assert.True(c.Meta.QuarrySealsAdopted);
        var zone = c.Zones.Single();
        Assert.Equal(new HashSet<P3> { new(15, 50, 12), new(16, 58, 12) }, zone.QuarryPlaced!.ToHashSet());
        // Saved with the zone; a zone with none writes nothing new.
        Assert.Contains("\"QuarryPlaced\"", System.Text.Json.JsonSerializer.Serialize(zone));
        Assert.DoesNotContain("QuarryPlaced", System.Text.Json.JsonSerializer.Serialize(new ZoneState { Id = "z9", Kind = "mine" }));
        Assert.Equal(zone.QuarryPlaced, zone.CopySettings("z9").QuarryPlaced);
    }

    /// <summary>A quarry that becomes done says so once, and the pawns' blocks left beside it (out of the load look's range) are swept.</summary>
    [Fact]
    public void A_quarry_that_becomes_done_says_so_once_and_its_pawns_strays_are_swept()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        var pawn = c.P();
        c.Meta.QuarrySealsAdopted = true;
        c.Tick(); // the load look: nothing near home
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(300, 63, 10, 305, 63, 15)).Error);
        var zone = c.Zones.Single();
        var ring = StoryQuarry.Ring(zone.Cells)!;
        // Dug out already, down to the stone at y 0; only its stair stands.
        var steps = Enumerable.Range(0, 63).Select(k => StoryQuarry.Step(ring, 63, k)).ToHashSet();
        for (int x = 300; x <= 305; x++)
            for (int z = 10; z <= 15; z++)
                for (int y = 1; y <= 63; y++)
                    if (!steps.Contains(new P3(x, y, z))) w.Set(x, y, z, 0);
        void Placed(int x, int y, int z, int id) { w.Set(x, y, z, id); h.Placed.Add((new P3(x, y, z), id, pawn)); }
        Placed(306, 40, 12, 3); // a pawn's block beside the pit
        var step = StoryQuarry.Step(ring, 63, 5);
        Placed(step.X, step.Y, step.Z, 4); // a step it placed
        zone.QuarryPlaced = [new P3(306, 45, 12)]; // a seal its place job put in a cave's mouth
        Placed(306, 45, 12, 4);
        Assert.True(c.State()["zones"]![0]!["quarry"]!["done"]!.GetValue<bool>());
        Assert.Single(c.Alerts, a => a.Text.StartsWith("Quarry z") && a.Text.EndsWith("is done: dug down to bedrock"));
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Equal(new P3(306, 40, 12), Assert.Single(Cleanups(c)).At);
        Assert.Single(c.Alerts, a => a.Text.Contains("found 1 stray block(s) the pawns left in or beside it"));
        clock.Now += TimeSpan.FromSeconds(40);
        c.State();
        c.Tick();
        Assert.Single(c.Alerts, a => a.Text.Contains("is done"));
        Assert.Single(Cleanups(c));
    }

    /// <summary>
    /// Live 11-06: oak planks on the lids of storage chests 216,64,400 and 223,64,400 kept them shut (241 failed opens in 7 hours). A
    /// lid block a pawn of this colony placed is queued for removal; one the placed-block log can't put on a pawn may be a player's: it
    /// is only listed, never removed.
    /// </summary>
    [Fact]
    public void On_load_a_pawns_block_on_a_storage_chests_lid_is_queued_and_one_no_pawn_placed_is_only_listed()
    {
        var (c, h, w) = StoryRig.Make(1);
        var pawn = c.P();
        w.Set(15, 64, 0, 54);
        w.Set(23, 64, 0, 54);
        Assert.Null(c.StorageOp("set", [new P3(15, 64, 0)], null, null, null).Error);
        Assert.Null(c.StorageOp("set", [new P3(23, 64, 0)], null, null, null).Error);
        w.Set(15, 65, 0, 5);
        h.Placed.Add((new P3(15, 65, 0), 5, pawn));
        w.Set(23, 65, 0, 5); // nobody's record: a player's, as far as the colony knows
        c.Tick();
        var mark = Assert.Single(Cleanups(c));
        Assert.Equal(new P3(15, 65, 0), mark.At);
        Assert.DoesNotContain(c.Designations, d => d.At == new P3(23, 65, 0));
        Assert.Contains(c.Alerts, a => a.Text.Contains("left for you to remove") && a.Text.Contains("23,65,0"));
    }

    /// <summary>
    /// Live 11-06, the first sweep: "deconstruct at 243,64,359 failed 3 times: stonebrick … is protected (a placed stonebrick)" (a pawn's
    /// dirt had been replaced by someone's stonebrick), "won't dig the dirt I'm standing on at 243,63,360", and old boxes in a cave
    /// "unreachable even by pillaring/bridging" retried every minute. A cleanup digs from beside its block; one refused as protected is
    /// never dug (not ours any more) but listed for the owner; one out of reach rests 30 minutes, and a second miss leaves it to the owner.
    /// </summary>
    [Fact]
    public void A_cleanup_digs_from_beside_one_refused_as_protected_is_listed_and_one_out_of_reach_rests_then_is_left_to_the_owner()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Build);
        var pawn = c.P();
        w.Set(5, 64, 5, 3);
        w.Set(9, 64, 9, 3);
        c.Temporary(pawn, "5,64,5", true, "reach pillar");
        c.Temporary(pawn, "9,64,9", true, "reach pillar");
        h.Lives[pawn] = PawnLive.Offline;
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Equal(2, Cleanups(c).Count);
        h.Lives[pawn] = FakeStoryHost.Live(20, 64, 20);
        // The first: refused as someone's block.
        var j1 = c.Next();
        Assert.Equal("dig", j1["kind"]!.GetValue<string>());
        var at1 = j1["at"]!.GetValue<string>();
        var stand = j1["stand"]!.GetValue<string>();
        Assert.NotEqual(at1, stand); // from beside it, never on top of it
        c.Report(pawn, j1["id"]!.GetValue<string>(), "failed", $"dirt at {at1} is protected (a placed dirt (someone's floor, wall or path))", null);
        Assert.Single(Cleanups(c));
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("Left for you to remove") && a.Text.Contains(at1) && a.Text.Contains("not the pawns' block"));
        // The second: out of reach. It rests, then a second miss leaves it to the owner.
        var j2 = c.Next();
        var at2 = j2["at"]!.GetValue<string>();
        Assert.NotEqual(at1, at2);
        c.Report(pawn, j2["id"]!.GetValue<string>(), "failed", $"no path to within 3 of ({at2.Replace(",", ", ")}) (not from under the ground) (timeout); unreachable even by pillaring/bridging", null);
        Assert.Single(Cleanups(c));
        var saved = System.Text.Json.JsonSerializer.Serialize(c.Meta);
        var loaded = new StoryColony(System.Text.Json.JsonSerializer.Deserialize<ColonyMeta>(saved)!, h, clock);
        loaded.Joined([pawn]);
        Only(loaded, WorkTypes.Build);
        loaded.Designations.AddRange(c.Designations);
        Assert.NotEqual(at2, loaded.Next()["at"]?.GetValue<string>()); // restart cannot bypass the cleanup's rest
        var other = c.Next();
        Assert.NotEqual(at2, other["at"]?.GetValue<string>()); // resting: not offered again at once
        c.Report(pawn, other["id"]!.GetValue<string>(), "done", null, null);
        clock.Now += TimeSpan.FromMinutes(31);
        c.Tick();
        var j3 = c.Next();
        Assert.Equal(at2, j3["at"]!.GetValue<string>());
        c.Report(pawn, j3["id"]!.GetValue<string>(), "failed", $"no path to within 3 of ({at2.Replace(",", ", ")}) (no route); unreachable even by pillaring/bridging", null);
        Assert.Empty(Cleanups(c));
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("Left for you to remove") && a.Text.Contains(at2) && a.Text.Contains("out of reach"));
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn" && a.Text.Contains("deconstruct"));
        foreach (var t in c.Meta.TempBlocks!) h.Placed.Add((t.At, t.Id, pawn));
        saved = System.Text.Json.JsonSerializer.Serialize(c.Meta);
        loaded = new StoryColony(System.Text.Json.JsonSerializer.Deserialize<ColonyMeta>(saved)!, h, clock);
        loaded.Joined([pawn]);
        loaded.Tick();
        Assert.Empty(Cleanups(loaded)); // the next startup's stray scan must not recreate the failed cave jobs
        Assert.All(loaded.Meta.TempBlocks!, t => Assert.NotNull(t.DeferredReason));
    }

    [Fact]
    public void A_stuck_pawn_on_its_pillar_is_teleported_and_the_pillar_left_is_the_colonys_cleanup()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        var pawn = c.P();
        for (int y = 64; y <= 69; y++) { w.Set(3, y, 3, 3); c.Temporary(pawn, $"3,{y},3", true, "reach pillar"); }
        // It dug the top back, couldn't take the block under it: no way down without it, so the colony takes it home.
        w.Set(3, 69, 3, 0);
        c.Temporary(pawn, "3,69,3", false, "reach pillar");
        h.Lives[pawn] = FakeStoryHost.Live(3, 69, 3);
        var said = c.Stuck(pawn, "stranded on its pillar at 3,69,3: 1 block(s) under it it could not take back");
        Assert.Equal("teleported home", said);
        Assert.Contains(h.Teleported, t => t.Account == pawn);
        h.Lives[pawn] = FakeStoryHost.Live(30, 64, 30); // home, and off to other work
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        Assert.Equal(5, Cleanups(c).Count); // the rest of the pillar: a builder digs it, nothing stays
    }
}
