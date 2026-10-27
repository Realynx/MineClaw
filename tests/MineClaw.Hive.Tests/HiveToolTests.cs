using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Hive.Tests;

public class HiveToolTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Same estimate as Bot.Tests' brain budget check (chars/3.5 + 6 per tool).
    private static int Tokens(IEnumerable<LlmToolDefinition> defs) =>
        defs.Sum(d => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6);

    [Fact]
    public async Task Merged_hive_tool_is_brain_only_and_cheap()
    {
        await using var h = new TestHive();
        var tools = new ToolRegistry();
        HiveTools.RegisterMain(tools, h.Supervisor);
        var brain = tools.Definitions(ModelTier.Main);
        Assert.Equal(["hive"], brain.Select(d => d.Name));
        Assert.Empty(tools.Definitions(ModelTier.Worker));
        int t = Tokens(brain);
        output.WriteLine($"hive tool ≈{t} tokens");
        Assert.True(t <= 75, $"hive tool ≈{t} tok");

        var list = await tools.ExecuteAsync("hive", System.Text.Json.JsonSerializer.SerializeToElement(new { op = "list" }), default);
        Assert.StartsWith("67 idle | 69 idle | 420 idle | 1337 idle", list.Text);
        var bad = await tools.ExecuteAsync("hive", System.Text.Json.JsonSerializer.SerializeToElement(new { op = "spawn", name = "99", task = "x" }), default);
        Assert.False(bad.Ok);
    }
}
