using MineClaw.Bot.GameTools;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>B12/B21 (night log 2026-10-27): the generator cell is dug only from the demo's stand spot.</summary>
public class NightLogGeneratorStandTests
{
    [Fact]
    public void Stand_comes_from_the_live_demo_steps()
    {
        // alice 07:20:07: "learned dual cobble generator other side (2 steps): stood at 7,63,8 facing cobblestone; punched
        // cobblestone at 6,64,8 (x2)".
        string[] steps = ["stood at 7,63,8 facing cobblestone", "punched cobblestone at 6,64,8 (x2)"];
        Assert.Equal(new BlockPos(7, 63, 8), GeneratorStand.FromSteps(steps, new BlockPos(6, 64, 8)));
        Assert.Null(GeneratorStand.FromSteps(steps, new BlockPos(4, 64, 6)));                      // another cell: no stand
        Assert.Equal(new BlockPos(7, 63, 8), GeneratorStand.FromSteps(["stood at 7,63,8 facing cobblestone; punched cobblestone at 6,64,8 (x2)"], new BlockPos(6, 64, 8)));
    }

    [Fact]
    public void Relative_steps_are_placed_with_the_structure_origin()
    {
        // live2 00:52:23: "stood at -1,+1,-1 ...; broke cobblestone at +0,+1,+0" (offsets from the structure's min corner).
        string[] steps = ["stood at -1,+1,-1 facing cobblestone", "broke cobblestone at +0,+1,+0"];
        var origin = new BlockPos(4, 63, 6);
        Assert.Equal(new BlockPos(3, 64, 5), GeneratorStand.FromSteps(steps, new BlockPos(4, 64, 6), origin));
        Assert.Null(GeneratorStand.FromSteps(steps, new BlockPos(4, 64, 6)));               // no anchor: can't place it
        Assert.Equal(new BlockPos(3, 64, 5), GeneratorStand.FromSteps(["(relative to 4,63,6) stood at -1,+1,-1", "broke cobblestone at +0,+1,+0"], new BlockPos(4, 64, 6)));
    }

    [Fact]
    public async Task Stand_is_found_in_memory_and_the_tool_digs_from_it()
    {
        var (h, c) = TestBot.Create();
        h.Runtime.Memory.UpsertStructure("dual cobble generator", s =>
        {
            s.Kind = "cobble_generator";
            s.Min = new MineClaw.Agent.Memory.BlockPos(4, 63, 6);
            s.Steps.AddRange(["stood at 7,63,8 facing cobblestone", "punched cobblestone at 6,64,8 (x2)"]);
            s.KeyBlocks.Add(new MineClaw.Agent.Memory.KeyBlock { Label = "mine_here", X = 6, Y = 64, Z = 8 });
        });
        Assert.Equal(new BlockPos(7, 63, 8), GeneratorStand.Find(h.Runtime.Memory, new BlockPos(6, 64, 8)));

        c.Give(36, MineClaw.Data.Items.StonePickaxe, 1);
        var r = await h.Run("harvest_generator", new { at = "dual cobble generator", count = 20, stand = "7,63,8" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("generator 6,64,8 20 stand 7,63,8", c.CallList);
        Assert.False((await h.Run("harvest_generator", new { at = "dual cobble generator", count = 20, stand = "here" })).Ok);
    }

    [Fact]
    public async Task Loop_stops_instead_of_digging_from_elsewhere_when_the_stand_is_unreachable()
    {
        var (_, c) = TestBot.Create();
        c.Give(36, MineClaw.Data.Items.StonePickaxe, 1);
        c.Results["generator"] = () => MineClaw.Client.ActionResult.Fail("can't get onto the taught stand spot 7,63,8 for the cell 6,64,8 (no path); not digging from anywhere else");
        var r = await new GeneratorLoop(c) { RetryPause = TimeSpan.Zero }.RunAsync(new BlockPos(6, 64, 8), 64, null, default, new BlockPos(7, 63, 8));
        Assert.False(r.Ok);
        Assert.Contains("taught stand spot", r.Message);
        Assert.Single(c.CallList, x => x.StartsWith("generator", StringComparison.Ordinal));
    }
}
