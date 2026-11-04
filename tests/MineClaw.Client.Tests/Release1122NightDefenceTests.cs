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
/// 1337 waited at the hangout: a zombie villager, "critical hp 4/20: stopped, retreating inward", the box started at hp 4 and it
/// died building it. A fight a 1.9+ body can't win (bare hands: 2 dmg/s against 20 hp while taking 3/s) boxes in with the blocks
/// it carries (logs are walls) instead, in time; and in a normal world there is no "inward".
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
    public async Task A_zombie_on_a_bare_handed_chopping_body_at_night_makes_it_box_in_with_its_logs_and_it_lives()
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

        // It boxes in (the engine's estimate, before the zombie gets many hits in), with logs as walls.
        Assert.True(await Harness.WaitAsync(() => Shelter.Enclosed(c.World, FeetCell(c)), 15000),
            $"not boxed in: hp {c.World.Player.Health}, combat {c.Combat.Status}");
        await chop; // the chop was interrupted, not left running into the zombie
        Assert.True(c.World.Player.Health >= 10, $"hp {c.World.Player.Health}: boxed in too late");
        var f = FeetCell(c);
        int logWalls = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Count(d => c.World.GetBlock(f.X + d.Item1, f.Y, f.Z + d.Item2) == Blocks.Log);
        Assert.True(logWalls >= 3, $"the walls are its logs ({logWalls} log sides)");
        Assert.False(c.Combat.Engaged);

        // Inside the box nothing reaches it: health holds while the zombie stands outside.
        int hp = c.World.Player.Health;
        await Task.Delay(400); // ≈ 80 ticks: 4 zombie attacks if it could reach
        Assert.True(c.World.Player.Health >= hp, $"hp {hp} → {c.World.Player.Health} inside the box");
        Assert.False(zombie.Dead);
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
        Assert.Contains("boxing in", crit.Value.Message);

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
