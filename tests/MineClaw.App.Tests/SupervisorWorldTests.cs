using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Bot.WorldIndex;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>A fake merged world: two known columns at (0,0) and (1,0), one bot, one claim.</summary>
internal sealed class FakeSupervisorWorld : ISupervisorWorld
{
    public int IndexCalls, ColumnCalls;
    public IReadOnlyList<WorldChestState> OpenChests = [];
    public IReadOnlyList<WorldChestState> Chests() => OpenChests;
    public readonly ChunkSnapshot Snap;

    public FakeSupervisorWorld()
    {
        var ids = new byte[ChunkSnapshot.Volume];
        ids[ChunkColumn.Index(3, 64, 4)] = 4;
        Snap = new ChunkSnapshot(ids, new byte[ChunkSnapshot.Volume]);
    }

    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max)
    {
        Interlocked.Increment(ref IndexCalls);
        return [new(0, 0, Snap.Hash, 1_700_000_000_000, "maid_chan", true), new(1, 0, 42, 1_600_000_000_000, "maid_chan_67", false)];
    }

    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols)
    {
        Interlocked.Increment(ref ColumnCalls);
        return cols.Select(c => c == (0, 0) ? new ColumnPayloadEntry(0, 0, Snap.Hash, 1_700_000_000_000, "maid_chan", true, Snap.Encode())
            : new ColumnPayloadEntry(c.Cx, c.Cz, 0, 0, null, false, [])).ToList();
    }

    public string? Version => "b1.8.1";
    public long? WorldTime(int dim) => dim == 0 ? 6000 : null;
    public (float Rain, float Thunder)? WorldWeather(int dim) => dim == 0 ? (1f, 0.5f) : null;

    public IReadOnlyList<WorldBot> Bots() =>
        [new("maid_chan", "main", true, 0, 1.5, 65, 1.5, 90, 20, 0, 20, 18, "idle", null, "cobblestone x12", ["block 4,64,6"], true, 0)];

    public IReadOnlyList<WorldEntity> Entities(int? dim) => [new("player", "PoofImaFox", 0, 3, 65, 3)];

    public WorldOverlays Overlays(int dim) => new(DateTimeOffset.UnixEpoch, dim,
        [new WorldMark("block", "block 4,64,6", [4, 64, 6], [4, 64, 6], "maid_chan")], [new WorldMark("waypoint", "home", [0, 65, 0], [0, 65, 0])],
        [new WorldMark("chest", "chest", [2, 65, 2], [2, 65, 2])], [new WorldPlan(7, "tower", "building", 0, [[5, 65, 5, 4, 0, 0]])], [], []);
}

public class SupervisorWorldTests
{
    private static SupervisorRequest Get(string path, string? query = null, string? token = "s3cret")
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query != null)
            foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = kv.IndexOf('=');
                q[i < 0 ? kv : kv[..i]] = i < 0 ? "" : Uri.UnescapeDataString(kv[(i + 1)..]);
            }
        return new SupervisorRequest("GET", path, q, token);
    }

    private static (SupervisorServer Server, FakeSupervisorWorld World) Server(WorldRouteLimits? limits = null, TimeProvider? time = null)
    {
        var w = new FakeSupervisorWorld();
        var s = new SupervisorServer(new SupervisorOptions { Token = "s3cret" }, new FakeSupervisorData(), time: time)
        {
            World = new SupervisorWorldRoutes(w, limits, time),
        };
        return (s, w);
    }

    [Fact]
    public async Task World_routes_need_the_token()
    {
        var (s, w) = Server();
        await using var _ = s;
        foreach (var p in new[] { "/api/world/index", "/api/world/chunks", "/api/world/entities", "/api/world/overlays" })
        {
            Assert.Equal(401, (await s.HandleAsync(Get(p, "x=0&z=0&cols=0,0", token: null))).Status);
            Assert.Equal(401, (await s.HandleAsync(Get(p, "x=0&z=0&cols=0,0", token: "wrong"))).Status);
        }
        Assert.Equal(0, w.IndexCalls + w.ColumnCalls);
    }

    [Fact]
    public async Task Index_lists_columns_snake_case()
    {
        var (s, _) = Server();
        await using var __ = s;
        var r = await s.HandleAsync(Get("/api/world/index", "dim=0&x=8&z=8&r=32"));
        Assert.Equal(200, r.Status);
        using var doc = JsonDocument.Parse(r.Text);
        var cols = doc.RootElement.GetProperty("columns");
        Assert.Equal(2, cols.GetArrayLength());
        var c0 = cols[0];
        Assert.Equal(0, c0.GetProperty("cx").GetInt32());
        Assert.True(c0.GetProperty("live").GetBoolean());
        Assert.Equal("maid_chan", c0.GetProperty("by").GetString());
        Assert.Equal(1_700_000_000_000, c0.GetProperty("seen").GetInt64());
        Assert.True(c0.TryGetProperty("hash", out _));
        Assert.Equal(400, (await s.HandleAsync(Get("/api/world/index", "dim=0"))).Status);
    }

    [Fact]
    public async Task Chunks_are_a_binary_payload_with_snapshots()
    {
        var (s, w) = Server();
        await using var _ = s;
        var r = await s.HandleAsync(Get("/api/world/chunks", "dim=0&cols=0,0;5,5"));
        Assert.Equal(200, r.Status);
        Assert.Equal("application/octet-stream", r.ContentType);
        var (dim, cols) = ColumnPayload.Decode(r.Body);
        Assert.Equal(0, dim);
        Assert.Equal(2, cols.Count);
        var snap = ChunkSnapshot.Decode(cols[0].Data);
        Assert.Equal(w.Snap.Hash, snap.Hash);
        Assert.Equal(cols[0].Hash, snap.Hash);
        Assert.Equal(4, snap.Ids[ChunkColumn.Index(3, 64, 4)]);
        Assert.True(cols[1].Missing);
        Assert.Equal(400, (await s.HandleAsync(Get("/api/world/chunks", "cols=nope"))).Status);
        var tooMany = string.Join(';', Enumerable.Range(0, SupervisorWorldRoutes.MaxColumnsPerRequest + 1).Select(i => $"{i},0"));
        Assert.Equal(400, (await s.HandleAsync(Get("/api/world/chunks", "cols=" + tooMany))).Status);
    }

    [Fact]
    public async Task Every_entity_delta_includes_current_open_chests_even_without_new_entity_revision()
    {
        var (s, world) = Server();
        await using var dispose = s;
        world.OpenChests = [new(0, 5, 64, 6, 2)];
        using var full = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/entities", "since=0"))).Text);
        Assert.Equal(2, full.RootElement.GetProperty("chests")[0].GetProperty("open_count").GetInt32());
        long rev = full.RootElement.GetProperty("rev").GetInt64();
        world.OpenChests = [];
        using var delta = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/entities", "since=" + rev))).Text);
        Assert.Equal(0, delta.RootElement.GetProperty("chests").GetArrayLength());
    }

    [Fact]
    public async Task Entities_and_overlays_shape()
    {
        var (s, _) = Server();
        await using var __ = s;
        using (var doc = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/entities", "dim=0"))).Text))
        {
            var bot = doc.RootElement.GetProperty("bots")[0];
            Assert.Equal("main", bot.GetProperty("short").GetString());
            Assert.Equal(90, bot.GetProperty("yaw").GetSingle());
            Assert.Equal("cobblestone x12", bot.GetProperty("held").GetString());
            Assert.Equal("block 4,64,6", bot.GetProperty("claims")[0].GetString());
            Assert.Equal("PoofImaFox", doc.RootElement.GetProperty("entities")[0].GetProperty("name").GetString());
        }
        using (var doc = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/overlays", "dim=0"))).Text))
        {
            var root = doc.RootElement;
            Assert.Equal(4, root.GetProperty("claims")[0].GetProperty("min")[0].GetInt32());
            Assert.Equal("home", root.GetProperty("places")[0].GetProperty("text").GetString());
            Assert.Equal(6, root.GetProperty("plans")[0].GetProperty("blocks")[0].GetArrayLength());
        }
    }

    [Fact]
    public async Task Over_the_cap_is_429_with_retry_after()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var (s, _) = Server(new WorldRouteLimits(ChunksPerSec: 1, ChunksBurst: 2), clock);
        await using var __ = s;
        var q = Get("/api/world/chunks", "cols=0,0");
        Assert.Equal(200, (await s.HandleAsync(q)).Status);
        Assert.Equal(200, (await s.HandleAsync(q)).Status);
        var limited = await s.HandleAsync(q);
        Assert.Equal(429, limited.Status);
        Assert.Equal("1", limited.Headers!["Retry-After"]);
        // Other routes have their own bucket.
        Assert.Equal(200, (await s.HandleAsync(Get("/api/world/overlays"))).Status);
        clock.Now += TimeSpan.FromSeconds(1.1);
        Assert.Equal(200, (await s.HandleAsync(q)).Status);
    }

    [Fact]
    public async Task Without_world_routes_the_paths_are_404()
    {
        await using var s = new SupervisorServer(new SupervisorOptions(), new FakeSupervisorData());
        Assert.Equal(404, (await s.HandleAsync(Get("/api/world/index", "x=0&z=0"))).Status);
    }

    private static WorldState LiveWorld(int cx, int cz, int id)
    {
        var w = new WorldState { Dimension = 0 };
        var ids = new byte[16 * 128 * 16];
        ids[(0 * 16 + 0) * 128 + 64] = (byte)id; // ApplyChunkRegion order: x, z, y
        w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, ids, null, null, null);
        return w;
    }

    // The Explorer freecam drew no light ("no light in hive memory"): live columns now ship their light nibbles (and biomes) when
    // the viewer asks, and the light joins the delta key, so a light-only change is resent while an unchanged column is not.
    [Fact]
    public void Column_source_ships_light_on_request_and_keys_light_changes()
    {
        var live = LiveWorld(0, 0, 4);
        var src = new WorldColumnSource(() => [new LiveBody("maid_chan", "main", true, live)], () => null) { LiveHashTtl = TimeSpan.Zero };
        var plain = src.Columns(0, [(0, 0)])[0];
        Assert.Null(plain.Light);                                            // an old viewer gets exactly what it got before
        Assert.Equal(1, BitConverter.ToUInt16(ColumnPayload.Encode(0, [plain]), 4));

        var lit = src.Columns(0, [(0, 0)], light: true)[0];
        Assert.NotNull(lit.Light);
        var payload = ColumnPayload.Encode(0, [lit]);
        Assert.Equal(ColumnPayload.VersionLight, BitConverter.ToUInt16(payload, 4));
        var back = ColumnPayload.Decode(payload).Columns[0];
        var light = LightSnapshot.Decode(back.Light!);
        Assert.Equal(live.GetSkyLight(5, 100, 5), light.Sky(5, 100, 5));
        Assert.Equal(src.Index(0, 0, 0, 0, 0, 10, light: true)[0].Hash, lit.Hash); // the index and the payload agree on the key
        Assert.NotEqual(plain.Hash, lit.Hash);

        long before = src.Index(0, 0, 0, 0, 0, 10, light: true)[0].Hash;
        Assert.True(live.SetBlock(3, 80, 3, MineClaw.Data.Blocks.Glowstone, 0));
        Assert.NotEqual(LightSnapshot.Decode(lit.Light!).Hash, LightSnapshot.Capture(live.GetColumn(0, 0)!).Hash); // the glowstone relit the column
        long after = src.Index(0, 0, 0, 0, 0, 10, light: true)[0].Hash;
        Assert.NotEqual(before, after);
        var relit = LightSnapshot.Decode(src.Columns(0, [(0, 0)], light: true)[0].Light!);
        Assert.Equal(live.GetBlockLight(3, 81, 3), relit.Block(3, 81, 3));
        Assert.Equal(live.GetBlockLight(5, 80, 3), relit.Block(5, 80, 3));
    }

    [Fact]
    public async Task Index_says_the_game_version_and_the_world_time()
    {
        var (s, _) = Server();
        await using var __ = s;
        var r = await s.HandleAsync(Get("/api/world/index", "dim=0&x=0&z=0&r=32&light=1"));
        Assert.Equal(200, r.Status);
        var json = r.Text;
        Assert.Contains("\"version\":\"b1.8.1\"", json);
        Assert.Contains("\"time\":6000", json);
        Assert.Contains("\"rain\":1", json); // the viewer's sky, light and streaks follow the hive's weather (live 10-29)
        Assert.Contains("\"thunder\":0.5", json);
    }

    [Fact]
    public void Column_source_merges_live_over_remembered_and_ships_stored_blobs()
    {
        var store = new InMemoryWorldMemoryStore();
        var ids = new byte[ChunkSnapshot.Volume];
        ids[ChunkColumn.Index(1, 70, 1)] = 1;
        var old = new ChunkSnapshot(ids, new byte[ChunkSnapshot.Volume]);
        var seen = DateTimeOffset.FromUnixTimeMilliseconds(1_650_000_000_000);
        store.PutChunks([
            new ChunkRow(0, 0, 0, old.Hash, old.Summary(), old.Encode(), seen) { SeenBy = "maid_chan_67" },
            new ChunkRow(0, 2, 0, old.Hash, old.Summary(), old.Encode(), seen) { SeenBy = "maid_chan_69" },
            new ChunkRow(-1, 0, 0, old.Hash, old.Summary(), old.Encode(), seen),
        ]);
        var live = LiveWorld(0, 0, 4);
        var src = new WorldColumnSource(() => [new LiveBody("maid_chan", "main", true, live)], () => store);

        var index = src.Index(0, -1, -1, 3, 3, 100).ToDictionary(c => (c.Cx, c.Cz));
        Assert.Equal(2, index.Count);
        Assert.True(index[(0, 0)].Live);
        Assert.Equal("maid_chan", index[(0, 0)].By);
        Assert.NotEqual(old.Hash, index[(0, 0)].Hash);
        Assert.False(index[(2, 0)].Live);
        Assert.Equal("maid_chan_69", index[(2, 0)].By);
        Assert.Equal(seen.ToUnixTimeMilliseconds(), index[(2, 0)].Seen);
        Assert.Single(src.Index(-1, -1, -1, 3, 3, 100)); // the nether: the remembered column only (main is in the overworld)

        var cols = src.Columns(0, [(0, 0), (2, 0), (9, 9)]);
        var liveSnap = ChunkSnapshot.Decode(cols[0].Data);
        Assert.Equal(index[(0, 0)].Hash, liveSnap.Hash);
        Assert.Equal(cols[0].Hash, liveSnap.Hash);
        Assert.Equal(4, liveSnap.Ids[ChunkColumn.Index(0, 64, 0)]);
        Assert.Equal(store.GetChunk(0, 2, 0)!.Data, cols[1].Data); // stored bytes shipped as-is
        Assert.True(cols[2].Missing);
    }

    [Fact]
    public void Chunk_index_default_and_sqlite_agree_on_rectangles()
    {
        using var db = MineClaw.Agent.Rag.AgentDb.Open(null);
        {
            IWorldMemoryStore sql = new SqliteWorldMemoryStore(db, "maid_chan");
            IWorldMemoryStore mem = new InMemoryWorldMemoryStore();
            var snap = new ChunkSnapshot(new byte[ChunkSnapshot.Volume], new byte[ChunkSnapshot.Volume]);
            var rows = Enumerable.Range(-3, 7).SelectMany(x => Enumerable.Range(-3, 7).Select(z =>
                new ChunkRow(0, x, z, snap.Hash + x * 10 + z, "", snap.Encode(), DateTimeOffset.FromUnixTimeMilliseconds(1000 + x)))).ToList();
            sql.PutChunks(rows);
            mem.PutChunks(rows);
            var a = sql.ChunkIndex(0, -1, -1, 1, 2, 100).OrderBy(r => (r.X, r.Z)).ToList();
            var b = mem.ChunkIndex(0, -1, -1, 1, 2, 100).OrderBy(r => (r.X, r.Z)).ToList();
            Assert.Equal(12, a.Count);
            Assert.Equal(a.Select(r => (r.X, r.Z, r.Hash)), b.Select(r => (r.X, r.Z, r.Hash)));
            Assert.Equal("maid_chan", a[0].SeenBy);
            Assert.Equal(5, sql.ChunkIndex(0, -3, -3, 3, 3, 5).Count);
            Assert.Empty(sql.ChunkIndex(-1, -3, -3, 3, 3, 5));
        }
    }
}
