namespace MineClaw.ViewerBridge.Graph;

/// <summary>
/// The memory cloud's "Tree (force)" arrangement: a force-directed layout of the RAPTOR tree itself. Nodes are the leaves and every
/// summary above them; springs run along the parent links (the primary parent strongest, longer the higher the parent's layer); a
/// short-range grid repulsion keeps siblings apart; a weak pull toward the start keeps the global picture. It starts from the MDS
/// picture (leaves at their MDS spots, summaries at their children's mean), so it is stable from build to build. Sequential, fixed
/// iteration count and order: deterministic.
/// </summary>
public static class CloudTreeLayout
{
    public sealed record Result(Dictionary<int, (double X, double Y)> Leaves, Dictionary<int, (double X, double Y)> Summaries, double LeafLength);

    public static Result Fit(IReadOnlyList<int> leaves, CloudTreeInput tree, CloudLayoutOptions o, CancellationToken ct = default)
    {
        // Nodes: the leaves, then every summary reachable from them (by layer, then index).
        var (ids, slot) = Nodes(leaves, tree);
        int nLeaves = leaves.Count;
        int n = ids.Count;
        var layer = ids.Select((id, i) => i < nLeaves ? 0 : Math.Max(1, tree.Layer.GetValueOrDefault(id, 1))).ToArray();
        var (edges, childCount) = Edges(ids, slot, tree);

        // Start: leaves at the MDS spot, summaries at their children's mean (bottom-up; summaries are ordered by layer).
        var x = new double[n];
        var y = new double[n];
        SeedLeaves(ids, nLeaves, tree, x, y);
        MeanSummaries(ids, slot, nLeaves, edges, tree, x, y);

        // Lengths in the unit square of the start: a leaf sits ℓ₀ from its parent, each layer up 2.2× farther.
        double l0 = 0.5 / Math.Sqrt(Math.Max(1, n));
        var forces = new Forces(x, y, layer, edges, childCount, l0);
        for (int it = 0; it < o.TreeIterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            forces.Iterate(1.0 - 0.9 * it / Math.Max(1, o.TreeIterations - 1));
        }
        var outLeaves = new Dictionary<int, (double X, double Y)>(nLeaves);
        var outSummaries = new Dictionary<int, (double X, double Y)>(n - nLeaves);
        for (int i = 0; i < n; i++)
            if (i < nLeaves) outLeaves[ids[i]] = (x[i], y[i]);
            else outSummaries[ids[i]] = (x[i], y[i]);
        return new Result(outLeaves, outSummaries, l0);
    }

    /// <summary>The node list (the leaves, then the reachable summaries by layer, then index) and each id's slot in it.</summary>
    private static (List<int> Ids, Dictionary<int, int> Slot) Nodes(IReadOnlyList<int> leaves, CloudTreeInput tree)
    {
        var ids = new List<int>(leaves);
        var slot = new Dictionary<int, int>();
        for (int i = 0; i < ids.Count; i++) slot[ids[i]] = i;
        foreach (var s in ReachableSummaries(leaves, tree).OrderBy(s => tree.Layer.GetValueOrDefault(s, 1)).ThenBy(s => s))
        {
            slot[s] = ids.Count;
            ids.Add(s);
        }
        return (ids, slot);
    }

    /// <summary>Every summary above the leaves, walking the parent links layer by layer.</summary>
    private static HashSet<int> ReachableSummaries(IReadOnlyList<int> leaves, CloudTreeInput tree)
    {
        var frontier = new SortedSet<int>();
        foreach (var l in leaves) foreach (var p in tree.Parents.GetValueOrDefault(l, [])) frontier.Add(p);
        var summaries = new HashSet<int>();
        while (frontier.Count > 0)
        {
            var next = new SortedSet<int>();
            foreach (var s in frontier)
            {
                if (!summaries.Add(s)) continue;
                foreach (var p in tree.Parents.GetValueOrDefault(s, [])) if (!summaries.Contains(p)) next.Add(p);
            }
            frontier = next;
        }
        return summaries;
    }

    /// <summary>Edges child → parent (weight 1 for the primary parent, 0.35 for the soft extra ones) and each node's child count.</summary>
    private static (List<(int C, int P, double W)> Edges, int[] ChildCount) Edges(List<int> ids, Dictionary<int, int> slot, CloudTreeInput tree)
    {
        int n = ids.Count;
        var edges = new List<(int C, int P, double W)>();
        var childCount = new int[n];
        for (int i = 0; i < n; i++)
        {
            var ps = tree.Parents.GetValueOrDefault(ids[i], []);
            for (int k = 0; k < ps.Length; k++)
                if (slot.TryGetValue(ps[k], out var p) && p != i)
                {
                    edges.Add((i, p, k == 0 ? 1 : 0.35));
                    childCount[p]++;
                }
        }
        return (edges, childCount);
    }

    private static void SeedLeaves(List<int> ids, int nLeaves, CloudTreeInput tree, double[] x, double[] y)
    {
        for (int i = 0; i < nLeaves; i++)
        {
            var s = tree.Seed.TryGetValue(ids[i], out var p) ? p : (X: 0.5f, Y: 0.5f);
            x[i] = s.X;
            y[i] = s.Y;
        }
    }

    /// <summary>Each summary at its children's mean, bottom-up (a summary with none at the centre), handing itself on to the parents above it.</summary>
    private static void MeanSummaries(List<int> ids, Dictionary<int, int> slot, int nLeaves, List<(int C, int P, double W)> edges, CloudTreeInput tree,
        double[] x, double[] y)
    {
        int n = ids.Count;
        var acc = new (double X, double Y, int N)[n];
        foreach (var (c, p, _) in edges.Where(e => e.C < nLeaves)) acc[p] = (acc[p].X + x[c], acc[p].Y + y[c], acc[p].N + 1);
        for (int i = nLeaves; i < n; i++)
        {
            (x[i], y[i]) = acc[i].N > 0 ? (acc[i].X / acc[i].N, acc[i].Y / acc[i].N) : (0.5, 0.5);
            foreach (var p in tree.Parents.GetValueOrDefault(ids[i], []))
                if (slot.TryGetValue(p, out var ps) && ps > i) acc[ps] = (acc[ps].X + x[i], acc[ps].Y + y[i], acc[ps].N + 1);
        }
    }

    /// <summary>
    /// The force iterations over the node positions (moved in place): springs along the parent links, a short-range grid repulsion
    /// over [-0.3, 1.3]², a weak pull toward the start, and a step cap. Its buffers are allocated once.
    /// </summary>
    private sealed class Forces
    {
        private const double KSpring = 0.5, KRep = 0.6, KAnchor = 0.012;
        private readonly double[] _x, _y, _sx, _sy, _fx, _fy;
        private readonly int[] _layer, _childCount, _cellOf, _cellStart, _order;
        private readonly List<(int C, int P, double W)> _edges;
        private readonly double _l0, _rRep;
        private readonly int _n, _cells;

        public Forces(double[] x, double[] y, int[] layer, List<(int C, int P, double W)> edges, int[] childCount, double l0)
        {
            (_x, _y, _layer, _edges, _childCount, _l0) = (x, y, layer, edges, childCount, l0);
            _n = x.Length;
            _sx = (double[])x.Clone();
            _sy = (double[])y.Clone();
            _rRep = 0.9 * l0;
            _fx = new double[_n];
            _fy = new double[_n];
            _cells = Math.Clamp((int)Math.Ceiling(1.6 / _rRep), 1, 2048);
            _cellOf = new int[_n];
            _cellStart = new int[_cells * _cells + 1];
            _order = new int[_n];
        }

        public void Iterate(double step)
        {
            Array.Clear(_fx);
            Array.Clear(_fy);
            Springs();
            // Short-range repulsion on a grid over [-0.3, 1.3]².
            BuildGrid();
            for (int i = 0; i < _n; i++) RepelAround(i);
            Move(step);
        }

        private void Springs()
        {
            double[] x = _x, y = _y, fx = _fx, fy = _fy;
            foreach (var (c, p, w) in _edges)
            {
                double dx = x[p] - x[c], dy = y[p] - y[c];
                double d = Math.Sqrt(dx * dx + dy * dy);
                double rest = _l0 * Math.Pow(2.2, _layer[p] - 1);
                if (d < 1e-12) continue;
                double f = KSpring * w * (d - rest) / d;
                // The child follows; a parent is pulled by the mean of its children.
                fx[c] += f * dx;
                fy[c] += f * dy;
                double pw = 1.0 / Math.Max(1, _childCount[p]);
                fx[p] -= f * dx * pw;
                fy[p] -= f * dy * pw;
            }
        }

        /// <summary>Buckets every node into its grid cell (a counting sort: <c>_order</c> by cell, <c>_cellStart</c> the offsets).</summary>
        private void BuildGrid()
        {
            int cells = _cells;
            Array.Clear(_cellStart);
            for (int i = 0; i < _n; i++)
            {
                int cx = Math.Clamp((int)((_x[i] + 0.3) / _rRep), 0, cells - 1), cy = Math.Clamp((int)((_y[i] + 0.3) / _rRep), 0, cells - 1);
                _cellOf[i] = cy * cells + cx;
                _cellStart[_cellOf[i] + 1]++;
            }
            for (int c = 0; c < cells * cells; c++) _cellStart[c + 1] += _cellStart[c];
            var fill = (int[])_cellStart.Clone();
            for (int i = 0; i < _n; i++) _order[fill[_cellOf[i]]++] = i;
        }

        /// <summary>Node <paramref name="i"/> against every later node in its cell and the 8 around it.</summary>
        private void RepelAround(int i)
        {
            int cells = _cells;
            int cx = _cellOf[i] % cells, cy = _cellOf[i] / cells;
            for (int gy = Math.Max(0, cy - 1); gy <= Math.Min(cells - 1, cy + 1); gy++)
                for (int gx = Math.Max(0, cx - 1); gx <= Math.Min(cells - 1, cx + 1); gx++)
                {
                    int c = gy * cells + gx;
                    for (int q = _cellStart[c]; q < _cellStart[c + 1]; q++)
                    {
                        int j = _order[q];
                        if (j > i) Repel(i, j);
                    }
                }
        }

        private void Repel(int i, int j)
        {
            double rRep = _rRep;
            double dx = _x[i] - _x[j], dy = _y[i] - _y[j];
            double d2 = dx * dx + dy * dy;
            if (d2 >= rRep * rRep) return;
            double d = Math.Sqrt(d2);
            if (d < 1e-9)
            {
                // Coincident: a fixed direction from the pair's indexes.
                double a = (CloudLayout.Mix((ulong)(uint)i * 31 + (ulong)(uint)j) & 0xFFFF) / 65536.0 * 2 * Math.PI;
                dx = Math.Cos(a) * 1e-6;
                dy = Math.Sin(a) * 1e-6;
                d = 1e-6;
            }
            double f = KRep * (rRep - d) / d * 0.5;
            _fx[i] += f * dx;
            _fy[i] += f * dy;
            _fx[j] -= f * dx;
            _fy[j] -= f * dy;
        }

        private void Move(double step)
        {
            double cap = _l0 * step;
            for (int i = 0; i < _n; i++)
            {
                double dx = (_fx[i] + KAnchor * (_sx[i] - _x[i])) * step, dy = (_fy[i] + KAnchor * (_sy[i] - _y[i])) * step;
                double m = Math.Sqrt(dx * dx + dy * dy);
                if (m > cap) { dx *= cap / m; dy *= cap / m; }
                _x[i] += dx;
                _y[i] += dy;
            }
        }
    }

    /// <summary>A memory inserted since the build: next to its parent summaries (the primary one weighted most), at the leaf spring length in a direction from its id; with no laid-out parent, at its MDS spot.</summary>
    public static (float X, float Y) Place(CloudAnchors a, int index, CloudTreeInput tree)
    {
        double wx = 0, wy = 0, ws = 0;
        var ps = tree.Parents.GetValueOrDefault(index, []);
        for (int k = 0; k < ps.Length; k++)
            if (a.SummaryXy != null && a.SummaryXy.TryGetValue(ps[k], out var p))
            {
                double w = k == 0 ? 1 : 0.35;
                wx += w * p.X;
                wy += w * p.Y;
                ws += w;
            }
        if (ws > 0)
        {
            ulong h = CloudLayout.Mix((ulong)(uint)index * 0x9E3779B97F4A7C15UL);
            double angle = (h & 0xFFFF) / 65536.0 * 2 * Math.PI;
            double r = a.LeafRadius * (0.6 + 0.6 * ((h >> 16 & 0xFFFF) / 65536.0));
            return CloudLayout.Clamp(wx / ws + r * Math.Cos(angle), wy / ws + r * Math.Sin(angle));
        }
        var s = tree.Seed.TryGetValue(index, out var sp) ? sp : (X: 0.5f, Y: 0.5f);
        return CloudLayout.Clamp(s.X * a.Transform.S + a.Transform.Ox, s.Y * a.Transform.S + a.Transform.Oy);
    }
}
