using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>A fixed ring of samples (ms) with allocation-free percentiles; thread-safe (one lock, a few ns per add).</summary>
public sealed class StatRing(int capacity = 240)
{
    private readonly double[] _v = new double[capacity];
    private readonly double[] _sorted = new double[capacity];
    private readonly object _lock = new();
    private int _n, _i;
    private long _total;

    public long Total => Interlocked.Read(ref _total);

    public void Add(double ms)
    {
        lock (_lock)
        {
            _v[_i] = ms;
            _i = (_i + 1) % _v.Length;
            if (_n < _v.Length) _n++;
            _total++;
        }
    }

    public void Clear() { lock (_lock) { _n = 0; _i = 0; } }

    /// <summary>(mean, p50, p95, p99, max) of the samples held.</summary>
    public (double Mean, double P50, double P95, double P99, double Max, int N) Summary()
    {
        lock (_lock)
        {
            if (_n == 0) return (0, 0, 0, 0, 0, 0);
            Array.Copy(_v, _sorted, _n);
            Array.Sort(_sorted, 0, _n);
            double sum = 0;
            for (int i = 0; i < _n; i++) sum += _sorted[i];
            double P(double q) => _sorted[Math.Clamp((int)Math.Ceiling(q * _n) - 1, 0, _n - 1)];
            return (sum / _n, P(0.5), P(0.95), P(0.99), _sorted[_n - 1], _n);
        }
    }
}

/// <summary>
/// The free camera's measurements (the F3 overlay and <c>--freecam-profile</c> read them): producer frame cost, UI-thread work
/// (bitmap copy / GL submit), presented-frame intervals (pacing), input→photon latency, chunk ingest (decode on the sync worker,
/// mesh build, GPU upload), GC pauses and allocations. Everything is wall-clock via <see cref="Stopwatch"/>.
/// </summary>
public sealed class FreecamStats
{
    public static FreecamStats Shared { get; } = new();

    public static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    public static double NowMs => Stopwatch.GetTimestamp() * TickMs;

    /// <summary>Producer cost of one frame (CPU raster + overlays, or GL submit).</summary>
    public readonly StatRing FrameMs = new();
    public readonly StatRing RasterMs = new();
    /// <summary>UI-thread cost of showing a frame (WriteableBitmap copy, or GL draw-call submission).</summary>
    public readonly StatRing PresentMs = new();
    /// <summary>Time between consecutive presented frames (pacing).</summary>
    public readonly StatRing IntervalMs = new();
    /// <summary>Input event → first presented frame that includes it.</summary>
    public readonly StatRing InputLatencyMs = new();
    /// <summary>Chunk payload decode + cache apply (sync worker).</summary>
    public readonly StatRing SyncMs = new();
    /// <summary>Network wait per chunk batch (sync worker).</summary>
    public readonly StatRing FetchMs = new();
    /// <summary>One section mesh build (mesh worker).</summary>
    public readonly StatRing MeshMs = new();
    /// <summary>GPU uploads per frame (render thread, within the ingest budget).</summary>
    public readonly StatRing UploadMs = new();
    /// <summary>Bytes allocated by the producer per frame.</summary>
    public readonly StatRing AllocBytes = new();

    public long MeshAllocBytes;
    public long Frames, Presented, SyncOnUiThread, SyncCalls, MeshesBuilt, SectionsLive, Hitches;
    public volatile string Renderer = "cpu";
    public volatile int Width, Height;
    public volatile int DrawCalls;
    public volatile int PendingMeshes;
    private double _lastPresent;
    private readonly int _gc0 = GC.CollectionCount(0), _gc1 = GC.CollectionCount(1), _gc2 = GC.CollectionCount(2);

    /// <summary>A frame reached the screen (UI/render thread); <paramref name="inputStampMs"/> = oldest unshown input it carries (0 = none).</summary>
    public void OnPresented(double inputStampMs)
    {
        double now = NowMs;
        if (_lastPresent > 0)
        {
            double d = now - _lastPresent;
            IntervalMs.Add(d);
            if (d > 25) Interlocked.Increment(ref Hitches);
        }
        _lastPresent = now;
        if (inputStampMs > 0) InputLatencyMs.Add(now - inputStampMs);
        Interlocked.Increment(ref Presented);
    }

    public void ResetWindow()
    {
        foreach (var r in new[] { FrameMs, RasterMs, PresentMs, IntervalMs, InputLatencyMs, SyncMs, FetchMs, MeshMs, UploadMs, AllocBytes }) r.Clear();
        _lastPresent = 0;
        Interlocked.Exchange(ref Hitches, 0);
    }

    private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The F3 overlay text (allocates; drawn at most a few times a second).</summary>
    public string Overlay(double fps)
    {
        var sb = new StringBuilder(512);
        var fr = FrameMs.Summary(); var iv = IntervalMs.Summary(); var pr = PresentMs.Summary(); var il = InputLatencyMs.Summary();
        var sy = SyncMs.Summary(); var me = MeshMs.Summary(); var up = UploadMs.Summary(); var al = AllocBytes.Summary();
        sb.Append(CultureInfo.InvariantCulture, $"{Renderer}  {fps:0} fps  {Width}x{Height}  draws {DrawCalls}\n");
        sb.Append($"frame {F(fr.Mean)} ms (p95 {F(fr.P95)})  present {F(pr.Mean)} ms  interval p50 {F(iv.P50)} p99 {F(iv.P99)} max {F(iv.Max)}\n");
        sb.Append($"input→screen {F(il.P50)} ms (p95 {F(il.P95)})  hitches {Interlocked.Read(ref Hitches)}\n");
        sb.Append($"sync {F(sy.Mean)} ms/batch (ui-thread {Interlocked.Read(ref SyncOnUiThread)})  mesh {F(me.Mean)} ms  upload {F(up.Mean)} ms/frame  pending {PendingMeshes}  sections {Interlocked.Read(ref SectionsLive)}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"gc {GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2}  pause {GC.GetTotalPauseDuration().TotalMilliseconds:0} ms  alloc/frame {al.Mean / 1024:0.0} KB");
        return sb.ToString();
    }
}

/// <summary>What the profiler / view model drive on whichever free-camera renderer is active.</summary>
public interface IFreecamInput
{
    bool Ready { get; }
    void SetKeys(bool w, bool s, bool a, bool d, bool up, bool down, bool fast);
    /// <summary>The camera changed outside an input event: draw a frame.</summary>
    void Poke();
}
