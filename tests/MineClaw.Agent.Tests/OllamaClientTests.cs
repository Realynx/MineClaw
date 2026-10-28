using System.Net;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>The Ollama /api/chat client against a fake server: request mapping, tool calls, repair, streaming, cancel, usage.</summary>
public class OllamaClientTests
{
    private static LlmOptions Opts(Action<LlmOptions>? more = null)
    {
        var o = new LlmOptions { MainProvider = "ollama", MainModel = "qwen3:8b", MainMaxTokens = 900 };
        o.Ollama.Endpoint = "http://ollama.test:11434";
        o.Ollama.DefaultNumCtx = 8192;
        o.Ollama.KeepAlive = "30m";
        more?.Invoke(o);
        return o;
    }

    private static (OllamaLlmClient Client, FakeOllama Server) Create(Action<LlmOptions>? more = null)
    {
        var fake = new FakeOllama();
        return (new OllamaLlmClient(Opts(more), new HttpClient(fake)), fake);
    }

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static readonly LlmToolDefinition Goto = new("goto", "Walk somewhere.",
        J(new { type = "object", properties = new { to = new { type = "string" } }, required = new[] { "to" } }));

    private static string Reply(object message, string doneReason = "stop", int prompt = 1234, int eval = 56) =>
        JsonSerializer.Serialize(new { model = "qwen3:8b", message, done = true, done_reason = doneReason, prompt_eval_count = prompt, eval_count = eval });

    [Fact]
    public async Task Request_maps_system_prefix_messages_tool_pairs_tools_and_options()
    {
        var (client, fake) = Create();
        fake.Chat = (_, _) => Task.FromResult(FakeOllama.Raw(Reply(new { role = "assistant", content = "ok" })));
        var request = new LlmRequest
        {
            Tier = ModelTier.Main,
            System = [new LlmSystemBlock("static rules", Cache: true), new LlmSystemBlock("identity", Cache: true)],
            Tools = [Goto],
            Messages =
            [
                LlmMessage.User("go to the gen"),
                LlmMessage.Assistant(new LlmText("on it"), new LlmToolUse("tu_9", "goto", J(new { to = "gen" })), new LlmThinking("hmm", "sig")),
                LlmMessage.User(new LlmToolResult("tu_9", "no route", IsError: true), new LlmText("[new events] kiwi: hi")),
            ],
        };
        var r = await client.CompleteAsync(request, default);
        Assert.Equal("ok", r.Text);
        Assert.Equal(LlmProviders.Ollama, r.Provider);

        var body = fake.ChatRequests.Single().Json;
        Assert.Equal("qwen3:8b", body.GetProperty("model").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal("30m", body.GetProperty("keep_alive").GetString());
        Assert.Equal(8192, body.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(900, body.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(body.TryGetProperty("think", out _)); // capabilities say no "thinking": never sent

        var msgs = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user", "assistant", "tool", "user"], msgs.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("static rules\n\nidentity", msgs[0].GetProperty("content").GetString()); // same prefix every call (KV-cache reuse)
        var call = msgs[2].GetProperty("tool_calls")[0].GetProperty("function");
        Assert.Equal("goto", call.GetProperty("name").GetString());
        Assert.Equal("gen", call.GetProperty("arguments").GetProperty("to").GetString());
        Assert.False(msgs[2].TryGetProperty("thinking", out _));   // provider-specific thinking is never sent
        Assert.Equal("goto", msgs[3].GetProperty("tool_name").GetString());
        Assert.Equal("error: no route", msgs[3].GetProperty("content").GetString());
        Assert.Equal("[new events] kiwi: hi", msgs[4].GetProperty("content").GetString());

        var tool = body.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("goto", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("to", tool.GetProperty("function").GetProperty("parameters").GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task Images_go_to_vision_models_only_and_think_follows_capabilities()
    {
        var img = new LlmImage([1, 2, 3, 4], 64, 64, "[image omitted]");
        LlmRequest Req() => new()
        {
            Tier = ModelTier.Main,
            Messages = [LlmMessage.User("look"), LlmMessage.Assistant(new LlmToolUse("s1", "see", J(new { }))),
                LlmMessage.User(new LlmToolResult("s1", "view") { Image = img })],
        };

        var (plain, f1) = Create();
        f1.Chat = (_, _) => Task.FromResult(FakeOllama.Raw(Reply(new { role = "assistant", content = "" })));
        await plain.CompleteAsync(Req(), default);
        var m1 = f1.ChatRequests.Single().Json.GetProperty("messages").EnumerateArray().ToList();
        Assert.DoesNotContain(m1, m => m.TryGetProperty("images", out _));
        Assert.Equal("view [image omitted]", m1.Single(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString());

        var (vision, f2) = Create(o => o.MainThinking = "adaptive");
        f2.Capabilities = ["completion", "tools", "vision", "thinking"];
        f2.Chat = (_, _) => Task.FromResult(FakeOllama.Raw(Reply(new { role = "assistant", content = "a cobble gen" })));
        await vision.CompleteAsync(Req(), default);
        var b2 = f2.ChatRequests.Single().Json;
        var withImage = b2.GetProperty("messages").EnumerateArray().Single(m => m.TryGetProperty("images", out _));
        Assert.Equal("user", withImage.GetProperty("role").GetString());
        Assert.Equal(Convert.ToBase64String(img.Data), withImage.GetProperty("images")[0].GetString());
        Assert.True(b2.GetProperty("think").GetBoolean());

        // Configured vision model without capabilities reported (older server): images still go.
        var (cfg, f3) = Create(o => o.Ollama.VisionModels = ["qwen3"]);
        f3.Capabilities = null;
        f3.Chat = (_, _) => Task.FromResult(FakeOllama.Raw(Reply(new { role = "assistant", content = "" })));
        await cfg.CompleteAsync(Req(), default);
        Assert.Contains(f3.ChatRequests.Single().Json.GetProperty("messages").EnumerateArray(), m => m.TryGetProperty("images", out _));
    }

    [Fact]
    public async Task Tool_calls_get_ids_usage_maps_and_cache_is_zero()
    {
        var (client, fake) = Create();
        fake.Chat = (_, _) => Task.FromResult(FakeOllama.Raw(Reply(new
        {
            role = "assistant", content = "",
            tool_calls = new object[]
            {
                new { function = new { name = "goto", arguments = new { to = "gen" } } },
                new { function = new { name = "status", arguments = new { } } },
            },
        }, prompt: 4321, eval: 77)));
        var r = await client.CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Tools = [Goto], Messages = [LlmMessage.User("go")] }, default);
        Assert.Equal(StopReasons.ToolUse, r.StopReason);
        var uses = r.ToolUses.ToList();
        Assert.Equal(["goto", "status"], uses.Select(u => u.Name));
        Assert.All(uses, u => Assert.StartsWith("call_", u.Id));
        Assert.Equal(2, uses.Select(u => u.Id).Distinct().Count());
        Assert.Equal("gen", uses[0].Input.GetProperty("to").GetString());
        Assert.Equal(new LlmUsage(4321, 77, 0, 0), r.Usage);
        Assert.Equal("qwen3:8b", r.Model); // the configured id, so tier accounting works

        // The generated ids pair with our tool_results next call (by order + tool_name).
        fake.Chat = (_, _) => Task.FromResult(FakeOllama.Raw(Reply(new { role = "assistant", content = "there" }, "length")));
        var r2 = await client.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Main,
            Messages = [LlmMessage.User("go"), new LlmMessage(LlmRole.Assistant, r.Content),
                LlmMessage.User(new LlmToolResult(uses[0].Id, "arrived"), new LlmToolResult(uses[1].Id, "hp 20"))],
        }, default);
        Assert.Equal(StopReasons.MaxTokens, r2.StopReason);
        var tools = fake.ChatRequests.Last().Json.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToList();
        Assert.Equal(["goto", "status"], tools.Select(t => t.GetProperty("tool_name").GetString()));
        Assert.Equal(["arrived", "hp 20"], tools.Select(t => t.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task No_tool_calls_and_empty_content_end_the_turn()
    {
        var (client, fake) = Create();
        fake.Chat = (_, _) => Task.FromResult(FakeOllama.Raw(Reply(new { role = "assistant", content = "   " })));
        var r = await client.CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Tools = [Goto], Messages = [LlmMessage.User("hi")] }, default);
        Assert.Equal(StopReasons.EndTurn, r.StopReason);
        Assert.Empty(r.Content);
    }

    [Fact]
    public void Malformed_arguments_get_one_repair_then_an_error_marker()
    {
        JsonElement Parse(string s) => OllamaLlmClient.ParseArguments(J(s));
        Assert.Equal("gen", Parse("""{"to":"gen"}""").GetProperty("to").GetString());                  // string-encoded JSON
        Assert.Equal("gen", Parse("```json\n{\"to\": \"gen\",}\n```").GetProperty("to").GetString());  // fences + trailing comma
        Assert.Equal("gen", Parse("{'to': 'gen'}").GetProperty("to").GetString());                     // single quotes
        Assert.Equal(3, Parse("""{"item":"log","n":3""").GetProperty("n").GetInt32());                 // cut off: closed
        Assert.True(LlmToolArgs.IsInvalid(Parse("go to the gen please"), out var raw));
        Assert.Equal("go to the gen please", raw);
        Assert.Equal(JsonValueKind.Object, OllamaLlmClient.ParseArguments(default).ValueKind);
    }

    [Fact]
    public async Task Invalid_arguments_answer_with_an_error_result_and_never_run_the_tool()
    {
        int ran = 0;
        var tools = new ToolRegistry();
        tools.Register("mine", "Mine blocks.", ToolSchema.Empty, (_, _) => { ran++; return Task.FromResult(ToolResult.Success("mined")); });
        var input = OllamaLlmClient.ParseArguments(J("mine cobble x8 ???"));
        var r = await tools.ExecuteAsync("mine", input, default);
        Assert.False(r.Ok);
        Assert.Contains("invalid arguments for mine", r.Text);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Streamed_tool_calls_are_parsed_and_reported_early()
    {
        var (client, fake) = Create();
        fake.Chat = (body, _) =>
        {
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("stream").GetBoolean());
            return Task.FromResult(FakeOllama.Stream(
            [
                """{"message":{"role":"assistant","content":"on "},"done":false}""",
                """{"message":{"role":"assistant","content":"it"},"done":false}""",
                """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"goto","arguments":"{\"to\":\"gen\",}"}}]},"done":false}""",
                """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":900,"eval_count":12}""",
            ]));
        };
        var early = new List<LlmToolUse>();
        var r = await client.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Main, Tools = [Goto], Messages = [LlmMessage.User("go")], Interruptible = true, OnToolUse = early.Add,
        }, default);
        Assert.Equal("on it", r.Text);
        var use = Assert.Single(r.ToolUses);
        Assert.Equal("gen", use.Input.GetProperty("to").GetString()); // repaired (trailing comma)
        Assert.Equal(use.Id, Assert.Single(early).Id);                  // the early hook saw the same call id
        Assert.Equal(new LlmUsage(900, 12, 0, 0), r.Usage);
    }

    [Fact]
    public async Task Cancelling_an_interruptible_stream_throws_interrupted_with_partial_usage()
    {
        var (client, fake) = Create();
        var started = new TaskCompletionSource();
        fake.Chat = (_, _) =>
        {
            started.TrySetResult();
            return Task.FromResult(FakeOllama.Stream(["""{"message":{"role":"assistant","content":"let me think about thaaat"},"done":false}"""], hang: true));
        };
        using var cts = new CancellationTokenSource();
        var call = client.CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Messages = [LlmMessage.User("hi")], Interruptible = true }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        cts.Cancel(); // the chat-debounce restart
        var ex = await Assert.ThrowsAsync<LlmInterruptedException>(() => call.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("qwen3:8b", ex.Model);
        Assert.NotNull(ex.PartialUsage);
        Assert.True(ex.PartialUsage!.OutputTokens > 0);
        Assert.Equal(0, ex.PartialUsage.CacheReadTokens);

        // Not interruptible: a plain cancellation (never reported as an interrupted turn).
        fake.Chat = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return FakeOllama.Raw("{}"); };
        using var cts2 = new CancellationTokenSource(150);
        var ex2 = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteAsync(new LlmRequest { Tier = ModelTier.Worker, Messages = [LlmMessage.User("x")] }, cts2.Token));
        Assert.IsNotType<LlmInterruptedException>(ex2);
    }

    [Fact]
    public async Task Errors_are_clear_and_the_bearer_key_is_sent()
    {
        var (client, fake) = Create(o => o.Ollama.ApiKey = "proxy-secret");
        fake.Chat = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"model 'qwen3:8b' not found"}""") });
        var ex = await Assert.ThrowsAsync<OllamaException>(() => client.CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Messages = [LlmMessage.User("x")] }, default));
        Assert.Contains("model 'qwen3:8b' not found", ex.Message);
        Assert.Single(fake.ChatRequests); // 4xx: no retry
        Assert.All(fake.Requests, r => Assert.Equal("Bearer proxy-secret", r.Auth));
    }

    [Fact]
    public async Task Catalog_check_finds_missing_and_tool_less_models()
    {
        var fake = new FakeOllama();
        var o = Opts().Ollama;
        var catalog = new OllamaCatalog(o, new HttpClient(fake));
        var ok = await catalog.CheckAsync([("qwen3:8b", "brain tier", true), ("llama3.2", "memory tier", true)], default);
        Assert.True(ok.Ok, ok.Message);

        var missing = await catalog.CheckAsync([("mistral:7b", "worker tier", true)], default);
        Assert.False(missing.Ok);
        Assert.False(missing.Transient);
        Assert.Contains("isn't on the Ollama server", missing.Message);

        fake.Capabilities = ["completion"];
        var noTools = await new OllamaCatalog(o, new HttpClient(fake)).CheckAsync([("gpt-oss:120b-cloud", "brain tier", true)], default);
        Assert.False(noTools.Ok);
        Assert.Contains("can't call tools", noTools.Message);
    }
}
