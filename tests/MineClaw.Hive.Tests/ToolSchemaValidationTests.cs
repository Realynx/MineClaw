using System.Text.Json;
using MineClaw.Agent.Tools;

namespace MineClaw.Hive.Tests;

public class ToolSchemaValidationTests
{
    [Fact]
    public async Task Hive_validates_declared_fields_without_losing_recall_or_profile_aliases()
    {
        await using var hive = new TestHive();
        var tools = new ToolRegistry();
        HiveTools.RegisterMain(tools, hive.Supervisor);
        var invalid = await tools.ExecuteAsync("hive", JsonSerializer.SerializeToElement(new { op = "list", bodies = "two" }), default);
        Assert.False(invalid.Ok);
        Assert.Contains("$.bodies", invalid.Text);
        var recalled = await tools.ExecuteAsync("hive", JsonSerializer.SerializeToElement(new { op = " RECALL ", name = "all", profile = "legacy", body = "main" }), default);
        Assert.True(recalled.Ok, recalled.Text);
        Assert.All(hive.Supervisor.Slots, slot => Assert.Equal(Supervisor.CloneState.Idle, slot.State));
    }
}
