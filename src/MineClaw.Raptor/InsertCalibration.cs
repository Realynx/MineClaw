using MineClaw.Raptor.Model;
using MineClaw.Raptor.Numerics;

namespace MineClaw.Raptor;

/// <summary>
/// Calibrates incremental soft attachment against the static build (README D12). Insertion models a leaf's
/// layer-1 membership as <c>r_j = softmax_j(cos(leaf, node_j) / τ)</c> over its top candidates and attaches to every
/// node with r_j &gt; 0.1 (GMM_cluster's threshold). τ is the maximum-likelihood temperature that best explains the
/// memberships the faithful UMAP+GMM build actually produced (Σ over leaves and their parents of log r_parent),
/// so inserts are exactly as "soft" as the build was.
/// </summary>
public static class InsertCalibration
{
    public const double DefaultTemperature = 0.02;

    public static double FitTemperature(RaptorTree tree, int candidates, int maxSamples = 2000)
    {
        var layer1 = tree.LayerIndices(1).Order().ToArray();
        if (layer1.Length < 2) return DefaultTemperature;
        var leaves = tree.Leaves.Where(l => l.Parents.Count > 0).OrderBy(l => l.Index).ToList();
        if (leaves.Count == 0) return DefaultTemperature;
        int step = Math.Max(1, leaves.Count / maxSamples);
        var samples = new List<(double[] Sims, int[] ParentPos)>();
        for (int s = 0; s < leaves.Count; s += step)
        {
            var leaf = leaves[s];
            var all = layer1.Select(i => (Index: i, Sim: (double)VectorMath.Dot(leaf.Embedding, tree[i].Embedding)))
                .OrderByDescending(c => c.Sim).ThenBy(c => c.Index).ToList();
            var cand = all.Take(Math.Max(2, candidates)).ToList();
            foreach (var p in leaf.Parents)
                if (!cand.Any(c => c.Index == p)) cand.Add(all.First(c => c.Index == p));
            var sims = cand.Select(c => c.Sim).ToArray();
            var pos = cand.Select((c, i) => (c, i)).Where(t => leaf.Parents.Contains(t.c.Index)).Select(t => t.i).ToArray();
            samples.Add((sims, pos));
        }

        double LogLik(double logTau)
        {
            double tau = Math.Exp(logTau), ll = 0;
            foreach (var (sims, pos) in samples)
            {
                double max = sims.Max() / tau, z = 0;
                foreach (var v in sims) z += Math.Exp(v / tau - max);
                double lse = max + Math.Log(z);
                foreach (var p in pos) ll += sims[p] / tau - lse;
            }
            return ll;
        }

        // golden-section search on log τ ∈ [ln 1e-4, ln 1]; the objective is unimodal (log-sum-exp is convex in 1/τ)
        double a = Math.Log(1e-4), b = 0, g = (Math.Sqrt(5) - 1) / 2;
        double c1 = b - g * (b - a), c2 = a + g * (b - a);
        double f1 = LogLik(c1), f2 = LogLik(c2);
        for (int it = 0; it < 60; it++)
        {
            if (f1 > f2) { b = c2; c2 = c1; f2 = f1; c1 = b - g * (b - a); f1 = LogLik(c1); }
            else { a = c1; c1 = c2; f1 = f2; c2 = a + g * (b - a); f2 = LogLik(c2); }
        }
        return Math.Exp((a + b) / 2);
    }
}
