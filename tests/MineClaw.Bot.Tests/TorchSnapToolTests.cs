using MineClaw.Data;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 2026-10-28: the worker torching the island for Robin guessed cells over air and failed twice the same way ("-12,64,-3
/// has nothing solid on any side ... all air"). The place tool snaps an attachable to ground/a wall within 2 and says so.
/// </summary>
public class TorchSnapToolTests
{
    /// <summary>A 5×5 cobble floor at y63 (x -14..-10, z -2..2) in the fake client's loaded chunks.</summary>
    private static (BotHost H, FakeGameClient C) Island()
    {
        var (h, c) = TestBot.Create();
        for (int x = -14; x <= -10; x++)
        for (int z = -2; z <= 2; z++)
            c.World.SetBlock(x, 63, z, Blocks.Cobblestone, 0);
        return (h, c);
    }

    [Fact]
    public async Task A_torch_requested_over_air_snaps_to_the_adjacent_ground_and_says_so()
    {
        var (h, c) = Island();
        var r = await h.Run("place", new { block = "torch", at = "-12,64,-3" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains($"place -12,64,-2 {Blocks.Torch}", c.CallList);
        Assert.EndsWith("(snapped 1 from -12,64,-3: nothing to attach)", r.Text);
    }

    [Fact]
    public async Task A_torch_with_nothing_within_2_fails_with_one_line_and_no_click()
    {
        var (h, c) = Island();
        var r = await h.Run("place", new { block = "torch", at = "-9,64,-12" });
        Assert.False(r.Ok);
        Assert.Contains("no ground or wall within 2 of -9,64,-12 (it's over the void): pick a spot on the island", r.Text);
        Assert.Empty(c.CallList);
    }

    [Fact]
    public async Task Ordinary_blocks_and_valid_torch_spots_are_placed_as_asked()
    {
        var (h, c) = Island();
        await h.Run("place", new { block = "cobblestone", at = "-12,64,-3" }); // over air: the client explains, no snapping
        await h.Run("place", new { block = "torch", at = "-12,64,0" });       // on the floor already
        Assert.Contains($"place -12,64,-3 {Blocks.Cobblestone}", c.CallList);
        Assert.Contains($"place -12,64,0 {Blocks.Torch}", c.CallList);
        Assert.Equal(2, c.CallList.Count);
    }
}
