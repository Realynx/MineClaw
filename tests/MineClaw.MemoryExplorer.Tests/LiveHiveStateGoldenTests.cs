using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.MemoryExplorer.Services;
using MineClaw.Render;
using MineClaw.World;
using Xunit.Abstractions;
using static MineClaw.MemoryExplorer.Tests.LiveHive.LiveHiveDecoderTests;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Live Hive's sync and state code byte for byte (WP08 complexity refactor, checkpoint 3): the world pass's requests and
/// cache/overlay/view results over a scripted fake Supervisor (normal, failing, backing-off), the HTTP client's errors, the entity
/// store's glide (fake clock), the cache's reconcile, the section diffs and the mesh worker's uploads, the GPU atlas and quad
/// cells, and the vision panel's state after scripted polls and compose-from-sheet. The dumps that format local times are
/// compared only in the time zone they were recorded in. Set EXPLORER_GOLDEN_OUT to a directory to write the dumps out.
/// </summary>
[Collection("avalonia")] // the vision panel is an Avalonia view model: kept off the shared dispatcher's toes
public sealed class LiveHiveStateGoldenTests(ITestOutputHelper output)
{
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20];
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b))[..20];

    private void Compare(List<(string Name, string Dump)> rows, string expected)
    {
        var outDir = Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_OUT");
        var actual = new StringBuilder();
        foreach (var (n, dump) in rows)
        {
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, n + ".txt"), dump);
            actual.Append(n).Append(' ').Append(Hash(dump)).Append('\n');
        }
        var want = string.Join("\n", expected.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0)) + "\n";
        if (want != actual.ToString()) output.WriteLine(actual.ToString());
        Assert.Equal(want, actual.ToString());
    }

    // ------------------------------------------------------------------ a scripted Supervisor

    private sealed class Script : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public Func<string, int, HttpResponseMessage> Reply = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        private readonly Dictionary<string, int> _n = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.PathAndQuery;
            var route = r.RequestUri.AbsolutePath;
            int n;
            lock (Calls)
            {
                Calls.Add(path);
                n = _n.GetValueOrDefault(route);
                _n[route] = n + 1;
            }
            return Task.FromResult(Reply(path, n));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Status(int code, double? retryAfter = null)
    {
        var res = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("no") };
        if (retryAfter is { } s) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return res;
    }

    private static ChunkSnapshot Col(int cx, int cz) => Snap((cx & 15, 60 + (cz & 7), 3, (byte)(1 + ((cx * 7 + cz) & 3))), (5, 64, 5, 4));

    private static string IndexJson(int dim, IEnumerable<(int Cx, int Cz)> cols, string extra = "") =>
        $$"""{"dim":{{dim}},"at":1,"columns":[{{string.Join(",", cols.Select(c => $$"""{"cx":{{c.Cx}},"cz":{{c.Cz}},"hash":{{Col(c.Cx, c.Cz).Hash}},"seen":5,"by":"maid_chan","live":true}"""))}}]{{extra}}}""";

    /// <summary>The columns a chunks request asks for, as a payload (every third one missing).</summary>
    private static byte[] ChunksFor(string pathAndQuery)
    {
        var q = Uri.UnescapeDataString(pathAndQuery);
        int dim = int.Parse(q.Split("dim=")[1].Split('&')[0]);
        var cols = q.Split("cols=")[1].Split('&')[0].Split(';').Select(s => s.Split(',')).Select(p => (int.Parse(p[0]), int.Parse(p[1]))).ToList();
        var entries = cols.Select((c, i) => i % 3 == 2 ? new ColumnPayloadEntry(c.Item1, c.Item2, 0, 0, null, false, []) : Entry(c.Item1, c.Item2, Col(c.Item1, c.Item2), 5)).ToList();
        return ColumnPayload.Encode(dim, entries);
    }

    private const string OverlaysJson = """{"at":"2026-01-01T00:00:00Z","dim":DIM,"claims":[{"kind":"claim","text":"home","min":[0,60,0],"max":[4,64,4],"owner":"fox"}],"places":[],"points":[],"plans":[],"dangers":[],"notes":["n"]}""";

    private static readonly MethodInfo WorldPassM = typeof(HiveSync).GetMethod("WorldPass", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static Task WorldPass(HiveSync s) => (Task)WorldPassM.Invoke(s, [CancellationToken.None])!;

    private static string SyncState(HiveSync sync, HiveWorldCache cache, Script fake, List<string> views)
    {
        var sb = new StringBuilder();
        lock (fake.Calls)
        {
            foreach (var c in fake.Calls) sb.Append("  GET ").Append(c).Append('\n');
            fake.Calls.Clear();
        }
        foreach (int d in new[] { 0, -1, 1 })
        {
            sb.Append("  dim ").Append(d).Append(" cols ").Append(cache.CountIn(d)).Append(" time ").Append(cache.TimeNow(d, long.MaxValue / 2)?.ToString() ?? "null")
                .Append(" weather ").Append(cache.WeatherIn(d)).Append(" overlays ").Append(sync.Overlays(d) is { } o ? o.Dim + "/" + o.Claims.Count + "/" + o.Notes.Count : "none").Append('\n');
        }
        foreach (var v in views) sb.Append("  view ").Append(v).Append('\n');
        views.Clear();
        sb.Append("  version ").Append(cache.GameVersion ?? "null").Append(" error ").Append(sync.LastError ?? "null").Append(" errors ").Append(Interlocked.Read(ref sync.Errors)).Append(" state ").Append(sync.State).Append('\n');
        return sb.ToString();
    }

    private static async Task<string> SyncDump()
    {
        var sb = new StringBuilder();
        async Task Scenario(string name, Func<string, int, HttpResponseMessage> reply, Action<HiveSync> setup, int passes, Action<HiveSync, int>? between = null,
            Action<string, string, byte[]>? onView = null)
        {
            var fake = new Script { Reply = reply };
            using var http = new HttpClient(fake) { BaseAddress = new Uri("http://hive.test/") };
            using var api = new HiveApi(new SupervisorEndpoint("http://hive.test/"), http);
            var cache = new HiveWorldCache();
            var sync = new HiveSync(api, cache) { MaxColumnsPerDim = 40 };
            var views = new List<string>();
            sync.ViewPng += (b, m, png) => { lock (views) views.Add($"{b} {m} {png.Length}"); onView?.Invoke(b, m, png); };
            setup(sync);
            sb.Append("== ").Append(name).Append('\n');
            for (int p = 0; p < passes; p++)
            {
                between?.Invoke(sync, p);
                await WorldPass(sync);
                sb.Append(" pass ").Append(p).Append('\n').Append(SyncState(sync, cache, fake, views));
            }
        }

        HttpResponseMessage Normal(string path, int n)
        {
            if (path.StartsWith("/api/world/index"))
            {
                var q = path.Split('?')[1].Split('&').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => kv[1]);
                int dim = int.Parse(q["dim"]), x = int.Parse(q["x"]), z = int.Parse(q["z"]), r = int.Parse(q["r"]);
                var cols = new List<(int, int)>();
                for (int cx = (x - r) >> 4; cx <= (x + r) >> 4 && cols.Count < 130; cx++)
                    for (int cz = (z - r) >> 4; cz <= (z + r) >> 4 && cols.Count < 130; cz++)
                        if ((cx * 31 + cz * 17 + n) % 4 != 0) cols.Add((cx, cz));
                var extra = dim == 0 ? ""","version":"b1.8.1","time":6000,"rain":0.5""" : dim == -1 ? ""","version":"b1.8.1","time":12,"rain":1,"thunder":0.25""" : "";
                return Json(IndexJson(dim, cols, extra));
            }
            if (path.StartsWith("/api/world/chunks")) return Bytes(ChunksFor(path));
            if (path.StartsWith("/api/world/overlays")) return Json(OverlaysJson.Replace("DIM", path.Split("dim=")[1]));
            if (path.StartsWith("/view/")) return Bytes(Encoding.UTF8.GetBytes(path));
            return Status(404);
        }

        await Scenario("normal", Normal, s =>
        {
            s.Interests = () => [new Interest(0, 8, 8, 32, 60_000), new Interest(0, 20, 30, 40, 60_000), new Interest(-1, 100, -50, 300, 0), new Interest(1, 1000, 1000, 5, 0)];
            s.MapViews = () => [("main", "iso"), ("67", "top"), ("main", "iso"), ("a b", "side")];
            s.OverlayDim = () => 0;
            s.XRay = "on";
        }, 3, (s, p) =>
        {
            if (p == 2) { s.OverlayDim = () => -1; s.XRay = "auto"; s.Interests = () => [new Interest(0, 600, 600, 16, 0)]; }
        });

        await Scenario("no overlays, off xray", Normal, s =>
        {
            s.Interests = () => [];
            s.MapViews = () => [("main", "iso")];
            s.OverlayDim = () => null;
            s.XRay = "off";
        }, 1);

        await Scenario("index busy", (path, n) => path.StartsWith("/api/world/index") ? Status(503, 60) : Normal(path, n), s =>
        {
            s.Interests = () => [new Interest(0, 8, 8, 32, 0), new Interest(0, 200, 8, 32, 0)];
            s.OverlayDim = () => 0;
        }, 2);

        await Scenario("index fails once", (path, n) => path.StartsWith("/api/world/index") && n == 0 ? Status(429, 0) : Normal(path, n), s =>
        {
            s.Interests = () => [new Interest(0, 8, 8, 32, 0), new Interest(0, 200, 8, 32, 0)];
        }, 2);

        await Scenario("chunks fail", (path, n) => path.StartsWith("/api/world/chunks") ? (n == 0 ? Status(503, 60) : Status(500)) : Normal(path, n), s =>
        {
            s.Interests = () => [new Interest(0, 8, 8, 32, 0), new Interest(-1, 8, 8, 32, 0)];
        }, 2);

        await Scenario("chunks garbage", (path, n) => path.StartsWith("/api/world/chunks") && n == 1 ? Bytes([1, 2, 3, 4, 5]) : Normal(path, n), s =>
        {
            s.Interests = () => [new Interest(0, 8, 8, 200, 0)];
        }, 2);

        await Scenario("views fail", (path, n) => path.StartsWith("/view/67") ? Status(404) : path.StartsWith("/view/x") ? Status(503, 60) : Normal(path, n), s =>
        {
            s.MapViews = () => [("main", "top"), ("main", "side"), ("67", "iso"), ("x", "iso")];
            s.OverlayDim = () => 0;
        }, 1, onView: (b, _, _) => { if (b == "main") throw new InvalidOperationException("decode broke"); });

        await Scenario("overlays fail", (path, n) => path.StartsWith("/api/world/overlays") ? Status(401) : Normal(path, n), s =>
        {
            s.OverlayDim = () => 0;
        }, 2, (s, p) => { if (p == 1) s.OverlayDim = () => -1; });

        await Scenario("many views", Normal, s =>
        {
            s.MapViews = () => Enumerable.Range(0, 70).Select(i => ($"b{i}", "iso")).ToList();
        }, 2);
        return sb.ToString();
    }

    private static readonly MethodInfo FastLoopM = typeof(HiveSync).GetMethod("FastLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private const string EntitiesJson = """{"at":1,"rev":REV,"full":FULL,"bots":[{"name":"maid_chan","short":"main","main":true,"dim":0,"x":8,"y":21,"z":8,"hp":20,"food":20,"claims":[],"live":true,"age_seconds":0}],"entities":[{"kind":"item","name":"cobblestone","dim":0,"x":1,"y":21,"z":1,"id":ID,"item":4,"count":2,"flags":1}],"removed":[]}""";

    /// <summary>The fast loop's states over ok, rate-limited, dropped, garbage, dropped again, unauthorized, ok (≈10 s of back-off waits).</summary>
    private static async Task<string> FastLoopDump()
    {
        using var cts = new CancellationTokenSource();
        var fake = new Script();
        fake.Reply = (path, n) => n switch
        {
            0 => Json(EntitiesJson.Replace("REV", "3").Replace("FULL", "true").Replace("ID", "7")),
            1 => Status(429, 0),
            2 or 4 => throw new HttpRequestException("refused"),
            3 => Json("{not json"),
            5 => Status(403),
            6 => Json(EntitiesJson.Replace("REV", "4").Replace("FULL", "false").Replace("ID", "8")),
            _ => Cancel(),
        };
        HttpResponseMessage Cancel() { cts.Cancel(); throw new TaskCanceledException("stop"); }
        using var http = new HttpClient(fake) { BaseAddress = new Uri("http://hive.test/") };
        using var api = new HiveApi(new SupervisorEndpoint("http://hive.test/"), http);
        var sync = new HiveSync(api, new HiveWorldCache());
        var sb = new StringBuilder();
        int changed = 0;
        sync.StateChanged += () => sb.Append("state ").Append(sync.State).Append(" '").Append(sync.StateText).Append("' error '").Append(sync.LastError).Append("'\n");
        sync.EntitiesChanged += e => { changed++; sb.Append("entities ").Append(e.Rev).Append(' ').Append(e.Entities.Count).Append('\n'); };
        await (Task)FastLoopM.Invoke(sync, [cts.Token])!;
        sb.Append("calls ").Append(string.Join(" ", fake.Calls)).Append("\nerrors ").Append(Interlocked.Read(ref sync.Errors)).Append(" changed ").Append(changed)
            .Append(" store ").Append(sync.Store.Count).Append(" last '").Append(sync.LastError).Append("' entities ").Append(sync.Entities?.Rev).Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the HTTP client's errors

    private sealed class Throwing(Exception ex) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(ex);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static async Task<string> ApiDump()
    {
        var sb = new StringBuilder();
        var cases = new (string Name, Func<HttpResponseMessage> Reply, bool Track)[]
        {
            ("ok", () => Bytes([1, 2, 3]), false),
            ("ok tracked", () => Bytes([1, 2, 3, 4]), true),
            ("401", () => Status(401), false),
            ("403 tracked", () => Status(403), true),
            ("404", () => Status(404), false),
            ("429", () => Status(429), false),
            ("429 retry", () => Status(429, 7), false),
            ("503 retry", () => Status(503, 3), false),
            ("503", () => Status(503), false),
            ("500", () => Status(500), false),
            ("418", () => Status(418), false),
            ("204", () => new HttpResponseMessage(HttpStatusCode.NoContent), false),
            ("send fails inner", () => throw new HttpRequestException("outer", new IOException("refused")), false),
            ("send fails", () => throw new HttpRequestException("plain"), false),
            ("send times out", () => throw new TaskCanceledException("t"), false),
            ("read fails io", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new Throwing(new IOException("reset")) }, false),
            ("read fails http", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new Throwing(new HttpRequestException("h", new IOException("inner reset"))) }, false),
            ("read times out", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new Throwing(new OperationCanceledException("c")) }, false),
        };
        foreach (var (name, reply, track) in cases)
        {
            var fake = new Script { Reply = (_, _) => reply() };
            using var http = new HttpClient(fake) { BaseAddress = new Uri("http://hive.test/") };
            using var api = new HiveApi(new SupervisorEndpoint("http://hive.test/"), http);
            sb.Append(name).Append(": ");
            for (int round = 0; round < 2; round++)
            {
                try
                {
                    var b = await api.GetBytesAsync("route", "api/x?y=1", CancellationToken.None, track);
                    sb.Append("bytes ").Append(b.Length);
                }
                catch (HiveApiException ex)
                {
                    sb.Append("hive ").Append(ex.Status).Append(" '").Append(ex.Message).Append("' retry ")
                        .Append(ex.RetryAfter is { } ra ? (ra >= TimeSpan.FromSeconds(2.9) || ra == TimeSpan.FromSeconds(1) ? ra.ToString() : "jitter") : "null");
                }
                catch (Exception ex) { sb.Append(ex.GetType().Name).Append(" '").Append(ex.Message).Append('\''); }
                sb.Append(" | in ").Append(Interlocked.Read(ref api.BytesIn)).Append(" off ").Append(api.BackingOff("route")).Append(" latency ").Append(api.LatencyMs >= 0)
                    .Append(" calls ").Append(fake.Calls.Count).Append(" ; ");
            }
            sb.Append('\n');
        }
        // A cancelled caller: the cancellation goes through untouched.
        {
            var fake = new Script { Reply = (_, _) => throw new TaskCanceledException("caller") };
            using var http = new HttpClient(fake) { BaseAddress = new Uri("http://hive.test/") };
            using var api = new HiveApi(new SupervisorEndpoint("http://hive.test/"), http);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { await api.GetBytesAsync("route", "api/x", cts.Token); sb.Append("no throw\n"); }
            catch (Exception ex) { sb.Append("cancelled: ").Append(ex is OperationCanceledException).Append(" off ").Append(api.BackingOff("route")).Append('\n'); }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the entity store

    private static string EntityDump()
    {
        double now = 1000;
        var store = new EntityStore { Clock = () => now };
        var sb = new StringBuilder();
        var views = new List<EntityView>();
        void Show(string name)
        {
            sb.Append("== ").Append(name).Append(" count ").Append(store.Count).Append(" version ").Append(store.Version).Append('\n');
            foreach (int dim in new[] { 0, -1 })
            {
                store.Interpolated(dim, views);
                foreach (var v in views)
                    sb.Append("  ").Append(v.Id).Append(' ').Append(v.Dim).Append(' ').Append(v.Kind).Append(' ').Append(v.Name ?? "null").Append(' ')
                        .Append(v.X.ToString("R")).Append(',').Append(v.Y.ToString("R")).Append(',').Append(v.Z.ToString("R")).Append(" age ").Append(v.AgeTicks.ToString("R"))
                        .Append(" i").Append(v.Item).Append(" d").Append(v.Damage).Append(" c").Append(v.Count).Append(" t").Append(v.Type).Append(" y").Append(v.Yaw.ToString("R"))
                        .Append(" v").Append(v.Vx.ToString("R")).Append(',').Append(v.Vz.ToString("R")).Append(" f").Append(v.Flags).Append(" var").Append(v.Variant).Append('\n');
            }
        }
        void Apply(EntitiesDto d)
        {
            var full = store.Apply(d);
            sb.Append("apply -> ").Append(full.Full).Append(' ').Append(full.Removed == null).Append(' ').Append(full.Rev).Append(' ')
                .Append(string.Join(";", full.Entities.Select(e => $"{e.Id?.ToString() ?? "-"}:{e.Dim}:{e.Kind}:{e.X:R}"))).Append('\n');
        }
        Show("empty");
        Apply(new EntitiesDto(1, [], [new("player", "Fox", 0, 1, 64, 1, Id: 1, Yaw: 90, Flags: 2), new("item", "cobblestone", 0, 5, 64, 5, Id: 2, Item: 4, Damage: 1, Count: 3, Age: 2),
            new("mob", "zombie", -1, 9, 64, 9, Id: 1, Type: 54, Vx: 0.1, Vz: -0.2, Variant: 3), new("player", "anon", 0, 7, 64, 7)], 5, true));
        Show("first");
        now += 100;
        Apply(new EntitiesDto(2, [], [new("player", "Fox", 0, 3, 64, 1, Id: 1), new("item", "cobblestone", 0, 50, 64, 5, Id: 2, Age: 2.1)], 6, false));
        Show("glide + snap");
        now += 50;
        Show("mid glide");
        now += 400;
        Apply(new EntitiesDto(3, [], [new("item", "cobblestone", 0, 50, 64, 6, Id: 2, Age: 9)], 7, false, [[0, 1], [5], [-1, 9]]));
        Show("removed + age jump");
        now += 5000;
        Apply(new EntitiesDto(4, [], [new("mob", "pig", 0, 2, 64, 2, Id: 4), new("player", null, 0, 3, 64, 3)], 8, true));
        Show("full again");
        now += 1;
        Apply(new EntitiesDto(5, [], [new("mob", "pig", 0, 2.5, 64, 2, Id: 4), new("player", "late", 0, 3, 64, 3)], 9, false));
        Show("delta with anon");
        store.Clear();
        Show("cleared");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ cache reconcile

    private static string ReconcileDump()
    {
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, Enumerable.Range(0, 9).Select(i => Entry(i % 3, i / 3, Col(i % 3, i / 3), 5, "a", true)).ToList()));
        cache.Apply(ColumnPayload.Encode(-1, [Entry(0, 0, Col(0, 0), 5)]));
        var sb = new StringBuilder();
        void Run(string name, int dim, int cx0, int cz0, int cx1, int cz1, List<ColumnInfoDto> index)
        {
            long v0 = cache.Version;
            var want = cache.Reconcile(dim, cx0, cz0, cx1, cz1, index);
            sb.Append(name).Append(": want ").Append(string.Join(";", want)).Append(" changed ").Append(cache.Version != v0).Append(" counts ")
                .Append(cache.CountIn(0)).Append('/').Append(cache.CountIn(-1));
            for (int i = 0; i < 9; i++)
                if (cache.Get(0, i % 3, i / 3) is { } c) sb.Append(' ').Append(c.Cx).Append(',').Append(c.Cz).Append(':').Append(c.SeenMs).Append(':').Append(c.By ?? "null").Append(':').Append(c.Live);
            sb.Append('\n');
        }
        ColumnInfoDto Info(int cx, int cz, long seen = 5, string? by = "a", bool live = true, long? hash = null) => new(cx, cz, hash ?? Col(cx, cz).Hash, seen, by, live);
        Run("same", 0, 0, 0, 2, 2, Enumerable.Range(0, 9).Select(i => Info(i % 3, i / 3)).ToList());
        Run("touched", 0, 0, 0, 2, 2, [Info(0, 0, seen: 9), Info(1, 0, by: "b"), Info(2, 0, live: false), Info(0, 1, by: null), Info(1, 1), Info(2, 1), Info(0, 2), Info(1, 2), Info(2, 2)]);
        Run("changed hashes + new", 0, 0, 0, 2, 2, [Info(0, 0, hash: 1), Info(1, 0), Info(5, 5), Info(2, 0, seen: 9, live: false), Info(0, 1), Info(1, 1), Info(2, 1), Info(0, 2), Info(1, 2), Info(2, 2)]);
        Run("dropped inside", 0, 0, 0, 1, 1, [Info(0, 0, seen: 9), Info(1, 1)]);
        Run("outside kept", 0, 5, 5, 6, 6, []);
        Run("other dim", -1, -1, -1, 1, 1, []);
        Run("empty", 0, 0, 0, 2, 2, []);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ section diffs and the mesh worker

    private static ChunkSnapshot RandomSnap(Random r, int height)
    {
        var ids = new byte[16 * 16 * height];
        var meta = new byte[ids.Length];
        for (int i = 0; i < 300; i++)
        {
            int x = r.Next(16), y = r.Next(height), z = r.Next(16);
            ids[ChunkColumn.Index(x, y, z)] = (byte)r.Next(1, 6);
        }
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) ids[ChunkColumn.Index(x, 10, z)] = 1;
        return new ChunkSnapshot(ids, meta);
    }

    private static ChunkSnapshot Mutate(Random r, ChunkSnapshot s, int edits, bool edge)
    {
        var ids = (byte[])s.Ids.Clone();
        var meta = (byte[])s.Meta.Clone();
        for (int i = 0; i < edits; i++)
        {
            int x = edge ? (r.Next(2) == 0 ? 0 : 15) : r.Next(16), z = edge && r.Next(2) == 0 ? (r.Next(2) == 0 ? 0 : 15) : r.Next(16);
            int y = r.Next(4) == 0 ? r.Next(s.Height / 16) * 16 + (r.Next(2) == 0 ? 0 : 15) : r.Next(s.Height);
            int idx = ChunkColumn.Index(x, y, z);
            if (r.Next(3) == 0) meta[idx] = (byte)r.Next(16); else ids[idx] = (byte)r.Next(0, 6);
        }
        return new ChunkSnapshot(ids, meta);
    }

    private static ClientColumn Client(ChunkSnapshot s, Random r, int lightMode)
    {
        LightSnapshot? light = lightMode switch
        {
            0 => null,
            1 => LightSnapshot.From(s.Height, (_, y, _) => y > 20 ? 15 : 0, (x, y, z) => (x + y + z) % 16),
            2 => LightSnapshot.From(s.Height, (_, y, _) => y > 21 ? 15 : 0, (x, y, z) => (x + y + z) % 16),
            _ => LightSnapshot.From(s.Height, (_, y, _) => y > 20 ? 15 : 0, (x, y, z) => (x + y + z) % 16, Enumerable.Range(0, 256).Select(i => (byte)(i % 7)).ToArray()),
        };
        return new ClientColumn(0, 0, s.Hash, 1, "a", true, s, light);
    }

    private static string DiffDump()
    {
        var r = new Random(4242);
        var sb = new StringBuilder();
        for (int i = 0; i < 160; i++)
        {
            int height = i % 4 == 3 ? 256 : 128;
            var a = RandomSnap(r, height);
            var b = i % 17 == 0 ? RandomSnap(r, i % 2 == 0 ? 256 : 128) : Mutate(r, a, r.Next(0, 6), i % 2 == 0);
            var ca = Client(a, r, r.Next(4));
            var cb = Client(b, r, r.Next(4));
            var cur = Client(RandomSnap(r, height), r, 0);
            sb.Append(SectionStore.Diff(a, b, ca.Top, cb.Top)).Append(' ').Append(SectionStore.LightDiff(ca, cb));
            for (int dir = 0; dir < 4; dir++) sb.Append(' ').Append(SectionStore.EdgeDiff(dir, ca, cb, cur));
            sb.Append(' ').Append(SectionStore.EdgeDiff(i % 4, null, cb, cur)).Append(' ').Append(SectionStore.EdgeDiff(i % 4, ca, null, cur)).Append('\n');
        }
        return sb.ToString();
    }

    private static void Put(HiveWorldCache cache, int dim, IEnumerable<(int Cx, int Cz, ChunkSnapshot S, bool Lit)> cols) =>
        cache.Apply(ColumnPayload.Encode(dim, cols.Select(c =>
        {
            if (!c.Lit) return Entry(c.Cx, c.Cz, c.S, 5);
            var light = LightSnapshot.From(c.S.Height, (_, y, _) => y > 20 ? 15 : 0, (x, y, z) => (x * 3 + y + z) % 16);
            return new ColumnPayloadEntry(c.Cx, c.Cz, LightSnapshot.Combine(c.S.Hash, light.Hash), 5, "a", true, c.S.Encode(), light.Encode());
        }).ToList()));

    private static string StoreDump()
    {
        var r = new Random(77);
        var cache = new HiveWorldCache();
        var snaps = new Dictionary<(int, int), ChunkSnapshot>();
        for (int cx = -3; cx <= 3; cx++)
            for (int cz = -3; cz <= 3; cz++)
                if ((cx + cz) % 5 != 0) snaps[(cx, cz)] = RandomSnap(r, 128);
        Put(cache, 0, snaps.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value, (kv.Key.Item1 & 1) == 0)));
        Put(cache, -1, [(0, 0, RandomSnap(r, 128), false), (1, 0, RandomSnap(r, 128), true)]);
        var store = new SectionStore(cache) { Log = [] };
        var sb = new StringBuilder();
        void Step(string name, int dim, int cx, int cz, int radius, int max = int.MaxValue)
        {
            int n = store.Update(dim, cx, cz, radius, max);
            sb.Append("== ").Append(name).Append(" built ").Append(n).Append(" total ").Append(store.Built).Append('\n');
            while (store.Ready.TryDequeue(out var u))
            {
                var vs = new StringBuilder();
                for (int i = 0; i < u.SolidCount; i++) { var v = u.Solid![i]; vs.Append(v.X).Append(v.Y).Append(v.Z).Append(v.U).Append(v.V).Append(v.Col).Append(v.Row).Append(v.Light).Append(v.Flags).Append(v.Rgba); }
                vs.Append('|');
                for (int i = 0; i < u.TranslucentCount; i++) { var v = u.Translucent![i]; vs.Append(v.X).Append(v.Y).Append(v.Z).Append(v.Col).Append(v.Light).Append(v.Rgba); }
                sb.Append("  ").Append(u.Key).Append(" all ").Append(u.DropAll).Append(" col ").Append(u.DropColumn).Append(' ').Append(u.SolidCount).Append('/').Append(u.TranslucentCount)
                    .Append(' ').Append(Hash(vs.ToString())).Append('\n');
                u.Release();
            }
        }
        Step("first, limited", 0, 0, 0, 2, 7);
        Step("rest", 0, 0, 0, 2);
        Step("nothing new", 0, 0, 0, 2);
        // A block edit in the centre, an edge edit next to it, a neighbour that appears and one that vanishes.
        var edited = new List<(int, int, ChunkSnapshot, bool)>
        {
            (1, 1, Mutate(r, snaps[(1, 1)], 3, false), true),
            (1, 0, Mutate(r, snaps[(1, 0)], 4, true), false),
            (0, -1, Mutate(r, snaps[(0, -1)], 2, true), true),
            (0, 0, RandomSnap(r, 128), false),
        };
        Put(cache, 0, edited);
        Put(cache, 0, [(-2, -3, RandomSnap(r, 128), false)]);
        cache.Apply(ColumnPayload.Encode(0, [new ColumnPayloadEntry(-1, 0, 0, 0, null, false, [])]));
        Step("edits", 0, 0, 0, 2);
        Step("moved", 0, 2, 1, 1);
        Step("bigger ring", 0, 2, 1, 3, 20);
        store.Textured = true;
        Step("textured", 0, 2, 1, 1, 12);
        store.Smooth = !store.Smooth;
        Step("smooth", 0, 2, 1, 1, 5);
        store.RequestResend();
        Step("resend", 0, 2, 1, 1, 6);
        Step("nether", -1, 0, 0, 1);
        Step("back", 0, 0, 0, 1, 3);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the GPU atlas and quad cells

    private static string AtlasDump()
    {
        var sb = new StringBuilder();
        var (w0, h0, px0) = GpuAtlas.Build(null);
        sb.Append("null ").Append(w0).Append('x').Append(h0).Append(' ').Append(Hash(px0)).Append('\n');
        var r = new Random(5);
        var terrain = new uint[256 * 256];
        for (int i = 0; i < terrain.Length; i++) terrain[i] = (uint)r.Next() | (i % 5 == 0 ? 0x10000000u : 0xff000000u);
        var items = new uint[256 * 256];
        for (int i = 0; i < items.Length; i++) items[i] = (uint)r.Next() | 0x80000000u;
        foreach (var atlas in new[] { new TextureAtlas(16, terrain), new TextureAtlas(16, terrain, items), new TextureAtlas(16, terrain, new uint[10]),
                     TextureAtlas.FromPng(Png.Encode(terrain, 256, 256), Png.Encode(items, 256, 256), null) })
        {
            var (w, h, px) = GpuAtlas.Build(atlas);
            sb.Append(atlas.Size).Append(' ').Append(w).Append('x').Append(h).Append(' ').Append(Hash(px)).Append('\n');
        }
        var big = new uint[512 * 512];
        for (int i = 0; i < big.Length; i++) big[i] = (uint)(i * 2654435761u) | 0xff000000u;
        var (bw, bh, bpx) = GpuAtlas.Build(new TextureAtlas(32, big));
        sb.Append("32 ").Append(bw).Append('x').Append(bh).Append(' ').Append(Hash(bpx)).Append('\n');
        return sb.ToString();
    }

    private static string CellDump()
    {
        var sb = new StringBuilder();
        foreach (var tex in Enum.GetValues<QuadTex>())
            foreach (int tile in new[] { -1, 0, 1, 3, 17, 19, 20, 255, 300 })
                foreach (int overlay in new[] { -1, BlockTextures.GrassSideOverlay, 5 })
                    foreach (uint tint in new uint[] { 0, 0x7fb238 })
                        foreach (bool textured in new[] { false, true })
                        {
                            var q = new GeoQuad { Tex = tex, Tile = (short)tile, Overlay = (short)overlay, Tint = tint };
                            bool ok = QuadEmit.Cell(q, textured, out var col, out var row, out var t, out var uv, out var flags);
                            sb.Append(tex).Append(' ').Append(tile).Append(' ').Append(overlay).Append(' ').Append(tint).Append(' ').Append(textured).Append(" => ")
                                .Append(ok).Append(' ').Append(col).Append(',').Append(row).Append(' ').Append(t).Append(' ').Append(uv).Append(' ').Append(flags).Append('\n');
                        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the vision panel

    private static readonly DateTimeOffset At0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly BotDto PanelBot = new("maid_chan_67", "67", false, 0, 5.5, 65, 9.5, 180, 10, 0, 20, 20, "mining", null, null, null, false, 0);

    private sealed class Sheets : IVisionSource
    {
        public readonly List<VisionSheetDto> All = [];
        public int Capacity = 4;
        public string? Note;
        public readonly HashSet<long> NoPng = [];

        public Task<VisionListDto> ListAsync(string body, long since, CancellationToken ct) =>
            Task.FromResult(new VisionListDto(At0, body, Capacity, Note, All.Where(s => s.Id > since).OrderByDescending(s => s.Id).ToList()));

        public Task<byte[]> PngAsync(string body, long id, CancellationToken ct) =>
            NoPng.Contains(id) ? Task.FromException<byte[]>(new HiveApiException(404, "vision_png: not found")) : Task.FromResult(new byte[] { (byte)id, 1, 2 });

        public Task<SheetPicture> RenderAsync(string body, string seeJson, CancellationToken ct) =>
            Task.FromResult(new SheetPicture([9, 9], 40, 30, "server legend " + seeJson.Length, 50, "server"));
    }

    private sealed class Composer : IComposeRenderer
    {
        public SheetPicture Render(ComposeSpec spec) => new([7, (byte)spec.Radius, (byte)spec.Views.Count], 30, 20, "local " + spec.SeeCall(), 15, "local");
    }

    private static VisionSheetDto Sheet(long id, string mode, List<string>? views, int[]? focus = null, string? answer = null, string? note = null, string? call = null,
        string? question = null, string? model = null, int radius = 6, string sha = "abcdef0123456789abcdef") =>
        new(id, At0.AddSeconds(id), "maid_chan_67", id % 2 == 0 ? "sheet" : "look", id % 3 == 0 ? "auto" : "see", mode, views, focus, radius, question, call,
            200 + (int)id, 100, 30 + (int)id, 2048 + (int)id * 100, sha, "legend " + id, answer, note, model, model != null ? 1234 : null, model != null ? 56 : null);

    private static string Scrub(string dump) => string.Join("\n", dump.Split('\n').Where(l => !l.TrimStart().StartsWith("ComposeRenders =")));

    private static async Task<string> VisionDump()
    {
        var src = new Sheets();
        var plans = new List<PlanDto> { new(3, "wall", "building", 0, [[2, 64, 2, 4, 0, 0], [9, 66, 5, 4, 0, 1], [4, 63, 11, 4, 0, 1]]), new(4, "empty", "done", 0, []) };
        using var vm = new VisionPanelViewModel(src, "67", "maid_chan_67 · 67", () => PanelBot, new Composer(), () => plans, a => a(), _ => null)
            { Debounce = TimeSpan.FromMilliseconds(5) };
        var sb = new StringBuilder();
        async Task Dump(string name)
        {
            await vm.WhenComposedAsync();
            sb.Append("== ").Append(name).Append('\n').Append(Scrub(StateDump.Of(vm, 3))).Append("\ncopy ").Append(vm.CopyText()).Append('\n');
        }
        await Dump("fresh");
        src.All.Add(Sheet(1, "perception", ["eye", "iso yaw=200 pitch=50 zoom=2", "top dx=3"], [5, 65, 7], question: "what is that?"));
        src.All.Add(Sheet(2, "direct", ["top"], call: """{"views":["top"]}""", model: "claude-opus-5-5"));
        src.All.Add(Sheet(3, "perception", null, [1, 2, 3], answer: "[vision] tree", note: "cached", sha: "abc"));
        await vm.PollOnceAsync();
        await Dump("first poll");
        vm.Selected = vm.History[2];
        await Dump("older selected");
        src.All[0] = src.All[0] with { Answer = "[vision] stone" };
        src.All.Add(Sheet(4, "perception", ["orbit yaw=10 pitch=20 dist=9.5", "high pitch=70", "side zoom=2 dx=1 dy=-2 axis=E", "shoulder dist=5", "layers"], [0, 70, 0], radius: 20));
        src.All.Add(Sheet(5, "direct", [" ", "ISO", "Top zoom=1.5 dz=4"], [8, 8, 8], radius: 1));
        src.NoPng.Add(5);
        await vm.PollOnceAsync();
        await Dump("answer arrives, trimmed to 4");
        vm.Selected = vm.History[0];
        await Dump("newest again");
        src.Note = "the ring is off";
        src.Capacity = 0;
        src.All.Add(Sheet(6, "perception", ["eye"], null, note: "timed out"));
        await vm.PollOnceAsync();
        await Dump("note and default capacity");
        foreach (var row in vm.History.ToList())
        {
            vm.Selected = row;
            vm.ComposeFromSheetCommand.Execute(null);
            await Dump("compose from " + row.Sheet.Id);
            vm.ComposeMode = false;
        }
        vm.ComposeMode = true;
        vm.Target = vm.Targets[2];
        vm.Plan = "#3 wall";
        await Dump("plan target");
        vm.Plan = "#4 empty";
        await Dump("empty plan");
        vm.Target = vm.Targets[1];
        vm.TargetText = "1, 2";
        await Dump("bad block");
        vm.TargetText = "1 2 x";
        await Dump("bad block 2");
        vm.TargetText = "-4, 70, 12";
        (vm.UseEye, vm.UseShoulder, vm.UseIso, vm.UseOrbit, vm.UseHigh, vm.UseTop, vm.UseSide, vm.UseLayers) = (false, false, false, false, false, false, false, false);
        await Dump("no panels");
        (vm.UseEye, vm.UseShoulder, vm.UseIso, vm.UseOrbit, vm.UseHigh, vm.UseTop, vm.UseSide, vm.UseLayers) = (true, true, true, true, true, true, true, true);
        (vm.IsoYaw, vm.IsoPitch, vm.IsoZoom, vm.OrbitYaw, vm.OrbitPitch, vm.OrbitDist, vm.HighYaw, vm.HighPitch) = (225.0000001, 30.123, 2, 315, 41, 7.77, 180, 60);
        (vm.TopZoom, vm.TopDx, vm.TopDz, vm.SideZoom, vm.SideDx, vm.SideDy, vm.SideAxis, vm.ShoulderDist, vm.Radius) = (1.5, -2, 3, 0.5, 1, -1, "N", 3.4, 40);
        await Dump("every panel");
        (vm.UseEye, vm.UseShoulder, vm.UseIso, vm.ShoulderDist) = (false, false, false, 2.25);
        await Dump("side N, layers");
        vm.UseShoulder = true;
        vm.SideAxis = "E";
        vm.Aids = false;
        vm.Target = vm.Targets[0];
        await Dump("side E, bot target");
        vm.ResetCamerasCommand.Execute(null);
        vm.SideAxis = "auto";
        await Dump("reset");
        sb.Append(vm.ComposeInterest()).Append('\n');
        vm.ComposeMode = false;
        sb.Append(vm.ComposeInterest()?.ToString() ?? "null").Append('\n');
        return sb.ToString();
    }

    [Fact]
    public async Task Live_hive_sync_and_state_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>
        {
            ("sync", await SyncDump()),
            ("api", await ApiDump()),
            ("entities", EntityDump()),
            ("reconcile", ReconcileDump()),
            ("section-diff", DiffDump()),
            ("section-store", StoreDump()),
            ("atlas", AtlasDump()),
            ("quad-cell", CellDump()),
        };
        Compare(rows, Golden);
    }

    /// <summary>Compose mode's local renderer over a synced world (columns seen a minute ago; no world time: noon).</summary>
    private static string LocalSheetDump()
    {
        var cache = new HiveWorldCache();
        long seen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        var entries = new List<ColumnPayloadEntry>();
        for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var blocks = new List<(int, int, int, byte)>();
                for (int x = 0; x < 16; x++)
                    for (int z = 0; z < 16; z++)
                        blocks.Add((x, 63, z, (byte)((x + z) % 5 == 0 ? 4 : 2)));
                if (cx == 0 && cz == 0) { blocks.Add((5, 64, 5, 4)); blocks.Add((6, 64, 5, 1)); blocks.Add((6, 65, 5, 1)); }
                entries.Add(Entry(cx, cz, Snap([.. blocks]), seen));
            }
        cache.Apply(ColumnPayload.Encode(0, entries));
        List<BotDto> bots =
        [
            new("maid_chan", "main", true, 0, 2.5, 64, 2.5, 30, 10, 0, 20, 20, null, null, null, null, true, 0),
            new("maid_chan_67", "67", false, 0, -3.5, 64, 6.5, 90, null, 0, 20, 20, null, null, null, null, true, 0),
            new("nether_bot", "68", false, -1, 0, 64, 0, 0, 0, 0, 20, 20, null, null, null, null, true, 0),
            new("far_bot", "69", false, 0, 300, 64, 0, 0, 0, 0, 20, 20, null, null, null, null, true, 0),
        ];
        List<EntityDto> ents =
        [
            new("player", "Fox", 0, 6.5, 64, -2.5, Yaw: 45), new("player", "MAID_CHAN", 0, 1, 64, 1), new("mob", "zombie", 0, 3, 64, 3, Type: 54),
            new("item", "cobblestone", 0, 4.5, 64, 4.5, Item: 4, Count: 12), new("item", "far", 0, 100, 64, 6.5), new("player", "Robin", -1, 0, 64, 0),
            new("arrow", "a", 0, 2, 64, 2),
        ];
        var composer = new LocalSheetComposer(cache, () => null, () => bots, () => ents);
        var specs = new[]
        {
            new ComposeSpec(bots[0], (5, 64, 5), 6, [new SeeView("eye"), new SeeView("iso"), new SeeView("top")]),
            new ComposeSpec(bots[0], (2, 64, 2), 4, [new SeeView("eye")], Aids: false),
            new ComposeSpec(bots[1], (5, 64, 5), 20, [new SeeView("iso", new CamParams(Yaw: 200, Pitch: 50)), new SeeView("side", new CamParams(Axis: "N")), new SeeView("TOP")]),
            new ComposeSpec(bots[1] with { Yaw = null }, (-3, 64, 6), 2, []),
            new ComposeSpec(bots[0], (5, 64, 9), 8, [new SeeView("orbit", new CamParams(Dist: 9)), new SeeView("high"), new SeeView("layers"), new SeeView("shoulder")]),
        };
        var sb = new StringBuilder();
        foreach (var spec in specs)
        {
            var pic = composer.Render(spec);
            sb.Append(spec.SeeCall()).Append(" => ").Append(pic.Width).Append('x').Append(pic.Height).Append(' ').Append(pic.Where).Append(' ').Append(Hash(pic.Png))
                .Append('\n').Append(pic.Legend).Append('\n');
        }
        foreach (var (feet, focus, yaw) in new[] { ((0, 64, 0), (3, 64, 0), 0f), ((0, 64, 0), (0, 64, -5), 0f), ((0, 64, 0), (1, 64, 0), 90f), ((0, 64, 0), (0, 64, 0), 180f), ((0, 64, 0), (0, 64, 1), 45f) })
            sb.Append(LocalSheetComposer.AxisFor(feet, focus, yaw)).Append(' ');
        return sb.ToString();
    }

    [Fact]
    public void Local_sheet_composer_matches_the_golden_hash() => Compare([("local-sheet", LocalSheetDump())], LocalGolden);

    private const string LocalGolden = """
        local-sheet 08A06984EFA5914A6930
        """;

    [Fact]
    public async Task Fast_loop_states_match_the_golden_hash() => Compare([("fast-loop", await FastLoopDump())], FastGolden);

    private const string FastGolden = """
        fast-loop A0EE29CB63B86E43295C
        """;

    [Fact]
    public async Task Vision_panel_matches_the_golden_hash()
    {
        var dump = await VisionDump();
        var name = "vision-panel";
        if (TimeZoneInfo.Local.BaseUtcOffset != TimeSpan.FromMinutes(ZoneMinutes))
        {
            output.WriteLine($"different time zone: {name} {Hash(dump)} not compared");
            return;
        }
        Compare([(name, dump)], VisionGolden);
    }

    private const int ZoneMinutes = -300;

    private const string Golden = """
        sync 2AD09C36B82A2EF315B7
        api 648EE232FEB6AA5A1F82
        entities 27D56F3391A27EE4E7F2
        reconcile E0B3FE6EB1476C00C7DD
        section-diff 87157AF84C5EC705DBEA
        section-store C5FD3DB420269A235EC8
        atlas E94F2A111F3FA16B78BB
        quad-cell D723DB08C77A6CF693AB
        """;

    private const string VisionGolden = """
        vision-panel C3D1924C1B03475D9E21
        """;
}
