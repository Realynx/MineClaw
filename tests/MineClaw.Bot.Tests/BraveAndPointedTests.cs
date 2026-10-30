using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-30, Fox's stair shaft: main refused to dig the next column he punched ("part of the spiral_staircase build").
/// A trusted player's punch on a column is that player's choice, so soft protection yields there; containers and
/// never-break marks don't.
/// </summary>
public class PointedByTrustedTests
{
    [Fact]
    public void A_trusted_punch_on_the_column_lets_the_bot_dig_a_soft_protected_block_there_and_nowhere_else()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var punched = new BlockPos(154, 41, 276);
        var p = new Protection(new BotOptions(), new NoMemory(), new NoMemory()) { IndexerKind = _ => WorldIndexer.FloorKind };
        Assert.True(p.IsProtected(new BlockPos(154, 40, 276), Blocks.Stone));
        p.PointedByTrusted = c => c.X == punched.X && c.Z == punched.Z && Math.Abs(c.Y - punched.Y) <= 2;
        Assert.False(p.IsProtected(new BlockPos(154, 40, 276), Blocks.Stone));
        Assert.False(p.IsProtected(new BlockPos(154, 39, 276), Blocks.Stone));
        Assert.True(p.IsProtected(new BlockPos(155, 40, 276), Blocks.Stone)); // the next column over stays protected
    }
}
