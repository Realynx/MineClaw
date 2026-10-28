using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MineClaw.MemoryExplorer.ViewModels;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// <c>--livehive-watch OUT.json [--profile-seconds N] [--profile-tab feeds|freecam]</c>: opens the Live Hive (no input injected)
/// and records once a second what a user would see stall: frames shown per eye tile, server pictures updated per map tile, GPU
/// frames, the worst UI-thread gap (a 10 ms heartbeat), the link state, and every distinct first-chance exception. Then exits.
/// </summary>
internal static class LiveHiveWatch
{
    public static void Start(Window window, MainWindowViewModel vm, StartupOptions o)
    {
        if (o.ProfileSize is { } sz) { window.Width = sz.W; window.Height = sz.H; }
        var lh = vm.LiveHive;
        var clock = Stopwatch.StartNew();
        var errors = new ConcurrentDictionary<string, int>();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var k = e.Exception.GetType().Name + ": " + (e.Exception.Message.Length > 120 ? e.Exception.Message[..120] : e.Exception.Message);
            errors.AddOrUpdate(k, 1, (_, n) => n + 1);
        };
        Dispatcher.UIThread.Post(() => { vm.Navigate("live"); lh.SelectedTab = o.ProfileTab == "freecam" ? 1 : 0; }, DispatcherPriority.Background);
        var timeline = new List<object>();
        double lastBeat = 0, worstGap = 0, worstGapAll = 0;
        int gapsOver250 = 0;
        var mapSeen = new Dictionary<string, object?>();
        var mapUpdates = new Dictionary<string, int>();
        var lastShown = new Dictionary<FrameView, int>();
        long lastGl = 0;
        double lastSecond = 0, seconds = Math.Clamp(o.ProfileSeconds, 5, 600);
        var beat = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (lastBeat > 0)
            {
                double gap = now - lastBeat;
                worstGap = Math.Max(worstGap, gap);
                worstGapAll = Math.Max(worstGapAll, gap);
                if (gap > 250) gapsOver250++;
            }
            lastBeat = now;
            foreach (var t in lh.AllTiles)
                if (!ReferenceEquals(mapSeen.GetValueOrDefault(t.Key), t.MapImage))
                {
                    mapSeen[t.Key] = t.MapImage;
                    mapUpdates[t.Key] = mapUpdates.GetValueOrDefault(t.Key) + 1;
                }
            if (now - lastSecond < 1000) return;
            lastSecond = now;
            var tiles = new Dictionary<string, object>();
            foreach (var fv in window.GetVisualDescendants().OfType<FrameView>())
            {
                if (fv is FreecamView || fv.DataContext is not BotTileViewModel t) continue;
                int shown = fv.FramesShown - lastShown.GetValueOrDefault(fv);
                lastShown[fv] = fv.FramesShown;
                tiles[t.Key] = new { eye_frames = shown, visible = fv.IsEffectivelyVisible };
            }
            foreach (var (k, n) in mapUpdates) tiles[k + ":map"] = n;
            mapUpdates.Clear();
            long gl = Interlocked.Read(ref FreecamStats.Shared.Presented);
            timeline.Add(new
            {
                t = Math.Round(now / 1000, 1), state = lh.StateText, ui_worst_gap_ms = Math.Round(worstGap), tiles,
                gl_frames = gl - lastGl, cached = lh.CachedColumns, gc2 = GC.CollectionCount(2),
            });
            lastGl = gl;
            worstGap = 0;
            if (now / 1000 >= seconds)
            {
                var path = Path.GetFullPath(o.LiveHiveWatch!);
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    seconds, ui_worst_gap_ms = Math.Round(worstGapAll), ui_gaps_over_250ms = gapsOver250,
                    errors = errors.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => kv.Value), timeline,
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(path);
                (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        });
        beat.Start();
    }
}
