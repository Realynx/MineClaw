using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Live session 5 (2026-10-27 evening) client fixes: eat when full, empty takes that name what the chest held, the
/// join status line before health/chunks, the reflex meal reported before its slot update.</summary>
public class LiveSession5ClientTests
{
    [Fact]
    public async Task Eat_when_already_full_is_ok_not_a_failure()
    {
        // 21:05: the survival reflex had just eaten; the Opus script's eat{} then failed "not hungry (food 20/20)".
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Items.Bread, 3));
        await using var _ = c;
        using var __ = s;
        s.SetVitals(20, 20, 5);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Food == 20));
        var r = await c.EatAsync();
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("already full (food 20/20)", r.Message);
        Assert.Equal(3, c.World.Inventory.CountItem(Items.Bread));
        var used = await c.UseItemAsync(Items.Bread);
        Assert.True(used.Ok, used.ToString());
        Assert.Contains("already full", used.Message);
    }

    [Fact]
    public async Task Take_of_an_item_the_chest_no_longer_has_names_what_it_held()
    {
        // 21:06: Fox had taken the bread back out; "none in container or inventory full" left 67 unable to tell emptiness from a bug.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(8, 64, 10)] = Blocks.Chest;
            var chest = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            chest[0] = Harness.Stack(Blocks.Log, 40);
            chest[5] = Harness.Stack(Blocks.Cobblestone, 64);
            chest[6] = Harness.Stack(Items.StoneAxe, 1);
            srv.Chests[(8, 64, 10)] = chest;
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.WithdrawAsync(Items.Bread, 5, new BlockPos(8, 64, 10));
        Assert.False(r.Ok);
        Assert.StartsWith("no bread in the chest at 8,64,10 (it had: ", r.Message);
        Assert.Contains("cobblestone x64", r.Message);
        Assert.Contains(" x40", r.Message); // the 40 logs (display name "oak wood")
        Assert.Contains("stone axe x1", r.Message.ToLowerInvariant().Replace('_', ' '));
    }

    [Fact]
    public async Task Take_from_an_empty_chest_says_it_is_empty()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(8, 64, 10)] = Blocks.Chest;
            srv.Chests[(8, 64, 10)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.WithdrawAsync(Items.Bread, 5, new BlockPos(8, 64, 10));
        Assert.False(r.Ok);
        Assert.Equal("no bread in the chest at 8,64,10: it is empty", r.Message);
    }

    [Fact]
    public async Task Join_status_never_shows_default_vitals_or_a_fake_void()
    {
        // 21:03:50: "hp 20/20 food 20/20 … airborne | OVER VOID" before the 0x08 and the chunks; a second later food 10, ground verified.
        var c = new MinecraftClient(new ClientOptions { Username = "maid_chan" });
        var st = c.DescribeStatus();
        Assert.Contains("hp/food loading…", st);
        Assert.Contains("ground loading…", st);
        Assert.DoesNotContain("20/20", st);
        Assert.DoesNotContain("OVER VOID", st);
        Assert.DoesNotContain("airborne", st);

        var (live, s) = await Harness.StartAsync();
        await using var _ = live;
        using var __ = s;
        Assert.True(await live.WaitForJoinStateAsync(TimeSpan.FromSeconds(3)));
        var ok = live.DescribeStatus();
        Assert.Contains("hp 20/20 food 20/20", ok);
        Assert.DoesNotContain("loading", ok);
    }

    [Fact]
    public async Task Hp_7_and_no_food_still_mines_at_the_gen()
    {
        // Item 13, live clone 67: "Cobble run stalled: HP 7/20 with no food, tools refuse mining below HP 10".
        // The user's rule: nothing in Minecraft needs food or health to swing a pickaxe.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(10, 64, 8)] = Blocks.Cobblestone;
            srv.Regenerate[(10, 64, 8)] = Blocks.Cobblestone;
            srv.DropOnDig = true;
            srv.Inventory[36] = Harness.Stack(Items.WoodenPickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        s.SetVitals(7, 0);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 7 && c.World.Player.Food == 0));
        var r = await c.HarvestGeneratorAsync(10, 64, 8, 16); // a "long mining trip" (LongMiningCount 16) used to be refused
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(16, r.Count);
        Assert.DoesNotContain("too low", r.Message);
    }

    [Fact]
    public async Task Eating_is_announced_before_the_item_leaves_the_inventory()
    {
        // The ledger booked every reflex meal as "vanished without an action": the slot update lands after the eat action ends.
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Items.Bread, 3));
        await using var _ = c;
        using var __ = s;
        s.SetVitals(20, 10, 0);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Food == 10));
        var started = new List<(int Id, bool Auto)>();
        c.EatingStarted += (id, auto) => { lock (started) started.Add((id, auto)); };
        await c.EatAsync();
        Assert.True(await Harness.WaitAsync(() => { lock (started) return started.Count > 0; }));
        lock (started) Assert.Equal(Items.Bread, started[0].Id);
    }
}
