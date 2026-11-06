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
