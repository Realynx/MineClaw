using System.Net;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class ProviderLengthStopTests
{
    private const string Model = "local-test-model";

    private sealed class LengthStoppedReply(bool stream) : HttpMessageHandler
    {
        public int ChatCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(Model, body.RootElement.GetProperty("model").GetString());
            if (request.RequestUri!.AbsolutePath == "/api/show")
                return Reply("""{"capabilities":["completion","tools","thinking"]}""", "application/json");

            Assert.Equal("/api/chat", request.RequestUri.AbsolutePath);
            Assert.Equal(stream, body.RootElement.GetProperty("stream").GetBoolean());
            ChatCalls++;
            // Ollama sends complete tool calls before its terminal chunk; only that later chunk reveals truncation.
            const string call = """{"model":"local-test-model","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"effect","arguments":{}}}]},"done":false}""";
            const string done = """{"model":"local-test-model","message":{"role":"assistant","content":""},"done":true,"done_reason":"length","prompt_eval_count":10,"eval_count":20}""";
            const string plain = """{"model":"local-test-model","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"effect","arguments":{}}}]},"done":true,"done_reason":"length","prompt_eval_count":10,"eval_count":20}""";
            return Reply(stream ? call + "\n" + done + "\n" : plain, stream ? "application/x-ndjson" : "application/json");
        }

        private static HttpResponseMessage Reply(string body, string mediaType) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Length_stopped_provider_tool_calls_keep_max_tokens_and_do_not_authorize_effects(bool stream)
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Brainstem.StreamingExecution = stream);
        int effects = 0, callbacks = 0;
        rt.Tools.Register("effect", "effect", ToolSchema.Empty, (_, _) =>
        { effects++; return Task.FromResult(ToolResult.Success("changed world")); });
        using var handler = new LengthStoppedReply(stream);
        using var http = new HttpClient(handler);
        using var client = new OllamaLlmClient(new LlmOptions { Ollama = new OllamaOptions { Endpoint = "http://ollama.test" } }, http);
        LlmResponse? parsed = null;
        llm.ThenAsync(async (r, ct) =>
        {
            parsed = await client.CompleteAsync(new LlmRequest
            {
                Model = Model, Tier = r.Tier, System = r.System, Tools = r.Tools, Messages = r.Messages,
                Interruptible = stream,
                OnToolUse = stream ? use => { callbacks++; r.OnToolUse!(use); } : null,
            }, ct);
            return parsed;
        }).ThenText("(silent)");

        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("fox", "clawd do the work")]), default);

        Assert.NotNull(parsed);
        Assert.Equal(StopReasons.MaxTokens, parsed.StopReason);
        Assert.Equal(Model, parsed.Model);
        Assert.Equal(new LlmUsage(10, 20, 0, 0), parsed.Usage);
        var use = Assert.Single(parsed.ToolUses);
        Assert.Equal(stream ? 1 : 0, callbacks);
        Assert.Equal(1, handler.ChatCalls);
        Assert.Single(llm.Requests);
        Assert.Equal(0, effects);
        var result = Assert.Single(rt.Context.Episodes.Last().Messages.SelectMany(m => m.Content).OfType<LlmToolResult>());
        Assert.Equal(use.Id, result.ToolUseId);
        Assert.True(result.IsError);
        Assert.Contains("truncated", result.Content);
    }
}
