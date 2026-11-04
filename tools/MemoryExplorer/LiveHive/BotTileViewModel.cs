using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>A view a tile can show: "eye" is rendered locally from the cached world; "vision" is the last sheet the body's AI
/// received (the exact bytes); the rest come from the server as PNGs.</summary>
public sealed record TileMode(string Key, string Label, string? ServerMode)
{
    public static readonly IReadOnlyList<TileMode> All =
    [
        new("eye", "Eye", null),
        new("iso", "Iso (3D)", "iso"),
        new("shoulder", "Shoulder", "shoulder"),
        new("orbit", "Orbit", "orbit"),
        new("top", "Top + roles", "top"),
        new("side", "Side", "side"),
        new("vision", "AI's last sheet", null),
    ];

    public override string ToString() => Label;
}

/// <summary>One online body's feed tile: who, what it is doing, vitals, held item, claims, and the picture.</summary>
public sealed partial class BotTileViewModel : ObservableObject, IDisposable
{
    public BotTileViewModel(BotDto b, HiveWorldCache cache, TileMode mode)
    {
        Name = b.Name;
        Key = b.Key;
        Eye = new EyeTile(Key, cache);
        _mode = mode;
        Update(b);
    }

    public string Name { get; }
    /// <summary>"main" / "67" (what /view and the players use).</summary>
    public string Key { get; }
    public EyeTile Eye { get; }
    public IReadOnlyList<TileMode> Modes => TileMode.All;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _job = "";
    [ObservableProperty] private int _hp;
    [ObservableProperty] private int _food;
    [ObservableProperty] private string _vitals = "";
    [ObservableProperty] private string? _held;
    [ObservableProperty] private string _claims = "";
    [ObservableProperty] private bool _hasClaims;
    [ObservableProperty] private bool _lowHp;
    [ObservableProperty] private string _where = "";
    [ObservableProperty] private string? _poseNote;
    [ObservableProperty] private Bitmap? _mapImage;
    [ObservableProperty] private bool _mapLoading = true;
    [ObservableProperty] private bool _isFocused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEye), nameof(IsMap), nameof(IsVision))]
    private TileMode _mode;

    public bool IsEye => Mode.Key == "eye";
    public bool IsMap => Mode.ServerMode != null;
    /// <summary>The tile shows what the AI saw (the body's last vision sheet) instead of a live feed.</summary>
    public bool IsVision => Mode.Key == "vision";

    /// <summary>The last data (UI thread).</summary>
    public BotDto? Bot { get; private set; }

    partial void OnModeChanged(TileMode value)
    {
        MapImage = null;
        MapLoading = true;
        MapAt = DateTime.UtcNow;
    }

    public void Update(BotDto b)
    {
        Bot = b;
        Title = b.Main ? $"{b.Name} · main" : $"{b.Name} · {b.Short}";
        Subtitle = SubtitleOf(b);
        Job = string.IsNullOrWhiteSpace(b.Job) ? "idle" : b.Job!;
        Hp = b.Hp;
        Food = b.Food;
        LowHp = b.Hp is > 0 and <= 6;
        Vitals = $"♥ {b.Hp}  ◆ {b.Food}";
        Held = string.IsNullOrEmpty(b.Held) ? null : "holding " + b.Held;
        var claims = b.Claims ?? [];
        HasClaims = claims.Count > 0;
        Claims = ClaimsText(claims);
        Where = string.Create(CultureInfo.InvariantCulture, $"{(b.Dim == -1 ? "nether " : "")}{b.X:0},{b.Y:0},{b.Z:0}");
        PoseNote = PoseNoteOf(b);
    }

    /// <summary>The brain, else what kind of body it is.</summary>
    private static string SubtitleOf(BotDto b) => string.IsNullOrEmpty(b.Brain) ? (b.Live ? "in-process body" : "clone") : b.Brain!;

    /// <summary>"claims a, b, c +2" (the first three).</summary>
    private static string ClaimsText(List<string> claims) =>
        claims.Count == 0 ? "" : "claims " + string.Join(", ", claims.Take(3)) + (claims.Count > 3 ? $" +{claims.Count - 3}" : "");

    private static string? PoseNoteOf(BotDto b) => b.Yaw == null ? "no pose yet (eye faces south)" : b.PoseAge is > 3 ? $"pose {b.PoseAge:0} s old" : null;

    /// <summary>A new server picture (decoded off the UI thread).</summary>
    public void SetMap(Bitmap bmp)
    {
        var old = MapImage;
        MapImage = bmp;
        old?.Dispose();
        MapLoading = false;
        MapAt = DateTime.UtcNow;
    }

    /// <summary>When the server picture last changed (UI thread).</summary>
    public DateTime MapAt { get; private set; } = DateTime.UtcNow;

    /// <summary>"stale 7 s" / "reconnecting…" over the picture; empty while it is fresh.</summary>
    [ObservableProperty] private string _staleText = "";
    public bool IsStale => StaleText.Length > 0;
    partial void OnStaleTextChanged(string value) => OnPropertyChanged(nameof(IsStale));

    /// <summary>Recomputes the badge (UI thread, twice a second): link down → reconnecting; a picture older than a few seconds → stale.</summary>
    public void RefreshStale(bool linkUp, DateTime now)
    {
        if (IsVision)
        {
            // A sheet is history, not a feed: say how old it is, never "stale".
            StaleText = !linkUp ? "reconnecting…" : "";
            VisionAge = VisionInfo is { } vi ? Ago(DateTimeOffset.UtcNow - vi.At) : "";
            return;
        }
        double age = IsEye ? (Eye.LastFrameAt == default ? 0 : (now - Eye.LastFrameAt).TotalSeconds) : (now - MapAt).TotalSeconds;
        StaleText = !linkUp ? (age > 2 ? $"reconnecting… · {age:0} s old" : "reconnecting…")
            : age > (IsEye ? 3 : 5) ? $"stale {age:0} s" : "";
    }

    // ------------------------------------------------------------------ the "script" badge (ScriptBadgePoller)

    /// <summary>The script this body runs (null = none): the badge that opens the live script debugger.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScript))]
    private string? _scriptName;
    [ObservableProperty] private string _scriptBadge = "";
    [ObservableProperty] private bool _scriptPaused;
    public bool HasScript => ScriptName != null;

    public void SetScript(string? name, bool paused, int line)
    {
        ScriptName = name;
        ScriptPaused = paused;
        ScriptBadge = name == null ? "" : $"script {name}" + (paused ? " · paused" : line > 0 ? $" · L{line}" : "");
    }

    // ------------------------------------------------------------------ "AI's last sheet" (VisionTilePoller)

    [ObservableProperty] private Bitmap? _visionImage;
    [ObservableProperty] private string _visionCaption = "";
    [ObservableProperty] private string _visionAge = "";
    [ObservableProperty] private bool _visionEmpty = true;
    /// <summary>The sheet on the tile (null = none yet).</summary>
    public VisionSheetDto? VisionInfo { get; private set; }

    /// <summary>The body's newest sheet (decoded off the UI thread; UI thread here).</summary>
    public void SetVision(VisionSheetDto info, Bitmap? bmp)
    {
        var old = VisionImage;
        VisionImage = bmp;
        old?.Dispose();
        VisionInfo = info;
        VisionEmpty = false;
        VisionCaption = $"{info.Kind} → {(info.Mode == "perception" ? "perception" : "brain")} · {string.Join(", ", (info.Views ?? []).Take(6))} · ~{info.Tokens} tok";
        VisionAge = Ago(DateTimeOffset.UtcNow - info.At);
    }

    private static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? $"{Math.Max(0, t.TotalSeconds):0} s ago" : t.TotalMinutes < 60 ? $"{t.TotalMinutes:0} min ago" : $"{t.TotalHours:0.#} h ago";

    public void Dispose()
    {
        MapImage?.Dispose();
        VisionImage?.Dispose();
    }
}
