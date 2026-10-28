using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>The Controller panel: a brain's live context, turns and thinking, or the script debugger (or both, as tabs).</summary>
public partial class ControllerPanelView : UserControl
{
    public ControllerPanelView() => InitializeComponent();

    private ControllerPanelViewModel? Vm => DataContext as ControllerPanelViewModel;

    private async void OnToggleTurn(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not ControllerTurnRow row) return;
        try { await vm.ToggleAsync(row); }
        catch (Exception ex) { row.DetailState = ex.Message; }
    }

    private async void OnCopyTurn(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not ControllerTurnRow row || TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        try
        {
            var json = await vm.TurnJsonAsync(row);
            await cb.SetTextAsync(json);
            row.DetailState = $"copied turn #{row.Id} as JSON ({json.Length / 1024.0:0.0} KB)";
        }
        catch (Exception ex) { row.DetailState = "copy failed: " + ex.Message; }
    }
}
