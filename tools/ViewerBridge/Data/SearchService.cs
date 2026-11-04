using System.Globalization;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Data;

public sealed record SearchSignals(double? Bm25, double? Cos, double? Distance);
public sealed record SearchHitDto(int Rank, long DocId, string Ref, string Kind, string? Tag, string Title, string Snippet, double Score, SearchSignals Signals,
    long? Updated, int? Dimension, string? Where, int? RaptorLeaf, IReadOnlyList<GraphCrumb> RaptorPath);
public sealed record TreeHitDto(string List, int Index, int Layer, double Score, string Preview);
public sealed record SearchResponse(string Query, string Mode, string ModeNote, bool Embedded, double ElapsedMs, IReadOnlyList<SearchHitDto> Hits, IReadOnlyList<TreeHitDto> TreeHits);

/// <summary>
/// The bot's own retrieval, offline (ported from tools/MemoryExplorer/Services/SearchService.cs): <see cref="RagIndex.Search(RagQuery, float[], SearchAugment)"/>
/// with the RAPTOR augmenter (FTS5/BM25 + flat cosine + RAPTOR collapsed/traversal lists fused by RRF) when query embedding is
/// configured and the stored vectors match; otherwise the same index lexical-only, and the response says why.
/// </summary>
public static class SearchService
{
    public static (string Mode, string Note) ModeOf(MemoryBank bank) => bank.VectorSearch
        ? bank.HasTree ? ("hybrid", "hybrid: FTS/BM25 + vectors + RAPTOR tree (the bot's recall pipeline)") : ("hybrid-no-tree", "hybrid: FTS/BM25 + vectors (no RAPTOR tree in this db)")
        : ("lexical", bank.Embeddings.IsAvailable
            ? "lexical fallback: the stored vectors were made by another embedding model"
            : "lexical fallback: query embedding isn't configured (Agent:Embeddings), so only FTS/BM25 ranks");

    public static async Task<SearchResponse> SearchAsync(MemoryBank bank, string text, bool forceLexical, IReadOnlyCollection<string>? kinds, int? dimension, int limit,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var q = new RagQuery { Text = text, Kinds = kinds is { Count: > 0 } ? kinds : null, Dimension = dimension ?? WorldDim.Any, Limit = limit };
        float[]? qv = null;
        List<RagHit> hits;
        var tree = new List<TreeHitDto>();
        var (mode, note) = ModeOf(bank);
        if (bank.VectorSearch && !forceLexical)
        {
            (hits, qv) = await HybridSearchAsync(bank, q, text, tree, ct).ConfigureAwait(false);
            if (qv == null) (mode, note) = ("lexical", "lexical fallback: the query embedding call failed or timed out");
        }
        else
        {
            hits = await Task.Run(() => bank.Rag.Search(q), ct).ConfigureAwait(false);
            if (forceLexical) (mode, note) = ("lexical", "lexical: requested (FTS/BM25 only)");
        }

        var g = bank.Graph();
        var outHits = hits.Select((h, i) => HitDto(bank, g, h, i + 1)).ToList();
        return new SearchResponse(text, mode, note, qv != null, Math.Round(sw.Elapsed.TotalMilliseconds, 1), outHits, tree);
    }

    /// <summary>
    /// The query embedded (null when the call fails: the index then ranks lexically), the RAPTOR lists fused in when there is a tree
    /// (its own candidates go to <paramref name="tree"/>), and the hits.
    /// </summary>
    private static async Task<(List<RagHit> Hits, float[]? Qv)> HybridSearchAsync(MemoryBank bank, RagQuery q, string text, List<TreeHitDto> tree,
        CancellationToken ct)
    {
        float[]? qv;
        try { qv = await bank.Rag.EmbedQueryAsync(text, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { qv = null; }
        SearchAugment? aug = null;
        if (bank.Raptor is { } raptor && qv != null)
        {
            aug = raptor.Augment(q, qv);
            tree.AddRange(TreeHits(raptor, qv, q));
        }
        var hits = await Task.Run(() => bank.Rag.Search(q, qv, aug), ct).ConfigureAwait(false);
        return (hits, qv);
    }

    private static SearchHitDto HitDto(MemoryBank bank, GraphModel? g, RagHit h, int rank)
    {
        int? leaf = bank.NodeOfDoc(h.Id);
        var path = leaf is { } n
            ? bank.ReadTree(t => RaptorViews.Path(t, g, n).Where(t.AllNodes.ContainsKey).Select(p => new GraphCrumb(p, t[p].Layer, RaptorViews.CrumbLabel(bank, g, t[p]))).ToList(), [])
            : [];
        var snippet = (h.Snippet ?? h.Body).ReplaceLineEndings(" ");
        if (snippet.Length > 260) snippet = snippet[..259] + "…";
        return new SearchHitDto(rank, h.Id, h.Ref, h.Kind, h.Tag, h.Title, snippet, h.Score,
            new SearchSignals(Finite(h.Lexical), Finite(h.Cosine), h.Distance is { } d ? Finite(d) : null),
            h.Updated.ToUnixTimeMilliseconds(), h.Bounds != null ? h.Dimension : null, Where(h), leaf, path);
    }

    private static double? Finite(double v) => double.IsFinite(v) && v != 0 ? v : null;

    private static string? Where(RagHit h)
    {
        if (h.Bounds is not { } b) return null;
        string P(double x, double y, double z) => string.Create(CultureInfo.InvariantCulture, $"{x:0},{y:0},{z:0}");
        var a = P(b.MinX, b.MinY, b.MinZ);
        var c = P(b.MaxX, b.MaxY, b.MaxZ);
        return (h.Dimension == WorldDim.Nether ? "nether " : "") + (a == c ? a : a + ".." + c);
    }

    /// <summary>The tree's own candidate lists, as RaptorMemory builds them for the fusion.</summary>
    private static IEnumerable<TreeHitDto> TreeHits(RaptorMemory raptor, float[] qv, RagQuery q)
    {
        var (count, dims) = raptor.Index.Read(t => (t.Count, t.Dimensions));
        if (count == 0 || dims != qv.Length) yield break;
        RaptorFilter? filter = q.Kinds is { Count: > 0 } k ? new RaptorFilter { Kinds = k } : null;
        var o = raptor.Options;
        if (raptor.Mode is RaptorMode.Collapsed or RaptorMode.Hybrid)
        {
            var r = raptor.Index.Retrieve(qv, new RaptorQuery { Collapsed = true, TopK = Math.Max(1, o.CandidateTopK), MaxTokens = Math.Max(1, o.CandidateMaxTokens), Filter = filter });
            foreach (var n in r.Nodes) yield return Map("collapsed", n);
        }
        if (raptor.Mode is RaptorMode.Traversal or RaptorMode.Hybrid)
        {
            var r = raptor.Index.Retrieve(qv, new RaptorQuery { Collapsed = false, TopK = Math.Max(1, o.TraversalTopK), Filter = filter });
            foreach (var n in r.Nodes.OrderByDescending(n => n.Score)) yield return Map("traversal", n);
        }
    }

    private static TreeHitDto Map(string list, RetrievedNode n)
    {
        var one = n.Text.ReplaceLineEndings(" ");
        return new TreeHitDto(list, n.Index, n.Layer, n.Score, one.Length > 140 ? one[..140] + "…" : one);
    }
}
