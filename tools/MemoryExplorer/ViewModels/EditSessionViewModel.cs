using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Agent.Admin;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

public sealed record AuditRow(string When, string Who, string Op, string Kind, string Id, bool Ok, string Message, string Target, string Change);
public sealed record DiffRow(string Node, string Old, string New);

/// <summary>An edit form request the view turns into a dialog: fields with their starting values.</summary>
public sealed record EditRequest(string Title, string Kind, string Op, IReadOnlyList<EditField> Fields, IReadOnlyDictionary<string, string?> Values, string? Note);

/// <summary>
/// Edit mode (off by default: browsing stays read-only) and everything that writes: the target (a local db file, or the live bot
/// through the Supervisor), the one-click backup taken before the first edit of a session, edits and deletes through the bot's own
/// stores, section rebuilds with a cost estimate + confirmation + progress + summary diff, and the audit trail.
/// </summary>
public sealed partial class EditSessionViewModel(MainWindowViewModel shell) : PageViewModel(shell), IDisposable
{
    private IAdminTarget? _session;
    private string? _sessionKey;
    private bool _backedUp;
    private CancellationTokenSource? _rebuildCts;

    public ObservableCollection<AuditRow> Audit { get; } = [];
    public ObservableCollection<DiffRow> Diffs { get; } = [];
    public IReadOnlyList<string> TargetOptions { get; } = ["Local file", "Live alice"];

    [ObservableProperty] private bool _editMode;
    [ObservableProperty] private string _target = "Local file";
    [ObservableProperty] private string _targetInfo = "";
    [ObservableProperty] private string _backupInfo = "A backup is taken automatically before the first edit of a session.";
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _rebuildRunning;
    [ObservableProperty] private double _rebuildPercent;
    [ObservableProperty] private string _rebuildStage = "";
    [ObservableProperty] private string _rebuildSummary = "";
    [ObservableProperty] private string _auditInfo = "";

    /// <summary>The view shows the edit dialog and returns the edited values (null = cancelled).</summary>
    public Func<EditRequest, Task<Dictionary<string, string?>?>>? ShowEditor { get; set; }

    public bool IsLive => Target == "Live alice";

    partial void OnEditModeChanged(bool value)
    {
        Shell.NotifyEditMode();
        if (value) _ = EnterEditModeAsync();
        else CloseTarget();
    }

    partial void OnTargetChanged(string value)
    {
        OnPropertyChanged(nameof(IsLive));
        CloseTarget();
        UpdateTargetInfo();
        if (EditMode) _ = RefreshAuditAsync();
    }

    public override Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        UpdateTargetInfo();
        return RefreshAuditAsync();
    }

    private void UpdateTargetInfo()
    {
        if (IsLive)
            TargetInfo = $"The running bot applies each edit with its own stores and audit trail ({Shell.Supervisor.BaseUrl}, {Shell.Supervisor.TokenStatus}). " +
                         "It must run with Supervisor:AllowAdminWrites=true. Browsing here still shows the snapshot: fetch a new one to see the change.";
        else if (Bank is { } b)
            TargetInfo = b.Kind == SourceKind.LocalDev
                ? $"Edits {ExplorerPaths.Display(b.Path)} directly. This is the local dev db: stop a locally running bot first (never write a db a bot is using)."
                : $"Edits {ExplorerPaths.Display(b.Path)} directly (a local copy; alice is never written).";
        else TargetInfo = "Open a database first.";
    }

    private async Task EnterEditModeAsync()
    {
        UpdateTargetInfo();
        if (!IsLive && Bank is { Kind: SourceKind.LocalDev } && Shell.Confirm is { } confirm &&
            !await confirm("Edit the local dev db?", "data/mineclaw.db is the db a locally running bot uses. Make sure no bot is running on it. Continue in edit mode?"))
        {
            EditMode = false;
            return;
        }
        Shell.Status = "Edit mode on: edits and rebuilds are enabled (" + (IsLive ? "live alice" : "local file") + ")";
        await RefreshAuditAsync();
    }

    private IAdminTarget Open()
    {
        var key = IsLive ? "live:" + Shell.Supervisor.BaseUrl : "local:" + Bank?.Path;
        if (_session != null && _sessionKey == key) return _session;
        CloseTarget();
        if (IsLive) _session = new LiveAdminTarget(Shell.Supervisor);
        else
        {
            if (Bank is not { } bank) throw new InvalidOperationException("open a database first");
            _session = new LocalAdminTarget(bank.Path, Shell.Config);
        }
        _sessionKey = key;
        return _session;
    }

    private void CloseTarget()
    {
        _session?.Dispose();
        _session = null;
        _sessionKey = null;
        _backedUp = false;
        BackupInfo = "A backup is taken automatically before the first edit of a session.";
    }

    public async Task CloseSessionAsync()
    {
        _rebuildCts?.Cancel();
        EditMode = false;
        CloseTarget();
        await Task.CompletedTask;
    }

    private string Who => "admin:" + Environment.UserName;

    private async Task<bool> EnsureBackupAsync(IAdminTarget t)
    {
        if (_backedUp) return true;
        try
        {
            var path = await t.BackupAsync(CancellationToken.None);
            _backedUp = true;
            BackupInfo = $"Session backup: {(IsLive ? path : ExplorerPaths.Display(path))} ({DateTime.Now:HH:mm:ss})";
            return true;
        }
        catch (Exception ex)
        {
            BackupInfo = "Backup failed: " + ex.Message;
            Shell.Status = "Backup failed, nothing was edited: " + ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private async Task BackupNow()
    {
        if (!EditMode) return;
        try
        {
            _backedUp = false;
            await EnsureBackupAsync(Open());
        }
        catch (Exception ex) { Shell.Status = ex.Message; }
    }

    // ------------------------------------------------------------------ edits

    public Task BeginEditAsync(string kind, MemoryRow row) => EditAsync("update", kind, row);
    public Task BeginAddAsync(string kind) => EditAsync("add", kind, null);

    private async Task EditAsync(string op, string kind, MemoryRow? row)
    {
        if (!EditMode || ShowEditor == null) return;
        if (!MemoryEditSchema.Ops(kind).Contains(op)) { Shell.Status = $"{kind}: {op} is not supported"; return; }
        var fields = MemoryEditSchema.For(kind);
        var before = row != null ? EditPrefill.For(kind, row) : fields.ToDictionary(f => f.Name, _ => (string?)"");
        var title = op == "add" ? $"Add a {kind}" : $"Edit {kind} {row!.Name}";
        var values = await ShowEditor(new EditRequest(title, kind, op, fields, before, IsLive ? "Applied by the live bot on alice." : "Applied to the local db file."));
        if (values == null) return;
        string? reason = values.Remove("__reason", out var r) ? r : null;
        var changed = op == "add" ? values.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value) : EditPrefill.Changed(before, values);
        if (op == "update" && changed.Count == 0) { Shell.Status = "Nothing changed"; return; }
        await ApplyAsync(new MemoryEdit(op, kind, row != null ? EditPrefill.IdOf(kind, row) : null, changed, Who, reason));
    }

    public async Task DeleteAsync(string kind, MemoryRow row)
    {
        if (!EditMode || Shell.Confirm == null) return;
        if (!await Shell.Confirm($"Delete this {kind}?", $"{row.Name}\n\n{row.Preview}\n\nThe record is removed through the bot's store (index, vectors and RAPTOR leaf included). A backup is taken first."))
            return;
        await ApplyAsync(new MemoryEdit("delete", kind, EditPrefill.IdOf(kind, row), new Dictionary<string, string?>(), Who, "deleted in the memory explorer"));
    }

    private async Task ApplyAsync(MemoryEdit edit)
    {
        try
        {
            var t = Open();
            if (!await EnsureBackupAsync(t)) return;
            Shell.Status = $"Applying {edit.Op} {edit.Kind}…";
            var res = await t.ApplyAsync(edit, CancellationToken.None);
            LastResult = res.Ok
                ? $"{edit.Op} {edit.Kind} {res.Id}: {res.Message}" + (res.Reembedded ? " (re-embedded)" : "")
                : $"Refused: {res.Message}";
            Shell.Status = LastResult;
            await RefreshAuditAsync();
            if (res.Ok && !IsLive) await ReloadAfterWriteAsync();
        }
        catch (Exception ex) { Shell.Status = LastResult = "Edit failed: " + ex.Message; }
    }

    /// <summary>Re-opens the browsing bank (read-only) so every view shows the new state; the write session stays open.</summary>
    private Task ReloadAfterWriteAsync() => Shell.ReloadAsync();

    // ------------------------------------------------------------------ rebuilds

    public Task StartRebuildAsync(int node) => RebuildAsync(new RebuildRequest("node", Node: node, Who: Who), $"subtree #{node}");

    public Task StartKindRebuildAsync(string browserKind)
    {
        var kind = MemoryQueries.Kinds.FirstOrDefault(k => k.Key == browserKind)?.EditKind ?? browserKind switch
        {
            "episodes" => "episode",
            "knowledge" => "knowledge",
            "recipes" => "gamedata",
            _ => browserKind,
        };
        return RebuildAsync(new RebuildRequest("kind", Kind: kind, Who: Who), $"kind {kind}");
    }

    private async Task RebuildAsync(RebuildRequest request, string what)
    {
        if (!EditMode) { Shell.Status = "Turn on edit mode to rebuild"; return; }
        if (RebuildRunning) { Shell.Status = "A rebuild is already running"; return; }
        if (!IsLive && LocalAdminTarget.LlmProblems(Shell.Config) is { Count: > 0 } problems)
        {
            Shell.Status = "Rebuild refused: " + string.Join("; ", problems);
            return;
        }
        try
        {
            var t = Open();
            Shell.Status = $"Estimating the rebuild of {what}…";
            var est = await t.EstimateAsync(request, CancellationToken.None);
            if (est.Summaries == 0)
            {
                Shell.Status = "Nothing to rebuild: " + est.Note;
                return;
            }
            var cap = Math.Max(0.05m, Math.Ceiling(est.Usd * 150m) / 100m); // estimate + 50 %, rounded up to a cent
            var msg = FormattableString.Invariant($"Rebuild {what}: re-cluster, re-summarize and re-embed.\n\n") +
                FormattableString.Invariant($"• {est.Summaries} summaries with {est.Model}\n• ~{est.InputTokens:N0} input + ~{est.OutputTokens:N0} output tokens\n") +
                FormattableString.Invariant($"• ~{est.EmbeddingTokens:N0} embedding tokens\n• estimated ${est.Usd:0.0000} (hard cap ${cap:0.00})\n\n{est.Note}\n\n") +
                (IsLive ? "Runs in the background inside the bot, under its budget caps." : "Runs here on the local file. A backup is taken first.");
            if (Shell.Confirm == null || !await Shell.Confirm("Rebuild with the model?", msg)) return;
            if (!await EnsureBackupAsync(t)) return;
            Shell.Navigate("audit");
            Diffs.Clear();
            RebuildRunning = true;
            RebuildPercent = 0;
            RebuildSummary = "";
            RebuildStage = "starting…";
            var cts = _rebuildCts = new CancellationTokenSource();
            var progress = new Progress<RebuildStatus>(s =>
            {
                if (s.Progress is { } p)
                {
                    RebuildStage = $"{p.Stage} {p.Done}/{p.Total}" + (p.Message != null ? " · " + p.Message : "");
                    RebuildPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                }
            });
            var final = await t.RebuildAsync(request with { MaxUsd = cap }, progress, cts.Token);
            if (final.Result is { } res)
            {
                foreach (var d in res.Diffs.OrderByDescending(d => d.Layer).ThenBy(d => d.Index))
                    Diffs.Add(new DiffRow($"L{d.Layer} #{d.Index}", d.Old ?? "(new node)", d.New ?? "(removed)"));
                RebuildSummary = string.Create(CultureInfo.InvariantCulture, $"{(res.Ok ? "Done" : "Failed")}: {res.Message} · ${res.SpentUsd:0.0000} · {res.Elapsed.TotalSeconds:0.0} s");
            }
            else RebuildSummary = final.Error ?? final.State;
            Shell.Status = "Rebuild " + RebuildSummary;
            await RefreshAuditAsync();
            if (final.Result?.Ok == true && !IsLive) await ReloadAfterWriteAsync();
        }
        catch (OperationCanceledException) { RebuildSummary = "Cancelled"; }
        catch (Exception ex) { Shell.Status = RebuildSummary = "Rebuild failed: " + ex.Message; }
        finally
        {
            RebuildRunning = false;
            RebuildPercent = 100;
        }
    }

    [RelayCommand]
    private void CancelRebuild() => _rebuildCts?.Cancel();

    // ------------------------------------------------------------------ audit

    [RelayCommand]
    public async Task RefreshAuditAsync()
    {
        Audit.Clear();
        try
        {
            IReadOnlyList<AuditEntry> entries;
            if (IsLive)
            {
                if (!EditMode) { AuditInfo = "Turn on edit mode to read the live bot's audit trail."; return; }
                entries = await Open().AuditAsync(300, CancellationToken.None);
            }
            else
            {
                if (Bank is not { } b) { AuditInfo = ""; return; }
                var path = LocalAdminTarget.AuditPathFor(b.Path);
                entries = File.Exists(path) ? new AdminAudit(path).Read(300) : [];
                AuditInfo = File.Exists(path) ? ExplorerPaths.Display(path) : "No edits on this file yet.";
            }
            foreach (var e in entries.OrderByDescending(e => e.At))
                Audit.Add(new AuditRow(e.At.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), e.Who, e.Op, e.Kind, e.Id ?? "", e.Ok, e.Message,
                    e.Target, $"{e.Before ?? "∅"}\n→ {e.After ?? "∅"}"));
            if (IsLive) AuditInfo = $"{entries.Count} entries from the live bot";
        }
        catch (Exception ex) { AuditInfo = "Audit unavailable: " + ex.Message; }
    }

    public void Dispose()
    {
        _rebuildCts?.Cancel();
        CloseTarget();
    }
}
