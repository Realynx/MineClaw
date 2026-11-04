using System.Diagnostics;
using System.Text;
using MineClaw.Raptor.Clustering;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Numerics;
using MineClaw.Raptor.Text;

namespace MineClaw.Raptor;

/// <summary>Counters for one build / rebuild.</summary>
public sealed record BuildStats
{
    public int Leaves { get; init; }
    public int NumLayers { get; init; }
    public IReadOnlyList<int> NodesPerLayer { get; init; } = [];
    public int Summaries { get; init; }
    /// <summary>Summaries copied from the previous tree because the cluster text was identical (rebuild only).</summary>
    public int ReusedSummaries { get; init; }
    /// <summary>Clusters that got an extractive placeholder because the budget was exhausted (SummaryPending).</summary>
    public int Placeholders { get; init; }
    public int SummaryFailures { get; init; }
    public int Reclusters { get; init; }
    public int FallbackSplits { get; init; }
    public long EmbeddingTokens { get; init; }
    public TimeSpan ClusteringTime { get; init; }
    public TimeSpan SummaryTime { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// tree_builder.py + cluster_tree_builder.py: chunk → embed leaves → for each layer (≤ num_layers): stop when the
/// layer has ≤ reduction_dimension + 1 nodes, else RAPTOR-cluster it, summarize each cluster with the paper's prompt
/// (max summarization_length tokens), embed the summaries, and make them the next layer. Node indices follow the
/// reference: leaves 0…n−1, then each new node takes the next index in cluster order.
/// </summary>
public sealed class RaptorTreeBuilder
{
    private readonly IEmbedder _embedder;
    private readonly ISummarizer _summarizer;
    private readonly RaptorOptions _o;
    private readonly ITokenCounter _tok;

    public RaptorTreeBuilder(IEmbedder embedder, ISummarizer summarizer, RaptorOptions? options = null, ITokenCounter? tokenCounter = null)
    {
        _embedder = embedder;
        _summarizer = summarizer;
        _o = options ?? new RaptorOptions();
        _tok = tokenCounter ?? ApproxTokenCounter.Instance;
    }

    public RaptorOptions Options => _o;
    public ITokenCounter TokenCounter => _tok;

    /// <summary>utils.py get_text: each node's text with newlines flattened to spaces, followed by a blank line.</summary>
    public static string GetText(IEnumerable<RaptorNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes)
        {
            sb.Append(string.Join(' ', n.Text.Split('\n', '\r').Where(s => s.Length > 0)));
            sb.Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>build_from_text: one document, chunked by split_text.</summary>
    public Task<(RaptorTree Tree, BuildStats Stats)> BuildFromTextAsync(string text, RaptorBudget? budget = null, CancellationToken ct = default)
        => BuildAsync([new RaptorDocument(text)], budget, ct);

    /// <summary>Static faithful build from documents (chunked, embedded, clustered, summarized).</summary>
    public async Task<(RaptorTree Tree, BuildStats Stats)> BuildAsync(IEnumerable<RaptorDocument> documents, RaptorBudget? budget = null, CancellationToken ct = default)
    {
        var (leaves, embedTokens) = await CreateLeavesAsync(documents, 0, null, ct).ConfigureAwait(false);
        var (tree, stats) = await BuildFromLeavesAsync(leaves, budget, ct).ConfigureAwait(false);
        return (tree, stats with { EmbeddingTokens = stats.EmbeddingTokens + embedTokens });
    }

    /// <summary>Chunks and embeds documents into unattached leaf nodes numbered from <paramref name="startIndex"/>.</summary>
    public async Task<(List<RaptorNode> Leaves, long EmbeddingTokens)> CreateLeavesAsync(IEnumerable<RaptorDocument> documents, int startIndex, long? insertSeqStart, CancellationToken ct)
    {
        var leaves = new List<RaptorNode>();
        var toEmbed = new List<int>();
        int index = startIndex;
        long seq = insertSeqStart ?? 0;
        foreach (var doc in documents)
        {
            var chunks = Chunks(doc);
            for (int c = 0; c < chunks.Count; c++)
            {
                bool pre = doc.Embedding != null && chunks.Count == 1;
                var node = NewLeaf(doc, chunks[c], c, index++, pre, insertSeqStart == null ? 0 : ++seq);
                if (!pre) toEmbed.Add(leaves.Count);
                leaves.Add(node);
            }
        }
        var (vecs, tokens) = await EmbedAllAsync(toEmbed.Select(i => leaves[i].Text).ToList(), ct).ConfigureAwait(false);
        for (int t = 0; t < toEmbed.Count; t++) leaves[toEmbed[t]].Embedding = vecs[t];
        return (leaves, tokens);
    }

    private List<string> Chunks(RaptorDocument doc) => doc.Chunk
        ? TextSplitter.Split(doc.Text, _tok, _o.ChunkMaxTokens, _o.ChunkOverlap, _o.Split)
        : string.IsNullOrWhiteSpace(doc.Text) ? [] : [doc.Text];

    /// <summary>An unattached leaf for one chunk; a precomputed embedding is used only for a single-chunk document.</summary>
    private RaptorNode NewLeaf(RaptorDocument doc, string chunk, int c, int index, bool pre, long insertedSeq) =>
        new(index, 0, chunk, pre ? VectorMath.Normalized(doc.Embedding!) : [], _tok.Count(chunk))
        {
            Key = doc.Key,
            Chunk = c,
            Kind = doc.Kind,
            Source = doc.Source,
            Pinned = doc.Pinned,
            InsertedSeq = insertedSeq,
        };

    /// <summary>
    /// Builds a fresh tree over already-embedded leaves (used by the static build and by Rebuild, which keeps leaf
    /// texts and embeddings). Leaves are renumbered 0…n−1 in the given order.
    /// </summary>
    public async Task<(RaptorTree Tree, BuildStats Stats)> BuildFromLeavesAsync(IReadOnlyList<RaptorNode> leaves, RaptorBudget? budget = null, CancellationToken ct = default, SummaryCache? reuse = null)
    {
        var sw = Stopwatch.StartNew();
        budget ??= RaptorBudget.Unlimited();
        var tree = NewTree(leaves);
        var clustering = new RaptorClustering(_o);
        var current = tree.Layer(0).OrderBy(n => n.Index).ToList();
        var tally = new BuildTally();
        long embedTokens = 0;
        TimeSpan clusterTime = TimeSpan.Zero, summaryTime = TimeSpan.Zero;
        tree.NumLayers = 0;
        for (int layer = 0; layer < _o.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            if (current.Count <= _o.ReductionDimension + 1)
            {
                // "Stopping Layer construction: Cannot Create More Layers."
                tree.NumLayers = layer;
                break;
            }
            var csw = Stopwatch.StartNew();
            var clusters = await Task.Run(() => clustering.Cluster(current.Select(n => n.Embedding).ToList(), current.Select(n => n.TokenCount).ToList()), ct).ConfigureAwait(false);
            clusterTime += csw.Elapsed;
            tally.Reclusters += clustering.ReclusterCount;
            tally.Fallback += clustering.FallbackSplits;

            var groups = clusters.Select(c => c.Select(i => current[i]).ToList()).ToList();
            var ssw = Stopwatch.StartNew();
            var results = await SummarizeManyAsync(groups, budget, ct, reuse).ConfigureAwait(false);
            summaryTime += ssw.Elapsed;
            var (vecs, tokens) = await SummaryVectorsAsync(results, ct).ConfigureAwait(false);
            embedTokens += tokens;
            current = AddLayer(tree, layer, groups, results, vecs, tally);
            tree.NumLayers = layer + 1;
        }

        tree.LeavesAtBuild = tree.LeafCount;
        (tree.BaselineLeafParentSimilarity, tree.BaselineLeafParentSimilarityStd) = LeafParentSimilarity(tree);
        tree.InsertTemperature = InsertCalibration.FitTemperature(tree, _o.InsertCandidates);
        var stats = new BuildStats
        {
            Leaves = tree.LeafCount,
            NumLayers = tree.NumLayers,
            NodesPerLayer = Enumerable.Range(0, tree.NumLayers + 1).Select(l => tree.LayerIndices(l).Count).ToList(),
            Summaries = tally.Summaries,
            ReusedSummaries = tally.Reused,
            Placeholders = tally.Placeholders,
            SummaryFailures = tally.Failures,
            Reclusters = tally.Reclusters,
            FallbackSplits = tally.Fallback,
            EmbeddingTokens = embedTokens,
            ClusteringTime = clusterTime,
            SummaryTime = summaryTime,
            Elapsed = sw.Elapsed,
        };
        return (tree, stats);
    }

    /// <summary>Counters for <see cref="BuildStats"/> gathered over the layers of one build.</summary>
    private sealed class BuildTally
    {
        public int Summaries, Placeholders, Failures, Reclusters, Fallback, Reused;

        public void Add(SummaryOutcome r)
        {
            if (r.Summarized) Summaries++;
            if (r.Pending) Placeholders++;
            if (r.Failed) Failures++;
            if (r.Reused) Reused++;
        }
    }

    /// <summary>A tree holding copies of the leaves, renumbered 0…n−1.</summary>
    private RaptorTree NewTree(IReadOnlyList<RaptorNode> leaves)
    {
        var tree = new RaptorTree { EmbeddingModel = _embedder.Model, BuildId = DateTimeOffset.UtcNow.UtcTicks, BuiltAt = DateTimeOffset.UtcNow };
        for (int i = 0; i < leaves.Count; i++)
        {
            var src = leaves[i];
            tree.Add(new RaptorNode(i, 0, src.Text, src.Embedding, src.TokenCount)
            {
                Key = src.Key, Chunk = src.Chunk, Kind = src.Kind, Source = src.Source, Pinned = src.Pinned,
            });
        }
        tree.Dimensions = leaves.Count > 0 ? leaves[0].Embedding.Length : _embedder.Dimensions;
        tree.NextIndex = leaves.Count;
        return tree;
    }

    /// <summary>Summary embeddings: reused ones as they are, the rest embedded in one batch.</summary>
    private async Task<(float[]?[] Vectors, long Tokens)> SummaryVectorsAsync(List<SummaryOutcome> results, CancellationToken ct)
    {
        var need = Enumerable.Range(0, results.Count).Where(g => results[g].Embedding == null).ToList();
        var (fresh, tokens) = await EmbedAllAsync(need.Select(g => results[g].Text).ToList(), ct).ConfigureAwait(false);
        var vecs = results.Select(r => r.Embedding).ToArray();
        for (int t = 0; t < need.Count; t++) vecs[need[t]] = fresh[t];
        return (vecs, tokens);
    }

    /// <summary>One summary node per group (layer + 1), linked to its children; returns the new layer.</summary>
    private List<RaptorNode> AddLayer(RaptorTree tree, int layer, List<List<RaptorNode>> groups, List<SummaryOutcome> results, float[]?[] vecs, BuildTally tally)
    {
        var next = new List<RaptorNode>(groups.Count);
        for (int g = 0; g < groups.Count; g++)
        {
            var r = results[g];
            tally.Add(r);
            var node = new RaptorNode(tree.NextIndex, layer + 1, r.Text, vecs[g]!, _tok.Count(r.Text)) { SummaryPending = r.Pending };
            tree.Add(node);
            foreach (var child in groups[g]) tree.Link(node.Index, child.Index);
            next.Add(node);
        }
        return next;
    }

    internal static (double Mean, double Std) LeafParentSimilarity(RaptorTree tree)
    {
        double sum = 0, sq = 0;
        long count = 0;
        foreach (var leaf in tree.Leaves)
            foreach (var p in leaf.Parents)
            {
                double s = VectorMath.Dot(leaf.Embedding, tree[p].Embedding);
                sum += s;
                sq += s * s;
                count++;
            }
        if (count == 0) return (0, 0);
        double mean = sum / count;
        return (mean, Math.Sqrt(Math.Max(0, sq / count - mean * mean)));
    }

    internal sealed record SummaryOutcome(string Text, bool Summarized, bool Pending, bool Failed, float[]? Embedding = null, bool Reused = false);

    /// <summary>
    /// Summarizes clusters with bounded concurrency. Budget reservations are made sequentially in cluster order so
    /// which clusters get a real summary is deterministic.
    /// </summary>
    internal async Task<List<SummaryOutcome>> SummarizeManyAsync(IReadOnlyList<IReadOnlyList<RaptorNode>> groups, RaptorBudget budget, CancellationToken ct, SummaryCache? reuse = null)
    {
        var results = new SummaryOutcome[groups.Count];
        var requests = new (SummaryRequest Req, int EstIn, int EstOut, bool Allowed)[groups.Count];
        int sysTokens = _tok.Count(_o.SummarySystemPrompt);
        for (int g = 0; g < groups.Count; g++)
        {
            var context = GetText(groups[g]);
            if (reuse != null && reuse.TryGet(context, out var cached))
            {
                results[g] = new SummaryOutcome(cached.Text, false, false, false, cached.Embedding, true);
                continue;
            }
            var user = _o.UserPrompt(context);
            int estIn = sysTokens + _tok.Count(user);
            int estOut = _o.SummarizationLength;
            bool allowed = budget.TryReserve(estIn, estOut);
            requests[g] = (new SummaryRequest(context, _o.SummarySystemPrompt, user, _o.SummarizationLength), estIn, estOut, allowed);
        }
        using var gate = new SemaphoreSlim(Math.Max(1, _o.SummaryConcurrency));
        var tasks = new List<Task>();
        for (int g = 0; g < groups.Count; g++)
        {
            int gi = g;
            if (results[gi] != null) continue;
            var (req, estIn, estOut, allowed) = requests[g];
            if (!allowed)
            {
                results[gi] = new SummaryOutcome(Placeholder(groups[gi]), false, true, false);
                continue;
            }
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var r = await _summarizer.SummarizeAsync(req, ct).ConfigureAwait(false);
                    budget.Settle(estIn, estOut, r);
                    var text = string.IsNullOrWhiteSpace(r.Text) ? Placeholder(groups[gi]) : r.Text.Trim();
                    results[gi] = new SummaryOutcome(text, true, string.IsNullOrWhiteSpace(r.Text), false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    budget.Release(estIn, estOut);
                    throw;
                }
                catch (Exception)
                {
                    budget.Release(estIn, estOut);
                    results[gi] = new SummaryOutcome(Placeholder(groups[gi]), false, true, true);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    /// <summary>Extractive stand-in (README D10): the head of each child, round-robin, within summarization_length.</summary>
    internal string Placeholder(IReadOnlyList<RaptorNode> group)
    {
        int share = Math.Max(8, _o.SummarizationLength / Math.Max(1, group.Count));
        var parts = new List<string>();
        int total = 0;
        foreach (var n in group)
        {
            var words = n.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            int t = 0;
            foreach (var w in words)
            {
                int c = _tok.Count(" " + w);
                if (t + c > share) break;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w);
                t += c;
            }
            if (sb.Length == 0) continue;
            if (total + t > _o.SummarizationLength) break;
            parts.Add(sb.ToString());
            total += t;
        }
        return string.Join(" | ", parts);
    }

    internal async Task<(float[][] Vectors, long Tokens)> EmbedAllAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new float[texts.Count][];
        if (texts.Count == 0) return (result, 0);
        int bs = Math.Max(1, _o.EmbedBatchSize);
        long tokens = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = new List<Task>();
        for (int start = 0; start < texts.Count; start += bs)
        {
            int s = start, len = Math.Min(bs, texts.Count - start);
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var batch = texts.Skip(s).Take(len).ToList();
                    var r = await _embedder.EmbedAsync(batch, ct).ConfigureAwait(false);
                    if (r.Vectors.Count != len) throw new InvalidOperationException($"Embedder returned {r.Vectors.Count} vectors for {len} inputs.");
                    for (int i = 0; i < len; i++) result[s + i] = VectorMath.Normalized(r.Vectors[i]);
                    Interlocked.Add(ref tokens, r.Tokens);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        int dims = result[0].Length;
        if (result.Any(v => v.Length != dims)) throw new InvalidOperationException("Embedder returned vectors of differing dimensions.");
        return (result, tokens);
    }
}
