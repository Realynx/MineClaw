using MineClaw.Raptor.Clustering;
using MineClaw.Raptor.Numerics;
using MineClaw.Raptor.Umap;

namespace MineClaw.Raptor.Tests;

public class NumericsTests
{
    /// <summary>n points per blob around well-separated centers in d dims (row-major).</summary>
    internal static (double[] X, int[] Truth) Blobs(int[] sizes, int d, double separation, double sigma, int seed)
    {
        var rng = new Rng(seed);
        int n = sizes.Sum();
        var x = new double[n * d];
        var truth = new int[n];
        var centers = new double[sizes.Length * d];
        for (int c = 0; c < centers.Length; c++) centers[c] = rng.Uniform(-separation, separation);
        int p = 0;
        for (int b = 0; b < sizes.Length; b++)
            for (int i = 0; i < sizes[b]; i++, p++)
            {
                truth[p] = b;
                for (int j = 0; j < d; j++) x[p * d + j] = centers[b * d + j] + sigma * rng.NextGaussian();
            }
        return (x, truth);
    }

    [Fact]
    public void SymmetricEigen_reconstructs_matrix()
    {
        var rng = new Rng(3);
        int n = 12;
        var a = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
                a[i * n + j] = a[j * n + i] = rng.NextGaussian();
        var e = new SymmetricEigen(a, n);
        for (int i = 1; i < n; i++) Assert.True(e.Values[i] >= e.Values[i - 1]);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < n; k++) s += e.Vector(i, k) * e.Values[k] * e.Vector(j, k);
                Assert.Equal(a[i * n + j], s, 9);
            }
    }

    [Fact]
    public void AbParams_match_umap_learn_defaults()
    {
        var (a, b) = AbParams.Find(1.0, 0.1);
        // umap-learn find_ab_params(1.0, 0.1) → (1.5769434603113077, 0.8950608781227859)
        Assert.Equal(1.5769, a, 3);
        Assert.Equal(0.8951, b, 3);
    }

    [Fact]
    public void Bic_matches_closed_form_for_one_component()
    {
        var (x, _) = Blobs([200], 3, 5, 1.0, 11);
        int n = 200, d = 3;
        var gm = GaussianMixture.Fit(x, n, d, 1, 224);
        // MLE Gaussian: log L = -n/2 (d ln 2π + ln|Σ| + d), Σ = sample covariance (+1e-6 I)
        var mu = new double[d];
        for (int i = 0; i < n; i++) for (int j = 0; j < d; j++) mu[j] += x[i * d + j] / n;
        var cov = new double[d * d];
        for (int i = 0; i < n; i++)
            for (int a = 0; a < d; a++)
                for (int b = 0; b < d; b++) cov[a * d + b] += (x[i * d + a] - mu[a]) * (x[i * d + b] - mu[b]) / n;
        for (int a = 0; a < d; a++) cov[a * d + a] += 1e-6;
        double det = cov[0] * (cov[4] * cov[8] - cov[5] * cov[7]) - cov[1] * (cov[3] * cov[8] - cov[5] * cov[6]) + cov[2] * (cov[3] * cov[7] - cov[4] * cov[6]);
        // the trace term is tr(Σ⁻¹ S) = d − tiny (because of reg_covar); exact enough at 1e-4
        double logL = -0.5 * n * (d * Math.Log(2 * Math.PI) + Math.Log(det) + d);
        int p = d * (d + 1) / 2 + d + 1 - 1;
        Assert.Equal(-2 * logL + p * Math.Log(n), gm.Bic(x, n), 2);
        Assert.Equal(p, gm.NParameters);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Gmm_bic_picks_true_k_on_blobs(int k)
    {
        var (x, _) = Blobs(Enumerable.Repeat(80, k).ToArray(), 4, 20, 1.0, 100 + k);
        var rc = new RaptorClustering(TestOptions.Default with { MaxClusters = 12 });
        Assert.Equal(k, rc.GetOptimalClusters(x, 80 * k, 4));
    }

    [Fact]
    public void Gmm_recovers_blob_partition()
    {
        var (x, truth) = Blobs([60, 60, 60], 2, 40, 1.0, 5);
        var gm = GaussianMixture.Fit(x, 180, 2, 3, 0);
        var p = gm.PredictProba(x, 180);
        var map = new Dictionary<int, int>();
        int agree = 0;
        for (int i = 0; i < 180; i++)
        {
            int arg = Enumerable.Range(0, 3).MaxBy(c => p[i * 3 + c]);
            if (!map.TryGetValue(truth[i], out var m)) map[truth[i]] = m = arg;
            if (m == arg) agree++;
        }
        Assert.Equal(180, agree);
        Assert.True(gm.Converged);
    }

    [Fact]
    public void Soft_assignment_puts_boundary_points_in_two_clusters()
    {
        // two overlapping 1-D-ish blobs plus points exactly on the midpoint
        var rng = new Rng(9);
        var pts = new List<double>();
        for (int i = 0; i < 100; i++) { pts.Add(-2 + 0.7 * rng.NextGaussian()); pts.Add(0.3 * rng.NextGaussian()); }
        for (int i = 0; i < 100; i++) { pts.Add(2 + 0.7 * rng.NextGaussian()); pts.Add(0.3 * rng.NextGaussian()); }
        int boundaryStart = pts.Count / 2;
        for (int i = 0; i < 4; i++) { pts.Add(0.05 * (i - 1.5)); pts.Add(0.3 * rng.NextGaussian()); }
        var x = pts.ToArray();
        int n = x.Length / 2;
        var rc = new RaptorClustering(TestOptions.Default with { MaxClusters = 6 });
        var (labels, k) = rc.GmmCluster(x, n, 2, 0.1);
        Assert.Equal(2, k);
        for (int i = boundaryStart; i < n; i++) Assert.True(labels[i].Count == 2, $"x={x[2 * i]:F3} y={x[2 * i + 1]:F3} labels={string.Join(",", labels[i])}");
        for (int i = 0; i < boundaryStart; i++)
            if (Math.Abs(x[2 * i]) > 1.6) Assert.Single(labels[i]);
    }

    [Fact]
    public void Kmeans_plusplus_is_seeded()
    {
        var (x, _) = Blobs([50, 50, 50], 3, 10, 1, 4);
        var a = KMeans.FitLabels(x, 150, 3, 3, new Rng(1));
        var b = KMeans.FitLabels(x, 150, 3, 3, new Rng(1));
        Assert.Equal(a, b);
        Assert.Equal(3, a.Distinct().Count());
    }

    [Fact]
    public void Lanczos_matches_dense_solver()
    {
        // random sparse symmetric graph, 600 vertices (above the dense cutoff)
        var rng = new Rng(21);
        int n = 600;
        var rows = new SortedDictionary<int, double>[n];
        for (int i = 0; i < n; i++) rows[i] = new();
        for (int i = 0; i < n; i++)
        {
            rows[i][(i + 1) % n] = 1; rows[(i + 1) % n][i] = 1; // ring keeps it connected
            for (int t = 0; t < 4; t++)
            {
                int j = rng.NextInt(n);
                if (j == i) continue;
                double w = rng.NextDouble();
                rows[i][j] = w; rows[j][i] = w;
            }
        }
        var rp = new int[n + 1];
        var col = new List<int>();
        var val = new List<double>();
        for (int i = 0; i < n; i++) { foreach (var kv in rows[i]) { col.Add(kv.Key); val.Add(kv.Value); } rp[i + 1] = col.Count; }
        var g = new SparseGraph(n, rp, col.ToArray(), val.ToArray());
        var lan = SpectralSolver.TopNormalizedAdjacency(g, 4, tol: 1e-8, maxRestarts: 500);
        Assert.NotNull(lan);
        var deg = g.RowSums();
        var m = new double[n * n];
        for (int i = 0; i < n; i++) for (int p = rp[i]; p < rp[i + 1]; p++) m[i * n + col[p]] = g.Val[p] / Math.Sqrt(deg[i] * deg[col[p]]);
        var dense = SpectralSolver.TopDense(m, n, 4);
        for (int c = 0; c < 4; c++)
        {
            double dot = 0;
            for (int i = 0; i < n; i++) dot += lan![c][i] * dense[c][i];
            Assert.True(Math.Abs(dot) > 0.999, $"eigvec {c}: |dot|={Math.Abs(dot)}");
        }
    }

    [Fact]
    public void Umap_separates_clusters_and_is_deterministic()
    {
        var rng = new Rng(17);
        int d = 48;
        var centers = Enumerable.Range(0, 4).Select(_ => Enumerable.Range(0, d).Select(_ => (float)rng.NextGaussian()).ToArray()).ToArray();
        var data = new List<float[]>();
        var truth = new List<int>();
        for (int c = 0; c < 4; c++)
            for (int i = 0; i < 60; i++)
            {
                data.Add(centers[c].Select(v => v + 0.35f * (float)rng.NextGaussian()).ToArray());
                truth.Add(c);
            }
        var opts = new UmapOptions { NNeighbors = 15, NComponents = 10, Seed = 5 };
        var e1 = new UmapReducer(opts).FitTransform(data);
        var e2 = new UmapReducer(opts).FitTransform(data);
        Assert.Equal(e1, e2);
        int n = data.Count, good = 0;
        for (int i = 0; i < n; i++)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                double s = 0;
                for (int t = 0; t < 10; t++) { double q = e1[i * 10 + t] - e1[j * 10 + t]; s += q * q; }
                if (s < bd) { bd = s; best = j; }
            }
            if (truth[best] == truth[i]) good++;
        }
        Assert.True(good >= n * 0.98, $"1-NN purity {good}/{n}");
    }
}
