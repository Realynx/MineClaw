using System.Collections.Concurrent;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>One cached column: blocks as last fetched, its hash (the delta key), who saw it when, and an opaque-top height map
/// (remembered columns carry no light: sky light is estimated from it). Immutable; replaced on update.</summary>
public sealed class ClientColumn
{
    public ClientColumn(int cx, int cz, long hash, long seenMs, string? by, bool live, ChunkSnapshot snap)
    {
        Cx = cx;
        Cz = cz;
        Hash = hash;
        SeenMs = seenMs;
        By = by;
        Live = live;
        Snap = snap;
        Top = new byte[256];
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
            {
                int y = snap.Height - 1;
                while (y > 0 && !FaceLight.Opaque(snap.Ids[ChunkColumn.Index(x, y, z)])) y--;
                Top[x * 16 + z] = (byte)y;
            }
    }

    private ClientColumn(ClientColumn c, long seenMs, string? by, bool live)
    {
        (Cx, Cz, Hash, Snap, Top) = (c.Cx, c.Cz, c.Hash, c.Snap, c.Top);
        (SeenMs, By, Live) = (seenMs, by, live);
    }

    public int Cx { get; }
    public int Cz { get; }
    public long Hash { get; }
    public long SeenMs { get; }
    public string? By { get; }
    public bool Live { get; }
    public ChunkSnapshot Snap { get; }
    public byte[] Top { get; }

    public ClientColumn Touched(long seenMs, string? by, bool live) => new(this, seenMs, by, live);
}

/// <summary>
/// The viewer's copy of the hive's merged world memory, per dimension, fed by the index/chunks routes. Delta logic: an index
/// lists (cx, cz, hash) for a rectangle; <see cref="Reconcile"/> returns only the columns whose hash differs from the cache (or
/// that are new), refreshes seen/by on the rest, and drops cached columns the index no longer lists. Thread-safe: the sync
/// loop writes, the renderers read (each through its own <see cref="HiveVoxels"/>).
/// </summary>
public sealed class HiveWorldCache
{
    private readonly ConcurrentDictionary<(int Dim, int Cx, int Cz), ClientColumn> _cols = new();
    private long _version;
    private int _height = ChunkColumn.SizeY;

    public int Count => _cols.Count;
    /// <summary>Bumped on every change (renderers redraw when it moves).</summary>
    public long Version => Interlocked.Read(ref _version);
    public long BytesReceived { get; private set; }
    /// <summary>Blocks per column: the highest snapshot seen (b1.8.1 128, 1.12.2 256).</summary>
    public int WorldHeight => Volatile.Read(ref _height);

    public ClientColumn? Get(int dim, int cx, int cz) => _cols.GetValueOrDefault((dim, cx, cz));

    public int CountIn(int dim) => _cols.Keys.Count(k => k.Dim == dim);

    /// <summary>Columns to fetch for an index of the rectangle (cx0..cx1, cz0..cz1): new or changed hash. Same-hash columns get
    /// the index's seen/by/live; cached columns inside the rectangle that the index doesn't list are dropped.</summary>
    public List<(int Cx, int Cz)> Reconcile(int dim, int cx0, int cz0, int cx1, int cz1, IReadOnlyList<ColumnInfoDto> index)
    {
        var want = new List<(int, int)>();
        var listed = new HashSet<(int, int)>();
        bool changed = false;
        foreach (var c in index)
        {
            listed.Add((c.Cx, c.Cz));
            if (_cols.TryGetValue((dim, c.Cx, c.Cz), out var have) && have.Hash == c.Hash)
            {
                if (have.SeenMs != c.Seen || have.Live != c.Live || have.By != c.By)
                {
                    _cols[(dim, c.Cx, c.Cz)] = have.Touched(c.Seen, c.By, c.Live);
                    changed = true;
                }
                continue;
            }
            want.Add((c.Cx, c.Cz));
        }
        foreach (var k in _cols.Keys)
            if (k.Dim == dim && k.Cx >= cx0 && k.Cx <= cx1 && k.Cz >= cz0 && k.Cz <= cz1 && !listed.Contains((k.Cx, k.Cz)) && _cols.TryRemove(k, out _))
                changed = true;
        if (changed) Interlocked.Increment(ref _version);
        return want;
    }

    /// <summary>Decodes a <see cref="ColumnPayload"/> and stores its columns (a missing one is dropped). Returns columns stored.
    /// Throws <see cref="InvalidDataException"/> on a bad payload (nothing is applied then).</summary>
    public int Apply(ReadOnlySpan<byte> payload)
    {
        var (dim, cols) = ColumnPayload.Decode(payload);
        var decoded = new List<(ColumnPayloadEntry E, ChunkSnapshot? S)>(cols.Count);
        foreach (var c in cols) decoded.Add((c, c.Missing ? null : ChunkSnapshot.Decode(c.Data)));
        int n = 0;
        foreach (var (c, s) in decoded)
        {
            if (s == null) { _cols.TryRemove((dim, c.Cx, c.Cz), out _); continue; }
            _cols[(dim, c.Cx, c.Cz)] = new ClientColumn(c.Cx, c.Cz, c.Hash, c.SeenMs, c.By, c.Live, s);
            if (s.Height > _height) Volatile.Write(ref _height, s.Height);
            n++;
        }
        BytesReceived += payload.Length;
        Interlocked.Increment(ref _version);
        return n;
    }

    /// <summary>Drops the columns furthest from (cx, cz) of <paramref name="dim"/> until at most <paramref name="max"/> remain in it.</summary>
    public int Trim(int dim, int cx, int cz, int max)
    {
        var keys = _cols.Keys.Where(k => k.Dim == dim).ToList();
        if (keys.Count <= max) return 0;
        int n = 0;
        foreach (var k in keys.OrderByDescending(k => (long)(k.Cx - cx) * (k.Cx - cx) + (long)(k.Cz - cz) * (k.Cz - cz)).Take(keys.Count - max))
            if (_cols.TryRemove(k, out _)) n++;
        if (n > 0) Interlocked.Increment(ref _version);
        return n;
    }

    public void Clear()
    {
        _cols.Clear();
        Interlocked.Increment(ref _version);
    }
}

/// <summary>
/// <see cref="IVoxelWorld"/> over the cache for one renderer: <see cref="Prepare"/> snapshots the column references around the
/// eye once per frame (array lookups after that). Recently seen columns render as live; older ones as remembered (the
/// raycaster dims them). Light: open sky above the opaque top, unknown (an even 0.75 shade) below. Not thread-safe; one per renderer.
/// </summary>
public sealed class HiveVoxels(HiveWorldCache cache) : IVoxelWorld
{
    private ClientColumn?[] _a = [];
    private int _cx0, _cz0, _n, _dim, _h = ChunkColumn.SizeY;
    private long _freshAfterMs;

    /// <summary>Columns seen within this long count as live (not dimmed).</summary>
    public TimeSpan Fresh { get; set; } = TimeSpan.FromMinutes(2);
    public int Dim => _dim;
    /// <summary>Blocks per column as of the last <see cref="Prepare"/>.</summary>
    public int WorldHeight => _h;

    public void Prepare(int dim, int x, int z, int radiusChunks, DateTimeOffset now)
    {
        _dim = dim;
        _h = cache.WorldHeight;
        _n = 2 * radiusChunks + 1;
        _cx0 = (x >> 4) - radiusChunks;
        _cz0 = (z >> 4) - radiusChunks;
        if (_a.Length != _n * _n) _a = new ClientColumn?[_n * _n];
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
                _a[i * _n + j] = cache.Get(dim, _cx0 + i, _cz0 + j);
        _freshAfterMs = (now - Fresh).ToUnixTimeMilliseconds();
    }

    public ClientColumn? Column(int x, int z)
    {
        int i = (x >> 4) - _cx0, j = (z >> 4) - _cz0;
        return (uint)i < (uint)_n && (uint)j < (uint)_n ? _a[i * _n + j] : cache.Get(_dim, x >> 4, z >> 4);
    }

    public int Block(int x, int y, int z) =>
        (uint)y >= _h || Column(x, z) is not { } c || y >= c.Snap.Height ? 0 : c.Snap.Ids[ChunkColumn.Index(x & 15, y, z & 15)];

    public int Meta(int x, int y, int z) =>
        (uint)y >= _h || Column(x, z) is not { } c || y >= c.Snap.Height ? 0 : c.Snap.Meta[ChunkColumn.Index(x & 15, y, z & 15)];

    public int SkyLight(int x, int y, int z)
    {
        if (y >= _h) return 15;
        if (y < 0) return 0;
        if (Column(x, z) is not { } c) return 15; // nothing known: open void
        return y > c.Top[(x & 15) * 16 + (z & 15)] ? 15 : -1;
    }

    public int BlockLight(int x, int y, int z) => SkyLight(x, y, z) < 0 ? -1 : 0;

    public ColumnState State(int x, int z) => Column(x, z) is not { } c ? ColumnState.Unknown
        : c.SeenMs >= _freshAfterMs ? ColumnState.Live : ColumnState.Remembered; // seen is refreshed by every index of the area
}
