using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class ExactSmeltInputTests
{
    private static readonly BlockPos At = new(10, 64, 10);
    private static Task<(MinecraftClient Client, FakeServer Server)> Start(int id, int wanted, int other, ItemStack[]? contents = null, bool modern = true) =>
        Harness.StartAsync(s =>
        {
            s.Blocks[(At.X, At.Y, At.Z)] = Blocks.Furnace;
            s.Furnaces[(At.X, At.Y, At.Z)] = contents ?? [ItemStack.Empty, ItemStack.Empty, ItemStack.Empty];
            s.Inventory[9] = Harness.Stack(id, 6, other); // wrong variant comes first in inventory order
            s.Inventory[10] = Harness.Stack(id, 2, wanted);
            s.Inventory[11] = Harness.Stack(Items.Coal, 8);
        }, release1122: modern);

    [Theory]
    [InlineData(349, 1, 0)]
    [InlineData(349, 0, 1)]
    [InlineData(17, 1, 2)]
    public async Task Mixed_carried_variants_only_load_and_count_the_selected_input(int id, int wanted, int other)
    {
        var (c, s) = await Start(id, wanted, other);
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(id, 12, Items.Coal, furnace: At, inputMeta: wanted);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.Count);
        var input = s.Furnaces[(At.X, At.Y, At.Z)][0];
        Assert.Equal((id, wanted, 2), ((int)input.Id, (int)input.Damage, (int)input.Count));
        Assert.Equal(6, c.World.Inventory.CountItem(id, other));
        Assert.Equal(0, c.World.Inventory.CountItem(id, wanted));
    }

    [Fact]
    public async Task Other_variants_are_not_substituted_when_the_exact_input_is_absent()
    {
        var (c, s) = await Start(Items.RawFish, 0, 0);
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Items.RawFish, 2, Items.Coal, furnace: At, inputMeta: 1);
        Assert.False(r.Ok);
        Assert.StartsWith("no ", r.Message);
        Assert.All(s.Furnaces[(At.X, At.Y, At.Z)], stack => Assert.True(stack.IsEmpty));
        Assert.Equal(8, c.World.Inventory.CountItem(Items.Coal));
        Assert.Equal(8, c.World.Inventory.CountItem(Items.RawFish, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_conflicting_existing_input_or_output_is_refused_before_spending_fuel(bool output)
    {
        ItemStack[] contents = [ItemStack.Empty, ItemStack.Empty, ItemStack.Empty];
        contents[output ? 2 : 0] = Harness.Stack(output ? Items.CookedFish : Items.RawFish, 3, 0);
        var (c, s) = await Start(Items.RawFish, 1, 0, contents);
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Items.RawFish, 2, Items.Coal, furnace: At, inputMeta: 1);
        Assert.False(r.Ok);
        Assert.Contains(output ? "output holds" : "busy with", r.Message);
        Assert.True(s.Furnaces[(At.X, At.Y, At.Z)][1].IsEmpty);
        Assert.Equal(8, c.World.Inventory.CountItem(Items.Coal));
        Assert.Equal(2, c.World.Inventory.CountItem(Items.RawFish, 1));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(true, 1)]
    public async Task Same_family_fuel_does_not_consume_the_reserved_input_variant(bool modern, int fuelMeta)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Blocks.Furnace;
            srv.Furnaces[(At.X, At.Y, At.Z)] = [ItemStack.Empty, ItemStack.Empty, ItemStack.Empty];
            srv.Inventory[9] = Harness.Stack(Blocks.Log, 1, 1); // the chosen spruce input precedes the birch fuel
            srv.Inventory[10] = Harness.Stack(Blocks.Log, 1, fuelMeta);
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        ItemStack[]? closed = null;
        c.World.Inventory.WindowClosed += w => closed = [w.Get(0), w.Get(1), w.Get(2)];
        var r = await c.SmeltAsync(Blocks.Log, 1, Blocks.Log, furnace: At, inputMeta: 1);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, r.Count);
        Assert.NotNull(closed);
        var slots = modern ? s.Furnaces[(At.X, At.Y, At.Z)] : closed;
        Assert.Equal((Blocks.Log, 1), ((int)slots[0].Id, (int)slots[0].Damage));
        Assert.Equal((Blocks.Log, fuelMeta), ((int)slots[1].Id, (int)slots[1].Damage));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32768)]
    [InlineData(2)] // clownfish cannot be cooked, even with ordinary cod carried
    public async Task Invalid_or_unsmeltable_exact_input_never_loads_a_furnace(int meta)
    {
        var (c, s) = await Start(Items.RawFish, 1, 0);
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Items.RawFish, 2, Items.Coal, furnace: At, inputMeta: meta);
        Assert.False(r.Ok);
        Assert.All(s.Furnaces[(At.X, At.Y, At.Z)], stack => Assert.True(stack.IsEmpty));
        Assert.Equal(8, c.World.Inventory.CountItem(Items.Coal));
    }
}
