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
    /// <summary>Blocks per column (b1.8.1 128, 1.12.2 256): y outside 0..WorldHeight−1 is air with sky light 15.
    /// Implementations that predate multi-version are 128 high.</summary>
    int WorldHeight => ChunkColumn.SizeY;
    /// <summary>The biome id at (x, z) for tints (1.12.2 chunk data), or −1 = unknown (b1.8.1, remembered columns): plains colours.</summary>
    int Biome(int x, int z) => -1;
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
    private readonly int _h;
    private int _cx0, _cz0, _n;

    public WorldVoxels(WorldState world) { _w = world; _h = world.WorldHeight; }

    public int WorldHeight => _h;

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
        if ((uint)y >= _h) return 0;
        int k = Slot(x, z);
        if (k < 0) return _w.PlanningView.GetBlock(x, y, z);
        if (_live[k] is { } c) return c.GetBlock(x & 15, y, z & 15);
        return _rem[k] is { } r && y < r.Height ? r.Ids[ChunkColumn.Index(x & 15, y, z & 15)] : 0;
    }

    public int Meta(int x, int y, int z)
    {
        if ((uint)y >= _h) return 0;
        int k = Slot(x, z);
        if (k < 0) return _w.PlanningView.GetMeta(x, y, z);
        if (_live[k] is { } c) return c.GetMeta(x & 15, y, z & 15);
        return _rem[k] is { } r && y < r.Height ? r.Meta[ChunkColumn.Index(x & 15, y, z & 15)] : 0;
    }

    public int SkyLight(int x, int y, int z)
    {
        if (y >= _h) return 15;
        if (y < 0) return 0;
        int k = Slot(x, z);
        if (k >= 0 && _live[k] is { } c) return c.GetSkyLight(x & 15, y, z & 15);
        if (k >= 0 && _rem[k] is { } r) return y > RememberedTop(k, r, x & 15, z & 15) ? 15 : 0; // no light stored: open sky or not
        return k < 0 && _w.IsLoaded(x, z) ? _w.GetSkyLight(x, y, z) : -1;
    }

    public int BlockLight(int x, int y, int z)
    {
        if ((uint)y >= _h) return 0;
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
                    int y = r.Height - 1;
                    while (y > 0 && !FaceLight.Opaque(r.Ids[ChunkColumn.Index(ix, y, iz)])) y--;
                    top[ix * 16 + iz] = (byte)y;
                }
            _remTop[k] = top;
        }
        return top[lx * 16 + lz];
    }

    public int Biome(int x, int z)
    {
        int k = Slot(x, z);
        return k >= 0 ? _live[k]?.GetBiome(x & 15, z & 15) ?? -1 : _w.GetBiome(x, z);
    }

    public ColumnState State(int x, int z)
    {
        int k = Slot(x, z);
        if (k >= 0) return _live[k] != null ? ColumnState.Live : _rem[k] != null ? ColumnState.Remembered : ColumnState.Unknown;
        return _w.IsLoaded(x, z) ? ColumnState.Live : _w.IsStale(x, z) ? ColumnState.Remembered : ColumnState.Unknown;
    }
}

/// <summary>A sparse in-memory voxel world (tests, synthetic scenes). Cells that aren't opaque cubes (air, water, plants,
/// slabs) have full sky light unless set.</summary>
public sealed class SparseVoxels : IVoxelWorld
{
    private readonly Dictionary<(int, int, int), (byte Id, byte Meta)> _b = new();
    private readonly Dictionary<(int, int, int), (int Sky, int Block)> _l = new();
    public ColumnState Columns { get; set; } = ColumnState.Live;
    public int WorldHeight { get; init; } = ChunkColumn.SizeY;

    public SparseVoxels Set(int x, int y, int z, int id, int meta = 0) { _b[(x, y, z)] = ((byte)id, (byte)meta); return this; }
    public SparseVoxels Light(int x, int y, int z, int sky, int block) { _l[(x, y, z)] = (sky, block); return this; }

    public int Block(int x, int y, int z) => _b.TryGetValue((x, y, z), out var v) ? v.Id : 0;
    public int Meta(int x, int y, int z) => _b.TryGetValue((x, y, z), out var v) ? v.Meta : 0;
    public int SkyLight(int x, int y, int z) => _l.TryGetValue((x, y, z), out var v) ? v.Sky : Block(x, y, z) is var id && (id == 0 || !BlockGeometry.OpaqueCube(id)) ? 15 : 0;
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
    public static int CellLevel(IVoxelWorld w, int x, int y, int z, int subtracted) =>
        w is ICellLevels memo ? memo.Level(x, y, z, subtracted) : CellLevelNow(w, x, y, z, subtracted);

    /// <summary>A world that memoizes <see cref="CellLevel"/> and the opacity of cells (the GPU mesher's per-section cache: a cell
    /// in front of a floor is asked for by the nine faces around it). Implementations answer exactly <see cref="CellLevelNow"/>.</summary>
    public interface ICellLevels
    {
        int Level(int x, int y, int z, int subtracted);
        bool OpaqueAt(int x, int y, int z);
    }

    /// <summary><see cref="CellLevel"/> computed (no memo).</summary>
    public static int CellLevelNow(IVoxelWorld w, int x, int y, int z, int subtracted)
    {
        int h = w.WorldHeight;
        if (y >= h) return 15 - subtracted;
        int sky = w.SkyLight(x, y, z), blk = w.BlockLight(x, y, z);
        if (sky < 0 || blk < 0) return -1;
        if (sky == 0 && blk == 0 && w.Block(x, y, z) == 0)
        {
            // Stale after an edit (an air cell reading 0 everywhere): estimate from the six neighbours.
            for (int f = 0; f < 6; f++)
            {
                var (dx, dy, dz) = Lighting.Normal((Face)f);
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (ny >= h) { sky = 15; continue; }
                if (ny < 0 || Opaque(w.Block(nx, ny, nz))) continue;
                int ns = w.SkyLight(nx, ny, nz), nb = w.BlockLight(nx, ny, nz);
                if (ns > 0) sky = Math.Max(sky, ns - (dy == 1 && ns == 15 ? 0 : 1));
                if (nb > 0) blk = Math.Max(blk, nb - 1);
            }
        }
        return Lighting.Level(sky, blk, subtracted);
    }

    public static bool Opaque(int id)
    {
        if (id == 0) return false;
        if ((uint)id >= 256) return OpaqueNow(id);
        var t = _opaque;
        var d = MineClaw.Data.Versioning.GameData.Current;
        // One table per version, kept: renderers on different versions at once (an override in one flow) only swap the hot slot
        // instead of rebuilding a table on every call.
        if (t == null || !ReferenceEquals(t.Data, d)) _opaque = t = _opaqueTables.GetValue(d, x => new OpaqueTable(x));
        return t.Opaque[id];
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MineClaw.Data.Versioning.IGameData, OpaqueTable> _opaqueTables = new();

    private static bool OpaqueNow(int id) => id != 0 && MineClaw.Data.Blocks.Get(id) is { Solid: true } && id != MineClaw.Data.Blocks.Leaves
        && id != MineClaw.Data.Blocks.Glass && id != MineClaw.Data.Blocks.Slab;

    /// <summary><see cref="Opaque"/> for the 256 block ids of one version's tables (the renderers ask it millions of times a frame).</summary>
    private sealed class OpaqueTable
    {
        public readonly MineClaw.Data.Versioning.IGameData Data;
        public readonly bool[] Opaque = new bool[256];

        public OpaqueTable(MineClaw.Data.Versioning.IGameData data)
        {
            Data = data;
            using (MineClaw.Data.Versioning.GameData.Override(data))
                for (int i = 1; i < 256; i++) Opaque[i] = OpaqueNow(i);
        }
    }

    private static volatile OpaqueTable? _opaque;

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

    // The light every renderer bakes (the CPU raycaster per sample, the Explorer's GPU mesher per vertex): one implementation,
    // so the two can't drift. Remembered-column dimming is not in here: the raycaster applies it per sample, the GPU per section.

    /// <summary>Texture-space (u, v) of a block-local point on a full-cube face (the raycaster's hit mapping; v down). Smooth
    /// light's corners are in this order.</summary>
    public static (double U, double V) CubeFaceUv(Face f, double fx, double fy, double fz) => f switch
    {
        Face.Up or Face.Down => (fx, fz),
        Face.North => (1 - fx, 1 - fy),
        Face.South => (fx, 1 - fy),
        Face.West => (fz, 1 - fy),
        _ => (1 - fz, 1 - fy),
    };

    /// <summary>The smooth-light corner (0 = u0 v0, 1 = u1 v0, 2 = u0 v1, 3 = u1 v1) of a full-cube face vertex.</summary>
    public static int Corner(Face f, System.Numerics.Vector3 p)
    {
        var (u, v) = CubeFaceUv(f, p.X, p.Y, p.Z);
        return (u > 0.5 ? 1 : 0) | (v > 0.5 ? 2 : 0);
    }

    /// <summary>A full-cube face's brightness at its four corners (u, v order of <see cref="Corner"/>): emissive 1; flat lighting
    /// (vanilla "Fast") = <see cref="Of"/> at every corner; smooth = <see cref="SmoothCorners"/>. Bilinear across the face.</summary>
    public static void CubeCorners(IVoxelWorld w, int id, int x, int y, int z, Face f, int subtracted, bool nether, bool smooth, float minB, Span<float> c)
    {
        if (BlockTextures.Emissive(id)) { c[..4].Fill(1f); return; }
        if (!smooth) { c[..4].Fill(Math.Max(minB, Of(w, id, x, y, z, f, subtracted, nether))); return; }
        SmoothCorners(w, x, y, z, f, subtracted, nether, c, minB);
    }

    /// <summary>Smooth lighting: each face corner averages the light of the 4 cells around it in the layer in front of the
    /// face (opaque cells count as the face cell's own light), times the face shade. Corners in (u,v) order 00, 10, 01, 11.</summary>
    public static void SmoothCorners(IVoxelWorld w, int x, int y, int z, Face f, int sub, bool nether, Span<float> outC, float minB)
    {
        var (nx, ny, nz) = Lighting.Normal(f);
        int ax = x + nx, ay = y + ny, az = z + nz;
        int centre = CellLevel(w, ax, ay, az, sub);
        float shade = Lighting.FaceShade(f);
        (int, int, int) uA, vA;
        switch (f)
        {
            case Face.Up or Face.Down: uA = (1, 0, 0); vA = (0, 0, 1); break;
            case Face.North: uA = (-1, 0, 0); vA = (0, -1, 0); break;
            case Face.South: uA = (1, 0, 0); vA = (0, -1, 0); break;
            case Face.West: uA = (0, 0, 1); vA = (0, -1, 0); break;
            default: uA = (0, 0, -1); vA = (0, -1, 0); break;
        }
        // The 3 × 3 cells in front of the face (i along u, j along v, −1..1), each read once: every corner averages its 2 × 2.
        Span<int> g = stackalloc int[9];
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                if (i == 0 && j == 0) { g[4] = centre; continue; }
                int cx = ax + uA.Item1 * i + vA.Item1 * j, cy = ay + uA.Item2 * i + vA.Item2 * j, cz = az + uA.Item3 * i + vA.Item3 * j;
                bool opaque = w is ICellLevels memo ? memo.OpaqueAt(cx, cy, cz) : Opaque(w.Block(cx, cy, cz));
                g[(i + 1) * 3 + j + 1] = opaque ? centre : CellLevel(w, cx, cy, cz, sub);
            }
        for (int corner = 0; corner < 4; corner++)
        {
            int su = (corner & 1) == 0 ? -1 : 1, sv = (corner & 2) == 0 ? -1 : 1;
            int sum = 0, n = 0;
            for (int a = 0; a <= 1; a++)
                for (int b = 0; b <= 1; b++)
                {
                    int lvl = g[(su * a + 1) * 3 + sv * b + 1];
                    if (lvl < 0) continue;
                    sum += lvl; n++;
                }
            float k = n == 0 ? 0.75f : Lighting.Brightness((int)Math.Round(sum / (double)n), nether);
            outC[corner] = Math.Max(minB, k * shade);
        }
    }

    /// <summary>Brightness of a shape quad's light cell before its shade: the neighbouring cell its face looks into (an opaque
    /// one reading 0 = stale: the cell above), or the block's own cell (max with the one above, so slabs and plants aren't dark
    /// from stale light); unknown → 0.75.</summary>
    public static float QuadLevel(IVoxelWorld w, int lightFace, int x, int y, int z, int sub, bool nether)
    {
        int lvl;
        if (lightFace >= 0)
        {
            var (dx, dy, dz) = Lighting.Normal((Face)lightFace);
            lvl = CellLevel(w, x + dx, y + dy, z + dz, sub);
            if (lvl == 0 && BlockGeometry.OpaqueCube(w.Block(x + dx, y + dy, z + dz))) lvl = CellLevel(w, x, y + 1, z, sub);
        }
        else lvl = Math.Max(CellLevel(w, x, y, z, sub), CellLevel(w, x, y + 1, z, sub));
        return lvl < 0 ? 0.75f : Lighting.Brightness(lvl, nether);
    }

    /// <summary>A shape quad's brightness (emissive 1, else <see cref="QuadLevel"/> × its shade, at least <paramref name="minB"/>).</summary>
    public static float Quad(IVoxelWorld w, in GeoQuad q, int x, int y, int z, int sub, bool nether, float minB) =>
        q.Emissive ? 1f : Math.Max(minB, QuadLevel(w, q.LightFace, x, y, z, sub, nether) * q.Shade);
}
