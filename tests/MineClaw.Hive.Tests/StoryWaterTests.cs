using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Water (docs/web/story-mode.md "Water"; Fox 11-04: "make them pour water down in different blocks so that I can make aquifers and
/// hydroponics"): water items in the build queue, their checks and expected flow, the pour job (bucket, fill source, every other cell
/// first), bucket crafting, infinite sources and the irrigation planner.
/// </summary>
public sealed class StoryWaterTests
{
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, 0);

    /// <summary>The director's clock (the fill-source scan is kept 10 s: a test that changes the water moves it on).</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private void Later() => _clock.Now += TimeSpan.FromSeconds(11);

    private (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(IGameData? data = null, params Stack[] carried)
    {
        var (c, h, w) = StoryRig.Make(1, data, _clock);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(2, 64, 4, inv: carried);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        c.StorageOp("set", [Chest], null, null, null);
        return (c, h, w);
    }

    /// <summary>A still pool of <paramref name="n"/>×<paramref name="n"/> sources dug into the stone (top at y 63).</summary>
    private static void Pool(FakeStoryWorld w, int x, int z, int n = 2)
    {
        for (int dx = 0; dx < n; dx++) for (int dz = 0; dz < n; dz++) w.Set(x + dx, 63, z + dz, 9);
    }

    private static JsonArray Cells(params (int X, int Y, int Z)[] cells) => new(cells.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray());
    private static JsonArray Line(int x1, int x2, int y, int z) => Cells(Enumerable.Range(x1, x2 - x1 + 1).Select(x => (x, y, z)).ToArray());
    private static List<string> JobCells(JsonObject j) => j["cells"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private static (JsonNode? Result, string? Error) Add(StoryColony c, params JsonObject[] items) => c.BuildsOp("add", new JsonArray(items), null);

    [Fact]
    public void A_channel_on_open_ground_is_queued_with_a_spread_warning_and_its_flow()
    {
        var (c, _, _) = Rig();
        var r = Add(c, new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 9, 64, 12) });
        Assert.Null(r.Error);
        var b = c.Builds.Single();
        Assert.Equal("water channel (6 sources)", b.Label);
        Assert.All(b.Ghosts, g => Assert.Equal((9, 0, "water_bucket"), (g.Id, g.Meta, g.Item)));
        // Flat stone around it: the water runs 7 blocks to each side. Fox wants deliberate placement: said, with the cells.
        Assert.Contains(r.Result!["warnings"]!.AsArray(), x => x!.GetValue<string>().Contains("will spread to"));
        Assert.True(r.Result!["flow"]!["spread"]!.GetValue<int>() > 20);
        // The ghosts are pour cells (the holograms show them as water).
        var ghost = c.State()["ghosts"]!["blocks"]!.AsArray()[0]!.AsArray();
        Assert.Equal(9, ghost[3]!.GetValue<int>());
        Assert.Equal("pour", ghost[7]!.GetValue<string>());
        Assert.Equal("water", c.State()["builds"]![0]!["liquid"]!.GetValue<string>());
    }

    [Fact]
    public void A_cell_holding_a_block_is_refused_unless_its_group_digs_it_first_and_then_the_dig_goes_before_the_pour()
    {
        var (c, h, w) = Rig(carried: new Stack(325, 0, 1));
        Pool(w, 0, 20);
        var water = new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 6, 63, 12) };
        var refused = Add(c, (JsonObject)water.DeepClone());
        Assert.Contains("holds stone: dig it first", refused.Error);
        Assert.Empty(c.Builds);
        // A trench dug by the same group: accepted, and the water stays in it (no spread).
        var dig = new JsonObject { ["kind"] = "remove", ["blocks"] = Line(4, 6, 63, 12), ["group"] = "canal" };
        water["group"] = "canal";
        var r = Add(c, dig, water);
        Assert.Null(r.Error);
        Assert.Null(r.Result!["warnings"]);
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>()); // the trench first
        // Dig every trench cell, then the water goes in.
        while (job["kind"]!.GetValue<string>() == "dig")
        {
            var at = job["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
            w.Set(at[0], at[1], at[2], 0);
            Assert.True(c.Report(c.P(), Id(job), "done", null, null));
            job = c.Next();
        }
        Assert.Equal("pour", job["kind"]!.GetValue<string>());
        Assert.Equal("water", job["liquid"]!.GetValue<string>());
        // Every other cell first (the one between two sources fills by itself), from one stand, filled at the pool.
        Assert.Equal(["4,63,12", "6,63,12", "5,63,12"], JobCells(job));
        Assert.NotNull(job["stand"]);
        var src = job["source"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        Assert.Equal(9, w.Block(src[0], src[1], src[2]).Id);
        Assert.Null(job["fetch"]); // its own bucket
        // A cell reported done that shows a source is done; one that shows flowing water is not.
        w.Set(4, 63, 12, 8);
        c.Report(c.P(), Id(job), "progress", "cell 4,63,12 done", null);
        w.Set(6, 63, 12, 9, 3);
        c.Report(c.P(), Id(job), "progress", "cell 6,63,12 done", null);
        var b = c.Builds.Single(x => x.Kind == "water");
        Assert.Equal("done", b.Ghosts.Single(g => g.At == new P3(4, 63, 12)).State);
        Assert.NotEqual("done", b.Ghosts.Single(g => g.At == new P3(6, 63, 12)).State);
        // Already a source (it filled by itself): done at the end of the batch without a pour.
        w.Set(5, 63, 12, 9);
        c.Report(c.P(), Id(job), "done", "batch: 1/3 poured", null);
        Assert.Equal("done", b.Ghosts.Single(g => g.At == new P3(5, 63, 12)).State);
    }

    [Fact]
    public void Fill_sources_prefer_an_infinite_pool_the_colony_made_then_a_lake_never_a_lone_puddle()
    {
        var (c, h, w) = Rig(carried: new Stack(325, 0, 1));
        var near = new P3(10, 64, 10);
        w.Set(11, 63, 10, 9); // a lone puddle right beside: scooping it uses it up
        Assert.Null(c.FillSource(w, false, near, new HashSet<P3>()));
        Pool(w, 18, 10, 3); // a lake 8 away (its middle cells refill)
        Later();
        var lake = c.FillSource(w, false, near, new HashSet<P3>());
        Assert.NotNull(lake);
        Assert.InRange(lake!.Value.X, 18, 20);
        // The colony's own infinite source (a 2×2 the pawns poured) is preferred, even farther.
        foreach (var (x, z) in new[] { (0, 24), (1, 24), (0, 25), (1, 25) }) w.Set(x, 63, z, 0); // dug out by hand
        var r = Add(c, new JsonObject { ["kind"] = "water", ["blocks"] = Cells((0, 63, 24), (1, 63, 24), (0, 63, 25), (1, 63, 25)), ["infinite"] = true });
        Assert.Null(r.Error);
        var pool = c.Builds.Single();
        Assert.Equal("infinite water source (4 cells)", pool.Label);
        var job = c.Next();
        var order = JobCells(job).Select(x => x.Split(',').Select(int.Parse).ToArray()).Select(a => new P3(a[0], a[1], a[2])).ToList();
        Assert.Equal(4, order.Count);
        Assert.True(order[0].X != order[1].X && order[0].Z != order[1].Z, "two opposite corners first: the others fill by themselves");
        Assert.False(c.Renewable(pool));
        foreach (var (x, z) in new[] { (0, 24), (1, 24), (0, 25), (1, 25) }) w.Set(x, 63, z, 9);
        foreach (var cell in JobCells(job)) c.Report(c.P(), Id(job), "progress", $"cell {cell} done", null);
        Assert.Equal("done", pool.Status);
        Assert.True(c.Renewable(pool));
        Assert.Contains(c.Alerts, a => a.Text.Contains("refills: pawns fill their buckets there"));
        Later();
        var best = c.FillSource(w, false, near, new HashSet<P3>());
        Assert.InRange(best!.Value.Z, 24, 25);
        Assert.InRange(best.Value.X, 0, 1);
    }

    [Fact]
    public void Water_the_colony_poured_that_doesnt_refill_is_never_scooped()
    {
        var (c, _, w) = Rig(carried: new Stack(325, 0, 1));
        Pool(w, 16, 20, 3);
        Assert.Null(Add(c, new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 5, 64, 12) }).Error);
        w.Set(4, 64, 12, 9);
        w.Set(5, 64, 12, 9);
        var job = c.Next();
        c.Report(c.P(), Id(job), "done", "batch: 2/2 poured", null);
        Assert.Equal("done", c.Builds.Single().Status);
        var s = c.FillSource(w, false, new P3(5, 64, 12), new HashSet<P3>());
        Assert.NotNull(s);
        Assert.True(s!.Value.X >= 16, $"took {s} (the channel's own water)");
    }

    [Fact]
    public void A_pawn_without_a_bucket_fetches_one_from_storage_and_with_none_anywhere_a_bucket_is_crafted_for_the_build()
    {
        var (c, h, w) = Rig(Release1122GameData.Instance);
        Pool(w, 0, 20);
        c.AddContents(c.StorageAt(Chest)!, new Stack(325, 0, 3));
        Assert.Null(Add(c, new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 6, 64, 12) }).Error);
        var job = c.Next();
        Assert.Equal("pour", job["kind"]!.GetValue<string>());
        var fetch = job["fetch"]!.AsArray().Single()!;
        Assert.Equal(("bucket", 2, Chest.ToString()), (fetch["item"]!.GetValue<string>(), fetch["n"]!.GetValue<int>(), fetch["from"]!.GetValue<string>()));
        Assert.Equal(2, job["buckets"]!.GetValue<int>());
        // The last liquid cells: the buckets go back after.
        Assert.Equal(Chest.ToString(), job["stash"]![0]!["to"]!.GetValue<string>());

        // No bucket anywhere: iron ingots in storage and a colony table → an auto bill makes one; the build waits for it.
        var (c2, h2, w2) = Rig(Release1122GameData.Instance);
        Pool(w2, 0, 20);
        c2.AddContents(c2.StorageAt(Chest)!, new Stack(265, 0, 5));
        w2.Set(Table.X, Table.Y, Table.Z, 58);
        Assert.Null(c2.WorkbenchOp("designate", Table).Error);
        Assert.Null(Add(c2, new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 6, 64, 12) }).Error);
        var wait = c2.Next();
        Assert.NotEqual("pour", wait["kind"]!.GetValue<string>());
        var b = c2.Builds.Single();
        Assert.Equal("waiting", b.Status);
        Assert.Contains("bucket", b.Reason);
        var mat = b.Materials.Single();
        Assert.Equal(("bucket", 1, 1), (mat.Item, mat.Need, mat.Crafting)); // one bucket for the channel, not one per cell
        var bill = c2.Benches.SelectMany(x => x.Bills).Single(x => x.ForBuild == b.Id);
        Assert.Equal("craft:bucket", bill.Recipe);
        // Nothing to make one from: blocked, said once.
        var (c3, _, w3) = Rig(Release1122GameData.Instance);
        Pool(w3, 0, 20);
        Assert.Null(Add(c3, new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 6, 64, 12) }).Error);
        c3.Next();
        Assert.Equal("blocked", c3.Builds.Single().Status);
        Assert.Contains("needs a bucket", c3.Builds.Single().Reason);
    }

    [Fact]
    public void No_still_water_to_fill_from_blocks_the_build_unless_the_pawn_carries_a_full_bucket()
    {
        var (c, h, _) = Rig(carried: new Stack(325, 0, 1));
        Assert.Null(Add(c, new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 6, 64, 12) }).Error);
        var idle = c.Next();
        var b = c.Builds.Single();
        Assert.Equal("blocked", b.Status);
        c.Report(c.P(), Id(idle), "done", null, null);
        Assert.Contains("no still water within", b.Reason);
        // A full bucket in hand: it pours that one (one cell), no source needed.
        h.Lives[c.P()] = FakeStoryHost.Live(2, 64, 4, inv: new Stack(326, 0, 1));
        var job = c.Next();
        Assert.Equal("pour", job["kind"]!.GetValue<string>());
        Assert.Single(JobCells(job));
        Assert.Null(job["source"]);
    }

    [Fact]
    public void Lava_needs_the_colony_option_and_is_never_poured_at_home_or_beside_wood()
    {
        var (c, _, w) = Rig();
        var lava = new JsonObject { ["kind"] = "water", ["material"] = "lava", ["blocks"] = Line(10, 11, 64, 30) };
        Assert.Contains("allow_lava", Add(c, (JsonObject)lava.DeepClone()).Error);
        Assert.False(c.Architect()["entries"]!.AsArray().Any(e => e!["id"]!.GetValue<string>() == "lava_source"));
        c.SetWaterOptions(true);
        Assert.True(c.OptionsNow()["allow_lava"]!.GetValue<bool>());
        Assert.Contains(c.Architect()["entries"]!.AsArray(), e => e!["id"]!.GetValue<string>() == "lava_source");
        w.Set(12, 64, 31, 5); // planks beside it
        Assert.Contains("alight", Add(c, (JsonObject)lava.DeepClone()).Error);
        w.Set(12, 64, 31, 0);
        Assert.Null(c.ZoneOp("add", "home", Area.Of(8, 64, 28, 14, 66, 34)).Error);
        Assert.Contains("no lava at home", Add(c, (JsonObject)lava.DeepClone()).Error);
        Assert.Null(c.ZoneOp("remove", "home", Area.Of(8, 64, 28, 14, 66, 34)).Error);
        Assert.Null(Add(c, (JsonObject)lava.DeepClone()).Error);
        Assert.Equal("lava channel (2 sources)", c.Builds.Single().Label);
        Assert.Equal((11, "lava_bucket"), (c.Builds.Single().Ghosts[0].Id, c.Builds.Single().Ghosts[0].Item));
    }

    [Fact]
    public void Water_indoors_needs_the_explicit_tool_and_water_never_goes_onto_lava()
    {
        var (c, _, w) = Rig();
        Assert.Null(c.ZoneOp("add", "home", Area.Of(20, 64, 20, 24, 66, 24)).Error);
        for (int x = 20; x <= 24; x++) for (int z = 20; z <= 24; z++) w.Set(x, 67, z, 5); // a roof
        var item = new JsonObject { ["kind"] = "water", ["blocks"] = Cells((22, 64, 22)) };
        Assert.Contains("indoors", Add(c, (JsonObject)item.DeepClone()).Error);
        item["indoor"] = true;
        Assert.Null(Add(c, item).Error);
        w.Set(30, 64, 30, 11);
        Assert.Contains("obsidian", Add(c, new JsonObject { ["kind"] = "water", ["blocks"] = Cells((30, 64, 30)) }).Error);
    }

    [Fact]
    public void A_cell_already_holding_a_source_is_done_at_once_and_preview_queues_nothing()
    {
        var (c, _, w) = Rig();
        w.Set(4, 64, 12, 9);
        var items = new JsonArray(new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 5, 64, 12) });
        var p = c.BuildsOp("preview", items, null);
        Assert.Null(p.Error);
        Assert.Equal((2, 1), (p.Result!["cells"]!.GetValue<int>(), p.Result!["done"]!.GetValue<int>()));
        Assert.True(p.Result!["flow"]!["spread"]!.GetValue<int>() > 0);
        Assert.Empty(c.Builds);
        var bad = c.BuildsOp("preview", new JsonArray(new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 5, 60, 12) }), null);
        Assert.Contains("dig it first", bad.Result!["error"]!.GetValue<string>());
        Assert.Null(Add(c, new JsonObject { ["kind"] = "water", ["blocks"] = Line(4, 5, 64, 12) }).Error);
        Assert.Equal(1, c.Builds.Single().BlocksDone);
    }

    [Fact]
    public void The_water_tab_lists_sources_channels_and_the_infinite_pool_at_the_buckets_tier()
    {
        var (c, h, w) = Rig(Release1122GameData.Instance);
        var arch = c.Architect();
        Assert.Contains("water", arch["tabs"]!.AsArray().Select(t => t!.GetValue<string>()));
        var water = arch["entries"]!.AsArray().Where(e => e!["tab"]!.GetValue<string>() == "water").ToList();
        Assert.Equal(["water_source", "water_channel", "infinite_water"], water.Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal(["water", "water_channel", "infinite_water"], water.Select(e => e!["kind"]!.GetValue<string>()));
        Assert.All(water, e => Assert.Equal("workbench", e!["tier"]!.GetValue<string>()));
        Assert.All(water, e => Assert.False(e!["unlocked"]!.GetValue<bool>())); // no bucket, no table
        // A bucket in a pawn's hand unlocks them.
        h.Lives[c.P()] = FakeStoryHost.Live(2, 64, 4, inv: new Stack(325, 0, 1));
        c.MaterialsChanged();
        var again = c.Architect()["entries"]!.AsArray().First(e => e!["id"]!.GetValue<string>() == "water_source")!;
        Assert.True(again["unlocked"]!.GetValue<bool>());
        Assert.Equal(1, again["have"]!.GetValue<int>());
    }

    [Fact]
    public void Irrigation_for_a_dry_wheat_field_suggests_a_channel_every_9th_row_at_soil_level()
    {
        var (c, _, w) = Rig();
        // A 20×12 grass field at y 63 (soil), nothing to water it.
        for (int x = 0; x < 20; x++) for (int z = 40; z < 52; z++) w.Set(x, 63, z, 2);
        var add = c.ZoneOp("add", "farm", Area.Of(0, 64, 40, 19, 64, 51), grow: new JsonObject { ["plant"] = "wheat" });
        Assert.Null(add.Error);
        var id = add.Result!["id"]!.GetValue<string>();
        var r = c.ZoneOp("irrigation", null, null, id);
        Assert.Null(r.Error);
        Assert.Equal("x", r.Result!["axis"]!.GetValue<string>()); // along the longer side
        var cells = r.Result!["cells"]!.AsArray().Select(P3.Parse).Select(p => p!.Value).ToList();
        // 12 rows across: a line at z 44 waters 40..48, one more by the far edge (z 51) for 49..51.
        Assert.Equal([44, 51], cells.Select(p => p.Z).Distinct().OrderBy(z => z));
        Assert.All(cells, p => Assert.Equal(63, p.Y)); // at soil level: dug first
        Assert.Equal(40, cells.Count);
        Assert.Equal(40, r.Result!["dig"]!.AsArray().Count);
        Assert.Contains("2 channels", r.Result!["note"]!.GetValue<string>());
        // Queued as the web does (dig, then pour, one group): accepted.
        var dig = new JsonObject { ["kind"] = "remove", ["blocks"] = r.Result!["dig"]!.DeepClone(), ["group"] = "irr" };
        var water = new JsonObject { ["kind"] = "water", ["blocks"] = r.Result!["cells"]!.DeepClone(), ["group"] = "irr" };
        Assert.Null(Add(c, dig, water).Error);
        // Watered: once the channel is there, nothing more is suggested.
        foreach (var p in cells) w.Set(p.X, p.Y, p.Z, 9);
        var again = c.ZoneOp("irrigation", null, null, id);
        Assert.Empty(again.Result!["cells"]!.AsArray());
        Assert.Contains("watered already", again.Result!["note"]!.GetValue<string>());
    }

    [Fact]
    public void Sugar_cane_irrigation_puts_water_beside_every_cane_row()
    {
        var (c, _, w) = Rig();
        for (int x = 0; x < 10; x++) for (int z = 60; z < 66; z++) w.Set(x, 63, z, 12); // sand
        w.Set(0, 63, 59, 9); // one cell of it beside water (else the zone is refused)
        var add = c.ZoneOp("add", "farm", Area.Of(0, 64, 60, 9, 64, 65), grow: new JsonObject { ["plant"] = "sugar_cane" });
        Assert.Null(add.Error);
        var r = c.ZoneOp("irrigation", null, null, add.Result!["id"]!.GetValue<string>());
        var zs = r.Result!["cells"]!.AsArray().Select(P3.Parse).Select(p => p!.Value.Z).Distinct().OrderBy(z => z).ToList();
        Assert.Equal([61, 64], zs); // every 3rd row: each cane row has water right beside it
    }

    [Fact]
    public void Pour_order_takes_every_other_cell_first()
    {
        GhostState G(int x, int z) => new() { At = new P3(x, 64, z), Id = 9, Item = "water_bucket" };
        var line = Enumerable.Range(0, 5).Select(x => G(x, 0)).ToList();
        Assert.Equal([0, 2, 4, 1, 3], StoryColony.PourOrder(line).Select(g => g.At.X));
    }
}
