using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Rag;

/// <summary>
/// One clause rule of a <see cref="KnowledgeCorrection"/>: a clause (sentence / "|" or ";" separated part) matches when
/// every <see cref="AllOf"/> regex matches it and no <see cref="NoneOf"/> regex does.
/// </summary>
public sealed record ClauseRule(IReadOnlyList<Regex> AllOf, IReadOnlyList<Regex>? NoneOf = null)
{
    public bool IsMatch(string clause) =>
        AllOf.Count > 0 && AllOf.All(r => r.IsMatch(clause)) && (NoneOf == null || !NoneOf.Any(r => r.IsMatch(clause)));
}

/// <summary>
/// A system-authored correction of a wrong lesson the bot learned (a <c>knowledge_correction</c> record, source=system):
/// memories whose text matches <see cref="Clauses"/> (per clause) or <see cref="Whole"/> (anywhere) are superseded —
/// recall / auto-retrieval / ask_memory drop them and surface the correction doc (<see cref="Ref"/>) in their place.
/// Deterministic regex only (zero tokens).
/// </summary>
public sealed record KnowledgeCorrection(string Id, string Title, string Correction, string Short,
    IReadOnlyList<ClauseRule> Clauses, IReadOnlyList<Regex>? Whole = null)
{
    public const string RefPrefix = "corr:";
    public string Ref => RefPrefix + Id;

    /// <summary>Sentence / list-part boundaries: ". " "! " "? ", newlines, " | " (episode lines) and ";".</summary>
    private static readonly Regex ClauseSplit = new(@"(?<=[.!?])\s+|[\r\n|;]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public bool Matches(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            if (Whole != null && Whole.Any(r => r.IsMatch(text))) return true;
            foreach (var clause in ClauseSplit.Split(text))
                if (clause.Length > 0 && Clauses.Any(c => c.IsMatch(clause))) return true;
        }
        catch (RegexMatchTimeoutException) { }
        return false;
    }
}

/// <summary>
/// Deterministic, zero-token corrections of stale lessons in memory. <see cref="Apply"/> (every startup; idempotent, so a
/// db reset to the seed gets them again) stores each correction as a <c>knowledge_corrections</c> row (source=system) plus
/// a retrievable knowledge doc (<c>corr:{id}</c>), marks every contradicted doc in <c>doc_corrections</c>, and attaches
/// itself to <see cref="RagIndex.Corrections"/>: from then on <see cref="RagIndex.Search"/> drops superseded hits (also ones
/// written later, by any process — the check runs at retrieval time) and puts the correction doc in their place; the chat
/// log readers annotate matching lines. Sources: <see cref="BuiltIn"/> plus an optional <c>corrections.json</c> in the
/// knowledge directory.
/// </summary>
public sealed class KnowledgeCorrections
{
    public const string FileName = "corrections.json";
    public const string Source = "system";
    public const string DocTag = "correction";
    public const double DocWeight = 1.5;

    /// <summary>Doc kinds never superseded: curated/generated reference text (fix those at the source instead).</summary>
    private static readonly HashSet<string> ExemptKinds = new(StringComparer.Ordinal) { KnowledgeIndexer.Kind, "gamedata", "skill", "chat" };

    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static Regex R(string pattern) => new(pattern, Rx, TimeSpan.FromMilliseconds(200));

    // ---- sapling-dark: b1.8.1 BlockFlower.f(): (world.k(x,y,z) >= 8 || world.isChunkLoaded(x,y,z) /* = can see sky */) && soil ----
    private static readonly Regex PlantWord = R(@"\b(?:re-?plant\w*|un-?replanted|plant(?:ed|ing|s)?|saplings?)\b");
    private static readonly Regex Refused = R(
        @"\b(?:fail\w*|blocked|blocks?\s+(?:the\s+)?(?:re-?)?plant\w*|couldn'?t|could\s+not|can'?t|cannot|won'?t|wouldn'?t|will\s+not|unable|refus\w*|" +
        @"not\s+re-?planted|un-?replanted|no\s+(?:sapling\s+)?re-?plant\w*|didn'?t\s+(?:re-?)?plant|too\s+dark|" +
        @"(?:needs?|requires?|needed|required)\s+(?:a\s+|some\s+|more\s+)?(?:light|torch\w*|daylight|day))\b");
    private static readonly Regex DarkWord = R(@"\b(?:too\s+dark|in\s+the\s+dark|dark(?:ness)?|at\s+night|night(?:time)?|light\s*(?:level\s*)?(?:of\s*)?(?:>=?|≥|at\s+least)?\s*8)\b");
    private static readonly Regex NeedsTorchToPlant = R(@"\b(?:needs?|requires?|needed|required|must\s+(?:place|put))\s+(?:a\s+|some\s+)?torch\w*");
    /// <summary>A clause about growing (not planting/placing) is true: growth does need light ≥ 9.</summary>
    private static readonly Regex GrowthOnly = R(@"^(?!.*\b(?:re-?plant\w*|plant(?:ed|ing|s)?|plac\w*)\b).*\b(?:grow\w*|growth|grew)\b");
    /// <summary>Clauses already stating the correct rule.</summary>
    private static readonly Regex Affirms = R(
        @"\b(?:can|could|may)\s+(?:still\s+|always\s+|be\s+)*(?:re-?)?plant\w*|\b(?:don'?t|doesn'?t|do\s+not|does\s+not|no)\s+need\w*|\bnot\s+needed|" +
        @"\bnever\s+blocks?|\bany\s*time\b|\bsuperseded\b|\bsee(?:s)?\s+the\s+sky\b");
    private static readonly Regex ReplantInDaylight = R(
        @"\bre-?plant\w*\b[^.!?]{0,30}?\b(?:in|during|at|by|until|after)\s+(?:the\s+)?(?:daylight|daytime|day|morning|dawn|sunrise)\b|" +
        @"\bwait\w*\s+(?:for|until|till)\s+(?:the\s+)?(?:day|daylight|daytime|morning|dawn|sunrise)\b[^.!?]{0,30}?\b(?:re-?)?plant");
    /// <summary>"…and replant uwu. It's night though, and last time a repl…" (a caveat spanning sentences).</summary>
    private static readonly Regex NightCaveat = R(@"\bre-?plant\w*\b[^|\n]{0,40}?\bit'?s\s+(?:night|dark)\s+though\b");

    public static readonly KnowledgeCorrection SaplingDark = new(
        "sapling_dark",
        "sapling planting",
        // ≤ 180 chars: FormatHit shows a knowledge body whole (not an FTS snippet) and the [memory] line (220) keeps all of it.
        "superseded: saplings CAN be planted in the dark if the spot sees the sky (or light >= 8); no torch needed. " +
        "They only GROW at light >= 9 (torches keep growth going at night).",
        "saplings CAN be planted at night (sky or light>=8); only growth needs light>=9",
        [
            new ClauseRule([PlantWord, Refused, DarkWord], [GrowthOnly, Affirms]),
            new ClauseRule([PlantWord, NeedsTorchToPlant], [GrowthOnly, Affirms]),
            new ClauseRule([ReplantInDaylight], [Affirms]),
        ],
        [NightCaveat]);

    /// <summary>The built-in corrections (C#; reapplied at every start).</summary>
    public static readonly IReadOnlyList<KnowledgeCorrection> BuiltIn = [SaplingDark];

    private readonly List<KnowledgeCorrection> _items;
    private HashSet<string> _adminIds = new(StringComparer.OrdinalIgnoreCase);

    public KnowledgeCorrections(IEnumerable<KnowledgeCorrection> items) => _items = items.ToList();

    public IReadOnlyList<KnowledgeCorrection> Items => _items;

    // ======================= admin lessons (memory admin tool) =======================

    /// <summary>Source of lessons added or overridden by an admin: kept across restarts (startup only prunes source=system).</summary>
    public const string AdminSource = "admin";

    private const string TablesSql = """
        CREATE TABLE IF NOT EXISTS knowledge_corrections(id TEXT PRIMARY KEY, title TEXT NOT NULL, correction TEXT NOT NULL,
            patterns TEXT NOT NULL, source TEXT NOT NULL, updated INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS doc_corrections(doc_id INTEGER PRIMARY KEY REFERENCES docs(id) ON DELETE CASCADE,
            ref TEXT NOT NULL, correction_id TEXT NOT NULL, at INTEGER NOT NULL);
        """;

    /// <summary>The stored patterns JSON of a correction: <c>{"clauses":[{"all":[re..],"none":[re..]}],"whole":[re..]}</c>.</summary>
    public static string PatternsJson(KnowledgeCorrection c) => JsonSerializer.Serialize(new
    {
        clauses = c.Clauses.Select(r => new { all = r.AllOf.Select(x => x.ToString()), none = r.NoneOf?.Select(x => x.ToString()) }),
        whole = c.Whole?.Select(x => x.ToString()),
    });

    /// <summary>Builds a correction from its stored row (patterns as <see cref="PatternsJson"/>). Throws on a bad pattern.</summary>
    public static KnowledgeCorrection FromRow(string id, string title, string correction, string patternsJson)
    {
        using var doc = JsonDocument.Parse(patternsJson);
        var root = doc.RootElement;
        var clauses = new List<ClauseRule>();
        if (root.TryGetProperty("clauses", out var cl) && cl.ValueKind == JsonValueKind.Array)
            foreach (var c in cl.EnumerateArray())
                clauses.Add(new ClauseRule(Regexes(c, "all"), c.TryGetProperty("none", out var n) && n.ValueKind == JsonValueKind.Array ? Regexes(c, "none") : null));
        var whole = Regexes(root, "whole");
        return new KnowledgeCorrection(id, title, correction, correction.Length <= 180 ? correction : correction[..177] + "...", clauses,
            whole.Count > 0 ? whole : null);

        static List<Regex> Regexes(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => R(x.GetString()!)).ToList() : [];
    }

    /// <summary>Compiles one admin pattern (same options and timeout as the built-ins). Throws ArgumentException when invalid.</summary>
    public static Regex Pattern(string pattern) => R(pattern);

    /// <summary>Admin lessons stored in this db (bad rows are skipped with a warning).</summary>
    public static List<KnowledgeCorrection> LoadAdmin(AgentDb db, ILogger? log = null)
    {
        if (!db.TableExists("knowledge_corrections")) return [];
        var list = new List<KnowledgeCorrection>();
        foreach (var (id, title, corr, patterns) in db.Query("SELECT id, title, correction, patterns FROM knowledge_corrections WHERE source = $s ORDER BY id",
                     r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("$s", AdminSource)))
        {
            try { list.Add(FromRow(id, title, corr, patterns)); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                log?.LogWarning("skipping admin lesson {Id}: {Error}", id, ex.Message);
            }
        }
        return list;
    }

    /// <summary>Source of a stored lesson row (system | admin), or null when there is none.</summary>
    public static string? SourceOf(AgentDb db, string id) =>
        db.TableExists("knowledge_corrections") ? db.Scalar("SELECT source FROM knowledge_corrections WHERE id = $i", ("$i", id)) as string : null;

    /// <summary>Writes an admin lesson row (insert or override). Call <see cref="Apply"/> afterwards to activate it.</summary>
    public static void SaveAdmin(AgentDb db, KnowledgeCorrection c) => db.Tx(() =>
    {
        db.Exec(TablesSql);
        db.Exec("""
            INSERT INTO knowledge_corrections(id, title, correction, patterns, source, updated) VALUES ($i, $t, $c, $p, $s, $u)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, correction = excluded.correction, patterns = excluded.patterns,
                source = excluded.source, updated = excluded.updated
            """, ("$i", c.Id), ("$t", c.Title), ("$c", c.Correction), ("$p", PatternsJson(c)), ("$s", AdminSource),
            ("$u", AgentDb.ToUnixMs(DateTimeOffset.UtcNow)));
    });

    /// <summary>Deletes an admin lesson row (system lessons are rebuilt at every start and can't be deleted here).</summary>
    public static bool DeleteAdmin(AgentDb db, string id) =>
        db.TableExists("knowledge_corrections") && db.Exec("DELETE FROM knowledge_corrections WHERE id = $i AND source = $s", ("$i", id), ("$s", AdminSource)) > 0;

    /// <summary>The correction that supersedes this text, or null.</summary>
    public KnowledgeCorrection? Superseding(string text) => _items.FirstOrDefault(c => c.Matches(text));

    /// <summary>The correction that supersedes this doc (exempt kinds and the corrections themselves never are).</summary>
    public KnowledgeCorrection? Superseding(string reference, string kind, string title, string body) =>
        ExemptKinds.Contains(kind) || reference.StartsWith(KnowledgeCorrection.RefPrefix, StringComparison.Ordinal) ? null : Superseding(title + "\n" + body);

    public KnowledgeCorrection? Superseding(RagHit h) => Superseding(h.Ref, h.Kind, h.Title, h.Body);

    /// <summary>
    /// Retrieval-time filter (ranked hits in, ranked hits out): superseded hits are dropped and the correction doc takes
    /// the best dropped score (unless already present or excluded, e.g. already in the context window).
    /// </summary>
    public List<RagHit> Filter(RagIndex index, IEnumerable<RagHit> ranked, Func<long, bool>? exclude)
    {
        var list = ranked.ToList();
        Dictionary<KnowledgeCorrection, double>? replaced = null;
        var kept = new List<RagHit>(list.Count);
        foreach (var h in list)
        {
            if (Superseding(h) is { } c)
            {
                replaced ??= new();
                replaced[c] = Math.Max(replaced.GetValueOrDefault(c), h.Score);
            }
            else kept.Add(h);
        }
        if (replaced == null) return list;
        foreach (var (c, score) in replaced)
        {
            int at = kept.FindIndex(h => h.Ref == c.Ref);
            if (at >= 0)
            {
                kept[at] = kept[at] with { Score = Math.Max(kept[at].Score, score), Snippet = null }; // whole correction, not an FTS snippet
                continue;
            }
            if (index.GetByRef(c.Ref) is not { } doc || exclude?.Invoke(doc.Id) == true) continue;
            kept.Add(doc with { Score = score, Lexical = 1, Cosine = 1 });
        }
        return kept.OrderByDescending(h => h.Score).ThenByDescending(h => h.Updated).ToList();
    }

    /// <summary>Chat rows (verbatim history) that match are kept but prefixed, so the stale claim isn't taken as fact.</summary>
    internal List<ChatRow> AnnotateChat(List<ChatRow> rows, Func<ChatRow, string> context)
    {
        for (int i = 0; i < rows.Count; i++)
            if (Superseding(context(rows[i])) is { } c)
                rows[i] = rows[i] with { Text = $"[superseded: {c.Short}] {rows[i].Text}" };
        return rows;
    }

    // ======================= startup =======================

    public sealed record ApplyResult(int Corrections, int Superseded, IReadOnlyList<string> SupersededRefs);

    /// <summary>
    /// Idempotent startup pass: built-ins + <c>{knowledgeDirectory}/corrections.json</c> → <c>knowledge_corrections</c> rows and
    /// <c>corr:*</c> docs (stale system ones removed), <c>doc_corrections</c> recomputed, and <see cref="RagIndex.Corrections"/> set.
    /// </summary>
    public static KnowledgeCorrections Apply(RagIndex index, string? knowledgeDirectory = null, ILogger? log = null,
        IEnumerable<KnowledgeCorrection>? extra = null)
    {
        log ??= NullLogger.Instance;
        var items = BuiltIn.ToList();
        if (knowledgeDirectory != null) items.AddRange(LoadFile(Path.Combine(knowledgeDirectory, FileName), log));
        if (extra != null) items.AddRange(extra);
        // Lessons written by the memory admin tool (source=admin) come last: they add lessons and override same-id ones.
        var admin = LoadAdmin(index.Db, log);
        items.AddRange(admin);
        items = items.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        var engine = new KnowledgeCorrections(items) { _adminIds = admin.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) };
        var result = engine.Store(index);
        index.Corrections = engine;
        if (result.Superseded > 0)
            log.LogInformation("knowledge corrections: {Count} correction(s), {Superseded} stale memories superseded ({Refs})",
                result.Corrections, result.Superseded, string.Join(", ", result.SupersededRefs.Take(12)));
        return engine;
    }

    private ApplyResult Store(RagIndex index)
    {
        var db = index.Db;
        long now = AgentDb.ToUnixMs(DateTimeOffset.UtcNow);
        var refs = new List<string>();
        db.Tx(() =>
        {
            db.Exec("""
                CREATE TABLE IF NOT EXISTS knowledge_corrections(id TEXT PRIMARY KEY, title TEXT NOT NULL, correction TEXT NOT NULL,
                    patterns TEXT NOT NULL, source TEXT NOT NULL, updated INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS doc_corrections(doc_id INTEGER PRIMARY KEY REFERENCES docs(id) ON DELETE CASCADE,
                    ref TEXT NOT NULL, correction_id TEXT NOT NULL, at INTEGER NOT NULL);
                """);
            var keep = _items.Select(c => c.Ref).ToHashSet(StringComparer.Ordinal);
            foreach (var c in _items)
            {
                var patterns = JsonSerializer.Serialize(new
                {
                    clauses = c.Clauses.Select(r => new { all = r.AllOf.Select(x => x.ToString()), none = r.NoneOf?.Select(x => x.ToString()) }),
                    whole = c.Whole?.Select(x => x.ToString()),
                });
                // Only touch the row when it changed (hive processes share the file; no needless writes).
                db.Exec("""
                    INSERT INTO knowledge_corrections(id, title, correction, patterns, source, updated) VALUES ($i, $t, $c, $p, $s, $u)
                    ON CONFLICT(id) DO UPDATE SET title = excluded.title, correction = excluded.correction, patterns = excluded.patterns,
                        source = excluded.source, updated = excluded.updated
                    WHERE title <> excluded.title OR correction <> excluded.correction OR patterns <> excluded.patterns OR source <> excluded.source
                    """, ("$i", c.Id), ("$t", c.Title), ("$c", c.Correction), ("$p", patterns), ("$s", _adminIds.Contains(c.Id) ? AdminSource : Source), ("$u", now));
                index.Upsert(new RagDoc(c.Ref, KnowledgeIndexer.Kind, c.Title, c.Correction) { Tag = DocTag, Source = Source, Weight = DocWeight });
            }
            foreach (var id in db.Query("SELECT id FROM knowledge_corrections WHERE source = $s", r => r.GetString(0), ("$s", Source)))
                if (!keep.Contains(KnowledgeCorrection.RefPrefix + id)) db.Exec("DELETE FROM knowledge_corrections WHERE id = $i", ("$i", id));
            index.DeleteByPrefix(KnowledgeCorrection.RefPrefix, keep);

            // Recompute the marks (audit trail of what's retracted; retrieval re-checks the text, so later writes are caught too).
            db.Exec("DELETE FROM doc_corrections");
            var docs = db.Query("SELECT id, ref, kind, title, body FROM docs WHERE kind NOT IN ('knowledge', 'gamedata', 'skill', 'chat')",
                r => (Id: r.GetInt64(0), Ref: r.GetString(1), Kind: r.GetString(2), Title: r.GetString(3), Body: r.GetString(4)));
            foreach (var d in docs)
            {
                if (Superseding(d.Ref, d.Kind, d.Title, d.Body) is not { } c) continue;
                db.Exec("INSERT OR REPLACE INTO doc_corrections(doc_id, ref, correction_id, at) VALUES ($d, $r, $c, $t)",
                    ("$d", d.Id), ("$r", d.Ref), ("$c", c.Id), ("$t", now));
                refs.Add(d.Ref);
            }
        });
        return new ApplyResult(_items.Count, refs.Count, refs);
    }

    /// <summary>Refs currently marked superseded (by the last <see cref="Apply"/>), with their correction id.</summary>
    public static List<(string Ref, string CorrectionId)> Marked(AgentDb db) =>
        db.TableExists("doc_corrections")
            ? db.Query("SELECT ref, correction_id FROM doc_corrections ORDER BY doc_id", r => (r.GetString(0), r.GetString(1)))
            : [];

    /// <summary>
    /// Optional <c>corrections.json</c>: <c>[{"id","title","correction","short"?,"clauses":[{"all":[regex..],"none":[regex..]}],"whole":[regex..]}]</c>.
    /// Bad entries are skipped with a warning.
    /// </summary>
    public static List<KnowledgeCorrection> LoadFile(string path, ILogger? log = null)
    {
        var list = new List<KnowledgeCorrection>();
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                try
                {
                    string id = e.GetProperty("id").GetString()!, correction = e.GetProperty("correction").GetString()!;
                    string title = e.TryGetProperty("title", out var t) ? t.GetString() ?? id : "correction: " + id;
                    string shortText = e.TryGetProperty("short", out var s) ? s.GetString() ?? correction : correction;
                    var clauses = new List<ClauseRule>();
                    if (e.TryGetProperty("clauses", out var cl))
                        foreach (var c in cl.EnumerateArray())
                            clauses.Add(new ClauseRule(Regexes(c, "all"), c.TryGetProperty("none", out _) ? Regexes(c, "none") : null));
                    var whole = Regexes(e, "whole");
                    if (clauses.Count == 0 && whole.Count == 0) throw new FormatException("no patterns");
                    list.Add(new KnowledgeCorrection(id, title, correction, shortText, clauses, whole));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
                {
                    log?.LogWarning("skipping bad entry in {File}: {Error}", path, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            log?.LogWarning("could not read {File}: {Error}", path, ex.Message);
        }
        return list;

        static List<Regex> Regexes(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(x => R(x.GetString()!)).ToList() : [];
    }
}
