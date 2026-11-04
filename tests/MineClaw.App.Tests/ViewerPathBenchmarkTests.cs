using System.Diagnostics;
using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Chunks;
using Xunit.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// The viewer's data path with the live shape (11-04: <c>world/index</c> avg 145 ms p95 415 ms through the gateway, players moving
/// in steps): main + ten pawn bodies on one <see cref="SharedChunkStore"/>, ~300 live columns, every pawn's tracker holding the same
/// drops, mobs and players. Measures the bot-side handler costs: the index (radius 192, light) as the browser asks it, a 64-column
/// chunk batch, the entity merge behind the 5 Hz delta, and how long an entity delta waits behind heavy reads.
/// </summary>
public sealed class ViewerPathBenchmarkTests(ITestOutputHelper output)
{
    private const int HomeX = 200, HomeZ = 400;

    private static void LoadColumn(WorldState w, int cx, int cz)
    {
        var ids = new byte[16 * 128 * 16];
        var meta = new byte[ids.Length];
        var sky = new byte[ids.Length];
        var blk = new byte[ids.Length];
        for (int dx = 0; dx < 16; dx++)
        for (int dz = 0; dz < 16; dz++)
        {
            int b = (dx * 16 + dz) * 128, x = cx * 16 + dx, z = cz * 16 + dz;
            for (int y = 0; y < 128; y++)
            {
                byte id = y == 0 ? (byte)7 : y < 60 ? (byte)1 : y < 63 ? (byte)3 : y == 63 ? (byte)2 : (byte)0;
                if (y is > 5 and < 50 && (x * 31 + y * 17 + z * 7) % 97 == 0) id = 16;
                if (y is > 10 and < 40 && (x * 13 + y * 5 + z * 11) % 41 == 0) { id = 0; blk[b + y] = (byte)((x + y + z) & 7); } // caves, some lit
                ids[b + y] = id;
                meta[b + y] = id == 1 && (x + z) % 5 == 0 ? (byte)1 : (byte)0;
                sky[b + y] = y >= 64 ? (byte)15 : y > 58 ? (byte)(y - 50) : (byte)0;
            }
            if ((x * 7 + z * 13) % 53 == 0) { for (int y = 64; y < 68; y++) ids[b + y] = 17; ids[b + 68] = 18; }
            else if ((x * 11 + z * 3) % 7 == 0) ids[b + 64] = 31;
        }
        w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, ids, meta, blk, sky);
    }

    internal static (List<LiveBody> Bodies, SharedChunkStore Store) Hive(int pawns = 10, int view = 5)
    {
        var store = new SharedChunkStore(128);
        var bodies = new List<LiveBody>();
        var main = new WorldState(chunkStore: store);
        int mcx = (HomeX - 160) >> 4, mcz = HomeZ >> 4, hcx = HomeX >> 4, hcz = HomeZ >> 4;
        for (int cx = mcx - view; cx <= mcx + view; cx++) for (int cz = mcz - view; cz <= mcz + view; cz++) LoadColumn(main, cx, cz);
        bodies.Add(new LiveBody("maid_chan", "main", true, main));
        var rng = new Random(3);
        var ents = new List<Action<WorldState>>();
        for (int e = 0; e < 40; e++)
        {
            int id = 1000 + e;
            var pos = new Vec3d(HomeX - 20 + rng.Next(40) + 0.5, 64, HomeZ - 20 + rng.Next(40) + 0.5);
            ents.Add(w => w.Entities.SpawnItem(id, new ItemStack(4, 1, 0), pos));
        }
        for (int e = 0; e < 25; e++)
        {
            int id = 2000 + e, type = new[] { 90, 91, 92, 93, 54, 50, 51 }[rng.Next(7)];
            var pos = new Vec3d(HomeX - 40 + rng.Next(80) + 0.5, 64, HomeZ - 40 + rng.Next(80) + 0.5);
            ents.Add(w => w.Entities.SpawnMob(id, type, pos, 0, 0));
        }
        for (int i = 0; i < pawns; i++)
        {
            int id = 5000 + i;
            string n = "maid_chan_" + (101 + i);
            var pos = new Vec3d(HomeX + i, 64, HomeZ);
            ents.Add(w => { if (w.Entities.SelfId != id) w.Entities.SpawnPlayer(id, n, pos, 0, 0); });
        }
        for (int i = 0; i < pawns; i++)
        {
            var w = new WorldState(chunkStore: store);
            int ox = (i % 5) - 2, oz = (i / 5) - 1;
            for (int cx = hcx + ox - view; cx <= hcx + ox + view; cx++) for (int cz = hcz + oz - view; cz <= hcz + oz + view; cz++) LoadColumn(w, cx, cz);
            w.Entities.SelfId = 5000 + i;
            foreach (var a in ents) a(w);
            bodies.Add(new LiveBody("maid_chan_" + (101 + i), (101 + i).ToString(), false, w));
        }
        return (bodies, store);
    }

    private static double Time(int n, Action a)
    {
        a();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        return sw.Elapsed.TotalMilliseconds / n;
    }

    [Fact]
    public void Index_chunks_and_entity_merge_with_ten_pawns()
    {
        var (bodies, store) = Hive();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddDays(20000));
        var src = new WorldColumnSource(() => bodies, () => null, clock);
        int r = 192, cx0 = (HomeX - r) >> 4, cx1 = (HomeX + r) >> 4, cz0 = (HomeZ - r) >> 4, cz1 = (HomeZ + r) >> 4;
        int max = (cx1 - cx0 + 1) * (cz1 - cz0 + 1);
        int live = src.Index(0, cx0, cz0, cx1, cz1, max, light: true).Count(c => c.Live);

        // The browser asks for the index about once a second: every ask is past the live-hash TTL, nothing changed in between.
        double indexMs = Time(10, () => { clock.Now += TimeSpan.FromSeconds(1.1); src.Index(0, cx0, cz0, cx1, cz1, max, light: true); });
        // The same with one block changed in each of 20 columns per second (pawns digging and building).
        int k = 0;
        var pawn = bodies[1].World;
        double indexBusyMs = Time(10, () =>
        {
            clock.Now += TimeSpan.FromSeconds(1.1);
            for (int i = 0; i < 20; i++, k++) pawn.SetBlock(HomeX - 70 + (k * 16) % 140, 63, HomeZ - 50 + (k / 9 * 16) % 100, (k & 1) == 0 ? 0 : 4, 0);
            src.Index(0, cx0, cz0, cx1, cz1, max, light: true);
        });
        // A 64-column batch with light (the viewer's chunk fetch), the same columns again (unchanged).
        var cols = new List<(int, int)>();
        for (int cx = (HomeX >> 4) - 4; cx < (HomeX >> 4) + 4; cx++) for (int cz = (HomeZ >> 4) - 4; cz < (HomeZ >> 4) + 4; cz++) cols.Add((cx, cz));
        double chunksMs = Time(5, () => ColumnPayload.Encode(0, src.Columns(0, cols, light: true)));
        // The entity merge behind every 200 ms delta.
        var names = new HashSet<string>(bodies.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        double mergeMs = Time(50, () => HiveEntityMerge.Merge(bodies.Select(b => new BodyEntities(b.Name, 0, b.World.Entities.All())), names, DateTime.UtcNow));

        output.WriteLine($"{bodies.Count} bodies, {store.ColumnCount} shared columns, index r={r}: {max} cells, {live} live");
        output.WriteLine($"index (light, every 1.1 s, unchanged) {indexMs:0.00} ms; with 20 changed columns {indexBusyMs:0.00} ms; " +
                         $"chunks x64 (light) {chunksMs:0.00} ms; entity merge {mergeMs:0.000} ms");
        Assert.True(indexMs < 1000, $"index {indexMs:0.0} ms");
    }

    [Fact]
    public void A_live_column_is_hashed_and_encoded_again_only_when_it_changed()
    {
        var (bodies, _) = Hive(pawns: 1, view: 1);
        var w = bodies[1].World;
        int cx = (HomeX >> 4) - 2, cz = (HomeZ >> 4) - 1, x = cx * 16 + 3, z = cz * 16 + 5; // the first pawn's view centre
        var src = new WorldColumnSource(() => bodies, () => null); // the default TTL: the column's version decides
        long Hash() => src.Index(0, cx, cz, cx, cz, 1, light: true).Single().Hash;
        long h0 = Hash();
        var p0 = src.Columns(0, [(cx, cz)], light: true).Single();
        Assert.Equal(h0, Hash());
        Assert.Same(p0.Data, src.Columns(0, [(cx, cz)], light: true).Single().Data); // unchanged: the last payload ships again
        long v = w.GetColumn(cx, cz)!.Version;
        Assert.True(w.SetBlock(x, 70, z, Blocks.Glowstone, 0));
        Assert.True(w.GetColumn(cx, cz)!.Version > v);
        long h1 = Hash();
        Assert.NotEqual(h0, h1);                       // a block change shows at once (no TTL wait)
        var p1 = src.Columns(0, [(cx, cz)], light: true).Single();
        Assert.NotSame(p0.Data, p1.Data);
        Assert.Equal(h1, p1.Hash);                     // the index and the payload agree on the key
        Assert.Equal(Blocks.Glowstone, ChunkSnapshot.Decode(p1.Data).Ids[ChunkColumn.Index(x & 15, 70, z & 15)]);
    }

    [Theory]
    [InlineData("/api/world/index", "/api/world/index")]
    [InlineData("/api/story/colonies/story-1", "/api/story/colonies/*")]
    [InlineData("/api/story/colonies/story-1/architect", "/api/story/colonies/*/architect")]
    [InlineData("/api/viewer/bodies/maid_chan_101/inventory", "/api/viewer/bodies/*/inventory")]
    [InlineData("/api/brains/7/turn/12", "/api/brains/*/turn/*")]
    [InlineData("/", "/")]
    public void Perf_lines_fold_ids_out_of_routes(string path, string route) => Assert.Equal(route, SupervisorPerf.RouteOf(path));

    [Fact]
    public void Perf_line_names_the_slow_routes()
    {
        var perf = new SupervisorPerf();
        for (int i = 0; i < 20; i++) perf.Record("/api/world/index", i);
        perf.Record("/api/story/colonies/story-1/architect", 640);
        var line = perf.Summary();
        Assert.Contains("/api/story/colonies/*/architect 1x", line);
        Assert.Contains("/api/world/index 20x p50 10 p95 19 max 19 ms", line);
        Assert.Contains("a* ", line);
        Assert.DoesNotContain("/api/world/index", perf.Summary()); // the next line starts over
    }

    /// <summary>A world whose heavy reads take a while (a 64-column chunk batch, an index) and whose entities are instant.</summary>
    private sealed class SlowWorld : ISupervisorWorld
    {
        public int ColumnsMs = 300, IndexMs = 200;
        public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max) { Thread.Sleep(IndexMs); return []; }
        public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols) { Thread.Sleep(ColumnsMs); return []; }
        public IReadOnlyList<WorldBot> Bots() => [];
        public IReadOnlyList<WorldEntity> Entities(int? dim) => [new("player", "PoofImaFox", 0, 3, 65, 3, Id: 7)];
        public WorldOverlays Overlays(int dim) => new(DateTimeOffset.UnixEpoch, dim, [], [], [], [], [], []);
    }

    private static SupervisorRequest Get(string path, string query)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries)) { var i = kv.IndexOf('='); q[kv[..i]] = kv[(i + 1)..]; }
        return new SupervisorRequest("GET", path, q, "s3cret");
    }

    [Fact]
    public async Task An_entity_delta_never_waits_behind_chunk_batches()
    {
        var routes = new SupervisorWorldRoutes(new SlowWorld(), new WorldRouteLimits(IndexPerSec: 100, IndexBurst: 100, ChunksPerSec: 100, ChunksBurst: 100,
            EntitiesPerSec: 100, EntitiesBurst: 100));
        var lat = new List<double>();
        int busy = 0;
        for (int round = 0; round < 3; round++)
        {
            // Two chunk batches and an index in flight (a viewer panning), then the bridge's 5 Hz entity poll.
            var heavy = new[]
            {
                routes.TryHandleAsync(Get("/api/world/chunks", "dim=0&cols=0,0;1,0"), "/api/world/chunks"),
                routes.TryHandleAsync(Get("/api/world/chunks", "dim=0&cols=2,0;3,0"), "/api/world/chunks"),
                routes.TryHandleAsync(Get("/api/world/index", $"dim=0&x={round * 500}&z=0&r=64&light=1"), "/api/world/index"),
            };
            await Task.Delay(30);
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                var r = await routes.TryHandleAsync(Get("/api/world/entities", "since=" + i), "/api/world/entities");
                lat.Add(sw.Elapsed.TotalMilliseconds);
                if (r!.Status != 200) busy++;
                await Task.Delay(20);
            }
            await Task.WhenAll(heavy);
        }
        output.WriteLine($"entity delta behind heavy reads: avg {lat.Average():0} ms, max {lat.Max():0} ms, {busy} not 200");
        Assert.True(lat.Max() < 100, $"an entity delta waited {lat.Max():0} ms");
    }
}
