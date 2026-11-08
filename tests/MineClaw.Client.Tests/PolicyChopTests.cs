using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class PolicyChopTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_required_axe_missing_or_lost_mid_tree_never_turns_into_hand_chopping(bool modern, bool loseAfterFirst)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            TreeWorld.Square(srv, 8, 8, 5);
            TreeWorld.Tree(srv, 9, 8, 3);
            if (loseAfterFirst) srv.Inventory[36] = Harness.Stack(Items.StoneAxe, 1);
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        if (loseAfterFirst) s.Hook = p =>
        {
            if (p is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusFinished, X: 9, Z: 8 })
            {
                s.Inventory[36] = ItemStack.Empty;
                c.World.Inventory.SetSlot(0, 36, ItemStack.Empty);
            }
            return false;
        };
        var r = await c.ChopTreeAsync(new BlockPos(9, 64, 8), options: TreeWorld.Fast with
        {
            RequiredToolId = Items.StoneAxe, OnlyThisTree = true, MaxLeafBreaks = 0, LeafPhase = TimeSpan.Zero,
            DecayWait = TimeSpan.Zero, WantSaplings = 0,
        });
        Assert.False(r.Ok, r.Message);
        Assert.Contains("required chopping tool", r.Message);
        Assert.Equal(loseAfterFirst ? 1 : 0, s.Dug.Count(d => d.Id == Blocks.Log));
        Assert.True(Enumerable.Range(64, 3).Any(y => c.World.GetBlock(9, y, 8) == Blocks.Log));
    }
}
