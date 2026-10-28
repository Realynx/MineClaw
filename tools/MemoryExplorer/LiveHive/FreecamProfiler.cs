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
        var stats = FreecamStats.Shared;
        var clock = Stopwatch.StartNew();
        int phase = 0; // 0 wait for data, 1 warm-up, 2 measure
        double phaseAt = 0, seconds = Math.Clamp(o.ProfileSeconds, 3, 300);
        long gc0 = 0, gc1 = 0, gc2 = 0, alloc = 0, uiAlloc = 0, meshAlloc = 0;
        double pause = 0, cpu = 0, compositorFrames = 0;
        var uiDelay = new StatRing(4096);
        var rafIntervals = new StatRing(4096);
        double lastRaf = 0;
        bool rafOn = false;

        void Raf(TimeSpan _)
        {
            double now = FreecamStats.NowMs;
            if (phase == 2) { if (lastRaf > 0) rafIntervals.Add(now - lastRaf); compositorFrames++; }
            lastRaf = now;
            if (rafOn) TopLevel.GetTopLevel(window)?.RequestAnimationFrame(Raf);
        }

        Dispatcher.UIThread.Post(() => { vm.Navigate("live"); lh.SelectedTab = 1; }, DispatcherPriority.Background);

        double lastInput = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(4), DispatcherPriority.Input, (_, _) =>
        {
            double t = clock.Elapsed.TotalSeconds;
            if (phase == 0)
            {
                bool ready = lh.IsConnected && lh.CachedColumns > 120 && lh.CameraPlaced && lh.RendererReady;
                if (ready || t > 45)
                {
                    phase = 1; phaseAt = t;
                    rafOn = true;
                    TopLevel.GetTopLevel(window)?.RequestAnimationFrame(Raf);
                }
                return;
            }
            // Scripted flight: yaw sweeps 50°/s with a pitch wobble; keys cycle W, D, S, A every 1.5 s, Shift on the second lap.
            double dt = lastInput == 0 ? 0 : t - lastInput;
            lastInput = t;
            double pxPerSec = 50 / lh.LookSensitivity;
            lh.InjectLook(pxPerSec * dt, Math.Sin(t * 1.3) * 20 * dt / lh.LookSensitivity);
            int k = (int)((t - phaseAt) / 1.5) % 8;
            lh.InjectKeys(k % 4 == 0, k % 4 == 2, k % 4 == 3, k % 4 == 1, false, false, k >= 4);
            // UI queue delay: how long an input-priority job waits behind whatever the UI thread is doing.
            double posted = FreecamStats.NowMs;
            Dispatcher.UIThread.Post(() => { if (phase == 2) uiDelay.Add(FreecamStats.NowMs - posted); }, DispatcherPriority.Input);

            if (phase == 1 && t - phaseAt >= 3)
            {
                phase = 2; phaseAt = t;
                stats.ResetWindow();
                Interlocked.Exchange(ref stats.Presented, 0);
                gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
                uiAlloc = GC.GetAllocatedBytesForCurrentThread(); meshAlloc = Interlocked.Read(ref stats.MeshAllocBytes);
                alloc = GC.GetTotalAllocatedBytes(); pause = GC.GetTotalPauseDuration().TotalMilliseconds;
                cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            }
            else if (phase == 2 && t - phaseAt >= seconds - 0.3 && t - phaseAt < seconds - 0.25 && o.FreecamProfile is { } fp)
                lh.Gpu.ShotPath = Path.ChangeExtension(Path.GetFullPath(fp), ".png");
            else if (phase == 2 && t - phaseAt >= seconds)
            {
                phase = 3;
                rafOn = false;
                double span = t - phaseAt;
                object S(StatRing r) { var s = r.Summary(); return new { mean = R(s.Mean), p50 = R(s.P50), p95 = R(s.P95), p99 = R(s.P99), max = R(s.Max), n = s.N }; }
                var report = new
                {
                    renderer = stats.Renderer,
                    seconds = R(span),
                    size = $"{stats.Width}x{stats.Height}",
                    presented_fps = R(Interlocked.Read(ref stats.Presented) / span),
                    compositor_fps = R(compositorFrames / span),
                    hitches_over_25ms = Interlocked.Read(ref stats.Hitches),
                    frame_ms = S(stats.FrameMs),
                    raster_ms = S(stats.RasterMs),
                    present_ui_ms = S(stats.PresentMs),
                    present_interval_ms = S(stats.IntervalMs),
                    compositor_interval_ms = S(rafIntervals),
                    input_to_present_ms = S(stats.InputLatencyMs),
                    ui_queue_delay_ms = S(uiDelay),
                    sync_decode_ms = S(stats.SyncMs),
                    sync_fetch_ms = S(stats.FetchMs),
                    sync_on_ui_thread = Interlocked.Read(ref stats.SyncOnUiThread),
                    sync_batches = Interlocked.Read(ref stats.SyncCalls),
                    mesh_ms = S(stats.MeshMs),
                    upload_ms = S(stats.UploadMs),
                    producer_alloc_bytes_per_frame = S(stats.AllocBytes),
                    gc = new { gen0 = GC.CollectionCount(0) - gc0, gen1 = GC.CollectionCount(1) - gc1, gen2 = GC.CollectionCount(2) - gc2,
                        pause_ms = R(GC.GetTotalPauseDuration().TotalMilliseconds - pause), alloc_mb_per_s = R((GC.GetTotalAllocatedBytes() - alloc) / 1048576.0 / span) },
                    alloc_mb_per_s_ui_thread = R((GC.GetAllocatedBytesForCurrentThread() - uiAlloc) / 1048576.0 / span),
                    alloc_mb_per_s_mesh_worker = R((Interlocked.Read(ref stats.MeshAllocBytes) - meshAlloc) / 1048576.0 / span),
                    cpu_cores_busy = R((Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - cpu) / (span * 1000)),
                    cached_columns = lh.CachedColumns,
                    sections = Interlocked.Read(ref stats.SectionsLive),
                    draw_calls = stats.DrawCalls,
                    entities_drawn = lh.Gpu.Scene.EntitiesDrawn,
                    entities_known = lh.EntityCount,
                    gl = lh.Gpu.GlInfo,
                };
                var path = Path.GetFullPath(o.FreecamProfile!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(path);
                lh.InjectKeys(false, false, false, false, false, false, false);
                (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        });
        timer.Start();
    }

    private static double R(double v) => Math.Round(v, 2);
}
