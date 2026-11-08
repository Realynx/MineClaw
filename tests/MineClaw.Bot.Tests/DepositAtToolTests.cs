using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public sealed class DepositAtToolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_or_partly_filled_destination_keeps_items_and_never_selects_another_chest(bool partial)
    {
        var (host, client) = TestBot.Create();
        using var h = host;
        client.World.SetBlock(3, 65, 1, Blocks.Chest, 0);
        client.World.SetBlock(1, 65, 1, Blocks.Chest, 0); // evacuation source is a tempting nearby fallback
        client.Give(9, Blocks.Dirt, 32);
        client.Results["deposit"] = () => partial ? ActionResult.Success("deposited dirt x16 (chest full)", 16) : ActionResult.Fail("chest is full");
        if (partial) client.Effects["deposit"] = () => client.World.Inventory.SetSlot(0, 9, new ItemStack(Blocks.Dirt, 16, 0));
        var r = await h.RunAs(null, DepositAtTool.ToolName, new { at = "3,65,1", items = new[] { "dirt" } });
        Assert.False(r.Ok);
        Assert.Contains("remaining items carried", r.Text);
        Assert.Equal(partial ? 16 : 32, client.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Single(client.CallList);
        Assert.Equal("deposit 3,65,1 filter=True", client.CallList.Single());
    }
}
