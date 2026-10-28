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
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--db": o.Db = Next(); break;
                case "--config": o.Config = Next(); break;
                case "--page": o.Page = Next(); break;
                case "--theme": o.Theme = Next()?.ToLowerInvariant(); break;
                case "--screenshot": o.Screenshot = Next(); break;
                case "--query": o.Query = Next(); break;
                case "--node": o.Node = int.TryParse(Next(), out var n) ? n : null; break;
                case "--graph": o.Graph = true; break;
                case "--graph-layout": o.GraphLayout = Next()?.ToLowerInvariant(); o.Graph = true; break;
                case "--color-by": o.ColorBy = Next()?.ToLowerInvariant(); break;
                case "--focus": o.Focus = int.TryParse(Next(), out var fo) ? fo : null; o.Graph = true; break;
                case "--tip": o.Tip = int.TryParse(Next(), out var tp) ? tp : null; break;
                case "--freecam-profile": o.FreecamProfile = Next(); break;
                case "--livehive-watch": o.LiveHiveWatch = Next(); break;
                case "--profile-tab": o.ProfileTab = Next()?.ToLowerInvariant(); break;
                case "--profile-seconds": o.ProfileSeconds = double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ps) ? ps : 15; break;
                case "--renderer": o.Renderer = Next()?.ToLowerInvariant(); break;
                case "--profile-size":
                    if (Next()?.Split('x') is [var pw, var ph] && int.TryParse(pw, out var iw) && int.TryParse(ph, out var ih)) o.ProfileSize = (iw, ih);
                    break;
            }
        }
        return o;
    }
}
