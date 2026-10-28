using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using MineClaw.World;

namespace MineClaw.MemoryExplorer.LiveHive.Gpu;

public readonly record struct SectionKey(int Cx, int Sy, int Cz);

/// <summary>A finished section mesh waiting for upload (vertex arrays rented from the pool; <see cref="Release"/> returns them).
/// Zero counts = the section is now empty (delete its buffers). <see cref="Column"/> set = drop every section of that column.</summary>
public sealed class MeshUpload
{
    public SectionKey Key;
    public GpuVertex[]? Solid, Translucent;
    public int SolidCount, TranslucentCount;
    public bool DropColumn;
    public bool DropAll;

    public void Release()
    {
        if (Solid != null) ArrayPool<GpuVertex>.Shared.Return(Solid);
        if (Translucent != null) ArrayPool<GpuVertex>.Shared.Return(Translucent);
        Solid = Translucent = null;
    }
}

/// <summary>
/// Keeps GPU section meshes in step with the <see cref="HiveWorldCache"/>, off the UI thread: <see cref="Update"/> (the mesh
/// worker) walks the columns around the camera nearest first, diffs each against the column (and its four neighbours) it was
/// last meshed from, and rebuilds only the sections that changed — a block edit inside a section rebuilds that section; one on
/// a section face also its neighbour across that face; a changed height map (sky light) the sections its light reaches; a
/// neighbour column that appears, vanishes or changes along the shared edge the sections it borders. Results queue for the
/// render thread, which uploads them within a per-frame budget.
/// </summary>
public sealed class SectionStore
{
    private sealed class ColState
    {
        public ClientColumn? Src;
        public readonly ClientColumn?[] N = new ClientColumn?[4]; // W(−x) E(+x) N(−z) S(+z) at mesh time
        public byte Pending;   // sections still to rebuild
        public byte Meshed;   // sections that currently have a mesh uploaded (or queued)
    }

    private static readonly (int Dx, int Dz)[] Dirs = [(-1, 0), (1, 0), (0, -1), (0, 1)];
    private readonly HiveWorldCache _cache;
    private readonly Dictionary<(int, int), ColState> _cols = new();
    private readonly SectionMesher _mesher = new();
    private readonly ColumnNeighbourhood _hood = new();
    private readonly List<(int Dx, int Dz)> _ring = [];
    private readonly List<(int, int)> _drop = [];
    private int _ringR = -1, _dim = int.MinValue;
    private bool _textured;
    private volatile bool _resend;

    /// <summary>The GPU lost every buffer (new context): drop the queue and send every section again (any thread).</summary>
    public void RequestResend() => _resend = true;

    public SectionStore(HiveWorldCache cache) => _cache = cache;

    public ConcurrentQueue<MeshUpload> Ready { get; } = new();
    /// <summary>Sections rebuilt so far (tests and stats).</summary>
    public long Built;
    /// <summary>Every rebuilt key, when set (tests).</summary>
    public List<SectionKey>? Log { get; set; }
    public const int Sections = ChunkColumn.SizeY / 16;

    public bool Nether { get => _mesher.Nether; set => _mesher.Nether = value; }

    /// <summary>Switches textured/palette meshing; everything is rebuilt.</summary>
    public bool Textured
    {
        get => _textured;
        set { if (_textured == value) return; _textured = value; _mesher.Textured = value; foreach (var s in _cols.Values) s.Pending = AllSections(s.Src); }
    }

    private static byte AllSections(ClientColumn? c)
    {
        if (c == null) return 0;
        byte m = 0;
        for (int s = 0; s < Sections; s++) if (SectionMesher.NonEmpty(c.Snap, s)) m |= (byte)(1 << s);
        return m;
    }

    private void Ring(int r)
    {
        if (r == _ringR) return;
        _ringR = r;
        _ring.Clear();
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                if (dx * dx + dz * dz <= (r + 0.5) * (r + 0.5)) _ring.Add((dx, dz));
        _ring.Sort((a, b) => (a.Dx * a.Dx + a.Dz * a.Dz).CompareTo(b.Dx * b.Dx + b.Dz * b.Dz));
    }

    /// <summary>One pass: rebuilds at most <paramref name="maxSections"/> sections (or until <paramref name="maxMs"/>), nearest
    /// first. Returns the number rebuilt; call again while it returns &gt; 0.</summary>
    public int Update(int dim, int camCx, int camCz, int radius, int maxSections = int.MaxValue, double maxMs = double.MaxValue)
    {
        var sw = Stopwatch.StartNew();
        if (_resend)
        {
            _resend = false;
            while (Ready.TryDequeue(out var u)) u.Release();
            Ready.Enqueue(new MeshUpload { DropAll = true });
            foreach (var s in _cols.Values) { s.Pending = AllSections(s.Src); s.Meshed = 0; }
        }
        if (dim != _dim)
        {
            if (_dim != int.MinValue) Ready.Enqueue(new MeshUpload { DropAll = true });
            _cols.Clear();
            _dim = dim;
            _mesher.Nether = dim == -1;
        }
        Ring(radius);
        // Forget columns that left the area (their meshes are dropped on the render thread).
        _drop.Clear();
        foreach (var k in _cols.Keys)
            if (Math.Max(Math.Abs(k.Item1 - camCx), Math.Abs(k.Item2 - camCz)) > radius + 1) _drop.Add(k);
        foreach (var k in _drop)
        {
            _cols.Remove(k);
            Ready.Enqueue(new MeshUpload { Key = new SectionKey(k.Item1, 0, k.Item2), DropColumn = true });
        }
        int built = 0;
        foreach (var (dx, dz) in _ring)
        {
            int cx = camCx + dx, cz = camCz + dz;
            var cur = _cache.Get(dim, cx, cz);
            if (!_cols.TryGetValue((cx, cz), out var st))
            {
                if (cur == null) continue;
                st = new ColState();
                _cols[(cx, cz)] = st;
                st.Src = cur;
                for (int i = 0; i < 4; i++) st.N[i] = _cache.Get(dim, cx + Dirs[i].Dx, cz + Dirs[i].Dz);
                st.Pending = AllSections(cur);
            }
            else
            {
                if (!ReferenceEquals(cur, st.Src))
                {
                    st.Pending |= cur == null || st.Src == null ? (byte)(AllSections(cur) | st.Meshed) : Diff(st.Src.Snap, cur.Snap, st.Src.Top, cur.Top);
                    st.Src = cur;
                }
                for (int i = 0; i < 4; i++)
                {
                    var n = _cache.Get(dim, cx + Dirs[i].Dx, cz + Dirs[i].Dz);
                    if (ReferenceEquals(n, st.N[i])) continue;
                    if (cur != null) st.Pending |= EdgeDiff(i, st.N[i], n, cur);
                    st.N[i] = n;
                }
            }
            if (st.Pending == 0) continue;
            for (int sy = 0; sy < Sections; sy++)
            {
                if ((st.Pending >> sy & 1) == 0) continue;
                if (built >= maxSections || sw.Elapsed.TotalMilliseconds > maxMs) return built;
                st.Pending &= (byte)~(1 << sy);
                BuildOne(dim, cx, sy, cz, st);
                built++;
            }
        }
        return built;
    }

    private void BuildOne(int dim, int cx, int sy, int cz, ColState st)
    {
        var key = new SectionKey(cx, sy, cz);
        var up = new MeshUpload { Key = key };
        _hood.Set(_cache, dim, cx, cz);
        if (_hood.Centre is { } c && SectionMesher.NonEmpty(c.Snap, sy))
        {
            double t0 = FreecamStats.NowMs;
            _mesher.Build(_hood, cx, sy, cz);
            FreecamStats.Shared.MeshMs.Add(FreecamStats.NowMs - t0);
            up.SolidCount = _mesher.Solid.Count;
            up.TranslucentCount = _mesher.Translucent.Count;
            if (up.SolidCount > 0)
            {
                up.Solid = ArrayPool<GpuVertex>.Shared.Rent(up.SolidCount);
                Array.Copy(_mesher.Solid.V, up.Solid, up.SolidCount);
            }
            if (up.TranslucentCount > 0)
            {
                up.Translucent = ArrayPool<GpuVertex>.Shared.Rent(up.TranslucentCount);
                Array.Copy(_mesher.Translucent.V, up.Translucent, up.TranslucentCount);
            }
        }
        if (up.SolidCount + up.TranslucentCount > 0) st.Meshed |= (byte)(1 << sy);
        else if ((st.Meshed >> sy & 1) == 0) return; // empty and nothing to replace
        else st.Meshed &= (byte)~(1 << sy);
        Ready.Enqueue(up);
        Interlocked.Increment(ref Built);
        Log?.Add(key);
        Interlocked.Increment(ref FreecamStats.Shared.MeshesBuilt);
    }

    private static byte SpanMask(int lo, int hi)
    {
        lo = Math.Clamp(lo, 0, ChunkColumn.SizeY - 1); hi = Math.Clamp(hi, 0, ChunkColumn.SizeY - 1);
        byte m = 0;
        for (int s = lo >> 4; s <= hi >> 4; s++) m |= (byte)(1 << s);
        return m;
    }

    /// <summary>Sections of a column to rebuild after its blocks changed from <paramref name="a"/> to <paramref name="b"/>:
    /// each section with a changed block, plus the section across when the change touches its top/bottom layer, plus the
    /// sections whose sky light changed (between the old and new opaque top).</summary>
    public static byte Diff(ChunkSnapshot a, ChunkSnapshot b, byte[] topA, byte[] topB)
    {
        byte m = 0;
        for (int xz = 0; xz < 256; xz++)
        {
            int o = xz * ChunkColumn.SizeY;
            var ia = a.Ids.AsSpan(o, ChunkColumn.SizeY); var ib = b.Ids.AsSpan(o, ChunkColumn.SizeY);
            var ma = a.Meta.AsSpan(o, ChunkColumn.SizeY); var mb = b.Meta.AsSpan(o, ChunkColumn.SizeY);
            if (!ia.SequenceEqual(ib) || !ma.SequenceEqual(mb))
                for (int s = 0; s < Sections; s++)
                {
                    int y0 = s * 16;
                    if (ia.Slice(y0, 16).SequenceEqual(ib.Slice(y0, 16)) && ma.Slice(y0, 16).SequenceEqual(mb.Slice(y0, 16))) continue;
                    m |= (byte)(1 << s);
                    if (s > 0 && (ia[y0] != ib[y0] || ma[y0] != mb[y0])) m |= (byte)(1 << (s - 1));
                    if (s < Sections - 1 && (ia[y0 + 15] != ib[y0 + 15] || ma[y0 + 15] != mb[y0 + 15])) m |= (byte)(1 << (s + 1));
                }
            if (topA[xz] != topB[xz]) m |= SpanMask(Math.Min(topA[xz], topB[xz]) - 1, Math.Max(topA[xz], topB[xz]) + 1);
        }
        return m;
    }

    /// <summary>Sections of <paramref name="cur"/> to rebuild because its neighbour in direction <paramref name="dir"/> changed
    /// from <paramref name="a"/> to <paramref name="b"/>: all of them when the neighbour appeared or vanished, else only the
    /// sections facing changed cells of the neighbour's shared edge (blocks and their sky light).</summary>
    public static byte EdgeDiff(int dir, ClientColumn? a, ClientColumn? b, ClientColumn cur)
    {
        if (a == null || b == null) return AllSections(cur);
        byte m = 0;
        for (int i = 0; i < 16; i++)
        {
            // The neighbour's plane that touches us: W neighbour → its x=15, E → x=0, N → z=15, S → z=0.
            int x = dir switch { 0 => 15, 1 => 0, _ => i }, z = dir switch { 2 => 15, 3 => 0, _ => i };
            int xz = x * 16 + z, o = xz * ChunkColumn.SizeY;
            var ia = a.Snap.Ids.AsSpan(o, ChunkColumn.SizeY); var ib = b.Snap.Ids.AsSpan(o, ChunkColumn.SizeY);
            var ma = a.Snap.Meta.AsSpan(o, ChunkColumn.SizeY); var mb = b.Snap.Meta.AsSpan(o, ChunkColumn.SizeY);
            if (!ia.SequenceEqual(ib) || !ma.SequenceEqual(mb))
                for (int s = 0; s < Sections; s++)
                    if (!ia.Slice(s * 16, 16).SequenceEqual(ib.Slice(s * 16, 16)) || !ma.Slice(s * 16, 16).SequenceEqual(mb.Slice(s * 16, 16)))
                        m |= (byte)(1 << s);
            if (a.Top[xz] != b.Top[xz]) m |= SpanMask(Math.Min(a.Top[xz], b.Top[xz]), Math.Max(a.Top[xz], b.Top[xz]) + 1);
        }
        return m;
    }
}

/// <summary>
/// The mesh worker: a background thread that runs <see cref="SectionStore.Update"/> whenever the world cache changed or the
/// camera crossed a chunk, in small time slices (so a new camera position re-sorts the queue quickly), and stops producing
/// while the render thread has a backlog to upload.
/// </summary>
public sealed class MeshWorker : IDisposable
{
    private readonly SectionStore _store;
    private readonly HiveWorldCache _cache;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _run;
    private volatile int _dim, _cx, _cz, _radius = 5;
    private volatile bool _textured;

    public MeshWorker(SectionStore store, HiveWorldCache cache)
    {
        _store = store;
        _cache = cache;
    }

    public SectionStore Store => _store;
    public int MaxBacklog { get; set; } = 96;
    /// <summary>True when the last pass found nothing to rebuild.</summary>
    public bool Idle { get; private set; }

    public void Aim(int dim, int cx, int cz, int radiusChunks, bool textured)
    {
        bool moved = dim != _dim || cx != _cx || cz != _cz || radiusChunks != _radius || textured != _textured;
        (_dim, _cx, _cz, _radius, _textured) = (dim, cx, cz, radiusChunks, textured);
        if (moved) _wake.Set();
    }

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "livehive-mesher", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Loop()
    {
        long seen = -1;
        while (_run)
        {
            if (_store.Ready.Count > MaxBacklog) { _wake.WaitOne(4); continue; }
            long v = _cache.Version;
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            int n = 0;
            try
            {
                _store.Textured = _textured;
                n = _store.Update(_dim, _cx, _cz, _radius, 64, 6);
            }
            catch (Exception) { /* a racing eviction: next pass */ }
            Interlocked.Add(ref FreecamStats.Shared.MeshAllocBytes, GC.GetAllocatedBytesForCurrentThread() - a0);
            Idle = n == 0;
            FreecamStats.Shared.PendingMeshes = _store.Ready.Count;
            if (n > 0) continue;
            if (v == seen) _wake.WaitOne(60);
            seen = v;
        }
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
