using System.Globalization;
using MineClaw.Agent.Tools;
using Microsoft.Data.Sqlite;

namespace MineClaw.Agent.Rag;

/// <summary>Where a world fact came from. Taught/llm facts outrank automatic ones and are never overwritten by the indexer.</summary>
public enum ObservationSource { Auto, Taught, Llm }

public sealed record ItemCount(int ItemId, string Name, int Count, int Damage = 0);

/// <summary>
/// One thing the world indexer (or a player/the model) knows about: a chest, furnace, bed, sign, farm, island,
/// hangout spot, portal, notable event... Idempotent per (<see cref="Kind"/>, <see cref="Key"/>, dimension); the key is
/// usually the block position ("10,64,-3"), so the same coordinates in the overworld and the nether are two rows.
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
    /// <summary>Which bot read <see cref="Contents"/> from an open window (stored with the time as the contents' freshness).</summary>
    public string? ContentsBy { get; init; }
    /// <summary>When <see cref="Contents"/> were read (null = <see cref="SeenAt"/> / now).</summary>
    public DateTimeOffset? ContentsAt { get; init; }
    public DateTimeOffset? SeenAt { get; init; }
    /// <summary>0 overworld, -1 nether; null = the bot's current dimension (writers that may lag a switch pass it explicitly).</summary>
    public int? Dimension { get; init; }
}

public sealed record ObservationRow(long Id, string Kind, string Key, Box? Bounds, string? FieldsJson, string Text,
    ObservationSource Source, double Confidence, string? Annotation, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    int SeenCount, DateTimeOffset? Removed)
{
    public int Dimension { get; init; }
    /// <summary>When the contents were last read from an open window (null = unknown: a row from before v6).</summary>
    public DateTimeOffset? ContentsAt { get; init; }
    /// <summary>Which bot read them.</summary>
    public string? ContentsBy { get; init; }
    public string Where => Bounds?.Format() ?? Key;
    public string DocRef => ObservationStore.DocRef(Kind, Key, Dimension);
}

public sealed record ItemLocation(ObservationRow Container, string Name, int Count);

/// <summary>
/// Ingestion + structured-query API for world facts written by C# (the WorldIndexer), not the LLM. Each
/// observation is a row in <c>observations</c> (freshness: first_seen/last_seen/seen_count, tombstones) plus a
/// searchable doc (kind "world", tag = observation kind) in <see cref="RagIndex"/>. Noisy kinds
/// (<see cref="RagOptions.NoEmbedKinds"/>) are FTS-only; others re-embed only when their text materially changes
/// (numbers ignored). Structured queries (<see cref="FindItemInContainers(int,int,int?)"/>, <see cref="Nearest"/>,
/// <see cref="ListKinds"/>) never touch embeddings.
/// <para>Dimensions: every row has one. Readers take <c>int? dimension</c>: null = the bot's current dimension
/// (<see cref="RagIndex.CurrentDimension"/>), <see cref="WorldDim.Any"/> = all. Thread-safe.</para>
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
        Portals = new PortalLinks(this, _db, _time);
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    public RagIndex Index => _index;
    /// <summary>Linked portal pairs across dimensions (and the 1:8 travel planner).</summary>
    public PortalLinks Portals { get; }
    public int CurrentDimension => _index.CurrentDimension();
    private int Dim(int? d) => d ?? _index.CurrentDimension();

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
        int dim = Dim(o.Dimension);
        var now = o.SeenAt ?? Now;
        long ms = AgentDb.ToUnixMs(now);
        var text = TextUtil.Truncate(TextUtil.OneLine(o.Text), 400);
        var existing = Get(kind, key, includeRemoved: true, dim);

        if (existing == null) InsertObservation(o, kind, key, dim, ms, text);
        else if (existing.Source != ObservationSource.Auto && o.Source == ObservationSource.Auto)
        {
            // Protected entry: the indexer only annotates and refreshes freshness.
            _db.Exec("UPDATE observations SET last_seen = $t, seen_count = seen_count + 1, removed = NULL, annotation = $a WHERE id = $id",
                ("$t", ms), ("$a", text), ("$id", existing.Id));
        }
        else UpdateObservation(existing, o, ms, text);

        var row = Get(kind, key, includeRemoved: true, dim)!;
        if (o.Contents != null) row = WriteContents(row, o, now, dim);

        // Hash-aware: only a material text change (numbers ignored) re-queues an embedding, and only for embeddable kinds.
        _index.UpsertMany([ToDoc(row)]);
        return row;
    }

    private void InsertObservation(Observation o, string kind, string key, int dim, long ms, string text)
    {
        var b = o.Bounds;
        _db.Exec("""
            INSERT INTO observations(kind, key, dimension, min_x, min_y, min_z, max_x, max_y, max_z, fields, text, source, confidence, annotation,
                first_seen, last_seen, seen_count, removed)
            VALUES ($k, $key, $dim, $x0, $y0, $z0, $x1, $y1, $z1, $f, $text, $src, $c, NULL, $t, $t, 1, NULL)
            """, ("$k", kind), ("$key", key), ("$dim", dim), ("$x0", b?.MinX), ("$y0", b?.MinY), ("$z0", b?.MinZ), ("$x1", b?.MaxX), ("$y1", b?.MaxY), ("$z1", b?.MaxZ),
            ("$f", o.FieldsJson), ("$text", text), ("$src", Source(o.Source)), ("$c", o.Confidence), ("$t", ms));
    }

    /// <summary>A fresh sighting or a taught/manual update: new text and place (kept when not given), the stronger source kept.</summary>
    private void UpdateObservation(ObservationRow existing, Observation o, long ms, string text)
    {
        var b = o.Bounds;
        var src = o.Source == ObservationSource.Auto ? existing.Source : o.Source;
        _db.Exec("""
            UPDATE observations SET min_x = $x0, min_y = $y0, min_z = $z0, max_x = $x1, max_y = $y1, max_z = $z1, fields = coalesce($f, fields),
                text = $text, source = $src, confidence = $c, last_seen = $t, seen_count = seen_count + 1, removed = NULL WHERE id = $id
            """, ("$x0", b?.MinX ?? existing.Bounds?.MinX), ("$y0", b?.MinY ?? existing.Bounds?.MinY), ("$z0", b?.MinZ ?? existing.Bounds?.MinZ),
            ("$x1", b?.MaxX ?? existing.Bounds?.MaxX), ("$y1", b?.MaxY ?? existing.Bounds?.MaxY), ("$z1", b?.MaxZ ?? existing.Bounds?.MaxZ),
            ("$f", o.FieldsJson), ("$text", text), ("$src", Source(src)), ("$c", o.Confidence), ("$t", ms), ("$id", existing.Id));
    }

    /// <summary>A container's snapshot: when and by whom it was opened, and its items grouped by (id, damage).</summary>
    private ObservationRow WriteContents(ObservationRow row, Observation o, DateTimeOffset now, int dim)
    {
        _db.Exec("UPDATE observations SET contents_at = $t, contents_by = $by WHERE id = $id",
            ("$t", AgentDb.ToUnixMs(o.ContentsAt ?? now)), ("$by", o.ContentsBy), ("$id", row.Id));
        row = row with { ContentsAt = o.ContentsAt ?? now, ContentsBy = o.ContentsBy };
        _db.Exec("DELETE FROM container_items WHERE obs_id = $id", ("$id", row.Id));
        foreach (var g in o.Contents!.Where(c => c.Count > 0).GroupBy(c => (c.ItemId, c.Damage)))
            _db.Exec("INSERT INTO container_items(obs_id, item_id, damage, name, count, dimension) VALUES ($o, $i, $d, $n, $c, $dim)",
                ("$o", row.Id), ("$i", g.Key.ItemId), ("$d", g.Key.Damage), ("$n", g.First().Name.ToLowerInvariant()), ("$c", g.Sum(x => x.Count)), ("$dim", dim));
        return row;
    }

    private RagDoc ToDoc(ObservationRow r)
    {
        var body = r.Text + (r.Annotation != null && r.Annotation != r.Text ? $" [seen: {TextUtil.Truncate(r.Annotation, 160)}]" : "");
        return new RagDoc(r.DocRef, DocKind, $"{r.Kind} {r.Key}", body)
        {
            Tag = r.Kind,
            Source = Source(r.Source),
            Bounds = r.Bounds,
            Dimension = r.Dimension,
            Embed = !_noEmbed.Contains(r.Kind),
            Weight = Weight(r.Source),
            At = r.LastSeen,
        };
    }

    /// <summary>Doc ref of an observation: "obs:chest:1,2,3" in the overworld (unchanged since v1), "obs:d-1:chest:1,2,3" elsewhere.</summary>
    public static string DocRef(string kind, string key, int dimension = WorldDim.Overworld) =>
        dimension == WorldDim.Overworld ? $"obs:{kind}:{key}" : string.Create(CultureInfo.InvariantCulture, $"obs:d{dimension}:{kind}:{key}");

    /// <summary>
    /// A block/thing disappeared. Tombstones (keeps first_seen/history, drops it from search and container queries)
    /// or deletes outright. A taught/llm entry removed by the indexer is only annotated as missing.
    /// </summary>
    public bool Remove(string kind, string key, bool tombstone = true, ObservationSource by = ObservationSource.Auto, int? dimension = null)
    {
        kind = kind.Trim().ToLowerInvariant();
        int dim = Dim(dimension);
        return _db.Tx(() =>
        {
            var row = Get(kind, key, includeRemoved: true, dim);
            if (row == null) return false;
            if (row.Source != ObservationSource.Auto && by == ObservationSource.Auto)
            {
                _db.Exec("UPDATE observations SET annotation = $a WHERE id = $id", ("$a", $"not seen here since {Now:MM-dd HH:mm}"), ("$id", row.Id));
                _index.Upsert(ToDoc(Get(kind, key, includeRemoved: true, dim)!));
                return true;
            }
            _db.Exec("DELETE FROM container_items WHERE obs_id = $id", ("$id", row.Id));
            // Its contents' date goes with its items: a row revived at the same key (the surviving west half of a broken double
            // chest, a chest re-placed) must read "contents unknown", never "opened 3m ago: empty".
            if (tombstone) _db.Exec("UPDATE observations SET removed = $t, contents_at = NULL, contents_by = NULL WHERE id = $id",
                ("$t", AgentDb.ToUnixMs(Now)), ("$id", row.Id));
            else _db.Exec("DELETE FROM observations WHERE id = $id", ("$id", row.Id));
            _index.Delete(row.DocRef);
            return true;
        });
    }

    /// <summary>
    /// Admin correction (memory admin tool): new text and/or annotation ("" clears it) for one row, its doc re-indexed, one
    /// transaction. A text edit marks the row taught, so the world indexer only annotates it from then on instead of
    /// overwriting the correction. Null when there is no such row.
    /// </summary>
    public ObservationRow? AdminEdit(long id, string? text, string? annotation)
    {
        return _db.Tx(() =>
        {
            if (GetById(id) is not { } row) return null;
            if (text != null)
            {
                var t = TextUtil.Truncate(TextUtil.OneLine(text), 400);
                if (t.Length == 0) throw new ArgumentException("observation text can't be empty");
                _db.Exec("UPDATE observations SET text = $t, source = $s WHERE id = $id",
                    ("$t", t), ("$s", row.Source == ObservationSource.Auto ? Source(ObservationSource.Taught) : Source(row.Source)), ("$id", id));
            }
            if (annotation != null)
                _db.Exec("UPDATE observations SET annotation = $a WHERE id = $id", ("$a", annotation.Length == 0 ? null : TextUtil.OneLine(annotation)), ("$id", id));
            var after = GetById(id)!;
            if (after.Removed == null) _index.Upsert(ToDoc(after));
            return after;
        });
    }

    // ---------------- reads ----------------

    /// <summary>One row by id (removed ones included).</summary>
    public ObservationRow? GetById(long id) =>
        _db.Query($"SELECT {Cols} FROM observations WHERE id = $id", r => Map(r), ("$id", id)).FirstOrDefault();

    private const string Cols = "id, kind, key, min_x, min_y, min_z, max_x, max_y, max_z, fields, text, source, confidence, annotation, first_seen, last_seen, seen_count, removed, dimension, contents_at, contents_by";
    private const int NCols = 21;
    private static readonly string OCols = string.Join(", ", Cols.Split(", ").Select(c => "o." + c));

    /// <summary>SQL filter on a dimension column ($dim = <see cref="WorldDim.Any"/> matches all).</summary>
    private static string DimFilter(string col = "dimension") => $"($dim = {WorldDim.Any} OR {col} = $dim)";

    private static ObservationRow Map(SqliteDataReader r, int o = 0) => new(
        r.GetInt64(o), r.GetString(o + 1), r.GetString(o + 2),
        r.IsDBNull(o + 3) ? null : new Box(r.GetDouble(o + 3), r.GetDouble(o + 4), r.GetDouble(o + 5), r.GetDouble(o + 6), r.GetDouble(o + 7), r.GetDouble(o + 8)),
        AgentDb.StrOrNull(r, o + 9), r.GetString(o + 10), ParseSource(r.GetString(o + 11)), r.GetDouble(o + 12), AgentDb.StrOrNull(r, o + 13),
        AgentDb.FromUnixMs(r.GetInt64(o + 14)), AgentDb.FromUnixMs(r.GetInt64(o + 15)), (int)r.GetInt64(o + 16),
        AgentDb.LongOrNull(r, o + 17) is { } rm ? AgentDb.FromUnixMs(rm) : null)
    {
        Dimension = (int)r.GetInt64(o + 18),
        ContentsAt = AgentDb.LongOrNull(r, o + 19) is { } ca ? AgentDb.FromUnixMs(ca) : null,
        ContentsBy = AgentDb.StrOrNull(r, o + 20),
    };

    /// <param name="dimension">Null = current; <see cref="WorldDim.Any"/> = first match in any dimension (current preferred).</param>
    public ObservationRow? Get(string kind, string key, bool includeRemoved = false, int? dimension = null)
    {
        int dim = Dim(dimension);
        return _db.Query($"""
            SELECT {Cols} FROM observations WHERE kind = $k AND key = $key AND {DimFilter()} AND ($all = 1 OR removed IS NULL)
            ORDER BY dimension = $cur DESC LIMIT 1
            """, r => Map(r), ("$k", kind.Trim().ToLowerInvariant()), ("$key", key.Trim()), ("$dim", dim), ("$all", includeRemoved ? 1 : 0),
            ("$cur", CurrentDimension)).FirstOrDefault();
    }

    public IReadOnlyList<ItemCount> Contents(long observationId) =>
        _db.Query("SELECT item_id, name, count, damage FROM container_items WHERE obs_id = $id ORDER BY count DESC",
            r => new ItemCount((int)r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2), (int)r.GetInt64(3)), ("$id", observationId));

    /// <summary>"Where's my iron?" — containers holding an item, biggest stacks first. Zero LLM tokens.</summary>
    public List<ItemLocation> FindItemInContainers(int itemId, int limit = 8, int? dimension = null) =>
        _db.Query($"""
            SELECT {OCols}, ci.name, sum(ci.count)
            FROM container_items ci JOIN observations o ON o.id = ci.obs_id
            WHERE ci.item_id = $i AND o.removed IS NULL AND {DimFilter("o.dimension")} GROUP BY o.id ORDER BY sum(ci.count) DESC LIMIT $n
            """, r => new ItemLocation(Map(r), r.GetString(NCols), (int)r.GetInt64(NCols + 1)), ("$i", itemId), ("$n", limit), ("$dim", Dim(dimension)));

    /// <summary>By name: resolves via the item table (aliases, plurals); falls back to a substring match on stored names.</summary>
    public List<ItemLocation> FindItemInContainers(string item, int limit = 8, int? dimension = null)
    {
        var info = MineClaw.Data.Items.ByName(item);
        if (info != null)
        {
            var byId = FindItemInContainers(info.Id, limit, dimension);
            if (byId.Count > 0) return byId;
        }
        var needle = "%" + item.Trim().ToLowerInvariant().Replace(' ', '_') + "%";
        return _db.Query($"""
            SELECT {OCols}, ci.name, sum(ci.count)
            FROM container_items ci JOIN observations o ON o.id = ci.obs_id
            WHERE ci.name LIKE $n AND o.removed IS NULL AND {DimFilter("o.dimension")} GROUP BY o.id, ci.name ORDER BY sum(ci.count) DESC LIMIT $l
            """, r => new ItemLocation(Map(r), r.GetString(NCols), (int)r.GetInt64(NCols + 1)), ("$n", needle), ("$l", limit), ("$dim", Dim(dimension)));
    }

    /// <summary>Nearest live observations of a kind (null = any) to a point, in one dimension (null = current).</summary>
    public List<(ObservationRow Row, double Distance)> Nearest(string? kind, double x, double y, double z, int limit = 3, double maxDistance = 256,
        int? dimension = null)
    {
        var rows = _db.Query($"""
            SELECT {Cols} FROM observations WHERE removed IS NULL AND ($k IS NULL OR kind = $k) AND {DimFilter()} AND min_x IS NOT NULL
              AND max_x >= $x0 AND min_x <= $x1 AND max_z >= $z0 AND min_z <= $z1
            """, r => Map(r), ("$k", kind?.Trim().ToLowerInvariant()), ("$dim", Dim(dimension)),
            ("$x0", x - maxDistance), ("$x1", x + maxDistance), ("$z0", z - maxDistance), ("$z1", z + maxDistance));
        return rows.Select(r => (r, r.Bounds!.Value.Distance(x, y, z))).Where(t => t.Item2 <= maxDistance)
            .OrderBy(t => t.Item2).Take(limit).ToList();
    }

    /// <summary>
    /// Live containers of the given kinds with a known inventory (<c>contents_at</c> set) whose bounds meet <paramref name="region"/>
    /// in one dimension, each with its items (biggest first, as <see cref="Contents"/>): one statement, served by the
    /// <c>(dimension, kind, removed)</c> index, where <see cref="Nearest"/> with any kind and a <see cref="Contents"/> per row scanned
    /// the whole table. At most <paramref name="limit"/> containers, in no particular order.
    /// </summary>
    public List<(ObservationRow Row, IReadOnlyList<ItemCount> Items)> ContainersIn(IReadOnlyList<string> kinds, Box region, int dimension, int limit)
    {
        if (kinds.Count == 0 || limit <= 0) return [];
        var args = new List<(string Name, object? Value)>
        {
            ("$dim", dimension), ("$n", limit), ("$x0", region.MinX), ("$x1", region.MaxX), ("$y0", region.MinY), ("$y1", region.MaxY),
            ("$z0", region.MinZ), ("$z1", region.MaxZ),
        };
        for (int i = 0; i < kinds.Count; i++) args.Add(($"$k{i}", kinds[i]));
        var rows = new List<(ObservationRow Row, IReadOnlyList<ItemCount> Items)>();
        List<ItemCount>? items = null;
        _db.Query(ContainersInSql(kinds.Count), r =>
        {
            long id = r.GetInt64(0);
            if (rows.Count == 0 || rows[^1].Row.Id != id) rows.Add((Map(r), items = []));
            if (!r.IsDBNull(NCols)) items!.Add(new ItemCount((int)r.GetInt64(NCols), r.GetString(NCols + 1), (int)r.GetInt64(NCols + 2), (int)r.GetInt64(NCols + 3)));
            return 0;
        }, args.ToArray());
        return rows;
    }

    /// <summary>The statement of <see cref="ContainersIn"/> for <paramref name="kinds"/> kinds (<c>$k0</c>…): the inner select is what
    /// the <c>(dimension, kind, removed)</c> index serves; the items join by their primary key.</summary>
    internal static string ContainersInSql(int kinds) => $"""
        SELECT {OCols}, ci.item_id, ci.name, ci.count, ci.damage
        FROM (SELECT * FROM observations WHERE dimension = $dim AND kind IN ({string.Join(", ", Enumerable.Range(0, kinds).Select(i => $"$k{i}"))})
                AND removed IS NULL AND contents_at IS NOT NULL AND min_x IS NOT NULL
                AND max_x >= $x0 AND min_x <= $x1 AND max_y >= $y0 AND min_y <= $y1 AND max_z >= $z0 AND min_z <= $z1
              LIMIT $n) o
        LEFT JOIN container_items ci ON ci.obs_id = o.id
        ORDER BY o.id, ci.count DESC
        """;

    /// <summary>All live rows of a kind in a dimension (null = current), newest first (portals, small kinds).</summary>
    public List<ObservationRow> OfKind(string kind, int? dimension = null, int limit = 50) =>
        _db.Query($"SELECT {Cols} FROM observations WHERE removed IS NULL AND kind = $k AND {DimFilter()} ORDER BY last_seen DESC LIMIT $n",
            r => Map(r), ("$k", kind.Trim().ToLowerInvariant()), ("$dim", Dim(dimension)), ("$n", limit));

    /// <summary>When each live row of a kind had its contents read (rows with contents only), by key (null dimension = current): one
    /// query for a caller that would otherwise read every row to learn which changed.</summary>
    public List<(string Key, DateTimeOffset ContentsAt)> ContentsTimes(string kind, int? dimension = null) =>
        _db.Query($"SELECT key, contents_at FROM observations WHERE removed IS NULL AND contents_at IS NOT NULL AND kind = $k AND {DimFilter()}",
            r => (r.GetString(0), AgentDb.FromUnixMs(r.GetInt64(1))), ("$k", kind.Trim().ToLowerInvariant()), ("$dim", Dim(dimension)));

    /// <summary>Counts of live observation kinds intersecting a region (null = current dimension).</summary>
    public Dictionary<string, int> ListKinds(Box region, int? dimension = null) =>
        _db.Query($"""
            SELECT kind, count(*) FROM observations WHERE removed IS NULL AND min_x IS NOT NULL AND {DimFilter()}
              AND max_x >= $x0 AND min_x <= $x1 AND max_y >= $y0 AND min_y <= $y1 AND max_z >= $z0 AND min_z <= $z1
            GROUP BY kind ORDER BY count(*) DESC
            """, r => (r.GetString(0), (int)r.GetInt64(1)), ("$dim", Dim(dimension)),
            ("$x0", region.MinX), ("$x1", region.MaxX), ("$y0", region.MinY), ("$y1", region.MaxY), ("$z0", region.MinZ), ("$z1", region.MaxZ))
        .ToDictionary(t => t.Item1, t => t.Item2);

    /// <summary>Live rows (all dimensions unless one is given).</summary>
    public int Count(string? kind = null, int dimension = WorldDim.Any) =>
        (int)_db.ScalarLong($"SELECT count(*) FROM observations WHERE removed IS NULL AND ($k IS NULL OR kind = $k) AND {DimFilter()}",
            ("$k", kind), ("$dim", dimension));
}
