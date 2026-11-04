using System.Text;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Rag;

/// <summary>Axis-aligned box in block coordinates (a point is a zero-size box).</summary>
public readonly record struct Box(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public static Box Point(double x, double y, double z) => new(x, y, z, x, y, z);

    public static Box Of(double x1, double y1, double z1, double x2, double y2, double z2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(y1, y2), Math.Max(z1, z2));

    public static Box Around(double x, double y, double z, double r) => new(x - r, y - r, z - r, x + r, y + r, z + r);

    public Box Union(Box o) => new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Min(MinZ, o.MinZ),
        Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY), Math.Max(MaxZ, o.MaxZ));

    /// <summary>Euclidean distance from a point to the box (0 inside).</summary>
    public double Distance(double x, double y, double z)
    {
        double dx = Math.Max(Math.Max(MinX - x, 0), x - MaxX);
        double dy = Math.Max(Math.Max(MinY - y, 0), y - MaxY);
        double dz = Math.Max(Math.Max(MinZ - z, 0), z - MaxZ);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public bool IsPoint => MinX == MaxX && MinY == MaxY && MinZ == MaxZ;

    public string Format() => IsPoint ? $"{MinX:0},{MinY:0},{MinZ:0}" : $"{MinX:0},{MinY:0},{MinZ:0}..{MaxX:0},{MaxY:0},{MaxZ:0}";
}

/// <summary>One searchable record. <see cref="Ref"/> is the stable unique key ("wp:home", "kb:file.md:3", "obs:chest:1,2,3").</summary>
public sealed record RagDoc(string Ref, string Kind, string Title, string Body)
{
    /// <summary>Sub-kind (world observation kind such as "chest").</summary>
    public string? Tag { get; init; }
    public string? Source { get; init; }
    public Box? Bounds { get; init; }
    /// <summary>Dimension of <see cref="Bounds"/> (0 overworld, -1 nether); meaningless for positionless docs.</summary>
    public int Dimension { get; init; }
    /// <summary>False keeps the doc out of the embedding queue (FTS + structured columns only).</summary>
    public bool Embed { get; init; } = true;
    /// <summary>Score multiplier (taught/llm facts outrank automatic observations).</summary>
    public double Weight { get; init; } = 1.0;
    /// <summary>0 = leaf; &gt;0 = summary node of a hierarchical index (children linked via doc_edges).</summary>
    public int Level { get; init; }
    /// <summary>Timestamp for "updated" (defaults to now). Re-upserting identical content with At set just touches it.</summary>
    public DateTimeOffset? At { get; init; }
    /// <summary>Structured hints for the memory groomer (an activity rollup's player/start/end/place); never stored.</summary>
    public IReadOnlyDictionary<string, string>? Meta { get; init; }
}

/// <summary>The pre-processing step between a writer and the docs table (<see cref="Memory.Grooming.MemoryGroomer"/>).</summary>
public interface IDocGroomer
{
    /// <summary>The docs to store for <paramref name="doc"/> (empty = dropped; refs may differ: a merge extends an existing doc).</summary>
    IReadOnlyList<RagDoc> Groom(RagDoc doc);
}

public sealed record RagQuery
{
    public string? Text { get; init; }
    /// <summary>Doc kinds to include (null = all except chat, which lives in its own table).</summary>
    public IReadOnlyCollection<string>? Kinds { get; init; }
    public IReadOnlyCollection<string>? Tags { get; init; }
    public (double X, double Y, double Z)? Near { get; init; }
    public double Radius { get; init; } = 16;
    /// <summary>Kinds eligible for the spatial list (null = same as <see cref="Kinds"/>).</summary>
    public IReadOnlyCollection<string>? NearKinds { get; init; }
    /// <summary>
    /// Only docs with a position in this dimension (positionless docs always pass; episodes pass the text/vector lists and
    /// are tagged "(overworld)" when rendered, but only same-dimension ones are "near"); null or <see cref="WorldDim.Any"/> =
    /// every dimension. Readers that answer "where" (recall, auto-retrieval) pass the bot's current one.
    /// </summary>
    public int? Dimension { get; init; }
    public int Limit { get; init; } = 6;
    /// <summary>Apply the auto-retrieval relevance gate (MinLexical / MinSimilarity) instead of "any match".</summary>
    public bool Strict { get; init; }
    /// <summary>Words never used as search terms (the bot's own names...).</summary>
    public IEnumerable<string>? IgnoreWords { get; init; }
    /// <summary>Docs to skip (dedupe against the context window) before taking the top <see cref="Limit"/>.</summary>
    public Func<long, bool>? Exclude { get; init; }
}

public sealed record RagHit(long Id, string Ref, string Kind, string? Tag, string Title, string Body, string? Snippet,
    double Score, double Lexical, double Cosine, double? Distance, DateTimeOffset Updated, Box? Bounds)
{
    /// <summary>Dimension of <see cref="Bounds"/> (0 for positionless docs).</summary>
    public int Dimension { get; init; }
}

/// <summary>
/// Retrieval seam: the flat hybrid <see cref="RagIndex"/> implements it; a hierarchical (tree/summary-node) retriever
/// can implement it too and be fused with or replace the flat one in <see cref="AutoRetriever"/> and the recall tool.
/// </summary>
public interface IRetrievalIndex
{
    /// <summary>Scored hits (node id + text) for a query with kind/tag/spatial filters; never throws for "no results".</summary>
    Task<List<RagHit>> SearchAsync(RagQuery query, CancellationToken ct = default);

    /// <summary>One compact model-facing line for a hit.</summary>
    string FormatHit(RagHit hit, int maxChars);

    /// <summary>The bot's current dimension, used as the default spatial filter by auto-retrieval (null = unknown: no filter).</summary>
    int? CurrentDim => null;
}

/// <summary>One extra ranked candidate list (best first) for reciprocal-rank fusion, e.g. from the RAPTOR tree.</summary>
/// <param name="Items">Doc ids with the cosine similarity that produced the rank (used by the relevance gates).</param>
public sealed record RankedList(string Name, IReadOnlyList<(long Id, double Cosine)> Items);

/// <summary>Extra lists to fuse into a hybrid search, plus docs whose flat vector match they replace.</summary>
/// <param name="SkipFlatVector">Docs left out of the flat cosine list (already ranked semantically by <see cref="Lists"/>).</param>
public sealed record SearchAugment(IReadOnlyList<RankedList> Lists, Func<long, bool>? SkipFlatVector = null)
{
    public static readonly SearchAugment None = new([]);
}

/// <summary>
/// Adds ranked lists to <see cref="RagIndex.SearchAsync(RagQuery, CancellationToken)"/> (hierarchical / tree retrievers).
/// Gets the query vector the index already computed, so the query is embedded once. Must not throw for "no results".
/// </summary>
public interface ISearchAugmenter
{
    SearchAugment? Augment(RagQuery query, float[]? queryVector);
}

public sealed record ChatRow(long Id, DateTimeOffset At, string Kind, string? Player, string Text)
{
    public string Format() => $"c{Id} {At:MM-dd HH:mm} {Player ?? Kind}: {Text}";
}

/// <summary>
/// Hybrid local retrieval over <see cref="AgentDb"/>: FTS5 BM25 (porter/unicode61) + cosine similarity over
/// cached float32 vectors + R*Tree spatial hits, fused with reciprocal-rank fusion, weighted, recency-boosted for
/// episodes. Embedding happens off the hot path in a background queue, de-duplicated by content hash; when the
/// provider is missing or failing the index silently runs FTS-only. Thread-safe.
/// </summary>
public sealed class RagIndex : IRetrievalIndex, IDisposable
{
    /// <param name="Dim">Dimension of a positioned doc; null for positionless docs (never dimension-filtered).</param>
    private sealed record VecEntry(string Kind, string? Tag, float[] Vec, int? Dim);
    private sealed record Pending(long Id, string Text, string Hash, string Kind, string? Tag, int? Dim);

    private const int CandidateLimit = 40;

    private readonly AgentDb _db;
    private readonly RagOptions _o;
    private readonly IEmbeddingProvider _embedder;
    private readonly EmbeddingOptions _embOptions;
    private readonly UsageTracker? _usage;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Dictionary<long, VecEntry> _vecs = new();
    private readonly object _vecLock = new();
    private readonly Dictionary<string, float[]> _queryCache = new();
    private readonly SemaphoreSlim _embedGate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _worker;
    private DateTimeOffset _disabledUntil = DateTimeOffset.MinValue;
    private int _failures;
    private bool _loggedUnavailable;
    private int _chatInserts;

    public RagIndex(AgentDb db, RagOptions? options = null, IEmbeddingProvider? embedder = null, UsageTracker? usage = null,
        EmbeddingOptions? embeddingOptions = null, TimeProvider? time = null, ILogger? log = null, bool backgroundEmbedding = true)
    {
        _db = db;
        _o = options ?? new RagOptions();
        _embedder = embedder ?? NullEmbeddingProvider.Instance;
        _embOptions = embeddingOptions ?? new EmbeddingOptions();
        _usage = usage;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        LoadVectors();
        if (!_embedder.IsAvailable) _log.LogInformation("embeddings not configured: retrieval runs FTS-only");
        else if (backgroundEmbedding)
        {
            _worker = Task.Run(() => WorkerAsync(_cts.Token));
            SignalEmbedding();
        }
    }

    public AgentDb Db => _db;
    public RagOptions Options => _o;
    /// <summary>Optional extra ranked lists (RAPTOR) fused into every <see cref="SearchAsync(RagQuery, CancellationToken)"/>.</summary>
    public ISearchAugmenter? Augmenter { get; set; }
    /// <summary>Stale-lesson corrections (set by <see cref="KnowledgeCorrections.Apply"/>): superseded hits are dropped from every
    /// search and the correction doc takes their place; matching chat-log lines are annotated. Null = no filtering.</summary>
    public KnowledgeCorrections? Corrections { get; set; }
    /// <summary>Raised after docs are deleted, with their refs (keep handlers tiny; they run on the deleting thread).</summary>
    public event Action<IReadOnlyList<string>>? Deleted;
    public IEmbeddingProvider Embedder => _embedder;
    /// <summary>The bot's current dimension (the host wires it to the client's world): default filter of the "where" readers
    /// and the reference for the "(overworld)" render tag. Overworld when no host sets it.</summary>
    public Func<int> CurrentDimension { get; set; } = () => WorldDim.Overworld;
    int? IRetrievalIndex.CurrentDim => CurrentDimension();
    public bool VectorsAvailable => _embedder.IsAvailable && DateTimeOffset.UtcNow >= _disabledUntil;
    public int VectorCount { get { lock (_vecLock) return _vecs.Count; } }
    /// <summary>Texts actually sent to the provider (cache hits excluded) — for tests/diagnostics.</summary>
    public int EmbeddedTexts { get; private set; }
    public int EmbedRequests { get; private set; }

    private DateTimeOffset Now => _time.GetUtcNow();

    // ======================= writes =======================

    /// <summary>
    /// Every write goes through this groomer first (normalize, de-noise, merge, phrase, size, redact per source); null = stored as
    /// given (tests, tools that write already-groomed docs).
    /// </summary>
    public IDocGroomer? Groomer { get; set; }

    /// <summary>
    /// One shared memory for every hive: in a hive process that isn't main, raw docs go to main (which grooms and stores them, with
    /// provenance) instead of being groomed and written here. Returns the stored ids, or null = not forwarded (main unreachable, or
    /// this thread is inside a db transaction): the doc is then groomed and written locally as before.
    /// </summary>
    public Func<IReadOnlyList<RagDoc>, long[]?>? WriteThrough { get; set; }

    /// <summary>Grooms (see <see cref="Groomer"/>) and stores the doc; returns the id of the (first) stored doc, 0 when dropped.</summary>
    public long Upsert(RagDoc d)
    {
        if (WriteThrough is { } w && w([d]) is { } forwarded) return forwarded.Length > 0 ? forwarded[0] : 0;
        if (Groomer is { } g)
        {
            var docs = g.Groom(d);
            if (docs.Count == 0) return 0;
            if (docs.Count == 1) d = docs[0];
            else
            {
                long first = 0;
                bool any = false;
                _db.Tx(() =>
                {
                    foreach (var x in docs)
                    {
                        long i = UpsertCore(x, out bool s);
                        if (first == 0) first = i;
                        any |= s;
                    }
                });
                if (any) SignalEmbedding();
                return first;
            }
        }
        long id = UpsertCore(d, out bool signal);
        if (signal) SignalEmbedding();
        return id;
    }

    /// <summary>Stores the doc exactly as given (no grooming): the groomer's own output, the compaction.</summary>
    public long UpsertGroomed(RagDoc d)
    {
        long id = UpsertCore(d, out bool signal);
        if (signal) SignalEmbedding();
        return id;
    }

    /// <summary>Batched upsert in one transaction (one embedding wake-up for the whole batch).</summary>
    public int UpsertMany(IEnumerable<RagDoc> docs)
    {
        if (WriteThrough is { } w)
        {
            var list = docs as IReadOnlyList<RagDoc> ?? docs.ToList();
            if (list.Count == 0) return 0;
            if (w(list) != null) return list.Count;
            docs = list;
        }
        bool any = false;
        int n = _db.Tx(() =>
        {
            int c = 0;
            foreach (var raw in docs)
            {
                foreach (var d in Groomer is { } g ? g.Groom(raw) : [raw])
                {
                    UpsertCore(d, out bool s);
                    any |= s;
                }
                c++;
            }
            return c;
        });
        if (any) SignalEmbedding();
        return n;
    }

    private long UpsertCore(RagDoc d, out bool signal)
    {
        signal = false;
        long ms = AgentDb.ToUnixMs(d.At ?? Now);
        string hash = TextTokens.MaterialHash(d.Title, d.Body);
        var b = d.Bounds;
        lock (_db.Gate)
        {
            var existing = _db.Query("SELECT id, kind, tag, title, body, hash, source, embed, weight, min_x, min_y, min_z, max_x, max_y, max_z, dimension FROM docs WHERE ref = $r",
                r => (Id: r.GetInt64(0), Kind: r.GetString(1), Tag: AgentDb.StrOrNull(r, 2), Title: r.GetString(3), Body: r.GetString(4),
                    Hash: r.GetString(5), Source: AgentDb.StrOrNull(r, 6), Embed: r.GetInt64(7) != 0, Weight: r.GetDouble(8),
                    Bounds: r.IsDBNull(9) ? (Box?)null : new Box(r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13), r.GetDouble(14)),
                    Dim: (int)r.GetInt64(15)),
                ("$r", d.Ref)).FirstOrDefault();

            var args = new (string, object?)[]
            {
                ("$ref", d.Ref), ("$kind", d.Kind), ("$tag", d.Tag), ("$title", d.Title), ("$body", d.Body), ("$hash", hash),
                ("$source", d.Source), ("$embed", d.Embed ? 1 : 0), ("$weight", d.Weight), ("$t", ms),
                ("$x0", b?.MinX), ("$y0", b?.MinY), ("$z0", b?.MinZ), ("$x1", b?.MaxX), ("$y1", b?.MaxY), ("$z1", b?.MaxZ),
                ("$level", d.Level), ("$tok", TextUtil.EstimateTokens(d.Title) + TextUtil.EstimateTokens(d.Body)), ("$dim", d.Dimension),
            };
            if (existing.Id == 0)
            {
                _db.Exec("""
                    INSERT INTO docs(ref, kind, tag, title, body, hash, source, embed, weight, created, updated, min_x, min_y, min_z, max_x, max_y, max_z,
                        level, token_count, dirty, dimension)
                    VALUES ($ref, $kind, $tag, $title, $body, $hash, $source, $embed, $weight, $t, $t, $x0, $y0, $z0, $x1, $y1, $z1, $level, $tok, 1, $dim)
                    """, args);
                signal = d.Embed;
                return _db.ScalarLong("SELECT last_insert_rowid()");
            }

            bool same = existing.Kind == d.Kind && existing.Tag == d.Tag && existing.Title == d.Title && existing.Body == d.Body &&
                        existing.Source == d.Source && existing.Embed == d.Embed && existing.Weight == d.Weight && existing.Bounds == b &&
                        existing.Dim == d.Dimension;
            if (same)
            {
                if (d.At != null) _db.Exec("UPDATE docs SET updated = $t WHERE id = $id", ("$t", ms), ("$id", existing.Id));
                return existing.Id;
            }

            bool textChanged = existing.Title != d.Title || existing.Body != d.Body;
            bool boundsChanged = existing.Bounds != b || existing.Dim != d.Dimension;
            var sql = new StringBuilder("UPDATE docs SET kind = $kind, tag = $tag, hash = $hash, source = $source, embed = $embed, weight = $weight, level = $level, updated = $t");
            if (textChanged) sql.Append(", title = $title, body = $body, token_count = $tok, dirty = 1"); // only then do the FTS triggers fire
            if (boundsChanged) sql.Append(", min_x = $x0, min_y = $y0, min_z = $z0, max_x = $x1, max_y = $y1, max_z = $z1, dimension = $dim");
            sql.Append(" WHERE id = $id");
            _db.Exec(sql.ToString(), [.. args, ("$id", existing.Id)]);

            if (existing.Hash != hash || !d.Embed)
            {
                _db.Exec("DELETE FROM vectors WHERE doc_id = $id", ("$id", existing.Id));
                lock (_vecLock) _vecs.Remove(existing.Id);
                signal = d.Embed;
            }
            else if (existing.Kind != d.Kind || existing.Tag != d.Tag || boundsChanged)
            {
                lock (_vecLock)
                    if (_vecs.TryGetValue(existing.Id, out var v)) _vecs[existing.Id] = v with { Kind = d.Kind, Tag = d.Tag, Dim = b != null ? d.Dimension : null };
            }
            return existing.Id;
        }
    }

    public bool Delete(string reference)
    {
        lock (_db.Gate)
        {
            var id = _db.ScalarLong("SELECT id FROM docs WHERE ref = $r", ("$r", reference));
            if (id == 0) return false;
            _db.Exec("DELETE FROM docs WHERE id = $id", ("$id", id));
            lock (_vecLock) _vecs.Remove(id);
        }
        RaiseDeleted([reference]);
        return true;
    }

    private void RaiseDeleted(IReadOnlyList<string> refs)
    {
        if (refs.Count == 0) return;
        try { Deleted?.Invoke(refs); }
        catch (Exception ex) { _log.LogWarning(ex, "doc delete handler failed"); }
    }

    /// <summary>Deletes every doc whose ref starts with <paramref name="prefix"/> except those in <paramref name="keep"/>.</summary>
    public int DeleteByPrefix(string prefix, IReadOnlySet<string>? keep = null)
    {
        var gone = new List<string>();
        lock (_db.Gate)
        {
            var rows = _db.Query("SELECT id, ref FROM docs WHERE substr(ref, 1, length($p)) = $p", r => (r.GetInt64(0), r.GetString(1)), ("$p", prefix));
            _db.Tx(() =>
            {
                foreach (var (id, rf) in rows)
                {
                    if (keep != null && keep.Contains(rf)) continue;
                    _db.Exec("DELETE FROM docs WHERE id = $id", ("$id", id));
                    lock (_vecLock) _vecs.Remove(id);
                    gone.Add(rf);
                }
            });
        }
        RaiseDeleted(gone);
        return gone.Count;
    }

    public int Count(string? kind = null) =>
        (int)(kind == null ? _db.ScalarLong("SELECT count(*) FROM docs") : _db.ScalarLong("SELECT count(*) FROM docs WHERE kind = $k", ("$k", kind)));

    // ======================= reads =======================

    private const string HitColumns = "d.id, d.ref, d.kind, d.tag, d.title, d.body, d.updated, d.weight, d.min_x, d.min_y, d.min_z, d.max_x, d.max_y, d.max_z, d.dimension";

    private static RagHit ReadHit(SqliteDataReader r, double score = 0, string? snippet = null) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), AgentDb.StrOrNull(r, 3), r.GetString(4), r.GetString(5), snippet,
        score, 0, 0, null, AgentDb.FromUnixMs(r.GetInt64(6)),
        r.IsDBNull(8) ? null : new Box(r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13)))
    {
        Dimension = (int)r.GetInt64(14),
    };

    public RagHit? Get(long id) =>
        _db.Query($"SELECT {HitColumns} FROM docs d WHERE d.id = $id", r => ReadHit(r), ("$id", id)).FirstOrDefault();

    public RagHit? GetByRef(string reference) =>
        _db.Query($"SELECT {HitColumns} FROM docs d WHERE d.ref = $r", r => ReadHit(r), ("$r", reference)).FirstOrDefault();

    /// <summary>Newest docs of a kind (optionally since a time / with a ref prefix), newest first.</summary>
    public List<RagHit> Recent(string kind, DateTimeOffset? since = null, int limit = 10, string? refPrefix = null) =>
        _db.Query($"""
            SELECT {HitColumns} FROM docs d WHERE d.kind = $k AND d.updated >= $s
              AND ($p IS NULL OR substr(d.ref, 1, length($p)) = $p)
            ORDER BY d.updated DESC, d.id DESC LIMIT $n
            """, r => ReadHit(r), ("$k", kind), ("$s", since is { } t ? AgentDb.ToUnixMs(t) : 0L), ("$p", refPrefix), ("$n", limit));

    /// <summary>
    /// Hybrid search with query embedding (bounded by <see cref="RagOptions.QueryEmbedTimeoutMs"/>; falls back to FTS-only),
    /// fused with the <see cref="Augmenter"/>'s lists (RAPTOR) when one is set.
    /// </summary>
    public Task<List<RagHit>> SearchAsync(RagQuery q, CancellationToken ct = default) => SearchAsync(q, Augmenter, ct);

    /// <summary>Hybrid search fused with an explicit augmenter (null = flat only).</summary>
    public async Task<List<RagHit>> SearchAsync(RagQuery q, ISearchAugmenter? augmenter, CancellationToken ct = default)
    {
        float[]? qv = null;
        if (!string.IsNullOrWhiteSpace(q.Text)) qv = await EmbedQueryAsync(q.Text!, ct).ConfigureAwait(false);
        SearchAugment? aug = null;
        if (augmenter != null)
        {
            try { aug = augmenter.Augment(q, qv); }
            catch (Exception ex) { _log.LogWarning(ex, "search augmenter failed; flat retrieval only"); }
        }
        return Search(q, qv, aug);
    }

    /// <summary>
    /// Synchronous hybrid search; pass a query vector to include the semantic list. <paramref name="augment"/> adds
    /// further ranked lists (RAPTOR) to the reciprocal-rank fusion; their cosines feed the same relevance gates.
    /// </summary>
    public List<RagHit> Search(RagQuery q, float[]? queryVector = null, SearchAugment? augment = null)
    {
        var terms = TextTokens.Terms(q.Text, q.IgnoreWords);
        var s = new SearchLists
        {
            Kinds = q.Kinds?.ToArray(),
            Tags = q.Tags?.ToArray(),
            Dim = q.Dimension is { } qd && qd != WorldDim.Any ? qd : null,
        };
        lock (_db.Gate)
        {
            AddLexicalList(s, terms);
            AddSpatialList(s, q);
        }
        AddSemanticList(s, queryVector, augment);
        AddAugmentLists(s, augment);
        if (s.Lists.Count == 0) return [];

        var hits = ScoreHits(q, s, Fuse(s.Lists));
        IEnumerable<RagHit> ranked = hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Updated);
        if (Corrections is { } cx) ranked = cx.Filter(this, ranked, q.Exclude); // retrieval time: catches later writes too
        return ranked.Take(Math.Max(1, q.Limit)).ToList();
    }

    /// <summary>One search's ranked candidate lists (fused by RRF) and the per-doc evidence the relevance gates read.</summary>
    private sealed class SearchLists
    {
        public required string[]? Kinds;
        public required string[]? Tags;
        public required int? Dim;
        public readonly List<List<long>> Lists = new(3);
        public readonly Dictionary<long, double> Lexical = new();
        public readonly Dictionary<long, string> Snippets = new();
        public readonly Dictionary<long, double> Distances = new();
        public readonly Dictionary<long, double> Cosines = new();
    }

    private void AddLexicalList(SearchLists s, List<string> terms)
    {
        if (terms.Count == 0) return;
        var order = _db.HasFts ? FtsCandidates(terms, s.Kinds, s.Tags, s.Dim, s.Snippets, s.Lexical) : LikeCandidates(terms, s.Kinds, s.Tags, s.Dim, s.Lexical);
        if (order.Count > 0) s.Lists.Add(order);
    }

    private void AddSpatialList(SearchLists s, RagQuery q)
    {
        if (q.Near is not { } p || !(q.Radius > 0)) return;
        var near = Spatial(p.X, p.Y, p.Z, q.Radius, q.NearKinds?.ToArray() ?? s.Kinds, s.Tags, CandidateLimit, s.Dim ?? WorldDim.Any);
        foreach (var (id, d) in near) s.Distances[id] = d;
        if (near.Count > 0) s.Lists.Add(near.Select(n => n.Id).ToList());
    }

    private void AddSemanticList(SearchLists s, float[]? queryVector, SearchAugment? augment)
    {
        if (queryVector == null || queryVector.Length == 0) return;
        var sem = VectorTop(VectorMath.Normalize(queryVector), s.Kinds, s.Tags, CandidateLimit, _o.MinSimilarity * 0.75, augment?.SkipFlatVector, s.Dim);
        foreach (var (id, c) in sem) s.Cosines[id] = c;
        if (sem.Count > 0) s.Lists.Add(sem.Select(x => x.Id).ToList());
    }

    /// <summary>The augmenter's lists (RAPTOR): each id once, best rank first; their cosines feed the same gates.</summary>
    private static void AddAugmentLists(SearchLists s, SearchAugment? augment)
    {
        if (augment == null) return;
        foreach (var extra in augment.Lists)
        {
            if (extra.Items.Count == 0) continue;
            var ids = new List<long>(extra.Items.Count);
            var seen = new HashSet<long>();
            foreach (var (id, c) in extra.Items)
            {
                if (!seen.Add(id)) continue;
                ids.Add(id);
                if (c > s.Cosines.GetValueOrDefault(id)) s.Cosines[id] = c;
            }
            s.Lists.Add(ids);
        }
    }

    /// <summary>Reciprocal-rank fusion: Σ 1 / (k + rank) over every list a doc appears in.</summary>
    private Dictionary<long, double> Fuse(List<List<long>> lists)
    {
        var fused = new Dictionary<long, double>();
        foreach (var list in lists)
            for (int rank = 0; rank < list.Count; rank++)
                fused[list[rank]] = fused.GetValueOrDefault(list[rank]) + 1.0 / (_o.RrfK + rank + 1);
        return fused;
    }

    private List<RagHit> ScoreHits(RagQuery q, SearchLists s, Dictionary<long, double> fused)
    {
        var meta = LoadHits(fused.Keys);
        var now = Now;
        var hits = new List<RagHit>(meta.Count);
        foreach (var (h, weight) in meta)
            if (ScoreHit(q, s, h, fused[h.Id] * weight, now) is { } hit) hits.Add(hit);
        return hits;
    }

    /// <summary>A candidate past the relevance, dimension and exclude gates, scored (episodes get a recency boost); else null.</summary>
    private RagHit? ScoreHit(RagQuery q, SearchLists s, RagHit h, double score, DateTimeOffset now)
    {
        double lex = s.Lexical.GetValueOrDefault(h.Id);
        double cos = s.Cosines.GetValueOrDefault(h.Id);
        double? dist = s.Distances.TryGetValue(h.Id, out var dd) ? dd : null;
        if (!Relevant(q.Strict, lex, cos, dist)) return null;
        // Positioned docs of another dimension are dropped (e.g. RAPTOR leaves); episodes stay (tagged when rendered).
        if (OtherDimension(s.Dim, h)) return null;
        if (q.Exclude?.Invoke(h.Id) == true) return null;
        return h with { Score = WithRecency(h, score, now), Lexical = lex, Cosine = cos, Distance = dist, Snippet = s.Snippets.GetValueOrDefault(h.Id) };
    }

    private static bool OtherDimension(int? dim, RagHit h) =>
        dim is { } wantDim && h.Bounds != null && h.Dimension != wantDim && h.Kind != EpisodeKind;

    /// <summary>Episodes get a recency boost that halves every <see cref="RagOptions.RecencyHalfLifeHours"/>.</summary>
    private double WithRecency(RagHit h, double score, DateTimeOffset now)
    {
        if (h.Kind != "episode" || !(_o.RecencyBoost > 0) || !(_o.RecencyHalfLifeHours > 0)) return score;
        double ageH = Math.Max(0, (now - h.Updated).TotalHours);
        return score * (1 + _o.RecencyBoost * Math.Pow(0.5, ageH / _o.RecencyHalfLifeHours));
    }

    /// <summary>Strict = the auto-retrieval gate (MinLexical / MinSimilarity); else any lexical match counts.</summary>
    private bool Relevant(bool strict, double lex, double cos, double? dist) =>
        strict ? lex >= _o.MinLexical || cos >= _o.MinSimilarity || dist != null
            : lex > 0 || cos >= _o.MinSimilarity || dist != null;

    private static string KindFilter(string[]? kinds, string[]? tags, List<(string, object?)> args, int? dim = null)
    {
        var sb = new StringBuilder();
        if (dim is { } dm)
        {
            sb.Append(" AND (d.min_x IS NULL OR d.dimension = $dim OR d.kind = 'episode')");
            args.Add(("$dim", dm));
        }
        if (kinds is { Length: > 0 })
        {
            sb.Append(" AND d.kind IN (").Append(string.Join(",", kinds.Select((_, i) => "$k" + i))).Append(')');
            for (int i = 0; i < kinds.Length; i++) args.Add(("$k" + i, kinds[i]));
        }
        else sb.Append(" AND d.kind <> 'chat'");
        if (tags is { Length: > 0 })
        {
            sb.Append(" AND d.tag IN (").Append(string.Join(",", tags.Select((_, i) => "$g" + i))).Append(')');
            for (int i = 0; i < tags.Length; i++) args.Add(("$g" + i, tags[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Lexical relevance: 1 − exp(−Σ idf(matched terms) / 2), with N floored at 50 (small corpora stay meaningful)
    /// and terms present in over half the docs ignored — length-independent, so a long event text isn't penalised.
    /// </summary>
    private static double Relevance(IEnumerable<int> dfs, long n)
    {
        double sum = 0;
        long nn = Math.Max(50, n);
        foreach (var df in dfs)
        {
            if (n >= 4 && df > n * 0.5) continue;
            sum += Math.Log((nn - df + 0.5) / (df + 0.5) + 1);
        }
        return 1 - Math.Exp(-sum / 2.0);
    }

    private List<long> FtsCandidates(List<string> terms, string[]? kinds, string[]? tags, int? dim, Dictionary<long, string> snippets, Dictionary<long, double> lexical)
    {
        var args = new List<(string, object?)> { ("$q", TextTokens.FtsQuery(terms)), ("$n", CandidateLimit) };
        var filter = KindFilter(kinds, tags, args, dim);
        List<(long Id, string Snip)> rows;
        try
        {
            rows = _db.Query($"""
                SELECT d.id, snippet(docs_fts, 1, '', '', '..', 16) FROM docs_fts JOIN docs d ON d.id = docs_fts.rowid
                WHERE docs_fts MATCH $q{filter} ORDER BY bm25(docs_fts, 3.0, 1.0) LIMIT $n
                """, r => (r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1)), [.. args]);
        }
        catch (SqliteException ex)
        {
            _log.LogDebug(ex, "fts query failed");
            return [];
        }
        if (rows.Count == 0) return [];

        long n = _db.ScalarLong("SELECT count(*) FROM docs");
        var ids = rows.Select(r => r.Id).ToHashSet();
        var matched = ids.ToDictionary(id => id, _ => new List<int>());
        foreach (var t in terms)
        {
            var hits = _db.Query("SELECT rowid FROM docs_fts WHERE docs_fts MATCH $t", r => r.GetInt64(0), ("$t", TextTokens.FtsQuery([t])));
            foreach (var id in hits)
                if (matched.TryGetValue(id, out var l)) l.Add(hits.Count);
        }
        foreach (var (id, snip) in rows)
        {
            lexical[id] = Relevance(matched[id], n);
            if (snip.Length > 0) snippets[id] = snip;
        }
        return rows.Select(r => r.Id).ToList();
    }

    private List<long> LikeCandidates(List<string> terms, string[]? kinds, string[]? tags, int? dim, Dictionary<long, double> lexical)
    {
        var args = new List<(string, object?)>();
        var filter = KindFilter(kinds, tags, args, dim);
        var docs = _db.Query($"SELECT d.id, lower(d.title || ' ' || d.body) FROM docs d WHERE 1 = 1{filter}", r => (r.GetInt64(0), r.GetString(1)), [.. args]);
        var stems = terms.Select(TextTokens.Stem).ToArray();
        var df = new int[stems.Length];
        var matches = new List<(long Id, int[] Terms)>();
        foreach (var (id, text) in docs)
        {
            var m = new List<int>();
            for (int i = 0; i < stems.Length; i++)
                if (text.Contains(stems[i], StringComparison.Ordinal)) { m.Add(i); df[i]++; }
            if (m.Count > 0) matches.Add((id, m.ToArray()));
        }
        long n = docs.Count;
        var scored = matches.Select(x => (x.Id, Rel: Relevance(x.Terms.Select(i => df[i]), n), Cnt: x.Terms.Length))
            .OrderByDescending(x => x.Cnt).ThenByDescending(x => x.Rel).Take(CandidateLimit).ToList();
        foreach (var s in scored) lexical[s.Id] = s.Rel;
        return scored.Select(s => s.Id).ToList();
    }

    /// <param name="dim">Dimension to match (<see cref="WorldDim.Any"/> = all). It is an R*Tree axis, so the tree prunes it.</param>
    private List<(long Id, double Distance)> Spatial(double x, double y, double z, double r, string[]? kinds, string[]? tags, int limit, int dim = WorldDim.Any)
    {
        var args = new List<(string, object?)> { ("$x0", x - r), ("$x1", x + r), ("$y0", y - r), ("$y1", y + r), ("$z0", z - r), ("$z1", z + r) };
        var filter = KindFilter(kinds, tags, args);
        bool anyDim = dim == WorldDim.Any;
        if (!anyDim) args.Add(("$dim", dim));
        string sql = _db.HasRtree
            ? $"""
               SELECT d.id, d.min_x, d.min_y, d.min_z, d.max_x, d.max_y, d.max_z FROM docs_rtree t JOIN docs d ON d.id = t.id
               WHERE t.max_x >= $x0 AND t.min_x <= $x1 AND t.max_y >= $y0 AND t.min_y <= $y1 AND t.max_z >= $z0 AND t.min_z <= $z1{(anyDim ? "" : " AND t.min_d <= $dim AND t.max_d >= $dim")}{filter}
               """
            : $"""
               SELECT d.id, d.min_x, d.min_y, d.min_z, d.max_x, d.max_y, d.max_z FROM docs d
               WHERE d.min_x IS NOT NULL AND d.max_x >= $x0 AND d.min_x <= $x1 AND d.max_y >= $y0 AND d.min_y <= $y1 AND d.max_z >= $z0 AND d.min_z <= $z1{(anyDim ? "" : " AND d.dimension = $dim")}{filter}
               """;
        return _db.Query(sql, rd => (rd.GetInt64(0), new Box(rd.GetDouble(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4), rd.GetDouble(5), rd.GetDouble(6))), [.. args])
            .Select(t => (t.Item1, D: t.Item2.Distance(x, y, z)))
            .Where(t => t.D <= r)
            .OrderBy(t => t.D).Take(limit).ToList();
    }

    /// <summary>Spatial-only lookup (no text): what's known within r of a point.</summary>
    /// <param name="dimension">Null = the bot's current dimension; <see cref="WorldDim.Any"/> = all.</param>
    public List<RagHit> Nearby(double x, double y, double z, double r, IReadOnlyCollection<string>? kinds = null, int limit = 6, int? dimension = null) =>
        Search(new RagQuery { Near = (x, y, z), Radius = r, Kinds = kinds, Limit = limit, Dimension = dimension ?? CurrentDimension() });

    /// <summary>Doc kind of hierarchical summary nodes (RAPTOR); ranked by the tree retriever, not the flat vector list.</summary>
    public const string SummaryKind = "summary";
    private const string EpisodeKind = "episode";

    private List<(long Id, double Cos)> VectorTop(float[] q, string[]? kinds, string[]? tags, int limit, double floor, Func<long, bool>? skip = null, int? dim = null)
    {
        var top = new List<(long Id, double Cos)>(limit + 1);
        lock (_vecLock)
        {
            foreach (var (id, e) in _vecs)
            {
                if (kinds is { Length: > 0 } ? Array.IndexOf(kinds, e.Kind) < 0 : e.Kind is "chat" or SummaryKind) continue;
                if (skip != null && skip(id)) continue;
                if (tags is { Length: > 0 } && (e.Tag == null || Array.IndexOf(tags, e.Tag) < 0)) continue;
                if (dim is { } dm && e.Dim is { } ed && ed != dm && e.Kind != EpisodeKind) continue;
                double c = VectorMath.Dot(q, e.Vec);
                if (c < floor) continue;
                if (top.Count == limit && c <= top[^1].Cos) continue;
                int i = top.Count;
                while (i > 0 && top[i - 1].Cos < c) i--;
                top.Insert(i, (id, c));
                if (top.Count > limit) top.RemoveAt(top.Count - 1);
            }
        }
        return top;
    }

    private List<(RagHit Hit, double Weight)> LoadHits(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        var result = new List<(RagHit, double)>(list.Count);
        for (int i = 0; i < list.Count; i += 200)
        {
            var chunk = list.Skip(i).Take(200).ToList();
            var args = chunk.Select((id, k) => ("$i" + k, (object?)id)).ToArray();
            result.AddRange(_db.Query($"SELECT {HitColumns} FROM docs d WHERE d.id IN ({string.Join(",", args.Select(a => a.Item1))})",
                r => (ReadHit(r), r.GetDouble(7)), args));
        }
        return result;
    }

    /// <summary>One compact line for the model ("#12 struct gen1 [...]", "#40 ep 10-26 14:02: ...").</summary>
    public string FormatHit(RagHit h, int maxChars)
    {
        string text = h.Kind switch
        {
            "knowledge" => $"kb {h.Title}: {(h.Snippet != null && h.Body.Length > 180 ? h.Snippet : h.Body)}",
            "episode" => "ep " + h.Body,
            "world" => $"{h.Body} (seen {TextTokens.Ago(Now - h.Updated)})",
            SummaryKind => "sum " + h.Body,
            _ => h.Body,
        };
        // Things in the other dimension say so up front (survives truncation): "#12 (overworld) furnace at 3,64,0".
        var tag = h.Bounds != null ? WorldDim.Tag(h.Dimension, CurrentDimension()) : "";
        return TextUtil.Truncate(TextUtil.OneLine($"#{h.Id}{tag} {text}"), maxChars);
    }

    // ======================= chat log =======================

    public void LogChat(DateTimeOffset at, string kind, string? player, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_db.Gate)
        {
            _db.Exec("INSERT INTO chat_log(at, kind, player, text) VALUES ($a, $k, $p, $t)",
                ("$a", AgentDb.ToUnixMs(at)), ("$k", kind), ("$p", player), ("$t", TextUtil.Truncate(text, 400)));
            if (++_chatInserts % 500 == 0 && _o.ChatLogMaxRows > 0)
                _db.Exec("DELETE FROM chat_log WHERE id <= (SELECT max(id) FROM chat_log) - $m", ("$m", _o.ChatLogMaxRows));
        }
    }

    public List<ChatRow> SearchChat(string? query, string? player = null, int limit = 6) => AnnotateChat(SearchChatRaw(query, player, limit));

    /// <summary>
    /// Chat-log lines matching a <see cref="Corrections"/> entry get a "[superseded: …]" prefix. A long bot message is split
    /// over consecutive rows, so each row is checked together with its same-speaker neighbours (within 5 s).
    /// </summary>
    private List<ChatRow> AnnotateChat(List<ChatRow> rows)
    {
        if (Corrections is not { } cx || rows.Count == 0) return rows;
        return cx.AnnotateChat(rows, row =>
        {
            var around = ChatAroundRaw(row.Id, 1, 1);
            var parts = new List<string>(3);
            foreach (var r in around)
                if (r.Id == row.Id || (string.Equals(r.Player, row.Player, StringComparison.OrdinalIgnoreCase) && r.Kind == row.Kind
                                       && Math.Abs((r.At - row.At).TotalSeconds) <= 5))
                    parts.Add(r.Id == row.Id ? row.Text : r.Text);
            return string.Join(" ", parts);
        });
    }

    private List<ChatRow> SearchChatRaw(string? query, string? player, int limit)
    {
        var terms = TextTokens.Terms(query);
        static ChatRow Map(SqliteDataReader r) => new(r.GetInt64(0), AgentDb.FromUnixMs(r.GetInt64(1)), r.GetString(2), AgentDb.StrOrNull(r, 3), r.GetString(4));
        lock (_db.Gate)
        {
            if (terms.Count == 0)
                return _db.Query("SELECT id, at, kind, player, text FROM chat_log WHERE ($p IS NULL OR player = $p COLLATE NOCASE) ORDER BY id DESC LIMIT $n",
                    Map, ("$p", player), ("$n", limit));
            if (_db.HasFts)
            {
                try
                {
                    return _db.Query("""
                        SELECT c.id, c.at, c.kind, c.player, c.text FROM chat_fts JOIN chat_log c ON c.id = chat_fts.rowid
                        WHERE chat_fts MATCH $q AND ($p IS NULL OR c.player = $p COLLATE NOCASE) ORDER BY bm25(chat_fts), c.id DESC LIMIT $n
                        """, Map, ("$q", TextTokens.FtsQuery(terms)), ("$p", player), ("$n", limit));
                }
                catch (SqliteException) { return []; }
            }
            var like = string.Join(" OR ", terms.Select((_, i) => $"lower(text) LIKE $t{i}"));
            var args = terms.Select((t, i) => ("$t" + i, (object?)("%" + TextTokens.Stem(t) + "%"))).Append(("$p", player)).Append(("$n", limit)).ToArray();
            return _db.Query($"SELECT id, at, kind, player, text FROM chat_log WHERE ({like}) AND ($p IS NULL OR player = $p COLLATE NOCASE) ORDER BY id DESC LIMIT $n", Map, args);
        }
    }

    public List<ChatRow> ChatAround(long id, int before = 3, int after = 3) => AnnotateChat(ChatAroundRaw(id, before, after));

    private List<ChatRow> ChatAroundRaw(long id, int before, int after)
    {
        static ChatRow Map(SqliteDataReader r) => new(r.GetInt64(0), AgentDb.FromUnixMs(r.GetInt64(1)), r.GetString(2), AgentDb.StrOrNull(r, 3), r.GetString(4));
        return _db.Query("SELECT id, at, kind, player, text FROM chat_log WHERE id BETWEEN $a AND $b ORDER BY id", Map, ("$a", id - before), ("$b", id + after));
    }

    public bool PlayerSpokeSince(string player, DateTimeOffset since) =>
        _db.ScalarLong("SELECT count(*) FROM chat_log WHERE player = $p COLLATE NOCASE AND kind IN ('chat','whisper') AND at >= $s",
            ("$p", player), ("$s", AgentDb.ToUnixMs(since))) > 0;

    // ======================= embeddings =======================

    private string VecModel => _embedder.Model;

    /// <summary>
    /// Hive: re-reads the in-memory vector cache from the db, dropping stale entries. Called when another process committed
    /// (<c>PRAGMA data_version</c> changed), so docs/vectors written by other hive bots become searchable here.
    /// </summary>
    public void ReloadVectors() => LoadVectors(clear: true);

    private void LoadVectors(bool clear = false)
    {
        if (!_embedder.IsAvailable) return;
        var rows = _db.Query("""
            SELECT v.doc_id, d.kind, d.tag, v.vec, CASE WHEN d.min_x IS NULL THEN NULL ELSE d.dimension END
            FROM vectors v JOIN docs d ON d.id = v.doc_id WHERE v.model = $m AND v.dims = $d AND v.hash = d.hash
            """, r => (r.GetInt64(0), r.GetString(1), AgentDb.StrOrNull(r, 2), (byte[])r.GetValue(3), r.IsDBNull(4) ? (int?)null : (int)r.GetInt64(4)),
            ("$m", VecModel), ("$d", _embedder.Dimensions));
        lock (_vecLock)
        {
            if (clear) _vecs.Clear();
            foreach (var (id, kind, tag, blob, dim) in rows) _vecs[id] = new VecEntry(kind, tag, VectorMath.FromBlob(blob), dim);
        }
    }

    public void SignalEmbedding()
    {
        if (_worker != null) _signal.Release();
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                while (_signal.CurrentCount > 0) _signal.Wait(0);
                await Task.Delay(250, ct).ConfigureAwait(false); // debounce bursts (chunk loads, startup re-index)
                var wait = _disabledUntil - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                await FlushEmbeddingsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "embedding worker error"); }
        }
    }

    /// <summary>Embeds every doc whose vector is missing or stale. Returns how many vectors were written.</summary>
    public async Task<int> FlushEmbeddingsAsync(CancellationToken ct = default)
    {
        if (!_embedder.IsAvailable || DateTimeOffset.UtcNow < _disabledUntil) return 0;
        await _embedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int written = 0;
            for (int guard = 0; guard < 1000; guard++)
            {
                var pending = _db.Query("""
                    SELECT d.id, d.title, d.body, d.hash, d.kind, d.tag, CASE WHEN d.min_x IS NULL THEN NULL ELSE d.dimension END
                    FROM docs d LEFT JOIN vectors v ON v.doc_id = d.id
                    WHERE d.embed = 1 AND (v.doc_id IS NULL OR v.model <> $m OR v.dims <> $d OR v.hash <> d.hash) LIMIT 256
                    """, r => new Pending(r.GetInt64(0), r.GetString(1) + "\n" + r.GetString(2), r.GetString(3), r.GetString(4), AgentDb.StrOrNull(r, 5),
                        r.IsDBNull(6) ? null : (int)r.GetInt64(6)),
                    ("$m", VecModel), ("$d", _embedder.Dimensions));
                if (pending.Count == 0) break;

                var need = new List<Pending>();
                foreach (var p in pending)
                {
                    var blob = _db.Scalar("SELECT vec FROM embed_cache WHERE hash = $h AND model = $m AND dims = $d",
                        ("$h", p.Hash), ("$m", VecModel), ("$d", _embedder.Dimensions)) as byte[];
                    if (blob != null) { StoreVector(p, VectorMath.FromBlob(blob)); written++; }
                    else if (!need.Any(x => x.Hash == p.Hash)) need.Add(p);
                }

                int batch = Math.Max(1, _embOptions.MaxBatch);
                for (int i = 0; i < need.Count; i += batch)
                {
                    var chunk = need.Skip(i).Take(batch).ToList();
                    EmbeddingResponse resp;
                    try
                    {
                        resp = await _embedder.EmbedAsync(chunk.Select(c => c.Text).ToList(), ct).ConfigureAwait(false);
                        Interlocked.Exchange(ref _warm, 1);
                        EmbedRequests++;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Fail(ex);
                        return written;
                    }
                    RecordUsage(resp.Tokens);
                    EmbeddedTexts += chunk.Count;
                    for (int k = 0; k < chunk.Count; k++)
                    {
                        var v = VectorMath.Normalize(resp.Vectors[k]);
                        _db.Exec("INSERT OR REPLACE INTO embed_cache(hash, model, dims, vec, created) VALUES ($h, $m, $d, $v, $t)",
                            ("$h", chunk[k].Hash), ("$m", VecModel), ("$d", _embedder.Dimensions), ("$v", VectorMath.ToBlob(v)), ("$t", AgentDb.ToUnixMs(Now)));
                        StoreVector(chunk[k], v);
                        written++;
                    }
                }
                // Same-hash duplicates in this round now hit the cache on the next loop.
                if (_failures > 0) { _log.LogInformation("embeddings recovered"); _failures = 0; }
            }
            return written;
        }
        finally
        {
            _embedGate.Release();
        }
    }

    private void StoreVector(Pending p, float[] v)
    {
        lock (_db.Gate)
        {
            // The doc may have changed or vanished meanwhile: only store if its hash still matches.
            int n = _db.Exec("""
                INSERT OR REPLACE INTO vectors(doc_id, model, dims, hash, vec)
                SELECT id, $m, $d, $h, $v FROM docs WHERE id = $id AND hash = $h
                """, ("$id", p.Id), ("$m", VecModel), ("$d", _embedder.Dimensions), ("$h", p.Hash), ("$v", VectorMath.ToBlob(v)));
            if (n > 0) lock (_vecLock) _vecs[p.Id] = new VecEntry(p.Kind, p.Tag, v, p.Dim);
        }
    }

    private void Fail(Exception ex)
    {
        _failures++;
        var backoff = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, Math.Min(_failures - 1, 6))));
        _disabledUntil = DateTimeOffset.UtcNow + backoff;
        if (_failures == 1 || !_loggedUnavailable)
        {
            _loggedUnavailable = true;
            _log.LogWarning("embeddings failing ({Error}); running FTS-only, retry in {Backoff}", TextUtil.OneLine(ex.Message), backoff);
        }
        else _log.LogDebug("embeddings still failing ({Failures}): {Error}", _failures, ex.Message);
    }

    private void RecordUsage(long tokens)
    {
        if (tokens > 0) _usage?.RecordEmbedding(VecModel, tokens, _embOptions.PricePerMTok);
    }

    /// <summary>True once one embedding call succeeded in this process (DNS, TCP and TLS to the provider are warm).</summary>
    public bool EmbeddingsWarm => Volatile.Read(ref _warm) != 0;
    private int _warm;
    private Task? _warmup;

    /// <summary>
    /// Warms the provider connection at startup (DNS + first TLS handshake are the slow part on a fresh container: they blew
    /// the per-turn query budget, "query embedding timed out; FTS-only"). One tiny embed in the background, retried with
    /// backoff; never blocks a turn and never throws. Idempotent.
    /// </summary>
    public Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!_embedder.IsAvailable || _o.WarmUpAttempts <= 0) return Task.CompletedTask;
        lock (_queryCache) return _warmup ??= Task.Run(() => WarmCoreAsync(ct));
    }

    private async Task WarmCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        for (int attempt = 0; attempt < _o.WarmUpAttempts; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await _embedder.EmbedAsync(["warm up"], linked.Token).ConfigureAwait(false);
                RecordUsage(resp.Tokens);
                Interlocked.Exchange(ref _warm, 1);
                _log.LogInformation("embeddings warm ({Ms} ms)", sw.ElapsedMilliseconds);
                return;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogDebug("embedding warm-up {Attempt} failed: {Error}", attempt + 1, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)), linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Embeds a query (cached; time-boxed: the turn waits at most <see cref="RagOptions.QueryEmbedTimeoutMs"/>, a bit longer
    /// while the connection is still cold). Null when unavailable, slow or failing — callers go FTS-only for this turn. A
    /// timed-out request is NOT abandoned: it finishes in the background (provider timeout + retries), warms the connection
    /// and lands in the cache, so the same query next turn is instant.
    /// </summary>
    public async Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct)
    {
        if (!VectorsAvailable || VectorCount == 0) return null;
        text = TextUtil.Truncate(text.Trim(), 1000);
        var key = VectorMath.Hash(text);
        lock (_queryCache)
            if (_queryCache.TryGetValue(key, out var hit)) return hit;

        if (!EmbeddingsWarm) _ = WarmUpAsync();
        int budget = Math.Max(100, _o.QueryEmbedTimeoutMs) + (EmbeddingsWarm ? 0 : Math.Max(0, _o.ColdQueryExtraMs));
        Task<float[]> call;
        lock (_queryCache)
        {
            if (!_inflight.TryGetValue(key, out call!))
            {
                call = EmbedAndCacheAsync(text, key);
                _inflight[key] = call;
            }
        }
        try { return await call.WaitAsync(TimeSpan.FromMilliseconds(budget), ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _log.LogDebug("query embedding slower than {Ms} ms; FTS-only for this turn (it finishes in the background)", budget);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // Fail() already ran in EmbedAndCacheAsync
    }

    private readonly Dictionary<string, Task<float[]>> _inflight = new();

    private async Task<float[]> EmbedAndCacheAsync(string text, string key)
    {
        try
        {
            var resp = await _embedder.EmbedAsync([text], _cts.Token).ConfigureAwait(false);
            RecordUsage(resp.Tokens);
            Interlocked.Exchange(ref _warm, 1);
            var v = VectorMath.Normalize(resp.Vectors[0]);
            lock (_queryCache)
            {
                if (_queryCache.Count >= 256) _queryCache.Clear();
                _queryCache[key] = v;
            }
            return v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            if (ex is not OperationCanceledException) Fail(ex);
            throw;
        }
        finally { lock (_queryCache) _inflight.Remove(key); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
