using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>One sheet in the history strip.</summary>
public sealed partial class VisionSheetRow : ObservableObject
{
    public VisionSheetRow(VisionSheetDto s)
    {
        Sheet = s;
        Label = s.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Detail = $"{s.Kind} · {(s.Mode == "perception" ? "→ perception" : "→ brain")} · {s.Tokens} tok";
    }

    public VisionSheetDto Sheet { get; }
    public string Label { get; }
    public string Detail { get; }
    [ObservableProperty] private Bitmap? _thumb;
}

/// <summary>A target the compose mode can centre on.</summary>
public sealed record ComposeTarget(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The vision panel of one body (a tile's "AI vision" button). Two modes:
/// <list type="bullet">
/// <item><b>What the AI saw</b>: the exact sheets the body's models received (the bytes from the server's ring, never
/// re-rendered), newest first, with when, the see request (views, focus, question, the call), the token/size cost and the
/// perception tier's answer; a history strip, zoom/pan and Save PNG. Polls the list every <see cref="Interval"/> while open.</item>
/// <item><b>Compose</b>: pick panels and adjust every non-eye camera (iso yaw/pitch/zoom, orbit and high angle/distance,
/// top/side zoom and pan, the side axis, the shoulder distance), the centre (the bot, a block, a build plan's footprint) and the
/// radius; a live preview rendered locally by the same ContactSheet (<see cref="IComposeRenderer"/>), debounced
/// (<see cref="Debounce"/>; changes during a render coalesce into one more), "Render exact" from the server (roles, marks), and
/// "Copy as see{...}": the tool call that renders that sheet.</item>
/// </list>
/// Read-only: nothing here moves a bot or asks a model.
/// </summary>
public sealed partial class VisionPanelViewModel : ObservableObject, IDisposable
{
    private readonly IVisionSource _source;
    private readonly IComposeRenderer? _local;
    private readonly Func<BotDto?> _bot;
    private readonly Func<IReadOnlyList<PlanDto>> _plans;
    private readonly Action<Action> _post;
    private readonly Func<byte[], Bitmap?> _decode;
    private readonly Dictionary<long, byte[]> _pngs = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _composeDelay;
    private readonly object _composeLock = new();
    private bool _rendering, _again, _suspend;
    private Task _composeTask = Task.CompletedTask;
    private long _lastId;
    private readonly HashSet<long> _pending = [];

    public VisionPanelViewModel(IVisionSource source, string body, string title, Func<BotDto?> bot, IComposeRenderer? local = null,
        Func<IReadOnlyList<PlanDto>>? plans = null, Action<Action>? post = null, Func<byte[], Bitmap?>? decode = null)
    {
        _source = source;
        _local = local;
        _bot = bot;
        _plans = plans ?? (() => []);
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _decode = decode ?? Decode;
        Body = body;
        Title = title;
        _target = Targets[0];
    }

    public string Body { get; }
    public string Title { get; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(90);
    /// <summary>Compose renders done (tests: the debounce coalesces bursts).</summary>
    public int ComposeRenders { get; private set; }

    private static Bitmap? Decode(byte[] png)
    {
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ mode

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaw), nameof(IsCompose))]
    private bool _composeMode;
    public bool IsSaw => !ComposeMode;
    public bool IsCompose => ComposeMode;

    partial void OnComposeModeChanged(bool value)
    {
        if (value) RequestCompose();
        else if (Selected != null) ShowSheet(Selected);
    }

    [RelayCommand] private void ShowSaw() => ComposeMode = false;
    [RelayCommand] private void ShowCompose() => ComposeMode = true;

    // ------------------------------------------------------------------ the picture (both modes)

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private int _imageWidth;
    [ObservableProperty] private int _imageHeight;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private string _status = "";
    /// <summary>The PNG on screen (Save PNG writes exactly these bytes).</summary>
    public byte[]? CurrentPng { get; private set; }
    public string SuggestedFileName { get; private set; } = "sheet.png";
    public double ShownWidth => ImageWidth * Zoom;
    public double ShownHeight => ImageHeight * Zoom;
    partial void OnZoomChanged(double value) { OnPropertyChanged(nameof(ShownWidth)); OnPropertyChanged(nameof(ShownHeight)); }
    partial void OnImageWidthChanged(int value) => OnPropertyChanged(nameof(ShownWidth));
    partial void OnImageHeightChanged(int value) => OnPropertyChanged(nameof(ShownHeight));

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(6, Math.Round(Zoom * 1.25, 2));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.25, Math.Round(Zoom / 1.25, 2));
    [RelayCommand] private void ZoomReset() => Zoom = 1;

    private void SetPicture(byte[] png, int w, int h, string file)
    {
        var bmp = _decode(png);
        var old = Image;
        Image = bmp;
        if (!ReferenceEquals(old, bmp)) old?.Dispose();
        CurrentPng = png;
        ImageWidth = w;
        ImageHeight = h;
        SuggestedFileName = file;
    }

    // ------------------------------------------------------------------ what the AI saw

    public ObservableCollection<VisionSheetRow> History { get; } = [];
    [ObservableProperty] private VisionSheetRow? _selected;
    [ObservableProperty] private string _sheetTitle = "";
    [ObservableProperty] private string _requestText = "";
    [ObservableProperty] private string _costText = "";
    [ObservableProperty] private string _answerText = "";
    [ObservableProperty] private string _legendText = "";
    [ObservableProperty] private string _historyNote = "";
    [ObservableProperty] private bool _hasAnswer;
    [ObservableProperty] private bool _following = true;

    partial void OnSelectedChanged(VisionSheetRow? value)
    {
        if (value == null) return;
        Following = History.Count > 0 && ReferenceEquals(value, History[0]);
        ShowSheet(value);
    }

    public void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _post(() => HistoryNote = ex.Message); }
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One list poll: new sheets go to the front of the strip (the newest is shown while following).</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        // From the oldest sheet still waiting for its perception answer (it arrives after the sheet), else only newer ones.
        long since = _lastId;
        lock (_pending) if (_pending.Count > 0) since = Math.Min(since, _pending.Min() - 1);
        var list = await _source.ListAsync(Body, since, ct).ConfigureAwait(false);
        var fresh = list.Sheets.Where(s => s.Id > _lastId).OrderBy(s => s.Id).ToList();
        if (fresh.Count > 0) _lastId = fresh[^1].Id;
        var pngs = new List<(long Id, byte[] Png)>();
        foreach (var s in fresh.TakeLast(6))
        {
            try { pngs.Add((s.Id, await _source.PngAsync(Body, s.Id, ct).ConfigureAwait(false))); }
            catch (HiveApiException) { }
        }
        var done = new TaskCompletionSource();
        _post(() =>
        {
            try { Apply(list, fresh, pngs); }
            finally { done.TrySetResult(); }
        });
        await done.Task.ConfigureAwait(false);
    }

    private void Apply(VisionListDto list, List<VisionSheetDto> fresh, List<(long Id, byte[] Png)> pngs)
    {
        int cap = Math.Max(1, list.Capacity == 0 ? 20 : list.Capacity);
        foreach (var (id, png) in pngs) _pngs[id] = png;
        UpdatePending(list);
        foreach (var s in fresh) History.Insert(0, NewRow(s));
        RefreshAnswers(list);
        TrimHistory(cap);
        HistoryNote = list.Note ?? (History.Count == 0 ? "no sheet yet: the AI hasn't looked since the bot started" : $"{History.Count} of the last {cap} sheets");
        if (History.Count > 0 && (Following || Selected == null)) Selected = History[0];
    }

    /// <summary>Perception sheets still waiting for their answer (asked for again on the next poll).</summary>
    private void UpdatePending(VisionListDto list)
    {
        lock (_pending)
        {
            foreach (var x in list.Sheets) if (x.Mode == "perception" && x.Answer == null && x.Note == null) _pending.Add(x.Id); else _pending.Remove(x.Id);
            // Gone from the server's ring, or waited long enough: stop asking.
            _pending.RemoveWhere(id => list.Sheets.All(x => x.Id != id) && id <= _lastId || History.FirstOrDefault(r => r.Sheet.Id == id) is { } r && DateTimeOffset.UtcNow - r.Sheet.At > TimeSpan.FromMinutes(2));
        }
    }

    private VisionSheetRow NewRow(VisionSheetDto s)
    {
        var row = new VisionSheetRow(s);
        if (_pngs.TryGetValue(s.Id, out var png)) row.Thumb = _decode(png);
        return row;
    }

    /// <summary>Answers arrive after the sheet: refresh rows whose perception answer is new (keeping the selection on them).</summary>
    private void RefreshAnswers(VisionListDto list)
    {
        foreach (var s in list.Sheets)
            if (History.FirstOrDefault(r => r.Sheet.Id == s.Id) is { } r && r.Sheet.Answer != s.Answer)
            {
                int i = History.IndexOf(r);
                var nr = new VisionSheetRow(s) { Thumb = r.Thumb };
                History[i] = nr;
                if (ReferenceEquals(Selected, r)) Selected = nr;
            }
    }

    private void TrimHistory(int cap)
    {
        while (History.Count > cap)
        {
            var old = History[^1];
            History.RemoveAt(History.Count - 1);
            _pngs.Remove(old.Sheet.Id);
            old.Thumb?.Dispose();
        }
    }

    /// <summary>"views [...] · focus x,y,z rN · Q: ..." plus the see call on its own line.</summary>
    private static string RequestLine(VisionSheetDto s)
    {
        string focus = s.Focus is { Length: 3 } f ? $"focus {f[0]},{f[1]},{f[2]} r{s.Radius}" : "focus: hive";
        return $"views [{string.Join(", ", s.Views ?? [])}] · {focus}" + (s.Question != null ? $" · Q: {s.Question}" : "") + (s.Call != null ? $"\nsee {s.Call}" : "");
    }

    /// <summary>The picture's size and token cost, the perception model's tokens and the sheet's note.</summary>
    private static string CostLine(VisionSheetDto s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s.Width}x{s.Height}px · ~{s.Tokens} image tokens · {s.Bytes / 1024.0:0.0} KB · sha256 {s.Sha256[..Math.Min(12, s.Sha256.Length)]}")
        + (s.Model != null ? $" · {s.Model} in {s.ModelInputTokens} / out {s.ModelOutputTokens} tok" : "") + (s.Note != null ? " · " + s.Note : "");

    /// <summary>Shows one captured sheet (its bytes, fetched once and kept while it is in the strip).</summary>
    public void ShowSheet(VisionSheetRow row)
    {
        var s = row.Sheet;
        SheetTitle = $"{s.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {s.Kind} for the {(s.Mode == "perception" ? "perception tier" : "brain")} · {s.Source}";
        RequestText = RequestLine(s);
        CostText = CostLine(s);
        AnswerText = s.Answer ?? (s.Mode == "perception" ? "(waiting for the perception answer…)" : "(direct mode: the brain read the picture itself)");
        HasAnswer = s.Answer != null;
        LegendText = s.Legend;
        if (IsCompose) return;
        if (_pngs.TryGetValue(s.Id, out var png)) SetPicture(png, s.Width, s.Height, $"{Body}-sheet-{s.Id}.png");
        else _ = FetchAsync(row);
    }

    private async Task FetchAsync(VisionSheetRow row)
    {
        try
        {
            var png = await _source.PngAsync(Body, row.Sheet.Id, CancellationToken.None).ConfigureAwait(false);
            _post(() =>
            {
                _pngs[row.Sheet.Id] = png;
                row.Thumb ??= _decode(png);
                if (ReferenceEquals(Selected, row) && IsSaw) SetPicture(png, row.Sheet.Width, row.Sheet.Height, $"{Body}-sheet-{row.Sheet.Id}.png");
            });
        }
        catch (HiveApiException ex) { _post(() => Status = ex.Message); }
    }

    // ------------------------------------------------------------------ compose

    public IReadOnlyList<ComposeTarget> Targets { get; } = [new("bot", "the bot"), new("block", "a block (x,y,z)"), new("plan", "a build plan footprint")];
    public IReadOnlyList<string> PlanNames => _plans().Select(p => $"#{p.Id} {p.Name}").ToList();
    public IReadOnlyList<string> SideAxes { get; } = ["auto", "N", "E"];

    [ObservableProperty] private ComposeTarget _target;
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string? _plan;
    [ObservableProperty] private int _radius = 6;
    [ObservableProperty] private bool _aids = true;
    [ObservableProperty] private bool _serverRender;

    [ObservableProperty] private bool _useEye = true;
    [ObservableProperty] private bool _useShoulder;
    [ObservableProperty] private bool _useIso = true;
    [ObservableProperty] private bool _useOrbit = true;
    [ObservableProperty] private bool _useHigh;
    [ObservableProperty] private bool _useTop = true;
    [ObservableProperty] private bool _useSide;
    [ObservableProperty] private bool _useLayers;

    // Every non-eye camera, at its default (the sliders start where the AI's default sheet is).
    [ObservableProperty] private double _isoYaw = 225;
    [ObservableProperty] private double _isoPitch = 35;
    [ObservableProperty] private double _isoZoom = 1;
    [ObservableProperty] private double _orbitYaw = 315;
    [ObservableProperty] private double _orbitPitch = 40;
    [ObservableProperty] private double _orbitDist;
    [ObservableProperty] private double _highYaw = 180;
    [ObservableProperty] private double _highPitch = 65;
    [ObservableProperty] private double _topZoom = 1;
    [ObservableProperty] private double _topDx;
    [ObservableProperty] private double _topDz;
    [ObservableProperty] private double _sideZoom = 1;
    [ObservableProperty] private double _sideDx;
    [ObservableProperty] private double _sideDy;
    [ObservableProperty] private string _sideAxis = "auto";
    [ObservableProperty] private double _shoulderDist = 3.4;

    [ObservableProperty] private string _seeCallText = "";
    [ObservableProperty] private string _composeStats = "";
    [ObservableProperty] private string _composeLegend = "";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } n && ComposeProps.Contains(n)) RequestCompose();
    }

    private static readonly HashSet<string> ComposeProps =
    [
        nameof(Target), nameof(TargetText), nameof(Plan), nameof(Radius), nameof(Aids), nameof(ServerRender),
        nameof(UseEye), nameof(UseShoulder), nameof(UseIso), nameof(UseOrbit), nameof(UseHigh), nameof(UseTop), nameof(UseSide), nameof(UseLayers),
        nameof(IsoYaw), nameof(IsoPitch), nameof(IsoZoom), nameof(OrbitYaw), nameof(OrbitPitch), nameof(OrbitDist), nameof(HighYaw), nameof(HighPitch),
        nameof(TopZoom), nameof(TopDx), nameof(TopDz), nameof(SideZoom), nameof(SideDx), nameof(SideDy), nameof(SideAxis), nameof(ShoulderDist),
    ];

    /// <summary>Back to the AI's default sheet cameras.</summary>
    [RelayCommand]
    private void ResetCameras()
    {
        _suspend = true;
        try
        {
            (IsoYaw, IsoPitch, IsoZoom, OrbitYaw, OrbitPitch, OrbitDist, HighYaw, HighPitch) = (225, 35, 1, 315, 40, 0, 180, 65);
            (TopZoom, TopDx, TopDz, SideZoom, SideDx, SideDy, SideAxis, ShoulderDist) = (1, 0, 0, 1, 0, 0, "auto", 3.4);
        }
        finally { _suspend = false; }
        RequestCompose();
    }

    private static double? Off(double v, double def) => Math.Abs(v - def) < 1e-6 ? null : Math.Round(v, 2);

    /// <summary>The panels with their camera adjustments (only what differs from the default goes into the call).</summary>
    public IReadOnlyList<SeeView> Views()
    {
        var list = new List<SeeView>();
        if (UseEye) list.Add(new SeeView("eye"));
        if (UseShoulder) list.Add(new SeeView("shoulder", new CamParams(Dist: Off(ShoulderDist, 3.4))));
        if (UseIso) list.Add(new SeeView("iso", new CamParams(Yaw: Off(IsoYaw, 225), Pitch: Off(IsoPitch, 35), Zoom: Off(IsoZoom, 1))));
        if (UseOrbit) list.Add(new SeeView("orbit", OrbitCam()));
        if (UseHigh) list.Add(new SeeView("high", new CamParams(Yaw: Off(HighYaw, 180), Pitch: Off(HighPitch, 65))));
        if (UseTop) list.Add(new SeeView("top", new CamParams(Zoom: Off(TopZoom, 1), Dx: Off(TopDx, 0), Dz: Off(TopDz, 0))));
        if (UseSide) list.Add(new SeeView("side", SideCam()));
        if (UseLayers) list.Add(new SeeView("layers"));
        return list.Take(ContactSheet.MaxPanels).ToList();
    }

    private CamParams OrbitCam() => new(Yaw: Off(OrbitYaw, 315), Pitch: Off(OrbitPitch, 40), Dist: OrbitDist > 0 ? Math.Round(OrbitDist, 1) : null);

    private CamParams SideCam() => new(Zoom: Off(SideZoom, 1), Dx: Off(SideDx, 0), Dy: Off(SideDy, 0), Axis: SideAxis is "N" or "E" ? SideAxis : null);

    /// <summary>The spec of the current compose state; null = no bot data yet or a bad target.</summary>
    public ComposeSpec? Spec(out string? problem)
    {
        problem = null;
        if (_bot() is not { } b) { problem = "waiting for the bot's position…"; return null; }
        var feet = ((int)Math.Floor(b.X), (int)Math.Floor(b.Y), (int)Math.Floor(b.Z));
        (int X, int Y, int Z) focus = feet;
        int radius = Math.Clamp(Radius, 3, 12);
        switch (Target.Key)
        {
            case "block":
                var parts = TargetText.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                {
                    problem = "type a block as x,y,z";
                    return null;
                }
                focus = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
                break;
            case "plan":
                var plan = _plans().FirstOrDefault(p => $"#{p.Id} {p.Name}" == Plan);
                if (plan is not { Blocks.Count: > 0 }) { problem = "pick a build plan (the hive's overlays list them)"; return null; }
                int x0 = plan.Blocks.Min(c => c[0]), x1 = plan.Blocks.Max(c => c[0]), z0 = plan.Blocks.Min(c => c[2]), z1 = plan.Blocks.Max(c => c[2]);
                focus = ((x0 + x1) / 2, plan.Blocks.Min(c => c[1]), (z0 + z1) / 2);
                radius = Math.Clamp((Math.Max(x1 - x0, z1 - z0) + 1) / 2 + 2, 3, 12);
                break;
        }
        var views = Views();
        if (views.Count == 0) { problem = "pick at least one panel"; return null; }
        return new ComposeSpec(b, focus, radius, views, Aids);
    }

    /// <summary>Schedules a debounced render: a burst of slider changes renders once; changes during a render render once more after it.</summary>
    public void RequestCompose()
    {
        if (_suspend || !IsCompose) return;
        var spec = Spec(out var problem);
        SeeCallText = spec?.SeeCall() ?? "";
        if (spec == null) { ComposeStats = problem ?? ""; return; }
        CancellationTokenSource cts;
        lock (_composeLock)
        {
            _composeDelay?.Cancel();
            cts = _composeDelay = new CancellationTokenSource();
        }
        _composeTask = Task.Run(async () =>
        {
            try { await Task.Delay(Debounce, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await RenderLoopAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Completes when no compose render is pending (tests).</summary>
    public async Task WhenComposedAsync()
    {
        for (int i = 0; i < 200; i++)
        {
            var t = _composeTask;
            await t.ConfigureAwait(false);
            await Task.Delay(Debounce + TimeSpan.FromMilliseconds(30)).ConfigureAwait(false);
            lock (_composeLock) if (!_rendering && ReferenceEquals(t, _composeTask)) return;
        }
    }

    private async Task RenderLoopAsync()
    {
        lock (_composeLock)
        {
            if (_rendering) { _again = true; return; }
            _rendering = true;
        }
        try
        {
            while (true)
            {
                var spec = await OnUi(() => Spec(out _)).ConfigureAwait(false);
                if (spec != null) await RenderOnceAsync(spec).ConfigureAwait(false);
                lock (_composeLock)
                {
                    if (!_again) { _rendering = false; return; }
                    _again = false;
                }
            }
        }
        catch (Exception)
        {
            lock (_composeLock) _rendering = false;
            throw;
        }
    }

    private Task<T> OnUi<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        _post(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private async Task RenderOnceAsync(ComposeSpec spec)
    {
        SheetPicture? pic = null;
        string? error = null;
        try
        {
            if (!ServerRender && _local != null) pic = _local.Render(spec);
            else pic = await _source.RenderAsync(Body, spec.SeeJson(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { error = ex.Message; }
        var bmp = pic != null ? _decode(pic.Png) : null;
        _post(() =>
        {
            ComposeRenders++;
            if (pic == null) { ComposeStats = "render failed: " + error; return; }
            if (!IsCompose) { bmp?.Dispose(); return; }
            var old = Image;
            Image = bmp;
            old?.Dispose();
            CurrentPng = pic.Png;
            ImageWidth = pic.Width;
            ImageHeight = pic.Height;
            SuggestedFileName = $"{Body}-compose.png";
            ComposeLegend = pic.Legend;
            ComposeStats = string.Create(CultureInfo.InvariantCulture,
                $"{pic.Where} · {pic.Width}x{pic.Height}px ~{(pic.Width * pic.Height + 749) / 750} tok · {pic.Ms:0} ms ({1000 / Math.Max(1, pic.Ms):0} fps)")
                + (pic.Where == "local" ? " · no role layer or memory labels here: Render exact for those" : "");
        });
    }

    /// <summary>"Render exact": the server renders the same call (roles, marks, world time: what the AI would get).</summary>
    [RelayCommand]
    private void RenderExact()
    {
        if (Spec(out var problem) is not { } spec) { ComposeStats = problem ?? ""; return; }
        ComposeStats = "asking the server…";
        _ = Task.Run(() => ExactAsync(spec));
    }

    private async Task ExactAsync(ComposeSpec spec)
    {
        try
        {
            var pic = await _source.RenderAsync(Body, spec.SeeJson(), CancellationToken.None).ConfigureAwait(false);
            var bmp = _decode(pic.Png);
            _post(() =>
            {
                var old = Image;
                Image = bmp;
                old?.Dispose();
                CurrentPng = pic.Png;
                (ImageWidth, ImageHeight) = (pic.Width, pic.Height);
                SuggestedFileName = $"{Body}-exact.png";
                ComposeStats = string.Create(CultureInfo.InvariantCulture, $"server (exact) · {pic.Width}x{pic.Height}px · {pic.Ms:0} ms");
            });
        }
        catch (Exception ex) { _post(() => ComposeStats = "server render failed: " + ex.Message); }
    }

    /// <summary>Starts compose from a captured sheet: its focus, radius and panels (with their angles).</summary>
    [RelayCommand]
    private void ComposeFromSheet()
    {
        if (Selected?.Sheet is not { } s) return;
        _suspend = true;
        try { LoadSheetSpec(s); }
        finally { _suspend = false; }
        ComposeMode = true;
        RequestCompose();
    }

    /// <summary>The sheet's focus, radius, panels and each panel's camera (what it leaves out goes back to the default).</summary>
    private void LoadSheetSpec(VisionSheetDto s)
    {
        if (s.Focus is { Length: 3 } f) { Target = Targets[1]; TargetText = $"{f[0]},{f[1]},{f[2]}"; }
        Radius = Math.Clamp(s.Radius, 3, 12);
        var views = (s.Views ?? []).Select(v => v.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 0).ToList();
        bool Has(string k) => views.Any(w => w[0].Equals(k, StringComparison.OrdinalIgnoreCase));
        (UseEye, UseShoulder, UseIso, UseOrbit, UseHigh, UseTop, UseSide, UseLayers) =
            (Has("eye"), Has("shoulder"), Has("iso"), Has("orbit"), Has("high"), Has("top"), Has("side"), Has("layers"));
        foreach (var w in views)
        {
            var p = CamParams.Extract(w.Skip(1).ToList());
            if (CameraSetters.TryGetValue(w[0].ToLowerInvariant(), out var set)) set(this, p);
        }
    }

    /// <summary>Per panel kind: sets its camera sliders from a view's parameters (defaults where it has none).</summary>
    private static readonly Dictionary<string, Action<VisionPanelViewModel, CamParams>> CameraSetters = new(StringComparer.Ordinal)
    {
        ["iso"] = (vm, p) => (vm.IsoYaw, vm.IsoPitch, vm.IsoZoom) = (p.Yaw ?? 225, p.Pitch ?? 35, p.Zoom ?? 1),
        ["orbit"] = (vm, p) => (vm.OrbitYaw, vm.OrbitPitch, vm.OrbitDist) = (p.Yaw ?? 315, p.Pitch ?? 40, p.Dist ?? 0),
        ["high"] = (vm, p) => (vm.HighYaw, vm.HighPitch) = (p.Yaw ?? 180, p.Pitch ?? 65),
        ["top"] = (vm, p) => (vm.TopZoom, vm.TopDx, vm.TopDz) = (p.Zoom ?? 1, p.Dx ?? 0, p.Dz ?? 0),
        ["side"] = (vm, p) => (vm.SideZoom, vm.SideDx, vm.SideDy, vm.SideAxis) = (p.Zoom ?? 1, p.Dx ?? 0, p.Dy ?? 0, p.Axis ?? "auto"),
        ["shoulder"] = (vm, p) => vm.ShoulderDist = p.Dist ?? 3.4,
    };

    /// <summary>"Copy as see{...}": compose → the call of the current spec; a captured sheet → its tool input (auto-vision's
    /// looks have none: rebuilt from the views, focus, radius and question).</summary>
    public string CopyText()
    {
        if (IsCompose) return SeeCallText;
        if (Selected?.Sheet is not { } s) return "";
        if (s.Call is { Length: > 0 } call) return "see " + call;
        var views = (s.Views ?? []).Select(v =>
        {
            var w = v.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var kind = w.Count > 0 ? w[0] : v;
            var p = CamParams.Extract(w);
            return new SeeView(kind, p, w.Count > 1 ? string.Join(' ', w.Skip(1)) : null);
        });
        return SeeCall.Format(views, s.Focus is { Length: 3 } f ? SeeCall.Coords(f[0], f[1], f[2]) : "hive", s.Radius, true, s.Question);
    }

    /// <summary>The centre the compose view needs loaded (the Live Hive keeps those columns synced while the panel is open).</summary>
    public Interest? ComposeInterest()
    {
        if (!IsCompose || Spec(out _) is not { } s) return null;
        return new Interest(s.Body.Dim, s.Focus.X, s.Focus.Z, 64, 1500);
    }

    public void Dispose()
    {
        Stop();
        _composeDelay?.Cancel();
        Image?.Dispose();
        foreach (var r in History) r.Thumb?.Dispose();
    }
}
