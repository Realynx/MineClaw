using System.Security.Cryptography;
using System.Text;
using MineClaw.Raptor.Clustering;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Numerics;
using MineClaw.Raptor.Text;
using MineClaw.Raptor.Umap;
using Xunit.Abstractions;

namespace MineClaw.Raptor.Tests;

/// <summary>
/// Bit-exact goldens for the Raptor numerics and retrieval (pinned before the complexity refactor): every double and
/// float is written as its raw bits, each section hashed. A refactor must keep floating-point operation order exactly.
/// Set RAPTOR_GOLDEN_OUT=&lt;dir&gt; to write the section texts for diffing.
/// </summary>
public class NumericsGoldenTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> Pinned = new()
    {
        ["eigen"] = "4C82AE597C2D05725560F6168DEE63C0",
        ["spectral"] = "B64F314EA969A1A660BF6A343C3D1476",
        ["fuzzy"] = "45A7DD3442783147FA0F212A14EBA84D",
        ["umap"] = "0182FBF477CBA454B7288F8A4689C47F",
        ["abparams"] = "51BC180E293BF7285D233D056A018612",
        ["clustering"] = "72002C80F0D07962A0D5ABBF601C5F69",
        ["tokens"] = "6C1D5DD3449758073A40B9898A59F6BE",
        ["validate"] = "43A417216531046AD4A4A8D5C3C330D4",
        ["tree"] = "ECC5924C954FCE8B3795F046BEEEDE07",
    };

    private sealed class Dump
    {
        private readonly StringBuilder _s = new();
        public Dump Line(string text) { _s.Append(text).Append('\n'); return this; }
        public Dump D(string tag, IEnumerable<double> v) => Line(tag + ":" + string.Join(',', v.Select(x => BitConverter.DoubleToInt64Bits(x).ToString("x16"))));
        public Dump F(string tag, IEnumerable<float> v) => Line(tag + ":" + string.Join(',', v.Select(x => BitConverter.SingleToInt32Bits(x).ToString("x8"))));
        public Dump I(string tag, IEnumerable<int> v) => Line(tag + ":" + string.Join(',', v));
        public override string ToString() => _s.ToString();
    }

    private void Check(string section, Dump d)
    {
        var text = d.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        if (Environment.GetEnvironmentVariable("RAPTOR_GOLDEN_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, section + ".txt"), text);
        }
        output.WriteLine($"{section} {hash} ({text.Length} chars)");
        Assert.Equal(Pinned[section], hash);
    }

    private static double[] RandomSymmetric(Rng rng, int n)
    {
        var m = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double v = rng.NextGaussian();
                m[i * n + j] = v;
                m[j * n + i] = v;
            }
        return m;
    }

    private static IEnumerable<(string Name, double[] M, int N)> EigenCases()
    {
        var rng = new Rng(11);
        foreach (int n in new[] { 1, 2, 3, 4, 5, 8, 13, 21, 40 })
            for (int s = 0; s < 3; s++) yield return ($"rand{n}.{s}", RandomSymmetric(rng, n), n);
        yield return ("zero4", new double[16], 4);
        var id = new double[25];
        for (int i = 0; i < 5; i++) id[i * 5 + i] = 1;
        yield return ("id5", id, 5);
        var rep = new double[36];
        for (int i = 0; i < 6; i++) rep[i * 6 + i] = i % 2 == 0 ? 2 : -1;
        yield return ("diagrep6", rep, 6);
        var zeroRow = RandomSymmetric(rng, 7);
        for (int i = 0; i < 7; i++) { zeroRow[3 * 7 + i] = 0; zeroRow[i * 7 + 3] = 0; }
        yield return ("zerorow7", zeroRow, 7);
        var lastZero = RandomSymmetric(rng, 6);
        for (int i = 0; i < 6; i++) { lastZero[5 * 6 + i] = 0; lastZero[i * 6 + 5] = 0; }
        yield return ("lastzero6", lastZero, 6);
        var rank1 = new double[64];
        var u = Enumerable.Range(0, 8).Select(_ => rng.NextGaussian()).ToArray();
        for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) rank1[i * 8 + j] = u[i] * u[j];
        yield return ("rank1x8", rank1, 8);
        var tri = new double[100];
        for (int i = 0; i < 10; i++) { tri[i * 10 + i] = 2; if (i > 0) { tri[i * 10 + i - 1] = -1; tri[(i - 1) * 10 + i] = -1; } }
        yield return ("tridiag10", tri, 10);
        var asym = new double[16];
        for (int i = 0; i < 16; i++) asym[i] = rng.NextGaussian();
        yield return ("asym4", asym, 4);
    }

    [Fact]
    public void Eigen_decomposition_is_bit_exact()
    {
        var d = new Dump();
        foreach (var (name, m, n) in EigenCases())
        {
            var e = new SymmetricEigen(m, n);
            d.D(name + ".values", e.Values).D(name + ".vectors", e.Vectors);
        }
        Check("eigen", d);
    }

    private static float[][] Blobs(int seed, int perBlob, int blobs, int dims, double spread, double sep)
    {
        var rng = new Rng(seed);
        var centers = Enumerable.Range(0, blobs).Select(_ => Enumerable.Range(0, dims).Select(_ => rng.NextGaussian() * sep).ToArray()).ToArray();
        var x = new float[perBlob * blobs][];
        for (int i = 0; i < x.Length; i++)
        {
            var c = centers[i % blobs];
            x[i] = Enumerable.Range(0, dims).Select(j => (float)(c[j] + rng.NextGaussian() * spread)).ToArray();
        }
        return x;
    }

    private static float[][] Normalized(float[][] x) => x.Select(v => VectorMath.Normalized(v)).ToArray();

    private static SparseGraph Cliques(int cliques, int size)
    {
        int n = cliques * size;
        var rowPtr = new int[n + 1];
        var col = new List<int>();
        var val = new List<double>();
        for (int i = 0; i < n; i++)
        {
            int c = i / size;
            for (int j = c * size; j < (c + 1) * size; j++)
                if (j != i) { col.Add(j); val.Add(1.0); }
            rowPtr[i + 1] = col.Count;
        }
        return new SparseGraph(n, rowPtr, col.ToArray(), val.ToArray());
    }

    [Fact]
    public void Spectral_solver_is_bit_exact()
    {
        var d = new Dump();
        void Vecs(string tag, double[][]? v)
        {
            if (v == null) { d.Line(tag + ":null"); return; }
            for (int i = 0; i < v.Length; i++) d.D($"{tag}.{i}", v[i]);
        }
        foreach (var (name, n, blobs) in new[] { ("g60", 60, 1), ("g450", 450, 1), ("g810", 810, 3) })
        {
            var x = Normalized(Blobs(n, n / blobs, blobs, 6, 1.0, blobs == 1 ? 0.1 : 0.6));
            var g = FuzzySimplicialSet.Build(x, 12, 1.0, 1.0);
            Vecs(name + ".k3", SpectralSolver.TopNormalizedAdjacency(g, 3));
            Vecs(name + ".k5", SpectralSolver.TopNormalizedAdjacency(g, 5));
            if (n > SpectralSolver.DenseMaxN)
            {
                Vecs(name + ".ncv8", SpectralSolver.TopNormalizedAdjacency(g, 3, ncv: 8));
                Vecs(name + ".r0", SpectralSolver.TopNormalizedAdjacency(g, 3, tol: 1e-12, maxRestarts: 0, ncv: 5));
                Vecs(name + ".r2", SpectralSolver.TopNormalizedAdjacency(g, 4, tol: 1e-9, maxRestarts: 2, ncv: 7));
                Vecs(name + ".mdense", SpectralSolver.TopNormalizedAdjacency(g, 3, ncv: 2));
            }
        }
        var cl = Cliques(10, 42);
        Vecs("cliques.k3", SpectralSolver.TopNormalizedAdjacency(cl, 3));
        Vecs("cliques.k2", SpectralSolver.TopNormalizedAdjacency(cl, 2, ncv: 6));
        var rng = new Rng(5);
        Vecs("dense5", SpectralSolver.TopDense(RandomSymmetric(rng, 5), 5, 3));
        Vecs("dense12", SpectralSolver.TopDense(RandomSymmetric(rng, 12), 12, 4));
        var flip = new[] { 0.1, -0.9, 0.3, 0.9, -0.2 };
        SpectralSolver.SignFlip(flip);
        d.D("flip", flip);
        Check("spectral", d);
    }

    [Fact]
    public void Fuzzy_simplicial_set_is_bit_exact()
    {
        var d = new Dump();
        foreach (var (name, x, k) in new[]
        {
            ("blob40", Normalized(Blobs(3, 40, 1, 5, 1.0, 0)), 8),
            ("three90", Normalized(Blobs(4, 30, 3, 8, 0.2, 1.0)), 10),
            ("dup12", Normalized(Enumerable.Range(0, 12).Select(i => new float[] { 1, i % 3, 0.5f }).ToArray()), 4),
            ("tiny3", Normalized(Blobs(6, 3, 1, 4, 1.0, 0)), 2),
        })
        {
            var (idx, dist) = FuzzySimplicialSet.NearestNeighbors(x, k);
            for (int i = 0; i < idx.Length; i++) d.I($"{name}.nn{i}", idx[i]).F($"{name}.nd{i}", dist[i]);
            foreach (var (lc, bw) in new[] { (1.0, 1.0), (0.5, 1.0), (1.5, 1.0), (2.0, 0.5), (20.0, 1.0) })
            {
                var (sig, rho) = FuzzySimplicialSet.SmoothKnnDist(dist, k, lc, bandwidth: bw);
                d.D($"{name}.sig{lc}/{bw}", sig).D($"{name}.rho{lc}/{bw}", rho);
            }
            foreach (var (mix, lc) in new[] { (1.0, 1.0), (0.5, 1.0), (0.0, 2.0) })
            {
                var g = FuzzySimplicialSet.Build(x, k, mix, lc);
                d.I($"{name}.g{mix}/{lc}.ptr", g.RowPtr).I($"{name}.g{mix}/{lc}.col", g.Col).D($"{name}.g{mix}/{lc}.val", g.Val);
            }
        }
        Check("fuzzy", d);
    }

    [Fact]
    public void Umap_is_bit_exact()
    {
        var d = new Dump();
        var cases = new (string Name, float[][] X, UmapOptions O)[]
        {
            ("blob60", Blobs(1, 60, 1, 8, 1.0, 0), new UmapOptions { NNeighbors = 10, NEpochs = 60 }),
            ("blob60.d3", Blobs(1, 60, 1, 8, 1.0, 0), new UmapOptions { NNeighbors = 10, NComponents = 3, NEpochs = 40 }),
            ("blob60.d1", Blobs(1, 60, 1, 8, 1.0, 0), new UmapOptions { NNeighbors = 10, NComponents = 1, NEpochs = 40 }),
            ("blob60.rand", Blobs(1, 60, 1, 8, 1.0, 0), new UmapOptions { NNeighbors = 10, SpectralInit = false, NEpochs = 40 }),
            ("blob60.e5", Blobs(1, 60, 1, 8, 1.0, 0), new UmapOptions { NNeighbors = 10, NEpochs = 5, MinDist = 0.3, Spread = 1.5 }),
            ("three", Blobs(2, 20, 3, 10, 0.05, 2.0), new UmapOptions { NNeighbors = 6, NEpochs = 50 }),
            ("eight", Blobs(3, 10, 8, 12, 0.02, 2.0), new UmapOptions { NNeighbors = 5, NEpochs = 40 }),
            ("small", Blobs(4, 3, 5, 12, 0.01, 2.0), new UmapOptions { NNeighbors = 2, NEpochs = 30 }),
            ("n1", Blobs(5, 1, 1, 4, 1.0, 0), new UmapOptions()),
            ("n2", Blobs(5, 2, 1, 4, 1.0, 0), new UmapOptions { NEpochs = 20 }),
            ("n3", Blobs(5, 3, 1, 4, 1.0, 0), new UmapOptions { NEpochs = 20 }),
            ("n7", Blobs(5, 7, 1, 4, 1.0, 0), new UmapOptions { NEpochs = 20 }),
            ("big450", Blobs(6, 450, 1, 6, 1.0, 0), new UmapOptions { NNeighbors = 10, NEpochs = 15 }),
        };
        foreach (var (name, x, o) in cases) d.D(name, new UmapReducer(o).FitTransform(x));
        d.D("eps", UmapReducer.MakeEpochsPerSample([0.5, 1.0, 0.0, 0.25, 1e-9], 200));
        Check("umap", d);
    }

    [Fact]
    public void Ab_params_are_bit_exact()
    {
        var d = new Dump();
        foreach (var spread in new[] { 0.5, 1.0, 1.5, 3.0 })
            foreach (var minDist in new[] { 0.0, 0.001, 0.1, 0.25, 0.5, 0.99 })
            {
                var (a, b) = AbParams.Find(spread, minDist);
                d.D($"{spread}/{minDist}", [a, b]);
            }
        Check("abparams", d);
    }

    [Fact]
    public void Clustering_is_bit_exact()
    {
        var d = new Dump();
        foreach (var (name, n, dim, k, blobs) in new[] { ("a", 60, 3, 3, 3), ("b", 40, 5, 2, 4), ("c", 25, 2, 1, 1), ("d", 30, 4, 6, 2) })
        {
            var pts = Blobs(n + dim, n / blobs, blobs, dim, 0.3, 2.0);
            var x = pts.SelectMany(v => v.Select(f => (double)f)).ToArray();
            int rows = pts.Length;
            d.I($"{name}.km", KMeans.FitLabels(x, rows, dim, k, new Rng(17)));
            d.D($"{name}.pp", KMeans.PlusPlus(x, rows, dim, k, new Rng(18)));
            var gmm = GaussianMixture.Fit(x, rows, dim, k, 224);
            d.D($"{name}.w", gmm.Weights).D($"{name}.mu", gmm.Means).D($"{name}.cov", gmm.Covariances).D($"{name}.pc", gmm.PrecisionsCholesky)
                .Line($"{name}.conv:{gmm.Converged}:{gmm.NIter}").D($"{name}.lb", [gmm.LowerBound, gmm.Bic(x, rows)]).D($"{name}.proba", gmm.PredictProba(x, rows));
            var rc = new RaptorClustering(TestOptions.Default with { MaxDegreeOfParallelism = 1 });
            d.Line($"{name}.opt:{rc.GetOptimalClusters(x, rows, dim)}");
            var (labels, nc) = rc.GmmCluster(x, rows, dim, 0.1);
            d.Line($"{name}.gc:{nc}:" + string.Join('|', labels.Select(l => string.Join(',', l))));
        }
        var emb = Normalized(Blobs(9, 30, 4, 24, 0.2, 1.0));
        var opt = TestOptions.Default with { MaxDegreeOfParallelism = 1, ReductionDimension = 4 };
        var rc2 = new RaptorClustering(opt);
        var pc = rc2.PerformClustering(emb, 4, 0.1);
        d.Line("perform:" + string.Join('|', pc.Select(l => string.Join(',', l))));
        var cl = rc2.Cluster(emb, emb.Select((_, i) => 40 + i % 7).ToArray());
        d.Line("cluster:" + string.Join('|', cl.Select(l => string.Join(',', l))));
        var cl2 = new RaptorClustering(opt with { MaxLengthInCluster = 300 }).Cluster(emb, emb.Select((_, i) => 40 + i % 7).ToArray());
        d.Line("cluster300:" + string.Join('|', cl2.Select(l => string.Join(',', l))));
        d.Line($"stats:{rc2.ReclusterCount}:{rc2.FallbackSplits}:{rc2.LastGlobalClusters}:{rc2.LastTotalClusters}");
        Check("clustering", d);
    }

    [Fact]
    public void Token_counts_are_exact()
    {
        var d = new Dump();
        string[] texts =
        [
            "", " ", "a", "hello", "hello world", "  two  spaces", "extraordinary", "antidisestablishmentarianism",
            "1", "12", "123", "1234", "1234567", "a1b2c3", "x\ny", "x\r\n\r\ny", "\n\n\n", "tab\tseparated\t\tvalues",
            "!!", "!!!", "...?!", "end.", "(paren) [bracket] {brace}", "naïve café Ünïcödé", "日本語のテキスト", "emoji 🙂 here",
            "mixed: 3.14159, e=2.71828; done!", "The quick brown fox jumps over the lazy dog. " + new string('z', 33),
        ];
        foreach (var t in texts) d.Line($"{ApproxTokenCounter.Instance.Count(t)}");
        Check("tokens", d);
    }

    [Fact]
    public void Tree_validate_messages_are_exact()
    {
        var t = new RaptorTree { NumLayers = 2, Dimensions = 2 };
        t.Add(new RaptorNode(0, 0, "a", [1, 0], 1));
        t.Add(new RaptorNode(1, 0, "b", [0, 1], 1));
        t.Add(new RaptorNode(2, 0, "c", [0, 1, 0], 1));
        t.Add(new RaptorNode(3, 1, "s", [1, 0], 1));
        t.Add(new RaptorNode(4, 1, "empty", [1, 0], 1));
        t.Add(new RaptorNode(5, 2, "root", [1, 0], 1));
        t.Add(new RaptorNode(6, 3, "above", [1, 0], 1));
        t.Link(3, 0);
        t[3].Children.Add(1);
        t[3].Children.Add(99);
        t[5].Children.Add(0);
        t[1].Parents.Add(4);
        t.Link(5, 3);
        t[5].Layer = 1;
        var d = new Dump();
        foreach (var e in t.Validate()) d.Line(e);
        Check("validate", d);
    }

    [Fact]
    public async Task Tree_build_maintenance_and_retrieval_are_bit_exact()
    {
        var d = new Dump();
        var corpus = new SyntheticCorpus(420, 4, 3, seed: 7);
        var embedder = new HashingEmbedder(128);
        var o = TestOptions.Default with { MaxDegreeOfParallelism = 1, SummaryConcurrency = 1 };
        using var index = new RaptorIndex(embedder, new KeywordSummarizer(), o) { AutoPersist = false };
        var docs = corpus.Documents(l => l.Id % 5 == 0 ? "event" : null).ToList();
        docs.Add(new RaptorDocument(string.Join(" ", Enumerable.Range(0, 60).Select(i => $"Sentence number {i} talks about tlong topic."))) { });
        docs.Add(new RaptorDocument("pinned fact about everything", Key: "pin", Pinned: true));
        await index.BuildAsync(docs);
        void Tree(string tag) => index.Read(t =>
        {
            d.Line($"{tag}:layers={t.NumLayers} count={t.Count} leaves={t.LeafCount} next={t.NextIndex}");
            d.D($"{tag}.base", [t.BaselineLeafParentSimilarity, t.BaselineLeafParentSimilarityStd, t.InsertTemperature, t.InsertedSimilaritySum]);
            foreach (var n in t.AllNodes.Values.OrderBy(n => n.Index))
                d.Line($"{n.Index} L{n.Layer} t{n.TokenCount} {n.Key}/{n.Chunk}/{n.Kind}/{n.Source}/{n.Pinned}/{n.Dirty}/{n.SummaryPending}/{n.InsertedSeq} c[{string.Join(',', n.Children)}] p[{string.Join(',', n.Parents)}] {n.Text}")
                    .F("e", n.Embedding);
            foreach (var e in t.Validate()) d.Line("invalid " + e);
            return 0;
        });
        Tree("built");

        async Task Q(string tag, RaptorQuery q)
        {
            var r = await index.RetrieveAsync(q);
            d.Line($"{tag}:{r.Tokens}:{r.Collapsed}:{r.Nodes.Count}");
            foreach (var n in r.Nodes) d.Line($" {n.Index} L{n.Layer} {BitConverter.DoubleToInt64Bits(n.Score):x16} {n.Tokens} {n.Key} {n.Leaf}");
            d.Line(r.Context);
        }
        async Task Queries(string phase)
        {
            int qi = 0;
            foreach (var leaf in corpus.Leaves.Where((_, i) => i % 37 == 0))
            {
                string p = $"{phase}.q{qi++}";
                await Q(p + ".col", new RaptorQuery { Text = leaf.Query });
                await Q(p + ".col120", new RaptorQuery { Text = leaf.Query, MaxTokens = 120 });
                await Q(p + ".colk3", new RaptorQuery { Text = leaf.Query, TopK = 3, IncludePinned = true });
                await Q(p + ".trav", new RaptorQuery { Text = leaf.Query, Collapsed = false });
                await Q(p + ".trav2", new RaptorQuery { Text = leaf.Query, Collapsed = false, TopK = 2, MaxTokens = 90, IncludePinned = true });
                await Q(p + ".thr", new RaptorQuery { Text = leaf.Query, Collapsed = false, Selection = SelectionMode.Threshold, Threshold = 0.25 });
                await Q(p + ".start1", new RaptorQuery { Text = leaf.Query, Collapsed = false, StartLayer = 1, NumLayers = 2 });
                await Q(p + ".start0", new RaptorQuery { Text = leaf.Query, Collapsed = false, StartLayer = 0 });
                await Q(p + ".fkind", new RaptorQuery { Text = leaf.Query, Filter = new RaptorFilter { Kinds = ["EVENT"] } });
                await Q(p + ".fsrc", new RaptorQuery { Text = leaf.Query, Collapsed = false, Filter = new RaptorFilter { Sources = ["topic1", "topic2"] } });
                await Q(p + ".fpin", new RaptorQuery { Text = leaf.Query, Filter = new RaptorFilter { PinnedOnly = true }, IncludePinned = true });
            }
        }
        await Queries("built");

        var ins = await index.InsertAsync(new SyntheticCorpus(30, 4, 3, seed: 99).Documents().Select((doc, i) => doc with { Key = $"new:{i}" }));
        d.Line($"insert:{ins.Leaves}:{ins.Attached}:{ins.DirtyNodes}:{ins.EmbeddingTokens}").D("insert.sim", [ins.MeanParentSimilarity]);
        int removed = await index.RemoveAsync(Enumerable.Range(0, 40).Select(i => $"leaf:{i * 3}"));
        d.Line($"removed:{removed}");
        var decision = index.ShouldRebuild();
        d.Line($"decision:{decision.Rebuild}:{decision.Reason}:{decision.DirtyNodes}").D("decision.d", [decision.ChangedFraction, decision.SimilarityDrift]);
        var refresh = await index.RefreshDirtyAsync();
        d.Line($"refresh:{refresh.Refreshed}:{refresh.Splits}:{refresh.RemainingDirty}:{refresh.EmbeddingTokens}:{refresh.BudgetExhausted}");
        Tree("maintained");
        await Queries("maintained");
        Check("tree", d);
    }
}
