using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// The 1.12.2 stations end to end (MinecraftClient.Stations.cs against FakeServer.Stations.cs over the 1.12.2 wire): a brewing stand's
/// window slot flow through a two-step chain, and an enchanting table's offers read and one taken with Enchant Item by the bill's rule.
/// </summary>
public class Release1122StationTests
{
    private static readonly BlockPos At = new(9, 64, 8);

    [Fact]
    public async Task A_brew_loads_bottles_fuel_and_each_ingredient_then_takes_the_potions()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Brewing.StandBlock;
            srv.BrewingStands[(At.X, At.Y, At.Z)] = Enumerable.Repeat(ItemStack.Empty, 5).ToArray();
            for (int i = 0; i < 3; i++) srv.Inventory[36 + i] = FakeServer.Potion("water");
            srv.Inventory[39] = Harness.Stack(Brewing.NetherWart, 4);
            srv.Inventory[40] = Harness.Stack(353, 2); // sugar
            srv.Inventory[41] = Harness.Stack(Brewing.BlazePowder, 1);
        }, release1122: true);
        await using var _ = c;
        using var __ = s;

        var chain = Brewing.Chain(Brewing.Potion, "swiftness")!;
        var r = await c.BrewAsync(At, "water", Brewing.Potion, chain, 3);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("brewed 3 Potion of Swiftness", r.Message);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.PlayerSlots().Count(x => x.Id == Brewing.Potion && x.Tag?.Potion == "swiftness") == 3));
        Assert.Equal(3, c.World.Inventory.CountItem(Brewing.NetherWart));
        Assert.Equal(1, c.World.Inventory.CountItem(353));
        Assert.Equal(0, c.World.Inventory.CountItem(Brewing.BlazePowder)); // into the stand's fuel slot, burnt
        Assert.Equal(6, s.Brewed.Count); // two brews of three bottles
        Assert.All(s.Clicks, k => Assert.True(k.Accepted, $"click {k} rejected"));
        Assert.All(s.BrewingStands[(At.X, At.Y, At.Z)].Take(4), x => Assert.True(x.IsEmpty));
    }

    [Fact]
    public async Task A_brew_without_a_mix_the_stand_takes_gives_its_bottles_back()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Brewing.StandBlock;
            srv.BrewingStands[(At.X, At.Y, At.Z)] = Enumerable.Repeat(ItemStack.Empty, 5).ToArray();
            srv.Inventory[36] = FakeServer.Potion("water");
            srv.Inventory[37] = Harness.Stack(353, 1); // sugar on water: no mix
            srv.Inventory[38] = Harness.Stack(Brewing.BlazePowder, 1);
        }, release1122: true);
        await using var _ = c;
        using var __ = s;

        var steps = new[] { new Brewing.Step(new Brewing.Ingredient(353, 0, "sugar"), "water", Brewing.Potion, "swiftness", Brewing.Potion) };
        var r = await c.BrewAsync(At, "water", Brewing.Potion, steps, 1);
        Assert.False(r.Ok);
        Assert.StartsWith("the stand won't brew sugar", r.Message);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.PlayerSlots().Any(x => x.Id == Brewing.Potion && x.Tag?.Potion == "water")));
        Assert.Equal(1, c.World.Inventory.CountItem(353));
    }

    private static Task<(MinecraftClient C, FakeServer S)> Table(int level) => Harness.StartAsync(srv =>
    {
        srv.Blocks[(At.X, At.Y, At.Z)] = Enchanting.TableBlock;
        srv.EnchantingTables.Add((At.X, At.Y, At.Z));
        srv.XpLevel = level;
        srv.OnLogin = srv.SendXp;
        srv.Inventory[36] = Harness.Stack(278, 1); // a diamond pickaxe
        srv.Inventory[37] = Harness.Stack(Enchanting.Lapis, 5, Enchanting.LapisMeta);
    }, release1122: true);

    [Fact]
    public async Task An_enchant_reads_the_offers_and_takes_the_preferred_one()
    {
        var (c, s) = await Table(30);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Snapshot.XpLevel == 30));

        var r = await c.EnchantAsync(At, 278, 3, Enchanting.ByName("fortune")!.Id, preferOnly: false);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Fortune II", r.Message);
        Assert.Equal([2], s.Enchanted);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.PlayerSlots().Any(x => x.Id == 278 && x.Tag?.EnchantmentLevel(35) == 2)));
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Snapshot.XpLevel == 27));
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Enchanting.Lapis) == 2)); // 3 paid, the rest given back
    }

    [Fact]
    public async Task An_enchant_takes_the_highest_offer_it_can_pay_or_says_why_not()
    {
        var (c, s) = await Table(10);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Snapshot.XpLevel == 10));

        var only = await c.EnchantAsync(At, 278, 3, Enchanting.ByName("silk_touch")!.Id, preferOnly: true);
        Assert.False(only.Ok);
        Assert.StartsWith("no offer shows Silk Touch", only.Message);
        Assert.Empty(s.Enchanted);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.PlayerSlots().Any(x => x.Id == 278 && x.Tag is null)));

        var r = await c.EnchantAsync(At, 278, 3, null, preferOnly: false);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Efficiency II", r.Message); // offer 2 (level 9); offer 3 needs 18
        Assert.Equal([1], s.Enchanted);
    }
}
