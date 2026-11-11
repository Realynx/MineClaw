using MineClaw.ViewerBridge.Data;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>The leaves (columns, same order in every array). <c>Xy</c> is x, y interleaved in the unit square.</summary>
public sealed record CloudPointsDto(int[] Id, float[] Xy, int[] Cluster, int[] Kind, string[] Title, int[][] Parents, bool[] Inserted);
/// <summary>Summaries sit at the mean of their children (bottom-up; the tree arrangement lays them out itself), for the link overlay and region labels.</summary>
public sealed record CloudSummariesDto(int[] Id, float[] Xy, int[] Layer, int[] Cluster, int[][] Parents);
public sealed record CloudClusterDto(int Id, int Node, int Layer, string Label, IReadOnlyList<string> Terms, string Summary, int Size, double X, double Y, double Spread);
public sealed record MemoryCloudDto(string Status, string Version, string StateKey, string? Note, int ClusterLayer, IReadOnlyList<string> Kinds, CloudPointsDto Points,
    CloudSummariesDto Summaries, IReadOnlyList<CloudClusterDto> Clusters, int Anchors, int PlacedSinceBuild, double? ComputeMs, DateTimeOffset? ComputedAt,
    string? ComputingStateKey, string Arrangement = CloudArrangements.Default, CloudFidelityDto? Fidelity = null,
    IReadOnlyList<CloudArrangementStatusDto>? Arrangements = null);
/// <summary>One arrangement's state for the current tree: ready, computing, queued, error, or idle (not asked for since the tree changed).</summary>
public sealed record CloudArrangementStatusDto(string Id, string Label, bool Metric, string Status);
/// <summary>The cosine distance between two RAPTOR nodes' stored embeddings (vectors never leave the bridge).</summary>
public sealed record CloudDistanceDto(int A, int B, string Metric, double Distance, double Similarity);

/// <summary>
/// The memory cloud (architecture.md, "Memory activity tab" → Cloud): every RAPTOR leaf at its 2-D position in one of several
/// arrangements (<see cref="CloudArrangements"/>: metric MDS by default, UMAP, PCA, the tree's own force layout), coloured by the bot's
/// own clusters at one summary layer, with summaries placed at their children's mean (the tree arrangement places them itself). Each
/// arrangement is computed lazily (on its first request) on one background queue on <see cref="BridgeOptions.CloudThreads"/> threads and
/// cached per tree state, with its fidelity numbers; a request never waits: it gets the previous layout (or none) with
/// <c>status: "computing"</c> and polls.
/// </summary>
public sealed class MemoryCloudService(BridgeConfig config, ILogger<MemoryCloudService> log, TimeProvider time)
{
    private sealed class Slot
    {
        public CloudAnchors? Anchors;
        public MemoryCloudDto? Ready;
        /// <summary>Leaf positions of <see cref="Ready"/> (the tree arrangement starts from the MDS ones).</summary>
        public Dictionary<int, (float X, float Y)>? Positions;
        public string? Failed;
        /// <summary>Set when <see cref="Failed"/> was a refusal for want of memory: asked again after this, the layout is retried.</summary>
        public DateTimeOffset? RetryAfter;
        public string? FailedNote;
    }

    /// <summary>A layout refused before it started because the process could not afford it.</summary>
    internal sealed class ShortOfMemoryException(string message) : Exception(message);

    /// <summary>How long a layout refused for want of memory waits before a request may try it again.</summary>
    internal static readonly TimeSpan ShortOfMemoryRetry = TimeSpan.FromSeconds(60);

    /// <summary>What must stay free beside a layout's own estimate (the live stream, requests, SQLite).</summary>
    internal const long SafetyBytes = 96L * 1024 * 1024;

    /// <summary>Bytes the process may still allocate before its managed heap limit (tests replace it).</summary>
    internal Func<long> Headroom { get; set; } = DefaultHeadroom;

    private static long DefaultHeadroom() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes - GC.GetTotalMemory(false);

    /// <summary>
    /// The transient bytes one layout of <paramref name="leaves"/> leaves of <paramref name="dims"/> dimensions needs at its peak: the
    /// fit's own matrices (MDS: the anchors' all-pairs distances; UMAP: its kNN; PCA: the covariance), the anchors' unit vectors, and
    /// per leaf the tree read, the positions and the wire arrays. The leaf vectors themselves are the tree's (shared, not copied).
    /// </summary>
    internal static long EstimateBytes(string arrangement, int leaves, int dims, CloudLayoutOptions o)
    {
        long l = Math.Max(0, leaves), d = Math.Max(1, dims);
        long perLeaf = 160 * l;
        long Anchors(int max) => Math.Min(l, max);
        long fit = arrangement switch
        {
            CloudArrangements.Umap => Anchors(Math.Clamp(o.MaxUmapPoints, 3, CloudLayoutOptions.UmapSequentialLimit)) is var u ? u * u * 8 + u * d * 4 : 0,
            CloudArrangements.Pca => Anchors(Math.Max(3, o.MaxPcaPoints)) * d * 4 + d * d * 8,
            // Linear in the leaves, plus the MDS picture it starts from when that is not cached.
            CloudArrangements.Tree => 400 * l + EstimateBytes(CloudArrangements.Mds, leaves, dims, o) - perLeaf,
            _ => Anchors(Math.Clamp(o.MaxMdsPoints, 3, CloudLayoutOptions.MdsAllPairsLimit)) is var m ? m * m * 4 + m * d * 4 : 0,
        };
        return fit + perLeaf;
    }

    private readonly object _lock = new();
    private readonly BoundedRunner _runner = new(config.Bridge.CloudThreads);
    private readonly CloudLayoutOptions _options = new()
    {
        MaxUmapPoints = Math.Clamp(config.Bridge.CloudMaxUmapPoints, 100, CloudLayoutOptions.UmapSequentialLimit),
        MaxMdsPoints = Math.Clamp(config.Bridge.CloudMaxMdsPoints, 100, CloudLayoutOptions.MdsAllPairsLimit),
    };
    private readonly Dictionary<string, Slot> _slots = CloudArrangements.Ids.ToDictionary(a => a, _ => new Slot());
    private readonly List<string> _queue = new();
    private string? _running;
    private bool _looping;
    private MemoryBank? _bank;
    private Task _job = Task.CompletedTask;

    public CloudLayoutOptions Options => _options;
    public CloudLayoutOptions OptionsFor(string arrangement) => _options with { Arrangement = arrangement };

    /// <summary>The current <paramref name="arrangement"/> for <paramref name="bank"/>; queues (at most once) a background computation of it when it is missing.</summary>
    public MemoryCloudDto Get(MemoryBank bank, string arrangement = CloudArrangements.Default)
    {
        if (!CloudArrangements.IsKnown(arrangement)) throw new ArgumentException($"arrangement must be one of {string.Join("|", CloudArrangements.Ids)}");
        if (!bank.HasTree)
        {
            lock (_lock) return Empty(bank, "empty", bank.TreeNote ?? "no RAPTOR tree stored in this db", arrangement) with { Arrangements = Statuses("none") };
        }
        string key = bank.StateKey;
        string version = VersionOf(key, arrangement);
        lock (_lock)
        {
            var slot = _slots[arrangement];
            if (slot.Ready is { } r && r.Version == version) return r with { Arrangements = Statuses(key) };
            if (slot.Failed == version && slot.RetryAfter is { } retry && time.GetUtcNow() >= retry) slot.Failed = null; // short of memory: try again
            if (slot.Failed == version)
                return (slot.Ready ?? Empty(bank, "error", "", arrangement)) with
                {
                    Status = "error", Note = slot.FailedNote ?? "the memory cloud layout failed (see the bridge log)", Arrangements = Statuses(key),
                };
            _bank = bank;
            Enqueue(arrangement);
            var stale = slot.Ready;
            var dto = stale != null
                ? stale with { Status = "computing", ComputingStateKey = key }
                : Empty(bank, "computing", "laying out the memory cloud", arrangement) with { ComputingStateKey = key };
            return dto with { Arrangements = Statuses(key) };
        }
    }

    /// <summary>Queues a computation of <paramref name="arrangement"/> (once) and starts the loop when it isn't running. Caller holds the lock.</summary>
    private void Enqueue(string arrangement)
    {
        if (_running != arrangement && !_queue.Contains(arrangement)) _queue.Add(arrangement);
        if (_looping) return;
        _looping = true;
        _job = Task.Run(LoopAsync);
    }

    /// <summary>The cosine distance between two nodes' stored embeddings, or null when either is unknown or has no vector.</summary>
    public static CloudDistanceDto? Distance(MemoryBank bank, int a, int b) =>
        bank.ReadTree(t =>
        {
            if (!t.TryGet(a, out var na) || !t.TryGet(b, out var nb)) return null;
            var d = CloudFidelity.CosineDistance(na.Embedding, nb.Embedding);
            return d is { } v ? new CloudDistanceDto(a, b, "cosine", Math.Round(v, 5), Math.Round(1 - v, 5)) : null;
        }, (CloudDistanceDto?)null);

    /// <summary>Waits until the queue is empty (tests).</summary>
    public async Task Idle()
    {
        while (true)
        {
            Task job;
            lock (_lock)
            {
                if (!_looping) return;
                job = _job;
            }
            await job;
        }
    }

    private string VersionOf(string key, string arrangement) => $"{key}|{OptionsFor(arrangement).Version}";

    /// <summary>Caller holds the lock.</summary>
    private List<CloudArrangementStatusDto> Statuses(string key) =>
        CloudArrangements.All.Select(a => new CloudArrangementStatusDto(a.Id, a.Label, a.Metric,
            _slots[a.Id].Ready?.Version == VersionOf(key, a.Id) ? "ready"
            : _running == a.Id ? "computing"
            : _queue.Contains(a.Id) ? "queued"
            : _slots[a.Id].Failed == VersionOf(key, a.Id) ? "error"
            : "idle")).ToList();

    private async Task LoopAsync()
    {
        while (true)
        {
            string arrangement;
            MemoryBank bank;
            string key;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _running = null;
                    _looping = false;
                    // Layout DTOs and anchors are self-contained. Keeping the last bank here (or in the completed
                    // async state machine) would retain its flat index and tree through every later bank reload.
                    _bank = null;
                    _job = Task.CompletedTask;
                    return;
                }
                arrangement = _queue[0];
                _queue.RemoveAt(0);
                _running = arrangement;
                bank = _bank!;
                key = bank.StateKey;
            }
            try
            {
                await _runner.Run(() => Build(bank, arrangement));
            }
            catch (Exception ex) when (ex is ShortOfMemoryException or OutOfMemoryException or InsufficientMemoryException)
            {
                // Refused (or ran out) for want of memory: never retried at once, but a later request may try again.
                log.LogWarning("memory cloud layout ({Arrangement}) skipped: {Message}", arrangement, ex.Message);
                MarkFailed(arrangement, key, time.GetUtcNow() + ShortOfMemoryRetry,
                    "the bridge is short of memory for this layout right now; a later request tries again");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "memory cloud layout ({Arrangement}) failed", arrangement);
                MarkFailed(arrangement, key, null, null);
            }
        }
    }

    private void MarkFailed(string arrangement, string key, DateTimeOffset? retryAfter, string? note)
    {
        lock (_lock)
        {
            var slot = _slots[arrangement];
            slot.Failed = VersionOf(key, arrangement);
            slot.RetryAfter = retryAfter;
            slot.FailedNote = note;
        }
    }

    private sealed record TreeRead(long BuildId, string Key, List<CloudLeaf> Leaves, Dictionary<int, int[]> Parents, Dictionary<int, int> Layer);

    /// <summary>Runs on the bounded runner: reads the tree once and lays out <paramref name="arrangement"/> (and the MDS it starts from, for the tree).</summary>
    private int Build(MemoryBank bank, string arrangement)
    {
        EnsureAffordable(bank, arrangement);
        var g = bank.Graph();
        if (g == null) return 0;
        var read = bank.ReadTree(t => new TreeRead(t.BuildId, MemoryBank.StateKeyOf(t),
                t.Leaves.Select(n => new CloudLeaf(n.Index, n.Embedding, n.InsertedSeq)).ToList(),
                t.AllNodes.Values.ToDictionary(n => n.Index, n => n.Parents.ToArray()),
                t.AllNodes.Values.ToDictionary(n => n.Index, n => n.Layer)),
            new TreeRead(0L, "none", [], [], []));
        BuildOne(g, read, arrangement);
        return 0;
    }

    /// <summary>
    /// Refuses a layout the process cannot afford now (its estimate plus <see cref="SafetyBytes"/> above the headroom to the heap
    /// limit, after one compacting collection): at the bridge's 384 MiB heap limit an all-pairs fit beside a bank reload used to
    /// throw OutOfMemoryException in whatever else allocated at that moment, the live world stream included.
    /// </summary>
    private void EnsureAffordable(MemoryBank bank, string arrangement)
    {
        var (leaves, dims) = bank.ReadTree(t => (t.Count > 0 ? t.LeafCount : 0, t.Dimensions), (0, 0));
        long need = EstimateBytes(arrangement, leaves, dims, OptionsFor(arrangement)) + SafetyBytes;
        if (Headroom() >= need) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long free = Headroom();
        if (free >= need) return;
        throw new ShortOfMemoryException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{leaves} leaves need about {need / (1024 * 1024)} MiB, {Math.Max(0, free) / (1024 * 1024)} MiB free"));
    }

    private Dictionary<int, (float X, float Y)> BuildOne(GraphModel g, TreeRead read, string arrangement)
    {
        string version = VersionOf(read.Key, arrangement);
        Slot slot;
        lock (_lock)
        {
            slot = _slots[arrangement];
            if (slot.Ready?.Version == version && slot.Positions != null) return slot.Positions;
        }
        CloudTreeInput? tree = null;
        if (arrangement == CloudArrangements.Tree)
            tree = new CloudTreeInput(read.Parents, read.Layer, BuildOne(g, read, CloudArrangements.Mds));
        CloudAnchors? prev;
        lock (_lock) prev = slot.Anchors;
        var o = OptionsFor(arrangement);
        var r = CloudLayout.Compute(read.BuildId, read.Leaves, prev, o, default, tree);
        var fidelity = CloudFidelity.Measure(read.Leaves, r.Positions, o.Seed, varianceExplained: r.Anchors.VarianceExplained);
        var dto = Assemble(g, read.Leaves, read.Parents, r.Positions, read.Key, version, r.Anchors.SummaryXy) with
        {
            Anchors = r.Anchors.Index.Length, PlacedSinceBuild = r.Anchors.Placed.Count, ComputeMs = Math.Round(r.Ms, 1),
            ComputedAt = time.GetUtcNow(), Arrangement = arrangement, Fidelity = fidelity,
        };
        lock (_lock)
        {
            slot.Anchors = r.Anchors;
            slot.Ready = dto;
            slot.Positions = r.Positions;
        }
        log.LogInformation("memory cloud {Arrangement}: {Leaves} leaves ({Mode}, {Placed} placed) in {Ms:0} ms (fit {Fit:0} ms, {Iter} iterations) on {Threads} thread(s); r={R} stress={Stress}",
            arrangement, read.Leaves.Count, r.Full ? "full fit" : "incremental", r.PlacedNow, r.Ms, r.Anchors.FitMs, r.Anchors.FitIterations, _runner.Threads, fidelity.Pearson, fidelity.Stress);
        return r.Positions;
    }

    /// <summary>
    /// The cluster layer: the highest summary layer with at least <paramref name="minClusters"/> nodes (the top one or two layers of a
    /// big tree hold only a handful of very broad summaries), else layer 1.
    /// </summary>
    public static int ClusterLayer(GraphModel g, int minClusters = 6)
    {
        for (int l = g.NumLayers; l >= 1; l--)
            if (l < g.LayerCounts.Length && g.LayerCounts[l] >= minClusters) return l;
        return Math.Min(1, g.NumLayers);
    }

    /// <summary>Builds the wire shape from the graph (clusters, labels, kinds) and the leaf positions (and, for the tree arrangement, the summaries' own).</summary>
    public static MemoryCloudDto Assemble(GraphModel g, IReadOnlyList<CloudLeaf> leaves, IReadOnlyDictionary<int, int[]> parents,
        IReadOnlyDictionary<int, (float X, float Y)> positions, string stateKey, string version, IReadOnlyDictionary<int, (float X, float Y)>? summaryPositions = null)
    {
        int layer = ClusterLayer(g);
        var clusterNodeOf = ClusterNodes(g, layer);
        var placedLeaves = leaves.Where(l => positions.ContainsKey(l.Index)).OrderBy(l => l.Index).ToList();
        var (clusters, clusterId) = Clusters(g, placedLeaves, clusterNodeOf, positions);
        var (kinds, points) = Points(g, placedLeaves, parents, positions, clusterNodeOf, clusterId);
        var summaries = Summaries(g, placedLeaves, parents, positions, clusterId, summaryPositions);
        return new MemoryCloudDto("ready", version, stateKey, null, layer, kinds, points, summaries, clusters, 0, 0, null, null, null);
    }

    /// <summary>Leaf → cluster node: the first node at or below the cluster layer on its primary path from the top (a summary only).</summary>
    private static Dictionary<int, int> ClusterNodes(GraphModel g, int layer)
    {
        var clusterNodeOf = new Dictionary<int, int>();
        foreach (var n in g.Nodes)
        {
            if (!n.IsLeaf) continue;
            int found = -1;
            foreach (var s in g.PathToTop(n.Slot))
                if (g.Nodes[s].Layer <= layer) { found = s; break; }
            if (found >= 0 && !g.Nodes[found].IsLeaf) clusterNodeOf[n.Index] = found;
        }
        return clusterNodeOf;
    }

    /// <summary>Clusters ordered by size (desc), then index: colour slots follow this order, so the biggest regions get the palette.
    /// Also each cluster node's slot → cluster id.</summary>
    private static (List<CloudClusterDto> Clusters, Dictionary<int, int> ClusterId) Clusters(GraphModel g, List<CloudLeaf> placedLeaves,
        Dictionary<int, int> clusterNodeOf, IReadOnlyDictionary<int, (float X, float Y)> positions)
    {
        var bySlot = placedLeaves.Where(l => clusterNodeOf.ContainsKey(l.Index)).GroupBy(l => clusterNodeOf[l.Index])
            .OrderByDescending(x => x.Count()).ThenBy(x => g.Nodes[x.Key].Index).ToList();
        var clusterId = new Dictionary<int, int>();
        var clusters = new List<CloudClusterDto>();
        foreach (var grp in bySlot)
        {
            var node = g.Nodes[grp.Key];
            double mx = grp.Average(l => positions[l.Index].X), my = grp.Average(l => positions[l.Index].Y);
            double spread = Math.Sqrt(grp.Average(l => Sq(positions[l.Index].X - mx) + Sq(positions[l.Index].Y - my)));
            clusterId[grp.Key] = clusters.Count;
            clusters.Add(new CloudClusterDto(clusters.Count, node.Index, node.Layer, node.Terms.Length > 0 ? string.Join(" · ", node.Terms.Take(3)) : $"L{node.Layer} #{node.Index}",
                node.Terms, Short(node.Text, 180), grp.Count(), R(mx), R(my), R(spread)));
        }
        return (clusters, clusterId);
    }

    /// <summary>The placed leaves as parallel arrays (ids, positions, cluster, kind, title, parents, inserted) and the kinds' names.</summary>
    private static (List<string> Kinds, CloudPointsDto Points) Points(GraphModel g, List<CloudLeaf> placedLeaves, IReadOnlyDictionary<int, int[]> parents,
        IReadOnlyDictionary<int, (float X, float Y)> positions, Dictionary<int, int> clusterNodeOf, Dictionary<int, int> clusterId)
    {
        var kinds = new List<string>();
        var kindIx = new Dictionary<string, int>(StringComparer.Ordinal);
        int n0 = placedLeaves.Count;
        var ids = new int[n0];
        var xy = new float[n0 * 2];
        var cl = new int[n0];
        var kd = new int[n0];
        var titles = new string[n0];
        var ps = new int[n0][];
        var ins = new bool[n0];
        for (int i = 0; i < n0; i++)
        {
            var l = placedLeaves[i];
            var p = positions[l.Index];
            ids[i] = l.Index;
            xy[2 * i] = p.X;
            xy[2 * i + 1] = p.Y;
            cl[i] = clusterNodeOf.TryGetValue(l.Index, out var cs) && clusterId.TryGetValue(cs, out var cid) ? cid : -1;
            var gn = g.TryGetSlot(l.Index, out var slot) ? g.Nodes[slot] : null;
            kd[i] = KindIndex(gn?.Category.ToString().ToLowerInvariant() ?? "fact", kinds, kindIx);
            titles[i] = gn?.Title ?? $"leaf #{l.Index}";
            ps[i] = parents.TryGetValue(l.Index, out var pp) ? pp : [];
            ins[i] = l.InsertedSeq > 0;
        }
        return (kinds, new CloudPointsDto(ids, xy, cl, kd, titles, ps, ins));
    }

    private static int KindIndex(string kind, List<string> kinds, Dictionary<string, int> kindIx)
    {
        if (!kindIx.TryGetValue(kind, out var ki)) { ki = kinds.Count; kinds.Add(kind); kindIx[kind] = ki; }
        return ki;
    }

    /// <summary>Summaries at the mean of their placed children, bottom-up (slots descend with layer ascending), unless laid out themselves.</summary>
    private static CloudSummariesDto Summaries(GraphModel g, List<CloudLeaf> placedLeaves, IReadOnlyDictionary<int, int[]> parents,
        IReadOnlyDictionary<int, (float X, float Y)> positions, Dictionary<int, int> clusterId, IReadOnlyDictionary<int, (float X, float Y)>? summaryPositions)
    {
        var sum = new Dictionary<int, (double X, double Y, int N)>();
        foreach (var l in placedLeaves)
            foreach (var p in parents.GetValueOrDefault(l.Index, []))
                Add(sum, p, positions[l.Index].X, positions[l.Index].Y);
        var sIds = new List<int>();
        var sXy = new List<float>();
        var sLayer = new List<int>();
        var sCluster = new List<int>();
        var sParents = new List<int[]>();
        foreach (var s in g.Nodes.Where(n => !n.IsLeaf).OrderBy(n => n.Layer).ThenBy(n => n.Index))
        {
            if (!sum.TryGetValue(s.Index, out var a) || a.N == 0) continue;
            float x = (float)R(a.X / a.N), y = (float)R(a.Y / a.N);
            if (summaryPositions != null && summaryPositions.TryGetValue(s.Index, out var own)) (x, y) = own;
            sIds.Add(s.Index);
            sXy.Add(x);
            sXy.Add(y);
            sLayer.Add(s.Layer);
            sCluster.Add(SummaryCluster(g, s.Slot, clusterId));
            var sp = parents.GetValueOrDefault(s.Index, []);
            sParents.Add(sp);
            foreach (var p in sp) Add(sum, p, x, y);
        }
        return new CloudSummariesDto(sIds.ToArray(), sXy.ToArray(), sLayer.ToArray(), sCluster.ToArray(), sParents.ToArray());
    }

    /// <summary>A summary's cluster: its own, else the first on its path to the top; -1 = none.</summary>
    private static int SummaryCluster(GraphModel g, int slot, Dictionary<int, int> clusterId)
    {
        if (clusterId.TryGetValue(slot, out var mine)) return mine;
        foreach (var t in g.PathToTop(slot)) if (clusterId.TryGetValue(t, out var up)) return up;
        return -1;
    }

    private static void Add(Dictionary<int, (double X, double Y, int N)> sum, int p, double x, double y)
    {
        var a = sum.GetValueOrDefault(p);
        sum[p] = (a.X + x, a.Y + y, a.N + 1);
    }

    private static MemoryCloudDto Empty(MemoryBank bank, string status, string note, string arrangement) =>
        new(status, $"{bank.StateKey}|none", bank.StateKey, note, 0, [], new CloudPointsDto([], [], [], [], [], [], []), new CloudSummariesDto([], [], [], [], []), [],
            0, 0, null, null, null, arrangement);

    private static double Sq(double v) => v * v;
    private static double R(double v) => Math.Round(v, 4);
    private static string Short(string s, int max)
    {
        var t = s.ReplaceLineEndings(" ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}
