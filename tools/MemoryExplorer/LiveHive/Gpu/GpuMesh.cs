using System.Numerics;
using System.Runtime.InteropServices;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.MemoryExplorer.LiveHive.Gpu;

/// <summary>
/// One GPU vertex (28 bytes): position (section- or camera-relative), tile-local UV (wraps for flowing fluids), the atlas cell
/// (column, row), the baked light (face light × shade, 0..255), flags, and the colour (tint or flat colour, alpha = constant
/// opacity of translucent quads). The shader turns (cell, uv) into atlas coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GpuVertex
{
    public float X, Y, Z, U, V;
    public byte Col, Row, Light, Flags;
    public uint Rgba;

    public const int Size = 28;
    public const byte Textured = 1, Wrap = 2, Span = 4;
}

/// <summary>A growable vertex list (quads: 4 vertices each), reused between builds.</summary>
public sealed class VertexList
{
    public GpuVertex[] V = new GpuVertex[4096];
    public int Count;
    public int Quads => Count / 4;

    public void Clear() => Count = 0;

    public ref GpuVertex Add4()
    {
        if (Count + 4 > V.Length) Array.Resize(ref V, V.Length * 2);
        ref var r = ref V[Count];
        Count += 4;
        return ref r;
    }
}

/// <summary>
/// The combined GPU texture, 16 cells wide (a cell = one terrain tile): terrain.png (rows 0–15), gui/items.png (rows 16–31), a row
/// of extras (white for flat colours, the grass side with its tinted fringe baked in, the drop shadow, the four procedural
/// liquid textures from <see cref="FluidTextures"/>), then the entity textures of <see cref="EntityTextures"/> (player skin
/// char.png = id 0, the mobs, their stand-ins) as 4×2-cell blocks, four per row. Texels are 0xAARRGGBB in MineClaw.Render and
/// are written as R, G, B, A bytes (GL_RGBA / GL_UNSIGNED_BYTE), so a skin texel on the GPU is exactly the PNG's RGBA.
/// </summary>
public static class GpuAtlas
{
    public const int Cols = 16, ItemRow = 16, ExtraRow = 32, EntityRow = 33, EntitySlots = 20, Rows = EntityRow + EntitySlots / 4 * 2;
    public const int White = 0, GrassSide = 1, Shadow = 2, WaterStill = 3, WaterFlow = 4, LavaStill = 5, LavaFlow = 6, FireA = 7, FireB = 8;

    /// <summary>Top-left cell of entity texture <paramref name="id"/>.</summary>
    public static (int Col, int Row) EntityCell(int id) => (id % 4 * 4, EntityRow + id / 4 * 2);

    public static (int W, int H, byte[] Rgba) Build(TextureAtlas? atlas)
    {
        int t = atlas?.Tile ?? 16;
        int w = Cols * t, h = Rows * t;
        var px = new byte[w * h * 4];
        void Put(int x, int y, uint argb)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)(argb >> 16); px[i + 1] = (byte)(argb >> 8); px[i + 2] = (byte)argb; px[i + 3] = (byte)(argb >> 24);
        }
        void Cell(int col, int row, int cw, int ch, int sw, int sh, uint[] src)
        {
            // Nearest-neighbour fit of an sw×sh image into cw×ch cells (1:1 for 16 px tiles and 64×32 entity textures).
            int dw = cw * t, dh = ch * t;
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                    Put(col * t + x, row * t + y, src[y * sh / dh * sw + x * sw / dw]);
        }
        if (atlas != null)
        {
            for (int tile = 0; tile < 256; tile++)
                for (int y = 0; y < t; y++)
                    for (int x = 0; x < t; x++)
                        Put(tile % 16 * t + x, tile / 16 * t + y, atlas.Texel(tile, x, y));
            if (atlas.Items is { } items && items.Length >= atlas.Size * atlas.Size)
                for (int y = 0; y < atlas.Size; y++)
                    for (int x = 0; x < atlas.Size; x++)
                        Put(x, ItemRow * t + y, items[y * atlas.Size + x]);
            // Grass side: tile 3 with the tinted fringe (tile 38) over it, like BlockTextures.Texel does per sample.
            for (int y = 0; y < t; y++)
                for (int x = 0; x < t; x++)
                {
                    uint p = atlas.Texel(3, x, y), o = atlas.Texel(BlockTextures.GrassSideOverlay, x, y);
                    if (o >> 24 >= 128) p = BlockTextures.Tinted(o, BlockTextures.GrassTint) | 0xff000000;
                    Put(GrassSide * t + x, ExtraRow * t + y, p);
                }
        }
        for (int y = 0; y < t; y++)
            for (int x = 0; x < t; x++)
            {
                Put(White * t + x, ExtraRow * t + y, 0xffffffff);
                double dx = (x + 0.5) / t * 2 - 1, dy = (y + 0.5) / t * 2 - 1, r = Math.Sqrt(dx * dx + dy * dy);
                uint a = (uint)Math.Clamp((1 - r) * 1.6 * 255, 0, 255);
                Put(Shadow * t + x, ExtraRow * t + y, a << 24);
            }
        Cell(WaterStill, ExtraRow, 1, 1, 16, 16, FluidTextures.WaterStill);
        Cell(WaterFlow, ExtraRow, 1, 1, 16, 16, FluidTextures.WaterFlow);
        Cell(LavaStill, ExtraRow, 1, 1, 16, 16, FluidTextures.LavaStill);
        Cell(LavaFlow, ExtraRow, 1, 1, 16, 16, FluidTextures.LavaFlow);
        Cell(FireA, ExtraRow, 1, 1, 16, 16, FluidTextures.FireA); // burning blocks and Render.renderEntityOnFire
        Cell(FireB, ExtraRow, 1, 1, 16, 16, FluidTextures.FireB);
        for (int id = 0; id < EntitySlots; id++)
        {
            var (ew, eh, epx) = EntityTextures.Get(atlas, id);
            if (ew <= 0 || eh <= 0 || epx.Length < ew * eh) continue;
            var (c, r) = EntityCell(id);
            Cell(c, r, 4, 2, ew, eh, epx);
        }
        return (w, h, px);
    }

    /// <summary>The 0xAARRGGBB value of the texel at (x, y) of a built atlas (tests).</summary>
    public static uint Texel(byte[] rgba, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (uint)rgba[i + 3] << 24 | (uint)rgba[i] << 16 | (uint)rgba[i + 1] << 8 | rgba[i + 2];
    }
}

/// <summary>
/// Turns <see cref="GeoQuad"/>s (MineClaw.Render's renderer-agnostic block and entity geometry) into <see cref="GpuVertex"/>es:
/// atlas cell from the quad's texture (terrain, items, the procedural liquids, entity skins/mob textures with their model UVs),
/// light from the cell the quad faces (vanilla flat lighting, the same numbers the CPU raycaster uses), the flat colour when
/// there is no texture.
/// </summary>
public static class QuadEmit
{
    public static uint Pack(uint rgb, float alpha) =>
        (rgb >> 16 & 0xff) | (rgb & 0xff00) | (rgb & 0xff) << 16 | (uint)Math.Clamp((int)(alpha * 255 + 0.5f), 0, 255) << 24;

    /// <summary>Atlas cell, UV scale (cells the texture spans) and flags for a quad; false = draw its flat colour.</summary>
    public static bool Cell(in GeoQuad q, bool textured, out byte col, out byte row, out uint tint, out Vector2 uvScale, out byte flags)
    {
        tint = q.Tint == 0 ? 0xffffff : q.Tint;
        col = row = 0;
        uvScale = Vector2.One;
        flags = GpuVertex.Textured;
        switch (q.Tex)
        {
            case QuadTex.Terrain when textured && q.Tile >= 0:
                if (q.Overlay == BlockTextures.GrassSideOverlay && q.Tile == 3) { col = GpuAtlas.GrassSide; row = GpuAtlas.ExtraRow; tint = 0xffffff; return true; }
                col = (byte)(q.Tile & 15); row = (byte)(q.Tile >> 4 & 15);
                return true;
            case QuadTex.Item when textured && q.Tile >= 0:
                col = (byte)(q.Tile & 15); row = (byte)(GpuAtlas.ItemRow + (q.Tile >> 4 & 15));
                return true;
            case QuadTex.WaterStill or QuadTex.WaterFlow or QuadTex.LavaStill or QuadTex.LavaFlow:
                // Procedural (MineClaw.Render's FluidTextures): available with or without the jar's textures.
                col = q.Tex switch { QuadTex.WaterStill => GpuAtlas.WaterStill, QuadTex.WaterFlow => GpuAtlas.WaterFlow, QuadTex.LavaStill => GpuAtlas.LavaStill, _ => GpuAtlas.LavaFlow };
                row = GpuAtlas.ExtraRow;
                flags |= GpuVertex.Wrap;
                return true;
            case QuadTex.Fire:
                col = (byte)(q.Tile == 1 ? GpuAtlas.FireB : GpuAtlas.FireA); row = GpuAtlas.ExtraRow;
                return true;
            case QuadTex.Entity when q.Tile >= 0 && q.Tile < GpuAtlas.EntitySlots:
                // Skin / mob textures (EntityTextures has stand-ins without the jar too); model UVs span the whole image = 4×2 cells.
                var (c, r) = GpuAtlas.EntityCell(q.Tile);
                col = (byte)c; row = (byte)r;
                uvScale = new Vector2(4, 2);
                flags |= GpuVertex.Span;
                return true;
            default:
                flags = 0;
                return false;
        }
    }

    public static void Emit(VertexList dst, in GeoQuad q, Vector3 offset, float light, bool textured)
    {
        bool tex = Cell(q, textured, out byte col, out byte row, out uint tint, out var k, out byte flags);
        uint rgb = tex ? tint : q.Flat == 0 ? 0x808080 : q.Flat;
        float alpha = q.Alpha > 0 ? q.Alpha : q.Pass == RenderPass.Translucent && !tex ? 0.62f : 1f;
        uint rgba = Pack(rgb, alpha);
        byte l = (byte)Math.Clamp((int)(light * 255 + 0.5f), 0, 255);
        ref var v0 = ref dst.Add4();
        var span = MemoryMarshal.CreateSpan(ref v0, 4);
        Set(ref span[0], q.P0 + offset, q.T0 * k, col, row, l, flags, rgba);
        Set(ref span[1], q.P1 + offset, q.T1 * k, col, row, l, flags, rgba);
        Set(ref span[2], q.P2 + offset, q.T2 * k, col, row, l, flags, rgba);
        Set(ref span[3], q.P3 + offset, q.T3 * k, col, row, l, flags, rgba);
    }

    /// <summary>A quad on one of the extras cells (the shadow), coloured <paramref name="rgb"/>.</summary>
    public static void EmitExtra(VertexList dst, in GeoQuad q, Vector3 offset, float light, int extraCol, uint rgb)
    {
        uint rgba = Pack(rgb, q.Alpha > 0 ? q.Alpha : 1);
        byte l = (byte)Math.Clamp((int)(light * 255 + 0.5f), 0, 255), col = (byte)extraCol, row = GpuAtlas.ExtraRow;
        ref var v0 = ref dst.Add4();
        var span = MemoryMarshal.CreateSpan(ref v0, 4);
        Set(ref span[0], q.P0 + offset, q.T0, col, row, l, GpuVertex.Textured, rgba);
        Set(ref span[1], q.P1 + offset, q.T1, col, row, l, GpuVertex.Textured, rgba);
        Set(ref span[2], q.P2 + offset, q.T2, col, row, l, GpuVertex.Textured, rgba);
        Set(ref span[3], q.P3 + offset, q.T3, col, row, l, GpuVertex.Textured, rgba);
    }

    private static void Set(ref GpuVertex v, Vector3 p, Vector2 t, byte col, byte row, byte l, byte flags, uint rgba)
    {
        v.X = p.X; v.Y = p.Y; v.Z = p.Z; v.U = t.X; v.V = t.Y; v.Col = col; v.Row = row; v.Light = l; v.Flags = flags; v.Rgba = rgba;
    }
}

/// <summary>
/// <see cref="IVoxelWorld"/> over a 3×3 block of cached columns around one column (fixed references for one mesh build, so
/// a build sees one consistent world). Sky light like <see cref="HiveVoxels"/>: open sky above the opaque top, unknown below.
/// </summary>
public sealed class ColumnNeighbourhood : IVoxelWorld
{
    private readonly ClientColumn?[] _c = new ClientColumn?[9];
    private int _cx, _cz;

    public void Set(HiveWorldCache cache, int dim, int cx, int cz)
    {
        _cx = cx; _cz = cz;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                _c[i * 3 + j] = cache.Get(dim, cx + i - 1, cz + j - 1);
    }

    public ClientColumn? Centre => _c[4];

    private ClientColumn? Col(int x, int z)
    {
        int i = (x >> 4) - _cx + 1, j = (z >> 4) - _cz + 1;
        return (uint)i < 3 && (uint)j < 3 ? _c[i * 3 + j] : null;
    }

    public int Block(int x, int y, int z) => (uint)y >= ChunkColumn.SizeY || Col(x, z) is not { } c ? 0 : c.Snap.Ids[ChunkColumn.Index(x & 15, y, z & 15)];
    public int Meta(int x, int y, int z) => (uint)y >= ChunkColumn.SizeY || Col(x, z) is not { } c ? 0 : c.Snap.Meta[ChunkColumn.Index(x & 15, y, z & 15)];

    public int SkyLight(int x, int y, int z)
    {
        if (y >= ChunkColumn.SizeY) return 15;
        if (y < 0) return 0;
        if (Col(x, z) is not { } c) return 15;
        return y > c.Top[(x & 15) * 16 + (z & 15)] ? 15 : -1;
    }

    public int BlockLight(int x, int y, int z) => SkyLight(x, y, z) < 0 ? -1 : 0;
    public ColumnState State(int x, int z) => Col(x, z) == null ? ColumnState.Unknown : ColumnState.Live;
}

/// <summary>
/// Meshes one 16×16×16 section into a solid (opaque + alpha-tested) and a translucent vertex list. Full opaque cubes take a
/// fast path (six faces, culled against opaque neighbours); every other block goes through <see cref="BlockGeometry.Build"/>,
/// so stairs, fences, doors, torches, plants, panes and fluid corner heights match the shared b1.8.1 geometry exactly.
/// Positions are relative to the section origin. Not thread-safe; one per worker.
/// </summary>
public sealed class SectionMesher
{
    private readonly List<GeoQuad> _quads = new(64);
    public readonly VertexList Solid = new(), Translucent = new();
    public bool Textured { get; set; }
    public bool Nether { get; set; }
    public int SkySubtracted { get; set; }
    /// <summary>Darkest a lit face gets (the CPU eye's MinBrightness).</summary>
    public float MinBrightness { get; set; } = 0.12f;

    /// <summary>True when the section holds any non-air block.</summary>
    public static bool NonEmpty(ChunkSnapshot s, int sy)
    {
        var ids = s.Ids.AsSpan();
        for (int xz = 0; xz < 256; xz++)
            if (ids.Slice(xz * ChunkColumn.SizeY + sy * 16, 16).IndexOfAnyExcept((byte)0) >= 0) return true;
        return false;
    }

    public void Build(IVoxelWorld w, int cx, int sy, int cz)
    {
        Solid.Clear();
        Translucent.Clear();
        int bx = cx * 16, by = sy * 16, bz = cz * 16;
        for (int ly = 0; ly < 16; ly++)
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int x = bx + lx, y = by + ly, z = bz + lz;
                    int id = w.Block(x, y, z);
                    if (id == 0) continue;
                    var off = new Vector3(lx, ly, lz);
                    if (BlockGeometry.Classify(id) == BlockShape.Cube) { Cube(w, x, y, z, id, off); continue; }
                    _quads.Clear();
                    try { BlockGeometry.Build(w, x, y, z, _quads); }
                    catch (Exception) { continue; } // a shape that can't be built here: leave the cell empty
                    foreach (var q in _quads)
                        QuadEmit.Emit(q.Pass == RenderPass.Translucent ? Translucent : Solid, q, off, LightOf(w, id, x, y, z, q), Textured);
                }
    }

    private float LightOf(IVoxelWorld w, int id, int x, int y, int z, in GeoQuad q)
    {
        float k;
        if (q.Emissive || BlockTextures.Emissive(id)) k = 1f;
        else
        {
            int lx = x, ly = y, lz = z;
            if (q.LightFace >= 0) { var (dx, dy, dz) = Lighting.Normal((Face)q.LightFace); lx += dx; ly += dy; lz += dz; }
            int lvl = FaceLight.CellLevel(w, lx, ly, lz, SkySubtracted);
            k = lvl < 0 ? 0.75f : Math.Max(0.05f, Lighting.Brightness(lvl, Nether));
        }
        return Math.Max(MinBrightness, k * q.Shade);
    }

    private void Cube(IVoxelWorld w, int x, int y, int z, int id, Vector3 off)
    {
        int meta = -1;
        for (int fi = 0; fi < 6; fi++)
        {
            var f = (Face)fi;
            var (dx, dy, dz) = Lighting.Normal(f);
            int n = y + dy < 0 ? 0 : w.Block(x + dx, y + dy, z + dz);
            if (n != 0 && BlockGeometry.OpaqueCube(n)) continue;
            if (meta < 0) meta = w.Meta(x, y, z);
            Vector3 a, b, c, d;
            switch (f)
            {
                case Face.Up: a = new(0, 1, 0); b = new(0, 1, 1); c = new(1, 1, 1); d = new(1, 1, 0); break;
                case Face.Down: a = new(0, 0, 1); b = new(0, 0, 0); c = new(1, 0, 0); d = new(1, 0, 1); break;
                case Face.North: a = new(1, 1, 0); b = new(1, 0, 0); c = new(0, 0, 0); d = new(0, 1, 0); break;
                case Face.South: a = new(0, 1, 1); b = new(0, 0, 1); c = new(1, 0, 1); d = new(1, 1, 1); break;
                case Face.West: a = new(0, 1, 0); b = new(0, 0, 0); c = new(0, 0, 1); d = new(0, 1, 1); break;
                default: a = new(1, 1, 1); b = new(1, 0, 1); c = new(1, 0, 0); d = new(1, 1, 0); break;
            }
            var t = BlockGeometry.CubeTile(w, x, y, z, id, meta, f);
            var q = GeoQuad.Make(a, b, c, d, BlockGeometry.FaceUv(f, a), BlockGeometry.FaceUv(f, b), BlockGeometry.FaceUv(f, c), BlockGeometry.FaceUv(f, d));
            q.Tile = (short)t.Tile; q.Tint = t.Tint; q.Overlay = (short)t.Overlay; q.OverlayTint = t.OverlayTint;
            q.Flat = BlockGeometry.FlatColour(id, f); q.Shade = Lighting.FaceShade(f); q.LightFace = (sbyte)f;
            q.Emissive = BlockTextures.Emissive(id);
            if (t.IsNone || BlockTextures.UsePalette(id)) q.Tile = -1;
            QuadEmit.Emit(Solid, q, off, LightOf(w, id, x, y, z, q), Textured);
        }
    }
}
