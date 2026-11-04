
namespace MineClaw.ViewerBridge.Graph;

/// <summary>
/// Icicle/partition layout in unit space: each node is the band [X0, X1) on its row (row 0 = the top layer, the last row = leaves),
/// its width proportional to its leaf count and nested inside its primary parent's band. The radial and node-link views reuse it
/// (x becomes an angle, or a node sits at its band's centre). Computed once per model.
/// </summary>
public sealed class PartitionLayout
{
    public required double[] X0 { get; init; }
    public required double[] X1 { get; init; }
    public required int[] Row { get; init; }
    public required int Rows { get; init; }
    /// <summary>Slots of each row sorted by X0 (for culling and hit-testing by binary search).</summary>
    public required int[][] RowSlots { get; init; }
    /// <summary>X0 of <see cref="RowSlots"/>, parallel arrays.</summary>
    public required double[][] RowStarts { get; init; }
    /// <summary>Contiguous runs of leaves under one parent: the leaf strip drawn as one band per run when leaves are hidden (LOD).</summary>
    public required (double X0, double X1, int Owner)[] LeafRuns { get; init; }
    public int TotalLeaves { get; init; }
    public int LeafRow => Rows - 1;

    public static PartitionLayout Compute(GraphModel m)
    {
        int n = m.Nodes.Length;
        var x0 = new double[n];
        var x1 = new double[n];
        var row = new int[n];
        int total = m.TopLevel.Sum(s => m.Nodes[s].Leaves);
        SplitBands(m, total, x0, x1, row);
        int rows = m.NumLayers + 1;
        var bucket = Enumerable.Range(0, rows).Select(_ => new List<int>()).ToArray();
        for (int s = 0; s < n; s++) bucket[row[s]].Add(s);
        var rowSlots = bucket.Select(b => b.OrderBy(s => x0[s]).ThenBy(s => s).ToArray()).ToArray();
        var rowStarts = rowSlots.Select(r => r.Select(s => x0[s]).ToArray()).ToArray();
        return new PartitionLayout
        {
            X0 = x0, X1 = x1, Row = row, Rows = rows, RowSlots = rowSlots, RowStarts = rowStarts, LeafRuns = LeafRunsOf(m, rowSlots[rows - 1], x0, x1),
            TotalLeaves = total,
        };
    }

    /// <summary>Top-down: each node splits its band among its primary children by leaf count.</summary>
    private static void SplitBands(GraphModel m, int total, double[] x0, double[] x1, int[] row)
    {
        double unit = total > 0 ? 1.0 / total : 1;
        var stack = new Stack<(int Slot, double A, double B)>();
        double cursor = 0;
        foreach (var s in m.TopLevel)
        {
            double w = m.Nodes[s].Leaves * unit;
            stack.Push((s, cursor, cursor + w));
            cursor += w;
        }
        while (stack.Count > 0)
        {
            var (s, a, b) = stack.Pop();
            var g = m.Nodes[s];
            x0[s] = a;
            x1[s] = b;
            row[s] = m.NumLayers - g.Layer;
            if (g.Children.Length > 0) PushChildren(m, g.Children, a, b, stack);
        }
    }

    private static void PushChildren(GraphModel m, int[] children, double a, double b, Stack<(int Slot, double A, double B)> stack)
    {
        int sum = children.Sum(c => m.Nodes[c].Leaves);
        double at = a;
        for (int i = 0; i < children.Length; i++)
        {
            int c = children[i];
            double end = i == children.Length - 1 ? b : at + (b - a) * m.Nodes[c].Leaves / sum;
            stack.Push((c, at, end));
            at = end;
        }
    }

    /// <summary>The leaf row's runs: adjacent leaves of one parent merged into one band.</summary>
    private static (double, double, int)[] LeafRunsOf(GraphModel m, int[] leafRow, double[] x0, double[] x1)
    {
        var runs = new List<(double, double, int)>();
        foreach (var s in leafRow)
        {
            int owner = m.Nodes[s].Parent;
            if (runs.Count > 0 && runs[^1].Item3 == owner && Math.Abs(runs[^1].Item2 - x0[s]) < 1e-12)
                runs[^1] = (runs[^1].Item1, x1[s], owner);
            else runs.Add((x0[s], x1[s], owner));
        }
        return runs.ToArray();
    }

    /// <summary>The slot on <paramref name="row"/> whose band contains unit x <paramref name="ux"/>, or -1.</summary>
    public int SlotAt(int row, double ux)
    {
        if (row < 0 || row >= Rows) return -1;
        var starts = RowStarts[row];
        int lo = 0, hi = starts.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (starts[mid] <= ux) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return -1;
        int s = RowSlots[row][found];
        return ux < X1[s] ? s : -1;
    }

    /// <summary>Index range [from, to) of the row's slots whose bands can overlap the unit range [a, b].</summary>
    public (int From, int To) RowRange(int row, double a, double b)
    {
        var starts = RowStarts[row];
        // First band starting after b is the end; bands are disjoint, so the start is the last band starting at or before a.
        int lo = 0, hi = starts.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (starts[mid] <= b) lo = mid + 1; else hi = mid; }
        int to = lo;
        lo = 0; hi = to;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (starts[mid] <= a) lo = mid + 1; else hi = mid; }
        return (Math.Max(0, lo - 1), to);
    }
}

