using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using Microsoft.Data.Sqlite;

namespace MineClaw.Agent.Admin.Shards;

/// <summary>
/// Reads shard records straight from a db's tables (plain SELECTs, so it works on a read-only snapshot as well as the live
/// bot's file; every query tolerates a table or column an older db lacks) and evaluates a <see cref="ShardSelection"/> against
/// them. The same code enumerates the TARGET's records for an import diff, so both sides get identical ids and hashes.
/// </summary>
public sealed class ShardCollector
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AgentDb _db;
    private Dictionary<string, (string Hash, string? Source)>? _docs;

    public ShardCollector(AgentDb db) => _db = db;

    public AgentDb Db => _db;

    // ======================= stable ids =======================

    private static string Key(params string?[] parts) => ShardFile.Sha256(string.Join("\u001f", parts.Select(p => (p ?? "").Trim().ToLowerInvariant())))[..20];

    public static string FactId(string subject, string text) => "fact:" + Key(subject, text);
    public static string ClaimId(string norm, int dim) => "claim:" + Key(norm, dim.ToString(Inv));
    public static string RuleId(string player, string text) => "rule:" + Key(player, text);
    public static string TraitId(string section, string? player, string text) => "trait:" + Key(section, player, text);
    public static string LessonId(string id) => "lesson:" + id.Trim().ToLowerInvariant();
    public static string PlaceId(string name) => "place:" + name.Trim().ToLowerInvariant();
    public static string StructureId(string name) => "structure:" + name.Trim().ToLowerInvariant();

    // ======================= helpers =======================

    private bool Has(string table) =>
        _db.ScalarLong("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", table)) > 0;

    private bool HasCol(string table, string col) => Has(table) && _db.HasColumn(table, col);

    private List<T> Q<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        try { return _db.Query(sql, map, args); }
        catch (SqliteException) { return []; }
    }

    private Dictionary<string, (string Hash, string? Source)> Docs() => _docs ??= Q(
            "SELECT ref, hash, source FROM docs WHERE level = 0", r => (r.GetString(0), r.GetString(1), AgentDb.StrOrNull(r, 2)))
        .GroupBy(x => x.Item1, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (g.First().Item2, g.First().Item3), StringComparer.Ordinal);

    private string? DocHash(string reference) => Docs().TryGetValue(reference, out var d) ? d.Hash : null;

    private static string F(double v) => v.ToString("0.###", Inv);
    private static DateTimeOffset Ms(long ms) => AgentDb.FromUnixMs(ms);

    private static ShardRecord Make(string id, string kind, SortedDictionary<string, string?> fields, DateTimeOffset created, DateTimeOffset updated,
        string? reference, string? docHash, int? dim = null, string? source = null, string? clone = null, SortedDictionary<string, string?>? info = null) => new()
    {
        Id = id, Kind = kind, Fields = fields, Created = created, Updated = updated, Ref = reference, DocHash = docHash, Dimension = dim,
        Source = source, CloneId = clone, Info = info, Hash = ShardFile.RecordHash(kind, fields),
    };

    // ======================= per kind =======================

    /// <summary>Every record of a kind in this db (world and rule kinds included: the flags are applied by the selection).</summary>
    public IReadOnlyList<ShardRecord> All(string kind) => kind switch
    {
        ShardKinds.Fact => Facts(),
        ShardKinds.Claim => Claims(),
        ShardKinds.Rule => Rules(),
        ShardKinds.Trait => Traits(),
        ShardKinds.Lesson => Lessons(),
        ShardKinds.Place => Places(),
        ShardKinds.Structure => Structures(),
        ShardKinds.Episode or ShardKinds.Knowledge or ShardKinds.GameData => DocRecords(kind),
        _ => [],
    };

    private List<ShardRecord> Facts()
    {
        if (!Has("notes")) return [];
        bool clone = HasCol("notes", "clone_id");
        return Q($"SELECT id, subject, text, created{(clone ? ", clone_id" : "")} FROM notes ORDER BY id", r =>
        {
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal) { ["subject"] = r.GetString(1), ["text"] = r.GetString(2) };
            var at = Ms(r.GetInt64(3));
            var reference = "note:" + r.GetInt64(0).ToString(Inv);
            return Make(FactId(r.GetString(1), r.GetString(2)), ShardKinds.Fact, fields, at, at, reference, DocHash(reference), null, "memory",
                clone ? AgentDb.StrOrNull(r, 4) : null);
        });
    }

    private List<ShardRecord> Claims()
    {
        if (!Has("claims")) return [];
        bool hasDim = HasCol("claims", "dimension");
        var sources = Q("SELECT claim_id, claimant FROM claim_sources ORDER BY at, claimant", r => (r.GetInt64(0), r.GetString(1)))
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Select(x => x.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return Q($"""
            SELECT id, claimant, text, norm, status, confidence, reason, category, created, updated, clone_id{(hasDim ? ", dimension" : "")}
            FROM claims WHERE directive = 0 ORDER BY id
            """, r =>
        {
            long id = r.GetInt64(0);
            int dim = hasDim ? (int)r.GetInt64(11) : 0;
            var claimants = sources.TryGetValue(id, out var s) && s.Count > 0 ? s : [r.GetString(1)];
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["claimant"] = r.GetString(1), ["claimants"] = string.Join(",", claimants), ["text"] = r.GetString(2),
                ["status"] = r.GetString(4), ["confidence"] = F(r.GetDouble(5)), ["reason"] = AgentDb.StrOrNull(r, 6), ["category"] = r.GetString(7),
            };
            var reference = "claim:" + id.ToString(Inv);
            return Make(ClaimId(r.GetString(3), dim), ShardKinds.Claim, fields, Ms(r.GetInt64(8)), Ms(r.GetInt64(9)), reference, DocHash(reference),
                hasDim ? dim : null, "belief", r.GetString(10));
        });
    }

    private List<ShardRecord> Rules()
    {
        if (!Has("directives")) return [];
        return Q("SELECT id, player, text, scope, topics, created, updated, clone_id FROM directives WHERE active = 1 ORDER BY id", r =>
        {
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["player"] = r.GetString(1), ["text"] = r.GetString(2), ["scope"] = r.GetString(3), ["topics"] = r.GetString(4),
            };
            var reference = "rule:" + r.GetInt64(0).ToString(Inv);
            return Make(RuleId(r.GetString(1), r.GetString(2)), ShardKinds.Rule, fields, Ms(r.GetInt64(5)), Ms(r.GetInt64(6)), reference, DocHash(reference),
                null, "rule", r.GetString(7));
        });
    }

    private List<ShardRecord> Traits()
    {
        if (!Has("self_profile")) return [];
        bool scoped = HasCol("self_profile", "scope");
        return Q($"SELECT id, section, player, text, strength, reason, created, reinforced FROM self_profile{(scoped ? " WHERE scope = 'main'" : "")} ORDER BY id", r =>
        {
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["section"] = r.GetString(1), ["player"] = AgentDb.StrOrNull(r, 2), ["text"] = r.GetString(3),
            };
            var info = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["strength"] = F(r.GetDouble(4)), ["reason"] = AgentDb.StrOrNull(r, 5),
            };
            // Ref "self#id" names the row in this db (traits have no per-entry doc).
            return Make(TraitId(r.GetString(1), AgentDb.StrOrNull(r, 2), r.GetString(3)), ShardKinds.Trait, fields, Ms(r.GetInt64(6)), Ms(r.GetInt64(7)),
                "self#" + r.GetInt64(0).ToString(Inv), null, null, "self", null, info);
        });
    }

    private List<ShardRecord> Lessons()
    {
        if (!Has("knowledge_corrections")) return [];
        return Q("SELECT id, title, correction, patterns, updated FROM knowledge_corrections WHERE source = 'admin' ORDER BY id", r =>
        {
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["id"] = r.GetString(0), ["title"] = r.GetString(1), ["correction"] = r.GetString(2), ["patterns"] = r.GetString(3),
            };
            var reference = KnowledgeCorrection.RefPrefix + r.GetString(0);
            var at = Ms(r.GetInt64(4));
            return Make(LessonId(r.GetString(0)), ShardKinds.Lesson, fields, at, at, reference, DocHash(reference), null, "admin");
        });
    }

    private List<ShardRecord> Places()
    {
        if (!Has("waypoints")) return [];
        bool hasDim = HasCol("waypoints", "dimension");
        return Q($"SELECT name, x, y, z, note, updated{(hasDim ? ", dimension" : "")} FROM waypoints ORDER BY name", r =>
        {
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["name"] = r.GetString(0), ["x"] = r.GetInt64(1).ToString(Inv), ["y"] = r.GetInt64(2).ToString(Inv), ["z"] = r.GetInt64(3).ToString(Inv),
                ["note"] = AgentDb.StrOrNull(r, 4),
            };
            int dim = hasDim ? (int)r.GetInt64(6) : 0;
            var reference = "wp:" + r.GetString(0).Trim().ToLowerInvariant();
            var at = Ms(r.GetInt64(5));
            return Make(PlaceId(r.GetString(0)), ShardKinds.Place, fields, at, at, reference, DocHash(reference), dim, "memory");
        });
    }

    private List<ShardRecord> Structures()
    {
        if (!Has("structures")) return [];
        bool hasDim = HasCol("structures", "dimension");
        return Q($"SELECT name, kind, taught_by, confidence, data, updated{(hasDim ? ", dimension" : "")} FROM structures ORDER BY name", r =>
        {
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["name"] = r.GetString(0), ["kind"] = r.GetString(1), ["taught_by"] = AgentDb.StrOrNull(r, 2), ["data"] = r.GetString(4),
            };
            int dim = hasDim ? (int)r.GetInt64(6) : 0;
            var reference = "st:" + r.GetString(0).Trim().ToLowerInvariant();
            var at = Ms(r.GetInt64(5));
            return Make(StructureId(r.GetString(0)), ShardKinds.Structure, fields, at, at, reference, DocHash(reference), dim, "memory");
        });
    }

    /// <summary>Plain docs: live episodes (no test, journal or personality-log docs), knowledge chunks (kb:*), seed game data.</summary>
    private List<ShardRecord> DocRecords(string kind)
    {
        var where = kind switch
        {
            ShardKinds.Episode => "d.kind = 'episode' AND substr(d.ref, 1, 3) = 'ep:' AND (d.source IS NULL OR d.source NOT IN ('test', 'self'))",
            ShardKinds.Knowledge => "d.kind = 'knowledge' AND substr(d.ref, 1, 3) = 'kb:'",
            _ => "d.kind = 'gamedata'",
        };
        bool clone = HasCol("docs", "clone_id");
        return Q($"""
            SELECT d.ref, d.kind, d.tag, d.title, d.body, d.source, d.weight, d.embed, d.created, d.updated,
                   d.min_x, d.min_y, d.min_z, d.max_x, d.max_y, d.max_z, d.dimension, d.hash{(clone ? ", d.clone_id" : "")}
            FROM docs d WHERE d.level = 0 AND {where} ORDER BY d.id
            """, r =>
        {
            string? bounds = r.IsDBNull(10) ? null
                : string.Join(",", Enumerable.Range(10, 6).Select(i => r.GetDouble(i).ToString("0.###", Inv)));
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ref"] = r.GetString(0), ["kind"] = r.GetString(1), ["tag"] = AgentDb.StrOrNull(r, 2), ["title"] = r.GetString(3), ["body"] = r.GetString(4),
                ["source"] = AgentDb.StrOrNull(r, 5), ["weight"] = F(r.GetDouble(6)), ["embed"] = r.GetInt64(7) != 0 ? "1" : "0", ["bounds"] = bounds,
            };
            return Make(r.GetString(0), kind, fields, Ms(r.GetInt64(8)), Ms(r.GetInt64(9)), r.GetString(0), r.GetString(17),
                bounds != null ? (int)r.GetInt64(16) : null, AgentDb.StrOrNull(r, 5), clone ? AgentDb.StrOrNull(r, 18) : null);
        });
    }

    // ======================= doc ref → record =======================

    private Dictionary<string, ShardRecord>? _byRef;

    /// <summary>The record a doc ref belongs to (RAPTOR leaves → their source rows), or null (observations, summaries...).</summary>
    public ShardRecord? ForRef(string reference)
    {
        _byRef ??= ShardKinds.All.SelectMany(All).Where(r => r.Ref != null)
            .GroupBy(r => r.Ref!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return _byRef.GetValueOrDefault(reference);
    }

    // ======================= selection =======================

    /// <summary>
    /// The records a selection matches in THIS db. <paramref name="semanticRefs"/>: for a semantic query, the doc refs the bot's
    /// own hybrid search returned (the caller ran it); records whose doc is among them match.
    /// </summary>
    public List<ShardRecord> Select(ShardSelection s, IReadOnlyCollection<string>? semanticRefs = null)
    {
        bool Allowed(string kind) => (!ShardKinds.IsWorld(kind) || s.IncludeWorld) && (kind != ShardKinds.Rule || s.IncludeRules);
        IEnumerable<ShardRecord> result;
        switch (s.Mode)
        {
            case "subtree":
            {
                var refs = s.LeafRefs ?? [];
                result = refs.Distinct(StringComparer.Ordinal).Select(ForRef).OfType<ShardRecord>().Where(r => Allowed(r.Kind));
                break;
            }
            case "person":
            {
                var names = PersonNames(s.People);
                if (names.Count == 0) throw new ArgumentException("a person shard needs at least one name");
                var rx = MentionRegex(names);
                result = ShardKinds.All.Where(Allowed).Where(k => k is not (ShardKinds.Knowledge or ShardKinds.GameData or ShardKinds.Lesson))
                    .SelectMany(All).Where(r => AboutPerson(r, names, rx));
                break;
            }
            case "pack":
                result = All(ShardKinds.GameData).Concat(s.IncludeKnowledgeDocs ? All(ShardKinds.Knowledge) : []);
                break;
            default:
            {
                var kinds = s.Kinds is { Count: > 0 } ks ? ks.Select(ShardKinds.Normalize).OfType<string>().Distinct().ToList() : ShardKinds.All.ToList();
                result = kinds.Where(Allowed).SelectMany(All);
                if (s.Dimension is { } d) result = result.Where(r => r.Dimension == d);
                if (!string.IsNullOrWhiteSpace(s.Query))
                {
                    if (s.Semantic && semanticRefs != null)
                    {
                        var set = semanticRefs as IReadOnlySet<string> ?? semanticRefs.ToHashSet(StringComparer.Ordinal);
                        result = result.Where(r => r.Ref != null && set.Contains(r.Ref));
                    }
                    else
                    {
                        var terms = s.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        result = result.Where(r => Haystack(r) is var h && terms.All(t => h.Contains(t, StringComparison.Ordinal)));
                    }
                }
                break;
            }
        }
        return result.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    private static string Haystack(ShardRecord r) => string.Join(" ", r.Fields.Values.Where(v => v != null)).ToLowerInvariant();

    /// <summary>The names to look for: each given name plus its configured nicknames (login ↔ nicknames both ways).</summary>
    public static List<string> PersonNames(IEnumerable<string>? given, IReadOnlyDictionary<string, string[]>? nicknames = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in given ?? [])
            foreach (var part in n.Split([',', '/', ';'], StringSplitOptions.RemoveEmptyEntries))
                if (part.Trim() is { Length: >= 2 } p) set.Add(p);
        if (nicknames != null)
            foreach (var (login, nicks) in nicknames)
                if (set.Contains(login) || nicks.Any(set.Contains))
                {
                    set.Add(login);
                    foreach (var k in nicks) set.Add(k);
                }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Regex MentionRegex(IReadOnlyList<string> names) =>
        new(@"(?<![A-Za-z0-9_])(?:" + string.Join("|", names.Where(n => n.Length >= 3).Select(Regex.Escape)) + @")(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool Is(string? value, IReadOnlyList<string> names) => value != null && names.Any(n => n.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool Mentions(Regex rx, params string?[] texts) => texts.Any(t => t != null && rx.IsMatch(t));

    /// <summary>Mentioning or attributed to the person: facts about them, their claims, their rules, relationship entries, episodes with them.</summary>
    private static bool AboutPerson(ShardRecord r, IReadOnlyList<string> names, Regex rx) => r.Kind switch
    {
        ShardKinds.Fact => Is(r.Get("subject"), names) || Mentions(rx, r.Get("text")),
        ShardKinds.Claim => (r.Get("claimants") ?? r.Get("claimant") ?? "").Split(',').Any(c => Is(c, names)) || Mentions(rx, r.Get("text")),
        ShardKinds.Rule => Is(r.Get("player"), names),
        ShardKinds.Trait => Is(r.Get("player"), names) || Mentions(rx, r.Get("text")),
        ShardKinds.Place => Mentions(rx, r.Get("name"), r.Get("note")),
        ShardKinds.Structure => Is(r.Get("taught_by"), names) || Mentions(rx, r.Get("name")),
        ShardKinds.Episode => Mentions(rx, r.Get("title"), r.Get("body")),
        _ => false,
    };

    /// <summary>Doc refs of every RAPTOR leaf under a node (deduplicated across soft-cluster parents).</summary>
    public static List<string> LeafRefsUnder(RaptorMemory raptor, int node) => raptor.Index.Read(t => LeafRefsUnder(t, node));

    public static List<string> LeafRefsUnder(MineClaw.Raptor.Model.RaptorTree t, int node)
    {
        if (!t.TryGet(node, out _)) throw new ArgumentException($"no node #{node} in the tree");
        var keys = new List<string>();
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (!seen.Add(i) || !t.TryGet(i, out var n)) continue;
            if (n.IsLeaf) { if (n.Key != null) keys.Add(n.Key); }
            else foreach (var c in n.Children) stack.Push(c);
        }
        return keys.Distinct(StringComparer.Ordinal).ToList();
    }
}
