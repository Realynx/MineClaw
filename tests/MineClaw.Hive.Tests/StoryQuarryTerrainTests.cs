using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A quarry on ground that isn't flat (docs/web/story-mode.md "Quarries", "The stair"). The top is the zone's highest ground, so where
/// the ring starts lower its first steps were air: a block went on the surface, or (two lower, nothing beside it) no supported scaffold
/// was found and the stair stayed "broken" before the quarry ever started. And the top was looked for only 16 over and under the
/// level the zone was painted at (the Controller's working level, not the ground).
/// </summary>
public sealed class StoryQuarryTerrainTests
{
    private const int X0 = 10, Z0 = 10, BedrockTop = 2;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(int w, int d, TimeProvider? time = null)
    {
        var (c, h, world) = StoryRig.Make(time: time);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(4, 0, 64)); // a stone pickaxe and cobblestone for steps
        for (int x = X0 - 2; x < X0 + w + 2; x++)
            for (int z = Z0 - 2; z < Z0 + d + 2; z++)
                for (int y = 0; y <= BedrockTop; y++)
                    world.Set(x, y, z, 7);
        return (c, h, world);
    }

    private static string Quarry(StoryColony c, int w, int d)
    {
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(X0, 63, Z0, X0 + w - 1, 63, Z0 + d - 1));
        Assert.True(err == null, err);
        return res!["id"]!.GetValue<string>();
    }

    private static P3 At(JsonObject j) => P3.Parse(JsonNode.Parse("[" + j["at"]!.GetValue<string>() + "]"))!.Value;
    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    /// <summary>The pawn does every dig and place the quarry hands out (the world changes as the script would), until it has none.</summary>
    private static (int Places, List<JsonObject> Jobs) Work(StoryColony c, FakeStoryWorld w, Func<JsonObject, bool>? until = null)
    {
        int places = 0;
        var jobs = new List<JsonObject>();
        for (int i = 0; i < 6000; i++)
        {
            var j = c.Next();
            if (Kind(j) is not ("dig" or "place") || until?.Invoke(j) == true) break;
            jobs.Add(j);
            var at = At(j);
            if (Kind(j) == "dig") { Assert.NotEqual(7, w.Block(at.X, at.Y, at.Z).Id); w.Set(at.X, at.Y, at.Z, 0); }
            else { w.Set(at.X, at.Y, at.Z, 4); places++; }
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        }
        return (places, jobs);
    }

    /// <summary>Can a body walk (1-block rises with room over its head) from <paramref name="from"/> to stand over <paramref name="surface"/>?</summary>
    private static bool WalksOut(FakeStoryWorld w, P3 from, int surface)
    {
        var seen = new HashSet<P3> { from };
        var todo = new Queue<P3>([from]);
        while (todo.Count > 0)
        {
            var a = todo.Dequeue();
            if (a.Y >= surface + 1) return true;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                for (int dy = -1; dy <= 1; dy++)
                {
                    var b = new P3(a.X + dx, a.Y + dy, a.Z + dz);
                    if (b.X < X0 - 2 || b.X > X0 + 12 || b.Z < Z0 - 2 || b.Z > Z0 + 12) continue;
                    if (!StoryWander.Standable(w, b.X, b.Y, b.Z) || seen.Contains(b)) continue;
                    if (dy == 1 && !StoryWander.Passable(w.Block(a.X, a.Y + 2, a.Z).Id)) continue;
                    if (dy == -1 && !StoryWander.Passable(w.Block(b.X, b.Y + 2, b.Z).Id)) continue;
                    seen.Add(b);
                    todo.Enqueue(b);
                }
        }
        return false;
    }

    private static P3 PitBottom(FakeStoryWorld w, int width, int depthZ) =>
        Enumerable.Range(X0, width).SelectMany(x => Enumerable.Range(Z0, depthZ).Select(zz => new P3(x, BedrockTop + 1, zz)))
            .First(p => StoryWander.Standable(w, p.X, p.Y, p.Z));

    private static JsonObject QuarryState(StoryColony c, string id) =>
        c.State()["zones"]!.AsArray().Select(n => n!.AsObject()).Single(o => o["id"]!.GetValue<string>() == id)["quarry"]!.AsObject();

    [Fact]
    public void A_bump_on_flat_ground_neither_stalls_the_stair_nor_puts_blocks_in_the_air()
    {
        var (c, _, w) = Rig(6, 6);
        w.Set(X0 + 3, 64, Z0 + 3, 1);
        w.Set(X0 + 3, 65, Z0 + 3, 1);
        var id = Quarry(c, 6, 6);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        // The top is the bump's (65): steps 0 and 1 would be air at y 65 and 64 over the ground at 63. The stair starts at step 2.
        Assert.Equal(StoryQuarry.Step(ring, 65, 2).Json().ToJsonString(), QuarryState(c, id)["stair"]![0]!.ToJsonString());
        var (places, jobs) = Work(c, w);
        Assert.DoesNotContain(jobs, j => Kind(j) == "place" && At(j).Y >= 64);
        Assert.Equal(0, places);
        Assert.Equal(0, w.Block(X0 + 3, 65, Z0 + 3).Id); // the bump is dug
        Assert.Equal(0, w.Block(X0 + 3, 64, Z0 + 3).Id);
        Assert.Equal(65, c.Zones.Single().QuarryTop);
        Assert.True(QuarryState(c, id)["done"]!.GetValue<bool>());
        Assert.True(WalksOut(w, PitBottom(w, 6, 6), 63));
    }

    [Fact]
    public void Lower_ground_at_the_rings_start_gets_no_block_on_the_surface()
    {
        var (c, _, w) = Rig(6, 6);
        // A terrace: the ground is a block lower along the west of the zone and beside it (y 62).
        for (int x = X0 - 2; x <= X0 + 1; x++)
            for (int z = Z0 - 2; z < Z0 + 8; z++)
                w.Set(x, 63, z, 0);
        var id = Quarry(c, 6, 6);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var first = StoryQuarry.Step(ring, 63, 0);
        var (places, jobs) = Work(c, w);
        Assert.Equal(0, places);
        Assert.DoesNotContain(jobs, j => At(j) == first);
        Assert.Equal(0, w.Block(first.X, first.Y, first.Z).Id); // nothing put on the surface
        Assert.True(QuarryState(c, id)["done"]!.GetValue<bool>());
        Assert.True(WalksOut(w, PitBottom(w, 6, 6), 63));
    }

    [Fact]
    public void A_step_over_a_deep_hole_at_the_rings_start_is_still_placed()
    {
        var (c, _, w) = Rig(6, 6);
        var id = Quarry(c, 6, 6);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var first = StoryQuarry.Step(ring, 63, 0);
        for (int y = 57; y <= 63; y++) w.Set(first.X, y, first.Z, 0); // no ground 1 or 2 under it: a pawn can't step from there
        var (places, jobs) = Work(c, w);
        var place = jobs.First(j => Kind(j) == "place");
        Assert.Equal(first, At(place));
        Assert.True(places >= 1);
        Assert.Equal(4, w.Block(first.X, first.Y, first.Z).Id);
        Assert.True(QuarryState(c, id)["done"]!.GetValue<bool>());
        Assert.True(WalksOut(w, PitBottom(w, 6, 6), 63));
    }

    [Fact]
    public void A_missing_step_in_the_middle_of_the_stair_is_still_repaired()
    {
        // Only leading steps are left out: one lost in the middle (its column dug out under it and open to the sky) is placed again.
        var clock = new Clock();
        var (c, _, w) = Rig(9, 9, clock);
        var id = Quarry(c, 9, 9);
        Work(c, w, until: j => At(j).Y <= 55);
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var gone = StoryQuarry.Step(ring, 63, 5);
        w.Set(gone.X, gone.Y, gone.Z, 0);
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Contains($"stair broken at y {gone.Y}", QuarryState(c, id)["status"]!.GetValue<string>());
        var next = c.Next();
        Assert.Equal("place", Kind(next));
        Assert.Equal(gone, At(next));
    }

    [Fact]
    public void A_miner_enters_and_leaves_at_the_first_step_not_at_the_top()
    {
        // A pit whose top was a bump (65) dug out to y 49 over flat ground at 63: steps 0 and 1 were never part of the stair. The walk
        // in started at "the top + 1" (step 0's head, standing on air): no route at all.
        var (c, h, w) = StoryRig.Make();
        c.DoneGrace = TimeSpan.Zero;
        var pawn = c.P();
        foreach (var type in WorkTypes.All) c.Pawns[pawn].Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        c.SetHaulOptions(null, null, null, pillarStack: 0);
        c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 29, 63, 19));
        var zone = c.Zones.Single(z => z.Kind == "mine");
        zone.QuarryTop = 65;
        zone.QuarryLayer = 16;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int x = 10; x <= 29; x++) for (int z = 10; z <= 19; z++) for (int y = 49; y <= 63; y++) w.Set(x, y, z, 0);
        for (int k = 2; k <= 16; k++) { var s = StoryQuarry.Step(ring, 65, k); w.Set(s.X, s.Y, s.Z, 1); }
        c.StorageOp("set", [new P3(30, 64, 19)], null, null, null);
        h.Lives[pawn] = FakeStoryHost.Live(30, 64, 19);
        var entry = StoryQuarry.Step(ring, 65, 2).Add(0, 1, 0).ToString();
        var job = c.Next();
        Assert.Equal("dig", Kind(job));
        var route = job["quarry_approach"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(entry, route[0]);
        w.Set(At(job).X, At(job).Y, At(job).Z, 0);
        c.Report(pawn, job["id"]!.GetValue<string>(), "done", null, null);
        h.Lives[pawn] = FakeStoryHost.Live(26, 49, 11, inv: new Stack(4, 0, 8)) with { FreeSlots = 2 };
        var trip = c.Next();
        Assert.Equal("go", Kind(trip));
        route = trip["quarry_exit"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(entry, route[^1]);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(30)]
    public void A_quarry_painted_from_a_level_far_under_or_over_the_ground_finds_its_top(int ground)
    {
        var (c, _, w) = Rig(4, 4);
        for (int x = X0 - 2; x < X0 + 6; x++)
            for (int z = Z0 - 2; z < Z0 + 6; z++)
                if (ground > 63) for (int y = 64; y <= ground; y++) w.Set(x, y, z, 1);
                else for (int y = ground + 1; y <= 63; y++) w.Set(x, y, z, 0);
        Quarry(c, 4, 4); // painted at y 63, the working level
        var job = c.Next();
        Assert.Equal(ground, c.Zones.Single().QuarryTop);
        Assert.Equal("dig", Kind(job));
        Assert.Equal(ground, At(job).Y);
    }

    [Fact]
    public void Trees_on_a_quarry_are_felled_before_the_ground_under_them()
    {
        // The top is the highest ground and logs are no ground: the trunk was never a cell, the ground under it was dug, and the tree
        // was left floating over the pit.
        var (c, _, w) = Rig(6, 6);
        w.Tree(X0 + 2, Z0 + 2);
        var id = Quarry(c, 6, 6);
        var first = c.Next();
        Assert.Equal("chop", Kind(first));
        Assert.Equal("mine", first["type"]!.GetValue<string>());
        Assert.Equal(new P3(X0 + 2, 64, Z0 + 2), At(first));
        Assert.Equal(64, first["quarry_access_y"]!.GetValue<int>());
        Assert.Null(first["quarry_approach"]); // it walks there on the surface, not down the stair
        Assert.Contains("felling 1 tree(s) on it first", QuarryState(c, id)["status"]!.GetValue<string>());
        for (int y = 64; y <= 68; y++) w.Set(X0 + 2, y, Z0 + 2, 0); // felled, its leaf cap too
        c.Report(c.P(), first["id"]!.GetValue<string>(), "done", null, null);
        var (_, jobs) = Work(c, w);
        Assert.Equal("dig", Kind(jobs[0])); // then the digging starts
        Assert.True(QuarryState(c, id)["done"]!.GetValue<bool>());
    }

    [Fact]
    public void A_paint_that_would_leave_a_quarry_without_a_ring_is_refused_with_why()
    {
        // A 2×2 added at the corner of a 4×4 quarry makes an L: its ring walk ends at the inner corner, every cell would be dug, the old
        // steps too, and no stair kept. Merging a quarry with an ores-only zone is refused the same way.
        var (c, _, _) = StoryRig.Make();
        var quarry = c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 13, 63, 13)).Result!["id"]!.GetValue<string>();
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(10, 63, 14, 11, 63, 15));
        Assert.Null(res);
        Assert.Contains($"Quarry {quarry} would lose its stair", err);
        Assert.Contains("Nothing changed: paint the new part as a quarry of its own", err);
        Assert.Equal(16, c.Zones.Single().Cells.Count);
        // Another work zone painted into its side would notch it.
        (res, err) = c.ZoneOp("add", "chop", Area.Of(11, 63, 10, 12, 63, 10));
        Assert.Null(res);
        Assert.Contains("only whole rows or columns can come off its sides", err);
        Assert.Single(c.Zones);
        // A whole row painted over is fine: it stays a rectangle with a ring.
        Assert.Null(c.ZoneOp("add", "chop", Area.Of(10, 63, 10, 13, 63, 10)).Error);
        Assert.Equal(12, c.Zones.Single(z => z.Id == quarry).Cells.Count);
        // A quarry and an ores-only zone never merge.
        var ores = c.ZoneOp("add", "mine", Area.Of(10, 63, 16, 13, 63, 17), null, null, new JsonObject { ["mode"] = "ores" }).Result!["id"]!.GetValue<string>();
        (res, err) = c.ZoneOp("add", "mine", Area.Of(10, 63, 14, 13, 63, 15));
        Assert.Null(res);
        Assert.Contains($"Quarry {quarry} and ores-only zone {ores} would merge", err);
        Assert.Equal(3, c.Zones.Count);
    }

    [Fact]
    public void Erasing_a_notch_out_of_a_quarry_is_refused()
    {
        var (c, _, _) = StoryRig.Make();
        var quarry = c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 15, 63, 15)).Result!["id"]!.GetValue<string>();
        foreach (var kind in new[] { "mine", null })
        {
            var (res, err) = c.ZoneOp("remove", kind, Area.Of(12, 63, 10, 13, 63, 11)); // a notch in its north side
            Assert.Null(res);
            Assert.Contains($"Quarry {quarry} would lose its stair", err);
            Assert.Equal(36, c.Zones.Single().Cells.Count);
        }
        // Cutting it so that a 1-wide strip is left over is a notch too.
        Assert.NotNull(c.ZoneOp("remove", "mine", Area.Of(11, 63, 10, 11, 63, 15)).Error);
        // Whole rows off a side, or the whole quarry, are fine.
        Assert.Null(c.ZoneOp("remove", "mine", Area.Of(10, 63, 10, 15, 63, 11)).Error);
        Assert.Equal(24, c.Zones.Single().Cells.Count);
        Assert.NotNull(StoryQuarry.Ring(c.Zones.Single().Cells));
        Assert.Null(c.ZoneOp("remove", null, Area.Of(10, 63, 10, 15, 63, 15)).Error);
        Assert.Empty(c.Zones);
    }

    [Fact]
    public void The_top_is_kept_only_once_every_column_of_the_zone_is_loaded()
    {
        var clock = new Clock();
        var (c, _, w) = Rig(6, 6, clock);
        w.Set(X0 + 3, 64, Z0 + 3, 1);
        w.Set(X0 + 3, 65, Z0 + 3, 1);
        w.Unloaded.Add((X0 + 3, Z0 + 3)); // the bump's column isn't in any pawn's view yet
        Quarry(c, 6, 6);
        c.State();
        Assert.Null(c.Zones.Single().QuarryTop);
        w.Unloaded.Clear();
        clock.Now += TimeSpan.FromSeconds(6);
        c.State();
        Assert.Equal(65, c.Zones.Single().QuarryTop);
    }

    [Fact]
    public void The_top_seen_while_a_column_is_out_of_view_never_goes_down()
    {
        // Recomputed from the ground on every look, the top went down once its highest cell was dug, and the stair moved a block down:
        // the steps placed for the old one were dug and placed again.
        var clock = new Clock();
        var (c, _, w) = Rig(6, 6, clock);
        w.Set(X0 + 2, 64, Z0 + 2, 1); // a bump, the highest ground
        w.Unloaded.Add((X0 + 4, Z0 + 4)); // a column no pawn has in view yet
        Quarry(c, 6, 6);
        Assert.Equal(64, c.State()["zones"]![0]!["quarry"]!["top"]!.GetValue<int>());
        w.Set(X0 + 2, 64, Z0 + 2, 0); // dug
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal(64, c.State()["zones"]![0]!["quarry"]!["top"]!.GetValue<int>());
        Assert.Null(c.Zones.Single().QuarryTop);
        w.Unloaded.Clear();
        clock.Now += TimeSpan.FromSeconds(6);
        c.State();
        Assert.Equal(64, c.Zones.Single().QuarryTop);
    }
}
