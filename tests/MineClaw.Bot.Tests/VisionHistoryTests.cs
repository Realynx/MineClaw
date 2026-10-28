using System.Security.Cryptography;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// "What the AI saw": every sheet a model gets is kept (the exact bytes the LLM request serializes, a bounded ring), and the
/// camera of every non-eye view can be adjusted (iso/orbit/high yaw, pitch, zoom, distance; top/side zoom, pan, axis) by the
/// Explorer's compose mode and by the brain's <c>see</c>, with the defaults unchanged.
/// </summary>
public class VisionHistoryTests
{
    /// <summary>Answers perception calls; everything else ends the turn quietly.</summary>
    private sealed class PerceptionLlm : ILlmClient
    {
        public List<LlmRequest> Perception { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            if (request.Purpose == "perception")
            {
                lock (Perception) Perception.Add(request);
                return Task.FromResult(new LlmResponse([new LlmText(
                    """{"answer":"mine 5,65,7","facts":[{"claim":"gen cell","coords":[[5,65,7]],"role":"gen cell","confidence":0.9}]}""")],
                    StopReasons.EndTurn, new LlmUsage(600, 90, 0, 0), request.Model ?? "claude-sonnet-5-5"));
            }
            return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, request.Model ?? "claude-haiku-5-5"));
        }
    }

    private static (BotHost Host, FakeGameClient Client, PerceptionLlm Llm) Bot(bool direct, Action<BotOptions>? bot = null)
    {
        var o = TestBot.Options();
        o.Llm.ApiKey = "sk-ant-test";
        o.Llm.Perception.Auto.Enabled = false;
        if (direct) o.Llm.Perception.Mode = "direct";
        var b = new BotOptions();
        b.Vision.Textures = false;
        bot?.Invoke(b);
        var c = new FakeGameClient();
        var llm = new PerceptionLlm();
        var host = BotHost.Create(c, o, b, llm, persist: false);
        for (int x = -2; x <= 9; x++) for (int z = -2; z <= 11; z++) c.World.SetBlock(x, 64, z, Blocks.Cobblestone, 0);
        c.World.SetBlock(4, 65, 7, Blocks.Water, 0);
        c.World.SetBlock(6, 65, 7, Blocks.Lava, 0);
        c.World.SetBlock(5, 65, 7, Blocks.Cobblestone, 0);
        c.World.Player.ServerTeleport(new Vec3d(5.5, 65, 9.5), 66.62, 180, 0, true);
        return (host, c, llm);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>The PNGs inside the JSON the Anthropic client would POST (base64 "data" of each image block).</summary>
    private static List<byte[]> SerializedImages(LlmRequest req)
    {
        var o = new LlmOptions { ApiKey = "sk-test-not-real" };
        var json = JsonSerializer.Serialize(new AnthropicLlmClient(o).BuildParams(req, req.Model ?? o.MainModel));
        var found = new List<byte[]>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "base64" && e.TryGetProperty("data", out var d))
                    found.Add(Convert.FromBase64String(d.GetString()!));
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
        }
        Walk(JsonDocument.Parse(json).RootElement);
        return found;
    }

    // ------------------------------------------------------------------ capture: the bytes the model got

    [Fact]
    public async Task Direct_sheet_capture_is_the_exact_bytes_the_brain_request_carries()
    {
        var (h, _, _) = Bot(direct: true);
        var hub = h.Vision!.Hub!;
        var r = await h.Run("see", new { views = new[] { "eye", "iso yaw=90 pitch=50", "top zoom=2" }, focus = "5,65,7", question = "gen cell?" });
        Assert.NotNull(r.Image);
        // What the brain's next request carries: the tool result (BotAgent.Execution), serialized by the Anthropic client.
        var req = new LlmRequest
        {
            Tier = ModelTier.Main,
            Messages =
            [
                LlmMessage.User("look"),
                LlmMessage.Assistant(new LlmToolUse("t1", "see", JsonSerializer.SerializeToElement(new { focus = "5,65,7" }))),
                LlmMessage.User(new LlmToolResult("t1", r.Text) { Image = r.Image }),
            ],
        };
        // (the SDK's params may carry a block twice, e.g. typed + raw JSON: one distinct image either way)
        var sent = Assert.Single(SerializedImages(req).DistinctBy(Sha));
        var rec = Assert.Single(hub.History.List());
        Assert.Equal(Sha(sent), rec.Sha256);
        Assert.Equal(Sha(hub.History.Png(rec.Id)!), rec.Sha256);
        Assert.Same(r.Image!.Data, hub.History.Png(rec.Id));             // the request's own array: no copy, no re-render
        Assert.Equal(("sheet", "see", "direct"), (rec.Kind, rec.Source, rec.Mode));
        Assert.Equal(["eye", "iso yaw=90 pitch=50", "top zoom=2"], rec.Views);
        Assert.Equal([5, 65, 7], rec.Focus!);
        Assert.Equal("gen cell?", rec.Question);
        Assert.Equal(r.Image.Tokens, rec.Tokens);
        Assert.Equal(sent.Length, rec.Bytes);
        Assert.Contains("\"iso yaw=90 pitch=50\"", rec.Call);
        Assert.Contains("ISO from E looking W (yaw 90), 50 deg down", rec.Legend);
    }

    [Fact]
    public async Task Perception_capture_is_the_exact_bytes_the_perception_request_carries_and_keeps_the_answer()
    {
        var (h, _, llm) = Bot(direct: false);
        var hub = h.Vision!.Hub!;
        var r = await h.Run("see", new { question = "which one is the middle block of the gen?", focus = "5,65,7", views = new[] { "orbit yaw=10", "top" } });
        Assert.True(r.Ok, r.Text);
        Assert.Null(r.Image);                                          // the brain gets text
        var sent = Assert.Single(SerializedImages(llm.Perception.Single()).DistinctBy(Sha));
        var rec = Assert.Single(hub.History.List());
        Assert.Equal(Sha(sent), rec.Sha256);
        Assert.Equal("perception", rec.Mode);
        Assert.StartsWith("[vision]", rec.Answer);
        Assert.Equal(600, rec.ModelInputTokens);
        // The same question again: the cache answers; the sheet is still recorded, marked "not sent".
        await h.Run("see", new { question = "which one is the middle block of the gen?", focus = "5,65,7", views = new[] { "orbit yaw=10", "top" } });
        Assert.Single(llm.Perception);
        Assert.Contains("cached", hub.History.List()[0].Note);
    }

    [Fact]
    public void Ring_buffer_keeps_the_last_n_and_the_disk_ring_its_own_bound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mc-vision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var h = new VisionHistory(capacity: 3, diskDir: dir, diskKeep: 4);
            var t = new VisionSheetInfo(0, DateTimeOffset.UtcNow, "maid_chan", "sheet", "see", "direct", ["top"], [0, 64, 0], 6, null, null, 0, 0, 0, 0, "", "legend");
            var ids = new List<long>();
            for (int i = 0; i < 6; i++) ids.Add(h.Add([(byte)i, 1, 2, 3], 10, 10, t).Id);
            Assert.Equal(3, h.Count);
            Assert.Equal(ids.TakeLast(3).Reverse(), h.List().Select(x => x.Id));   // newest first
            Assert.Equal(4, Directory.GetFiles(dir, "*.png").Length);              // the disk ring is bounded too
            Assert.Equal(4, Directory.GetFiles(dir, "*.json").Length);
            Assert.NotNull(h.Png(ids[2]));                                          // fell out of memory, still on disk
            Assert.Null(h.Png(ids[0]));                                             // fell out of both
            Assert.Empty(h.List(since: ids[^1]));
            Assert.Single(h.List(since: ids[^2]));
            Assert.Empty(h.List(body: "someone_else"));
            Assert.Equal("ok", h.Update(ids[^1], x => x with { Answer = "ok" })!.Answer);
            var mem = new VisionHistory(capacity: 2);
            for (int i = 0; i < 50; i++) mem.Add([1], 1, 1, t);
            Assert.Equal(2, mem.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    // ------------------------------------------------------------------ view parameters

    [Fact]
    public void See_views_parse_camera_parameters_and_round_trip_through_copy_as_see()
    {
        var p = VisionHub.ParseString("iso yaw=200 pitch=50 zoom=1.3")!;
        Assert.Equal("iso", p.Kind);
        Assert.Null(p.At);
        Assert.Equal(new CamParams(Yaw: 200, Pitch: 50, Zoom: 1.3), p.Cam);
        Assert.False(p.Composite);
        var cam = VisionHub.ParseString("cam tower NE 14 pitch=60")!;
        Assert.Equal(("cam", "tower", "NE", 14.0, 60.0), (cam.Kind, cam.At, cam.From, cam.Dist!.Value, cam.Cam.Pitch!.Value));
        var side = VisionHub.ParseString("side axis=E dx=2 zoom=2")!;
        Assert.Equal(new CamParams(Zoom: 2, Dx: 2, Axis: "E"), side.Cam);
        Assert.Equal(VisionHub.ParseString("orbit gen 3")!, VisionHub.ParseString("orbit gen 3")! with { Cam = default }); // old forms unchanged
        Assert.True(VisionHub.ParseString("iso")!.Cam.IsEmpty);

        var call = SeeCall.Json([new SeeView("eye"), new SeeView("iso", new CamParams(Yaw: 200, Pitch: 50)), new SeeView("top", new CamParams(Zoom: 2, Dx: 3))], "4,64,6", 6);
        Assert.Equal("""{"views":["eye","iso yaw=200 pitch=50","top zoom=2 dx=3"],"focus":"4,64,6","radius":6}""", call);
        var back = VisionHub.ParseViews(JsonDocument.Parse(call).RootElement, out var err)!;
        Assert.Null(err);
        Assert.Equal(["eye", "iso yaw=200 pitch=50", "top zoom=2 dx=3"], back.Select(x => x.Format()));
        // The object form takes the same keys.
        var obj = VisionHub.ParseViews(JsonDocument.Parse("""{"views":[{"iso":{"yaw":200,"pitch":50}},{"top":{"zoom":2}}]}""").RootElement, out _)!;
        Assert.Equal(["iso yaw=200 pitch=50", "top zoom=2"], obj.Select(x => x.Format()));
    }

    [Fact]
    public void Empty_parameters_keep_every_default_camera()
    {
        var f = (5, 64, 7);
        var body = new PerspectivePanel.Body(5.5, 65, 9.5, 180, 10, "me");
        foreach (var v in PerspectivePanel.Views.Where(v => v != "eye"))
            Assert.Equal(PerspectivePanel.For(v, f, 64, 6, body, null), PerspectivePanel.For(v, f, 64, 6, body, null, default(CamParams)));
        Assert.Equal(PerspectivePanel.Top(f, 64, 6), PerspectivePanel.Top(f, 64, 6, default));
        Assert.Equal(PerspectivePanel.Side(f, 64, 6, SideAxis.LookEast), PerspectivePanel.Side(f, 64, 6, SideAxis.LookEast, default));
        Assert.Equal(PerspectivePanel.Cam(f, 64, "NE", 14, "CAM", 6), PerspectivePanel.Cam(f, 64, "NE", 14, "CAM", 6, default));
        // The params path at the defaults lands on the same camera (to rounding), so a slider at its default changes nothing visible.
        foreach (var v in new[] { "orbit", "orbit2", "orbit3", "orbit4", "high", "iso" })
        {
            var d = PerspectivePanel.OrbitDefaults(v)!.Value;
            var a = PerspectivePanel.For(v, f, 64, 6, null, null)!.Value;
            var b = PerspectivePanel.For(v, f, 64, 6, null, null, new CamParams(Yaw: d.Yaw, Pitch: d.Pitch))!.Value;
            Assert.True(Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 && Math.Abs(a.Z - b.Z) < 1e-6, $"{v}: {a} vs {b}");
            double dyaw = Math.Abs(CamParams.NormYaw(a.Yaw) - CamParams.NormYaw(b.Yaw));
            Assert.True(Math.Min(dyaw, 360 - dyaw) < 1e-3 && Math.Abs(a.Pitch - b.Pitch) < 1e-3, $"{v}: {a.Yaw} vs {b.Yaw}");
        }
        // A whole sheet with no params and with an empty map: the same bytes.
        var c = VisionEval.FloorVsGen();
        var q = new ContactSheetRequest { Blocks = c.World.PlanningView, Focus = c.Focus, Radius = c.Radius, Self = c.Self, Light = new WorldVoxels(c.World), Views = ["iso", "top", "orbit"] };
        var sheets = new ContactSheet { EyeThreads = 2 };
        Assert.Equal(Sha(sheets.Render(q).Png), Sha(sheets.Render(new ContactSheetRequest
        {
            Blocks = q.Blocks, Focus = q.Focus, Radius = q.Radius, Self = q.Self, Light = q.Light, Views = q.Views,
            ViewParams = new Dictionary<string, CamParams>(StringComparer.OrdinalIgnoreCase) { ["iso"] = default },
        }).Png));
    }

    /// <summary>Where a block shows in a panel: the centroid of the pixels whose cell is that block (normalised 0..1).</summary>
    private static (double X, double Y)? Where(PerspectivePanel.Rig rig, IVoxelWorld world, (int X, int Y, int Z) block)
    {
        var eye = new EyeRenderer { Width = 320, Height = 240, MaxThreads = 2 };
        var fr = eye.Raster(new EyeScene
        {
            World = world, EyeX = rig.X, EyeY = rig.Y, EyeZ = rig.Z, Yaw = rig.Yaw, Pitch = rig.Pitch, Fov = rig.Fov, MaxDistance = rig.MaxDistance,
            Ortho = rig.Ortho, Clip = rig.Clip,
        });
        long key = EyeRenderer.CellKey(block.X, block.Y, block.Z);
        double sx = 0, sy = 0;
        int n = 0;
        for (int y = 0; y < fr.Height; y++)
            for (int x = 0; x < fr.Width; x++)
                if (fr.Cells![y * fr.Width + x] == key) { sx += x; sy += y; n++; }
        return n < 4 ? null : (sx / n / fr.Width, sy / n / fr.Height);
    }

    [Fact]
    public void Iso_yaw_and_pitch_move_the_projection_on_a_fixture_island()
    {
        // A flat cobble island with a gold block 4 east of the focus and a diamond block 4 north of it.
        var w = new WorldState();
        w.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        for (int x = -8; x <= 8; x++) for (int z = -8; z <= 8; z++) w.SetBlock(x, 63, z, Blocks.Cobblestone, 0);
        w.SetBlock(4, 64, 0, Blocks.GoldBlock, 0);
        w.SetBlock(0, 64, -4, Blocks.DiamondBlock, 0);
        var vox = new BlockAccessVoxels(w.PlanningView);
        var f = (0, 64, 0);
        var gold = (4, 64, 0);
        var diamond = (0, 64, -4);
        PerspectivePanel.Rig Iso(double yaw, double pitch, double zoom = 1) => PerspectivePanel.For("iso", f, 64, 6, null, null, new CamParams(Yaw: yaw, Pitch: pitch, Zoom: zoom))!.Value;

        // Facing north (yaw 180): east is on the right; facing south (yaw 0): on the left.
        var north = Where(Iso(180, 35), vox, gold)!.Value;
        Assert.True(north.X > 0.6, $"facing N, gold at x {north.X:0.00}");
        var south = Where(Iso(0, 35), vox, gold)!.Value;
        Assert.True(south.X < 0.4, $"facing S, gold at x {south.X:0.00}");
        // Facing east (yaw 270): gold straight ahead, in the middle column.
        var east = Where(Iso(270, 35), vox, gold)!.Value;
        Assert.InRange(east.X, 0.35, 0.65);
        // Facing north the diamond (farther away, north) sits above the centre; from overhead it moves up the frame.
        var low = Where(Iso(180, 20), vox, diamond)!.Value;
        var high = Where(Iso(180, 80), vox, diamond)!.Value;
        Assert.True(low.Y < 0.5, $"pitch 20: diamond at y {low.Y:0.00}");
        Assert.True(high.Y < low.Y, $"pitch 80 y {high.Y:0.00} vs pitch 20 y {low.Y:0.00}");
        // Zoom brings it closer: the block's offset from the centre grows.
        var zoomed = Where(Iso(180, 35, zoom: 2), vox, gold)!.Value;
        Assert.True(zoomed.X - 0.5 > north.X - 0.5, $"zoom 2: {zoomed.X:0.00} vs {north.X:0.00}");
        // The default iso (from the SW, facing NE): gold (east) right of centre, diamond (north) left of it.
        var def = PerspectivePanel.For("iso", f, 64, 6, null, null)!.Value;
        Assert.True(Where(def, vox, gold)!.Value.X > 0.5);
        Assert.True(Where(def, vox, diamond)!.Value.X < 0.5);
        // TOP pans with dx: a pan east moves the gold block left in the frame.
        var top = Where(PerspectivePanel.Top(f, 64, 6), vox, gold)!.Value;
        var panned = Where(PerspectivePanel.Top(f, 64, 6, new CamParams(Dx: 3)), vox, gold)!.Value;
        Assert.True(panned.X < top.X - 0.1, $"top pan: {panned.X:0.00} vs {top.X:0.00}");
    }

    [Fact]
    public async Task Copy_as_see_renders_the_same_sheet_again()
    {
        var (h, _, _) = Bot(direct: true);
        var hub = h.Vision!.Hub!;
        var call = SeeCall.Json([new SeeView("iso", new CamParams(Yaw: 90, Pitch: 50)), new SeeView("top", new CamParams(Zoom: 1.5, Dz: -2)), new SeeView("orbit")], "5,65,7", 6);
        var r = await h.Run("see", JsonDocument.Parse(call).RootElement);
        Assert.NotNull(r.Image);
        var rec = hub.History.List()[0];
        Assert.Equal(call, rec.Call);
        var (img, error) = await hub.RenderSeeAsync(JsonDocument.Parse(rec.Call!).RootElement, new ContactSheet { EyeThreads = 1 }, CancellationToken.None);
        Assert.Null(error);
        Assert.Equal(rec.Sha256, Sha(img!.Png));
        // The same view twice at two angles: one panel each (the composite path).
        var two = await h.Run("see", new { views = new[] { "iso yaw=0", "iso yaw=180" }, focus = "5,65,7" });
        Assert.NotNull(two.Image);
        Assert.Equal("composite", hub.History.List()[0].Kind);
        Assert.Contains("ISO", two.Text);
    }
}
