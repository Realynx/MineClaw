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

    public static async Task<MemoryBank> OpenAsync(string path, AgentOptions options, IEmbeddingProvider provider, CancellationToken ct = default)
    {
        var db = AgentDb.OpenReadOnly(path);
        try
        {
            if (provider.IsAvailable && provider.Dimensions > 0 && db.TableExists("vectors"))
            {
                long other = db.ScalarLong("SELECT count(*) FROM vectors WHERE dims <> $d OR model <> $m", ("$d", provider.Dimensions), ("$m", provider.Model));
                long same = db.ScalarLong("SELECT count(*) FROM vectors WHERE dims = $d AND model = $m", ("$d", provider.Dimensions), ("$m", provider.Model));
                if (same == 0 && other > 0) provider = NullEmbeddingProvider.Instance;
            }
            var rag = new RagIndex(db, options.Rag, provider, usage: null, options.Embeddings, backgroundEmbedding: false);
            var bank = new MemoryBank(path, db, rag, provider) { MoodHalfLifeHours = options.Self.MoodHalfLifeHours };
            await Task.Run(() => bank.LoadTreeAsync(options, ct), ct).ConfigureAwait(false);
            return bank;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private async Task LoadTreeAsync(AgentOptions options, CancellationToken ct)
    {
        var json = Db.TableExists("meta") ? Db.GetMeta(AgentDbRaptorStore.MetaKey) : null;
        if (string.IsNullOrEmpty(json)) { TreeNote = "no RAPTOR tree stored in this db"; return; }
        try { TreeMeta = JsonSerializer.Deserialize<RaptorTreeMeta>(json); }
        catch (JsonException) { TreeMeta = null; }
        if (TreeMeta == null) { TreeNote = "unreadable RAPTOR metadata"; return; }
        var embedder = new StoredModelEmbedder(TreeMeta.EmbeddingModel, TreeMeta.Dimensions, Embeddings);
        var raptor = new RaptorMemory(Db, Rag, embedder, ReadOnlySummarizer.Instance, options.Rag.Raptor);
        raptor.Store.LoadWritesBack = false;
        bool loaded;
        try { loaded = await raptor.LoadAsync(ct).ConfigureAwait(false); }
        catch (Microsoft.Data.Sqlite.SqliteException) { loaded = false; }
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

/// <summary>
/// The live bank: opened lazily, re-opened in the background when the bot changed what the bank caches in memory (the stored
/// tree, the docs or the vectors), checked at most every <see cref="BridgeOptions.ReloadCheckSeconds"/>. Requests always get a
/// complete bank; the replaced one is disposed after a grace period.
/// </summary>
public sealed class BankHost(BridgeConfig config, ILogger<BankHost> log) : IDisposable
{
    private readonly SemaphoreSlim _open = new(1, 1);
    private MemoryBank? _bank;
    private string? _signature;
    private DateTimeOffset _checked;
    private Task? _reload;
    private bool _disposed;

    /// <summary>Overrides the provider (tests).</summary>
    public IEmbeddingProvider? Embeddings { get; init; }

    public async Task<MemoryBank> GetAsync(CancellationToken ct)
    {
        var b = _bank;
        if (b != null)
        {
            MaybeCheck(b);
            return b;
        }
        await _open.WaitAsync(ct);
        try
        {
            if (_bank != null) return _bank;
            var path = config.DbPath;
            if (!File.Exists(path)) throw new BankUnavailableException("the agent db is not there");
            var bank = await MemoryBank.OpenAsync(path, config.Agent, Embeddings ?? config.CreateEmbeddings(), ct);
            _signature = Signature(bank.Db);
            _checked = DateTimeOffset.UtcNow;
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
        var now = DateTimeOffset.UtcNow;
        if (now - _checked < TimeSpan.FromSeconds(Math.Max(1, config.Bridge.ReloadCheckSeconds)) || _reload is { IsCompleted: false }) return;
        _checked = now;
        _reload = Task.Run(async () =>
        {
            try
            {
                var sig = Signature(b.Db);
                if (sig == _signature) return;
                var fresh = await MemoryBank.OpenAsync(b.Path, config.Agent, Embeddings ?? config.CreateEmbeddings());
                if (_disposed) { fresh.Dispose(); return; }
                var old = Interlocked.Exchange(ref _bank, fresh);
                _signature = Signature(fresh.Db);
                log.LogInformation("reloaded the agent db caches (the bot changed the tree, docs or vectors)");
                if (old != null) _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
            }
            catch (Exception ex) { log.LogWarning("db reload check failed: {Message}", ex.Message); }
        });
    }

    /// <summary>What the bank caches: the tree meta, the docs and the vectors (cheap aggregate reads).</summary>
    public static string Signature(AgentDb db)
    {
        string meta = db.TableExists("meta") ? db.GetMeta(AgentDbRaptorStore.MetaKey) ?? "" : "";
        string docs = db.TableExists("docs") ? string.Join(",", db.Query("SELECT count(*), max(id), max(updated) FROM docs", r => $"{r.GetValue(0)}/{r.GetValue(1)}/{r.GetValue(2)}")) : "";
        string vec = db.TableExists("vectors") ? db.ScalarLong("SELECT count(*) FROM vectors").ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return $"{meta.GetHashCode(StringComparison.Ordinal)}|{docs}|{vec}";
    }

    public void Dispose()
    {
        _disposed = true;
        _bank?.Dispose();
        _bank = null;
    }
}

public sealed class BankUnavailableException(string message) : Exception(message);
