using MineClaw.World;

namespace MineClaw.Render;

/// <summary>Column state for rendering: nothing known, live (from the server), or remembered from an earlier visit.</summary>
public enum ColumnState : byte { Unknown, Live, Remembered }

/// <summary>
/// Read-only voxel access for the textured renderers: blocks + metadata + light. Light is −1 when unknown (remembered
/// columns carry no light). Implementations must be cheap per call: the raycaster calls them millions of times per frame.
/// </summary>
public interface IVoxelWorld
{
    int Block(int x, int y, int z);
    int Meta(int x, int y, int z);
    int SkyLight(int x, int y, int z);
    int BlockLight(int x, int y, int z);
    ColumnState State(int x, int z);
}

/// <summary>
/// <see cref="IVoxelWorld"/> over a <see cref="WorldState"/>: live columns, else the remembered layer. <see cref="Prepare"/>
/// snapshots the column references of a square of chunks once per frame, so a lookup is two shifts and an array index
/// (no dictionary). Not thread-safe; one per renderer.
/// </summary>
public sealed class WorldVoxels : IVoxelWorld
{
    private readonly WorldState _w;
    private ChunkColumn?[] _live = [];
    private ChunkSnapshot?[] _rem = [];
    private byte[]?[] _remTop = [];
    private int _cx0, _cz0, _n;

    public WorldVoxels(WorldState world) => _w = world;

    /// <summary>Caches the chunks within <paramref name="radiusChunks"/> of block (x, z).</summary>
    public void Prepare(int x, int z, int radiusChunks)
    {
        _n = 2 * radiusChunks + 1;
        _cx0 = (x >> 4) - radiusChunks;
        _cz0 = (z >> 4) - radiusChunks;
        if (_live.Length != _n * _n) { _live = new ChunkColumn?[_n * _n]; _rem = new ChunkSnapshot?[_n * _n]; _remTop = new byte[]?[_n * _n]; }
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
            {
                int k = i * _n + j;
                var col = _w.GetColumn(_cx0 + i, _cz0 + j);
                _live[k] = col is { HasData: true } && col.Dimension == _w.Dimension ? col : null;
                _rem[k] = _live[k] == null ? _w.GetRemembered(_cx0 + i, _cz0 + j)?.Blocks : null;
                _remTop[k] = null;
            }
    }

    private int Slot(int x, int z)
    {
        int i = (x >> 4) - _cx0, j = (z >> 4) - _cz0;
        return (uint)i < (uint)_n && (uint)j < (uint)_n ? i * _n + j : -1;
    }

    public int Block(int x, int y, int z)
    {
        if ((uint)y >= WorldState.Height) return 0;
        int k = Slot(x, z);
        if (k < 0) return _w.PlanningView.GetBlock(x, y, z);
        if (_live[k] is { } c) return c.GetBlock(x & 15, y, z & 15);
        return _rem[k] is { } r ? r.Ids[ChunkColumn.Index(x & 15, y, z & 15)] : 0;
    }

    public int Meta(int x, int y, int z)
    {
        if ((uint)y >= WorldState.Height) return 0;
        int k = Slot(x, z);
        if (k < 0) return _w.PlanningView.GetMeta(x, y, z);
        if (_live[k] is { } c) return c.GetMeta(x & 15, y, z & 15);
        return _rem[k] is { } r ? r.Meta[ChunkColumn.Index(x & 15, y, z & 15)] : 0;
    }

    public int SkyLight(int x, int y, int z)
    {
        if (y >= WorldState.Height) return 15;
        if (y < 0) return 0;
        int k = Slot(x, z);
        if (k >= 0 && _live[k] is { } c) return c.GetSkyLight(x & 15, y, z & 15);
        if (k >= 0 && _rem[k] is { } r) return y > RememberedTop(k, r, x & 15, z & 15) ? 15 : 0; // no light stored: open sky or not
        return k < 0 && _w.IsLoaded(x, z) ? _w.GetSkyLight(x, y, z) : -1;
    }

    public int BlockLight(int x, int y, int z)
    {
        if ((uint)y >= WorldState.Height) return 0;
        int k = Slot(x, z);
        if (k >= 0 && _live[k] is { } c) return c.GetBlockLight(x & 15, y, z & 15);
        if (k >= 0 && _rem[k] != null) return 0;
        return k < 0 && _w.IsLoaded(x, z) ? _w.GetBlockLight(x, y, z) : -1;
    }

    /// <summary>Remembered columns carry no light: estimate sky light from a lazily built height map of opaque blocks.</summary>
    private int RememberedTop(int k, ChunkSnapshot r, int lx, int lz)
    {
        var top = _remTop[k];
        if (top == null)
        {
            top = new byte[256];
            for (int ix = 0; ix < 16; ix++)
                for (int iz = 0; iz < 16; iz++)
                {
                    int y = WorldState.Height - 1;
                    while (y > 0 && !FaceLight.Opaque(r.Ids[ChunkColumn.Index(ix, y, iz)])) y--;
                    top[ix * 16 + iz] = (byte)y;
                }
            _remTop[k] = top;
        }
        return top[lx * 16 + lz];
    }

    public ColumnState State(int x, int z)
    {
        int k = Slot(x, z);
        if (k >= 0) return _live[k] != null ? ColumnState.Live : _rem[k] != null ? ColumnState.Remembered : ColumnState.Unknown;
        return _w.IsLoaded(x, z) ? ColumnState.Live : _w.IsStale(x, z) ? ColumnState.Remembered : ColumnState.Unknown;
    }
}

/// <summary>A sparse in-memory voxel world (tests, synthetic scenes). Unset cells are air with full sky light.</summary>
public sealed class SparseVoxels : IVoxelWorld
{
    private readonly Dictionary<(int, int, int), (byte Id, byte Meta)> _b = new();
    private readonly Dictionary<(int, int, int), (int Sky, int Block)> _l = new();
    public ColumnState Columns { get; set; } = ColumnState.Live;

    public SparseVoxels Set(int x, int y, int z, int id, int meta = 0) { _b[(x, y, z)] = ((byte)id, (byte)meta); return this; }
    public SparseVoxels Light(int x, int y, int z, int sky, int block) { _l[(x, y, z)] = (sky, block); return this; }

    public int Block(int x, int y, int z) => _b.TryGetValue((x, y, z), out var v) ? v.Id : 0;
    public int Meta(int x, int y, int z) => _b.TryGetValue((x, y, z), out var v) ? v.Meta : 0;
    public int SkyLight(int x, int y, int z) => _l.TryGetValue((x, y, z), out var v) ? v.Sky : Block(x, y, z) == 0 ? 15 : 0;
    public int BlockLight(int x, int y, int z) => _l.TryGetValue((x, y, z), out var v) ? v.Block : 0;
    public ColumnState State(int x, int z) => Columns;
}

/// <summary>
/// Face light sampling shared by the textured views: the light level of the air cell in front of a face, with the cheap
/// stale-light fix (block-change packets carry no light, so a cell that was opaque still reads 0 until relit: like the sapling
/// fix, take max(stored, brightest non-opaque neighbour − 1)), emitters at full brightness, remembered columns at a fixed level.
/// </summary>
public static class FaceLight
{
    /// <summary>Light level (0..15) of the cell (x, y, z) as seen by a face; −1 when unknown (remembered).</summary>
    public static int CellLevel(IVoxelWorld w, int x, int y, int z, int subtracted)
    {
        if (y >= WorldState.Height) return 15 - subtracted;
        int sky = w.SkyLight(x, y, z), blk = w.BlockLight(x, y, z);
        if (sky < 0 || blk < 0) return -1;
        if (sky == 0 && blk == 0 && w.Block(x, y, z) == 0)
        {
            // Stale after an edit (an air cell reading 0 everywhere): estimate from the six neighbours.
            for (int f = 0; f < 6; f++)
            {
                var (dx, dy, dz) = Lighting.Normal((Face)f);
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (ny >= WorldState.Height) { sky = 15; continue; }
                if (ny < 0 || Opaque(w.Block(nx, ny, nz))) continue;
                int ns = w.SkyLight(nx, ny, nz), nb = w.BlockLight(nx, ny, nz);
                if (ns > 0) sky = Math.Max(sky, ns - (dy == 1 && ns == 15 ? 0 : 1));
                if (nb > 0) blk = Math.Max(blk, nb - 1);
            }
        }
        return Lighting.Level(sky, blk, subtracted);
    }

    public static bool Opaque(int id) => id != 0 && MineClaw.Data.Blocks.Get(id) is { Solid: true } && id != MineClaw.Data.Blocks.Leaves
        && id != MineClaw.Data.Blocks.Glass && id != MineClaw.Data.Blocks.Slab;

    /// <summary>Brightness multiplier of a face (vanilla table × face shade); remembered/unknown light → 0.75 × shade.</summary>
    public static float Of(IVoxelWorld w, int id, int x, int y, int z, Face f, int subtracted, bool nether)
    {
        float shade = Lighting.FaceShade(f);
        if (BlockTextures.Emissive(id)) return 1f;
        var (dx, dy, dz) = Lighting.Normal(f);
        int lvl = CellLevel(w, x + dx, y + dy, z + dz, subtracted);
        if (lvl < 0) return 0.75f * shade;
        return Math.Max(0.05f, Lighting.Brightness(lvl, nether)) * shade;
    }
}
