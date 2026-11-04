using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>A furnace smelts on unseen: the container panel estimates its slots between looks (an item every 10 s while fuelled) and
/// says so; a fresh look shows what is really there.</summary>
[Collection(GameDataDefaultCollection.Name)]
public class FurnaceEstimateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly BlockPos F = new(6, 64, 3);

    private static ItemStack S(int id, int n) => new((short)id, (byte)n, 0);

    [Fact]
    public void Three_ore_loaded_read_as_ingots_by_now_and_marked_estimated()
    {
        var w = new WorldState(inventory: InventoryModel.Beta181) { Dimension = 0 };
        w.ApplyChunkRegion(0, 0, 0, 16, w.WorldHeight, 16, new byte[16 * w.WorldHeight * 16], null, null, null);
        Assert.True(w.SetBlock(F.X, F.Y, F.Z, Blocks.Furnace, 0));
        var clock = new ManualClock(T0);
        var store = new ContainerSightings();
        var k = new ContainerKnowledge(store, () => [w], () => null, () => "b1.8.1", clock);
        // Loaded with 3 iron ore; the coal went straight into burn time (the slot reads empty).
        store.Record(new ContainerSighting(0, F, [F], Blocks.Furnace, [S(15, 3), ItemStack.Empty, ItemStack.Empty], T0.ToUnixTimeMilliseconds(), "maid_chan"));
        var now = k.Get(0, F)!;
        Assert.Null(now.Estimated);
        Assert.Equal("iron_ore", now.Slots.Single().Name);

        clock.Now = T0 + TimeSpan.FromSeconds(15);
        var mid = k.Get(0, F)!;
        Assert.True(mid.Estimated);
        Assert.Equal((2, 1), (mid.Slots.Single(s => s.Slot == 0).Count, mid.Slots.Single(s => s.Slot == 2).Count));

        clock.Now = T0 + TimeSpan.FromMinutes(2);
        var done = k.Get(0, F)!;
        Assert.DoesNotContain(done.Slots, s => s.Slot == 0); // the input is used up
        Assert.Equal(("iron_ingot", 3), (done.Slots.Single().Name, done.Slots.Single().Count));

        // A fresh look wins: nothing to estimate.
        store.Record(new ContainerSighting(0, F, [F], Blocks.Furnace, [ItemStack.Empty, ItemStack.Empty, ItemStack.Empty],
            clock.Now.ToUnixTimeMilliseconds(), "maid_chan"));
        var seen = k.Get(0, F)!;
        Assert.Null(seen.Estimated);
        Assert.Empty(seen.Slots);
    }

    [Fact]
    public void The_estimate_stops_at_the_fuel_and_never_mixes_outputs()
    {
        var (est, slots) = ContainerKnowledge.EstimateFurnace([S(4, 20), S(5, 1), ItemStack.Empty], 600_000, Blocks.Furnace);
        Assert.True(est);
        Assert.Equal(20 - 9, slots[0].Count); // one plank (1.5) + a coal's worth that may have burned (8): 9
        Assert.False(ContainerKnowledge.EstimateFurnace([S(15, 3), ItemStack.Empty, S(20, 4)], 60_000, Blocks.Furnace).Estimated); // glass in the output
        Assert.False(ContainerKnowledge.EstimateFurnace([S(15, 3), ItemStack.Empty, ItemStack.Empty], 5_000, Blocks.Furnace).Estimated);
    }
}
