using MineClaw.ViewerBridge.Data;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>The leaves (columns, same order in every array). <c>Xy</c> is x, y interleaved in the unit square.</summary>
public sealed record CloudPointsDto(int[] Id, float[] Xy, int[] Cluster, int[] Kind, string[] Title, int[][] Parents, bool[] Inserted);
/// <summary>Summaries sit at the mean of their children (bottom-up), for the faded link overlay and region labels.</summary>
public sealed record CloudSummariesDto(int[] Id, float[] Xy, int[] Layer, int[] Cluster, int[][] Parents);
public sealed record CloudClusterDto(int Id, int Node, int Layer, string Label, IReadOnlyList<string> Terms, string Summary, int Size, double X, double Y, double Spread);
public sealed record MemoryCloudDto(string Status, string Version, string StateKey, string? Note, int ClusterLayer, IReadOnlyList<string> Kinds, CloudPointsDto Points,
    CloudSummariesDto Summaries, IReadOnlyList<CloudClusterDto> Clusters, int Anchors, int PlacedSinceBuild, double? ComputeMs, DateTimeOffset? ComputedAt,
    string? ComputingStateKey);

/// <summary>
/// The memory cloud (architecture.md, "Memory activity tab" → Cloud): every RAPTOR leaf at its 2-D UMAP position (<see cref="CloudLayout"/>),
/// coloured by the bot's own clusters at one summary layer, with summaries placed at their children's mean. The projection runs in the
/// background on <see cref="BridgeOptions.CloudThreads"/> threads and is cached per tree state; a request never waits for it: it gets
/// the previous layout (or none) with <c>status: "computing"</c> and polls.
/// </summary>
public sealed class MemoryCloudService(BridgeConfig config, ILogger<MemoryCloudService> log, TimeProvider time)
{
    private readonly object _lock = new();
    private readonly BoundedRunner _runner = new(config.Bridge.CloudThreads);
    private readonly CloudLayoutOptions _options = new() { MaxUmapPoints = Math.Clamp(config.Bridge.CloudMaxUmapPoints, 100, CloudLayoutOptions.UmapSequentialLimit) };
    private CloudAnchors? _anchors;
    private MemoryCloudDto? _ready;
    private string? _computingKey;
    private string? _failed;
    private Task? _job;

    public CloudLayoutOptions Options => _options;

    /// <summary>The current layout for <paramref name="bank"/>; starts (at most one) background computation when it is missing.</summary>
    public MemoryCloudDto Get(MemoryBank bank)
    {
        if (!bank.HasTree) return Empty(bank, "empty", bank.TreeNote ?? "no RAPTOR tree stored in this db");
        string key = bank.StateKey;
        string version = $"{key}|{_options.Version}";
        lock (_lock)
        {
            if (_ready is { } r && r.Version == version) return r;
            if (_failed == version) return (_ready ?? Empty(bank, "error", "")) with { Status = "error", Note = "the memory cloud layout failed (see the bridge log)" };
            if (_job is not { IsCompleted: false })
            {
                _computingKey = key;
                _job = Task.Run(() => ComputeAsync(bank, key, version));
            }
            var stale = _ready;
            return stale != null
                ? stale with { Status = "computing", ComputingStateKey = _computingKey }
                : Empty(bank, "computing", "laying out the memory cloud") with { ComputingStateKey = _computingKey };
        }
    }

    /// <summary>Waits for the running computation (tests).</summary>
    public Task Idle() { lock (_lock) return _job ?? Task.CompletedTask; }

    private async Task ComputeAsync(MemoryBank bank, string key, string version)
    {
        try
        {
            var dto = await _runner.Run(() => Build(bank, key, version));
            lock (_lock)
            {
                if (dto != null) _ready = dto;
                _computingKey = null;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "memory cloud layout failed");
            lock (_lock)
            {
                _computingKey = null;
                _failed = version;
            }
        }
    }

    private MemoryCloudDto? Build(MemoryBank bank, string key, string version)
    {
        var g = bank.Graph();
        if (g == null) return null;
        var (buildId, leaves, parents) = bank.ReadTree(t => (t.BuildId,
                t.Leaves.Select(n => new CloudLeaf(n.Index, n.Embedding, n.InsertedSeq)).ToList(),
                t.AllNodes.Values.ToDictionary(n => n.Index, n => n.Parents.ToArray())),
            (0L, new List<CloudLeaf>(), new Dictionary<int, int[]>()));
        CloudAnchors? prev;
        lock (_lock) prev = _anchors;
        var r = CloudLayout.Compute(buildId, leaves, prev, _options);
        lock (_lock) _anchors = r.Anchors;
        var dto = Assemble(g, leaves, parents, r.Positions, key, version) with
        {
            Anchors = r.Anchors.Index.Length, PlacedSinceBuild = r.Anchors.Placed.Count, ComputeMs = Math.Round(r.Ms, 1),
            ComputedAt = time.GetUtcNow(),
        };
        log.LogInformation("memory cloud: {Leaves} leaves ({Mode}, {Placed} placed) in {Ms:0} ms on {Threads} thread(s)", leaves.Count,
            r.Full ? "full UMAP" : "incremental", r.PlacedNow, r.Ms, _runner.Threads);
        return dto;
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

    /// <summary>Builds the wire shape from the graph (clusters, labels, kinds) and the leaf positions.</summary>
    public static MemoryCloudDto Assemble(GraphModel g, IReadOnlyList<CloudLeaf> leaves, IReadOnlyDictionary<int, int[]> parents,
        IReadOnlyDictionary<int, (float X, float Y)> positions, string stateKey, string version)
    {
        int layer = ClusterLayer(g);
        // Leaf → cluster node: the first node at or below the cluster layer on its primary path from the top.
        var clusterNodeOf = new Dictionary<int, int>();
        foreach (var n in g.Nodes)
        {
            if (!n.IsLeaf) continue;
            int found = -1;
            foreach (var s in g.PathToTop(n.Slot))
                if (g.Nodes[s].Layer <= layer) { found = s; break; }
            if (found >= 0 && !g.Nodes[found].IsLeaf) clusterNodeOf[n.Index] = found;
        }
        // Clusters ordered by size (desc), then index: colour slots follow this order, so the biggest regions get the palette.
        var placedLeaves = leaves.Where(l => positions.ContainsKey(l.Index)).OrderBy(l => l.Index).ToList();
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
            string kind = gn?.Category.ToString().ToLowerInvariant() ?? "fact";
            if (!kindIx.TryGetValue(kind, out var ki)) { ki = kinds.Count; kinds.Add(kind); kindIx[kind] = ki; }
            kd[i] = ki;
            titles[i] = gn?.Title ?? $"leaf #{l.Index}";
            ps[i] = parents.TryGetValue(l.Index, out var pp) ? pp : [];
            ins[i] = l.InsertedSeq > 0;
        }

        // Summaries at the mean of their placed children, bottom-up (slots descend with layer ascending).
        var sum = new Dictionary<int, (double X, double Y, int N)>();
        foreach (var l in placedLeaves)
            foreach (var p in parents.GetValueOrDefault(l.Index, []))
                Add(sum, p, positions[l.Index].X, positions[l.Index].Y);
        var summaries = g.Nodes.Where(n => !n.IsLeaf).OrderBy(n => n.Layer).ThenBy(n => n.Index).ToList();
        var sIds = new List<int>();
        var sXy = new List<float>();
        var sLayer = new List<int>();
        var sCluster = new List<int>();
        var sParents = new List<int[]>();
        foreach (var s in summaries)
        {
            if (!sum.TryGetValue(s.Index, out var a) || a.N == 0) continue;
            float x = (float)R(a.X / a.N), y = (float)R(a.Y / a.N);
            sIds.Add(s.Index);
            sXy.Add(x);
            sXy.Add(y);
            sLayer.Add(s.Layer);
            int c = -1;
            if (clusterId.TryGetValue(s.Slot, out var own)) c = own;
            else foreach (var t in g.PathToTop(s.Slot)) if (clusterId.TryGetValue(t, out var up)) { c = up; break; }
            sCluster.Add(c);
            var sp = parents.GetValueOrDefault(s.Index, []);
            sParents.Add(sp);
            foreach (var p in sp) Add(sum, p, x, y);
        }

        return new MemoryCloudDto("ready", version, stateKey, null, layer, kinds,
            new CloudPointsDto(ids, xy, cl, kd, titles, ps, ins),
            new CloudSummariesDto(sIds.ToArray(), sXy.ToArray(), sLayer.ToArray(), sCluster.ToArray(), sParents.ToArray()),
            clusters, 0, 0, null, null, null);
    }

    private static void Add(Dictionary<int, (double X, double Y, int N)> sum, int p, double x, double y)
    {
        var a = sum.GetValueOrDefault(p);
        sum[p] = (a.X + x, a.Y + y, a.N + 1);
    }

    private static MemoryCloudDto Empty(MemoryBank bank, string status, string note) =>
        new(status, $"{bank.StateKey}|none", bank.StateKey, note, 0, [], new CloudPointsDto([], [], [], [], [], [], []), new CloudSummariesDto([], [], [], [], []), [],
            0, 0, null, null, null);

    private static double Sq(double v) => v * v;
    private static double R(double v) => Math.Round(v, 4);
    private static string Short(string s, int max)
    {
        var t = s.ReplaceLineEndings(" ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}
