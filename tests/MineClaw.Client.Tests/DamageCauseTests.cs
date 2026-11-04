using MineClaw.Data;

namespace MineClaw.Client.Tests;

/// <summary>
/// <see cref="MinecraftClient.DamageCause"/> names the block causes in their order (lava, fire, suffocation, cactus) from what the body
/// touches, and nothing on clear ground with no one around (WP05 refactor: the causes became one helper each).
/// </summary>
public class DamageCauseTests
{
    [Fact]
    public async Task Block_causes_are_named_in_order_and_clear_ground_is_no_cause()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var feet = c.World.Player.Position;
        int x = (int)Math.Floor(feet.X), y = (int)Math.Floor(feet.Y), z = (int)Math.Floor(feet.Z);

        Assert.Null(c.DamageCause(1));
        string? With(int id, int dy = 0)
        {
            c.World.SetBlock(x, y + dy, z, id, 0);
            var cause = c.DamageCause(1);
            c.World.SetBlock(x, y + dy, z, 0, 0);
            return cause;
        }
        Assert.Equal("lava (standing in it)", With(Blocks.Lava));
        Assert.Equal("fire (standing in it)", With(Blocks.Fire));
        Assert.Equal("cactus (touching one)", With(Blocks.Cactus));
        Assert.Equal("suffocation (head inside a block)", With(Blocks.Stone, dy: 1));
        // Lava beats a cactus in the same box.
        c.World.SetBlock(x, y + 1, z, Blocks.Cactus, 0);
        Assert.Equal("lava (standing in it)", With(Blocks.Lava));
        c.World.SetBlock(x, y + 1, z, 0, 0);
        Assert.Null(c.DamageCause(3));
    }
}
