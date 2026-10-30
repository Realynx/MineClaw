using System.Text.Json;
using MineClaw.Agent.Tools;
using Xunit;

namespace MineClaw.Agent.Tests;

/// <summary>Live 10-30 CT 114 (the stair shaft to diamond level, rooms off it): the brain runs the excavate skill itself (do dig),
/// no worker round and no cell list: "extend the stairs to y12" is one step.</summary>
public class DoDigTests
{
    [Fact]
    public void Do_dig_runs_the_excavate_skill()
    {
        Assert.Contains("dig", DoTool.Actions);
        Assert.Contains("dig{op?:seal|room|strip|learn,box|site,to?}", DoTool.Description);
        var steps = DoTool.Expand(JsonSerializer.SerializeToElement(new { action = "dig", args = new { site = "spiral stair", to = 12 } }), out var err);
        Assert.Null(err);
        var (tool, input) = Assert.Single(steps);
        Assert.Equal("skill", tool);
        Assert.Equal("excavate", input.GetProperty("name").GetString());
        Assert.Equal(12, input.GetProperty("args").GetProperty("to").GetInt32());
        // Shorthand {dig: "spiral stair"} = that site; a room then a strip mine, chained.
        var chain = DoTool.Expand(JsonSerializer.SerializeToElement(new
        {
            dig = "spiral stair",
            then = new object[] { new { action = "dig", args = new { op = "room", site = "spiral stair", y = 12 } }, new { action = "dig", args = new { op = "strip", site = "spiral stair room y12", signs = true } } },
        }), out err);
        Assert.Null(err);
        Assert.Equal(3, chain.Count);
        Assert.Equal("spiral stair", chain[0].Input.GetProperty("args").GetProperty("site").GetString());
        Assert.Equal("room", chain[1].Input.GetProperty("args").GetProperty("op").GetString());
    }
}
