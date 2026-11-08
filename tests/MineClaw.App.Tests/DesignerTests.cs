using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>The Design Studio: models from config, sessions over a sandbox canvas with a (fake) model's tool calls, saves through
/// the designs history, loading a design, budget accounting and the route gate.</summary>
[Collection(GameDataDefaultCollection.Name)]
public class DesignerTests
{
    internal const string Token = "s3cret-token";

    /// <summary>A scripted model: each call pops the next reply (tool calls or text) and records what it was asked.</summary>
    internal sealed class ScriptedModel(params Func<LlmRequest, LlmResponse>[] steps) : ILlmClient
    {
        private int _i;
        public readonly ConcurrentQueue<LlmRequest> Requests = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            Requests.Enqueue(r);
            var step = steps[Math.Min(_i++, steps.Length - 1)];
            return Task.FromResult(step(r));
        }
    }

    internal static readonly LlmUsage Use = new(1000, 200, 3000, 0);

    internal static LlmResponse Tools(LlmRequest r, params (string Name, object Input)[] calls) =>
        new(calls.Select((c, i) => (LlmBlock)new LlmToolUse($"t{i}", c.Name, JsonSerializer.SerializeToElement(c.Input))).ToList(), StopReasons.ToolUse, Use, r.Model ?? "claude-opus-5-5");

    internal static LlmResponse Say(LlmRequest r, string text) => new([new LlmText(text)], StopReasons.EndTurn, Use, r.Model ?? "claude-opus-5-5");

    internal static LlmOptions Llm(bool key = true) => new() { ApiKey = key ? "sk-test" : null };

    private static (BlueprintService Service, AgentDb Db) Designs()
    {
        var db = AgentDb.Open(null);
        var s = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (null, "n/a"),
        };
        s.History = new DesignHistory(s, new DesignRevisions(db));
        return (s, db);
    }

    internal static SupervisorOptions Options(bool viewer = true, bool designer = true) => new()
    {
        Token = Token, Viewer = new SupervisorViewerOptions { Enabled = viewer, Designer = new DesignerOptions { Enabled = designer } },
    };

    internal static (SupervisorServer Server, DesignerRoutes Routes, BlueprintService Designs) Server(ILlmClient llm, UsageTracker? usage = null,
        SupervisorOptions? o = null, LlmOptions? llmOptions = null)
    {
        var (svc, db) = Designs();
        o ??= Options();
        var routes = new DesignerRoutes(o, () => svc, new DesignerStore(db), llm, llmOptions ?? Llm(), usage, () => "maid_chan");
        return (new SupervisorServer(o, new FakeSupervisorData()) { Designer = routes }, routes, svc);
    }

    internal static SupervisorRequest Req(string method, string path, object? body = null, string? token = Token) =>
        new(method, path, new Dictionary<string, string>(), token, body == null ? null : JsonSerializer.Serialize(body));

    internal static JsonElement J(SupervisorReply r)
    {
        Assert.True(r.Status == 200, $"{r.Status}: {r.Text}");
        return JsonDocument.Parse(r.Text).RootElement.Clone();
    }

    // ------------------------------------------------------------------ models

    [Fact]
    public void Models_come_from_the_tiers_and_usable_profiles_5_5_only()
    {
        var o = Llm();
        o.Ollama.Endpoint = "http://localhost:11434";
        o.Profiles["deepseek"] = new LlmProfile { Provider = "ollama", Model = "deepseek-v3.1:671b-cloud", CostHint = "free" };
        o.Profiles["old"] = new LlmProfile { Provider = "anthropic", Model = "claude-3-5-sonnet-20241022" };
        var list = DesignerModels.List(o);
        Assert.Equal("main", list[0].Id);
        Assert.Equal("Opus 5.5 (brain)", list[0].Label);
        Assert.Contains(list, m => m.Id == "worker" && m.Model == "claude-sonnet-5-5");
        Assert.Contains(list, m => m.Id == "cheap" && m.Model == "claude-haiku-5-5");
        Assert.Contains(list, m => m.Id == "profile:deepseek" && m.Provider == "ollama");
        Assert.DoesNotContain(list, m => m.Model.StartsWith("claude-3", StringComparison.Ordinal)); // never below the 5.5 generation
        Assert.Equal(list.Count, list.Select(m => (m.Provider, m.Model)).Distinct().Count()); // the opus/sonnet/haiku profiles are the tiers
        // No Anthropic key: only the Ollama profile is offered.
        var noKey = Llm(key: false);
        noKey.Ollama.Endpoint = "http://localhost:11434";
        noKey.Profiles["deepseek"] = new LlmProfile { Provider = "ollama", Model = "deepseek-v3.1:671b-cloud" };
        Assert.Equal(["profile:deepseek"], DesignerModels.List(noKey).Select(m => m.Id));
        Assert.Equal("worker", DesignerModels.Resolve(o, "claude-sonnet-5-5")?.Id);
        Assert.Null(DesignerModels.Resolve(o, "gpt-9"));
    }

    [Fact]
    public async Task Models_route_lists_them_with_a_default_and_the_version()
    {
        var (server, _, _) = Server(new ScriptedModel());
        var j = J(await server.HandleAsync(Req("GET", "/api/designer/models")));
        Assert.Equal("main", j.GetProperty("default").GetString());
        Assert.True(j.GetProperty("models")[0].GetProperty("default").GetBoolean());
        Assert.Equal("anthropic", j.GetProperty("models")[0].GetProperty("provider").GetString());
        Assert.False(string.IsNullOrEmpty(j.GetProperty("version").GetString()));
    }

    // ------------------------------------------------------------------ canvas and tools

    [Fact]
    public void Tools_fill_hollow_line_remove_replace_and_keep_doors_whole()
    {
        var c = new DesignerCanvas();
        JsonElement E(object o) => JsonSerializer.SerializeToElement(o);
        var (_, fill, _) = DesignerTools.Run(c, "box", E(new { b = "cobblestone", from = new[] { 10, 0, 10 }, to = new[] { 14, 0, 14 } }));
        Assert.Equal(25, fill!.Cells.Count);
        var (_, hollow, _) = DesignerTools.Run(c, "box", E(new { b = "planks", from = new[] { 10, 1, 10 }, to = new[] { 14, 3, 14 }, mode = "hollow" }));
        Assert.Equal(5 * 5 * 3 - 3 * 3 * 1, c.Blocks.Count(kv => kv.Key.Y >= 1)); // the shell; the inside stays air
        Assert.Equal(0, c.Get(12, 2, 12).Id);
        var (_, line, _) = DesignerTools.Run(c, "line", E(new { b = "fence", from = new[] { 0, 0, 0 }, to = new[] { 4, 0, 0 } }));
        Assert.Equal(5, line!.Cells.Count);
        var (text, door, err) = DesignerTools.Run(c, "place", E(new { b = "wooden_door:south", at = new[] { new[] { 12, 1, 14 } } }));
        Assert.False(err, text);
        Assert.Equal(64, c.Get(12, 1, 14).Id);
        Assert.Equal(8, c.Get(12, 2, 14).Meta & 8); // the upper half came with it
        Assert.Equal(2, door!.Cells.Count);
        DesignerTools.Run(c, "remove", E(new { from = new[] { 12, 1, 14 }, to = new[] { 12, 1, 14 } }));
        Assert.Equal(0, c.Get(12, 2, 14).Id); // never half a door
        var (_, rep, _) = DesignerTools.Run(c, "replace", E(new { a = "cobblestone", b = "stone_bricks" }));
        Assert.Equal(25, rep!.Cells.Count);
        var (bad, none, isErr) = DesignerTools.Run(c, "place", E(new { b = "grass", at = new[] { new[] { 1, 1, 1 } } }));
        Assert.True(isErr && none == null, bad); // only design-palette blocks
        Assert.Contains("blocks", c.Describe());
        Assert.Contains("y0", DesignerTools.Run(c, "describe", E(new { y = 0 })).Text);
    }

    // ------------------------------------------------------------------ a session with a model

    [Fact]
    public async Task A_message_runs_the_models_tool_calls_on_the_canvas_and_books_the_cost()
    {
        var model = new ScriptedModel(
            r => Tools(r, ("box", new { b = "cobblestone", from = new[] { 28, 0, 28 }, to = new[] { 34, 0, 34 } }),
                ("box", new { b = "planks", from = new[] { 28, 1, 28 }, to = new[] { 34, 3, 34 }, mode = "walls" })),
            r => Tools(r, ("place", new { b = "wooden_door:south", at = new[] { new[] { 31, 1, 34 } } })),
            r => Say(r, "Built a small plank hut with a door on the south side."));
        var usage = new UsageTracker(new BudgetOptions { DailyCapUsd = 100, MonthlyCapUsd = 1000 }, path: null);
        var (server, routes, _) = Server(model, usage);
        var s = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { model = "main", player = "poofimafox" })));
        long id = s.GetProperty("id").GetInt64();
        Assert.Equal(0, s.GetProperty("blocks").GetArrayLength());
        Assert.True(s.GetProperty("palette").GetArrayLength() > 30);
        // The user's own edit first (the AI hears about it), then the message.
        var edit = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas",
            new { rev = 0, ops = new[] { new { kind = "place", label = "placed glass", cells = new[] { new[] { 2, 0, 2, 20, 0 } } } }, player = "poofimafox" })));
        Assert.Equal(1, edit.GetProperty("applied").GetInt32());
        var m = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/messages",
            new { text = "build me a little hut", canvas = new { rev = 1 }, selection = new { min = new[] { 28, 0, 28 }, max = new[] { 34, 3, 34 } }, player = "poofimafox" })));
        Assert.Equal("Built a small plank hut with a door on the south side.", m.GetProperty("reply").GetString());
        var ops = m.GetProperty("ops");
        Assert.Equal(3, ops.GetArrayLength());
        Assert.Equal("fill", ops[0].GetProperty("kind").GetString());
        Assert.Equal(49, ops[0].GetProperty("cells").GetArrayLength());
        Assert.Equal("walls", ops[1].GetProperty("kind").GetString());
        Assert.Equal(3, model.Requests.Count);
        // Prompt caching on the system block, the canvas summary + selection + the user's edit in the first turn.
        var first = model.Requests.First();
        Assert.True(first.System[0].Cache);
        Assert.Equal("designer", first.Purpose);
        Assert.Equal(8, first.Tools.Count); // the seven canvas tools + find_blocks
        var brief = first.Messages[^1].Text;
        Assert.Contains("[canvas]", brief);
        Assert.Contains("[selection] from [28,0,28] to [34,3,34]", brief);
        Assert.Contains("placed 1 glass", brief);
        // Budget: three calls on the bot's own tracker, the session's cost and a transcript line with the ops.
        double cost = m.GetProperty("cost_usd").GetDouble();
        Assert.True(cost > 0);
        Assert.Equal(3, usage.Today.Calls);
        Assert.Equal((double)usage.Today.Usd, cost, 6);
        var after = J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}")));
        Assert.Equal(cost, after.GetProperty("cost_usd").GetDouble(), 6);
        Assert.Equal(3, after.GetProperty("calls").GetInt32());
        var msgs = after.GetProperty("messages");
        Assert.Equal(2, msgs.GetArrayLength());
        Assert.Equal("assistant", msgs[1].GetProperty("role").GetString());
        Assert.Equal(3, msgs[1].GetProperty("ops").GetArrayLength());
        // glass + the 7×7 floor + three layers of the 24-cell wall ring; the door replaced two wall cells (both halves).
        Assert.Equal(1 + 49 + 24 * 3, after.GetProperty("blocks").GetArrayLength());
        // The user's edit was told once: the next message no longer repeats it.
        Assert.Equal("", routes.Store.Get(id)!.PendingEdits);
    }

    [Fact]
    public async Task A_paused_budget_refuses_the_message_and_the_model_is_never_called()
    {
        var model = new ScriptedModel(r => Say(r, "never"));
        var usage = new UsageTracker(new BudgetOptions { DailyCapUsd = 0.001m, MonthlyCapUsd = 1000 }, path: null);
        usage.Record("claude-opus-5-5", new LlmUsage(10_000, 10_000, 0, 0), ModelTier.Main); // over today's cap
        var (server, _, _) = Server(model, usage);
        long id = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { player = "poofimafox" }))).GetProperty("id").GetInt64();
        var r = await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/messages", new { text = "hi", canvas = new { rev = 0 }, player = "poofimafox" }));
        Assert.Equal(429, r.Status);
        Assert.Contains("paused", r.Text);
        Assert.Empty(model.Requests);
    }

    // ------------------------------------------------------------------ saves and loads

    [Fact]
    public async Task Save_makes_a_design_and_revisions_and_it_loads_back()
    {
        var model = new ScriptedModel(
            r => Tools(r, ("box", new { b = "cobblestone", from = new[] { 30, 0, 30 }, to = new[] { 33, 0, 33 } }),
                ("box", new { b = "planks", from = new[] { 30, 1, 30 }, to = new[] { 33, 2, 33 }, mode = "walls" })),
            r => Say(r, "A tiny hut."));
        var (server, routes, svc) = Server(model);
        long id = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { player = "poofimafox" }))).GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/messages", new { text = "a tiny hut", canvas = new { rev = 0 }, player = "poofimafox" })));
        var save = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "tiny hut", summary = "first draft", player = "poofimafox" })));
        long did = save.GetProperty("design_id").GetInt64();
        Assert.True(save.GetProperty("created").GetBoolean());
        Assert.Equal(1, save.GetProperty("rev").GetInt32());
        var rec = svc.Store.Get(did)!;
        Assert.Equal("tiny hut", rec.Name);
        Assert.Equal(DesignStatus.Accepted, rec.Status);
        Assert.Equal("poofimafox", rec.Requester);
        Assert.Null(rec.Origin); // a studio design has no site: the bot builds a copy where asked
        Assert.DoesNotContain("\norigin ", rec.Dsl);
        var head = svc.History!.Revisions.Head(did)!;
        Assert.Equal(("poofimafox", RevisionAuthor.Player, "first draft"), (head.Author, head.AuthorKind, head.Message));
        Assert.Equal(16 + 12 * 2, head.Blocks);
        // The DSL builds exactly the canvas.
        var plan = svc.PlanOf(rec, out var err);
        Assert.Null(err);
        Assert.Equal(16 + 12 * 2, plan!.Blocks.Count);
        // An edit and a second save: r2 of the same design (never locked, Git-style).
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas",
            new { rev = 0, ops = new[] { new { kind = "place", label = "", cells = new[] { new[] { 31, 3, 31, 5, 0 } } } }, player = "poofimafox" })));
        var save2 = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "tiny hut", player = "poofimafox" })));
        Assert.Equal(did, save2.GetProperty("design_id").GetInt64());
        Assert.Equal(2, save2.GetProperty("rev").GetInt32());
        Assert.False(save2.GetProperty("created").GetBoolean());
        // "Save as new": another design.
        var save3 = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "tiny hut copy", as_new = true, player = "poofimafox" })));
        Assert.NotEqual(did, save3.GetProperty("design_id").GetInt64());
        // Loading the design: its newest revision, centred on the plot, standing on the grass, with its history.
        var loaded = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = did, player = "kiwi" })));
        Assert.Equal(did, loaded.GetProperty("design_id").GetInt64());
        var blocks = loaded.GetProperty("blocks").EnumerateArray().Select(b => b.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToList();
        Assert.Equal(16 + 12 * 2 + 1, blocks.Count);
        Assert.Equal(0, blocks.Min(b => b[1]));
        Assert.Equal(2, loaded.GetProperty("revisions").GetArrayLength());
        Assert.Contains(blocks, b => b[3] == 5 && b[1] == 3); // the r2 plank on top
        // Saving the loaded session again changes nothing (same block set): no new revision.
        long id2 = loaded.GetProperty("id").GetInt64();
        var same = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id2}/save", new { name = "tiny hut", player = "kiwi" })));
        Assert.Equal(2, same.GetProperty("rev").GetInt32());
        _ = routes;
    }

    [Fact]
    public async Task An_existing_design_from_its_DSL_loads_onto_the_plot()
    {
        var (server, _, svc) = Server(new ScriptedModel());
        var rec = svc.Store.Insert(new BlueprintRecord
        {
            Name = "hut", Description = "hut", Bot = "maid_chan", Status = DesignStatus.Accepted,
            Dsl = "blueprint hut\nsize 3x2x3\npalette C=cobblestone\nlayer 0-1\nCCC\nC.C\nCCC\n",
        });
        var j = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = rec.Id, player = "poofimafox" })));
        var blocks = j.GetProperty("blocks").EnumerateArray().Select(b => b.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToList();
        Assert.Equal(16, blocks.Count);
        Assert.Equal(31, blocks.Min(b => b[0])); // centred: 64/2 - 3/2
        Assert.Equal("hut", j.GetProperty("name").GetString());
        Assert.Equal(1, j.GetProperty("revisions").GetArrayLength()); // r1 made on first look (migration)
        Assert.Equal(404, (await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = 999, player = "poofimafox" }))).Status);
    }

    // ------------------------------------------------------------------ gate

    [Fact]
    public async Task Routes_are_gated_by_the_viewer_switch_and_the_token()
    {
        var model = new ScriptedModel(r => Say(r, "x"));
        Assert.Equal(404, (await Server(model, o: Options(viewer: false)).Server.HandleAsync(Req("GET", "/api/designer/models"))).Status);
        Assert.Equal(404, (await Server(model, o: Options(designer: false)).Server.HandleAsync(Req("GET", "/api/designer/models"))).Status);
        var (server, _, _) = Server(model);
        Assert.Equal(403, (await server.HandleAsync(Req("GET", "/api/designer/models", token: null))).Status);
        Assert.Equal(403, (await server.HandleAsync(Req("POST", "/api/designer/sessions", new { }, token: "wrong"))).Status);
        Assert.Equal(405, (await server.HandleAsync(Req("POST", "/api/designer/models", new { }))).Status);
        Assert.Equal(404, (await server.HandleAsync(Req("GET", "/api/designer/sessions/77"))).Status);
        Assert.Equal(400, (await server.HandleAsync(Req("POST", "/api/designer/sessions", new { model = "gpt-9" }))).Status);
        // Without the route object the paths are 404, never the read-only 405 (and never another route).
        var bare = new SupervisorServer(Options(), new FakeSupervisorData());
        Assert.Equal(404, (await bare.HandleAsync(Req("POST", "/api/designer/sessions", new { }))).Status);
        // Bad bodies.
        long id = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { }))).GetProperty("id").GetInt64();
        Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/messages", new { text = "" }))).Status);
        Assert.Equal(400, (await server.HandleAsync(new SupervisorRequest("POST", $"/api/designer/sessions/{id}/canvas", new Dictionary<string, string>(), Token, "{not json"))).Status);
        Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "x" }))).Status); // empty canvas
    }
}
