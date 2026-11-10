using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A mine design's digs are mining work, not building (Fox 11-10: "these types of designs, when imported, should count as mining, not
/// building"): a pawn with Mine work takes them as jobs of type <c>mine</c>, a pawn that only builds never gets them, and the blocks a
/// mine design puts in (a torch) stay building work. Saved colonies load as they are: a <c>mine</c> build of an older save, and an ore
/// vein its digs found while they were still building work, are mining work now (docs/web/story-mode.md "Mine designs are mining").
/// </summary>
public sealed class StoryMineWorkTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-10T10:00:00Z");

    private static void Only(PawnState p, params string[] types)
    {
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static string Str(JsonObject j, string key) => j[key]?.GetValue<string>() ?? "";

    /// <summary>A shaft from the surface at 5,·,5 (the ground is stone up to y 63), and a torch beside its top when asked.</summary>
    private static JsonObject Shaft(bool torch = false)
    {
        var item = new JsonObject
        {
            ["kind"] = "mine", ["name"] = "shaft",
            ["blocks"] = new JsonArray(new JsonArray(5, 63, 5), new JsonArray(5, 62, 5), new JsonArray(5, 61, 5)),
        };
        if (torch) item["place"] = new JsonArray(new JsonArray(4, 64, 5, 50, 5));
        return item;
    }

    /// <summary>Pawn 0 only builds, pawn 1 only mines.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) BuilderAndMiner(Clock clock, bool torch = false)
    {
        var (c, h, w) = StoryRig.Make(pawns: 2, time: clock);
        Only(c.Pawns[c.P(0)], WorkTypes.Build);
        Only(c.Pawns[c.P(1)], WorkTypes.Mine);
        h.Lives[c.P(0)] = FakeStoryHost.Live(5, 64, 3, inv: new Stack(50, 0, 64));
        h.Lives[c.P(1)] = FakeStoryHost.Live(6, 64, 3);
        Assert.Null(c.BuildsOp("add", new JsonArray(Shaft(torch)), null).Error);
        return (c, h, w);
    }

    [Fact]
    public void A_mine_designs_dig_goes_to_a_miner_and_not_to_a_pure_builder()
    {
        var (c, h, _) = BuilderAndMiner(new Clock(Start));
        // Before: the digs were building work, so the builder took them and the miner found nothing.
        Assert.Null(c.Candidate(WorkTypes.Build, c.Pawns[c.P(0)], h.Live(c.P(0))));
        Assert.NotEqual("dig", Str(c.Next(0), "kind"));
        var dig = c.Next(1);
        Assert.Equal("dig", Str(dig, "kind"));
        Assert.Equal("5,63,5", Str(dig, "at"));
        Assert.Equal(WorkTypes.Mine, Str(dig, "type"));
        // The pawn's row in the colony state says so too (the Controller's badge and glyph follow it).
        var row = c.State()["pawns"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == c.P(1))!;
        Assert.Equal(WorkTypes.Mine, row["job"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void A_builder_still_places_the_mine_designs_torch_while_the_miner_digs()
    {
        var (c, _, w) = BuilderAndMiner(new Clock(Start), torch: true);
        var torch = c.Next(0);
        Assert.Equal("place", Str(torch, "kind"));
        Assert.Equal("4,64,5", Str(torch, "at"));
        Assert.Equal(WorkTypes.Build, Str(torch, "type"));
        var dig = c.Next(1);
        Assert.Equal("dig", Str(dig, "kind"));
        Assert.Equal(WorkTypes.Mine, Str(dig, "type"));
        // The torch goes in; the builder is done with the mine, and its digs stay the miner's.
        w.Set(4, 64, 5, 50, 5);
        Assert.True(c.Report(c.P(0), Str(torch, "id"), "done", null, null));
        Assert.NotEqual("dig", Str(c.Next(0), "kind"));
    }

    [Fact]
    public void A_pure_builder_with_only_mine_design_digs_about_is_idle_for_priority_0()
    {
        var clock = new Clock(Start);
        var (c, _, _) = BuilderAndMiner(clock);
        Assert.NotEqual("dig", Str(c.Next(0), "kind"));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        // There is work, of a kind it doesn't do: the miner's.
        Assert.Equal("priority 0", c.IdleReasonOf(c.P(0)));
    }

    [Fact]
    public void A_miner_with_no_free_slot_digs_no_mine_design_cell_until_it_has_one()
    {
        var (c, h, _) = BuilderAndMiner(new Clock(Start));
        h.Lives[c.P(1)] = FakeStoryHost.Live(6, 64, 3, inv: new Stack(4, 0, 35 * 64)) with { FreeSlots = 0 };
        Assert.Null(c.Candidate(WorkTypes.Mine, c.Pawns[c.P(1)], h.Live(c.P(1))));
        h.Lives[c.P(1)] = FakeStoryHost.Live(6, 64, 3);
        Assert.Equal("5,63,5", Str(c.Candidate(WorkTypes.Mine, c.Pawns[c.P(1)], h.Live(c.P(1)))!.Json, "at"));
    }

    [Fact]
    public void A_saved_mine_build_and_the_vein_it_found_as_building_work_load_as_mining_work()
    {
        using var db = AgentDb.Open(null);
        var clock = new Clock(Start);
        var w = new FakeStoryWorld();
        var h = new FakeStoryHost(w);
        var c = new StoryColony(new ColonyMeta { Id = "story-1", Name = "Test", Home = new P3(0, 64, 0) }, h, clock, new StoryStore(db));
        var (accounts, _) = h.Spawn("story-1", 2, null);
        c.Joined(accounts);
        foreach (var p in c.Pawns.Values) p.Schedule = Enumerable.Repeat(ScheduleBlocks.Anything, 24).ToArray();
        Only(c.Pawns[c.P(0)], WorkTypes.Build);
        Only(c.Pawns[c.P(1)], WorkTypes.Mine);
        Assert.Null(c.BuildsOp("add", new JsonArray(Shaft()), null).Error);
        var mine = c.Builds.Single();
        // Ore its digs found while they were building work: the vein's origin was saved with the work type "build".
        var ore = new P3(2, 64, 0);
        w.Set(ore.X, ore.Y, ore.Z, 15);
        c.Designations.Add(new DesignationState
        {
            Id = "d-old", Kind = "mine", At = ore, Auto = true, Ore = 15, OreOrigin = new OreWorkOrigin("build", mine.Id, WorkTypes.Build),
            Label = "following iron ore vein", Created = Start,
        });
        c.Save(force: true);
        var h2 = new FakeStoryHost(w);
        var loaded = Assert.Single(new StoryStore(db).Load(meta => new StoryColony(meta, h2, clock)));
        // Loaded as saved: no migration rewrote the build or the mark.
        Assert.Equal("mine", loaded.Builds.Single().Kind);
        Assert.Equal(JsonSerializer.Serialize(mine, StoryJson.Options), JsonSerializer.Serialize(loaded.Builds.Single(), StoryJson.Options));
        Assert.Equal(WorkTypes.Build, loaded.Designations.Single().OreOrigin!.Type);
        h2.Lives[c.P(0)] = FakeStoryHost.Live(5, 64, 3);
        h2.Lives[c.P(1)] = FakeStoryHost.Live(2, 64, 2);
        Assert.Equal(WorkTypes.Mine, loaded.State()["designations"]!.AsArray().Single()!["work_type"]!.GetValue<string>());
        // The builder gets neither; the miner takes the vein (nearest work first: its own search), then the shaft.
        Assert.NotEqual("dig", Str(loaded.Next(0), "kind"));
        var vein = loaded.Next(1);
        Assert.Equal(ore.ToString(), Str(vein, "at"));
        Assert.Equal(WorkTypes.Mine, Str(vein, "type"));
        w.Set(ore.X, ore.Y, ore.Z, 0);
        Assert.True(loaded.Report(c.P(1), Str(vein, "id"), "done", null, null));
        var dig = loaded.Next(1);
        Assert.Equal("5,63,5", Str(dig, "at"));
        Assert.Equal(WorkTypes.Mine, Str(dig, "type"));
    }
}
