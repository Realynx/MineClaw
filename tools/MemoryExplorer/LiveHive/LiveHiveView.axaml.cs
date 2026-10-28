using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MineClaw.MemoryExplorer.LiveHive;

public partial class LiveHiveView : UserControl
{
    public LiveHiveView()
    {
        InitializeComponent();
        Cam.SpeedChanged += v => Vm?.SpeedFromWheel(v);
        GlCam.SpeedChanged += v => Vm?.SpeedFromWheel(v);
        GlCam.GlFailed += why => Vm?.GpuFailed(why);
        GlCam.FrameRendered += () => { if (GlOverlay.NeedsRedraw()) GlOverlay.InvalidateVisual(); };
        GlCam.StatsToggled += () => { if (Vm is { } vm) vm.Gpu.ShowStats = !vm.Gpu.ShowStats; };
        GlCam.EngagedChanged += _ => GlOverlay.InvalidateVisual();
        // While the GPU camera is engaged its keys come first (tunnel), wherever focus ended up on this page; text boxes keep theirs.
        AddHandler(KeyDownEvent, (_, e) => CameraKey(e, true), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => CameraKey(e, false), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private LiveHiveViewModel? Vm => DataContext as LiveHiveViewModel;

    private void CameraKey(KeyEventArgs e, bool down)
    {
        if (Vm is not { UseGpuFreecam: true, IsFreecamTab: true } vm || !vm.Gpu.Engaged || e.Source is TextBox) return;
        GlCam.HandleKey(e, down);
    }

    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BotTileViewModel t) Vm?.Focus(t);
    }

    private void OnScriptBadge(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BotTileViewModel t) Vm?.OpenScript(t);
    }

    private void OnControllerButton(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BotTileViewModel t) Vm?.OpenController(t);
    }

    private void OnVisionButton(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BotTileViewModel t) Vm?.OpenVision(t);
    }

    private void OnFlyHere(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BotTileViewModel t && Vm is { } vm)
        {
            vm.SnapToCommand.Execute(t);
            if (vm.UseGpuFreecam) GlCam.Engage();
            else Cam.Focus(NavigationMethod.Pointer);
        }
    }

    private void OnJumpKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm) return;
        vm.JumpCommand.Execute(null);
        e.Handled = true;
    }
}
