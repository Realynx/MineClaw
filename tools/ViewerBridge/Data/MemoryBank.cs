using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Storage;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Data;

/// <summary>One leaf/summary doc behind a tree node (docs row).</summary>
public sealed record NodeDoc(long Id, string Ref, string Kind, string? Tag, string Title, string? Source, DateTimeOffset Updated, int Dimension);

/// <summary>
/// The bot's db opened strictly read-only (<see cref="AgentDb.OpenReadOnly"/>), the bot's own hybrid index (<see cref="RagIndex"/>,
/// no background embedding) and its RAPTOR tree (<see cref="RaptorMemory"/>, <see cref="AgentDbRaptorStore.LoadWritesBack"/> off).
/// Ported from tools/MemoryExplorer/Services/MemoryBank.cs. Nothing here writes: query embeddings live in memory.
/// </summary>
public sealed class MemoryBank : IDisposable
{
    private Dictionary<int, NodeDoc> _nodeDocs = new();
    private Dictionary<long, int> _nodeOfDoc = new();
    private Dictionary<string, int> _nodeOfRef = new(StringComparer.Ordinal);
    private GraphModel? _graph;
    private readonly object _graphLock = new();
    private int _disposed;

    private MemoryBank(string path, AgentDb db, RagIndex rag, IEmbeddingProvider embeddings)
    {
        Path = System.IO.Path.GetFullPath(path);
        Db = db;
        Rag = rag;
        Embeddings = embeddings;
    }

    public string Path { get; }
    public AgentDb Db { get; }
    public RagIndex Rag { get; }
    public RaptorMemory? Raptor { get; private set; }
    public IEmbeddingProvider Embeddings { get; }
    /// <summary>Mood half-life from the bot's <c>Agent:Self</c> config, so mood reads decay exactly as the bot's own do.</summary>
    public double MoodHalfLifeHours { get; private init; } = new MineClaw.Agent.Self.SelfOptions().MoodHalfLifeHours;
    public RaptorTreeMeta? TreeMeta { get; private set; }
    public string? TreeNote { get; private set; }
    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.UtcNow;
    public bool HasTree => Raptor is { Ready: true } r && r.Index.NodeCount > 0;
    public bool VectorSearch => Rag.VectorsAvailable && Rag.VectorCount > 0;

    /// <summary>Changes whenever the tree changes (graph/label cache key; memory heat ids line up when equal).</summary>
    public string StateKey => ReadTree(StateKeyOf, "none");

    public static string StateKeyOf(RaptorTree t) => $"{t.BuildId}-{t.Count}-{t.InsertSeq}-{t.NextIndex}";

    /// <param name="previous">The bank this one replaces: its unchanged vectors are shared, not decoded again.</param>
    /// <param name="shareTreeVectors">The tree's nodes share the flat index's arrays for the same stored vectors instead of holding a
    /// second copy of every leaf and summary vector (false only to measure the difference).</param>
    public static async Task<MemoryBank> OpenAsync(string path, AgentOptions options, IEmbeddingProvider provider, CancellationToken ct = default,
        MemoryBank? previous = null, bool shareTreeVectors = true)
    {
        var db = AgentDb.OpenReadOnly(path);
        RagIndex? rag = null;
        MemoryBank? bank = null;
        try
        {
            if (provider.IsAvailable && provider.Dimensions > 0 && db.TableExists("vectors"))
            {
                long other = db.ScalarLong("SELECT count(*) FROM vectors WHERE dims <> $d OR model <> $m", ("$d", provider.Dimensions), ("$m", provider.Model));
                long same = db.ScalarLong("SELECT count(*) FROM vectors WHERE dims = $d AND model = $m", ("$d", provider.Dimensions), ("$m", provider.Model));
                if (same == 0 && other > 0) provider = NullEmbeddingProvider.Instance;
            }
            rag = new RagIndex(db, options.Rag, provider, usage: null, options.Embeddings, backgroundEmbedding: false, reuseStoredVectors: previous?.Rag);
            bank = new MemoryBank(path, db, rag, provider) { MoodHalfLifeHours = options.Self.MoodHalfLifeHours };
            await Task.Run(() => bank.LoadTreeAsync(options, shareTreeVectors, ct), ct).ConfigureAwait(false);
            return bank;
        }
        catch
        {
            if (bank != null) bank.Dispose();
            else { rag?.Dispose(); db.Dispose(); }
            throw;
        }
    }

    private async Task LoadTreeAsync(AgentOptions options, bool shareVectors, CancellationToken ct)
    {
        var json = Db.TableExists("meta") ? Db.GetMeta(AgentDbRaptorStore.MetaKey) : null;
        if (string.IsNullOrEmpty(json)) { TreeNote = "no RAPTOR tree stored in this db"; return; }
        try { TreeMeta = JsonSerializer.Deserialize<RaptorTreeMeta>(json); }
        catch (JsonException) { TreeMeta = null; }
        if (TreeMeta == null) { TreeNote = "unreadable RAPTOR metadata"; return; }
        var embedder = new StoredModelEmbedder(TreeMeta.EmbeddingModel, TreeMeta.Dimensions, Embeddings);
        // Banks never mutate loaded embeddings. Reuse those arrays for retrieval instead of copying the entire tree into
        // a second dense matrix; during a live refresh both the old and replacement bank must fit in the bridge's heap.
        var raptor = new RaptorMemory(Db, Rag, embedder, ReadOnlySummarizer.Instance, options.Rag.Raptor,
            raptorOptions: new RaptorOptions { ReuseImmutableEmbeddings = true });
        raptor.Store.LoadWritesBack = false;
        // The flat index already holds every vector the tree is made of (same rows of the vectors table): share those arrays.
        var meta = TreeMeta;
        if (shareVectors) raptor.Store.ReuseVector = (docId, hash) => Rag.SharedVector(docId, hash, meta.EmbeddingModel, meta.Dimensions);
        bool loaded;
        try { loaded = await raptor.LoadAsync(ct).ConfigureAwait(false); }
        catch (Microsoft.Data.Sqlite.SqliteException) { loaded = false; }
        catch { raptor.Dispose(); throw; }
        finally { raptor.Store.ReuseVector = null; }
        if (!loaded) { TreeNote = "the stored tree could not be loaded"; raptor.Dispose(); return; }
        Raptor = raptor;
        if (VectorSearch) Rag.Augmenter = raptor;
        _nodeDocs = Db.Query("""
            SELECT raptor_idx, id, ref, kind, tag, title, source, updated, dimension FROM docs WHERE raptor_idx IS NOT NULL
            """, r => (Idx: (int)r.GetInt64(0), Doc: new NodeDoc(r.GetInt64(1), r.GetString(2), r.GetString(3), AgentDb.StrOrNull(r, 4), r.GetString(5),
                AgentDb.StrOrNull(r, 6), AgentDb.FromUnixMs(r.GetInt64(7)), (int)r.GetInt64(8))))
            .GroupBy(x => x.Idx).ToDictionary(g => g.Key, g => g.First().Doc);
        _nodeOfDoc = _nodeDocs.GroupBy(kv => kv.Value.Id).ToDictionary(g => g.Key, g => g.First().Key);
        _nodeOfRef = _nodeDocs.GroupBy(kv => kv.Value.Ref, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
    }

    public T ReadTree<T>(Func<RaptorTree, T> read, T fallback) => Raptor is { } r ? r.Index.Read(read) : fallback;

    public NodeDoc? DocOf(int nodeIndex) => _nodeDocs.GetValueOrDefault(nodeIndex);

    public int? NodeOfDoc(long docId) => _nodeOfDoc.TryGetValue(docId, out var n) ? n : null;

    public int? NodeOfRef(string? docRef) => docRef != null && _nodeOfRef.TryGetValue(docRef, out var n) ? n : null;

    /// <summary>The first chain of parents from a node up to a root (lowest-index parent at each step).</summary>
    public IReadOnlyList<int> PathToRoot(int index) => ReadTree(t => PathToRoot(t, index), []);

    /// <summary><see cref="PathToRoot(int)"/> for a caller already inside <see cref="ReadTree{T}"/> (the index lock is not re-entrant).</summary>
    public static IReadOnlyList<int> PathToRoot(RaptorTree t, int index)
    {
        var path = new List<int>();
        var seen = new HashSet<int>();
        int cur = index;
        while (t.TryGet(cur, out var n) && seen.Add(cur))
        {
            path.Add(cur);
            if (n.Parents.Count == 0) break;
            cur = n.Parents.Min;
        }
        path.Reverse();
        return path;
    }

    /// <summary>The partition graph with TF-IDF labels, built once per bank (the bank is replaced when the tree changes).</summary>
    public GraphModel? Graph()
    {
        if (!HasTree) return null;
        lock (_graphLock)
        {
            if (_graph != null) return _graph;
            var g = ReadTree(t => GraphModel.Build(t, DocOf), null!);
            TopicLabeler.Label(g);
            return _graph = g;
        }
    }

    public static long FileSize(string path) =>
        new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Raptor?.Dispose();
        Rag.Dispose();
        Db.Dispose();
    }
}

/// <summary>IEmbedder with the tree's stored model id and size; embeds through the provider only when it makes the same vectors.</summary>
public sealed class StoredModelEmbedder(string model, int dimensions, IEmbeddingProvider provider) : IEmbedder
{
    public string Model => model;
    public int Dimensions => dimensions;
    public bool CanEmbed => provider.IsAvailable && provider.Model == model && provider.Dimensions == dimensions;

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!CanEmbed) throw new InvalidOperationException("query embedding is not configured for this tree's model");
        var r = await provider.EmbedAsync(texts, ct).ConfigureAwait(false);
        return new EmbeddingResult(r.Vectors.Select(VectorMath.Normalize).ToList(), r.Tokens);
    }
}

/// <summary>The bridge never summarizes: any call is a bug.</summary>
public sealed class ReadOnlySummarizer : ISummarizer
{
    public static ReadOnlySummarizer Instance { get; } = new();
    public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("read-only bridge: summaries are only written by the bot");
}

//// <summary>
/// The live bank: opened lazily and kept up to date with what the bot writes, checked at most every
/// <see cref="BridgeOptions.ReloadCheckSeconds"/> on a request that reads it (never on <c>/v1/health</c>). Only what changed is
/// reloaded: new, re-embedded or deleted vectors are refreshed in place in the current bank (ids and hashes, then only the changed
/// blobs); a changed RAPTOR tree opens a replacement bank (sharing every unchanged vector array), at most every
/// <see cref="BridgeOptions.TreeReloadSeconds"/>. Requests always get a complete bank; a replaced one is disposed after a grace period.
/// Before 11-10 every change of any doc (the bot touches <c>updated</c> constantly) rebuilt the whole bank about once a minute.
/// </summary>
public sealed class BankHost(BridgeConfig config, ILogger<BankHost> log) : IDisposable
{
    private readonly SemaphoreSlim _open = new(1, 1);
    private readonly object _reloadLock = new();
    private MemoryBank? _bank;
    private (string Tree, string Docs) _signature;
    private DateTimeOffset _checked, _treeLoaded;
    private Task? _reload;
    private bool _disposed;

    /// <summary>Overrides the provider (tests).</summary>
    public IEmbeddingProvider? Embeddings { get; init; }

    /// <summary>The bank, opened on first use; a due check for the bot's changes starts in the background.</summary>
    public Task<MemoryBank> GetAsync(CancellationToken ct) => GetAsync(ct, check: true);

    /// <param name="check">False: never starts a reload check (the health probe, polled all the time, must not drive reloads).</param>
    public async Task<MemoryBank> GetAsync(CancellationToken ct, bool check)
    {
        var b = _bank;
        if (b != null)
        {
            if (check) MaybeCheck(b);
            return b;
        }
        await _open.WaitAsync(ct);
        try
        {
            if (_bank != null) return _bank;
            var path = config.DbPath;
            if (!File.Exists(path)) throw new BankUnavailableException("the agent db is not there");
            var bank = await MemoryBank.OpenAsync(path, config.Agent, Embeddings ?? config.CreateEmbeddings(), ct);
            _signature = Signatures(bank.Db);
            _checked = _treeLoaded = DateTimeOffset.UtcNow;
            log.LogInformation("opened the agent db read-only ({Tree})", bank.HasTree ? "tree loaded" : bank.TreeNote);
            return _bank = bank;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("agent db unavailable: {Message}", ex.Message);
            throw new BankUnavailableException("the agent db could not be opened");
        }
        finally { _open.Release(); }
    }

    private void MaybeCheck(MemoryBank b)
    {
        // Requests arrive concurrently. Check and publish the pending reload together so only one replacement bank
        // can be built while the active bank remains available to in-flight readers.
        lock (_reloadLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_disposed || now - _checked < TimeSpan.FromSeconds(Math.Max(1, config.Bridge.ReloadCheckSeconds)) || _reload is { IsCompleted: false }) return;
            _checked = now;
            _reload = Task.Run(() => ReloadAsync(b));
        }
    }

    private async Task ReloadAsync(MemoryBank b)
    {
        try
        {
            var sig = Signatures(b.Db);
            (string Tree, string Docs) was;
            DateTimeOffset treeLoaded;
            lock (_reloadLock) (was, treeLoaded) = (_signature, _treeLoaded);
            bool treeDue = sig.Tree != was.Tree && DateTimeOffset.UtcNow - treeLoaded >= TimeSpan.FromSeconds(Math.Max(0, config.Bridge.TreeReloadSeconds));
            if (treeDue) { await ReplaceAsync(b, sig); return; }
            if (sig.Docs == was.Docs) return; // nothing, or a tree change that waits for its minimum interval
            var (changed, removed) = b.Rag.RefreshVectors();
            lock (_reloadLock) _signature = (was.Tree, sig.Docs); // the tree part stays pending until the tree is reloaded
            if (changed + removed > 0) log.LogDebug("refreshed the agent db vectors in place ({Changed} new or changed, {Removed} removed)", changed, removed);
        }
        catch (Exception ex) { log.LogWarning(ex, "db reload check failed: {Message}", ex.Message); }
        finally { lock (_reloadLock) _reload = null; }
    }

    /// <summary>The tree changed: a replacement bank (vectors shared with the current one) is swapped in.</summary>
    private async Task ReplaceAsync(MemoryBank b, (string Tree, string Docs) sig)
    {
        var fresh = await MemoryBank.OpenAsync(b.Path, config.Agent, Embeddings ?? config.CreateEmbeddings(), previous: b);
        MemoryBank? old;
        lock (_reloadLock)
        {
            if (_disposed) { fresh.Dispose(); return; }
            old = Interlocked.Exchange(ref _bank, fresh);
            _signature = sig; // A change during loading must trigger another refresh, not be mistaken for loaded data.
            _treeLoaded = DateTimeOffset.UtcNow;
        }
        log.LogInformation("reloaded the agent db caches (the bot changed the RAPTOR tree)");
        if (old != null) _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
    }

    /// <summary>What the bank caches, in two parts (cheap aggregate reads over indexes): the RAPTOR tree (its meta row, its node
    /// docs, the summaries' last rewrite) and the flat vectors (the docs and vectors tables).</summary>
    public static (string Tree, string Docs) Signatures(AgentDb db)
    {
        bool docs = db.TableExists("docs");
        string meta = db.TableExists("meta") ? db.GetMeta(AgentDbRaptorStore.MetaKey) ?? "" : "";
        string nodes = docs ? Row(db, "SELECT count(*), max(raptor_idx) FROM docs WHERE raptor_idx IS NOT NULL") : "";
        string summaries = docs ? Row(db, $"SELECT max(updated) FROM docs WHERE kind = '{RagIndex.SummaryKind}'") : "";
        string all = docs ? Row(db, "SELECT count(*), max(id), max(updated) FROM docs") : "";
        string vec = db.TableExists("vectors") ? db.ScalarLong("SELECT count(*) FROM vectors").ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return ($"{meta.GetHashCode(StringComparison.Ordinal)}|{nodes}|{summaries}", $"{all}|{vec}");
    }

    private static string Row(AgentDb db, string sql) => string.Join(",", db.Query(sql, r =>
    {
        var parts = new string[r.FieldCount];
        for (int i = 0; i < parts.Length; i++) parts[i] = Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return string.Join("/", parts);
    }));

    /// <summary>Both parts in one string (a change of either changes it).</summary>
    public static string Signature(AgentDb db)
    {
        var (tree, docs) = Signatures(db);
        return tree + "|" + docs;
    }

    public void Dispose()
    {
        lock (_reloadLock)
        {
            _disposed = true;
            _bank?.Dispose();
            _bank = null;
        }
    }
}

public sealed class BankUnavailableException(string message) : Exception(message);
