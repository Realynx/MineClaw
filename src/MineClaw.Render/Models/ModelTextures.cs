namespace MineClaw.Render.Models;

/// <summary>
/// The 1.12.2 block textures (<c>textures/blocks/*.png</c> read from the user's client jar at runtime, never committed), one
/// 16×16 tile per texture: animated strips keep their first frame, other sizes are resampled to 16 (nearest). Indexed by the
/// model texture path ("blocks/stone"); index 0 is a magenta/black "missing" tile. <see cref="Stitch"/> lays them into one
/// square atlas for a GPU. Pixels are 0xAARRGGBB.
/// </summary>
public sealed class ModelTextures
{
    public const int TileSize = 16;
    private readonly List<string> _names = ["missingno"];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal) { ["missingno"] = 0 };
    private readonly List<uint[]> _px = [Missing()];
    private readonly List<uint> _avg = [0xff00ff];
    private readonly List<float> _coverage = [1f];
    private readonly List<bool> _semi = [false];

    public int Count => _px.Count;
    public string Name(int tile) => (uint)tile < (uint)_names.Count ? _names[tile] : "missingno";

    /// <summary>The index of a texture path ("blocks/stone" or "minecraft:blocks/stone"); 0 (missing) when unknown.</summary>
    public int IndexOf(string path)
    {
        if (path.StartsWith("minecraft:", StringComparison.Ordinal)) path = path[10..];
        return _index.TryGetValue(path, out int i) ? i : 0;
    }

    public bool Has(string path) => _index.ContainsKey(path.StartsWith("minecraft:", StringComparison.Ordinal) ? path[10..] : path);

    /// <summary>Adds (or replaces) a texture from decoded pixels; returns its index.</summary>
    public int Add(string path, int w, int h, uint[] px)
    {
        var tile = Resample(w, h, px);
        long r = 0, g = 0, b = 0, n = 0;
        bool semi = false;
        foreach (uint p in tile)
        {
            uint a = p >> 24;
            if (a is > 8 and < 247) semi = true;
            if (a < 128) continue;
            r += (p >> 16) & 0xff; g += (p >> 8) & 0xff; b += p & 0xff; n++;
        }
        uint avg = n == 0 ? 0 : (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
        if (_index.TryGetValue(path, out int i))
        {
            _px[i] = tile; _avg[i] = avg; _coverage[i] = n / (float)tile.Length; _semi[i] = semi;
            return i;
        }
        _index[path] = _px.Count;
        _names.Add(path); _px.Add(tile); _avg.Add(avg); _coverage.Add(n / (float)tile.Length); _semi.Add(semi);
        return _px.Count - 1;
    }

    /// <summary>Texel at (u, v) in [0,1) of the tile (u right, v down), 0xAARRGGBB.</summary>
    public uint Sample(int tile, float u, float v)
    {
        var px = (uint)tile < (uint)_px.Count ? _px[tile] : _px[0];
        int x = (int)(u * TileSize), y = (int)(v * TileSize);
        x = x < 0 ? 0 : x >= TileSize ? TileSize - 1 : x;
        y = y < 0 ? 0 : y >= TileSize ? TileSize - 1 : y;
        return px[y * TileSize + x];
    }

    public uint[] Pixels(int tile) => (uint)tile < (uint)_px.Count ? _px[tile] : _px[0];
    /// <summary>Average opaque colour (0xRRGGBB).</summary>
    public uint Average(int tile) => (uint)tile < (uint)_avg.Count ? _avg[tile] : 0;
    /// <summary>Fraction of opaque texels (alpha ≥ 128).</summary>
    public float Coverage(int tile) => (uint)tile < (uint)_coverage.Count ? _coverage[tile] : 1f;
    /// <summary>Has partly transparent texels (stained glass, ice): drawn blended, not alpha-tested.</summary>
    public bool SemiTransparent(int tile) => (uint)tile < (uint)_semi.Count && _semi[tile];

    /// <summary>One square atlas of every tile (row-major, <paramref name="perRow"/> tiles per row) for a GPU upload.</summary>
    public uint[] Stitch(out int size, out int perRow)
    {
        perRow = 1;
        while (perRow * perRow < _px.Count) perRow *= 2;
        size = perRow * TileSize;
        var atlas = new uint[size * size];
        for (int t = 0; t < _px.Count; t++)
        {
            int ox = t % perRow * TileSize, oy = t / perRow * TileSize;
            var src = _px[t];
            for (int y = 0; y < TileSize; y++) Array.Copy(src, y * TileSize, atlas, (oy + y) * size + ox, TileSize);
        }
        return atlas;
    }

    private static uint[] Resample(int w, int h, uint[] px)
    {
        int frame = Math.Min(w, h); // animated strips (w × n·w): the first frame
        var dst = new uint[TileSize * TileSize];
        for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                dst[y * TileSize + x] = px[y * frame / TileSize * w + x * frame / TileSize];
        return dst;
    }

    private static uint[] Missing()
    {
        var px = new uint[TileSize * TileSize];
        for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                px[y * TileSize + x] = (x < 8) == (y < 8) ? 0xffff00ffu : 0xff000000u;
        return px;
    }
}
