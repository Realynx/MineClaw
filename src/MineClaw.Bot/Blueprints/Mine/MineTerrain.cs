using System.Collections.Concurrent;
using System.Globalization;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>
/// The Design Studio's mine terrain (docs/web/api.md "Mine designs"): MineClaw's own deterministic generator, never the game's.
/// A seed and a game version give the same world on the bot and in the browser (<c>mineTerrain.ts</c> is a line-for-line port:
/// integer math only, fixed-point value noise, a 32-bit hash and a 32-bit random stream, so both languages agree bit for bit;
/// a golden hash in each side's tests holds them together). Per column: rolling hills (surface y 58..81 from three octaves of
/// value noise), grass on 3-4 dirt layers, stone, a bedrock floor at y 0 with a patchy layer up to y 4. Per 16×16 chunk, from
/// the version's table (<see cref="OreTable"/>): stone pockets (dirt, gravel; 1.12.2 granite, diorite, andesite), then ore
/// veins at their heights, then now and then one light cave. Every feature is generated inside its own chunk only, so a chunk
/// depends on nothing but the seed, the version and its own position.
/// </summary>
public sealed class MineTerrain
{
    /// <summary>The generated column height (b1.8.1's world height; 1.12.2's terrain stays far under it).</summary>
    public const int Height = 128;
    public const int Stone = 1, Grass = 2, Dirt = 3, Bedrock = 7;
    private const uint SaltOctave1 = 0x5EED0001, SaltOctave2 = 0x5EED0002, SaltOctave3 = 0x5EED0003, SaltDirt = 0xD1A7, SaltBedrock = 0xBEDC0C,
        SaltFeature = 0x0BE5EED, SaltCave = 0xCA7E;

    public string Seed { get; }
    public string Version { get; }
    public OreVersion Table { get; }
    private readonly uint _lo, _hi;
    private readonly ConcurrentDictionary<(int Cx, int Cz), ushort[]> _chunks = new();

    private MineTerrain(string seed, string version)
    {
        Seed = seed;
        Version = version;
        Table = OreTable.For(version);
        (_lo, _hi) = SeedBits(seed);
    }

    private static readonly ConcurrentDictionary<(string, string), MineTerrain> Cache = new();

    /// <summary>The terrain of a seed and a version (a few kept: a session's requests reuse it).</summary>
    public static MineTerrain For(string? seed, string? version)
    {
        var v = DesignVersions.Normalize(version) ?? DesignVersions.Normalize(DesignVersions.Server) ?? DesignVersions.Beta;
        var s = NormalizeSeed(seed);
        if (Cache.Count > 6) Cache.Clear();
        return Cache.GetOrAdd((s, v), k => new MineTerrain(k.Item1, k.Item2));
    }

    /// <summary>The seed as stored (trimmed, at most 32 chars; empty = "0").</summary>
    public static string NormalizeSeed(string? seed)
    {
        var t = (seed ?? "").Trim();
        if (t.Length > 32) t = t[..32];
        return t.Length == 0 ? "0" : t;
    }

    /// <summary>A random numeric seed (a new mine session's default).</summary>
    public static string RandomSeed() => Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);

    /// <summary>The seed's 64 bits as (low, high) words: a whole number as it is, any other text by Java's string hash (as the game
    /// reads a text seed), sign-extended.</summary>
    public static (uint Lo, uint Hi) SeedBits(string seed)
    {
        var t = (seed ?? "").Trim();
        if (t.Length > 0 && (char.IsDigit(t[0]) || t[0] is '-' or '+') && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            return ((uint)n, (uint)(n >> 32));
        int h = 0;
        foreach (char c in t) h = unchecked(31 * h + c);
        return ((uint)h, h < 0 ? 0xFFFFFFFFu : 0u);
    }

    // ------------------------------------------------------------------ hashing and randomness (mirrored in mineTerrain.ts)

    internal static uint Mix(uint h)
    {
        unchecked
        {
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }

    internal uint H(int a, int b, int c, uint salt)
    {
        uint h = Mix(_lo ^ salt);
        h = Mix(h ^ _hi);
        h = Mix(h ^ (uint)a);
        h = Mix(h ^ (uint)b);
        return Mix(h ^ (uint)c);
    }

    /// <summary>A 32-bit random stream (mulberry32).</summary>
    internal struct Rng(uint state)
    {
        private uint _a = state;

        public uint Next()
        {
            unchecked
            {
                _a += 0x6D2B79F5u;
                uint t = (_a ^ (_a >> 15)) * (1u | _a);
                t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
                return t ^ (t >> 14);
            }
        }

        public int Below(int n) => n <= 1 ? 0 : (int)(Next() % (uint)n);
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>Value noise on a lattice of <paramref name="s"/> blocks, 0..65535, smoothstep-blended in 16.16 fixed point.</summary>
    private long Noise(int x, int z, int s, uint salt)
    {
        long i = FloorDiv(x, s), j = FloorDiv(z, s);
        long tx = x - i * s, tz = z - j * s, s3 = (long)s * s * s;
        long ux = (3 * s - 2 * tx) * tx * tx * 65536 / s3, uz = (3 * s - 2 * tz) * tz * tz * 65536 / s3;
        long v00 = H((int)i, (int)j, 0, salt) >> 16, v10 = H((int)i + 1, (int)j, 0, salt) >> 16;
        long v01 = H((int)i, (int)j + 1, 0, salt) >> 16, v11 = H((int)i + 1, (int)j + 1, 0, salt) >> 16;
        long a = v00 + FloorDiv((v10 - v00) * ux, 65536), b = v01 + FloorDiv((v11 - v01) * ux, 65536);
        return a + FloorDiv((b - a) * uz, 65536);
    }

    /// <summary>The y of the grass block of a column (58..81).</summary>
    public int Surface(int x, int z)
    {
        long n = 55 * Noise(x, z, 48, SaltOctave1) + 30 * Noise(x, z, 24, SaltOctave2) + 15 * Noise(x, z, 12, SaltOctave3);
        return 58 + (int)(n * 24 / 6553600);
    }

    // ------------------------------------------------------------------ blocks

    private static int Index(int lx, int y, int lz) => (y * 16 + lz) * 16 + lx;

    /// <summary>The generated state at a world cell as (id, meta); above the column: air, below y 0: bedrock.</summary>
    public (int Id, int Meta) Block(int x, int y, int z)
    {
        if (y >= Height) return (0, 0);
        if (y < 0) return (Bedrock, 0);
        var c = Chunk(x >> 4, z >> 4);
        int v = c[Index(x & 15, y, z & 15)];
        return (v >> 4, v & 15);
    }

    /// <summary>Solid generated ground (anything but air).</summary>
    public bool Solid(int x, int y, int z) => Block(x, y, z).Id != 0;

    /// <summary>A chunk's states (id &lt;&lt; 4 | meta), indexed (y·16 + lz)·16 + lx.</summary>
    public ushort[] Chunk(int cx, int cz) => _chunks.GetOrAdd((cx, cz), k => Generate(k.Cx, k.Cz));

    private ushort[] Generate(int cx, int cz)
    {
        var cells = new ushort[16 * 16 * Height];
        var tops = new int[256];
        for (int lz = 0; lz < 16; lz++)
            for (int lx = 0; lx < 16; lx++)
            {
                int x = cx * 16 + lx, z = cz * 16 + lz;
                int top = Surface(x, z);
                tops[lz * 16 + lx] = top;
                int dirt = 3 + (int)(H(x, z, 0, SaltDirt) % 2);
                for (int y = 0; y <= top && y < Height; y++)
                {
                    int id = y == top ? Grass : y >= top - dirt ? Dirt : Stone;
                    if (y <= Table.BedrockTop && (y == Table.BedrockFloor || (int)(H(x, y, z, SaltBedrock) % 5) >= y)) id = Bedrock;
                    cells[Index(lx, y, lz)] = (ushort)(id << 4);
                }
            }
        // Pockets first, then the ores (in the table's order), each feature its own random stream.
        var features = Table.Pockets.Concat(Table.Ores).ToList();
        for (int k = 0; k < features.Count; k++)
        {
            var f = features[k];
            if (f.Veins <= 0 || f.Size <= 0) continue;
            bool pocket = k < Table.Pockets.Count;
            ushort state = (ushort)(f.Block << 4 | f.Meta);
            var rng = new Rng(H(cx, cz, k, SaltFeature));
            for (int v = 0; v < f.Veins; v++)
            {
                int x = rng.Below(16), z = rng.Below(16);
                int y = f.Shape == "triangle" ? f.Center - f.Spread + rng.Below(f.Spread) + rng.Below(f.Spread) : f.Min + rng.Below(f.Max - f.Min + 1);
                for (int s = 0; s < f.Size; s++)
                {
                    Put(cells, x, y, z, state);
                    if (pocket)
                    {
                        Put(cells, x + 1, y, z, state); Put(cells, x - 1, y, z, state);
                        Put(cells, x, y + 1, z, state); Put(cells, x, y - 1, z, state);
                        Put(cells, x, y, z + 1, state); Put(cells, x, y, z - 1, state);
                    }
                    switch (rng.Below(6))
                    {
                        case 0: x++; break;
                        case 1: x--; break;
                        case 2: y++; break;
                        case 3: y--; break;
                        case 4: z++; break;
                        default: z--; break;
                    }
                }
            }
        }
        // Now and then one light cave: a 3×2 worm inside the chunk, well under the grass.
        var cr = new Rng(H(cx, cz, 0, SaltCave));
        if (cr.Below(3) == 0)
        {
            int x = 3 + cr.Below(10), z = 3 + cr.Below(10), y = 16 + cr.Below(40), dir = cr.Below(4);
            for (int s = 0; s < 40; s++)
            {
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = 0; dy <= 1; dy++)
                        {
                            int lx = x + dx, lz = z + dz, yy = y + dy;
                            if (lx is < 0 or > 15 || lz is < 0 or > 15 || yy < 1 || yy >= tops[lz * 16 + lx] - 6) continue;
                            int i = Index(lx, yy, lz);
                            if (cells[i] >> 4 != Bedrock) cells[i] = 0;
                        }
                if (cr.Below(4) == 0) dir = cr.Below(4);
                if (cr.Below(5) == 0) y += cr.Below(3) - 1;
                switch (dir)
                {
                    case 0: x++; break;
                    case 1: x--; break;
                    case 2: z++; break;
                    default: z--; break;
                }
                x = Math.Clamp(x, 1, 14);
                z = Math.Clamp(z, 1, 14);
                y = Math.Clamp(y, 8, 70);
            }
        }
        return cells;
    }

    /// <summary>Sets a cell of the chunk to <paramref name="state"/> when it is plain stone (features never replace anything else).</summary>
    private static void Put(ushort[] cells, int lx, int y, int lz, ushort state)
    {
        if (lx is < 0 or > 15 || lz is < 0 or > 15 || y is < 0 or >= Height) return;
        int i = Index(lx, y, lz);
        if (cells[i] == Stone << 4) cells[i] = state;
    }

    // ------------------------------------------------------------------ summaries

    /// <summary>FNV-1a over the plot's 4×4 chunks (y, z, x order): the golden value both languages' tests check.</summary>
    public uint PlotHash(int size = 64)
    {
        uint h = 2166136261;
        unchecked
        {
            for (int y = 0; y < Height; y++)
                for (int z = 0; z < size; z++)
                    for (int x = 0; x < size; x++)
                    {
                        var (id, meta) = Block(x, y, z);
                        h = (h ^ (uint)(id << 4 | meta)) * 16777619u;
                    }
        }
        return h;
    }

    /// <summary>The lowest and highest surface over the plot.</summary>
    public (int Min, int Max) SurfaceRange(int size = 64)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                int s = Surface(x, z);
                lo = Math.Min(lo, s);
                hi = Math.Max(hi, s);
            }
        return (lo, hi);
    }

    /// <summary>The highest surface over a box's columns (a shaft's top: "surface").</summary>
    public int SurfaceOver(int x0, int z0, int x1, int z1)
    {
        int hi = int.MinValue;
        for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
                hi = Math.Max(hi, Surface(x, z));
        return hi;
    }
}
