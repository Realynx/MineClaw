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
        var builder = AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        builder.SetupWithoutStarting();
        if (o.Theme is { } t)
            Avalonia.Application.Current!.RequestedThemeVariant = t == "dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        var vm = new MainWindowViewModel(o);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        var init = vm.InitializeAsync();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while ((!init.IsCompleted || vm.IsBusy) && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        if (vm.Pages.FirstOrDefault(p => p.Key == "search") is { } sp && o.Query != null && o.Page is "search" or "tree")
        {
            var s = (SearchViewModel)sp.Page;
            s.Query = o.Query;
            var run = s.RunSearchAsync();
            while (!run.IsCompleted && DateTime.UtcNow < deadline) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        }
        if (o.Graph) vm.Tree.GraphMode = true;
        if (o.GraphLayout != null) vm.Tree.SetLayoutCommand.Execute(o.GraphLayout);
        if (o.ColorBy != null) vm.Tree.ColorByIndex = o.ColorBy switch { "time" => 1, "cluster" => 2, _ => 0 };
        if (o.Node is { } node) vm.Tree.Reveal(node);
        if (o.Focus is { } focus) vm.Tree.GraphFocus = focus;
        for (int i = 0; i < 30; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        if (o.Tip is { } tip && Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Controls.LayeredGraph>().FirstOrDefault() is { } graph)
        {
            graph.PinTooltip(tip);
            for (int i = 0; i < 5; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        }
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
