using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// A Story colony's preserved blocks (docs/web/story-mode.md "Preserve zones"): the pawn body's digs refuse them (a pick, a path's dig,
/// a pillar's or a clutch's dig), and only the preserved zone's own repairs, an ordered clear, dig there. A block that isn't the kept one
/// any more (something else in the cell) is the repairs' to clear, and anyone's to dig.
/// </summary>
public class PreservedBlockBuildDigTests
{
    private static Task<bool> Until(Func<bool> c, int ms = 4000) => Harness.WaitAsync(c, ms);

    [Fact]
    public async Task A_preserved_block_is_never_dug_for_other_work_but_a_repairs_ordered_clear_digs_it()
    {
        using var mode = MineClaw.Data.Versioning.WorldMode.Override(MineClaw.Data.Versioning.WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Dirt;
            srv.Blocks[(9, 64, 9)] = Blocks.Dirt;
        });
        await using var _ = c;
        using var __ = s;
        var kept = new BlockPos(9, 64, 8);
        c.Preserved = (p, id) => p == kept && id == Blocks.Dirt;
        var plain = await c.DigBlockAsync(9, 64, 8);
        Assert.False(plain.Ok);
        Assert.Contains("a preserved block", plain.Message);
        Assert.Equal(Blocks.Dirt, c.World.GetBlock(9, 64, 8));
        Assert.True((await c.DigBlockAsync(9, 64, 9)).Ok, "a block nobody preserves is dug as before");
        var r = await c.DigForBuildAsync(kept);
        Assert.True(r.Ok, r.Message);
        Assert.True(await Until(() => c.World.GetBlock(9, 64, 8) == 0));
    }
}
