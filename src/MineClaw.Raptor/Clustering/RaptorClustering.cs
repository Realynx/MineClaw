using MineClaw.Raptor.Umap;

namespace MineClaw.Raptor.Clustering;

/// <summary>
/// cluster_utils.py: <c>global_cluster_embeddings</c> / <c>local_cluster_embeddings</c> (UMAP, cosine),
/// <c>get_optimal_clusters</c> (min BIC over k = 1 … min(50, n) − 1, random_state 224), <c>GMM_cluster</c>
/// (soft labels: every component with probability &gt; threshold, final fit random_state 0),
/// <c>perform_clustering</c> (global then local) and <c>RAPTOR_Clustering</c> (recursive re-clustering of clusters
/// whose total tokens exceed max_length_in_cluster).
/// </summary>
public sealed class RaptorClustering
{
    /// <summary>Smallest set UMAP can embed with RAPTOR's global rule: n_neighbors = int(sqrt(n-1)) must be ≥ 2.</summary>
    public const int MinUmapPoints = 5;

    private readonly RaptorOptions _o;

    public RaptorClustering(RaptorOptions options) => _o = options;

    /// <summary>Diagnostics of the last top-level <see cref="Cluster"/> call (tests and logging).</summary>
    public int ReclusterCount { get; private set; }
    public int FallbackSplits { get; private set; }
    /// <summary>Global and total (global × local) cluster counts of the last <see cref="PerformClustering"/> call.</summary>
    public int LastGlobalClusters { get; private set; }
    public int LastTotalClusters { get; private set; }

    // ---------------- get_optimal_clusters / GMM_cluster ----------------

    /// <summary>k in [minK, min(MaxClusters, n) − 1] with the lowest BIC (first minimum wins, as np.argmin).</summary>
    public int GetOptimalClusters(double[] x, int n, int d, int minK = 1)
    {
        int maxClusters = Math.Min(_o.MaxClusters, n);
        int lo = minK, hi = maxClusters - 1;
        if (hi < lo) return Math.Max(1, Math.Min(minK, n));
        var ks = Enumerable.Range(lo, hi - lo + 1).ToArray();
        var bics = new double[ks.Length];
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _o.MaxDegreeOfParallelism) };
        // Largest k first for load balance; every fit gets its own RandomState(224) exactly like sklearn.
        Parallel.ForEach(Enumerable.Range(0, ks.Length).Reverse(), po, t =>
        {
            try
            {
                var gm = GaussianMixture.Fit(x, n, d, ks[t], _o.BicSearchSeed, _o.Gmm);
                bics[t] = gm.Bic(x, n);
            }
            catch (GmmFitException)
            {
                bics[t] = double.PositiveInfinity; // sklearn would raise; README D5
            }
        });
        int best = 0;
        for (int t = 1; t < bics.Length; t++) if (bics[t] < bics[best]) best = t;
        return ks[best];
    }

    /// <summary>Soft labels per point (ascending component ids) and the component count.</summary>
    public (List<int>[] Labels, int NClusters) GmmCluster(double[] x, int n, int d, double threshold, int minK = 1)
    {
        int k = GetOptimalClusters(x, n, d, minK);
        var probs = FitProbabilities(x, n, d, k);
        var labels = new List<int>[n];
        if (probs == null)
        {
            for (int i = 0; i < n; i++) labels[i] = [0];
            return (labels, 1);
        }
        for (int i = 0; i < n; i++) labels[i] = SoftLabels(probs, i, k, threshold);
        return (labels, k);
    }

    /// <summary>GMM membership probabilities; up to 4 fits, raising reg_covar tenfold after each failure (null if all fail).</summary>
    private double[]? FitProbabilities(double[] x, int n, int d, int k)
    {
        var gopt = _o.Gmm;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return GaussianMixture.Fit(x, n, d, k, _o.ClusterFitSeed, gopt).PredictProba(x, n);
            }
            catch (GmmFitException)
            {
                gopt = gopt with { RegCovar = gopt.RegCovar * 10 };
            }
        }
        return null;
    }

    /// <summary>Components over the threshold, or the most likely one (README D3: never drop a node from the next layer).</summary>
    private static List<int> SoftLabels(double[] probs, int i, int k, double threshold)
    {
        var l = new List<int>();
        int arg = 0;
        for (int c = 0; c < k; c++)
        {
            double p = probs[i * k + c];
            if (p > threshold) l.Add(c);
            if (p > probs[i * k + arg]) arg = c;
        }
        if (l.Count == 0) l.Add(arg);
        return l;
    }

    // ---------------- perform_clustering ----------------

    /// <summary>Two-stage clustering; returns, per input point, the (global-local) cluster ids it belongs to.</summary>
    public List<int>[] PerformClustering(IReadOnlyList<float[]> embeddings, int dim, double threshold, int minGlobalK = 1)
    {
        int n = embeddings.Count;
        int globalDim = Math.Min(dim, n - 2);
        int globalNeighbors = _o.GlobalNeighbors ?? (int)Math.Floor(Math.Sqrt(n - 1));
        var reducedGlobal = new UmapReducer(_o.Umap with { NNeighbors = globalNeighbors, NComponents = globalDim }).FitTransform(embeddings);
        var (globalClusters, nGlobal) = GmmCluster(reducedGlobal, n, globalDim, threshold, minGlobalK);

        var members = new int[nGlobal][];
        for (int i = 0; i < nGlobal; i++)
            members[i] = Enumerable.Range(0, n).Where(p => globalClusters[p].Contains(i)).ToArray();

        var local = new (List<int>[] Labels, int N)[nGlobal];
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _o.MaxDegreeOfParallelism) };
        Parallel.For(0, nGlobal, po, i =>
        {
            var m = members[i];
            if (m.Length == 0) { local[i] = ([], 0); return; }
            if (m.Length <= dim + 1)
            {
                local[i] = (m.Select(_ => new List<int> { 0 }).ToArray(), 1);
                return;
            }
            var sub = m.Select(p => embeddings[p]).ToArray();
            var reducedLocal = new UmapReducer(_o.Umap with { NNeighbors = _o.LocalNeighbors, NComponents = dim }).FitTransform(sub);
            local[i] = GmmCluster(reducedLocal, m.Length, dim, threshold);
        });

        var all = new List<int>[n];
        for (int p = 0; p < n; p++) all[p] = [];
        int total = 0;
        for (int i = 0; i < nGlobal; i++)
        {
            var m = members[i];
            if (m.Length == 0) continue;
            var (labels, nLocal) = local[i];
            for (int j = 0; j < nLocal; j++)
                for (int t = 0; t < m.Length; t++)
                    if (labels[t].Contains(j)) all[m[t]].Add(j + total);
            total += nLocal;
        }
        LastGlobalClusters = nGlobal;
        LastTotalClusters = total;
        foreach (var l in all) l.Sort();
        return all;
    }

    // ---------------- RAPTOR_Clustering.perform_clustering ----------------

    /// <summary>
    /// Clusters nodes (given by embedding and token count) into groups of indices into the input lists, re-clustering
    /// any multi-node group whose total tokens exceed <see cref="RaptorOptions.MaxLengthInCluster"/>.
    /// </summary>
    public List<int[]> Cluster(IReadOnlyList<float[]> embeddings, IReadOnlyList<int> tokenCounts)
    {
        ReclusterCount = 0;
        FallbackSplits = 0;
        var all = Enumerable.Range(0, embeddings.Count).ToArray();
        if (all.Length == 0) return [];
        if (all.Length < MinUmapPoints) return [all];
        return Recurse(all, embeddings, tokenCounts, 0, false);
    }

    private List<int[]> Recurse(int[] members, IReadOnlyList<float[]> emb, IReadOnlyList<int> tokens, int depth, bool forceSplit)
    {
        if (members.Length < MinUmapPoints) return GreedyPack(members, tokens);
        var sub = members.Select(i => emb[i]).ToArray();
        var clusters = PerformClustering(sub, _o.ReductionDimension, _o.ClusterThreshold, forceSplit ? 2 : 1);
        var result = new List<int[]>();
        foreach (var label in clusters.SelectMany(c => c).Distinct().Order())
        {
            var idx = Enumerable.Range(0, members.Length).Where(t => clusters[t].Contains(label)).Select(t => members[t]).ToArray();
            if (idx.Length == 1) { result.Add(idx); continue; }
            long total = idx.Sum(i => (long)tokens[i]);
            if (total <= _o.MaxLengthInCluster) { result.Add(idx); continue; }
            ReclusterCount++;
            bool noProgress = idx.Length == members.Length;
            if (depth >= _o.MaxReclusterDepth || (noProgress && forceSplit))
                result.AddRange(GreedyPack(idx, tokens));
            else
                result.AddRange(Recurse(idx, emb, tokens, depth + 1, noProgress));
        }
        return result;
    }

    /// <summary>Last-resort split (README D4): consecutive groups each within the token cap.</summary>
    private List<int[]> GreedyPack(int[] members, IReadOnlyList<int> tokens)
    {
        long total = members.Sum(i => (long)tokens[i]);
        if (total <= _o.MaxLengthInCluster || members.Length <= 1) return [members];
        FallbackSplits++;
        var res = new List<int[]>();
        var cur = new List<int>();
        long len = 0;
        foreach (var i in members)
        {
            if (cur.Count > 0 && len + tokens[i] > _o.MaxLengthInCluster)
            {
                res.Add(cur.ToArray());
                cur.Clear();
                len = 0;
            }
            cur.Add(i);
            len += tokens[i];
        }
        if (cur.Count > 0) res.Add(cur.ToArray());
        return res;
    }
}
