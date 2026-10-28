using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Tests;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// script{name, goal}: the brain commissions a script, a separate Opus author context writes/lints/revises/saves it, and the
/// brain gets ONLY the final facts (no code, no drafts, no thinking, no fixed lint errors).
/// </summary>
public class ScriptAuthorTests
{
    private const string Thought = "SECRET-CHAIN-OF-THOUGHT";
    private const string Draft = "-- purpose: first draft\nlocal m = require(\"evil\")\nreturn \"x\"";

    /// <summary>Brain steps (queue) on brain calls; author calls ("author-script") get a lint-failing draft, then the exemplar.</summary>
    private sealed class Llm : ILlmClient
    {
        public ConcurrentQueue<LlmRequest> Requests { get; } = new();
        public ConcurrentQueue<Func<LlmRequest, LlmResponse>> Brain { get; } = new();
        private int _author;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            var model = request.Model ?? "claude-opus-5-5";
            if (request.Purpose == "author-script")
            {
                var text = _author++ == 0 ? "Here is my first try:\n```lua\n" + Draft + "\n```" : "```lua\n" + ScriptPrompts.Exemplar + "\n```";
                return Task.FromResult(new LlmResponse([new LlmThinking(Thought, "sig"), new LlmText(text)], StopReasons.EndTurn,
                    new LlmUsage(3000, 9000, 0, 2500), model));
            }
            if (request.Tier == ModelTier.Main && Brain.TryDequeue(out var step)) return Task.FromResult(step(request));
            return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, model));
        }

        public LlmRequest[] Authors => Requests.Where(r => r.Purpose == "author-script").ToArray();
    }

    private static void NoAuthorInternals(string brainFacing)
    {
        Assert.DoesNotContain(Thought, brainFacing);
        Assert.DoesNotContain("first draft", brainFacing);
        Assert.DoesNotContain("first try", brainFacing);
        Assert.DoesNotContain("require", brainFacing);
        Assert.DoesNotContain("lint error", brainFacing);
        Assert.DoesNotContain("version 1", brainFacing);
        Assert.DoesNotContain("ensure\"pickaxe\"", brainFacing); // no code
        Assert.DoesNotContain("upto(", brainFacing);
    }

    [Fact]
    public async Task Goal_authors_apart_revises_lint_inside_and_returns_only_the_final_facts()
    {
        var llm = new Llm();
        var (h, _, b) = BotScriptTests.Bot(llm: llm);
        var r = await h.Run("script", new { name = "cobble_store", goal = "mine n cobble at the gen and store it" });
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("ok: wrote cobble_store v1 | mine n cobble at the gen, store it | args(n,at) | lint ok", r.Text);
        Assert.Contains("$", r.Text);
        NoAuthorInternals(r.Text);
        Assert.True(r.Text.Length < 400, r.Text); // ≈100 tokens at most

        // Saved: the library has the exemplar (author-tagged as the Opus brain tier).
        var sc = b.Library.Get("cobble_store")!.Script!;
        Assert.Equal(ScriptPrompts.Exemplar, sc.Code);
        Assert.Equal("main/claude-opus-5-5", sc.Author);

        // The author context: its own cheat-sheet prefix, 100K output, Opus 5.5, the lint feedback in round 2.
        var authors = llm.Authors;
        Assert.Equal(2, authors.Length);
        Assert.All(authors, a =>
        {
            Assert.Equal(100_000, a.MaxTokens);
            Assert.Equal("claude-opus-5-5", a.Model);
            Assert.StartsWith("You write Lua scripts", a.System[0].Text);
        });
        Assert.Contains("require", authors[1].Messages[^1].Text);
        // The brain never got a call for this (it only issued the tool call).
        Assert.DoesNotContain(llm.Requests, q => q.Purpose != "author-script");
    }

    [Fact]
    public async Task The_brain_history_gains_only_the_summary()
    {
        var llm = new Llm();
        llm.Brain.Enqueue(q => new LlmResponse([new LlmToolUse("tu_a", "script",
            JsonSerializer.SerializeToElement(new { name = "cobble_store", goal = "mine n cobble at the gen and store it" }))],
            StopReasons.ToolUse, LlmUsage.Zero, q.Model ?? "claude-opus-5-5"));
        llm.Brain.Enqueue(q => new LlmResponse([new LlmText("(silent)")], StopReasons.EndTurn, LlmUsage.Zero, q.Model ?? "claude-opus-5-5"));
        var (h, c, _) = BotScriptTests.Bot(o => { o.InlineToolWaitSeconds = 1; o.Self.TrustedPlayers = ["kiwi"]; }, llm: llm);
        using var cts = new CancellationTokenSource();
        _ = h.Runtime.Agent.RunAsync(cts.Token);
        try
        {
            await Task.Delay(50);
            c.RaiseChat("<kiwi> claudechan write a script that mines cobble and stores it");
            await Rig.Until(() => llm.Requests.Any(q => q.Purpose != "author-script" && q.Tier == ModelTier.Main
                && q.Messages.SelectMany(m => m.Content).OfType<LlmToolResult>().Any(t => t.ToolUseId == "tu_a")), 10_000);
            var next = llm.Requests.First(q => q.Purpose != "author-script"
                && q.Messages.SelectMany(m => m.Content).OfType<LlmToolResult>().Any(t => t.ToolUseId == "tu_a"));
            var result = next.Messages.SelectMany(m => m.Content).OfType<LlmToolResult>().Single(t => t.ToolUseId == "tu_a").Content;
            Assert.Contains("wrote cobble_store v1", result);
            // Nothing of the author's context anywhere in the brain's request: not the drafts, the thinking, the code or the reference.
            var whole = JsonSerializer.Serialize(next.Messages.Select(m => m.Content.Select(bl => bl switch
            {
                LlmText t => t.Text, LlmToolResult tr => tr.Content, LlmThinking th => th.Thinking, LlmToolUse u => u.Input.GetRawText(), _ => "",
            })));
            NoAuthorInternals(whole);
            Assert.DoesNotContain(next.System, s => s.Text.StartsWith("You write Lua scripts"));
            Assert.Equal(h.Runtime.Options.Llm.MainMaxTokens, next.MaxTokens ?? h.Runtime.Options.Llm.MainMaxTokens); // brain turns unchanged
        }
        finally { cts.Cancel(); }
    }

    [Fact]
    public void Goal_is_documented_for_the_brain_only()
    {
        var (h, _, _) = BotScriptTests.Bot();
        Assert.Contains(ScriptPrompts.AuthorLine.Trim(), h.Runtime.Agent.SystemPrompt[0].Text);
        Assert.DoesNotContain("goal=", h.Runtime.Worker!.SystemPrompt[0].Text);
        Assert.True(Math.Ceiling(ScriptPrompts.AuthorLine.Trim().Length / 3.5) <= 40);
    }

    [Fact]
    public async Task Only_the_Opus_brain_commissions_scripts()
    {
        var llm = new Llm();
        var (h, _, _) = BotScriptTests.Bot(llm: llm);
        var worker = await h.RunAs(ModelTier.Worker, "script", new { name = "x", goal = "mine stuff" });
        Assert.False(worker.Ok);
        Assert.Equal(ScriptPrompts.BrainOnly, worker.Text);
        Assert.Empty(llm.Authors);
    }

    [Fact]
    public async Task A_failed_job_reports_only_the_blocker()
    {
        var llm = new BadAuthor();
        var (h, _, b) = BotScriptTests.Bot(llm: llm);
        var r = await h.Run("script", new { name = "nope", goal = "do it" });
        Assert.False(r.Ok);
        Assert.StartsWith("script nope not written: checks still failing:", r.Text);
        Assert.DoesNotContain(Thought, r.Text);
        Assert.DoesNotContain("```", r.Text);
        Assert.Null(b.Library.Get("nope"));
        Assert.Equal(3, llm.Calls); // AuthorMaxVersions
    }

    private sealed class BadAuthor : ILlmClient
    {
        public int Calls;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse([new LlmThinking(Thought, "s"), new LlmText("```lua\n" + Draft + "\n```")], StopReasons.EndTurn,
                new LlmUsage(10, 10, 0, 0), request.Model ?? "claude-opus-5-5"));
        }
    }

    [Fact]
    public async Task Show_returns_a_short_excerpt()
    {
        var llm = new Llm();
        var (h, _, _) = BotScriptTests.Bot(llm: llm);
        Assert.True((await h.Run("script", new { name = "cobble_store", goal = "mine and store" })).Ok);
        var show = await h.Run("script", new { op = "show", name = "cobble_store" });
        Assert.True(show.Ok, show.Text);
        Assert.StartsWith("cobble_store v1: -- purpose:", show.Text);
        Assert.True(show.Text.Length <= 640);
    }
}
