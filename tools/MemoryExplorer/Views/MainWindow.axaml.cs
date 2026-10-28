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
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static string? ShardIn(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null && p.EndsWith(Agent.Admin.Shards.ShardFile.Extension, StringComparison.OrdinalIgnoreCase));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool shard = ShardIn(e) != null;
        e.DragEffects = shard ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = shard;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (ShardIn(e) is not { } path || DataContext is not MainWindowViewModel vm) return;
        e.Handled = true;
        try { await vm.Shards.ImportAsync(path); }
        catch (Exception ex) { vm.Status = "Import failed: " + ex.Message; }
    }

    private async Task<string?> SaveShard(string suggested)
    {
        var dir = Path.GetDirectoryName(suggested)!;
        Directory.CreateDirectory(dir);
        var start = await StorageProvider.TryGetFolderFromPathAsync(dir);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the memory shard",
            SuggestedFileName = Path.GetFileName(suggested),
            SuggestedStartLocation = start,
            DefaultExtension = "mcshard",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Memory shard") { Patterns = ["*.mcshard"] }],
        });
        return file?.TryGetLocalPath();
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
        vm.Shards.ShowExport = req => ShardExportDialog.ShowAsync(this, req);
        vm.Shards.ShowImport = req => ShardImportDialog.ShowAsync(this, req);
        vm.Shards.PickShard = () => PickFile("Import a memory shard", ["*.mcshard"], ShardService.ShardsDir);
        vm.Shards.PickSavePath = SaveShard;
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
