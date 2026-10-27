using System.Collections.Concurrent;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Survival reflex rules (pure) — b1.8.1 hunger facts, food choice, thresholds, debouncing, gating.</summary>
public class SurvivalRuleTests
{
    private static readonly SurvivalOptions O = new();
    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    private static ItemStack[] Inv(params (int Slot, int Id, int Count)[] items)
    {
        var a = Enumerable.Repeat(ItemStack.Empty, 45).ToArray();
        foreach (var (slot, id, n) in items) a[slot] = new ItemStack((short)id, (byte)n, 0);
        return a;
    }

    [Fact]
    public void B181_HungerFacts()
    {
        Assert.Equal(18, HungerRules.RegenFood);
        Assert.Equal(32, HungerRules.EatTicks);
        Assert.Equal(10, HungerRules.StarvationFloor(1));   // easy: starvation stops at 5 hearts
        Assert.Equal(1, HungerRules.StarvationFloor(2));
        Assert.Equal(0, HungerRules.StarvationFloor(3));
        Assert.Equal((5, 0.6f), Items.Food(Items.Bread));
        Assert.Equal((8, 0.8f), Items.Food(Items.Steak));
        Assert.Equal((4, 0.1f), Items.Food(Items.RottenFlesh));
        Assert.Equal(12.8f, HungerRules.SaturationGain(8, 0.8f), 3);
        Assert.True(HungerRules.IsRisky(Items.RottenFlesh) && HungerRules.IsRisky(Items.RawChicken));
        Assert.False(HungerRules.IsRisky(Items.Porkchop));
    }

    [Theory]
    [InlineData(20, 20, EatReason.None)]
    [InlineData(20, 15, EatReason.None)]
    [InlineData(20, 14, EatReason.Hungry)]
    [InlineData(19, 17, EatReason.Regenerate)]
    [InlineData(19, 18, EatReason.None)]
    [InlineData(8, 17, EatReason.Urgent)]
    [InlineData(20, 6, EatReason.Urgent)]
    public void WhenToEat(int hp, int food, EatReason expected) => Assert.Equal(expected, SurvivalReflex.WantsEat(hp, food, O));

    [Fact]
    public void FoodChoice_FitsTheDeficit_SkipsReservedPoisonAndGoldenApples()
    {
        var inv = Inv((36, Items.Steak, 4), (37, Items.Bread, 4));
        Assert.Equal(Items.Bread, SurvivalReflex.PickFood(inv, None, 19, 16, O)!.Value.ItemId);  // deficit 4: bread wastes 1, steak 4
        Assert.Equal(Items.Steak, SurvivalReflex.PickFood(inv, None, 20, 4, O)!.Value.ItemId);   // deficit 16: the big meal
        Assert.Equal(Items.Steak, SurvivalReflex.PickFood(inv, new HashSet<int> { Items.Bread }, 19, 16, O)!.Value.ItemId);
        Assert.Null(SurvivalReflex.PickFood(inv, new HashSet<int> { Items.Bread, Items.Steak }, 20, 4, O));

        var flesh = Inv((36, Items.RottenFlesh, 8));
        Assert.Null(SurvivalReflex.PickFood(flesh, None, 20, 10, O));                            // not starving: no poison
        Assert.Equal(Items.RottenFlesh, SurvivalReflex.PickFood(flesh, None, 20, 6, O)!.Value.ItemId);
        var mixed = Inv((36, Items.RottenFlesh, 8), (12, Items.Apple, 1));
        Assert.Equal(Items.Apple, SurvivalReflex.PickFood(mixed, None, 20, 2, O)!.Value.ItemId); // anything safe first

        var gold = Inv((36, Items.GoldenApple, 1));
        Assert.Null(SurvivalReflex.PickFood(gold, None, 10, 10, O));
        Assert.Equal(Items.GoldenApple, SurvivalReflex.PickFood(gold, None, 4, 10, O)!.Value.ItemId);
    }

    [Fact]
    public void RegenerationOnlyMeal_AvoidsWaste_UnlessHurtBadly()
    {
        var steak = Inv((36, Items.Steak, 4));
        Assert.Null(SurvivalReflex.EatNow(steak, None, 18, 17, O));        // 5 points wasted for 2 hp: wait
        Assert.NotNull(SurvivalReflex.EatNow(steak, None, 12, 17, O));     // hurt: eat it
        Assert.NotNull(SurvivalReflex.EatNow(Inv((36, Items.Cookie, 4)), None, 18, 17, O));
        Assert.Null(SurvivalReflex.EatNow(steak, None, 20, 15, O));        // full health, food 15: nothing to do
    }

    [Fact]
    public void HealthAlerts_OncePerEpisode_WithHysteresis()
    {
        var r = new SurvivalReflex();
        Assert.Null(r.OnHealth(12, 20, true));
        var low = r.OnHealth(8, 20, true);
        Assert.NotNull(low);
        Assert.False(low!.Value.Critical);
        Assert.StartsWith("low health 8/20", low.Value.Message);
        Assert.Null(r.OnHealth(7, 20, true));
        Assert.Null(r.OnHealth(10, 20, true));   // not above 8 + 2 yet
        Assert.Null(r.OnHealth(8, 20, true));
        var crit = r.OnHealth(4, 9, false);
        Assert.True(crit!.Value.Critical);
        Assert.Contains("no food!", crit.Value.Message);
        Assert.Null(r.OnHealth(3, 9, false));
        Assert.Null(r.OnHealth(11, 20, true));   // re-armed
        Assert.NotNull(r.OnHealth(8, 20, true));
    }

    [Fact]
    public void OutOfFood_OneEventPerSeverity_WithAMemoryHint()
    {
        var r = new SurvivalReflex { FoodHint = () => "chest at 3,64,2 had bread" };
        var empty = Inv();
        Assert.Null(r.CheckFood(15, empty, None));
        var e1 = r.CheckFood(9, empty, None);
        Assert.Equal("hungry: food 9, no food in inventory; memory: chest at 3,64,2 had bread", e1!.Message);
        Assert.Equal(1, e1.Level);
        Assert.Null(r.CheckFood(8, empty, None));
        var e2 = r.CheckFood(6, empty, None);
        Assert.Equal(2, e2!.Level);
        Assert.StartsWith("starving: food 6", e2.Message);
        Assert.Null(r.CheckFood(5, empty, None));
        var e3 = r.CheckFood(0, empty, None);
        Assert.Equal(3, e3!.Level);
        Assert.Contains("stops at 10", e3.Message);
        Assert.True(e3.Critical);
        Assert.Null(r.CheckFood(0, Inv((36, Items.Bread, 1)), None));    // food found: reset
        Assert.NotNull(r.CheckFood(12, empty, None));                     // a new episode
        var risky = new SurvivalReflex();
        Assert.Contains("only risky food", risky.CheckFood(10, Inv((36, Items.RottenFlesh, 3)), None)!.Message);
        Assert.Null(risky.CheckFood(5, Inv((36, Items.RottenFlesh, 3)), None));  // starving: it will eat it
    }

    [Fact]
    public void Gate_RefusesRiskyStarts_WithClearReasons()
    {
        Assert.Null(SurvivalReflex.GateReason("bridge", 20, 5, false, O));                    // b1.8.1: hunger never blocks digging/placing
        Assert.Null(SurvivalReflex.GateReason("mine 64 blocks", 20, 2, false, O));
        Assert.Equal("too hungry to fight (food 5, no food)", SurvivalReflex.GateReason("fight", 20, 5, false, O));
        Assert.Null(SurvivalReflex.GateReason("bridge", 20, 5, true, O));                     // eats first
        Assert.Equal("hp 7/20 too low to fight (need 10) (food 9, no food)", SurvivalReflex.GateReason("fight", 7, 9, false, O));
        Assert.Equal("hp 7/20 too low to spar (need 10); eating to regenerate", SurvivalReflex.GateReason("spar", 7, 9, true, O));
        Assert.Null(SurvivalReflex.GateReason("mine 32 blocks", 14, 12, false, O));
        Assert.Null(SurvivalReflex.GateReason("bridge", 1, 1, false, O with { Enabled = false }));
        // Item 13: hp 7, food 0, no food: mining, chopping, building and bridging all go ahead; only fights/spars wait.
        Assert.Null(SurvivalReflex.GateReason("mine 32 blocks", 7, 0, false, O));
        Assert.Null(SurvivalReflex.GateReason("chop a tree", 7, 0, false, O));
        Assert.Null(SurvivalReflex.GateReason("pillar_up", 7, 0, false, O));
        Assert.Null(SurvivalReflex.GateReason("bridge", 7, 0, false, O));
        Assert.Equal("hp 4/20 too low to bridge over the void (need more than 4)", SurvivalReflex.GateReason("bridge", 4, 0, false, O));
        Assert.Equal("hp 3/20 too low to expand_island over the void (need more than 4)", SurvivalReflex.GateReason("expand_island", 3, 0, false, O, edge: true));
        Assert.NotNull(SurvivalReflex.GateReason("fight", 7, 0, false, O));
    }

    [Fact]
    public void Header_IsCompact()
    {
        Assert.Equal("hp 14/20 food 9 (no food!)", SurvivalReflex.HeaderText(14, 9, Inv(), None));
        Assert.Equal("hp 20/20 food 9", SurvivalReflex.HeaderText(20, 9, Inv((36, Items.Bread, 2)), None));
        Assert.Equal("hp 7/20 food 5 (hungry) LOW", SurvivalReflex.HeaderText(7, 5, Inv((36, Items.Bread, 2)), None));
        Assert.Equal("hp 3/20 food 0 (starving, no food!) CRITICAL", SurvivalReflex.HeaderText(3, 0, Inv(), None));
        Assert.Equal("hp 20/20 food 12 (only risky food!)", SurvivalReflex.HeaderText(20, 12, Inv((36, Items.RottenFlesh, 2)), None));
    }
}

/// <summary>The reflex on the real client against the fake server.</summary>
public class SurvivalClientTests
{
    [Fact]
    public async Task AutoEats_WhenIdleAndHungry_AndNeverEatsReservedFood()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 10);
            srv.Inventory[37] = Harness.Stack(Items.Bread, 3);
            srv.Inventory[20] = Harness.Stack(Items.Apple, 2);
        }, o => o with { ReservedItems = new HashSet<int> { Items.Apple } });
        await using var _ = c;
        using var __ = s;
        s.SetVitals(20, 12);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Food >= 17, 4000), $"food {c.World.Player.Food}");
        Assert.Equal(Items.Bread, s.Eaten.First());
        Assert.DoesNotContain(Items.Apple, s.Eaten);
        Assert.True(c.AutoMeals >= 1);
        Assert.Contains(s.Got<PlayerBlockPlacementPacket>(), p => p.Direction == PlayerBlockPlacementPacket.NoDirection && p.HeldItem.Id == Items.Bread);
    }

    [Fact]
    public async Task NoFood_OneHungryEvent_AndHungerDoesNotGateBuilding()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32));
        await using var _ = c;
        using var __ = s;
        var dangers = new ConcurrentQueue<DangerEvent>();
        c.Danger += dangers.Enqueue;
        s.SetVitals(20, 5);
        Assert.True(await Harness.WaitAsync(() => dangers.Any(d => d.Kind == DangerKind.Hunger), 2000));
        await Task.Delay(300);
        var hunger = dangers.Where(d => d.Kind == DangerKind.Hunger).ToList();
        Assert.Single(hunger);
        Assert.Equal("starving: food 5, no food in inventory (no sprint, no regen)", hunger[0].Message);
        var r = await c.GotoAsync(new GoalBlock(14, 64, 8), GotoOptions.Default with { AllowPlace = true });
        Assert.True(r.Ok, r.ToString()); // b1.8.1: hunger never blocks placing/bridging
        Assert.True((await c.GotoAsync(new GoalBlock(9, 64, 9))).Ok); // plain walking is not gated
    }

    [Fact]
    public async Task LowHealth_BridgingGoesOn_OnlyCriticalHpRefusesVoidWork()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32);
            srv.Inventory[37] = Harness.Stack(Items.Bread, 3);
        });
        await using var _ = c;
        using var __ = s;
        var dangers = new ConcurrentQueue<DangerEvent>();
        c.Danger += dangers.Enqueue;
        var bridge = c.GotoAsync(new GoalBlock(20, 64, 8), GotoOptions.Default with { AllowPlace = true, Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(await Harness.WaitAsync(() => c.CurrentAction == "goto", 1000));
        await Task.Delay(50);
        s.SetVitals(7, 20);
        // Item 13 (the user's rule): low hp no longer stops work; only fights/spars, and void work at critical hp.
        var r = await bridge.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(r.Ok, r.ToString());
        Assert.Single(dangers, d => d.Kind == DangerKind.LowHealth && !d.Critical);
        s.SetVitals(4, 20);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 4));
        var refused = await c.GotoAsync(new GoalBlock(26, 64, 8), GotoOptions.Default with { AllowPlace = true });
        Assert.Contains("hp 4/20 too low to bridge over the void (need more than 4)", refused.Message);
    }

    [Fact]
    public async Task ContainerOpened_ReportsTheActivatedChestPosition()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(8, 64, 10)] = Blocks.Chest;
            srv.Chests[(8, 64, 10)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        });
        await using var _ = c;
        using var __ = s;
        var got = new ConcurrentQueue<(int, int, int, int, int)>();
        c.ContainerOpened += (id, type, x, y, z) => got.Enqueue((id, type, x, y, z));
        var r = await c.OpenContainerAsync(8, 64, 10);
        Assert.True(r.Ok, r.ToString());
        Assert.True(await Harness.WaitAsync(() => got.Count == 1, 1000));
        Assert.Equal((1, 0, 8, 64, 10), got.Single());
    }
}
