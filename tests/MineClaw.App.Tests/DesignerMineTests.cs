using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// The Design Studio's mine designs (docs/web/api.md "Mine designs"): a mine canvas over generated terrain at absolute y 0..127,
/// its dug cells as the design (<c>kind mine</c>, <c>anchor absolute</c>, clear boxes), save → load → revision → revert keeping the
/// absolute y, the routes' kind / seed / preference, and the AI's mine prompt and templates.
/// </summary>
[Collection(GameDataDefaultCollection.Name)]
public class DesignerMineTests
{
    private const string Token = "s3cret-token";
    private static JsonElement E(object o) => JsonSerializer.SerializeToElement(o);

    private sealed class ScriptedModel(params Func<LlmRequest, LlmResponse>[] steps) : ILlmClient
    {
        private int _i;
        public readonly ConcurrentQueue<LlmRequest> Requests = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            Requests.Enqueue(r);
            return Task.FromResult(steps[Math.Min(_i++, steps.Length - 1)](r));
        }
    }

    private static readonly LlmUsage Use = new(1000, 200, 3000, 0);

    private static LlmResponse Tools(LlmRequest r, params (string Name, object Input)[] calls) =>
        new(calls.Select((c, i) => (LlmBlock)new LlmToolUse($"t{i}", c.Name, JsonSerializer.SerializeToElement(c.Input))).ToList(), StopReasons.ToolUse, Use, r.Model ?? "claude-opus-5-5");

    private static LlmResponse Say(LlmRequest r, string text) => new([new LlmText(text)], StopReasons.EndTurn, Use, r.Model ?? "claude-opus-5-5");

    private static BlueprintService Designs(AgentDb db)
    {
        var s = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (null, "n/a"), Trusted = () => ["poofimafox"],
        };
        s.History = new DesignHistory(s, new DesignRevisions(db));
        return s;
    }

    private static SupervisorServer Server(ILlmClient llm, out BlueprintService designs, Func<long?>? seed = null)
    {
        var db = AgentDb.Open(null);
        designs = Designs(db);
        var s = designs;
        var o = new SupervisorOptions { Token = Token, Viewer = new SupervisorViewerOptions { Enabled = true, Designer = new DesignerOptions { Enabled = true } } };
        var routes = new DesignerRoutes(o, () => s, new DesignerStore(db), llm, new LlmOptions { ApiKey = "sk-test" }, null, () => "maid_chan") { ServerSeed = seed };
        return new SupervisorServer(o, new FakeSupervisorData()) { Designer = routes };
    }

    private static SupervisorRequest Req(string method, string path, object? body = null) =>
        new(method, path, new Dictionary<string, string>(), Token, body == null ? null : JsonSerializer.Serialize(body));

    private static JsonElement J(SupervisorReply r)
    {
        Assert.True(r.Status == 200, $"{r.Status}: {r.Text}");
        return JsonDocument.Parse(r.Text).RootElement.Clone();
    }

    private static DesignerCanvas MineCanvas(string seed = "4242", string version = "1.12.2") =>
        new() { Kind = DesignerCanvas.KindMine, Seed = seed, Version = version };

    [Fact]
    public void A_mine_canvas_spans_bedrock_to_the_sky_and_digs_only_the_ground()
    {
        var c = MineCanvas();
        Assert.True(c.Contains(0, 0, 0));
        Assert.True(c.Contains(63, 127, 63));
        Assert.False(c.Contains(0, 128, 0));
        Assert.False(c.Contains(0, -1, 0));
        Assert.False(c.Contains(64, 10, 0));
        Assert.Equal((0, 127), c.YRange);
        var t = c.Terrain!;
        int top = t.Surface(10, 10);
        Assert.True(c.IsEarth(10, top, 10));
        Assert.False(c.IsEarth(10, top + 1, 10));
        // Dig stone deep down, nothing above the surface, never bedrock.
        var dug = c.Apply([new CanvasCell(10, 6, 10, 0, 0), new CanvasCell(10, top + 3, 10, 0, 0), new CanvasCell(10, 0, 10, 0, 0)], out var rejected);
        Assert.Single(dug);
        Assert.Equal(1, rejected);
        Assert.Contains(new BlockPos(10, 6, 10), c.Dug);
        Assert.Contains("dug 1 to mine out (y6..6)", c.Describe());
        Assert.Contains("dug 1", DesignerCanvas.Summarize(dug, on: c));
        // A torch in the tunnel, earth fills the cell back; persistence keeps the kind's cells.
        c.Apply([new CanvasCell(10, 6, 10, Blocks.Torch, 5)]);
        Assert.DoesNotContain(new BlockPos(10, 6, 10), c.Dug);
        c.Apply([new CanvasCell(10, 6, 10, 0, 0), new CanvasCell(11, 6, 10, 0, 0)]);
        var again = DesignerCanvas.Decode(c.Encode(), c.Rev, DesignerCanvas.KindMine, "4242", "1.12.2");
        Assert.True(again.Mine);
        Assert.Equal(c.Dug.ToHashSet(), again.Dug.ToHashSet());
        c.Apply([new CanvasCell(11, 6, 10, DesignerCanvas.Earth, 0)]);
        Assert.True(c.IsEarth(11, 6, 10));
        c.Clear();
        Assert.Empty(c.Dug);
        Assert.Contains("empty mine", c.Describe());
        Assert.Contains("surface y", c.Describe());
    }

    [Fact]
    public void A_mine_saves_with_its_absolute_y_and_reverts_through_its_revisions()
    {
        using var db = AgentDb.Open(null);
        var svc = Designs(db);
        var c = MineCanvas();
        var (text, op, err) = DesignerTools.Run(c, "spiral_shaft", E(new { at = new[] { 30, 30 }, bottom = 11, size = 4, core = 2, torch_every = 6, landings = new[] { 11 } }));
        Assert.False(err, text);
        Assert.Contains("entry", text);
        (text, _, err) = DesignerTools.Run(c, "strip_mine", E(new { at = new[] { 34, 11, 31 }, length = 20, corridor = "2x3", spacing = 3, branch_length = 6 }));
        Assert.False(err, text);
        int top = c.Terrain!.SurfaceOver(30, 30, 33, 33);
        var session = new DesignerSessionRecord { Canvas = c, Owner = "poofimafox", Anchor = new BlockPos(30, top, 30), PreferredFor = "1.12.2" };
        var r1 = DesignerSave.Save(svc, session, "deep strip", "the first cut", "poofimafox", false);
        Assert.True(r1.Ok, r1.Text);
        Assert.Contains("anchor absolute y 11", r1.Text);
        var rec = svc.Store.Get(r1.DesignId)!;
        Assert.Contains("kind mine", rec.Dsl);
        Assert.Contains("anchor absolute 30,11,", rec.Dsl);
        Assert.Contains("preferred_for 1.12.2", rec.Dsl);
        Assert.Contains("seed 4242", rec.Dsl);
        Assert.Contains("clear ", rec.Dsl);
        Assert.True(rec.Dsl.Length < 40_000, $"the DSL is {rec.Dsl.Length} chars");
        Assert.Equal("1.12.2", DesignVersions.OfDsl(rec.Dsl));
        var firstDug = c.Dug.ToHashSet();
        var firstBlocks = c.Blocks.ToDictionary();

        // Opened again: a mine canvas, the same cells at the same absolute y and plot place, its entry as the anchor.
        var l = DesignerSave.LoadDesign(svc, rec.Id);
        Assert.Null(l.Why);
        Assert.True(l.Canvas!.Mine);
        Assert.Equal("4242", l.Canvas.Seed);
        Assert.Equal(firstDug, l.Canvas.Dug.ToHashSet());
        Assert.Equal(firstBlocks, l.Canvas.Blocks.ToDictionary());
        Assert.Equal(new BlockPos(30, top, 30), l.Anchor);
        Assert.Equal("1.12.2", l.PreferredFor);

        // r2: the strip filled back in.
        var s2 = new DesignerSessionRecord { Canvas = l.Canvas, Owner = "poofimafox", DesignId = rec.Id, Anchor = l.Anchor, PreferredFor = l.PreferredFor };
        s2.Canvas.Apply(s2.Canvas.Dug.Where(p => p.X >= 34).Select(p => new CanvasCell(p.X, p.Y, p.Z, DesignerCanvas.Earth, 0)).ToList());
        var r2 = DesignerSave.Save(svc, s2, "deep strip", "shaft only", "poofimafox", false);
        Assert.True(r2.Ok, r2.Text);
        Assert.Equal(r1.Rev + 1, r2.Rev);
        Assert.DoesNotContain(DesignerSave.LoadDesign(svc, rec.Id).Canvas!.Dug, p => p.X >= 34);

        // Revert: r1 opened and saved again is the newest revision, the same cells at the same y.
        var back = DesignerSave.LoadDesign(svc, rec.Id, r1.Rev);
        Assert.Equal(r1.Rev, back.Rev);
        Assert.Equal(firstDug, back.Canvas!.Dug.ToHashSet());
        var s3 = new DesignerSessionRecord { Canvas = back.Canvas, Owner = "poofimafox", DesignId = rec.Id, Anchor = back.Anchor, PreferredFor = back.PreferredFor };
        var r3 = DesignerSave.Save(svc, s3, "deep strip", "revert to r1", "poofimafox", false);
        Assert.True(r3.Ok, r3.Text);
        var head = DesignerSave.LoadDesign(svc, rec.Id);
        Assert.Equal(r2.Rev + 1, head.Rev);
        Assert.Equal(firstDug, head.Canvas!.Dug.ToHashSet());
        Assert.Equal(11, head.Canvas.Dug.Min(p => p.Y));
        Assert.Null(DesignerSave.LoadDesign(svc, rec.Id, 99).Canvas);

        // The design's plan stands at its absolute y wherever it is built (the entry's column on the site's).
        var (bp, issues) = BlueprintParser.Parse(svc.Store.Get(rec.Id)!.Dsl);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.True(bp.Mine);
        Assert.Equal(11, bp.AbsoluteY);
        var origin = bp.OriginOn(new BlockPos(500, 70, -200));
        Assert.Equal(11, origin.Y);
        Assert.Equal(500, origin.X + bp.Entry!.Value.X);
        Assert.Equal(-200, origin.Z + bp.Entry!.Value.Z);
    }

    [Fact]
    public void A_build_design_never_grows_past_32_high_and_a_mine_never_digs_below_y_1()
    {
        var tall = "blueprint t\nsize 3x40x3\nversion 1.12.2\npalette A=stone\nlayer 0\nAAA\nAAA\nAAA\n";
        Assert.Contains(BlueprintParser.Parse(tall).Issues, i => i.Code == "size" && i.Level == IssueLevel.Error);
        var mine = "blueprint m\nsize 3x40x3\nversion 1.12.2\nkind mine\nanchor absolute 0,5,0\nfacing south\nclear 0,0,0 2,39,2\n";
        var (bp, issues) = BlueprintParser.Parse(mine);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        var (plan, more) = BlueprintNormalizer.Normalize(bp, bp.OriginOn(new BlockPos(0, 64, 0)));
        Assert.NotNull(plan);
        Assert.DoesNotContain(more, i => i.Level == IssueLevel.Error);
        Assert.Equal(3 * 40 * 3, plan!.Air.Count);
        Assert.Equal(5, plan.Air.Min(p => p.Y));
        Assert.Contains(BlueprintParser.Parse(mine.Replace("anchor absolute 0,5,0", "anchor absolute 0,0,0")).Issues, i => i.Code == "anchor");
        var snap = new MineClaw.World.Designs.DesignSnapshot();
        snap.Air.Add(new BlockPos(1, 0, 1));
        Assert.Contains("below y 1", MineDesign.Draw("x", snap, null, null, null, "1.12.2").Why);
    }

    [Fact]
    public async Task The_routes_open_switch_and_seed_a_mine_session()
    {
        var server = Server(new ScriptedModel(), out _, () => 987654321L);
        var models = J(await server.HandleAsync(Req("GET", "/api/designer/models")));
        Assert.Equal("987654321", models.GetProperty("server_seed").GetString());
        var s = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { player = "poofimafox", kind = "mine", seed = "777", version = "1.12.2" })));
        long id = s.GetProperty("id").GetInt64();
        Assert.Equal("mine", s.GetProperty("kind").GetString());
        Assert.Equal("777", s.GetProperty("seed").GetString());
        Assert.Equal(128, s.GetProperty("plot").GetProperty("height").GetInt32());
        Assert.Equal(0, s.GetProperty("plot").GetProperty("depth").GetInt32());
        // Edits at absolute y; the seed and the preference change in place.
        var top = MineTerrain.For("777", "1.12.2").Surface(5, 5);
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas", new { rev = 0, ops = new[] { new { kind = "dig", label = "dig", cells = new[] { new[] { 5, 6, 5, 0, 0 }, new[] { 5, top + 2, 5, 0, 0 } } } }, anchor = new[] { 5, top, 5 } })));
        var v = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/version", new { version = "1.12.2", seed = "778", preferred_for = "1.12.2" })));
        Assert.True(v.GetProperty("changed").GetBoolean());
        Assert.Equal("778", v.GetProperty("session").GetProperty("seed").GetString());
        Assert.Equal("1.12.2", v.GetProperty("session").GetProperty("preferred_for").GetString());
        Assert.Equal(1, v.GetProperty("session").GetProperty("dug").GetArrayLength());
        Assert.Equal(top, v.GetProperty("session").GetProperty("anchor")[1].GetInt32());
        // Back to a build plot: the full canvas asks first, then clears.
        var ask = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/version", new { version = "1.12.2", kind = "build" })));
        Assert.False(ask.GetProperty("changed").GetBoolean());
        Assert.True(ask.GetProperty("needs_clear").GetBoolean());
        var done = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/version", new { version = "1.12.2", kind = "build", apply = true })));
        Assert.Equal("build", done.GetProperty("session").GetProperty("kind").GetString());
        Assert.Equal(0, done.GetProperty("session").GetProperty("dug").GetArrayLength());
        Assert.Equal(16, done.GetProperty("session").GetProperty("plot").GetProperty("depth").GetInt32());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("session").GetProperty("preferred_for").ValueKind);
        // An empty build canvas becomes a mine at once (a random seed when none is given).
        var mine = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/version", new { version = "b1.8.1", kind = "mine" })));
        Assert.True(mine.GetProperty("changed").GetBoolean());
        Assert.Equal("mine", mine.GetProperty("session").GetProperty("kind").GetString());
        Assert.Equal("b1.8.1", mine.GetProperty("session").GetProperty("version").GetString());
        Assert.False(string.IsNullOrEmpty(mine.GetProperty("session").GetProperty("seed").GetString()));
        Assert.Equal(400, (await server.HandleAsync(Req("POST", "/api/designer/sessions", new { kind = "cave" }))).Status);
    }

    [Fact]
    public async Task The_mine_designer_prompt_is_compact_and_its_tools_dig_the_templates()
    {
        var model = new ScriptedModel(
            r => Tools(r, ("spiral_shaft", new { at = new[] { 20, 20 }, bottom = 12, size = 3, core = 1 }), ("strip_mine", new { at = new[] { 23, 12, 21 }, length = 12 })),
            r => Say(r, "Dug a spiral down to y 12 and a strip mine east."));
        var server = Server(model, out var designs);
        var s = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { player = "poofimafox", kind = "mine", seed = "31", version = "1.12.2" })));
        long id = s.GetProperty("id").GetInt64();
        var m = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/messages", new { text = "dig me a diamond mine", canvas = new { rev = 0 }, player = "poofimafox" })));
        Assert.Equal(2, m.GetProperty("ops").GetArrayLength());
        Assert.Contains("spiral shaft", m.GetProperty("ops")[0].GetProperty("label").GetString());
        var first = model.Requests.First();
        var system = first.System[0].Text;
        Assert.Contains("ABSOLUTE world y", system);
        Assert.Contains("diamond y0-15 best 11", system);
        Assert.Contains("1.12.2-diamond-y11", system);
        Assert.True(system.Length < 2200, $"mine prompt is {system.Length} chars");
        Assert.Contains(first.Tools!, t => t.Name == "spiral_shaft");
        // A build session's turn has neither the mine tools nor the section.
        Assert.DoesNotContain(DesignerTools.All, t => t.Name == "spiral_shaft");
        Assert.DoesNotContain("ABSOLUTE", DesignerTools.System("poofimafox", "1.12.2"));
        var (text, _, err) = DesignerTools.Run(new DesignerCanvas(), "spiral_shaft", E(new { at = new[] { 1, 1 }, bottom = 5 }));
        Assert.True(err);
        Assert.Contains("mine canvas", text);
        // Saved as a mine; a preset applies on the terrain.
        var saved = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "ai mine", player = "poofimafox" })));
        Assert.Contains("kind mine", designs.Store.Get(saved.GetProperty("design_id").GetInt64())!.Dsl);
        var c = MineCanvas("31");
        var (ptext, pop, perr) = DesignerTools.Run(c, "mine_preset", E(new { id = "1.12.2-diamond-y11" }));
        Assert.False(perr, ptext);
        Assert.True(pop!.Cells.Count > 500);
        Assert.Equal(11, c.Dug.Min(p => p.Y));
    }
}
