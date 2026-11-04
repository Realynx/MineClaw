using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// An input never goes into a furnace without the fuel to smelt it. Live 11-04 story-1 (release 20261104-181056): "collect_furnace
/// 218,64,399 failed: nothing to collect; furnace (3 slots, 2 free) progress 0%; in=oak wood x1 fuel=- out=-" ×108. Charcoal from logs
/// burning logs: the log about to go in was counted as the fuel too, the input went in first, and the fuel move found nothing left.
/// A refuel (count 0) tops up the fuel for what the furnace already holds.
/// </summary>
public class SmeltFuelFirstTests
{
    private static readonly BlockPos At = new(10, 64, 10);

    private static Task<(MinecraftClient C, FakeServer S)> Start(ItemStack[] furnace, params (int Slot, ItemStack Stack)[] inv) => Harness.StartAsync(srv =>
    {
        srv.Blocks[(At.X, At.Y, At.Z)] = Blocks.Furnace;
        srv.Furnaces[(At.X, At.Y, At.Z)] = furnace;
        foreach (var (slot, stack) in inv) srv.Inventory[slot] = stack;
    });

    private static ItemStack[] Empty() => [ItemStack.Empty, ItemStack.Empty, ItemStack.Empty];

    [Fact]
    public async Task Charcoal_on_logs_splits_the_carried_logs_between_input_and_fuel()
    {
        var (c, s) = await Start(Empty(), (20, Harness.Stack(Blocks.Log, 2)));
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Blocks.Log, 2, Blocks.Log, false, At);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, r.Count); // one log smelts, the other burns
        var slots = s.Furnaces[(At.X, At.Y, At.Z)];
        Assert.Equal((Blocks.Log, 1), (slots[0].Id, (int)slots[0].Count));
        Assert.Equal((Blocks.Log, 1), (slots[1].Id, (int)slots[1].Count));
    }

    [Fact]
    public async Task The_only_log_is_never_loaded_as_input_with_nothing_to_burn()
    {
        var (c, s) = await Start(Empty(), (20, Harness.Stack(Blocks.Log, 1)));
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Blocks.Log, 1, Blocks.Log, false, At);
        Assert.False(r.Ok, r.Message);
        Assert.Contains("not enough fuel", r.Message);
        Assert.All(s.Furnaces[(At.X, At.Y, At.Z)], slot => Assert.True(slot.IsEmpty));
        Assert.Equal(1, c.World.Inventory.CountItem(Blocks.Log));
    }

    [Fact]
    public async Task A_refuel_tops_up_the_fuel_for_the_input_the_furnace_holds()
    {
        var (c, s) = await Start([Harness.Stack(Blocks.Log, 1), ItemStack.Empty, ItemStack.Empty], (20, Harness.Stack(Blocks.Planks, 3)));
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Blocks.Log, 0, Blocks.Planks, false, At);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("fuelled the furnace", r.Message);
        var slots = s.Furnaces[(At.X, At.Y, At.Z)];
        Assert.Equal((Blocks.Log, 1), (slots[0].Id, (int)slots[0].Count)); // the input as it was
        Assert.Equal((Blocks.Planks, 1), (slots[1].Id, (int)slots[1].Count)); // one plank smelts one log
        Assert.Equal(2, c.World.Inventory.CountItem(Blocks.Planks));
    }

    [Fact]
    public async Task A_refuel_of_an_empty_furnace_says_so()
    {
        var (c, s) = await Start(Empty(), (20, Harness.Stack(Blocks.Planks, 3)));
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Blocks.Log, 0, Blocks.Planks, false, At);
        Assert.False(r.Ok, r.Message);
        Assert.StartsWith("nothing to fuel", r.Message);
        Assert.Equal(3, c.World.Inventory.CountItem(Blocks.Planks));
    }
}
