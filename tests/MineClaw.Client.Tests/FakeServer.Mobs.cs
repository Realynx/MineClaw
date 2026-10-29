using System.Collections.Concurrent;
using MineClaw.Client.Combat;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Client.Tests;

/// <summary>
/// Multi-version P4 of the fake server: mobs with health, metadata, a chase-and-hit AI, hurt and death (status 2/3, the health
/// metadata, a drop and an experience orb), effects, and the bot's melee judged like a vanilla 1.12.2 server
/// (<c>attackTargetEntityWithCurrentItem</c>: the charge from the server's own <c>ticksSinceLastSwing</c>, reset by every attack,
/// arm swing and held-item change; the crit from its fall distance and on-ground state; the 10-tick hurt resistance).
/// One server tick = <see cref="TickMs"/> (the client tests run 5 ms ticks).
/// </summary>
internal sealed partial class FakeServer
{
    internal sealed class FakeMob
    {
        public required int Id;
        public required int Type;
        public double X, Y, Z;
        public float Health;
        public bool Chase;
        public int AttackDamage;
        public DateTime LastHurt = DateTime.MinValue, LastAttack = DateTime.MinValue;
        public float LastDamage;
        public bool Dead;
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

    /// <summary>Runs the mob AI: chasing mobs walk 0.12 b/t toward the bot and hit it (1.5 blocks, once per 20 ticks).</summary>
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
                lock (BlockLock)
                    foreach (var m in Mobs.Values)
                    {
                        if (m.Dead || !m.Chase) continue;
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
                        else if (DateTime.UtcNow - m.LastAttack > TimeSpan.FromMilliseconds(TickMs * 20) && Health > 0)
                        {
                            m.LastAttack = DateTime.UtcNow;
                            Health = Math.Max(1, Health - m.AttackDamage);
                            Send(new UpdateHealthPacket { Health = (short)Health, Food = (short)Food, FoodSaturation = 2, HealthExact = Health });
                            Send(new EntityStatusPacket { EntityId = EntityId, Status = 2 });
                        }
                    }
            }
        }, CancellationToken.None);
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
