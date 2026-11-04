using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the exact Anthropic request JSON (WP06 refactor of <c>BuildParams</c> / <c>ToParam</c>) over every block kind, cache
/// breakpoint and TTL order, thinking mode, effort and tier. No network.
/// </summary>
public class AnthropicParamsGoldenTests
{
    private const string Expected = "8FACAF7BA7BF86AEA503176C697BBC2392C2A06097E617C34EDCA03FBBD4CD44";

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Request_params_serialize_the_same()
    {
        var img = new LlmImage([1, 2, 3, 4], 2, 2, "[image omitted]");
        var jpg = new LlmImage([9, 8], 1, 1, "[jpg]", "image/jpeg");
        var tools = new[]
        {
            new LlmToolDefinition("mine", "Mine.", ToolSchema.Create().Str("block").Int("count", required: false).Build()),
            new LlmToolDefinition("look", "Look.", J(new { type = "object" })),
            new LlmToolDefinition("raw", "Raw.", default),
        };
        LlmMessage[] messages =
        [
            LlmMessage.User("hi"),
            LlmMessage.Assistant(new LlmThinking("hmm", "sig"), new LlmRedactedThinking("xyz"), new LlmText("ok"),
                new LlmToolUse("t1", "mine", J(new { block = "dirt", count = 3 })), new LlmToolUse("t2", "look", J(new[] { 1 })), new LlmToolUse("t3", "see", J(new { }))),
            LlmMessage.User(new LlmToolResult("t1", "mined 3 dirt"), new LlmToolResult("t2", "error: bad", IsError: true),
                new LlmToolResult("t3", "a view") { Image = img }),
            LlmMessage.Assistant("next"),
            LlmMessage.User(new LlmText("look"), new LlmImageBlock(jpg), new LlmImageBlock(img)),
            LlmMessage.Assistant(new LlmToolUse("t4", "see", J(new { }))),
            LlmMessage.User(new LlmToolResult("t4", "view", IsError: true) { Image = img }),
        ];
        LlmSystemBlock[][] systems =
        [
            [],
            [new LlmSystemBlock("plain")],
            [new LlmSystemBlock("static", Cache: true), new LlmSystemBlock("identity", Cache: true), new LlmSystemBlock("tail")],
            [new LlmSystemBlock("static", Cache: true, Ttl: "1h"), new LlmSystemBlock("identity", Cache: true, Ttl: "1h"), new LlmSystemBlock("x", Cache: true)],
            [new LlmSystemBlock("a", Cache: true, Ttl: "5m"), new LlmSystemBlock("b", Cache: true, Ttl: "1h"), new LlmSystemBlock("c", Cache: true, Ttl: "bogus")],
        ];
        var options = new[]
        {
            new LlmOptions { ApiKey = "sk-test-not-real" },
            new LlmOptions { ApiKey = "sk-test-not-real", StaticPrefixTtl = "1h", MainModel = "claude-sonnet-5-5" },
            new LlmOptions { ApiKey = "sk-test-not-real", StaticPrefixTtl = "weird" },
        };
        var sb = new StringBuilder();
        foreach (var o in options)
            foreach (var tier in new[] { ModelTier.Main, ModelTier.Worker, ModelTier.Cheap })
                foreach (var system in systems)
                    foreach (var (thinking, effort) in new (string?, string?)[] { (null, null), ("adaptive", "high"), ("omit", "low"), ("off", "max"), ("OFF", "bogus") })
                        foreach (var cacheHistory in new[] { true, false })
                        {
                            var req = new LlmRequest
                            {
                                Tier = tier, System = system, Tools = system.Length > 2 ? tools : [], Messages = messages, CacheHistory = cacheHistory,
                                Thinking = thinking, Effort = effort, MaxTokens = thinking == null ? null : 333,
                            };
                            var client = new AnthropicLlmClient(o);
                            sb.Append(JsonSerializer.Serialize(client.BuildParams(req, o.ModelFor(tier)))).Append('\n');
                        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "anthropic-params.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
