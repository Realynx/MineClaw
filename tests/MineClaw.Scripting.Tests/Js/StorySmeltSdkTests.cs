using System.Text.Json.Nodes;
using MineClaw.Scripting.Story;

namespace MineClaw.Scripting.Tests.Js;

public sealed class StorySmeltSdkTests
{
    private sealed class Director(int? meta) : IStoryPort
    {
        private int _next;
        public JsonObject? Next(string body) => _next++ == 0
            ? new JsonObject { ["id"] = "smelt", ["kind"] = "smelt", ["at"] = "1,65,0", ["item"] = "raw_fish", ["n"] = 2,
                ["fuel"] = "coal", ["input_meta"] = meta }
            : new JsonObject { ["id"] = "end", ["kind"] = "end" };
        public bool Report(string body, string job, string status, string? note, double? pct) => true;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_shipped_pawn_forwards_exact_input_metadata_without_changing_legacy_calls(int? meta)
    {
        using var rig = Rig.Js(actions:
        [new ScriptAction("smelt", ["item", "count", "fuel", "wait", "at", "input_meta"], Atomic: true), new ScriptAction("goto", ["to", "dist"])]);
        rig.Host.Story = new Director(meta);
        rig.On["smelt"] = (a, _) =>
        {
            Assert.Equal("1,65,0", a.GetProperty("at").GetString());
            Assert.Equal(2, a.GetProperty("count").GetInt32());
            Assert.False(a.GetProperty("wait").GetBoolean());
            if (meta == null) Assert.False(a.TryGetProperty("input_meta", out var ignored));
            else Assert.Equal(meta, a.GetProperty("input_meta").GetInt32());
            return Task.FromResult(ActResult.Success("smelting"));
        };
        var r = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(r.Ok, r.Outcome);
        Assert.Single(rig.CallList, call => call.StartsWith("smelt "));
    }
}
