using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

public sealed class StoryExactPlacementSdkTests
{
    private const string Job = "{id:'edit',block:'wooden_stairs',want:'wooden_stairs',want_id:53,cells:['2,65,0'],metas:[3],exact_state:true}";
    private sealed class Director : IStoryPort
    {
        public readonly List<string> Reports = [];
        public JsonObject? Next(string body) => null;
        public bool Report(string body, string job, string status, string? note, double? pct)
        { Reports.Add(note ?? status); return true; }
    }

    private static Rig Make(Director port)
    {
        var rig = Rig.Js(actions:
        [
            new ScriptAction("goto", ["to", "dist"]),
            new ScriptAction("place", ["block", "at"], Atomic: true),
            new ScriptAction("place_here", ["block", "at", "meta", "exact"], Atomic: true),
            new ScriptAction("place_turned", ["block", "at", "meta", "exact"], Atomic: true),
            new ScriptAction("clear_dig", ["at"], Atomic: true),
            new ScriptAction("dig_here", ["at"], Atomic: true),
        ]);
        rig.Host.Story = port;
        rig.Give(36, Blocks.WoodenStairs, 1);
        rig.Give(37, Blocks.Dirt, 4);
        return rig;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_existing_stair_with_the_wrong_facing_is_never_reported_built(bool reach)
    {
        var port = new Director();
        using var rig = Make(port);
        rig.World.SetBlock(2, 65, 0, Blocks.WoodenStairs, 0);
        string action = reach ? "place_here" : "place_turned";
        rig.On[action] = (_, _) => Task.FromResult(ActResult.Fail("occupied by wooden_stairs"));
        var r = await rig.Run("return story.place(" + Job.Replace("id:'edit'", "id:'edit',reach:" + reach.ToString().ToLowerInvariant()) + ").msg");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("0/1 placed", r.Outcome);
        Assert.Single(rig.CallList, c => c.StartsWith(action + " "));
        Assert.Contains(port.Reports, n => n.Contains("failed: occupied"));
        Assert.DoesNotContain(port.Reports, n => n.EndsWith("done"));
    }

    [Fact]
    public async Task An_existing_matching_full_state_needs_no_new_placement()
    {
        var port = new Director();
        using var rig = Make(port);
        rig.World.SetBlock(2, 65, 0, Blocks.WoodenStairs, 3);
        var r = await rig.Run("return story.place(" + Job + ").msg");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("1/1 placed", r.Outcome);
        Assert.Empty(rig.CallList);
        Assert.Equal(["cell 2,65,0 done"], port.Reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_permanent_placement_passes_the_complete_state_requirement_to_the_client(bool reach)
    {
        var port = new Director();
        using var rig = Make(port);
        string action = reach ? "place_here" : "place_turned";
        rig.On[action] = (a, _) =>
        {
            Assert.True(a.GetProperty("exact").GetBoolean());
            Assert.Equal(3, a.GetProperty("meta").GetInt32());
            rig.World.SetBlock(2, 65, 0, Blocks.WoodenStairs, 3);
            return Task.FromResult(ActResult.Success("placed"));
        };
        var r = await rig.Run("return story.place(" + Job.Replace("id:'edit'", "id:'edit',reach:" + reach.ToString().ToLowerInvariant()) + ").msg");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("1/1 placed", r.Outcome);
        Assert.Single(rig.CallList, c => c.StartsWith(action + " "));
    }

    [Fact]
    public async Task Temporary_support_does_not_inherit_the_permanent_cells_exact_state()
    {
        var port = new Director();
        using var rig = Make(port);
        rig.World.SetBlock(2, 65, 0, Blocks.Stone, 0);
        rig.On["place_here"] = (a, _) =>
        {
            bool support = a.GetProperty("at").GetString() == "2,66,0";
            Assert.Equal(!support, a.TryGetProperty("exact", out var ex) && ex.GetBoolean());
            if (support) Assert.False(a.TryGetProperty("meta", out var ignoredMeta));
            rig.World.SetBlock(2, support ? 66 : 67, 0, support ? Blocks.Dirt : Blocks.WoodenStairs, support ? 0 : 3);
            return Task.FromResult(ActResult.Success("placed"));
        };
        rig.On["clear_dig"] = (_, _) =>
        {
            rig.World.SetBlock(2, 66, 0, 0, 0);
            return Task.FromResult(ActResult.Success("dug"));
        };
        string job = Job.Replace("id:'edit'", "id:'edit',reach:true,scaffold:{'2,67,0':'2,66,0'}").Replace("cells:['2,65,0']", "cells:['2,67,0']");
        var r = await rig.Run("return story.place(" + job + ").msg");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("1/1 placed", r.Outcome);
        Assert.Equal(2, rig.CallList.Count(c => c.StartsWith("place_here ")));
        Assert.Equal(Blocks.WoodenStairs, rig.World.GetBlock(2, 67, 0));
        Assert.Equal(3, rig.World.GetMeta(new BlockPos(2, 67, 0)));
        Assert.Equal(0, rig.World.GetBlock(2, 66, 0));
    }
}
