using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Agent.Admin.Shards;
using MineClaw.MemoryExplorer.Controls;
using MineClaw.MemoryExplorer.Services;
using MineClaw.Raptor.Model;

namespace MineClaw.MemoryExplorer.ViewModels;

/// <summary>One node of the lazily expanded RAPTOR tree view.</summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    private readonly RaptorTreeViewModel? _owner;
    private bool _loaded;

    private TreeNodeViewModel() { Title = "loading…"; Preview = ""; IsPlaceholder = true; }

    public TreeNodeViewModel(RaptorTreeViewModel owner, RaptorNode n, NodeDoc? doc)
    {
        _owner = owner;
        Index = n.Index;
        Layer = n.Layer;
        ChildCount = n.Children.Count;
        ParentCount = n.Parents.Count;
        Tokens = n.TokenCount;
        IsLeaf = n.IsLeaf;
        Dirty = n.Dirty || n.SummaryPending;
        var one = n.Text.ReplaceLineEndings(" ");
        Preview = one.Length > 140 ? one[..140] + "…" : one;
        Title = IsLeaf ? (doc != null ? $"{doc.Kind} · {Trim(doc.Title, 70)}" : $"leaf #{n.Index}") : Trim(one, 90);
        Kind = IsLeaf ? doc?.Kind ?? n.Kind ?? "leaf" : $"L{n.Layer}";
        if (ChildCount > 0) Children.Add(new TreeNodeViewModel());
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    public bool IsPlaceholder { get; }
    public int Index { get; }
    public int Layer { get; }
    public int ChildCount { get; }
    public int ParentCount { get; }
    public int Tokens { get; }
    public bool IsLeaf { get; }
    public bool Dirty { get; }
    public string Title { get; }
    public string Preview { get; }
    public string Kind { get; }
    public string Meta => IsPlaceholder ? "" : IsLeaf ? $"#{Index} · {Tokens} tok" : $"#{Index} · {ChildCount} children · {Tokens} tok";
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Right-click "Export this subtree as a shard…".</summary>
    [RelayCommand]
    private Task Export() => _owner?.ExportSubtreeCommand.ExecuteAsync(Index) ?? Task.CompletedTask;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) EnsureChildren();
    }

    public void EnsureChildren()
    {
        if (_loaded || _owner == null) return;
        _loaded = true;
        var kids = _owner.ChildrenOf(Index);
        Children.Clear();
        foreach (var k in kids) Children.Add(k);
    }
}

public sealed record Crumb(int Index, string Label);
public sealed record NodeRow(int Index, int Layer, string Label, string Preview, int Tokens);

public sealed partial class RaptorTreeViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];
    public ObservableCollection<Crumb> Breadcrumb { get; } = [];
    public ObservableCollection<NodeRow> DetailChildren { get; } = [];
    public ObservableCollection<NodeRow> DetailParents { get; } = [];
    public ObservableCollection<NodeRow> Leaves { get; } = [];

    [ObservableProperty] private TreeNodeViewModel? _selected;
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _detailMeta = "";
    [ObservableProperty] private string? _detailDoc;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _showLeaves;
    [ObservableProperty] private string _leavesHeader = "";
    [ObservableProperty] private bool _graphMode;
    [ObservableProperty] private GraphModel? _graph;
    [ObservableProperty] private int _graphSelected = -1;
    [ObservableProperty] private string _goTo = "";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private GraphLayoutKind _graphLayout = GraphLayoutKind.Icicle;
    [ObservableProperty] private int _colorByIndex;
    [ObservableProperty] private int _graphFocus = -1;
    [ObservableProperty] private IReadOnlyCollection<int>? _graphHits;
    [ObservableProperty] private string _graphHitsInfo = "";

    /// <summary>Focus breadcrumb above the graph: "all memory › L3 … › L2 …" (the focused subtree's path).</summary>
    public ObservableCollection<Crumb> FocusCrumbs { get; } = [];
    public IReadOnlyList<string> ColorOptions { get; } = ["colour: kind", "colour: time", "colour: cluster"];
    public GraphColorBy GraphColorBy => (GraphColorBy)Math.Clamp(ColorByIndex, 0, 2);
    public bool IsIcicle => GraphLayout == GraphLayoutKind.Icicle;
    public bool IsRadial => GraphLayout == GraphLayoutKind.Radial;
    public bool IsNodeLink => GraphLayout == GraphLayoutKind.NodeLink;
    public bool HasGraphHits => GraphHits is { Count: > 0 };

    public bool TreeMode => !GraphMode;
    partial void OnGraphModeChanged(bool value) => OnPropertyChanged(nameof(TreeMode));
    partial void OnColorByIndexChanged(int value) => OnPropertyChanged(nameof(GraphColorBy));
    partial void OnGraphHitsChanged(IReadOnlyCollection<int>? value) => OnPropertyChanged(nameof(HasGraphHits));

    partial void OnGraphLayoutChanged(GraphLayoutKind value)
    {
        OnPropertyChanged(nameof(IsIcicle));
        OnPropertyChanged(nameof(IsRadial));
        OnPropertyChanged(nameof(IsNodeLink));
    }

    partial void OnGraphFocusChanged(int value) => RefreshFocusCrumbs(value);

    private void RefreshFocusCrumbs(int value)
    {
        FocusCrumbs.Clear();
        FocusCrumbs.Add(new Crumb(-1, "all memory"));
        if (value < 0 || Bank is not { } bank || Graph is not { } g) return;
        foreach (var p in GraphPath(value))
            if (g.TryGetSlot(p, out var s))
            {
                var n = g.Nodes[s];
                FocusCrumbs.Add(new Crumb(p, n.IsLeaf ? $"leaf #{p}" : n.Terms.Length > 0 ? $"L{n.Layer} · {n.Terms[0]}" : $"L{n.Layer} #{p}"));
            }
    }

    [RelayCommand]
    private void SetLayout(string kind) => GraphLayout = kind switch
    {
        "radial" => GraphLayoutKind.Radial,
        "nodelink" => GraphLayoutKind.NodeLink,
        _ => GraphLayoutKind.Icicle,
    };

    [RelayCommand]
    private void FocusOn(int index) => GraphFocus = index;

    [RelayCommand]
    private void ClearHits()
    {
        GraphHits = null;
        GraphHitsInfo = "";
    }

    /// <summary>Search results to light up in the graph (doc hits mapped to tree nodes, plus the tree's own hits).</summary>
    public void SetSearchHits(IEnumerable<int> nodeIndexes, string query)
    {
        var set = nodeIndexes.Where(i => i >= 0).ToHashSet();
        GraphHits = set.Count > 0 ? set : null;
        GraphHitsInfo = set.Count > 0 ? $"{set.Count} hits for \"{query}\"" : "";
    }

    /// <summary>Opens the graph on a node: selected, with its neighbourhood in focus (a leaf: its grandparent, so the leaves show).</summary>
    public void ShowInGraph(int index)
    {
        GraphMode = true;
        Reveal(index);
        if (Bank is not { } bank) return;
        var path = GraphPath(index);
        bool leaf = bank.ReadTree(t => t.TryGet(index, out var n) && n.IsLeaf, false);
        int up = leaf ? 2 : 1;
        GraphFocus = path.Count > up ? path[^(up + 1)] : path.Count > 1 ? path[0] : -1;
    }

    public override async Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        Roots.Clear();
        Clear();
        Graph = null;
        GraphFocus = -1;
        if (bank == null || !bank.HasTree)
        {
            EmptyText = bank == null ? "Open a database to browse its RAPTOR tree." : $"No tree: {bank.TreeNote}.";
            Headline = "";
            return;
        }
        IsLoading = true;
        try
        {
            var (roots, graph, head) = await Task.Run(() => bank.ReadTree(t =>
            {
                var r = t.Roots.OrderBy(n => n.Index).Select(n => new TreeNodeViewModel(this, n, bank.DocOf(n.Index))).ToList();
                var top = t.NumLayers;
                var g = GraphModel.Build(t, bank.DocOf);
                LabelCached(g, $"{bank.Path}|{t.BuildId}|{t.Count}|{t.InsertSeq}|{t.NextIndex}");
                return (r, g, $"{t.Count:N0} nodes in {top + 1} layers · {r.Count} roots · {t.LeafCount:N0} leaves");
            }, (new List<TreeNodeViewModel>(), (GraphModel?)null, "")), ct);
            foreach (var r in roots) Roots.Add(r);
            Graph = graph;
            RefreshFocusCrumbs(GraphFocus);
            Headline = head;
            EmptyText = null;
        }
        finally { IsLoading = false; }
    }

    /// <summary>Root → node path the graph draws (its partition parents), so the detail breadcrumb, the focus breadcrumb and the
    /// highlighted path agree; the bank's own path when there is no graph.</summary>
    public IReadOnlyList<int> GraphPath(int index) =>
        Graph is { } g && g.TryGetSlot(index, out var s) ? g.PathToTop(s).Select(x => g.Nodes[x].Index).ToList()
        : Bank is { } bank ? bank.PathToRoot(index) : [];

    private static readonly Dictionary<string, string[][]> LabelCache = new();

    /// <summary>TF-IDF topic labels, cached per tree state (reloading the same db reuses them).</summary>
    private static void LabelCached(GraphModel g, string key)
    {
        string[][]? terms;
        lock (LabelCache)
            if (!LabelCache.TryGetValue(key, out terms) || terms.Length != g.Nodes.Length) terms = null;
        if (terms == null)
        {
            terms = TopicLabeler.Compute(g);
            lock (LabelCache)
            {
                if (LabelCache.Count > 8) LabelCache.Clear();
                LabelCache[key] = terms;
            }
        }
        for (int s = 0; s < g.Nodes.Length; s++) g.Nodes[s].Terms = terms[s];
    }

    internal List<TreeNodeViewModel> ChildrenOf(int index)
    {
        if (Bank is not { } bank) return [];
        return bank.ReadTree(t => t.TryGet(index, out var n)
            ? n.Children.Select(c => t[c]).OrderByDescending(c => c.Children.Count).ThenBy(c => c.Index)
                .Select(c => new TreeNodeViewModel(this, c, bank.DocOf(c.Index))).ToList()
            : [], []);
    }

    private void Clear()
    {
        Breadcrumb.Clear(); DetailChildren.Clear(); DetailParents.Clear(); Leaves.Clear();
        DetailTitle = DetailText = DetailMeta = "";
        DetailDoc = null;
        HasSelection = false;
        ShowLeaves = false;
    }

    partial void OnSelectedChanged(TreeNodeViewModel? value)
    {
        if (value is { IsPlaceholder: false }) ShowNode(value.Index);
    }

    partial void OnGraphSelectedChanged(int value)
    {
        if (value >= 0 && Selected?.Index != value) ShowNode(value);
    }

    /// <summary>Fills the detail pane for a node.</summary>
    public void ShowNode(int index)
    {
        if (Bank is not { } bank) return;
        var data = ReadDetail(bank, index);
        if (data == null) return;
        Clear();
        HasSelection = true;
        GraphSelected = index;
        var n = data.Node;
        var doc = bank.DocOf(index);
        DetailTitle = n.IsLeaf ? doc?.Title ?? $"Leaf #{index}" : $"Layer {n.Layer} summary #{index}";
        DetailText = data.Text;
        DetailMeta = DetailMetaOf(data);
        if (doc != null) DetailDoc = DocLine(doc);
        FillBreadcrumb(bank, index);
        foreach (var c in data.Kids.OrderByDescending(k => k.Tokens)) DetailChildren.Add(c);
        foreach (var p in data.Parents) DetailParents.Add(p);
        LeavesHeader = n.IsLeaf ? "" : $"{data.LeafCount:N0} leaves under this node";
    }

    /// <summary>A node with its children and parents as rows and the leaves below it, read under the tree lock.</summary>
    private sealed record NodeDetail(RaptorNode Node, string Text, List<NodeRow> Kids, List<NodeRow> Parents, int LeafCount);

    private static NodeDetail? ReadDetail(MemoryBank bank, int index) => bank.ReadTree(t =>
    {
        if (!t.TryGet(index, out var n)) return null;
        NodeRow Row(RaptorNode c) => new(c.Index, c.Layer, c.IsLeaf ? bank.DocOf(c.Index)?.Kind ?? "leaf" : $"L{c.Layer}",
            One(c.Text, 150), c.TokenCount);
        return new NodeDetail(n, n.Text, n.Children.Select(c => Row(t[c])).ToList(), n.Parents.Select(p => Row(t[p])).ToList(),
            n.IsLeaf ? 1 : CountLeaves(t, n));
    }, (NodeDetail?)null);

    private static string DetailMetaOf(NodeDetail data)
    {
        var n = data.Node;
        return n.IsLeaf
            ? $"leaf · {n.TokenCount} tokens · {data.Parents.Count} parent(s)" + (n.Pinned ? " · pinned" : "")
            : $"layer {n.Layer} · {n.Children.Count} children · {data.LeafCount:N0} leaves below · {n.TokenCount} tokens" +
              (n.Dirty ? " · dirty" : "") + (n.SummaryPending ? " · placeholder summary" : "");
    }

    private static string DocLine(NodeDoc doc) =>
        $"{doc.Ref} · {doc.Kind}{(doc.Tag != null ? "/" + doc.Tag : "")} · {doc.Source ?? "—"} · updated {doc.Updated.ToLocalTime():yyyy-MM-dd HH:mm}";

    private void FillBreadcrumb(MemoryBank bank, int index)
    {
        foreach (var p in GraphPath(index))
            Breadcrumb.Add(new Crumb(p, p == index ? $"#{p}" : bank.ReadTree(t => t.TryGet(p, out var pn) ? $"L{pn.Layer} #{p}" : $"#{p}", $"#{p}")));
    }

    private static int CountLeaves(RaptorTree t, RaptorNode n)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(n.Index);
        int leaves = 0;
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (!seen.Add(i) || !t.TryGet(i, out var x)) continue;
            if (x.IsLeaf) leaves++;
            else foreach (var c in x.Children) stack.Push(c);
        }
        return leaves;
    }

    private static string One(string s, int max)
    {
        var t = s.ReplaceLineEndings(" ");
        return t.Length > max ? t[..max] + "…" : t;
    }

    /// <summary>Every leaf chunk below the selected node (deduplicated across soft-cluster parents).</summary>
    [RelayCommand]
    private void ShowLeavesUnder()
    {
        if (Bank is not { } bank || !HasSelection || GraphSelected < 0) return;
        int index = GraphSelected;
        var rows = bank.ReadTree(t =>
        {
            var list = new List<NodeRow>();
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(index);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                if (!seen.Add(i) || !t.TryGet(i, out var x)) continue;
                if (x.IsLeaf)
                {
                    var d = bank.DocOf(i);
                    list.Add(new NodeRow(i, 0, d != null ? $"{d.Kind} · {d.Title}" : "leaf", One(x.Text, 220), x.TokenCount));
                }
                else foreach (var c in x.Children) stack.Push(c);
            }
            return list.OrderBy(r => r.Index).ToList();
        }, []);
        Leaves.Clear();
        foreach (var r in rows) Leaves.Add(r);
        LeavesHeader = $"{rows.Count:N0} leaves under #{index} · {rows.Sum(r => r.Tokens):N0} tokens";
        ShowLeaves = true;
    }

    [RelayCommand]
    private void HideLeaves() => ShowLeaves = false;

    /// <summary>Selects a node in the tree, expanding its path (lazy children load on the way).</summary>
    [RelayCommand]
    public void Reveal(int index)
    {
        if (Bank is not { } bank) return;
        var path = bank.PathToRoot(index);
        if (path.Count == 0) return;
        var cur = ExpandPath(path, index);
        if (cur != null && cur.Index == index) Selected = cur;
        else ShowNode(index);
        // Breadcrumb sync: picking an ancestor of the focused subtree in the detail pane zooms the graph out to it.
        if (GraphMode && GraphFocus >= 0 && index != GraphFocus && GraphPath(GraphFocus).Contains(index)) GraphFocus = index;
    }

    /// <summary>Expands the tree along <paramref name="path"/> down to <paramref name="index"/>; the last node found on the way.</summary>
    private TreeNodeViewModel? ExpandPath(IEnumerable<int> path, int index)
    {
        IEnumerable<TreeNodeViewModel> level = Roots;
        TreeNodeViewModel? cur = null;
        foreach (var p in path)
        {
            cur = level.FirstOrDefault(x => x.Index == p);
            if (cur == null) break;
            if (p == index) continue;
            cur.IsExpanded = true;
            cur.EnsureChildren();
            level = cur.Children;
        }
        return cur;
    }

    [RelayCommand]
    private void GoToNode()
    {
        if (int.TryParse(GoTo.Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) Reveal(i);
    }

    [RelayCommand]
    private void ToggleGraph() => GraphMode = !GraphMode;

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var r in Roots) Collapse(r);
        static void Collapse(TreeNodeViewModel n)
        {
            n.IsExpanded = false;
            foreach (var c in n.Children) if (!c.IsPlaceholder) Collapse(c);
        }
    }

    [RelayCommand]
    private Task CopyNodeText() => Shell.CopyAsync(DetailText);

    [RelayCommand]
    private Task CopyLeaves() => Shell.CopyAsync(string.Join("\n", Leaves.Select(l => $"#{l.Index}\t{l.Label}\t{l.Preview}")));

    /// <summary>
    /// "Export shard…" on a node (the parameter) or the selected one: every leaf under it, mapped back to its memory record. The
    /// summaries wholly inside travel as a cache; the import rebuilds the tree from the leaves.
    /// </summary>
    [RelayCommand]
    private Task ExportSubtree(object? parameter)
    {
        int node = parameter is int i ? i : GraphSelected;
        if (Bank is not { } bank || node < 0) { Shell.Status = "Select a node to export its subtree"; return Task.CompletedTask; }
        var info = bank.ReadTree(t => t.TryGet(node, out var n)
            ? (Refs: ShardCollector.LeafRefsUnder(t, node), Layer: n.Layer, Leaf: n.IsLeaf)
            : (Refs: new List<string>(), Layer: -1, Leaf: false), (Refs: new List<string>(), Layer: -1, Leaf: false));
        if (info.Layer < 0) { Shell.Status = $"No node #{node}"; return Task.CompletedTask; }
        var topic = SubtreeTopic(bank, node, info.Leaf);
        var label = $"#{node} · {(info.Leaf ? "leaf" : $"layer {info.Layer}")} · {topic} ({info.Refs.Count:N0} leaves)";
        return Shell.Shards.ExportAsync(new ShardSelection { Mode = "subtree", Node = node, LeafRefs = info.Refs }, $"subtree {topic}", label);
    }

    /// <summary>The node's top topic terms (the graph's labels), else a leaf's title, else "subtree".</summary>
    private string SubtreeTopic(MemoryBank bank, int node, bool leaf)
    {
        string[] terms = Graph is { } g && g.TryGetSlot(node, out var slot) ? g.Nodes[slot].Terms : [];
        return terms.Length > 0 ? string.Join(" ", terms.Take(3)) : leaf ? bank.DocOf(node)?.Title ?? "leaf" : "subtree";
    }

    /// <summary>Opens the rebuild flow for the selected node (edit mode).</summary>
    [RelayCommand]
    private Task RebuildNode() => GraphSelected >= 0 ? Shell.Edit.StartRebuildAsync(GraphSelected) : Task.CompletedTask;
}
