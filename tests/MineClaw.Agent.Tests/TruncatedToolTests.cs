using System.Net;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class TruncatedToolTests
{
    private static LlmToolUse Use(string name, object input, string id) => new(id, name, JsonSerializer.SerializeToElement(input));
    private static EventBatch Chat() => new([GameEvent.Chat("fox", "clawd do the work")]);
    private static List<LlmToolResult> Results(AgentRuntime rt) => rt.Context.Episodes.Last().Messages
        .SelectMany(m => m.Content).OfType<LlmToolResult>().ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capped_brain_batch_pairs_every_call_without_starting_effects(bool streaming)
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Brainstem.StreamingExecution = streaming);
        int effects = 0;
        rt.Tools.Register("effect", "effect", ToolSchema.Empty, (_, _) =>
        { effects++; return Task.FromResult(ToolResult.Success("changed world")); });
        var uses = new[] { Use("effect", new { }, "a"), Use("effect", new { }, "b") };
        llm.Then(r =>
        {
            if (streaming) foreach (var u in uses) r.OnToolUse!(u);
            return llm.Resp(r, StopReasons.MaxTokens, uses);
        }).ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(Chat(), default);
        Assert.Equal(0, effects);
        Assert.Single(llm.Requests); // do not automatically retry a partial plan
        var results = Results(rt);
        Assert.Equal(["a", "b"], results.Select(r => r.ToolUseId));
        Assert.All(results, r => { Assert.True(r.IsError); Assert.Contains("truncated", r.Content); });
    }

    [Fact]
    public async Task Capped_reply_keeps_the_actual_early_lookup_and_cancels_an_already_started_walk()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Brainstem.MoveInlineWaitMs = 0);
        int reads = 0, walks = 0, effects = 0;
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readResult = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var walkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.Tools.Register("look", "look", ToolSchema.Empty, async (_, ct) =>
        { Interlocked.Increment(ref reads); readStarted.SetResult(); return await readResult.Task.WaitAsync(ct); });
        rt.Tools.Register("goto", "walk", ToolSchema.Create().Str("to").Build(), async (_, ct) =>
        {
            Interlocked.Increment(ref walks); walkStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.TrySetResult(); }
            return ToolResult.Success("arrived");
        }, longRunning: true);
        rt.Tools.Register("effect", "effect", ToolSchema.Empty, (_, _) =>
        { effects++; return Task.FromResult(ToolResult.Success("changed")); });
        var look = Use("look", new { }, "read");
        var walk = Use("goto", new { to = "fox" }, "walk");
        var effect = Use("effect", new { }, "effect");
        llm.ThenAsync(async (r, ct) =>
        {
            r.OnToolUse!(look);
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            r.OnToolUse(walk);
            await walkStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            r.OnToolUse(effect);
            readResult.SetResult(ToolResult.Success("observed before truncation"));
            return llm.Resp(r, StopReasons.MaxTokens, look, walk, effect);
        }).ThenText("(silent)");
        try
        {
            await rt.Agent.HandleBatchAsync(Chat(), default);
            Assert.Equal(0, effects);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var results = Results(rt);
            Assert.Equal(["read", "walk", "effect"], results.Select(r => r.ToolUseId));
            Assert.False(results[0].IsError);
            Assert.Equal("observed before truncation", results[0].Content);
            Assert.True(results[1].IsError);
            Assert.Contains("already started", results[1].Content);
            Assert.Contains("cancel", results[1].Content);
            Assert.True(results[2].IsError);
            Assert.Equal(1, reads);
            Assert.Equal(1, walks);
        }
        finally { rt.Activities.Cancel("test finished"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Movement_with_an_effectful_tail_waits_for_a_valid_terminal_response(bool capped)
    {
        var (rt, llm, _) = TestRuntime.Create();
        int walks = 0, mines = 0;
        rt.Tools.Register("goto", "walk", ToolSchema.Create().Str("to").Build(), (_, _) =>
        { Interlocked.Increment(ref walks); return Task.FromResult(ToolResult.Success("arrived")); }, longRunning: true);
        rt.Tools.Register("mine", "mine", ToolSchema.Create().Str("block").Build(), (_, _) =>
        { Interlocked.Increment(ref mines); return Task.FromResult(ToolResult.Success("mined")); }, longRunning: true);
        rt.ApplyBrainSplit();
        var use = Use("do", new { action = "goto", args = new { to = "gen" },
            then = new[] { new { action = "mine", args = new { block = "dirt" } } } }, "chain");
        llm.ThenAsync(async (r, _) =>
        {
            r.OnToolUse!(use);
            // Give any wrongly scheduled early chain time to run; no timing assertion is used for expected execution.
            await Task.Delay(150);
            Assert.Equal(0, Volatile.Read(ref walks));
            Assert.Equal(0, Volatile.Read(ref mines));
            return llm.Resp(r, capped ? StopReasons.MaxTokens : StopReasons.ToolUse, use);
        }).ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(Chat(), default);
        if (!capped) await TestWaits.UntilAsync(() => Volatile.Read(ref mines) == 1);
        Assert.Equal(capped ? 0 : 1, walks);
        Assert.Equal(capped ? 0 : 1, mines);
        Assert.Equal(capped, Assert.Single(Results(rt)).IsError);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("into_void")]
    [InlineData("allow_parkour")]
    public async Task Movement_with_irreversible_or_void_options_is_not_started_early(string option)
    {
        var (rt, llm, _) = TestRuntime.Create();
        int walks = 0;
        rt.Tools.Register("goto", "walk", ToolSchema.Create().Str("to").Build(), (_, _) =>
        { Interlocked.Increment(ref walks); return Task.FromResult(ToolResult.Success("moved")); }, longRunning: true);
        var args = new Dictionary<string, object> { ["to"] = "gen", [option] = option == "build" ? true : option == "into_void" ? "jump" : "void" };
        var use = Use("goto", args, "hazard");
        llm.ThenAsync(async (r, _) =>
        {
            r.OnToolUse!(use);
            await Task.Delay(150);
            Assert.Equal(0, Volatile.Read(ref walks));
            return llm.Resp(r, StopReasons.MaxTokens, use);
        }).ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(Chat(), default);
        Assert.Equal(0, walks);
        Assert.True(Assert.Single(Results(rt)).IsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capped_worker_never_executes_tools_or_reports_partial_success(bool withTools)
    {
        var (rt, llm, _) = TestRuntime.Create();
        int effects = 0;
        rt.Tools.Register("mine", "mine", ToolSchema.Empty, (_, _) =>
        { effects++; return Task.FromResult(ToolResult.Success("mined")); });
        llm.Then(r => llm.Resp(r, StopReasons.MaxTokens, withTools
            ? [new LlmText("ok: finished"), Use("mine", new { }, "a"), Use("mine", new { }, "b")]
            : [new LlmText("ok: finished")])).ThenText("ok: finished");
        var outcome = await rt.Worker!.RunAsync("mine", null, null, default);
        Assert.False(outcome.Ok);
        Assert.Equal(StopReasons.MaxTokens, outcome.StopReason);
        Assert.Equal(0, effects);
        Assert.Equal(0, outcome.ToolCalls);
        Assert.Equal(1, outcome.Calls);
        Assert.Contains("truncated", outcome.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capped_worker_wrap_up_is_not_a_successful_summary(bool withTools)
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Worker.MaxRounds = 0);
        llm.Then(r => llm.Resp(r, StopReasons.MaxTokens, withTools
            ? [new LlmText("ok: finished"), Use("mine", new { }, "wrap")]
            : [new LlmText("ok: finished")]));
        var outcome = await rt.Worker!.RunAsync("mine", null, null, default);
        Assert.False(outcome.Ok);
        Assert.Equal(StopReasons.MaxTokens, outcome.StopReason);
        Assert.Contains("truncated", outcome.Summary);
        Assert.Equal(1, outcome.Calls);
        Assert.Equal(0, outcome.ToolCalls);
    }

    private sealed class ReplyHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }

    [Theory]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    [InlineData("ollama", false)]
    [InlineData("ollama", true)]
    public async Task Provider_capped_tool_reply_reaches_the_brain_without_authorizing_effects(string provider, bool stream)
    {
        var (rt, llm, _) = TestRuntime.Create();
        int effects = 0;
        rt.Tools.Register("effect", "effect", ToolSchema.Empty, (_, _) =>
        { effects++; return Task.FromResult(ToolResult.Success("changed")); });
        string body;
        if (provider == "anthropic")
        {
            const string message = """{"id":"msg_1","type":"message","role":"assistant","model":"test-model","content":[{"type":"tool_use","id":"a","name":"effect","input":{}}],"stop_reason":"max_tokens","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":20}}""";
            body = !stream ? message :
                "event: message_start\ndata: " + """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"test-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""" + "\n\n" +
                "event: content_block_start\ndata: " + """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"a","name":"effect","input":{}}}""" + "\n\n" +
                "event: content_block_delta\ndata: " + """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{}"}}""" + "\n\n" +
                "event: content_block_stop\ndata: " + """{"type":"content_block_stop","index":0}""" + "\n\n" +
                "event: message_delta\ndata: " + """{"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null},"usage":{"output_tokens":20}}""" + "\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        }
        else
        {
            body = """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"effect","arguments":{}}}]},"done":true,"done_reason":"length","prompt_eval_count":10,"eval_count":20}""" + "\n";
        }
        using var http = new HttpClient(new ReplyHandler(body, stream && provider == "anthropic" ? "text/event-stream" : "application/json"));
        var options = new LlmOptions { ApiKey = "fake", MaxRetries = 0, Ollama = new OllamaOptions { Endpoint = "http://ollama.test" } };
        ILlmClient client = provider == "anthropic" ? new AnthropicLlmClient(options, http) : new OllamaLlmClient(options, http);
        llm.ThenAsync(async (r, ct) => await client.CompleteAsync(new LlmRequest
        {
            Tier = r.Tier, Model = r.Model, System = r.System, Tools = r.Tools, Messages = r.Messages,
            Interruptible = stream, OnToolUse = stream ? r.OnToolUse : null,
        }, ct)).ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(Chat(), default);
        Assert.Equal(0, effects);
        Assert.Single(llm.Requests);
        var result = Assert.Single(Results(rt));
        Assert.True(result.IsError);
        Assert.Contains("truncated", result.Content);
    }
}
