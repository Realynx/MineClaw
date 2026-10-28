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
    private ScriptBadgePoller? _badges;
    private VisionTilePoller? _visionTiles;
    private volatile (string Key, long Since)[] _visionWanted = [];
    private Process? _tunnel;
    private bool _active, _wantConnected, _autoTried;
    private volatile Interest[] _interests = [];
    private volatile (string, string)[] _mapViews = [];
    private volatile EyeTile[] _eyeTiles = [];
    private volatile BotDto[] _bots = [];
    private volatile EntityDto[] _entities = [];
    private readonly EntityStore _entityStore = new();
    private readonly List<EntityView> _dropScratch = [];

    public LiveHiveViewModel()
    {
        Stats.UiThreadId = Environment.CurrentManagedThreadId; // constructed on the UI thread
        _atlas = new Lazy<TextureAtlas?>(LoadAtlas, LazyThreadSafetyMode.ExecutionAndPublication);
        Freecam = new FreecamEngine(_cache, _camera, () => _atlas.IsValueCreated ? _atlas.Value : null)
        {
            Bots = () => _bots, Entities = () => _entities, Overlays = () => _sync?.Overlays(_camera.Pose.Dim),
        };
        Gpu = new Gpu.GpuFreecam(_cache, _camera, () => _atlas.IsValueCreated ? _atlas.Value : null);
        Gpu.Scene.Entities = _entityStore;
        Gpu.Scene.Bots = () => _bots;
        Gpu.Scene.Atlas = () => _atlas.IsValueCreated ? _atlas.Value : null;
        Gpu.Scene.Overlays = () => _sync?.Overlays(_camera.Pose.Dim);
        _useGpuFreecam = App.Startup.Renderer != "cpu";
        FreecamInput = _useGpuFreecam ? Gpu : null;
        global::MineClaw.MemoryExplorer.LiveHive.FreecamStats.Shared.Renderer = _useGpuFreecam ? "gl" : "cpu";
        Tiles = new TileRenderer(_cache, () => _atlas.IsValueCreated ? _atlas.Value : null)
        {
            Tiles = () => _eyeTiles, Bots = () => _bots, Entities = () => _entities,
        };
        Composer = new LocalSheetComposer(_cache, () => _atlas.IsValueCreated ? _atlas.Value : null, () => _bots, () => _entities);
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
    /// <summary>Compose mode's local sheet renderer (the bot's ContactSheet over the synced world).</summary>
    public LocalSheetComposer Composer { get; }
    /// <summary>The GPU free camera (default; the CPU <see cref="Freecam"/> is the fallback when GL is unavailable).</summary>
    public Gpu.GpuFreecam Gpu { get; }
    private static global::MineClaw.MemoryExplorer.LiveHive.FreecamStats Stats => global::MineClaw.MemoryExplorer.LiveHive.FreecamStats.Shared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCpuFreecam))]
    private bool _useGpuFreecam = true;
    public bool UseCpuFreecam => !UseGpuFreecam;
    [ObservableProperty] private string _rendererNote = "";

    partial void OnUseGpuFreecamChanged(bool value)
    {
        FreecamInput = value ? Gpu : null;
        global::MineClaw.MemoryExplorer.LiveHive.FreecamStats.Shared.Renderer = value ? "gl" : "cpu";
        UpdateWants();
    }

    /// <summary>The GL view could not start: fall back to the CPU raycaster.</summary>
    public void GpuFailed(string why)
    {
        RendererNote = "CPU renderer (" + why + ")";
        UseGpuFreecam = false;
    }

    // Profiling / tests hooks (FreecamProfiler).
    internal int CachedColumns => _cache.Count;
    internal int EntityCount => _entityStore.Count;
    internal bool CameraPlaced => _camera.Placed;
    internal double LookSensitivity => _camera.Sensitivity;
    internal HiveWorldCache Cache => _cache;
    internal FlyCamera Camera => _camera;
    /// <summary>True once the active renderer has something to draw (the GL path: its first meshes are in).</summary>
    internal bool RendererReady => FreecamInput?.Ready ?? true;
    /// <summary>The free camera's input sink (the GL view registers itself; null = the CPU engine).</summary>
    internal IFreecamInput? FreecamInput { get; set; }
    internal void InjectLook(double dx, double dy) { _camera.Look(dx, dy); FreecamInput?.Poke(); }
    internal void InjectKeys(bool w, bool s, bool a, bool d, bool up, bool down, bool fast)
    {
        _camera.StampInput();
        if (FreecamInput is { } i) i.SetKeys(w, s, a, d, up, down, fast);
        else Freecam.SetKeys(w, s, a, d, up, down, fast);
    }

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
        _entityStore.Clear();
        _sync = new HiveSync(_api, _cache)
        {
            Store = _entityStore,
            Interests = () => _interests, MapViews = () => _mapViews,
            // The free camera's overlays; else the vision panel's compose mode (its build-plan footprints).
            OverlayDim = () => IsFreecamTab && OverlaysWanted ? _camera.Pose.Dim
                : Vision is { IsCompose: true } v && _bots.FirstOrDefault(b => b.Key == v.Body) is { } vb ? vb.Dim : null,
        };
        _sync.StateChanged += () => Dispatcher.UIThread.Post(SyncStateChanged);
        _sync.EntitiesChanged += e => Dispatcher.UIThread.Post(() => ApplyEntities(e));
        _sync.ViewPng += (bot, mode, png) =>
        {
            // Decoded here, on the sync worker: a large PNG decode on the UI thread stalled every tile and the camera.
            Avalonia.Media.Imaging.Bitmap bmp;
            try { using var ms = new MemoryStream(png); bmp = new Avalonia.Media.Imaging.Bitmap(ms); }
            catch (Exception) { return; } // a bad frame: keep the last one
            Dispatcher.UIThread.Post(() =>
            {
                var tile = AllTiles.FirstOrDefault(t => t.Key == bot && t.Mode.ServerMode == mode);
                if (tile != null) tile.SetMap(bmp);
                else bmp.Dispose();
            });
        };
        _badges = new ScriptBadgePoller(_api, rows => Dispatcher.UIThread.Post(() => ScriptBadgePoller.Apply(AllTiles, rows.Bodies)));
        _visionTiles = new VisionTilePoller(new HiveVisionSource(_api), () => _visionWanted, (key, info, bmp) => Dispatcher.UIThread.Post(() =>
        {
            if (AllTiles.FirstOrDefault(t => t.Key == key) is { IsVision: true } tile && info.Id > (tile.VisionInfo?.Id ?? 0)) tile.SetVision(info, bmp);
            else bmp?.Dispose();
            UpdateWants();
        }));
        ConnectLabel = "Disconnect";
        IsConnecting = true;
        if (_active) { _sync.Start(); _badges.Start(); _visionTiles.Start(); }
        else StateText = "connects when the tab is shown";
    }

    private void Disconnect()
    {
        CloseScript();
        CloseVision();
        _visionTiles?.Dispose();
        _visionTiles = null;
        _badges?.Dispose();
        _badges = null;
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
            interests.Add(new Interest(p.Dim, (int)Math.Floor(p.X), (int)Math.Floor(p.Z), (UseGpuFreecam ? Gpu.MaxDistance : Freecam.MaxDistance) + 24, 900));
        }
        foreach (var t in shown.Where(t => t.IsEye && t.Bot != null))
            interests.Add(new Interest(t.Bot!.Dim, (int)Math.Floor(t.Bot.X), (int)Math.Floor(t.Bot.Z), Tiles.MaxDistance + 16, 1500));
        // The vision panel's compose mode renders locally: keep its centre's columns synced.
        if (Vision?.ComposeInterest() is { } ci) interests.Add(ci);
        _visionWanted = shown.Where(t => t.IsVision).Select(t => (t.Key, t.VisionInfo?.Id ?? 0L)).ToArray();
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
    [ObservableProperty] private bool _showDrops = true;

    /// <summary>Dropped items near the camera, nearest first (the Drops panel; click one to fly to it).</summary>
    public System.Collections.ObjectModel.ObservableCollection<DropRow> Drops { get; } = [];
    [ObservableProperty] private string _dropsSummary = "";

    private bool OverlaysWanted => ShowClaims || ShowPlaces || ShowChests || ShowPlans || ShowDangers;

    public IReadOnlyList<string> BotChoices => AllTiles.Select(t => t.Key).ToList();

    partial void OnShowBotsChanged(bool value) => PushFlags();
    partial void OnShowClaimsChanged(bool value) => PushFlags();
    partial void OnShowPlacesChanged(bool value) => PushFlags();
    partial void OnShowChestsChanged(bool value) => PushFlags();
    partial void OnShowPlansChanged(bool value) => PushFlags();
    partial void OnShowDangersChanged(bool value) => PushFlags();
    partial void OnShowStalenessChanged(bool value) => PushFlags();
    partial void OnShowDropsChanged(bool value) => PushFlags();

    private void PushFlags()
    {
        Freecam.Flags = (ShowBots ? Overlay.Bots : 0) | (ShowClaims ? Overlay.Claims : 0) | (ShowPlaces ? Overlay.Places : 0)
            | (ShowChests ? Overlay.Chests : 0) | (ShowPlans ? Overlay.Plans : 0) | (ShowDangers ? Overlay.Dangers : 0)
            | (ShowStaleness ? Overlay.Staleness : 0) | (ShowDrops ? Overlay.Drops : 0);
        Gpu.Scene.Flags = Freecam.Flags;
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

    /// <summary>Flies the camera to a drop: a few blocks back and up, looking at it.</summary>
    [RelayCommand]
    private void FlyToDrop(DropRow? d)
    {
        if (d == null) return;
        var p = _camera.Pose;
        double dx = d.X - p.X, dz = d.Z - p.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1e-3) { dx = 0; dz = 1; len = 1; }
        double ex = d.X - dx / len * 3.5, ez = d.Z - dz / len * 3.5, ey = d.Y + 2.2;
        float yaw = (float)(Math.Atan2(-(d.X - ex), d.Z - ez) * 180 / Math.PI);
        float pitch = (float)(Math.Atan2(ey - d.Y - 0.2, 3.5) * 180 / Math.PI);
        Place(new CamPose(ex, ey, ez, yaw, pitch, d.Dim));
        JumpStatus = $"at {d.Text}";
    }

    private void RefreshDrops()
    {
        var p = _camera.Pose;
        _entityStore.Interpolated(p.Dim, _dropScratch);
        var rows = _dropScratch.Where(e => e.Kind == "item")
            .Select(e => (E: e, D: Math.Sqrt((e.X - p.X) * (e.X - p.X) + (e.Y - p.Y) * (e.Y - p.Y) + (e.Z - p.Z) * (e.Z - p.Z))))
            .OrderBy(t => t.D).Take(60).ToList();
        int total = _dropScratch.Count(e => e.Kind == "item");
        DropsSummary = total == 0 ? "no drops known in this dimension" : $"{total} drop{(total == 1 ? "" : "s")} known · nearest {rows[0].D:0} m";
        for (int i = 0; i < rows.Count; i++)
        {
            var (e, d) = rows[i];
            var text = e.Count > 1 ? $"{e.Name ?? "item"} x{e.Count}" : e.Name ?? "item";
            var row = new DropRow(e.Id, e.Dim, text, string.Create(CultureInfo.InvariantCulture, $"{d:0} m · {e.X:0}, {e.Y:0}, {e.Z:0}"), e.X, e.Y, e.Z);
            if (i < Drops.Count) { if (Drops[i] != row) Drops[i] = row; }
            else Drops.Add(row);
        }
        while (Drops.Count > rows.Count) Drops.RemoveAt(Drops.Count - 1);
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

    // ------------------------------------------------------------------ live script debugger

    /// <summary>The open debugger panel (a tile's "script" badge), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScriptDebug))]
    private ScriptDebugViewModel? _scriptDebug;
    public bool HasScriptDebug => ScriptDebug != null;

    /// <summary>Opens the debugger on that body's script (polls ≈ 4 Hz while open and the tab is shown).</summary>
    public void OpenScript(BotTileViewModel t)
    {
        if (_api is not { } api) return;
        ScriptDebug?.Dispose();
        ScriptDebug = new ScriptDebugViewModel(new HiveScriptDebugSource(api), t.Key, t.Title);
        if (_active) ScriptDebug.Start();
    }

    [RelayCommand]
    private void CloseScript()
    {
        ScriptDebug?.Dispose();
        ScriptDebug = null;
    }

    // ------------------------------------------------------------------ the AI's vision (what it saw; compose)

    /// <summary>The open vision panel (a tile's "AI vision" button), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVision))]
    private VisionPanelViewModel? _vision;
    public bool HasVision => Vision != null;

    /// <summary>Opens the vision panel on that body: its last sheets (polled while open) and the compose mode.</summary>
    public void OpenVision(BotTileViewModel t, bool compose = false)
    {
        if (_api is not { } api) return;
        Vision?.Dispose();
        var key = t.Key;
        Vision = new VisionPanelViewModel(new HiveVisionSource(api), key, t.Title,
            () => _bots.FirstOrDefault(b => b.Key == key || b.Name == key), Composer, () => _sync?.Overlays(t.Bot?.Dim ?? 0)?.Plans ?? []);
        Vision.PropertyChanged += (_, a) => { if (a.PropertyName is nameof(VisionPanelViewModel.ComposeMode) or nameof(VisionPanelViewModel.TargetText)) UpdateWants(); };
        if (compose) Vision.ComposeMode = true;
        if (_active) Vision.Start();
        UpdateWants();
    }

    [RelayCommand]
    private void CloseVision()
    {
        Vision?.Dispose();
        Vision = null;
    }

    // ------------------------------------------------------------------ lifecycle

    private int _lastIndexX = int.MinValue, _lastIndexZ = int.MinValue;

    private void RefreshStats()
    {
        MaybePollTree();
        if (_api is { } api)
        {
            double ms = api.LatencyMs;
            LatencyText = ms < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms");
        }
        var p = _camera.Pose;
        FreecamStats = UseGpuFreecam
            ? string.Create(CultureInfo.InvariantCulture,
                $"GPU · {Gpu.Fps:0} fps · {Gpu.Scene.Width}x{Gpu.Scene.Height} · {Stats.SectionsLive} sections · {Stats.PendingMeshes} queued · {Gpu.GlInfo}")
            : string.Create(CultureInfo.InvariantCulture,
                $"CPU · {Freecam.Fps:0} fps · raster {Freecam.RasterMs:0.0} ms · {Freecam.LastW}x{Freecam.LastH} on {Freecam.Threads} threads");
        CacheStats = string.Create(CultureInfo.InvariantCulture,
            $"{_cache.Count} columns cached ({_cache.CountIn(p.Dim)} here) · {(_api?.BytesIn ?? 0) / 1024.0 / 1024.0:0.0} MB received");
        OverlayNotes = _sync?.Overlays(p.Dim) is { } ov
            ? $"{ov.Claims.Count} claims · {ov.Places.Count} places · {ov.Points.Count} points · {ov.Plans.Sum(x => x.Blocks.Count)} plan blocks · {ov.Dangers.Count} dangers"
              + (ov.Notes.Count > 0 ? " · " + string.Join(", ", ov.Notes) : "")
            : "";
        if (IsFreecamTab) RefreshDrops();
        // Per-tile "stale Ns" / "reconnecting…" badges, and the same for the free camera's HUD.
        var utcNow = DateTime.UtcNow;
        bool linkUp = _sync is { State: LinkState.Connected };
        foreach (var t in AllTiles) t.RefreshStale(linkUp, utcNow);
        ScriptDebug?.Tick(utcNow);
        Controller?.Tick(utcNow);
        double worldAge = _sync is { WorldOkAt: var ok } && ok > DateTime.MinValue ? (utcNow - ok).TotalSeconds : 0;
        Gpu.LinkNote = _sync == null ? null : !linkUp ? "reconnecting… (" + StateText + ")"
            : worldAge > 5 ? $"world stale {worldAge:0} s" + (_sync.LastError is { } le ? " · " + le : "") : null;
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
        _badges?.Start();
        _visionTiles?.Start();
        ScriptDebug?.Start();
        Controller?.Start();
        Vision?.Start();
        UpdateWants();
    }

    public void Deactivate()
    {
        _active = false;
        _stats.Stop();
        _sync?.Stop();
        _badges?.Stop();
        _visionTiles?.Stop();
        Vision?.Stop();
        ScriptDebug?.Stop(); // a hidden panel stops polling (and so the server's sampling lease runs out)
        Controller?.Stop();
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
        Gpu.Dispose();
        Tiles.Dispose();
        foreach (var t in AllTiles) t.Dispose();
        if (_tunnel is { HasExited: false })
            try { _tunnel.Kill(entireProcessTree: true); } catch (Exception) { }
    }
}

/// <summary>A row of the Drops panel.</summary>
public sealed record DropRow(int Id, int Dim, string Text, string Where, double X, double Y, double Z);
