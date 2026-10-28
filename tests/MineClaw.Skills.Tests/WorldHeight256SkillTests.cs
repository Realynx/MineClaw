using MineClaw.Data;
using MineClaw.World;
using Xunit;

namespace MineClaw.Skills.Tests;

/// <summary>Skills on a 1.12.2 (256-high) world: an island above y 127 is a valid area (multi-version P3).</summary>
public class WorldHeight256SkillTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Spawn_map_above_y_127_counts_the_island_on_a_256_high_world()
    {
        var g = new FakeGame(256);
        g.Fill(-7, 198, -7, 7, 200, 7, Blocks.Dirt);
        g.Stand(0.5, 201, 0.5);
        g.World.Time = 18000;
        var r = await g.Run("spawn_map", new { area = "-9,-9,9,9", y = "201" });
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("area (x -9..9, z -9..9, y 201): ", r.Text);
        Assert.DoesNotContain(": 0 spawnable", r.Text);
        // Around me with no y: the default band (feet −4..+3 = 197..204) isn't clipped at 126.
        var around = await g.Run("spawn_map", new { });
        output.WriteLine(around.ToString());
        Assert.True(around.Ok, around.Text);
        Assert.Contains("y 197..204", around.Text);
    }
}
