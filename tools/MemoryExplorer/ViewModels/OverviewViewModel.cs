using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MineClaw.MemoryExplorer.Services;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Storage;

namespace MineClaw.MemoryExplorer.ViewModels;

public sealed record LayerStat(int Layer, string Name, int Nodes, string AvgChildren, string AvgTokens, string MaxTokens, int Dirty, double Share);
public sealed record StatLine(string Label, string Value);

/// <summary>RAPTOR statistics computed from the loaded tree.</summary>
public sealed record RaptorStats(IReadOnlyList<LayerStat> Layers, IReadOnlyList<StatLine> Lines, int Nodes, int Leaves, int NumLayers)
{
    public static RaptorStats From(RaptorTree t, RaptorTreeMeta? meta)
    {
        var layers = new List<LayerStat>();
        int max = Math.Max(1, Enumerable.Range(0, t.NumLayers + 1).Max(l => t.LayerIndices(l).Count));
        for (int l = t.NumLayers; l >= 0; l--)
        {
            var nodes = t.Layer(l).ToList();
            if (nodes.Count == 0) continue;
            string avgKids = l == 0 ? "—" : nodes.Average(n => n.Children.Count).ToString("0.0", CultureInfo.InvariantCulture);
            layers.Add(new LayerStat(l, l == 0 ? "Leaves" : l == t.NumLayers ? $"L{l} · roots" : $"L{l}", nodes.Count, avgKids,
                nodes.Average(n => n.TokenCount).ToString("0", CultureInfo.InvariantCulture), nodes.Max(n => n.TokenCount).ToString(CultureInfo.InvariantCulture),
                nodes.Count(n => n.Dirty || n.SummaryPending), nodes.Count / (double)max));
        }
        var lines = new List<StatLine>
        {
            new("Embedding model", $"{t.EmbeddingModel} · {t.Dimensions} dims"),
            new("Built", t.BuiltAt == default ? "never (incremental only)" : $"{t.BuiltAt.ToLocalTime():yyyy-MM-dd HH:mm} ({MainWindowViewModel.Ago(DateTimeOffset.UtcNow - t.BuiltAt)})"),
            new("Leaves at build", t.LeavesAtBuild.ToString("N0", CultureInfo.InvariantCulture)),
            new("Inserted / removed since", $"{t.InsertedSinceBuild:N0} / {t.RemovedSinceBuild:N0}"),
            new("Leaf→parent cosine at build", $"{t.BaselineLeafParentSimilarity:0.000} ± {t.BaselineLeafParentSimilarityStd:0.000}"),
            new("Inserted leaf→parent cosine", t.InsertedSimilarityCount > 0 ? (t.InsertedSimilaritySum / t.InsertedSimilarityCount).ToString("0.000", CultureInfo.InvariantCulture) : "—"),
            new("Insert temperature τ", t.InsertTemperature.ToString("0.0000", CultureInfo.InvariantCulture)),
            new("Dirty / pending summaries", t.AllNodes.Values.Count(n => n.Dirty || n.SummaryPending).ToString(CultureInfo.InvariantCulture)),
        };
        if (meta != null && meta.NumLayers != t.NumLayers) lines.Add(new("Stored layers", meta.NumLayers.ToString(CultureInfo.InvariantCulture)));
        return new RaptorStats(layers, lines, t.Count, t.LeafCount, t.NumLayers);
    }
}

public sealed partial class OverviewViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<CountCard> Cards { get; } = [];
    public ObservableCollection<LayerStat> Layers { get; } = [];
    public ObservableCollection<StatLine> TreeLines { get; } = [];
    public ObservableCollection<StatLine> DbLines { get; } = [];

    [ObservableProperty] private string _treeHeadline = "";
    [ObservableProperty] private string? _treeNote;
    [ObservableProperty] private bool _hasTree;

    public override async Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        Cards.Clear(); Layers.Clear(); TreeLines.Clear(); DbLines.Clear();
        if (bank == null) { EmptyText = "Open a database to see its memory."; return; }
        IsLoading = true;
        try
        {
            var (cards, stats, db) = await Task.Run(() =>
            {
                var q = new MemoryQueries(bank.Db);
                var c = q.Counts();
                var s = bank.HasTree ? bank.ReadTree(t => RaptorStats.From(t, bank.TreeMeta), null) : null;
                var vec = q.VectorModel();
                var d = new List<StatLine>
                {
                    new("File", ExplorerPaths.Display(bank.Path)),
                    new("Size", MemoryBank.FormatBytes(bank.SizeBytes) + " (db + wal)"),
                    new("Schema", $"v{bank.Db.SchemaVersion} · FTS5 {(bank.Db.HasFts ? "yes" : "no")} · R*Tree {(bank.Db.HasRtree ? "yes" : "no")}"),
                    new("Vectors", vec is { } v ? $"{v.Count:N0} × {v.Model} @ {v.Dims}" : "none"),
                    new("Search", new SearchService(bank).ModeNote),
                };
                return (c, s, d);
            }, ct);
            foreach (var c in cards) Cards.Add(c);
            foreach (var l in db) DbLines.Add(l);
            HasTree = stats != null;
            TreeNote = bank.TreeNote;
            if (stats != null)
            {
                foreach (var l in stats.Layers) Layers.Add(l);
                foreach (var l in stats.Lines) TreeLines.Add(l);
                TreeHeadline = $"{stats.Nodes:N0} nodes · {stats.Leaves:N0} leaves · {stats.NumLayers} summary layers";
            }
            EmptyText = null;
        }
        finally { IsLoading = false; }
    }
}
