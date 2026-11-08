using System.Text.Json;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;
using static MineClaw.App.Tests.DesignerTests;

namespace MineClaw.App.Tests;

/// <summary>
/// The Design Studio's tool loop under the failures a real model produces: a reply cut at the output limit, a model stuck on
/// failing calls, a call failing after the first, a context that grows past its budget, the round limit, and the transcript
/// the next message starts from. A message never loses the edits it made, and the user is told why it stopped.
/// </summary>
[Collection(GameDataDefaultCollection.Name)]
public class DesignerAgentLoopTests
{
    private static readonly DesignerModel Opus = new("main", "Opus 5.5 (brain)", LlmProviders.Anthropic, "claude-opus-5-5", ModelTier.Main, null, "$$$");

    private static DesignerSessionRecord Session() => new() { Id = 1, Owner = "poofimafox", Canvas = new DesignerCanvas() };

    private static object Floor(int y = 0) => new { b = "cobblestone", from = new[] { 10, y, 10 }, to = new[] { 14, y, 14 } };

    private static Task<DesignerTurnResult> Run(ILlmClient llm, DesignerSessionRecord s, DesignerOptions? o = null, LlmOptions? llmOptions = null,
        IReadOnlyList<DesignerMessageRecord>? history = null) =>
        new DesignerAgent(llm, llmOptions ?? Llm(), null, o).RunAsync(s, history ?? [], Opus, "build a floor", null, "poofimafox", CancellationToken.None);

    /// <summary>The tool results the model was sent in request <paramref name="i"/> (its last message).</summary>
    private static List<LlmToolResult> ResultsSent(ScriptedModel m, int i) => m.Requests.ElementAt(i).Messages[^1].Content.OfType<LlmToolResult>().ToList();

    [Fact]
    public async Task A_reply_cut_at_the_output_limit_runs_its_complete_calls_and_asks_for_the_cut_one_again()
    {
        var model = new ScriptedModel(
            r =>
            {
                var cut = Tools(r, ("box", Floor()), ("place", new { b = "planks" }));
                return cut with { StopReason = StopReasons.MaxTokens };
            },
            r => Tools(r, ("place", new { b = "planks", at = new[] { new[] { 12, 1, 12 } } })),
            r => Say(r, "Laid a floor and a plank on it."));
        var s = Session();
        var turn = await Run(model, s);
        Assert.Equal(["fill", "place"], turn.Ops.Select(o => o.Kind));
        Assert.Equal(25 + 1, s.Canvas.Count);
        var sent = ResultsSent(model, 1);
        Assert.False(sent[0].IsError);
        Assert.True(sent[1].IsError);
        Assert.Contains("output limit", sent[1].Content);
        Assert.Equal("Laid a floor and a plank on it.", turn.Reply);
        Assert.Null(turn.Stopped);
    }

    [Fact]
    public async Task A_model_stuck_on_failing_calls_is_stopped_with_the_last_error()
    {
        var model = new ScriptedModel(r => Tools(r, ("box", new { b = "cobblestone", from = "here", to = new[] { 1, 1, 1 } })));
        var turn = await Run(model, Session(), new DesignerOptions { MaxFailedRounds = 3 });
        Assert.Equal(3, turn.Calls);
        Assert.Empty(turn.Ops);
        Assert.NotNull(turn.Stopped);
        Assert.Contains("3 failed attempts", turn.Reply);
        Assert.Contains("usage: box {b, from, to, mode?}", turn.Reply);
        // The errors said what was wrong and how to call it.
        Assert.Contains("$.from", ResultsSent(model, 1)[0].Content);
    }

    [Fact]
    public async Task A_call_failing_after_the_first_keeps_the_edits_and_says_why()
    {
        var model = new ScriptedModel(r => Tools(r, ("box", Floor())), _ => throw new HttpRequestException("overloaded"));
        var s = Session();
        var turn = await Run(model, s);
        Assert.Single(turn.Ops);
        Assert.Equal(25, s.Canvas.Count);
        Assert.Equal(1, turn.Calls);
        Assert.True(turn.CostUsd > 0);
        Assert.Contains("the model call failed: overloaded", turn.Reply);
        Assert.Contains("the edits so far are kept", turn.Reply);
    }

    [Fact]
    public async Task A_first_call_failing_throws_for_the_route_to_answer()
    {
        var model = new ScriptedModel(_ => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(model, Session()));
    }

    [Fact]
    public async Task The_route_returns_the_edits_of_a_message_whose_later_call_failed()
    {
        var model = new ScriptedModel(r => Tools(r, ("box", Floor())), _ => throw new HttpRequestException("overloaded"));
        var (server, routes, _) = Server(model);
        long id = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { player = "poofimafox" }))).GetProperty("id").GetInt64();
        var m = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/messages", new { text = "a floor", canvas = new { rev = 0 }, player = "poofimafox" })));
        // The browser gets the ops the server's canvas holds (no silent drift), and the spend of the call that ran.
        Assert.Equal(25, m.GetProperty("ops")[0].GetProperty("cells").GetArrayLength());
        Assert.Equal(routes.Store.Get(id)!.Canvas.Rev, m.GetProperty("rev").GetInt32());
        Assert.True(m.GetProperty("session_cost_usd").GetDouble() > 0);
        Assert.Contains("overloaded", m.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task The_round_limit_says_how_to_continue()
    {
        var model = new ScriptedModel(r => Tools(r, ("box", Floor())));
        var turn = await Run(model, Session(), new DesignerOptions { MaxRounds = 2 });
        Assert.Equal(2, turn.Calls);
        Assert.Contains("go on", turn.Reply);
    }

    [Fact]
    public async Task Older_tool_results_are_cut_to_fit_the_context_budget()
    {
        var s = Session();
        DesignerTools.Run(s.Canvas, "box", JsonSerializer.SerializeToElement(new { b = "planks", from = new[] { 0, 0, 0 }, to = new[] { 63, 0, 63 } }));
        var model = new ScriptedModel(r => Tools(r, ("describe", new { y = 0 })), r => Say(r, "It is one big floor."));
        // Room for the prefix, the output and the brief, not for a whole 64×64 layer as well.
        var o = new DesignerOptions { MaxTokens = 256 };
        var system = DesignerTools.System("poofimafox", s.Canvas.Version);
        o.MaxContextTokens = ContextManager.FixedTokens([new LlmSystemBlock(system)], DesignerTools.All, o.MaxTokens) + 600;
        var turn = await Run(model, s, o);
        Assert.Equal("It is one big floor.", turn.Reply);
        var layer = ResultsSent(model, 1)[0].Content;
        Assert.True(layer.Length <= 80, $"{layer.Length} chars sent");
    }

    [Fact]
    public async Task A_conversation_that_cannot_fit_is_not_sent()
    {
        var model = new ScriptedModel(r => Say(r, "never"));
        var turn = await Run(model, Session(), new DesignerOptions { MaxContextTokens = 1024, MaxTokens = 1000 });
        Assert.Empty(model.Requests);
        Assert.Contains("no longer fits", turn.Reply);
    }

    [Fact]
    public void An_ollama_models_context_is_its_num_ctx()
    {
        var o = Llm();
        o.Ollama.Endpoint = "http://localhost:11434";
        o.Ollama.DefaultNumCtx = 8192;
        var agent = new DesignerAgent(new ScriptedModel(), o, null, new DesignerOptions { MaxContextTokens = 32_000 });
        Assert.Equal(8192, agent.ContextBudget(new DesignerModel("profile:ds", "ds", LlmProviders.Ollama, "deepseek", ModelTier.Main, "ds", null)));
        Assert.Equal(32_000, agent.ContextBudget(Opus));
    }

    [Fact]
    public void The_transcript_starts_with_the_user_alternates_and_remembers_the_edits()
    {
        DesignerMessageRecord M(int seq, string role, string text, params DesignerOpSummary[] ops) =>
            new(1, seq, role, text, DateTimeOffset.UnixEpoch, null, 0, ops);
        var history = new[]
        {
            M(1, "assistant", "Switched to a mine design."), // a session note before any user line
            M(2, "user", "a floor"),
            M(3, "assistant", "Laid a floor.", new DesignerOpSummary("fill", "filled 5×1×5 with cobblestone", 25)),
            M(4, "assistant", "Saved \"hut\" as design #3."),
            M(5, "user", "walls too"), // its reply never came (a cancelled message)
        };
        var messages = DesignerConversation.Build(history, 6, "[canvas] r1\nnow a roof");
        Assert.Equal([LlmRole.User, LlmRole.Assistant, LlmRole.User], messages.Select(m => m.Role));
        Assert.Contains("[your edits: filled 5×1×5 with cobblestone (25)] Laid a floor.", messages[1].Text);
        Assert.Contains("Saved \"hut\"", messages[1].Text);
        Assert.StartsWith("walls too", messages[2].Text);
        Assert.EndsWith("now a roof", messages[2].Text);
        // One turn back: only the newest exchange.
        Assert.Single(DesignerConversation.Build(history, 1, "x"));
    }

    [Fact]
    public void Tools_take_whole_numbers_written_as_decimals_and_name_their_tools()
    {
        var c = new DesignerCanvas();
        var (text, op, err) = DesignerTools.Run(c, "box", JsonDocument.Parse("""{"b":"stone","from":[1.0,0,1],"to":[2,0,2.0]}""").RootElement);
        Assert.False(err, text);
        Assert.Equal(4, op!.Cells.Count);
        var (bad, _, isErr) = DesignerTools.Run(c, "box", JsonDocument.Parse("""{"b":"stone","from":[1.5,0,1],"to":[2,0,2]}""").RootElement);
        Assert.True(isErr);
        Assert.Contains("$.from[0]: expected integer", bad);
        Assert.Contains("spiral_shaft", DesignerTools.Run(new DesignerCanvas { Kind = DesignerCanvas.KindMine }, "dig", default).Text);
    }

    [Fact]
    public void A_tall_varied_canvas_folds_the_middle_of_its_layer_summary()
    {
        var c = new DesignerCanvas();
        // 48 layers, each a different count: nothing merges.
        for (int y = 0; y < 48; y++)
            DesignerTools.Run(c, "line", JsonSerializer.SerializeToElement(new { b = "stone", from = new[] { 0, y, 0 }, to = new[] { y, y, 0 } }));
        var d = c.Describe();
        Assert.Contains("more layer groups (describe y for one)", d);
        Assert.Contains("y0 1 stone", d);
        Assert.Contains("y47 48 stone", d);
    }
}
