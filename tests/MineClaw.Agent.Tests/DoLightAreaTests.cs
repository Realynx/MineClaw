using System.Text.Json;
using MineClaw.Agent.Tools;
using Xunit;

namespace MineClaw.Agent.Tests;

/// <summary>Live 10-28 16:45 "light up the floor of the build": the brain runs light_area itself (do light) (no worker round), and
/// <c>report:true</c> is the spawn map.</summary>
public class DoLightAreaTests
{
    [Fact]
    public void Do_runs_light_area_as_a_skill()
    {
        Assert.Contains("light", DoTool.Actions);
        Assert.Contains("light{area|design,report?}", DoTool.Description);
        var steps = DoTool.Expand(JsonSerializer.SerializeToElement(new { action = "light", args = new { design = "4", report = true } }), out var err);
        Assert.Null(err);
        var (tool, input) = Assert.Single(steps);
        Assert.Equal("skill", tool);
        Assert.Equal("light_area", input.GetProperty("name").GetString());
        Assert.Equal("4", input.GetProperty("args").GetProperty("design").GetString());
        // Shorthand: {light: "4"} = design 4.
        var short_ = DoTool.Expand(JsonSerializer.SerializeToElement(new { light = "4" }), out err);
        Assert.Null(err);
        Assert.Equal("4", Assert.Single(short_).Input.GetProperty("args").GetProperty("design").GetString());
    }
}
