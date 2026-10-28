using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Agent.Rag;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

public sealed record HitRow(int Rank, long Id, string Ref, string Kind, string Title, string Snippet, string Body, string Score, string Signals,
    string Updated, IReadOnlyList<Crumb> Path, string Where)
{
    public bool HasPath => Path.Count > 0;
}

public sealed record TreeHitRow(string List, int Index, string Label, string Score, string Preview);

public sealed partial class SearchViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<HitRow> Hits { get; } = [];
    public ObservableCollection<TreeHitRow> TreeHits { get; } = [];
    public IReadOnlyList<string> KindOptions { get; } = ["all kinds", "episode", "knowledge", "gamedata", "structure", "note", "player", "world", "claim", "skill", "routine", "summary", "selfnote"];
    public IReadOnlyList<string> DimensionOptions { get; } = ["any dimension", "overworld", "nether"];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _kind = "all kinds";
    [ObservableProperty] private string _dimension = "any dimension";
    [ObservableProperty] private HitRow? _selectedHit;
    [ObservableProperty] private string _modeNote = "";
    [ObservableProperty] private bool _isLexical;
    [ObservableProperty] private string _resultInfo = "";
    [ObservableProperty] private bool _hasResults;

    public override Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        Hits.Clear();
        TreeHits.Clear();
        HasResults = false;
        ResultInfo = "";
        if (bank == null) { EmptyText = "Open a database to search it."; ModeNote = ""; return Task.CompletedTask; }
        var svc = new SearchService(bank);
        ModeNote = svc.ModeNote;
        IsLexical = svc.Mode == SearchMode.Lexical;
        EmptyText = "Type a question or keywords and press Enter. Ctrl+K jumps here from anywhere.";
        return string.IsNullOrWhiteSpace(Query) ? Task.CompletedTask : RunSearchAsync();
    }

    [RelayCommand]
    public async Task RunSearchAsync()
    {
        if (Bank is not { } bank || string.IsNullOrWhiteSpace(Query)) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            IReadOnlyCollection<string>? kinds = Kind == "all kinds" ? null : [Kind];
            int? dim = Dimension switch { "overworld" => 0, "nether" => -1, _ => null };
            var outcome = await new SearchService(bank).SearchAsync(Query.Trim(), kinds, dim, 30, cts.Token);
            if (cts.IsCancellationRequested) return;
            Hits.Clear();
            TreeHits.Clear();
            foreach (var h in outcome.Hits) Hits.Add(Row(bank, h));
            foreach (var t in outcome.TreeHits)
                TreeHits.Add(new TreeHitRow(t.List, t.Index, t.Leaf ? $"leaf #{t.Index}" : $"L{t.Layer} #{t.Index}", t.Score.ToString("0.000", CultureInfo.InvariantCulture), t.Preview));
            ModeNote = outcome.ModeNote;
            IsLexical = outcome.Mode == SearchMode.Lexical;
            HasResults = Hits.Count > 0;
            ResultInfo = $"{Hits.Count} hits in {outcome.Elapsed.TotalMilliseconds:0} ms" + (outcome.QueryEmbedded ? " · query embedded" : "");
            EmptyText = Hits.Count == 0 ? $"Nothing found for \"{Query.Trim()}\"." : null;
            SelectedHit = Hits.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EmptyText = "Search failed: " + ex.Message; }
        finally { if (ReferenceEquals(_cts, cts)) IsLoading = false; }
    }

    partial void OnKindChanged(string value) => _ = RunSearchAsync();
    partial void OnDimensionChanged(string value) => _ = RunSearchAsync();

    private static HitRow Row(MemoryBank bank, SearchHit s)
    {
        var h = s.Hit;
        var signals = new List<string>();
        if (h.Lexical > 0) signals.Add($"bm25 {h.Lexical:0.00}");
        if (h.Cosine > 0) signals.Add($"cos {h.Cosine:0.000}");
        if (h.Distance is { } d) signals.Add($"{d:0.#} m");
        var crumbs = s.TreePath.Select(i => new Crumb(i, bank.ReadTree(t => t.TryGet(i, out var n) ? (n.IsLeaf ? $"leaf #{i}" : $"L{n.Layer} #{i}") : $"#{i}", $"#{i}"))).ToList();
        var snippet = (h.Snippet ?? h.Body).ReplaceLineEndings(" ");
        if (snippet.Length > 260) snippet = snippet[..260] + "…";
        string where = h.Bounds is { } b ? b.Format() + (h.Dimension == -1 ? " (nether)" : "") : "";
        return new HitRow(s.Rank, h.Id, h.Ref, h.Kind + (h.Tag != null ? "/" + h.Tag : ""), h.Title, snippet, h.Body,
            h.Score.ToString("0.0000", CultureInfo.InvariantCulture), string.Join(" · ", signals), h.Updated.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            crumbs, where);
    }

    [RelayCommand]
    private void OpenInTree(int index)
    {
        Shell.Navigate("tree");
        Shell.Tree.Reveal(index);
    }
}
