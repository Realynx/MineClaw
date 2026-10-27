using System.Collections.Concurrent;
using System.Text;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Rag.Raptor;

public sealed record RaptorSyncReport(int Inserted, int Reinserted, int Removed, long EmbeddingTokens)
{
    public bool Any => Inserted + Reinserted + Removed > 0;
}

/// <summary>
/// The agent's RAPTOR tree: a <see cref="RaptorIndex"/> persisted in <see cref="AgentDb"/> through
/// <see cref="AgentDbRaptorStore"/>, kept in sync with the eligible docs of <see cref="RagIndex"/>
/// (<see cref="RaptorTreePolicy"/>), and plugged into hybrid search as an <see cref="ISearchAugmenter"/>: its
/// collapsed and/or traversal list is fused with the FTS / spatial / flat-vector lists by RRF (tree members leave the
/// flat vector list, which they would duplicate). Leaves reuse the flat index's vectors, so inserting costs no tokens.
/// </summary>
public sealed class RaptorMemory : ISearchAugmenter, IDisposable
{
    public const string CollapsedList = "raptor-collapsed";
    public const string TraversalList = "raptor-traversal";

    private readonly AgentDb _db;
    private readonly RagIndex _rag;
    private readonly RaptorMemoryOptions _o;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<string> _removed = new();
    private readonly object _loadLock = new();
    private Task<bool>? _loading;
    private volatile bool _ready;

    public RaptorMemory(AgentDb db, RagIndex rag, IEmbedder embedder, ISummarizer summarizer, RaptorMemoryOptions? options = null,
        RaptorOptions? raptorOptions = null, ILogger? log = null)
    {
        _db = db;
        _rag = rag;
        _o = options ?? new RaptorMemoryOptions();
        _log = log ?? NullLogger.Instance;
        Policy = new RaptorTreePolicy(_o);
        Store = new AgentDbRaptorStore(db);
        Embedder = embedder;
        Summarizer = summarizer;
        int threads = _o.MaxThreads > 0 ? _o.MaxThreads : Math.Max(1, Environment.ProcessorCount / 2);
        Index = new RaptorIndex(embedder, summarizer, (raptorOptions ?? new RaptorOptions()) with { MaxDegreeOfParallelism = threads }, store: Store);
        Mode = _o.ParsedMode;
        rag.Deleted += OnDeleted;
    }

    public RaptorIndex Index { get; }
    public AgentDbRaptorStore Store { get; }
    public RaptorTreePolicy Policy { get; }
    public IEmbedder Embedder { get; }
    public ISummarizer Summarizer { get; }
    public RaptorMemoryOptions Options => _o;
    /// <summary>Retrieval mode; switchable at runtime.</summary>
    public RaptorMode Mode { get; set; }
    /// <summary>True once the stored tree was loaded (or found absent); until then search is flat-only.</summary>
    public bool Ready => _ready;

    private void OnDeleted(IReadOnlyList<string> refs)
    {
        foreach (var r in refs) _removed.Enqueue(r);
    }

    // ======================= lifecycle =======================

    /// <summary>Loads the stored tree once, on the thread pool (idempotent; search stays flat-only until it finishes).</summary>
    public Task<bool> StartLoading(CancellationToken ct = default)
    {
        lock (_loadLock) return _loading ??= Task.Run(() => LoadAsync(ct), ct);
    }

    /// <summary>
    /// Loads the stored tree. A tree built with another embedding model/dimension is discarded (leaves are re-inserted
    /// by <see cref="SyncAsync"/> and the next rebuild re-summarizes).
    /// </summary>
    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        bool loaded = await Index.LoadAsync(ct).ConfigureAwait(false);
        if (!loaded && _db.GetMeta(AgentDbRaptorStore.MetaKey) != null)
        {
            _log.LogInformation("raptor tree was built with another embedding model; discarding it");
            Store.Clear();
        }
        _ready = true;
        if (loaded)
            _log.LogInformation("raptor tree loaded: {Leaves} leaves, {Nodes} nodes, {Layers} layers", Index.LeafCount, Index.NodeCount, Index.NumLayers);
        return loaded;
    }

    /// <summary>Faithful static build over every eligible doc that has a vector (seed builder, forced rebuild).</summary>
    public async Task<BuildStats> BuildAsync(RaptorBudget? budget = null, CancellationToken ct = default)
    {
        var docs = EligibleDocs(onlyPending: false, limit: int.MaxValue);
        var stats = await Index.BuildAsync(docs.Select(ToDocument), budget, ct).ConfigureAwait(false);
        while (_removed.TryDequeue(out _)) { }
        _ready = true;
        return stats;
    }

    // ======================= doc → tree sync =======================

    private sealed record DocRow(long Id, string Ref, string Kind, string? Source, string Title, string Body, float[] Vec, bool Mapped);

    private static RaptorDocument ToDocument(DocRow d) =>
        new(RaptorTreePolicy.LeafText(d.Title, d.Body), d.Ref, d.Kind, d.Source) { Chunk = false, Embedding = d.Vec };

    /// <summary>Eligible level-0 docs with a current vector; <paramref name="onlyPending"/> = not in the tree yet or changed.</summary>
    private List<DocRow> EligibleDocs(bool onlyPending, int limit)
    {
        var args = new List<(string, object?)> { ("$m", Embedder.Model), ("$d", Embedder.Dimensions), ("$n", limit) };
        var sql = new StringBuilder("""
            SELECT d.id, d.ref, d.kind, d.source, d.title, d.body, v.vec, d.raptor_idx IS NOT NULL
            FROM docs d JOIN vectors v ON v.doc_id = d.id AND v.model = $m AND v.dims = $d AND v.hash = d.hash
            WHERE d.level = 0
            """);
        var kinds = Policy.Kinds.ToArray();
        if (kinds.Length == 0) return [];
        sql.Append(" AND d.kind IN (").Append(string.Join(",", kinds.Select((_, i) => "$k" + i))).Append(')');
        for (int i = 0; i < kinds.Length; i++) args.Add(("$k" + i, kinds[i]));
        var sources = Policy.ExcludedSources.ToArray();
        if (sources.Length > 0)
        {
            sql.Append(" AND (d.source IS NULL OR d.source NOT IN (").Append(string.Join(",", sources.Select((_, i) => "$s" + i))).Append("))");
            for (int i = 0; i < sources.Length; i++) args.Add(("$s" + i, sources[i]));
        }
        for (int i = 0; i < Policy.ExcludedRefPrefixes.Count; i++)
        {
            sql.Append($" AND lower(substr(d.ref, 1, length($p{i}))) <> lower($p{i})");
            args.Add(("$p" + i, Policy.ExcludedRefPrefixes[i]));
        }
        if (onlyPending) sql.Append(" AND (d.raptor_idx IS NULL OR d.dirty = 1)");
        sql.Append(" ORDER BY d.id LIMIT $n");
        return _db.Query(sql.ToString(), r => new DocRow(r.GetInt64(0), r.GetString(1), r.GetString(2), AgentDb.StrOrNull(r, 3), r.GetString(4),
                r.GetString(5), VectorMath.FromBlob((byte[])r.GetValue(6)), r.GetInt64(7) != 0), [.. args])
            .Where(d => Policy.Includes(d.Ref, d.Kind, d.Source, 0)).ToList();
    }

    /// <summary>
    /// One incremental step: removes leaves of deleted docs, re-inserts changed docs and inserts new ones (at most
    /// <see cref="RaptorMemoryOptions.InsertBatch"/>). Zero LLM tokens: leaves reuse stored vectors; the parents they
    /// attach to become dirty for <see cref="RaptorIndex.RefreshDirtyAsync"/>.
    /// </summary>
    public async Task<RaptorSyncReport> SyncAsync(CancellationToken ct = default)
    {
        if (!_ready) return new RaptorSyncReport(0, 0, 0, 0);
        var inTree = Index.Read(t => t.Leaves.Where(l => l.Key != null).Select(l => l.Key!).ToHashSet(StringComparer.Ordinal));

        var gone = new HashSet<string>(StringComparer.Ordinal);
        while (_removed.TryDequeue(out var r)) if (inTree.Contains(r)) gone.Add(r);
        int removed = gone.Count > 0 ? await Index.RemoveAsync(gone, ct).ConfigureAwait(false) : 0;

        var batch = EligibleDocs(onlyPending: true, limit: Math.Max(1, _o.InsertBatch));
        var changed = batch.Where(d => inTree.Contains(d.Ref)).Select(d => d.Ref).ToList();
        if (changed.Count > 0) await Index.RemoveAsync(changed, ct).ConfigureAwait(false);
        long tokens = 0;
        if (batch.Count > 0)
        {
            var report = await Index.InsertAsync(batch.Select(ToDocument), ct).ConfigureAwait(false);
            tokens = report.EmbeddingTokens;
        }
        return new RaptorSyncReport(batch.Count - changed.Count, changed.Count, removed, tokens);
    }

    // ======================= retrieval =======================

    public SearchAugment? Augment(RagQuery q, float[]? queryVector)
    {
        if (!_ready || queryVector == null || queryVector.Length == 0) return null;
        if (q.Tags is { Count: > 0 }) return null; // tag filters target world observations, which are not in the tree
        var (count, dims) = Index.Read(t => (t.Count, t.Dimensions));
        if (count == 0 || dims != queryVector.Length) return null;
        RaptorFilter? filter = null;
        if (q.Kinds is { Count: > 0 } kinds)
        {
            if (!kinds.Any(k => Policy.Kinds.Contains(k))) return null;
            filter = new RaptorFilter { Kinds = kinds };
        }
        bool summaries = _o.IncludeSummaries && filter == null;
        var lists = new List<RankedList>(2);
        if (Mode is RaptorMode.Collapsed or RaptorMode.Hybrid)
        {
            var r = Index.Retrieve(queryVector, new RaptorQuery
            {
                Collapsed = true, TopK = Math.Max(1, _o.CandidateTopK), MaxTokens = Math.Max(1, _o.CandidateMaxTokens), Filter = filter,
            });
            lists.Add(Map(CollapsedList, r.Nodes, summaries));
        }
        if (Mode is RaptorMode.Traversal or RaptorMode.Hybrid)
        {
            var r = Index.Retrieve(queryVector, new RaptorQuery { Collapsed = false, TopK = Math.Max(1, _o.TraversalTopK), Filter = filter });
            lists.Add(Map(TraversalList, r.Nodes.OrderByDescending(n => n.Score).ThenBy(n => n.Index).ToList(), summaries));
        }
        return new SearchAugment(lists, Store.ContainsDoc);
    }

    private RankedList Map(string name, IReadOnlyList<RetrievedNode> nodes, bool summaries)
    {
        var items = new List<(long, double)>(nodes.Count);
        foreach (var n in nodes)
        {
            if (!n.Leaf && !summaries) continue;
            if (Store.TryGetDocId(n.Index, out var id)) items.Add((id, n.Score));
        }
        return new RankedList(name, items);
    }

    public void Dispose()
    {
        _rag.Deleted -= OnDeleted;
        Index.Dispose();
    }
}

/// <summary>
/// <see cref="IRetrievalIndex"/> adapter: hybrid FTS + spatial + flat vectors from <see cref="RagIndex"/> fused by RRF
/// with the RAPTOR tree's ranked list(s). Used by auto-retrieval; <c>recall</c> and <c>ask_memory</c> reach the same
/// pipeline through <see cref="RagIndex.Augmenter"/>.
/// </summary>
public sealed class RaptorRetrievalIndex(RagIndex rag, RaptorMemory raptor) : IRetrievalIndex
{
    public Task<List<RagHit>> SearchAsync(RagQuery query, CancellationToken ct = default) => rag.SearchAsync(query, raptor, ct);

    public string FormatHit(RagHit hit, int maxChars) => rag.FormatHit(hit, maxChars);
}
