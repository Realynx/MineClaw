using System.Net;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Perception;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Vision v2: the contact sheet (several views of one focus), the role layer, the swappable perception tier (Claude default,
/// Azure OpenAI / OpenAI-compatible / Ollama by config), the voxel verifier, auto-vision and the offline eval over today's
/// live failures (floor vs gen cell, faces in a hole, edge distance, the middle one, the portal on a void nether platform).
/// </summary>
public class VisionV2Tests(ITestOutputHelper output)
{
    private static string RepoFile(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return Path.Combine([d!.FullName, .. parts]);
    }

    private static string NetherFixture() => File.ReadAllText(RepoFile("tests", "fixtures", "portal-trip", "nether.txt"));

    // ------------------------------------------------------------------ roles

    [Fact]
    public void Roles_separate_gen_cells_from_the_floor_and_mark_void_fluids_and_overrides()
    {
        var c = VisionEval.FloorVsGen();
        var roles = VisionEval.Roles(c);
        Assert.Equal(BlockRole.GenCell, roles.RoleOf(5, 63, 7));   // the cobble between water and lava: MINE HERE
        Assert.Equal(BlockRole.Floor, roles.RoleOf(5, 62, 7));     // the island floor right under it: not a gen cell
        Assert.Equal(BlockRole.Floor, roles.RoleOf(8, 62, 7));
        Assert.Equal(BlockRole.Water, roles.RoleOf(4, 63, 7));
        Assert.Equal(BlockRole.Lava, roles.RoleOf(6, 63, 7));
        Assert.Equal(BlockRole.Void, roles.RoleOf(20, 63, 20));    // nothing below: void
        Assert.Equal(BlockRole.None, roles.RoleOf(5, 63, 9));      // air over the floor

        // Protection verdicts, own placements and the index reuse the policy's sources.
        var w = c.World;
        var custom = new RoleClassifier
        {
            Blocks = w.PlanningView,
            Verdict = (p, _) => p == new BlockPos(0, 62, 0) ? new ProtectionVerdict(ProtectionLevel.Hard, "never_break", "marked")
                : p == new BlockPos(1, 62, 0) ? new ProtectionVerdict(ProtectionLevel.Soft, "structure", "the house") : ProtectionVerdict.None,
            IsOwnPlaced = (p, _) => p == new BlockPos(2, 62, 0),
            IndexerKind = p => p == new BlockPos(3, 62, 0) ? MineClaw.Bot.WorldIndex.IndexKinds.UnknownStructure : null,
        };
        Assert.Equal(BlockRole.NeverBreak, custom.RoleOf(0, 62, 0));
        Assert.Equal(BlockRole.SoftProtected, custom.RoleOf(1, 62, 0));
        Assert.Equal(BlockRole.OwnPlaced, custom.RoleOf(2, 62, 0));
        Assert.Equal(BlockRole.PlayerBuild, custom.RoleOf(3, 62, 0));

        // The nether portal sheet and its obsidian frame.
        var portal = VisionEval.PortalVoidNether(NetherFixture())!;
        var pr = VisionEval.Roles(portal);
        Assert.Equal(BlockRole.Portal, pr.RoleOf(2, 66, 0));
        Assert.Equal(BlockRole.Portal, pr.RoleOf(2, 65, 0));       // obsidian under the sheet
        Assert.Equal(BlockRole.Void, pr.RoleOf(3, 66, 0));         // east of the portal: void
        Assert.Equal(BlockRole.Floor, pr.RoleOf(1, 65, 0));        // the glowstone platform top
    }

    [Fact]
    public void Ascii_map_shows_role_letters_and_their_legend()
    {
        var c = VisionEval.FloorVsGen();
        var text = AsciiMap.Render(c.World.PlanningView, 5, 63, 7, 9, (5, 63, 9), null, null, VisionEval.Roles(c).Map);
        var rows = text.Split('\n');
        string Row(int z) => rows[2 + z - 3][4..13]; // z 3..11, x 1..9
        Assert.Equal('G', Row(7)[5 - 1]);                          // x=5: the gen cell
        Assert.Equal('~', Row(7)[4 - 1]);
        Assert.Equal('!', Row(7)[6 - 1]);
        Assert.Equal('.', Row(9)[8 - 1]);                          // plain floor stays '.'
        Assert.Contains("roles: G gen cell=MINE HERE", text);
        // Without roles the map is unchanged (the old cheap look map).
        Assert.DoesNotContain("roles:", AsciiMap.Render(c.World.PlanningView, 5, 63, 7, 9, (5, 63, 9)));
    }

    // ------------------------------------------------------------------ contact sheet + tokens

    [Fact]
    public void Contact_sheet_is_one_image_of_the_same_focus_near_300_400_tokens()
    {
        var c = VisionEval.HoleFaces();
        var sheet = VisionEval.Sheet(c);
        Assert.InRange(sheet.Tokens, 250, 400);
        Assert.Contains("focus 7,62,9", sheet.Legend);
        // The 3D default: orbits from opposite corners, a high oblique, the shoulder view (eye/shoulder fall back to more orbits
        // without a body pose) and the flat TOP map that carries the coordinates.
        foreach (var name in new[] { "SHOULDER", "ORBIT from NW looking SE", "ORBIT from SE looking NW", "HIGH from S", "TOP (straight down" }) Assert.Contains(name, sheet.Legend);
        Assert.Contains("Cyan box = focus in every panel", sheet.Legend);
        // Every panel draws the focus: cyan pixels in each of the 3×2 cells.
        var (w, h, px) = Png.Decode(sheet.Png);
        var (_, _, pw, ph) = ContactSheet.Layout(6);
        int cyan(int x0, int y0, int x1, int y1)
        {
            int n = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if ((px[y * w + x] & 0xffffff) == 0x5fe8ff) n++;
            return n;
        }
        for (int i = 0; i < 6; i++)
        {
            int ox = i % 3 * (pw + 4), oy = i / 3 * (ph + 4);
            Assert.True(cyan(ox, oy, ox + pw, oy + ph) > 4, $"panel {i} draws the focus");
        }

        // The old way: the same four views as separate default pictures (≈200 tokens each) + one image overhead each.
        var r = new VisionRenderer();
        var sep = new[] { VisionView.Top, VisionView.Side, VisionView.Iso, VisionView.Layers }.Select(v => r.Render(new VisionScene
        {
            View = v, Radius = 12, Cx = 7, Cy = 62, Cz = 9, Blocks = c.World.PlanningView, Self = c.Self,
        })).ToList();
        int separate = ContactSheet.SeparateTokens(sep);
        // What a caller really pays: image tokens + the legend text that rides with each picture (≈ chars / 3.5).
        int sheetTotal = sheet.Tokens + (int)(sheet.Legend.Length / 3.5), sepTotal = separate + sep.Sum(s => (int)(s.Legend.Length / 3.5));
        output.WriteLine($"sheet {sheet.Width}x{sheet.Height} ~{sheet.Tokens} img tok (+legend = {sheetTotal}) vs separate " +
            $"{string.Join("+", sep.Select(s => s.Tokens))} = {separate} img tok (+legends = {sepTotal})");
        Assert.True(sheet.Tokens < separate, $"sheet {sheet.Tokens} vs separate {separate}");
        Assert.True(sheetTotal * 10 < sepTotal * 7, $"sheet {sheetTotal} vs separate {sepTotal}");

        // Five panels (with a first-person one) stay in budget too.
        var five = new ContactSheet().Render(new ContactSheetRequest
        {
            Blocks = c.World.PlanningView, Focus = c.Focus, Radius = 6, Self = c.Self, Views = ["top", "side", "iso", "layers", "eye"],
            Eye = new EyeScene { World = new WorldVoxels(c.World), EyeX = 7.5, EyeY = 63.62, EyeZ = 9.5, Yaw = 0, Pitch = 40 },
            Roles = VisionEval.Roles(c).Map,
        });
        output.WriteLine($"5-panel sheet {five.Width}x{five.Height} ~{five.Tokens} tok");
        Assert.InRange(five.Tokens, 250, 420);
        Assert.Contains("EYE", five.Legend);
    }

    [Fact]
    public void Layers_panel_marks_placeable_air_and_the_side_cut_follows_the_work_axis()
    {
        var c = VisionEval.HoleFaces();
        var r = new VisionRenderer();
        var layers = r.Render(new VisionScene { View = VisionView.Layers, Radius = 4, Cx = 7, Cy = 62, Cz = 9, Blocks = c.World.PlanningView, Focus = (7, 62, 9) });
        // A layer is the 3D renderer clipped to one y, seen from straight above (placeable faces come from the router's text).
        Assert.Contains("only y62, from above", layers.Legend);
        var east = r.Render(new VisionScene { View = VisionView.Side, Axis = SideAxis.LookEast, Radius = 6, Cx = 7, Cy = 62, Cz = 9, Blocks = c.World.PlanningView });
        Assert.Contains("looking E through x7, N left", east.Legend);
        var north = r.Render(new VisionScene { View = VisionView.Side, Radius = 6, Cx = 7, Cy = 62, Cz = 9, Blocks = c.World.PlanningView });
        Assert.Contains("looking N through z9", north.Legend);
        // Roles change the picture (and its cache key) even when the block ids don't.
        var plain = r.Render(new VisionScene { Radius = 6, Cx = 5, Cy = 63, Cz = 7, Blocks = VisionEval.FloorVsGen().World.PlanningView });
        var withRoles = r.Render(new VisionScene { Radius = 6, Cx = 5, Cy = 63, Cz = 7, Blocks = VisionEval.FloorVsGen().World.PlanningView, Roles = VisionEval.Roles(VisionEval.FloorVsGen()).Map });
        Assert.NotEqual(plain.Key, withRoles.Key);
        Assert.Contains("Roles: G gen cell=MINE HERE", withRoles.Legend);
    }

    // ------------------------------------------------------------------ perception JSON + verifier

    [Fact]
    public void Perception_answers_parse_leniently()
    {
        var (facts, answer, error) = PerceptionPrompt.Parse("""
            ```json
            {"answer":"mine 5,63,7","facts":[{"claim":"gen cell","coords":[[5,63,7]],"role":"gen_cell","confidence":0.9},
              {"claim":"floor","coords":[5,62,7],"role":null,"confidence":"0.8"},{"claim":"pool","coords":["4, 63, 7"]},
              {"claim":"x","coords":[{"x":1,"y":2,"z":3}]}]}
            ```
            """);
        Assert.Null(error);
        Assert.Equal("mine 5,63,7", answer);
        Assert.Equal(4, facts.Count);
        Assert.Equal((5, 63, 7), facts[0].Coords[0]);
        Assert.Equal("gen_cell", facts[0].Role);
        Assert.Equal((5, 62, 7), facts[1].Coords[0]);
        Assert.Null(facts[1].Role);
        Assert.Equal(0.8, facts[1].Confidence, 3);
        Assert.Equal((4, 63, 7), facts[2].Coords[0]);
        Assert.Equal((1, 2, 3), facts[3].Coords[0]);
        Assert.NotNull(PerceptionPrompt.Parse("sorry, I can't see").Error);
    }

    private static PerceptionResult Result(string json) => PerceptionPrompt.Result(json, new LlmUsage(500, 80, 0, 0), "claude-sonnet-5-5", "anthropic", 900);

    [Fact]
    public void Verifier_keeps_true_claims_fixes_off_by_one_drops_hallucinations_and_counts_them()
    {
        var c = VisionEval.FloorVsGen();
        var roles = VisionEval.Roles(c).Map;
        var r = Result("""
            {"answer":"mine the gen cell at 5,62,7","facts":[
              {"claim":"gen cell between water and lava","coords":[[5,62,7]],"role":"gen cell","confidence":0.9},
              {"claim":"water source of the gen","coords":[[4,63,7]],"role":"water","confidence":0.9},
              {"claim":"floor under the gen is not a gen cell","coords":[[5,62,7]],"role":"floor","confidence":0.8},
              {"claim":"second gen cell","coords":[[0,63,0]],"role":"gen cell","confidence":0.6},
              {"claim":"the gen looks well built","coords":[],"confidence":0.3}]}
            """);
        var v = PerceptionVerifier.Verify(c.Question, r, c.World.PlanningView, roles, c.Focus, c.Radius);
        Assert.Contains(v.Facts, f => f.Status == FactStatus.Corrected && f.Coords.SequenceEqual([(5, 63, 7)])); // y misread: floor → the cell above
        Assert.Contains(v.Facts, f => f.Status == FactStatus.Verified && f.Coords.SequenceEqual([(4, 63, 7)]));
        Assert.Contains(v.Facts, f => f.Status == FactStatus.Verified && f.Claim.StartsWith("floor under"));
        Assert.DoesNotContain(v.Facts, f => f.Coords.Contains((0, 63, 0)));                 // air over the floor is no gen cell
        Assert.Equal(2, v.Dropped);                                                          // + the vague low-confidence one
        Assert.Null(v.Answer);                       // the model made a refuted claim: its free-text answer isn't passed on
        Assert.Equal(2, v.Mismatched);
        Assert.Equal(4, v.Checked);
        Assert.Equal(0.5, v.HallucinationRate, 3);
        Assert.StartsWith("[vision] seen: gen cell between water and lava @5,63,7 (coords fixed)", v.Text);
        // Without a refuted claim the snapped answer survives, rewritten with the fixed coords.
        var snapOnly = Result("""{"answer":"mine the gen cell at 5,62,7","facts":[{"claim":"gen cell","coords":[[5,62,7]],"role":"gen cell","confidence":0.9}]}""");
        Assert.Equal("mine the gen cell at 5,63,7", PerceptionVerifier.Verify(c.Question, snapOnly, c.World.PlanningView, roles, c.Focus, c.Radius).Answer);
        Assert.True(v.Text.Length <= PerceptionVerifier.MaxChars);
        Assert.True(v.Text.Length / 3.5 <= 120, $"~{v.Text.Length / 3.5:0} tokens: {v.Text}");

        // An answer naming a coordinate no kept fact supports is dropped (never handed to the brain).
        var bad = Result("""{"answer":"mine 9,63,9","facts":[{"claim":"gen cell","coords":[[5,63,7]],"role":"gen cell","confidence":0.9}]}""");
        var vb = PerceptionVerifier.Verify(c.Question, bad, c.World.PlanningView, roles, c.Focus, c.Radius);
        Assert.Null(vb.Answer);
        Assert.DoesNotContain("9,63,9", vb.Text);
        Assert.Equal(1, vb.Mismatched);
    }

    [Fact]
    public void Verifier_checks_air_faces_and_the_nether_void()
    {
        var hole = VisionEval.HoleFaces();
        var r = Result("""
            {"answer":"place against 8,62,9 or 7,62,8","facts":[
              {"claim":"solid face to place against","coords":[[8,62,9],[7,62,8],[6,62,9]],"role":"floor","confidence":0.8},
              {"claim":"hole west of you (air)","coords":[[6,62,9]],"role":null,"confidence":0.8}]}
            """);
        var v = PerceptionVerifier.Verify(hole.Question, r, hole.World.PlanningView, VisionEval.Roles(hole).Map, hole.Focus, hole.Radius);
        var faces = v.Facts.First(f => f.Claim.StartsWith("solid face"));     // partly refuted: rewritten from the voxels
        Assert.Equal([(8, 62, 9), (7, 62, 8)], faces.Coords.ToArray()); // the air cell is not a face
        Assert.Contains(v.Facts, f => f.Claim.StartsWith("hole") && f.Status == FactStatus.Verified);
        // Deterministic grounding for an air focus (the live 7,62,9 hole): its solid faces, never the dug cell west of it.
        var g = PerceptionService.Grounding(hole.World.PlanningView, VisionEval.Roles(hole).Map, hole.Focus, hole.Radius, []);
        Assert.Equal("faces: E 8,62,9, S 7,62,10, N 7,62,8, below 7,61,9", g);

        var portal = VisionEval.PortalVoidNether(NetherFixture())!;
        var p = Result("""
            {"answer":"portal at x=2; void east of it","facts":[
              {"claim":"portal sheet","coords":[[2,66,0],[2,67,1]],"role":"portal","confidence":0.9},
              {"claim":"void east of the portal","coords":[[3,66,0]],"role":"void","confidence":0.9},
              {"claim":"glowstone floor east of the portal","coords":[[3,65,0]],"role":"floor","confidence":0.7}]}
            """);
        var vp = PerceptionVerifier.Verify(portal.Question, p, portal.World.PlanningView, VisionEval.Roles(portal).Map, portal.Focus, portal.Radius);
        Assert.Contains(vp.Facts, f => f.Claim == "portal sheet" && f.Status == FactStatus.Verified);
        Assert.Contains(vp.Facts, f => f.Claim.StartsWith("void") && f.Status == FactStatus.Verified);
        Assert.DoesNotContain(vp.Facts, f => f.Claim.StartsWith("glowstone"));                 // there is no floor east of it
        Assert.True(portal.Grade(vp.ModelText), vp.ModelText);
    }

    // ------------------------------------------------------------------ providers (swap by config)

    private sealed class FakeHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            var (code, text) = answer(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly PerceptionQuery Q = new([0x89, 0x50, 0x4e, 0x47], 480, 420, "where is the gen cell?", "legend text");

    [Fact]
    public async Task Azure_openai_sends_an_image_url_data_uri_and_parses_json_facts()
    {
        var h = new FakeHandler((_, _) => (HttpStatusCode.OK, """
            {"model":"gpt-6-astra","choices":[{"message":{"role":"assistant","content":"{\"answer\":\"5,63,7\",\"facts\":[{\"claim\":\"gen cell\",\"coords\":[[5,63,7]],\"role\":\"gen cell\",\"confidence\":0.9}]}"}}],
             "usage":{"prompt_tokens":812,"completion_tokens":64}}
            """));
        var llm = new LlmOptions();
        llm.Perception.Provider = "azure-openai";
        llm.Perception.Model = "gpt-6-astra";
        llm.Perception.Endpoint = "https://mineclaw-vision.openai.azure.com/";
        llm.Perception.Deployment = "astra-vision";
        llm.Perception.ApiKey = "test-key";
        var (model, problem) = PerceptionModelFactory.Create(llm, new NoLlm(), new HttpClient(h));
        Assert.Null(problem);
        Assert.IsType<OpenAIChatPerceptionModel>(model);
        var r = await model!.AskAsync(Q, CancellationToken.None);
        Assert.True(r.Ok, r.Error);
        Assert.Equal((5, 63, 7), r.Facts.Single().Coords[0]);
        Assert.Equal(new LlmUsage(812, 64, 0, 0), r.Usage);
        Assert.Equal("azure-openai", r.Provider);
        var (req, body) = h.Seen.Single();
        Assert.Equal("https://mineclaw-vision.openai.azure.com/openai/deployments/astra-vision/chat/completions?api-version=2024-10-21", req.RequestUri!.ToString());
        Assert.Equal("test-key", req.Headers.GetValues("api-key").Single());
        using var doc = JsonDocument.Parse(body);
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("image_url", user[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", user[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Contains("question: where is the gen cell?", user[0].GetProperty("text").GetString());
        Assert.Equal("json_object", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("model", out _)); // the deployment URL names the model

        // HTTP failure: an error result, never an exception.
        var bad = new OpenAIChatPerceptionModel(llm.Perception, new HttpClient(new FakeHandler((_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"no\"}"))), azure: true);
        var e = await bad.AskAsync(Q, CancellationToken.None);
        Assert.False(e.Ok);
        Assert.Contains("HTTP 401", e.Error);
    }

    [Fact]
    public async Task OpenAI_compatible_and_ollama_sit_behind_the_same_interface()
    {
        var oh = new FakeHandler((_, _) => (HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"answer\":\"x\",\"facts\":[]}"}}],"usage":{"prompt_tokens":1,"completion_tokens":2}}"""));
        var llm = new LlmOptions();
        llm.Perception.Provider = "openai";
        llm.Perception.Model = "some-vision-model";
        llm.Perception.Endpoint = "https://api.example.com";
        llm.Perception.ApiKey = "k";
        var (om, _) = PerceptionModelFactory.Create(llm, new NoLlm(), new HttpClient(oh));
        Assert.True((await om!.AskAsync(Q, CancellationToken.None)).Ok);
        Assert.Equal("https://api.example.com/v1/chat/completions", oh.Seen[0].Req.RequestUri!.ToString());
        Assert.Equal("Bearer", oh.Seen[0].Req.Headers.Authorization!.Scheme);
        Assert.Contains("\"model\":\"some-vision-model\"", oh.Seen[0].Body);

        var lh = new FakeHandler((_, _) => (HttpStatusCode.OK, """{"message":{"content":"{\"answer\":\"y\",\"facts\":[{\"claim\":\"c\",\"coords\":[[1,2,3]]}]}"},"prompt_eval_count":700,"eval_count":40}"""));
        var lo = new LlmOptions();
        lo.Ollama.Endpoint = "http://localhost:11434";
        lo.Perception.Provider = "ollama";
        lo.Perception.Model = "qwen3-vl";
        var (lm, lp) = PerceptionModelFactory.Create(lo, new NoLlm(), new HttpClient(lh));
        Assert.Null(lp);
        var lr = await lm!.AskAsync(Q, CancellationToken.None);
        Assert.Equal(new LlmUsage(700, 40, 0, 0), lr.Usage);
        Assert.Equal("http://localhost:11434/api/chat", lh.Seen[0].Req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(lh.Seen[0].Body);
        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("messages")[1].GetProperty("images").GetArrayLength());

        // Config problems: missing endpoint/key, direct mode, unknown provider, Anthropic without a key.
        var m = new LlmOptions();
        m.Perception.Provider = "azure-openai";
        Assert.Contains("Endpoint is empty", PerceptionModelFactory.Create(m, new NoLlm()).Problem);
        m.Perception.Provider = "anthropic";
        Assert.Equal("no Anthropic API key", PerceptionModelFactory.Create(m, new NoLlm()).Problem);
        m.ApiKey = "sk-ant-test";
        Assert.IsType<AnthropicPerceptionModel>(PerceptionModelFactory.Create(m, new NoLlm()).Model);
        m.Perception.Mode = "direct";
        Assert.Null(PerceptionModelFactory.Create(m, new NoLlm()).Model);
        m.Perception.Mode = "perception";
        m.Perception.Provider = "gemini-magic";
        Assert.Contains("not a provider", PerceptionModelFactory.Create(m, new NoLlm()).Problem);
    }

    /// <summary>Answers perception calls with <see cref="Json"/>; everything else ends the turn quietly.</summary>
    private sealed class PerceptionLlm : ILlmClient
    {
        public string Json { get; set; } = """{"answer":"gen cell 5,63,7","facts":[{"claim":"gen cell","coords":[[5,63,7]],"role":"gen cell","confidence":0.9}]}""";
        public List<LlmRequest> Perception { get; } = new();
        public int Other;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            if (request.Purpose == "perception")
            {
                lock (Perception) Perception.Add(request);
                return Task.FromResult(new LlmResponse([new LlmText(Json)], StopReasons.EndTurn, new LlmUsage(600, 90, 0, 0), request.Model ?? "claude-sonnet-5-5"));
            }
            Interlocked.Increment(ref Other);
            return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, request.Model ?? "claude-haiku-5-5"));
        }
    }

    [Fact]
    public async Task Anthropic_perception_is_claude_to_claude_cached_by_view_and_booked_as_its_own_tier()
    {
        var llm = new PerceptionLlm();
        var o = new PerceptionOptions();
        var usage = new UsageTracker(new BudgetOptions(), new PriceTable(), path: null);
        var svc = new PerceptionService(new AnthropicPerceptionModel(llm, o), o, usage);
        var c = VisionEval.FloorVsGen();
        var sheet = VisionEval.Sheet(c);
        var look = new PerceptionLook(sheet, "which block is the gen cell?", c.World.PlanningView, VisionEval.Roles(c).Map, c.Focus, c.Radius);
        var a = await svc.AskAsync(look, CancellationToken.None);
        Assert.Null(a.Error);
        Assert.Contains("gen cell 5,63,7", a.Text);
        var req = llm.Perception.Single();
        Assert.Equal("claude-sonnet-5-5", req.Model);
        Assert.Equal("off", req.Thinking);
        Assert.IsType<LlmImageBlock>(req.Messages[0].Content[0]);
        Assert.Contains("question: which block is the gen cell?", ((LlmText)req.Messages[0].Content[1]).Text);
        Assert.Contains("ONLY this JSON", req.System[0].Text);
        Assert.Equal(250, req.MaxTokens);                               // terse schema, capped output

        // Same view + question (whitespace/case aside): the cache answers, no second call.
        var again = await svc.AskAsync(look with { Question = "Which block is the gen cell" }, CancellationToken.None);
        Assert.True(again.Cached);
        Assert.Single(llm.Perception);
        Assert.Equal(1, svc.Telemetry.CacheHits);
        // The ledger: perception is its own tier, never "worker" (Sonnet's usual tier).
        var tiers = usage.Today.Tiers;
        Assert.True(tiers.ContainsKey(UsageTracker.PerceptionLabel));
        Assert.Equal(1, tiers[UsageTracker.PerceptionLabel].Calls);
        Assert.False(tiers.ContainsKey("worker"));
        Assert.Contains(UsageTracker.PerceptionLabel, UsageTracker.TierLabels);
        Assert.Contains("perception: 1 calls", svc.Telemetry.Summary());
    }

    // ------------------------------------------------------------------ the see tool + auto-vision in a bot

    private static (BotHost Host, FakeGameClient Client, PerceptionLlm Llm) PerceptionBot(Action<Agent.AgentOptions>? configure = null)
    {
        var o = TestBot.Options();
        o.Llm.ApiKey = "sk-ant-test"; // Claude to Claude: the shared client
        o.Llm.Perception.Auto.WorkerWaitMs = 2000;
        configure?.Invoke(o);
        var bot = new BotOptions();
        bot.Vision.Textures = false;
        var c = new FakeGameClient();
        var llm = new PerceptionLlm();
        var host = BotHost.Create(c, o, bot, llm, persist: false);
        // A floor with a gen at 5,64,7 (lava east, water west), the bot at 5,65,9.
        for (int x = -2; x <= 9; x++) for (int z = -2; z <= 11; z++) c.World.SetBlock(x, 64, z, Blocks.Cobblestone, 0);
        c.World.SetBlock(4, 65, 7, Blocks.Water, 0);
        c.World.SetBlock(6, 65, 7, Blocks.Lava, 0);
        c.World.SetBlock(5, 65, 7, Blocks.Cobblestone, 0);
        c.World.Player.ServerTeleport(new Vec3d(5.5, 65, 9.5), 66.62, 180, 0, true);
        llm.Json = """{"answer":"mine 5,65,7","facts":[{"claim":"gen cell","coords":[[5,65,7]],"role":"gen cell","confidence":0.9},{"claim":"floor","coords":[[5,64,7]],"role":"floor","confidence":0.8}]}""";
        return (host, c, llm);
    }

    [Fact]
    public async Task See_with_perception_returns_verified_text_never_the_image()
    {
        var (h, _, llm) = PerceptionBot();
        Assert.True(h.Vision!.Perception.Available);
        // A deterministic question is answered from the voxels: no model call.
        var exact = await h.Run("see", new { question = "which block is the gen cell?", focus = "5,65,7" });
        Assert.StartsWith("[vision] exact: gen cells (G, mine only these): 5,65,7 water W lava E", exact.Text);
        Assert.Empty(llm.Perception);
        // A relational one goes to the perception model and comes back as verified text.
        var r = await h.Run("see", new { question = "which one is the middle block of the gen?", focus = "5,65,7" });
        Assert.True(r.Ok, r.Text);
        Assert.Null(r.Image);
        Assert.StartsWith("[vision] mine 5,65,7. seen: gen cell", r.Text);
        Assert.Contains("floor @5,64,7", r.Text);
        Assert.True(r.Text.Length / 3.5 < 130, r.Text);
        var sent = (LlmImageBlock)llm.Perception.Single().Messages[0].Content[0];
        Assert.InRange(sent.Image.Tokens, 250, 400);
        // The legacy single view still returns the old picture; look map carries role letters.
        var legacy = await h.Run("see", new { view = "top", radius = 8 });
        Assert.NotNull(legacy.Image);
        var map = await h.Run("look", new { map = true });
        Assert.Contains("G gen cell=MINE HERE", map.Text);
        // A bad view name is refused.
        Assert.False((await h.Run("see", new { views = new[] { "xray" } })).Ok);
    }

    [Fact]
    public async Task Direct_mode_returns_the_contact_sheet_to_the_caller()
    {
        var (h, _, llm) = PerceptionBot(o => o.Llm.Perception.Mode = "direct");
        Assert.False(h.Vision!.Perception.Available);
        var r = await h.Run("see", new { views = new[] { "top", "layers" }, focus = "5,65,7", question = "gen cell?" });
        Assert.NotNull(r.Image);
        Assert.Contains("see sheet focus 5,65,7", r.Text);
        Assert.StartsWith("[picture omitted: see sheet r6 at 5,65,7]", r.Image!.Stub);
        Assert.InRange(r.Image.Tokens, 100, 400);
        Assert.Empty(llm.Perception);
    }

    [Fact]
    public async Task Auto_vision_attaches_one_answer_per_job_step_and_skips_unchanged_spots()
    {
        var (h, c, llm) = PerceptionBot();
        var auto = h.Vision!.Auto!;
        Assert.NotNull(auto);
        Assert.Equal("gen", auto.Classify("harvest_generator", TestBot.J(new { at = "5,65,7", count = 4 }))!.Value.Kind);
        Assert.Equal("gen", auto.Classify("mine", TestBot.J(new { block = "cobblestone" }))!.Value.Kind);
        Assert.Null(auto.Classify("mine", TestBot.J(new { block = "log" })));
        Assert.Null(auto.Classify("inventory", TestBot.J(new { })));
        var build = auto.Classify("skill", TestBot.J(new { name = "fill_floor", args = new { at = "1,64,1" } }))!.Value;
        Assert.True(build.Build);
        Assert.Equal(new BlockPos(1, 64, 1), build.Focus);

        // A non-spatial tool: nothing. A dig (through the registry, as brain/worker/script would run it): one look, answered
        // exactly from the voxels (what is at the focus, what's under it): no model call.
        await h.Run("look", new { map = true });
        await auto.Idle();
        Assert.Null(h.Runtime.Agent.TurnPerception!());
        await h.RunAs(Agent.Llm.ModelTier.Worker, "dig", new { at = "5,65,7" });
        await auto.Idle();
        Assert.Empty(llm.Perception);
        // The brain's next turn gets it once (TurnPerception composes with the pointing line); it never waits for it.
        var line = h.Runtime.Agent.TurnPerception!();
        Assert.Contains("[vision] exact: 5,65,7 cobblestone, gen cell (MINE HERE); under 5,64,7 cobblestone | void under: no", line);
        Assert.DoesNotContain("[vision]", h.Runtime.Agent.TurnPerception!() ?? "");
        // The same step again over an unchanged spot: skipped.
        await h.RunAs(Agent.Llm.ModelTier.Worker, "dig", new { at = "5,65,7" });
        await auto.Idle();
        Assert.True(auto.SkippedUnchanged >= 1);
        // The cell was mined (the spot changed): the next gen step looks again (gen cells, exact) and the worker's round gets it.
        c.World.SetBlock(5, 65, 7, 0, 0);
        auto.OnToolStarted("harvest_generator", TestBot.J(new { at = "5,65,7", count = 1 }));
        var note = await auto.ForWorkerAsync(CancellationToken.None);
        Assert.StartsWith("[vision] exact: gen cells (G, mine only these): 5,65,7 (air: refilling) water W lava E", note);
        Assert.Empty(llm.Perception);
        Assert.Same(auto, h.Runtime.Worker!.RoundNote!.Target);
        Assert.StartsWith("perception: 0 calls (0 auto", h.Vision.Perception.Telemetry.Summary());
        Assert.Contains("2 exact", h.Vision.Perception.Telemetry.Summary());
    }

    [Fact]
    public async Task Builds_end_with_a_before_after_perception_check()
    {
        var (h, c, llm) = PerceptionBot();
        var auto = h.Vision!.Auto!;
        llm.Json = """{"answer":"floor patched, 1 gap at 2,64,2","facts":[{"claim":"gap in the floor","coords":[[2,64,2]],"role":null,"confidence":0.8}]}""";
        c.World.SetBlock(2, 64, 2, 0, 0);
        c.World.SetBlock(1, 64, 1, 0, 0);
        var job = TestBot.J(new { name = "fill_floor", args = new { at = "1,64,1" } });
        auto.OnToolStarted("skill", job);
        // The build changed nothing: the after-check is skipped (no call).
        Assert.Null(await auto.ForWorkerAsync(CancellationToken.None));
        Assert.Empty(llm.Perception);
        auto.OnToolStarted("skill", job);
        c.World.SetBlock(1, 64, 1, Blocks.Cobblestone, 0); // the build placed a block
        var check = await auto.ForWorkerAsync(CancellationToken.None);
        Assert.StartsWith("[vision check] floor patched, 1 gap at 2,64,2", check);
        Assert.Contains("Check the fill floor", ((LlmText)llm.Perception.Single().Messages[0].Content[1]).Text);
        // A brain activity ending a build runs the same check in the background for its next turn.
        auto.OnToolStarted("skill", job);
        c.World.SetBlock(2, 64, 2, Blocks.Cobblestone, 0);
        llm.Json = """{"answer":"floor complete, no gaps","facts":[{"claim":"patched floor block","coords":[[2,64,2]],"role":"floor","confidence":0.9}]}""";
        auto.OnStepFinished("skill");
        await auto.Idle();
        Assert.StartsWith("[vision check] floor complete, no gaps", auto.TakeReady());
    }

    /// <summary>
    /// Live 10-27: clone 69 dug 5,64,6, a cell that came from perception output. Only voxel-verified coordinates may reach a
    /// brain or worker: a refuted or unchecked coordinate never appears in the round note (not even marked uncertain), and
    /// harvest digs only real generator cells (taught / indexer / geometry).
    /// </summary>
    [Fact]
    public async Task A_wrong_perception_gen_cell_never_reaches_the_worker_or_the_dig()
    {
        var (h, c, llm) = PerceptionBot();
        var auto = h.Vision!.Auto!;
        // The model names a wrong gen cell (a wall), an unchecked spot and puts both in its answer.
        llm.Json = """{"answer":"the gen cell is 4,65,6; also 3,65,6","facts":[{"claim":"gen cell west of the lava","coords":[[4,65,8]],"role":"gen cell","confidence":0.9},{"claim":"a spot to note","coords":[[3,65,6]],"confidence":0.9},{"claim":"cobble to mine at 4,65,6","coords":[],"confidence":0.9}]}""";
        c.World.SetBlock(1, 64, 1, 0, 0);
        var job = TestBot.J(new { name = "fill_floor", args = new { at = "1,64,1" } });
        auto.OnToolStarted("skill", job);                            // a model question (build check): perception answers
        c.World.SetBlock(1, 64, 1, Blocks.Cobblestone, 0);
        var note = await auto.ForWorkerAsync(CancellationToken.None);
        Assert.Single(llm.Perception);
        Assert.NotNull(note);
        foreach (var wrong in new[] { "4,65,8", "3,65,6", "4,65,6" }) Assert.DoesNotContain(wrong, note);
        Assert.DoesNotContain("the gen cell is", note);              // a model with refuted claims loses its answer line
        // The gen step itself is answered from voxels: the real cell only.
        c.World.SetBlock(5, 65, 6, Blocks.Cobblestone, 0);           // the spot changed: a fresh gen look
        auto.OnToolStarted("harvest_generator", TestBot.J(new { at = "5,65,7", count = 1 }));
        var gen = await auto.ForWorkerAsync(CancellationToken.None);
        Assert.StartsWith("[vision] exact: gen cells (G, mine only these): 5,65,7 ", gen);
        Assert.Single(llm.Perception);
        // A worker that still tries the wrong cell is refused: harvest only digs generator cells.
        var r = await h.RunAs(Agent.Llm.ModelTier.Worker, "harvest_generator", new { at = "4,65,8", count = 1, store = "false" });
        Assert.DoesNotContain(c.CallList, call => call.StartsWith("dig 4,65,8", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ eval (offline, recorded/fake answers)

    /// <summary>Replays answers per (model, case): Opus reads the sheet directly, Sonnet/Haiku answer as perception models.</summary>
    private sealed class RecordedLlm(Dictionary<(string Model, string Case), string> answers, Func<LlmRequest, string> caseOf) : ILlmClient
    {
        public int Calls;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var model = request.Model ?? "claude-opus-5-5";
            var text = answers.GetValueOrDefault((model, caseOf(request)), "ANSWER: unsure");
            bool image = request.Messages.Any(m => m.Content.Any(b => b is LlmImageBlock));
            long inTok = (image ? 300 : 0) + (request.Purpose == "perception" ? 520 : 260);
            return Task.FromResult(new LlmResponse([new LlmText(text)], StopReasons.EndTurn, new LlmUsage(inTok, text.Length / 4 + 10, 0, 0), model));
        }
    }

    private sealed record Recorded(string Case, string Model, string Purpose, string Text, long InputTokens, long OutputTokens);

    /// <summary>
    /// The live run (tools/VisionAB perception --record, 2026-10-27: real Opus/Sonnet/Haiku answers to the real sheets) replayed
    /// offline through today's verifier and graders: deterministic, zero API calls.
    /// </summary>
    [Fact]
    public async Task Recorded_live_answers_replay_through_the_verifier()
    {
        var path = RepoFile("tests", "fixtures", "vision-eval", "recorded.json");
        var recorded = JsonSerializer.Deserialize<List<Recorded>>(File.ReadAllText(path))!;
        var cases = VisionEval.Cases(NetherFixture());
        string CaseOf(LlmRequest r)
        {
            var t = string.Join(" ", r.Messages.SelectMany(m => m.Content.OfType<LlmText>()).Select(x => x.Text));
            return cases.First(c => t.Contains(c.Question, StringComparison.Ordinal)).Id;
        }
        var llm = new ReplayLlm(recorded, CaseOf);
        var rows = new List<VisionEvalRow>();
        foreach (var c in cases)
        {
            var sheet = VisionEval.Sheet(c);
            foreach (var arm in VisionEvalArm.Default) rows.Add(await VisionEval.RunAsync(c, arm, llm, new PriceTable(), CancellationToken.None, sheet));
        }
        var report = VisionEval.Report(rows);
        output.WriteLine(report);
        foreach (var r in rows) output.WriteLine($"{r.Case,-19} {r.Arm,-18} {(r.Ok ? "OK " : "BAD")} raw {(r.RawOk ? "ok " : "bad")} {r.Mismatched}/{r.Checked} {r.Answer}");
        Assert.Equal(20, rows.Count);
        Assert.All(rows, r => Assert.Null(r.Error));
        // The live failure reproduced: Opus reading the sheet offered the air cell west of the hole as a face.
        Assert.False(rows.Single(r => r is { Case: "hole_faces", Arm: "opus-direct" }).Ok);
        // The perception arms hand the brain ≈100 tokens instead of a ≈300-token image + legend.
        Assert.All(rows.Where(r => r.Arm != "opus-direct"), r => Assert.True(r.BrainTokens <= 130, $"{r.Case}/{r.Arm}: {r.BrainTokens}"));
        Assert.True(rows.Where(r => r.Arm == "sonnet-perception").Average(r => r.Usd) < rows.Where(r => r.Arm == "opus-direct").Average(r => r.Usd), report);
        // Verification never makes an answer worse, and it caught refuted claims.
        Assert.All(rows.Where(r => r.Arm != "opus-direct"), r => Assert.True(r.Ok || !r.RawOk, $"{r.Case}/{r.Arm} verified worse than raw"));
        Assert.True(rows.Sum(r => r.Mismatched) > 0);
    }

    private sealed class ReplayLlm(List<Recorded> recorded, Func<LlmRequest, string> caseOf) : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var c = caseOf(request);
            var r = recorded.Single(x => x.Case == c && x.Model == request.Model);
            return Task.FromResult(new LlmResponse([new LlmText(r.Text)], StopReasons.EndTurn, new LlmUsage(r.InputTokens, r.OutputTokens, 0, 0), r.Model));
        }
    }

    [Fact]
    public async Task Eval_runs_offline_and_scores_accuracy_tokens_cost_and_hallucinations()
    {
        var cases = VisionEval.Cases(NetherFixture(), File.ReadAllText(RepoFile("tests", "fixtures", "vision-eval", "dual-gen.txt")));
        Assert.Equal(["floor_vs_gen", "hole_faces", "edge_distance", "middle_one", "portal_void_nether", "dual_gen_live"], cases.Select(c => c.Id));
        foreach (var c in cases)
        {
            var s = VisionEval.Sheet(c);
            output.WriteLine($"{c.Id}: sheet {s.Width}x{s.Height} ~{s.Tokens} tok");
            Assert.InRange(s.Tokens, 200, 400);
        }
        const string opus = "claude-opus-5-5", sonnet = "claude-sonnet-5-5", haiku = "claude-haiku-5-5";
        var answers = new Dictionary<(string, string), string>
        {
            [(opus, "floor_vs_gen")] = "The G cell.\nANSWER: 5,63,7",
            [(opus, "hole_faces")] = "ANSWER: 8,62,9, 7,62,8, 6,62,9",           // offers the air cell: wrong
            [(opus, "edge_distance")] = "ANSWER: 3",
            [(opus, "middle_one")] = "ANSWER: 4,63,7",
            [(opus, "portal_void_nether")] = "ANSWER: portal x=2 y66-68 z0-1; east is void",
            [(sonnet, "floor_vs_gen")] = """{"answer":"mine 5,63,7","facts":[{"claim":"gen cell","coords":[[5,63,7]],"role":"gen cell","confidence":0.9}]}""",
            [(sonnet, "hole_faces")] = """{"answer":"place against 8,62,9 or 7,62,8","facts":[{"claim":"solid face to place against","coords":[[8,62,9],[7,62,8],[6,62,9]],"role":"floor","confidence":0.8}]}""",
            [(sonnet, "edge_distance")] = """{"answer":"3 blocks east to the edge","facts":[{"claim":"last floor block east","coords":[[4,62,0]],"role":"floor","confidence":0.8},{"claim":"void","coords":[[5,62,0]],"role":"void","confidence":0.8}]}""",
            [(sonnet, "middle_one")] = """{"answer":"the middle one is 4,63,7","facts":[{"claim":"middle gen cell","coords":[[4,63,7]],"role":"gen cell","confidence":0.9}]}""",
            [(sonnet, "portal_void_nether")] = """{"answer":"portal at x=2, y66-68, z0-1; void east","facts":[{"claim":"portal","coords":[[2,66,0],[2,68,1]],"role":"portal","confidence":0.9},{"claim":"void east of the portal","coords":[[3,66,0]],"role":"void","confidence":0.9}]}""",
            // Haiku misreads rulers: the floor as the gen cell, a far "middle" cell, ground east of the portal.
            [(haiku, "floor_vs_gen")] = """{"answer":"mine 5,62,8","facts":[{"claim":"gen cell","coords":[[5,62,8]],"role":"gen cell","confidence":0.7}]}""",
            [(haiku, "hole_faces")] = """{"answer":"place against 8,62,9","facts":[{"claim":"solid face","coords":[[8,62,9],[7,62,10]],"role":"floor","confidence":0.7}]}""",
            [(haiku, "edge_distance")] = """{"answer":"4 blocks","facts":[]}""",
            [(haiku, "middle_one")] = """{"answer":"the middle one is 4,63,9","facts":[{"claim":"middle gen cell","coords":[[4,63,9]],"role":"gen cell","confidence":0.6}]}""",
            // Live 21:12 on the roofed dual gen: cells read one column off, one right (the verifier keeps only that one).
            [(sonnet, "dual_gen_live")] = """{"answer":"gen cells at 6,64,7 and 7,64,8","facts":[{"claim":"gen cells in rows z7-z8","coords":[[6,64,7],[7,64,8],[6,64,8]],"role":"gen cell","confidence":0.7},{"claim":"island floor","coords":[[4,63,6]],"role":"floor","confidence":0.8}]}""",
            [(haiku, "portal_void_nether")] = """{"answer":"portal at 2,67,0; glowstone floor east","facts":[{"claim":"portal","coords":[[2,67,0]],"role":"portal","confidence":0.8},{"claim":"glowstone floor east of the portal","coords":[[3,65,0]],"role":"floor","confidence":0.6}]}""",
        };
        string CaseOf(LlmRequest r)
        {
            var t = string.Join(" ", r.Messages.SelectMany(m => m.Content.OfType<LlmText>()).Select(x => x.Text));
            return cases.First(c => t.Contains(c.Question, StringComparison.Ordinal)).Id;
        }
        var llm = new RecordedLlm(answers, CaseOf);
        var prices = new PriceTable();
        var rows = new List<VisionEvalRow>();
        foreach (var c in cases)
        {
            var sheet = VisionEval.Sheet(c);
            foreach (var arm in VisionEvalArm.Default) rows.Add(await VisionEval.RunAsync(c, arm, llm, prices, CancellationToken.None, sheet));
        }
        var report = VisionEval.Report(rows);
        output.WriteLine(report);
        Assert.Equal(19, llm.Calls);                // 6 cases x 3 model arms + the router's one model question (the middle one)
        int Acc(string arm) => rows.Count(r => r.Arm == arm && r.Ok);
        Assert.Equal(4, Acc("opus-direct"));        // offered the air cell in the hole; no answer for the dual gen
        Assert.Equal(5, Acc("sonnet-perception"));  // the verifier removed the air cell from its faces; the dual gen stays wrong
        Assert.Equal(1, Acc("haiku-perception"));   // only the hole faces survive; the rest are dropped, never handed on
        Assert.Equal(6, Acc("router+sonnet"));      // 5 exact from voxels (0 tokens), the middle one by Sonnet
        Assert.All(rows.Where(r => r.Arm == "router+sonnet" && r.Case != "middle_one"), r => Assert.Equal(0, r.InputTokens));
        Assert.DoesNotContain("6,64,7", rows.Single(r => r is { Case: "dual_gen_live", Arm: "sonnet-perception" }).Answer);
        var haikuRows = rows.Where(r => r.Arm == "haiku-perception").ToList();
        Assert.True(haikuRows.Sum(r => r.Mismatched) >= 4, report);
        Assert.DoesNotContain(haikuRows, r => r.Answer.Contains("glowstone floor east") || r.Answer.Contains("5,62,8") || r.Answer.Contains("4,63,9"));
        // The brain reads ~100 tokens of text in the perception arms instead of the ~300-token image.
        Assert.All(rows.Where(r => r.Arm != "opus-direct"), r => Assert.True(r.BrainTokens <= 120, r.Answer));
        Assert.All(rows, r => Assert.True(r.Usd > 0));
        Assert.True(rows.Where(r => r.Arm == "sonnet-perception").Average(r => r.Usd) < rows.Where(r => r.Arm == "opus-direct").Average(r => r.Usd), report);
    }
}
