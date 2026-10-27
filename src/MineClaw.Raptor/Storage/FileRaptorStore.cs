using System.Text;

namespace MineClaw.Raptor.Storage;

/// <summary>
/// Single-file binary store (tests, seed tool). Each apply rewrites the whole file via a temp file + atomic move,
/// so a crash leaves either the old or the new tree. Embeddings are stored as raw float32.
/// </summary>
public sealed class FileRaptorStore : IRaptorStore
{
    private const uint Magic = 0x52505452; // "RPTR"
    private const int Version = 1;

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SortedDictionary<int, RaptorNodeRecord>? _cache;
    private RaptorTreeMeta? _meta;

    public FileRaptorStore(string path) => _path = Path.GetFullPath(path);

    public string FilePath => _path;

    public async Task<RaptorSnapshot?> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return _meta == null ? null : new RaptorSnapshot(_meta, _cache!.Values.ToList());
        }
        finally { _lock.Release(); }
    }

    public async Task ApplyAsync(RaptorChangeSet changes, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var next = changes.ReplaceAll ? new SortedDictionary<int, RaptorNodeRecord>() : new SortedDictionary<int, RaptorNodeRecord>(_cache!);
            InMemoryRaptorStore.Apply(next, changes);
            Write(changes.Meta, next.Values);
            _cache = next;
            _meta = changes.Meta;
        }
        finally { _lock.Release(); }
    }

    private void EnsureLoaded()
    {
        if (_cache != null) return;
        _cache = new SortedDictionary<int, RaptorNodeRecord>();
        if (!File.Exists(_path)) return;
        using var fs = File.OpenRead(_path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException($"{_path} is not a RAPTOR tree file.");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"Unsupported RAPTOR file version {version}.");
        _meta = new RaptorTreeMeta(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadString(), r.ReadInt64(),
            new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble(),
            r.ReadDouble(), r.ReadInt32(), r.ReadInt64(), r.ReadDouble(), r.ReadDouble());
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            int index = r.ReadInt32(), layer = r.ReadInt32();
            string text = r.ReadString();
            int dims = r.ReadInt32();
            var emb = new float[dims];
            for (int d = 0; d < dims; d++) emb[d] = r.ReadSingle();
            int tokens = r.ReadInt32();
            int nc = r.ReadInt32();
            var children = new int[nc];
            for (int c = 0; c < nc; c++) children[c] = r.ReadInt32();
            string? key = ReadNullable(r);
            int chunk = r.ReadInt32();
            string? kind = ReadNullable(r), source = ReadNullable(r);
            byte flags = r.ReadByte();
            long seq = r.ReadInt64();
            _cache[index] = new RaptorNodeRecord(index, layer, text, emb, tokens, children, key, chunk, kind, source,
                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, seq);
        }
    }

    private void Write(RaptorTreeMeta m, IEnumerable<RaptorNodeRecord> nodes)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs, Encoding.UTF8))
        {
            var list = nodes.ToList();
            w.Write(Magic);
            w.Write(Version);
            w.Write(m.NumLayers); w.Write(m.NextIndex); w.Write(m.Dimensions); w.Write(m.EmbeddingModel ?? ""); w.Write(m.BuildId);
            w.Write(m.BuiltAt.UtcTicks); w.Write(m.LeavesAtBuild); w.Write(m.InsertedSinceBuild); w.Write(m.RemovedSinceBuild);
            w.Write(m.BaselineLeafParentSimilarity); w.Write(m.InsertedSimilaritySum); w.Write(m.InsertedSimilarityCount); w.Write(m.InsertSeq); w.Write(m.BaselineLeafParentSimilarityStd); w.Write(m.InsertTemperature);
            w.Write(list.Count);
            foreach (var n in list)
            {
                w.Write(n.Index); w.Write(n.Layer); w.Write(n.Text);
                w.Write(n.Embedding.Length);
                foreach (var f in n.Embedding) w.Write(f);
                w.Write(n.TokenCount);
                w.Write(n.Children.Length);
                foreach (var c in n.Children) w.Write(c);
                WriteNullable(w, n.Key);
                w.Write(n.Chunk);
                WriteNullable(w, n.Kind);
                WriteNullable(w, n.Source);
                w.Write((byte)((n.Pinned ? 1 : 0) | (n.Dirty ? 2 : 0) | (n.SummaryPending ? 4 : 0)));
                w.Write(n.InsertedSeq);
            }
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private static string? ReadNullable(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static void WriteNullable(BinaryWriter w, string? s)
    {
        w.Write(s != null);
        if (s != null) w.Write(s);
    }
}
