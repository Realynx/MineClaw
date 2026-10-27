using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>A cluster after a recluster: its stable key, cells and whether it is new this time.</summary>
internal sealed record ClusterChange(string Key, HashSet<BlockPos> Cells, bool IsNew);

/// <summary>
/// A sparse layer of "interesting" cells (farm blocks, logs, unusual blocks, island footprint columns...) grouped into
/// connected clusters (Chebyshev distance ≤ radius). Incremental: only clusters touching dirty cells are recomputed,
/// and recomputed components inherit the key of the old cluster they overlap most (merges keep the biggest key, splits
/// give the biggest part the old key), so keys stay stable while things grow, shrink, merge and split.
/// Not thread-safe (owned by the indexer's worker).
/// </summary>
internal sealed class ClusterIndex
{
    private readonly BlockPos[] _offsets;
    private readonly HashSet<BlockPos> _cells = new();
    private readonly Dictionary<long, HashSet<BlockPos>> _byChunk = new();
    private readonly Dictionary<BlockPos, string> _owner = new();
    private readonly Dictionary<string, HashSet<BlockPos>> _clusters = new(StringComparer.Ordinal);
    private readonly HashSet<BlockPos> _dirty = new();
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    public ClusterIndex(int radius, bool flat = false)
    {
        var list = new List<BlockPos>();
        int ry = flat ? 0 : radius;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -ry; dy <= ry; dy++)
        for (int dz = -radius; dz <= radius; dz++)
            if (dx != 0 || dy != 0 || dz != 0) list.Add(new BlockPos(dx, dy, dz));
        _offsets = list.ToArray();
    }

    public int CellCount => _cells.Count;
    public bool HasWork => _dirty.Count > 0 || _touched.Count > 0;
    public bool Contains(BlockPos p) => _cells.Contains(p);
    public string? OwnerOf(BlockPos p) => _owner.GetValueOrDefault(p);
    public IReadOnlyCollection<BlockPos>? CellsOf(string key) => _clusters.GetValueOrDefault(key);
    public IEnumerable<string> Keys => _clusters.Keys;

    private static long ChunkKey(BlockPos p) => ((long)(p.X >> 4) << 32) | (uint)(p.Z >> 4);

    public bool Add(BlockPos p)
    {
        if (!_cells.Add(p)) return false;
        var k = ChunkKey(p);
        if (!_byChunk.TryGetValue(k, out var set)) _byChunk[k] = set = new HashSet<BlockPos>();
        set.Add(p);
        _dirty.Add(p);
        return true;
    }

    public bool Remove(BlockPos p)
    {
        if (!_cells.Remove(p)) return false;
        if (_byChunk.TryGetValue(ChunkKey(p), out var set)) set.Remove(p);
        _dirty.Add(p);
        return true;
    }

    public void Set(BlockPos p, bool member)
    {
        if (member) Add(p);
        else Remove(p);
    }

    /// <summary>Marks the cluster owning <paramref name="p"/> changed (content, not membership) so it is re-rendered.</summary>
    public void Touch(BlockPos p)
    {
        if (_owner.TryGetValue(p, out var k)) _touched.Add(k);
    }

    /// <summary>Replaces this layer's cells inside one chunk column with <paramref name="cells"/> (from a full scan).</summary>
    public void ReplaceChunk(int chunkX, int chunkZ, IEnumerable<BlockPos> cells)
    {
        long key = ((long)chunkX << 32) | (uint)chunkZ;
        var fresh = cells as HashSet<BlockPos> ?? cells.ToHashSet();
        if (_byChunk.TryGetValue(key, out var old))
            foreach (var p in old.Where(p => !fresh.Contains(p)).ToList()) Remove(p);
        foreach (var p in fresh) Add(p);
    }

    public IEnumerable<BlockPos> CellsInChunk(int chunkX, int chunkZ) =>
        _byChunk.TryGetValue(((long)chunkX << 32) | (uint)chunkZ, out var s) ? s : [];

    /// <summary>
    /// Recomputes the clusters touched since the last call. <paramref name="newKey"/> names a brand-new component
    /// (it may adopt a persisted key). Returns every recomputed or touched cluster and the keys that disappeared.
    /// </summary>
    public (List<ClusterChange> Changed, List<string> Removed) Recluster(Func<HashSet<BlockPos>, string> newKey)
    {
        var changed = new List<ClusterChange>();
        var removed = new List<string>();
        if (_dirty.Count == 0 && _touched.Count == 0) return (changed, removed);

        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in _dirty)
        {
            if (_owner.TryGetValue(d, out var k)) affected.Add(k);
            foreach (var o in _offsets)
                if (_owner.TryGetValue(d + o, out var k2)) affected.Add(k2);
        }

        var seeds = new List<BlockPos>();
        foreach (var d in _dirty) if (_cells.Contains(d)) seeds.Add(d);
        foreach (var k in affected)
            foreach (var c in _clusters[k]) if (_cells.Contains(c)) seeds.Add(c);

        var visited = new HashSet<BlockPos>();
        var components = new List<HashSet<BlockPos>>();
        var queue = new Queue<BlockPos>();
        foreach (var s in seeds)
        {
            if (!visited.Add(s)) continue;
            var comp = new HashSet<BlockPos> { s };
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                if (_owner.TryGetValue(c, out var ok)) affected.Add(ok); // reached an untouched cluster: it merges
                foreach (var o in _offsets)
                {
                    var n = c + o;
                    if (_cells.Contains(n) && visited.Add(n)) { comp.Add(n); queue.Enqueue(n); }
                }
            }
            components.Add(comp);
        }
        // Cells of clusters pulled in by the walk above but not seeded must still be covered.
        foreach (var k in affected)
            foreach (var c in _clusters[k])
            {
                if (!_cells.Contains(c) || visited.Contains(c)) continue;
                var comp = new HashSet<BlockPos>();
                queue.Enqueue(c);
                visited.Add(c);
                comp.Add(c);
                while (queue.Count > 0)
                {
                    var x = queue.Dequeue();
                    foreach (var o in _offsets)
                    {
                        var n = x + o;
                        if (_cells.Contains(n) && visited.Add(n)) { comp.Add(n); queue.Enqueue(n); }
                    }
                }
                components.Add(comp);
            }

        // Greedy overlap matching: biggest overlaps first, each old key used once.
        var pairs = new List<(int Comp, string Key, int Overlap)>();
        for (int i = 0; i < components.Count; i++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in components[i])
                if (_owner.TryGetValue(c, out var k)) counts[k] = counts.GetValueOrDefault(k) + 1;
            foreach (var (k, n) in counts) pairs.Add((i, k, n));
        }
        pairs.Sort((a, b) => b.Overlap != a.Overlap ? b.Overlap.CompareTo(a.Overlap) : components[b.Comp].Count.CompareTo(components[a.Comp].Count));
        var compKey = new string?[components.Count];
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ci, k, _) in pairs)
            if (compKey[ci] == null && !usedKeys.Contains(k)) { compKey[ci] = k; usedKeys.Add(k); }

        foreach (var k in affected)
        {
            foreach (var c in _clusters[k]) if (_owner.TryGetValue(c, out var ok) && ok == k) _owner.Remove(c);
            _clusters.Remove(k);
            if (!usedKeys.Contains(k)) removed.Add(k);
        }
        for (int i = 0; i < components.Count; i++)
        {
            bool isNew = compKey[i] == null;
            var key = compKey[i] ?? Unique(newKey(components[i]));
            _clusters[key] = components[i];
            foreach (var c in components[i]) _owner[c] = key;
            changed.Add(new ClusterChange(key, components[i], isNew));
        }
        foreach (var t in _touched)
            if (_clusters.TryGetValue(t, out var cells) && changed.All(c => c.Key != t)) changed.Add(new ClusterChange(t, cells, false));
        _dirty.Clear();
        _touched.Clear();
        return (changed, removed);
    }

    private string Unique(string key)
    {
        if (!_clusters.ContainsKey(key)) return key;
        for (int i = 2; ; i++)
            if (!_clusters.ContainsKey($"{key}#{i}")) return $"{key}#{i}";
    }
}
