using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Perception.Vision;

namespace MineClaw.App.Tests;

/// <summary>The vision routes ("what the AI saw" + on-demand renders): token-gated (403), read-only, and their payload shape.</summary>
public class SupervisorVisionRouteTests
{
    private const string Token = "s3cret-token";

    /// <summary>Two captured sheets of main (the history ring stores the exact PNG), a render that echoes the see input.</summary>
    private sealed class FakeVisionData : IVisionSupervisorData
    {
        public readonly VisionHistory History = new(capacity: 20);
        public string? LastSee;
        public int Calls;

        public FakeVisionData()
        {
            var t = new VisionSheetInfo(0, DateTimeOffset.Parse("2026-10-28T10:00:00Z"), "maid_chan", "sheet", "see", "perception", ["eye", "iso yaw=200 pitch=50", "top"],
                [5, 65, 7], 6, "which one is the middle block?", """{"views":["eye","iso yaw=200 pitch=50","top"],"focus":"5,65,7"}""", 0, 0, 0, 0, "", "see sheet focus 5,65,7 r6, ...");
            var first = History.Add(Png(16, 12), 16, 12, t);
            History.Update(first.Id, i => i with { Answer = "[vision] mine 5,65,7", Model = "claude-sonnet-5-5", ModelInputTokens = 600, ModelOutputTokens = 90 });
            History.Add(Png(20, 10), 20, 10, t with { Mode = "direct", Views = ["top"] });
        }

        /// <summary>A real (tiny) PNG so the size headers come from its IHDR.</summary>
        public static byte[] Png(int w, int h) => MineClaw.Render.Png.Encode(new uint[w * h], w, h);

        private static bool Main(string body) => body is "main" or "maid_chan";

        public Task<VisionListReply?> ListAsync(string body, long since, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Main(body) ? new VisionListReply("maid_chan", History.Capacity, History.List(null, since)) : null);
        }

        public Task<byte[]?> PngAsync(string body, long id, CancellationToken ct) => Task.FromResult(Main(body) ? History.Png(id) : null);

        public Task<ViewImage?> RenderAsync(string body, string seeJson, RenderGate gate, CancellationToken ct)
        {
            LastSee = seeJson;
            if (!Main(body)) return Task.FromResult<ViewImage?>(null);
            var png = Png(32, 24);
            return Task.FromResult<ViewImage?>(new ViewImage(png, 32, 24, 2, "rendered " + seeJson, []));
        }
    }

    private static SupervisorServer Server(FakeVisionData data, string? token = Token)
    {
        var s = new SupervisorServer(new SupervisorOptions { Token = token }, new FakeSupervisorData());
        s.Vision = new SupervisorVisionRoutes(data, token, s.Gate);
        return s;
    }

    private static SupervisorRequest Get(string path, string? token = Token, Dictionary<string, string>? query = null) =>
        new("GET", path, query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), token);

    [Fact]
    public async Task Vision_routes_are_403_without_the_token_and_read_only()
    {
        var data = new FakeVisionData();
        await using var s = Server(data);
        long id = data.History.List()[0].Id;
        foreach (var path in new[] { "/api/bodies/main/vision", $"/api/bodies/main/vision/{id}", $"/api/bodies/main/vision/{id}.png", "/api/bodies/main/vision/render" })
        {
            Assert.Equal(403, (await s.HandleAsync(Get(path, token: null))).Status);
            Assert.Equal(403, (await s.HandleAsync(Get(path, token: "wrong"))).Status);
            Assert.Equal(405, (await s.HandleAsync(new SupervisorRequest("POST", path, new Dictionary<string, string>(), Token, "{}"))).Status);
            Assert.Equal(200, (await s.HandleAsync(Get(path))).Status);
        }
        Assert.Equal(2, data.Calls); // only the two authorised list/info reads reached the data
        Assert.Equal(200, (await s.HandleAsync(Get("/api/bodies/main/vision", token: null, new() { ["token"] = Token }))).Status);
        // Not a vision path: the script debugger's route stays the script routes' business.
        Assert.False(SupervisorVisionRoutes.IsVisionPath("/api/bodies/main/script"));
        Assert.False(SupervisorVisionRoutes.IsVisionPath("/api/bodies/vision"));
        Assert.True(SupervisorVisionRoutes.IsVisionPath("/api/bodies/67/vision/"));
    }

    [Fact]
    public async Task List_info_png_and_render_have_the_documented_shape()
    {
        var data = new FakeVisionData();
        await using var s = Server(data);
        var list = JsonDocument.Parse((await s.HandleAsync(Get("/api/bodies/main/vision"))).Text).RootElement;
        Assert.Equal("maid_chan", list.GetProperty("body").GetString());
        Assert.Equal(20, list.GetProperty("capacity").GetInt32());
        var sheets = list.GetProperty("sheets").EnumerateArray().ToList();
        Assert.Equal(2, sheets.Count);
        var newest = sheets[0];
        var older = sheets[1];
        Assert.True(newest.GetProperty("id").GetInt64() > older.GetProperty("id").GetInt64());   // newest first
        Assert.Equal("direct", newest.GetProperty("mode").GetString());
        Assert.Equal("perception", older.GetProperty("mode").GetString());
        Assert.Equal("[vision] mine 5,65,7", older.GetProperty("answer").GetString());
        Assert.Equal(600, older.GetProperty("model_input_tokens").GetInt64());
        Assert.Equal(["eye", "iso yaw=200 pitch=50", "top"], older.GetProperty("views").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal([5, 65, 7], older.GetProperty("focus").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(16, older.GetProperty("width").GetInt32());
        Assert.Equal(1, older.GetProperty("tokens").GetInt32());
        Assert.Equal(64, older.GetProperty("sha256").GetString()!.Length);
        Assert.Contains("iso yaw=200", older.GetProperty("call").GetString());
        Assert.Equal("which one is the middle block?", older.GetProperty("question").GetString());

        long id = older.GetProperty("id").GetInt64();
        var png = await s.HandleAsync(Get($"/api/bodies/main/vision/{id}.png"));
        Assert.Equal("image/png", png.ContentType);
        Assert.Equal(data.History.Png(id), png.Body);                                           // the exact bytes
        Assert.Equal(older.GetProperty("sha256").GetString(), png.Headers!["X-Sha256"]);
        Assert.Equal("16", png.Headers["X-Width"]);
        var info = JsonDocument.Parse((await s.HandleAsync(Get($"/api/bodies/main/vision/{id}"))).Text).RootElement;
        Assert.Equal(id, info.GetProperty("id").GetInt64());
        Assert.Contains("see sheet focus", info.GetProperty("legend").GetString());

        // ?since= only returns newer sheets (the Explorer's tiles poll that way).
        var since = JsonDocument.Parse((await s.HandleAsync(Get("/api/bodies/main/vision", query: new() { ["since"] = id.ToString() }))).Text).RootElement;
        Assert.Single(since.GetProperty("sheets").EnumerateArray());

        // The render route: the see input JSON in, a PNG (or meta) out; bad JSON is a 400.
        var see = """{"views":["iso yaw=90 pitch=60"],"focus":"5,65,7","radius":6}""";
        var r = await s.HandleAsync(Get("/api/bodies/main/vision/render", query: new() { ["see"] = see }));
        Assert.Equal("image/png", r.ContentType);
        Assert.Equal(see, data.LastSee);
        var meta = JsonDocument.Parse((await s.HandleAsync(Get("/api/bodies/main/vision/render", query: new() { ["see"] = see, ["meta"] = "1" }))).Text).RootElement;
        Assert.Equal(32, meta.GetProperty("width").GetInt32());
        Assert.Equal(400, (await s.HandleAsync(Get("/api/bodies/main/vision/render", query: new() { ["see"] = "{nope" }))).Status);

        // Unknown bodies and sheets: 404.
        Assert.Equal(404, (await s.HandleAsync(Get("/api/bodies/nobody/vision"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/bodies/main/vision/12345.png"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/bodies/main/vision/12345"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/bodies/nobody/vision/render"))).Status);
    }
}
