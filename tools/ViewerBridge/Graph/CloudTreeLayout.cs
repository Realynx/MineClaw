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
        var ids = new List<int>(leaves);
        var slot = new Dictionary<int, int>();
        for (int i = 0; i < ids.Count; i++) slot[ids[i]] = i;
        int nLeaves = ids.Count;
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
        foreach (var s in summaries.OrderBy(s => tree.Layer.GetValueOrDefault(s, 1)).ThenBy(s => s))
        {
            slot[s] = ids.Count;
            ids.Add(s);
        }
        int n = ids.Count;
        var layer = ids.Select((id, i) => i < nLeaves ? 0 : Math.Max(1, tree.Layer.GetValueOrDefault(id, 1))).ToArray();

        // Edges child → parent (weight 1 for the primary parent, 0.35 for the soft extra ones).
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

        // Start: leaves at the MDS spot, summaries at their children's mean (bottom-up; summaries are ordered by layer).
        var x = new double[n];
        var y = new double[n];
        for (int i = 0; i < nLeaves; i++)
        {
            var s = tree.Seed.TryGetValue(ids[i], out var p) ? p : (X: 0.5f, Y: 0.5f);
            x[i] = s.X;
            y[i] = s.Y;
        }
        var acc = new (double X, double Y, int N)[n];
        foreach (var (c, p, _) in edges.Where(e => e.C < nLeaves)) acc[p] = (acc[p].X + x[c], acc[p].Y + y[c], acc[p].N + 1);
        for (int i = nLeaves; i < n; i++)
        {
            (x[i], y[i]) = acc[i].N > 0 ? (acc[i].X / acc[i].N, acc[i].Y / acc[i].N) : (0.5, 0.5);
            foreach (var p in tree.Parents.GetValueOrDefault(ids[i], []))
                if (slot.TryGetValue(p, out var ps) && ps > i) acc[ps] = (acc[ps].X + x[i], acc[ps].Y + y[i], acc[ps].N + 1);
        }
        var sx = (double[])x.Clone();
        var sy = (double[])y.Clone();

        // Lengths in the unit square of the start: a leaf sits ℓ₀ from its parent, each layer up 2.2× farther.
        double l0 = 0.5 / Math.Sqrt(Math.Max(1, n));
        double rRep = 0.9 * l0;
        const double kSpring = 0.5, kRep = 0.6, kAnchor = 0.012;
        var fx = new double[n];
        var fy = new double[n];
        int cells = Math.Clamp((int)Math.Ceiling(1.6 / rRep), 1, 2048);
        var cellOf = new int[n];
        var cellStart = new int[cells * cells + 1];
        var order = new int[n];
        for (int it = 0; it < o.TreeIterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            double step = 1.0 - 0.9 * it / Math.Max(1, o.TreeIterations - 1);
            Array.Clear(fx);
            Array.Clear(fy);
            foreach (var (c, p, w) in edges)
            {
                double dx = x[p] - x[c], dy = y[p] - y[c];
                double d = Math.Sqrt(dx * dx + dy * dy);
                double rest = l0 * Math.Pow(2.2, layer[p] - 1);
                if (d < 1e-12) continue;
                double f = kSpring * w * (d - rest) / d;
                // The child follows; a parent is pulled by the mean of its children.
                fx[c] += f * dx;
                fy[c] += f * dy;
                double pw = 1.0 / Math.Max(1, childCount[p]);
                fx[p] -= f * dx * pw;
                fy[p] -= f * dy * pw;
            }
            // Short-range repulsion on a grid over [-0.3, 1.3]².
            Array.Clear(cellStart);
            for (int i = 0; i < n; i++)
            {
                int cx = Math.Clamp((int)((x[i] + 0.3) / rRep), 0, cells - 1), cy = Math.Clamp((int)((y[i] + 0.3) / rRep), 0, cells - 1);
                cellOf[i] = cy * cells + cx;
                cellStart[cellOf[i] + 1]++;
            }
            for (int c = 0; c < cells * cells; c++) cellStart[c + 1] += cellStart[c];
            var fill = (int[])cellStart.Clone();
            for (int i = 0; i < n; i++) order[fill[cellOf[i]]++] = i;
            for (int i = 0; i < n; i++)
            {
                int cx = cellOf[i] % cells, cy = cellOf[i] / cells;
                for (int gy = Math.Max(0, cy - 1); gy <= Math.Min(cells - 1, cy + 1); gy++)
                    for (int gx = Math.Max(0, cx - 1); gx <= Math.Min(cells - 1, cx + 1); gx++)
                    {
                        int c = gy * cells + gx;
                        for (int q = cellStart[c]; q < cellStart[c + 1]; q++)
                        {
                            int j = order[q];
                            if (j <= i) continue;
                            double dx = x[i] - x[j], dy = y[i] - y[j];
                            double d2 = dx * dx + dy * dy;
                            if (d2 >= rRep * rRep) continue;
                            double d = Math.Sqrt(d2);
                            if (d < 1e-9)
                            {
                                // Coincident: a fixed direction from the pair's indexes.
                                double a = (CloudLayout.Mix((ulong)(uint)i * 31 + (ulong)(uint)j) & 0xFFFF) / 65536.0 * 2 * Math.PI;
                                dx = Math.Cos(a) * 1e-6;
                                dy = Math.Sin(a) * 1e-6;
                                d = 1e-6;
                            }
                            double f = kRep * (rRep - d) / d * 0.5;
                            fx[i] += f * dx;
                            fy[i] += f * dy;
                            fx[j] -= f * dx;
                            fy[j] -= f * dy;
                        }
                    }
            }
            double cap = l0 * step;
            for (int i = 0; i < n; i++)
            {
                double dx = (fx[i] + kAnchor * (sx[i] - x[i])) * step, dy = (fy[i] + kAnchor * (sy[i] - y[i])) * step;
                double m = Math.Sqrt(dx * dx + dy * dy);
                if (m > cap) { dx *= cap / m; dy *= cap / m; }
                x[i] += dx;
                y[i] += dy;
            }
        }
        var outLeaves = new Dictionary<int, (double X, double Y)>(nLeaves);
        var outSummaries = new Dictionary<int, (double X, double Y)>(n - nLeaves);
        for (int i = 0; i < n; i++)
            if (i < nLeaves) outLeaves[ids[i]] = (x[i], y[i]);
            else outSummaries[ids[i]] = (x[i], y[i]);
        return new Result(outLeaves, outSummaries, l0);
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
