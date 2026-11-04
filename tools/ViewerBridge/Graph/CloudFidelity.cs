using MineClaw.Raptor.Numerics;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>
/// How faithfully an arrangement draws the embedding distances, measured the same way for every arrangement on one seeded pair sample:
/// <c>pearson</c> / <c>spearman</c> between cosine distance (1 − cos) and the drawn distance; <c>stress</c> = √(Σ(δ − αd)² / Σδ²) after the
/// best uniform scale α (0 = perfect; it never credits an arrangement for its own scale); <c>unit_distance</c> = α, the cosine distance
/// one unit of the unit square stands for (the scale bar and the hover's "drawn" number). <c>variance_explained</c>: PCA only, per axis.
/// </summary>
public sealed record CloudFidelityDto(string Metric, double Stress, double Pearson, double Spearman, double UnitDistance, int Pairs, double[]? VarianceExplained);

public static class CloudFidelity
{
    public const int DefaultPairs = 20_000;

    public static CloudFidelityDto Measure(IReadOnlyList<CloudLeaf> leaves, IReadOnlyDictionary<int, (float X, float Y)> positions, long seed,
        int maxPairs = DefaultPairs, double[]? varianceExplained = null)
    {
        int dims = CloudLayout.Dimension(leaves);
        var pts = leaves.Where(l => l.Vector.Length == dims && dims > 0 && positions.ContainsKey(l.Index)).OrderBy(l => l.Index).ToList();
        int n = pts.Count;
        if (n < 3) return new CloudFidelityDto("cosine", 0, 1, 1, 1, 0, varianceExplained);
        var vecs = new float[n][];
        var xy = new (double X, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            vecs[i] = VectorMath.Normalized(pts[i].Vector);
            xy[i] = positions[pts[i].Index];
        }
        var pairs = Pairs(n, maxPairs, seed);
        int m = pairs.Count;
        var delta = new double[m];
        var drawn = new double[m];
        double sdd = 0, sd2 = 0, sdl2 = 0;
        for (int k = 0; k < m; k++)
        {
            var (a, b) = pairs[k];
            delta[k] = Math.Max(0, 1 - VectorMath.Dot(vecs[a], vecs[b]));
            double dx = xy[a].X - xy[b].X, dy = xy[a].Y - xy[b].Y;
            drawn[k] = Math.Sqrt(dx * dx + dy * dy);
            sdd += delta[k] * drawn[k];
            sd2 += drawn[k] * drawn[k];
            sdl2 += delta[k] * delta[k];
        }
        double alpha = sd2 > 0 ? sdd / sd2 : 0;
        double res = 0;
        for (int k = 0; k < m; k++) res += Math.Pow(delta[k] - alpha * drawn[k], 2);
        double stress = sdl2 > 0 ? Math.Sqrt(res / sdl2) : 0;
        return new CloudFidelityDto("cosine", R(stress), R(Pearson(delta, drawn)), R(Pearson(Ranks(delta), Ranks(drawn))), Math.Round(alpha, 6), m, varianceExplained);
    }

    /// <summary>Every pair when they fit in <paramref name="maxPairs"/>, else that many seeded random pairs (a ≠ b).</summary>
    private static List<(int A, int B)> Pairs(int n, int maxPairs, long seed)
    {
        long all = (long)n * (n - 1) / 2;
        var pairs = new List<(int A, int B)>((int)Math.Min(all, maxPairs));
        if (all <= maxPairs)
        {
            for (int a = 0; a < n; a++) for (int b = a + 1; b < n; b++) pairs.Add((a, b));
            return pairs;
        }
        var rng = new Rng(seed);
        while (pairs.Count < maxPairs)
        {
            int a = rng.NextInt(n), b = rng.NextInt(n);
            if (a != b) pairs.Add((a, b));
        }
        return pairs;
    }

    public static double Pearson(double[] a, double[] b)
    {
        int n = a.Length;
        if (n < 2) return 0;
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db;
            saa += da * da;
            sbb += db * db;
        }
        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
    }

    /// <summary>Fractional ranks (ties share their mean rank).</summary>
    public static double[] Ranks(double[] v)
    {
        var order = Enumerable.Range(0, v.Length).OrderBy(i => v[i]).ThenBy(i => i).ToArray();
        var r = new double[v.Length];
        for (int i = 0; i < order.Length;)
        {
            int j = i;
            while (j + 1 < order.Length && v[order[j + 1]] == v[order[i]]) j++;
            double rank = (i + j) / 2.0;
            for (int k = i; k <= j; k++) r[order[k]] = rank;
            i = j + 1;
        }
        return r;
    }

    /// <summary>Cosine distance between two stored vectors (unit-normalised here), or null when either is missing or they differ in length.</summary>
    public static double? CosineDistance(float[]? a, float[]? b)
    {
        if (a is not { Length: > 0 } || b is null || a.Length != b.Length) return null;
        return Math.Max(0, 1 - VectorMath.Cosine(a, b));
    }

    private static double R(double v) => Math.Round(v, 4);
}
