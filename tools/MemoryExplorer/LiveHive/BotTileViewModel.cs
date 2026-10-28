using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>A view a tile can show: "eye" is rendered locally from the cached world; the rest come from the server as PNGs.</summary>
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
    [NotifyPropertyChangedFor(nameof(IsEye), nameof(IsMap))]
    private TileMode _mode;

    public bool IsEye => Mode.ServerMode == null;
    public bool IsMap => !IsEye;

    /// <summary>The last data (UI thread).</summary>
    public BotDto? Bot { get; private set; }

    partial void OnModeChanged(TileMode value)
    {
        MapImage = null;
        MapLoading = true;
    }

    public void Update(BotDto b)
    {
        Bot = b;
        Title = b.Main ? $"{b.Name} · main" : $"{b.Name} · {b.Short}";
        Subtitle = string.IsNullOrEmpty(b.Brain) ? (b.Live ? "in-process body" : "clone") : b.Brain!;
        Job = string.IsNullOrWhiteSpace(b.Job) ? "idle" : b.Job!;
        Hp = b.Hp;
        Food = b.Food;
        LowHp = b.Hp is > 0 and <= 6;
        Vitals = $"♥ {b.Hp}  ◆ {b.Food}";
        Held = string.IsNullOrEmpty(b.Held) ? null : "holding " + b.Held;
        var claims = b.Claims ?? [];
        HasClaims = claims.Count > 0;
        Claims = claims.Count == 0 ? "" : "claims " + string.Join(", ", claims.Take(3)) + (claims.Count > 3 ? $" +{claims.Count - 3}" : "");
        Where = string.Create(CultureInfo.InvariantCulture, $"{(b.Dim == -1 ? "nether " : "")}{b.X:0},{b.Y:0},{b.Z:0}");
        PoseNote = b.Yaw == null ? "no pose yet (eye faces south)" : b.PoseAge is > 3 ? $"pose {b.PoseAge:0} s old" : null;
    }

    public void SetMap(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var old = MapImage;
            MapImage = new Bitmap(ms);
            old?.Dispose();
            MapLoading = false;
        }
        catch (Exception) { /* a bad frame: keep the last one */ }
    }

    public void Dispose() => MapImage?.Dispose();
}
