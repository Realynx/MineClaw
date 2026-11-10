using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryOreVeinTests
{
    private static readonly P3 Seed = new(2, 64, 0), Next = new(3, 64, 0), Tail = new(4, 64, 0);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, JsonObject J) Rig(bool design = false, int ore = 15, int pawns = 1)
    {
        var (c, h, w) = StoryRig.Make(pawns);
        foreach (var p in c.Pawns.Values)
            foreach (var type in WorkTypes.All) p.Priorities[type] = type == WorkTypes.Mine ? 1 : 0; // a mine design's digs are mining too
        c.DoneGrace = TimeSpan.Zero;
        w.Set(Seed.X, Seed.Y, Seed.Z, ore);
        w.Set(Next.X, Next.Y, Next.Z, ore);
        w.Set(Tail.X, Tail.Y, Tail.Z, ore);
        if (design)
            Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "mine", ["group"] = "mine", ["blocks"] = new JsonArray(Seed.Json()) }), null).Error);
        else Assert.Null(c.Designate("add", "mine", null, [Seed], null).Error);
        var j = c.Next();
        Assert.True(j["follow_veins"]!.GetValue<bool>());
        return (c, h, w, j);
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static JsonArray Dig(StoryColony c, FakeStoryWorld w, JsonObject j, P3 at, int ore = 15)
    {
        w.Set(at.X, at.Y, at.Z, 0);
        var added = c.DigExposed(c.P(), Id(j), at.ToString(), ore, 0);
        Assert.NotNull(added);
        Assert.True(c.Report(c.P(), Id(j), "progress", $"digcell {at} done", null));
        return added;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_connected_branch_continues_in_one_batch_beyond_the_instruction(bool design)
    {
        var (c, _, w, j) = Rig(design);
        w.Set(3, 65, 1, 15); // diagonal-only ore must not be followed
        Assert.Equal(Next.ToString(), Assert.Single(Dig(c, w, j, Seed))!["at"]!.GetValue<string>());
        Assert.Equal(Tail.ToString(), Assert.Single(Dig(c, w, j, Next))!["at"]!.GetValue<string>());
        Assert.Empty(Dig(c, w, j, Tail));
        Assert.True(c.Report(c.P(), Id(j), "done", "batch: 3/3 dug", null));
        Assert.Empty(c.Designations);
        if (design) Assert.All(c.Builds.Single().Ghosts, g => Assert.Equal("done", g.State));
        Assert.Equal(15, w.Block(3, 65, 1).Id);
    }

    [Fact]
    public void Exposed_ore_from_a_stone_cut_waits_for_the_right_tool_then_resumes()
    {
        var (c, h, w, j) = Rig(ore: 1);
        w.Set(Next.X, Next.Y, Next.Z, 56); // iron pick required; current pawn carries stone
        Assert.Empty(Dig(c, w, j, Seed, ore: 1));
        Assert.Single(c.Designations, d => d.Ore == 56);
        c.Report(c.P(), Id(j), "done", "batch", null);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(257, 0, 1));
        var resumed = c.Next();
        Assert.Equal(Next.ToString(), resumed["at"]!.GetValue<string>());
        Assert.Equal(56, resumed["vein_ores"]![Next.ToString()]!.GetValue<int>());
    }

    [Fact]
    public void A_full_or_interrupted_batch_keeps_its_undug_frontier_and_exclusive_lease()
    {
        var (c, _, w, j) = Rig(pawns: 2);
        Dig(c, w, j, Seed);
        Assert.Equal(c.P(), c.Leases()["b:" + Next]);
        Assert.NotEqual(Next.ToString(), c.Next(1)["at"]?.GetValue<string>());
        c.Report(c.P(), Id(j), "done", "batch: 1/2 dug; inventory full", null);
        var resumed = c.Next();
        Assert.Equal(Next.ToString(), resumed["at"]!.GetValue<string>());
        Assert.Equal(15, resumed["vein_ores"]![Next.ToString()]!.GetValue<int>());
        Assert.Empty(c.Designations.Where(d => d.At == Seed));
    }

    [Fact]
    public void A_miner_with_no_free_slot_gets_no_vein_job_until_it_has_one()
    {
        // Its batch would dig nothing ("0/1 dug; inventory full", a success) and the same vein job came back at once. The gate is the
        // body's free slots, never the report's text: the batch above says "inventory full" with room to spare and goes on.
        var (c, h, w, j) = Rig();
        Dig(c, w, j, Seed);
        c.Report(c.P(), Id(j), "done", "batch: 1/2 dug; inventory full", null);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(4, 0, 35 * 64)) with { FreeSlots = 0 };
        var held = c.Next();
        Assert.NotEqual("dig", held["kind"]!.GetValue<string>());
        Assert.Single(c.Designations, d => d.At == Next); // the vein's mark stays
        c.Report(c.P(), Id(held), "done", null, null);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0);
        var resumed = c.Next();
        Assert.Equal("dig", resumed["kind"]!.GetValue<string>());
        Assert.Equal(Next.ToString(), resumed["at"]!.GetValue<string>());
    }

    [Fact]
    public void Cancelling_an_extension_by_id_interrupts_the_owner_and_prevents_rediscovery()
    {
        var (c, h, w, j) = Rig();
        Dig(c, w, j, Seed);
        var extra = c.Designations.Single();
        Assert.Null(c.Designate("cancel", null, null, null, null, [extra.Id]).Error);
        Assert.Contains(Next, c.Meta.CancelledOre!);
        Assert.Single(h.Interrupted);
        Assert.Null(c.DigExposed(c.P(), Id(j), Next.ToString(), 15, 0));
        w.Set(Seed.X, Seed.Y, Seed.Z, 15);
        c.Designate("add", "mine", null, [Seed], null);
        Assert.Empty(Dig(c, w, c.Next(), Seed));
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void Pausing_a_finished_blueprint_seed_stops_the_vein_and_removal_drops_its_marks()
    {
        var (c, _, w, j) = Rig(design: true);
        Dig(c, w, j, Seed);
        var b = c.Builds.Single();
        c.BuildsOp("pause", null, [b.Id]);
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Single(c.Designations);
        Assert.NotEqual("dig", c.Next()["kind"]!.GetValue<string>());
        c.BuildsOp("remove", null, [b.Id]);
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void Blueprint_original_floor_and_sibling_access_remain_intact()
    {
        var (c, _, w, j) = Rig(design: true);
        // This completed sibling's original dig geometry makes Next the natural floor of a higher tunnel/step.
        c.Builds.Add(new BuildState
        {
            Id = "sibling", Kind = "mine", Status = "done",
            Spec = new JsonObject { ["group"] = "mine", ["blocks"] = new JsonArray(Next.Add(0, 1, 0).Json()) },
        });
        w.Set(Seed.X, Seed.Y - 1, Seed.Z, 15);
        Assert.Empty(Dig(c, w, j, Seed));
        Assert.Empty(c.Designations);
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("preserve")]
    [InlineData("build")]
    public void Veins_never_override_protected_cells(string protection)
    {
        var (c, _, w, j) = Rig();
        if (protection == "build")
            c.Builds.Add(new BuildState { Id = "keep", Kind = "block", Ghosts = [new GhostState { At = Next, Id = 15, Job = true }] });
        else c.ZoneOp("add", protection, Area.Of(3, 64, 0, 3, 64, 0));
        Assert.Empty(Dig(c, w, j, Seed));
        Assert.Empty(c.Designations);
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("preserve")]
    public void A_new_protection_stops_an_already_reserved_extension(string protection)
    {
        var (c, _, w, j) = Rig();
        Dig(c, w, j, Seed);
        Assert.Equal(c.P(), c.Leases()["b:" + Next]);
        c.ZoneOp("add", protection, Area.Of(3, 64, 0, 3, 64, 0));
        c.Tick();
        Assert.Null(c.CurrentOf(c.P()));
        Assert.False(c.Leases().ContainsKey("b:" + Next));
        Assert.Null(c.DigExposed(c.P(), Id(j), Next.ToString(), 15, 0));
    }

    [Fact]
    public void A_liquid_seal_is_visible_as_waiting_ore_but_is_not_reserved_to_dig()
    {
        var (c, _, w, j) = Rig();
        w.Set(Next.X, Next.Y + 1, Next.Z, 9);
        Assert.Empty(Dig(c, w, j, Seed));
        Assert.Single(c.Designations, d => d.At == Next && d.Wait!.Contains("liquids"));
        Assert.False(c.Leases().ContainsKey("b:" + Next));
    }

    [Fact]
    public void Unloaded_boundary_and_pending_ore_survive_serialization_and_resume_when_loaded()
    {
        var (c, h, w, j) = Rig();
        w.Unloaded.Add((Next.X, Next.Z));
        Assert.Empty(Dig(c, w, j, Seed));
        c.Report(c.P(), Id(j), "done", "batch", null);
        var meta = JsonSerializer.Deserialize<ColonyMeta>(JsonSerializer.Serialize(c.Meta))!;
        Assert.Contains(meta.OreFrontiers!, f => f.At == Next);
        var restored = new StoryColony(meta, h);
        foreach (var (name, pawn) in c.Pawns) restored.Pawns[name] = pawn;
        w.Unloaded.Clear();
        restored.Tick();
        Assert.Single(restored.Designations, d => d.At == Next);
        Assert.Equal(Next.ToString(), restored.Next()["at"]!.GetValue<string>());
        var saved = JsonSerializer.Deserialize<DesignationState>(JsonSerializer.Serialize(restored.Designations.Single()))!;
        Assert.Equal(15, saved.Ore);
        Assert.Equal(WorkTypes.Mine, saved.OreOrigin!.Type);
    }

    [Fact]
    public void Ore_zone_jobs_survive_unrelated_zone_edits_and_continue_beyond_the_zone()
    {
        var (c, _, w) = StoryRig.Make();
        foreach (var type in WorkTypes.All) c.Pawns[c.P()].Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        w.Set(Seed.X, Seed.Y, Seed.Z, 15);
        w.Set(Next.X, Next.Y, Next.Z, 15);
        c.ZoneOp("add", "mine", Area.Of(2, 64, 0, 2, 64, 0), grow: new JsonObject { ["mode"] = "ores" });
        var j = c.Next();
        c.ZoneOp("add", "home", Area.Of(10, 64, 10, 11, 64, 11));
        Assert.Equal(Id(j), c.CurrentOf(c.P())!.Id);
        Assert.Equal(Next.ToString(), Assert.Single(Dig(c, w, j, Seed))!["at"]!.GetValue<string>());
    }

    [Fact]
    public void Quarry_discovers_ore_outside_its_painted_columns_but_keeps_its_stair()
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var type in WorkTypes.All) c.Pawns[c.P()].Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 14, 63, 14));
        var zone = c.Zones.Single(); zone.QuarryTop = 63;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        var step = StoryQuarry.Step(ring, 63, 0);
        for (int x = 10; x <= 14; x++) for (int z = 10; z <= 14; z++) w.Set(x, 63, z, 0);
        w.Set(step.X, step.Y, step.Z, 15);
        var seed = new P3(14, 63, 12); var outside = seed.Add(1, 0, 0);
        w.Set(seed.X, seed.Y, seed.Z, 15); w.Set(outside.X, outside.Y, outside.Z, 15);
        h.Lives[c.P()] = FakeStoryHost.Live(14, 64, 13);
        var j = c.Next();
        Assert.Equal(seed.ToString(), j["at"]!.GetValue<string>());
        Assert.Equal(outside.ToString(), Assert.Single(Dig(c, w, j, seed))!["at"]!.GetValue<string>());
        Assert.DoesNotContain(c.Designations, d => d.At == step);
        c.ZoneOp("remove", "mine", null, id: zone.Id);
        Assert.Empty(c.Designations);
        Assert.Null(c.CurrentOf(c.P()));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The south edge cell of a 5×5 quarry's working layer (y 59) and the ore beside it, outside the zone.</summary>
    private static readonly P3 Edge = new(12, 59, 14), WallOre = new(12, 59, 15);

    /// <summary>A 5×5 quarry dug down to y 60 (its stair natural stone), iron ore in its south wall beside <see cref="Edge"/>; the pawn
    /// on the pit's floor has a dig batch that holds the edge cell.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, JsonObject J) QuarryWall(Clock clock)
    {
        var (c, h, w) = StoryRig.Make(time: clock);
        foreach (var type in WorkTypes.All) c.Pawns[c.P()].Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        c.DoneGrace = TimeSpan.Zero;
        c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 14, 63, 14));
        var zone = c.Zones.Single();
        zone.QuarryTop = 63;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int x = 10; x <= 14; x++) for (int z = 10; z <= 14; z++) for (int y = 60; y <= 63; y++) w.Set(x, y, z, 0);
        for (int k = 0; k <= 3; k++)
        {
            var step = StoryQuarry.Step(ring, 63, k);
            w.Set(step.X, step.Y, step.Z, 1);
        }
        w.Set(WallOre.X, WallOre.Y, WallOre.Z, 15);
        h.Lives[c.P()] = FakeStoryHost.Live(Edge.X, 60, Edge.Z - 1);
        var j = c.Next();
        Assert.Contains(Edge.ToString(), j["dig_cells"]!.AsArray().Select(n => n!.GetValue<string>()));
        return (c, h, w, j);
    }

    [Fact]
    public void A_quarry_vein_that_would_open_a_cave_waits()
    {
        // Vein work came before the quarry's own and its only check was liquid or sand beside: a wall ore beside a cave let the cave
        // into the pit.
        var clock = new Clock();
        var (c, _, w, j) = QuarryWall(clock);
        for (int z = WallOre.Z + 1; z <= WallOre.Z + 70; z++) w.Set(WallOre.X, WallOre.Y, z, 0); // a cave tunnel behind the ore
        Assert.Empty(Dig(c, w, j, Edge, ore: 1));
        Assert.Single(c.Designations, d => d.At == WallOre && d.Wait!.Contains("would open the quarry to a cave"));
        Assert.False(c.Leases().ContainsKey("b:" + WallOre));
        c.Report(c.P(), Id(j), "done", "batch", null);
        w.Set(WallOre.X, WallOre.Y, WallOre.Z + 1, 1); // the cave walled off behind the ore: followed again
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal(WallOre.ToString(), c.Next()["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_quarry_vein_stops_while_a_spawner_stops_the_quarry()
    {
        var clock = new Clock();
        var (c, _, w, j) = QuarryWall(clock);
        Assert.Equal(WallOre.ToString(), Assert.Single(Dig(c, w, j, Edge, ore: 1))!["at"]!.GetValue<string>());
        c.Report(c.P(), Id(j), "done", "batch: 1/2 dug", null); // the ore left for later: its mark stays
        var spawner = new P3(WallOre.X, 57, WallOre.Z + 2);
        w.Set(spawner.X, spawner.Y, spawner.Z, 52);
        clock.Now += TimeSpan.FromSeconds(6);
        var held = c.Next();
        Assert.NotEqual("dig", held["kind"]!.GetValue<string>());
        Assert.Single(c.Designations, d => d.At == WallOre && d.Wait!.Contains("monster spawner"));
        c.Report(c.P(), Id(held), "done", null, null);
        w.Set(spawner.X, spawner.Y, spawner.Z, 0); // broken by the owner
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal(WallOre.ToString(), c.Next()["at"]!.GetValue<string>());
    }
}
