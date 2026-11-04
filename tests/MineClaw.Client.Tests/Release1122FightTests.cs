using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Multi-version P4 end to end over the 1.12.2 wire against <see cref="FakeServer"/>, which judges every hit like a vanilla
/// 1.12.2 server (its own charge clock, crits from its fall tracking, hurt resistance, death, a drop and an orb).
/// </summary>
public class Release1122FightTests
{
    private static Task<(MinecraftClient C, FakeServer S)> Start(Action<FakeServer>? setup = null) =>
        Harness.StartAsync(srv =>
        {
            for (int x = 0; x <= 31; x++) // room east: the zombie comes from there, and every hit knocks it back that way
            for (int z = 0; z <= 16; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Stone;
            setup?.Invoke(srv);
        }, release1122: true);

    [Fact]
    public async Task A_zombie_approaches_the_bot_waits_for_the_charge_hits_crits_kills_and_collects_the_drop_and_xp()
    {
        var (c, s) = await Start(srv => srv.Inventory[36] = Harness.Stack(Items.DiamondSword, 1));
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.HeldItem(0).Id == Items.DiamondSword));
        s.StartMobAi();
        var zombie = s.SpawnMob(MobTypes.Zombie, 14.5, 64, 8.5, health: 20, chase: true, attackDamage: 2);
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(zombie.Id) is { Profile.Name: "zombie" }));
        Assert.Equal(20f, c.World.Entities.Get(zombie.Id)!.Health);               // 1.12.2 health metadata reached the tracker

        Assert.True(await Harness.WaitAsync(() => zombie.Dead, 10000), $"zombie at {zombie.Health} hp; hits {string.Join(" ", s.MeleeHits)}");
        var hits = s.MeleeHits.Where(h => !h.Ignored).ToList();
        Assert.NotEmpty(hits);
        // The server's own charge clock: every hit waited for the cooldown (attack before swing, as vanilla 1.12.2 sends them).
        Assert.All(hits, h => Assert.True(h.Strength >= 0.85f, $"hit at charge {h.Strength}"));
        Assert.Contains(hits, h => h.Crit);                                      // jump-and-fall crit (×1.5)
        Assert.True(hits.Count <= 3, $"{hits.Count} hits for 20 hp with a diamond sword (7, crit 10.5)");
        // The attack goes out before the swing.
        var order = s.Snapshot().Where(p => p is UseEntityPacket { LeftClick: true } or AnimationPacket).ToList();
        int firstAttack = order.FindIndex(p => p is UseEntityPacket);
        Assert.True(firstAttack >= 0 && firstAttack + 1 < order.Count && order[firstAttack + 1] is AnimationPacket);

        Assert.True(await Harness.WaitAsync(() => !c.Combat.Engaged, 3000), c.Combat.Status);
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(zombie.Id) is null, 3000)); // destroyed after its death
        Assert.True(await Harness.WaitAsync(() => s.Drops.Count > 0 && s.Orbs.Count + s.XpTotal / 5 > 0, 3000));
        var pick = await c.CollectDropsAsync();
        Assert.True(pick.Ok, pick.Message);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Items.RottenFlesh) == 1, 3000));
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Snapshot.XpTotal == 5, 3000), $"xp {c.World.Player.Snapshot.XpTotal}");
        Assert.Equal(1, c.XpOrbsCollected);
    }

    [Fact]
    public async Task The_offhand_shield_goes_up_with_use_item_hand_1_and_an_axe_disable_drops_it()
    {
        var (c, s) = await Start(srv => srv.Inventory[36] = Harness.Stack(Items.WoodenSword, 1));
        await using var _ = c;
        using var __ = s;
        s.Send(new SetSlotPacket { WindowId = 0, Slot = InventoryModel.OffhandSlot, Item = Harness.Stack(Release1122CombatRules.ShieldId, 1) });
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.Offhand.Id == Release1122CombatRules.ShieldId));
        s.SpawnMob(MobTypes.Zombie, 10.2, 64, 8.5, health: 200);
        Assert.True(s.WaitFor(p => p is PlayerBlockPlacementPacket { Direction: PlayerBlockPlacementPacket.NoDirection, Hand: 1 }, 5000),
            "the shield goes up from the off hand between swings");
        // The server disables it (an axe hit): status 30 + the cooldown; the bot stops raising it for 100 ticks.
        s.Send(new EntityStatusPacket { EntityId = FakeServer.EntityId, Status = 30 });
        s.Send(new ItemCooldownMessage { ItemId = Release1122CombatRules.ShieldId, Ticks = 100 });
        Assert.True(await Harness.WaitAsync(() => c.ItemCooldownTicks(Release1122CombatRules.ShieldId) > 50));
        int before = s.Got<PlayerBlockPlacementPacket>().Count(p => p.Hand == 1);
        await Task.Delay(150); // ≈ 30 ticks of a fight
        Assert.True(s.Got<PlayerBlockPlacementPacket>().Count(p => p.Hand == 1) - before <= 1);
        c.Combat.RequestStop("test over");
    }

    [Fact]
    public async Task Effects_feed_the_dig_rule_and_the_physics_and_the_server_picks_the_hotbar_slot()
    {
        var (c, s) = await Start(srv => srv.Inventory[36] = Harness.Stack(Items.WoodenPickaxe, 1));
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.HeldItem(0).Id == Items.WoodenPickaxe));
        int plain = c.DigTicksFor(Blocks.Stone);
        s.ApplyEffect(FakeServer.EntityId, PotionEffects.Haste, 1, 600);         // haste II
        Assert.True(await Harness.WaitAsync(() => c.World.Player.EffectAmplifier(PotionEffects.Haste) == 1));
        int hasted = c.DigTicksFor(Blocks.Stone);
        var held = new ItemStack((short)Items.WoodenPickaxe, 1, 0);
        Assert.Equal(MineClaw.Data.Versioning.Release1122GameData.Instance.DigTicks(new MineClaw.Data.Versioning.DigInput(Blocks.Stone, 0, held, Haste: 2)), hasted);
        Assert.True(hasted < plain, $"haste II: {hasted} vs {plain} ticks");
        s.Send(new RemoveEntityEffectPacket { EntityId = FakeServer.EntityId, EffectId = PotionEffects.Haste });
        s.ApplyEffect(FakeServer.EntityId, PotionEffects.MiningFatigue, 0, 600);
        Assert.True(await Harness.WaitAsync(() => !c.World.Player.HasEffect(PotionEffects.Haste) && c.World.Player.HasEffect(PotionEffects.MiningFatigue)));
        Assert.True(c.DigTicksFor(Blocks.Stone) > plain);

        s.ApplyEffect(FakeServer.EntityId, PotionEffects.Speed, 1, 600);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Physics.SpeedAmplifier == 1), "the tick hands the effect to the physics");
        Assert.Equal((float)(0.10000000149011612 * 1.4000000059604645), MineClaw.World.Release1122.Physics1122.LandSpeed(false, 1, -1));
        // Another entity's effect is a tracker fact.
        var cow = s.SpawnMob(MobTypes.Cow, 4.5, 64, 4.5);
        s.ApplyEffect(cow.Id, PotionEffects.Poison, 0, 100);
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(cow.Id)?.EffectAmplifier(PotionEffects.Poison) == 0));
        Assert.Contains("mining fatigue", MineClaw.Client.Survival.SurvivalReflex.EffectFacts(c.World.Player.Effects));

        // The server's Held Item Change (P2 left it unhandled).
        s.Send(new HoldingChangePacket { Slot = 3 });
        Assert.True(await Harness.WaitAsync(() => c.World.Player.HeldSlot == 3));
    }

    [Fact]
    public async Task Death_message_and_the_respawn_through_client_status()
    {
        var (c, s) = await Start();
        await using var _ = c;
        using var __ = s;
        s.Send(new DeathMessage { PlayerId = FakeServer.EntityId, KillerId = 77, Message = "maid_chan was slain by Zombie" });
        s.SetVitals(0, 20);
        Assert.True(await Harness.WaitAsync(() => c.LastDeathMessage == "maid_chan was slain by Zombie"));
        Assert.True(s.WaitFor(p => p is RespawnPacket, 5000), "1.12.2 respawns with Client Status 0 (the canonical respawn request)");
        s.Send(new RespawnPacket { Dimension = 0, Difficulty = 1, GameMode = 0, WorldHeight = 256 });
        s.SetVitals(20, 20);
        Assert.True(await Harness.WaitAsync(() => !c.World.Player.IsDead));
    }
}
