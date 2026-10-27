using MineClaw.Agent.Budget;
using MineClaw.Raptor;

namespace MineClaw.Agent.Rag.Raptor;

/// <summary>
/// RAPTOR <see cref="IEmbedder"/> over the agent's <see cref="IEmbeddingProvider"/> (Azure text-embedding-3-large),
/// reusing the <c>embed_cache</c> table (keyed by exact text hash + model + dims), batching by
/// <see cref="EmbeddingOptions.MaxBatch"/> and reporting tokens to <see cref="UsageTracker"/>. Vectors are returned
/// L2-normalized.
/// </summary>
public sealed class CachedEmbedder : IEmbedder
{
    private readonly AgentDb _db;
    private readonly IEmbeddingProvider _provider;
    private readonly EmbeddingOptions _o;
    private readonly UsageTracker? _usage;

    public CachedEmbedder(AgentDb db, IEmbeddingProvider provider, EmbeddingOptions? options = null, UsageTracker? usage = null)
    {
        _db = db;
        _provider = provider;
        _o = options ?? new EmbeddingOptions();
        _usage = usage;
    }

    public string Model => _provider.Model;
    public int Dimensions => _provider.Dimensions;

    public long Tokens { get; private set; }
    public int CacheHits { get; private set; }
    public int Embedded { get; private set; }
    public decimal CostUsd { get; private set; }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new float[texts.Count][];
        var hashes = texts.Select(t => VectorMath.Hash(t)).ToArray();
        var need = new List<int>();
        var firstOf = new Dictionary<string, int>();
        for (int i = 0; i < texts.Count; i++)
        {
            if (_db.Scalar("SELECT vec FROM embed_cache WHERE hash = $h AND model = $m AND dims = $d",
                    ("$h", hashes[i]), ("$m", Model), ("$d", Dimensions)) is byte[] blob)
            {
                result[i] = VectorMath.FromBlob(blob);
                CacheHits++;
            }
            else if (firstOf.TryAdd(hashes[i], i)) need.Add(i);
        }

        long tokens = 0;
        int batch = Math.Max(1, _o.MaxBatch);
        for (int s = 0; s < need.Count; s += batch)
        {
            var chunk = need.Skip(s).Take(batch).ToList();
            var resp = await _provider.EmbedAsync(chunk.Select(i => texts[i]).ToList(), ct).ConfigureAwait(false);
            tokens += resp.Tokens;
            if (resp.Tokens > 0)
            {
                var cost = _usage?.RecordEmbedding(Model, resp.Tokens, _o.PricePerMTok) ?? resp.Tokens * _o.PricePerMTok / 1_000_000m;
                CostUsd += cost;
            }
            long now = AgentDb.ToUnixMs(DateTimeOffset.UtcNow);
            _db.Tx(() =>
            {
                for (int k = 0; k < chunk.Count; k++)
                {
                    var v = VectorMath.Normalize(resp.Vectors[k]);
                    result[chunk[k]] = v;
                    _db.Exec("INSERT OR REPLACE INTO embed_cache(hash, model, dims, vec, created) VALUES ($h, $m, $d, $v, $t)",
                        ("$h", hashes[chunk[k]]), ("$m", Model), ("$d", Dimensions), ("$v", VectorMath.ToBlob(v)), ("$t", now));
                }
            });
            Embedded += chunk.Count;
        }
        for (int i = 0; i < texts.Count; i++) result[i] ??= result[firstOf[hashes[i]]];
        Tokens += tokens;
        return new EmbeddingResult(result, tokens);
    }
}
