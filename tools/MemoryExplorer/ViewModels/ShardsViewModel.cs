using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.Views;

namespace MineClaw.MemoryExplorer.ViewModels;

/// <summary>One export/import from the audit trail.</summary>
public sealed record ShardHistoryRow(string When, string Op, string Name, bool Ok, string Message, string Who, string Target);

/// <summary>
/// The Shards page: the exported <c>.mcshard</c> files under <c>data/shards</c>, the export/import history from the audit trail, the
/// active knowledge pack, and the two flows: Export (from the opened, read-only bank) and Import (diff first, then the write through
/// the edit target: the local file with its backup, or the live bot through the Supervisor), followed by the scoped RAPTOR rebuild.
/// Dropping a .mcshard on the window starts an import.
/// </summary>
public sealed partial class ShardsViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<ShardFileInfo> Files { get; } = [];
    public ObservableCollection<ShardHistoryRow> History { get; } = [];

    [ObservableProperty] private ShardFileInfo? _selectedFile;
    [ObservableProperty] private string _activePack = "open a db to see it";
    [ObservableProperty] private string _filesInfo = "";
    [ObservableProperty] private string _historyInfo = "";
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private double _percent;

    /// <summary>The view supplies the dialogs and the pickers.</summary>
    public Func<ShardExportDialogRequest, Task<string?>>? ShowExport { get; set; }
    public Func<ShardImportDialogRequest, Task<ShardImportOptions?>>? ShowImport { get; set; }
    public Func<Task<string?>>? PickShard { get; set; }
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    public string Folder => ExplorerPaths.Display(ShardService.ShardsDir);

    public override Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        ActivePack = bank == null ? "open a db to see it" : PackLabel(KnowledgePack.Active(bank.Db));
        return RefreshAsync();
    }

    public override void Activate() => _ = RefreshAsync();

    public static string PackLabel(KnowledgePackInfo p) => p.IsDefault ? $"{p.Version} (built-in seed)" : $"{p.Version} · {p.Name}";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var files = await Task.Run(() => ShardService.ListFiles());
        Files.Clear();
        foreach (var f in files) Files.Add(f);
        FilesInfo = files.Count == 0 ? $"No shards in {Folder} yet: export one, or drop a .mcshard on the window to import it." : $"{files.Count} shard file(s) in {Folder}";
        await RefreshHistoryAsync();
    }

    private async Task RefreshHistoryAsync()
    {
        History.Clear();
        var entries = new List<AuditEntry>();
        try
        {
            if (Bank is { } b && File.Exists(LocalAdminTarget.AuditPathFor(b.Path))) entries.AddRange(new AdminAudit(LocalAdminTarget.AuditPathFor(b.Path)).Read(1000));
            if (Shell.Edit is { EditMode: true, IsLive: true }) entries.AddRange(await Shell.Edit.OpenTarget().AuditAsync(1000, CancellationToken.None));
        }
        catch (Exception ex) { HistoryInfo = "History unavailable: " + ex.Message; return; }
        foreach (var e in entries.Where(e => e.Kind == "shard" && e.Op is "import" or "export").OrderByDescending(e => e.At).Take(200))
            History.Add(new ShardHistoryRow(e.At.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), e.Op, e.Id ?? "", e.Ok, e.Message, e.Who,
                e.Target.StartsWith("live:", StringComparison.Ordinal) ? e.Target : "local"));
        HistoryInfo = History.Count == 0 ? "No shard exports or imports recorded for this db yet." : $"{History.Count} shard operations (audit trail)";
    }

    // ------------------------------------------------------------------ export

    [RelayCommand] private Task ExportFilter() => ExportAsync(new ShardSelection { Mode = "filter" }, "memory");
    [RelayCommand] private Task ExportPerson() => ExportAsync(new ShardSelection { Mode = "person" }, "about");
    [RelayCommand] private Task ExportPack() => ExportAsync(new ShardSelection { Mode = "pack" }, "");

    /// <summary>Opens the export dialog on a selection (the tree, the browser and this page all come here).</summary>
    public async Task ExportAsync(ShardSelection seed, string name, string? subtreeLabel = null)
    {
        if (Bank is not { } bank) { Shell.Status = "Open a database first"; return; }
        if (ShowExport == null || PickSavePath == null) return;
        var source = $"{ExplorerPaths.Display(bank.Path)}";
        var path = await ShowExport(new ShardExportDialogRequest(name, seed, subtreeLabel, ShardService.People(Shell.Config), KnowledgePack.ActiveVersion(bank.Db), source,
            req => ShardService.PreviewAsync(bank, req),
            async (req, p) =>
            {
                var (shard, bytes) = await ShardService.ExportAsync(bank, req, p);
                return $"Exported {shard.Manifest.Total:N0} records ({MemoryBank.FormatBytes(bytes)}) to {ExplorerPaths.Display(p)}";
            },
            PickSavePath));
        if (path == null) return;
        LastResult = Shell.Status = $"Exported shard {ExplorerPaths.Display(path)}";
        await RefreshAsync();
        SelectedFile = Files.FirstOrDefault(f => string.Equals(f.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ import

    [RelayCommand]
    private async Task ImportFile()
    {
        if (PickShard == null) return;
        if (await PickShard() is { } p) await ImportAsync(p);
    }

    [RelayCommand]
    private Task ImportSelected() => SelectedFile is { } f ? ImportAsync(f.Path) : Task.CompletedTask;

    /// <summary>
    /// Diff first, then the write: needs edit mode (it offers to turn it on), goes to the edit target (local file + backup, or the
    /// live bot), then runs the import's scoped RAPTOR rebuild (local) or follows the bot's background job (live).
    /// </summary>
    public async Task ImportAsync(string path)
    {
        Shell.Navigate("shards");
        if (Running) { Shell.Status = "An import is already running"; return; }
        var info = ShardService.Describe(path);
        if (!info.Ok) { LastResult = Shell.Status = $"Can't import {Path.GetFileName(path)}: {info.Error}"; return; }
        if (ShowImport == null) return;
        if (!Shell.Edit.EditMode)
        {
            var where = Shell.Edit.IsLive ? "the live bot on alice" : Bank != null ? ExplorerPaths.Display(Bank.Path) : "a db";
            if (Shell.Confirm == null || !await Shell.Confirm("Turn on edit mode?", $"Importing \"{info.Name}\" writes memory into {where}. Turn on edit mode to continue?")) return;
            if (!await Shell.Edit.TurnOnAsync()) return;
        }
        if (!Shell.Edit.IsLive && Bank == null) { Shell.Status = "Open the database to import into first"; return; }
        try
        {
            var target = Shell.Edit.OpenTarget();
            var active = Bank != null ? KnowledgePack.ActiveVersion(Bank.Db) : KnowledgePack.DefaultVersion;
            var options = await ShowImport(new ShardImportDialogRequest(info, target.Describe, Shell.Edit.IsLive, active,
                o => target.PreviewShardAsync(path, o, CancellationToken.None)));
            if (options == null) return;
            if (!await Shell.Edit.EnsureBackupAsync(target)) return;
            Running = true;
            Percent = 0;
            Stage = "importing…";
            var cts = _cts = new CancellationTokenSource();
            var result = await target.ImportShardAsync(path, options with { Who = Shell.Edit.Who }, cts.Token);
            LastResult = Shell.Status = (result.Ok ? "" : "Import failed: ") + result.Message;
            if (result.Ok && result.Rebuild.Count > 0)
            {
                Stage = "rebuilding " + string.Join(", ", result.Rebuild.Select(r => r.Kind)) + "…";
                var progress = new Progress<RebuildStatus>(s =>
                {
                    if (s.Progress is { } p)
                    {
                        Stage = $"{p.Stage} {p.Done}/{p.Total}" + (p.Message != null ? " · " + p.Message : "");
                        Percent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                    }
                });
                var rb = await target.RunImportRebuildAsync(result, progress, cts.Token);
                if (rb != null)
                    LastResult = Shell.Status = result.Message + " · rebuild " + (rb.Result is { } r
                        ? string.Create(CultureInfo.InvariantCulture, $"{rb.State}: {r.Message} (${r.SpentUsd:0.0000})")
                        : rb.Error ?? rb.State);
            }
            await RefreshHistoryAsync();
            if (!Shell.Edit.IsLive && result.Ok) await Shell.ReloadAsync();
        }
        catch (OperationCanceledException) { LastResult = Shell.Status = "Import cancelled"; }
        catch (Exception ex) { LastResult = Shell.Status = "Import failed: " + ex.Message; }
        finally
        {
            Running = false;
            Percent = 100;
            Stage = "";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ShardService.ShardsDir);
            Process.Start(new ProcessStartInfo { FileName = ShardService.ShardsDir, UseShellExecute = true });
        }
        catch (Exception ex) { Shell.Status = "Can't open the folder: " + ex.Message; }
    }
}
