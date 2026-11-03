using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>Homes (a primary home plus any number of home zones) and beds: the scan, assignments, and the sleep hours.</summary>
public sealed class StoryHomesAndBedsTests
{
    /// <summary>A bed: foot at x,y,z, head one block toward <paramref name="facing"/> (0 +z, 1 −x, 2 −z, 3 +x).</summary>
    private static void PutBed(FakeStoryWorld w, int x, int y, int z, int facing)
    {
        var (dx, dz) = facing switch { 0 => (0, 1), 1 => (-1, 0), 2 => (0, -1), _ => (1, 0) };
        w.Set(x, y, z, 26, facing);
        w.Set(x + dx, y, z + dz, 26, facing | 8);
    }

    [Fact]
    public void A_beds_two_halves_are_one_bed_named_by_its_foot_in_every_home_zone_and_around_the_primary_home()
    {
        var (c, _, w) = StoryRig.Make();
        PutBed(w, 3, 64, 3, 0);      // by the primary home (0,64,0)
        PutBed(w, -5, 64, 2, 1);     // facing −x: head at −6
        PutBed(w, 200, 64, 200, 3);  // a far house
        PutBed(w, 500, 64, 500, 2);  // nowhere near any home
        Assert.Equal(new[] { new P3(-5, 64, 2), new P3(3, 64, 3) }, c.Beds().Select(b => b.Pos).OrderBy(p => p.X).ToArray());
        c.ZoneOp("add", "home", Area.Of(195, 60, 195, 205, 70, 205), name: "Farmhouse");
        var beds = c.Beds();
        Assert.Equal(3, beds.Count);
        var far = beds.Single(b => b.Pos == new P3(200, 64, 200));
        Assert.Equal(c.Zones.Single().Id, far.Zone);
        Assert.Null(beds.Single(b => b.Pos == new P3(3, 64, 3)).Zone);
        // The head half names the same bed.
        Assert.Equal(new P3(-5, 64, 2), StoryColony.BedFoot(w, new P3(-6, 64, 2)));
        Assert.Equal(new P3(200, 64, 200), StoryColony.BedFoot(w, new P3(201, 64, 200)));
        Assert.Null(StoryColony.BedFoot(w, new P3(0, 64, 0)));
        // In the state: {pos, dim, pawn, zone}.
        var shown = c.State()["beds"]!.AsArray();
        Assert.Equal(3, shown.Count);
        Assert.All(shown, b => { Assert.NotNull(b!["pos"]); Assert.Equal(0, b["dim"]!.GetValue<int>()); Assert.Null(b["pawn"]); });
    }

    [Fact]
    public void One_pawn_per_bed_one_bed_per_pawn_and_assigning_moves_them()
    {
        var (c, _, w) = StoryRig.Make(pawns: 2);
        PutBed(w, 3, 64, 3, 0);
        PutBed(w, 6, 64, 3, 0);
        var (a, b) = (c.P(0), c.P(1));
        Assert.Null(c.BedsOp("assign", new P3(3, 64, 4), a).Error); // the head half: the same bed
        Assert.Equal(a, c.Meta.Beds["3,64,3"]);
        Assert.Null(c.BedsOp("assign", new P3(6, 64, 3), a).Error); // a moves: its old bed is free
        Assert.False(c.Meta.Beds.ContainsKey("3,64,3"));
        Assert.Null(c.BedsOp("assign", new P3(6, 64, 3), b).Error); // b takes it: a has none now
        Assert.Equal(b, Assert.Single(c.Meta.Beds).Value);
        var (res, err, _) = c.BedsOp("unassign", new P3(6, 64, 3), null);
        Assert.Null(err);
        Assert.Empty(c.Meta.Beds);
        Assert.All(res!["beds"]!.AsArray(), x => Assert.Null(x!["pawn"]));
        // Not a bed, unknown pawn, bad op.
        Assert.False(c.BedsOp("assign", new P3(0, 64, 0), a).NotFound);
        Assert.Contains("no bed", c.BedsOp("assign", new P3(0, 64, 0), a).Error);
        Assert.True(c.BedsOp("assign", new P3(3, 64, 3), "nobody").NotFound);
        Assert.NotNull(c.BedsOp("sleep", new P3(3, 64, 3), a).Error);
    }

    [Fact]
    public void In_its_sleep_hours_a_pawn_goes_to_its_bed_else_the_nearest_free_one_else_rests_at_home_with_one_alert()
    {
        var (c, _, w) = StoryRig.Make(pawns: 3);
        foreach (var p in c.Pawns.Values) p.Schedule = Enumerable.Repeat(ScheduleBlocks.Sleep, 24).ToArray();
        PutBed(w, 3, 64, 3, 0);
        PutBed(w, 9, 64, 3, 0);
        c.BedsOp("assign", new P3(9, 64, 3), c.P(1));
        var own = c.Next(1);
        Assert.Equal("sleep", own["kind"]!.GetValue<string>());
        Assert.Equal("9,64,3", own["bed"]!.GetValue<string>());
        var free = c.Next(0); // unassigned: the nearest free bed (not p1's)
        Assert.Equal("3,64,3", free["bed"]!.GetValue<string>());
        var none = c.Next(2); // the only free bed is taken for the night: rest at home
        Assert.Equal("sleep", none["kind"]!.GetValue<string>());
        Assert.Null(none["bed"]);
        Assert.Equal("0,64,0", none["at"]!.GetValue<string>());
        c.Report(c.P(2), none["id"]!.GetValue<string>(), "done", null, null);
        c.Next(2);
        Assert.Single(c.Alerts, a => a.Text.Contains("has no bed"));
    }

    [Fact]
    public void Home_is_every_home_zone_plus_the_radius_around_the_primary_home()
    {
        var (c, h, w) = StoryRig.Make();
        c.ZoneOp("add", "home", Area.Of(300, 60, 300, 310, 70, 310), name: "Lodge");
        c.ZoneOp("add", "home", Area.Of(-100, 60, -100, -90, 70, -90));
        Assert.True(c.InHome(new P3(5, 64, 5)));         // near the primary home: still home with zones around
        Assert.True(c.InHome(new P3(305, 64, 305)));     // a far house
        Assert.True(c.InHome(new P3(-95, 80, -95)));     // any height in a zone's columns
        Assert.False(c.InHome(new P3(100, 64, 100)));
        // Zones are added, renamed and removed any time.
        var lodge = c.Zones.First(z => z.Name == "Lodge");
        Assert.Null(c.ZoneOp("rename", null, null, lodge.Id, "Hunting lodge").Error);
        Assert.Equal("Hunting lodge", lodge.Name);
        Assert.Equal("Hunting lodge", c.State()["zones"]!.AsArray().Single(z => z!["id"]!.GetValue<string>() == lodge.Id)!["name"]!.GetValue<string>());
        Assert.Null(c.ZoneOp("remove", null, null, lodge.Id).Error);
        Assert.False(c.InHome(new P3(305, 64, 305)));
        Assert.NotNull(c.ZoneOp("rename", null, null, "z999", "x").Error);
        // A drop in a far house is hauled like one at the primary home (no teleport: the pawn walks there).
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        w.Ents.Add(new StoryEntity(77, "item", "cobblestone", -95.5, 64, -95.5, ItemId: 4, Count: 3));
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Haul ? 1 : 0;
        var j = c.Next();
        Assert.Equal("collect", j["kind"]!.GetValue<string>());
        Assert.Empty(h.Teleported);
    }
}
