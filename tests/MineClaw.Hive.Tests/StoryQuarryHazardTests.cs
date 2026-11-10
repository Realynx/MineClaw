using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// What a quarry meets on its way down (docs/web/story-mode.md "Quarry hazards"; Fox 11-05: "the quarry will also need to take into
/// consideration natural caves that it can come into, and lava and other dangerous things"): a breached cave is walled off and the stair
/// stays walkable, lava and water are never let into the pit (sealed with a block that doesn't burn, said once), nothing is dug out from
/// under standing sand, and a monster spawner stops the quarry until it is gone.
/// </summary>
public sealed class StoryQuarryHazardTests
{
    private const int X0 = 10, Z0 = 10, Size = 6, BedrockTop = 2;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(params Stack[] inv) => Rig(null, inv);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(IGameData? data, params Stack[] inv)
    {
        var clock = new Clock();
        var (c, h, world) = StoryRig.Make(data: data, time: clock);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: inv.Length > 0 ? inv : [new Stack(4, 0, 64)]);
        for (int x = X0 - 6; x < X0 + Size + 6; x++)
            for (int z = Z0 - 6; z < Z0 + Size + 6; z++)
                for (int y = 0; y <= BedrockTop; y++)
                    world.Set(x, y, z, 7);
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(X0, 63, Z0, X0 + Size - 1, 63, Z0 + Size - 1)).Error);
        return (c, h, world, clock);
    }

    private static P3 At(JsonObject j) => P3.Parse(JsonNode.Parse("[" + j["at"]!.GetValue<string>() + "]"))!.Value;

    private static bool InZone(P3 p) => p.X >= X0 && p.X < X0 + Size && p.Z >= Z0 && p.Z < Z0 + Size;

    private static List<(int X, int Z)> Ring(StoryColony c) => StoryQuarry.Ring(c.Zones.Single().Cells)!;

    /// <summary>The pawn does every quarry job (the world changes as the script would); <paramref name="check"/> sees each before it.</summary>
    private static List<JsonObject> Work(StoryColony c, FakeStoryWorld w, Action<JsonObject>? check = null, Func<JsonObject, bool>? until = null, int max = 6000)
    {
        var jobs = new List<JsonObject>();
        for (int i = 0; i < max; i++)
        {
            var j = c.Next();
            if (j["kind"]!.GetValue<string>() is not ("dig" or "place") || until?.Invoke(j) == true) break;
            check?.Invoke(j);
            jobs.Add(j);
            var at = At(j);
            w.Set(at.X, at.Y, at.Z, j["kind"]!.GetValue<string>() == "dig" ? 0 : 4);
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        }
        return jobs;
    }

    /// <summary>Every cell a body can stand in, walking (1-block rises with room to jump, steps down) from <paramref name="from"/>.</summary>
    private static HashSet<P3> Walkable(FakeStoryWorld w, P3 from)
    {
        var seen = new HashSet<P3> { from };
        var todo = new Queue<P3>([from]);
        while (todo.Count > 0)
        {
            var a = todo.Dequeue();
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                for (int dy = -1; dy <= 1; dy++)
                {
                    var b = new P3(a.X + dx, a.Y + dy, a.Z + dz);
                    if (Math.Abs(b.X - X0) > 14 || Math.Abs(b.Z - Z0) > 14 || b.Y > 66) continue;
                    if (!StoryWander.Standable(w, b.X, b.Y, b.Z) || seen.Contains(b)) continue;
                    if (dy == 1 && !StoryWander.Passable(w.Block(a.X, a.Y + 2, a.Z).Id)) continue;
                    if (dy == -1 && !StoryWander.Passable(w.Block(b.X, b.Y + 2, b.Z).Id)) continue;
                    seen.Add(b);
                    todo.Enqueue(b);
                }
        }
        return seen;
    }

    private static P3 PitBottom(FakeStoryWorld w) =>
        Enumerable.Range(X0, Size).SelectMany(x => Enumerable.Range(Z0, Size).Select(z => new P3(x, BedrockTop + 1, z)))
            .First(p => StoryWander.Standable(w, p.X, p.Y, p.Z));

    private static void Carve(FakeStoryWorld w, int x1, int y1, int z1, int x2, int y2, int z2, int id = 0)
    {
        for (int x = x1; x <= x2; x++)
            for (int y = y1; y <= y2; y++)
                for (int z = z1; z <= z2; z++)
                    w.Set(x, y, z, id);
    }

    private static string Status(StoryColony c) =>
        c.State()["zones"]!.AsArray().Single()!["quarry"]!["status"]!.GetValue<string>();

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    /// <summary>The kinds of job a quarry hands out: a wait is none of them.</summary>
    private static readonly string[] QuarryKinds = ["dig", "place", "scoop_lava"];

    private static P3[] Sides(P3 p) => [p.Add(1, 0, 0), p.Add(-1, 0, 0), p.Add(0, 0, 1), p.Add(0, 0, -1), p.Add(0, 1, 0), p.Add(0, -1, 0)];

    [Fact]
    public void An_open_cave_wall_starts_at_a_supported_cell_instead_of_a_floating_upper_cell()
    {
        var (c, _, w, _) = Rig();
        c.Zones.Single().QuarryTop = 63;
        Carve(w, X0, 59, Z0, X0 + Size - 1, 63, Z0 + Size - 1);
        var ring = Ring(c);
        for (int k = 0; k <= 4; k++)
        {
            var s = StoryQuarry.Step(ring, 63, k);
            w.Set(s.X, s.Y, s.Z, 1);
        }
        // A wide cave the whole east side long (and round both corners), y 59..66 under a roof at 67..69: no closed pocket, and its
        // cells beside the pit have nothing on any side to attach a block to but the floor.
        Carve(w, X0 + Size, 59, Z0 - 1, X0 + Size + 2, 66, Z0 + Size);
        Carve(w, X0 + Size - 1, 59, Z0 - 1, X0 + Size - 1, 66, Z0 - 1);
        Carve(w, X0 + Size - 1, 59, Z0 + Size, X0 + Size - 1, 66, Z0 + Size);
        Carve(w, X0 + Size, 67, Z0 - 1, X0 + Size + 2, 69, Z0 + Size, 1);
        Carve(w, X0 + Size - 1, 67, Z0 - 1, X0 + Size - 1, 69, Z0 - 1, 1);
        Carve(w, X0 + Size - 1, 67, Z0 + Size, X0 + Size - 1, 69, Z0 + Size, 1);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal(59, At(job).Y);
        Assert.NotEqual(0, w.Block(At(job).X, 58, At(job).Z).Id);
        Assert.False(InZone(At(job)), $"{At(job)}: the block goes into the cave's cell, the pit keeps its own");
    }

    [Fact]
    public void A_failed_seal_above_the_floor_does_not_hold_every_lower_layer_and_is_retried()
    {
        var (c, _, w, clock) = Rig();
        // Previously dug upper layers, one new cave opening in their wall.
        var ring = Ring(c);
        Carve(w, X0, 59, Z0, X0 + Size - 1, 63, Z0 + Size - 1);
        for (int k = 0; k <= 4; k++)
        {
            var s = StoryQuarry.Step(ring, 63, k);
            w.Set(s.X, s.Y, s.Z, 1);
        }
        Carve(w, X0 + Size, 59, Z0 + 2, X0 + Size + 70, 59, Z0 + 2); // a cave tunnel, longer than any closed pocket
        var seal = c.Next();
        Assert.Equal("place", seal["kind"]!.GetValue<string>());
        Assert.True(seal["reach"]!.GetValue<bool>());
        Assert.Equal(59, At(seal).Y);
        c.Report(c.P(), seal["id"]!.GetValue<string>(), "failed", "can't reach the support face", null);
        clock.Now += TimeSpan.FromSeconds(6);
        var mining = c.Next();
        Assert.Equal("dig", mining["kind"]!.GetValue<string>());
        Assert.Equal(58, At(mining).Y);
        c.Report(c.P(), mining["id"]!.GetValue<string>(), "done", null, null);
        clock.Now += TimeSpan.FromMinutes(15);
        var retry = c.Next();
        Assert.Equal("place", retry["kind"]!.GetValue<string>());
        Assert.Equal(At(seal), At(retry));
    }

    [Fact]
    public void A_breached_cave_beside_the_quarry_is_walled_off_no_pawn_goes_in_and_the_quarry_goes_on_to_bedrock()
    {
        var (c, _, w, _) = Rig();
        Carve(w, X0 + Size, 50, Z0 + 1, X0 + Size + 4, 52, Z0 + 4); // a cave beside the east edge, y 50..52
        w.Unloaded.Add((X0 + Size + 5, Z0 + 2)); // that runs on into ground no body has loaded: no closed pocket
        var cave = new HashSet<P3>();
        for (int x = X0 + Size; x <= X0 + Size + 4; x++) for (int y = 50; y <= 52; y++) for (int z = Z0 + 1; z <= Z0 + 4; z++) cave.Add(new P3(x, y, z));
        Work(c, w, check: j => Assert.True(InZone(At(j)), $"{j["kind"]} {At(j)}: outside the quarry"));
        // The edge cells between the pit and the cave stand.
        for (int y = 50; y <= 52; y++)
            for (int z = Z0 + 1; z <= Z0 + 4; z++)
                Assert.NotEqual(0, w.Block(X0 + Size - 1, y, z).Id);
        var walk = Walkable(w, PitBottom(w));
        Assert.Contains(walk, p => p.Y >= 64); // the stair still leads out
        Assert.DoesNotContain(walk, cave.Contains); // and nothing leads into the cave
        Assert.True(c.Zones.Single().QuarryLayer > 60);
        Assert.Single(c.Alerts, a => a.Text.Contains("breached a cave at y 52"));
        Assert.Contains("breached a cave at y 52", Status(c));
    }

    [Fact]
    public void A_cave_beside_the_stairs_head_room_is_sealed_outside_so_the_stair_stays_walkable()
    {
        var (c, _, w, _) = Rig();
        var ring = Ring(c);
        var step = StoryQuarry.Step(ring, 63, 7); // on the east edge at y 56
        Assert.Equal(X0 + Size - 1, step.X);
        Carve(w, step.X + 1, step.Y + 1, step.Z, step.X + 40, step.Y + 2, step.Z); // a cave tunnel beside the two cells over it
        var jobs = Work(c, w);
        var seals = jobs.Where(j => j["kind"]!.GetValue<string>() == "place").Select(At).ToList();
        Assert.Contains(step.Add(1, 1, 0), seals); // the cave's cells beside the head room got the blocks
        Assert.Contains(step.Add(1, 2, 0), seals);
        Assert.Equal(0, w.Block(step.X, step.Y + 1, step.Z).Id); // the head room is open
        Assert.Equal(0, w.Block(step.X, step.Y + 2, step.Z).Id);
        var walk = Walkable(w, PitBottom(w));
        Assert.Contains(walk, p => p.Y >= 64);
        Assert.DoesNotContain(walk, p => p.X > step.X + 1 && p.Y is 57 or 58 && p.Z == step.Z);
    }

    private static List<P3> DigCells(JsonObject j) => j["dig_cells"] is JsonArray a
        ? a.Select(n => P3.Parse(JsonNode.Parse("[" + n!.GetValue<string>() + "]"))!.Value).ToList() : [At(j)];

    /// <summary>One cell of a dig batch dug the way the pawn's script does it: open, the colony told (vein faces it opened), reported.</summary>
    private static JsonArray DigCell(StoryColony c, FakeStoryWorld w, JsonObject j, P3 at, int before)
    {
        w.Set(at.X, at.Y, at.Z, 0);
        var added = c.DigExposed(c.P(), Id(j), at.ToString(), before, 0);
        Assert.NotNull(added);
        Assert.True(c.Report(c.P(), Id(j), "progress", $"digcell {at} done", null));
        return added!;
    }

    [Fact]
    public void An_ore_hole_in_the_wall_is_not_a_breached_cave()
    {
        // Ore followed out of the pit's wall leaves a hole with natural rock over it, which reads as cave air: its edge cell got a block
        // placed in the pit and "breached a cave" was alerted at every wall ore deeper than 3 (single cobblestone on the pit walls).
        var (c, _, w, clock) = Rig();
        var edge = new P3(X0 + Size - 1, 50, Z0 + 2);
        var ore = edge.Add(1, 0, 0);
        w.Set(ore.X, ore.Y, ore.Z, 15);
        JsonObject? batch = null;
        Work(c, w, until: j => (batch = DigCells(j).Contains(edge) ? j : null) != null);
        Assert.NotNull(batch);
        Assert.Equal(ore.ToString(), Assert.Single(DigCell(c, w, batch!, edge, 1))!["at"]!.GetValue<string>()); // followed out of the zone
        Assert.Empty(DigCell(c, w, batch!, ore, 15));
        c.Report(c.P(), Id(batch!), "done", null, null);
        clock.Now += TimeSpan.FromSeconds(6);
        Work(c, w, check: j => Assert.False(Kind(j) == "place" && InZone(At(j)), $"a block placed in the pit at {At(j)}"));
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c));
        Assert.Equal(0, IdAt(w, edge));
        Assert.Equal(0, IdAt(w, ore));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("breached a cave"));
        Assert.DoesNotContain("breached a cave", Status(c));
    }

    [Fact]
    public void A_cave_opening_is_sealed_in_the_cave_cell_when_it_can_attach()
    {
        var (c, _, w, _) = Rig();
        c.Zones.Single().QuarryTop = 63;
        Carve(w, X0, 58, Z0, X0 + Size - 1, 63, Z0 + Size - 1);
        for (int k = 0; k <= 5; k++)
        {
            var s = Step(c, k);
            w.Set(s.X, s.Y, s.Z, 1);
        }
        // A cave tunnel two high, open to the pit's dug east edge where that is no head room of the stair (there the block went into
        // the cave's cell already).
        var opening = new P3(X0 + Size, 58, Z0 + 4);
        Carve(w, opening.X, 58, opening.Z, opening.X + 70, 59, opening.Z);
        var jobs = Work(c, w, until: j => At(j).Y < 57);
        var seals = jobs.Where(j => Kind(j) == "place").Select(At).ToList();
        Assert.Contains(opening, seals); // into the cave's own cells: rock round them to attach to
        Assert.Contains(opening.Add(0, 1, 0), seals);
        Assert.DoesNotContain(seals, InZone);
        Assert.Equal(0, IdAt(w, opening.Add(-1, 0, 0))); // the pit keeps its edge cells
        Assert.Equal(0, IdAt(w, opening.Add(-1, 1, 0)));
        Assert.Single(c.Alerts, a => a.Text.Contains("breached a cave"));
    }

    [Fact]
    public void A_cave_inside_the_quarry_is_already_dug_and_the_stair_over_its_void_is_placed()
    {
        var (c, _, w, _) = Rig();
        Carve(w, X0 + 3, 54, Z0 + 1, X0 + Size - 1, 57, Z0 + 3); // a cave in the zone, under steps 6..9 of the east edge
        var jobs = Work(c, w);
        Assert.DoesNotContain(jobs, j => j["kind"]!.GetValue<string>() == "dig" && At(j) is { Y: >= 54 and <= 57 } a && a.X >= X0 + 3 && a.Z is >= Z0 + 1 and <= Z0 + 3);
        var ring = Ring(c);
        for (int k = 0; k < 63 - BedrockTop; k++)
        {
            var s = StoryQuarry.Step(ring, 63, k);
            Assert.True(w.Block(s.X, s.Y, s.Z).Id != 0, $"step {k} at {s} missing");
        }
        Assert.Contains(Walkable(w, PitBottom(w)), p => p.Y >= 64);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("breached a cave")); // no opening to the outside
    }

    [Theory]
    [InlineData(11, 2, "lava")]
    [InlineData(10, 8, "lava")]
    [InlineData(9, 0, "water")]
    [InlineData(8, 0, "water")]
    public void Flowing_lava_or_water_is_covered_with_a_nonburning_block_before_its_cells_are_dug(int liquid, int meta, string word)
    {
        var (c, _, w, _) = Rig(new Stack(5, 0, 64), new Stack(4, 0, 64)); // planks first in the inventory: never used for a seal
        var pool = new P3(X0 + 2, 45, Z0 + 2);
        w.Set(pool.X, pool.Y, pool.Z, liquid, meta);
        w.Set(pool.X, pool.Y + 1, pool.Z, 0); // open above (a cave pocket): closed in by rock it would be left alone, not covered
        var outside = new P3(X0 + Size, 40, Z0 + 2); // and a pocket just outside the east edge
        w.Set(outside.X, outside.Y, outside.Z, liquid);
        bool sealedPool = false;
        Work(c, w, check: j =>
        {
            var at = At(j);
            if (j["kind"]!.GetValue<string>() == "place" && at == pool)
            {
                Assert.Equal("cobblestone", j["block"]!.GetValue<string>());
                sealedPool = true;
            }
            if (j["kind"]!.GetValue<string>() != "dig") return;
            Assert.NotEqual(outside.Add(-1, 0, 0), at); // never opened to the pocket outside
            foreach (var n in new[] { at.Add(1, 0, 0), at.Add(-1, 0, 0), at.Add(0, 0, 1), at.Add(0, 0, -1), at.Add(0, 1, 0), at.Add(0, -1, 0) })
                Assert.False(w.Block(n.X, n.Y, n.Z).Id == liquid, $"dug {at} beside the {word} at {n}");
        });
        Assert.True(sealedPool);
        Assert.Equal(liquid, w.Block(outside.X, outside.Y, outside.Z).Id);
        Assert.NotEqual(0, w.Block(outside.X - 1, outside.Y, outside.Z).Id);
        Assert.True(c.Zones.Single().QuarryLayer > 60);
        Assert.Equal(2, c.Alerts.Count(a => a.Text.Contains(word))); // one for the pool's layer, one for the pocket's: never one a look
        Assert.Contains($"{word}", Status(c));
    }

    [Fact]
    public void Nothing_is_dug_out_from_under_sand_left_standing_beside_water()
    {
        var (c, _, w, _) = Rig();
        var sand = new P3(X0 + Size - 1, 50, Z0 + 2);
        w.Set(sand.X, sand.Y, sand.Z, 12);
        w.Set(sand.X + 1, sand.Y, sand.Z, 9); // water beside it, outside the zone
        Work(c, w, check: j => Assert.NotEqual(sand.Add(0, -1, 0), At(j)));
        Assert.Equal(12, w.Block(sand.X, sand.Y, sand.Z).Id);
        Assert.NotEqual(0, w.Block(sand.X, sand.Y - 1, sand.Z).Id);
        Assert.Contains(c.Alerts, a => a.Text.Contains("under sand or gravel"));
        Assert.True(c.Zones.Single().QuarryLayer > 60);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void A_lava_source_is_bucketed_and_its_filled_bucket_goes_to_storage_before_mining_resumes(int id)
    {
        var (c, h, w, _) = Rig(new Stack(325, 0, 1), new Stack(4, 0, 16));
        var chest = new P3(0, 64, 9);
        c.StorageOp("set", [chest], null, null, null);
        var at = new P3(X0 + 2, 63, Z0 + 2);
        w.Set(at.X, at.Y, at.Z, id, 0);
        var job = c.Next();
        Assert.Equal("scoop_lava", job["kind"]!.GetValue<string>());
        Assert.Equal(at, At(job));
        Assert.True(c.Pawns[c.P()].QuarryLava);
        w.Set(at.X, at.Y, at.Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(12, 64, 12, inv: [new Stack(327, 0, 1), new Stack(4, 0, 16)]);
        c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null);
        job = c.Next();
        Assert.Equal("go", job["kind"]!.GetValue<string>());
        Assert.Contains("327:0", job["stash"]!.ToJsonString());
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 9, inv: new Stack(4, 0, 16));
        c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null);
        Assert.Contains(c.StorageAt(chest)!.Contents, s => s.Id == 327 && s.Count == 1);
        Assert.Equal("dig", c.Next()["kind"]!.GetValue<string>());
        Assert.False(c.Pawns[c.P()].QuarryLava);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_lava_source_including_one_on_the_stair_waits_for_an_empty_bucket(bool onStair)
    {
        var (c, _, w, _) = Rig();
        c.Zones.Single().QuarryTop = 63;
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        w.Set(1, 64, 9, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(1, 64, 9)).Error);
        var at = new P3(X0 + (onStair ? 0 : 2), 63, Z0 + (onStair ? 0 : 2));
        w.Set(at.X, at.Y, at.Z, 11);
        var job = c.Next();
        Assert.DoesNotContain(job["kind"]!.GetValue<string>(), new[] { "dig", "place", "scoop_lava" });
        Assert.Equal(11, w.Block(at.X, at.Y, at.Z).Id);
        var craft = Assert.Single(c.Benches.Single().Tasks);
        Assert.Equal("craft:bucket", craft.Recipe);
        Assert.True(c.Immediate(craft));
    }

    [Fact]
    public void A_refilling_water_pool_is_plugged_before_any_of_its_plugs_are_mined()
    {
        var (c, _, w, _) = Rig();
        var pool = new HashSet<P3>();
        for (int x = 12; x <= 13; x++) for (int z = 12; z <= 13; z++)
        {
            var at = new P3(x, 63, z);
            pool.Add(at);
            w.Set(x, 63, z, 9);
        }
        var plugged = new HashSet<P3>();
        var mined = new HashSet<P3>();
        Work(c, w, check: job =>
        {
            var at = At(job);
            if (!pool.Contains(at)) return;
            if (job["kind"]!.GetValue<string>() == "place") plugged.Add(at);
            else { Assert.Equal(4, plugged.Count); mined.Add(at); }
        }, until: job => At(job).Y < 63);
        Assert.Equal(pool, plugged);
        Assert.Equal(pool, mined);
        Assert.All(pool, at => Assert.Equal(0, w.Block(at.X, at.Y, at.Z).Id));
    }

    [Fact]
    public void Stored_empty_buckets_are_fetched_and_filled_before_one_storage_trip()
    {
        var (c, h, w, _) = Rig();
        var chest = new P3(0, 64, 9);
        c.StorageOp("set", [chest], null, null, null);
        c.AddContents(c.StorageAt(chest)!, new Stack(325, 0, 2));
        w.Set(12, 63, 12, 11);
        w.Set(13, 63, 12, 10);
        var job = c.Next();
        Assert.Equal("scoop_lava", job["kind"]!.GetValue<string>());
        Assert.Equal("bucket", job["fetch"]![0]!["item"]!.GetValue<string>());
        Assert.Equal(2, job["fetch"]![0]!["n"]!.GetValue<int>());
        var at = At(job);
        w.Set(at.X, at.Y, at.Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(12, 64, 12, inv: [new Stack(325, 0, 1), new Stack(327, 0, 1)]);
        c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null);
        job = c.Next();
        Assert.Equal("scoop_lava", job["kind"]!.GetValue<string>());
        Assert.Null(job["fetch"]);
        at = At(job);
        w.Set(at.X, at.Y, at.Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(12, 64, 12, inv: [new Stack(327, 0, 1), new Stack(327, 0, 1)]);
        // Even an interrupted report after filling must keep the collected lava as cargo, not forget it as a tool.
        c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", "interrupted after filling", null);
        var saved = System.Text.Json.JsonSerializer.Serialize(c.Pawns[c.P()]);
        c.Pawns[c.P()] = System.Text.Json.JsonSerializer.Deserialize<PawnState>(saved)!;
        job = c.Next();
        Assert.Equal("go", job["kind"]!.GetValue<string>());
        Assert.Contains("327:0", job["stash"]!.ToJsonString());
    }

    [Fact]
    public void Lava_collection_waits_when_no_chest_can_store_the_filled_bucket()
    {
        var (c, _, w, _) = Rig(new Stack(325, 0, 1), new Stack(4, 0, 16));
        w.Set(12, 63, 12, 11);
        Assert.DoesNotContain(c.Next()["kind"]!.GetValue<string>(), new[] { "place", "dig", "scoop_lava" });
        Assert.Equal(11, w.Block(12, 63, 12).Id);
    }

    // ------------------------------------------------------------------ liquid nobody can get at is no job

    [Theory]
    [InlineData(9, 0, "water")] // a water source
    [InlineData(10, 2, "lava")] // flowing lava
    [InlineData(11, 0, "lava")] // a lava source: this rig has no bucket and no storage
    public void A_liquid_pocket_closed_in_by_rock_is_left_and_never_asked_for(int liquid, int meta, string word)
    {
        // The cells round a liquid stand, so a pocket in rock has no open side: no pawn gets a face of it in view to plug or scoop
        // it by. Asked for anyway, its seal failed "out of reach even from a pillar" for ever and held the quarry between tries.
        var (c, _, w, _) = Rig();
        var pocket = new P3(X0 + 2, 45, Z0 + 2);
        w.Set(pocket.X, pocket.Y, pocket.Z, liquid, meta);
        Work(c, w, check: j => Assert.NotEqual(pocket, At(j)));
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds); // and no scoop after the last dig
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c));
        Assert.StartsWith("done: down to bedrock", Status(c));
        Assert.Equal((liquid, meta), w.Block(pocket.X, pocket.Y, pocket.Z));
        Assert.All(Sides(pocket), n => Assert.NotEqual(0, w.Block(n.X, n.Y, n.Z).Id)); // its six neighbours stand: it never spills
        Assert.Single(c.Alerts, a => a.Text.Contains(word)); // counted and said once
    }

    [Fact]
    public void A_contained_pocket_is_sealed_after_all_once_a_cell_beside_it_is_opened()
    {
        var (c, _, w, clock) = Rig();
        var pocket = new P3(X0 + 2, 62, Z0 + 2);
        w.Set(pocket.X, pocket.Y, pocket.Z, 9);
        var jobs = Work(c, w, until: j => At(j).Y < 62); // its layer is done with: left, the cell over it standing
        Assert.DoesNotContain(jobs, j => At(j) == pocket);
        Assert.NotEqual(0, w.Block(pocket.X, 63, pocket.Z).Id);
        EndHold(c);
        w.Set(pocket.X, 63, pocket.Z, 0); // blown open, or dug by a player: now it can spill, and a pawn can get at it
        clock.Now += TimeSpan.FromSeconds(31); // the next full look from the top
        var seal = c.Next();
        Assert.Equal("place", Kind(seal));
        Assert.Equal(pocket, At(seal));
    }

    [Fact]
    public void A_buried_lava_source_does_not_stop_the_quarry_without_a_bucket()
    {
        // With storage for the filled bucket and a table to make one at, a source under rock had a bucket crafted for it; with no
        // iron the quarry stood at its layer for good ("collecting a lava source at y 45").
        var (c, _, w, _) = Rig();
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        w.Set(1, 64, 9, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(1, 64, 9)).Error);
        var lava = new P3(X0 + 2, 45, Z0 + 2);
        w.Set(lava.X, lava.Y, lava.Z, 11);
        Work(c, w, check: j => Assert.NotEqual(lava, At(j)));
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c));
        Assert.StartsWith("done: down to bedrock", Status(c));
        Assert.Equal(11, w.Block(lava.X, lava.Y, lava.Z).Id);
        Assert.Empty(c.Benches.Single().Tasks); // no bucket was asked for: there was nothing to collect
    }

    [Fact]
    public void A_pool_two_deep_is_capped_and_the_water_under_the_cap_is_no_job()
    {
        var (c, _, w, _) = Rig();
        var top = new HashSet<P3>();
        var under = new HashSet<P3>();
        for (int x = 12; x <= 13; x++) for (int z = 12; z <= 13; z++)
        {
            top.Add(new P3(x, 63, z));
            under.Add(new P3(x, 62, z));
            w.Set(x, 63, z, 9);
            w.Set(x, 62, z, 9);
        }
        var caps = new List<P3>();
        Work(c, w, check: job =>
        {
            var at = At(job);
            Assert.DoesNotContain(at, under); // closed in by the caps and the cells standing beside it
            if (!top.Contains(at)) return;
            Assert.Equal("place", Kind(job)); // a cap over water is never mined
            caps.Add(at);
        });
        Assert.Equal(4, caps.Count);
        Assert.Equal(top, caps.ToHashSet());
        Assert.All(top, at => Assert.Equal(4, w.Block(at.X, at.Y, at.Z).Id));
        Assert.All(under, at => Assert.Equal(9, w.Block(at.X, at.Y, at.Z).Id));
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c)); // the quarry went on to the bottom
    }

    [Fact]
    public void A_lava_source_that_waits_does_not_hide_the_other_seals()
    {
        var (c, _, w, _) = Rig(); // no bucket, no storage: the lava source waits
        c.Zones.Single().QuarryTop = 63;
        var lava = new P3(X0 + 1, 63, Z0 + 1);
        var water = new P3(X0 + 4, 63, Z0 + 4);
        w.Set(lava.X, lava.Y, lava.Z, 11);
        w.Set(water.X, water.Y, water.Z, 9);
        var plug = c.Next();
        Assert.Equal("place", Kind(plug));
        Assert.Equal(water, At(plug));
        w.Set(water.X, water.Y, water.Z, 4);
        c.Report(c.P(), Id(plug), "done", null, null);
        // The lava source is still never plugged, and nothing is dug while it waits.
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Equal(11, w.Block(lava.X, lava.Y, lava.Z).Id);
        Assert.Contains("lava source at y 63: needs storage accepting lava buckets", Status(c));
    }

    // ------------------------------------------------------------------ the wait for a bucket: said, and over by itself

    /// <summary>Ends what the pawn holds now (an idle hold, a wander leg), so its next ask is a new pick.</summary>
    private static void EndHold(StoryColony c)
    {
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
    }

    /// <summary>The pawn asks again and is turned away: the quarry's status and its own idle reason say what the lava waits for.</summary>
    private static void Waits(StoryColony c, Clock clock, string where, string hold)
    {
        EndHold(c);
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Contains(where + hold, Status(c));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.StartsWith($"waiting: quarry {c.Zones.Single().Id}: {hold}", c.IdleReasonOf(c.P()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_lava_source_waiting_for_a_bucket_says_what_holds_it_and_is_collected_once_one_can_be_made(bool onStair)
    {
        // "collecting a lava source at y 63" (or "stair broken at y 63: placing a step") while nobody was, with nothing to say what
        // the colony was short of. The source is still never plugged: the wait names its hold, and ends when the hold does.
        var (c, _, w, clock) = Rig(new Stack(4, 0, 16)); // only its pillar blocks: nothing to store, a wait is an idle pawn
        c.Zones.Single().QuarryTop = 63;
        var at = new P3(X0 + (onStair ? 0 : 2), 63, Z0 + (onStair ? 0 : 2));
        w.Set(at.X, at.Y, at.Z, 11);
        string where = onStair ? "stair broken at y 63: lava source on the step: " : "lava source at y 63: ";

        Waits(c, clock, where, "needs storage accepting lava buckets");

        var chest = new P3(0, 64, 9);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        c.StorageOp("set", [chest], null, null, null);
        Waits(c, clock, where, "needs an empty bucket to collect lava: no crafting table to craft one at");

        w.Set(1, 64, 9, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(1, 64, 9)).Error);
        Waits(c, clock, where, "needs an empty bucket to collect lava: no pawn does craft work");
        var craft = Assert.Single(c.Benches.Single().Tasks); // asked for, once
        Assert.Equal("craft:bucket", craft.Recipe);
        Assert.True(c.Immediate(craft));

        c.Pawns[c.P()].Priorities[WorkTypes.Craft] = 2;
        Waits(c, clock, where, "needs an empty bucket to collect lava: its craft waits (no iron ingot");
        Assert.Single(c.Benches.Single().Tasks);

        // Iron in storage: the bucket is crafted and stored, then fetched and filled. Nothing had to be retried by hand.
        c.AddContents(c.StorageAt(chest)!, new Stack(265, 0, 3));
        EndHold(c);
        var make = c.Next();
        Assert.Equal("craft", Kind(make));
        c.Report(c.P(), Id(make), "done", null, null);
        var scoop = c.Next();
        Assert.Equal("scoop_lava", Kind(scoop));
        Assert.Equal(at, At(scoop));
        Assert.Equal("bucket", scoop["fetch"]![0]!["item"]!.GetValue<string>());
        Assert.Contains(onStair ? "stair broken at y 63: collecting a lava source on the step" : "collecting a lava source at y 63", Status(c));
    }

    /// <summary>A lava source in the top layer (in the stair's first step: <paramref name="onStair"/>), a chest that takes tools only
    /// (so a filled bucket, never cobblestone), and a miner with no free slot who carries <paramref name="buckets"/> empty buckets
    /// and cobblestone that has nowhere to go.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) FullWithBuckets(IGameData data, int buckets, bool onStair = false)
    {
        var (c, h, w, clock) = Rig(data);
        c.Zones.Single().QuarryTop = 63;
        var chest = new P3(0, 64, 9);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        c.SetHaulOptions(null, false, null); // unfiltered storage off: a chest takes only what its filter allows
        Assert.Null(c.StorageOp("set", [chest], new StorageFilter { Allow = ["tools"] }, null, null).Error);
        w.Set(X0 + (onStair ? 0 : 2), 63, Z0 + (onStair ? 0 : 2), 11);
        Stack[] carried = buckets > 0 ? [new Stack(325, 0, buckets), new Stack(4, 0, 33 * 64)] : [new Stack(4, 0, 34 * 64)];
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: carried) with { FreeSlots = 0 };
        return (c, h, w, clock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void On_1_12_2_a_full_pawn_with_stacked_buckets_does_not_scoop(bool onStair)
    {
        // 1.12.2 stacks empty buckets: filling one of a stack puts the filled bucket in another slot, and with none free it drops at
        // the pawn's feet ("lava collection not confirmed": a failed job, and the lava bucket lost on the pit floor).
        var (c, h, _, clock) = FullWithBuckets(Release1122GameData.Instance, buckets: 2, onStair);
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Contains((onStair ? "stair broken at y 63: lava source on the step: " : "lava source at y 63: ")
            + "needs a free slot for the bucket (pockets full)", Status(c));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Equal($"waiting: quarry {c.Zones.Single().Id}: needs a free slot for the bucket (pockets full)", c.IdleReasonOf(c.P()));
        // One slot free: it collects.
        h.Lives[c.P()] = h.Lives[c.P()] with { FreeSlots = 1 };
        EndHold(c);
        var scoop = c.Next();
        Assert.Equal("scoop_lava", Kind(scoop));
        Assert.Null(scoop["fetch"]);
    }

    [Theory]
    [InlineData(true, 1)] // 1.12.2, a single bucket: filled where it is
    [InlineData(false, 2)] // b1.8.1 never stacks buckets: each is filled in its own slot
    public void A_full_pawn_whose_bucket_is_filled_in_its_own_slot_still_scoops(bool modern, int buckets)
    {
        var (c, _, _, _) = FullWithBuckets(modern ? Release1122GameData.Instance : Beta181GameData.Instance, buckets);
        var scoop = c.Next();
        Assert.Equal("scoop_lava", Kind(scoop));
        Assert.Null(scoop["fetch"]);
    }

    [Fact]
    public void A_full_pawn_does_not_set_out_to_fetch_a_bucket_it_has_no_slot_for()
    {
        var (c, _, _, _) = FullWithBuckets(Beta181GameData.Instance, buckets: 0);
        c.AddContents(c.StorageAt(new P3(0, 64, 9))!, new Stack(325, 0, 1));
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Contains("lava source at y 63: needs a free slot for the bucket (pockets full)", Status(c));
    }

    // ------------------------------------------------------------------ the stair always gets through

    /// <summary>Step <paramref name="k"/> of the rig's stair, or the cell <paramref name="over"/> it (1..3: its head room).</summary>
    private static P3 Step(StoryColony c, int k, int over = 0) => StoryQuarry.Step(Ring(c), 63, k).Add(0, over, 0);

    private static int IdAt(FakeStoryWorld w, P3 p) => w.Block(p.X, p.Y, p.Z).Id;

    /// <summary>The quarry dug down to y 55 (its working layer: 8), the pawn free to ask again.</summary>
    private static void WorkToY55(StoryColony c, FakeStoryWorld w)
    {
        Work(c, w, until: j => At(j).Y < 56);
        EndHold(c); // the first job of y 55: left for later
    }

    /// <summary>The alerts that say the quarry's stair can't get through by itself.</summary>
    private static List<string> HeldUp(StoryColony c) => c.Alerts.Where(a => a.Text.Contains("is held up")).Select(a => a.Text).ToList();

    [Theory]
    [InlineData(9, 0, "water")] // a water source
    [InlineData(11, 0, "lava")] // a lava source outside the quarry's columns: a hazard like any liquid, it gets a block too
    [InlineData(10, 2, "lava")] // flowing lava
    public void Liquid_beside_the_stairs_head_room_is_plugged_and_the_stair_stays_walkable(int liquid, int meta, string word)
    {
        // The block two over step 7 (east edge, y 56) has the liquid right behind it, outside the zone, in solid rock. Left standing
        // like any cell beside a liquid, it blocked the stair for good once the pit was a step deeper: "stair blocked at 15,58,12:
        // clearing its headroom", with nothing cleared and nothing else offered.
        var (c, _, w, _) = Rig();
        var head = Step(c, 7, over: 2);
        Assert.Equal(X0 + Size - 1, head.X);
        var behind = head.Add(1, 0, 0);
        w.Set(behind.X, behind.Y, behind.Z, liquid, meta);
        JsonObject? plug = null;
        int y = int.MaxValue;
        Work(c, w, check: j =>
        {
            var at = At(j);
            if (at == behind) plug = j;
            if (at == head) Assert.Equal(4, IdAt(w, behind)); // dug only once the liquid behind it is a block
            if (at.Y == y) return;
            y = at.Y;
            Assert.DoesNotContain("stair blocked", Status(c));
        });
        Assert.NotNull(plug);
        Assert.Equal("place", Kind(plug!));
        Assert.Null(plug!["reach"]); // no face of that cell is in view from the pit: the ordinary place, which clicks a hidden one
        Assert.Equal("cobblestone", plug["block"]!.GetValue<string>());
        Assert.Equal(4, IdAt(w, behind)); // outside the quarry: the plug stays
        Assert.Equal(0, IdAt(w, head));
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c));
        Assert.Contains(Walkable(w, PitBottom(w)), p => p.Y >= 64);
        Assert.DoesNotContain("stair blocked", Status(c));
        Assert.Single(c.Alerts, a => a.Text.Contains(word)); // said once
    }

    [Fact]
    public void Head_room_left_standing_in_a_pit_dug_past_it_is_plugged_from_the_step_before_and_then_dug()
    {
        // What the old rule left in a pit that is deeper by now: the block still standing two over step 7, the lava behind it.
        var (c, _, w, clock) = Rig();
        WorkToY55(c, w);
        var head = Step(c, 7, over: 2);
        var behind = head.Add(1, 0, 0);
        w.Set(head.X, head.Y, head.Z, 1);
        w.Set(behind.X, behind.Y, behind.Z, 11);
        clock.Now += TimeSpan.FromSeconds(6);
        var plug = c.Next();
        Assert.Equal("place", Kind(plug));
        Assert.Equal(behind, At(plug));
        Assert.Null(plug["reach"]);
        // It works from the last step it can stand on: the one before the blocked one, the lava's cell beside it behind the wall.
        Assert.Equal(Step(c, 6, over: 1).Y, plug["quarry_access_y"]!.GetValue<int>());
        Assert.Equal(Step(c, 6, over: 1).ToString(), plug["quarry_approach"]!.AsArray().Last()!.GetValue<string>());
        Assert.Contains($"stair blocked at {head}: sealing the lava at {behind} first", Status(c));
        w.Set(behind.X, behind.Y, behind.Z, 4);
        c.Report(c.P(), Id(plug), "done", null, null);
        var clear = c.Next();
        Assert.Equal("dig", Kind(clear));
        Assert.Equal(head, At(clear));
        Assert.Contains($"stair blocked at {head}: clearing its headroom", Status(c));
    }

    [Fact]
    public void A_liquid_step_cell_under_rock_does_not_break_the_stair_and_is_plugged_once_opened()
    {
        // Water where step 9 belongs, under rock. It was a missing step as soon as the layer over it was the working one: the pawn
        // sent to place the block had no face of it in view, the repair failed and rested, and nothing else was offered again
        // ("stair broken at y 54: placing a step"). Now the cell over it is dug first (liquid doesn't flow up), then the step is placed.
        var (c, _, w, _) = Rig();
        var step = Step(c, 9);
        var over = step.Add(0, 1, 0);
        w.Set(step.X, step.Y, step.Z, 9);
        bool placed = false;
        Work(c, w, check: j =>
        {
            if (!placed && IdAt(w, over) != 0) Assert.DoesNotContain("stair broken", Status(c)); // no missing step yet
            if (At(j) != step) return;
            Assert.Equal("place", Kind(j));
            Assert.Equal(0, IdAt(w, over)); // opened: placed from above, with a face in view
            placed = true;
        });
        Assert.True(placed);
        Assert.Equal(4, IdAt(w, step));
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c));
        Assert.DoesNotContain("stair broken", Status(c));
        Assert.Contains(Walkable(w, PitBottom(w)), p => p.Y >= 64);
    }

    [Fact]
    public void A_lava_source_step_under_rock_is_collected_once_the_cell_over_it_is_dug_never_plugged()
    {
        var (c, _, w, _) = Rig(new Stack(325, 0, 1), new Stack(4, 0, 16));
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        var step = Step(c, 9);
        var over = step.Add(0, 1, 0);
        w.Set(step.X, step.Y, step.Z, 11);
        var jobs = Work(c, w, check: j => Assert.NotEqual(step, At(j))); // never a block in it
        Assert.Contains(jobs, j => Kind(j) == "dig" && At(j) == over);
        var scoop = c.Next(); // what ended the digging: the bucket, from above
        Assert.Equal("scoop_lava", Kind(scoop));
        Assert.Equal(step, At(scoop));
        Assert.Equal(0, IdAt(w, over));
        Assert.Equal(11, IdAt(w, step));
    }

    [Fact]
    public void Liquid_that_got_into_head_room_is_plugged_by_the_stair_repair()
    {
        // Water in the head room of an excavated step (a pocket blown open, a plug taken out beside a source): the cell was listed as
        // blocked head room, the clearing refused a liquid, and the seal that would have plugged it waited for the stair.
        var (c, _, w, clock) = Rig();
        WorkToY55(c, w);
        var wet = Step(c, 5, over: 1);
        w.Set(wet.X, wet.Y, wet.Z, 8, 2); // flowing water
        clock.Now += TimeSpan.FromSeconds(6);
        var plug = c.Next();
        Assert.Equal("place", Kind(plug));
        Assert.Equal(wet, At(plug));
        Assert.Null(plug["reach"]);
        Assert.Equal(63 + 1 - 4, plug["quarry_access_y"]!.GetValue<int>()); // from the step before it
        Assert.Contains($"stair blocked at {wet}: sealing the water in it first", Status(c));
        w.Set(wet.X, wet.Y, wet.Z, 4);
        c.Report(c.P(), Id(plug), "done", null, null);
        var clear = c.Next();
        Assert.Equal("dig", Kind(clear));
        Assert.Equal(wet, At(clear));
        w.Set(wet.X, wet.Y, wet.Z, 0);
        c.Report(c.P(), Id(clear), "done", null, null);
        Assert.Equal(55, At(c.Next()).Y); // and the quarry goes on where it was
    }

    [Fact]
    public void A_block_over_liquid_in_head_room_is_dug_first_and_the_liquid_under_it_then_plugged()
    {
        var (c, _, w, clock) = Rig();
        WorkToY55(c, w);
        var wet = Step(c, 5, over: 1);
        var lid = Step(c, 5, over: 2);
        w.Set(wet.X, wet.Y, wet.Z, 9);
        w.Set(lid.X, lid.Y, lid.Z, 1);
        clock.Now += TimeSpan.FromSeconds(6);
        var jobs = Work(c, w, until: j => At(j).Y < 56);
        List<(string, P3)> want = [("dig", lid), ("place", wet), ("dig", wet)];
        Assert.Equal(want, jobs.Select(j => (Kind(j), At(j))).ToList());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_lava_source_that_got_into_head_room_is_collected_by_the_stair_repair_never_plugged(bool bucket)
    {
        var (c, _, w, clock) = bucket ? Rig(new Stack(325, 0, 1), new Stack(4, 0, 16)) : Rig();
        var chest = new P3(0, 64, 9);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        if (bucket) c.StorageOp("set", [chest], null, null, null);
        WorkToY55(c, w);
        var lava = Step(c, 5, over: 1);
        w.Set(lava.X, lava.Y, lava.Z, 11);
        clock.Now += TimeSpan.FromSeconds(6);
        var job = c.Next();
        Assert.Equal(11, IdAt(w, lava));
        if (bucket)
        {
            Assert.Equal("scoop_lava", Kind(job));
            Assert.Equal(lava, At(job));
            Assert.Equal(63 + 1 - 4, job["quarry_access_y"]!.GetValue<int>());
            Assert.Contains($"stair blocked at {lava}: collecting a lava source in it", Status(c));
            return;
        }
        Assert.DoesNotContain(Kind(job), QuarryKinds); // it waits, with the hold named
        Assert.Contains($"stair blocked at {lava}: a lava source in it: needs storage accepting lava buckets", Status(c));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Equal($"waiting: quarry {c.Zones.Single().Id}: needs storage accepting lava buckets", c.IdleReasonOf(c.P()));
        clock.Now += TimeSpan.FromSeconds(6); // a new look: the hold is still what it waits for, not "collecting" with nobody at it
        Assert.Contains($"stair blocked at {lava}: a lava source in it: needs storage accepting lava buckets", Status(c));
    }

    [Fact]
    public void A_lava_source_of_the_quarrys_own_behind_head_room_is_never_plugged_and_the_stair_says_what_it_waits_for()
    {
        // The owner's rule: a lava source in the quarry's own columns is collected, never plugged, behind the stair's head room too.
        // Closed in by rock it can't be collected either (a bucket needs an open side), so the block of head room in front of it
        // stands and the stair ends there: said in the status, the idle audit and one alert, never a silent "clearing its headroom".
        var (c, _, w, clock) = Rig(new Stack(325, 0, 1), new Stack(4, 0, 16));
        var chest = new P3(0, 64, 9);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        c.StorageOp("set", [chest], null, null, null);
        var head = Step(c, 7, over: 2);
        var lava = head.Add(-1, 0, 0); // inside the zone
        Assert.True(InZone(lava));
        w.Set(lava.X, lava.Y, lava.Z, 11);
        Work(c, w, check: j => Assert.True(At(j) != lava && At(j) != head, $"{Kind(j)} at {At(j)}"));
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Equal(8, c.Zones.Single().QuarryLayer); // the stair ends at step 7
        Assert.Equal(11, IdAt(w, lava));
        string held = $"stair blocked at {head}: the lava source at {lava} can't be collected (no open side) and is never plugged";
        Assert.Contains(held, Status(c));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.StartsWith($"waiting: quarry {c.Zones.Single().Id}: stair blocked at {head}: the lava source at {lava}", c.IdleReasonOf(c.P()));
        Assert.Contains(held, Assert.Single(HeldUp(c)));
        // Opened (the owner digs the cell over it): it can be collected, so it is, and the stair goes on.
        w.Set(lava.X, lava.Y + 1, lava.Z, 0);
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(6);
        var scoop = c.Next();
        Assert.Equal("scoop_lava", Kind(scoop));
        Assert.Equal(lava, At(scoop));
        w.Set(lava.X, lava.Y, lava.Z, 0);
        c.Report(c.P(), Id(scoop), "done", null, null);
        var clear = c.Next();
        Assert.Equal("dig", Kind(clear));
        Assert.Equal(head, At(clear));
    }

    [Fact]
    public void A_danger_cell_in_head_room_is_cleared()
    {
        // A cell a pawn met danger at is left standing from then on; in the stair's head room that ended the stair.
        var (c, _, w, clock) = Rig();
        var head = Step(c, 7, over: 2);
        Work(c, w, until: j => At(j) == head);
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "failed", "in lava!", null);
        clock.Now += TimeSpan.FromMinutes(15); // its rest over
        Work(c, w);
        Assert.Equal(0, IdAt(w, head));
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c));
        Assert.Contains(Walkable(w, PitBottom(w)), p => p.Y >= 64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sand_or_gravel_standing_over_head_room_is_taken_from_its_top(bool wet)
    {
        // Dug from under it, it would fall into the stair. Water touching it would follow: that gets its block first.
        var (c, _, w, clock) = Rig();
        WorkToY55(c, w);
        var head = Step(c, 7, over: 3);
        var low = head.Add(0, 1, 0);
        var high = head.Add(0, 2, 0);
        var water = low.Add(1, 0, 0); // outside the zone
        w.Set(head.X, head.Y, head.Z, 1);
        w.Set(low.X, low.Y, low.Z, 13);
        w.Set(high.X, high.Y, high.Z, 13);
        if (wet) w.Set(water.X, water.Y, water.Z, 9);
        clock.Now += TimeSpan.FromSeconds(6);
        var jobs = Work(c, w, until: j => At(j).Y < 56).Select(j => (Kind(j), At(j), Reach: j["reach"] != null)).ToList();
        List<(string, P3, bool)> want = [("dig", high, false), ("dig", low, false), ("dig", head, false)];
        if (wet) want.Insert(0, ("place", water, false));
        Assert.Equal(want, jobs);
    }

    [Fact]
    public void What_cannot_be_cleared_is_named_in_the_status()
    {
        // A chest in the stair's head room is not the quarry's to dig. It said "clearing its headroom" while nothing was, for good.
        var (c, _, w, clock) = Rig();
        WorkToY55(c, w);
        var chest = Step(c, 5, over: 1);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        clock.Now += TimeSpan.FromSeconds(6);
        string held = $"stair blocked at {chest}: chest in its head room (move it)";
        for (int i = 0; i < 5; i++)
        {
            Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
            Assert.Contains(held, Status(c));
            clock.Now += TimeSpan.FromSeconds(2);
            c.Tick();
            Assert.Equal($"waiting: quarry {c.Zones.Single().Id}: {held}", c.IdleReasonOf(c.P()));
            EndHold(c);
            clock.Now += TimeSpan.FromSeconds(20);
        }
        Assert.Contains(held, Assert.Single(HeldUp(c))); // one alert, however often it asked
        Assert.Equal(54, IdAt(w, chest));
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Equal(2, HeldUp(c).Count); // and again after 10 minutes, while it stands
        // Moved by the owner: the quarry goes on by itself.
        w.Set(chest.X, chest.Y, chest.Z, 0);
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal("dig", Kind(c.Next()));
        Assert.DoesNotContain("stair blocked", Status(c));
    }

    [Fact]
    public void Head_room_waiting_for_a_tool_names_the_tool()
    {
        // Diamond ore in the stair's head room and only a stone pickaxe: nobody digs it, and the status said "clearing its headroom".
        var (c, h, w, clock) = Rig();
        WorkToY55(c, w);
        var head = Step(c, 5, over: 1);
        w.Set(head.X, head.Y, head.Z, 56);
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        string waits = $"stair blocked at {head}: clearing its headroom: waiting for a tool (needs an iron pickaxe";
        Assert.Contains(waits, Status(c));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        // The idle audit cuts a note at 90 characters.
        Assert.StartsWith($"waiting: quarry {c.Zones.Single().Id}: stair blocked at {head}: clearing its headroom: waiting for a tool",
            c.IdleReasonOf(c.P()));
        // An iron pickaxe at hand: it is dug, and the wait is no longer said.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(4, 0, 64), new Stack(257, 0, 1)]);
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(6);
        var dig = c.Next();
        Assert.Equal("dig", Kind(dig));
        Assert.Equal(head, At(dig));
        Assert.DoesNotContain("waiting for a tool", Status(c));
    }

    [Fact]
    public void A_plug_the_server_refuses_twice_is_named_with_its_failure_and_tried_again()
    {
        // The plug is clicked against a face the pawn can't see. A server that refuses such a click (an anti-cheat plugin) leaves
        // the stair blocked: that is said, with the failure, instead of a silent wait; and the plug is tried again after each rest.
        var (c, _, w, clock) = Rig();
        var head = Step(c, 7, over: 2);
        var behind = head.Add(1, 0, 0);
        w.Set(behind.X, behind.Y, behind.Z, 9);
        void Refuse()
        {
            var j = c.Next();
            Assert.Equal("place", Kind(j));
            Assert.Equal(behind, At(j));
            c.Report(c.P(), Id(j), "failed", $"cobblestone didn't appear at {behind}", null);
        }
        Work(c, w, until: j => At(j) == behind);
        Refuse();
        Work(c, w, check: j => Assert.True(At(j) != behind && At(j) != head, $"{Kind(j)} at {At(j)}")); // the rest of the pit goes on
        Assert.Equal(8, c.Zones.Single().QuarryLayer); // down to the step under the blocked head room, no further
        string held = $"stair blocked at {head}: sealing the water at {behind} first: resting after failures (cobblestone didn't appear)";
        Assert.Contains(held, Status(c));
        Assert.Empty(HeldUp(c)); // one refusal may be anything
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(31);
        Refuse();
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Contains(held, Assert.Single(HeldUp(c)));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.StartsWith($"waiting: quarry {c.Zones.Single().Id}: stair blocked at {head}: sealing the water at {behind} first", c.IdleReasonOf(c.P()));
        // Its rest over, it is tried again; placed, the head room is dug and the quarry goes on down.
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(61);
        var jobs = Work(c, w);
        List<(string, P3)> first = [("place", behind), ("dig", head)];
        Assert.Equal(first, jobs.Take(2).Select(j => (Kind(j), At(j))).ToList());
        Assert.True(c.Zones.Single().QuarryLayer > 60, Status(c));
        Assert.Contains(Walkable(w, PitBottom(w)), p => p.Y >= 64);
    }

    [Fact]
    public void A_step_placement_that_keeps_failing_is_named_with_its_failure()
    {
        // A missing step whose placement failed rested with the status still "placing a step", and nobody was.
        var (c, _, w, clock) = Rig();
        WorkToY55(c, w);
        var gone = Step(c, 5);
        w.Set(gone.X, gone.Y, gone.Z, 0);
        clock.Now += TimeSpan.FromSeconds(6);
        void Refuse()
        {
            var j = c.Next();
            Assert.Equal("place", Kind(j));
            Assert.Equal(gone, At(j));
            c.Report(c.P(), Id(j), "failed", $"cobblestone didn't appear at {gone}", null);
        }
        Refuse();
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds); // nothing else while the stair is broken
        string held = $"stair broken at y {gone.Y}: placing a step: resting after failures (cobblestone didn't appear)";
        Assert.Contains(held, Status(c));
        Assert.Empty(HeldUp(c));
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(31);
        Refuse();
        Assert.DoesNotContain(Kind(c.Next()), QuarryKinds);
        Assert.Contains(held, Assert.Single(HeldUp(c))); // twice: said
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(61);
        var again = c.Next();
        Assert.Equal("place", Kind(again));
        Assert.Equal(gone, At(again));
    }

    [Fact]
    public void A_monster_spawner_beside_the_quarry_stops_it_with_one_alert_until_it_is_gone()
    {
        var (c, _, w, clock) = Rig();
        var spawner = new P3(X0 + Size + 2, 48, Z0 + 2);
        w.Set(spawner.X, spawner.Y, spawner.Z, 52);
        Work(c, w, check: j => Assert.True(At(j).Y >= 52, $"{At(j)} dug past the spawner's reach"));
        Assert.Contains("monster spawner", Status(c));
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.NotEqual("dig", c.Next()["kind"]?.GetValue<string>());
        Assert.Single(c.Alerts, a => a.Level == "warn" && a.Text.Contains("monster spawner"));
        w.Set(spawner.X, spawner.Y, spawner.Z, 0); // broken by the owner
        clock.Now += TimeSpan.FromSeconds(6);
        var j = c.Next();
        if (j["kind"]?.GetValue<string>() is not ("dig" or "place")) { c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null); clock.Now += TimeSpan.FromSeconds(6); j = c.Next(); }
        Assert.Equal("dig", j["kind"]!.GetValue<string>());
    }

    [Fact]
    public void A_pawn_that_met_lava_at_a_quarry_cell_leaves_it_standing()
    {
        var (c, _, w, clock) = Rig();
        var at = new P3(X0 + 2, 63, Z0 + 2); // not in the stair's head room: there it is dug all the same
        Work(c, w, until: x => At(x) == at);
        var j = c.Next();
        Assert.Equal(at, At(j));
        c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "in lava!", null);
        clock.Now += TimeSpan.FromMinutes(15);
        Work(c, w, check: x => Assert.NotEqual(at, At(x)), until: x => At(x).Y < 60);
        Assert.NotEqual(0, w.Block(at.X, at.Y, at.Z).Id);
    }

    // ------------------------------------------------------------------ what a done quarry leaves, said once

    [Fact]
    public void A_done_quarry_lists_exactly_what_stands_and_why()
    {
        // Before: "sealed" was the working layer's count only (0 once done), labelled "sealed by lava or water", the status summed every
        // hazard this process had met, and nothing listed where the cells were or said that the quarry was done.
        var (c, _, w, clock) = Rig();
        var pool = new P3(X0 + 2, 45, Z0 + 2); // water closed in by rock: the pool and its six walls stay
        w.Set(pool.X, pool.Y, pool.Z, 9);
        w.Set(X0 - 1, 40, Z0 + 2, 11); // lava just outside the west edge: the edge cell beside it stays
        Carve(w, X0 + Size, 52, Z0 + 2, X0 + Size + 4, 52, Z0 + 2); // a cave tunnel off the east edge
        w.Unloaded.Add((X0 + Size + 5, Z0 + 2)); // running on into ground no body has loaded: a cave, no closed pocket
        var expected = new HashSet<(P3, string)>
        {
            (new P3(X0 + Size - 1, 52, Z0 + 2), "cave"),
            (pool.Add(0, 1, 0), "water"), (pool.Add(1, 0, 0), "water"), (pool.Add(-1, 0, 0), "water"),
            (pool.Add(0, 0, 1), "water"), (pool.Add(0, 0, -1), "water"), (pool.Add(0, -1, 0), "water"),
            (pool, "pool"),
            (new P3(X0, 40, Z0 + 2), "lava"),
        };
        Work(c, w, check: j => Assert.DoesNotContain(At(j), expected.Select(e => e.Item1)));
        var q = c.State()["zones"]!.AsArray().Single()!["quarry"]!;
        Assert.True(q["done"]!.GetValue<bool>(), Status(c));
        Assert.Equal("""{"cave":1,"lava":1,"pool":1,"step":61,"water":6}""", q["left"]!.ToJsonString());
        Assert.Equal(9, q["sealed"]!.GetValue<int>());
        var cells = q["leftovers"]!.AsArray().Select(n => (P3.Parse(n!["at"])!.Value, n["kind"]!.GetValue<string>())).ToList();
        Assert.Equal(expected, cells.ToHashSet());
        Assert.Equal(expected.Count, cells.Count);
        Assert.Equal(cells.Select(x => x.Item1.Y).OrderByDescending(y => y), cells.Select(x => x.Item1.Y)); // top-most first
        Assert.All(expected, e => Assert.NotEqual(0, w.Block(e.Item1.X, e.Item1.Y, e.Item1.Z).Id));
        Assert.Equal("done: down to bedrock; left standing: 6 water, 1 cave, 1 lava, 1 pool (breached a cave at y 52)", Status(c));
        var done = Assert.Single(c.Alerts, a => a.Text.Contains("is done"));
        Assert.Contains("9 cell(s) left standing apart from its stair", done.Text);
        // The next full looks from the top find the same, and say nothing again.
        for (int i = 0; i < 3; i++)
        {
            clock.Now += TimeSpan.FromSeconds(31);
            Assert.Equal("""{"cave":1,"lava":1,"pool":1,"step":61,"water":6}""", c.State()["zones"]![0]!["quarry"]!["left"]!.ToJsonString());
        }
        Assert.Single(c.Alerts, a => a.Text.Contains("is done"));
    }

    [Fact]
    public void What_is_left_comes_from_the_world_not_from_what_was_once_met()
    {
        // A plug placed over a water pool and mined once the water was gone used to stay in the summary for good ("sealed: 1 water").
        var (c, _, w, clock) = Rig();
        var wet = new P3(X0 + 2, 63, Z0 + 2);
        w.Set(wet.X, wet.Y, wet.Z, 9);
        var seal = c.Next();
        Assert.Equal(("place", wet), (Kind(seal), At(seal)));
        w.Set(wet.X, wet.Y, wet.Z, 4);
        c.Report(c.P(), Id(seal), "done", null, null);
        Assert.Equal(new[] { wet }, c.Zones.Single().QuarryPlaced!); // the seal is the quarry's own (never swept as a stray)
        Work(c, w, until: j => At(j).Y < 62);
        clock.Now += TimeSpan.FromSeconds(31); // a full look: the plug was mined, nothing stands for the water
        var left = c.State()["zones"]![0]!["quarry"]!["left"]!.AsObject();
        Assert.Equal(new[] { "step" }, left.Select(e => e.Key));
        Assert.Equal(0, c.State()["zones"]![0]!["quarry"]!["sealed"]!.GetValue<int>());
        Assert.DoesNotContain("left standing", Status(c));
        Assert.Null(c.Zones.Single().QuarryPlaced); // its cell is open again: forgotten
    }

    [Fact]
    public void A_lava_source_over_the_painted_level_of_a_partly_seen_quarry_is_collected_never_plugged()
    {
        // Painted at y 63 on a hill up to y 66, one column out of view: no top is kept yet, and the quarry's own lava source at y 66 was
        // taken for one outside it (over the painted level) and got a block.
        var (c, h, w, _) = Rig(new Stack(325, 0, 1), new Stack(4, 0, 16));
        c.StorageOp("set", [new P3(0, 64, 9)], null, null, null);
        for (int x = X0; x < X0 + Size; x++)
            for (int z = Z0; z < Z0 + Size; z++)
                for (int y = 64; y <= 66; y++) w.Set(x, y, z, 1);
        w.Unloaded.Add((X0 + 4, Z0 + 4));
        var lava = new P3(X0 + 2, 66, Z0 + 2);
        w.Set(lava.X, lava.Y, lava.Z, 11);
        var job = c.Next();
        Assert.Null(c.Zones.Single().QuarryTop);
        Assert.Equal(("scoop_lava", lava), (Kind(job), At(job)));
        Assert.Same(c.Zones.Single(), c.QuarryAt(lava)); // the look's top, while none is kept
        Assert.Equal(11, w.Block(lava.X, lava.Y, lava.Z).Id);
        // The Quarries panel shows it all the same (it showed nothing while no top was kept).
        var quarry = c.State()["zones"]![0]!["quarry"]!;
        Assert.Equal(66, quarry["top"]!.GetValue<int>());
        Assert.Equal(64, quarry["depth"]!.GetValue<int>());
    }
}
