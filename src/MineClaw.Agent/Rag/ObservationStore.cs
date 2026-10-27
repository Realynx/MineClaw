using System.Globalization;
using MineClaw.Agent.Tools;
using Microsoft.Data.Sqlite;

namespace MineClaw.Agent.Rag;

/// <summary>Where a world fact came from. Taught/llm facts outrank automatic ones and are never overwritten by the indexer.</summary>
public enum ObservationSource { Auto, Taught, Llm }

public sealed record ItemCount(int ItemId, string Name, int Count, int Damage = 0);

/// <summary>
/// One thing the world indexer (or a player/the model) knows about: a chest, furnace, bed, sign, farm, island,
/// hangout spot, notable event... Idempotent per (<see cref="Kind"/>, <see cref="Key"/>); the key is usually the
/// block position ("10,64,-3").
/// </summary>
public sealed record Observation(string Kind, string Key, string Text)
{
    public Box? Bounds { get; init; }
    /// <summary>Structured fields as JSON (stored verbatim, returned by <see cref="ObservationStore.Get"/>).</summary>
    public string? FieldsJson { get; init; }
    public ObservationSource Source { get; init; } = ObservationSource.Auto;
    public double Confidence { get; init; } = 1.0;
    /// <summary>Container snapshot; when non-null it replaces the previous contents.</summary>
    public IReadOnlyList<ItemCount>? Contents { get; init; }
    public DateTimeOffset? SeenAt { get; init; }
}

public sealed record ObservationRow(long Id, string Kind, string Key, Box? Bounds, string? FieldsJson, string Text,
    ObservationSource Source, double Confidence, string? Annotation, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    int SeenCount, DateTimeOffset? Removed)
{
    public string Where => Bounds?.Format() ?? Key;
}

public sealed record ItemLocation(ObservationRow Container, string Name, int Count);

/// <summary>
/// Ingestion + structured-query API for world facts written by C# (the WorldIndexer), not the LLM. Each
/// observation is a row in <c>observations</c> (freshness: first_seen/last_seen/seen_count, tombstones) plus a
/// searchable doc (kind "world", tag = observation kind) in <see cref="RagIndex"/>. Noisy kinds
/// (<see cref="RagOptions.NoEmbedKinds"/>) are FTS-only; others re-embed only when their text materially changes
/// (numbers ignored). Structured queries (<see cref="FindItemInContainers(int,int)"/>, <see cref="Nearest"/>,
/// <see cref="ListKinds"/>) never touch embeddings. Thread-safe.
/// </summary>
public sealed class ObservationStore
{
    public const string DocKind = "world";

    private readonly RagIndex _index;
    private readonly AgentDb _db;
    private readonly RagOptions _o;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _noEmbed;

    public ObservationStore(RagIndex index, TimeProvider? time = null)
    {
        _index = index;
        _db = index.Db;
        _o = index.Options;
        _time = time ?? TimeProvider.System;
        _noEmbed = new HashSet<string>(_o.NoEmbedKinds, StringComparer.OrdinalIgnoreCase);
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    public static string Source(ObservationSource s) => s switch { ObservationSource.Taught => "taught", ObservationSource.Llm => "llm", _ => "auto" };
    private static ObservationSource ParseSource(string s) => s switch { "taught" => ObservationSource.Taught, "llm" => ObservationSource.Llm, _ => ObservationSource.Auto };
    private static double Weight(ObservationSource s) => s switch { ObservationSource.Taught => 1.3, ObservationSource.Llm => 1.15, _ => 1.0 };

    public static string KeyFor(int x, int y, int z) => string.Create(CultureInfo.InvariantCulture, $"{x},{y},{z}");

    // ---------------- writes ----------------

    /// <summary>Insert or refresh one observation (UpsertObservation).</summary>
    public ObservationRow Upsert(Observation o)
    {
        return _db.Tx(() => UpsertCore(o));
    }

    /// <summary>Batched upsert in one transaction — a chunk-load burst costs one commit and one embedding wake-up.</summary>
    public int UpsertMany(IEnumerable<Observation> batch)
    {
        return _db.Tx(() =>
        {
            int c = 0;
            foreach (var o in batch) { UpsertCore(o); c++; }
            return c;
        });
    }

    private ObservationRow UpsertCore(Observation o)
    {
        var kind = o.Kind.Trim().ToLowerInvariant();
        var key = o.Key.Trim();
        var now = o.SeenAt ?? Now;
        long ms = AgentDb.ToUnixMs(now);
        var text = TextUtil.Truncate(TextUtil.OneLine(o.Text), 400);
        var existing = Get(kind, key, includeRemoved: true);
        var b = o.Bounds;

        if (existing == null)
        {
            _db.Exec("""
                INSERT INTO observations(kind, key, min_x, min_y, min_z, max_x, max_y, max_z, fields, text, source, confidence, annotation,
                    first_seen, last_seen, seen_count, removed)
                VALUES ($k, $key, $x0, $y0, $z0, $x1, $y1, $z1, $f, $text, $src, $c, NULL, $t, $t, 1, NULL)
                """, ("$k", kind), ("$key", key), ("$x0", b?.MinX), ("$y0", b?.MinY), ("$z0", b?.MinZ), ("$x1", b?.MaxX), ("$y1", b?.MaxY), ("$z1", b?.MaxZ),
                ("$f", o.FieldsJson), ("$text", text), ("$src", Source(o.Source)), ("$c", o.Confidence), ("$t", ms));
        }
        else if (existing.Source != ObservationSource.Auto && o.Source == ObservationSource.Auto)
        {
            // Protected entry: the indexer only annotates and refreshes freshness.
            _db.Exec("UPDATE observations SET last_seen = $t, seen_count = seen_count + 1, removed = NULL, annotation = $a WHERE id = $id",
                ("$t", ms), ("$a", text), ("$id", existing.Id));
        }
        else
        {
            var src = o.Source == ObservationSource.Auto ? existing.Source : o.Source;
            _db.Exec("""
                UPDATE observations SET min_x = $x0, min_y = $y0, min_z = $z0, max_x = $x1, max_y = $y1, max_z = $z1, fields = coalesce($f, fields),
                    text = $text, source = $src, confidence = $c, last_seen = $t, seen_count = seen_count + 1, removed = NULL WHERE id = $id
                """, ("$x0", b?.MinX ?? existing.Bounds?.MinX), ("$y0", b?.MinY ?? existing.Bounds?.MinY), ("$z0", b?.MinZ ?? existing.Bounds?.MinZ),
                ("$x1", b?.MaxX ?? existing.Bounds?.MaxX), ("$y1", b?.MaxY ?? existing.Bounds?.MaxY), ("$z1", b?.MaxZ ?? existing.Bounds?.MaxZ),
                ("$f", o.FieldsJson), ("$text", text), ("$src", Source(src)), ("$c", o.Confidence), ("$t", ms), ("$id", existing.Id));
        }

        var row = Get(kind, key, includeRemoved: true)!;
        if (o.Contents != null)
        {
            _db.Exec("DELETE FROM container_items WHERE obs_id = $id", ("$id", row.Id));
            foreach (var g in o.Contents.Where(c => c.Count > 0).GroupBy(c => (c.ItemId, c.Damage)))
                _db.Exec("INSERT INTO container_items(obs_id, item_id, damage, name, count) VALUES ($o, $i, $d, $n, $c)",
                    ("$o", row.Id), ("$i", g.Key.ItemId), ("$d", g.Key.Damage), ("$n", g.First().Name.ToLowerInvariant()), ("$c", g.Sum(x => x.Count)));
        }

        // Hash-aware: only a material text change (numbers ignored) re-queues an embedding, and only for embeddable kinds.
        _index.UpsertMany([ToDoc(row)]);
        return row;
    }

    private RagDoc ToDoc(ObservationRow r)
    {
        var body = r.Text + (r.Annotation != null && r.Annotation != r.Text ? $" [seen: {TextUtil.Truncate(r.Annotation, 160)}]" : "");
        return new RagDoc(DocRef(r.Kind, r.Key), DocKind, $"{r.Kind} {r.Key}", body)
        {
            Tag = r.Kind,
            Source = Source(r.Source),
            Bounds = r.Bounds,
            Embed = !_noEmbed.Contains(r.Kind),
            Weight = Weight(r.Source),
            At = r.LastSeen,
        };
    }

    public static string DocRef(string kind, string key) => $"obs:{kind}:{key}";

    /// <summary>
    /// A block/thing disappeared. Tombstones (keeps first_seen/history, drops it from search and container queries)
    /// or deletes outright. A taught/llm entry removed by the indexer is only annotated as missing.
    /// </summary>
    public bool Remove(string kind, string key, bool tombstone = true, ObservationSource by = ObservationSource.Auto)
    {
        kind = kind.Trim().ToLowerInvariant();
        return _db.Tx(() =>
        {
            var row = Get(kind, key, includeRemoved: true);
            if (row == null) return false;
            if (row.Source != ObservationSource.Auto && by == ObservationSource.Auto)
            {
                _db.Exec("UPDATE observations SET annotation = $a WHERE id = $id", ("$a", $"not seen here since {Now:MM-dd HH:mm}"), ("$id", row.Id));
                _index.Upsert(ToDoc(Get(kind, key, includeRemoved: true)!));
                return true;
            }
            _db.Exec("DELETE FROM container_items WHERE obs_id = $id", ("$id", row.Id));
            if (tombstone) _db.Exec("UPDATE observations SET removed = $t WHERE id = $id", ("$t", AgentDb.ToUnixMs(Now)), ("$id", row.Id));
            else _db.Exec("DELETE FROM observations WHERE id = $id", ("$id", row.Id));
            _index.Delete(DocRef(kind, key));
            return true;
        });
    }

    // ---------------- reads ----------------

    private const string Cols = "id, kind, key, min_x, min_y, min_z, max_x, max_y, max_z, fields, text, source, confidence, annotation, first_seen, last_seen, seen_count, removed";

    private static ObservationRow Map(SqliteDataReader r, int o = 0) => new(
        r.GetInt64(o), r.GetString(o + 1), r.GetString(o + 2),
        r.IsDBNull(o + 3) ? null : new Box(r.GetDouble(o + 3), r.GetDouble(o + 4), r.GetDouble(o + 5), r.GetDouble(o + 6), r.GetDouble(o + 7), r.GetDouble(o + 8)),
        AgentDb.StrOrNull(r, o + 9), r.GetString(o + 10), ParseSource(r.GetString(o + 11)), r.GetDouble(o + 12), AgentDb.StrOrNull(r, o + 13),
        AgentDb.FromUnixMs(r.GetInt64(o + 14)), AgentDb.FromUnixMs(r.GetInt64(o + 15)), (int)r.GetInt64(o + 16),
        AgentDb.LongOrNull(r, o + 17) is { } rm ? AgentDb.FromUnixMs(rm) : null);

    public ObservationRow? Get(string kind, string key, bool includeRemoved = false) =>
        _db.Query($"SELECT {Cols} FROM observations WHERE kind = $k AND key = $key AND ($all = 1 OR removed IS NULL)",
            r => Map(r), ("$k", kind.Trim().ToLowerInvariant()), ("$key", key.Trim()), ("$all", includeRemoved ? 1 : 0)).FirstOrDefault();

    public IReadOnlyList<ItemCount> Contents(long observationId) =>
        _db.Query("SELECT item_id, name, count, damage FROM container_items WHERE obs_id = $id ORDER BY count DESC",
            r => new ItemCount((int)r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2), (int)r.GetInt64(3)), ("$id", observationId));

    /// <summary>"Where's my iron?" — containers holding an item, biggest stacks first. Zero LLM tokens.</summary>
    public List<ItemLocation> FindItemInContainers(int itemId, int limit = 8) =>
        _db.Query($"""
            SELECT {string.Join(", ", Cols.Split(", ").Select(c => "o." + c))}, ci.name, sum(ci.count)
            FROM container_items ci JOIN observations o ON o.id = ci.obs_id
            WHERE ci.item_id = $i AND o.removed IS NULL GROUP BY o.id ORDER BY sum(ci.count) DESC LIMIT $n
            """, r => new ItemLocation(Map(r), r.GetString(18), (int)r.GetInt64(19)), ("$i", itemId), ("$n", limit));

    /// <summary>By name: resolves via the item table (aliases, plurals); falls back to a substring match on stored names.</summary>
    public List<ItemLocation> FindItemInContainers(string item, int limit = 8)
    {
        var info = MineClaw.Data.Items.ByName(item);
        if (info != null)
        {
            var byId = FindItemInContainers(info.Id, limit);
            if (byId.Count > 0) return byId;
        }
        var needle = "%" + item.Trim().ToLowerInvariant().Replace(' ', '_') + "%";
        return _db.Query($"""
            SELECT {string.Join(", ", Cols.Split(", ").Select(c => "o." + c))}, ci.name, sum(ci.count)
            FROM container_items ci JOIN observations o ON o.id = ci.obs_id
            WHERE ci.name LIKE $n AND o.removed IS NULL GROUP BY o.id, ci.name ORDER BY sum(ci.count) DESC LIMIT $l
            """, r => new ItemLocation(Map(r), r.GetString(18), (int)r.GetInt64(19)), ("$n", needle), ("$l", limit));
    }

    /// <summary>Nearest live observations of a kind (null = any) to a point.</summary>
    public List<(ObservationRow Row, double Distance)> Nearest(string? kind, double x, double y, double z, int limit = 3, double maxDistance = 256)
    {
        var rows = _db.Query($"""
            SELECT {Cols} FROM observations WHERE removed IS NULL AND ($k IS NULL OR kind = $k) AND min_x IS NOT NULL
              AND max_x >= $x0 AND min_x <= $x1 AND max_z >= $z0 AND min_z <= $z1
            """, r => Map(r), ("$k", kind?.Trim().ToLowerInvariant()),
            ("$x0", x - maxDistance), ("$x1", x + maxDistance), ("$z0", z - maxDistance), ("$z1", z + maxDistance));
        return rows.Select(r => (r, r.Bounds!.Value.Distance(x, y, z))).Where(t => t.Item2 <= maxDistance)
            .OrderBy(t => t.Item2).Take(limit).ToList();
    }

    /// <summary>Counts of live observation kinds intersecting a region.</summary>
    public Dictionary<string, int> ListKinds(Box region) =>
        _db.Query("""
            SELECT kind, count(*) FROM observations WHERE removed IS NULL AND min_x IS NOT NULL
              AND max_x >= $x0 AND min_x <= $x1 AND max_y >= $y0 AND min_y <= $y1 AND max_z >= $z0 AND min_z <= $z1
            GROUP BY kind ORDER BY count(*) DESC
            """, r => (r.GetString(0), (int)r.GetInt64(1)),
            ("$x0", region.MinX), ("$x1", region.MaxX), ("$y0", region.MinY), ("$y1", region.MaxY), ("$z0", region.MinZ), ("$z1", region.MaxZ))
        .ToDictionary(t => t.Item1, t => t.Item2);

    public int Count(string? kind = null) =>
        (int)_db.ScalarLong("SELECT count(*) FROM observations WHERE removed IS NULL AND ($k IS NULL OR kind = $k)", ("$k", kind));
}
