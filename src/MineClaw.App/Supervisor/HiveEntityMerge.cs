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
                if (e.Kind is not (EntityKind.Player or EntityKind.Mob or EntityKind.Item)) continue;
                if (e.Kind == EntityKind.Player && (e.Name == null || botNames.Contains(e.Name))) continue;
                if (e.Kind == EntityKind.Item && e.Item.IsEmpty) continue;
                var k = (b.Dim, e.Id);
                if (best.TryGetValue(k, out var have) && have.E.UpdatedAt >= e.UpdatedAt) continue;
                best[k] = (e, b.Body, b.Dim);
            }
        var list = new List<WorldEntity>(Math.Min(best.Count, max));
        // Items first (they are what a bot looks for), then players, then mobs; stable by id.
        foreach (var (e, by, dim) in best.Values.OrderBy(v => v.E.Kind == EntityKind.Item ? 0 : v.E.Kind == EntityKind.Player ? 1 : 2).ThenBy(v => v.E.Id))
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
        return e.Kind switch
        {
            EntityKind.Item => new WorldEntity("item", ItemName(e.Item.Id, e.Item.Damage), dim, R(p.X), R(p.Y), R(p.Z), e.Id, null,
                e.Item.Id, e.Item.Damage, e.Item.Count, R3(e.Velocity.X), R3(e.Velocity.Y), R3(e.Velocity.Z), age, null, by, FlagsOf(e)),
            EntityKind.Mob => new WorldEntity("mob", MobTypes.Name(e.TypeId), dim, R(p.X), R(p.Y), R(p.Z), e.Id, e.TypeId,
                null, null, null, R3(e.Velocity.X), R3(e.Velocity.Y), R3(e.Velocity.Z), age, (float)Math.Round(e.Yaw, 1), by, FlagsOf(e), e.MetaInt(16)),
            _ => new WorldEntity("player", e.Name, dim, R(p.X), R(p.Y), R(p.Z), e.Id, null, e.HeldItem > 0 ? e.HeldItem : null, null, null,
                R3(e.Velocity.X), R3(e.Velocity.Y), R3(e.Velocity.Z), age, (float)Math.Round(e.Yaw, 1), by, FlagsOf(e)),
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

    public sealed record Delta(long Rev, bool Full, IReadOnlyList<WorldEntity> Entities, IReadOnlyList<int[]> Removed);

    /// <summary>Rebuilds from <paramref name="build"/> when stale, then answers for a client at revision <paramref name="since"/>.</summary>
    public Delta Since(long since, Func<IReadOnlyList<WorldEntity>> build)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (now - _builtAt >= MinInterval) { Apply(build(), now); _builtAt = now; }
            if (since <= 0 || since < _floor || since > _rev)
                return new Delta(_rev, true, _cur.Values.Select(v => v.E).ToList(), []);
            var ups = _cur.Values.Where(v => v.Rev > since).Select(v => v.E).ToList();
            var gone = _removed.Where(r => r.Rev > since).Select(r => new[] { r.Dim, r.Id }).ToList();
            return new Delta(_rev, false, ups, gone);
        }
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
