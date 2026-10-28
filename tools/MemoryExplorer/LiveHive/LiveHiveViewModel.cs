using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.MemoryExplorer.Services;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive;

public sealed record LayoutOption(string Key, string Label);
public sealed record DimOption(int Dim, string Label);

/// <summary>
/// The Live Hive tab: connects to the running hive's Supervisor (read-only GETs; the token header comes from
/// <see cref="SupervisorEndpoint"/> and is never shown), keeps a local copy of the merged world memory, and shows one feed tile
/// per online body (eye views rendered locally, iso/top/side from the server) plus a free camera over everything the hive
/// knows. Nothing here moves a bot or chats.
/// </summary>
public sealed partial class LiveHiveViewModel : ObservableObject, IDisposable
{
    private readonly HiveWorldCache _cache = new();
    private readonly FlyCamera _camera = new();
    private readonly Lazy<TextureAtlas?> _atlas;
    private readonly DispatcherTimer _stats;
    private SupervisorEndpoint? _endpoint;
    private HiveApi? _api;
    private HiveSync? _sync;
    private Process? _tunnel;
    private bool _active, _wantConnected, _autoTried;
    private volatile Interest[] _interests = [];
    private volatile (string, string)[] _mapViews = [];
    private volatile EyeTile[] _eyeTiles = [];
    private volatile BotDto[] _bots = [];
    private volatile EntityDto[] _entities = [];

    public LiveHiveViewModel()
    {
        _atlas = new Lazy<TextureAtlas?>(LoadAtlas, LazyThreadSafetyMode.ExecutionAndPublication);
        Freecam = new FreecamEngine(_cache, _camera, () => _atlas.IsValueCreated ? _atlas.Value : null)
        {
            Bots = () => _bots, Entities = () => _entities, Overlays = () => _sync?.Overlays(_camera.Pose.Dim),
        };
        Tiles = new TileRenderer(_cache, () => _atlas.IsValueCreated ? _atlas.Value : null)
        {
            Tiles = () => _eyeTiles, Bots = () => _bots, Entities = () => _entities,
        };
        _baseUrl = SupervisorEndpoint.DefaultUrl;
        _selectedLayout = Layouts[2];
        _selectedDim = Dims[0];
        _defaultMode = TileMode.All[0];
        _stats = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => RefreshStats());
        TokenStatus = new SupervisorEndpoint(_baseUrl).TokenStatus;
        _ = Task.Run(() => _atlas.Value).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            TextureStatus = t.Result != null ? "textures: local b1.8.1 atlas" : "textures: none found locally (flat colours)"), TaskScheduler.Default);
    }

    public FreecamEngine Freecam { get; }
    public TileRenderer Tiles { get; }

    // ------------------------------------------------------------------ connection

    [ObservableProperty] private string _baseUrl;
    [ObservableProperty] private string _stateText = "not connected";
    [ObservableProperty] private string _latencyText = "";
    [ObservableProperty] private string _tokenStatus = "";
    [ObservableProperty] private string _textureStatus = "textures: loading…";
    [ObservableProperty] private string _tunnelStatus = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _hasProblem;
    [ObservableProperty] private string _connectLabel = "Connect";
    [ObservableProperty] private string _syncText = "";

    /// <summary>The "not connected" card: only while there is nothing cached to look at (after a drop the tiles stay, stale).</summary>
    public bool ShowDisconnected => !IsConnected && AllTiles.Count == 0;
    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDisconnected));
        OnPropertyChanged(nameof(ShowNoBots));
    }

    [RelayCommand]
    private void ToggleConnect()
    {
        if (_sync != null) { _wantConnected = false; Disconnect(); }
        else { _wantConnected = true; Connect(); }
    }

    private void Connect()
    {
        Disconnect();
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            StateText = "the URL must look like http://10.1.2.73:8765";
            HasProblem = true;
            return;
        }
        _endpoint = new SupervisorEndpoint(BaseUrl.Trim());
        TokenStatus = _endpoint.TokenStatus;
        _api = new HiveApi(_endpoint);
        _sync = new HiveSync(_api, _cache)
        {
            Interests = () => _interests, MapViews = () => _mapViews,
            OverlayDim = () => IsFreecamTab && OverlaysWanted ? _camera.Pose.Dim : null,
        };
        _sync.StateChanged += () => Dispatcher.UIThread.Post(SyncStateChanged);
        _sync.EntitiesChanged += e => Dispatcher.UIThread.Post(() => ApplyEntities(e));
        _sync.ViewPng += (bot, mode, png) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var t in AllTiles) if (t.Key == bot && t.Mode.ServerMode == mode) t.SetMap(png);
        });
        ConnectLabel = "Disconnect";
        IsConnecting = true;
        if (_active) _sync.Start();
        else StateText = "connects when the tab is shown";
    }

    private void Disconnect()
    {
        _sync?.Dispose();
        _sync = null;
        _api?.Dispose();
        _api = null;
        IsConnected = false;
        IsConnecting = false;
        ConnectLabel = "Connect";
        StateText = "not connected";
        LatencyText = "";
    }

    private void SyncStateChanged()
    {
        if (_sync is not { } s) return;
        if (!_active)
        {
            (IsConnected, IsConnecting, HasProblem, StateText) = (false, false, false, "paused (tab hidden)");
            return;
        }
        IsConnected = s.State == LinkState.Connected;
        IsConnecting = s.State == LinkState.Connecting;
        HasProblem = s.State is LinkState.Unauthorized or LinkState.Error or LinkState.Disconnected && _wantConnected;
        StateText = s.State switch
        {
            LinkState.Connected => "connected",
            LinkState.Connecting => "connecting…",
            LinkState.Unauthorized => "unauthorized: check ~/.mineclaw-supervisor-token",
            _ => s.StateText,
        };
    }

    [RelayCommand]
    private void StartTunnel()
    {
        if (_tunnel is { HasExited: false })
        {
            try { _tunnel.Kill(entireProcessTree: true); } catch (Exception) { }
            _tunnel = null;
            TunnelStatus = "tunnel stopped";
            return;
        }
        try
        {
            _tunnel = SupervisorEndpoint.StartTunnel();
            _tunnel.EnableRaisingEvents = true;
            _tunnel.Exited += (_, _) => Dispatcher.UIThread.Post(() => TunnelStatus = $"tunnel exited ({SafeExit(_tunnel)})");
            TunnelStatus = "tunnel: ssh -N -L 8765:10.1.2.73:8765 alice";
            BaseUrl = "http://localhost:8765";
            if (_wantConnected || _sync != null) { _wantConnected = true; Connect(); }
        }
        catch (Exception ex) { TunnelStatus = "tunnel failed: " + ex.Message; }
    }

    private static string SafeExit(Process? p)
    {
        try { return p?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "?"; } catch (Exception) { return "?"; }
    }

    // ------------------------------------------------------------------ tiles and layouts

    public static IReadOnlyList<LayoutOption> Layouts { get; } =
        [new("1", "1"), new("2", "2"), new("4", "4"), new("6", "6"), new("focus", "Focus + strip")];
    public IReadOnlyList<TileMode> ModeOptions => TileMode.All;
    public IReadOnlyList<LayoutOption> LayoutOptions => Layouts;
    public IReadOnlyList<DimOption> DimOptions => Dims;

    public ObservableCollection<BotTileViewModel> AllTiles { get; } = [];
    public ObservableCollection<BotTileViewModel> GridTiles { get; } = [];
    public ObservableCollection<BotTileViewModel> StripTiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocusLayout), nameof(IsGridLayout))]
    private LayoutOption _selectedLayout;
    [ObservableProperty] private BotTileViewModel? _focusedTile;
    [ObservableProperty] private int _gridColumns = 2;
    [ObservableProperty] private int _gridRows = 2;
    [ObservableProperty] private TileMode _defaultMode;
    [ObservableProperty] private string _hiddenTiles = "";

    public bool IsFocusLayout => SelectedLayout.Key == "focus";
    public bool IsGridLayout => !IsFocusLayout;
    public bool ShowNoBots => IsConnected && AllTiles.Count == 0;

    partial void OnSelectedLayoutChanged(LayoutOption value) => Relayout();
    partial void OnFocusedTileChanged(BotTileViewModel? value) => Relayout();

    partial void OnDefaultModeChanged(TileMode value)
    {
        foreach (var t in AllTiles) t.Mode = value;
        UpdateWants();
    }

    /// <summary>Double-click on a tile: focus it (switches to focus + strip).</summary>
    public void Focus(BotTileViewModel t)
    {
        FocusedTile = t;
        if (!IsFocusLayout) SelectedLayout = Layouts[^1];
    }

    private void ApplyEntities(EntitiesDto e)
    {
        _bots = e.Bots.ToArray();
        _entities = e.Entities.ToArray();
        Interlocked.Increment(ref Freecam.DataVersion);
        Interlocked.Increment(ref Tiles.DataVersion);
        bool changed = false;
        foreach (var b in e.Bots)
        {
            var t = AllTiles.FirstOrDefault(x => x.Name == b.Name);
            if (t == null)
            {
                t = new BotTileViewModel(b, _cache, DefaultMode);
                t.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(BotTileViewModel.Mode)) UpdateWants(); };
                int at = b.Main ? 0 : AllTiles.TakeWhile(x => x.Bot?.Main == true || string.CompareOrdinal(x.Key, b.Key) < 0).Count();
                AllTiles.Insert(at, t);
                changed = true;
            }
            else t.Update(b);
        }
        foreach (var gone in AllTiles.Where(t => e.Bots.All(b => b.Name != t.Name)).ToList())
        {
            AllTiles.Remove(gone);
            gone.Dispose();
            changed = true;
        }
        if (!_camera.Placed && (e.Bots.FirstOrDefault(b => b.Main) ?? e.Bots.FirstOrDefault()) is { } first)
            _camera.Set(new CamPose(first.X - 6, first.Y + 6, first.Z - 6, 315, 30, first.Dim));
        if (changed) { Relayout(); OnPropertyChanged(nameof(ShowNoBots)); OnPropertyChanged(nameof(ShowDisconnected)); OnPropertyChanged(nameof(BotChoices)); }
        UpdateWants();
    }

    private bool _inRelayout;

    private void Relayout()
    {
        if (_inRelayout) return; // setting FocusedTile below re-enters through OnFocusedTileChanged
        _inRelayout = true;
        try { RelayoutCore(); }
        finally { _inRelayout = false; }
    }

    private void RelayoutCore()
    {
        int n = SelectedLayout.Key switch { "1" => 1, "2" => 2, "4" => 4, "6" => 6, _ => 0 };
        (GridColumns, GridRows) = n switch { 1 => (1, 1), 2 => (2, 1), 4 => (2, 2), 6 => (3, 2), _ => (1, 1) };
        GridTiles.Clear();
        StripTiles.Clear();
        if (IsFocusLayout)
        {
            if (FocusedTile == null || !AllTiles.Contains(FocusedTile)) FocusedTile = AllTiles.FirstOrDefault();
            foreach (var t in AllTiles) { t.IsFocused = t == FocusedTile; if (t != FocusedTile) StripTiles.Add(t); }
            HiddenTiles = "";
        }
        else
        {
            foreach (var t in AllTiles.Take(n)) { GridTiles.Add(t); t.IsFocused = false; }
            if (n > 0 && AllTiles.Count == 1) (GridColumns, GridRows) = (1, 1);
            else if (n >= 4 && AllTiles.Count <= 2) (GridColumns, GridRows) = (AllTiles.Count, 1);
            HiddenTiles = AllTiles.Count > n ? $"{AllTiles.Count - n} more online (pick a bigger grid or focus + strip)" : "";
        }
        UpdateWants();
    }

    private IEnumerable<BotTileViewModel> ShownTiles() =>
        !IsFeedsTab ? [] : IsFocusLayout ? (FocusedTile != null ? [FocusedTile, .. StripTiles] : StripTiles) : GridTiles;

    /// <summary>What the sync and the renderers need now: regions to keep loaded, server tiles, eye tiles.</summary>
    private void UpdateWants()
    {
        var shown = ShownTiles().ToList();
        var interests = new List<Interest>();
        if (IsFreecamTab && _camera.Placed)
        {
            var p = _camera.Pose;
            interests.Add(new Interest(p.Dim, (int)Math.Floor(p.X), (int)Math.Floor(p.Z), Freecam.MaxDistance + 24, 900));
        }
        foreach (var t in shown.Where(t => t.IsEye && t.Bot != null))
            interests.Add(new Interest(t.Bot!.Dim, (int)Math.Floor(t.Bot.X), (int)Math.Floor(t.Bot.Z), Tiles.MaxDistance + 16, 1500));
        _interests = interests.ToArray();
        _mapViews = shown.Where(t => t.IsMap).Select(t => (t.Key, t.Mode.ServerMode!)).ToArray();
        _eyeTiles = shown.Where(t => t.IsEye).Select(t => t.Eye).ToArray();
    }

    // ------------------------------------------------------------------ sub tabs

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFeedsTab), nameof(IsFreecamTab))]
    private int _selectedTab;

    public bool IsFeedsTab => SelectedTab == 0;
    public bool IsFreecamTab => SelectedTab == 1;

    partial void OnSelectedTabChanged(int value) => UpdateWants();

    // ------------------------------------------------------------------ free camera

    public static IReadOnlyList<DimOption> Dims { get; } = [new(0, "Overworld"), new(-1, "Nether")];

    [ObservableProperty] private DimOption _selectedDim;
    [ObservableProperty] private string _jumpText = "";
    [ObservableProperty] private string _jumpStatus = "";
    [ObservableProperty] private double _speed = 12;
    [ObservableProperty] private string _freecamStats = "";
    [ObservableProperty] private string _cacheStats = "";
    [ObservableProperty] private string _overlayNotes = "";

    [ObservableProperty] private bool _showBots = true;
    [ObservableProperty] private bool _showClaims = true;
    [ObservableProperty] private bool _showPlaces = true;
    [ObservableProperty] private bool _showChests = true;
    [ObservableProperty] private bool _showPlans = true;
    [ObservableProperty] private bool _showDangers = true;
    [ObservableProperty] private bool _showStaleness = true;

    private bool OverlaysWanted => ShowClaims || ShowPlaces || ShowChests || ShowPlans || ShowDangers;

    public IReadOnlyList<string> BotChoices => AllTiles.Select(t => t.Key).ToList();

    partial void OnShowBotsChanged(bool value) => PushFlags();
    partial void OnShowClaimsChanged(bool value) => PushFlags();
    partial void OnShowPlacesChanged(bool value) => PushFlags();
    partial void OnShowChestsChanged(bool value) => PushFlags();
    partial void OnShowPlansChanged(bool value) => PushFlags();
    partial void OnShowDangersChanged(bool value) => PushFlags();
    partial void OnShowStalenessChanged(bool value) => PushFlags();

    private void PushFlags()
    {
        Freecam.Flags = (ShowBots ? Overlay.Bots : 0) | (ShowClaims ? Overlay.Claims : 0) | (ShowPlaces ? Overlay.Places : 0)
            | (ShowChests ? Overlay.Chests : 0) | (ShowPlans ? Overlay.Plans : 0) | (ShowDangers ? Overlay.Dangers : 0)
            | (ShowStaleness ? Overlay.Staleness : 0);
        Interlocked.Increment(ref Freecam.DataVersion);
    }

    partial void OnSpeedChanged(double value) => _camera.Speed = Math.Clamp(value, 1, 200);

    partial void OnSelectedDimChanged(DimOption value)
    {
        if (_camera.Pose.Dim != value.Dim) _camera.SetDim(value.Dim);
        UpdateWants();
    }

    /// <summary>The wheel on the camera changed the speed.</summary>
    public void SpeedFromWheel(double v) => Speed = Math.Round(v, 1);

    [RelayCommand]
    private async Task Jump()
    {
        var q = JumpText.Trim();
        if (q.Length == 0) return;
        var parts = q.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var v = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            Place(new CamPose(v[0] + 0.5, v[1] + 1.6, v[2] + 0.5, _camera.Pose.Yaw, _camera.Pose.Pitch, _camera.Pose.Dim));
            JumpStatus = $"at {q}";
            return;
        }
        if (AllTiles.FirstOrDefault(t => t.Key.Equals(q, StringComparison.OrdinalIgnoreCase) || t.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) is { } tile)
        {
            SnapTo(tile);
            return;
        }
        if (_api == null) { JumpStatus = "connect first"; return; }
        JumpStatus = "looking up…";
        try
        {
            var hit = await _api.LocateAsync(q, CancellationToken.None);
            if (hit == null) { JumpStatus = $"no place called “{q}”"; return; }
            Place(new CamPose(hit.X - 4, hit.Y + 4, hit.Z - 4, 315, 35, hit.Dim));
            JumpStatus = $"{hit.Name} ({(hit.Dim == -1 ? "nether" : "overworld")})";
        }
        catch (HiveApiException ex) { JumpStatus = ex.Message; }
    }

    [RelayCommand]
    private void SnapTo(BotTileViewModel? t)
    {
        if (t?.Bot is not { } b) return;
        var yaw = b.Yaw ?? 0f;
        Place(new CamPose(b.X, b.Y + 1.62, b.Z, yaw, b.Pitch ?? 20f, b.Dim));
        JumpStatus = $"at {b.Key}'s eyes";
        SelectedTab = 1;
    }

    private void Place(CamPose p)
    {
        _camera.Set(p);
        SelectedDim = Dims.FirstOrDefault(d => d.Dim == p.Dim) ?? Dims[0];
        UpdateWants();
    }

    // ------------------------------------------------------------------ lifecycle

    private int _lastIndexX = int.MinValue, _lastIndexZ = int.MinValue;

    private void RefreshStats()
    {
        if (_api is { } api)
        {
            double ms = api.LatencyMs;
            LatencyText = ms < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms");
        }
        var p = _camera.Pose;
        FreecamStats = string.Create(CultureInfo.InvariantCulture,
            $"{Freecam.Fps:0} fps · raster {Freecam.RasterMs:0.0} ms · {Freecam.LastW}x{Freecam.LastH} on {Freecam.Threads} threads");
        CacheStats = string.Create(CultureInfo.InvariantCulture,
            $"{_cache.Count} columns cached ({_cache.CountIn(p.Dim)} here) · {(_api?.BytesIn ?? 0) / 1024.0 / 1024.0:0.0} MB received");
        OverlayNotes = _sync?.Overlays(p.Dim) is { } ov
            ? $"{ov.Claims.Count} claims · {ov.Places.Count} places · {ov.Points.Count} points · {ov.Plans.Sum(x => x.Blocks.Count)} plan blocks · {ov.Dangers.Count} dangers"
              + (ov.Notes.Count > 0 ? " · " + string.Join(", ", ov.Notes) : "")
            : "";
        SyncText = _sync?.EntitiesAt is { } at && at > DateTime.MinValue ? $"bots updated {Math.Max(0, (DateTime.UtcNow - at).TotalSeconds):0.0} s ago" : "";
        // The camera moved on: re-aim the world sync at it.
        int bx = (int)Math.Floor(p.X) >> 4, bz = (int)Math.Floor(p.Z) >> 4;
        if (bx != _lastIndexX || bz != _lastIndexZ) { _lastIndexX = bx; _lastIndexZ = bz; UpdateWants(); }
    }

    public void Activate()
    {
        _active = true;
        Freecam.Start();
        Tiles.Start();
        _stats.Start();
        PushFlags();
        if (_sync == null && !_autoTried) { _autoTried = true; _wantConnected = true; Connect(); } // first show: connect once
        _sync?.Start();
        UpdateWants();
    }

    public void Deactivate()
    {
        _active = false;
        _stats.Stop();
        _sync?.Stop();
        Freecam.Stop();
        Tiles.Stop();
        if (_sync != null) { IsConnected = false; StateText = "paused (tab hidden)"; }
    }

    private static TextureAtlas? LoadAtlas()
    {
        try
        {
            var dir = Path.Combine(ExplorerPaths.DataDir, "assets");
            return TextureLocator.Load(new TextureOptions { AssetsDir = Directory.Exists(dir) ? dir : null, AllowDownload = false, SearchLauncherDirs = true });
        }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        _stats.Stop();
        Disconnect();
        Freecam.Dispose();
        Tiles.Dispose();
        foreach (var t in AllTiles) t.Dispose();
        if (_tunnel is { HasExited: false })
            try { _tunnel.Kill(entireProcessTree: true); } catch (Exception) { }
    }
}
