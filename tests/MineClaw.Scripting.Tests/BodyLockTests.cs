using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;

namespace MineClaw.Scripting.Tests;

/// <summary>Scripted brain for body-lock tests: a queue of responses, then plain text.</summary>
public sealed class StepLlm : ILlmClient
{
    private readonly ConcurrentQueue<Func<LlmRequest, LlmResponse>> _steps = new();
    public ConcurrentQueue<LlmRequest> Requests { get; } = new();
    public int Brain => Requests.Count(r => r.Tier == ModelTier.Main);

    public StepLlm Tool(string name, object input, string id = "tu_1") { _steps.Enqueue(r => Resp(r, StopReasons.ToolUse, new LlmToolUse(id, name, JsonSerializer.SerializeToElement(input)))); return this; }
    public StepLlm Text(string text) { _steps.Enqueue(r => Resp(r, StopReasons.EndTurn, new LlmText(text))); return this; }

    private static LlmResponse Resp(LlmRequest r, string stop, params LlmBlock[] b) => new(b, stop, LlmUsage.Zero, r.Model ?? "claude-opus-5-5");

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (request.Tier == ModelTier.Main && _steps.TryDequeue(out var s)) return Task.FromResult(s(request));
        return Task.FromResult(Resp(request, StopReasons.EndTurn, new LlmText("")));
    }

    public string ResultOf(string id) => Requests.SelectMany(r => r.Messages).SelectMany(m => m.Content).OfType<LlmToolResult>()
        .LastOrDefault(t => t.ToolUseId == id)?.Content ?? "";
}

/// <summary>
/// Body lock, not brain lock: a running script keeps the body while the brain keeps chatting, thinking and coordinating.
/// Only danger, a trusted "stop"/"wait" and the brain's own script ops pause it.
/// </summary>
public class BodyLockTests
{
    private static async Task<(BotHost H, FakeGameClient C, ScriptBridge B, CancellationTokenSource Cts)> Start(StepLlm llm, Action<AgentOptions>? configure = null)
    {
        var (h, c, b) = BotScriptTests.Bot(o => { o.InlineToolWaitSeconds = 1; o.Self.TrustedPlayers = ["kiwi"]; configure?.Invoke(o); },
            new ScriptOptions { MaxPauseMinutes = 600 }, llm);
        var cts = new CancellationTokenSource();
        _ = h.Runtime.Agent.RunAsync(cts.Token);
        await Task.Delay(50);
        var r = await h.Run("script", new { op = "run", name = "cobble_loop", code = "for (let i = 1; i <= 400; i++) { wait(0.05); prog(i / 4) }" });
        Assert.Contains("running in background", r.Text);
        await Rig.Until(() => b.Controller.Current?.ProgressPct > 0, 5000);
        return (h, c, b, cts);
    }

    [Fact]
    public async Task Chat_during_a_script_gets_a_reply_while_the_script_keeps_progressing()
    {
        var llm = new StepLlm().Text("going great, still mining!");
        var (h, c, b, cts) = await Start(llm);
        try
        {
            var before = b.Controller.Current!.ProgressPct ?? 0;
            c.RaiseChat("<robin> claudechan how's it going?");
            await Rig.Until(() => c.Said.Any(s => s.Contains("going great")), 5000);
            Assert.Equal(ScriptStatus.Running, b.Controller.Current!.Status); // chat never paused it
            await Rig.Until(() => (b.Controller.Current?.ProgressPct ?? 0) > before + 2, 5000);
            Assert.Contains("[body: script cobble_loop", llm.Requests.First(r => r.Tier == ModelTier.Main).Messages[^1].Content.OfType<LlmText>().First().Text);
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task A_body_tool_during_a_script_answers_busy_with_queue_and_replace_options()
    {
        var llm = new StepLlm().Tool("do", new { action = "mine", args = new { block = "dirt" } }).Text("(silent)");
        var (h, c, b, cts) = await Start(llm);
        try
        {
            c.RaiseChat("<robin> claudechan mine some dirt");
            await Rig.Until(() => llm.ResultOf("tu_1").Length > 0, 5000);
            var busy = llm.ResultOf("tu_1");
            Assert.StartsWith("body busy: script cobble_loop", busy);
            Assert.Contains("op=pause", busy);
            Assert.Contains("op=replace", busy);
            Assert.Contains("queue:true", busy);
            Assert.DoesNotContain(c.CallList, x => x.StartsWith("mine"));
            Assert.Equal(ScriptStatus.Running, b.Controller.Current!.Status); // not superseded
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task A_queued_body_tool_runs_after_the_script()
    {
        var llm = new StepLlm().Tool("do", new { action = "mine", args = new { block = "dirt" }, queue = true }).Text("(silent)");
        var (h, c, b, cts) = await Start(llm);
        try
        {
            c.RaiseChat("<robin> claudechan mine some dirt after that");
            await Rig.Until(() => llm.ResultOf("tu_1").Length > 0, 5000);
            Assert.StartsWith("queued behind script", llm.ResultOf("tu_1"));
            Assert.DoesNotContain(c.CallList, x => x.StartsWith("mine"));
            Assert.Equal(ScriptStatus.Running, b.Controller.Current!.Status);
            b.Controller.Current!.Stop("done for the test"); // the script ends → the queued job takes the body
            await Rig.Until(() => c.CallList.Any(x => x.StartsWith("mine")), 5000);
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task Hive_work_during_a_script_runs_at_once_without_disturbing_it()
    {
        var spawned = new ConcurrentQueue<string>();
        var llm = new StepLlm().Tool("hive", new { op = "spawn", task = "work the other gen cell" }).Text("67 is on it");
        var (h, c, b, cts) = await Start(llm, o => { });
        // A free (non-body) tool like the main process's hive tool.
        h.Runtime.Tools.Register("hive", "hive", ToolSchema.Create().Str("op").Str("task", required: false).Build(),
            (i, _) => { spawned.Enqueue(i.Str("task") ?? ""); return Task.FromResult(ToolResult.Success("ok: 67 spawning")); }, tiers: ToolTiers.Main);
        try
        {
            c.RaiseChat("<kiwi> claudechan get a clone on the other gen cell");
            await Rig.Until(() => c.Said.Any(s => s.Contains("67 is on it")), 5000);
            Assert.Equal(["work the other gen cell"], spawned);
            Assert.Equal("ok: 67 spawning", llm.ResultOf("tu_1"));
            Assert.Equal(ScriptStatus.Running, b.Controller.Current!.Status);
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task Danger_still_pauses_the_script()
    {
        var (h, _, b, cts) = await Start(new StepLlm());
        try
        {
            h.Runtime.Events.Post(GameEvent.LowHealth(5));
            await Rig.Until(() => b.Controller.Current?.Status == ScriptStatus.Paused, 5000);
            Assert.Equal(PauseKind.Danger, b.Controller.Current!.PausedFor);
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Theory]
    [InlineData("claudechan stop")]
    [InlineData("claudechan wait")]
    public async Task A_trusted_stop_or_wait_still_pauses_it_and_the_reflex_never_kills_it(string line)
    {
        var (h, c, b, cts) = await Start(new StepLlm());
        try
        {
            c.RaiseChat($"<kiwi> {line}");
            await Rig.Until(() => b.Controller.Current?.Status == ScriptStatus.Paused, 5000);
            Assert.Equal(PauseKind.Stop, b.Controller.Current!.PausedFor);
            await Task.Delay(200);
            Assert.NotNull(b.Controller.Current); // paused, not cancelled by the brainstem's stop reflex
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }
}
