using Avalonia;
using Avalonia.Headless;
using MineClaw.MemoryExplorer.ViewModels;
using MineClaw.MemoryExplorer.Views;

namespace MineClaw.MemoryExplorer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var startup = StartupOptions.Parse(args);
        App.Startup = startup;
        if (startup.Screenshot != null) return Screenshot.Run(startup);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

/// <summary>
/// <c>--screenshot out.png [--db …] [--page tree] [--theme dark]</c>: renders the main window with Avalonia's headless platform
/// (real Skia drawing, no window on screen), waits for the data to load, saves one frame and exits.
/// </summary>
internal static class Screenshot
{
    public static int Run(StartupOptions o)
    {
        SetUpHeadless(o);
        var vm = new MainWindowViewModel(o);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        var init = vm.InitializeAsync();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        PumpWhile(() => !init.IsCompleted || vm.IsBusy, deadline);
        RunQuery(vm, o, deadline);
        ApplyTreeOptions(vm.Tree, o);
        Pump(30);
        PinTip(window, o);
        return Save(window, vm, o);
    }

    /// <summary>Avalonia's headless platform with real Skia drawing, in the requested theme.</summary>
    private static void SetUpHeadless(StartupOptions o)
    {
        var builder = AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        builder.SetupWithoutStarting();
        if (o.Theme is { } t)
            Avalonia.Application.Current!.RequestedThemeVariant = t == "dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    private static void PumpWhile(Func<bool> busy, DateTime deadline)
    {
        while (busy() && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    private static void Pump(int frames)
    {
        for (int i = 0; i < frames; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    }

    /// <summary><c>--query</c> on the search or tree page: runs the search first (the tree lights its hits up).</summary>
    private static void RunQuery(MainWindowViewModel vm, StartupOptions o, DateTime deadline)
    {
        if (vm.Pages.FirstOrDefault(p => p.Key == "search") is { } sp && o.Query != null && o.Page is "search" or "tree")
        {
            var s = (SearchViewModel)sp.Page;
            s.Query = o.Query;
            var run = s.RunSearchAsync();
            PumpWhile(() => !run.IsCompleted, deadline);
        }
    }

    /// <summary><c>--graph --graph-layout --color-by --node --focus</c>.</summary>
    private static void ApplyTreeOptions(RaptorTreeViewModel tree, StartupOptions o)
    {
        if (o.Graph) tree.GraphMode = true;
        if (o.GraphLayout != null) tree.SetLayoutCommand.Execute(o.GraphLayout);
        if (o.ColorBy != null) tree.ColorByIndex = o.ColorBy switch { "time" => 1, "cluster" => 2, _ => 0 };
        if (o.Node is { } node) tree.Reveal(node);
        if (o.Focus is { } focus) tree.GraphFocus = focus;
    }

    /// <summary><c>--tip</c>: pins the graph's tooltip on a node.</summary>
    private static void PinTip(MainWindow window, StartupOptions o)
    {
        if (o.Tip is { } tip && Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Controls.LayeredGraph>().FirstOrDefault() is { } graph)
        {
            graph.PinTooltip(tip);
            Pump(5);
        }
    }

    private static int Save(MainWindow window, MainWindowViewModel vm, StartupOptions o)
    {
        var frame = window.CaptureRenderedFrame();
        if (frame == null) { Console.Error.WriteLine("no frame rendered"); return 1; }
        var path = Path.GetFullPath(o.Screenshot!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path);
        Console.WriteLine(path);
        vm.Dispose();
        return 0;
    }
}
