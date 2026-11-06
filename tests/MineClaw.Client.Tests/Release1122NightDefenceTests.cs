using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 2026-10-29, the first 1.12.2 night in a normal world: clone 69 chopped bare-handed with 30 spruce logs, a zombie came,
/// "fight started: zombie 5m away (defending myself)", "low health 8/20 …, work goes on", slain 3 s later (3 hits, took 22). Clone
/// Normal-world self defence: fight or retreat without placing walls that block other pawns joining the fight.
/// </summary>
public class Release1122NightDefenceTests
{
    private static Task<(MinecraftClient C, FakeServer S)> Start(Action<FakeServer>? setup = null) =>
        Harness.StartAsync(srv =>
        {
            for (int x = 0; x <= 16; x++)
            for (int z = 0; z <= 16; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Grass;
            srv.Difficulty = 2; // normal, as live
            srv.ConsumeOnPlace = true; // a vanilla server takes the placed block (else every box block waits out the 300 ms check)
            setup?.Invoke(srv);
        }, release1122: true);

    [Fact]
    public void Bare_hands_lose_to_a_zombie_and_a_stone_sword_wins_on_1_12_2()
    {
        // The estimate: kill time = mob hp / our charged dps, die time = (hp − 1) / their dps (the target, knocked back by each
        // hit, lands 0.6 of its hits); lost when kill > ¾ die. Normal difficulty: a zombie hits 3.
        var rules = Release1122CombatRules.Instance;
        double hand = WeaponChoice.Dps(0, rules)!.Value, stone = WeaponChoice.Dps(Items.StoneSword, rules)!.Value;
        double die = 19 / (3.0 * CombatEngine.KnockedBackHitShare) * 0.75;
        Assert.Equal(2.0, hand, 3);
        Assert.True(20 / hand > die);   // 10 s to kill, 7.9 s: box in
        Assert.True(20 / stone <= die); // 2.5 s: fight
        Assert.Null(WeaponChoice.Dps(0, null));     // b1.8.1: no estimate, the old behaviour
    }

    [Fact]
    public async Task A_zombie_on_a_bare_handed_chopping_body_at_night_does_not_make_it_wall_itself_in()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(srv =>
        {
            for (int y = 64; y <= 69; y++) { srv.Blocks[(11, y, 8)] = Blocks.Log; srv.Metas[(11, y, 8)] = 1; } // a spruce trunk
            srv.Inventory[9] = new ItemStack(Blocks.Log, 30, 1);                                                // 30 spruce logs
        });
        await using var _ = c;
        using var __ = s;
        s.Send(new TimeUpdatePacket { Time = 18000 });
        Assert.True(await Harness.WaitAsync(() => c.World.IsNight));
        s.StartMobAi();
        var chop = c.ChopTreeAsync(new BlockPos(11, 64, 8));
        var zombie = s.SpawnMob(MobTypes.Zombie, 3.5, 64, 8.5, health: 20, chase: true, attackDamage: 3);
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(zombie.Id) is { Profile.Name: "zombie" }));

        Assert.False(c.Options.Survival.ShelterEnabled);
        Assert.True(await Harness.WaitAsync(() => s.MeleeHits.Any(h => h.Target == zombie.Id) || c.World.Player.Health < 20, 10000),
            $"the fight never started: {c.Combat.Status}");
        await chop; // the chop was interrupted, not left running into the zombie
        s.SetVitals(6, 20); // low-health self defence must not enable the box either
        await Task.Delay(400);
        Assert.Empty(c.TemporaryBlocks());
        Assert.True(c.World.Inventory.CountItem(Blocks.Log) >= 30);
    }

    [Fact]
    public async Task With_a_stone_sword_the_same_zombie_is_fought_and_killed()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(srv =>
        {
            srv.Inventory[36] = Harness.Stack(Items.StoneSword, 1);
            srv.Inventory[9] = new ItemStack(Blocks.Log, 30, 1);
        });
        await using var _ = c;
        using var __ = s;
        s.StartMobAi();
        var zombie = s.SpawnMob(MobTypes.Zombie, 3.5, 64, 8.5, health: 20, chase: true, attackDamage: 3);
        Assert.True(await Harness.WaitAsync(() => zombie.Dead, 15000), $"zombie at {zombie.Health}; hp {c.World.Player.Health}");
        Assert.Equal(30, c.World.Inventory.CountItem(Blocks.Log)); // no box needed
    }

    /// <summary>Live 10-29: Fox, then Robin, were killed by a zombie at a door while main warned them. A zombie on a guarded player
    /// 10 blocks away at night is fought, bare-handed and with no food, not boxed in against (the body's own hp is fine).</summary>
    [Fact]
    public async Task At_night_with_no_food_a_zombie_on_a_trusted_player_ten_blocks_away_is_fought()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(srv =>
        {
            srv.SpawnAt = (2.5, 64, 2.5);
            srv.Inventory[9] = new ItemStack(Blocks.Log, 30, 1);
        });
        await using var _ = c;
        using var __ = s;
        s.Send(new TimeUpdatePacket { Time = 18000 });
        Assert.True(await Harness.WaitAsync(() => c.World.IsNight));
        s.Send(new NamedEntitySpawnPacket { EntityId = 77, PlayerName = "PoofimaFox", Exact = (9.5, 64, 9.5) });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.FindPlayer("PoofimaFox") is not null));
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        c.CombatEventRaised += e => events.Enqueue(e.Message);
        var zombie = s.SpawnMob(MobTypes.Zombie, 11.5, 64, 8.5, health: 20); // by Fox, 9.9 blocks from the bot
        Assert.True(await Harness.WaitAsync(() => s.MeleeHits.Any(h => h.Target == zombie.Id && !h.Ignored), 10000),
            $"no hit on the zombie by Fox; combat {c.Combat.Status}, hp {c.World.Player.Health}, zombie {c.World.Entities.Get(zombie.Id)?.Position} {c.World.Entities.Get(zombie.Id)?.Profile?.Name}, fox {c.World.Entities.FindPlayer("PoofimaFox")?.Position}, me {c.World.Player.Position}, events: {string.Join(" | ", events)}");
        Assert.False(Shelter.Enclosed(c.World, FeetCell(c)));
        c.Combat.RequestStop("test over");
    }

    [Fact]
    public void A_normal_world_low_alert_with_a_mob_on_the_body_says_what_happens_and_never_retreats_inward()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var r = new SurvivalReflex();
        var low = r.OnHealth(8, 20, false, "zombie 2m away");
        Assert.NotNull(low);
        Assert.DoesNotContain("work goes on", low!.Value.Message);
        Assert.Contains("zombie 2m away", low.Value.Message);
        var crit = r.OnHealth(4, 20, false, "zombie villager 1m away");
        Assert.DoesNotContain("inward", crit!.Value.Message);
        Assert.Contains("getting away", crit.Value.Message);

        using var sky = WorldMode.Override(WorldMode.SkyblockWorld);
        var r2 = new SurvivalReflex();
        Assert.Contains("work goes on", r2.OnHealth(8, 20, false)!.Value.Message);
        Assert.Contains("retreating inward", r2.OnHealth(4, 20, false)!.Value.Message);
    }

    private static BlockPos FeetCell(MinecraftClient c)
    {
        var p = c.World.Player.Position;
        return new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y + 1e-6), (int)Math.Floor(p.Z));
    }
}
