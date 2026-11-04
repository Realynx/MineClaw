using MineClaw.Raptor.Numerics;
using MineClaw.Raptor.Umap;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>One RAPTOR leaf to place: its index, its stored embedding and its insertion sequence (0 = part of the last build).</summary>
public sealed record CloudLeaf(int Index, float[] Vector, long InsertedSeq);

/// <summary>
/// The memory cloud's arrangements (architecture.md, "Memory cloud"): each is computed lazily, cached per tree state and judged by the
/// same fidelity numbers, so they can be compared honestly. <c>Metric</c> = on-screen distance is meant to be read as embedding distance.
/// </summary>
public static class CloudArrangements
{
    public const string Mds = "mds", Umap = "umap", Pca = "pca", Tree = "tree";
    public const string Default = Mds;

    public sealed record Info(string Id, string Label, bool Metric);

    public static readonly IReadOnlyList<Info> All =
    [
        new(Mds, "Distances (MDS)", true),
        new(Umap, "Neighbourhoods (UMAP)", false),
        new(Pca, "PCA", true),
        new(Tree, "Tree (force)", false),
    ];

    public static readonly string[] Ids = All.Select(a => a.Id).ToArray();
    public static bool IsKnown(string id) => Ids.Contains(id, StringComparer.Ordinal);
}

/// <summary>The RAPTOR structure the tree arrangement needs: every node's parents and layer, and the MDS leaf positions it starts from.</summary>
public sealed record CloudTreeInput(IReadOnlyDictionary<int, int[]> Parents, IReadOnlyDictionary<int, int> Layer, IReadOnlyDictionary<int, (float X, float Y)> Seed);

public sealed record CloudLayoutOptions
{
    /// <summary>Which arrangement <see cref="CloudLayout.Compute"/> lays out.</summary>
    public string Arrangement { get; init; } = CloudArrangements.Default;

    // ---- UMAP (neighbourhoods) ----
    /// <summary>UMAP n_neighbors (umap-learn default).</summary>
    public int NNeighbors { get; init; } = 15;
    /// <summary>UMAP min_dist (umap-learn default): tight enough for clusters to read as islands.</summary>
    public double MinDist { get; init; } = 0.1;
    /// <summary>
    /// Above this many build leaves UMAP runs on an even, index-ordered sample; the rest are placed like new memories. Capped at
    /// <see cref="UmapSequentialLimit"/>: the bot's UMAP stays single-threaded up to there (its exact kNN switches to an unbounded
    /// <c>Parallel.For</c> above n² = 4M), and the placement that covers the rest runs on the caller's bounded scheduler.
    /// </summary>
    public int MaxUmapPoints { get; init; } = UmapSequentialLimit;

    public const int UmapSequentialLimit = 2000;
    /// <summary>Neighbours a placed memory is averaged from (UMAP; also the MDS placement's starting point).</summary>
    public int PlaceK { get; init; } = 6;
    /// <summary>Softmax temperature over cosine similarity for the placement weights.</summary>
    public double PlaceTemperature { get; init; } = 0.04;
    public long Seed { get; init; } = 0x5EED_C10D;

    // ---- metric MDS (distances) ----
    /// <summary>All-pairs SMACOF runs on at most this many build leaves (an even sample above); the rest minimise their own stress against them.</summary>
    public int MaxMdsPoints { get; init; } = MdsAllPairsLimit;
    public const int MdsAllPairsLimit = 3000;
    /// <summary>Landmarks (max-min chosen) of the classical (landmark) MDS that starts SMACOF.</summary>
    public int Landmarks { get; init; } = 400;
    public int SmacofIterations { get; init; } = 200;
    /// <summary>SMACOF stops when an iteration lowers the raw stress by less than this fraction.</summary>
    public double SmacofTolerance { get; init; } = 1e-5;
    /// <summary>Majorisation steps when placing one memory against the fixed anchors.</summary>
    public int PlaceIterations { get; init; } = 60;

    // ---- PCA ----
    /// <summary>The principal axes are fitted on at most this many build leaves (an even sample); every leaf is projected.</summary>
    public int MaxPcaPoints { get; init; } = 5000;

    // ---- tree (force) ----
    public int TreeIterations { get; init; } = 300;

    public string UmapVersion => $"umap{NNeighbors}-{MinDist:0.###}-{MaxUmapPoints}-{PlaceK}-{PlaceTemperature:0.###}-{Seed:x}";
    public string MdsVersion => $"mds{MaxMdsPoints}-{Landmarks}-{SmacofIterations}-{SmacofTolerance:0.#######}-{PlaceIterations}";
    public string PcaVersion => $"pca{MaxPcaPoints}-{Seed:x}";
    public string TreeVersion => $"tree{TreeIterations}-{MdsVersion}";

    /// <summary>Changes whenever the algorithm or its parameters change, so cached layouts are not reused across versions.</summary>
    public string Version => Arrangement switch
    {
        CloudArrangements.Umap => UmapVersion,
        CloudArrangements.Pca => PcaVersion,
        CloudArrangements.Tree => TreeVersion,
        _ => MdsVersion,
    };
}

/// <summary>
/// The fitted state of one arrangement for one RAPTOR build: the anchor leaves it was fitted on (the build's leaves, <c>InsertedSeq == 0</c>,
/// or an even sample of them) at their unit-square positions, plus what placing another leaf needs (the MDS scale, the PCA axes, the
/// tree's summary positions). Every other leaf (sampled-out build leaves and every memory inserted since the build) is placed from this
/// state and its own vector only, so placing new memories one refresh at a time gives exactly the layout a restart computes at once.
/// </summary>
public sealed class CloudAnchors
{
    public required long BuildId { get; init; }
    public required string Arrangement { get; init; }
    public required string Version { get; init; }
    public required int Dimensions { get; init; }
    /// <summary>Anchor leaf indexes, unit-normalised vectors and unit-square positions (x, y interleaved).</summary>
    public required int[] Index { get; init; }
    public required float[][] Vectors { get; init; }
    public required float[] Xy { get; init; }
    public required ulong[] Hash { get; init; }
    /// <summary>Positions of placed (non-anchor) leaves, by index, with the hash of the vector they were placed from.</summary>
    public Dictionary<int, (float X, float Y, ulong Hash)> Placed { get; } = new();
    public double FitMs { get; init; }
    /// <summary>Raw layout coordinates → unit square: <c>x * S + Ox</c>.</summary>
    public (double S, double Ox, double Oy) Transform { get; init; } = (1, 0, 0);
    /// <summary>PCA: the anchors' mean and the two principal axes (unit vectors), and the share of variance each explains.</summary>
    public float[]? Mean { get; init; }
    public float[][]? Axes { get; init; }
    public double[]? VarianceExplained { get; init; }
    /// <summary>Tree: summary positions (unit square) and the leaf → parent spring length (unit square).</summary>
    public Dictionary<int, (float X, float Y)>? SummaryXy { get; init; }
    public double LeafRadius { get; init; }
    /// <summary>MDS: the final normalised stress of the anchor fit (diagnostic).</summary>
    public double FitStress { get; init; }
    public int FitIterations { get; init; }
}

public sealed record CloudLayoutResult(CloudAnchors Anchors, Dictionary<int, (float X, float Y)> Positions, int PlacedNow, bool Full, double Ms);

/// <summary>
/// The memory cloud's 2-D arrangements (architecture.md, "Memory cloud"), normalised to the unit square:
/// <list type="bullet">
/// <item><b>mds</b> (default): metric MDS on cosine distance (1 − cos) — landmark MDS start, all-pairs SMACOF — so on-screen distance
/// is embedding distance up to one scale (<see cref="CloudMds"/>).</item>
/// <item><b>umap</b>: the bot's own <see cref="UmapReducer"/> (cosine, spectral init, seeded single-threaded SGD) — neighbourhoods, not distances.</item>
/// <item><b>pca</b>: the top two principal axes (<see cref="CloudPca"/>).</item>
/// <item><b>tree</b>: force-directed along the RAPTOR parent links, summaries as nodes, started from the MDS picture (<see cref="CloudTreeLayout"/>).</item>
/// </list>
/// Pure: no I/O, no clocks. Parallel loops run on <see cref="TaskScheduler.Current"/>, so the caller bounds the CPU by the scheduler it
/// runs this on (the UMAP itself is single-threaded at ≤ 2000 points).
/// </summary>
public static class CloudLayout
{
    public static CloudLayoutResult Compute(long buildId, IReadOnlyList<CloudLeaf> leaves, CloudAnchors? previous, CloudLayoutOptions o, CancellationToken ct = default,
        CloudTreeInput? tree = null)
    {
        if (o.Arrangement == CloudArrangements.Tree && tree == null) throw new ArgumentException("the tree arrangement needs the RAPTOR links and the MDS positions");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int dims = Dimension(leaves);
        var usable = leaves.Where(l => l.Vector.Length == dims && dims > 0).ToList();
        bool full = previous == null || previous.BuildId != buildId || previous.Version != o.Version || previous.Dimensions != dims
            || previous.Arrangement != o.Arrangement;
        var anchors = full ? Fit(buildId, usable, dims, o, tree, ct) : previous!;
        var anchorSlot = new Dictionary<int, int>(anchors.Index.Length);
        for (int i = 0; i < anchors.Index.Length; i++) anchorSlot[anchors.Index[i]] = i;

        var pos = new Dictionary<int, (float X, float Y)>(leaves.Count);
        var toPlace = new List<(CloudLeaf Leaf, float[] V, ulong H)>();
        foreach (var l in usable)
        {
            var v = VectorMath.Normalized(l.Vector);
            ulong h = LeafHash(v, l.Index, o, tree);
            if (anchorSlot.TryGetValue(l.Index, out var s) && anchors.Hash[s] == h)
                pos[l.Index] = (anchors.Xy[2 * s], anchors.Xy[2 * s + 1]);
            else if (anchors.Placed.TryGetValue(l.Index, out var p) && p.Hash == h)
                pos[l.Index] = (p.X, p.Y);
            else
                toPlace.Add((l, v, h));
        }
        var placed = new (float X, float Y)[toPlace.Count];
        Parallel.For(0, toPlace.Count, new ParallelOptions { CancellationToken = ct, TaskScheduler = TaskScheduler.Current }, i =>
            placed[i] = PlaceAny(anchors, toPlace[i].V, toPlace[i].Leaf.Index, o, tree));
        // Forget placements of leaves that are gone, then record the new ones (the cache only grows with the live tree).
        var live = new HashSet<int>(leaves.Select(l => l.Index));
        foreach (var gone in anchors.Placed.Keys.Where(k => !live.Contains(k)).ToList()) anchors.Placed.Remove(gone);
        for (int i = 0; i < toPlace.Count; i++)
        {
            anchors.Placed[toPlace[i].Leaf.Index] = (placed[i].X, placed[i].Y, toPlace[i].H);
            pos[toPlace[i].Leaf.Index] = placed[i];
        }
        return new CloudLayoutResult(anchors, pos, toPlace.Count, full, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>The most common vector length (a stray mis-sized vector never decides it).</summary>
    public static int Dimension(IReadOnlyList<CloudLeaf> leaves) =>
        leaves.Where(l => l.Vector.Length > 0).GroupBy(l => l.Vector.Length).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).Select(g => g.Key).FirstOrDefault();

    /// <summary>The build's own leaves, index-ordered (a tree with only inserted leaves, never the case after a build, falls back to all), evenly sampled down to <paramref name="max"/>.</summary>
    private static List<CloudLeaf> AnchorPool(List<CloudLeaf> usable, int max)
    {
        var pool = usable.Where(l => l.InsertedSeq == 0).OrderBy(l => l.Index).ToList();
        if (pool.Count < 3) pool = usable.OrderBy(l => l.Index).ToList();
        max = Math.Max(3, max);
        if (pool.Count > max)
        {
            double step = (double)pool.Count / max;
            pool = Enumerable.Range(0, max).Select(i => pool[(int)(i * step)]).ToList();
        }
        return pool;
    }

    private static CloudAnchors Fit(long buildId, List<CloudLeaf> usable, int dims, CloudLayoutOptions o, CloudTreeInput? tree, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int max = o.Arrangement switch
        {
            CloudArrangements.Umap => Math.Clamp(o.MaxUmapPoints, 3, CloudLayoutOptions.UmapSequentialLimit),
            CloudArrangements.Pca => Math.Max(3, o.MaxPcaPoints),
            CloudArrangements.Tree => int.MaxValue, // a spring per link and grid repulsion: linear in the leaves
            _ => Math.Clamp(o.MaxMdsPoints, 3, CloudLayoutOptions.MdsAllPairsLimit),
        };
        var pool = AnchorPool(usable, max);
        var vecs = pool.Select(l => VectorMath.Normalized(l.Vector)).ToArray();
        int n = vecs.Length;
        var xy = new float[n * 2];
        var hashes = new ulong[n];
        for (int i = 0; i < n; i++) hashes[i] = LeafHash(vecs[i], pool[i].Index, o, tree);
        var index = pool.Select(l => l.Index).ToArray();
        CloudAnchors Make(double[] raw, Func<CloudAnchors, CloudAnchors>? extra = null)
        {
            var t = n >= 1 ? Normalise(raw, xy) : (1, 0, 0);
            var a = new CloudAnchors
            {
                BuildId = buildId, Arrangement = o.Arrangement, Version = o.Version, Dimensions = dims, Index = index, Vectors = vecs, Xy = xy, Hash = hashes,
                FitMs = sw.Elapsed.TotalMilliseconds, Transform = t,
            };
            return extra?.Invoke(a) ?? a;
        }
        if (n < 3)
        {
            var raw = new double[n * 2];
            for (int i = 0; i < n; i++) { raw[2 * i] = 0.3 + 0.4 * i; raw[2 * i + 1] = 0.5; }
            return Make(raw);
        }
        ct.ThrowIfCancellationRequested();
        switch (o.Arrangement)
        {
            case CloudArrangements.Umap:
            {
                var umap = new UmapReducer(new UmapOptions { NNeighbors = Math.Min(o.NNeighbors, n - 1), NComponents = 2, MinDist = o.MinDist, Seed = o.Seed });
                return Make(umap.FitTransform(vecs));
            }
            case CloudArrangements.Pca:
            {
                var fit = CloudPca.Fit(vecs, o.Seed, ct);
                var raw = new double[n * 2];
                for (int i = 0; i < n; i++) (raw[2 * i], raw[2 * i + 1]) = fit.Project(vecs[i]);
                return Make(raw, a => new CloudAnchors
                {
                    BuildId = a.BuildId, Arrangement = a.Arrangement, Version = a.Version, Dimensions = a.Dimensions, Index = a.Index, Vectors = a.Vectors, Xy = a.Xy,
                    Hash = a.Hash, FitMs = sw.Elapsed.TotalMilliseconds, Transform = a.Transform, Mean = fit.Mean, Axes = fit.Axes, VarianceExplained = fit.VarianceExplained,
                });
            }
            case CloudArrangements.Tree:
            {
                var fit = CloudTreeLayout.Fit(index, tree!, o, ct);
                var raw = new double[n * 2];
                for (int i = 0; i < n; i++) (raw[2 * i], raw[2 * i + 1]) = fit.Leaves[index[i]];
                // Leaves and summaries share one frame: normalise over both.
                var all = new double[(n + fit.Summaries.Count) * 2];
                Array.Copy(raw, all, raw.Length);
                int k = n;
                var sIds = fit.Summaries.Keys.OrderBy(x => x).ToArray();
                foreach (var s in sIds) { all[2 * k] = fit.Summaries[s].X; all[2 * k + 1] = fit.Summaries[s].Y; k++; }
                var allXy = new float[all.Length];
                var t = Normalise(all, allXy);
                Array.Copy(allXy, xy, xy.Length);
                var sxy = new Dictionary<int, (float X, float Y)>(sIds.Length);
                for (int j = 0; j < sIds.Length; j++) sxy[sIds[j]] = (allXy[2 * (n + j)], allXy[2 * (n + j) + 1]);
                return new CloudAnchors
                {
                    BuildId = buildId, Arrangement = o.Arrangement, Version = o.Version, Dimensions = dims, Index = index, Vectors = vecs, Xy = xy, Hash = hashes,
                    FitMs = sw.Elapsed.TotalMilliseconds, Transform = t, SummaryXy = sxy, LeafRadius = fit.LeafLength * t.S,
                };
            }
            default:
            {
                var fit = CloudMds.Fit(vecs, o, ct);
                return Make(fit.Xy, a => new CloudAnchors
                {
                    BuildId = a.BuildId, Arrangement = a.Arrangement, Version = a.Version, Dimensions = a.Dimensions, Index = a.Index, Vectors = a.Vectors, Xy = a.Xy,
                    Hash = a.Hash, FitMs = sw.Elapsed.TotalMilliseconds, Transform = a.Transform, FitStress = fit.Stress, FitIterations = fit.Iterations,
                });
            }
        }
    }

    /// <summary>Uniform scale into [0.02, 0.98]² (aspect kept, centred), rounded to 1e-4 so the wire stays small. Returns the transform.</summary>
    internal static (double S, double Ox, double Oy) Normalise(double[] e, float[] xy)
    {
        int n = e.Length / 2;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            minX = Math.Min(minX, e[2 * i]); maxX = Math.Max(maxX, e[2 * i]);
            minY = Math.Min(minY, e[2 * i + 1]); maxY = Math.Max(maxY, e[2 * i + 1]);
        }
        double span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
        double s = 0.96 / span, ox = 0.5 - (minX + maxX) / 2 * s, oy = 0.5 - (minY + maxY) / 2 * s;
        for (int i = 0; i < n; i++)
        {
            xy[2 * i] = Round(e[2 * i] * s + ox);
            xy[2 * i + 1] = Round(e[2 * i + 1] * s + oy);
        }
        return (s, ox, oy);
    }

    private static (float X, float Y) PlaceAny(CloudAnchors a, float[] v, int index, CloudLayoutOptions o, CloudTreeInput? tree)
    {
        switch (a.Arrangement)
        {
            case CloudArrangements.Umap:
                return Place(a, v, index, o);
            case CloudArrangements.Pca:
            {
                var (px, py) = new CloudPca.Result(a.Mean!, a.Axes!, a.VarianceExplained!).Project(v);
                return Clamp(px * a.Transform.S + a.Transform.Ox, py * a.Transform.S + a.Transform.Oy);
            }
            case CloudArrangements.Tree:
                return CloudTreeLayout.Place(a, index, tree!);
            default:
                return CloudMds.Place(a, v, o);
        }
    }

    /// <summary>
    /// UMAP placement: the softmax(cos / τ)-weighted mean of its <see cref="CloudLayoutOptions.PlaceK"/> most similar anchors, nudged by a
    /// tiny deterministic offset (from its index) scaled by how far it is from its best match, so near-duplicates don't stack.
    /// </summary>
    public static (float X, float Y) Place(CloudAnchors a, float[] v, int index, CloudLayoutOptions o)
    {
        int n = a.Index.Length;
        if (n == 0) return (0.5f, 0.5f);
        var sims = new float[n];
        for (int j = 0; j < n; j++) sims[j] = VectorMath.Dot(v, a.Vectors[j]);
        var (x, y, best) = SoftmaxMean(a, sims, o);
        ulong h = Mix((ulong)(uint)index * 0x9E3779B97F4A7C15UL);
        double angle = (h & 0xFFFF) / 65536.0 * 2 * Math.PI;
        double r = 0.002 + 0.02 * Math.Clamp(1 - best, 0, 1) * ((h >> 16 & 0xFFFF) / 65536.0);
        return Clamp(x + r * Math.Cos(angle), y + r * Math.Sin(angle));
    }

    /// <summary>The softmax(cos / τ)-weighted mean position of the K most similar anchors (and the best similarity).</summary>
    internal static (double X, double Y, float Best) SoftmaxMean(CloudAnchors a, float[] sims, CloudLayoutOptions o)
    {
        int n = sims.Length;
        int k = Math.Min(o.PlaceK, n);
        var bestS = new float[k];
        var bestI = new int[k];
        Array.Fill(bestS, float.NegativeInfinity);
        for (int j = 0; j < n; j++)
        {
            float s = sims[j];
            if (s <= bestS[k - 1]) continue;
            int p = k - 1;
            while (p > 0 && bestS[p - 1] < s) { bestS[p] = bestS[p - 1]; bestI[p] = bestI[p - 1]; p--; }
            bestS[p] = s;
            bestI[p] = j;
        }
        double wx = 0, wy = 0, wsum = 0;
        for (int t = 0; t < k; t++)
        {
            if (float.IsNegativeInfinity(bestS[t])) continue;
            double w = Math.Exp((bestS[t] - bestS[0]) / o.PlaceTemperature);
            wx += w * a.Xy[2 * bestI[t]];
            wy += w * a.Xy[2 * bestI[t] + 1];
            wsum += w;
        }
        return (wx / wsum, wy / wsum, bestS[0]);
    }

    internal static (float X, float Y) Clamp(double x, double y) => (Round(Math.Clamp(x, 0, 1)), Round(Math.Clamp(y, 0, 1)));

    internal static float Round(double v) => (float)(Math.Round(v * 10000) / 10000);

    /// <summary>What a leaf's placement depends on: its vector (and, for the tree arrangement, its parents).</summary>
    private static ulong LeafHash(float[] v, int index, CloudLayoutOptions o, CloudTreeInput? tree)
    {
        ulong h = HashOf(v);
        if (o.Arrangement == CloudArrangements.Tree && tree != null && tree.Parents.TryGetValue(index, out var ps))
            foreach (var p in ps) h = Mix(h ^ (ulong)(uint)p);
        return h;
    }

    /// <summary>FNV-1a over the vector's bits: a re-embedded memory (same index, new vector) is placed again.</summary>
    public static ulong HashOf(float[] v)
    {
        ulong h = 14695981039346656037UL;
        foreach (var f in v)
        {
            h ^= BitConverter.SingleToUInt32Bits(f);
            h *= 1099511628211UL;
        }
        return h;
    }

    internal static ulong Mix(ulong z)
    {
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }
}

/// <summary>
/// Runs work on at most <c>threads</c> pool threads. A <see cref="Parallel"/> loop inside is bounded only when it passes
/// <c>TaskScheduler = TaskScheduler.Current</c> (a default-options loop ignores the ambient scheduler's limit), as the placement does.
/// </summary>
public sealed class BoundedRunner(int threads)
{
    private readonly TaskScheduler _scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Math.Max(1, threads)).ConcurrentScheduler;

    public int Threads { get; } = Math.Max(1, threads);

    public Task<T> Run<T>(Func<T> work, CancellationToken ct = default) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.DenyChildAttach, _scheduler);
}
