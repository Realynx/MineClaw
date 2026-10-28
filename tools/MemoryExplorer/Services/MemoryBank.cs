using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Storage;

namespace MineClaw.MemoryExplorer.Services;

public enum SourceKind { LocalDev, Snapshot, Seed, Other }

/// <summary>One leaf/summary doc behind a tree node (docs row).</summary>
public sealed record NodeDoc(long Id, string Ref, string Kind, string? Tag, string Title, string? Source, DateTimeOffset Updated, int Dimension);

/// <summary>
/// An opened memory bank: the bot's SQLite db opened strictly read-only (<see cref="AgentDb.OpenReadOnly"/>), the bot's own hybrid
/// index (<see cref="RagIndex"/>, no background embedding) and its RAPTOR tree (<see cref="RaptorMemory"/> with
/// <see cref="AgentDbRaptorStore.LoadWritesBack"/> off). Nothing here writes: query embeddings (when configured) live in memory.
/// </summary>
public sealed class MemoryBank : IDisposable
{
    private Dictionary<int, NodeDoc> _nodeDocs = new();

    private MemoryBank(string path, AgentDb db, RagIndex rag, IEmbeddingProvider embeddings)
    {
        Path = System.IO.Path.GetFullPath(path);
        Db = db;
        Rag = rag;
        Embeddings = embeddings;
        Kind = Classify(Path);
        SnapshotAt = SnapshotTime(Path);
        SizeBytes = FileSize(Path);
    }

    public string Path { get; }
    public AgentDb Db { get; }
    public RagIndex Rag { get; }
    public RaptorMemory? Raptor { get; private set; }
    public IEmbeddingProvider Embeddings { get; }
    public SourceKind Kind { get; }
    /// <summary>When the copy was taken (snapshot file name, else the file's last write time).</summary>
    public DateTimeOffset SnapshotAt { get; }
    /// <summary>db + -wal + -shm bytes.</summary>
    public long SizeBytes { get; }
    public RaptorTreeMeta? TreeMeta { get; private set; }
    /// <summary>Why the tree isn't available ("no RAPTOR tree stored", "built with model X"), else null.</summary>
    public string? TreeNote { get; private set; }
    public bool HasTree => Raptor is { Ready: true } r && r.Index.NodeCount > 0;
    /// <summary>The bot's full hybrid retrieval (FTS + vectors + RAPTOR) is possible offline: query embedding configured and
    /// the stored vectors match it.</summary>
    public bool VectorSearch => Rag.VectorsAvailable && Rag.VectorCount > 0;

    /// <param name="embeddings">Overrides the configured query-embedding provider (tests).</param>
    public static async Task<MemoryBank> OpenAsync(string path, ExplorerConfig config, CancellationToken ct = default, IEmbeddingProvider? embeddings = null)
    {
        var db = AgentDb.OpenReadOnly(path);
        try
        {
            var provider = embeddings ?? config.CreateEmbeddings();
            // Vectors of another model/size can't be compared with this query embedder: lexical only then.
            if (provider.IsAvailable && provider.Dimensions > 0)
            {
                long other = db.ScalarLong("SELECT count(*) FROM vectors WHERE dims <> $d OR model <> $m", ("$d", provider.Dimensions), ("$m", provider.Model));
                long same = db.ScalarLong("SELECT count(*) FROM vectors WHERE dims = $d AND model = $m", ("$d", provider.Dimensions), ("$m", provider.Model));
                if (same == 0 && other > 0) provider = NullEmbeddingProvider.Instance;
            }
            var rag = new RagIndex(db, config.Agent.Rag, provider, usage: null, config.Agent.Embeddings, backgroundEmbedding: false);
            var bank = new MemoryBank(path, db, rag, provider);
            await Task.Run(() => bank.LoadTreeAsync(config, ct), ct).ConfigureAwait(false);
            return bank;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private async Task LoadTreeAsync(ExplorerConfig config, CancellationToken ct)
    {
        var json = Db.GetMeta(AgentDbRaptorStore.MetaKey);
        if (string.IsNullOrEmpty(json)) { TreeNote = "no RAPTOR tree stored in this db"; return; }
        TreeMeta = JsonSerializer.Deserialize<RaptorTreeMeta>(json);
        if (TreeMeta == null) { TreeNote = "unreadable RAPTOR metadata"; return; }
        // The tree loads only for an embedder with the stored model/size; this one forwards to the real provider when it matches.
        var embedder = new StoredModelEmbedder(TreeMeta.EmbeddingModel, TreeMeta.Dimensions, Embeddings);
        var raptor = new RaptorMemory(Db, Rag, embedder, ReadOnlySummarizer.Instance, config.Agent.Rag.Raptor);
        raptor.Store.LoadWritesBack = false;
        bool loaded;
        try { loaded = await raptor.LoadAsync(ct).ConfigureAwait(false); }
        catch (Microsoft.Data.Sqlite.SqliteException) { loaded = false; } // read-only: a "discard the tree" write is refused
        if (!loaded) { TreeNote = "the stored tree could not be loaded"; raptor.Dispose(); return; }
        Raptor = raptor;
        if (VectorSearch) Rag.Augmenter = raptor; // recall pipeline: hybrid lists + RAPTOR fused by RRF, exactly like the bot
        _nodeDocs = Db.Query("""
            SELECT raptor_idx, id, ref, kind, tag, title, source, updated, dimension FROM docs WHERE raptor_idx IS NOT NULL
            """, r => (Idx: (int)r.GetInt64(0), Doc: new NodeDoc(r.GetInt64(1), r.GetString(2), r.GetString(3), AgentDb.StrOrNull(r, 4), r.GetString(5),
                AgentDb.StrOrNull(r, 6), AgentDb.FromUnixMs(r.GetInt64(7)), (int)r.GetInt64(8))))
            .GroupBy(x => x.Idx).ToDictionary(g => g.Key, g => g.First().Doc);
    }

    /// <summary>Tree read under the index's lock.</summary>
    public T ReadTree<T>(Func<RaptorTree, T> read, T fallback) => Raptor is { } r ? r.Index.Read(read) : fallback;

    public NodeDoc? DocOf(int nodeIndex) => _nodeDocs.GetValueOrDefault(nodeIndex);

    /// <summary>Tree node of a doc id (leaf or summary), if it is in the tree.</summary>
    public int? NodeOfDoc(long docId)
    {
        foreach (var (idx, d) in _nodeDocs) if (d.Id == docId) return idx;
        return null;
    }

    /// <summary>The first chain of parents from a node up to a root (soft clustering: the lowest-index parent at each step).</summary>
    public IReadOnlyList<int> PathToRoot(int index) => ReadTree(t =>
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
        return (IReadOnlyList<int>)path;
    }, []);

    public static SourceKind Classify(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (string.Equals(full, System.IO.Path.GetFullPath(ExplorerPaths.LocalDevDb), StringComparison.OrdinalIgnoreCase)) return SourceKind.LocalDev;
        if (string.Equals(full, System.IO.Path.GetFullPath(ExplorerPaths.SeedDb), StringComparison.OrdinalIgnoreCase)) return SourceKind.Seed;
        if (full.StartsWith(System.IO.Path.GetFullPath(ExplorerPaths.SnapshotDir), StringComparison.OrdinalIgnoreCase)) return SourceKind.Snapshot;
        return SourceKind.Other;
    }

    /// <summary><c>memory-20261028-143005.db</c> → that UTC time; otherwise the file's last write time.</summary>
    public static DateTimeOffset SnapshotTime(string path)
    {
        if (SnapshotService.TryParseStamp(System.IO.Path.GetFileName(path), out var at)) return at;
        var wal = path + "-wal";
        var t = File.GetLastWriteTimeUtc(path);
        if (File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > t) t = File.GetLastWriteTimeUtc(wal);
        return new DateTimeOffset(t, TimeSpan.Zero);
    }

    public static long FileSize(string path) =>
        new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    public static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => (b / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (b / 1024.0 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (b / 1024.0 / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };

    public void Dispose()
    {
        Raptor?.Dispose();
        Rag.Dispose();
        Db.Dispose();
    }
}

/// <summary>
/// IEmbedder with the tree's stored model id and size (so the stored tree loads); embeds through the configured provider only
/// when that provider makes the same vectors, else refuses. Never caches into the db.
/// </summary>
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

/// <summary>Browsing never summarizes: any call is a bug.</summary>
public sealed class ReadOnlySummarizer : ISummarizer
{
    public static ReadOnlySummarizer Instance { get; } = new();
    public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("read-only explorer: summaries are only written by an explicit rebuild");
}
