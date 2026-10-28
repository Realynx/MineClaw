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

    public event Action<Entity>? Spawned;
    public event Action<Entity>? Removed;

    /// <summary>Entity id of the local player, excluded from queries.</summary>
    public int SelfId { get; set; } = int.MinValue;

    // ---- spawns ----

    public Entity Spawn(Entity e)
    {
        lock (_sync) _entities[e.Id] = e;
        Spawned?.Invoke(e);
        return e;
    }

    /// <summary>0x14 Named Entity Spawn.</summary>
    public Entity SpawnPlayer(int id, string name, Vec3d pos, float yaw, float pitch, int heldItem = 0) =>
        Spawn(new Entity(id, EntityKind.Player, pos, Yaw: yaw, Pitch: pitch, Name: name, HeldItem: heldItem));

    /// <summary>0x18 Mob Spawn.</summary>
    public Entity SpawnMob(int id, int type, Vec3d pos, float yaw, float pitch, IReadOnlyDictionary<int, object>? metadata = null) =>
        Spawn(new Entity(id, EntityKind.Mob, pos, Yaw: yaw, Pitch: pitch, TypeId: type, Metadata: metadata));

    /// <summary>0x15 Pickup Spawn (dropped item).</summary>
    public Entity SpawnItem(int id, ItemStack item, Vec3d pos, Vec3d velocity = default) =>
        Spawn(new Entity(id, EntityKind.Item, pos, velocity, Item: item));

    /// <summary>0x17 Add Object/Vehicle.</summary>
    public Entity SpawnObject(int id, int type, Vec3d pos, Vec3d velocity = default) =>
        Spawn(new Entity(id, EntityKind.Object, pos, velocity, TypeId: type));

    /// <summary>0x1A Experience Orb.</summary>
    public Entity SpawnXpOrb(int id, Vec3d pos, int count) =>
        Spawn(new Entity(id, EntityKind.XpOrb, pos, Count: count));

    /// <summary>0x19 Painting.</summary>
    public Entity SpawnPainting(int id, string title, Vec3d pos) =>
        Spawn(new Entity(id, EntityKind.Painting, pos, Name: title));

    // ---- updates ----

    private bool Update(int id, Func<Entity, Entity> f)
    {
        lock (_sync)
        {
            if (!_entities.TryGetValue(id, out var e)) return false;
            _entities[id] = f(e) with { UpdatedAt = DateTime.UtcNow };
            return true;
        }
    }

    /// <summary>0x1F/0x21 relative move (deltas in blocks).</summary>
    public bool MoveRelative(int id, double dx, double dy, double dz) =>
        Update(id, e => e with { Position = e.Position + new Vec3d(dx, dy, dz) });

    /// <summary>0x20/0x21 look.</summary>
    public bool Look(int id, float yaw, float pitch) => Update(id, e => e with { Yaw = yaw, Pitch = pitch });

    /// <summary>0x22 teleport (absolute position).</summary>
    public bool Teleport(int id, Vec3d pos, float yaw, float pitch) =>
        Update(id, e => e with { Position = pos, Yaw = yaw, Pitch = pitch });

    /// <summary>0x1C velocity (blocks/tick).</summary>
    public bool SetVelocity(int id, Vec3d velocity) => Update(id, e => e with { Velocity = velocity });

    /// <summary>0x28 metadata: merges values by index.</summary>
    public bool SetMetadata(int id, IReadOnlyDictionary<int, object> values) => Update(id, e =>
    {
        var merged = e.Metadata is null ? new Dictionary<int, object>() : new Dictionary<int, object>(e.Metadata);
        foreach (var (k, v) in values) merged[k] = v;
        return e with { Metadata = merged };
    });

    /// <summary>A dropped item's stack changed (1.12.2 item metadata: merged stacks, a partial pickup).</summary>
    public bool SetItem(int id, ItemStack item) => Update(id, e => e.Kind == EntityKind.Item ? e with { Item = item } : e);

    /// <summary>0x05 equipment slot 0 (held item) for players.</summary>
    public bool SetHeldItem(int id, int itemId) => Update(id, e => e with { HeldItem = itemId });

    /// <summary>0x05 equipment: stores any slot 0..4 (0 = held, 1-4 = armor boots..helmet); slot 0 also sets <see cref="Entity.HeldItem"/>.</summary>
    public bool SetEquipment(int id, int slot, ItemStack item)
    {
        if (slot < 0 || slot > 4) return false;
        return Update(id, e =>
        {
            var eq = new ItemStack[5];
            for (int i = 0; i < 5; i++) eq[i] = e.EquipmentAt(i);
            eq[slot] = item;
            return e with { Equipment = eq, HeldItem = slot == 0 ? (item.IsEmpty ? 0 : item.Id) : e.HeldItem };
        });
    }

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
        Removed?.Invoke(e);
        return true;
    }

    public void Clear()
    {
        lock (_sync) _entities.Clear();
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
