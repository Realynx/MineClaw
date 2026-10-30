using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Supervisor;
using MineClaw.Bot.WorldIndex;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Tests;

/// <summary>A fake hive for the supervisor endpoint: main + two clones, counting every call that reaches the data.</summary>
internal sealed class FakeSupervisorData : ISupervisorData
{
    public int ViewCalls, HiveCalls, CameraCalls, StatusCalls, LocateCalls;
    /// <summary>The x-ray each view asked for (the Explorer's toggle).</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<MineClaw.Render.XRay> XRays = new();

    public Task<ViewImage?> ViewAsync(string bot, string mode, int radius, MineClaw.Render.XRay xray, RenderGate gate, CancellationToken ct)
    {
        XRays.Enqueue(xray);
        return ViewAsync(bot, mode, radius, gate, ct);
    }
    public readonly SupervisorFeed Feed = new();

    public IReadOnlyList<BotTile> Bots() =>
    [
        new("maid_chan", "main", true, 0, 1.5, 64, 1.5, 20, 18, "idle", "awake", 0.2, []),
        new("maid_chan_67", "67", false, 0, 6.5, 64, 8.5, 17, 12, "mining cobble at the gen", "asleep (script cobble_loop 42%)", 0.8, ["block 6,64,8"]),
        new("maid_chan_69", "69", false, -1, 10.5, 70, -3.5, 9, 4, "chop trees", "awake", 1.4, []),
    ];

    private static ViewImage Img(string legend, int w = 16, int h = 12) =>
        new(Png.Encode(new uint[w * h], w, h), w, h, (w * h + 749) / 750, legend, ["FocusBlock: [vision] exact: stone"], "here live (main)");

    public async Task<ViewImage?> ViewAsync(string bot, string mode, int radius, RenderGate gate, CancellationToken ct)
    {
        Interlocked.Increment(ref ViewCalls);
        var b = Bots().FirstOrDefault(x => x.Name == bot || x.Short == bot);
        return b == null ? null : await gate.RunAsync(() => Img($"{b.Name} {mode} r{radius}"), ct);
    }

    public Task<ViewImage> HiveAsync(RenderGate gate, CancellationToken ct) { Interlocked.Increment(ref HiveCalls); return gate.RunAsync(() => Img("hive"), ct); }

    public Task<ViewImage> CameraAsync(CameraQuery q, RenderGate gate, CancellationToken ct)
    {
        Interlocked.Increment(ref CameraCalls);
        return gate.RunAsync(() => Img($"camera {q.X},{q.Y},{q.Z} dim {q.Dim}", q.W / 10, q.H / 10), ct);
    }

    public object Status(string? selected)
    {
        Interlocked.Increment(ref StatusCalls);
        var snap = new BotSnapshot { Name = "maid_chan", Status = BotStatus.Online, Hp = 20, Food = 18, Chat = ["<Fox> hi"],
            Usage = new UsageSnapshot { TodayUsd = 1.25m, DailyCapUsd = 50 }, Viewport = new ViewportGrid { Radius = 1, Top = ["..."] },
            Thought = new ThoughtSnapshot { Output = "on it", Tools = [new ThoughtToolCall(DateTimeOffset.UtcNow, "see", "{}", "ok")] } };
        return new { main = "maid_chan", bots = new[] { LiveSupervisorData.BotStatusView(snap, selected == "maid_chan") } };
    }

    public IReadOnlyList<FeedEvent> Events(long since, int max) => Feed.Since(since, max);

    public LocateResult? Locate(string query)
    {
        Interlocked.Increment(ref LocateCalls);
        return query == "mob chamber" ? new LocateResult("mob chamber", 0, 20.5, 80, -4.5) : null;
    }
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class SupervisorTests
{
    private static SupervisorRequest Get(string path, string? query = null, string? token = null, string method = "GET")
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query != null)
            foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = kv.IndexOf('=');
                q[i < 0 ? kv : kv[..i]] = i < 0 ? "" : Uri.UnescapeDataString(kv[(i + 1)..]);
            }
        return new SupervisorRequest(method, path, q, token);
    }

    private static (SupervisorServer Server, FakeSupervisorData Data, ManualClock Clock) Make(string? token = null)
    {
        var data = new FakeSupervisorData();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-27T18:00:00Z"));
        var server = new SupervisorServer(new SupervisorOptions { Token = token, MinRenderIntervalMs = 0 }, data, time: clock);
        return (server, data, clock);
    }

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';

    [Fact]
    public async Task Bots_route_returns_one_tile_per_bot_as_snake_case_json()
    {
        var (s, _, _) = Make();
        await using var _s = s;
        var r = await s.HandleAsync(Get("/api/bots"));
        Assert.Equal(200, r.Status);
        Assert.StartsWith("application/json", r.ContentType);
        using var doc = JsonDocument.Parse(r.Body);
        var bots = doc.RootElement.GetProperty("bots");
        Assert.Equal(3, bots.GetArrayLength());
        var c67 = bots.EnumerateArray().Single(b => b.GetProperty("name").GetString() == "maid_chan_67");
        Assert.Equal("67", c67.GetProperty("short").GetString());
        Assert.False(c67.GetProperty("main").GetBoolean());
        Assert.Equal(17, c67.GetProperty("hp").GetInt32());
        Assert.Equal(12, c67.GetProperty("food").GetInt32());
        Assert.Equal("mining cobble at the gen", c67.GetProperty("job").GetString());
        Assert.Equal("block 6,64,8", c67.GetProperty("claims")[0].GetString());
        Assert.True(c67.TryGetProperty("age_seconds", out _));
        Assert.Equal(-1, bots.EnumerateArray().Single(b => b.GetProperty("short").GetString() == "69").GetProperty("dim").GetInt32());
    }

    [Fact]
    public async Task View_routes_serve_pngs_for_every_bot_and_mode_and_reject_unknowns()
    {
        var (s, data, _) = Make();
        await using var _s = s;
        foreach (var bot in new[] { "maid_chan", "maid_chan_67", "69" })
            foreach (var mode in SupervisorServer.Modes)
            {
                var r = await s.HandleAsync(Get("/view/" + bot, "mode=" + mode + "&radius=5"));
                Assert.Equal(200, r.Status);
                Assert.Equal("image/png", r.ContentType);
                Assert.True(IsPng(r.Body));
                Assert.NotNull(r.Headers?["X-Tokens"]);
            }
        Assert.Equal(404, (await s.HandleAsync(Get("/view/maid_chan_420"))).Status); // configured but not online
        Assert.Equal(400, (await s.HandleAsync(Get("/view/maid_chan_67", "mode=xray"))).Status);
        // The Explorer's x-ray toggle reaches the renderer (and is its own cache entry); no parameter = the automatic rule.
        Assert.All(data.XRays, x => Assert.Equal(MineClaw.Render.XRay.Auto, x));
        Assert.Equal(200, (await s.HandleAsync(Get("/view/maid_chan", "mode=iso&xray=on"))).Status);
        Assert.Equal(200, (await s.HandleAsync(Get("/view/maid_chan", "mode=iso&xray=off"))).Status);
        Assert.Equal([MineClaw.Render.XRay.On, MineClaw.Render.XRay.Off], data.XRays.Skip(data.XRays.Count - 2));
        var hive = await s.HandleAsync(Get("/view/hive"));
        Assert.Equal(200, hive.Status);
        Assert.True(IsPng(hive.Body));
        Assert.Equal(404, (await s.HandleAsync(Get("/nope"))).Status);
        var page = await s.HandleAsync(Get("/"));
        Assert.Equal(200, page.Status);
        Assert.Contains("Hive Supervisor", page.Text);
        Assert.True(data.ViewCalls > 0);
    }

    [Fact]
    public async Task Meta_of_the_brain_view_carries_tokens_legend_and_router_facts()
    {
        var (s, _, _) = Make();
        await using var _s = s;
        var r = await s.HandleAsync(Get("/view/maid_chan_67", "mode=brain&meta=1"));
        Assert.Equal(200, r.Status);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.True(doc.RootElement.GetProperty("tokens").GetInt32() > 0);
        Assert.Contains("maid_chan_67", doc.RootElement.GetProperty("legend").GetString());
        Assert.StartsWith("FocusBlock:", doc.RootElement.GetProperty("facts")[0].GetString());
        Assert.True(doc.RootElement.GetProperty("bytes").GetInt32() > 8);
    }

    [Fact]
    public async Task Renders_are_cached_per_bot_and_mode_for_about_a_second()
    {
        var (s, data, clock) = Make();
        await using var _s = s;
        for (int i = 0; i < 5; i++) Assert.Equal(200, (await s.HandleAsync(Get("/view/maid_chan_67", "mode=eye"))).Status);
        Assert.Equal(1, data.ViewCalls);
        // brain and sheet are the same render; meta shares it too
        await s.HandleAsync(Get("/view/maid_chan_67", "mode=sheet"));
        await s.HandleAsync(Get("/view/maid_chan_67", "mode=brain&meta=1"));
        Assert.Equal(2, data.ViewCalls);
        clock.Now += TimeSpan.FromMilliseconds(1100);
        await s.HandleAsync(Get("/view/maid_chan_67", "mode=eye"));
        Assert.Equal(3, data.ViewCalls);
    }

    [Fact]
    public async Task Camera_route_parses_and_clamps_the_pose()
    {
        var (s, data, _) = Make();
        await using var _s = s;
        var r = await s.HandleAsync(Get("/camera", "x=4.5&y=70&z=-2&yaw=90&pitch=200&dim=-1&w=320&h=240&meta=1"));
        Assert.Equal(200, r.Status);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Contains("dim -1", doc.RootElement.GetProperty("legend").GetString());
        Assert.Equal(400, (await s.HandleAsync(Get("/camera", "y=70&z=1"))).Status);
        Assert.Equal(400, (await s.HandleAsync(Get("/camera", "x=NaN&y=70&z=1"))).Status);
        Assert.Equal(1, data.CameraCalls);
        var hit = await s.HandleAsync(Get("/api/locate", "q=mob%20chamber"));
        Assert.Equal(200, hit.Status);
        Assert.Contains("mob chamber", hit.Text);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/locate", "q=narnia"))).Status);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task Everything_but_get_is_rejected_and_never_reaches_the_hive(string method)
    {
        var (s, data, _) = Make();
        await using var _s = s;
        foreach (var path in new[] { "/", "/api/bots", "/api/status", "/api/events", "/api/locate", "/view/maid_chan_67", "/view/hive", "/camera", "/api/chat", "/api/move" })
        {
            var r = await s.HandleAsync(Get(path, "x=1&y=2&z=3&q=mob%20chamber", method: method));
            Assert.Equal(405, r.Status);
            Assert.Equal("GET, HEAD", r.Headers!["Allow"]);
        }
        Assert.Equal(0, data.ViewCalls + data.HiveCalls + data.CameraCalls + data.StatusCalls + data.LocateCalls);
    }

    [Fact]
    public async Task Token_is_required_on_every_data_route_when_configured()
    {
        var (s, data, _) = Make(token: "s3cret-token");
        await using var _s = s;
        foreach (var path in new[] { "/api/bots", "/api/status", "/api/events", "/view/maid_chan_67", "/view/hive", "/camera" })
        {
            Assert.Equal(401, (await s.HandleAsync(Get(path, "x=1&y=70&z=1"))).Status);
            Assert.Equal(401, (await s.HandleAsync(Get(path, "x=1&y=70&z=1", token: "s3cret-tokeN"))).Status);
            Assert.Equal(401, (await s.HandleAsync(Get(path, "x=1&y=70&z=1&token=s3cret"))).Status);
        }
        Assert.Equal(0, data.ViewCalls + data.HiveCalls + data.CameraCalls + data.StatusCalls);
        Assert.Equal(200, (await s.HandleAsync(Get("/api/bots", token: "s3cret-token"))).Status);
        Assert.Equal(200, (await s.HandleAsync(Get("/view/maid_chan_67", "token=s3cret-token"))).Status);
        Assert.Equal(200, (await s.HandleAsync(Get("/"))).Status); // the page itself holds no data
        Assert.DoesNotContain("s3cret", (await s.HandleAsync(Get("/api/bots"))).Text);
    }

    [Fact]
    public async Task Status_mirrors_the_tui_panels_without_the_heavy_volumes_and_events_page_by_id()
    {
        var (s, data, _) = Make();
        await using var _s = s;
        var r = await s.HandleAsync(Get("/api/status", "bot=maid_chan"));
        Assert.Equal(200, r.Status);
        using (var doc = JsonDocument.Parse(r.Body))
        {
            var b = doc.RootElement.GetProperty("bots")[0];
            Assert.Equal(1.25m, b.GetProperty("usage").GetProperty("today_usd").GetDecimal());
            Assert.Equal("on it", b.GetProperty("thought").GetProperty("output").GetString());
            Assert.Equal("see", b.GetProperty("thought").GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal("<Fox> hi", b.GetProperty("chat")[0].GetString());
            Assert.False(b.TryGetProperty("viewport", out _));
            Assert.False(b.TryGetProperty("voxels", out _));
        }
        data.Feed.Add("danger", "maid_chan: VoidBelow: void below", "warn");
        data.Feed.Add("hive", "[hive] 67 logged out");
        var ev = await s.HandleAsync(Get("/api/events", "since=0"));
        using (var doc = JsonDocument.Parse(ev.Body))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("events").GetArrayLength());
            long last = doc.RootElement.GetProperty("last").GetInt64();
            data.Feed.Add("hive", "[hive] 69 lost");
            using var next = JsonDocument.Parse((await s.HandleAsync(Get("/api/events", "since=" + last))).Body);
            var only = Assert.Single(next.RootElement.GetProperty("events").EnumerateArray());
            Assert.Equal("[hive] 69 lost", only.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Render_gate_runs_work_one_at_a_time_on_its_background_thread_and_refuses_overflow()
    {
        using var gate = new RenderGate(minIntervalMs: 0, maxQueued: 2);
        int running = 0, peak = 0;
        var release = new ManualResetEventSlim();
        int Work() { int n = Interlocked.Increment(ref running); peak = Math.Max(peak, n); release.Wait(2000); Interlocked.Decrement(ref running); return Environment.CurrentManagedThreadId; }
        var a = gate.RunAsync(Work);
        await Task.Delay(100); // a is running, the queue is empty
        var b = gate.RunAsync(Work);
        var c = gate.RunAsync(Work);
        var d = gate.RunAsync(Work); // queue (2) full
        await Assert.ThrowsAsync<RenderBusyException>(() => d);
        release.Set();
        var ids = await Task.WhenAll(a, b, c);
        Assert.Equal(1, peak);
        Assert.Single(ids.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, ids[0]);
    }

    [Fact]
    public async Task Listener_serves_get_over_http_and_rejects_post()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var logs = new List<string>();
        await using var s = new SupervisorServer(new SupervisorOptions { Bind = "localhost", Port = port, Token = "tok-123", MinRenderIntervalMs = 0 },
            new FakeSupervisorData(), new ListLogger(logs));
        s.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/bots")).StatusCode);
        var ok = await http.GetAsync("api/bots?token=tok-123");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("maid_chan_67", await ok.Content.ReadAsStringAsync());
        var png = await http.GetAsync("view/67?mode=eye&token=tok-123");
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync("api/bots?token=tok-123", new StringContent("{}"))).StatusCode);
        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("tok-123"));
    }

    private sealed class ListLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add(formatter(state, exception) + (exception?.ToString() ?? ""));
        }
    }
}

/// <summary>The free camera over a db fixture: remembered <c>world_chunks</c> of an island seen by clone 67.</summary>
public class SupervisorCameraTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-supervisor-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private static ChunkRow Island(int cx, int cz, DateTimeOffset seen)
    {
        var w = new WorldState();
        var ids = new byte[16 * 128 * 16];
        var meta = new byte[16 * 128 * 16];
        for (int x = 2; x < 14; x++)
            for (int z = 2; z < 14; z++)
            {
                for (int y = 58; y < 63; y++) ids[(x * 16 + z) * 128 + y] = MineClaw.Data.Blocks.Stone;
                ids[(x * 16 + z) * 128 + 63] = MineClaw.Data.Blocks.Grass;
            }
        w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, ids, meta, null, null);
        var data = ChunkSnapshot.Capture(w.GetColumn(cx, cz)!).Encode();
        return new ChunkRow(0, cx, cz, 1, "island", data, seen) { SeenBy = "maid_chan_67" };
    }

    [Fact]
    public void Camera_renders_any_spot_from_the_shared_world_memory_with_staleness()
    {
        Directory.CreateDirectory(_dir);
        using var db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        var store = new SqliteWorldMemoryStore(db, "maid_chan_67");
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-27T18:00:00Z"));
        store.PutChunks([Island(0, 0, clock.Now - TimeSpan.FromMinutes(12))]);
        var cam = new CameraRenderer(() => null, "maid_chan", () => store, () => null, clock, distance: 32, maxWidth: 400, maxHeight: 300);

        // Above the island looking down: grass/stone fill the middle of the frame.
        var island = cam.Render(new CameraQuery(8, 75, 8, 0, 80, 0, 160, 120));
        var (w, h, px) = Png.Decode(island.Png);
        Assert.Equal((160, 120), (w, h));
        Assert.Contains("seen by 67 12 min ago", island.Seen);
        Assert.Contains("free camera @8,75,8 overworld", island.Legend);

        // Same height over empty memory (nothing remembered there): sky/void only, and it says so.
        var empty = cam.Render(new CameraQuery(400, 75, 400, 0, 80, 0, 160, 120));
        var (_, _, epx) = Png.Decode(empty.Png);
        Assert.Contains("never seen", empty.Seen);
        int centre = h / 2 * w + w / 2;
        Assert.NotEqual(epx[centre] & 0xffffff, px[centre] & 0xffffff);

        // Frames are clamped to the configured maximum.
        var big = cam.Render(new CameraQuery(8, 75, 8, 0, 80, 0, 4000, 4000));
        Assert.Equal((400, 300), (big.Width, big.Height));
    }

    [Fact]
    public void Flying_inside_the_region_reuses_it_instead_of_rereading_the_db()
    {
        Directory.CreateDirectory(_dir);
        using var db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        var store = new SqliteWorldMemoryStore(db, "maid_chan_67");
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-27T18:00:00Z"));
        store.PutChunks([Island(0, 0, clock.Now)]);
        var cam = new CameraRenderer(() => null, "maid_chan", () => store, () => null, clock, distance: 48);
        for (int i = 0; i < 6; i++) cam.Render(new CameraQuery(8 + i, 70, 8, i * 10, 30, 0, 96, 72));
        Assert.Equal(1, cam.Builds);
        cam.Render(new CameraQuery(8, 70, 8, 0, 30, -1, 96, 72)); // another dimension
        Assert.Equal(2, cam.Builds);
        clock.Now += TimeSpan.FromSeconds(30);                    // stale region: rebuilt for fresh columns
        cam.Render(new CameraQuery(8, 70, 8, 0, 30, -1, 96, 72));
        Assert.Equal(3, cam.Builds);
    }
}
