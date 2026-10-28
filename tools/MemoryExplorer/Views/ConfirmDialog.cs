using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MineClaw.MemoryExplorer.Views;

/// <summary>A small modal yes/no dialog.</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string ok = "Continue")
    {
        var result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        dlg.Bind(Window.BackgroundProperty, dlg.GetResourceObservable("CardBrush"));
        var yes = new Button { Content = ok, Classes = { "accent" }, IsDefault = true };
        var no = new Button { Content = "Cancel", Classes = { "ghost" }, IsCancel = true };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" } },
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "body" } },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } },
            },
        };
        await dlg.ShowDialog(owner);
        return result;
    }
}
