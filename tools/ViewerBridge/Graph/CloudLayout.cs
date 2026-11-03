using MineClaw.Raptor.Numerics;
using MineClaw.Raptor.Umap;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>One RAPTOR leaf to place: its index, its stored embedding and its insertion sequence (0 = part of the last build).</summary>
public sealed record CloudLeaf(int Index, float[] Vector, long InsertedSeq);

public sealed record CloudLayoutOptions
{
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
    /// <summary>Neighbours a placed memory is averaged from.</summary>
    public int PlaceK { get; init; } = 6;
    /// <summary>Softmax temperature over cosine similarity for the placement weights.</summary>
    public double PlaceTemperature { get; init; } = 0.04;
    public long Seed { get; init; } = 0x5EED_C10D;
    /// <summary>Changes whenever the algorithm or its parameters change, so cached layouts are not reused across versions.</summary>
    public string Version => $"umap{NNeighbors}-{MinDist:0.###}-{MaxUmapPoints}-{PlaceK}-{PlaceTemperature:0.###}-{Seed:x}";
}

/// <summary>
/// The anchor layout of one RAPTOR build: UMAP over the build's leaves (those with <c>InsertedSeq == 0</c>), or an even sample of
/// them when there are more than <see cref="CloudLayoutOptions.MaxUmapPoints"/>. Every other leaf (sampled-out build leaves and
/// every memory inserted since the build) is placed from its nearest anchors only, so a leaf's position depends on the build and its
/// own vector and nothing else: placing new memories one refresh at a time gives exactly the layout a restart computes at once.
/// </summary>
public sealed class CloudAnchors
{
    public required long BuildId { get; init; }
    public required string Version { get; init; }
    public required int Dimensions { get; init; }
    /// <summary>Anchor leaf indexes, unit-normalised vectors and unit-square positions (x, y interleaved).</summary>
    public required int[] Index { get; init; }
    public required float[][] Vectors { get; init; }
    public required float[] Xy { get; init; }
    public required ulong[] Hash { get; init; }
    /// <summary>Positions of placed (non-anchor) leaves, by index, with the hash of the vector they were placed from.</summary>
    public Dictionary<int, (float X, float Y, ulong Hash)> Placed { get; } = new();
    public double UmapMs { get; init; }
}

public sealed record CloudLayoutResult(CloudAnchors Anchors, Dictionary<int, (float X, float Y)> Positions, int PlacedNow, bool Full, double Ms);

/// <summary>
/// The memory cloud's 2-D projection (architecture.md, "Memory cloud"): the bot's own <see cref="UmapReducer"/> (cosine,
/// spectral init, seeded single-threaded SGD) over the RAPTOR leaf embeddings, normalised to the unit square, plus deterministic
/// nearest-anchor placement for new memories. Pure: no I/O, no clocks; the placement loop runs on <see cref="TaskScheduler.Current"/>, so the
/// caller bounds the CPU by the scheduler it runs this on (the UMAP itself is single-threaded at ≤ 2000 points).
/// </summary>
public static class CloudLayout
{
    public static CloudLayoutResult Compute(long buildId, IReadOnlyList<CloudLeaf> leaves, CloudAnchors? previous, CloudLayoutOptions o, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int dims = Dimension(leaves);
        var usable = leaves.Where(l => l.Vector.Length == dims && dims > 0).ToList();
        bool full = previous == null || previous.BuildId != buildId || previous.Version != o.Version || previous.Dimensions != dims;
        var anchors = full ? BuildAnchors(buildId, usable, dims, o, ct) : previous!;
        var anchorSlot = new Dictionary<int, int>(anchors.Index.Length);
        for (int i = 0; i < anchors.Index.Length; i++) anchorSlot[anchors.Index[i]] = i;

        var pos = new Dictionary<int, (float X, float Y)>(leaves.Count);
        var toPlace = new List<(CloudLeaf Leaf, float[] V, ulong H)>();
        foreach (var l in usable)
        {
            var v = VectorMath.Normalized(l.Vector);
            ulong h = HashOf(v);
            if (anchorSlot.TryGetValue(l.Index, out var s) && anchors.Hash[s] == h)
                pos[l.Index] = (anchors.Xy[2 * s], anchors.Xy[2 * s + 1]);
            else if (anchors.Placed.TryGetValue(l.Index, out var p) && p.Hash == h)
                pos[l.Index] = (p.X, p.Y);
            else
                toPlace.Add((l, v, h));
        }
        var placed = new (float X, float Y)[toPlace.Count];
        Parallel.For(0, toPlace.Count, new ParallelOptions { CancellationToken = ct, TaskScheduler = TaskScheduler.Current }, i =>
            placed[i] = Place(anchors, toPlace[i].V, toPlace[i].Leaf.Index, o));
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

    private static CloudAnchors BuildAnchors(long buildId, List<CloudLeaf> usable, int dims, CloudLayoutOptions o, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // The build's own leaves; a tree that has only inserted leaves (never the case after a build) falls back to all of them.
        var pool = usable.Where(l => l.InsertedSeq == 0).OrderBy(l => l.Index).ToList();
        if (pool.Count < 3) pool = usable.OrderBy(l => l.Index).ToList();
        int max = Math.Clamp(o.MaxUmapPoints, 3, CloudLayoutOptions.UmapSequentialLimit);
        if (pool.Count > max)
        {
            double step = (double)pool.Count / max;
            pool = Enumerable.Range(0, max).Select(i => pool[(int)(i * step)]).ToList();
        }
        var vecs = pool.Select(l => VectorMath.Normalized(l.Vector)).ToArray();
        int n = vecs.Length;
        var xy = new float[n * 2];
        if (n >= 3)
        {
            ct.ThrowIfCancellationRequested();
            var umap = new UmapReducer(new UmapOptions { NNeighbors = Math.Min(o.NNeighbors, n - 1), NComponents = 2, MinDist = o.MinDist, Seed = o.Seed });
            var e = umap.FitTransform(vecs);
            Normalise(e, xy);
        }
        else
        {
            for (int i = 0; i < n; i++) { xy[2 * i] = 0.3f + 0.4f * i; xy[2 * i + 1] = 0.5f; }
        }
        return new CloudAnchors
        {
            BuildId = buildId, Version = o.Version, Dimensions = dims, Index = pool.Select(l => l.Index).ToArray(), Vectors = vecs, Xy = xy,
            Hash = vecs.Select(HashOf).ToArray(), UmapMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>Uniform scale into [0.02, 0.98]² (aspect kept, centred), rounded to 1e-4 so the wire stays small.</summary>
    internal static void Normalise(double[] e, float[] xy)
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
    }

    /// <summary>
    /// A memory's spot: the softmax(cos / τ)-weighted mean of its <see cref="CloudLayoutOptions.PlaceK"/> most similar anchors, nudged
    /// by a tiny deterministic offset (from its index) scaled by how far it is from its best match, so near-duplicates don't stack.
    /// </summary>
    public static (float X, float Y) Place(CloudAnchors a, float[] v, int index, CloudLayoutOptions o)
    {
        int n = a.Index.Length;
        if (n == 0) return (0.5f, 0.5f);
        int k = Math.Min(o.PlaceK, n);
        var bestS = new float[k];
        var bestI = new int[k];
        Array.Fill(bestS, float.NegativeInfinity);
        for (int j = 0; j < n; j++)
        {
            float s = VectorMath.Dot(v, a.Vectors[j]);
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
        double x = wx / wsum, y = wy / wsum;
        ulong h = Mix((ulong)(uint)index * 0x9E3779B97F4A7C15UL);
        double angle = (h & 0xFFFF) / 65536.0 * 2 * Math.PI;
        double r = 0.002 + 0.02 * Math.Clamp(1 - bestS[0], 0, 1) * ((h >> 16 & 0xFFFF) / 65536.0);
        return (Round(Math.Clamp(x + r * Math.Cos(angle), 0, 1)), Round(Math.Clamp(y + r * Math.Sin(angle), 0, 1)));
    }

    private static float Round(double v) => (float)(Math.Round(v * 10000) / 10000);

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

    private static ulong Mix(ulong z)
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
