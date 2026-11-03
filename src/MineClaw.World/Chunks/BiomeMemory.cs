using System.Collections.Concurrent;

namespace MineClaw.World;

/// <summary>Where a biome was seen: one connected patch of explored chunks, and the nearest chunk of it to a point.</summary>
public sealed record BiomePatch(int Biome, int ChunkX, int ChunkZ, double Distance, int Chunks, int Columns,
    int MinX, int MinZ, int MaxX, int MaxZ, int CentreX, int CentreZ);

/// <summary>
/// What the chunks this client has loaded said their biome was, kept after they unload: one dominant biome per chunk, per
/// dimension (3 bytes of state per chunk, never persisted). The server only sends a chunk within its view distance, so
/// "where is a desert?" is answered from everything walked past, not from what is loaded now. 1.12.2 only: b1.8.1 chunks carry
/// no biomes, so the memory stays empty there.
/// </summary>
public sealed class BiomeMemory
{
    private readonly record struct Entry(byte Biome, byte Share);
    private readonly ConcurrentDictionary<(int Dim, int Cx, int Cz), Entry> _chunks = new();

    public int Count => _chunks.Count;

    /// <summary>Notes a full chunk's 256 biome ids (index z·16 + x): the most common one and how many of the 256 columns it covers.</summary>
    public void Record(int dimension, int chunkX, int chunkZ, ReadOnlySpan<byte> biomes)
    {
        if (biomes.Length < 256) return;
        Span<int> n = stackalloc int[256];
        for (int i = 0; i < 256; i++) n[biomes[i]]++;
        int best = 0;
        for (int b = 1; b < 256; b++) if (n[b] > n[best]) best = b;
        _chunks[(dimension, chunkX, chunkZ)] = new Entry((byte)best, (byte)(n[best] - 1));
    }

    /// <summary>The dominant biome of the chunk holding block (x, z), or −1 when it was never seen.</summary>
    public int At(int dimension, int x, int z) =>
        _chunks.TryGetValue((dimension, x >> 4, z >> 4), out var e) ? e.Biome : -1;

    /// <summary>Chunk counts per dominant biome in one dimension, most common first.</summary>
    public IReadOnlyList<(int Biome, int Chunks)> Survey(int dimension) =>
        _chunks.Where(kv => kv.Key.Dim == dimension).GroupBy(kv => (int)kv.Value.Biome)
            .Select(g => (g.Key, g.Count())).OrderByDescending(t => t.Item2).ToList();

    /// <summary>Chunks seen in one dimension.</summary>
    public int Seen(int dimension) => _chunks.Count(kv => kv.Key.Dim == dimension);

    /// <summary>True when chunk (cx, cz) of this dimension was seen.</summary>
    public bool Knows(int dimension, int cx, int cz) => _chunks.ContainsKey((dimension, cx, cz));

    /// <summary>
    /// Every patch of chunks whose dominant biome is in <paramref name="match"/> (a chunk counts when that biome covers at least
    /// <paramref name="minColumns"/> of its 256 columns), a patch being the chunks joined edge to edge; nearest to block (x, z) first.
    /// <see cref="BiomePatch.ChunkX"/>/<see cref="BiomePatch.ChunkZ"/> is the patch's chunk nearest to that point.
    /// </summary>
    public IReadOnlyList<BiomePatch> Patches(int dimension, double x, double z, IReadOnlySet<int> match, int minColumns = 64)
    {
        bool Hit(Entry e) => match.Contains(e.Biome) && e.Share + 1 >= minColumns;
        var hits = _chunks.Where(kv => kv.Key.Dim == dimension && Hit(kv.Value)).Select(kv => (kv.Key.Cx, kv.Key.Cz)).ToHashSet();
        double Dist((int Cx, int Cz) c) => Math.Sqrt(Math.Pow(c.Cx * 16 + 8 - x, 2) + Math.Pow(c.Cz * 16 + 8 - z, 2));
        var result = new List<BiomePatch>();
        while (hits.Count > 0)
        {
            var seed = hits.First();
            var members = new List<(int Cx, int Cz)>();
            var queue = new Queue<(int Cx, int Cz)>();
            queue.Enqueue(seed);
            hits.Remove(seed);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                members.Add(c);
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (c.Cx + dx, c.Cz + dz);
                    if (hits.Remove(n)) queue.Enqueue(n);
                }
            }
            var near = members.MinBy(Dist);
            int columns = members.Sum(c => _chunks[(dimension, c.Cx, c.Cz)].Share + 1);
            result.Add(new BiomePatch(_chunks[(dimension, near.Cx, near.Cz)].Biome, near.Cx, near.Cz, Dist(near), members.Count, columns,
                members.Min(c => c.Cx) * 16, members.Min(c => c.Cz) * 16, members.Max(c => c.Cx) * 16 + 15, members.Max(c => c.Cz) * 16 + 15,
                (int)Math.Round(members.Average(c => c.Cx * 16 + 8.0)), (int)Math.Round(members.Average(c => c.Cz * 16 + 8.0))));
        }
        return result.OrderBy(p => p.Distance).ToList();
    }
}
