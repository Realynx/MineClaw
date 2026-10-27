using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// One bot process against a persistent db: world + indexer + remembered world + scenery ledger, wired like BotHost.
/// Chunks arrive like the server sends them (full-column 0x33 regions), so restarts are realistic.
/// </summary>
internal sealed class MemorySession : IDisposable
{
    public WorldState World { get; } = new();
    public AgentDb Db { get; }
    public RagIndex Rag { get; }
    public ObservationStore Store { get; }
    public WorldIndexer Indexer { get; }
    public WorldMemory Memory { get; }
    public SqliteWorldMemoryStore WStore { get; }
    public List<UnknownStructure> Announced { get; } = new();
    public List<string> Lines { get; } = new();
    private readonly ManualClock _clock;

    public MemorySession(string dbPath, ManualClock clock, int dim = 0, RememberedWorldOptions? options = null)
    {
        _clock = clock;
        World.Dimension = dim;
        World.Spawn = new BlockPos(0, 64, 0);
        World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        Db = AgentDb.Open(dbPath);
        Rag = new RagIndex(Db, time: clock, backgroundEmbedding: false);
        Store = new ObservationStore(Rag, clock);
        var opts = new WorldIndexOptions { ClusterMinWriteSeconds = 0, ContainerMinWriteSeconds = 0, MaxColumnsPerPass = 64, ScanBudgetMs = 1000 };
        Indexer = new WorldIndexer(World, new ObservationStoreSink(Store), new ManualIndexFeed(), opts, "maid_chan", clock).Attach();
        WStore = new SqliteWorldMemoryStore(Db);
        Memory = new WorldMemory(World, WStore, options ?? new RememberedWorldOptions(), clock).Attach();
        WorldMemory.GateAnnouncements(Indexer, Memory, new SceneryLedger(WStore, clock));
        Indexer.UnknownStructureFound += Announced.Add;
        Memory.SinceLastTime += Lines.Add;
    }

    /// <summary>Sends every column in [-r, r) chunks around 0,0 as full 0x33 regions.</summary>
    public void Load(IReadOnlyDictionary<BlockPos, (int Id, int Meta)> blocks, int r = 2)
    {
        for (int cx = -r; cx < r; cx++)
        for (int cz = -r; cz < r; cz++)
        {
            var ids = new byte[16 * 128 * 16];
            var meta = new byte[16 * 128 * 16];
            foreach (var (p, b) in blocks)
            {
                if (p.X >> 4 != cx || p.Z >> 4 != cz) continue;
                int i = ((p.X & 15) * 16 + (p.Z & 15)) * 128 + p.Y;
                ids[i] = (byte)b.Id;
                meta[i] = (byte)b.Meta;
            }
            World.LoadColumn(cx, cz);
            World.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, ids, meta, null, null);
        }
    }

    /// <summary>Runs both passes for a while (the arrival window settles after a few quiet seconds).</summary>
    public void Run(int seconds = 12)
    {
        for (int i = 0; i < seconds; i++)
        {
            Indexer.PumpAll();
            Memory.Pump();
            _clock.Seconds(1);
        }
        Indexer.PumpAll();
        Memory.Pump();
    }

    public void Dispose()
    {
        Memory.Dispose();
        Indexer.Dispose();
        Rag.Dispose();
        Db.Dispose();
    }
}

public sealed class WorldMemoryTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-wm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 27, 4, 0, 0, TimeSpan.Zero));
    private string DbPath => Path.Combine(_dir, "test.db");

    public WorldMemoryTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void Fill(Dictionary<BlockPos, (int, int)> w, int x0, int y0, int z0, int x1, int y1, int z1, int id, int meta = 0)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            w[new BlockPos(x, y, z)] = (id, meta);
    }

    /// <summary>The island from the live session: dirt/grass, a plank-and-cobble hut, a little cobble tower.</summary>
    private static Dictionary<BlockPos, (int, int)> Island()
    {
        var w = new Dictionary<BlockPos, (int, int)>();
        Fill(w, -6, 60, -6, 6, 62, 6, Blocks.Dirt);
        Fill(w, -6, 63, -6, 6, 63, 6, Blocks.Grass);
        Fill(w, 2, 64, 2, 4, 66, 2, Blocks.Cobblestone);
        Fill(w, 2, 64, 4, 4, 66, 4, Blocks.Planks);
        w[new BlockPos(3, 65, 3)] = (Blocks.Glass, 0);
        w[new BlockPos(2, 65, 3)] = (Blocks.Glass, 0);
        w[new BlockPos(4, 65, 3)] = (Blocks.Torch, 0);
        return w;
    }

    private static void Doors(Dictionary<BlockPos, (int, int)> w, int x)
    {
        for (int z = -1; z <= 1; z++)
        {
            w[new BlockPos(x, 64, z)] = (Blocks.WoodenDoor, 0);
            w[new BlockPos(x, 65, z)] = (Blocks.WoodenDoor, 8);
        }
    }

    [Fact]
    public void Restart_with_an_identical_world_announces_nothing()
    {
        var world = Island();
        Doors(world, -5);
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            output.WriteLine("first join: " + string.Join(" | ", s.Announced.Select(a => a.Description)));
            Assert.NotEmpty(s.Announced); // a fresh db: genuinely new to the bot
            Assert.Empty(s.Lines);        // nothing to compare with yet
        }
        for (int restart = 0; restart < 2; restart++)
        {
            _clock.Advance(TimeSpan.FromMinutes(15));
            using var s = new MemorySession(DbPath, _clock);
            s.Load(world);
            s.Run();
            Assert.Empty(s.Announced);
            Assert.Empty(s.Lines);
            Assert.True(s.Memory.ColumnsUnchanged >= 16, $"unchanged {s.Memory.ColumnsUnchanged}");
            Assert.Equal(0, s.Memory.ColumnsChanged);
        }
    }

    [Fact]
    public void A_real_change_while_offline_gives_one_since_last_time_line()
    {
        var world = Island();
        using (var s = new MemorySession(DbPath, _clock)) { s.Load(world); s.Run(); }
        _clock.Advance(TimeSpan.FromHours(2));
        Doors(world, -10); // three doors on the west side while the bot was away
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            output.WriteLine(string.Join("\n", s.Lines));
            var line = Assert.Single(s.Lines);
            Assert.Equal("since last time: 3 wooden doors appeared on the west side", line);
            Assert.Equal(2, s.Memory.ColumnsChanged); // the row of doors straddles two columns
        }
    }

    [Fact]
    public void A_structure_already_asked_about_is_never_reasked_after_a_restart()
    {
        var world = Island();
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            Assert.Single(s.Announced);
            // Worst case for the old code: the observation rows are gone (keys can't be adopted) ...
            s.Db.Exec("DELETE FROM observations");
        }
        // ... and the hut's chunk changed a little elsewhere (so the baseline alone can't vouch for it).
        world[new BlockPos(-4, 64, -4)] = (Blocks.Cobblestone, 0);
        world.Remove(new BlockPos(4, 65, 3)); // torch taken down: a small edit to the hut itself
        _clock.Advance(TimeSpan.FromHours(1));
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            Assert.Empty(s.Announced);
            var scenery = s.WStore.Scenery(0);
            var hut = Assert.Single(scenery);
            Assert.NotNull(hut.AskedAt);
        }
    }

    [Fact]
    public void A_material_change_is_announced_again()
    {
        var world = Island();
        using (var s = new MemorySession(DbPath, _clock)) { s.Load(world); s.Run(); Assert.Single(s.Announced); }
        Fill(world, 2, 64, 5, 4, 70, 9, Blocks.Bookshelf); // the hut became a library tower
        _clock.Advance(TimeSpan.FromHours(1));
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            Assert.Single(s.Announced);
        }
    }

    [Fact]
    public void Stable_scenery_keys_survive_small_edits()
    {
        var store = new InMemoryWorldMemoryStore();
        var ledger = new SceneryLedger(store, _clock);
        var hut = new Dictionary<string, int> { ["planks"] = 40, ["cobblestone"] = 12, ["glass"] = 4, ["torch"] = 2 };
        Assert.Equal(SceneryLedger.Verdict.New, ledger.Observe(0, "unknown_structure", new(2, 64, 2), new(8, 68, 8), hut, false));
        var key = Assert.Single(store.Scenery(0)).Key;
        Assert.StartsWith("2,64,2~", key);

        // A door cut in, a torch moved, the corner shifted by one: same thing, same key, no announcement.
        var edited = new Dictionary<string, int> { ["planks"] = 38, ["cobblestone"] = 12, ["glass"] = 4, ["torch"] = 3, ["wooden_door"] = 2 };
        Assert.Equal(SceneryLedger.Verdict.Known, ledger.Observe(0, "unknown_structure", new(1, 64, 2), new(8, 68, 8), edited, false));
        Assert.Equal(key, Assert.Single(store.Scenery(0)).Key);
        Assert.Equal(SceneryLedger.Signature(hut), SceneryLedger.Signature(edited));

        // Somewhere else entirely: a different thing.
        Assert.Equal(SceneryLedger.Verdict.New, ledger.Observe(0, "unknown_structure", new(40, 64, 40), new(44, 66, 44), hut, false));
        // Same place, other dimension: a different thing too.
        Assert.Equal(SceneryLedger.Verdict.New, ledger.Observe(-1, "unknown_structure", new(2, 64, 2), new(8, 68, 8), hut, false));
    }

    private static Dictionary<BlockPos, (int, int)> Nether(bool glowstone = false)
    {
        var w = new Dictionary<BlockPos, (int, int)>();
        Fill(w, -10, 63, -2, 10, 63, 2, Blocks.Netherrack);
        Fill(w, -10, 50, 4, 10, 55, 8, Blocks.Lava); // lava sea on the left
        if (glowstone) Fill(w, 9, 64, 0, 9, 66, 0, Blocks.Glowstone);
        return w;
    }

    [Fact]
    public void Remembered_nether_survives_a_restart_and_a_dimension_switch_and_diffs_on_return()
    {
        var overworld = Island();
        // Visit the nether, then go home through a portal.
        using (var s = new MemorySession(DbPath, _clock, dim: -1))
        {
            s.Load(Nether());
            s.Run();
            s.World.Clear();
            s.World.Dimension = 0;
            s.Load(overworld);
            s.Run();
            Assert.Equal(0, s.World.RememberedCount); // live overworld everywhere; nether data is never mixed in
            Assert.False(s.World.IsStale(0, 0));
        }
        _clock.Advance(TimeSpan.FromHours(2));
        using (var s = new MemorySession(DbPath, _clock, dim: 0))
        {
            s.Load(overworld);
            s.Run();
            // Through the portal: nothing live yet, but the nether is remembered.
            s.World.Clear();
            s.World.Dimension = -1;
            s.Memory.Pump();
            Assert.True(s.World.RememberedCount >= 4, $"remembered {s.World.RememberedCount}");
            Assert.True(s.World.IsStale(0, 0));
            Assert.True(s.World.RememberedAt(0, 0) < _clock.GetUtcNow() - TimeSpan.FromHours(1)); // "(remembered 2h ago)"
            Assert.False(s.World.IsLoaded(0, 0));                         // movement/EdgeGuard still see "not loaded"
            Assert.Equal(0, s.World.GetBlock(0, 63, 0));                  // live reads stay live-only
            Assert.Equal(Blocks.Netherrack, s.World.PlanningView.GetBlock(0, 63, 0));

            // Planning uses the stale map ("route looks ~16 blocks"); movement can't (no live chunks: no path).
            var plan = MineClaw.World.Pathfinder.FindPath(s.World.PlanningView, new BlockPos(-8, 64, 0), new GoalBlock(8, 64, 0));
            Assert.True(plan.Success, plan.Reason);
            Assert.False(MineClaw.World.Pathfinder.FindPath(s.World, new BlockPos(-8, 64, 0), new GoalBlock(8, 64, 0)).Success);

            // Live chunks arrive: they replace the remembered ones, and what changed meanwhile is one line.
            s.Load(Nether(glowstone: true));
            s.Run();
            Assert.False(s.World.IsStale(0, 0));
            Assert.Equal(0, s.World.RememberedCount);
            var line = Assert.Single(s.Lines);
            Assert.Equal("since last time: 3 glowstone appeared on the east side", line);
        }
    }

    [Fact]
    public void Store_size_cap_evicts_least_recently_seen_far_columns_first()
    {
        using var db = AgentDb.Open(DbPath);
        var store = new SqliteWorldMemoryStore(db);
        var blob = new byte[1000];
        var t0 = _clock.GetUtcNow();
        var rows = new List<ChunkRow>();
        for (int i = 0; i < 10; i++) rows.Add(new ChunkRow(0, i * 4, 0, i, "", blob, t0 + TimeSpan.FromMinutes(i)));
        rows.Add(new ChunkRow(-1, 0, 0, 99, "", blob, t0 - TimeSpan.FromDays(1))); // oldest, but another dimension's spawn
        rows.Add(new ChunkRow(0, 0, 1, 98, "", blob, t0 - TimeSpan.FromDays(2))); // oldest of all, but next to the bot
        store.PutChunks(rows);
        long per = store.TotalBytes() / rows.Count;
        int gone = store.Evict(per * 6, keepDim: 0, keepX: 0, keepZ: 0, keepRadius: 1);
        Assert.Equal(6, gone);
        Assert.True(store.TotalBytes() <= per * 6);
        Assert.NotNull(store.GetChunk(0, 0, 1));      // near the bot: kept despite being the oldest
        Assert.Null(store.GetChunk(-1, 0, 0));        // old and far (other dimension): evicted first
        Assert.NotNull(store.GetChunk(0, 36, 0));     // most recently seen: kept
        Assert.Null(store.GetChunk(0, 4, 0));
    }

    [Fact]
    public void Snapshots_round_trip_and_stay_small()
    {
        var w = new WorldState();
        var ids = new byte[16 * 128 * 16];
        var meta = new byte[16 * 128 * 16];
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) { ids[(x * 16 + z) * 128 + 63] = Blocks.Grass; ids[(x * 16 + z) * 128 + 64] = (byte)(x == z ? Blocks.WoodenDoor : 0); meta[(x * 16 + z) * 128 + 64] = 8; }
        w.ApplyChunkRegion(0, 0, 0, 16, 128, 16, ids, meta, null, null);
        var snap = ChunkSnapshot.Capture(w.GetColumn(0, 0)!);
        var data = snap.Encode();
        Assert.True(data.Length < 2048, $"{data.Length} bytes");
        var back = ChunkSnapshot.Decode(data);
        Assert.Equal(snap.Hash, back.Hash);
        Assert.Equal(Blocks.WoodenDoor, back.Ids[ChunkColumn.Index(3, 64, 3)]);
        Assert.Equal(8, back.Meta[ChunkColumn.Index(3, 64, 3)]);
        Assert.Contains("2=256", snap.Summary());
    }
}

public sealed class ClusterIndexRegressionTests
{
    private static string Min(HashSet<BlockPos> c) { var m = c.MinBy(p => (p.X, p.Y, p.Z)); return $"{m.X},{m.Y},{m.Z}"; }

    /// <summary>
    /// Live 01:09:39: a tree split while being chopped; the small part's fresh key (its min corner) equalled the old key the
    /// big part kept, so its cells ended up owned by a key whose cluster was later removed → KeyNotFound every pass after.
    /// </summary>
    [Fact]
    public void A_split_never_hands_a_fresh_key_that_another_part_keeps()
    {
        for (int order = 0; order < 2; order++)
        {
            var layer = new ClusterIndex(1);
            var cells = Enumerable.Range(0, 6).Select(x => new BlockPos(x, 65, 3)).ToList();
            if (order == 1) cells.Reverse();
            foreach (var c in cells) layer.Add(c);
            var (first, _) = layer.Recluster(Min);
            Assert.Equal("0,65,3", Assert.Single(first).Key);

            layer.Remove(new BlockPos(1, 65, 3)); // split: {0} and {2..5}; the big part keeps "0,65,3"
            var (split, _) = layer.Recluster(Min);
            Assert.Equal(2, split.Count);
            Assert.Equal(2, split.Select(c => c.Key).Distinct().Count());
            Assert.Equal("0,65,3", split.Single(c => c.Cells.Count == 4).Key);

            for (int x = 2; x <= 5; x++) layer.Remove(new BlockPos(x, 65, 3)); // the big part is chopped away
            layer.Recluster(Min);
            layer.Add(new BlockPos(0, 66, 3)); // a sapling/leaf next to the lone log
            var (after, _) = layer.Recluster(Min); // threw KeyNotFoundException before the fix
            var c0 = Assert.Single(after);
            Assert.Contains(new BlockPos(0, 65, 3), c0.Cells);
            Assert.Equal(c0.Key, layer.OwnerOf(new BlockPos(0, 65, 3)));
        }
    }

    [Fact]
    public void Repair_rebuilds_a_layer_from_its_cells()
    {
        var layer = new ClusterIndex(1);
        for (int x = 0; x < 3; x++) layer.Add(new BlockPos(x, 64, 0));
        layer.Add(new BlockPos(10, 64, 0));
        layer.Recluster(Min);
        var dropped = layer.Repair();
        Assert.Equal(2, dropped.Count);
        var (rebuilt, removed) = layer.Recluster(Min);
        Assert.Empty(removed);
        Assert.Equal(2, rebuilt.Count);
        Assert.All(rebuilt, c => Assert.True(c.IsNew));
    }
}

public sealed class ChunkLoadActivityTests
{
    /// <summary>A chunk (re)load full of blocks next to a player is the world arriving, not that player building.</summary>
    [Fact]
    public void A_chunk_load_burst_is_never_building_activity()
    {
        using var a = new ActivityFixture();
        a.Player("PoofimaFox", 3.5, 64, 3.5);
        a.Step();
        var ids = new byte[16 * 128 * 16];
        for (int x = 1; x <= 6; x++) for (int z = 1; z <= 6; z++) for (int y = 64; y <= 68; y++) ids[(x * 16 + z) * 128 + y] = Blocks.Planks;
        a.World.ApplyChunkRegion(0, 0, 0, 16, 128, 16, ids, null, null, null);
        for (int i = 0; i < 40; i++) a.Step(1);
        Assert.DoesNotContain(a.Recorded, e => e.Kind is ActivityKinds.Placed or ActivityKinds.Broke); // "arrived" (them walking up) is fine
        Assert.DoesNotContain(a.Posted, e => e.Text.Contains("built", StringComparison.OrdinalIgnoreCase) || e.Text.Contains("placed", StringComparison.OrdinalIgnoreCase));
    }
}
