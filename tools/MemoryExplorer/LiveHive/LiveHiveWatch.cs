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
        var run = new WatchRun(window, lh, o);
        Dispatcher.UIThread.Post(() => { vm.Navigate("live"); lh.SelectedTab = o.ProfileTab == "freecam" ? 1 : 0; }, DispatcherPriority.Background);
        var beat = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) => run.Tick());
        beat.Start();
    }

    /// <summary>One watch: a 10 ms heartbeat that tracks UI gaps and map pictures, and a timeline entry each second.</summary>
    private sealed class WatchRun
    {
        private readonly Window _window;
        private readonly LiveHiveViewModel _lh;
        private readonly StartupOptions _o;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentDictionary<string, int> _errors = new();
        private readonly List<object> _timeline = [];
        private readonly Dictionary<string, object?> _mapSeen = [];
        private readonly Dictionary<string, int> _mapUpdates = [];
        private readonly Dictionary<FrameView, int> _lastShown = [];
        private readonly double _seconds;
        private double _lastBeat, _worstGap, _worstGapAll, _lastSecond;
        private int _gapsOver250;
        private long _lastGl;

        public WatchRun(Window window, LiveHiveViewModel lh, StartupOptions o)
        {
            _window = window;
            _lh = lh;
            _o = o;
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                var k = e.Exception.GetType().Name + ": " + (e.Exception.Message.Length > 120 ? e.Exception.Message[..120] : e.Exception.Message);
                _errors.AddOrUpdate(k, 1, (_, n) => n + 1);
            };
            _seconds = Math.Clamp(o.ProfileSeconds, 5, 600);
        }

        public void Tick()
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            Beat(now);
            CountMapUpdates();
            if (now - _lastSecond < 1000) return;
            _lastSecond = now;
            RecordSecond(now);
            if (now / 1000 >= _seconds) Finish();
        }

        /// <summary>The UI-thread gap since the last heartbeat (worst this second, worst overall, how many over 250 ms).</summary>
        private void Beat(double now)
        {
            if (_lastBeat > 0)
            {
                double gap = now - _lastBeat;
                _worstGap = Math.Max(_worstGap, gap);
                _worstGapAll = Math.Max(_worstGapAll, gap);
                if (gap > 250) _gapsOver250++;
            }
            _lastBeat = now;
        }

        /// <summary>A map tile's picture counts as updated when it is a different image object than last seen.</summary>
        private void CountMapUpdates()
        {
            foreach (var t in _lh.AllTiles)
                if (!ReferenceEquals(_mapSeen.GetValueOrDefault(t.Key), t.MapImage))
                {
                    _mapSeen[t.Key] = t.MapImage;
                    _mapUpdates[t.Key] = _mapUpdates.GetValueOrDefault(t.Key) + 1;
                }
        }

        private void RecordSecond(double now)
        {
            var tiles = new Dictionary<string, object>();
            foreach (var fv in _window.GetVisualDescendants().OfType<FrameView>())
            {
                if (fv is FreecamView || fv.DataContext is not BotTileViewModel t) continue;
                int shown = fv.FramesShown - _lastShown.GetValueOrDefault(fv);
                _lastShown[fv] = fv.FramesShown;
                tiles[t.Key] = new { eye_frames = shown, visible = fv.IsEffectivelyVisible };
            }
            foreach (var (k, n) in _mapUpdates) tiles[k + ":map"] = n;
            _mapUpdates.Clear();
            long gl = Interlocked.Read(ref FreecamStats.Shared.Presented);
            _timeline.Add(new
            {
                t = Math.Round(now / 1000, 1), state = _lh.StateText, ui_worst_gap_ms = Math.Round(_worstGap), tiles,
                gl_frames = gl - _lastGl, cached = _lh.CachedColumns, gc2 = GC.CollectionCount(2),
            });
            _lastGl = gl;
            _worstGap = 0;
        }

        private void Finish()
        {
            var path = Path.GetFullPath(_o.LiveHiveWatch!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                seconds = _seconds, ui_worst_gap_ms = Math.Round(_worstGapAll), ui_gaps_over_250ms = _gapsOver250,
                errors = _errors.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => kv.Value), timeline = _timeline,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(path);
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }
}
