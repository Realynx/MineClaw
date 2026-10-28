using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Task #53: the brain's stable prefix (tools + system + identity) caches for 1h, the history breakpoint stays 5m after it,
/// and 1h writes are billed at 2x base input. Live, 4-15 min gaps between brain calls cold-started the 5m cache and re-wrote
/// ~31-36K prefix tokens each time.
/// </summary>
public class PrefixCacheTtlTests
{
    private const string Ephemeral = "{\"type\":\"ephemeral\"}";
    private const string Ephemeral1h = "{\"type\":\"ephemeral\",\"ttl\":\"1h\"}";

    /// <summary>The cache_control of every breakpoint in request order (system blocks first, then messages): "1h" or "5m".</summary>
    private static List<string> Breakpoints(string json)
    {
        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "cache_control" && p.Value.ValueKind == JsonValueKind.Object)
                        list.Add(p.Value.TryGetProperty("ttl", out var t) && t.GetString() == "1h" ? "1h" : "5m");
                    else Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
        }
        // The wire body (the SDK serializes it under RawBodyData), walked in the API's prefix order: system, then messages.
        var root = Wire(doc);
        if (root.TryGetProperty("system", out var sys)) Walk(sys);
        if (root.TryGetProperty("messages", out var msgs)) Walk(msgs);
        return list;
    }

    private static JsonElement Wire(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("RawBodyData", out var body) ? body : doc.RootElement;

    private static string BrainRequestJson(Action<AgentOptions>? configure = null)
    {
        var (rt, llm, _) = TestRuntime.Create(o => { o.Llm.ApiKey = "sk-test-not-real"; configure?.Invoke(o); });
        using var _r = rt;
        llm.ThenText("hi");
        rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "hi clawd")]), default).GetAwaiter().GetResult();
        var req = llm.Requests[0];
        var p = new AnthropicLlmClient(rt.Options.Llm).BuildParams(req, rt.Options.Llm.MainModel);
        return JsonSerializer.Serialize(p);
    }

    [Fact]
    public void Default_brain_prefix_is_1h_then_history_5m()
    {
        Assert.Equal("1h", new LlmOptions().PrefixCacheTtl);
        var json = BrainRequestJson();
        // static block + identity block on 1h, then the moving history breakpoint on 5m: longer TTLs first.
        Assert.Equal(["1h", "1h", "5m"], Breakpoints(json));
        Assert.Contains("claude-opus-5-5", json);
        // Tools render before the system blocks and share breakpoint 1: they carry no cache_control of their own.
        using var doc = JsonDocument.Parse(json);
        var tools = Wire(doc).GetProperty("tools");
        Assert.All(tools.EnumerateArray(), t => Assert.False(t.TryGetProperty("cache_control", out _)));
        Assert.True(tools.GetArrayLength() > 0);
    }

    [Fact]
    public void PrefixCacheTtl_5m_sends_no_1h_breakpoint()
    {
        var json = BrainRequestJson(o => o.Llm.PrefixCacheTtl = "5m");
        Assert.Equal(["5m", "5m", "5m"], Breakpoints(json));
        Assert.DoesNotContain("\"1h\"", json);
    }

    [Fact]
    public void Unknown_PrefixCacheTtl_falls_back_to_1h()
    {
        Assert.Equal("1h", new LlmOptions { PrefixCacheTtl = "2h" }.PrefixTtl);
        Assert.Equal("5m", new LlmOptions { PrefixCacheTtl = " 5M " }.PrefixTtl);
    }

    [Fact]
    public void Legacy_static_override_never_puts_1h_after_5m()
    {
        // "auto" outside an active session = a 5m static block: the identity block can't be 1h after it.
        var (rt, _, _) = TestRuntime.Create(o => o.Llm.StaticPrefixTtl = "auto");
        using var _r = rt;
        Assert.Equal(["5m", "5m"], rt.Agent.SystemPrompt.Select(b => b.Ttl!).ToList());

        // And the client enforces the order whatever a caller sends.
        var o = new LlmOptions { ApiKey = "sk-test-not-real" };
        var req = new LlmRequest
        {
            Tier = ModelTier.Main,
            System = [new LlmSystemBlock("a", Cache: true, Ttl: "5m"), new LlmSystemBlock("b", Cache: true, Ttl: "1h")],
            Messages = [LlmMessage.User("hi")],
        };
        var json = JsonSerializer.Serialize(new AnthropicLlmClient(o).BuildParams(req, o.MainModel));
        Assert.Equal(["5m", "5m", "5m"], Breakpoints(json));
        Assert.Contains("\"text\":\"b\",\"cache_control\":" + Ephemeral, json);
    }

    [Fact]
    public void Explicit_1h_blocks_then_5m_history_in_wire_shape()
    {
        var o = new LlmOptions { ApiKey = "sk-test-not-real" };
        var req = new LlmRequest
        {
            Tier = ModelTier.Main,
            System = [new LlmSystemBlock("static", Cache: true, Ttl: "1h"), new LlmSystemBlock("identity", Cache: true, Ttl: "1h")],
            Tools = [new LlmToolDefinition("mine", "Mine.", ToolSchema.Create().Str("block").Build())],
            Messages = [LlmMessage.User("hi")],
        };
        var json = JsonSerializer.Serialize(new AnthropicLlmClient(o).BuildParams(req, o.MainModel));
        Assert.Contains("\"text\":\"static\",\"cache_control\":" + Ephemeral1h, json);
        Assert.Contains("\"text\":\"identity\",\"cache_control\":" + Ephemeral1h, json);
        Assert.Contains("\"text\":\"hi\",\"cache_control\":" + Ephemeral, json);
        Assert.Equal(["1h", "1h", "5m"], Breakpoints(json));
    }

    [Fact]
    public void Other_callers_without_a_ttl_keep_5m()
    {
        // Perception / blueprint author / evals send a cached block without a TTL: unchanged (5m), not the brain's 1h.
        var o = new LlmOptions { ApiKey = "sk-test-not-real" };
        var req = new LlmRequest { Tier = ModelTier.Worker, System = [new LlmSystemBlock("perception", Cache: true)], Messages = [LlmMessage.User("q")] };
        var json = JsonSerializer.Serialize(new AnthropicLlmClient(o).BuildParams(req, o.WorkerModel));
        Assert.DoesNotContain("\"1h\"", json);
    }

    [Fact]
    public void One_hour_writes_cost_2x_base_input_and_5m_writes_1_25x()
    {
        var prices = new PriceTable();
        // Opus 5.5: $4/MTok input → 5m write $5, 1h write $8 per MTok.
        var opus = prices.For("claude-opus-5-5");
        Assert.Equal(4.00m * 2m, opus.CacheWrite1hPerM);
        Assert.Equal(4.00m * 1.25m, opus.CacheWrite5mPerM);

        // 1M cache-write tokens, 600K of them 1h: 0.6 × $8 + 0.4 × $5 = $6.80.
        var mixed = new LlmUsage(0, 0, 0, 1_000_000, CacheWrite1hTokens: 600_000);
        Assert.Equal(400_000, mixed.CacheWrite5mTokens);
        Assert.Equal(6.80m, prices.Cost("claude-opus-5-5", mixed));
        Assert.Equal(5.00m, prices.Cost("claude-opus-5-5", new LlmUsage(0, 0, 0, 1_000_000)));
        Assert.Equal(8.00m, prices.Cost("claude-opus-5-5", new LlmUsage(0, 0, 0, 1_000_000, 1_000_000)));
        // Sonnet 5.5 ($2/MTok) and Haiku 5.5 ($0.10/MTok) follow the same 2x rule.
        Assert.Equal(4.00m, prices.Cost("claude-sonnet-5-5", new LlmUsage(0, 0, 0, 1_000_000, 1_000_000)));
        Assert.Equal(0.20m, prices.Cost("claude-haiku-5-5", new LlmUsage(0, 0, 0, 1_000_000, 1_000_000)));
    }

    [Fact]
    public void UsageTracker_books_1h_writes_at_the_1h_rate()
    {
        var usage = new UsageTracker(new BudgetOptions(), new PriceTable(), path: null); // in memory
        decimal c1h = usage.Record("claude-opus-5-5", new LlmUsage(1_000, 100, 0, 32_000, CacheWrite1hTokens: 32_000), ModelTier.Main);
        decimal c5m = usage.Record("claude-opus-5-5", new LlmUsage(1_000, 100, 0, 32_000), ModelTier.Main);
        // Same tokens: the 1h write costs 0.032 × ($8 − $5) = $0.096 more.
        Assert.Equal(0.096m, c1h - c5m);
        Assert.Equal(c1h + c5m, usage.Today.Usd);
    }

    [Fact]
    public void Telemetry_records_the_1h_share_of_cache_writes()
    {
        var o = new AgentOptions { KnowledgeDirectory = null };
        var tel = new Telemetry.PromptTelemetry(o);
        var fake = new FakeLlm { UsagePerCall = new LlmUsage(10, 5, 0, 30_000, 28_000) };
        fake.ThenText("ok");
        tel.Wrap(fake).CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Messages = [LlmMessage.User("hi")] }, default).GetAwaiter().GetResult();
        Assert.Equal(30_000, tel.Last!.CacheWriteTokens);
        Assert.Equal(28_000, tel.Last.CacheWrite1hTokens);
        Assert.Contains("\"cache_write1h_tokens\":28000", tel.Last.ToJsonLine());
    }
}
