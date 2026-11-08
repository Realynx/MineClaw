using MineClaw.Bot.GameTools;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public sealed class TransferItemSelectorTests
{
    [Theory]
    [InlineData("17:0", 17, 0)]
    [InlineData("17:1", 17, 1)]
    [InlineData("1:0", 1, 0)]
    [InlineData("35:14", 35, 14)]
    public async Task Withdraw_passes_the_exact_variant_instead_of_the_item_family(string selector, int id, int meta)
    {
        var (host, client) = TestBot.Create();
        using var h = host;
        client.World.SetBlock(3, 65, 1, Blocks.Chest, 0);
        var result = await h.RunAs(null, "withdraw", new { item = selector, count = 12, at = "3,65,1" });
        Assert.True(result.Ok, result.Text);
        Assert.Contains($"withdraw {id} 12 3,65,1 dmg={meta}", client.CallList);
    }

    [Fact]
    public async Task Strict_deposit_only_selects_the_requested_variant_and_leaves_other_wood_carried()
    {
        var (host, client) = TestBot.Create();
        using var h = host;
        client.World.SetBlock(3, 65, 1, Blocks.Chest, 0);
        client.Give(9, 17, 12, 0);
        client.Give(10, 17, 24, 1);
        client.Effects["deposit"] = () =>
        {
            Assert.True(client.LastDepositFilter!(new ItemStack(17, 12, 0)));
            Assert.False(client.LastDepositFilter!(new ItemStack(17, 24, 1)));
            client.World.Inventory.SetSlot(0, 9, ItemStack.Empty);
        };
        var result = await h.RunAs(null, DepositAtTool.ToolName, new { at = "3,65,1", items = new[] { "17:0" } });
        Assert.True(result.Ok, result.Text);
        Assert.Equal(24, client.World.Inventory.PlayerSlots().Where(s => s.Id == 17 && s.Damage == 1).Sum(s => s.Count));
        Assert.Single(client.CallList);
    }

    [Theory]
    [InlineData("17:-1")]
    [InlineData("17:32768")]
    [InlineData("999999:0")]
    public async Task Invalid_explicit_selectors_do_not_start_a_transfer(string selector)
    {
        var (host, client) = TestBot.Create();
        using var h = host;
        var result = await h.RunAs(null, "withdraw", new { item = selector, at = "3,65,1" });
        Assert.False(result.Ok);
        Assert.Empty(client.CallList);
    }
}
