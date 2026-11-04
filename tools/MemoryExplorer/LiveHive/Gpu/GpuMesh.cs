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
    /// <summary>1.12.2: the first row of the block-model textures (16 per row), below the fixed rows.</summary>
    public const int BlockRow = Rows;

    /// <summary>Rows of a built atlas: the fixed ones, plus the block-model textures when the atlas has models.</summary>
    public static int RowsFor(TextureAtlas? atlas) => atlas?.Models is { } m ? BlockRow + (m.Textures.Count + Cols - 1) / Cols : Rows;

    /// <summary>Top-left cell of entity texture <paramref name="id"/>.</summary>
    public static (int Col, int Row) EntityCell(int id) => (id % 4 * 4, EntityRow + id / 4 * 2);

    public static (int W, int H, byte[] Rgba) Build(TextureAtlas? atlas)
    {
        int t = atlas?.Tile ?? 16;
        var c = new Canvas(Cols * t, RowsFor(atlas) * t, t);
        if (atlas != null)
        {
            Terrain(c, atlas);
            Items(c, atlas);
            GrassSideCell(c, atlas);
        }
        WhiteAndShadow(c);
        c.Cell(WaterStill, ExtraRow, 1, 1, 16, 16, FluidTextures.WaterStill);
        c.Cell(WaterFlow, ExtraRow, 1, 1, 16, 16, FluidTextures.WaterFlow);
        c.Cell(LavaStill, ExtraRow, 1, 1, 16, 16, FluidTextures.LavaStill);
        c.Cell(LavaFlow, ExtraRow, 1, 1, 16, 16, FluidTextures.LavaFlow);
        c.Cell(FireA, ExtraRow, 1, 1, 16, 16, FluidTextures.FireA); // burning blocks and Render.renderEntityOnFire
        c.Cell(FireB, ExtraRow, 1, 1, 16, 16, FluidTextures.FireB);
        Entities(c, atlas);
        if (atlas?.Models is { } models)
        {
            var mt = models.Textures;
            for (int i = 0; i < mt.Count; i++) c.Cell(i % Cols, BlockRow + i / Cols, 1, 1, 16, 16, mt.Pixels(i));
        }
        return (c.W, c.H, c.Px);
    }

    /// <summary>The atlas being built: RGBA bytes, <see cref="T"/> pixels per cell.</summary>
    private sealed class Canvas(int w, int h, int t)
    {
        public readonly int W = w, H = h, T = t;
        public readonly byte[] Px = new byte[w * h * 4];

        public void Put(int x, int y, uint argb)
        {
            int i = (y * W + x) * 4;
            Px[i] = (byte)(argb >> 16); Px[i + 1] = (byte)(argb >> 8); Px[i + 2] = (byte)argb; Px[i + 3] = (byte)(argb >> 24);
        }

        /// <summary>Nearest-neighbour fit of an sw×sh image into cw×ch cells (1:1 for 16 px tiles and 64×32 entity textures).</summary>
        public void Cell(int col, int row, int cw, int ch, int sw, int sh, uint[] src)
        {
            int dw = cw * T, dh = ch * T;
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                    Put(col * T + x, row * T + y, src[y * sh / dh * sw + x * sw / dw]);
        }
    }

    private static void Terrain(Canvas c, TextureAtlas atlas)
    {
        int t = c.T;
        for (int tile = 0; tile < 256; tile++)
            for (int y = 0; y < t; y++)
                for (int x = 0; x < t; x++)
                    c.Put(tile % 16 * t + x, tile / 16 * t + y, atlas.Texel(tile, x, y));
    }

    private static void Items(Canvas c, TextureAtlas atlas)
    {
        if (atlas.Items is not { } items || items.Length < atlas.Size * atlas.Size) return;
        for (int y = 0; y < atlas.Size; y++)
            for (int x = 0; x < atlas.Size; x++)
                c.Put(x, ItemRow * c.T + y, items[y * atlas.Size + x]);
    }

    /// <summary>Grass side: tile 3 with the tinted fringe (tile 38) over it, like BlockTextures.Texel does per sample.</summary>
    private static void GrassSideCell(Canvas c, TextureAtlas atlas)
    {
        int t = c.T;
        for (int y = 0; y < t; y++)
            for (int x = 0; x < t; x++)
            {
                uint p = atlas.Texel(3, x, y), o = atlas.Texel(BlockTextures.GrassSideOverlay, x, y);
                if (o >> 24 >= 128) p = BlockTextures.Tinted(o, BlockTextures.GrassTint) | 0xff000000;
                c.Put(GrassSide * t + x, ExtraRow * t + y, p);
            }
    }

    private static void WhiteAndShadow(Canvas c)
    {
        int t = c.T;
        for (int y = 0; y < t; y++)
            for (int x = 0; x < t; x++)
            {
                c.Put(White * t + x, ExtraRow * t + y, 0xffffffff);
                double dx = (x + 0.5) / t * 2 - 1, dy = (y + 0.5) / t * 2 - 1, r = Math.Sqrt(dx * dx + dy * dy);
                uint a = (uint)Math.Clamp((1 - r) * 1.6 * 255, 0, 255);
                c.Put(Shadow * t + x, ExtraRow * t + y, a << 24);
            }
    }

    private static void Entities(Canvas c, TextureAtlas? atlas)
    {
        for (int id = 0; id < EntitySlots; id++)
        {
            var (ew, eh, epx) = EntityTextures.Get(atlas, id);
            if (ew <= 0 || eh <= 0 || epx.Length < ew * eh) continue;
            var (col, row) = EntityCell(id);
            c.Cell(col, row, 4, 2, ew, eh, epx);
        }
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
        bool tiled = textured && q.Tile >= 0;
        switch (q.Tex)
        {
            case QuadTex.Terrain when tiled:
                TerrainCell(q, out col, out row, ref tint);
                return true;
            case QuadTex.Block when tiled:
                // 1.12.2 model textures (a face's overlay is a second quad: see Emit).
                col = (byte)(q.Tile % GpuAtlas.Cols); row = (byte)(GpuAtlas.BlockRow + q.Tile / GpuAtlas.Cols);
                return true;
            case QuadTex.Item when tiled:
                col = (byte)(q.Tile & 15); row = (byte)(GpuAtlas.ItemRow + (q.Tile >> 4 & 15));
                return true;
            case QuadTex.Entity when IsEntitySlot(q.Tile):
                // Skin / mob textures (EntityTextures has stand-ins without the jar too); model UVs span the whole image = 4×2 cells.
                var (c, r) = GpuAtlas.EntityCell(q.Tile);
                col = (byte)c; row = (byte)r;
                uvScale = new Vector2(4, 2);
                flags |= GpuVertex.Span;
                return true;
            default:
                return ProceduralCell(q, ref col, ref row, ref flags);
        }
    }

    private static bool IsEntitySlot(int tile) => tile >= 0 && tile < GpuAtlas.EntitySlots;

    /// <summary>A terrain tile's cell (the grass side with its fringe has its own baked cell, untinted).</summary>
    private static void TerrainCell(in GeoQuad q, out byte col, out byte row, ref uint tint)
    {
        if (q.Overlay == BlockTextures.GrassSideOverlay && q.Tile == 3) { col = GpuAtlas.GrassSide; row = GpuAtlas.ExtraRow; tint = 0xffffff; return; }
        col = (byte)(q.Tile & 15); row = (byte)(q.Tile >> 4 & 15);
    }

    /// <summary>The procedural textures (MineClaw.Render's FluidTextures: available with or without the jar's): liquids (wrapping
    /// UVs) and fire; anything else is drawn in its flat colour (false, no flags).</summary>
    private static bool ProceduralCell(in GeoQuad q, ref byte col, ref byte row, ref byte flags)
    {
        switch (q.Tex)
        {
            case QuadTex.WaterStill or QuadTex.WaterFlow or QuadTex.LavaStill or QuadTex.LavaFlow:
                col = FluidCell(q.Tex);
                row = GpuAtlas.ExtraRow;
                flags |= GpuVertex.Wrap;
                return true;
            case QuadTex.Fire:
                col = (byte)(q.Tile == 1 ? GpuAtlas.FireB : GpuAtlas.FireA); row = GpuAtlas.ExtraRow;
                return true;
            default:
                flags = 0;
                return false;
        }
    }

    private static byte FluidCell(QuadTex tex) => tex switch
    {
        QuadTex.WaterStill => GpuAtlas.WaterStill,
        QuadTex.WaterFlow => GpuAtlas.WaterFlow,
        QuadTex.LavaStill => GpuAtlas.LavaStill,
        _ => GpuAtlas.LavaFlow,
    };

    public static void Emit(VertexList dst, in GeoQuad q, Vector3 offset, float light, bool textured) =>
        Emit(dst, q, offset, light, light, light, light, textured);

    /// <summary>A quad with a light per corner (P0..P3; smooth lighting). A 1.12.2 model face with an overlay (the grass side's
    /// tinted fringe, redstone dust's overlay) is the base quad plus the overlay as a second, alpha-tested quad on the same
    /// corners (drawn after it with LEQUAL: where the overlay is opaque it wins, as in the raycaster's per-texel compositing),
    /// each with its own tint, so a biome's grass colour and a wire's power colour reach the GPU as they reach the CPU.</summary>
    public static void Emit(VertexList dst, in GeoQuad q, Vector3 offset, float l0, float l1, float l2, float l3, bool textured)
    {
        EmitOne(dst, q, offset, l0, l1, l2, l3, textured);
        if (q.Tex == QuadTex.Block && q.Overlay >= 0 && textured)
        {
            var o = q;
            o.Tile = o.Overlay; o.Tint = o.OverlayTint; o.Overlay = -1; o.OverlayTint = 0;
            EmitOne(dst, o, offset, l0, l1, l2, l3, textured);
        }
    }

    private static void EmitOne(VertexList dst, in GeoQuad q, Vector3 offset, float l0, float l1, float l2, float l3, bool textured)
    {
        bool tex = Cell(q, textured, out byte col, out byte row, out uint tint, out var k, out byte flags);
        uint rgb = tex ? tint : q.Flat == 0 ? 0x808080 : q.Flat;
        float alpha = q.Alpha > 0 ? q.Alpha : q.Pass == RenderPass.Translucent && !tex ? 0.62f : 1f;
        uint rgba = Pack(rgb, alpha);
        ref var v0 = ref dst.Add4();
        var span = MemoryMarshal.CreateSpan(ref v0, 4);
        Set(ref span[0], q.P0 + offset, q.T0 * k, col, row, LightByte(l0), flags, rgba);
        Set(ref span[1], q.P1 + offset, q.T1 * k, col, row, LightByte(l1), flags, rgba);
        Set(ref span[2], q.P2 + offset, q.T2 * k, col, row, LightByte(l2), flags, rgba);
        Set(ref span[3], q.P3 + offset, q.T3 * k, col, row, LightByte(l3), flags, rgba);
    }

    /// <summary>A brightness 0..1 as the vertex byte (the shader reads it back as /255).</summary>
    public static byte LightByte(float light) => (byte)Math.Clamp((int)(light * 255 + 0.5f), 0, 255);

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
/// a build sees one consistent world). Light like <see cref="HiveVoxels"/>: the synced light where the hive sent it, else open
/// sky above the opaque top and unknown below.
/// </summary>
public sealed class ColumnNeighbourhood : IVoxelWorld
{
    private readonly ClientColumn?[] _c = new ClientColumn?[9];
    private int _cx, _cz, _h = ChunkColumn.SizeY;

    /// <summary>Blocks per column: the tallest of the nine (b1.8.1 128, 1.12.2 256).</summary>
    public int WorldHeight => _h;

    public void Set(HiveWorldCache cache, int dim, int cx, int cz)
    {
        _cx = cx; _cz = cz;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                _c[i * 3 + j] = cache.Get(dim, cx + i - 1, cz + j - 1);
        _h = ChunkColumn.SizeY;
        foreach (var c in _c) if (c != null && c.Snap.Height > _h) _h = c.Snap.Height;
    }

    public ClientColumn? Centre => _c[4];

    private ClientColumn? Col(int x, int z)
    {
        int i = (x >> 4) - _cx + 1, j = (z >> 4) - _cz + 1;
        return (uint)i < 3 && (uint)j < 3 ? _c[i * 3 + j] : null;
    }

    public int Block(int x, int y, int z) => (uint)y >= _h || Col(x, z) is not { } c || y >= c.Snap.Height ? 0 : c.Snap.Ids[ChunkColumn.Index(x & 15, y, z & 15)];
    public int Meta(int x, int y, int z) => (uint)y >= _h || Col(x, z) is not { } c || y >= c.Snap.Height ? 0 : c.Snap.Meta[ChunkColumn.Index(x & 15, y, z & 15)];

    public int SkyLight(int x, int y, int z)
    {
        if (y >= _h) return 15;
        if (y < 0) return 0;
        if (Col(x, z) is not { } c) return 15;
        return c.SkyAt(x, y, z);
    }

    public int BlockLight(int x, int y, int z)
    {
        if ((uint)y >= _h) return 0;
        return Col(x, z) is { } c ? c.BlockAt(x, y, z) : 0;
    }

    public int Biome(int x, int z) => Col(x, z)?.Light?.Biome(x, z) ?? -1;
    public ColumnState State(int x, int z) => Col(x, z) == null ? ColumnState.Unknown : ColumnState.Live;
}

/// <summary>
/// Meshes one 16×16×16 section into a solid (opaque + alpha-tested) and a translucent vertex list from exactly the geometry and
/// light the CPU raycaster (<see cref="EyeRenderer"/>) draws: <see cref="BlockMesh"/> decides a block's treatment and builds
/// its quads (the 1.12.2 models of <see cref="Models"/>, else the b1.8.1 <see cref="BlockGeometry"/>: stairs, fences, doors,
/// torches, plants, panes, fluid corner heights, neighbour-dependent states and metadata orientation), full cubes take a fast
/// path (six faces, culled the raycaster's way), and every vertex carries <see cref="FaceLight"/>'s brightness: smooth corners
/// (or vanilla flat light) on cube faces, the light cell × shade on shape quads, at <see cref="SkySubtracted"/> (the time of
/// day). Remembered-column dimming is the shader's (per section). Positions are relative to the section origin. Not
/// thread-safe; one per worker.
/// </summary>
public sealed class SectionMesher
{
    private readonly List<GeoQuad> _quads = new(64);
    private readonly float[] _corners = new float[4];
    public readonly VertexList Solid = new(), Translucent = new();
    public bool Textured { get; set; }
    public bool Nether { get; set; }
    /// <summary>World.k at the world time (0 by day, 11 at midnight): sky light minus this.</summary>
    public int SkySubtracted { get; set; }
    /// <summary>Smooth lighting on cube faces (per-corner averages, like the CPU eye's <c>Smooth</c>); false = vanilla flat light.</summary>
    public bool Smooth { get; set; } = true;
    /// <summary>Darkest a lit face gets (the CPU eye's MinBrightness).</summary>
    public float MinBrightness { get; set; } = 0.12f;
    /// <summary>1.12.2 block models (null = the b1.8.1 shapes): classification, cube faces and shapes come from them.</summary>
    public IBlockModels? Models { get; set; }
    /// <summary>When set, each block's vertex ranges are recorded (the parity tests attribute vertices to blocks).</summary>
    public List<BlockSpan>? Spans { get; set; }

    /// <summary>The vertices one block produced: [Solid0, Solid1) of <see cref="Solid"/>, [Trans0, Trans1) of <see cref="Translucent"/>.</summary>
    public readonly record struct BlockSpan(int X, int Y, int Z, int Solid0, int Solid1, int Trans0, int Trans1);

    /// <summary>True when the section holds any non-air block (false above the column's height).</summary>
    public static bool NonEmpty(ChunkSnapshot s, int sy)
    {
        if ((uint)(sy * 16) >= (uint)s.Height) return false;
        var ids = s.Ids.AsSpan();
        int b = ChunkColumn.Index(0, sy * 16, 0); // the section's half: (sy >> 3) << 15, then (sy & 7) · 16 into each 128-cell column
        for (int xz = 0; xz < 256; xz++)
            if (ids.Slice(b + xz * ChunkColumn.SizeY, 16).IndexOfAnyExcept((byte)0) >= 0) return true;
        return false;
    }

    private readonly SectionLevels _levels = new();

    public void Build(IVoxelWorld world, int cx, int sy, int cz)
    {
        Solid.Clear();
        Translucent.Clear();
        int bx = cx * 16, by = sy * 16, bz = cz * 16;
        _levels.Reset(world, bx, by, bz, SkySubtracted);
        IVoxelWorld w = _levels;
        var models = Models;
        for (int ly = 0; ly < 16; ly++)
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int x = bx + lx, y = by + ly, z = bz + lz;
                    int id = w.Block(x, y, z);
                    if (id == 0) continue;
                    var off = new Vector3(lx, ly, lz);
                    int s0 = Solid.Count, t0 = Translucent.Count;
                    BuildBlock(w, models, x, y, z, id, off);
                    Spans?.Add(new BlockSpan(x, y, z, s0, Solid.Count, t0, Translucent.Count));
                }
    }

    private void BuildBlock(IVoxelWorld w, IBlockModels? models, int x, int y, int z, int id, Vector3 off)
    {
        var shape = BlockMesh.Shape(models, id, w.Meta(x, y, z));
        if (models != null && BlockMesh.IsCube(shape)) { ModelCube(models, w, x, y, z, id, shape, off); return; }
        if (shape == BlockShape.Cube) { Cube(w, x, y, z, id, off); return; }
        _quads.Clear();
        BlockMesh.Quads(models, w, x, y, z, _quads);
        bool cube = BlockMesh.IsCube(shape); // b1.8.1 glass, leaves, spawners, ice: cube faces, cube light
        foreach (var q in _quads)
        {
            var dst = q.Pass == RenderPass.Translucent ? Translucent : Solid;
            if (cube && q.LightFace >= 0) EmitCubeFace(dst, w, q, id, x, y, z, (Face)q.LightFace, off, false);
            else QuadEmit.Emit(dst, q, off, FaceLight.Quad(w, q, x, y, z, SkySubtracted, Nether, MinBrightness), Textured);
        }
    }

    /// <summary>A full-cube face with the raycaster's cube light (<see cref="FaceLight.CubeCorners"/>) at each corner.</summary>
    private void EmitCubeFace(VertexList dst, IVoxelWorld w, in GeoQuad q, int id, int x, int y, int z, Face f, Vector3 off, bool emissive)
    {
        var c = _corners.AsSpan();
        if (emissive) c.Fill(1f);
        else FaceLight.CubeCorners(w, id, x, y, z, f, SkySubtracted, Nether, Smooth, MinBrightness, c);
        QuadEmit.Emit(dst, q, off, c[FaceLight.Corner(f, q.P0)], c[FaceLight.Corner(f, q.P1)], c[FaceLight.Corner(f, q.P2)], c[FaceLight.Corner(f, q.P3)], Textured);
    }

    /// <summary>A 1.12.2 full cube: its model's six faces, culled the raycaster's way (an opaque neighbour hides a face; glass
    /// beside the same glass shows none).</summary>
    private void ModelCube(IBlockModels models, IVoxelWorld w, int x, int y, int z, int id, BlockShape shape, Vector3 off)
    {
        int meta = w.Meta(x, y, z);
        bool emissive = models.Emissive(id);
        for (int fi = 0; fi < 6; fi++)
        {
            var f = (Face)fi;
            var (dx, dy, dz) = Lighting.Normal(f);
            int n = y + dy < 0 ? 0 : w.Block(x + dx, y + dy, z + dz);
            if (ModelFaceHidden(models, w, x + dx, y + dy, z + dz, n, id, shape)) continue;
            var q = models.CubeFace(w, x, y, z, id, meta, f);
            if (q.Tex != QuadTex.Block) continue;
            EmitCubeFace(q.Pass == RenderPass.Translucent ? Translucent : Solid, w, q, id, x, y, z, f, off, emissive);
        }
    }

    /// <summary>A model cube's face is hidden by an opaque cube beside it, or (not for leaves) by the same non-cube block (glass by glass).</summary>
    private static bool ModelFaceHidden(IBlockModels models, IVoxelWorld w, int nx, int ny, int nz, int n, int id, BlockShape shape)
    {
        if (n == 0) return false;
        var ns = models.Classify(n, w.Meta(nx, ny, nz));
        return ns == BlockShape.Cube || shape != BlockShape.Cube && n == id && id is not (18 or 161);
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
            var q = BlockMesh.CubeFace(w, x, y, z, id, meta, f);
            EmitCubeFace(Solid, w, q, id, x, y, z, f, off, false);
        }
    }
}

/// <summary>
/// The mesher's per-section memo of <see cref="FaceLight.CellLevel"/> and cell opacity over the section and a two-cell border
/// (smooth corners ask for each cell in front of a face from up to nine faces). Exactly the inner world's answers
/// (<see cref="FaceLight.CellLevelNow"/>), computed at most once per cell per build; everything else is passed through.
/// </summary>
internal sealed class SectionLevels : IVoxelWorld, FaceLight.ICellLevels
{
    private const int N = 20, B = 2;
    private readonly sbyte[] _lvl = new sbyte[N * N * N];
    private readonly byte[] _opq = new byte[N * N * N];
    private IVoxelWorld _w = null!;
    private int _x0, _y0, _z0, _sub;

    public void Reset(IVoxelWorld w, int bx, int by, int bz, int sub)
    {
        _w = w;
        (_x0, _y0, _z0, _sub) = (bx - B, by - B, bz - B, sub);
        Array.Fill(_lvl, sbyte.MinValue);
        Array.Clear(_opq);
    }

    private int Slot(int x, int y, int z)
    {
        int i = x - _x0, j = y - _y0, k = z - _z0;
        return (uint)i < N && (uint)j < N && (uint)k < N ? (j * N + k) * N + i : -1;
    }

    public int Level(int x, int y, int z, int subtracted)
    {
        int s = subtracted == _sub ? Slot(x, y, z) : -1;
        if (s < 0) return FaceLight.CellLevelNow(_w, x, y, z, subtracted);
        int v = _lvl[s];
        if (v == sbyte.MinValue) _lvl[s] = (sbyte)(v = FaceLight.CellLevelNow(_w, x, y, z, subtracted));
        return v;
    }

    public bool OpaqueAt(int x, int y, int z)
    {
        int s = Slot(x, y, z);
        if (s < 0) return FaceLight.Opaque(_w.Block(x, y, z));
        int v = _opq[s];
        if (v == 0) _opq[s] = (byte)(v = FaceLight.Opaque(_w.Block(x, y, z)) ? 2 : 1);
        return v == 2;
    }

    public int Block(int x, int y, int z) => _w.Block(x, y, z);
    public int Meta(int x, int y, int z) => _w.Meta(x, y, z);
    public int SkyLight(int x, int y, int z) => _w.SkyLight(x, y, z);
    public int BlockLight(int x, int y, int z) => _w.BlockLight(x, y, z);
    public ColumnState State(int x, int z) => _w.State(x, z);
    public int WorldHeight => _w.WorldHeight;
    public int Biome(int x, int z) => _w.Biome(x, z);
}
