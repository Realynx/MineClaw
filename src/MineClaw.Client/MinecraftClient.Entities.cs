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
        if (!DispatchEffects(packet)) DispatchPlayerState(packet);
    }

    /// <summary>Effects, attributes and block entities (true = one of them).</summary>
    private bool DispatchEffects(IPacket packet)
    {
        switch (packet)
        {
            case EntityEffectPacket ef: OnEntityEffect(ef); return true;
            case RemoveEntityEffectPacket re: OnRemoveEntityEffect(re); return true;
            case EntityPropertiesMessage pm: OnEntityProperties(pm); return true;
            case BlockEntityMessage be:
                World.BlockEntities.Set(new BlockPos(be.X, be.Y, be.Z), be.Info); // the viewer's beds, banners, skulls, pots, spawners
                return true;
            default: return false;
        }
    }

    /// <summary>Head look, the server's hotbar choice, experience, our death message, item cooldowns.</summary>
    private void DispatchPlayerState(IPacket packet)
    {
        var w = World;
        switch (packet)
        {
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
                OnItemCooldown(cd);
                break;
        }
    }

    private void OnEntityEffect(EntityEffectPacket ef)
    {
        var fx = new StatusEffect(ef.EffectId, ef.Amplifier, ef.Duration, DateTime.UtcNow);
        if (ef.EntityId != World.Player.EntityId)
        {
            World.Entities.SetEffect(ef.EntityId, fx);
            return;
        }
        World.Player.SetEffect(fx);
        Log($"effect: {PotionEffects.Name(fx.Id)} {fx.Amplifier + 1} for {(fx.DurationTicks >= short.MaxValue ? "ever" : $"{fx.DurationTicks / 20}s")}");
    }

    private void OnRemoveEntityEffect(RemoveEntityEffectPacket re)
    {
        if (re.EntityId == World.Player.EntityId) World.Player.RemoveEffect(re.EffectId);
        else World.Entities.RemoveEffect(re.EntityId, re.EffectId);
    }

    private void OnEntityProperties(EntityPropertiesMessage pm)
    {
        var values = pm.Attributes.Select(a => new KeyValuePair<string, double>(a.Key, a.Value)).ToList();
        if (pm.EntityId == World.Player.EntityId) foreach (var (k, v) in values) _selfAttributes[k] = v;
        else World.Entities.SetAttributes(pm.EntityId, values);
    }

    private void OnItemCooldown(ItemCooldownMessage cd)
    {
        if (cd.Ticks <= 0) _itemCooldownUntil.TryRemove(cd.ItemId, out _);
        else _itemCooldownUntil[cd.ItemId] = TickCount + cd.Ticks;
    }

    /// <summary>Our own metadata (1.12.2 sends the player's flags, health and absorption): only the absorption hearts are kept.</summary>
    private void OnSelfMetadata(EntityMetadata md)
    {
        if (md.Get(EntityMeta.Absorption) is { Value: float a }) World.Player.Absorption = a;
        if (md.Get(EntityMeta.Air)?.AsInt() is { } air) OnSelfAir(air);
        if (md.Get(EntityMeta.Flags)?.AsInt() is { } flags) OnSelfFlags(flags);
    }

    /// <summary>
    /// The op permission level the server granted this account (0 = not op, 1..4), or null = unknown. 1.12.2 tells it on login and on
    /// every /op or /deop: Entity Status 24..28 for the player itself (<c>PlayerList.updatePermissionLevel</c> → level + 24). b1.8.1 has
    /// no such packet: unknown there.
    /// </summary>
    public int? OpLevel { get; private set; }

    /// <summary>Op enough for server commands like /time and /tp (level 2+), or null when the server never said.</summary>
    public bool? IsOp => OpLevel is { } l ? l >= 2 : null;

    /// <summary>The op level an Entity Status value for our own player carries (24..28 → 0..4), else null.</summary>
    public static int? OpLevelOf(int status) => status is >= 24 and <= 28 ? status - 24 : null;

    /// <summary>Statuses for our own entity: 30 the shield was disabled (an axe; <c>EntityPlayer.disableShield</c>), 35 a totem of
    /// undying saved us (<c>EntityLivingBase.checkTotemDeathProtection</c>), 24..28 our op level.</summary>
    private void OnSelfStatus(int status)
    {
        if (OpLevelOf(status) is { } op)
        {
            if (OpLevel != op) Log($"server: my op level is {op}{(op >= 2 ? " (op)" : op == 0 ? " (not op)" : "")}");
            OpLevel = op;
            return;
        }
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
