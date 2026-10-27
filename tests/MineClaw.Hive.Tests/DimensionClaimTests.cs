using MineClaw.Hive.Claims;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>Clones may be in different dimensions: the same coordinates in the overworld and the nether are different leases.</summary>
public class DimensionClaimTests
{
    [Fact]
    public void Claim_keys_carry_the_dimension_and_only_conflict_within_one()
    {
        var p = new BlockPos(1, 64, -3);
        Assert.Equal("chest:1,64,-3", ClaimKey.Chest(p).Id);           // overworld ids unchanged on the wire
        Assert.Equal("chest:1,64,-3@-1", ClaimKey.Chest(p, -1).Id);
        Assert.Equal("area:0,60,0..4,70,4@-1", ClaimKey.Area(new BlockPos(4, 70, 4), new BlockPos(0, 60, 0), -1).Id);
        Assert.Equal("task:smelt", ClaimKey.Task("smelt").Id);
        foreach (var k in new[] { ClaimKey.Block(p, -1), ClaimKey.Chest(p, -1), ClaimKey.Furnace(p, -1), ClaimKey.Area(p, new BlockPos(5, 70, 5), -1), ClaimKey.Block(p) })
        {
            Assert.True(ClaimKey.TryParse(k.Id, out var back), k.Id);
            Assert.Equal(k, back);
        }
        Assert.False(ClaimKey.TryParse("chest:1,64,-3@x", out _));

        var b = new ClaimBoard(TimeSpan.FromSeconds(60), new ManualTime());
        Assert.True(b.TryClaim("maid_chan_67", ClaimKey.Chest(p)).Ok);
        Assert.True(b.TryClaim("maid_chan_69", ClaimKey.Chest(p, -1)).Ok);        // same coordinates, nether: free
        Assert.False(b.TryClaim("maid_chan_69", ClaimKey.Chest(p)).Ok);
        Assert.True(b.TryClaim("maid_chan_69", ClaimKey.Area(new BlockPos(0, 60, -5), new BlockPos(3, 70, 0))).Ok);
        Assert.False(b.IsFree("maid_chan_67", ClaimKey.Block(2, 65, -2)));
        Assert.True(b.IsFree("maid_chan_67", ClaimKey.Block(2, 65, -2, -1)));     // an overworld area doesn't cover the nether
        Assert.True(b.TryClaim("maid_chan_67", ClaimKey.Task("cobble")).Ok);
        Assert.False(b.TryClaim("maid_chan_69", ClaimKey.Task("cobble")).Ok);     // tasks are dimension-free
    }
}
