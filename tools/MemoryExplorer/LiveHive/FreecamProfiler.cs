using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MineClaw.MemoryExplorer.ViewModels;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// <c>--freecam-profile OUT.json [--profile-seconds N] [--renderer cpu|gl] [--profile-size WxH]</c>: opens the real window on the
/// Live Hive free camera, connects (read-only GETs), waits for the world to load, then flies a scripted path for N seconds with
/// mouse-look and WASD injected on the UI thread at pointer-event rate, and writes frame pacing, frame cost, UI-thread cost,
/// input latency, UI queue delay, sync/ingest timings and GC numbers as JSON. Then exits.
/// </summary>
internal static class FreecamProfiler
{
    public static void Start(Window window, MainWindowViewModel vm, StartupOptions o)
    {
        if (o.ProfileSize is { } sz) { window.Width = sz.W; window.Height = sz.H; }
        var lh = vm.LiveHive;
        var run = new ProfileRun(window, lh, o);
        Dispatcher.UIThread.Post(() => { vm.Navigate("live"); lh.SelectedTab = 1; }, DispatcherPriority.Background);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(4), DispatcherPriority.Input, (_, _) => run.Tick());
        timer.Start();
    }

    /// <summary>One profiling run: waits for the world, warms up for 3 s, measures for N s while flying, then writes the report.</summary>
    private sealed class ProfileRun(Window window, LiveHiveViewModel lh, StartupOptions o)
    {
        private readonly FreecamStats _stats = FreecamStats.Shared;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly double _seconds = Math.Clamp(o.ProfileSeconds, 3, 300);
        private readonly StatRing _uiDelay = new(4096);
        private readonly StatRing _rafIntervals = new(4096);
        private int _phase; // 0 wait for data, 1 warm-up, 2 measure
        private double _phaseAt;
        private long _gc0, _gc1, _gc2, _alloc, _uiAlloc, _meshAlloc;
        private double _pause, _cpu, _compositorFrames;
        private double _lastRaf;
        private bool _rafOn;
        private double _lastInput;

        private void Raf(TimeSpan _)
        {
            double now = FreecamStats.NowMs;
            if (_phase == 2) { if (_lastRaf > 0) _rafIntervals.Add(now - _lastRaf); _compositorFrames++; }
            _lastRaf = now;
            if (_rafOn) TopLevel.GetTopLevel(window)?.RequestAnimationFrame(Raf);
        }

        public void Tick()
        {
            double t = _clock.Elapsed.TotalSeconds;
            if (_phase == 0)
            {
                WaitForData(t);
                return;
            }
            Fly(t);
            if (_phase == 1 && t - _phaseAt >= 3) BeginMeasuring(t);
            else if (_phase == 2 && ShotDue(t) && o.FreecamProfile is { } fp)
                lh.Gpu.ShotPath = Path.ChangeExtension(Path.GetFullPath(fp), ".png");
            else if (_phase == 2 && t - _phaseAt >= _seconds) Finish(t);
        }

        /// <summary>Starts the warm-up once the world is in (connected, 120 columns, camera placed, renderer up) or after 45 s.</summary>
        private void WaitForData(double t)
        {
            bool ready = lh.IsConnected && lh.CachedColumns > 120 && lh.CameraPlaced && lh.RendererReady;
            if (!ready && t <= 45) return;
            _phase = 1; _phaseAt = t;
            _rafOn = true;
            TopLevel.GetTopLevel(window)?.RequestAnimationFrame(Raf);
        }

        /// <summary>Scripted flight: yaw sweeps 50°/s with a pitch wobble; keys cycle W, D, S, A every 1.5 s, Shift on the second lap.</summary>
        private void Fly(double t)
        {
            double dt = _lastInput == 0 ? 0 : t - _lastInput;
            _lastInput = t;
            double pxPerSec = 50 / lh.LookSensitivity;
            lh.InjectLook(pxPerSec * dt, Math.Sin(t * 1.3) * 20 * dt / lh.LookSensitivity);
            int k = (int)((t - _phaseAt) / 1.5) % 8;
            lh.InjectKeys(k % 4 == 0, k % 4 == 2, k % 4 == 3, k % 4 == 1, false, false, k >= 4);
            // UI queue delay: how long an input-priority job waits behind whatever the UI thread is doing.
            double posted = FreecamStats.NowMs;
            Dispatcher.UIThread.Post(() => { if (_phase == 2) _uiDelay.Add(FreecamStats.NowMs - posted); }, DispatcherPriority.Input);
        }

        private void BeginMeasuring(double t)
        {
            _phase = 2; _phaseAt = t;
            _stats.ResetWindow();
            Interlocked.Exchange(ref _stats.Presented, 0);
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _uiAlloc = GC.GetAllocatedBytesForCurrentThread(); _meshAlloc = Interlocked.Read(ref _stats.MeshAllocBytes);
            _alloc = GC.GetTotalAllocatedBytes(); _pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            _cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        }

        /// <summary>The screenshot goes out in the last 0.3-0.25 s of the measured window.</summary>
        private bool ShotDue(double t) => t - _phaseAt >= _seconds - 0.3 && t - _phaseAt < _seconds - 0.25;

        private void Finish(double t)
        {
            _phase = 3;
            _rafOn = false;
            double span = t - _phaseAt;
            var report = Report(span);
            var path = Path.GetFullPath(o.FreecamProfile!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(path);
            lh.InjectKeys(false, false, false, false, false, false, false);
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }

        private static object S(StatRing r) { var s = r.Summary(); return new { mean = R(s.Mean), p50 = R(s.P50), p95 = R(s.P95), p99 = R(s.P99), max = R(s.Max), n = s.N }; }

        private object Report(double span) => new
        {
            renderer = _stats.Renderer,
            seconds = R(span),
            size = $"{_stats.Width}x{_stats.Height}",
            presented_fps = R(Interlocked.Read(ref _stats.Presented) / span),
            compositor_fps = R(_compositorFrames / span),
            hitches_over_25ms = Interlocked.Read(ref _stats.Hitches),
            frame_ms = S(_stats.FrameMs),
            raster_ms = S(_stats.RasterMs),
            present_ui_ms = S(_stats.PresentMs),
            present_interval_ms = S(_stats.IntervalMs),
            compositor_interval_ms = S(_rafIntervals),
            input_to_present_ms = S(_stats.InputLatencyMs),
            ui_queue_delay_ms = S(_uiDelay),
            sync_decode_ms = S(_stats.SyncMs),
            sync_fetch_ms = S(_stats.FetchMs),
            sync_on_ui_thread = Interlocked.Read(ref _stats.SyncOnUiThread),
            sync_batches = Interlocked.Read(ref _stats.SyncCalls),
            mesh_ms = S(_stats.MeshMs),
            upload_ms = S(_stats.UploadMs),
            producer_alloc_bytes_per_frame = S(_stats.AllocBytes),
            gc = new { gen0 = GC.CollectionCount(0) - _gc0, gen1 = GC.CollectionCount(1) - _gc1, gen2 = GC.CollectionCount(2) - _gc2,
                pause_ms = R(GC.GetTotalPauseDuration().TotalMilliseconds - _pause), alloc_mb_per_s = R((GC.GetTotalAllocatedBytes() - _alloc) / 1048576.0 / span) },
            alloc_mb_per_s_ui_thread = R((GC.GetAllocatedBytesForCurrentThread() - _uiAlloc) / 1048576.0 / span),
            alloc_mb_per_s_mesh_worker = R((Interlocked.Read(ref _stats.MeshAllocBytes) - _meshAlloc) / 1048576.0 / span),
            cpu_cores_busy = R((Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - _cpu) / (span * 1000)),
            cached_columns = lh.CachedColumns,
            sections = Interlocked.Read(ref _stats.SectionsLive),
            draw_calls = _stats.DrawCalls,
            entities_drawn = lh.Gpu.Scene.EntitiesDrawn,
            entities_known = lh.EntityCount,
            gl = lh.Gpu.GlInfo,
        };
    }

    private static double R(double v) => Math.Round(v, 2);
}
