using System.Globalization;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.Raptor.Storage;
using Microsoft.Data.Sqlite;

namespace MineClaw.ViewerBridge.Data;

public sealed record CountCard(string Key, string Label, long Count, string? Detail, string Group, string? MemoryKind);
public sealed record VectorModelCount(string Model, int Dims, long Count);
public sealed record DbInfo(string File, long SizeBytes, int SchemaVersion, bool Fts5, bool Rtree, IReadOnlyList<VectorModelCount> Vectors, string SearchMode,
    string SearchNote, string ServerVersion, DateTimeOffset ReadAt);
public sealed record KnowledgePackDto(string Label, string? Version, IReadOnlyList<string> Modes, string? Detail);
public sealed record EmbeddingsStatusDto(bool Configured, string Status, string? Model, int? Dims, bool MatchesStored);
public sealed record RaptorLayerStat(int Layer, string Name, int Nodes, double AvgChildren, double AvgTokens, int MaxTokens, int Dirty, double Share);
public sealed record RaptorStatsDto(string Headline, int Nodes, int Leaves, int SummaryLayers, IReadOnlyList<RaptorLayerStat> Layers, RaptorMetaDto Meta,
    double? InsertedCosMean, int DirtySummaries, int PendingSummaries, int StoredLayers);
public sealed record OverviewDto(DbInfo Db, KnowledgePackDto? KnowledgePack, EmbeddingsStatusDto Embeddings, IReadOnlyList<CountCard> Cards, RaptorStatsDto? Raptor, string? RaptorNote);

public sealed record TraitEntry(long Id, string Section, string Text, string? Player, double Strength, long Observations, long? Created, long? Reinforced, string? Reason);
public sealed record TraitGroup(string Section, IReadOnlyList<TraitEntry> Entries);
public sealed record SelfHistoryEntry(long Version, long At, string Op, string Section, string Text, string? Reason, string? Source);
public sealed record MoodEntry(string Dim, double Value, long N);
public sealed record SelfAbstraction(int Layer, int Idx, string Text, int Leaves);
public sealed record PersonalityResponse(IReadOnlyList<string> Scopes, string Scope, IReadOnlyList<TraitGroup> Groups, IReadOnlyList<SelfHistoryEntry> History,
    IReadOnlyList<MoodEntry> Mood, IReadOnlyList<SelfAbstraction> Abstractions, string Summary);

/// <summary>Overview (explorer-spec §3) and Personality (§7), ported from the explorer's OverviewViewModel / MemoryQueries.</summary>
public static class DbViews
{
    public static OverviewDto Overview(MemoryBank bank, BridgeConfig config)
    {
        var db = bank.Db;
        bool Has(string t) => db.TableExists(t);
        long Count(string sql)
        {
            try { return db.ScalarLong(sql); }
            catch (SqliteException) { return 0; }
        }
        List<string> Top(string sql)
        {
            try { return db.Query(sql, r => $"{r.GetInt64(1)} {r.GetString(0)}"); }
            catch (SqliteException) { return []; }
        }
        var cards = new List<CountCard>();
        void Add(string key, string label, string table, string sql, string detail, string group, string? kind)
        {
            if (Has(table)) cards.Add(new CountCard(key, label, Count(sql), detail, group, kind));
        }
        Add("facts", "Facts", "notes", "SELECT count(*) FROM notes", "notes about players and the world", "Memory", "facts");
        Add("recipes", "Recipes", "docs", "SELECT count(*) FROM docs WHERE kind = 'gamedata' AND tag IN ('recipe','smelt')", "seeded crafting + smelting", "Knowledge", "recipes");
        if (Has("claims"))
        {
            var by = Top("SELECT status, count(*) FROM claims GROUP BY status ORDER BY 2 DESC");
            cards.Add(new("claims", "Claims", Count("SELECT count(*) FROM claims"), by.Count == 0 ? "none yet" : string.Join(" · ", by), "Beliefs", "claims"));
            cards.Add(new("beliefs", "Beliefs", Count("SELECT count(*) FROM claims WHERE status IN ('verified','provisional')"), "verified or provisional claims", "Beliefs", "beliefs"));
        }
        Add("episodes", "Episodes", "docs", "SELECT count(*) FROM docs WHERE kind = 'episode'", "event summaries", "Events", "episodes");
        Add("activity", "Activity", "activity_log", "SELECT count(*) FROM activity_log", "player actions noticed", "Events", "activity");
        Add("chat", "Chat lines", "chat_log", "SELECT count(*) FROM chat_log", "heard and said", "Events", "chat");
        Add("structures", "Structures", "structures", "SELECT count(*) FROM structures", "known builds", "World", "structures");
        Add("places", "Places", "waypoints", "SELECT count(*) FROM waypoints", "named waypoints", "World", "places");
        if (Has("observations"))
        {
            var live = db.HasColumn("observations", "removed") ? " WHERE removed IS NULL" : "";
            cards.Add(new("observations", "World observations", Count("SELECT count(*) FROM observations" + live),
                string.Join(" · ", Top($"SELECT kind, count(*) FROM observations{live} GROUP BY kind ORDER BY 2 DESC LIMIT 4")), "World", "observed"));
        }
        Add("chunks", "Remembered chunks", "world_chunks", "SELECT count(*) FROM world_chunks", "columns of world memory", "World", null);
        if (Has("self_profile"))
            cards.Add(new("traits", "Personality traits", Count("SELECT count(*) FROM self_profile"),
                string.Join(" · ", Top("SELECT section, count(*) FROM self_profile GROUP BY section ORDER BY 2 DESC")), "Self", "personality"));
        Add("lessons", "Lessons", "knowledge_corrections", "SELECT count(*) FROM knowledge_corrections", "superseding corrections", "Memory", "lessons");
        Add("routines", "Routines", "routines", "SELECT count(*) FROM routines", "routines + scripts", "Memory", "routines");
        Add("docs", "Docs indexed", "docs", "SELECT count(*) FROM docs", "every searchable doc (FTS)", "Index", null);
        Add("vectors", "Vectors", "vectors", "SELECT count(*) FROM vectors", "stored embeddings", "Index", null);

        var vectors = Has("vectors")
            ? db.Query("SELECT model, dims, count(*) FROM vectors GROUP BY model, dims ORDER BY 3 DESC", r => new VectorModelCount(r.GetString(0), (int)r.GetInt64(1), r.GetInt64(2)))
            : [];
        var (mode, note) = SearchService.ModeOf(bank);
        var info = new DbInfo(Path.GetFileName(bank.Path), MemoryBank.FileSize(bank.Path), db.SchemaVersion, db.HasFts, db.HasRtree, vectors, mode, note,
            config.ServerVersion, DateTimeOffset.UtcNow);

        KnowledgePackDto? pack = null;
        try
        {
            var a = KnowledgePack.Active(db);
            pack = new KnowledgePackDto(a.Label, a.Version, a.Modes?.Select(m => m.Mode).ToList() ?? [], a.Shard);
        }
        catch (Exception ex) when (ex is SqliteException or System.Text.Json.JsonException) { }

        var provider = bank.Embeddings;
        var emb = new EmbeddingsStatusDto(config.EmbeddingsConfigured, config.EmbeddingsStatus, provider.IsAvailable ? provider.Model : null,
            provider.IsAvailable ? provider.Dimensions : null, bank.VectorSearch);
        return new OverviewDto(info, pack, emb, cards, Stats(bank), bank.HasTree ? null : bank.TreeNote);
    }

    public static RaptorStatsDto? Stats(MemoryBank bank)
    {
        if (!bank.HasTree || bank.TreeMeta is not { } meta) return null;
        return bank.ReadTree(t =>
        {
            var layers = new List<RaptorLayerStat>();
            int max = Math.Max(1, Enumerable.Range(0, t.NumLayers + 1).Max(l => t.LayerIndices(l).Count));
            for (int l = t.NumLayers; l >= 0; l--)
            {
                var nodes = t.Layer(l).ToList();
                if (nodes.Count == 0) continue;
                layers.Add(new RaptorLayerStat(l, l == 0 ? "leaves" : l == t.NumLayers ? $"L{l} summaries (roots)" : $"L{l} summaries", nodes.Count,
                    l == 0 ? 0 : Math.Round(nodes.Average(n => n.Children.Count), 2), Math.Round(nodes.Average(n => n.TokenCount), 1), nodes.Max(n => n.TokenCount),
                    nodes.Count(n => n.Dirty || n.SummaryPending), Math.Round(nodes.Count / (double)max, 4)));
            }
            int dirty = t.AllNodes.Values.Count(n => n.Dirty), pending = t.AllNodes.Values.Count(n => n.SummaryPending);
            var liveMeta = meta with
            {
                NumLayers = t.NumLayers, NextIndex = t.NextIndex, Dimensions = t.Dimensions, EmbeddingModel = t.EmbeddingModel, BuildId = t.BuildId, BuiltAt = t.BuiltAt,
                LeavesAtBuild = t.LeavesAtBuild, InsertedSinceBuild = t.InsertedSinceBuild, RemovedSinceBuild = t.RemovedSinceBuild,
                BaselineLeafParentSimilarity = t.BaselineLeafParentSimilarity, InsertedSimilaritySum = t.InsertedSimilaritySum,
                InsertedSimilarityCount = t.InsertedSimilarityCount, InsertSeq = t.InsertSeq, BaselineLeafParentSimilarityStd = t.BaselineLeafParentSimilarityStd,
                InsertTemperature = t.InsertTemperature,
            };
            return new RaptorStatsDto($"{t.Count:N0} nodes · {t.LeafCount:N0} leaves · {t.NumLayers} summary layers", t.Count, t.LeafCount, t.NumLayers, layers, RaptorMetaDto.From(liveMeta)!,
                t.InsertedSimilarityCount > 0 ? t.InsertedSimilaritySum / t.InsertedSimilarityCount : null, dirty, pending, meta.NumLayers);
        }, null);
    }

    private static readonly string[] SectionOrder = ["trait", "like", "dislike", "opinion", "joke", "nickname", "relationship", "milestone"];
    private static readonly HashSet<string> Ops = ["add", "reinforce", "adjust", "remove"];

    internal static double MoodValue(double sum, long updatedMs, double halfLifeHours, long? nowMs = null)
    {
        double hours = Math.Max(0, ((nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - updatedMs) / 3_600_000.0);
        return Math.Tanh(sum * Math.Pow(0.5, hours / Math.Max(0.1, halfLifeHours)) / 1.5);
    }

    public static PersonalityResponse Personality(MemoryBank bank, string scope)
    {
        var db = bank.Db;
        bool Has(string t) => db.TableExists(t);
        bool HasCol(string t, string c) => Has(t) && db.HasColumn(t, c);
        var scopes = HasCol("self_profile", "scope")
            ? db.Query("SELECT DISTINCT scope FROM self_profile" + (HasCol("self_profile_history", "scope") ? " UNION SELECT DISTINCT scope FROM self_profile_history" : "") + " ORDER BY 1",
                r => r.IsDBNull(0) ? "main" : r.GetString(0))
            : Has("self_profile") ? ["main"] : new List<string>();

        var entries = new List<TraitEntry>();
        if (Has("self_profile"))
        {
            bool hasScope = HasCol("self_profile", "scope"), hasObs = HasCol("self_profile", "observations");
            entries = db.Query($"""
                SELECT id, section, player, text, strength, created, reinforced, reason, {(hasObs ? "observations" : "1")}
                FROM self_profile {(hasScope ? "WHERE scope = $s" : "")} ORDER BY section, strength DESC
                """, r => new TraitEntry(r.GetInt64(0), r.GetString(1), r.GetString(3), AgentDb.StrOrNull(r, 2), r.GetDouble(4), r.GetInt64(8),
                    AgentDb.LongOrNull(r, 5), AgentDb.LongOrNull(r, 6), AgentDb.StrOrNull(r, 7)), ("$s", scope));
        }
        var groups = SectionOrder.Select(s => new TraitGroup(s, entries.Where(e => e.Section == s).ToList())).Where(g => g.Entries.Count > 0).ToList();

        var history = new List<SelfHistoryEntry>();
        if (Has("self_profile_history"))
        {
            bool hasScope = HasCol("self_profile_history", "scope");
            history = db.Query($"""
                SELECT version, at, op, section, text, reason, source FROM self_profile_history
                {(hasScope ? "WHERE scope = $s" : "")} ORDER BY version DESC LIMIT 500
                """, r => new SelfHistoryEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), AgentDb.StrOrNull(r, 3) ?? "", AgentDb.StrOrNull(r, 4) ?? "",
                    AgentDb.StrOrNull(r, 5), AgentDb.StrOrNull(r, 6)), ("$s", scope)).Where(h => Ops.Contains(h.Op)).ToList();
        }
        var mood = Has("self_acc")
            // The stored sum is a decayed accumulator as of `updated`; report what the bot itself reads (SelfGrowth.Mood):
            // decayed to now, then tanh(sum / 1.5), so the value is always within -1..1.
            ? db.Query("SELECT dim, sum, n, updated FROM self_acc WHERE scope = $s AND family = 'mood' AND key IN ('', '_') ORDER BY dim",
                r => new MoodEntry(r.GetString(0), MoodValue(r.GetDouble(1), r.GetInt64(3), bank.MoodHalfLifeHours), r.GetInt64(2)), ("$s", scope))
            : [];
        var abstractions = Has("self_abstractions")
            ? db.Query("SELECT layer, idx, text, leaves FROM self_abstractions WHERE scope = $s ORDER BY layer DESC, idx",
                r => new SelfAbstraction((int)r.GetInt64(0), (int)r.GetInt64(1), r.GetString(2), (int)r.GetInt64(3)), ("$s", scope))
            : [];
        var last = history.FirstOrDefault();
        string summary = $"{entries.Count} entries · {history.Count} versions" +
            (last != null ? $" · last change {DateTimeOffset.FromUnixTimeMilliseconds(last.At):yyyy-MM-dd HH:mm} ({last.Source ?? last.Op})" : "");
        return new PersonalityResponse(scopes, scope, groups, history, mood, abstractions, summary);
    }
}
