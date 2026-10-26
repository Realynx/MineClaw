using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>Checks the request shape we'd send (no network calls).</summary>
public class AnthropicParamsTests
{
    private static string Build(ModelTier tier, LlmOptions? o = null)
    {
        o ??= new LlmOptions { ApiKey = "sk-test-not-real" };
        var client = new AnthropicLlmClient(o);
        var req = new LlmRequest
        {
            Tier = tier,
            System = [new LlmSystemBlock("you are a bot", Cache: true)],
            Tools = [new LlmToolDefinition("mine", "Mine.", ToolSchema.Create().Str("block").Build())],
            Messages =
            [
                LlmMessage.User("hi"),
                LlmMessage.Assistant(new LlmText("ok"), new LlmToolUse("t1", "mine", JsonSerializer.SerializeToElement(new { block = "dirt" }))),
                LlmMessage.User(new LlmToolResult("t1", "mined 1 dirt")),
            ],
        };
        var p = client.BuildParams(req, o.ModelFor(tier));
        return JsonSerializer.Serialize(p);
    }

    [Fact]
    public void MainTier_Sonnet55_UsesBetweenTools_CachesPrefixAndHistory()
    {
        var json = Build(ModelTier.Main);
        Assert.Contains("claude-sonnet-5-5", json);
        Assert.Contains("between_tools", json);
        Assert.Contains("\"1h\"", json);           // static prefix TTL
        Assert.Contains("tool_result", json);
        Assert.Contains("\"medium\"", json);
        Assert.Contains("\"max_tokens\":1200", json);
    }

    [Fact]
    public void CheapTier_Haiku55_DisablesThinking_LowEffort()
    {
        var json = Build(ModelTier.Cheap);
        Assert.Contains("claude-haiku-5-5", json);
        Assert.Contains("\"disabled\"", json);
        Assert.Contains("\"low\"", json);
    }
}
