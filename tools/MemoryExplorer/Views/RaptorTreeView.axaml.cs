using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MineClaw.MemoryExplorer.Views;

public partial class RaptorTreeView : UserControl
{
    public RaptorTreeView() => InitializeComponent();

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        Graph.Fit();
        Graph.Focus();
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        Graph.Reset();
        Graph.Focus();
    }
}
