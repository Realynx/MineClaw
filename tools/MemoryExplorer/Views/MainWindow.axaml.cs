using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.ViewModels;

namespace MineClaw.MemoryExplorer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RecentList.SelectionChanged += async (_, _) =>
        {
            if (RecentList.SelectedItem is RecentDb r && DataContext is MainWindowViewModel vm)
            {
                RecentList.SelectedItem = null;
                RecentButton.Flyout?.Hide();
                await vm.OpenAsync(r.Path);
            }
        };
        RecentButton.Click += (_, _) => (DataContext as MainWindowViewModel)?.RefreshRecent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm) return;
        vm.Clipboard = async text =>
        {
            if (Clipboard is { } cb) await cb.SetTextAsync(text);
        };
        vm.PickDatabase = () => PickFile("Open a MineClaw database", ["*.db", "*.sqlite"], ExplorerPaths.DataDir);
        vm.PickConfig = () => PickFile("Layer in a config file", ["*.json"], Path.GetDirectoryName(ExplorerPaths.BotConfig)!);
        vm.Confirm = (title, message) => ConfirmDialog.ShowAsync(this, title, message);
        vm.Edit.ShowEditor = req => EditDialog.ShowAsync(this, req);
        vm.FocusSearchRequested += () =>
        {
            GlobalSearch.Focus();
            GlobalSearch.SelectAll();
        };
    }

    private async Task<string?> PickFile(string title, IReadOnlyList<string> patterns, string startDir)
    {
        var start = Directory.Exists(startDir) ? await StorageProvider.TryGetFolderFromPathAsync(startDir) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
