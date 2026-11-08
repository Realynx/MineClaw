using System.Text.Json.Nodes;
using MineClaw.Scripting.Story;

namespace MineClaw.Scripting.Tests.Js;

public sealed class StoryOreSdkTests
{
    private sealed class Director : IStoryPort
    {
        public List<string> Events { get; } = [];
        public Func<string, JsonArray?> Expose { get; set; } = _ => [];
        public JsonObject? Next(string body) => null;
        public bool Report(string body, string job, string status, string? note, double? pct) { Events.Add(note ?? status); return true; }
        public JsonArray? DigExposed(string body, string job, string cell, int beforeId, int beforeMeta)
        {
            Events.Add($"exposed {cell} {beforeId}:{beforeMeta}");
            return Expose(cell);
        }
    }

    private static Rig Make(Director port, bool tool = true)
    {
        var rig = Rig.Js(more: tool ? [new ScriptAction("dig_ore_here", ["at", "ore"], Atomic: true)] : []);
        rig.Host.Story = port;
        rig.World.SetBlock(1, 65, 0, 1, 2);
        rig.World.SetBlock(2, 65, 0, 56, 0);
        rig.World.SetBlock(3, 65, 0, 56, 0);
        foreach (string name in new[] { "dig", "dig_ore_here" })
            rig.On[name] = (args, _) =>
            {
                string cell = args.GetProperty("at").GetString()!;
                var p = cell.Split(',').Select(int.Parse).ToArray();
                rig.World.SetBlock(p[0], p[1], p[2], 0, 0);
                return Task.FromResult(ActResult.Success("dug"));
            };
        return rig;
    }

    [Fact]
    public async Task Newly_exposed_cells_extend_the_same_batch_and_report_the_original_block_before_completion()
    {
        var port = new Director { Expose = cell => cell switch
        {
            "1,65,0" => [new JsonObject { ["at"] = "2,65,0", ["ore"] = 56 }],
            "2,65,0" => [new JsonObject { ["at"] = "3,65,0", ["ore"] = 56 }, new JsonObject { ["at"] = "1,65,0", ["ore"] = 56 }],
            _ => []
        } };
        using var rig = Make(port);
        var s = await rig.Run("return story.digBatch({id:'j1',at:'1,65,0',dig_cells:['1,65,0'],follow_veins:true}).msg");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("3/3 dug", s.Outcome);
        Assert.Equal(["exposed 1,65,0 1:2", "digcell 1,65,0 done", "exposed 2,65,0 56:0", "digcell 2,65,0 done", "exposed 3,65,0 56:0", "digcell 3,65,0 done"], port.Events);
        Assert.Equal(2, rig.CallList.Count(c => c.StartsWith("dig_ore_here ")));
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("goto ") || c.StartsWith("place ") || c.StartsWith("clear_dig "));
    }

    [Fact]
    public async Task Cancellation_at_exposure_stops_before_any_neighbor_is_mined()
    {
        var port = new Director { Expose = _ => null };
        using var rig = Make(port);
        var s = await rig.Run("const r=story.digBatch({id:'j1',at:'1,65,0',dig_cells:['1,65,0','2,65,0'],follow_veins:true}); return String(r.cancelled)");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true", s.Outcome);
        Assert.Single(rig.CallList, c => c.StartsWith("dig "));
        Assert.Equal(56, rig.World.GetBlock(2, 65, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persisted_vein_targets_always_use_the_guarded_primitive_without_a_destructive_fallback(bool tool)
    {
        var port = new Director();
        using var rig = Make(port, tool);
        var s = await rig.Run("return story.digBatch({id:'j1',at:'2,65,0',dig_cells:['2,65,0'],ordered:true,replace:true,follow_veins:true,vein_ores:{'2,65,0':56}}).msg");
        Assert.True(s.Ok, s.Outcome);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("dig ") || c.StartsWith("clear_dig "));
        Assert.Equal(tool ? 0 : 56, rig.World.GetBlock(2, 65, 0));
        Assert.Equal(tool ? 1 : 0, rig.CallList.Count(c => c.StartsWith("dig_ore_here ")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_chopping_policy_uses_its_required_tool_and_never_falls_back_to_the_unrestricted_macro(bool available)
    {
        using var rig = Rig.Js(more: available ? [new ScriptAction("chop_with_tool", ["at", "tool"])] : []);
        rig.Host.Story = new Director();
        var s = await rig.Run("return String(story.chop({id:'j1',tool:'stone_axe'},'2,65,0').ok)");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains(available ? "true" : "false", s.Outcome);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("chop_tree "));
        if (available) Assert.Contains("\"tool\":\"stone_axe\"", Assert.Single(rig.CallList));
        else Assert.Empty(rig.CallList);
    }
}
