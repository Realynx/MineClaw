using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MineClaw.MemoryExplorer.ViewModels;
using MineClaw.MemoryExplorer.Views;

namespace MineClaw.MemoryExplorer;

public partial class App : Application
{
    /// <summary>Set by Program for the headless screenshot mode.</summary>
    internal static StartupOptions Startup { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (Startup.Theme is { } t) RequestedThemeVariant = t == "dark" ? ThemeVariant.Dark : t == "light" ? ThemeVariant.Light : ThemeVariant.Default;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(Startup);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            if (Startup.FreecamProfile != null) LiveHive.FreecamProfiler.Start(window, vm, Startup);
            if (Startup.LiveHiveWatch != null) LiveHive.LiveHiveWatch.Start(window, vm, Startup);
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Command-line options: <c>--db PATH</c>, <c>--config FILE</c>, <c>--page NAME</c>, <c>--theme light|dark</c>,
/// <c>--screenshot OUT.png</c> (render headlessly and exit).</summary>
public sealed class StartupOptions
{
    public string? Db { get; set; }
    public string? Config { get; set; }
    public string? Page { get; set; }
    public string? Theme { get; set; }
    public string? Screenshot { get; set; }
    public string? Query { get; set; }
    public int? Node { get; set; }
    public bool Graph { get; set; }
    /// <summary><c>--graph-layout icicle|radial|nodelink</c>, <c>--color-by kind|time|cluster</c>, <c>--focus N</c> (screenshots of the RAPTOR graph).</summary>
    public string? GraphLayout { get; set; }
    public string? ColorBy { get; set; }
    public int? Focus { get; set; }
    /// <summary><c>--tip N</c>: pin the graph tooltip of node N (screenshots).</summary>
    public int? Tip { get; set; }
    /// <summary><c>--freecam-profile OUT.json</c>: fly the Live Hive free camera on a script and write timings (see FreecamProfiler).</summary>
    public string? FreecamProfile { get; set; }
    /// <summary><c>--livehive-watch OUT.json</c>: watch the Live Hive for stalls (see LiveHiveWatch).</summary>
    public string? LiveHiveWatch { get; set; }
    public string? ProfileTab { get; set; }
    public double ProfileSeconds { get; set; } = 15;
    /// <summary><c>--renderer cpu|gl</c>: force the free camera's renderer (default: GL, CPU when GL is unavailable).</summary>
    public string? Renderer { get; set; }
    public (int W, int H)? ProfileSize { get; set; }

    public static StartupOptions Parse(string[] args)
    {
        var o = new StartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            if (!Flags.TryGetValue(args[i].ToLowerInvariant(), out var apply)) continue;
            apply(o, () => i + 1 < args.Length ? args[++i] : null);
        }
        return o;
    }

    /// <summary>Each flag (lower case) and how it reads its value: <c>next()</c> takes the following argument (null at the end).</summary>
    private static readonly Dictionary<string, Action<StartupOptions, Func<string?>>> Flags = new(StringComparer.Ordinal)
    {
        ["--db"] = (o, next) => o.Db = next(),
        ["--config"] = (o, next) => o.Config = next(),
        ["--page"] = (o, next) => o.Page = next(),
        ["--theme"] = (o, next) => o.Theme = next()?.ToLowerInvariant(),
        ["--screenshot"] = (o, next) => o.Screenshot = next(),
        ["--query"] = (o, next) => o.Query = next(),
        ["--node"] = (o, next) => o.Node = Int(next()),
        ["--graph"] = (o, _) => o.Graph = true,
        ["--graph-layout"] = (o, next) => { o.GraphLayout = next()?.ToLowerInvariant(); o.Graph = true; },
        ["--color-by"] = (o, next) => o.ColorBy = next()?.ToLowerInvariant(),
        ["--focus"] = (o, next) => { o.Focus = Int(next()); o.Graph = true; },
        ["--tip"] = (o, next) => o.Tip = Int(next()),
        ["--freecam-profile"] = (o, next) => o.FreecamProfile = next(),
        ["--livehive-watch"] = (o, next) => o.LiveHiveWatch = next(),
        ["--profile-tab"] = (o, next) => o.ProfileTab = next()?.ToLowerInvariant(),
        ["--profile-seconds"] = (o, next) => o.ProfileSeconds =
            double.TryParse(next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ps) ? ps : 15,
        ["--renderer"] = (o, next) => o.Renderer = next()?.ToLowerInvariant(),
        ["--profile-size"] = (o, next) =>
        {
            if (next()?.Split('x') is [var pw, var ph] && int.TryParse(pw, out var iw) && int.TryParse(ph, out var ih)) o.ProfileSize = (iw, ih);
        },
    };

    private static int? Int(string? s) => int.TryParse(s, out var n) ? n : null;
}
