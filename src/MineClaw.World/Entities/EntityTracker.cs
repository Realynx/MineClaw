using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// Tracks entities from spawn/move/teleport/destroy packets. Thread-safe: all mutation under one lock;
/// readers get immutable <see cref="Entity"/> snapshots. Positions/velocities are plain doubles in blocks and
/// blocks/tick (the Client converts fixed-point and 1/8000 velocity units before calling).
/// </summary>
public sealed class EntityTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Entity> _entities = new();
    private readonly HashSet<string> _onlinePlayers = new(StringComparer.OrdinalIgnoreCase);
    private long _membership;

    /// <summary>Moves with every spawn, removal, clear and stack change (not with moves): a reader's cache of who is here can tell it is
    /// stale without walking the tracker (StoryWorld.cs: the colony reads every body's entities several times a second).</summary>
    public long Membership => Interlocked.Read(ref _membership);

    public event Action<Entity>? Spawned;
    public event Action<Entity>? Removed;
    /// <summary>
    /// Every update (move, look, head look, metadata, equipment...) with the new snapshot, raised on the updating (network) thread
    /// after the lock: the gesture detector samples other players' motion at packet rate (a 4 Hz poll misses a nod's strokes and a
    /// crouch-bob's toggles). Listeners must be quick; nobody subscribed = one null check.
    /// </summary>
    public event Action<Entity>? Updated;

    /// <summary>The version's entity facts, stamped on every mob and object spawn (b1.8.1: none).</summary>
    public Versioning.IEntityRules Rules { get; set; } = Versioning.Beta181EntityRules.Instance;

    /// <summary>Entity id of the local player, excluded from queries.</summary>
    public int SelfId { get; set; } = int.MinValue;

    // ---- spawns ----

    public Entity Spawn(Entity e)
    {
        lock (_sync) _entities[e.Id] = e;
        Interlocked.Increment(ref _membership);
        Spawned?.Invoke(e);
        return e;
    }

    /// <summary>0x14 Named Entity Spawn.</summary>
    public Entity SpawnPlayer(int id, string name, Vec3d pos, float yaw, float pitch, int heldItem = 0) =>
        Spawn(new Entity(id, EntityKind.Player, pos, Yaw: yaw, Pitch: pitch, Name: name, HeldItem: heldItem));

    /// <summary>0x18 Mob Spawn.</summary>
    public Entity SpawnMob(int id, int type, Vec3d pos, float yaw, float pitch, IReadOnlyDictionary<int, object>? metadata = null) =>
        Spawn(new Entity(id, EntityKind.Mob, pos, Yaw: yaw, Pitch: pitch, TypeId: type, Metadata: metadata)
        {
            Profile = Rules.Mob(type), TypeName = Rules.Mob(type)?.Name,
        });

    /// <summary>0x15 Pickup Spawn (dropped item).</summary>
    public Entity SpawnItem(int id, ItemStack item, Vec3d pos, Vec3d velocity = default) =>
        Spawn(new Entity(id, EntityKind.Item, pos, velocity, Item: item));

    /// <summary>0x17 Add Object/Vehicle (<paramref name="data"/>: its data word, see <see cref="Entity.Data"/>).</summary>
    public Entity SpawnObject(int id, int type, Vec3d pos, Vec3d velocity = default, int data = 0) =>
        Spawn(new Entity(id, EntityKind.Object, pos, velocity, TypeId: type) { TypeName = Rules.ObjectName(type), Data = data });

    /// <summary>0x1A Experience Orb.</summary>
    public Entity SpawnXpOrb(int id, Vec3d pos, int count) =>
        Spawn(new Entity(id, EntityKind.XpOrb, pos, Count: count));

    /// <summary>0x19 Painting (<paramref name="direction"/>: b1.8.1's 0 −Z, 1 −X, 2 +Z, 3 +X).</summary>
    public Entity SpawnPainting(int id, string title, Vec3d pos, int direction = 0) =>
        Spawn(new Entity(id, EntityKind.Painting, pos, Name: title) { Data = direction });

    // ---- one-shot events (the viewer's swings, hurts, deaths, crits, lightning) ----

    /// <summary>The last few seconds of <see cref="EntityEvent"/>s (display only).</summary>
    public EntityEventRing Events { get; } = new();

    /// <summary>Records an entity's one-shot event (an animation or status the viewer draws; <paramref name="pos"/> for lightning).</summary>
    public void NoteEvent(int id, string kind, Vec3d? pos = null) => Events.Add(new EntityEvent(id, kind, DateTime.UtcNow, pos));

    // ---- updates ----

    private bool Update(int id, Func<Entity, Entity> f)
    {
        Entity next;
        lock (_sync)
        {
            if (!_entities.TryGetValue(id, out var e)) return false;
            _entities[id] = next = f(e) with { UpdatedAt = DateTime.UtcNow };
        }
        if (Updated is { } u)
        {
            try { u(next); } catch (Exception) { /* a listener never breaks the tracker */ }
        }
        return true;
    }

    /// <summary>0x1F/0x21 relative move (deltas in blocks).</summary>
    public bool MoveRelative(int id, double dx, double dy, double dz) =>
        Update(id, e => Landed(e, e.Position + new Vec3d(dx, dy, dz)));

    /// <summary>An arrow whose server position stopped changing is in the ground (its motion is zero from then on).</summary>
    private static Entity Landed(Entity e, Vec3d to)
    {
        if (!e.IsArrow) return e with { Position = to };
        bool still = to.DistanceSquaredTo(e.Position) < 1e-8;
        return e with { Position = to, InGround = still || (e.InGround && to.DistanceSquaredTo(e.Position) < 1e-4), Velocity = still ? default : e.Velocity };
    }

    /// <summary>0x20/0x21 look.</summary>
    public bool Look(int id, float yaw, float pitch) => Update(id, e => e with { Yaw = yaw, Pitch = pitch });

    /// <summary>0x22 teleport (absolute position). A jump of <see cref="EntityEventRing.TeleportJump"/>+ blocks is noted as a
    /// <c>teleport</c> event (the viewer's puff; display only).</summary>
    public bool Teleport(int id, Vec3d pos, float yaw, float pitch) =>
        Update(id, e =>
        {
            if (e.Kind is EntityKind.Player or EntityKind.Mob && e.Position.DistanceSquaredTo(pos) >= EntityEventRing.TeleportJump * EntityEventRing.TeleportJump)
                NoteEvent(id, "teleport", e.Position);
            return Landed(e, pos) with { Yaw = yaw, Pitch = pitch };
        });

    /// <summary>0x1C velocity (blocks/tick).</summary>
    public bool SetVelocity(int id, Vec3d velocity) => Update(id, e => e with { Velocity = velocity });

    /// <summary>0x28 metadata: merges values by index.</summary>
    public bool SetMetadata(int id, IReadOnlyDictionary<int, object> values) => Update(id, e =>
    {
        var merged = e.Metadata is null ? new Dictionary<int, object>() : new Dictionary<int, object>(e.Metadata);
        foreach (var (k, v) in values) merged[k] = v;
        // 1.12.2 EntityArrow.onHit (a block): setIsCritical(false) — a critical arrow losing the flag has landed.
        bool landed = e.IsArrow && e.MetaFlag(EntityMeta.ArrowCritical) && values.TryGetValue(EntityMeta.ArrowCritical, out var c)
                      && Convert.ToInt32(c) == 0;
        return landed ? e with { Metadata = merged, InGround = true, Velocity = default } : e with { Metadata = merged };
    });

    /// <summary>A dropped item's stack changed (1.12.2 item metadata: merged stacks, a partial pickup).</summary>
    public bool SetItem(int id, ItemStack item)
    {
        Interlocked.Increment(ref _membership);
        return Update(id, e => e.Kind == EntityKind.Item ? e with { Item = item } : e);
    }

    /// <summary>0x05 equipment slot 0 (held item) for players.</summary>
    public bool SetHeldItem(int id, int itemId) => Update(id, e => e with { HeldItem = itemId });

    /// <summary>0x05 equipment: stores any slot 0..5 (0 = held, 1-4 = armor boots..helmet, 5 = the 1.12.2 off hand); slot 0 also
    /// sets <see cref="Entity.HeldItem"/>.</summary>
    public bool SetEquipment(int id, int slot, ItemStack item)
    {
        if (slot < 0 || slot > 5) return false;
        return Update(id, e =>
        {
            var eq = new ItemStack[Math.Max(slot + 1, e.Equipment?.Count ?? 5)];
            for (int i = 0; i < eq.Length; i++) eq[i] = e.EquipmentAt(i);
            eq[slot] = item;
            return e with { Equipment = eq, HeldItem = slot == 0 ? (item.IsEmpty ? 0 : item.Id) : e.HeldItem };
        });
    }

    /// <summary>An iron golem's offered flower (Entity Status 11 holds it for <see cref="EntityEventRing.RoseTicks"/> ticks, 34 drops
    /// it; <c>EntityIronGolem.handleStatusUpdate</c>). Display only.</summary>
    public bool HoldRose(int id, bool hold) =>
        Update(id, e => e with { RoseUntil = hold ? DateTime.UtcNow.AddMilliseconds(EntityEventRing.RoseTicks * 50) : DateTime.MinValue });

    /// <summary>1.12.2 Entity Head Look (angle in degrees).</summary>
    public bool SetHeadYaw(int id, float yaw) => Update(id, e => e with { HeadYaw = yaw });

    /// <summary>Entity Effect: adds or replaces the effect.</summary>
    public bool SetEffect(int id, StatusEffect fx) => Update(id, e =>
    {
        var d = e.Effects is null ? new Dictionary<int, StatusEffect>() : new Dictionary<int, StatusEffect>(e.Effects);
        d[fx.Id] = fx;
        return e with { Effects = d };
    });

    /// <summary>Remove Entity Effect.</summary>
    public bool RemoveEffect(int id, int effectId) => Update(id, e =>
    {
        if (e.Effects is null || !e.Effects.ContainsKey(effectId)) return e;
        var d = new Dictionary<int, StatusEffect>(e.Effects);
        d.Remove(effectId);
        return e with { Effects = d };
    });

    /// <summary>Entity Properties: merges the attribute values by key.</summary>
    public bool SetAttributes(int id, IEnumerable<KeyValuePair<string, double>> values) => Update(id, e =>
    {
        var d = e.Attributes is null ? new Dictionary<string, double>() : new Dictionary<string, double>(e.Attributes);
        foreach (var (k, v) in values) d[k] = v;
        return e with { Attributes = d };
    });

    /// <summary>0x27 attach entity (vehicleId -1 = dismount).</summary>
    public bool Attach(int id, int vehicleId) => Update(id, e => e with { VehicleId = vehicleId });

    /// <summary>0x1D destroy.</summary>
    public bool Destroy(int id)
    {
        Entity? e;
        lock (_sync)
        {
            if (!_entities.Remove(id, out e)) return false;
        }
        Interlocked.Increment(ref _membership);
        Removed?.Invoke(e);
        return true;
    }

    public void Clear()
    {
        lock (_sync) _entities.Clear();
        Interlocked.Increment(ref _membership);
    }

    // ---- queries ----

    public Entity? Get(int id)
    {
        lock (_sync) return _entities.GetValueOrDefault(id);
    }

    public int Count
    {
        get { lock (_sync) return _entities.Count; }
    }

    /// <summary>Snapshot of all entities (excluding self).</summary>
    public List<Entity> All()
    {
        lock (_sync) return _entities.Values.Where(e => e.Id != SelfId).ToList();
    }

    /// <summary>Entities within <paramref name="radius"/> of <paramref name="from"/>, nearest first, optionally filtered.</summary>
    public List<Entity> Nearby(Vec3d from, double radius, Func<Entity, bool>? filter = null, int max = int.MaxValue)
    {
        double r2 = radius * radius;
        List<Entity> list;
        lock (_sync)
            list = _entities.Values.Where(e => e.Id != SelfId && e.Position.DistanceSquaredTo(from) <= r2
                                                && (filter is null || filter(e))).ToList();
        list.Sort((a, b) => a.Position.DistanceSquaredTo(from).CompareTo(b.Position.DistanceSquaredTo(from)));
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    public Entity? Nearest(Vec3d from, double radius, Func<Entity, bool>? filter = null) =>
        Nearby(from, radius, filter, 1).FirstOrDefault();

    public Entity? NearestOfKind(Vec3d from, EntityKind kind, double radius = 64) =>
        Nearest(from, radius, e => e.Kind == kind);

    public Entity? NearestMob(Vec3d from, int mobType, double radius = 64) =>
        Nearest(from, radius, e => e.Kind == EntityKind.Mob && e.TypeId == mobType);

    public Entity? NearestHostile(Vec3d from, double radius = 16) => Nearest(from, radius, e => e.IsHostileMob);

    /// <summary>Nearest dropped item, optionally of a given item id.</summary>
    public Entity? NearestItem(Vec3d from, double radius = 32, int? itemId = null) =>
        Nearest(from, radius, e => e.Kind == EntityKind.Item && (itemId is null || e.Item.Id == itemId));

    public Entity? FindPlayer(string name)
    {
        lock (_sync)
            return _entities.Values.FirstOrDefault(e =>
                e.Kind == EntityKind.Player && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- online player list (0xC9 Player List Item) ----

    /// <summary>0xC9 with online=true adds, online=false removes.</summary>
    public void SetPlayerListEntry(string name, bool online)
    {
        lock (_sync)
        {
            if (online) _onlinePlayers.Add(name);
            else _onlinePlayers.Remove(name);
        }
    }

    public IReadOnlyList<string> OnlinePlayers
    {
        get { lock (_sync) return _onlinePlayers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
    }
}
