using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>A browsable memory kind (one table in the browser).</summary>
public sealed record MemoryKindInfo(string Key, string Label, string Group, string Description, string? EditKind = null);

/// <summary>One row of the memory browser, whatever its kind. <see cref="Props"/> drive the detail pane.</summary>
public sealed class MemoryRow
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = "";
    public string State { get; init; } = "";
    public int? Dimension { get; init; }
    public double? Confidence { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public string Text { get; init; } = "";
    public string? DocRef { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Props { get; init; } = [];
    /// <summary>Lower-cased haystack for the text filter.</summary>
    public string Search => _search ??= $"{Id} {Name} {Type} {State} {Text}".ToLowerInvariant();
    private string? _search;

    public string Preview => Text.Length > 160 ? Text[..160].ReplaceLineEndings(" ") + "…" : Text.ReplaceLineEndings(" ");
    public string DimensionText => Dimension switch { null => "", 0 => "overworld", -1 => "nether", var d => d.ToString()! };
    public string ConfidenceText => Confidence is { } c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "";
    public string UpdatedText => Updated is { } u ? u.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
    /// <summary>Sort key for the Updated column (DataGrid sorts on this).</summary>
    public long UpdatedSort => Updated?.ToUnixTimeMilliseconds() ?? 0;
}

public sealed record CountCard(string Label, long Count, string Detail, string Group);

/// <summary>
/// Read-only queries over the bot's db for the browser, overview and personality views. Plain SELECTs on the bot's own
/// tables (schema owned by AgentDb + the stores); every query tolerates a table missing from an older db.
/// </summary>
public sealed class MemoryQueries(AgentDb db)
{
    public static readonly IReadOnlyList<MemoryKindInfo> Kinds =
    [
        new("facts", "Facts", "Memory", "Notes the bot saved about players and the world (MemoryStore notes).", "fact"),
        new("claims", "Claims", "Beliefs", "Everything players told the bot, with its verification state.", "claim"),
        new("beliefs", "Beliefs", "Beliefs", "Claims the bot holds as true: verified or provisional.", "claim"),
        new("personality", "Self-profile", "Self", "Traits, likes, opinions, relationships and milestones of the grown personality.", "trait"),
        new("structures", "Structures", "World", "Builds the bot knows how to find and use (taught or found).", "structure"),
        new("places", "Places", "World", "Named waypoints.", "place"),
        new("chests", "Chests", "World", "Containers and their last-seen contents.", "observation"),
        new("gencells", "Gen cells", "World", "Cobblestone generator cells.", "observation"),
        new("observed", "Observed places", "World", "Islands, farms, trees, hangouts, hazards and unknown builds the indexer saw.", "observation"),
        new("lessons", "Lessons", "Memory", "Stale-lesson corrections that supersede wrong memories.", "lesson"),
        new("episodes", "Episodes", "Events", "Mechanical episode summaries (what happened, when, where)."),
        new("activity", "Activity log", "Events", "Player activity the bot noticed (mined, placed, crafted…)."),
        new("chat", "Chat log", "Events", "Chat lines the bot heard and said."),
        new("routines", "Routines & scripts", "Memory", "Saved routines and Lua scripts."),
        new("recipes", "Recipes & game data", "Knowledge", "Seeded b1.8.1 recipes, smelting, drops and block facts."),
        new("knowledge", "Knowledge & skills", "Knowledge", "Knowledge-doc chunks and the skill catalogue."),
    ];

    public AgentDb Db => db;

    public bool Has(string table) =>
        db.ScalarLong("SELECT count(*) FROM sqlite_master WHERE type IN ('table','view') AND name = $n", ("$n", table)) > 0;

    private bool HasCol(string table, string col) => Has(table) && db.HasColumn(table, col);

    private long Count(string sql, params (string, object?)[] args)
    {
        try { return db.ScalarLong(sql, args); }
        catch (SqliteException) { return 0; }
    }

    // ------------------------------------------------------------------ overview

    public IReadOnlyList<CountCard> Counts()
    {
        var cards = new List<CountCard>();
        void Add(string label, string table, string sql, string detail, string group)
        {
            if (Has(table)) cards.Add(new CountCard(label, Count(sql), detail, group));
        }
        Add("Facts", "notes", "SELECT count(*) FROM notes", "notes about players and the world", "Memory");
        Add("Recipes", "docs", "SELECT count(*) FROM docs WHERE kind = 'gamedata' AND tag IN ('recipe','smelt')", "seeded crafting + smelting", "Knowledge");
        if (Has("claims"))
        {
            var by = db.Query("SELECT status, count(*) FROM claims GROUP BY status ORDER BY 2 DESC", r => $"{r.GetInt64(1)} {r.GetString(0)}");
            cards.Add(new CountCard("Claims", Count("SELECT count(*) FROM claims"), by.Count == 0 ? "none yet" : string.Join(" · ", by), "Beliefs"));
            cards.Add(new CountCard("Beliefs", Count("SELECT count(*) FROM claims WHERE status IN ('verified','provisional')"), "verified or provisional claims", "Beliefs"));
        }
        Add("Episodes", "docs", "SELECT count(*) FROM docs WHERE kind = 'episode'", "event summaries", "Events");
        Add("Activity", "activity_log", "SELECT count(*) FROM activity_log", "player actions noticed", "Events");
        Add("Chat lines", "chat_log", "SELECT count(*) FROM chat_log", "heard and said", "Events");
        Add("Structures", "structures", "SELECT count(*) FROM structures", "known builds", "World");
        Add("Places", "waypoints", "SELECT count(*) FROM waypoints", "named waypoints", "World");
        if (Has("observations"))
        {
            bool hasRemoved = HasCol("observations", "removed");
            var live = hasRemoved ? " WHERE removed IS NULL" : "";
            cards.Add(new CountCard("World observations", Count("SELECT count(*) FROM observations" + live),
                string.Join(" · ", db.Query($"SELECT kind, count(*) FROM observations{live} GROUP BY kind ORDER BY 2 DESC LIMIT 4", r => $"{r.GetInt64(1)} {r.GetString(0)}")), "World"));
        }
        Add("Remembered chunks", "world_chunks", "SELECT count(*) FROM world_chunks", "columns of world memory", "World");
        if (Has("self_profile"))
            cards.Add(new CountCard("Personality traits", Count("SELECT count(*) FROM self_profile"),
                string.Join(" · ", db.Query("SELECT section, count(*) FROM self_profile GROUP BY section ORDER BY 2 DESC", r => $"{r.GetInt64(1)} {r.GetString(0)}")), "Self"));
        Add("Lessons", "knowledge_corrections", "SELECT count(*) FROM knowledge_corrections", "superseding corrections", "Memory");
        Add("Routines", "routines", "SELECT count(*) FROM routines", "routines + scripts", "Memory");
        Add("Docs indexed", "docs", "SELECT count(*) FROM docs", "every searchable doc (FTS)", "Index");
        Add("Vectors", "vectors", "SELECT count(*) FROM vectors", "stored embeddings", "Index");
        return cards;
    }

    public IReadOnlyList<(string Kind, long Count)> DocKinds() =>
        db.Query("SELECT kind, count(*) FROM docs GROUP BY kind ORDER BY 2 DESC", r => (r.GetString(0), r.GetInt64(1)));

    public (string Model, int Dims, long Count)? VectorModel() =>
        Has("vectors") ? db.Query("SELECT model, dims, count(*) FROM vectors GROUP BY model, dims ORDER BY 3 DESC LIMIT 1",
            r => (r.GetString(0), (int)r.GetInt64(1), r.GetInt64(2))).Cast<(string, int, long)?>().FirstOrDefault() : null;

    // ------------------------------------------------------------------ browser rows

    public IReadOnlyList<MemoryRow> Rows(string kind)
    {
        try
        {
            return kind switch
            {
                "facts" => Facts(),
                "claims" => Claims(null),
                "beliefs" => Claims("status IN ('verified','provisional')"),
                "personality" => SelfEntries(null),
                "structures" => Structures(),
                "places" => Places(),
                "chests" => Observations("kind IN ('chest','trapped_chest','furnace','dispenser')"),
                "gencells" => Observations("kind = 'cobble_generator'"),
                "observed" => Observations("kind NOT IN ('chest','trapped_chest','furnace','dispenser','cobble_generator')"),
                "lessons" => Lessons(),
                "episodes" => Docs("kind = 'episode'", "episodes"),
                "recipes" => Docs("kind = 'gamedata'", "recipes"),
                "knowledge" => Docs("kind IN ('knowledge','skill')", "knowledge"),
                "activity" => Activity(),
                "chat" => Chat(),
                "routines" => Routines(),
                _ => [],
            };
        }
        catch (SqliteException) { return []; }
    }

    private static List<KeyValuePair<string, string>> P(params (string K, object? V)[] kv) =>
        kv.Where(x => x.V is not null && x.V.ToString() is { Length: > 0 }).Select(x => new KeyValuePair<string, string>(x.K, Convert.ToString(x.V, CultureInfo.InvariantCulture)!)).ToList();

    private static DateTimeOffset Ms(long ms) => AgentDb.FromUnixMs(ms);

    private IReadOnlyList<MemoryRow> Facts()
    {
        if (!Has("notes")) return [];
        return db.Query("SELECT id, subject, text, created FROM notes ORDER BY id DESC", r => new MemoryRow
        {
            Kind = "facts", Id = r.GetInt64(0).ToString(CultureInfo.InvariantCulture), Name = r.GetString(1), Type = "note", Text = r.GetString(2),
            Updated = Ms(r.GetInt64(3)), DocRef = "note:" + r.GetInt64(0),
            Props = P(("id", r.GetInt64(0)), ("subject", r.GetString(1)), ("created", Ms(r.GetInt64(3)).ToLocalTime().ToString("u"))),
        });
    }

    private IReadOnlyList<MemoryRow> Claims(string? where)
    {
        if (!Has("claims")) return [];
        bool dim = HasCol("claims", "dimension");
        var sql = $"""
            SELECT id, claimant, text, category, stakes, confidence, status, reason, plan, risk, directive, attempts, checked, created, updated, clone_id,
                   {(dim ? "dimension" : "0")}, x, y, z
            FROM claims {(where != null ? "WHERE " + where : "")} ORDER BY updated DESC
            """;
        return db.Query(sql, r =>
        {
            long id = r.GetInt64(0);
            string? at = r.IsDBNull(17) ? null : $"{r.GetInt64(17)},{r.GetInt64(18)},{r.GetInt64(19)}";
            return new MemoryRow
            {
                Kind = "claims", Id = id.ToString(CultureInfo.InvariantCulture), Name = $"{r.GetString(1)}: {TextOne(r.GetString(2), 80)}", Type = r.GetString(3),
                State = r.GetString(6), Confidence = r.GetDouble(5), Updated = Ms(r.GetInt64(14)), Text = r.GetString(2), DocRef = "claim:" + id,
                Dimension = at != null ? (int)r.GetInt64(16) : null,
                Props = P(("id", id), ("claimant", r.GetString(1)), ("status", r.GetString(6)), ("reason", AgentDb.StrOrNull(r, 7)), ("category", r.GetString(3)),
                    ("stakes", r.GetString(4)), ("confidence", r.GetDouble(5).ToString("0.00", CultureInfo.InvariantCulture)), ("check plan", r.GetString(8)),
                    ("risk", r.GetString(9)), ("directive", r.GetInt64(10) != 0 ? "yes" : null), ("attempts", r.GetInt64(11)), ("at", at),
                    ("last checked", r.IsDBNull(12) ? null : Ms(r.GetInt64(12)).ToLocalTime().ToString("u")), ("clone", r.GetString(15)),
                    ("created", Ms(r.GetInt64(13)).ToLocalTime().ToString("u"))),
            };
        });
    }

    /// <summary>Evidence rows of one claim (detail pane).</summary>
    public IReadOnlyList<string> ClaimEvidence(long id) =>
        Has("claim_evidence")
            ? db.Query("SELECT at, kind, polarity, weight, ref, note FROM claim_evidence WHERE claim_id = $i ORDER BY at", r =>
                $"{Ms(r.GetInt64(0)).ToLocalTime():MM-dd HH:mm}  {r.GetString(1)} {(r.GetInt64(2) > 0 ? "supports" : r.GetInt64(2) < 0 ? "refutes" : "neutral")} (w {r.GetDouble(3):0.00})" +
                (AgentDb.StrOrNull(r, 4) is { } rf ? $" [{rf}]" : "") + (AgentDb.StrOrNull(r, 5) is { } n ? $": {n}" : ""), ("$i", id))
            : [];

    public IReadOnlyList<MemoryRow> SelfEntries(string? scope)
    {
        if (!Has("self_profile")) return [];
        bool hasScope = HasCol("self_profile", "scope"), hasObs = HasCol("self_profile", "observations");
        var sql = $"""
            SELECT id, section, player, text, strength, created, reinforced, reason, {(hasScope ? "scope" : "'main'")}, {(hasObs ? "observations" : "1")}
            FROM self_profile {(scope != null && hasScope ? "WHERE scope = $s" : "")} ORDER BY section, strength DESC
            """;
        return db.Query(sql, r => new MemoryRow
        {
            Kind = "personality", Id = r.GetInt64(0).ToString(CultureInfo.InvariantCulture), Name = r.GetString(3), Type = r.GetString(1),
            State = r.GetString(8), Confidence = r.GetDouble(4), Updated = Ms(r.GetInt64(6)), Text = r.GetString(3),
            Props = P(("id", r.GetInt64(0)), ("section", r.GetString(1)), ("player", AgentDb.StrOrNull(r, 2)), ("strength", r.GetDouble(4).ToString("0.00", CultureInfo.InvariantCulture)),
                ("observations", r.GetInt64(9)), ("scope", r.GetString(8)), ("reason", AgentDb.StrOrNull(r, 7)),
                ("created", Ms(r.GetInt64(5)).ToLocalTime().ToString("u")), ("reinforced", Ms(r.GetInt64(6)).ToLocalTime().ToString("u"))),
        }, ("$s", scope));
    }

    private IReadOnlyList<MemoryRow> Structures()
    {
        if (!Has("structures")) return [];
        bool dim = HasCol("structures", "dimension");
        return db.Query($"SELECT name, kind, taught_by, confidence, data, updated, {(dim ? "dimension" : "0")} FROM structures ORDER BY updated DESC", r =>
        {
            var props = P(("name", r.GetString(0)), ("kind", r.GetString(1)), ("taught by", AgentDb.StrOrNull(r, 2)),
                ("confidence", r.GetDouble(3).ToString("0.00", CultureInfo.InvariantCulture)));
            var text = new StringBuilder();
            // The bot's own model and serializer options (camelCase, MemoryStore.SaveStructure).
            Structure? st = null;
            try { st = JsonSerializer.Deserialize<Structure>(r.GetString(4), AtomicFile.Json); }
            catch (JsonException) { }
            if (st != null)
            {
                if (st.Min is { } mn) props.Add(new("min", mn.ToString()));
                if (st.Max is { } mx) props.Add(new("max", mx.ToString()));
                if (!string.IsNullOrWhiteSpace(st.Note)) text.AppendLine(st.Note);
                foreach (var step in st.Steps) text.AppendLine("• " + step);
                foreach (var k in st.KeyBlocks) props.Add(new("key " + k.Label, $"{k.X},{k.Y},{k.Z}"));
                foreach (var h in st.History) props.Add(new("history", h));
            }
            else text.Append(r.GetString(4));
            return new MemoryRow
            {
                Kind = "structures", Id = r.GetString(0), Name = r.GetString(0), Type = r.GetString(1), Confidence = r.GetDouble(3), Updated = Ms(r.GetInt64(5)),
                Dimension = (int)r.GetInt64(6), Text = text.ToString().TrimEnd(), Props = props, DocRef = "st:" + r.GetString(0).Trim().ToLowerInvariant(),
            };
        });
    }

    private IReadOnlyList<MemoryRow> Places()
    {
        if (!Has("waypoints")) return [];
        bool dim = HasCol("waypoints", "dimension");
        return db.Query($"SELECT name, x, y, z, note, updated, {(dim ? "dimension" : "0")} FROM waypoints ORDER BY name", r => new MemoryRow
        {
            Kind = "places", Id = r.GetString(0), Name = r.GetString(0), Type = "waypoint", Text = $"{r.GetInt64(1)},{r.GetInt64(2)},{r.GetInt64(3)}" + (AgentDb.StrOrNull(r, 4) is { } n ? $"  {n}" : ""),
            Updated = Ms(r.GetInt64(5)), Dimension = (int)r.GetInt64(6), DocRef = "wp:" + r.GetString(0).Trim().ToLowerInvariant(),
            Props = P(("name", r.GetString(0)), ("x", r.GetInt64(1)), ("y", r.GetInt64(2)), ("z", r.GetInt64(3)), ("note", AgentDb.StrOrNull(r, 4))),
        });
    }

    private IReadOnlyList<MemoryRow> Observations(string where)
    {
        if (!Has("observations")) return [];
        bool dim = HasCol("observations", "dimension"), contents = HasCol("observations", "contents_at");
        var sql = $"""
            SELECT id, kind, key, min_x, min_y, min_z, max_x, max_y, max_z, text, source, confidence, annotation, first_seen, last_seen, seen_count, removed,
                   {(dim ? "dimension" : "0")}, {(contents ? "contents_at, contents_by" : "NULL, NULL")}
            FROM observations WHERE {where} ORDER BY last_seen DESC
            """;
        var rows = db.Query(sql, r =>
        {
            string? box = r.IsDBNull(3) ? null : $"{r.GetDouble(3):0},{r.GetDouble(4):0},{r.GetDouble(5):0}" +
                (r.GetDouble(3) == r.GetDouble(6) && r.GetDouble(4) == r.GetDouble(7) && r.GetDouble(5) == r.GetDouble(8) ? "" : $"..{r.GetDouble(6):0},{r.GetDouble(7):0},{r.GetDouble(8):0}");
            bool removed = !r.IsDBNull(16);
            int d = (int)r.GetInt64(17);
            return new MemoryRow
            {
                Kind = "observation", Id = r.GetInt64(0).ToString(CultureInfo.InvariantCulture), Name = r.GetString(2), Type = r.GetString(1),
                State = removed ? "removed" : r.GetString(10), Confidence = r.GetDouble(11), Updated = Ms(r.GetInt64(14)), Dimension = d,
                Text = r.GetString(9) + (AgentDb.StrOrNull(r, 12) is { } a ? $"\nannotation: {a}" : ""),
                DocRef = ObservationStore.DocRef(r.GetString(1), r.GetString(2), d),
                Props = P(("id", r.GetInt64(0)), ("kind", r.GetString(1)), ("key", r.GetString(2)), ("at", box), ("source", r.GetString(10)),
                    ("confidence", r.GetDouble(11).ToString("0.00", CultureInfo.InvariantCulture)), ("seen", r.GetInt64(15) + "×"),
                    ("first seen", Ms(r.GetInt64(13)).ToLocalTime().ToString("u")), ("last seen", Ms(r.GetInt64(14)).ToLocalTime().ToString("u")),
                    ("removed", removed ? Ms(r.GetInt64(16)).ToLocalTime().ToString("u") : null),
                    ("contents read", r.IsDBNull(18) ? null : Ms(r.GetInt64(18)).ToLocalTime().ToString("u") + (AgentDb.StrOrNull(r, 19) is { } by ? " by " + by : ""))),
            };
        });
        return rows;
    }

    /// <summary>Contents of a container observation (detail pane).</summary>
    public IReadOnlyList<string> ContainerItems(long obsId) =>
        Has("container_items")
            ? db.Query("SELECT name, count, damage FROM container_items WHERE obs_id = $i ORDER BY count DESC", r =>
                $"{r.GetString(0)} × {r.GetInt64(1)}" + (r.GetInt64(2) != 0 ? $" (dmg {r.GetInt64(2)})" : ""), ("$i", obsId))
            : [];

    private IReadOnlyList<MemoryRow> Lessons()
    {
        if (!Has("knowledge_corrections")) return [];
        return db.Query("SELECT id, title, correction, source, updated FROM knowledge_corrections ORDER BY updated DESC", r => new MemoryRow
        {
            Kind = "lessons", Id = r.GetString(0), Name = r.GetString(1), Type = r.GetString(3), Text = r.GetString(2), Updated = Ms(r.GetInt64(4)),
            Props = P(("id", r.GetString(0)), ("title", r.GetString(1)), ("source", r.GetString(3))),
        });
    }

    private IReadOnlyList<MemoryRow> Docs(string where, string kind)
    {
        bool dim = HasCol("docs", "dimension");
        return db.Query($"SELECT id, ref, kind, tag, title, body, source, updated, {(dim ? "dimension" : "0")}, min_x, raptor_idx FROM docs WHERE {where} ORDER BY updated DESC", r => new MemoryRow
        {
            Kind = kind, Id = r.GetString(1), Name = r.GetString(4), Type = AgentDb.StrOrNull(r, 3) ?? r.GetString(2), State = AgentDb.StrOrNull(r, 6) ?? "",
            Text = r.GetString(5), Updated = Ms(r.GetInt64(7)), Dimension = r.IsDBNull(9) ? null : (int)r.GetInt64(8), DocRef = r.GetString(1),
            Props = P(("doc id", r.GetInt64(0)), ("ref", r.GetString(1)), ("kind", r.GetString(2)), ("tag", AgentDb.StrOrNull(r, 3)), ("source", AgentDb.StrOrNull(r, 6)),
                ("tree node", r.IsDBNull(10) ? null : "#" + r.GetInt64(10))),
        });
    }

    private IReadOnlyList<MemoryRow> Activity()
    {
        if (!Has("activity_log")) return [];
        bool dim = HasCol("activity_log", "dimension");
        return db.Query($"SELECT id, at, player, kind, category, text, place_name, x, y, z, confidence, {(dim ? "dimension" : "0")} FROM activity_log ORDER BY at DESC LIMIT 50000", r => new MemoryRow
        {
            Kind = "activity", Id = r.GetInt64(0).ToString(CultureInfo.InvariantCulture), Name = $"{r.GetString(2)} {r.GetString(3)}", Type = r.GetString(4),
            Text = r.GetString(5), Updated = Ms(r.GetInt64(1)), Confidence = r.GetDouble(10), Dimension = r.IsDBNull(7) ? null : (int)r.GetInt64(11),
            Props = P(("player", r.GetString(2)), ("kind", r.GetString(3)), ("category", r.GetString(4)), ("place", AgentDb.StrOrNull(r, 6)),
                ("at", r.IsDBNull(7) ? null : $"{r.GetInt64(7)},{r.GetInt64(8)},{r.GetInt64(9)}")),
        });
    }

    private IReadOnlyList<MemoryRow> Chat()
    {
        if (!Has("chat_log")) return [];
        return db.Query("SELECT id, at, kind, player, text FROM chat_log ORDER BY at DESC LIMIT 50000", r => new MemoryRow
        {
            Kind = "chat", Id = r.GetInt64(0).ToString(CultureInfo.InvariantCulture), Name = AgentDb.StrOrNull(r, 3) ?? r.GetString(2), Type = r.GetString(2),
            Text = r.GetString(4), Updated = Ms(r.GetInt64(1)), Props = P(("id", r.GetInt64(0)), ("kind", r.GetString(2)), ("player", AgentDb.StrOrNull(r, 3))),
        });
    }

    private IReadOnlyList<MemoryRow> Routines()
    {
        if (!Has("routines")) return [];
        return db.Query("SELECT name, data, updated FROM routines ORDER BY updated DESC", r =>
        {
            string type = "routine", text = r.GetString(1);
            try
            {
                if (JsonSerializer.Deserialize<Routine>(r.GetString(1), AtomicFile.Json) is { } rt)
                {
                    if (rt.Script is { Code.Length: > 0 } sc) { type = "script"; text = sc.Code; }
                    else if (rt.Note is { Length: > 0 } n) text = n + Environment.NewLine + Environment.NewLine + text;
                }
            }
            catch (JsonException) { }
            return new MemoryRow
            {
                Kind = "routines", Id = r.GetString(0), Name = r.GetString(0), Type = type, Text = text, Updated = Ms(r.GetInt64(2)),
                DocRef = "rt:" + r.GetString(0).Trim().ToLowerInvariant(), Props = P(("name", r.GetString(0)), ("type", type)),
            };
        });
    }

    // ------------------------------------------------------------------ personality

    public IReadOnlyList<string> SelfScopes() =>
        HasCol("self_profile", "scope")
            ? db.Query("SELECT DISTINCT scope FROM self_profile UNION SELECT DISTINCT scope FROM self_profile_history ORDER BY 1", r => r.GetString(0))
            : Has("self_profile") ? ["main"] : [];

    public sealed record HistoryRow(int Version, DateTimeOffset At, string Op, string? Section, string? Text, string? Reason, string Source);

    public IReadOnlyList<HistoryRow> SelfHistory(string? scope, int limit = 500)
    {
        if (!Has("self_profile_history")) return [];
        bool hasScope = HasCol("self_profile_history", "scope");
        return db.Query($"""
            SELECT version, at, op, section, text, reason, source FROM self_profile_history
            {(scope != null && hasScope ? "WHERE scope = $s" : "")} ORDER BY version DESC LIMIT $n
            """, r => new HistoryRow((int)r.GetInt64(0), Ms(r.GetInt64(1)), r.GetString(2), AgentDb.StrOrNull(r, 3), AgentDb.StrOrNull(r, 4), AgentDb.StrOrNull(r, 5), r.GetString(6)),
            ("$s", scope), ("$n", limit));
    }

    public sealed record MoodRow(string Key, string Dim, double Value, long Samples);

    /// <summary>Decaying aggregates (self_acc): mood axes, per-player affinity, preferences.</summary>
    public IReadOnlyList<MoodRow> SelfAggregates(string scope, string family)
    {
        if (!Has("self_acc")) return [];
        return db.Query("SELECT key, dim, sum, n FROM self_acc WHERE scope = $s AND family = $f ORDER BY key, dim",
            r => new MoodRow(r.GetString(0), r.GetString(1), r.GetInt64(3) > 0 && family != "mood" ? r.GetDouble(2) / r.GetInt64(3) : r.GetDouble(2), r.GetInt64(3)),
            ("$s", scope), ("$f", family));
    }

    public IReadOnlyList<(int Layer, int Idx, string Text, int Leaves)> SelfAbstractions(string scope) =>
        Has("self_abstractions")
            ? db.Query("SELECT layer, idx, text, leaves FROM self_abstractions WHERE scope = $s ORDER BY layer DESC, idx", r => ((int)r.GetInt64(0), (int)r.GetInt64(1), r.GetString(2), (int)r.GetInt64(3)), ("$s", scope))
            : [];

    private static string TextOne(string s, int max)
    {
        var t = s.ReplaceLineEndings(" ");
        return t.Length > max ? t[..max] + "…" : t;
    }
}
