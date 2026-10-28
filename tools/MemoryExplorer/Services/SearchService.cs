using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;

namespace MineClaw.MemoryExplorer.Services;

public enum SearchMode { Hybrid, Lexical }

/// <summary>One ranked hit with its RAPTOR path (root → … → the node, when the doc is in the tree).</summary>
public sealed record SearchHit(int Rank, RagHit Hit, IReadOnlyList<int> TreePath);

/// <summary>A node the tree retrieval itself returned (collapsed or traversal list), before fusion.</summary>
public sealed record TreeHit(string List, int Index, int Layer, bool Leaf, double Score, string Preview);

public sealed record SearchOutcome(SearchMode Mode, string ModeNote, IReadOnlyList<SearchHit> Hits, IReadOnlyList<TreeHit> TreeHits, TimeSpan Elapsed, bool QueryEmbedded);

/// <summary>
/// The bot's own retrieval, offline: <see cref="RagIndex.SearchAsync(RagQuery, CancellationToken)"/> with the RAPTOR augmenter —
/// FTS5/BM25 + flat cosine + RAPTOR collapsed/traversal lists fused by RRF — when query embedding is configured and the stored
/// vectors match. Otherwise the same index runs lexical-only (FTS + spatial), and the outcome says so.
/// </summary>
public sealed class SearchService(MemoryBank bank)
{
    public SearchMode Mode => bank.VectorSearch ? SearchMode.Hybrid : SearchMode.Lexical;

    public string ModeNote => bank.VectorSearch
        ? (bank.HasTree ? "hybrid: FTS/BM25 + vectors + RAPTOR tree (the bot's recall pipeline)" : "hybrid: FTS/BM25 + vectors (no RAPTOR tree in this db)")
        : bank.Embeddings.IsAvailable
            ? "lexical fallback: the stored vectors were made by another embedding model"
            : "lexical fallback: query embedding isn't configured (Agent:Embeddings), so only FTS/BM25 ranks";

    public async Task<SearchOutcome> SearchAsync(string text, IReadOnlyCollection<string>? kinds = null, int? dimension = null, int limit = 25,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var q = new RagQuery { Text = text, Kinds = kinds is { Count: > 0 } ? kinds : null, Dimension = dimension ?? WorldDim.Any, Limit = limit };
        float[]? qv = null;
        List<RagHit> hits;
        var tree = new List<TreeHit>();
        if (bank.VectorSearch)
        {
            qv = await bank.Rag.EmbedQueryAsync(text, ct).ConfigureAwait(false);
            SearchAugment? aug = null;
            if (bank.Raptor is { } raptor && qv != null)
            {
                aug = raptor.Augment(q, qv);
                tree.AddRange(TreeHits(raptor, qv, q));
            }
            hits = await Task.Run(() => bank.Rag.Search(q, qv, aug), ct).ConfigureAwait(false);
        }
        else hits = await Task.Run(() => bank.Rag.Search(q), ct).ConfigureAwait(false);

        var outHits = hits.Select((h, i) => new SearchHit(i + 1, h, bank.NodeOfDoc(h.Id) is { } n ? bank.PathToRoot(n) : [])).ToList();
        var mode = qv != null ? SearchMode.Hybrid : SearchMode.Lexical;
        var note = qv == null && bank.VectorSearch ? "lexical fallback: the query embedding call failed or timed out" : ModeNote;
        return new SearchOutcome(mode, note, outHits, tree, sw.Elapsed, qv != null);
    }

    /// <summary>The tree's own candidate lists, as RaptorMemory builds them for the fusion (what the tree contributed).</summary>
    private static IEnumerable<TreeHit> TreeHits(RaptorMemory raptor, float[] qv, RagQuery q)
    {
        var (count, dims) = raptor.Index.Read(t => (t.Count, t.Dimensions));
        if (count == 0 || dims != qv.Length) yield break;
        RaptorFilter? filter = q.Kinds is { Count: > 0 } k ? new RaptorFilter { Kinds = k } : null;
        var o = raptor.Options;
        if (raptor.Mode is RaptorMode.Collapsed or RaptorMode.Hybrid)
        {
            var r = raptor.Index.Retrieve(qv, new RaptorQuery { Collapsed = true, TopK = Math.Max(1, o.CandidateTopK), MaxTokens = Math.Max(1, o.CandidateMaxTokens), Filter = filter });
            foreach (var n in r.Nodes) yield return Map("collapsed", raptor, n);
        }
        if (raptor.Mode is RaptorMode.Traversal or RaptorMode.Hybrid)
        {
            var r = raptor.Index.Retrieve(qv, new RaptorQuery { Collapsed = false, TopK = Math.Max(1, o.TraversalTopK), Filter = filter });
            foreach (var n in r.Nodes.OrderByDescending(n => n.Score)) yield return Map("traversal", raptor, n);
        }
    }

    private static TreeHit Map(string list, RaptorMemory raptor, RetrievedNode n)
    {
        var one = n.Text.ReplaceLineEndings(" ");
        return new TreeHit(list, n.Index, n.Layer, n.Leaf, n.Score, one.Length > 140 ? one[..140] + "…" : one);
    }
}
