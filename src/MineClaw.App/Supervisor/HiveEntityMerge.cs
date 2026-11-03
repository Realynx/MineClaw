using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>One body's tracked entities (its own <see cref="EntityTracker"/>), in the dimension it is in.</summary>
public sealed record BodyEntities(string Body, int Dim, IReadOnlyList<Entity> Entities);

/// <summary>
/// Merges every body's entity tracker into one list for the Live Hive: players, mobs and item entities (drops), keyed by
/// (dimension, server entity id) — all bodies share one server, so the same id is the same entity — keeping whichever body
/// heard about it most recently. Hive bots themselves are left out (they come with the bots list). Pure and thread-safe.
/// </summary>
public static class HiveEntityMerge
{
    public const int MaxEntities = 2048;

    public static List<WorldEntity> Merge(IEnumerable<BodyEntities> bodies, ISet<string> botNames, DateTime nowUtc, int max = MaxEntities)
    {
        var best = new Dictionary<(int, int), (Entity E, string By, int Dim)>();
        foreach (var b in bodies)
            foreach (var e in b.Entities)
            {
                if (e.Kind is EntityKind.Weather) continue; // a bolt is an event (lightning), not a thing to draw
                if (e.Kind == EntityKind.Player && (e.Name == null || botNames.Contains(e.Name))) continue;
                if (e.Kind == EntityKind.Item && e.Item.IsEmpty) continue;
                var k = (b.Dim, e.Id);
                if (best.TryGetValue(k, out var have) && have.E.UpdatedAt >= e.UpdatedAt) continue;
                best[k] = (e, b.Body, b.Dim);
            }
        var list = new List<WorldEntity>(Math.Min(best.Count, max));
        // Items first (they are what a bot looks for), then players, then mobs, then objects; stable by id.
        foreach (var (e, by, dim) in best.Values.OrderBy(v => v.E.Kind switch { EntityKind.Item => 0, EntityKind.Player => 1, EntityKind.Mob => 2, _ => 3 })
                     .ThenBy(v => v.E.Id))
        {
            if (list.Count >= max) break;
            list.Add(ToWire(e, dim, by, nowUtc));
        }
        return list;
    }

    private static double R(double v) => Math.Round(v, 2);
    private static double R3(double v) => Math.Round(v, 3);

    public static WorldEntity ToWire(Entity e, int dim, string by, DateTime nowUtc)
    {
        var p = e.Position;
        double age = Math.Round(Math.Max(0, (nowUtc - e.SpawnedAt).TotalSeconds), 1);
        var w = e.Kind switch
        {
            EntityKind.Item => new WorldEntity("item", ItemName(e.Item.Id, e.Item.Damage), dim, R(p.X), R(p.Y), R(p.Z), e.Id, null,
                e.Item.Id, e.Item.Damage, e.Item.Count, R3(e.Velocity.X), R3(e.Velocity.Y), R3(e.Velocity.Z), age, null, by, FlagsOf(e)),
            EntityKind.Mob => new WorldEntity("mob", MobTypes.Name(e.TypeId), dim, R(p.X), R(p.Y), R(p.Z), e.Id, e.TypeId,
                null, null, null, R3(e.Velocity.X), R3(e.Velocity.Y), R3(e.Velocity.Z), age, (float)Math.Round(e.Yaw, 1), by, FlagsOf(e), EntityLooks.Variant(e)),
            EntityKind.Player => new WorldEntity("player", e.Name, dim, R(p.X), R(p.Y), R(p.Z), e.Id, null, e.HeldItem > 0 ? e.HeldItem : null, null, null,
                R3(e.Velocity.X), R3(e.Velocity.Y), R3(e.Velocity.Z), age, (float)Math.Round(e.Yaw, 1), by, FlagsOf(e)),
            _ => ObjectWire(e, dim, by, age),
        };
        // The looks every kind may carry (left out when unknown/default).
        return w with
        {
            Equipment = EntityLooks.Equipment(e),
            HeadYaw = float.IsNaN(e.HeadYaw) ? null : (float)Math.Round(e.HeadYaw, 1),
            Baby = e.IsBaby ? true : null,
            Look = EntityLooks.Look(e),
            Vehicle = e.VehicleId >= 0 ? e.VehicleId : null,
        };
    }

    // Objects, xp orbs and paintings: their wire kind, the type's name, and what draws them.
    private static WorldEntity ObjectWire(Entity e, int dim, string by, double age)
    {
        var p = e.Position;
        var kind = EntityLooks.ObjectKind(e);
        var framed = e.FramedItem; // an item frame's or firework's item (1.12.2 metadata)
        return new WorldEntity(kind, e.Kind == EntityKind.Painting ? e.Name : EntityLooks.NameOf(e), dim, R(p.X), R(p.Y), R(p.Z), e.Id,
            e.Kind == EntityKind.Object ? e.TypeId : null, framed.IsEmpty ? null : framed.Id, framed.IsEmpty ? null : framed.Damage,
            e.Kind == EntityKind.XpOrb ? e.Count : null, R3(e.Velocity.X), R3(e.Velocity.Y), R3(e.Velocity.Z), age,
            e.Kind == EntityKind.XpOrb ? null : (float)Math.Round(e.Yaw, 1), by, FlagsOf(e), EntityLooks.Variant(e))
        {
            Facing = EntityLooks.Facing(e),
            Rotation = kind == "item_frame" ? e.MetaInt(EntityMeta.FrameRotation) is { } rot and not 0 ? rot : null : null,
            Block = EntityLooks.FallingBlock(e),
            Fuse = kind == "tnt" ? e.MetaInt(EntityMeta.Fuse) : null,
            Pose = kind == "armor_stand" ? EntityLooks.Pose(e) : null,
            InGround = e.IsArrow && e.InGround ? true : null,
        };
    }

    /// <summary>The entity flags byte (metadata index 0: 0x01 burning, 0x02 sneaking, 0x04 riding, 0x08 sprinting, 0x10 eating);
    /// null when none are set (the field is then left out of the JSON).</summary>
    public static int? FlagsOf(Entity e) => e.Flags is var f && (f & 0xff) != 0 ? f & 0xff : null;

    /// <summary>"cobblestone", "red wool", "bone meal": the readable name of an item stack.</summary>
    public static string ItemName(int id, int damage)
    {
        try
        {
            if (id is > 0 and < 256) return Blocks.Get(id).Name.Replace('_', ' ');
            return Items.Get(id).Name.Replace('_', ' ');
        }
        catch (Exception) { return $"item {id}"; }
    }
}

/// <summary>
/// The entity stream's delta log: each rebuild (at most every <see cref="MinInterval"/>, on the caller's pool thread, never the
/// game thread) diffs the merged list against the last one and stamps changed entities with a new revision; removals are kept
/// for <see cref="RemovedRetention"/>. A client sends the revision it has (<c>since</c>) and gets only the entities that changed
/// after it plus the ids that went away — or the full list when it is new or too far behind. Thread-safe.
/// </summary>
public sealed class EntityDeltaLog(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<(int Dim, int Id), (WorldEntity E, WorldEntity Key, long Rev)> _cur = new();
    private readonly List<(long Rev, int Dim, int Id, DateTimeOffset At)> _removed = [];
    private long _rev, _floor; // _floor: oldest revision a delta can start from
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;

    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan RemovedRetention { get; init; } = TimeSpan.FromSeconds(60);
    public long Revision { get { lock (_lock) return _rev; } }

    public sealed record Delta(long Rev, bool Full, IReadOnlyList<WorldEntity> Entities, IReadOnlyList<int[]> Removed)
    {
        /// <summary>One-shot events new to this client (each body's copy of one deduped); empty when none.</summary>
        public IReadOnlyList<WorldEntityEvent> Events { get; init; } = [];
    }

    // One-shot events stamped with the revision they arrived in (kept EventRetention); the dedupe keys by (dim, id, kind).
    private readonly List<(long Rev, WorldEntityEvent E)> _events = [];
    private readonly Dictionary<(int Dim, int Id, string Kind), long> _eventSeen = new();

    /// <summary>The same event seen by several bodies within this window is one (their packets arrive a few ms apart).</summary>
    public TimeSpan EventDedupe { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan EventRetention { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Rebuilds from <paramref name="build"/> when stale, then answers for a client at revision <paramref name="since"/>.</summary>
    public Delta Since(long since, Func<IReadOnlyList<WorldEntity>> build) => Since(since, build, null);

    /// <summary>The same, plus the one-shot <paramref name="events"/> (read on each rebuild): a client sees each event once,
    /// after its <paramref name="since"/>; a full answer carries the last second's.</summary>
    public Delta Since(long since, Func<IReadOnlyList<WorldEntity>> build, Func<IReadOnlyList<WorldEntityEvent>>? events)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (now - _builtAt >= MinInterval)
            {
                Apply(build(), now);
                if (events != null) AddEvents(events(), now);
                _builtAt = now;
            }
            if (since <= 0 || since < _floor || since > _rev)
            {
                long recent = now.AddSeconds(-1).ToUnixTimeMilliseconds();
                return new Delta(_rev, true, _cur.Values.Select(v => v.E).ToList(), []) { Events = _events.Where(x => x.E.At >= recent).Select(x => x.E).ToList() };
            }
            var ups = _cur.Values.Where(v => v.Rev > since).Select(v => v.E).ToList();
            var gone = _removed.Where(r => r.Rev > since).Select(r => new[] { r.Dim, r.Id }).ToList();
            return new Delta(_rev, false, ups, gone) { Events = _events.Where(x => x.Rev > since).Select(x => x.E).ToList() };
        }
    }

    // caller holds _lock
    private void AddEvents(IReadOnlyList<WorldEntityEvent> list, DateTimeOffset now)
    {
        long nowMs = now.ToUnixTimeMilliseconds(), window = (long)EventDedupe.TotalMilliseconds;
        bool any = false;
        foreach (var e in list.OrderBy(e => e.At))
        {
            var k = (e.Dim, e.Id, e.Kind);
            if (_eventSeen.TryGetValue(k, out var at) && Math.Abs(e.At - at) <= window) continue;
            _eventSeen[k] = e.At;
            if (!any) { _rev++; any = true; }
            _events.Add((_rev, e));
        }
        long keep = nowMs - (long)EventRetention.TotalMilliseconds;
        _events.RemoveAll(x => x.E.At < keep);
        if (_eventSeen.Count > 4096)
            foreach (var key in _eventSeen.Where(kv => kv.Value < keep).Select(kv => kv.Key).ToList()) _eventSeen.Remove(key);
    }

    private void Apply(IReadOnlyList<WorldEntity> list, DateTimeOffset now)
    {
        long rev = _rev + 1;
        bool changed = false;
        var seen = new HashSet<(int, int)>();
        foreach (var e in list)
        {
            if (e.Id is not { } id) continue;
            var k = (e.Dim, id);
            seen.Add(k);
            var key = e with { Age = null }; // age ticks every rebuild; the client advances it locally
            if (_cur.TryGetValue(k, out var have) && have.Key == key) { _cur[k] = (e, key, have.Rev); continue; }
            _cur[k] = (e, key, rev);
            changed = true;
        }
        foreach (var k in _cur.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _cur.Remove(k);
            _removed.Add((rev, k.Dim, k.Id, now));
            changed = true;
        }
        if (changed) _rev = rev;
        int drop = 0;
        while (drop < _removed.Count && (now - _removed[drop].At > RemovedRetention || _removed.Count - drop > 4096)) drop++;
        if (drop > 0)
        {
            _floor = Math.Max(_floor, _removed[drop - 1].Rev);
            _removed.RemoveRange(0, drop);
        }
    }
}
