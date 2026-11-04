using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace MineClaw.ViewerBridge.Data;

public sealed record MemoryKindInfoDto(string Key, string Label, string Group, long Count, bool Available, string? EditKind, IReadOnlyList<string> States);
public sealed record MemoryKindsResponse(IReadOnlyList<MemoryKindInfoDto> Kinds);
public sealed record MemoryRowDto(string Id, string Name, string? Type, string? State, string Dim, double? Conf, long? Updated, string Preview, string? DocRef, int? RaptorLeaf);
public sealed record MemoryPage(string Kind, long Total, int Offset, int Limit, IReadOnlyList<MemoryRowDto> Items);
public sealed record MemoryProp(string Key, string Label, string? Value);
public sealed record MemoryDetail(string Kind, string Id, MemoryRowDto Row, IReadOnlyList<MemoryProp> Props, IReadOnlyDictionary<string, object?> Record,
    string? EditKind, IReadOnlyDictionary<string, string?>? EditPrefill, IReadOnlyList<string> People);

public sealed record MemoryListQuery(string? Q, string Sort, string Dir, string Dim, string? State, int Offset, int Limit);

/// <summary>
/// The memory browser (explorer-spec §6) over the read-only db, SERVER-PAGED: each kind is one SELECT with the grid's columns
/// (id, name, type, state, dim, conf, updated, text), filtered, sorted and paged in SQL (LIMIT/OFFSET), never loaded whole.
/// The kinds, labels and SQL follow tools/MemoryExplorer/Services/MemoryQueries.cs; every query tolerates a missing table/column.
/// </summary>
public sealed class MemoryBrowser(MemoryBank bank)
{
    private readonly AgentDb _db = bank.Db;

    public sealed record KindDef(string Key, string Label, string Group, string? EditKind, string Table);

    public static readonly IReadOnlyList<KindDef> Kinds =
    [
        new("facts", "Facts", "Memory", "fact", "notes"),
        new("claims", "Claims", "Beliefs", "claim", "claims"),
        new("beliefs", "Beliefs", "Beliefs", "claim", "claims"),
        new("personality", "Self-profile", "Self", "trait", "self_profile"),
        new("structures", "Structures", "World", "structure", "structures"),
        new("places", "Places", "World", "place", "waypoints"),
        new("chests", "Chests", "World", "observation", "observations"),
        new("gencells", "Gen cells", "World", "observation", "observations"),
        new("observed", "Observed places", "World", "observation", "observations"),
        new("lessons", "Lessons", "Memory", "lesson", "knowledge_corrections"),
        new("episodes", "Episodes", "Events", null, "docs"),
        new("activity", "Activity log", "Events", null, "activity_log"),
        new("chat", "Chat log", "Events", null, "chat_log"),
        new("routines", "Routines & scripts", "Memory", null, "routines"),
        new("recipes", "Recipes & game data", "Knowledge", null, "docs"),
        new("knowledge", "Knowledge & skills", "Knowledge", null, "docs"),
    ];

    private const string ContainerKinds = "('chest','trapped_chest','furnace','dispenser')";
    private static readonly HashSet<string> StatefulKinds = ["claims", "beliefs", "personality", "chests", "gencells", "observed", "episodes", "recipes", "knowledge"];

    public static KindDef? Kind(string key) => Kinds.FirstOrDefault(k => k.Key == key);

    private bool Has(string table) => _db.TableExists(table);
    private bool HasCol(string table, string col) => Has(table) && _db.HasColumn(table, col);

    /// <summary>The kind's grid SELECT (columns: id, name, type, state, dim, conf, updated, text, docref, raptor), or null when the table is missing.</summary>
    public string? Select(string key)
    {
        var k = Kind(key);
        if (k == null || !Has(k.Table)) return null;
        const string Cols = "id, name, type, state, dim, conf, updated, text, docref, raptor";
        return (MemorySelect(key) ?? WorldSelect(key) ?? EventSelect(key)) is { } sql ? $"SELECT {Cols} FROM ({sql})" : null;
    }

    /// <summary>The dimension column of <paramref name="table"/> ("0" when it has none), NULL where <paramref name="posCol"/> is.</summary>
    private string Dim(string table, string? posCol)
    {
        var d = HasCol(table, "dimension") ? "dimension" : "0";
        return posCol != null ? $"CASE WHEN {posCol} IS NULL THEN NULL ELSE {d} END" : d;
    }

    /// <summary>Notes, claims and beliefs, the self-profile, lessons and routines.</summary>
    private string? MemorySelect(string key)
    {
        return key switch
        {
            "facts" => $"SELECT CAST(id AS TEXT) AS id, subject AS name, 'note' AS type, '' AS state, NULL AS dim, NULL AS conf, created AS updated, text AS text, 'note:' || id AS docref, NULL AS raptor FROM notes",
            "claims" or "beliefs" =>
                $"""
                 SELECT CAST(id AS TEXT) AS id, claimant || ': ' || substr(replace(replace(text, char(13), ' '), char(10), ' '), 1, 80) AS name, category AS type, status AS state,
                        {Dim("claims", "x")} AS dim, confidence AS conf, updated AS updated, text AS text, 'claim:' || id AS docref, NULL AS raptor
                 FROM claims {(key == "beliefs" ? "WHERE status IN ('verified','provisional')" : "")}
                 """,
            "personality" =>
                $"SELECT CAST(id AS TEXT) AS id, text AS name, section AS type, {(HasCol("self_profile", "scope") ? "scope" : "'main'")} AS state, NULL AS dim, strength AS conf, reinforced AS updated, text AS text, NULL AS docref, NULL AS raptor FROM self_profile",
            "lessons" => $"SELECT id AS id, title AS name, source AS type, '' AS state, NULL AS dim, NULL AS conf, updated AS updated, correction AS text, NULL AS docref, NULL AS raptor FROM knowledge_corrections",
            "routines" =>
                $"""
                 SELECT name AS id, name AS name, CASE WHEN json_valid(data) AND coalesce(json_extract(data, '$.script.code'), '') <> '' THEN 'script' ELSE 'routine' END AS type,
                        '' AS state, NULL AS dim, NULL AS conf, updated AS updated, data AS text, 'rt:' || lower(trim(name)) AS docref, NULL AS raptor
                 FROM routines
                 """,
            _ => null,
        };
    }

    /// <summary>Structures, places and observations (chests, gen cells, other places seen).</summary>
    private string? WorldSelect(string key)
    {
        return key switch
        {
            "structures" =>
                $"SELECT name AS id, name AS name, kind AS type, '' AS state, {Dim("structures", null)} AS dim, confidence AS conf, updated AS updated, data AS text, 'st:' || lower(trim(name)) AS docref, NULL AS raptor FROM structures",
            "places" =>
                $"SELECT name AS id, name AS name, 'waypoint' AS type, '' AS state, {Dim("waypoints", null)} AS dim, NULL AS conf, updated AS updated, x || ',' || y || ',' || z || coalesce('  ' || note, '') AS text, 'wp:' || lower(trim(name)) AS docref, NULL AS raptor FROM waypoints",
            "chests" or "gencells" or "observed" =>
                $"""
                 SELECT CAST(id AS TEXT) AS id, key AS name, kind AS type, CASE WHEN removed IS NOT NULL THEN 'removed' ELSE source END AS state, {Dim("observations", null)} AS dim,
                        confidence AS conf, last_seen AS updated, text || coalesce(char(10) || 'annotation: ' || annotation, '') AS text, NULL AS docref, NULL AS raptor
                 FROM observations WHERE {ObservationFilter(key)}
                 """,
            _ => null,
        };
    }

    /// <summary>Docs (episodes, recipes, knowledge), the activity log and the chat log.</summary>
    private string? EventSelect(string key)
    {
        return key switch
        {
            "episodes" or "recipes" or "knowledge" =>
                $"""
                 SELECT ref AS id, title AS name, coalesce(tag, kind) AS type, coalesce(source, '') AS state, {Dim("docs", "min_x")} AS dim, NULL AS conf, updated AS updated,
                        body AS text, ref AS docref, raptor_idx AS raptor
                 FROM docs WHERE {DocFilter(key)}
                 """,
            "activity" =>
                $"SELECT CAST(id AS TEXT) AS id, player || ' ' || kind AS name, category AS type, '' AS state, {Dim("activity_log", "x")} AS dim, confidence AS conf, at AS updated, text AS text, NULL AS docref, NULL AS raptor FROM activity_log",
            "chat" => $"SELECT CAST(id AS TEXT) AS id, coalesce(player, kind) AS name, kind AS type, '' AS state, NULL AS dim, NULL AS conf, at AS updated, text AS text, NULL AS docref, NULL AS raptor FROM chat_log",
            _ => null,
        };
    }

    /// <summary>The observations a kind lists: containers, cobble generators, or everything else.</summary>
    private static string ObservationFilter(string key) => key switch
    {
        "chests" => "kind IN " + ContainerKinds,
        "gencells" => "kind = 'cobble_generator'",
        _ => "kind NOT IN ('chest','trapped_chest','furnace','dispenser','cobble_generator')",
    };

    /// <summary>The docs a kind lists: episodes, game data, or knowledge and skills.</summary>
    private static string DocFilter(string key) => key switch
    {
        "episodes" => "kind = 'episode'",
        "recipes" => "kind = 'gamedata'",
        _ => "kind IN ('knowledge','skill')",
    };

    public MemoryKindsResponse KindsInfo()
    {
        var list = new List<MemoryKindInfoDto>();
        foreach (var k in Kinds)
        {
            var sel = Select(k.Key);
            long count = 0;
            IReadOnlyList<string> states = [];
            if (sel != null)
            {
                try
                {
                    count = _db.ScalarLong($"SELECT count(*) FROM ({sel})");
                    if (StatefulKinds.Contains(k.Key))
                        states = _db.Query($"SELECT DISTINCT state FROM ({sel}) WHERE state IS NOT NULL AND state <> '' ORDER BY state LIMIT 64", r => r.GetString(0));
                }
                catch (SqliteException) { }
            }
            list.Add(new MemoryKindInfoDto(k.Key, k.Label, k.Group, count, sel != null, k.EditKind, states));
        }
        return new MemoryKindsResponse(list);
    }

    private static readonly Dictionary<string, string> SortCols = new()
    {
        ["updated"] = "updated", ["name"] = "name COLLATE NOCASE", ["type"] = "type COLLATE NOCASE", ["state"] = "state COLLATE NOCASE",
        ["dim"] = "dim", ["conf"] = "conf", ["id"] = "CASE WHEN id GLOB '[0-9]*' THEN CAST(id AS INTEGER) END",
    };

    public MemoryPage? Page(string key, MemoryListQuery q)
    {
        if (Kind(key) == null) return null;
        var sel = Select(key);
        if (sel == null) return new MemoryPage(key, 0, q.Offset, q.Limit, []);
        var (filter, args) = Filter(q);
        var order = SortCols.GetValueOrDefault(q.Sort, "updated");
        var dir = q.Dir == "asc" ? "ASC" : "DESC";
        try
        {
            long total = _db.ScalarLong($"SELECT count(*) FROM ({sel}){filter}", args.ToArray());
            var page = new List<(string, object?)>(args) { ("$limit", q.Limit), ("$offset", q.Offset) };
            var rows = _db.Query($"SELECT * FROM ({sel}){filter} ORDER BY {order} {dir}, id {dir} LIMIT $limit OFFSET $offset",
                r => ToRow(key, r), page.ToArray());
            return new MemoryPage(key, total, q.Offset, q.Limit, rows);
        }
        catch (SqliteException) { return new MemoryPage(key, 0, q.Offset, q.Limit, []); }
    }

    /// <summary>The query's WHERE clause and its parameters: every word (up to 12) somewhere in the row, the dimension, the state.</summary>
    private static (string Filter, List<(string, object?)> Args) Filter(MemoryListQuery q)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        int w = 0;
        foreach (var word in (q.Q ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(12))
        {
            var p = "$w" + w++;
            where.Add($"instr(lower(id || ' ' || coalesce(name, '') || ' ' || coalesce(type, '') || ' ' || coalesce(state, '') || ' ' || coalesce(text, '')), {p}) > 0");
            args.Add((p, word));
        }
        if (q.Dim != null && DimFilters.TryGetValue(q.Dim, out var dim)) where.Add(dim);
        if (!string.IsNullOrEmpty(q.State)) { where.Add("state = $state"); args.Add(("$state", q.State)); }
        return (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "", args);
    }

    private static readonly Dictionary<string, string> DimFilters = new(StringComparer.Ordinal)
    {
        ["overworld"] = "dim = 0", ["nether"] = "dim = -1", ["none"] = "dim IS NULL",
    };

    private MemoryRowDto ToRow(string key, SqliteDataReader r)
    {
        string id = Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) ?? "";
        string name = AgentDb.StrOrNull(r, 1) ?? "";
        string? type = TextOrNull(r, 2);
        string? state = TextOrNull(r, 3);
        int? dim = r.IsDBNull(4) ? null : (int)r.GetInt64(4);
        double? conf = r.IsDBNull(5) ? null : r.GetDouble(5);
        long? updated = r.IsDBNull(6) ? null : r.GetInt64(6);
        string text = RowText(key, TextOrNull(r, 7) ?? "");
        string? docRef = ObservationRef(key, type, name, dim) ?? AgentDb.StrOrNull(r, 8);
        int? raptor = r.IsDBNull(9) ? null : (int)r.GetInt64(9);
        raptor ??= bank.NodeOfRef(docRef);
        return new MemoryRowDto(id, name, type, state, DimName(dim), conf, updated, Preview(text), docRef, raptor);
    }

    private static string? TextOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);

    /// <summary>An observation's doc ref (from its kind, key and dimension); null = not an observation kind or no kind.</summary>
    private static string? ObservationRef(string key, string? type, string name, int? dim) =>
        key is "chests" or "gencells" or "observed" && type != null ? ObservationStore.DocRef(type, name, dim ?? 0) : null;

    /// <summary>The grid text: a structure's note and steps, a routine's script or note, else as stored.</summary>
    private static string RowText(string key, string text) => key switch
    {
        "structures" => StructureText(text),
        "routines" => RoutineText(text),
        _ => text,
    };

    public static string DimName(int? d) => d switch { 0 => "overworld", -1 => "nether", _ => "" };

    public static string Preview(string text)
    {
        var one = text.ReplaceLineEndings(" ");
        return one.Length > 200 ? one[..199] + "…" : one;
    }

    private static Structure? ParseStructure(string json)
    {
        try { return JsonSerializer.Deserialize<Structure>(json, AtomicFile.Json); }
        catch (JsonException) { return null; }
    }

    private static string StructureText(string json)
    {
        if (ParseStructure(json) is not { } st) return json;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(st.Note)) sb.AppendLine(st.Note);
        foreach (var step in st.Steps) sb.AppendLine("• " + step);
        return sb.ToString().TrimEnd();
    }

    private static string RoutineText(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize<Routine>(json, AtomicFile.Json) is { } rt)
            {
                if (rt.Script is { Code.Length: > 0 } sc) return sc.Code;
                if (rt.Note is { Length: > 0 } n) return n;
            }
        }
        catch (JsonException) { }
        return json;
    }

    // ------------------------------------------------------------------ detail

    private static readonly (string Col, string Key)[] ClaimCols =
    [
        ("id", "id"), ("claimant", "claimant"), ("text", "text"), ("category", "category"), ("stakes", "stakes"), ("confidence", "confidence"), ("status", "status"),
        ("reason", "reason"), ("plan", "plan"), ("risk", "risk"), ("directive", "directive"), ("attempts", "attempts"), ("checked", "checked"), ("created", "created"),
        ("updated", "updated"), ("clone_id", "clone_id"), ("dimension", "dimension"), ("x", "x"), ("y", "y"), ("z", "z"),
    ];

    private static readonly (string Col, string Key)[] ObservationCols =
    [
        ("id", "id"), ("kind", "observation_kind"), ("key", "key"), ("min_x", "min_x"), ("min_y", "min_y"), ("min_z", "min_z"), ("max_x", "max_x"), ("max_y", "max_y"),
        ("max_z", "max_z"), ("text", "text"), ("source", "source"), ("confidence", "confidence"), ("annotation", "annotation"), ("first_seen", "first_seen"),
        ("last_seen", "last_seen"), ("seen_count", "seen_count"), ("removed", "removed"), ("dimension", "dimension"), ("contents_at", "contents_at"), ("contents_by", "contents_by"),
    ];

    private static readonly (string Col, string Key)[] DocCols =
    [
        ("id", "id"), ("ref", "ref"), ("kind", "doc_kind"), ("tag", "tag"), ("title", "title"), ("body", "body"), ("source", "source"), ("updated", "updated"),
        ("dimension", "dimension"), ("min_x", "min_x"), ("raptor_idx", "raptor_idx"),
    ];

    /// <summary>Raw record columns per kind → wire key (contracts memory.ts *Record), the id column and the base filter.</summary>
    private static readonly Dictionary<string, (string Table, string IdCol, string? Base, (string Col, string Key)[] Cols)> RecordDefs = new(StringComparer.Ordinal)
    {
        ["facts"] = ("notes", "id", null, [("id", "id"), ("subject", "subject"), ("text", "text"), ("created", "created")]),
        ["claims"] = ("claims", "id", null, ClaimCols),
        ["beliefs"] = ("claims", "id", "status IN ('verified','provisional')", ClaimCols),
        ["personality"] = ("self_profile", "id", null,
            [("id", "id"), ("section", "section"), ("player", "player"), ("text", "text"), ("strength", "strength"), ("created", "created"), ("reinforced", "reinforced"),
             ("reason", "reason"), ("scope", "scope"), ("observations", "observations")]),
        ["structures"] = ("structures", "name", null,
            [("name", "name"), ("kind", "structure_kind"), ("taught_by", "taught_by"), ("confidence", "confidence"), ("data", "data"), ("updated", "updated"), ("dimension", "dimension")]),
        ["places"] = ("waypoints", "name", null, [("name", "name"), ("x", "x"), ("y", "y"), ("z", "z"), ("note", "note"), ("updated", "updated"), ("dimension", "dimension")]),
        ["chests"] = ("observations", "id", ObservationFilter("chests"), ObservationCols),
        ["gencells"] = ("observations", "id", ObservationFilter("gencells"), ObservationCols),
        ["observed"] = ("observations", "id", ObservationFilter("observed"), ObservationCols),
        ["lessons"] = ("knowledge_corrections", "id", null, [("id", "id"), ("title", "title"), ("correction", "correction"), ("source", "source"), ("updated", "updated")]),
        ["episodes"] = ("docs", "ref", DocFilter("episodes"), DocCols),
        ["recipes"] = ("docs", "ref", DocFilter("recipes"), DocCols),
        ["knowledge"] = ("docs", "ref", DocFilter("knowledge"), DocCols),
        ["activity"] = ("activity_log", "id", null,
            [("id", "id"), ("at", "at"), ("player", "player"), ("kind", "activity_kind"), ("category", "category"), ("text", "text"), ("place_name", "place_name"),
             ("x", "x"), ("y", "y"), ("z", "z"), ("confidence", "confidence"), ("dimension", "dimension")]),
        ["chat"] = ("chat_log", "id", null, [("id", "id"), ("at", "at"), ("kind", "chat_kind"), ("player", "player"), ("text", "text")]),
        ["routines"] = ("routines", "name", null, [("name", "name"), ("data", "data"), ("updated", "updated")]),
    };

    private static (string Table, string IdCol, string? Base, (string Col, string Key)[] Cols)? RecordDef(string key) =>
        key is not null && RecordDefs.TryGetValue(key, out var d) ? d : null;

    private static readonly HashSet<string> TimeKeys = ["created", "updated", "checked", "reinforced", "first_seen", "last_seen", "removed", "contents_at", "at"];
    private static readonly HashSet<string> IntCoordKeys = ["min_x", "min_y", "min_z", "max_x", "max_y", "max_z"];

    public MemoryDetail? Detail(string key, string id)
    {
        var def = RecordDef(key);
        var sel = Select(key);
        if (def is not { } d || sel == null) return null;
        try
        {
            var row = _db.Query($"SELECT * FROM ({sel}) WHERE id = $id LIMIT 1", r => ToRow(key, r), ("$id", id)).FirstOrDefault();
            if (row == null) return null;
            if (Record(d, key, id) is not { } rec) return null;
            Fill(key, rec);
            AddRelated(key, rec, row);
            var props = Props(rec, row);
            var editKind = Kind(key)!.EditKind;
            var people = new[] { "player", "claimant", "subject" }.Select(p => rec.GetValueOrDefault(p) as string).OfType<string>()
                .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new MemoryDetail(key, id, row, props, rec, editKind, editKind != null ? Prefill(key, rec, row) : null, people);
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>The raw record: the kind's columns the db has, by wire key (box corners as whole numbers, structure data as JSON).</summary>
    private Dictionary<string, object?>? Record((string Table, string IdCol, string? Base, (string Col, string Key)[] Cols) d, string key, string id)
    {
        var present = d.Cols.Where(c => HasCol(d.Table, c.Col)).ToArray();
        var where = $"{d.IdCol} = $id" + (d.Base != null ? $" AND {d.Base}" : "");
        object idArg = d.IdCol == "id" && long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nid) && key != "lessons" ? nid : id;
        return _db.Query($"SELECT {string.Join(", ", present.Select(c => c.Col))} FROM {d.Table} WHERE {where} LIMIT 1", r =>
        {
            var m = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = key };
            for (int i = 0; i < present.Length; i++) m[present[i].Key] = RecordValue(present[i].Key, r.IsDBNull(i) ? null : r.GetValue(i));
            return m;
        }, ("$id", idArg)).FirstOrDefault();
    }

    private static object? RecordValue(string k, object? v)
    {
        if (v is double dbl && IntCoordKeys.Contains(k)) return (long)Math.Round(dbl);
        if (k == "data" && v is string json) return ParseJson(json);
        return v;
    }

    /// <summary>A claim's evidence, a chest's contents, a routine's type.</summary>
    private void AddRelated(string key, Dictionary<string, object?> rec, MemoryRowDto row)
    {
        if (key is "claims" or "beliefs") rec["evidence"] = Evidence(Convert.ToInt64(rec["id"], CultureInfo.InvariantCulture));
        if (key == "chests") rec["contents"] = Contents(Convert.ToInt64(rec["id"], CultureInfo.InvariantCulture));
        if (key == "routines") rec["routine_type"] = row.Type ?? "routine";
    }

    /// <summary>Contract-required fields an older db may lack.</summary>
    private static void Fill(string key, Dictionary<string, object?> rec)
    {
        switch (key)
        {
            case "claims" or "beliefs":
                rec.TryAdd("status", "unverified");
                break;
            case "personality":
                rec.TryAdd("scope", "main");
                rec.TryAdd("observations", 1L);
                break;
        }
    }

    private static object? ParseJson(string json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return json; }
    }

    private List<Dictionary<string, object?>> Evidence(long id) => !Has("claim_evidence") ? [] :
        _db.Query("SELECT at, kind, polarity, weight, ref, note FROM claim_evidence WHERE claim_id = $i ORDER BY at", r => new Dictionary<string, object?>
        {
            ["at"] = r.GetInt64(0), ["kind"] = r.GetString(1), ["polarity"] = r.GetInt64(2), ["weight"] = r.GetDouble(3),
            ["ref"] = AgentDb.StrOrNull(r, 4), ["note"] = AgentDb.StrOrNull(r, 5),
        }, ("$i", id));

    private List<Dictionary<string, object?>> Contents(long obsId) => !Has("container_items") ? [] :
        _db.Query("SELECT name, count, damage FROM container_items WHERE obs_id = $i ORDER BY count DESC", r => new Dictionary<string, object?>
        {
            ["name"] = r.GetString(0), ["count"] = r.GetInt64(1), ["damage"] = r.IsDBNull(2) ? null : r.GetInt64(2),
        }, ("$i", obsId));

    private static IReadOnlyList<MemoryProp> Props(Dictionary<string, object?> rec, MemoryRowDto row)
    {
        var list = new List<MemoryProp>();
        foreach (var (k, v) in rec)
            if (ScalarProp(k, v) is { } prop) list.Add(prop);
        if (rec.GetValueOrDefault("data") is JsonElement data && rec["kind"] as string == "structures"
            && ParseStructure(data.GetRawText()) is { } st)
            AddStructureProps(list, st);
        if (row.RaptorLeaf is { } leaf) list.Add(new("raptor_leaf", "RAPTOR leaf", "#" + leaf));
        return list;
    }

    /// <summary>One record field as a prop (the long text clipped at 400); null for the kind, body, evidence, contents, nulls and blanks.</summary>
    private static MemoryProp? ScalarProp(string k, object? v)
    {
        if (k is "kind" or "body" or "evidence" or "contents" || v == null) return null;
        string? text = PropText(k, v);
        if (string.IsNullOrEmpty(text)) return null;
        if (k == "text" && text.Length > 400) text = text[..399] + "…";
        return new MemoryProp(k, k.Replace('_', ' '), text);
    }

    /// <summary>A record value as a prop: times as UTC, doubles to 3 places, JSON left out (null), anything else as text.</summary>
    private static string? PropText(string k, object v) => v switch
    {
        long ms when TimeKeys.Contains(k) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("u", CultureInfo.InvariantCulture),
        double dbl => dbl.ToString("0.###", CultureInfo.InvariantCulture),
        JsonElement => null,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static void AddStructureProps(List<MemoryProp> list, Structure st)
    {
        if (st.Min is { } mn) list.Add(new("min", "min", mn.ToString()));
        if (st.Max is { } mx) list.Add(new("max", "max", mx.ToString()));
        foreach (var kb in st.KeyBlocks) list.Add(new("key_block", "key " + kb.Label, $"{kb.X},{kb.Y},{kb.Z}"));
        foreach (var h in st.History) list.Add(new("history", "history", h));
    }

    /// <summary>The edit form's starting values (ported from tools/MemoryExplorer/Services/EditPrefill.cs).</summary>
    private static Dictionary<string, string?> Prefill(string key, Dictionary<string, object?> rec, MemoryRowDto row)
    {
        var p = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (PrefillForm(key))
        {
            case "facts":
                p["subject"] = S(rec, "subject"); p["text"] = S(rec, "text");
                break;
            case "places":
                p["name"] = S(rec, "name"); p["x"] = S(rec, "x"); p["y"] = S(rec, "y"); p["z"] = S(rec, "z"); p["dim"] = DimOf(rec); p["note"] = SOrEmpty(rec, "note");
                break;
            case "structures":
                PrefillStructure(rec, p);
                break;
            case "claims":
                p["text"] = S(rec, "text"); p["claimant"] = S(rec, "claimant"); p["status"] = S(rec, "status"); p["confidence"] = Conf(rec, "confidence"); p["reason"] = SOrEmpty(rec, "reason");
                break;
            case "personality":
                p["section"] = S(rec, "section"); p["text"] = S(rec, "text"); p["player"] = SOrEmpty(rec, "player"); p["reason"] = "";
                break;
            case "lessons":
                p["title"] = S(rec, "title"); p["correction"] = S(rec, "correction"); p["patterns"] = "";
                break;
            case "observations":
                p["text"] = S(rec, "text"); p["annotation"] = SOrEmpty(rec, "annotation");
                break;
        }
        return p;
    }

    /// <summary>Kinds that share an edit form: beliefs are claims; chests, gen cells and observed places are observations.</summary>
    private static string PrefillForm(string key) => key switch
    {
        "beliefs" => "claims",
        "chests" or "gencells" or "observed" => "observations",
        _ => key,
    };

    private static void PrefillStructure(Dictionary<string, object?> rec, Dictionary<string, string?> p)
    {
        var st = rec.GetValueOrDefault("data") is JsonElement e ? ParseStructure(e.GetRawText()) : null;
        p["name"] = S(rec, "name"); p["kind"] = S(rec, "structure_kind"); p["note"] = st?.Note ?? "";
        p["confidence"] = Conf(rec, "confidence"); p["steps"] = st != null ? string.Join("\n", st.Steps) : "";
        p["min"] = st?.Min?.ToString() ?? ""; p["max"] = st?.Max?.ToString() ?? ""; p["dim"] = DimOf(rec);
    }

    private static string? S(Dictionary<string, object?> rec, string k) => rec.GetValueOrDefault(k) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private static string SOrEmpty(Dictionary<string, object?> rec, string k) => S(rec, k) ?? "";

    private static string DimOf(Dictionary<string, object?> rec) =>
        rec.GetValueOrDefault("dimension") is { } d && Convert.ToInt64(d, CultureInfo.InvariantCulture) == -1 ? "nether" : "overworld";

    private static string Conf(Dictionary<string, object?> rec, string k) =>
        rec.GetValueOrDefault(k) is double c ? c.ToString("0.00", CultureInfo.InvariantCulture) : S(rec, k) ?? "";
}
