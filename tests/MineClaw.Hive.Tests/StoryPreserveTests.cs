using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Preserve zones (docs/web/story-mode.md "Preserve zones"; Fox 11-06: "my wheat field has taken some hits — craters from creepers. If I
/// could protect its state with the same mechanic, the pawns would keep the flat land it farms on"). A crater in a preserved growing zone
/// becomes repair work (dirt placed, then tilled by farming) and harvest, replanting, tilling and trampling never count as changes; a
/// foreign block is cleared and the block put back; a foreign block in an open cell is dug out by an ordered clear; the range takes cells
/// in and out; an owner's build is said, held while it stands and kept as it leaves the cells; other digs are refused preserved blocks;
/// the snapshot survives a restart.
/// </summary>
public sealed class StoryPreserveTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    public static TheoryData<string> Versions => new() { "1.12.2", "b1.8.1" };

    private static IGameData Data(string v) => v == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;

    private static readonly P3 Chest = new(-6, 64, 12);

    /// <summary>A colony whose one pawn does only <paramref name="work"/>, a storage chest with <paramref name="stock"/>.</summary>
    private (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(string v, string work, params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(1, Data(v), _clock);
        Only(c, work);
        h.Lives[c.P()] = FakeStoryHost.Live(-3, 64, 12, inv: [new Stack(290, 0, 1), new Stack(295, 0, 16)]); // a hoe and seeds
        w.Set(Chest.X, Chest.Y, Chest.Z, 54, 3);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        return (c, h, w);
    }

    private static void Only(StoryColony c, string work)
    {
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == work ? 1 : 0;
    }

    /// <summary>A 6×6 wheat field at y 63 (x 0..5, z 10..15), water in its middle, wheat growing on the farmland.</summary>
    private static void Field(FakeStoryWorld w)
    {
        for (int x = 0; x <= 5; x++)
            for (int z = 10; z <= 15; z++)
            {
                if (x == 2 && z == 12) { w.Set(x, 63, z, 9); continue; }
                w.Set(x, 63, z, 60, 7);
                w.Set(x, 64, z, 59, 3);
            }
    }

    private static string Op(StoryColony c, string op, string? kind, Area? area, JsonObject? body = null, string? id = null)
    {
        var (res, err) = c.ZoneOp(op, kind, area, id, grow: body);
        Assert.True(err == null, err);
        return res!["id"]!.GetValue<string>();
    }

    private static string GrowingField(StoryColony c, int below = 1)
    {
        var id = Op(c, "add", "farm", Area.Of(0, 63, 10, 5, 63, 15), new JsonObject { ["plant"] = "wheat" });
        Op(c, "preserve", null, null, new JsonObject { ["below"] = below }, id);
        return id;
    }

    private static ZoneState Z(StoryColony c, string id) => c.Zones.Single(z => z.Id == id);

    private static JsonObject PreserveOf(StoryColony c, string id) =>
        c.State()["zones"]!.AsArray().Select(z => z!.AsObject()).Single(z => z["id"]!.GetValue<string>() == id)["preserve"]!.AsObject();

    private static BuildState? Repairs(StoryColony c, string zone) =>
        Z(c, zone).Preserve?.Build is { } b ? c.Builds.SingleOrDefault(x => x.Id == b) : null;

    private static List<string> Strs(JsonNode? a) => a?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private void Later() => _clock.Now += TimeSpan.FromSeconds(6);

    [Theory, MemberData(nameof(Versions))]
    public void A_crater_in_a_preserved_wheat_field_is_filled_with_dirt_and_tilled_again(string v)
    {
        var (c, _, w) = Rig(v, WorkTypes.Build, new Stack(3, 0, 16));
        Field(w);
        var id = GrowingField(c);
        Assert.Equal(62, PreserveOf(c, id)["y1"]!.GetValue<int>());
        Assert.Equal(66, PreserveOf(c, id)["y2"]!.GetValue<int>());
        Assert.Equal("intact", PreserveOf(c, id)["status"]!.GetValue<string>());
        // A creeper: ten blocks of soil gone, the wheat on them with it.
        var crater = new List<P3>();
        for (int x = 0; x <= 4; x++)
            for (int z = 10; z <= 11; z++) { w.Set(x, 63, z, 0); w.Set(x, 64, z, 0); crater.Add(new P3(x, 63, z)); }
        c.Reconcile(w);
        var b = Repairs(c, id)!;
        Assert.Equal("preserve", b.Kind);
        Assert.Equal(crater.Select(p => p.ToString()).Order().ToList(), b.Ghosts.Select(g => g.At.ToString()).Order().ToList());
        Assert.All(b.Ghosts, g => Assert.Equal((3, 0), (g.Id, g.Meta))); // dirt: farmland is tilled again by farming, not placed
        Assert.Equal("10 blocks to repair: 10 missing", PreserveOf(c, id)["status"]!.GetValue<string>());
        Assert.Single(c.Alerts, a => a.Text.Contains("10 blocks changed (10 missing)"));
        // A builder fills it from storage.
        var j = c.Next();
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal(w.Data.Item(3).Name, j["block"]!.GetValue<string>());
        var cells = Strs(j["cells"]);
        Assert.True(cells.Count >= 9, string.Join(" ", cells)); // one may be its stand, left for the next batch
        foreach (var p in crater) w.Set(p.X, p.Y, p.Z, 3);
        foreach (var cell in cells) c.Report(c.P(), Id(j), "progress", $"cell {cell} done", null);
        c.Report(c.P(), Id(j), "done", $"batch: {cells.Count}/{cells.Count} placed", null);
        c.Reconcile(w);
        Assert.Empty(b.Ghosts);
        Assert.Equal("done", b.Status);
        Assert.Equal("intact", PreserveOf(c, id)["status"]!.GetValue<string>());
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains($"Build {b.Id}") && a.Text.Contains("is done")); // the zone says it, not a build alert
        // Farming takes the dirt back: tilled (it is within 4 of the water), then sown.
        Only(c, WorkTypes.Farm);
        Later();
        var till = c.Next();
        Assert.Equal("till", till["kind"]!.GetValue<string>());
        Assert.Contains(crater, p => p.ToString() == till["at"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Versions))]
    public void Harvest_replanting_tilling_and_trampling_are_never_changes_over_several_growth_cycles(string v)
    {
        var (c, _, w) = Rig(v, WorkTypes.Build, new Stack(3, 0, 16));
        Field(w);
        var id = GrowingField(c);
        int alerts = c.Alerts.Count;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            for (int x = 0; x <= 5; x++) w.Set(x, 64, 13, 59, 7); // ripe
            c.Reconcile(w);
            for (int x = 0; x <= 5; x++) w.Set(x, 64, 13, 0); // harvested
            w.Set(0, 63, 13, 3); // trampled back to dirt
            w.Set(1, 63, 13, 2); // grass spread over it
            c.Reconcile(w);
            w.Set(0, 63, 13, 60); // tilled again
            w.Set(1, 63, 13, 60);
            for (int x = 0; x <= 5; x++) w.Set(x, 64, 13, 59, 0); // sown again
            w.Set(4, 65, 14, 31, 1); // tall grass in the air over the crops
            w.Set(5, 65, 15, 78);    // a snow layer
            c.Reconcile(w);
        }
        Assert.True(Repairs(c, id) is null || Repairs(c, id)!.Ghosts.Count == 0);
        Assert.Equal(alerts, c.Alerts.Count);
        Assert.Equal("intact", PreserveOf(c, id)["status"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_foreign_block_is_cleared_and_the_block_put_back_and_one_in_an_open_cell_is_dug_out_by_an_ordered_clear(string v)
    {
        var (c, _, w) = Rig(v, WorkTypes.Build, new Stack(5, 0, 8));
        for (int x = 0; x <= 3; x++) w.Set(x, 64, 20, 5); // a planks wall
        var id = Op(c, "add", "preserve", Area.Of(0, 64, 20, 3, 64, 20), new JsonObject { ["below"] = 0, ["above"] = 1 });
        Assert.Equal("preserve", Z(c, id).Kind);
        Assert.Equal(PreserveSnap.Pack(5, 0), Z(c, id).Preserve!.Data!.At(1, 64, 20));
        w.Set(1, 64, 20, 4); // cobblestone where a plank was
        c.Reconcile(w);
        var g = Repairs(c, id)!.Ghosts.Single();
        Assert.Equal((5, 0, false), (g.Id, g.Meta, g.Remove));
        Assert.Equal("clear", g.Holds); // the map shows it: dug out first
        Assert.Contains("1 foreign", PreserveOf(c, id)["status"]!.GetValue<string>());
        var j = c.Next();
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal(["1,64,20"], Strs(j["clear"]));
        w.Set(1, 64, 20, 5);
        c.Report(c.P(), Id(j), "progress", "cell 1,64,20 done", null);
        c.Report(c.P(), Id(j), "done", "batch: 1/1 placed", null);
        c.Reconcile(w);
        Assert.Empty(Repairs(c, id)!.Ghosts);
        Assert.Contains("1,64,20", Strs(c.State(0)["removed"]?["ghosts"]).Select(s => s.Split('|')[1])); // the web drops its hologram
        // A block dropped on top of the wall (an open cell in the snapshot): dug out with the owner's clear.
        w.Set(2, 65, 20, 4);
        c.Reconcile(w);
        var dig = c.Next();
        Assert.Equal("dig", dig["kind"]!.GetValue<string>());
        Assert.Equal("2,65,20", dig["at"]!.GetValue<string>());
        Assert.True(dig["ordered"]!.GetValue<bool>());
    }

    [Fact]
    public void Changing_the_range_takes_cells_in_now_and_drops_the_ones_left_out()
    {
        var (c, _, w) = Rig("1.12.2", WorkTypes.Build);
        Field(w);
        var id = GrowingField(c);
        Assert.Null(Z(c, id).Preserve!.Data!.At(0, 60, 10));
        w.Set(0, 60, 10, 3); // dirt down there when the range grows: that is what is kept
        var (res, err) = c.ZoneOp("preserve", null, null, id, grow: new JsonObject { ["below"] = 3, ["above"] = 0 });
        Assert.Null(err);
        Assert.Equal(60, res!["preserve"]!["y1"]!.GetValue<int>());
        Assert.Equal(63, res["preserve"]!["y2"]!.GetValue<int>());
        var snap = Z(c, id).Preserve!.Data!;
        Assert.Equal(PreserveSnap.Pack(3, 0), snap.At(0, 60, 10));
        Assert.Equal(PreserveSnap.Pack(60, 7), snap.At(1, 63, 10)); // kept from before
        Assert.Null(snap.At(0, 64, 10)); // the crop layer is out now
        w.Set(0, 61, 10, 0);
        w.Set(3, 66, 12, 4); // out of the range: nobody's business
        c.Reconcile(w);
        Assert.Equal([new P3(0, 61, 10)], Repairs(c, id)!.Ghosts.Select(g => g.At));
        Assert.Contains("0..32", c.ZoneOp("preserve", null, null, id, grow: new JsonObject { ["below"] = 40 }).Error);
    }

    [Fact]
    public void A_build_over_preserved_cells_is_said_held_while_it_stands_and_kept_as_it_leaves_them()
    {
        var (c, _, w) = Rig("1.12.2", WorkTypes.Build, new Stack(5, 0, 8));
        var id = Op(c, "add", "preserve", Area.Of(0, 64, 30, 3, 64, 30), new JsonObject { ["below"] = 1, ["above"] = 2 });
        var item = new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(0, 65, 30), new JsonArray(1, 65, 30)), ["material"] = new JsonArray(5, 0),
        };
        var (added, err) = c.BuildsOp("add", new JsonArray(item), null);
        Assert.Null(err);
        Assert.Contains(Strs(added!["warnings"]), s => s.Contains("preserved zone") && s.Contains("the build's while it stands"));
        var build = c.Builds.Single(b => b.Kind == "block");
        w.Set(0, 65, 30, 5); // built
        w.Set(3, 63, 30, 0); // and a block of the ground under the zone gone
        c.Reconcile(w);
        Assert.Equal([new P3(3, 63, 30)], Repairs(c, id)!.Ghosts.Select(g => g.At)); // the build's cells are its own
        Assert.True(PreserveOf(c, id)["held"]!.GetValue<int>() >= 2);
        c.BuildsOp("remove", null, [build.Id]);
        c.Reconcile(w); // the build left a plank and an empty cell: that is what the zone keeps now
        Assert.DoesNotContain(Repairs(c, id)!.Ghosts, g => g.At.Y == 65);
        Assert.Equal(PreserveSnap.Pack(5, 0), Z(c, id).Preserve!.Data!.At(0, 65, 30));
        w.Set(0, 65, 30, 0);
        c.Reconcile(w);
        Assert.Contains(Repairs(c, id)!.Ghosts, g => g.At == new P3(0, 65, 30) && g.Id == 5);
    }

    [Fact]
    public void A_quarry_over_a_preserve_zone_is_said_and_digs_its_columns_unrepaired()
    {
        var (c, _, w) = Rig("1.12.2", WorkTypes.Build);
        var id = Op(c, "add", "preserve", Area.Of(0, 63, 40, 5, 63, 45), new JsonObject { ["below"] = 2, ["above"] = 0 });
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(0, 63, 40, 2, 63, 42), grow: new JsonObject { ["mode"] = "quarry" });
        Assert.Null(err);
        Assert.Contains(Strs(res!["warnings"]), s => s.Contains("preserved zone") && s.Contains("not repaired"));
        w.Set(1, 63, 41, 0); // the quarry dug it
        w.Set(4, 63, 44, 0); // outside the quarry: a repair
        c.Reconcile(w);
        Assert.Equal([new P3(4, 63, 44)], Repairs(c, id)!.Ghosts.Select(g => g.At));
        Assert.Contains("can't preserve", c.ZoneOp("preserve", null, null, res["id"]!.GetValue<string>(), grow: new JsonObject()).Error);
    }

    [Fact]
    public void Pawn_bodies_are_never_handed_a_preserved_block_to_dig_for_other_work()
    {
        var (c, h, w) = Rig("1.12.2", WorkTypes.Build);
        Field(w);
        GrowingField(c);
        w.Set(5, 65, 15, 4); // a foreign block on the field: anyone may dig it
        c.Tick();
        var keep = h.Preserved[c.P()]!;
        Assert.True(keep(new BlockPos(1, 63, 12), 60)); // the field's farmland
        Assert.True(keep(new BlockPos(1, 63, 12), 3));  // trampled to dirt: still the field
        Assert.True(keep(new BlockPos(1, 62, 12), 1));  // the stone under it (one below)
        Assert.False(keep(new BlockPos(1, 61, 12), 1)); // under the range
        Assert.False(keep(new BlockPos(1, 64, 12), 59)); // the crop: harvested freely
        Assert.False(keep(new BlockPos(5, 65, 15), 4)); // the foreign block
        Assert.False(keep(new BlockPos(1, 63, 12), 4)); // something else in a kept cell: cleared by the repairs
        Assert.False(keep(new BlockPos(9, 63, 12), 60)); // outside the zone
        c.ZoneOp("preserve", null, null, c.Zones.Single().Id, grow: new JsonObject { ["on"] = false });
        c.Tick();
        Assert.Null(h.Preserved[c.P()]);
    }

    [Fact]
    public void The_repair_build_is_never_removed_by_hand_and_goes_when_preserve_is_switched_off()
    {
        var (c, _, w) = Rig("1.12.2", WorkTypes.Build);
        Field(w);
        var id = GrowingField(c);
        w.Set(0, 63, 10, 0);
        c.Reconcile(w);
        var b = Repairs(c, id)!;
        Assert.Contains("switch Preserve off", c.BuildsOp("remove", null, [b.Id]).Error);
        Assert.Null(c.BuildsOp("pause", null, [b.Id]).Error); // repairs can be paused
        Assert.Contains("repairs paused", PreserveOf(c, id)["status"]!.GetValue<string>());
        Op(c, "preserve", null, null, new JsonObject { ["on"] = false }, id);
        Assert.Null(Z(c, id).Preserve);
        Assert.DoesNotContain(c.Builds, x => x.Id == b.Id);
        Assert.Contains("always on", c.ZoneOp("preserve", null, null, Op(c, "add", "preserve", Area.Of(20, 63, 20, 21, 63, 21)), grow: new JsonObject { ["on"] = false }).Error);
    }

    [Fact]
    public void A_pawns_temporary_block_is_never_kept_nor_repaired_and_unloaded_columns_are_taken_once_they_load()
    {
        var (c, h, w) = Rig("1.12.2", WorkTypes.Build);
        w.Set(1, 64, 50, 3);
        c.Temporary(c.P(), "1,64,50", true, "reach pillar"); // a pawn's pillar block
        w.Unloaded.Add((3, 50));
        var id = Op(c, "add", "preserve", Area.Of(0, 64, 50, 3, 64, 50), new JsonObject { ["below"] = 0, ["above"] = 0 });
        var snap = Z(c, id).Preserve!.Data!;
        Assert.Equal(0, snap.At(1, 64, 50)); // kept as air: the temporary-block rule takes it away
        Assert.Equal(PreserveSnap.Unknown, snap.At(3, 64, 50));
        c.Reconcile(w);
        Assert.True(Repairs(c, id) is null || Repairs(c, id)!.Ghosts.Count == 0); // the pillar is the cleanup's, not a repair
        Assert.Equal(1, PreserveOf(c, id)["unknown"]!.GetValue<int>());
        w.Set(3, 64, 50, 5);
        w.Unloaded.Clear();
        c.Reconcile(w);
        Assert.Equal(PreserveSnap.Pack(5, 0), Z(c, id).Preserve!.Data!.At(3, 64, 50));
        _ = h;
    }

    [Fact]
    public void The_snapshot_is_saved_compactly_and_survives_a_restart()
    {
        using var db = AgentDb.Open(null);
        var store = new StoryStore(db);
        var w = new FakeStoryWorld(Release1122GameData.Instance);
        Field(w);
        var colonies = new StoryColonies(new FakeStoryHost(w), store);
        var (c, err) = colonies.Create("Riverside", 1, new P3(1, 64, 1));
        Assert.Null(err);
        var id = GrowingField(c!, below: 2);
        var before = c!.Zones.Single().Preserve!.Data!;
        c.Save(force: true);
        var json = db.Query("SELECT json FROM story_objects WHERE kind = 'zone'", r => r.GetString(0)).Single();
        Assert.Contains("\"snap\":", json);
        Assert.True(json.Length < 2000, $"{json.Length} chars"); // 36 columns × 7 layers in a few runs
        var again = new StoryColonies(new FakeStoryHost(w), store);
        Assert.Equal(1, again.Load());
        var z = again.Get(c.Id)!.Zones.Single(x => x.Id == id);
        var after = z.Preserve!.Data!;
        Assert.Equal((before.Lo, before.Hi, before.Cols.Count), (after.Lo, after.Hi, after.Cols.Count));
        foreach (var (k, col) in before.Cols) Assert.Equal(col, after.Cols[k]);
        Assert.Equal(2, z.Preserve.Below);
    }

    [Fact]
    public void The_codec_round_trips_unknown_held_and_every_block()
    {
        var cols = new Dictionary<long, int[]>
        {
            [ZoneCells.Key(-5, 7)] = [PreserveSnap.Pack(255, 15), PreserveSnap.Unknown, PreserveSnap.Held, 0],
            [ZoneCells.Key(-4, 7)] = [0, 0, 0, 0],
            [ZoneCells.Key(100, -300)] = [PreserveSnap.Pack(3, 0), PreserveSnap.Pack(3, 0), PreserveSnap.Pack(60, 7), PreserveSnap.Pack(2, 0)],
        };
        var s = new PreserveSnap(-2, 1, cols);
        var back = PreserveCodec.Decode(PreserveCodec.Encode(s))!;
        Assert.Equal((-2, 1), (back.Lo, back.Hi));
        foreach (var (k, col) in cols) Assert.Equal(col, back.Cols[k]);
        Assert.Null(PreserveCodec.Decode("not base64!"));
    }
}
