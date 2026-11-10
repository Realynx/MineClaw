using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A quarry that hands out nothing must say why, and must not spin (docs/web/story-mode.md "Quarries"). A miner with no free slot digs
/// nothing: its script's batch ends "0/12 dug; inventory full" as a success, and the director handed the same batch out again at once,
/// a full pick and a fresh quarry scan per round, for as long as storage stayed full. Now it gets no dig: it stores first when it can,
/// waits when it can't, and the wait is named (the idle audit, and the storage-full alert the farm already had).
/// </summary>
public sealed class StoryQuarryStallTests
{
    private const int X0 = 10, Z0 = 10, Size = 6, BedrockTop = 2;
    private static readonly P3 Chest = new(0, 64, 9);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A 6×6 quarry on flat stone over bedrock, one miner at home, and (unless <paramref name="storage"/> is off) one storage
    /// chest (a real chest block: the tick checks it).</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(bool storage = true)
    {
        var clock = new Clock();
        var (c, h, world) = StoryRig.Make(time: clock);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        for (int x = X0 - 6; x < X0 + Size + 6; x++)
            for (int z = Z0 - 6; z < Z0 + Size + 6; z++)
                for (int y = 0; y <= BedrockTop; y++)
                    world.Set(x, y, z, 7);
        if (storage)
        {
            world.Set(Chest.X, Chest.Y, Chest.Z, 54);
            Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        }
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(X0, 63, Z0, X0 + Size - 1, 63, Z0 + Size - 1)).Error);
        return (c, h, world, clock);
    }

    /// <summary>A miner whose every slot is taken (cobblestone, and its pickaxe): its script's dig batch digs nothing.</summary>
    private static PawnLive Full() => FakeStoryHost.Live(0, 64, 0, inv: new Stack(4, 0, 35 * 64)) with { FreeSlots = 0 };

    /// <summary>Every slot of the colony's only chest holds a full stack.</summary>
    private static void FillChest(StoryColony c) => c.AddContents(c.StorageAt(Chest)!, new Stack(1, 0, 27 * 64));

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static string Zone(StoryColony c) => c.Zones.Single().Id;
    /// <summary>The chest a storage trip takes its load to.</summary>
    private static string StashTo(JsonObject j) => j["stash"]![0]!["to"]!.GetValue<string>();

    /// <summary>Ends what the pawn holds now (an idle hold, a wander leg), so its next ask is a new pick.</summary>
    private static void EndHold(StoryColony c)
    {
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
    }

    /// <summary>The idle audit's word for the pawn, two seconds into the job it holds now.</summary>
    private static string? IdleReason(StoryColony c, Clock clock)
    {
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        return c.IdleReasonOf(c.P());
    }

    [Fact]
    public void A_miner_with_full_pockets_and_nowhere_to_store_gets_no_dig_job()
    {
        var (c, h, _, clock) = Rig();
        FillChest(c);
        h.Lives[c.P()] = Full();
        for (int round = 0; round < 20; round++)
        {
            var j = c.Next();
            Assert.NotEqual("dig", Kind(j));
            c.Report(c.P(), Id(j), "done", null, null);
            clock.Now += TimeSpan.FromSeconds(3);
        }
        c.Next();
        Assert.Equal($"waiting: quarry {Zone(c)}: pockets full, no storage has room", IdleReason(c, clock));
        // Said once, however often it asked (the farm's alert, at most every 10 minutes), with what has nowhere to go and for whom.
        var said = Assert.Single(c.Alerts, a => a.Text.StartsWith("Storage is full", StringComparison.Ordinal));
        Assert.Contains("no chest has room for Cobblestone (1 of 1 storage chests full)", said.Text);
        Assert.Contains($"no digging for it (quarry {Zone(c)}) until one has room", said.Text);
        // Room again: it stores, then digs. Nothing had to be retried by the owner.
        c.TakeContents(c.StorageAt(Chest)!, 1, 0, 27 * 64);
        EndHold(c);
        var trip = c.Next();
        Assert.Equal("go", Kind(trip));
        Assert.Equal(Chest.ToString(), StashTo(trip));
        h.Lives[c.P()] = FakeStoryHost.Live(Chest.X, 64, Chest.Z - 1);
        c.Report(c.P(), Id(trip), "done", null, null);
        Assert.Equal("dig", Kind(c.Next()));
    }

    [Fact]
    public void With_room_in_storage_a_full_miner_delivers_first()
    {
        var (c, h, _, _) = Rig();
        h.Lives[c.P()] = Full();
        var trip = c.Next();
        Assert.Equal("go", Kind(trip));
        Assert.Equal(Chest.ToString(), StashTo(trip));
        h.Lives[c.P()] = FakeStoryHost.Live(Chest.X, 64, Chest.Z - 1);
        c.Report(c.P(), Id(trip), "done", null, null);
        Assert.Equal("dig", Kind(c.Next()));
        Assert.DoesNotContain(c.Alerts, a => a.Text.StartsWith("Storage is full", StringComparison.Ordinal));
    }

    [Fact]
    public void After_a_failed_delivery_a_full_miner_waits_instead_of_digging()
    {
        var (c, h, _, clock) = Rig();
        h.Lives[c.P()] = Full();
        var trip = c.Next();
        Assert.Equal("go", Kind(trip));
        c.Report(c.P(), Id(trip), "failed", "interrupted: fighting a zombie", null);
        // The delivery rests 30 s. Until then: no dig (its batch would dig nothing), and the wait says storing comes first.
        var held = c.Next();
        Assert.NotEqual("dig", Kind(held));
        Assert.Equal($"waiting: quarry {Zone(c)}: pockets full, storing its load first", IdleReason(c, clock));
        Assert.DoesNotContain(c.Alerts, a => a.Text.StartsWith("Storage is full", StringComparison.Ordinal)); // storage has room
        clock.Now += TimeSpan.FromSeconds(31);
        EndHold(c);
        var again = c.Next();
        Assert.Equal("go", Kind(again));
        Assert.Equal(Chest.ToString(), StashTo(again));
    }

    [Fact]
    public void With_no_storage_at_all_the_standing_no_storage_alert_says_it()
    {
        // Nothing to call full: the wait is named the same way, and the colony's "No storage for: …" alert (once) covers the rest.
        var (c, h, _, clock) = Rig(storage: false);
        h.Lives[c.P()] = Full();
        for (int round = 0; round < 5; round++)
        {
            var j = c.Next();
            Assert.NotEqual("dig", Kind(j));
            c.Report(c.P(), Id(j), "done", null, null);
            clock.Now += TimeSpan.FromSeconds(3);
        }
        c.Next();
        Assert.Equal($"waiting: quarry {Zone(c)}: pockets full, no storage has room", IdleReason(c, clock));
        Assert.Single(c.Alerts, a => a.Text.StartsWith("No storage for: Cobblestone", StringComparison.Ordinal));
        Assert.DoesNotContain(c.Alerts, a => a.Text.StartsWith("Storage is full", StringComparison.Ordinal));
    }

    [Fact]
    public void A_miner_whose_slots_hold_only_what_it_keeps_is_told_so()
    {
        // Tools and its pillar blocks are never stored: there is nothing to unload, and no storage is to blame.
        var (c, h, _, clock) = Rig();
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(4, 0, 16)) with { FreeSlots = 0 };
        Assert.NotEqual("dig", Kind(c.Next()));
        Assert.Equal($"waiting: quarry {Zone(c)}: pockets full of what it keeps (tools, food, its blocks)", IdleReason(c, clock));
        Assert.DoesNotContain(c.Alerts, a => a.Text.StartsWith("Storage is full", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ rests and silent holds (WP5)

    private static P3 At(JsonObject j) => P3.Parse(JsonNode.Parse("[" + j["at"]!.GetValue<string>() + "]"))!.Value;

    private static string Status(StoryColony c) => c.State()["zones"]![0]!["quarry"]!["status"]!.GetValue<string>();

    /// <summary>The cell and the cells its batch reserved.</summary>
    private static string[] Cells(JsonObject j) =>
        [j["at"]!.GetValue<string>(), .. j["dig_cells"]?.AsArray().Select(n => n!.GetValue<string>()) ?? []];

    /// <summary>Top layer (y 63) dug but for its first step and <paramref name="left"/>.</summary>
    private static void OnlyLeft(StoryColony c, FakeStoryWorld w, P3 left)
    {
        var step = StoryQuarry.Step(StoryQuarry.Ring(c.Zones.Single().Cells)!, 63, 0);
        for (int x = X0; x < X0 + Size; x++)
            for (int z = Z0; z < Z0 + Size; z++)
                if (new P3(x, 63, z) != step && new P3(x, 63, z) != left) w.Set(x, 63, z, 0);
    }

    /// <summary>The pawn does every dig and place the quarry hands out, until it has none; <paramref name="refuse"/>d cells fail with
    /// <paramref name="why"/>.</summary>
    private static void Work(StoryColony c, FakeStoryWorld w, Func<P3, bool>? refuse = null, string why = "")
    {
        for (int i = 0; i < 6000; i++)
        {
            var j = c.Next();
            if (Kind(j) is not ("dig" or "place")) return;
            var at = At(j);
            if (refuse?.Invoke(at) == true) { c.Report(c.P(), Id(j), "failed", why.Replace("{at}", at.ToString()), null); continue; }
            w.Set(at.X, at.Y, at.Z, Kind(j) == "dig" ? 0 : 4);
            c.Report(c.P(), Id(j), "done", null, null);
        }
    }

    /// <summary>A dug-out 10×10 pit (StoryQuarryBatchTests' rig): only its steps and three cobblestone cells stand.</summary>
    private static readonly P3 PitTop = new(14, 60, 14), PitLower = new(14, 59, 14), PitBeside = new(16, 60, 14);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) PitRig(int pawns = 1, TimeProvider? time = null)
    {
        var (c, h, w) = StoryRig.Make(pawns, time: time);
        c.DoneGrace = TimeSpan.Zero;
        c.SetHaulOptions(null, null, null, pillarStack: 0);
        foreach (var p in c.Pawns.Values)
        {
            foreach (var type in WorkTypes.All) p.Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
            h.Lives[p.Name] = FakeStoryHost.Live(14, 64, 14);
        }
        c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 19, 63, 19));
        var zone = c.Zones.Single();
        zone.QuarryTop = 63;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int x = 10; x <= 19; x++) for (int z = 10; z <= 19; z++) for (int y = 45; y <= 63; y++) w.Set(x, y, z, 0);
        for (int k = 0; k <= 18; k++)
        {
            var step = StoryQuarry.Step(ring, 63, k);
            w.Set(step.X, step.Y, step.Z, 1);
        }
        foreach (var at in new[] { PitTop, PitLower, PitBeside }) w.Set(at.X, at.Y, at.Z, 4);
        return (c, h, w);
    }

    [Theory]
    [InlineData("won't dig stone at {at}: someone is standing on it")]
    [InlineData("busy: fighting a zombie")]
    [InlineData("interrupted: fighting a zombie")]
    [InlineData("critical hp 4/20: stopped dig")]
    public void A_transient_refusal_rests_a_quarry_cell_15_seconds_and_never_ten_minutes(string why)
    {
        // A fight, a pawn on the cell, low health: "not now". Counted as failures, the cell rested 30 s, 1 min, then 10 min, and the
        // layer waited for it all that time.
        var (c, _, w, clock) = Rig();
        var cell = new P3(X0 + 2, 63, Z0 + 2);
        OnlyLeft(c, w, cell);
        for (int failure = 1; failure <= 4; failure++)
        {
            var job = c.Next();
            Assert.Equal("dig", Kind(job));
            Assert.Equal(cell, At(job));
            c.Report(c.P(), Id(job), "failed", why.Replace("{at}", cell.ToString()), null);
            Assert.NotEqual(cell.ToString(), c.Next()["at"]?.GetValue<string>()); // it rests a moment
            EndHold(c);
            clock.Now += TimeSpan.FromSeconds(16);
        }
        var again = c.Next(); // after the 4th too: 15 s, never 10 minutes
        Assert.Equal("dig", Kind(again));
        Assert.Equal(cell, At(again));
    }

    [Fact]
    public void A_dug_cell_reported_failed_is_credited_as_done()
    {
        var (c, _, w) = PitRig();
        var job = c.Next();
        Assert.Contains(PitBeside.ToString(), Cells(job));
        // The block is gone (the server's answer came after the client gave up on it), yet the pawn says it failed.
        w.Set(PitBeside.X, PitBeside.Y, PitBeside.Z, 0);
        c.Report(c.P(), Id(job), "progress", $"digcell {PitBeside} failed: stone at {PitBeside} didn't break", null);
        c.Report(c.P(), Id(job), "done", "batch: 0/3 dug", null);
        // Credited as dug: no rest. Filled in again, it is offered at once.
        w.Set(PitBeside.X, PitBeside.Y, PitBeside.Z, 4);
        Assert.Contains(PitBeside.ToString(), Cells(c.Next()));
        Assert.DoesNotContain("resting", Status(c));
    }

    [Fact]
    public void A_protected_cell_is_left_standing_said_once_and_the_layer_moves_on()
    {
        var (c, _, w, _) = Rig();
        var a = new P3(X0 + 2, 60, Z0 + 2);
        var b = new P3(X0 + 3, 60, Z0 + 2);
        Work(c, w, at => at == a || at == b, "won't dig stone at {at}: protected (someone else's block)");
        Assert.Equal(1, w.Block(a.X, a.Y, a.Z).Id);
        Assert.Equal(1, w.Block(b.X, b.Y, b.Z).Id);
        Assert.True(c.Zones.Single().QuarryLayer > 3); // not held at y 60 (layer 3)
        Assert.True(c.State()["zones"]![0]!["quarry"]!["done"]!.GetValue<bool>());
        var said = Assert.Single(c.Alerts, x => x.Text.Contains("no pawn may break them"));
        Assert.Contains(a.ToString(), said.Text);
        Assert.Contains("2 protected", Status(c));
    }

    [Fact]
    public void Cells_waiting_for_a_tool_are_named_in_the_quarrys_status()
    {
        var (c, _, w, clock) = Rig();
        var ore = new P3(X0 + 2, 63, Z0 + 2);
        OnlyLeft(c, w, ore);
        w.Set(ore.X, ore.Y, ore.Z, 56); // diamond ore: an iron pickaxe; the pawn has a stone one
        Assert.NotEqual("dig", Kind(c.Next()));
        Assert.Contains("1 cell(s) waiting for a tool (needs an iron pickaxe", Status(c));
        Assert.StartsWith($"waiting: quarry {Zone(c)}: 1 cell(s) waiting for a tool", IdleReason(c, clock));
    }

    [Fact]
    public void A_pawns_own_leftover_pillar_block_beside_it_is_offered_to_that_pawn()
    {
        // In a small pit its owner never got 8 blocks away from it, so it never counted as left behind: offered to nobody, and the
        // layer said only "1 cell(s) left". Another pawn still waits for it, and says so.
        var clock = new Clock();
        var (c, h, w) = PitRig(pawns: 2, time: clock);
        w.Set(PitBeside.X, PitBeside.Y, PitBeside.Z, 0);
        c.Temporary(c.P(), PitTop.ToString(), true, "reach pillar");
        h.Lives[c.P()] = FakeStoryHost.Live(15, 61, 14); // beside it, not on it
        h.Lives[c.P(1)] = FakeStoryHost.Live(0, 64, 0);
        clock.Now += TimeSpan.FromMinutes(2);
        c.Tick();
        Assert.NotEqual("dig", Kind(c.Next(1)));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Equal($"waiting: quarry {Zone(c)}: waiting for {c.Pawns[c.P()].Display}'s temporary block at {PitTop}", c.IdleReasonOf(c.P(1)));
        var job = c.Next();
        Assert.Equal("dig", Kind(job));
        Assert.Contains(PitTop.ToString(), Cells(job));
    }

    [Fact]
    public void A_cancelled_temporary_block_is_kept_and_does_not_hold_the_layer()
    {
        var (c, _, w, _) = Rig();
        var kept = new P3(X0 + 2, 60, Z0 + 2);
        c.Meta.TempBlocks = [new TempBlockState { At = kept, Id = 1, Pawn = c.P(), Kind = "reach pillar", Cancelled = true }];
        Work(c, w);
        Assert.Equal(1, w.Block(kept.X, kept.Y, kept.Z).Id); // the owner's to keep
        Assert.True(c.State()["zones"]![0]!["quarry"]!["done"]!.GetValue<bool>());
        Assert.Single(c.Alerts, a => a.Text.Contains("a temporary block whose removal was cancelled"));
    }

    [Fact]
    public void A_resting_seal_is_shown_with_its_failure()
    {
        // A seal resting after failures doesn't hold the quarry, and was not said at all.
        var (c, h, w, _) = Rig();
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(4, 0, 64)); // cobblestone to seal with
        var water = new P3(X0 + 2, 63, Z0 + 2);
        w.Set(water.X, water.Y, water.Z, 9);
        var seal = c.Next();
        Assert.Equal("place", Kind(seal));
        Assert.Equal(water, At(seal));
        c.Report(c.P(), Id(seal), "failed", $"no visible support face for cobblestone at {water}", null);
        Assert.Equal("dig", Kind(c.Next()));
        Assert.Contains($"sealing water at {water}: resting after failures (no visible support face for cobblestone)", Status(c));
    }

    [Fact]
    public void A_full_miner_still_places_the_stairs_missing_step()
    {
        // Only digging needs a free slot: a step (or a seal) is placed from what it carries.
        var (c, h, w, _) = Rig();
        FillChest(c);
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        // Step 1, under the standing first step: a missing first step on flat ground is no part of the stair (StoryQuarryTerrainTests).
        var step = StoryQuarry.Step(ring, 63, 1);
        w.Set(step.X, step.Y, step.Z, 0);
        h.Lives[c.P()] = Full();
        var job = c.Next();
        Assert.Equal("place", Kind(job));
        Assert.Equal(step.ToString(), job["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_full_miner_does_not_fetch_a_step_block_into_full_pockets()
    {
        // Its step block is in a chest: the fetch took nothing ("inventory full"), failed and rested the step for every pawn.
        var (c, h, w, clock) = Rig();
        FillChest(c); // stone, a step block; and no room for what the pawn carries
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var step = StoryQuarry.Step(ring, 63, 1);
        w.Set(step.X, step.Y, step.Z, 0);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(15, 0, 35 * 64)) with { FreeSlots = 0 }; // iron ore
        for (int round = 0; round < 5; round++)
        {
            var j = c.Next();
            Assert.DoesNotContain(Kind(j), new[] { "dig", "place" });
            c.Report(c.P(), Id(j), "done", null, null);
            clock.Now += TimeSpan.FromSeconds(3);
        }
        c.Next();
        Assert.StartsWith($"waiting: quarry {Zone(c)}: a block for y 62: pockets full, no storage has room", IdleReason(c, clock));
        var said = Assert.Single(c.Alerts, a => a.Text.StartsWith("Storage is full", StringComparison.Ordinal));
        Assert.Contains($"no fetching blocks for it (quarry {Zone(c)}: a block for y 62) until one has room", said.Text);
        // Room in its pockets: the step's block is fetched and placed, nothing rested meanwhile.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0);
        EndHold(c);
        var job = c.Next();
        Assert.Equal("place", Kind(job));
        Assert.Equal(step, At(job));
        Assert.NotNull(job["fetch"]);
    }

    [Fact]
    public void Unloaded_columns_are_never_dug_or_done()
    {
        // A column out of view reads as air, so it read as dug: with no body near, every layer was empty and the quarry "done: down to
        // bedrock", and a done quarry sends nobody to load it.
        var (c, h, w, clock) = Rig();
        var zone = c.Zones.Single();
        var step = StoryQuarry.Step(StoryQuarry.Ring(zone.Cells)!, 63, 0);
        for (int x = X0; x < X0 + Size; x++)
            for (int z = Z0; z < Z0 + Size; z++)
                if (new P3(x, 63, z) != step) w.Set(x, 63, z, 0);
        Status(c);
        Assert.Equal(63, zone.QuarryTop);
        Assert.Equal(1, zone.QuarryLayer);
        foreach (var (x, z) in zone.Cells.Sorted()) w.Unloaded.Add((x, z));
        clock.Now += TimeSpan.FromSeconds(31); // a full look from the top
        var quarry = c.State()["zones"]![0]!["quarry"]!;
        Assert.False(quarry["done"]!.GetValue<bool>());
        Assert.Equal(1, zone.QuarryLayer);
        Assert.EndsWith(": 36 column(s) not in view", quarry["status"]!.GetValue<string>());
        // A pawn walks to the top of the stair to load them (on the surface, not down the stair); nothing is dug or placed blind.
        var go = c.Next();
        Assert.Equal("go", Kind(go));
        Assert.Equal(step.Add(0, 1, 0), At(go));
        Assert.Null(go["quarry_approach"]);
        // Standing there with the columns still out of view, it isn't sent again (no loop): it waits, and says why.
        h.Lives[c.P()] = FakeStoryHost.Live(step.X, 64, step.Z);
        c.Report(c.P(), Id(go), "done", null, null);
        var next = c.Next();
        Assert.DoesNotContain(Kind(next), new[] { "go", "dig", "place" });
        Assert.Equal(1, zone.QuarryLayer);
        Assert.False(c.State()["zones"]![0]!["quarry"]!["done"]!.GetValue<bool>());
        // In view again, the digging goes on from the layer it had reached.
        w.Unloaded.Clear();
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(6);
        var dig = c.Next();
        Assert.Equal("dig", Kind(dig));
        Assert.Equal(62, At(dig).Y);
    }

    [Fact]
    public void A_finished_quarry_out_of_view_stays_done_and_sends_nobody()
    {
        // A column out of view has no bedrock to read: its bottom read as 1, under this quarry's (bedrock to y 2), so once its pawns
        // left a finished quarry had a layer more, held by the columns out of view, and a miner was sent back to look, over and over.
        var (c, h, w, clock) = Rig();
        var zone = c.Zones.Single();
        var ring = StoryQuarry.Ring(zone.Cells)!;
        var steps = Enumerable.Range(0, 61).Select(k => StoryQuarry.Step(ring, 63, k)).ToHashSet();
        for (int x = X0; x < X0 + Size; x++)
            for (int z = Z0; z < Z0 + Size; z++)
                for (int y = BedrockTop + 1; y <= 63; y++)
                    if (!steps.Contains(new P3(x, y, z))) w.Set(x, y, z, 0);
        Assert.StartsWith("done: down to bedrock", Status(c));
        Assert.Equal(BedrockTop + 1, zone.QuarryBottom); // kept with the zone, so a restart knows it too
        int layer = zone.QuarryLayer;
        w.Unloaded.Add((X0 + 2, Z0 + 2)); // one column out of view, then all of them
        for (int round = 0; round < 2; round++)
        {
            clock.Now += TimeSpan.FromSeconds(31); // a full look from the top
            var quarry = c.State()["zones"]![0]!["quarry"]!;
            Assert.True(quarry["done"]!.GetValue<bool>());
            Assert.StartsWith("done: down to bedrock", quarry["status"]!.GetValue<string>());
            Assert.Equal(layer, zone.QuarryLayer);
            var j = c.Next();
            Assert.DoesNotContain(Kind(j), new[] { "dig", "place" });
            Assert.DoesNotContain("looking over quarry", j.ToJsonString());
            c.Report(c.P(), Id(j), "done", null, null);
            foreach (var (x, z) in zone.Cells.Sorted()) w.Unloaded.Add((x, z));
        }
        Assert.Single(c.Alerts, a => a.Text.Contains("is done"));
    }

    [Fact]
    public void A_quarry_with_nothing_in_view_says_so_in_the_quarries_panel()
    {
        var (c, _, w, clock) = Rig();
        var zone = c.Zones.Single();
        foreach (var (x, z) in zone.Cells.Sorted()) w.Unloaded.Add((x, z));
        zone.QuarryTop = zone.QuarryBottom = null; // painted out of view: nothing of it seen
        clock.Now += TimeSpan.FromSeconds(6);
        var quarry = c.State()["zones"]![0]!["quarry"]!;
        Assert.Null(zone.QuarryTop);
        Assert.False(quarry["done"]!.GetValue<bool>());
        Assert.Equal("36 column(s) not in view", quarry["status"]!.GetValue<string>());
        Assert.Empty(quarry["stair"]!.AsArray());
    }
}
