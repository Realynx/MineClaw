using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Engine-level rig: a flat stone platform (|x|,|z| ≤ half at y=63) over the void, the bot at (0.5, 64, 0.5).</summary>
internal sealed class CombatRig
{
    public readonly WorldState W = new();
    public readonly CombatEngine E;
    public readonly List<string> Lines = new();
    public readonly List<CombatEvent> Events = new();
    public readonly EdgeGuard Guard = new();
    public long Tick;
    public PhysicsState P => W.Player.Physics;

    public CombatRig(CombatOptions? o = null, int half = 10, double x = 0.5, double z = 0.5, int minX = int.MinValue)
    {
        W.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        for (int bx = Math.Max(-half, minX); bx <= half; bx++)
        for (int bz = -half; bz <= half; bz++)
            W.SetBlock(bx, 63, bz, Blocks.Stone, 0);
        P.Teleport(new Vec3d(x, 64, z), 0, 0);
        P.OnGround = true;
        W.Player.SyncFromPhysics();
        W.Player.SetHealth(20, 20, 5);
        W.Time = 6000;
        var tel = new CombatTelemetry(null) { Sink = Lines.Add };
        E = new CombatEngine(W, o ?? new CombatOptions { TelemetryDirectory = null }, tel) { Username = "maid_chan" };
        E.EventRaised += Events.Add;
    }

    /// <summary>One engine tick; with <paramref name="physics"/> the input goes through the EdgeGuard and the real physics.</summary>
    public CombatStep Step(bool physics = false, MoveInput? proposed = null)
    {
        var s = E.Tick(P, Tick, proposed ?? new MoveInput(Yaw: P.Yaw, Pitch: P.Pitch));
        if (physics)
        {
            var g = Guard.Filter(W, P, s.Input);
            E.OnGuard(g);
            PlayerPhysics.Tick(W, P, g.Input);
            W.Player.SyncFromPhysics();
        }
        Tick++;
        return s;
    }

    public Entity Mob(int id, int type, double x, double z, Dictionary<int, object>? meta = null) =>
        W.Entities.SpawnMob(id, type, new Vec3d(x, 64, z), 0, 0, meta);

    public Entity Player(int id, string name, double x, double z, int held = 0) =>
        W.Entities.SpawnPlayer(id, name, new Vec3d(x, 64, z), 0, 0, held);

    public double DistTo(Entity e) => Math.Sqrt(Math.Pow(P.Position.X - e.Position.X, 2) + Math.Pow(P.Position.Z - e.Position.Z, 2));

    public IEnumerable<JsonElement> Json(string ev) =>
        Lines.Select(l => JsonDocument.Parse(l).RootElement).Where(j => j.GetProperty("ev").GetString() == ev);
}

public class CombatRulesTests
{
    [Fact]
    public void WeaponDamage_MatchesB181ItemSwordAndItemTool()
    {
        Assert.Equal(1, CombatRules.WeaponDamage(-1)); // fist
        Assert.Equal(1, CombatRules.WeaponDamage(Blocks.Cobblestone));
        Assert.Equal(4, CombatRules.WeaponDamage(Items.WoodenSword));
        Assert.Equal(4, CombatRules.WeaponDamage(Items.GoldenSword));
        Assert.Equal(6, CombatRules.WeaponDamage(Items.StoneSword));
        Assert.Equal(8, CombatRules.WeaponDamage(Items.IronSword));
        Assert.Equal(10, CombatRules.WeaponDamage(Items.DiamondSword));
        Assert.Equal(6, CombatRules.WeaponDamage(Items.DiamondAxe));
        Assert.Equal(5, CombatRules.WeaponDamage(Items.DiamondPickaxe));
        Assert.Equal(2, CombatRules.WeaponDamage(Items.StoneShovel));
        Assert.Equal(1, CombatRules.WeaponDamage(Items.DiamondHoe));
        Assert.Equal(16, CombatRules.MaxCritDamage(10)); // + rand(10/2 + 2) → at most +6
        Assert.Equal(4, CombatRules.BlockedDamage(7));   // (1 + 7) >> 1
    }

    [Fact]
    public void Armor_UsesB18PointsAndDurability()
    {
        var full = new[] { Items.DiamondHelmet, Items.DiamondChestplate, Items.DiamondLeggings, Items.DiamondBoots }
            .Select(i => new ItemStack((short)i, 1, 0)).ToList();
        Assert.Equal(20, CombatRules.ArmorValue(full));
        Assert.Equal(2, CombatRules.DamageAfterArmor(10, 20));
        var leather = new[] { new ItemStack((short)Items.LeatherChestplate, 1, 0) };
        Assert.Equal(8, CombatRules.ArmorValue(leather));
        // Half-worn: ((8 - 1) * 24) / 48 + 1 = 4
        var worn = new[] { new ItemStack((short)Items.LeatherChestplate, 1, 24) };
        Assert.Equal(4, CombatRules.ArmorValue(worn));
        Assert.Equal(0, CombatRules.ArmorValue([]));
    }

    [Fact]
    public void WeaponChoice_PicksBestUsable_NeverReservedOrNearlyBroken()
    {
        var slots = Enumerable.Repeat(ItemStack.Empty, 45).ToArray();
        slots[36] = new ItemStack((short)Items.WoodenSword, 1, 0);
        slots[37] = new ItemStack((short)Items.DiamondAxe, 1, 0);
        slots[12] = new ItemStack((short)Items.IronSword, 1, 0);
        slots[13] = new ItemStack((short)Items.DiamondSword, 1, (short)(Items.Get(Items.DiamondSword).MaxDamage - 1)); // about to break
        slots[38] = new ItemStack((short)Blocks.Dirt, 10, 0);
        var none = new HashSet<int>();
        var best = WeaponChoice.Best(slots, none, none);
        Assert.Equal(Items.IronSword, best.ItemId);
        Assert.Equal(12, best.Slot);
        Assert.False(best.InHotbar);

        var reserved = new HashSet<int> { Items.IronSword };
        Assert.Equal(Items.DiamondAxe, WeaponChoice.Best(slots, reserved, none).ItemId);
        Assert.Equal(Items.WoodenSword, WeaponChoice.Weakest(slots, none, none).ItemId);

        var hand = WeaponChoice.Hand(slots, none, none);
        Assert.True(hand.IsHand);
        Assert.Equal(39, hand.Slot); // first empty hotbar slot
        for (int i = 39; i < 45; i++) slots[i] = new ItemStack((short)Items.StonePickaxe, 1, 0);
        Assert.Equal(38, WeaponChoice.Hand(slots, none, none).Slot); // the dirt: 1 damage, not a tool
    }

    [Fact]
    public void EndermanGaze_IsAvoided()
    {
        var eye = new Vec3d(0.5, 65.62, 0.5);
        var center = new Vec3d(0.5, 65.45, 6.5);
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(eye, center);
        Assert.True(CombatRules.LooksAt(eye, yaw, pitch, center, 15));
        float safe = CombatRules.AvoidEndermanGaze(eye, yaw, pitch, [center]);
        Assert.False(CombatRules.LooksAt(eye, yaw, safe, center, 15));
    }

    [Fact]
    public void MobMetadata_CreeperFuse_TamedWolf_SlimeSize()
    {
        var creeper = new Entity(1, EntityKind.Mob, Vec3d.Zero, TypeId: MobTypes.Creeper, Metadata: new Dictionary<int, object> { [16] = (sbyte)1 });
        Assert.True(CombatRules.IsCreeperFusing(creeper));
        Assert.False(CombatRules.IsCreeperFusing(creeper with { Metadata = new Dictionary<int, object> { [16] = (sbyte)-1 } }));
        var wolf = new Entity(2, EntityKind.Mob, Vec3d.Zero, TypeId: MobTypes.Wolf, Metadata: new Dictionary<int, object> { [16] = (sbyte)4 });
        Assert.True(CombatRules.IsTamedWolf(wolf));
        Assert.False(CombatRules.IsThreat(wolf));
        Assert.True(CombatRules.IsThreat(wolf with { Metadata = new Dictionary<int, object> { [16] = (sbyte)2 } }));
        var slime = new Entity(3, EntityKind.Mob, Vec3d.Zero, TypeId: MobTypes.Slime, Metadata: new Dictionary<int, object> { [16] = (sbyte)1 });
        Assert.False(CombatRules.IsThreat(slime)); // size 1 deals no damage in b1.8
        Assert.True(CombatRules.IsThreat(slime with { Metadata = new Dictionary<int, object> { [16] = (sbyte)4 } }));
        Assert.Equal(2.4, CombatRules.Hitbox(slime with { Metadata = new Dictionary<int, object> { [16] = (sbyte)4 } }).MaxX * 2, 3);
    }
}

public class CombatEngineTests
{
    private static CombatOptions Opts(Func<CombatOptions, CombatOptions>? f = null)
    {
        var o = new CombatOptions { TelemetryDirectory = null, Crits = false, Block = false };
        return f is null ? o : f(o);
    }

    [Fact]
    public void HitTiming_OneHitPerInvulnerabilityWindow()
    {
        var r = new CombatRig(Opts());
        var z = r.Mob(9, MobTypes.Zombie, 2.5, 0.5);
        var attacks = new List<long>();
        for (int i = 0; i < 80; i++)
        {
            long t = r.Tick;
            var s = r.Step();
            if (s.AttackId == z.Id)
            {
                attacks.Add(t);
                r.E.OnEntityStatus(z.Id, CombatRules.StatusHurt); // the server confirms next tick
            }
        }
        Assert.True(attacks.Count >= 6, $"only {attacks.Count} attacks");
        Assert.Equal(0, attacks[0]);
        Assert.All(attacks.Zip(attacks.Skip(1)), p => Assert.True(p.Second - p.First >= 11, $"{p.First}->{p.Second}"));
        Assert.All(attacks.Zip(attacks.Skip(1)), p => Assert.True(p.Second - p.First <= 12, $"wasted ticks {p.First}->{p.Second}"));
        Assert.Contains(r.Events, e => e.Kind == CombatEventKind.FightStarted && e.Message.Contains("zombie"));
    }

    [Fact]
    public void HitTiming_WaitsOutInvulnerabilityFromSomeoneElsesHit()
    {
        var r = new CombatRig(Opts());
        var z = r.Mob(9, MobTypes.Zombie, 2.5, 0.5);
        var attacks = new List<long>();
        for (int i = 0; i < 30; i++)
        {
            long t = r.Tick;
            if (t == 1) r.E.OnEntityStatus(z.Id, CombatRules.StatusHurt);  // our hit at tick 0
            if (t == 6) r.E.OnEntityStatus(z.Id, CombatRules.StatusHurt);  // Fox hits it at tick 6
            if (r.Step().AttackId == z.Id) attacks.Add(t);
        }
        Assert.Equal(0, attacks[0]);
        Assert.True(attacks[1] >= 16, $"second swing at {attacks[1]} lands inside the window opened at tick 6");
    }

    [Fact]
    public void Crits_JumpAndSwingWhileFalling()
    {
        var r = new CombatRig(Opts(o => o with { Crits = true }));
        var z = r.Mob(9, MobTypes.Zombie, 2.3, 0.5);
        for (int i = 0; i < 80; i++)
            if (r.Step(physics: true).AttackId == z.Id) r.E.OnEntityStatus(z.Id, CombatRules.StatusHurt);
        var swings = r.Json("swing").ToList();
        Assert.True(swings.Count >= 3);
        Assert.Contains(swings, s => s.GetProperty("crit").GetBoolean());
    }

    [Fact]
    public void CreeperFusing_BacksOffAndNeverSwings()
    {
        var r = new CombatRig(Opts());
        var c = r.Mob(9, MobTypes.Creeper, 2.5, 0.5, new Dictionary<int, object> { [16] = (sbyte)1 });
        bool swung = false;
        for (int i = 0; i < 45; i++) swung |= r.Step(physics: true).AttackId >= 0;
        Assert.False(swung);
        Assert.True(r.DistTo(c) > 7, $"still {r.DistTo(c):0.0} from a fusing creeper");
        Assert.Contains(r.Events, e => e.Kind == CombatEventKind.Critical && e.Message.Contains("creeper fusing"));
        Assert.False(r.W.IsPlayerOverVoid(r.P.Position));
    }

    [Fact]
    public void Creeper_HitAndRun_BacksOffAfterTheHit()
    {
        var r = new CombatRig(Opts());
        var c = r.Mob(9, MobTypes.Creeper, 2.5, 0.5, new Dictionary<int, object> { [16] = (sbyte)-1 });
        long hitAt = -1;
        double maxAfter = 0;
        for (int i = 0; i < 40; i++)
        {
            long t = r.Tick;
            var s = r.Step(physics: true);
            if (s.AttackId == c.Id && hitAt < 0) { hitAt = t; r.E.OnEntityStatus(c.Id, CombatRules.StatusHurt); }
            if (hitAt >= 0) maxAfter = Math.Max(maxAfter, r.DistTo(c));
        }
        Assert.True(hitAt >= 0, "never hit the creeper");
        Assert.True(maxAfter >= 4.5, $"didn't back off: {maxAfter:0.0}");
    }

    [Fact]
    public void Positioning_NeverFightsWithTheVoidBehind()
    {
        // Bot at the east rim (x=9.5 of a platform ending at x=10), zombie to the west: knockback would push it off.
        var r = new CombatRig(Opts(), half: 10, x: 9.5);
        var z = r.Mob(9, MobTypes.Zombie, 5.5, 0.5);
        Assert.True(CombatGeometry.EdgeBehind(r.W, r.P.Position, z.Position, 3.5));
        bool overVoid = false;
        for (int i = 0; i < 80; i++)
        {
            r.Step(physics: true);
            overVoid |= r.W.IsPlayerOverVoid(r.P.Position);
        }
        Assert.False(overVoid);
        Assert.False(CombatGeometry.EdgeBehind(r.W, r.P.Position, z.Position, 3.5), $"still edge-behind at {r.P.Position}");
        Assert.NotEmpty(r.Json("reposition"));
    }

    [Fact]
    public void ChooseStance_ReturnsACellWithInteriorBehind()
    {
        var r = new CombatRig(Opts(), half: 6);
        var me = new Vec3d(5.5, 64, 0.5);
        var threat = new Vec3d(2.5, 64, 0.5);
        var cell = CombatGeometry.ChooseStance(r.W, me, threat, 3.5);
        Assert.NotNull(cell);
        Assert.False(CombatGeometry.EdgeBehind(r.W, cell!.Value.BottomCenter, threat, 3.5));
    }

    [Fact]
    public void Knockback_TowardTheEdgeMidFight_GuardKeepsTheBotOnTheIsland()
    {
        var r = new CombatRig(Opts(), half: 10, x: 7.5);
        var z = r.Mob(9, MobTypes.Zombie, 5.5, 0.5);
        bool over = false;
        for (int i = 0; i < 100; i++)
        {
            if (i == 20 || i == 50)
            {
                var kb = new Vec3d(0.4, 0.4, 0);
                r.P.Velocity = kb; // vanilla: 0x1C replaces motion
                r.P.OnGround = false;
                r.E.OnKnockback(kb, r.Guard.IsNearEdge(r.W, r.P));
            }
            if (r.Step(physics: true).AttackId == z.Id) r.E.OnEntityStatus(z.Id, CombatRules.StatusHurt);
            over |= r.W.IsPlayerOverVoid(r.P.Position);
        }
        Assert.False(over);
        Assert.True(r.P.OnGround);
        Assert.Equal(2, r.Json("knockback").Count());
    }

    [Fact]
    public void Slime_Split_RetargetsTheSmallerSlimes()
    {
        var r = new CombatRig(Opts());
        r.Mob(9, MobTypes.Slime, 2.5, 0.5, new Dictionary<int, object> { [16] = (sbyte)4 });
        r.Step();
        Assert.True(r.E.Engaged);
        r.W.Entities.Destroy(9);
        r.E.OnEntityStatus(9, CombatRules.StatusDead);
        r.Mob(10, MobTypes.Slime, 2.0, 1.5, new Dictionary<int, object> { [16] = (sbyte)2 });
        r.Mob(11, MobTypes.Slime, 2.5, -0.5, new Dictionary<int, object> { [16] = (sbyte)2 });
        for (int i = 0; i < 3; i++) r.Step();
        Assert.True(r.E.Engaged);
        Assert.NotEmpty(r.Json("retarget"));
        Assert.DoesNotContain(r.Events, e => e.Kind == CombatEventKind.FightEnded);
    }

    [Fact]
    public void Reflex_NeverAttacksPlayers_TamedWolves_OrPassiveMobs()
    {
        var r = new CombatRig(Opts());
        var p = r.Player(77, "kiwi", 1.5, 0.5);
        var wolf = r.Mob(8, MobTypes.Wolf, 0.5, 1.8, new Dictionary<int, object> { [16] = (sbyte)4 });
        r.Mob(7, MobTypes.Pig, -0.8, 0.5);
        for (int i = 0; i < 60; i++) Assert.True(r.Step().AttackId < 0);
        Assert.False(r.E.Engaged);
        Assert.False(r.E.MayAttack(p));
        Assert.False(r.E.MayAttack(wolf));
        var refused = r.E.StartAttack(wolf.Id);
        r.Step();
        Assert.StartsWith("refused", refused.Result);
    }

    [Fact]
    public void Guard_EngagesAMobNearAGuardedPlayer()
    {
        var r = new CombatRig(Opts());
        r.Player(78, "poofimafox", 8.5, 0.5);
        r.Mob(9, MobTypes.Zombie, 9.5, 1.5);
        r.Step();
        Assert.True(r.E.Engaged);
        Assert.Contains(r.Events, e => e.Message.Contains("guarding poofimafox"));
    }

    [Fact]
    public void Enderman_NotProvoked_IsLeftAlone_AndNotLookedAt()
    {
        var r = new CombatRig(Opts());
        var en = r.Mob(9, MobTypes.Enderman, 0.5, 4.5);
        var center = CombatRules.Hitbox(en).Center;
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(r.P.EyePosition, center);
        var s = r.Step(proposed: new MoveInput(Yaw: yaw, Pitch: pitch));
        Assert.False(r.E.Engaged);
        Assert.False(CombatRules.LooksAt(r.P.EyePosition, s.Input.Yaw, s.Input.Pitch, center, 15));
    }

    [Fact]
    public void LowHealth_DisengagesInward()
    {
        var r = new CombatRig(Opts());
        r.Mob(9, MobTypes.Zombie, 2.5, 0.5);
        r.Step();
        r.W.Player.SetHealth(5, 20, 0);
        for (int i = 0; i < 30; i++) r.Step(physics: true);
        Assert.Contains(r.Events, e => e.Kind == CombatEventKind.Critical && e.Message.Contains("disengaging"));
    }
}

public class SparTests
{
    private static CombatOptions Opts(Func<CombatOptions, CombatOptions>? f = null)
    {
        var o = new CombatOptions { TelemetryDirectory = null };
        return f is null ? o : f(o);
    }

    private static (CombatRig R, Entity Opp, Task<string> Done) StartSpar(CombatOptions? o = null, double oppX = 1.8, int held = 0, int? rounds = null)
    {
        var r = new CombatRig(o ?? Opts());
        var opp = r.Player(77, "kiwi", oppX, 0.5, held);
        var done = r.E.StartSpar("kiwi", 77, new BlockPos(0, 64, 0), rounds);
        r.Step();
        Assert.True(r.E.Sparring);
        return (r, opp, done);
    }

    /// <summary>Ticks until the spar ends, confirming each of our swings like the server would (0x26 status 2).</summary>
    private static string RunToEnd(CombatRig r, Task<string> done, int maxTicks = 400)
    {
        for (int i = 0; i < maxTicks && !done.IsCompleted; i++)
            if (r.Step().AttackId == 77) r.E.OnEntityStatus(77, CombatRules.StatusHurt);
        Assert.True(done.IsCompleted, "spar never ended");
        return done.Result;
    }

    [Fact]
    public void Arena_AcceptsABigFlatSafeArea_RefusesEdgesLavaCactusAndSteps()
    {
        var r = new CombatRig(Opts(), half: 10);
        Assert.Null(SparArena.Check(r.W, new BlockPos(0, 64, 0), 2, 3));
        Assert.Contains("void", SparArena.Check(r.W, new BlockPos(7, 64, 0), 2, 3));
        r.W.SetBlock(1, 63, 4, Blocks.Lava, 0);
        Assert.Contains("lava", SparArena.Check(r.W, new BlockPos(0, 64, 0), 2, 3));
        r.W.SetBlock(1, 63, 4, Blocks.Stone, 0);
        r.W.SetBlock(-3, 64, -1, Blocks.Cactus, 0);
        Assert.Contains("cactus", SparArena.Check(r.W, new BlockPos(0, 64, 0), 2, 3));
        r.W.SetBlock(-3, 64, -1, 0, 0);
        r.W.SetBlock(1, 64, 1, Blocks.Stone, 0);
        Assert.Contains("not flat", SparArena.Check(r.W, new BlockPos(0, 64, 0), 2, 3));
    }

    [Fact]
    public void Arena_FindsASaferSpotNearby()
    {
        var r = new CombatRig(Opts(), half: 10);
        var alt = SparArena.FindNearby(r.W, new Vec3d(8.5, 64, 8.5), 2, 3);
        Assert.NotNull(alt);
        Assert.Null(SparArena.Check(r.W, alt!.Value, 2, 3));
        var tiny = new CombatRig(Opts(), half: 3);
        Assert.Null(SparArena.FindNearby(tiny.W, new Vec3d(0.5, 64, 0.5), 2, 3));
    }

    [Theory]
    [InlineData("gg", true)]
    [InlineData("GG!", true)]
    [InlineData("ok I yield", true)]
    [InlineData("STOP", true)]
    [InlineData("uncle!!", true)]
    [InlineData("stopping by later", false)]
    [InlineData("eggs", false)]
    [InlineData("nice hit", false)]
    public void StopWords(string msg, bool stop) => Assert.Equal(stop, SparWords.IsStop(msg, new CombatOptions().StopWords));

    [Theory]
    [InlineData("wanna spar?", true)]
    [InlineData("fight me bot", true)]
    [InlineData("1v1 me", true)]
    [InlineData("sparkly", false)]
    [InlineData("go mine cobble", false)]
    public void SparRequests(string msg, bool req) => Assert.Equal(req, SparWords.IsRequest(msg));

    [Fact]
    public void StopWord_FromEitherParty_EndsImmediately_OwnChatIgnored()
    {
        var (r, _, done) = StartSpar();
        r.E.OnChat("maid_chan", "say stop, yield or gg to end");
        r.Step();
        Assert.True(r.E.Sparring);
        r.E.OnChat("poofimafox", "ok gg");
        r.Step();
        Assert.True(done.IsCompleted);
        Assert.Contains("stopped: poofimafox said", done.Result);
        Assert.Contains("gg", r.Events.Last().Message);
    }

    [Fact]
    public void SelfThreshold_StopsAtEightHp()
    {
        var (r, _, done) = StartSpar();
        r.E.OnAnimation(77, CombatRules.AnimSwing);
        r.W.Player.SetHealth(8, 20, 5);
        r.Step();
        Assert.True(done.IsCompleted);
        Assert.Contains("my hp 8 at threshold", done.Result);
        Assert.Contains("took 12 dmg", done.Result);
    }

    [Fact]
    public void SelfSafety_StopsWhenTheirNextHitCouldBeTooMuch()
    {
        var (r, _, done) = StartSpar(held: Items.DiamondSword);
        r.E.OnAnimation(77, CombatRules.AnimSwing);
        r.W.Player.SetHealth(17, 20, 5);
        r.Step();
        Assert.True(done.IsCompleted);
        Assert.Contains("their next hit could take 16", done.Result);
    }

    [Fact]
    public void DamageFromSomethingElse_Stops()
    {
        var (r, _, done) = StartSpar();
        r.W.Player.SetHealth(17, 20, 5); // no swing from the opponent: fall/cactus/arrow
        r.Step();
        Assert.True(done.IsCompleted);
        Assert.Contains("damage from", done.Result);
    }

    [Fact]
    public void HitCap_StopsAfterMaxConfirmedHits_GentleHandDamage()
    {
        var (r, _, done) = StartSpar(Opts(o => o with { SparMaxHits = 2 }));
        string summary = RunToEnd(r, done);
        Assert.Equal("spar vs kiwi: 2 hits landed, took 0 dmg, stopped: max hits", summary);
        var end = r.Json("end").Single();
        Assert.Equal(16, end.GetProperty("est_hp").GetDouble()); // fist: 1 dmg → worst 2 per hit with the 25% margin
        Assert.Equal(2, r.Json("hit").Count());
    }

    [Fact]
    public void EstimatedHp_Threshold_StopsBeforeTheOpponentIsHurtBadly()
    {
        var (r, _, done) = StartSpar(Opts(o => o with { SparMaxHits = 50 }));
        string summary = RunToEnd(r, done, 1000);
        Assert.Contains("stopped: est hp threshold", summary);
        Assert.Contains("5 hits landed", summary); // 20 - 5×2 = 10 ≤ threshold
    }

    [Fact]
    public void EstimatedHp_Model_ArmorCreditIsPartial_MarginAdded()
    {
        var o = Opts();
        var m = new OpponentHpModel(o);
        var diamond = new[] { Items.DiamondHelmet, Items.DiamondChestplate, Items.DiamondLeggings, Items.DiamondBoots }
            .Select(i => new ItemStack((short)i, 1, 0)).ToList();
        Assert.Equal(10, m.CreditedArmor(diamond));            // armor value 20, only half credited
        Assert.Equal(8, m.WorstHit(Items.DiamondSword, false, diamond)); // ceil(10·15/25)=6 → ×1.25 → 8
        Assert.Equal(13, m.WorstHit(Items.DiamondSword, false, []));     // 10 × 1.25 → 13
        Assert.Equal(20, m.WorstHit(Items.DiamondSword, true, []));      // crit max 16 × 1.25
        m.OnConfirmedHit(Items.DiamondSword, false, diamond);
        Assert.Equal(12, m.Estimate);
        Assert.Null(m.StopReason());
        m.OnOtherDamage();
        Assert.Equal(8, m.Estimate);
        Assert.Equal("est hp threshold", m.StopReason());
        Assert.True(m.NextHitRisky(8));
    }

    [Fact]
    public void OpponentHurtBySomethingElse_StopsAndCountsIt()
    {
        var (r, _, done) = StartSpar(oppX: 5); // out of reach: the bot hasn't swung
        r.E.OnEntityStatus(77, CombatRules.StatusHurt);
        r.Step();
        Assert.True(done.IsCompleted);
        Assert.Contains("opponent hurt by something else", done.Result);
        Assert.Equal(16, r.Json("opponent_other_damage").Single().GetProperty("est_hp").GetDouble());
    }

    [Fact]
    public void LeavingTheArena_MobsAndDanger_StopTheSpar()
    {
        var (r, opp, done) = StartSpar();
        r.W.Entities.Teleport(77, new Vec3d(8.5, 64, 0.5), 0, 0);
        r.Step();
        Assert.Contains("kiwi left the arena", done.Result);

        (r, _, done) = StartSpar();
        r.Mob(9, MobTypes.Zombie, -6.5, 0.5);
        r.Step();
        Assert.Contains("mob appeared: zombie", done.Result);

        (r, _, done) = StartSpar();
        r.E.OnDanger(new DangerEvent(DangerKind.Lava, "in lava!"));
        r.Step();
        Assert.Contains("danger: in lava!", done.Result);
    }

    [Fact]
    public void PreviousSparEstimate_IsReused_NoRegenAssumed()
    {
        var (r, _, done) = StartSpar(Opts(o => o with { SparMaxHits = 2 }));
        RunToEnd(r, done);
        Assert.Equal(16, r.E.SparStartHealth("kiwi"));
        Assert.Equal(20, r.E.SparStartHealth("poofimafox"));
    }

    [Fact]
    public void Rounds_FirstToThree_ThenStop()
    {
        var (r, _, done) = StartSpar(Opts(o => o with { SparMaxHits = 50, SparOpponentStopHealth = 0 }), rounds: 2);
        string summary = RunToEnd(r, done, 1000);
        Assert.Contains("rounds done", summary);
        Assert.Contains("6 hits landed", summary);
        Assert.Single(r.Json("round"));
    }

    [Fact]
    public void Telemetry_EveryEventIsAJsonLineWithTickAndPositions()
    {
        var (r, _, done) = StartSpar(Opts(o => o with { SparMaxHits = 2 }));
        RunToEnd(r, done);
        var evs = r.Lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.All(evs, e => Assert.True(e.TryGetProperty("tick", out _)));
        var kinds = evs.Select(e => e.GetProperty("ev").GetString()).ToHashSet();
        foreach (var k in new[] { "start", "spar_start", "swing", "hit", "end", "summary" }) Assert.Contains(k, kinds);
        var swing = r.Json("swing").First();
        Assert.True(swing.TryGetProperty("dist", out _));
        Assert.True(swing.TryGetProperty("me", out _));
        Assert.Equal("spar vs kiwi: 2 hits landed, took 0 dmg, stopped: max hits", r.Json("summary").Single().GetProperty("text").GetString());
    }

    [Fact]
    public async Task Telemetry_WritesCombatJsonlFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-combat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = new CombatTelemetry(dir);
            t.Log(1, "start", new Vec3d(1, 64, 1), new Vec3d(3, 64, 1));
            t.Log(2, "summary", fields: new Dictionary<string, object?> { ["text"] = "spar vs kiwi: 1 hits landed" });
            await t.DisposeAsync();
            var file = Directory.GetFiles(dir, "combat-*.jsonl").Single();
            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            Assert.Equal(2.0, JsonDocument.Parse(lines[0]).RootElement.GetProperty("dist").GetDouble());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// <summary>Combat against the fake server: real packets in and out.</summary>
public class CombatClientTests
{
    private static Task<(MinecraftClient Client, FakeServer Server)> StartAsync(Action<FakeServer>? setup = null) =>
        Harness.StartAsync(setup, o => o with { Combat = new CombatOptions { TelemetryDirectory = null, Crits = false } });

    private static void Knock(FakeServer s, double vx, double vy, double vz) =>
        s.Send(new EntityVelocityPacket
        {
            EntityId = FakeServer.EntityId, VelocityX = FixedPoint.ToVelocity(vx), VelocityY = FixedPoint.ToVelocity(vy),
            VelocityZ = FixedPoint.ToVelocity(vz),
        });

    [Fact]
    public async Task Zombie_SwingThenUseEntity_AndKnockbackStillGuarded()
    {
        var (c, s) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        var events = new ConcurrentQueue<CombatEvent>();
        c.CombatEventRaised += events.Enqueue;
        s.Send(new MobSpawnPacket { EntityId = 9, Type = (byte)MobTypes.Zombie, X = FixedPoint.ToAbsolute(7.0), Y = 64 * 32, Z = FixedPoint.ToAbsolute(8.5) });
        Assert.True(s.WaitFor(p => p is UseEntityPacket { TargetEntityId: 9, LeftClick: true }, 3000), "never attacked");
        var all = s.Snapshot();
        int use = all.FindIndex(p => p is UseEntityPacket);
        Assert.True(all[use - 1] is AnimationPacket { Animation: 1 }, "swing must precede the attack");
        Assert.True(await Harness.WaitAsync(() => events.Any(e => e.Kind == CombatEventKind.FightStarted), 1000));

        bool over = false;
        Knock(s, 0.3, 0.36, 0);
        var end = DateTime.UtcNow.AddMilliseconds(1200);
        while (DateTime.UtcNow < end)
        {
            over |= c.World.IsPlayerOverVoid(c.World.Player.Position);
            await Task.Delay(2);
        }
        Assert.False(over);
        Assert.True(c.World.Player.OnGround);
        Assert.False((await c.GotoAsync(8, 64, 8)).Ok); // actions wait while a fight owns the body
    }

    [Fact]
    public async Task Players_AreNeverAttackedOutsideASpar()
    {
        var (c, s) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        s.Send(new NamedEntitySpawnPacket { EntityId = 77, PlayerName = "kiwi", X = FixedPoint.ToAbsolute(9.5), Y = 64 * 32, Z = FixedPoint.ToAbsolute(8.5) });
        await Harness.WaitAsync(() => c.World.Entities.FindPlayer("kiwi") is not null);
        var r = await c.AttackAsync("kiwi");
        Assert.False(r.Ok);
        Assert.Contains("spar", r.Message);
        Assert.False((await c.SparAsync("kiwi")).Ok); // never asked
        await Task.Delay(400);
        Assert.Empty(s.Got<UseEntityPacket>());
        Assert.False(c.Combat.MayAttack(c.World.Entities.FindPlayer("kiwi")!));
    }

    [Fact]
    public async Task Spar_Refusals_ListConsentAndArena()
    {
        var (c, s) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        s.Send(new NamedEntitySpawnPacket { EntityId = 77, PlayerName = "kiwi", X = FixedPoint.ToAbsolute(9.5), Y = 64 * 32, Z = FixedPoint.ToAbsolute(8.5) });
        s.Send(new NamedEntitySpawnPacket { EntityId = 78, PlayerName = "stranger", X = FixedPoint.ToAbsolute(7.5), Y = 64 * 32, Z = FixedPoint.ToAbsolute(8.5) });
        var r = await c.SparAsync("stranger");
        Assert.Contains("not on my spar list", r.Message);
        r = await c.SparAsync("kiwi");
        Assert.Contains("hasn't asked", r.Message);
        s.Send(new ChatPacket("<kiwi> wanna spar?"));
        Assert.True(await Harness.WaitAsync(() => c.Combat.HasSparRequest("kiwi")));
        r = await c.SparAsync("kiwi");
        Assert.False(r.Ok);
        Assert.Contains("no safe arena", r.Message); // the 5x5 test platform is too small
        Assert.Empty(s.Got<UseEntityPacket>());
    }

    [Fact]
    public async Task NewEvents_AreRaisedFromTheirPackets()
    {
        var (c, s) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        var got = new ConcurrentQueue<string>();
        c.ItemCollected += (a, b, st) => got.Enqueue($"collect {a} {b} {st?.Id}");
        c.EquipmentChanged += (e, slot, id, d) => got.Enqueue($"equip {e} {slot} {id} {d}");
        c.EntityStatus += (e, st) => got.Enqueue($"status {e} {st}");
        c.EntityUsedBed += (e, x, y, z) => got.Enqueue($"bed {e} {x} {y} {z}");
        c.EntityAttached += (e, v) => got.Enqueue($"attach {e} {v}");
        c.SignUpdated += (x, y, z, l) => got.Enqueue($"sign {x} {y} {z} {l[0]}");
        s.Send(new NamedEntitySpawnPacket { EntityId = 77, PlayerName = "kiwi", X = 9 * 32, Y = 64 * 32, Z = 8 * 32 });
        s.SpawnDrop(9, 64, 8, Harness.Stack(Blocks.Dirt, 1));
        await Task.Delay(50);
        int itemId = c.World.Entities.All().First(e => e.Kind == EntityKind.Item).Id;
        s.Send(new CollectItemPacket { CollectedEntityId = itemId, CollectorEntityId = 77 });
        s.Send(new EntityEquipmentPacket { EntityId = 77, Slot = 3, ItemId = (short)Items.IronChestplate, Damage = 5 });
        s.Send(new EntityStatusPacket { EntityId = 77, Status = 2 });
        s.Send(new UseBedPacket { EntityId = 77, X = 1, Y = 64, Z = 2 });
        s.Send(new AttachEntityPacket { EntityId = 77, VehicleId = 5 });
        s.Send(new UpdateSignPacket { X = 3, Y = 65, Z = 4, Lines = ["hi", "", "", ""] });
        Assert.True(await Harness.WaitAsync(() => got.Count >= 6, 2000), string.Join("; ", got));
        var list = got.ToList();
        Assert.Contains($"collect 77 {itemId} {Blocks.Dirt}", list);
        Assert.Contains($"equip 77 3 {Items.IronChestplate} 5", list);
        Assert.Contains("status 77 2", list);
        Assert.Contains("bed 77 1 64 2", list);
        Assert.Contains("attach 77 5", list);
        Assert.Contains("sign 3 65 4 hi", list);
        Assert.Equal(Items.IronChestplate, c.World.Entities.Get(77)!.EquipmentAt(3).Id);
    }
}
