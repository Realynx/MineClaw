using System.Net;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Authoring;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Authoring calls: up to 100K output tokens, streamed above <see cref="LlmOptions.StreamAboveTokens"/> (fake HTTP handler,
/// no network), cost-capped per job; brain turns keep their small non-streaming calls.
/// </summary>
public class AuthorStreamingTests
{
    /// <summary>Answers /v1/messages: SSE when the body asks to stream, a plain JSON message otherwise. Records each body.</summary>
    private sealed class FakeHandler(int outputTokens = 42, int thinkingDeltas = 1) : HttpMessageHandler
    {
        public List<JsonDocument> Bodies { get; } = [];
        public bool LastStreamed { get; private set; }

        private string Fill(string s, string? model) => s.Replace("@MODEL@", model).Replace("@OUT@", outputTokens.ToString());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Bodies.Add(body);
            LastStreamed = body.RootElement.TryGetProperty("stream", out var st) && st.ValueKind == JsonValueKind.True;
            var model = body.RootElement.GetProperty("model").GetString();
            if (!LastStreamed)
            {
                var json = """
                {"id":"msg_1","type":"message","role":"assistant","model":"@MODEL@","content":[{"type":"text","text":"plain ok"}],
                 "stop_reason":"end_turn","stop_sequence":null,
                 "usage":{"input_tokens":10,"output_tokens":@OUT@,"cache_read_input_tokens":5,"cache_creation_input_tokens":3,
                          "cache_creation":{"ephemeral_5m_input_tokens":1,"ephemeral_1h_input_tokens":2}}}
                """;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fill(json, model), Encoding.UTF8, "application/json") };
            }
            var sse = new StringBuilder();
            void Ev(string type, string data) => sse.Append("event: ").Append(type).Append("\ndata: ").Append(Fill(data, model)).Append("\n\n");
            Ev("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"@MODEL@","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1,"cache_read_input_tokens":5,"cache_creation_input_tokens":3,"cache_creation":{"ephemeral_5m_input_tokens":1,"ephemeral_1h_input_tokens":2}}}}""");
            Ev("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""");
            for (int i = 0; i < thinkingDeltas; i++)
                Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"secret reasoning that is quite long indeed"}}""");
            Ev("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig123"}}""");
            Ev("content_block_stop", """{"type":"content_block_stop","index":0}""");
            Ev("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""");
            Ev("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"streamed "}}""");
            Ev("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"ok"}}""");
            Ev("content_block_stop", """{"type":"content_block_stop","index":1}""");
            Ev("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":@OUT@}}""");
            Ev("message_stop", """{"type":"message_stop"}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
        }
    }

    private static (AnthropicLlmClient Client, FakeHandler Handler, LlmOptions Options) Client(FakeHandler? handler = null, LlmOptions? o = null)
    {
        o ??= new LlmOptions { ApiKey = "sk-test-not-real", MaxRetries = 0 };
        handler ??= new FakeHandler();
        return (new AnthropicLlmClient(o, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }), handler, o);
    }

    private static LlmRequest Request(int? maxTokens, string? ttl = "1h") => new()
    {
        Tier = ModelTier.Main, MaxTokens = maxTokens, Purpose = "t",
        System = [new LlmSystemBlock("reference", Cache: true, Ttl: ttl)],
        Messages = [LlmMessage.User("write it")],
    };

    [Fact]
    public async Task Above_the_threshold_the_call_streams_and_returns_the_same_message_usage_and_stop_reason()
    {
        var (c, h, _) = Client();
        var r = await c.CompleteAsync(Request(100_000), CancellationToken.None);
        Assert.True(h.LastStreamed);
        Assert.Equal("streamed ok", r.Text);
        Assert.Equal(StopReasons.EndTurn, r.StopReason);
        Assert.Contains(r.Content, b => b is LlmThinking { Signature: "sig123" });
        // Usage: input/cache from message_start, output (thinking included) from the final message_delta, 1h writes kept.
        Assert.Equal(new LlmUsage(10, 42, 5, 3, 2), r.Usage);
        var body = h.Bodies[^1].RootElement;
        Assert.Equal(100_000, body.GetProperty("max_tokens").GetInt32());
        Assert.Contains("\"ttl\":\"1h\"", body.GetRawText()); // prefix caching + 1h TTL survive the streaming path
    }

    [Fact]
    public async Task Streamed_and_plain_usage_parse_identically()
    {
        var (c, h, _) = Client();
        var streamed = await c.CompleteAsync(Request(100_000), CancellationToken.None);
        var plain = await c.CompleteAsync(Request(1200), CancellationToken.None);
        Assert.False(h.LastStreamed);
        Assert.Equal("plain ok", plain.Text);
        Assert.Equal(plain.Usage, streamed.Usage);
        Assert.Equal(plain.StopReason, streamed.StopReason);
    }

    [Fact]
    public async Task Brain_turns_stay_small_and_unstreamed()
    {
        var (c, h, o) = Client();
        Assert.Equal(1200, o.MainMaxTokens);
        await c.CompleteAsync(Request(null), CancellationToken.None);
        Assert.False(h.LastStreamed);
        Assert.Equal(1200, h.Bodies[^1].RootElement.GetProperty("max_tokens").GetInt32());
        await c.CompleteAsync(Request(16_000), CancellationToken.None); // at the threshold: still plain
        Assert.False(h.LastStreamed);
    }

    [Fact]
    public async Task The_cost_cap_aborts_the_stream_with_the_usage_so_far()
    {
        var (c, _, _) = Client(new FakeHandler(outputTokens: 90_000, thinkingDeltas: 3));
        var req = new LlmRequest
        {
            Tier = ModelTier.Main, MaxTokens = 100_000, Messages = [LlmMessage.User("x")],
            MaxUsd = 0.50m, CostOf = u => u.OutputTokens * 20m / 1_000_000m,
        };
        var ex = await Assert.ThrowsAsync<LlmCostCapException>(() => c.CompleteAsync(req, CancellationToken.None));
        Assert.Equal(90_000, ex.PartialUsage.OutputTokens); // the message_delta's count pushed it over
        Assert.Equal(10, ex.PartialUsage.InputTokens);
        Assert.True(ex.Usd > 0.50m);
    }

    [Fact]
    public void Author_budget_defaults_to_100K_and_is_clamped_to_the_model_max()
    {
        var o = new LlmOptions();
        Assert.Equal(100_000, o.AuthorMaxTokensClamped);
        Assert.Equal("medium", o.AuthorEffort);
        Assert.Equal(3.00m, o.AuthorMaxUsd);
        Assert.Equal(1200, o.MainMaxTokens);
        o.AuthorMaxTokens = 500_000;
        Assert.Equal(LlmOptions.ModelMaxOutputTokens, o.AuthorMaxTokensClamped);
    }
}

/// <summary>The separate Opus author: own context, 100K budget, lint revisions inside, Opus 5.5 only, compact outcome.</summary>
public class AuthorAgentTests
{
    private sealed class ScriptedAuthor(params string[] replies) : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];
        private int _i;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var text = replies[Math.Min(_i++, replies.Length - 1)];
            return Task.FromResult(new LlmResponse([new LlmThinking("private chain of thought", "sig"), new LlmText(text)], StopReasons.EndTurn,
                new LlmUsage(1000, 2000, 0, 0), request.Model!));
        }
    }

    private static AuthorJob Job() => new()
    {
        Kind = "script", System = "REFERENCE", Brief = "goal: mine",
        Check = code => code.Contains("bad") ? AuthorCheck.Fail("lint errors: L1 bad thing") : AuthorCheck.Pass,
    };

    [Fact]
    public async Task Lint_failures_are_revised_inside_the_author_context_with_100K_output()
    {
        var llm = new ScriptedAuthor("draft:\n```lua\nbad()\n```", "```lua\n-- purpose: good\ngood()\n```");
        var o = new LlmOptions();
        var r = await new AuthorAgent(llm, o).RunAsync(Job(), CancellationToken.None);
        Assert.True(r.Ok, r.Blocker);
        Assert.Equal("-- purpose: good\ngood()", r.Artifact);
        Assert.Equal(2, r.Versions);
        Assert.All(llm.Requests, q =>
        {
            Assert.Equal(100_000, q.MaxTokens);
            Assert.Equal("claude-opus-5-5", q.Model);
            Assert.Equal("medium", q.Effort);
            Assert.Equal("REFERENCE", q.System[0].Text);
            Assert.NotNull(q.MaxUsd);
        });
        // The revision turn carries the lint feedback; both live only in the author's own conversation.
        Assert.Contains("L1 bad thing", llm.Requests[1].Messages[^1].Text);
        Assert.Equal(3, llm.Requests[1].Messages.Count);
    }

    [Fact]
    public async Task Only_Opus_55_authors()
    {
        // A brain configured on Sonnet still authors on Opus 5.5 (never on the brain's model).
        var sonnet = new ScriptedAuthor("```\nok()\n```");
        var r = await new AuthorAgent(sonnet, new LlmOptions { MainModel = "claude-sonnet-5-5" }).RunAsync(Job(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("claude-opus-5-5", sonnet.Requests.Single().Model);

        // A clone whose brain runs a non-Opus profile can't author at all.
        var o = new LlmOptions { ApiKey = "sk-test-not-real" };
        Assert.Null(o.ApplyBrainProfile("haiku"));
        var none = new ScriptedAuthor("```\nok()\n```");
        var refused = await new AuthorAgent(none, o).RunAsync(Job(), CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("Opus", refused.Blocker);
        Assert.Empty(none.Requests);

        // Opus served by Ollama isn't Anthropic's Opus 5.5.
        var local = new LlmOptions { MainProvider = LlmProviders.Ollama };
        Assert.NotNull(new AuthorAgent(none, local).Refusal());
    }

    [Fact]
    public async Task The_job_cost_cap_ends_the_job_cleanly()
    {
        var capped = new CapLlm();
        var r = await new AuthorAgent(capped, new LlmOptions { AuthorMaxUsd = 0.10m }).RunAsync(Job(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("cost cap $0.10", r.Blocker);
        Assert.Equal(50_000, r.Usage.OutputTokens); // the partial usage is still counted
        Assert.True(r.Usd > 0);
    }

    private sealed class CapLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new LlmCostCapException(new LlmUsage(100, 50_000, 0, 0), request.Model!, 1.0m, request.MaxUsd ?? 0);
    }
}
