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
        GlCam.GotFocus += (_, _) => GlOverlay.CameraFocused = true;
        GlCam.LostFocus += (_, _) => GlOverlay.CameraFocused = false;
    }

    private LiveHiveViewModel? Vm => DataContext as LiveHiveViewModel;

    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BotTileViewModel t) Vm?.Focus(t);
    }

    private void OnFlyHere(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BotTileViewModel t && Vm is { } vm)
        {
            vm.SnapToCommand.Execute(t);
            if (vm.UseGpuFreecam) GlCam.Focus(NavigationMethod.Pointer);
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
