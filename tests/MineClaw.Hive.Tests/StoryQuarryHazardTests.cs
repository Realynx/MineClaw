using System.Text.Json.Nodes;
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

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(params Stack[] inv)
    {
        var clock = new Clock();
        var (c, h, world) = StoryRig.Make(time: clock);
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
        Carve(w, X0 + Size, 59, Z0 + 3, X0 + Size, 63, Z0 + 3);
        Carve(w, X0 + Size, 64, Z0 + 3, X0 + Size, 66, Z0 + 3, 1);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal(59, At(job).Y);
        Assert.NotEqual(0, w.Block(At(job).X, 58, At(job).Z).Id);
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
        w.Set(X0 + Size, 59, Z0 + 2, 0);
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
        Carve(w, step.X + 1, step.Y + 1, step.Z, step.X + 4, step.Y + 2, step.Z); // a cave tunnel beside the two cells over it
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
    [InlineData(11, "lava")]
    [InlineData(9, "water")]
    public void Lava_or_water_in_the_quarry_is_covered_with_a_block_that_doesnt_burn_before_its_cells_are_dug_and_said_once(int liquid, string word)
    {
        var (c, _, w, _) = Rig(new Stack(5, 0, 64), new Stack(4, 0, 64)); // planks first in the inventory: never used for a seal
        var pool = new P3(X0 + 2, 45, Z0 + 2);
        w.Set(pool.X, pool.Y, pool.Z, liquid);
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
        Assert.Single(c.Alerts, a => a.Text.Contains("monster spawner"));
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
        var j = c.Next();
        var at = At(j);
        c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "in lava!", null);
        clock.Now += TimeSpan.FromMinutes(15);
        Work(c, w, check: x => Assert.NotEqual(at, At(x)), until: x => At(x).Y < 60);
        Assert.NotEqual(0, w.Block(at.X, at.Y, at.Z).Id);
    }
}
