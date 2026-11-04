using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the <c>see</c> tool (WP03 refactor of VisionHub.SeeAsync / SheetRequest) byte for byte: the text, the stub and the
/// PNG's hash for every input shape in direct mode (views, legacy view, radius, aids, camera params, composites, eye, bad
/// views, the budget fallback), and the image + prompt the perception model gets in perception mode.
/// </summary>
public class SeeGoldenTests
{
    private sealed class PerceptionLlm : ILlmClient
    {
        public string Json { get; set; } = """{"answer":"mine 5,65,7","facts":[{"claim":"gen cell","coords":[[5,65,7]],"role":"gen cell","confidence":0.9},{"claim":"floor","coords":[[5,64,7]],"role":"floor","confidence":0.8}]}""";
        public List<LlmRequest> Perception { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            if (request.Purpose == "perception")
            {
                lock (Perception) Perception.Add(request);
                return Task.FromResult(new LlmResponse([new LlmText(Json)], StopReasons.EndTurn, new LlmUsage(600, 90, 0, 0), request.Model ?? "claude-sonnet-5-5"));
            }
            return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, request.Model ?? "claude-haiku-5-5"));
        }
    }

    private static (BotHost Host, PerceptionLlm Llm) Bot(string mode, int imagesPerHour = 1000)
    {
        var o = TestBot.Options();
        o.Llm.ApiKey = "sk-ant-test";
        o.Llm.Perception.Mode = mode;
        var bot = new BotOptions();
        bot.Vision.Textures = false;
        bot.Vision.ImagesPerHour = imagesPerHour;
        var c = new FakeGameClient();
        var llm = new PerceptionLlm();
        var host = BotHost.Create(c, o, bot, llm, persist: false);
        for (int x = -2; x <= 9; x++) for (int z = -2; z <= 11; z++) c.World.SetBlock(x, 64, z, Blocks.Cobblestone, 0);
        c.World.SetBlock(4, 65, 7, Blocks.Water, 0);
        c.World.SetBlock(6, 65, 7, Blocks.Lava, 0);
        c.World.SetBlock(5, 65, 7, Blocks.Cobblestone, 0);
        c.World.SetBlock(2, 65, 3, Blocks.Log, 0);
        c.World.SetBlock(2, 66, 3, Blocks.Log, 0);
        c.World.SetBlock(8, 65, 1, Blocks.Chest, 0);
        c.World.Player.ServerTeleport(new Vec3d(5.5, 65, 9.5), 66.62, 180, 0, true);
        return (host, llm);
    }

    private static readonly object[] Inputs =
    [
        new { views = new[] { "top", "layers" }, focus = "5,65,7", question = "gen cell?" },
        new { view = "top", radius = 8 },
        new { view = "side" },
        new { views = new[] { "eye" }, focus = "5,65,7" },
        new { views = new[] { "eye", "iso" }, focus = "here" },
        new { views = new[] { "iso" }, focus = "5,65,7", radius = 20, aids = false },
        new { views = new[] { "iso yaw=200 pitch=50 zoom=1.3" }, focus = "5,65,7" },
        new { views = new[] { "iso yaw=0", "iso yaw=90" }, focus = "5,65,7" },
        new { views = new[] { "top", "high", "shoulder", "orbit" }, focus = "2,65,3", radius = 4 },
        new { views = new[] { "xray" } },
        new { views = new[] { "top" }, focus = "nowhere at all" },
        new { focus = "5,65,7" },
        new { },
    ];

    private static string Line(Agent.Tools.ToolResult r) =>
        $"{r.Ok} | {r.Text} | {(r.Image is { } im ? $"{im.Width}x{im.Height} {im.Stub} {Convert.ToHexString(SHA256.HashData(im.Data))[..16]}" : "-")}";

    [Fact]
    public async Task Direct_mode_see_is_byte_identical()
    {
        var (h, _) = Bot("direct");
        var sb = new StringBuilder();
        foreach (var input in Inputs) sb.Append(Line(await h.Run("see", input))).Append('\n');
        var (budget, _) = Bot("direct", imagesPerHour: 0);
        sb.Append(Line(await budget.Run("see", new { views = new[] { "top" }, focus = "5,65,7" }))).Append('\n');
        Check(sb.ToString(), "see-direct.txt", ExpectedDirect);
    }

    [Fact]
    public async Task Perception_mode_see_sends_the_same_image_and_prompt()
    {
        var (h, llm) = Bot("auto");
        var sb = new StringBuilder();
        foreach (var input in Inputs) sb.Append(Line(await h.Run("see", input))).Append('\n');
        llm.Json = "not json at all";
        sb.Append(Line(await h.Run("see", new { question = "which one is the middle block of the gen?", focus = "5,65,7" }))).Append('\n');
        foreach (var req in llm.Perception)
            foreach (var m in req.Messages)
                foreach (var block in m.Content)
                    sb.Append(block switch
                    {
                        LlmImageBlock img => "img " + Convert.ToHexString(SHA256.HashData(img.Image.Data))[..16],
                        LlmText t => "text " + t.Text,
                        _ => block.GetType().Name,
                    }).Append('\n');
        Check(sb.ToString(), "see-perception.txt", ExpectedPerception);
    }

    [Fact]
    public void Sheets_for_a_clone_pose_a_viewer_and_camera_params_are_byte_identical()
    {
        var (h, _) = Bot("direct");
        var hub = h.Vision!.Hub!;
        var focus = new BlockPos(5, 65, 7);
        var sb = new StringBuilder();
        void Add(Render.VisionImage img) =>
            sb.Append($"{img.Width}x{img.Height} {Convert.ToHexString(SHA256.HashData(img.Png))[..16]} {img.Legend} | {string.Join("; ", img.Labels)}\n");
        foreach (float yaw in new[] { 0f, 45f, 90f, 135f, 200f, 300f })
            Add(hub.Sheet(focus, 6, ["top", "side"], pose: new HiveEyePose("bot_67", 0, 3.5, 65, 4.5, yaw, 20, DateTimeOffset.UnixEpoch)));
        Add(hub.Sheet(focus, 2, null));
        Add(hub.Sheet(focus, 30, ["eye", " TOP "], aids: false));
        Add(hub.Sheet(new BlockPos(5, 65, 9), 6, ["eye"]));
        Add(hub.Sheet(focus, 6, ["iso", "eye"], viewParams: new Dictionary<string, Render.CamParams> { ["ISO"] = new(Yaw: 120, Zoom: 1.5) }));
        var fox = h.Client.World.Entities.SpawnPlayer(901, "Fox", new Vec3d(8.5, 65, 10.5), 135, 15);
        Add(hub.Sheet(focus, 6, ["eye"], viewer: fox));
        var req = hub.SheetRequest(focus, 6, ["layers"], xray: Render.XRay.Auto);
        sb.Append($"{req.Axis} {req.Self} {req.Target} {req.Pointed} {req.PointedBy} {req.Stamp} {req.XRay} {string.Join(",", req.Views)}\n");
        Check(sb.ToString(), "see-sheets.txt", ExpectedSheets);
    }

    private static void Check(string dump, string file, string expected)
    {
        dump = dump.Replace("\r\n", "\n");
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, file), dump);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string ExpectedDirect = "742BF7D6730E9845EBAE136D";
    private const string ExpectedPerception = "D4D6B8BC3A89FCBED764117F";
    private const string ExpectedSheets = "46B32783F3A0E926B91A81FE";
}
