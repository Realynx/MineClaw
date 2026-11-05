using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Quarry zones (docs/web/story-mode.md "Quarries"). Fox 11-04: "The quarry zone didn't make sense — nothing happened." A quarry digs
/// everything down to bedrock, layer by layer, leaving a perimeter stair (a 2×2 is a spiral shaft) to walk in and out by.
/// </summary>
public sealed class StoryQuarryTests
{
    private const int X0 = 10, Z0 = 10, BedrockTop = 2;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
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

    private static string Quarry(StoryColony c, int w, int d, JsonObject? body = null)
    {
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(X0, 63, Z0, X0 + w - 1, 63, Z0 + d - 1), grow: body);
        Assert.True(err == null, err);
        return res!["id"]!.GetValue<string>();
    }

    /// <summary>The pawn does every job the quarry hands out (the world changes as the script would), until it has none.</summary>
    private static (int Digs, int Places, List<JsonObject> Jobs) Work(StoryColony c, FakeStoryWorld w, int max = 6000,
        Func<JsonObject, bool>? until = null, Func<P3, bool>? fails = null, int others = 0, Func<P3, string>? failWhy = null)
    {
        int digs = 0, places = 0;
        var jobs = new List<JsonObject>();
        for (int i = 0; i < max; i++)
        {
            var j = c.Next();
            var kind = j["kind"]!.GetValue<string>();
            if (kind is not ("dig" or "place") && others-- > 0) { c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null); continue; }
            if (kind is not ("dig" or "place") || until?.Invoke(j) == true) break;
            jobs.Add(j);
            var at = P3.Parse(JsonNode.Parse("[" + j["at"]!.GetValue<string>() + "]"))!.Value;
            if (fails?.Invoke(at) == true)
            {
                c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", failWhy?.Invoke(at) ?? $"no path to within 3 of ({at}) (no route)", null);
                continue;
            }
            if (kind == "dig") { Assert.NotEqual(7, w.Block(at.X, at.Y, at.Z).Id); w.Set(at.X, at.Y, at.Z, 0); digs++; }
            else { w.Set(at.X, at.Y, at.Z, 4); places++; }
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        }
        return (digs, places, jobs);
    }

    /// <summary>Can a body walk (1-block rises, a jump needs the block over its head open) from <paramref name="from"/> to the surface?</summary>
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
                    if (dy == 1 && !StoryWander.Passable(w.Block(a.X, a.Y + 2, a.Z).Id)) continue; // the jump: room over its head
                    if (dy == -1 && !StoryWander.Passable(w.Block(b.X, b.Y + 2, b.Z).Id)) continue; // stepping down into the column
                    seen.Add(b);
                    todo.Enqueue(b);
                }
        }
        return false;
    }

    private static void AssertQuarried(StoryColony c, FakeStoryWorld w, string id, int width, int depthZ)
    {
        var z = c.Zones.Single(x => x.Id == id);
        var ring = StoryQuarry.Ring(z.Cells)!;
        Assert.NotNull(ring);
        int top = z.QuarryTop!.Value;
        Assert.Equal(63, top);
        var stair = Enumerable.Range(0, top - BedrockTop).Select(k => StoryQuarry.Step(ring, top, k)).ToHashSet();
        for (int x = X0; x < X0 + width; x++)
            for (int zz = Z0; zz < Z0 + depthZ; zz++)
            {
                for (int y = BedrockTop + 1; y <= top; y++)
                {
                    var p = new P3(x, y, zz);
                    int b = w.Block(x, y, zz).Id;
                    if (stair.Contains(p)) Assert.True(b != 0, $"step {p} gone");
                    else Assert.True(b == 0, $"{p} not dug ({b})");
                }
                for (int y = 0; y <= BedrockTop; y++) Assert.Equal(7, w.Block(x, y, zz).Id); // bedrock untouched
            }
        // The steps: one per layer, each one block below the last and beside it, 2 open cells over each.
        var steps = Enumerable.Range(0, top - BedrockTop).Select(k => StoryQuarry.Step(ring, top, k)).ToList();
        for (int k = 1; k < steps.Count; k++)
        {
            Assert.Equal(steps[k - 1].Y - 1, steps[k].Y);
            Assert.Equal(1, Math.Abs(steps[k].X - steps[k - 1].X) + Math.Abs(steps[k].Z - steps[k - 1].Z));
        }
        foreach (var s in steps.Skip(2))
        {
            Assert.Equal(0, w.Block(s.X, s.Y + 1, s.Z).Id);
            Assert.Equal(0, w.Block(s.X, s.Y + 2, s.Z).Id);
        }
        // A pawn at the bottom walks out by the stair.
        var bottom = Enumerable.Range(X0, width).SelectMany(x => Enumerable.Range(Z0, depthZ).Select(zz => new P3(x, BedrockTop + 1, zz)))
            .First(p => StoryWander.Standable(w, p.X, p.Y, p.Z));
        Assert.True(WalksOut(w, bottom, top), $"no way up from {bottom}");
        var st = c.State()["zones"]!.AsArray().Select(n => n!.AsObject()).Single(o => o["id"]!.GetValue<string>() == id);
        Assert.True(st["quarry"]!["done"]!.GetValue<bool>());
        Assert.Equal("quarry", st["mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(3, 3)]
    [InlineData(2, 2)]
    [InlineData(5, 3)]
    public void A_quarry_digs_everything_to_bedrock_and_leaves_a_walkable_spiral_stair(int width, int depthZ)
    {
        var (c, _, w) = Rig(width, depthZ);
        var id = Quarry(c, width, depthZ);
        var (digs, places, _) = Work(c, w);
        Assert.Equal(0, places);
        Assert.Equal((width * depthZ - 1) * (63 - BedrockTop), digs);
        AssertQuarried(c, w, id, width, depthZ);
    }

    [Fact]
    public void A_gravel_or_missing_step_gets_a_placed_block_before_the_layer_under_it()
    {
        var (c, _, w) = Rig(4, 4);
        var id = Quarry(c, 4, 4);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var gravel = StoryQuarry.Step(ring, 63, 2);
        var air = StoryQuarry.Step(ring, 63, 5);
        w.Set(gravel.X, gravel.Y, gravel.Z, 13);
        w.Set(air.X, air.Y, air.Z, 0); // a cave pocket where a step belongs
        var (_, places, jobs) = Work(c, w);
        Assert.Equal(2, places);
        var fix = jobs.First(j => j["kind"]!.GetValue<string>() == "place");
        Assert.Equal(gravel.ToString(), fix["at"]!.GetValue<string>());
        Assert.True(fix["clear"]!.GetValue<bool>());
        // Placed before anything under it was dug.
        int placedAt = jobs.IndexOf(fix);
        Assert.DoesNotContain(jobs.Take(placedAt), j => j["at"]!.GetValue<string>() == gravel.Add(0, -1, 0).ToString());
        Assert.Equal(4, w.Block(gravel.X, gravel.Y, gravel.Z).Id);
        Assert.Equal(4, w.Block(air.X, air.Y, air.Z).Id);
        AssertQuarried(c, w, id, 4, 4);
    }

    [Fact]
    public void A_cell_beside_lava_is_left_standing_as_a_seal_and_said()
    {
        var (c, _, w) = Rig(4, 4);
        var id = Quarry(c, 4, 4);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        // Lava just outside the east edge at y 40, beside a cell that isn't that layer's step.
        int k = 63 - 40;
        var step = StoryQuarry.Step(ring, 63, k);
        int zz = Enumerable.Range(Z0, 4).First(z => !(step.X == X0 + 3 && step.Z == z));
        w.Set(X0 + 4, 40, zz, 11);
        Work(c, w);
        Assert.Equal(1, w.Block(X0 + 3, 40, zz).Id); // the seal stays
        Assert.Single(c.Alerts, a => a.Text.Contains("left standing beside lava")); // said once, not once a look
        Assert.True(c.Zones.Single(x => x.Id == id).QuarryLayer > 60); // the quarry still went down to the bottom
    }

    private static JsonObject QuarryState(StoryColony c, string id) =>
        c.State()["zones"]!.AsArray().Select(n => n!.AsObject()).Single(o => o["id"]!.GetValue<string>() == id)["quarry"]!.AsObject();

    [Fact]
    public void Live_a_layer_is_never_left_half_dug_cells_that_failed_hold_it_until_they_are_dug()
    {
        // Live 11-04: "No pawn wants to mine in it any more after about the 7th depth layer." Cells that failed ("no path to within
        // 3", the pawn's path dropped onto a cell another pawn had dug) rested, the layer moved on without them, the floor went
        // uneven and pawns were stranded on the leftovers. Now the layer waits for them (and says so).
        var clock = new Clock();
        var (c, _, w) = Rig(9, 9, clock);
        var id = Quarry(c, 9, 9);
        var stuck = new P3(X0 + 4, 60, Z0 + 4); // layer 3
        Work(c, w, fails: at => at == stuck);
        // Everything else of layer 3 dug; the stuck cell rests: nothing deeper is offered, the status says why.
        Assert.Equal(1, w.Block(stuck.X, stuck.Y, stuck.Z).Id);
        Assert.Equal(3, c.Zones.Single().QuarryLayer);
        Assert.All(Enumerable.Range(X0, 9), x => Assert.Equal(1, w.Block(x, 59, Z0 + 1).Id)); // layer 4 untouched
        Assert.Contains("resting after failures", QuarryState(c, id)["status"]!.GetValue<string>());
        // Its rest over, it is dug, then the quarry goes on down.
        clock.Now += TimeSpan.FromMinutes(15);
        Work(c, w, others: 20, until: j => j["at"]!.GetValue<string>() is var a && P3.Parse(JsonNode.Parse("[" + a + "]"))!.Value.Y < 55);
        Assert.Equal(0, w.Block(stuck.X, stuck.Y, stuck.Z).Id);
    }

    [Fact]
    public void Live_a_cell_resting_after_failures_is_named_with_why_in_the_status_and_the_idle_audit()
    {
        // Live 11-04 story-1: "quarry z17860: 1 cell(s) on y 57 resting after failures" for half an hour, the layer held, and neither
        // the status nor the idle audit said which cell or why (181,57,366, under a stair step: "can't reach stone at 181,57,366").
        var clock = new Clock();
        var (c, _, w) = Rig(5, 5, clock);
        var id = Quarry(c, 5, 5);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var stuck = StoryQuarry.Step(ring, 63, 1).Add(0, -1, 0); // layer 2's cell under step 1
        Work(c, w, fails: at => at == stuck, failWhy: at => $"can't reach stone at {at}");
        Assert.Equal(1, w.Block(stuck.X, stuck.Y, stuck.Z).Id);
        Assert.Equal(2, c.Zones.Single().QuarryLayer);
        var status = QuarryState(c, id)["status"]!.GetValue<string>();
        Assert.Contains($"{stuck} resting after failures (can't reach stone)", status);
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        var why = c.IdleReasonOf(c.P());
        Assert.Equal($"waiting: quarry {id}: {stuck} resting after failures (can't reach stone)", why);
        // Dug after its rest: forgotten.
        clock.Now += TimeSpan.FromMinutes(15);
        Work(c, w, others: 20, until: j => j["at"]!.GetValue<string>() is var a && P3.Parse(JsonNode.Parse("[" + a + "]"))!.Value.Y < 59);
        Assert.Equal(0, w.Block(stuck.X, stuck.Y, stuck.Z).Id);
        Assert.DoesNotContain("resting", QuarryState(c, id)["status"]!.GetValue<string>());
    }

    [Fact]
    public void Live_a_broken_or_refilled_stair_is_repaired_before_any_more_digging_and_a_refilled_layer_is_found_again()
    {
        var clock = new Clock();
        var (c, _, w) = Rig(9, 9, clock);
        var id = Quarry(c, 9, 9);
        Work(c, w, until: j => j["at"]!.GetValue<string>().Contains(",55,")); // down to layer 8 (y 55)
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null); // that one is left for later
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var gone = StoryQuarry.Step(ring, 63, 5);
        w.Set(gone.X, gone.Y, gone.Z, 0); // a step lost (mined by mistake, a creeper…)
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Contains($"stair broken at y {gone.Y}", QuarryState(c, id)["status"]!.GetValue<string>());
        var next = c.Next();
        Assert.Equal("place", next["kind"]!.GetValue<string>());
        Assert.Equal(gone.ToString(), next["at"]!.GetValue<string>());
        w.Set(gone.X, gone.Y, gone.Z, 4);
        c.Report(c.P(), next["id"]!.GetValue<string>(), "done", null, null);
        // A cell three layers up refilled (a block placed back): found on the next full look, and dug before anything deeper.
        var refill = new P3(X0 + 4, 61, Z0 + 4);
        w.Set(refill.X, refill.Y, refill.Z, 1);
        clock.Now += TimeSpan.FromSeconds(31);
        var j = c.Next();
        Assert.Equal(refill.ToString(), j["at"]!.GetValue<string>());
    }

    [Fact]
    public void Ores_only_keeps_the_old_mine_zone_and_a_one_wide_quarry_is_refused()
    {
        var (c, _, w) = Rig(4, 4);
        w.Set(X0 + 1, 60, Z0 + 1, 16); // coal
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(X0, 58, Z0, X0 + 3, 62, Z0 + 3), grow: new JsonObject { ["mode"] = "ores" });
        Assert.True(err == null, err);
        var j = c.Next();
        Assert.Equal("dig", j["kind"]!.GetValue<string>());
        Assert.Equal(new P3(X0 + 1, 60, Z0 + 1).ToString(), j["at"]!.GetValue<string>());
        Assert.Equal("ores", c.State()["zones"]![0]!["mode"]!.GetValue<string>());
        Assert.Contains("at least 2×2", c.ZoneOp("add", "mine", Area.Of(30, 63, 0, 40, 63, 0)).Error);
        // The mode changes on an existing zone.
        var id = res!["id"]!.GetValue<string>();
        Assert.Null(c.ZoneOp("update", null, null, id, grow: new JsonObject { ["mode"] = "quarry" }).Error);
        Assert.True(StoryColony.IsQuarry(c.Zones.Single()));
    }
}
