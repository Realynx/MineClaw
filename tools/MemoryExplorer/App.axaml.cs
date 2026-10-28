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
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
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
            }
        }
        return o;
    }
}
