using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>The vision panel: the AI's sheets and compose mode. Drag pans the picture, Ctrl + wheel zooms.</summary>
public partial class VisionPanelView : UserControl
{
    private Point? _drag;
    private Vector _dragOffset;

    public VisionPanelView()
    {
        InitializeComponent();
        Pic.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(PicScroll).Properties.IsLeftButtonPressed) return;
            _drag = e.GetPosition(PicScroll);
            _dragOffset = PicScroll.Offset;
            e.Pointer.Capture(Pic);
        };
        Pic.PointerMoved += (_, e) =>
        {
            if (_drag is not { } d) return;
            var p = e.GetPosition(PicScroll);
            PicScroll.Offset = new Vector(Math.Max(0, _dragOffset.X - (p.X - d.X)), Math.Max(0, _dragOffset.Y - (p.Y - d.Y)));
        };
        Pic.PointerReleased += (_, e) => { _drag = null; e.Pointer.Capture(null); };
        PicScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (Vm is not { } vm || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
            if (e.Delta.Y > 0) vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private VisionPanelViewModel? Vm => DataContext as VisionPanelViewModel;

    private async void OnSavePng(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CurrentPng: { } png } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        try
        {
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the sheet", SuggestedFileName = vm.SuggestedFileName, DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
            });
            if (file == null) return;
            await using var s = await file.OpenWriteAsync();
            await s.WriteAsync(png); // the exact bytes (the AI's sheet is never re-encoded)
            vm.Status = "saved " + file.Name;
        }
        catch (Exception ex) { vm.Status = "save failed: " + ex.Message; }
    }

    private async void OnCopySee(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        var text = vm.CopyText();
        if (string.IsNullOrEmpty(text)) { vm.Status = "nothing to copy yet"; return; }
        try
        {
            await cb.SetTextAsync(text);
            vm.Status = "copied: " + (text.Length > 80 ? text[..80] + "…" : text);
        }
        catch (Exception ex) { vm.Status = "copy failed: " + ex.Message; }
    }
}
