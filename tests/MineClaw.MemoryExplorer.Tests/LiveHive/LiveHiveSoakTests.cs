using System.Diagnostics;
using System.Net;
using System.Text;
using MineClaw.Data;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.MemoryExplorer.Services;
using MineClaw.Render;
using MineClaw.World;
using static MineClaw.MemoryExplorer.Tests.LiveHive.LiveHiveDecoderTests;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>
/// 60 s of the Live Hive's loops against a misbehaving Supervisor (404s, 503 + Retry-After, slow replies, dropped connections,
/// requests that never answer, garbage JSON): the sync must keep going and recover, and every frame producer — the CPU free
/// camera, the GPU frame preparation fed by the mesh worker, the eye tile — must keep producing, never stalling past 250 ms.
/// </summary>
[Collection("avalonia")]
public class LiveHiveSoakTests
{
    private sealed class ChaosServer : HttpMessageHandler
    {
        private readonly Random _rnd = new(7);
        private readonly object _lock = new();
        private double _botX = 8;
        public int Requests, Injected;

        private double Roll() { lock (_lock) return _rnd.NextDouble(); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var path = r.RequestUri!.AbsolutePath;
            double roll = Roll();
            if (roll < 0.10) { Interlocked.Increment(ref Injected); return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found\"}") }; }
            if (roll < 0.16)
            {
                Interlocked.Increment(ref Injected);
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return busy;
            }
            if (roll < 0.24) { Interlocked.Increment(ref Injected); throw new HttpRequestException("connection reset by peer"); }
            if (roll < 0.28) { Interlocked.Increment(ref Injected); await Task.Delay(Timeout.Infinite, ct); } // never answers: the client's timeout must cut it
            if (roll < 0.38) { Interlocked.Increment(ref Injected); await Task.Delay(300 + (int)(Roll() * 2200), ct); }
            if (roll < 0.42 && !path.EndsWith("/chunks") && !path.Contains("/view/"))
            {
                Interlocked.Increment(ref Injected);
                return Reply("{\"at\": 1, \"bots\": [ {\"name\"", "application/json");
            }
            if (path.EndsWith("/entities"))
            {
                double x;
                lock (_lock) { _botX += 0.25; if (_botX > 14) _botX = 2; x = _botX; }
                return Reply($$"""{"at":1,"rev":{{(long)(x * 100)}},"full":true,"bots":[{"name":"maid_chan","short":"main","main":true,"dim":0,"x":{{x:0.00}},"y":21,"z":8,"yaw":{{x * 20:0}},"pitch":20,"hp":20,"food":20,"claims":[],"live":true,"age_seconds":0}],"entities":[{"kind":"item","name":"cobblestone","dim":0,"x":{{x:0.00}},"y":21.1,"z":5,"id":7,"item":4,"count":{{(int)x}},"age":5}],"removed":[]}""", "application/json");
            }
            if (path.EndsWith("/index"))
            {
                var cols = new StringBuilder();
                for (int cx = -1; cx <= 1; cx++)
                    for (int cz = -1; cz <= 1; cz++)
                        cols.Append(cols.Length > 0 ? "," : "").Append($$"""{"cx":{{cx}},"cz":{{cz}},"hash":{{Column(cx, cz).Hash}},"seen":1,"by":"maid_chan","live":true}""");
                return Reply($$"""{"dim":0,"at":1,"columns":[{{cols}}]}""", "application/json");
            }
            if (path.EndsWith("/chunks"))
            {
                var want = r.RequestUri.Query.Split("cols=")[1].Split('&')[0].Split(';').Select(c => c.Split(',')).Select(c => (int.Parse(c[0]), int.Parse(Uri.UnescapeDataString(c[1]))));
                return Reply(ColumnPayload.Encode(0, want.Select(w => Entry(w.Item1, w.Item2, Column(w.Item1, w.Item2))).ToList()), "application/octet-stream");
            }
            if (path.Contains("/view/")) return Reply(Png.Encode(Enumerable.Repeat(0x336699u, 32 * 24).ToArray(), 32, 24), "image/png");
            return Reply("""{"at":"2026-01-01T00:00:00Z","dim":0,"claims":[],"places":[],"points":[],"plans":[],"dangers":[],"notes":[]}""", "application/json");
        }

        private static HttpResponseMessage Reply(string body, string type) => Reply(Encoding.UTF8.GetBytes(body), type);

        private static HttpResponseMessage Reply(byte[] body, string type)
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            res.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return res;
        }
    }

    private static readonly Dictionary<(int, int), ChunkSnapshot> Columns = new();

    private static ChunkSnapshot Column(int cx, int cz)
    {
        lock (Columns)
        {
            if (Columns.TryGetValue((cx, cz), out var c)) return c;
            var blocks = new List<(int, int, int, byte)>();
            for (int x = 0; x < 16; x++)
                for (int z = 0; z < 16; z++)
                {
                    blocks.Add((x, 20, z, (byte)Blocks.Grass));
                    if ((x + z + cx) % 5 == 0) blocks.Add((x, 21, z, (byte)Blocks.Stone));
                }
            return Columns[(cx, cz)] = Snap([.. blocks]);
        }
    }

    /// <summary>Timestamps of produced frames → the longest gap (ms) after a warm-up.</summary>
    private sealed class Pacer
    {
        private readonly object _lock = new();
        private double _last, _worst;
        public int Frames;
        public double Worst { get { lock (_lock) return _worst; } }
        public void Tick(double nowMs, bool measure)
        {
            lock (_lock)
            {
                if (measure && _last > 0) _worst = Math.Max(_worst, nowMs - _last);
                _last = nowMs;
                Frames++;
            }
        }
    }

    [Fact]
    public async Task Sync_and_render_loops_survive_a_misbehaving_server_for_a_minute()
    {
        const double seconds = 60, warmup = 3;
        var chaos = new ChaosServer();
        var http = new HttpClient(chaos) { BaseAddress = new Uri("http://hive.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new HiveApi(new SupervisorEndpoint("http://hive.test/"), http);
        var cache = new HiveWorldCache();
        var camera = new FlyCamera();
        camera.Set(new CamPose(8, 30, -6, 20, 35, 0));
        using var sync = new HiveSync(api, cache)
        {
            Interests = () => [new Interest(0, 8, 8, 24, 400)],
            MapViews = () => [("main", "iso")],
            OverlayDim = () => 0,
        };
        int maps = 0;
        sync.ViewPng += (_, _, png) => { if (png.Length > 0) Interlocked.Increment(ref maps); };
        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalMilliseconds;
        bool Measuring() => Now() > warmup * 1000;

        // The CPU free camera (fallback renderer), driven by "mouse look" every 10 ms like a user dragging.
        BotDto[] Bots() => sync.Entities?.Bots.ToArray() ?? [];
        EntityDto[] Ents() => sync.Entities?.Entities.ToArray() ?? [];
        using var engine = new FreecamEngine(cache, camera, () => null) { Bots = Bots, Entities = Ents, Threads = 2, MaxFps = 60 };
        var cpu = new Pacer();
        engine.FrameReady += () => { engine.TakeFrame(); cpu.Tick(Now(), Measuring()); };
        engine.SetTarget(128, 72);
        engine.Visible = true;

        // The eye tile (feeds tab).
        var tile = new EyeTile("main", cache);
        tile.Frames.SetTarget(96, 64);
        tile.Frames.Visible = true;
        var tileFrames = new List<double>();
        tile.Frames.FrameReady += () => { tile.Frames.TakeFrame(); lock (tileFrames) tileFrames.Add(Now()); };
        using var tiles = new TileRenderer(cache, () => null) { Tiles = () => [tile], Bots = Bots, Entities = Ents, ThreadsPerTile = 1 };

        // The GPU path minus GL: mesh worker → upload queue → frame preparation at ~60 Hz on a "render thread".
        var store = new SectionStore(cache);
        using var worker = new MeshWorker(store, cache);
        var scene = new FrameScene(cache) { Entities = sync.Store, Bots = Bots, Flags = Overlay.Default };
        var gpu = new Pacer();
        var stop = new CancellationTokenSource();

        sync.Start();
        engine.Start();
        tiles.Start();
        worker.Start();
        var render = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var pose = camera.Pose;
                worker.Aim(0, (int)Math.Floor(pose.X) >> 4, (int)Math.Floor(pose.Z) >> 4, 2, false);
                for (int n = 0; n < 16 && store.Ready.TryDequeue(out var up); n++)
                {
                    if (up.DropAll) scene.Sections.Clear();
                    else if (up.DropColumn) { for (int sy = 0; sy < SectionStore.Sections; sy++) scene.Sections.Remove(new SectionKey(up.Key.Cx, sy, up.Key.Cz)); }
                    else if (up.SolidCount + up.TranslucentCount == 0) scene.Sections.Remove(up.Key);
                    else scene.Sections[up.Key] = new GpuSection { Key = up.Key, SolidVbo = 1, SolidCount = up.SolidCount, TransVbo = 1, TransCount = up.TranslucentCount };
                    up.Release();
                }
                scene.Prepare(pose, 640, 360, DateTimeOffset.UtcNow);
                gpu.Tick(Now(), Measuring());
                Thread.Sleep(15);
            }
        }, TaskCreationOptions.LongRunning);
        var look = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested) { camera.Look(1.5, Math.Sin(Now() / 700) * 0.6); Thread.Sleep(10); }
        }, TaskCreationOptions.LongRunning);

        var windows = new List<(double At, int Tile, int Cpu, int Gpu, bool Link)>();
        while (Now() < seconds * 1000)
        {
            await Task.Delay(5000);
            int tf; lock (tileFrames) tf = tileFrames.Count;
            windows.Add((Now(), tf, cpu.Frames, gpu.Frames, sync.State == LinkState.Connected));
        }
        stop.Cancel();
        await Task.WhenAll(render, look);
        sync.Stop();

        Assert.True(chaos.Injected > 20, $"only {chaos.Injected} faults injected");
        Assert.True(sync.Errors > 0);
        // Back-off must stay proportionate: a parked route (the old 30 s on any 404 gave 44 in a minute) shows up as a trickle; with ~40 % faults ~140 is normal.
        Assert.True(chaos.Requests > 100, $"only {chaos.Requests} requests in a minute: the sync backed off too far");
        Assert.True(cache.Count >= 9, $"{cache.Count} columns synced; state {sync.State} {sync.StateText}; last error {sync.LastError}; requests {chaos.Requests}, injected {chaos.Injected}, errors {sync.Errors}, entities {sync.Store.Count}");
        Assert.True(Volatile.Read(ref maps) > 3, "server pictures stopped");
        Assert.True(sync.Store.Count > 0, "entities never arrived");
        // Frames kept coming, with no stall over 250 ms, for the whole minute.
        Assert.True(cpu.Worst < 250, $"CPU free camera stalled {cpu.Worst:0} ms");
        Assert.True(gpu.Worst < 250, $"GPU frame preparation stalled {gpu.Worst:0} ms");
        // Every 5 s window: each producer made progress (the tile at least once: it redraws when the bot moves or each second).
        for (int i = 1; i < windows.Count; i++)
        {
            var (at, t, c, g, _) = windows[i];
            var p = windows[i - 1];
            Assert.True(c > p.Cpu && g > p.Gpu && t > p.Tile, $"no frames between {p.At / 1000:0} s and {at / 1000:0} s (tile {t - p.Tile}, cpu {c - p.Cpu}, gpu {g - p.Gpu})");
        }
        // The link recovered after the faults (connected in most windows).
        Assert.True(windows.Count(w => w.Link) >= windows.Count / 2, "the sync never recovered");
        Assert.NotEmpty(scene.Sections);
    }
}
