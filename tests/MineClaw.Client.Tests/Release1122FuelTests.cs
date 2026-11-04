using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>A named fuel is the only one a smelt burns (Story Mode's furnace fuel choice): another fuel in the slot comes out first,
/// and nothing else stands in when the named one is missing.</summary>
public class Release1122FuelTests
{
    private static readonly BlockPos At = new(9, 64, 8);

    private static Task<(MinecraftClient C, FakeServer S)> Start(params (int Slot, ItemStack Stack)[] inv) => Harness.StartAsync(srv =>
    {
        srv.Blocks[(At.X, At.Y, At.Z)] = Blocks.Furnace;
        srv.Furnaces[(At.X, At.Y, At.Z)] = [ItemStack.Empty, Harness.Stack(Blocks.Planks, 3), ItemStack.Empty];
        foreach (var (slot, stack) in inv) srv.Inventory[slot] = stack;
    }, release1122: true);

    [Fact]
    public async Task Coal_named_takes_the_planks_out_and_loads_coal()
    {
        var (c, s) = await Start((36, Harness.Stack(Blocks.Cobblestone, 2)), (37, Harness.Stack(Items.Coal, 1)));
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Blocks.Cobblestone, 2, Items.Coal, false, At);
        Assert.True(r.Ok, r.Message);
        var slots = s.Furnaces[(At.X, At.Y, At.Z)];
        Assert.Equal(Items.Coal, slots[1].Id);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Blocks.Planks) == 3)); // back in the inventory
    }

    [Fact]
    public async Task Coal_named_and_none_held_burns_nothing_else()
    {
        var (c, s) = await Start((36, Harness.Stack(Blocks.Cobblestone, 2)), (37, Harness.Stack(Blocks.Log, 4)));
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Blocks.Cobblestone, 2, Items.Coal, false, At);
        Assert.False(r.Ok);
        Assert.Contains("fuel", r.Message);
        Assert.NotEqual(Blocks.Log, s.Furnaces[(At.X, At.Y, At.Z)][1].Id);
        Assert.Equal(4, c.World.Inventory.CountItem(Blocks.Log));
    }
}
