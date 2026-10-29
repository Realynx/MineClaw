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
/// section whose synced light changed, and the ones above and below it (smooth corners read across); a neighbour column that
/// appears, vanishes or changes along the shared edge the sections it borders; a new time-of-day light step (World.k) or light
/// mode everything, nearest first. Results queue for the render thread, which uploads them within a per-frame budget.
/// </summary>
public sealed class SectionStore
{
    private sealed class ColState
    {
        public ClientColumn? Src;
        public readonly ClientColumn?[] N = new ClientColumn?[4]; // W(−x) E(+x) N(−z) S(+z) at mesh time
        public ushort Pending;   // sections still to rebuild (bit = section y; 16 for a 256-high column)
        public ushort Meshed;   // sections that currently have a mesh uploaded (or queued)
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
    /// <summary>Most sections a column has (a 1.12.2 column: 16; a b1.8.1 one has 8, its own <c>Height / 16</c>).</summary>
    public const int Sections = ChunkColumn.MaxHeight / 16;

    public bool Nether { get => _mesher.Nether; set => _mesher.Nether = value; }

    /// <summary>Where the 1.12.2 block models come from (the textures' atlas); null = b1.8.1 shapes.</summary>
    public Func<MineClaw.Render.IBlockModels?>? ModelsOf { get; set; }

    /// <summary>Switches the block models (b1.8.1 shapes or 1.12.2 models); everything is rebuilt.</summary>
    public MineClaw.Render.IBlockModels? Models
    {
        get => _mesher.Models;
        set { if (ReferenceEquals(_mesher.Models, value)) return; _mesher.Models = value; foreach (var s in _cols.Values) s.Pending = AllSections(s.Src); }
    }

    /// <summary>World.k (the time of day's sky light step, 0..11): a new value rebuilds everything, nearest first.</summary>
    public int SkySubtracted
    {
        get => _mesher.SkySubtracted;
        set { if (_mesher.SkySubtracted == value) return; _mesher.SkySubtracted = value; RebuildAll(); }
    }

    /// <summary>Smooth (per-corner) or flat cube light; a change rebuilds everything.</summary>
    public bool Smooth
    {
        get => _mesher.Smooth;
        set { if (_mesher.Smooth == value) return; _mesher.Smooth = value; RebuildAll(); }
    }

    private void RebuildAll() { foreach (var s in _cols.Values) s.Pending = (ushort)(AllSections(s.Src) | s.Meshed); }

    /// <summary>Switches textured/palette meshing; everything is rebuilt.</summary>
    public bool Textured
    {
        get => _textured;
        set { if (_textured == value) return; _textured = value; _mesher.Textured = value; foreach (var s in _cols.Values) s.Pending = AllSections(s.Src); }
    }

    private static ushort AllSections(ClientColumn? c)
    {
        if (c == null) return 0;
        ushort m = 0;
        for (int s = 0, n = c.Snap.Height / 16; s < n; s++) if (SectionMesher.NonEmpty(c.Snap, s)) m |= (ushort)(1 << s);
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
                    st.Pending |= cur == null || st.Src == null ? (ushort)(AllSections(cur) | st.Meshed)
                        : (ushort)(Diff(st.Src.Snap, cur.Snap, st.Src.Top, cur.Top) | LightDiff(st.Src, cur));
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
                st.Pending &= (ushort)~(1 << sy);
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
        if (up.SolidCount + up.TranslucentCount > 0) st.Meshed |= (ushort)(1 << sy);
        else if ((st.Meshed >> sy & 1) == 0) return; // empty and nothing to replace
        else st.Meshed &= (ushort)~(1 << sy);
        Ready.Enqueue(up);
        Interlocked.Increment(ref Built);
        Log?.Add(key);
        Interlocked.Increment(ref FreecamStats.Shared.MeshesBuilt);
    }

    private static ushort SpanMask(int lo, int hi, int height)
    {
        lo = Math.Clamp(lo, 0, height - 1); hi = Math.Clamp(hi, 0, height - 1);
        ushort m = 0;
        for (int s = lo >> 4; s <= hi >> 4; s++) m |= (ushort)(1 << s);
        return m;
    }

    /// <summary>Every section of a column <paramref name="height"/> high.</summary>
    private static ushort AllBits(int height) => (ushort)((1 << (height / 16)) - 1);

    /// <summary>The 128-cell run of one local column in one half of a snapshot: y 0..127 at xz·128, y 128..255 at
    /// 2^15 + xz·128 (<see cref="ChunkColumn.Index"/>).</summary>
    private static int Run(int half, int xz) => half << 15 | xz * ChunkColumn.SizeY;

    /// <summary>Sections of a column to rebuild after its blocks changed from <paramref name="a"/> to <paramref name="b"/>:
    /// each section with a changed block, plus the section across when the change touches its top/bottom layer, plus the
    /// sections whose sky light changed (between the old and new opaque top).</summary>
    public static ushort Diff(ChunkSnapshot a, ChunkSnapshot b, byte[] topA, byte[] topB)
    {
        if (a.Height != b.Height) return AllBits(Math.Max(a.Height, b.Height));
        int height = b.Height, sections = height / 16, halves = height >> 7;
        ushort m = 0;
        for (int xz = 0; xz < 256; xz++)
        {
            for (int half = 0; half < halves; half++)
            {
                int o = Run(half, xz);
                var ia = a.Ids.AsSpan(o, ChunkColumn.SizeY); var ib = b.Ids.AsSpan(o, ChunkColumn.SizeY);
                var ma = a.Meta.AsSpan(o, ChunkColumn.SizeY); var mb = b.Meta.AsSpan(o, ChunkColumn.SizeY);
                if (ia.SequenceEqual(ib) && ma.SequenceEqual(mb)) continue;
                for (int ls = 0; ls < ChunkColumn.SizeY / 16; ls++)
                {
                    int y0 = ls * 16, s = half * 8 + ls;
                    if (ia.Slice(y0, 16).SequenceEqual(ib.Slice(y0, 16)) && ma.Slice(y0, 16).SequenceEqual(mb.Slice(y0, 16))) continue;
                    m |= (ushort)(1 << s);
                    if (s > 0 && (ia[y0] != ib[y0] || ma[y0] != mb[y0])) m |= (ushort)(1 << (s - 1));
                    if (s < sections - 1 && (ia[y0 + 15] != ib[y0 + 15] || ma[y0 + 15] != mb[y0 + 15])) m |= (ushort)(1 << (s + 1));
                }
            }
            if (topA[xz] != topB[xz]) m |= SpanMask(Math.Min(topA[xz], topB[xz]) - 1, Math.Max(topA[xz], topB[xz]) + 1, height);
        }
        return m;
    }

    /// <summary>Sections whose synced light (or the biomes: tints) changed from <paramref name="a"/> to <paramref name="b"/>, each with
    /// the sections above and below it (their boundary faces and smooth corners read its cells).</summary>
    public static ushort LightDiff(ClientColumn a, ClientColumn b)
    {
        var la = a.Light; var lb = b.Light;
        if (la == null && lb == null) return 0;
        int height = Math.Max(a.Snap.Height, b.Snap.Height);
        if (la == null || lb == null || la.Sections != lb.Sections || !SameBiomes(la, lb)) return AllBits(height);
        ushort m = 0;
        for (int s = 0; s < la.Sections; s++)
            if (!la.SameSection(lb, s)) m |= (ushort)(7 << s >> 1);
        return (ushort)(m & AllBits(height));
    }

    private static bool SameBiomes(LightSnapshot a, LightSnapshot b) =>
        a.Biomes == null ? b.Biomes == null : b.Biomes != null && a.Biomes.AsSpan().SequenceEqual(b.Biomes);

    /// <summary>Sections of <paramref name="cur"/> to rebuild because its neighbour in direction <paramref name="dir"/> changed
    /// from <paramref name="a"/> to <paramref name="b"/>: all of them when the neighbour appeared or vanished, else only the
    /// sections facing changed cells of the neighbour's shared edge (blocks and their sky light).</summary>
    public static ushort EdgeDiff(int dir, ClientColumn? a, ClientColumn? b, ClientColumn cur)
    {
        if (a == null || b == null || a.Snap.Height != b.Snap.Height) return AllSections(cur);
        int height = b.Snap.Height, halves = height >> 7;
        ushort m = 0;
        for (int i = 0; i < 16; i++)
        {
            // The neighbour's plane that touches us: W neighbour → its x=15, E → x=0, N → z=15, S → z=0.
            int x = dir switch { 0 => 15, 1 => 0, _ => i }, z = dir switch { 2 => 15, 3 => 0, _ => i };
            int xz = x * 16 + z;
            for (int half = 0; half < halves; half++)
            {
                int o = Run(half, xz);
                var ia = a.Snap.Ids.AsSpan(o, ChunkColumn.SizeY); var ib = b.Snap.Ids.AsSpan(o, ChunkColumn.SizeY);
                var ma = a.Snap.Meta.AsSpan(o, ChunkColumn.SizeY); var mb = b.Snap.Meta.AsSpan(o, ChunkColumn.SizeY);
                if (ia.SequenceEqual(ib) && ma.SequenceEqual(mb)) continue;
                for (int ls = 0; ls < ChunkColumn.SizeY / 16; ls++)
                    if (!ia.Slice(ls * 16, 16).SequenceEqual(ib.Slice(ls * 16, 16)) || !ma.Slice(ls * 16, 16).SequenceEqual(mb.Slice(ls * 16, 16)))
                        m |= (ushort)(1 << (half * 8 + ls));
            }
            if (a.Top[xz] != b.Top[xz]) m |= SpanMask(Math.Min(a.Top[xz], b.Top[xz]), Math.Max(a.Top[xz], b.Top[xz]) + 1, height);
        }
        // The neighbour's light (its edge cells light our boundary faces); biomes don't cross columns.
        if (a.Light is { } la && b.Light is { } lb && la.Sections == lb.Sections)
        {
            for (int s = 0; s < la.Sections; s++)
                if (!la.SameSection(lb, s)) m |= (ushort)(7 << s >> 1);
        }
        else if (a.Light != null || b.Light != null) m |= AllSections(cur);
        return (ushort)(m & AllBits(height));
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
    private volatile int _dim, _cx, _cz, _radius = 5, _sub;
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

    public void Aim(int dim, int cx, int cz, int radiusChunks, bool textured, int skySubtracted = 0)
    {
        bool moved = dim != _dim || cx != _cx || cz != _cz || radiusChunks != _radius || textured != _textured || skySubtracted != _sub;
        (_dim, _cx, _cz, _radius, _textured, _sub) = (dim, cx, cz, radiusChunks, textured, skySubtracted);
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
                _store.Models = _textured ? _store.ModelsOf?.Invoke() : null;
                _store.SkySubtracted = _sub;
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
