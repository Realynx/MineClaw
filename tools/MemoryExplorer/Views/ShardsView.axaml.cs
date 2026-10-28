using Avalonia.Controls;
using Avalonia.Input;
using MineClaw.MemoryExplorer.ViewModels;

namespace MineClaw.MemoryExplorer.Views;

public partial class ShardsView : UserControl
{
    public ShardsView() => InitializeComponent();

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ShardsViewModel vm && vm.ImportSelectedCommand.CanExecute(null)) vm.ImportSelectedCommand.Execute(null);
    }
}
