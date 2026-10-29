using System.Collections.Concurrent;
using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Multi-version P4 on the client: the version-neutral entity facts newer versions send (potion effects for the bot and others,
/// attributes, head look, the server's held-item change, experience, the death screen's message, item cooldowns, our own
/// metadata) and the self statuses of the 1.9 combat (shield disabled 30, totem of undying 35).
/// </summary>
public sealed partial class MinecraftClient
{
    private readonly ConcurrentDictionary<int, long> _itemCooldownUntil = new();

    /// <summary>The death screen's message of the last death (1.12.2 Combat Event); null = none yet.</summary>
    public string? LastDeathMessage { get; private set; }
    /// <summary>The bot's own synced attributes (1.12.2 Entity Properties: movement speed, attack speed, max health…).</summary>
    public IReadOnlyDictionary<string, double> SelfAttributes => _selfAttributes;
    private readonly ConcurrentDictionary<string, double> _selfAttributes = new();
    /// <summary>Experience orbs the bot collected since the client was created.</summary>
    public int XpOrbsCollected => Volatile.Read(ref _xpOrbsCollected);
    private int _xpOrbsCollected;
    /// <summary>Totems of undying that saved the bot.</summary>
    public int TotemsUsed => Volatile.Read(ref _totemsUsed);
    private int _totemsUsed;

    /// <summary>Ticks an item's use cooldown has left (1.12.2 Set Cooldown: a shield an axe disabled, ender pearls); 0 = none.</summary>
    public int ItemCooldownTicks(int itemId) =>
        _itemCooldownUntil.TryGetValue(itemId, out var until) ? (int)Math.Max(0, until - TickCount) : 0;

    private void DispatchEntities(Session s, IPacket packet)
    {
        var w = World;
        switch (packet)
        {
            case EntityEffectPacket ef:
            {
                var fx = new StatusEffect(ef.EffectId, ef.Amplifier, ef.Duration, DateTime.UtcNow);
                if (ef.EntityId == w.Player.EntityId)
                {
                    w.Player.SetEffect(fx);
                    Log($"effect: {PotionEffects.Name(fx.Id)} {fx.Amplifier + 1} for {(fx.DurationTicks >= short.MaxValue ? "ever" : $"{fx.DurationTicks / 20}s")}");
                }
                else w.Entities.SetEffect(ef.EntityId, fx);
                break;
            }
            case RemoveEntityEffectPacket re:
                if (re.EntityId == w.Player.EntityId) w.Player.RemoveEffect(re.EffectId);
                else w.Entities.RemoveEffect(re.EntityId, re.EffectId);
                break;
            case EntityPropertiesMessage pm:
            {
                var values = pm.Attributes.Select(a => new KeyValuePair<string, double>(a.Key, a.Value)).ToList();
                if (pm.EntityId == w.Player.EntityId) foreach (var (k, v) in values) _selfAttributes[k] = v;
                else w.Entities.SetAttributes(pm.EntityId, values);
                break;
            }
            case EntityHeadLookMessage hl:
                w.Entities.SetHeadYaw(hl.EntityId, FixedPoint.FromAngle(hl.Yaw));
                break;
            case HoldingChangePacket hc when hc.Slot is >= 0 and <= 8:
                // handleHeldItemChange: the server chose the hotbar slot (/give into an empty hand, a plugin, a respawn).
                w.Player.HeldSlot = hc.Slot;
                _serverHeldSlot = hc.Slot;
                break;
            case ExperienceMessage xm:
                w.Player.SetExperience(xm.Bar, xm.Level, xm.Total);
                break;
            case DeathMessage dm when dm.PlayerId == w.Player.EntityId:
                LastDeathMessage = dm.Message;
                Log("death: " + dm.Message);
                break;
            case ItemCooldownMessage cd:
                if (cd.Ticks <= 0) _itemCooldownUntil.TryRemove(cd.ItemId, out _);
                else _itemCooldownUntil[cd.ItemId] = TickCount + cd.Ticks;
                break;
        }
    }

    /// <summary>Our own metadata (1.12.2 sends the player's flags, health and absorption): only the absorption hearts are kept.</summary>
    private void OnSelfMetadata(EntityMetadata md)
    {
        if (md.Get(EntityMeta.Absorption) is { Value: float a }) World.Player.Absorption = a;
    }

    /// <summary>Statuses for our own entity: 30 the shield was disabled (an axe; <c>EntityPlayer.disableShield</c>), 35 a totem of
    /// undying saved us (<c>EntityLivingBase.checkTotemDeathProtection</c>).</summary>
    private void OnSelfStatus(int status)
    {
        switch (status)
        {
            case 30:
                if (Options.Profile.Combat.ShieldDisableTicks > 0)
                    _itemCooldownUntil[Release1122CombatRules.ShieldId] = TickCount + Options.Profile.Combat.ShieldDisableTicks;
                _combatBlocking = false; // resetActiveHand: the shield is down
                Log("combat: my shield was disabled (an axe)");
                break;
            case 35:
                Interlocked.Increment(ref _totemsUsed);
                RaiseDanger(DangerKind.LowHealth, "a totem of undying saved me from death (it's used up)", abort: false, critical: true);
                break;
        }
    }

    /// <summary>An experience orb we picked up (Collect Item names the orb): counted, the tracker drops it with the destroy.</summary>
    private void NoteOrbCollected(int collector, int entityId)
    {
        if (collector == World.Player.EntityId && World.Entities.Get(entityId) is { Kind: EntityKind.XpOrb })
            Interlocked.Increment(ref _xpOrbsCollected);
    }
}
