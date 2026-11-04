using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Zones as cell sets, painted RimWorld-style (docs/web/story-mode.md "Zones"). Fox 11-03: "Removing zones doesn't seem to work either,
/// and overlapping zones seem to create two zones over top of each other when they should just mesh into one zone. Overlapping zones
/// of a different type should overwrite what was overlapped with the type of zone selected."
/// </summary>
public sealed class StoryZonePaintingTests
{
    private static JsonObject Op(StoryColony c, string op, string? kind, Area a, string? name = null, JsonObject? body = null)
    {
        var (res, err) = c.ZoneOp(op, kind, a, name: name, grow: body);
        Assert.True(err == null, err);
        return res!.AsObject();
    }

    private static string[] Ids(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    private static ZoneState Z(StoryColony c, string id) => c.Zones.Single(z => z.Id == id);

    [Fact]
    public void Fox_remove_erases_the_dragged_cells_of_any_kind_at_any_level()
    {
        // The web's Remove zone tool sent kind "home" whatever was under it, and the bot matched the box in 3D: a growing zone painted
        // at another level was never hit. Erase is column-wise and "any kind" by default.
        var (c, _, _) = StoryRig.Make();
        var farm = Op(c, "add", "farm", Area.Of(0, 63, 0, 4, 63, 4))["id"]!.GetValue<string>();
        Assert.Empty(Ids(Op(c, "remove", "home", Area.Of(0, 70, 0, 4, 70, 4))["deleted"])); // another kind: untouched
        var r = Op(c, "remove", null, Area.Of(0, 70, 0, 1, 70, 4)); // two columns, viewed at another level
        Assert.Equal([farm], Ids(r["shrunk"]));
        Assert.Equal(15, Z(c, farm).Cells.Count);
        Assert.False(Z(c, farm).Contains(1, 2));
        r = Op(c, "remove", "any", Area.Of(-10, 0, -10, 10, 0, 10));
        Assert.Equal([farm], Ids(r["deleted"]));
        Assert.Equal([farm], Ids(r["removed"]));
        Assert.Empty(c.Zones);
    }

    [Fact]
    public void Overlapping_or_touching_zones_of_a_kind_are_one_zone_and_a_bridge_merges_two()
    {
        var (c, _, _) = StoryRig.Make();
        var a = Op(c, "add", "chop", Area.Of(0, 63, 0, 4, 63, 4), "Grove")["id"]!.GetValue<string>();
        var r = Op(c, "add", "chop", Area.Of(3, 63, 3, 8, 63, 6)); // overlaps
        Assert.Equal(a, r["id"]!.GetValue<string>());
        Assert.Equal([a], Ids(r["grown"]));
        Assert.Single(c.Zones);
        Assert.Equal(25 + 24 - 4, Z(c, a).Cells.Count);
        Op(c, "add", "chop", Area.Of(9, 63, 6, 10, 63, 6)); // touches its edge
        Assert.Single(c.Zones);
        // Diagonal is not touching: a zone of its own.
        var d = Op(c, "add", "chop", Area.Of(11, 63, 7, 12, 63, 8))["id"]!.GetValue<string>();
        Assert.Equal(2, c.Zones.Count);
        Op(c, "add", "chop", Area.Of(20, 63, 20, 21, 63, 21)); // far away: a third
        long rev = c.Rev;
        // A bridge between the first and the diagonal one: they merge into the oldest (its id and name), the merged one is reported.
        r = Op(c, "add", "chop", Area.Of(11, 63, 6, 11, 63, 6));
        Assert.Equal(a, r["merged"]![0]!["into"]!.GetValue<string>());
        Assert.Equal([d], Ids(r["merged"]![0]!["from"]));
        Assert.Equal("Grove", Z(c, a).Name);
        Assert.Equal(2, c.Zones.Count); // the grove and the far one
        Assert.Equal([d], Ids(c.State(rev)["removed"]!["zones"])); // the browser drops the merged one
    }

    [Fact]
    public void A_work_zone_overwrites_other_work_zones_under_it_and_splits_them()
    {
        var (c, _, _) = StoryRig.Make();
        var chop = Op(c, "add", "chop", Area.Of(0, 63, 0, 9, 63, 2), "Woodlot")["id"]!.GetValue<string>();
        var r = Op(c, "add", "mine", Area.Of(4, 63, -5, 5, 63, 5));
        var mine = r["id"]!.GetValue<string>();
        Assert.Equal([mine], Ids(r["created"]));
        Assert.Equal([chop], Ids(r["shrunk"]));
        var split = r["split"]![0]!;
        Assert.Equal(chop, split["from"]!.GetValue<string>());
        var other = Ids(split["into"]).Single();
        Assert.Equal("Woodlot 2", Z(c, other).Name);
        Assert.Equal(12, Z(c, chop).Cells.Count);
        Assert.Equal(12, Z(c, other).Cells.Count);
        Assert.Equal(22, Z(c, mine).Cells.Count);
        // No cell is in two work zones.
        foreach (var x in Enumerable.Range(-1, 12))
            for (int z = -6; z <= 6; z++)
                Assert.True(c.Zones.Count(zn => zn.Kind is "chop" or "mine" or "farm" && zn.Contains(x, z)) <= 1);
        // Painting the quarry over the whole of a part deletes it.
        r = Op(c, "add", "mine", Area.Of(6, 63, 0, 9, 63, 2));
        Assert.Equal([other], Ids(r["deleted"]));
        Assert.Equal(mine, r["id"]!.GetValue<string>()); // it touches the quarry: the quarry grows
    }

    [Fact]
    public void Home_and_keep_out_are_overlay_layers()
    {
        var (c, _, _) = StoryRig.Make();
        var home = Op(c, "add", "home", Area.Of(0, 63, 0, 9, 63, 9))["id"]!.GetValue<string>();
        var farm = Op(c, "add", "farm", Area.Of(0, 63, 0, 4, 63, 4))["id"]!.GetValue<string>();
        var keep = Op(c, "add", "forbidden", Area.Of(2, 63, 2, 6, 63, 6))["id"]!.GetValue<string>();
        Assert.Equal(100, Z(c, home).Cells.Count);
        Assert.Equal(25, Z(c, farm).Cells.Count);
        Assert.Equal(25, Z(c, keep).Cells.Count);
        // A second house touching the first is the same home area.
        Op(c, "add", "home", Area.Of(10, 63, 0, 12, 63, 2));
        Assert.Single(c.Zones, z => z.Kind == "home");
        Assert.True(c.InHome(new P3(12, 64, 1)));
        // Erasing home only erases home.
        Op(c, "remove", "home", Area.Of(0, 63, 0, 12, 63, 12));
        Assert.DoesNotContain(c.Zones, z => z.Kind == "home");
        Assert.Equal(2, c.Zones.Count);
    }

    [Fact]
    public void Erase_shrinks_splits_and_deletes()
    {
        var (c, _, _) = StoryRig.Make();
        var z = Op(c, "add", "mine", Area.Of(0, 10, 0, 8, 12, 0), "Quarry")["id"]!.GetValue<string>();
        var r = Op(c, "remove", "mine", Area.Of(4, 0, 0, 4, 0, 0));
        Assert.Equal([z], Ids(r["shrunk"]));
        var part = Ids(r["split"]![0]!["into"]).Single();
        Assert.Equal("Quarry 2", Z(c, part).Name);
        Assert.Equal((10, 12), (Z(c, part).Area.Y1, Z(c, part).Area.Y2)); // the level range is kept
        r = Op(c, "remove", null, Area.Of(5, 0, 0, 8, 0, 0));
        Assert.Equal([part], Ids(r["deleted"]));
        Assert.Single(c.Zones);
        // By id: the whole zone.
        Assert.Equal([z], Ids(c.ZoneOp("remove", null, null, z).Result!["removed"]));
        Assert.Empty(c.Zones);
    }

    [Fact]
    public void A_growing_zone_of_another_plant_overwrites_unless_extending_a_zone()
    {
        var (c, _, w) = StoryRig.Make();
        for (int x = 0; x <= 9; x++) w.Set(x, 63, 0, 60); // farmland
        var wheat = Op(c, "add", "farm", Area.Of(0, 63, 0, 4, 63, 0), body: new JsonObject { ["plant"] = "wheat" })["id"]!.GetValue<string>();
        var more = Op(c, "add", "farm", Area.Of(4, 63, 0, 6, 63, 0), body: new JsonObject { ["plant"] = "wheat" });
        Assert.Equal(wheat, more["id"]!.GetValue<string>()); // same plant: one zone
        var r = Op(c, "add", "farm", Area.Of(5, 63, 0, 9, 63, 0), body: new JsonObject { ["plant"] = "pumpkin" });
        var pumpkin = r["id"]!.GetValue<string>();
        Assert.NotEqual(wheat, pumpkin);
        Assert.Equal([wheat], Ids(r["shrunk"]));
        Assert.Equal(5, Z(c, wheat).Cells.Count);
        Assert.Equal("pumpkin", Z(c, pumpkin).Plant);
        // Extending the wheat zone over the pumpkins: the cells join the wheat (its type wins), the pumpkin zone shrinks.
        r = Op(c, "add", null, Area.Of(5, 63, 0, 6, 63, 0), body: new JsonObject { ["into"] = wheat, ["plant"] = "pumpkin" });
        Assert.Equal(wheat, r["id"]!.GetValue<string>());
        Assert.Equal(7, Z(c, wheat).Cells.Count);
        Assert.Equal(3, Z(c, pumpkin).Cells.Count);
        Assert.Contains("is a farm zone", c.ZoneOp("add", "chop", Area.Of(0, 63, 0, 0, 63, 0), grow: new JsonObject { ["into"] = wheat }).Error);
    }

    [Fact]
    public void Rect_zones_from_older_saves_load_as_the_same_cells_and_are_saved_as_rows()
    {
        using var db = AgentDb.Open(null);
        var store = new StoryStore(db);
        var w = new FakeStoryWorld();
        var colonies = new StoryColonies(new FakeStoryHost(w), store);
        var (c, err) = colonies.Create("Riverside", 1, new P3(1, 64, 1));
        Assert.Null(err);
        c!.Save(force: true);
        // A zone as saved before cell sets: a rectangle, a growing zone's single plant.
        db.Exec("INSERT INTO story_objects(colony, kind, id, json) VALUES ($c, 'zone', 'z90', $j)", ("$c", c.Id),
            ("$j", """{"id":"z90","kind":"farm","area":[2,63,5,4,63,6],"name":"Old field","rev":3,"plant":"wheat","sow":true,"harvest":true}"""));
        var again = new StoryColonies(new FakeStoryHost(w), store);
        Assert.Equal(1, again.Load());
        var z = again.Get(c.Id)!.Zones.Single();
        Assert.Equal(6, z.Cells.Count);
        Assert.Equal(new[] { new[] { 5, 2, 4 }, new[] { 6, 2, 4 } }, z.Rows!.ToArray());
        Assert.Equal(Area.Of(2, 63, 5, 4, 63, 6), z.Area);
        Assert.Equal("wheat", z.Bills!.Single().Plant);
        Assert.Equal("forever", z.Bills!.Single().Mode);
        var json = db.Query("SELECT json FROM story_objects WHERE kind = 'zone'", r => r.GetString(0)).Single();
        Assert.Contains("\"rows\":[[5,2,4],[6,2,4]]", json);
        Assert.Contains("\"bills\":", json);
    }

    [Fact]
    public void Consumers_read_the_cells_not_the_bounding_box()
    {
        var (c, h, w) = StoryRig.Make();
        // A house far from the primary home, its corner erased: that corner is not at home.
        Op(c, "add", "home", Area.Of(300, 63, 300, 309, 63, 309), "Lodge");
        Op(c, "remove", "home", Area.Of(305, 63, 305, 309, 63, 309));
        Assert.True(c.InHome(new P3(301, 64, 301)));
        Assert.False(c.InHome(new P3(307, 64, 307)));
        // A quarry with an ore near the pawn erased out of it: the pawn mines the one still in the zone.
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0);
        w.Set(2, 60, 0, 15); // iron, near
        w.Set(8, 60, 0, 16); // coal, farther
        Op(c, "add", "mine", Area.Of(1, 58, -1, 9, 62, 1));
        Op(c, "remove", "mine", Area.Of(2, 0, 0, 2, 0, 0));
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.Equal("8,60,0", job["at"]!.GetValue<string>());
        // Keep-out, by cells: a designation in its notch is open, one in it is not.
        Op(c, "add", "forbidden", Area.Of(20, 60, 20, 24, 70, 24));
        Op(c, "remove", "forbidden", Area.Of(20, 0, 20, 20, 0, 20));
        var st = c.State()["zones"]!.AsArray().Select(n => n!.AsObject()).Single(o => o["kind"]!.GetValue<string>() == "forbidden");
        Assert.Equal(24, st["cells"]!.GetValue<int>());
        Assert.Equal(5, st["rows"]!.AsArray().Count);
    }
}
