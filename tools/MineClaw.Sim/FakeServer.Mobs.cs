using System.Collections.Concurrent;
using MineClaw.Client.Combat;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Sim;

/// <summary>
/// Multi-version P4 of the fake server: mobs with health, metadata, a chase-and-hit AI, hurt and death (status 2/3, the health
/// metadata, a drop and an experience orb), effects, and the bot's melee judged like a vanilla 1.12.2 server
/// (<c>attackTargetEntityWithCurrentItem</c>: the charge from the server's own <c>ticksSinceLastSwing</c>, reset by every attack,
/// arm swing and held-item change; the crit from its fall distance and on-ground state; the 10-tick hurt resistance).
/// One server tick = <see cref="TickMs"/> (the client tests run 5 ms ticks).
/// </summary>
public partial class FakeServer
{
    public sealed class FakeMob
    {
        public required int Id;
        public required int Type;
        public double X, Y, Z;
        public float Health;
        public bool Chase;
        /// <summary>A skeleton: stands (backs off when the player is within 4), shoots every 40 ticks within 15 blocks in sight.</summary>
        public bool Archer;
        public int AttackDamage;
        public DateTime LastHurt = DateTime.MinValue, LastAttack = DateTime.MinValue;
        public float LastDamage;
        public bool Dead;
        /// <summary>Knockback motion (blocks per AI tick) and the height above its walking level while it flies.</summary>
        public double VX, VY, VZ, Lift;
    }

    /// <summary>One melee hit as the server judged it.</summary>
    public readonly record struct MeleeHit(int Target, float Strength, bool Crit, float Damage, bool Ignored);

    public ConcurrentDictionary<int, FakeMob> Mobs { get; } = new();
    public ConcurrentQueue<MeleeHit> MeleeHits { get; } = new();
    public ConcurrentDictionary<int, (double X, double Y, double Z, int Xp)> Orbs { get; } = new();
    /// <summary>Real milliseconds per server tick.</summary>
    public int TickMs { get; set; } = 5;
    /// <summary>What a mob drops when it dies (rotten flesh by default) and its experience.</summary>
    public ItemStack MobDrop { get; set; } = new((short)Items.RottenFlesh, 1, 0);
    public int MobXp { get; set; } = 5;
    public int XpTotal { get; private set; }

    private ICombatRules ServerRules => Modern ? Release1122CombatRules.Instance : Beta181CombatRules.Instance;
    private DateTime _lastSwingAt = DateTime.UtcNow;
    private double _px = double.NaN, _py, _pz, _fall;
    private bool _onGround = true;
    private CancellationTokenSource? _ai;

    /// <summary>Spawns a mob (Spawn Mob with its health; 1.12.2 metadata index 7) at feet (x, y, z).</summary>
    public FakeMob SpawnMob(int type, double x, double y, double z, float health = 20, bool chase = false, int attackDamage = 3,
        EntityMetadata? meta = null)
    {
        int id = Interlocked.Increment(ref _nextEntity);
        var m = new FakeMob { Id = id, Type = type, X = x, Y = y, Z = z, Health = health, Chase = chase, AttackDamage = attackDamage };
        Mobs[id] = m;
        var md = meta ?? new EntityMetadata();
        if (md.Get(EntityMeta.Health) is null && Modern) md = new EntityMetadata([.. md, MetadataEntry.FromFloat(EntityMeta.Health, health)]);
        Send(new MobSpawnPacket
        {
            EntityId = id, Type = (byte)type, X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(y), Z = FixedPoint.ToAbsolute(z),
            Exact = (x, y, z), Metadata = md,
        });
        return m;
    }

    /// <summary>Entity Effect for any entity (the bot: <see cref="EntityId"/>).</summary>
    public void ApplyEffect(int entityId, int effect, int amplifier, int ticks) =>
        Send(new EntityEffectPacket { EntityId = entityId, EffectId = (sbyte)effect, Amplifier = (sbyte)amplifier, Duration = (short)ticks });

    /// <summary>
    /// Runs the mob AI: chasing mobs walk 0.12 b/t toward the bot (stopping at 1.2) and hit it once per 20 ticks within the vanilla
    /// melee reach (<c>EntityAIAttackMelee.getAttackReachSqr</c> against <c>getDistanceSq</c> to the player's feet). A mob that our
    /// hit knocked back flies and slides first (<see cref="KnockbackTick"/>).
    /// </summary>
    public void StartMobAi()
    {
        _ai = new CancellationTokenSource();
        var ct = _ai.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TickMs, CancellationToken.None).ConfigureAwait(false);
                if (double.IsNaN(_px)) continue;
                MobAiTick();
            }
        }, CancellationToken.None);
    }

    private void MobAiTick()
    {
        lock (BlockLock)
            foreach (var m in Mobs.Values)
            {
                if (!m.Dead && m.Archer) { ArcherTick(m); continue; }
                if (m.Dead || !m.Chase) continue;
                if (KnockbackTick(m)) continue; // in the air: no walking, no attack
                double dx = _px - m.X, dz = _pz - m.Z, d = Math.Sqrt(dx * dx + dz * dz);
                if (d > 1.2)
                {
                    double step = Math.Min(0.12, d - 1.2);
                    m.X += dx / d * step;
                    m.Z += dz / d * step;
                    byte yaw = (byte)((int)(Math.Atan2(-dx, dz) * 180 / Math.PI / 360 * 256) & 0xFF);
                    Send(new EntityTeleportPacket
                    {
                        EntityId = m.Id, X = FixedPoint.ToAbsolute(m.X), Y = FixedPoint.ToAbsolute(m.Y), Z = FixedPoint.ToAbsolute(m.Z),
                        Exact = (m.X, m.Y, m.Z), Yaw = yaw,
                    });
                }
                // Its own check, not the walk's else: the walk ends at 1.2 only up to rounding, and that rounding used to decide
                // whether the mob ever attacked at all.
                double dy = _py - m.Y;
                if (dx * dx + dz * dz + dy * dy <= MeleeReachSqr
                    && DateTime.UtcNow - m.LastAttack > TimeSpan.FromMilliseconds(TickMs * 20) && Health > 0 && !PlayerBoxedIn())
                {
                    m.LastAttack = DateTime.UtcNow;
                    Health = Math.Max(1, Health - m.AttackDamage);
                    Send(new UpdateHealthPacket { Health = (short)Health, Food = (short)Food, FoodSaturation = 2, HealthExact = Health });
                    Send(new EntityStatusPacket { EntityId = EntityId, Status = 2 });
                }
            }
    }

    /// <summary>A zombie's melee reach on a player, squared: (width 0.6 · 2)² + the player's width 0.6.</summary>
    private const double MeleeReachSqr = 0.6 * 2 * (0.6 * 2) + 0.6;

    /// <summary>
    /// <c>EntityLivingBase.knockBack(attacker, 0.4F, …)</c> from a hit that counted (<c>attackEntityFrom</c>): the motion halved, 0.4
    /// away from the attacker and, on the ground, half the vertical motion + 0.4 (capped at 0.4). It moves under the mob AI.
    /// </summary>
    private void Knock(FakeMob m)
    {
        if (double.IsNaN(_px)) return;
        double xr = _px - m.X, zr = _pz - m.Z, f = Math.Sqrt(xr * xr + zr * zr);
        if (f < 1e-4) { xr = 0.01; zr = 0; f = 0.01; }
        m.VX = m.VX / 2 - xr / f * Release1122CombatRules.HitKnockback;
        m.VZ = m.VZ / 2 - zr / f * Release1122CombatRules.HitKnockback;
        if (m.Lift <= 0) m.VY = Math.Min(m.VY / 2 + Release1122CombatRules.HitKnockback, 0.4000000059604645);
    }

    /// <summary>
    /// One tick of knockback motion (<c>EntityLivingBase.travel</c>): move, then gravity (−0.08, ×0.98) and friction (0.91 in the
    /// air, 0.6 · 0.91 on the ground). A wall stops the motion along its axis (<c>Entity.move</c>, simplified to the mob's centre
    /// column at feet and head). True while the mob is in the air. Caller holds <see cref="BlockLock"/>.
    /// </summary>
    private bool KnockbackTick(FakeMob m)
    {
        if (m.Lift <= 0 && m.VY <= 0 && Math.Abs(m.VX) + Math.Abs(m.VZ) < 0.003) { m.VX = m.VZ = 0; return false; }
        if (Walled(m.X + m.VX, m.Y + m.Lift, m.Z)) m.VX = 0;
        m.X += m.VX;
        if (Walled(m.X, m.Y + m.Lift, m.Z + m.VZ)) m.VZ = 0;
        m.Z += m.VZ;
        m.Lift += m.VY;
        bool air = m.Lift > 0;
        if (air) m.VY = (m.VY - 0.08) * 0.98;
        else { m.Lift = 0; m.VY = 0; }
        double friction = air ? 0.91 : 0.6 * 0.91;
        m.VX *= friction;
        m.VZ *= friction;
        Send(new EntityTeleportPacket
        {
            EntityId = m.Id, X = FixedPoint.ToAbsolute(m.X), Y = FixedPoint.ToAbsolute(m.Y + m.Lift), Z = FixedPoint.ToAbsolute(m.Z),
            Exact = (m.X, m.Y + m.Lift, m.Z),
        });
        return air;
    }

    /// <summary>A solid block at the feet or head cell of a mob standing at (x, y, z).</summary>
    private bool Walled(double x, double y, double z)
    {
        int bx = (int)Math.Floor(x), by = (int)Math.Floor(y + 1e-6), bz = (int)Math.Floor(z);
        for (int dy = 0; dy <= 1; dy++)
            if (Blocks.GetValueOrDefault((bx, by + dy, bz)) is var id && id != 0 && id is not (8 or 9 or 10 or 11)
                && id != MineClaw.Data.Blocks.TallGrass) return true;
        return false;
    }

    /// <summary>Arrows that hit the player (the skeleton AI).</summary>
    public int ArrowHits { get; private set; }

    /// <summary>
    /// 1.12.2 EntityAIAttackRangedBow, simplified: within 15 blocks and in sight (no solid block on the eye line) it shoots every
    /// 40 ticks (normal) for <see cref="FakeMob.AttackDamage"/>; it backs away 0.05 b/t while the player is within 4.
    /// Caller holds <see cref="BlockLock"/>.
    /// </summary>
    private void ArcherTick(FakeMob m)
    {
        double dx = _px - m.X, dz = _pz - m.Z, d = Math.Sqrt(dx * dx + dz * dz);
        if (d < 4 && d > 1e-3)
        {
            double nx = m.X - dx / d * 0.05, nz = m.Z - dz / d * 0.05;
            if (Blocks.GetValueOrDefault(((int)Math.Floor(nx), (int)Math.Floor(m.Y) - 1, (int)Math.Floor(nz))) != 0)
            {
                m.X = nx; m.Z = nz;
                Send(new EntityTeleportPacket
                {
                    EntityId = m.Id, X = FixedPoint.ToAbsolute(m.X), Y = FixedPoint.ToAbsolute(m.Y), Z = FixedPoint.ToAbsolute(m.Z), Exact = (m.X, m.Y, m.Z),
                });
            }
        }
        if (d > 15 || DateTime.UtcNow - m.LastAttack < TimeSpan.FromMilliseconds(TickMs * 40) || Health <= 0 || !InSight(m)) return;
        m.LastAttack = DateTime.UtcNow;
        ArrowHits++;
        Health = Math.Max(1, Health - m.AttackDamage);
        LowestHealth = Math.Min(LowestHealth, Health);
        Send(new UpdateHealthPacket { Health = (short)Health, Food = (short)Food, FoodSaturation = 2, HealthExact = Health });
        Send(new EntityStatusPacket { EntityId = EntityId, Status = 2 });
    }

    /// <summary>No solid block between the archer's eyes and the player's (sampled every 0.2 blocks).</summary>
    private bool InSight(FakeMob m)
    {
        double ax = m.X, ay = m.Y + 1.6, az = m.Z, bx = _px, by = _py + 1.62, bz = _pz;
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
        int n = Math.Max(1, (int)(len / 0.2));
        for (int i = 1; i < n; i++)
        {
            double t = (double)i / n;
            var cell = ((int)Math.Floor(ax + (bx - ax) * t), (int)Math.Floor(ay + (by - ay) * t), (int)Math.Floor(az + (bz - az) * t));
            if (Blocks.GetValueOrDefault(cell) is var id && id != 0 && id != MineClaw.Data.Blocks.Leaves && id != MineClaw.Data.Blocks.TallGrass) return false;
        }
        return true;
    }

    /// <summary>The player's air supply as the server tracks it (<c>EntityLivingBase.onEntityUpdate</c>).</summary>
    public int Air { get; private set; } = 300;
    /// <summary>Drowning hits dealt.</summary>
    public int DrownHits { get; private set; }
    /// <summary>Lowest health the breath simulation saw.</summary>
    public int LowestHealth { get; private set; } = 20;
    private CancellationTokenSource? _breath;

    /// <summary>
    /// Runs the server's breath rule every tick: the eye (feet + 1.62) in a water cell (still or flowing; surface per
    /// <c>getLiquidHeightPercent</c>) costs one air, −20 is a 2-damage hit and air 0; out of water the air is 300 at once. Our own
    /// metadata index 1 goes out when it changes (b1.8.1 short, 1.12.2 VarInt), every 5 ticks while under.
    /// </summary>
    public void StartBreathSim()
    {
        _breath = new CancellationTokenSource();
        var ct = _breath.Token;
        _ = Task.Run(async () =>
        {
            int sent = 300, tick = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TickMs, CancellationToken.None).ConfigureAwait(false);
                if (double.IsNaN(_px) || Health <= 0) continue;
                bool under;
                lock (BlockLock)
                {
                    double ey = _py + 1.62;
                    var cell = ((int)Math.Floor(_px), (int)Math.Floor(ey), (int)Math.Floor(_pz));
                    int id = Blocks.GetValueOrDefault(cell);
                    int meta = Metas.GetValueOrDefault(cell);
                    float pct = (meta >= 8 ? 1 : meta + 1) / 9f - 0.11111111f;
                    under = id is 8 or 9 && ey < cell.Item2 + 1 - pct;
                }
                tick++;
                if (!under) Air = 300;
                else if (--Air <= -20)
                {
                    Air = 0;
                    DrownHits++;
                    Health = Math.Max(0, Health - 2);
                    Send(new UpdateHealthPacket { Health = (short)Health, Food = (short)Food, FoodSaturation = 2, HealthExact = Health });
                }
                LowestHealth = Math.Min(LowestHealth, Health);
                if (Air != sent && (!under || tick % 5 == 0))
                {
                    sent = Air;
                    var entry = Modern ? MetadataEntry.FromInt(EntityMeta.Air, Air) : MetadataEntry.FromShort(EntityMeta.Air, (short)Air);
                    Send(new EntityMetadataPacket { EntityId = EntityId, Metadata = new EntityMetadata([entry]) });
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>The player's 1×2 column is walled on all four sides at feet and head (a melee mob can't reach it).</summary>
    private bool PlayerBoxedIn()
    {
        int x = (int)Math.Floor(_px), y = (int)Math.Floor(_py + 1e-6), z = (int)Math.Floor(_pz);
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            for (int dy = 0; dy <= 1; dy++)
                if (Blocks.GetValueOrDefault((x + dx, y + dy, z + dz)) == 0) return false;
        return true;
    }

    private int Ticks(DateTime since) => (int)((DateTime.UtcNow - since).TotalMilliseconds / TickMs);

    private void HandleMobs(IPacket p)
    {
        switch (p)
        {
            case PlayerPositionPacket pp: Moved(pp.X, pp.Y, pp.Z, pp.OnGround); break;
            case PlayerPositionLookPacket pl: Moved(pl.X, pl.Y, pl.Z, pl.OnGround); break;
            case PlayerOnGroundPacket g: _onGround = g.OnGround; if (g.OnGround) _fall = 0; break;
            case PlayerLookPacket lk: _onGround = lk.OnGround; break;
            case AnimationPacket { Animation: 1 }:
                _lastSwingAt = DateTime.UtcNow; // EntityPlayerMP.swingArm → resetCooldown
                break;
            case HoldingChangePacket:
                _lastSwingAt = DateTime.UtcNow; // the held item changed (EntityPlayer.onUpdate)
                break;
            case UseEntityPacket { LeftClick: true } u when Mobs.TryGetValue(u.TargetEntityId, out var m) && !m.Dead:
                Attack(m);
                break;
        }
    }

    private void Moved(double x, double y, double z, bool onGround)
    {
        if (!double.IsNaN(_px) && !onGround && y < _py) _fall += _py - y; // EntityPlayerMP.handleFalling
        if (onGround) _fall = 0;
        (_px, _py, _pz, _onGround) = (x, y, z, onGround);
        foreach (var (id, o) in Orbs)
        {
            if (Math.Abs(o.X - x) > 1.5 || Math.Abs(o.Z - z) > 1.5 || Math.Abs(o.Y - y) > 2 || !Orbs.TryRemove(id, out _)) continue;
            XpTotal += o.Xp;
            Send(new CollectItemPacket { CollectedEntityId = id, CollectorEntityId = EntityId });
            Send(new DestroyEntityPacket { EntityId = id });
            Send(new ExperienceMessage { Bar = XpTotal / 7f % 1f, Level = XpTotal / 7, Total = XpTotal });
        }
    }

    private void Attack(FakeMob m)
    {
        var rules = ServerRules;
        int held = Inventory[36 + _heldSlot].IsEmpty ? 0 : Inventory[36 + _heldSlot].Id;
        float strength = rules.AttackStrength(Ticks(_lastSwingAt), held);
        var ctx = new AttackContext(strength, _fall > 0 ? -0.1 : 0, _fall, _onGround, false, false, false, held);
        bool crit = rules.IsCriticalHit(ctx);
        float dmg = (float)rules.ChargedDamage(held, strength);
        if (crit) dmg = (float)rules.CritDamage(dmg);
        _lastSwingAt = DateTime.UtcNow; // resetCooldown in attackTargetEntityWithCurrentItem
        // EntityLivingBase.attackEntityFrom: within half the 20-tick hurt resistance only a bigger hit counts (the difference).
        bool resisting = Ticks(m.LastHurt) < 10;
        if (resisting && dmg <= m.LastDamage) { MeleeHits.Enqueue(new(m.Id, strength, crit, 0, true)); return; }
        float dealt = resisting ? dmg - m.LastDamage : dmg;
        if (!resisting) m.LastHurt = DateTime.UtcNow;
        m.LastDamage = dmg;
        m.Health -= dealt;
        MeleeHits.Enqueue(new(m.Id, strength, crit, dealt, false));
        if (!resisting && Modern) lock (BlockLock) Knock(m); // only a fresh hurt knocks back (attackEntityFrom's flag)
        Send(new EntityStatusPacket { EntityId = m.Id, Status = 2 });
        if (Modern) Send(new EntityMetadataPacket { EntityId = m.Id, Metadata = new EntityMetadata([MetadataEntry.FromFloat(EntityMeta.Health, Math.Max(0, m.Health))]) });
        if (m.Health > 0) return;
        m.Dead = true;
        Send(new EntityStatusPacket { EntityId = m.Id, Status = 3 });
        var (x, y, z) = (m.X, m.Y, m.Z);
        _ = Task.Delay(TickMs * 20).ContinueWith(_ =>
        {
            lock (BlockLock)
            {
                Mobs.TryRemove(m.Id, out var _);
                Send(new DestroyEntityPacket { EntityId = m.Id });
                if (!MobDrop.IsEmpty) SpawnDrop(x, y + 0.1, z, MobDrop);
                int orb = Interlocked.Increment(ref _nextEntity);
                Orbs[orb] = (x, y + 0.2, z, MobXp);
                Send(new ExperienceOrbPacket
                {
                    EntityId = orb, Count = (short)MobXp, X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(y + 0.2), Z = FixedPoint.ToAbsolute(z),
                    Exact = (x, y + 0.2, z),
                });
            }
        });
    }
}
