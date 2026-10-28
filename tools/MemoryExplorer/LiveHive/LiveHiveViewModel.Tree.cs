using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>The supervisor's <c>/api/hive/tree</c>: main at the root, each clone under the hive that owns it.</summary>
public sealed record HiveTreeNodeDto(string Id, string Name, string Kind, string? Brain, string? Owner, string State, string? Task,
    List<HiveTreeNodeDto>? Children);

/// <summary>One flattened, indented row of the Hive tree tab.</summary>
public sealed record HiveTreeRow(int Depth, string Label, string Name, string Kind, string Brain, string State, string? Task)
{
    public Thickness Indent => new(Depth * 22, 0, 0, 0);
    public string Branch => Depth == 0 ? "●" : "└";
    public string Detail => string.IsNullOrWhiteSpace(Task) ? State : $"{State} · {Task}";

    /// <summary>Depth-first rows, owners before their children.</summary>
    public static List<HiveTreeRow> Flatten(HiveTreeNodeDto root)
    {
        var rows = new List<HiveTreeRow>();
        void Walk(HiveTreeNodeDto n, int depth)
        {
            if (depth > 16) return;
            rows.Add(new HiveTreeRow(depth, n.Id, n.Name, n.Kind, n.Brain ?? "", n.State, n.Task));
            foreach (var c in n.Children ?? []) Walk(c, depth + 1);
        }
        Walk(root, 0);
        return rows;
    }
}

public sealed partial class LiveHiveViewModel
{
    private DateTime _treeAt;
    private int _treeBusy;

    public ObservableCollection<HiveTreeRow> TreeRows { get; } = [];
    [ObservableProperty] private string _treeSummary = "Connect to see who owns which clone.";

    /// <summary>Every 3 s while connected (the stats tick calls it): the ownership tree, not a fixed roster.</summary>
    private void MaybePollTree()
    {
        if (_api is not { } api || DateTime.UtcNow - _treeAt < TimeSpan.FromSeconds(3) || Interlocked.Exchange(ref _treeBusy, 1) == 1) return;
        _treeAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                var root = await api.GetJsonAsync<HiveTreeNodeDto>("tree", "api/hive/tree", CancellationToken.None).ConfigureAwait(false);
                var rows = HiveTreeRow.Flatten(root);
                Dispatcher.UIThread.Post(() =>
                {
                    TreeRows.Clear();
                    foreach (var r in rows) TreeRows.Add(r);
                    int clones = rows.Count - 1;
                    TreeSummary = clones == 0 ? "Main alone: no clones out (an empty hive)."
                        : $"{clones} clone(s): {rows.Count(r => r.Kind == "hive")} hive, {rows.Count(r => r.Kind == "subhive")} subhive, {rows.Count(r => r.Kind == "script")} script";
                });
            }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => TreeSummary = "Hive tree unavailable: " + ex.Message); }
            finally { Interlocked.Exchange(ref _treeBusy, 0); }
        });
    }
}
