using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Agent.Admin.Shards;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

public sealed partial class KindItem(MemoryKindInfo info) : ObservableObject
{
    public MemoryKindInfo Info { get; } = info;
    public string Label => Info.Label;
    public string Group => Info.Group;
    [ObservableProperty] private string _count = "";
}

public sealed record PropRow(string Key, string Value);

public sealed partial class MemoryBrowserViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    private IReadOnlyList<MemoryRow> _all = [];
    private CancellationTokenSource? _cts;

    public ObservableCollection<KindItem> Kinds { get; } = new(MemoryQueries.Kinds.Select(k => new KindItem(k)));
    public ObservableCollection<MemoryRow> Rows { get; } = [];
    public ObservableCollection<PropRow> Props { get; } = [];
    public ObservableCollection<string> Extra { get; } = [];
    public IReadOnlyList<string> DimensionOptions { get; } = ["any dimension", "overworld", "nether", "no position"];

    [ObservableProperty] private KindItem? _selectedKind;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _dimension = "any dimension";
    [ObservableProperty] private string _state = "any state";
    [ObservableProperty] private MemoryRow? _selected;
    [ObservableProperty] private string _rowInfo = "";
    [ObservableProperty] private string _extraHeader = "";
    [ObservableProperty] private string _kindDescription = "";

    public ObservableCollection<string> StateOptions { get; } = ["any state"];

    public override async Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        Rows.Clear();
        Props.Clear();
        Extra.Clear();
        _all = [];
        if (bank == null) { EmptyText = "Open a database to browse its memory."; return; }
        var counts = await Task.Run(() =>
        {
            var q = new MemoryQueries(bank.Db);
            return Kinds.ToDictionary(k => k.Info.Key, k => q.Rows(k.Info.Key).Count);
        }, ct);
        foreach (var k in Kinds) k.Count = counts.TryGetValue(k.Info.Key, out var c) ? c.ToString("N0") : "";
        SelectedKind ??= Kinds[0];
        await LoadKindAsync();
    }

    partial void OnSelectedKindChanged(KindItem? value)
    {
        OnPropertyChanged(nameof(CanEditKind));
        _ = LoadKindAsync();
    }
    partial void OnFilterChanged(string value) => Apply();
    partial void OnDimensionChanged(string value) => Apply();
    partial void OnStateChanged(string value) => Apply();

    private async Task LoadKindAsync()
    {
        if (Bank is not { } bank || SelectedKind is not { } kind) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        KindDescription = kind.Info.Description;
        try
        {
            var rows = await Task.Run(() => new MemoryQueries(bank.Db).Rows(kind.Info.Key), cts.Token);
            if (cts.IsCancellationRequested) return;
            _all = rows;
            var states = rows.Select(r => r.State).Where(s => s.Length > 0).Distinct().OrderBy(s => s).ToList();
            StateOptions.Clear();
            StateOptions.Add("any state");
            foreach (var s in states) StateOptions.Add(s);
            State = "any state";
            Apply();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_cts, cts)) IsLoading = false; }
    }

    private void Apply()
    {
        var f = Filter.Trim().ToLowerInvariant();
        var terms = f.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? dim = Dimension switch { "overworld" => 0, "nether" => -1, _ => null };
        bool noPos = Dimension == "no position";
        var list = _all.Where(r =>
            (terms.Length == 0 || terms.All(t => r.Search.Contains(t, StringComparison.Ordinal))) &&
            (dim == null || r.Dimension == dim) && (!noPos || r.Dimension == null) &&
            (State == "any state" || r.State == State)).ToList();
        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
        RowInfo = list.Count == _all.Count ? $"{_all.Count:N0} rows" : $"{list.Count:N0} of {_all.Count:N0} rows";
        EmptyText = _all.Count == 0 ? $"No {SelectedKind?.Label.ToLowerInvariant()} in this db." : list.Count == 0 ? "No rows match the filter." : null;
        if (Selected != null && !list.Contains(Selected)) Selected = list.FirstOrDefault();
        else Selected ??= list.FirstOrDefault();
    }

    partial void OnSelectedChanged(MemoryRow? value)
    {
        Props.Clear();
        Extra.Clear();
        ExtraHeader = "";
        if (value == null || Bank is not { } bank) return;
        foreach (var p in value.Props) Props.Add(new PropRow(p.Key, p.Value));
        ShowExtra(value, new MemoryQueries(bank.Db));
        if (LeafOf(bank, value.DocRef) is { } node) Props.Add(new PropRow("RAPTOR leaf", "#" + node));
    }

    /// <summary>The detail pane's extra list: a claim's evidence, or a container's last-seen contents.</summary>
    private void ShowExtra(MemoryRow value, MemoryQueries q)
    {
        if (value.Kind == "claims" && long.TryParse(value.Id, out var cid))
        {
            ExtraHeader = "Evidence";
            foreach (var e in q.ClaimEvidence(cid)) Extra.Add(e);
        }
        else if (value.Kind == "observation" && long.TryParse(value.Id, out var oid) && q.ContainerItems(oid) is { Count: > 0 } items)
        {
            ExtraHeader = "Contents (last seen)";
            foreach (var i in items) Extra.Add(i);
        }
    }

    /// <summary>The RAPTOR leaf of a row's doc, if it has one in the tree.</summary>
    private static int? LeafOf(MemoryBank bank, string? docRef) =>
        docRef != null && bank.Rag.GetByRef(docRef) is { } doc ? bank.NodeOfDoc(doc.Id) : null;

    [RelayCommand]
    private void ShowInTree()
    {
        if (Selected?.DocRef is not { } r || Bank is not { } bank || LeafOf(bank, r) is not { } node) return;
        Shell.Navigate("tree");
        Shell.Tree.Reveal(node);
    }

    [RelayCommand]
    private Task CopyRow() => Selected is { } s
        ? Shell.CopyAsync($"{s.Name}\n{s.Text}\n\n" + string.Join("\n", s.Props.Select(p => $"{p.Key}: {p.Value}")))
        : Task.CompletedTask;

    [RelayCommand]
    private void ClearFilter()
    {
        Filter = "";
        Dimension = "any dimension";
        State = "any state";
    }

    [RelayCommand]
    private Task EditRow() => Selected is { } s && SelectedKind?.Info.EditKind is { } k ? Shell.Edit.BeginEditAsync(k, s) : Task.CompletedTask;

    [RelayCommand]
    private Task DeleteRow() => Selected is { } s && SelectedKind?.Info.EditKind is { } k ? Shell.Edit.DeleteAsync(k, s) : Task.CompletedTask;

    [RelayCommand]
    private Task AddRow() => SelectedKind?.Info.EditKind is { } k ? Shell.Edit.BeginAddAsync(k) : Task.CompletedTask;

    [RelayCommand]
    private Task RebuildKind() => SelectedKind is { } k ? Shell.Edit.StartKindRebuildAsync(k.Info.Key) : Task.CompletedTask;

    public bool CanEditKind => SelectedKind?.Info.EditKind != null;

    /// <summary>"Export this kind as a shard…": the selected kind with the current text filter and dimension.</summary>
    [RelayCommand]
    private Task ExportKind()
    {
        if (SelectedKind is not { } k) return Task.CompletedTask;
        if (ShardService.KindOf(k.Info.Key) is not { } kind)
        {
            Shell.Status = $"{k.Label} stay out of shards (world observations, the inventory ledger, chat and activity are never exported)";
            return Task.CompletedTask;
        }
        int? dim = Dimension switch { "overworld" => 0, "nether" => -1, _ => null };
        var sel = new ShardSelection
        {
            Mode = "filter", Kinds = [kind], Dimension = dim, Query = string.IsNullOrWhiteSpace(Filter) ? null : Filter.Trim(),
            IncludeWorld = ShardKinds.IsWorld(kind), IncludeRules = kind == ShardKinds.Rule,
        };
        return Shell.Shards.ExportAsync(sel, k.Label.ToLowerInvariant());
    }

    /// <summary>"Export everything about…": the person shard, prefilled with the selected row's player when it names one.</summary>
    [RelayCommand]
    private Task ExportPerson()
    {
        var people = ShardService.People(Shell.Config);
        var hit = Selected is { } s
            ? people.FirstOrDefault(p => p.Names.Any(n => n.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ||
                s.Props.Any(kv => kv.Key is "player" or "claimant" or "subject" && kv.Value.Equals(n, StringComparison.OrdinalIgnoreCase))))
            : null;
        return Shell.Shards.ExportAsync(new ShardSelection { Mode = "person", People = hit?.Names.ToList() }, hit != null ? "about " + hit.Login : "about");
    }
}
