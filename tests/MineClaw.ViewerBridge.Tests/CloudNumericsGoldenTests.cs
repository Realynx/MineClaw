using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins the memory cloud's numerics (WP07) bit for bit: <see cref="CloudMds"/> (landmark start, SMACOF fit, placement),
/// <see cref="CloudPca.Fit"/>, <see cref="CloudLayout.Compute"/> for every arrangement (full and incremental, a mis-sized vector, a
/// changed leaf), <see cref="CloudFidelity.Measure"/>, <see cref="PartitionLayout.Compute"/> and <see cref="TopicLabeler.Compute"/>
/// over seeded clustered vectors and the seeded trees of <see cref="GraphModelGoldenTests"/>. Floats print round-trip ("R"). The test
/// keeps the dump's line count and SHA-256 (WP07_GOLDEN_OUT writes the dump for a diff).
/// </summary>
public class CloudNumericsGoldenTests
{
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string Fs(IEnumerable<float> v) => string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
    private static string Ds(IEnumerable<double> v) => string.Join(",", v.Select(F));

    /// <summary><paramref name="n"/> vectors in <paramref name="dims"/> dimensions around a few cluster centres.</summary>
    private static float[][] Vectors(int seed, int n, int dims, int clusters)
    {
        var rng = new Random(seed);
        var centres = Enumerable.Range(0, clusters).Select(_ => Enumerable.Range(0, dims).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray()).ToArray();
        return Enumerable.Range(0, n).Select(i =>
        {
            var c = centres[rng.Next(clusters)];
            return c.Select(x => x + (float)(rng.NextDouble() - 0.5) * 0.4f).ToArray();
        }).ToArray();
    }

    private static float[] Distances(float[][] v)
    {
        int n = v.Length;
        var d = new float[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < v[i].Length; k++) s += (v[i][k] - v[j][k]) * (v[i][k] - v[j][k]);
                d[i * n + j] = (float)Math.Sqrt(s);
            }
        return d;
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (seed, n, dims, clusters) in new[] { (1, 2, 4, 1), (2, 5, 3, 2), (3, 40, 8, 4), (4, 120, 16, 6) })
        {
            var v = Vectors(seed, n, dims, clusters);
            var d = Distances(v);
            foreach (var l in new[] { 1, 3, 10, 400 })
            {
                var (x, y) = CloudMds.LandmarkStart(d, n, Math.Min(n, l));
                sb.Append($"landmarks {seed} l={l}: {Fs(x)} | {Fs(y)}\n");
            }
            var o = new CloudLayoutOptions { Landmarks = 8, SmacofIterations = 40 };
            var mds = CloudMds.Fit(v, o);
            sb.Append($"mds {seed}: {F(mds.Stress)} {mds.Iterations} {Ds(mds.Xy)}\n");
            var pca = CloudPca.Fit(v, seed);
            sb.Append($"pca {seed}: mean {Fs(pca.Mean)} axes {string.Join(" / ", pca.Axes.Select(Fs))} var {Ds(pca.VarianceExplained)}\n");

            var leaves = v.Select((x, i) => new CloudLeaf(i * 3 + 1, x, i % 5 == 0 ? i : 0)).ToList();
            if (n > 4) leaves.Add(new CloudLeaf(9999, [1, 2], 0)); // mis-sized: left out
            foreach (var arrangement in new[] { CloudArrangements.Mds, CloudArrangements.Umap, CloudArrangements.Pca })
            {
                var lo = new CloudLayoutOptions { Arrangement = arrangement, Landmarks = 8, SmacofIterations = 30, PlaceIterations = 20, NNeighbors = Math.Min(5, Math.Max(2, n - 1)) };
                if (n < 5 && arrangement == CloudArrangements.Umap) continue;
                var r = CloudLayout.Compute(7, leaves, null, lo);
                sb.Append($"layout {seed} {arrangement}: full={r.Full} placed={r.PlacedNow} anchors {string.Join(",", r.Anchors.Index)} xy {Fs(r.Anchors.Xy)}\n");
                sb.Append("  pos " + string.Join(" ", r.Positions.OrderBy(p => p.Key).Select(p => $"{p.Key}:{Fs([p.Value.X, p.Value.Y])}")) + "\n");
                // PCA fitted on under 3 leaves has no axes, and placing a new leaf there throws (left as is: not pinned).
                if (n < 3 && arrangement == CloudArrangements.Pca) continue;
                var more = leaves.Select(l => l.Index == leaves[0].Index ? l with { Vector = l.Vector.Select(x => -x).ToArray() } : l)
                    .Append(new CloudLeaf(5000, v[^1].Select(x => x * 0.5f).ToArray(), 1)).ToList();
                var again = CloudLayout.Compute(7, more, r.Anchors, lo);
                sb.Append($"  again full={again.Full} placed={again.PlacedNow} " + string.Join(" ", again.Positions.OrderBy(p => p.Key).Select(p => $"{p.Key}:{Fs([p.Value.X, p.Value.Y])}")) + "\n");
                var fid = CloudFidelity.Measure(leaves, r.Positions, seed, 500, pca.VarianceExplained);
                sb.Append($"  fidelity {fid.Metric} {F(fid.Stress)} {F(fid.Pearson)} {F(fid.Spearman)} {F(fid.UnitDistance)} {fid.Pairs}\n");
            }
        }
        foreach (var (seed, leafCount, layers, numLayers) in new[] { (1, 0, 0, 0), (3, 7, 2, 2), (4, 30, 3, 5), (6, 200, 5, 5), (7, 12, 1, 1) })
        {
            var g = GraphModel.FromSpecs(GraphModelGoldenTests.Specs(seed, leafCount, layers), numLayers);
            var p = PartitionLayout.Compute(g);
            sb.Append($"partition {seed}: rows {p.Rows} total {p.TotalLeaves} x0 {Ds(p.X0)} x1 {Ds(p.X1)} row {string.Join(",", p.Row)}\n");
            sb.Append($"  slots {string.Join(" / ", p.RowSlots.Select(r => string.Join(",", r)))} starts {string.Join(" / ", p.RowStarts.Select(Ds))}\n");
            sb.Append($"  runs {string.Join(" ", p.LeafRuns.Select(r => $"{F(r.X0)}-{F(r.X1)}:{r.Owner}"))}\n");
            foreach (var max in new[] { 1, 3 })
                sb.Append($"topics {seed} max={max}: {string.Join(" / ", TopicLabeler.Compute(g, max).Select(t => string.Join(",", t)))}\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Numerics_match_the_golden_hash()
    {
        var dump = Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "cloud-numerics.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(CloudNumericsGoldenData.Expected, $"{lines} {hash}");
    }
}

internal static class CloudNumericsGoldenData
{
    public const string Expected = "91 837D0624313225A67347835235D007DA9ED808E78A3FE18A2E3BF382C34B29BB";
}
