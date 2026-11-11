using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Leftover pillars (docs/web/story-mode.md "Temporary blocks"). Fox 11-11: "I see some left over pillars it made, I don't know why it
/// even made some there, but one of them is not even marked for destruction." Live 11-11 the registry held 173 temporary blocks: 81
/// parked "out of reach" with no mark at all (63 escape pillars out of the quarry and a strip mine), 31 pointing at a mark that no
/// longer existed, 6 refused as "not the pawns' block any more". The owner's rule: a block a pawn put down for itself is dug back
/// whatever happens; if that strands the pawn, it teleports home rather than leave the block. Synthetic coordinates only.
/// </summary>
public sealed class StoryTempPillarsTests
{
    private static void Only(StoryColony c, params string[] types)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static List<DesignationState> Cleanups(StoryColony c) =>
        c.Designations.Where(d => d.Kind == "deconstruct" && d.Auto && (d.Label ?? "").Contains("temporary")).ToList();

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    /// <summary>A pit 9 deep (its floor at y 55) east of home, and a pawn's escape pillar against its east wall, its top at the rim.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, ManualTime Clock) PitWithEscapePillar(bool teleport = true)
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Build);
        c.Meta.RescueTeleport = teleport;
        for (int x = 10; x <= 14; x++)
            for (int z = 10; z <= 14; z++)
                for (int y = 55; y <= 63; y++) w.Set(x, y, z, 0);
        var climber = c.P(0);
        for (int y = 55; y <= 63; y++) { w.Set(14, y, 12, 3); c.Temporary(climber, $"14,{y},12", true, "escape pillar"); }
        h.Lives[climber] = FakeStoryHost.Live(40, 64, 40); // out, and off to other work
        h.Lives[c.P(1)] = FakeStoryHost.Live(20, 64, 12);
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        Assert.Equal(9, Cleanups(c).Count);
        return (c, h, w, clock);
    }

    /// <summary>
    /// The escape pillar's lower blocks stand in a pit with no walkable floor: dug from beside, each was "out of reach" and given up.
    /// Now the whole pillar is one job: walk onto its top from the rim, dig down beneath itself, walk back out; stranded below, the
    /// colony teleports it home (never the "bring blocks" help, which would only build another pillar).
    /// </summary>
    [Fact]
    public void A_pillar_in_a_pit_is_taken_down_from_its_top_and_the_pawn_left_below_is_teleported_home()
    {
        var (c, h, w, clock) = PitWithEscapePillar();
        var climber = c.P(0);
        var cleaner = c.P(1);
        var job = c.Next(1);
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.Equal("14,63,12", job["at"]!.GetValue<string>()); // the top's mark leads
        Assert.Equal("14,64,12", job["stand"]!.GetValue<string>()); // on top of it
        var unpole = job["unpole"]!.AsObject();
        Assert.Equal("15,64,12", unpole["back"]!.GetValue<string>()); // the rim beside its top
        var cells = unpole["cells"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(Enumerable.Range(55, 9).Reverse().Select(y => $"14,{y},12"), cells);
        // Every cell is this pawn's for the job (no other pawn digs part of it from below), and its body knows them as the hive's own.
        Assert.Equal(9, c.Leases().Count(l => l.Value == cleaner && l.Key.StartsWith("b:14,")));
        Assert.Equal(cells.Order(), h.Owned.Where(o => o.Account == cleaner && o.PlacedBy == climber).Select(o => o.At.ToString()).Order());
        // The other pawn is offered no part of it meanwhile.
        h.Lives[climber] = FakeStoryHost.Live(16, 64, 12);
        var other = c.Next(0);
        Assert.False(other["at"]?.GetValue<string>() is { } a && a.StartsWith("14,"), other.ToJsonString());

        // The pawn dug down to the pit's floor and couldn't walk back out: it asks for the teleport home.
        for (int y = 55; y <= 63; y++) w.Set(14, y, 12, 0);
        h.Lives[cleaner] = FakeStoryHost.Live(14, 55, 12);
        Assert.Equal("teleported home", c.Stuck(cleaner, "stranded at 14,55,12 after taking down a temporary pillar (no path to within 1 of (15, 64, 12) (no route))"));
        Assert.Contains(h.Teleported, t => t.Account == cleaner);
        Assert.True(c.Report(cleaner, Id(job), "done", "took 9 block(s) of the pillar down from its top; stranded at 14,55,12: teleported home", null));
        clock.Now += TimeSpan.FromSeconds(5);
        c.Tick();
        Assert.Empty(Cleanups(c));
        Assert.Null(c.Meta.TempBlocks);
    }

    /// <summary>
    /// The leftovers the owner saw: the old per-cell cleanup dug the escape pillar's upper 3 blocks from the rim and parked the rest
    /// "out of reach" with no mark. That stub's top is 3 under the rim's floor, no step from any walkable cell. On load it is marked
    /// again and taken down from its top: the pawn drops onto it from the rim (the pathfinder's 3-block drop), digs down, and the
    /// rim is where it walks back to (a teleport home when it can't). A miss rests the whole column, never cell by cell from beside.
    /// </summary>
    [Fact]
    public void A_stub_left_below_the_rim_is_taken_down_from_its_top_by_dropping_in_and_a_miss_rests_the_whole_column()
    {
        var (seed, h, w, clock) = PitWithEscapePillar();
        var climber = seed.P(0);
        var cleaner = seed.P(1);
        for (int y = 61; y <= 63; y++) w.Set(14, y, 12, 0); // dug from the rim
        var meta = seed.Meta;
        meta.TempBlocks = Enumerable.Range(55, 6).Select(y => new TempBlockState
        {
            At = new P3(14, y, 12), Id = 3, Pawn = climber, Kind = "escape pillar", Since = clock.Now - TimeSpan.FromDays(1), Misses = 2,
            DeferredReason = "out of reach",
        }).ToList();
        var c = new StoryColony(System.Text.Json.JsonSerializer.Deserialize<ColonyMeta>(System.Text.Json.JsonSerializer.Serialize(meta))!, h, clock);
        c.Joined([climber, cleaner]);
        Only(c, WorkTypes.Build);
        c.Tick();
        Assert.Equal(6, Cleanups(c).Count);

        var job = c.Next(1);
        Assert.Equal("14,60,12", job["at"]!.GetValue<string>());
        Assert.Equal("14,61,12", job["stand"]!.GetValue<string>()); // on the stub's top, 3 under the rim's floor
        var unpole = job["unpole"]!.AsObject();
        Assert.Equal("15,64,12", unpole["back"]!.GetValue<string>()); // the rim it drops in from
        Assert.Equal(Enumerable.Range(55, 6).Reverse().Select(y => $"14,{y},12"),
            unpole["cells"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(6, c.Leases().Count(l => l.Value == cleaner && l.Key.StartsWith("b:14,")));

        // It couldn't get there after all: the whole column rests with its top, so nobody digs its cells one by one from beside.
        c.Report(cleaner, Id(job), "failed", "out of reach even from a pillar: no walkable ground below or beside it (no route within the search limit)", null);
        Assert.All(c.Meta.TempBlocks!, t => Assert.Equal(clock.Now + TimeSpan.FromMinutes(30), t.RetryAfter));
        Assert.Equal(6, Cleanups(c).Count);
        Assert.All(Cleanups(c), d => Assert.StartsWith("out of reach for now (tried 1×", d.Wait));
        var next = c.Next(1);
        Assert.False(next["at"]?.GetValue<string>() is { } a && a.StartsWith("14,"), next.ToJsonString());
        c.Report(cleaner, Id(next), "done", null, null);
        clock.Now += TimeSpan.FromMinutes(31);
        c.Tick();
        Assert.NotNull(c.Next(1)["unpole"]); // and tried again as a whole
    }

    /// <summary>
    /// The pawn that climbed out on the pillar is the one sent to take it down (the nearest builder, or the only one). Standing on its
    /// own column for that job is not "temporary access still in use": the tick neither cuts the job nor takes the lower cells' marks
    /// while it digs down.
    /// </summary>
    [Fact]
    public void The_pawn_that_built_a_pillar_can_take_it_down_from_its_top_without_the_tick_cutting_it()
    {
        var (c, h, w, clock) = PitWithEscapePillar();
        var climber = c.P(0);
        h.Lives[c.P(1)] = PawnLive.Offline;
        var job = c.Next(0);
        Assert.Equal("14,64,12", job["stand"]!.GetValue<string>());
        Assert.NotNull(job["unpole"]);
        h.Lives[climber] = FakeStoryHost.Live(14.5, 64, 12.5); // on its top
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Equal(Id(job), c.CurrentOf(climber)?.Id);
        Assert.Equal(9, Cleanups(c).Count);
        for (int y = 60; y <= 63; y++) w.Set(14, y, 12, 0); // partway down
        h.Lives[climber] = FakeStoryHost.Live(14.5, 60, 12.5);
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Equal(Id(job), c.CurrentOf(climber)?.Id);
        Assert.Equal(Enumerable.Range(55, 5).Select(y => new P3(14, y, 12)), Cleanups(c).Select(d => d.At!.Value).OrderBy(p => p.Y));
        Assert.Equal(9, c.Leases().Count(l => l.Value == climber && l.Key.StartsWith("b:14,"))); // the job still holds its column
    }

    /// <summary>
    /// A registered block the colony only adopted from its view (the pawn's placement wasn't seen when it was reported) may be
    /// anybody's: its cleanup goes ahead, but the cleaner's body is never taught it as the hive's own, so the body's protection still
    /// judges it. A block the pawn reported is taught.
    /// </summary>
    [Fact]
    public void A_block_adopted_from_the_view_is_never_taught_to_the_cleaner()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Build);
        var pawn = c.P();
        c.Temporary(pawn, "5,64,5", true, "reach pillar"); // not seen yet
        w.Set(5, 64, 5, 3);
        w.Set(9, 64, 9, 3);
        c.Temporary(pawn, "9,64,9", true, "reach pillar");
        h.Lives[pawn] = PawnLive.Offline;
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        var adopted = c.Meta.TempBlocks!.Single(t => t.At == new P3(5, 64, 5));
        Assert.True(adopted.Adopted);
        Assert.False(c.Meta.TempBlocks!.Single(t => t.At == new P3(9, 64, 9)).Adopted);
        var saved = System.Text.Json.JsonSerializer.Serialize(c.Meta);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(saved, "\"Adopted\":true")); // written only when set
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<ColonyMeta>(saved)!.TempBlocks!.Single(t => t.At == new P3(5, 64, 5)).Adopted);
        h.Lives[pawn] = FakeStoryHost.Live(5, 64, 8); // nearer the adopted one
        Assert.Equal("5,64,5", c.Next()["at"]!.GetValue<string>()); // still cleaned up
        Assert.DoesNotContain(h.Owned, o => o.At == new P3(5, 64, 5)); // but never taught
    }

    /// <summary>Without the rescue teleport a pawn left at the bottom would pillar out again: the pillar is dug from beside, as before.</summary>
    [Fact]
    public void Without_the_rescue_teleport_a_pit_pillar_is_dug_from_beside()
    {
        var (c, _, _, _) = PitWithEscapePillar(teleport: false);
        var job = c.Next(1);
        Assert.Null(job["unpole"]);
        Assert.NotEqual("14,64,12", job["stand"]?.GetValue<string>());
    }

    /// <summary>A pillar standing on open ground whose top nobody can walk onto (a tree's reach pillar) is dug from beside, top first.</summary>
    [Fact]
    public void A_free_standing_pillar_whose_top_no_one_can_walk_onto_is_dug_from_beside()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Build);
        var pawn = c.P();
        for (int y = 64; y <= 67; y++) { w.Set(5, y, 5, 3); c.Temporary(pawn, $"5,{y},5", true, "reach pillar"); }
        h.Lives[pawn] = FakeStoryHost.Live(20, 64, 20);
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        var job = c.Next();
        Assert.Null(job["unpole"]);
        Assert.Equal("5,67,5", job["at"]!.GetValue<string>());
        Assert.NotEqual("5,68,5", job["stand"]?.GetValue<string>());
        Assert.Contains(h.Owned, o => o.At == new P3(5, 67, 5) && o.Account == pawn); // still taught before the dig
    }

    /// <summary>
    /// An older save parked the escape pillar "out of reach" with no mark (the owner saw it "not even marked for destruction"). On load
    /// it gets a fresh chance: marked again. A miss now rests it, longer each time, with its reason on the map, never parked again.
    /// </summary>
    [Fact]
    public void An_out_of_reach_entry_from_an_older_save_is_marked_again_and_a_miss_rests_it_without_dropping_the_mark()
    {
        var clock = new ManualTime();
        var (old, h, w) = StoryRig.Make(1, time: clock);
        var pawn = old.P();
        var at = new P3(40, 64, 40);
        w.Set(at.X, at.Y, at.Z, 3);
        old.Meta.TempBlocks =
        [
            new TempBlockState { At = at, Id = 3, Pawn = pawn, Kind = "escape pillar", Since = clock.Now - TimeSpan.FromDays(1), Misses = 2, DeferredReason = "out of reach" },
        ];
        var saved = System.Text.Json.JsonSerializer.Serialize(old.Meta);
        Assert.Contains("\"DeferredReason\":\"out of reach\"", saved); // the shape older saves have
        var c = new StoryColony(System.Text.Json.JsonSerializer.Deserialize<ColonyMeta>(saved)!, h, clock);
        c.Joined([pawn]);
        Only(c, WorkTypes.Build);
        h.Lives[pawn] = FakeStoryHost.Live(20, 64, 20);
        c.Tick();
        var mark = Assert.Single(Cleanups(c));
        Assert.Equal(at, mark.At);
        var temp = Assert.Single(c.Meta.TempBlocks!);
        Assert.Null(temp.DeferredReason);
        Assert.Equal(0, temp.Misses);

        var j = c.Next();
        Assert.Equal(at.ToString(), j["at"]!.GetValue<string>());
        c.Report(pawn, Id(j), "failed", "out of reach even from a pillar: no walkable ground below or beside it (no route within the search limit)", null);
        Assert.Same(mark, Assert.Single(Cleanups(c)));
        Assert.Equal("waiting", mark.Status);
        Assert.StartsWith("out of reach for now (tried 1×, again in 30 min): out of reach even from a pillar", mark.Wait);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(30), temp.RetryAfter);
        Assert.Contains(c.Alerts, a => a.Text.Contains("temporary block(s) the pawns couldn't reach yet") && a.Text.Contains("escape pillar: 40,64,40"));
        var idle = c.Next();
        Assert.NotEqual(at.ToString(), idle["at"]?.GetValue<string>()); // resting
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Contains("deconstruct marks: 1 temporary block(s) out of reach for now (next try in 30 min)", c.IdleReasonOf(pawn)); // the pawn says why
        c.Report(pawn, Id(idle), "done", null, null);

        foreach (var (minutes, rest) in new[] { (31, "1 h"), (61, "2 h"), (121, "4 h"), (241, "8 h"), (481, "8 h") })
        {
            clock.Now += TimeSpan.FromMinutes(minutes);
            c.Tick();
            var again = c.Next();
            Assert.Equal(at.ToString(), again["at"]!.GetValue<string>());
            c.Report(pawn, Id(again), "failed", "out of reach even from a pillar: more than 24 blocks up", null);
            Assert.Same(mark, Assert.Single(Cleanups(c)));
            Assert.Contains($"again in {rest})", mark.Wait);
            Assert.Null(temp.DeferredReason);
        }
    }

    /// <summary>
    /// Live 11-11: 31 entries pointed at a removal mark that no longer existed (a mark finished on a report the view couldn't check,
    /// or dropped with other work): nobody would take the block, and the registry tracked it silently. The block still standing is
    /// marked again; one gone with its mark is forgotten.
    /// </summary>
    [Fact]
    public void A_temporary_block_whose_mark_is_gone_is_marked_again_and_one_gone_with_it_is_forgotten()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        var pawn = c.P();
        w.Set(5, 64, 5, 4);
        w.Set(9, 64, 9, 3);
        c.Temporary(pawn, "5,64,5", true, "build scaffold");
        c.Temporary(pawn, "9,64,9", true, "scaffold");
        h.Lives[pawn] = PawnLive.Offline;
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        var old = Cleanups(c).Select(d => d.Id).ToHashSet();
        Assert.Equal(2, old.Count);
        c.Designations.RemoveAll(d => old.Contains(d.Id)); // gone by another path
        w.Set(9, 64, 9, 0); // and this one's block with it
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        var temp = Assert.Single(c.Meta.TempBlocks!);
        Assert.Equal(new P3(5, 64, 5), temp.At);
        var mark = Assert.Single(Cleanups(c));
        Assert.Equal(temp.Designation, mark.Id);
        Assert.DoesNotContain(mark.Id, old);
        Assert.Equal(new P3(5, 64, 5), mark.At);
    }

    /// <summary>
    /// The same block in another state is still the pawn's: a grass record (a stray seen grown over) that went back to dirt under a
    /// block was read as dug and the pillar forgotten while it stood; a block whose meta changed is the same block. And the cleaner's
    /// body is taught the block as the world shows it now (its own log may never have had it), so its protection doesn't refuse it as
    /// "not the pawns' block any more".
    /// </summary>
    [Fact]
    public void A_block_that_only_changed_state_stays_the_pawns_and_its_cleaner_is_taught_it()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(2, time: clock);
        Only(c, WorkTypes.Build);
        var placer = c.P(0);
        var cleaner = c.P(1);
        w.Set(5, 64, 5, 2); // grown over when the colony saw it
        c.Temporary(placer, "5,64,5", true, "stray");
        w.Set(5, 64, 5, 3); // dirt again
        w.Set(7, 64, 7, 5, 0);
        c.Temporary(placer, "7,64,7", true, "scaffold");
        w.Set(7, 64, 7, 5, 2); // another planks state
        h.Lives[placer] = PawnLive.Offline;
        h.Lives[cleaner] = FakeStoryHost.Live(6, 64, 9);
        clock.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Equal(2, c.Meta.TempBlocks!.Count);
        Assert.Equal(new[] { new P3(5, 64, 5), new P3(7, 64, 7) }, Cleanups(c).Select(d => d.At!.Value).OrderBy(p => p.X));
        var at = c.Next(1)["at"]!.GetValue<string>();
        var owned = Assert.Single(h.Owned, o => o.At.ToString() == at);
        Assert.Equal((cleaner, placer), (owned.Account, owned.PlacedBy));
        Assert.Equal(w.Block(owned.At.X, owned.At.Y, owned.At.Z).Id, owned.Id); // as the world shows it now
    }

    /// <summary>Live 11-11: 45 dirt pillars' marks waited for a shovel the colony couldn't make. The wait is said, on the mark and in
    /// the pawn's idle reason, never a silent stall.</summary>
    [Fact]
    public void A_removal_mark_waiting_for_a_tool_says_so()
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, time: clock);
        Only(c, WorkTypes.Build);
        var pawn = c.P();
        c.Meta.Gear.AutoCraftTools = false;
        Assert.Null(c.MaterialRulesOp(new JsonObject
        {
            ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "group:soil", ["tool"] = "shovel", ["tier"] = "wood", ["fallback"] = "plain" },
        }).Error);
        for (int y = 64; y <= 65; y++) { w.Set(5, y, 5, 3); c.Temporary(pawn, $"5,{y},5", true, "escape pillar"); }
        h.Lives[pawn] = FakeStoryHost.Live(20, 64, 20);
        clock.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        Assert.Equal(2, Cleanups(c).Count);
        Assert.NotEqual("dig", c.Next()["kind"]!.GetValue<string>());
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.All(Cleanups(c), d =>
        {
            Assert.Equal("waiting", d.Status);
            Assert.StartsWith("needs a wooden shovel", d.Wait);
        });
        Assert.Contains("deconstruct marks: 2 waiting for a tool (needs a wooden shovel", c.IdleReasonOf(pawn));
    }
}
