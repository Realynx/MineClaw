using System.Numerics;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>How the CPU raycaster treats a block: a plain DDA cube face (fast path) or the block's quads.</summary>
public enum BlockShape : byte
{
    Air,
    /// <summary>Full opaque cube (render type 0): the DDA's entry face.</summary>
    Cube,
    /// <summary>Full cube with alpha-tested texels (fancy leaves, glass, spawner).</summary>
    CutoutCube,
    /// <summary>Full blended cube (ice).</summary>
    TranslucentCube,
    /// <summary>Anything else: intersect <see cref="BlockGeometry.Build"/>'s quads.</summary>
    Custom,
}

/// <summary>
/// Renderer-agnostic b1.8.1 block geometry: per-block textured quads in block-local coordinates (0..1), the way the client's
/// RenderBlocks draws each render type (Block.b(): 0 cube, 1 crossed squares, 2 torch, 3 fire, 4 fluids, 5 redstone wire,
/// 6 crops, 7 door, 8 ladder, 9 rail, 10 stairs, 11 fence, 12 lever, 13 cactus, 14 bed, 15 repeater, 16/17 piston,
/// 18 pane, 19 stem, 20 vine, 21 fence gate, −1 signs via their tile-entity model). Shapes that depend on metadata or
/// neighbours (door state, torch wall, fence connections, fluid corner heights, chest halves) read the world. The CPU
/// raycaster (<see cref="EyeRenderer"/>) intersects these quads; a GPU mesher can emit them as triangles unchanged.
/// Bounds that the server also knows (doors, buttons, trapdoors, slabs, snow, plates) follow mc-dev 218a2a71; client-only
/// shapes (torch tilt, crossed squares, crops, fluids) follow the b1.8 RenderBlocks they were ported from.
/// </summary>
public static class BlockGeometry
{
    /// <summary>b1.8.1 Block.b() render type of a block id (0 = standard cube; −1 = tile-entity model or invisible).</summary>
    public static int RenderType(int id) => (uint)id < (uint)RenderTypes.Length ? RenderTypes[id] : 0;

    /// <summary><see cref="RenderType"/> per block id 0..255 (0 unless listed).</summary>
    private static readonly sbyte[] RenderTypes = BuildRenderTypes();

    private static sbyte[] BuildRenderTypes()
    {
        var t = new sbyte[256];
        static void Set(sbyte[] t, int type, params int[] ids) { foreach (int id in ids) t[id] = (sbyte)type; }
        Set(t, -1, 0, Blocks.SignPost, Blocks.WallSign, Blocks.PistonMoving);
        Set(t, 1, Blocks.Sapling, Blocks.Web, Blocks.TallGrass, Blocks.DeadBush, Blocks.Dandelion, Blocks.Rose, Blocks.BrownMushroom, Blocks.RedMushroom, Blocks.SugarCane);
        Set(t, 2, Blocks.Torch, Blocks.RedstoneTorchOff, Blocks.RedstoneTorch);
        Set(t, 3, Blocks.Fire);
        Set(t, 4, Blocks.FlowingWater, Blocks.Water, Blocks.FlowingLava, Blocks.Lava);
        Set(t, 5, Blocks.RedstoneWire);
        Set(t, 6, Blocks.Crops);
        Set(t, 7, Blocks.WoodenDoor, Blocks.IronDoor);
        Set(t, 8, Blocks.Ladder);
        Set(t, 9, Blocks.Rail, Blocks.PoweredRail, Blocks.DetectorRail);
        Set(t, 10, Blocks.WoodenStairs, Blocks.CobblestoneStairs, Blocks.BrickStairs, Blocks.StoneBrickStairs);
        Set(t, 11, Blocks.Fence);
        Set(t, 12, Blocks.Lever);
        Set(t, 13, Blocks.Cactus);
        Set(t, 14, Blocks.Bed);
        Set(t, 15, Blocks.RepeaterOff, Blocks.RepeaterOn);
        Set(t, 16, Blocks.Piston, Blocks.StickyPiston);
        Set(t, 17, Blocks.PistonHead);
        Set(t, 18, Blocks.IronBars, Blocks.GlassPane);
        Set(t, 19, Blocks.PumpkinStem, Blocks.MelonStem);
        Set(t, 20, Blocks.Vine);
        Set(t, 21, Blocks.FenceGate);
        return t;
    }

    /// <summary>Render type 0 blocks whose bounds are not the full cube.</summary>
    private static bool PartialCube(int id) => id is Blocks.Slab or Blocks.SnowLayer or Blocks.StonePressurePlate or Blocks.WoodenPressurePlate
        or Blocks.Farmland or Blocks.Trapdoor or Blocks.Cake or Blocks.StoneButton or Blocks.Portal;

    public static bool IsFluid(int id) => id is >= Blocks.FlowingWater and <= Blocks.Lava;
    public static bool IsWater(int id) => id is Blocks.FlowingWater or Blocks.Water;

    /// <summary>The raycaster's treatment of a block.</summary>
    public static BlockShape Classify(int id)
    {
        if (id == 0) return BlockShape.Air;
        if (id is Blocks.Leaves or Blocks.Glass or Blocks.MobSpawner) return BlockShape.CutoutCube;
        if (id == Blocks.Ice) return BlockShape.TranslucentCube;
        if (RenderType(id) != 0 || PartialCube(id) || id == Blocks.SignPost || id == Blocks.WallSign) return BlockShape.Custom;
        return BlockShape.Cube;
    }

    /// <summary>Opaque full cube (Block.opaqueCubeLookup): hides the neighbour's face and blocks light.</summary>
    public static bool OpaqueCube(int id) => Classify(id) == BlockShape.Cube && id < 256;

    private static readonly bool[] _opaque = Enumerable.Range(0, 256).Select(i => OpaqueCube(i)).ToArray();
    private static bool Op(int id) => (uint)id < 256 && _opaque[id];

    // ------------------------------------------------------------------ textures

    /// <summary>Texture of a full-cube face, with the world-dependent cases (chest halves facing away from walls; fancy leaves).</summary>
    public static BlockTextures.TileRef CubeTile(IVoxelWorld w, int x, int y, int z, int id, int meta, Face f)
    {
        if (id == Blocks.Chest) return new(ChestTile(w, x, y, z, (int)f));
        return BlockTextures.For(id, meta, f, fancy: true);
    }

    /// <summary>BlockChest.getBlockTexture(IBlockAccess, x, y, z, side): single chests face away from an opaque neighbour,
    /// double chests show the left/right halves of the wide front (41/42) and back (57/58).</summary>
    public static int ChestTile(IVoxelWorld w, int i, int j, int k, int l)
    {
        const int idx = 26, id = Blocks.Chest;
        if (l is 0 or 1) return idx - 1;
        int i1 = w.Block(i, j, k - 1), j1 = w.Block(i, j, k + 1), k1 = w.Block(i - 1, j, k), l1 = w.Block(i + 1, j, k);
        if (i1 == id || j1 == id) return PairAlongZ(w, i, j, k, l, i1, k1, l1);
        if (k1 == id || l1 == id) return PairAlongX(w, i, j, k, l, i1, j1, k1);
        return SingleChestTile(l, i1, j1, k1, l1);
    }

    /// <summary>A single chest faces south unless that side is walled: then away from the opaque neighbour.</summary>
    private static int SingleChestTile(int l, int i1, int j1, int k1, int l1)
    {
        const int idx = 26;
        int b0 = 3;
        if (Op(j1) && !Op(i1)) b0 = 2;
        if (Op(k1) && !Op(l1)) b0 = 5;
        if (Op(l1) && !Op(k1)) b0 = 4;
        return l != b0 ? idx : idx + 1;
    }

    /// <summary>The front side of a double chest: <paramref name="otherSide"/> when only the other side (b, b2) is walled.</summary>
    private static int FrontAway(int a, int a2, int b, int b2, int side, int otherSide) =>
        (Op(b) || Op(b2)) && !Op(a) && !Op(a2) ? otherSide : side;

    /// <summary>A double chest along z (the partner north or south): its half of the wide front/back on the east/west faces.</summary>
    private static int PairAlongZ(IVoxelWorld w, int i, int j, int k, int l, int i1, int k1, int l1)
    {
        const int idx = 26;
        if (l is 2 or 3) return idx;
        int i2 = i1 == Blocks.Chest ? -1 : 0;
        int kz = i1 != Blocks.Chest ? k + 1 : k - 1;
        int k2 = w.Block(i - 1, j, kz), i3 = w.Block(i + 1, j, kz);
        if (l == 4) i2 = -1 - i2;
        int b1 = FrontAway(k1, k2, l1, i3, 5, 4);
        return (l != b1 ? idx + 32 : idx + 16) + i2;
    }

    /// <summary>A double chest along x (the partner west or east): its half of the wide front/back on the north/south faces.</summary>
    private static int PairAlongX(IVoxelWorld w, int i, int j, int k, int l, int i1, int j1, int k1)
    {
        const int idx = 26;
        if (l is 4 or 5) return idx;
        int j2 = k1 == Blocks.Chest ? -1 : 0;
        int ix = k1 != Blocks.Chest ? i + 1 : i - 1;
        int l2 = w.Block(ix, j, k - 1), j3 = w.Block(ix, j, k + 1);
        if (l == 3) j2 = -1 - j2;
        int b2 = FrontAway(i1, l2, j1, j3, 3, 2);
        return (l != b2 ? idx + 32 : idx + 16) + j2;
    }

    /// <summary>Flat palette colour of a face (no atlas on this machine).</summary>
    public static uint FlatColour(int id, Face f)
    {
        if (IsWater(id)) return 0x2f5cff;
        if (id is Blocks.Lava or Blocks.FlowingLava) return 0xf55a10;
        var m = IsoPalette.Mat(id);
        if (m == null) return 0x808080;
        return f is Face.Up or Face.Down ? m.Top : m.Side;
    }

    // ------------------------------------------------------------------ fluids (RenderBlocks.renderBlockFluids)

    /// <summary>BlockFluids.d(meta): the fraction of the cell that is air above the fluid; falling (≥ 8) counts as a source.</summary>
    public static float PercentAir(int meta) => ((meta >= 8 ? 0 : meta) + 1) / 9f;

    /// <summary>Surface height of a lone fluid cell: 1 − d(meta) (a source sits at 8/9 ≈ 0.889).</summary>
    public static float FluidHeight(int meta) => 1f - PercentAir(meta);

    private static bool SameFluid(int a, int b) => IsWater(a) ? IsWater(b) : a is Blocks.Lava or Blocks.FlowingLava && b is Blocks.Lava or Blocks.FlowingLava;

    private static bool SolidMaterial(int id) => id != 0 && !IsFluid(id) && Blocks.Get(id).Solid && RenderType(id) is not (1 or 2 or 6 or 9 or 5 or 19);

    /// <summary>
    /// RenderBlocks.getFluidHeight: the height of the top corner at block corner (i, k) of layer j, averaged over the 4 cells
    /// sharing it. Sources and falling fluid weigh 10 + 1, flowing cells 1, non-solid non-fluid cells count as empty (1 each),
    /// solid cells are skipped; the same fluid above any of the four makes the corner 1.0.
    /// </summary>
    public static float CornerHeight(IVoxelWorld w, int i, int j, int k, int fluidId)
    {
        int l = 0;
        float f = 0;
        for (int n = 0; n < 4; n++)
        {
            int x = i - (n & 1), z = k - (n >> 1 & 1);
            if (SameFluid(fluidId, w.Block(x, j + 1, z))) return 1f;
            int id = w.Block(x, j, z);
            if (SameFluid(fluidId, id))
            {
                int m = w.Meta(x, j, z);
                if (m >= 8 || m == 0) { f += PercentAir(m) * 10f; l += 10; }
                f += PercentAir(m);
                l++;
                continue;
            }
            if (!SolidMaterial(id)) { f++; l++; }
        }
        return l == 0 ? FluidHeight(0) : 1f - f / l;
    }

    /// <summary>The four top-corner heights of a fluid cell in the vanilla order (x0z0, x0z1, x1z1, x1z0).</summary>
    public static (float H00, float H01, float H11, float H10) FluidCorners(IVoxelWorld w, int x, int y, int z, int id) =>
        (CornerHeight(w, x, y, z, id), CornerHeight(w, x, y, z + 1, id), CornerHeight(w, x + 1, y, z + 1, id), CornerHeight(w, x + 1, y, z, id));

    /// <summary>BlockFluids.c(IBlockAccess,...): the effective flow decay of a cell, −1 when it isn't this fluid.</summary>
    private static int Decay(IVoxelWorld w, int x, int y, int z, int fluidId)
    {
        if (!SameFluid(fluidId, w.Block(x, y, z))) return -1;
        int m = w.Meta(x, y, z);
        return m >= 8 ? 0 : m;
    }

    /// <summary>BlockFluids.c(IBlockAccess, i, j, k) → Vec3D: the flow vector (x, z) toward lower neighbours, normalised.</summary>
    public static (double X, double Y, double Z) FlowVector(IVoxelWorld w, int i, int j, int k, int fluidId)
    {
        double vx = 0, vy = 0, vz = 0;
        int l = Decay(w, i, j, k, fluidId);
        for (int n = 0; n < 4; n++)
        {
            var (x, z) = n switch { 0 => (i - 1, k), 1 => (i, k - 1), 2 => (i + 1, k), _ => (i, k + 1) };
            if (FlowToward(w, x, j, z, fluidId, l) is not { } d) continue;
            vx += (x - i) * d; vz += (z - k) * d;
        }
        if (w.Meta(i, j, k) >= 8 && FallsBesideSolid(w, i, j, k))
        {
            double len0 = Math.Sqrt(vx * vx + vz * vz);
            if (len0 > 0) { vx /= len0; vz /= len0; }
            vy -= 6;
        }
        double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        return len < 1e-9 ? (0, 0, 0) : (vx / len, vy / len, vz / len);
    }

    /// <summary>The decay difference toward a neighbour (the same fluid there, or below it when it's open), null = no pull.</summary>
    private static int? FlowToward(IVoxelWorld w, int x, int j, int z, int fluidId, int l)
    {
        int l1 = Decay(w, x, j, z, fluidId);
        if (l1 >= 0) return l1 - l;
        if (SolidMaterial(w.Block(x, j, z))) return null;
        l1 = Decay(w, x, j - 1, z, fluidId);
        return l1 >= 0 ? l1 - (l - 8) : null;
    }

    /// <summary>A falling fluid cell with a solid block beside it (at its level or the one above): it streams straight down.</summary>
    private static bool FallsBesideSolid(IVoxelWorld w, int i, int j, int k) =>
        SolidMaterial(w.Block(i, j, k - 1)) || SolidMaterial(w.Block(i, j, k + 1)) || SolidMaterial(w.Block(i - 1, j, k))
        || SolidMaterial(w.Block(i + 1, j, k)) || SolidMaterial(w.Block(i, j + 1, k - 1)) || SolidMaterial(w.Block(i, j + 1, k + 1))
        || SolidMaterial(w.Block(i - 1, j + 1, k)) || SolidMaterial(w.Block(i + 1, j + 1, k));

    /// <summary>BlockFluids.a(IBlockAccess, …, material): atan2(flow.z, flow.x) − π/2, or −1000 when still.</summary>
    public static float FlowAngle(IVoxelWorld w, int x, int y, int z, int fluidId)
    {
        var (fx, _, fz) = FlowVector(w, x, y, z, fluidId);
        if (fx == 0 && fz == 0) return -1000f;
        return (float)(Math.Atan2(fz, fx) - Math.PI / 2);
    }

    private static void Fluid(IVoxelWorld w, int x, int y, int z, int id, List<GeoQuad> dst)
    {
        bool water = IsWater(id);
        var (h00, h01, h11, h10) = FluidCorners(w, x, y, z, id);
        var pass = water ? RenderPass.Translucent : RenderPass.Opaque;
        uint flat = FlatColour(id, Face.Up);
        bool emissive = !water;
        // Top: unless the same fluid is above (then every corner is 1.0 and the cell above draws nothing between them).
        if (!SameFluid(id, w.Block(x, y + 1, z)))
        {
            float ang = FlowAngle(w, x, y, z, id);
            QuadTex tex;
            Vector2 t0, t1, t2, t3;
            if (ang < -999f)
            {
                tex = water ? QuadTex.WaterStill : QuadTex.LavaStill;
                t0 = new(0, 0); t1 = new(0, 1); t2 = new(1, 1); t3 = new(1, 0);
            }
            else
            {
                // The flowing texture rotated to the flow (RenderBlocks: centre ± (cos, sin) of the angle, half the 2×2 region).
                tex = water ? QuadTex.WaterFlow : QuadTex.LavaFlow;
                float s = MathF.Sin(ang) * 0.5f, c = MathF.Cos(ang) * 0.5f;
                t0 = new(0.5f - c - s, 0.5f - c + s);
                t1 = new(0.5f - c + s, 0.5f + c + s);
                t2 = new(0.5f + c + s, 0.5f + c - s);
                t3 = new(0.5f + c - s, 0.5f - c - s);
            }
            var q = GeoQuad.Make(new(0, h00, 0), new(0, h01, 1), new(1, h11, 1), new(1, h10, 0), t0, t1, t2, t3);
            q.Tex = tex; q.Pass = pass; q.Flat = flat; q.Shade = 1; q.LightFace = (sbyte)Face.Up; q.Emissive = emissive; q.TwoSided = true;
            dst.Add(q);
        }
        // Bottom: over anything that isn't this fluid or an opaque cube.
        int below = w.Block(x, y - 1, z);
        if (!SameFluid(id, below) && !Op(below))
        {
            var q = GeoQuad.Make(new(0, 0, 1), new(1, 0, 1), new(1, 0, 0), new(0, 0, 0), new(0, 1), new(1, 1), new(1, 0), new(0, 0));
            q.Tex = water ? QuadTex.WaterStill : QuadTex.LavaStill; q.Pass = pass; q.Flat = flat; q.Shade = 0.5f;
            q.LightFace = (sbyte)Face.Down; q.Emissive = emissive;
            dst.Add(q);
        }
        // Sides up to the corner heights where the neighbour is neither this fluid nor opaque (vanilla: also not ice).
        void Side(Face f, int nx, int nz, Vector3 a, Vector3 b, float ha, float hb)
        {
            int n = w.Block(x + nx, y, z + nz);
            if (SameFluid(id, n) || Op(n) || n == Blocks.Ice) return;
            var q = GeoQuad.Make(new(a.X, ha, a.Z), new(b.X, hb, b.Z), new(b.X, 0, b.Z), new(a.X, 0, a.Z),
                new(0, (1 - ha) * 0.5f), new(1, (1 - hb) * 0.5f), new(1, 0.5f), new(0, 0.5f));
            q.Tex = water ? QuadTex.WaterFlow : QuadTex.LavaFlow; q.Pass = pass; q.Flat = flat; q.Shade = Lighting.FaceShade(f);
            q.LightFace = (sbyte)f; q.Emissive = emissive;
            dst.Add(q);
        }
        Side(Face.North, 0, -1, new(0, 0, 0), new(1, 0, 0), h00, h10);
        Side(Face.South, 0, 1, new(1, 0, 1), new(0, 0, 1), h11, h01);
        Side(Face.West, -1, 0, new(0, 0, 1), new(0, 0, 0), h01, h00);
        Side(Face.East, 1, 0, new(1, 0, 0), new(1, 0, 1), h10, h11);
    }

    // ------------------------------------------------------------------ boxes

    /// <summary>Block-local (u, v) of a point on a box face, the vanilla mapping (top/bottom: x, z; sides: across, 1 − y).</summary>
    public static Vector2 FaceUv(Face f, Vector3 p) => f switch
    {
        Face.Up or Face.Down => new(p.X, p.Z),
        Face.North => new(1 - p.X, 1 - p.Y),
        Face.South => new(p.X, 1 - p.Y),
        Face.West => new(p.Z, 1 - p.Y),
        _ => new(1 - p.Z, 1 - p.Y),
    };

    private delegate BlockTextures.TileRef TileFn(Face f);

    /// <summary>A box with vanilla per-face UVs (from the block-local position, so a slab shows the lower half of its side tile).
    /// <paramref name="skip"/>: bit per <see cref="Face"/> not to emit (culled against an opaque neighbour).</summary>
    private static void Box(List<GeoQuad> dst, float x0, float y0, float z0, float x1, float y1, float z1, TileFn tile, int id,
        RenderPass pass = RenderPass.Opaque, int skip = 0, Matrix4x4? xf = null, bool shade = true)
    {
        for (int fi = 0; fi < 6; fi++)
        {
            if ((skip >> fi & 1) != 0) continue;
            var f = (Face)fi;
            var (a, b, c, d) = BoxFace(f, x0, y0, z0, x1, y1, z1);
            var t = tile(f);
            var q = GeoQuad.Make(a, b, c, d, FaceUv(f, a), FaceUv(f, b), FaceUv(f, c), FaceUv(f, d));
            q.Tile = (short)t.Tile; q.Tint = t.Tint; q.Overlay = (short)t.Overlay; q.OverlayTint = t.OverlayTint;
            q.Pass = pass; q.Flat = FlatColour(id, f); q.Shade = shade ? Lighting.FaceShade(f) : 1f;
            q.LightFace = xf == null && OnBoundary(f, x0, y0, z0, x1, y1, z1) ? (sbyte)f : (sbyte)-1;
            q.Emissive = BlockTextures.Emissive(id);
            if (xf is { } m)
            {
                q.P0 = Vector3.Transform(q.P0, m); q.P1 = Vector3.Transform(q.P1, m); q.P2 = Vector3.Transform(q.P2, m); q.P3 = Vector3.Transform(q.P3, m);
                if (shade) q.Shade = ShadeOf(q.Normal);
            }
            dst.Add(q);
        }
    }

    /// <summary>The corners of one face of a box, in the vanilla vertex order.</summary>
    private static (Vector3 A, Vector3 B, Vector3 C, Vector3 D) BoxFace(Face f, float x0, float y0, float z0, float x1, float y1, float z1) => f switch
    {
        Face.Up => (new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0)),
        Face.Down => (new(x0, y0, z1), new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1)),
        Face.North => (new(x1, y1, z0), new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0)),
        Face.South => (new(x0, y1, z1), new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1)),
        Face.West => (new(x0, y1, z0), new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1)),
        _ => (new(x1, y1, z1), new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0)),
    };

    /// <summary>The face lies on the block's boundary (it takes the neighbouring cell's light).</summary>
    private static bool OnBoundary(Face f, float x0, float y0, float z0, float x1, float y1, float z1) => f switch
    {
        Face.Up => y1 >= 0.999f, Face.Down => y0 <= 0.001f, Face.North => z0 <= 0.001f, Face.South => z1 >= 0.999f,
        Face.West => x0 <= 0.001f, _ => x1 >= 0.999f,
    };

    /// <summary>The face shade for an arbitrary normal (blend of the axis shades).</summary>
    private static float ShadeOf(Vector3 n) =>
        n.Y > 0 ? 1f * n.Y + (1 - n.Y) * SideShade(n) : -n.Y * 0.5f + (1 + n.Y) * SideShade(n);

    private static float SideShade(Vector3 n)
    {
        float ax = Math.Abs(n.X), az = Math.Abs(n.Z);
        return ax + az < 1e-6 ? 1f : (ax * 0.6f + az * 0.8f) / (ax + az);
    }

    private static BlockTextures.TileRef Std(int id, int meta, Face f) => BlockTextures.For(id, meta, f, fancy: true);

    /// <summary>A two-sided textured plane (plants, ladders, rails, torch sides). Corners a..d with tile UV (0,0) at a, (1,0) at d.</summary>
    private static void Plane(List<GeoQuad> dst, Vector3 a, Vector3 b, Vector3 c, Vector3 d, int tile, uint tint, int id,
        RenderPass pass = RenderPass.Cutout, float shade = 1f, Vector2? uvA = null, Vector2? uvC = null, sbyte light = -1)
    {
        var ua = uvA ?? new Vector2(0, 0);
        var uc = uvC ?? new Vector2(1, 1);
        var q = GeoQuad.Make(a, b, c, d, new(ua.X, ua.Y), new(ua.X, uc.Y), new(uc.X, uc.Y), new(uc.X, ua.Y));
        q.Tile = (short)tile; q.Tint = tint; q.Pass = pass; q.Flat = FlatColour(id, Face.North); q.Shade = shade; q.LightFace = light;
        q.TwoSided = true; q.Emissive = BlockTextures.Emissive(id);
        dst.Add(q);
    }

    // ------------------------------------------------------------------ build

    /// <summary>The block being built: its world and cell, id, meta and the faces against opaque cubes (bit per <see cref="Face"/>).</summary>
    private readonly record struct Cell(IVoxelWorld W, int X, int Y, int Z, int Id, int Meta, int Skip);

    private delegate void Shaper(in Cell c, List<GeoQuad> dst);

    /// <summary>The shape builder per render type 1..21 (index = <see cref="RenderType"/>).</summary>
    private static readonly Shaper?[] ByRenderType =
    [
        null,
        (in Cell c, List<GeoQuad> d) => Crossed(c.W, c.X, c.Y, c.Z, c.Id, c.Meta, d),
        (in Cell c, List<GeoQuad> d) => Torch(c.Id, c.Meta, d),
        (in Cell c, List<GeoQuad> d) => Fire(d),
        (in Cell c, List<GeoQuad> d) => Fluid(c.W, c.X, c.Y, c.Z, c.Id, d),
        WireShape,
        (in Cell c, List<GeoQuad> d) => Crops(c.Meta, d),
        (in Cell c, List<GeoQuad> d) => Door(c.Id, c.Meta, d),
        LadderShape,
        (in Cell c, List<GeoQuad> d) => Rail(c.Id, c.Meta, d),
        (in Cell c, List<GeoQuad> d) => Stairs(c.Id, c.Meta, d, c.Skip),
        (in Cell c, List<GeoQuad> d) => Fence(c.W, c.X, c.Y, c.Z, d),
        (in Cell c, List<GeoQuad> d) => Lever(c.Meta, d),
        CactusShape,
        (in Cell c, List<GeoQuad> d) => Bed(c.Meta, d, c.Skip),
        RepeaterShape,
        PistonShape,
        PistonHeadShape,
        (in Cell c, List<GeoQuad> d) => Pane(c.W, c.X, c.Y, c.Z, c.Id, d),
        StemShape,
        (in Cell c, List<GeoQuad> d) => Vine(c.Meta, d),
        (in Cell c, List<GeoQuad> d) => Gate(c.Meta, d),
    ];

    /// <summary>Render type 0 and −1 blocks that aren't full cubes: their builder by id.</summary>
    private static readonly Shaper?[] ById = BuildShapersById();

    private static Shaper?[] BuildShapersById()
    {
        var t = new Shaper?[256];
        t[Blocks.SignPost] = t[Blocks.WallSign] = (in Cell c, List<GeoQuad> d) => Sign(c.Id, c.Meta, d);
        t[Blocks.Slab] = SlabShape;
        t[Blocks.SnowLayer] = SnowShape;
        t[Blocks.StonePressurePlate] = t[Blocks.WoodenPressurePlate] = PlateShape;
        t[Blocks.Farmland] = FarmlandShape;
        t[Blocks.Trapdoor] = TrapdoorShape;
        t[Blocks.Cake] = CakeShape;
        t[Blocks.StoneButton] = ButtonShape;
        t[Blocks.Portal] = PortalShape;
        return t;
    }

    /// <summary>Adds the quads of the block at (x, y, z) in block-local coordinates; faces against opaque cubes are left out.</summary>
    public static void Build(IVoxelWorld w, int x, int y, int z, List<GeoQuad> dst)
    {
        int id = w.Block(x, y, z);
        if (id == 0) return;
        var c = new Cell(w, x, y, z, id, w.Meta(x, y, z), OpaqueNeighbours(w, x, y, z));
        int rt = RenderType(id);
        if ((uint)rt < (uint)ByRenderType.Length && ByRenderType[rt] is { } shape) shape(c, dst);
        else if ((uint)id < (uint)ById.Length && ById[id] is { } special) special(c, dst);
        else FullCube(c, dst);
    }

    /// <summary>Faces (bit per <see cref="Face"/>) with an opaque cube beside them.</summary>
    private static int OpaqueNeighbours(IVoxelWorld w, int x, int y, int z)
    {
        int skip = 0;
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = Lighting.Normal((Face)f);
            if (Op(w.Block(x + dx, y + dy, z + dz))) skip |= 1 << f;
        }
        return skip;
    }

    /// <summary>A full cube (the GPU path; the raycaster takes the DDA fast path for these).</summary>
    private static void FullCube(in Cell c, List<GeoQuad> dst)
    {
        var w = c.W;
        int x = c.X, y = c.Y, z = c.Z, id = c.Id, meta = c.Meta;
        var shape = Classify(id);
        var pass = shape == BlockShape.CutoutCube ? RenderPass.Cutout : shape == BlockShape.TranslucentCube ? RenderPass.Translucent : RenderPass.Opaque;
        int cull = c.Skip;
        if (shape != BlockShape.Cube && id != Blocks.Leaves) cull |= SameNeighbours(w, x, y, z, id); // glass beside glass, ice beside ice
        int i0 = dst.Count;
        Box(dst, 0, 0, 0, 1, 1, 1, f => CubeTile(w, x, y, z, id, meta, f), id, pass, cull);
        if (id == Blocks.Ice) for (int i = i0; i < dst.Count; i++) { var q = dst[i]; q.Alpha = 0.62f; dst[i] = q; }
    }

    /// <summary>Faces (bit per <see cref="Face"/>) with the same block beside them.</summary>
    private static int SameNeighbours(IVoxelWorld w, int x, int y, int z, int id)
    {
        int same = 0;
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = Lighting.Normal((Face)f);
            if (w.Block(x + dx, y + dy, z + dz) == id) same |= 1 << f;
        }
        return same;
    }

    /// <summary>Redstone wire (type 5): a flat quad 1/64 up, tinted by its power.</summary>
    private static void WireShape(in Cell c, List<GeoQuad> dst)
    {
        int meta = c.Meta;
        float p = meta / 15f;
        float r = meta == 0 ? 0.3f : p * 0.6f + 0.4f, g = Math.Max(0, p * p * 0.7f - 0.5f), b = Math.Max(0, p * p * 0.6f - 0.7f);
        uint tint = (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
        var q = GeoQuad.Make(new(0, 1 / 64f, 0), new(0, 1 / 64f, 1), new(1, 1 / 64f, 1), new(1, 1 / 64f, 0), new(0, 0), new(0, 1), new(1, 1), new(1, 0));
        q.Tile = 164; q.Tint = tint; q.Pass = RenderPass.Cutout; q.Flat = 0x8a0a0a; q.TwoSided = true;
        dst.Add(q);
    }

    /// <summary>Ladders (type 8): a plane 0.05 in front of the wall its meta names.</summary>
    private static void LadderShape(in Cell c, List<GeoQuad> dst)
    {
        const float o = 0.05f;
        int id = c.Id;
        switch (c.Meta)
        {
            case 5: Plane(dst, new(o, 1, 1), new(o, 0, 1), new(o, 0, 0), new(o, 1, 0), 83, 0, id, light: (sbyte)Face.East); break;
            case 4: Plane(dst, new(1 - o, 1, 0), new(1 - o, 0, 0), new(1 - o, 0, 1), new(1 - o, 1, 1), 83, 0, id, light: (sbyte)Face.West); break;
            case 3: Plane(dst, new(0, 1, o), new(0, 0, o), new(1, 0, o), new(1, 1, o), 83, 0, id, light: (sbyte)Face.South); break;
            default: Plane(dst, new(1, 1, 1 - o), new(1, 0, 1 - o), new(0, 0, 1 - o), new(0, 1, 1 - o), 83, 0, id, light: (sbyte)Face.North); break;
        }
    }

    /// <summary>Cactus (type 13): top and bottom, the sides drawn 1/16 inside the cell at full width (spines are transparent texels).</summary>
    private static void CactusShape(in Cell c, List<GeoQuad> dst)
    {
        const float i = 1 / 16f;
        int id = c.Id, meta = c.Meta;
        Box(dst, 0, 0, 0, 1, 1, 1, f => Std(id, meta, f), id, RenderPass.Opaque, c.Skip | 0b111100);
        Plane(dst, new(1, 1, i), new(1, 0, i), new(0, 0, i), new(0, 1, i), 70, 0, id, shade: 0.8f);
        Plane(dst, new(0, 1, 1 - i), new(0, 0, 1 - i), new(1, 0, 1 - i), new(1, 1, 1 - i), 70, 0, id, shade: 0.8f);
        Plane(dst, new(i, 1, 0), new(i, 0, 0), new(i, 0, 1), new(i, 1, 1), 70, 0, id, shade: 0.6f);
        Plane(dst, new(1 - i, 1, 1), new(1 - i, 0, 1), new(1 - i, 0, 0), new(1 - i, 1, 0), 70, 0, id, shade: 0.6f);
    }

    /// <summary>Repeaters (type 15): a 2/16 slab, its top turned to the meta's facing.</summary>
    private static void RepeaterShape(in Cell c, List<GeoQuad> dst)
    {
        int tile = c.Id == Blocks.RepeaterOn ? 147 : 131, from = dst.Count;
        Box(dst, 0, 0, 0, 1, 2 / 16f, 1, f => f == Face.Up ? new(tile) : new(1), c.Id, RenderPass.Opaque, c.Skip & 1);
        RotateTopUv(dst, c.Meta & 3, from);
    }

    /// <summary>Pistons (type 16): a cube with the head (or the open face when extended) on the meta's facing.</summary>
    private static void PistonShape(in Cell c, List<GeoQuad> dst)
    {
        int facing = c.Meta & 7;
        bool ext = (c.Meta & 8) != 0;
        int front = ext ? 110 : c.Id == Blocks.StickyPiston ? 106 : 107;
        Box(dst, 0, 0, 0, 1, 1, 1, f => (int)f == facing ? new(front) : (int)f == (facing ^ 1) ? new(109) : new(108), c.Id, RenderPass.Opaque, c.Skip);
    }

    /// <summary>Piston heads (type 17): a 4/16 plate on the meta's facing.</summary>
    private static void PistonHeadShape(in Cell c, List<GeoQuad> dst)
    {
        var (nx, ny, nz) = Lighting.Normal((Face)(c.Meta & 7));
        float x0 = nx > 0 ? 0.75f : 0, x1 = nx < 0 ? 0.25f : 1, y0 = ny > 0 ? 0.75f : 0, y1 = ny < 0 ? 0.25f : 1, z0 = nz > 0 ? 0.75f : 0, z1 = nz < 0 ? 0.25f : 1;
        Box(dst, x0, y0, z0, x1, y1, z1, _ => new(107), c.Id);
    }

    /// <summary>Pumpkin/melon stems (type 19): crossed planes growing with the age, tinted green → orange.</summary>
    private static void StemShape(in Cell c, List<GeoQuad> dst)
    {
        int meta = c.Meta;
        float h = Math.Min(1f, (meta * 2 + 2) / 16f);
        uint tint = (uint)(meta * 32) << 16 | (uint)(255 - meta * 8) << 8 | (uint)(meta * 4);
        CrossAt(dst, 0.5f, 0, 0.5f, h, 111, tint, c.Id, new(0, 1 - h), new(1, 1));
    }

    private static void SlabShape(in Cell c, List<GeoQuad> dst)
    {
        int id = c.Id, meta = c.Meta;
        if ((meta & 8) != 0) Box(dst, 0, 0.5f, 0, 1, 1, 1, f => Std(id, meta, f), id, RenderPass.Opaque, c.Skip & ~1);
        else Box(dst, 0, 0, 0, 1, 0.5f, 1, f => Std(id, meta, f), id, RenderPass.Opaque, c.Skip & ~2);
    }

    private static void SnowShape(in Cell c, List<GeoQuad> dst)
    {
        int id = c.Id, meta = c.Meta;
        Box(dst, 0, 0, 0, 1, ((meta & 7) + 1) / 8f, 1, f => Std(id, meta, f), id, RenderPass.Opaque, c.Skip & ~2);
    }

    private static void PlateShape(in Cell c, List<GeoQuad> dst)
    {
        int id = c.Id, meta = c.Meta;
        float h = meta == 1 ? 1 / 32f : 1 / 16f;
        Box(dst, 1 / 16f, 0, 1 / 16f, 15 / 16f, h, 15 / 16f, f => Std(id, meta, f), id, RenderPass.Opaque, c.Skip & 1);
    }

    private static void FarmlandShape(in Cell c, List<GeoQuad> dst)
    {
        int id = c.Id, meta = c.Meta;
        Box(dst, 0, 0, 0, 1, 15 / 16f, 1, f => Std(id, meta, f), id, RenderPass.Opaque, c.Skip & ~2);
    }

    /// <summary>Trapdoors: a 3/16 slab on the floor, or standing against the side its meta names when open.</summary>
    private static void TrapdoorShape(in Cell c, List<GeoQuad> dst)
    {
        const float t = 3 / 16f;
        int id = c.Id, meta = c.Meta;
        TileFn tile = f => Std(id, meta, f);
        if ((meta & 4) == 0) { Box(dst, 0, 0, 0, 1, t, 1, tile, id, RenderPass.Cutout, c.Skip & 1); return; }
        switch (meta & 3)
        {
            case 0: Box(dst, 0, 0, 1 - t, 1, 1, 1, tile, id, RenderPass.Cutout); break;
            case 1: Box(dst, 0, 0, 0, 1, 1, t, tile, id, RenderPass.Cutout); break;
            case 2: Box(dst, 1 - t, 0, 0, 1, 1, 1, tile, id, RenderPass.Cutout); break;
            default: Box(dst, 0, 0, 0, t, 1, 1, tile, id, RenderPass.Cutout); break;
        }
    }

    /// <summary>Cake: shorter by two pixels per slice eaten (meta), the cut face showing the inside.</summary>
    private static void CakeShape(in Cell c, List<GeoQuad> dst)
    {
        int meta = c.Meta;
        float x0 = (1 + meta * 2) / 16f;
        Box(dst, x0, 0, 1 / 16f, 15 / 16f, 0.5f, 15 / 16f,
            f => f == Face.Up ? new(121) : f == Face.Down ? new(124) : f == Face.West && meta > 0 ? new(123) : new(122), c.Id, RenderPass.Cutout, c.Skip & 1);
    }

    /// <summary>Stone buttons: a 6×4 px box on the wall its meta names, pressed in when bit 8 is set.</summary>
    private static void ButtonShape(in Cell c, List<GeoQuad> dst)
    {
        int i1 = c.Meta & 7;
        float f3 = (c.Meta & 8) != 0 ? 1 / 16f : 0.125f;
        const float a = 0.375f, b = 0.625f, cc = 0.1875f;
        var (x0, y0, z0, x1, y1, z1) = i1 switch
        {
            1 => (0f, a, 0.5f - cc, f3, b, 0.5f + cc),
            2 => (1 - f3, a, 0.5f - cc, 1f, b, 0.5f + cc),
            3 => (0.5f - cc, a, 0f, 0.5f + cc, b, f3),
            _ => (0.5f - cc, a, 1 - f3, 0.5f + cc, b, 1f),
        };
        Box(dst, x0, y0, z0, x1, y1, z1, _ => new(1), c.Id);
    }

    /// <summary>Portals: a 4/16 sheet across the frame's axis, flat translucent purple.</summary>
    private static void PortalShape(in Cell c, List<GeoQuad> dst)
    {
        var w = c.W;
        int x = c.X, y = c.Y, z = c.Z;
        bool alongX = w.Block(x - 1, y, z) is Blocks.Portal or Blocks.Obsidian || w.Block(x + 1, y, z) is Blocks.Portal or Blocks.Obsidian;
        int before = dst.Count;
        if (alongX) Box(dst, 0, 0, 0.375f, 1, 1, 0.625f, _ => BlockTextures.TileRef.None, c.Id, RenderPass.Translucent, c.Skip);
        else Box(dst, 0.375f, 0, 0, 0.625f, 1, 1, _ => BlockTextures.TileRef.None, c.Id, RenderPass.Translucent, c.Skip);
        for (int i = before; i < dst.Count; i++) { var q = dst[i]; q.Tex = QuadTex.Flat; q.Flat = 0x7a2bd9; q.Alpha = 0.62f; q.Emissive = true; dst[i] = q; }
    }


    private static void RotateTopUv(List<GeoQuad> dst, int quarter, int from)
    {
        for (int i = from; i < dst.Count; i++)
        {
            var q = dst[i];
            if (q.Normal.Y < 0.9f) continue;
            for (int r = 0; r < quarter; r++) (q.T0, q.T1, q.T2, q.T3) = (q.T1, q.T2, q.T3, q.T0);
            dst[i] = q;
        }
    }

    /// <summary>RenderBlocks.renderCrossedSquares: two diagonal planes (0.45 from the centre), both sides.</summary>
    private static void CrossAt(List<GeoQuad> dst, float cx, float y0, float cz, float h, int tile, uint tint, int id, Vector2? uvA = null, Vector2? uvC = null)
    {
        float a = cx - 0.45f, b = cx + 0.45f, c = cz - 0.45f, d = cz + 0.45f;
        Plane(dst, new(a, y0 + h, c), new(a, y0, c), new(b, y0, d), new(b, y0 + h, d), tile, tint, id, uvA: uvA, uvC: uvC);
        Plane(dst, new(a, y0 + h, d), new(a, y0, d), new(b, y0, c), new(b, y0 + h, c), tile, tint, id, uvA: uvA, uvC: uvC);
    }

    private static void Crossed(IVoxelWorld w, int x, int y, int z, int id, int meta, List<GeoQuad> dst)
    {
        var t = BlockTextures.For(id, meta, Face.North);
        float cx = 0.5f, cy = 0, cz = 0.5f;
        if (id == Blocks.TallGrass)
        {
            // renderBlockReed: tall grass is jittered by a position hash.
            long l = (long)(x * 0x2fc20f) ^ (long)z * 0x6ebfff5L ^ y;
            l = l * l * 0x285b825L + l * 11L;
            cx += ((l >> 16 & 15L) / 15f - 0.5f) * 0.5f;
            cy += ((l >> 20 & 15L) / 15f - 1f) * 0.2f;
            cz += ((l >> 24 & 15L) / 15f - 0.5f) * 0.5f;
        }
        CrossAt(dst, cx, cy, cz, 1, t.Tile < 0 ? 15 : t.Tile, t.Tint, id);
    }

    private static void Fire(List<GeoQuad> dst)
    {
        CrossAt(dst, 0.5f, 0, 0.5f, 1.4f, 31, 0, Blocks.Fire, new(0, 0), new(1, 1));
        for (int i = dst.Count - 2; i < dst.Count; i++) { var q = dst[i]; q.Emissive = true; q.Tex = QuadTex.Fire; q.Tile = (short)(i % 2); q.Flat = 0xff9a20; dst[i] = q; }
    }

    /// <summary>RenderBlocks.renderBlockCrops: four planes in a # 4/16 in from the sides, 1/16 sunk, the stage tile 88 + meta.</summary>
    private static void Crops(int meta, List<GeoQuad> dst)
    {
        int tile = 88 + Math.Clamp(meta, 0, 7);
        const float y0 = -1 / 16f, y1 = 1 - 1 / 16f;
        foreach (float p in new[] { 0.25f, 0.75f })
        {
            Plane(dst, new(p, y1, 0), new(p, y0, 0), new(p, y0, 1), new(p, y1, 1), tile, 0, Blocks.Crops);
            Plane(dst, new(0, y1, p), new(0, y0, p), new(1, y0, p), new(1, y1, p), tile, 0, Blocks.Crops);
        }
    }

    /// <summary>
    /// RenderBlocks.renderBlockTorch / renderTorchAtAngle: meta 5 (or 0) stands upright; 1-4 hang on a wall, the foot against
    /// it and the head leaning 0.4 away: 1 = on the west block's face (leans east), 2 = east (leans west), 3 = north (leans
    /// south), 4 = south (leans north); wall torches sit 0.2 higher. Four 1/16-off planes of the full tile + the 2×2 px cap.
    /// </summary>
    private static void Torch(int id, int meta, List<GeoQuad> dst)
    {
        const double d = 0.4, d1 = 0.1, d2 = 0.2;
        double bx = 0, by = 0, bz = 0, tx = 0, tz = 0;
        switch (meta)
        {
            case 1: bx = -d1; by = d2; tx = -d; break;
            case 2: bx = d1; by = d2; tx = d; break;
            case 3: bz = -d1; by = d2; tz = -d; break;
            case 4: bz = d1; by = d2; tz = d; break;
        }
        TorchAt(id, (float)(bx + 0.5), (float)by, (float)(bz + 0.5), (float)tx, (float)tz, dst);
    }

    internal static void TorchAt(int id, float x, float y, float z, float dx, float dz, List<GeoQuad> dst)
    {
        int tile = id == Blocks.RedstoneTorchOff ? 115 : id == Blocks.RedstoneTorch ? 99 : 80;
        const float e = 1 / 16f, cap = 0.625f;
        // Head cap: the 2×2 texels at u 7..9, v 6..8 of the tile.
        float cx = x + dx * (1 - cap), cz = z + dz * (1 - cap);
        var top = GeoQuad.Make(new(cx - e, y + cap, cz - e), new(cx - e, y + cap, cz + e), new(cx + e, y + cap, cz + e), new(cx + e, y + cap, cz - e),
            new(7 / 16f, 6 / 16f), new(7 / 16f, 8 / 16f), new(9 / 16f, 8 / 16f), new(9 / 16f, 6 / 16f));
        top.Tile = (short)tile; top.Pass = RenderPass.Cutout; top.Flat = 0xffd84a; top.Emissive = id != Blocks.RedstoneTorchOff; top.TwoSided = true;
        dst.Add(top);
        var off = new Vector3(dx, 0, dz);
        void Side(Vector3 ta, Vector3 tb)
        {
            // Top edge at y+1, bottom edge shifted by the tilt (the foot against the wall).
            var q = GeoQuad.Make(ta, ta - new Vector3(0, 1, 0) + off, tb - new Vector3(0, 1, 0) + off, tb, new(0, 0), new(0, 1), new(1, 1), new(1, 0));
            q.Tile = (short)tile; q.Pass = RenderPass.Cutout; q.Flat = 0x8a6a3a; q.Emissive = id != Blocks.RedstoneTorchOff; q.TwoSided = true;
            dst.Add(q);
        }
        float yt = y + 1;
        Side(new(x - e, yt, z - 0.5f), new(x - e, yt, z + 0.5f));
        Side(new(x + e, yt, z + 0.5f), new(x + e, yt, z - 0.5f));
        Side(new(x + 0.5f, yt, z - e), new(x - 0.5f, yt, z - e));
        Side(new(x - 0.5f, yt, z + e), new(x + 0.5f, yt, z + e));
    }

    /// <summary>BlockDoor (mc-dev): state = (meta &amp; 4) == 0 ? (meta − 1) &amp; 3 : meta &amp; 3; bounds 3/16 thick on that side;
    /// face textures by getBlockTextureFromSideAndMetadata (negative = mirrored), upper half = tile − 16.</summary>
    private static void Door(int id, int meta, List<GeoQuad> dst)
    {
        int st = (meta & 4) == 0 ? (meta - 1) & 3 : meta & 3;
        const float f = 0.1875f;
        var (x0, z0, x1, z1) = st switch
        {
            0 => (0f, 0f, 1f, f),
            1 => (1 - f, 0f, 1f, 1f),
            2 => (0f, 1 - f, 1f, 1f),
            _ => (0f, 0f, f, 1f),
        };
        int baseTile = id == Blocks.IronDoor ? 98 : 97;
        int start = dst.Count;
        Box(dst, x0, 0, z0, x1, 1, z1, face => new(Math.Abs(DoorTex(baseTile, (int)face, meta))), id, RenderPass.Cutout);
        for (int i = start; i < dst.Count; i++)
        {
            var q = dst[i];
            int side = i - start; // Box emits faces in Face order 0..5
            if (DoorTex(baseTile, side, meta) < 0) (q.T0, q.T1, q.T2, q.T3) = (new(1 - q.T0.X, q.T0.Y), new(1 - q.T1.X, q.T1.Y), new(1 - q.T2.X, q.T2.Y), new(1 - q.T3.X, q.T3.Y));
            dst[i] = q;
        }
    }

    private static int DoorTex(int idx, int side, int meta)
    {
        if (side is 0 or 1) return idx;
        int k = (meta & 4) == 0 ? (meta - 1) & 3 : meta & 3;
        int tile = idx - (meta & 8) * 2;
        if ((k == 0 || k == 2) ^ (side <= 3)) return tile;
        int l = k / 2 + ((side & 1) ^ k);
        l += (meta & 4) / 4;
        return (l & 1) != 0 ? -tile : tile;
    }

    /// <summary>RenderBlocks.renderBlockMinecartTrack: a flat (or ascending) quad 1/16 up; meta 0 N-S, 1 E-W, 2-5 ascending
    /// E/W/N/S, 6-9 curves (tile 112); powered/detector rails have their own tiles.</summary>
    private static void Rail(int id, int meta, List<GeoQuad> dst)
    {
        int m = meta;
        if (id != Blocks.Rail) m &= 7;
        int tile = id == Blocks.PoweredRail ? ((meta & 8) != 0 ? 179 : 163) : id == Blocks.DetectorRail ? 195 : m >= 6 ? 112 : 128;
        float X0 = 0, X1 = 1;
        // Corner x/z per vanilla vertex order.
        float[] vx = [X1, X1, X0, X0], vz = [0, 1, 1, 0];
        if (m is 1 or 2 or 3 or 7) { vx = [X1, X0, X0, X1]; vz = [1, 1, 0, 0]; }
        else if (m == 8) { vx = [X0, X0, X1, X1]; vz = [1, 0, 0, 1]; }
        else if (m == 9) { vx = [X0, X1, X1, X0]; vz = [0, 0, 1, 1]; }
        const float h = 1 / 16f;
        float[] vy = [h, h, h, h];
        if (m is 2 or 4) { vy[0] += 1; vy[3] += 1; }
        if (m is 3 or 5) { vy[1] += 1; vy[2] += 1; }
        var q = GeoQuad.Make(new(vx[0], vy[0], vz[0]), new(vx[1], vy[1], vz[1]), new(vx[2], vy[2], vz[2]), new(vx[3], vy[3], vz[3]),
            new(1, 0), new(1, 1), new(0, 1), new(0, 0));
        q.Tile = (short)tile; q.Pass = RenderPass.Cutout; q.Flat = 0x8a8070; q.TwoSided = true;
        dst.Add(q);
    }

    /// <summary>b1.8.1 stairs (meta 0 high side east, 1 west, 2 south, 3 north): two standard boxes.</summary>
    private static void Stairs(int id, int meta, List<GeoQuad> dst, int skip)
    {
        TileFn t = f => Std(id, meta, f);
        switch (meta & 3)
        {
            case 0: Box(dst, 0, 0, 0, 0.5f, 0.5f, 1, t, id, RenderPass.Opaque, skip & 0b011101); Box(dst, 0.5f, 0, 0, 1, 1, 1, t, id, RenderPass.Opaque, skip & 0b101111); break;
            case 1: Box(dst, 0, 0, 0, 0.5f, 1, 1, t, id, RenderPass.Opaque, skip & 0b011111); Box(dst, 0.5f, 0, 0, 1, 0.5f, 1, t, id, RenderPass.Opaque, skip & 0b101101); break;
            case 2: Box(dst, 0, 0, 0, 1, 0.5f, 0.5f, t, id, RenderPass.Opaque, skip & 0b110101); Box(dst, 0, 0, 0.5f, 1, 1, 1, t, id, RenderPass.Opaque, skip & 0b111011); break;
            default: Box(dst, 0, 0, 0, 1, 1, 0.5f, t, id, RenderPass.Opaque, skip & 0b110111); Box(dst, 0, 0, 0.5f, 1, 0.5f, 1, t, id, RenderPass.Opaque, skip & 0b111001); break;
        }
    }

    private static bool FenceLike(int id) => id is Blocks.Fence or Blocks.FenceGate;

    /// <summary>RenderBlocks.renderBlockFence: a 6/16..10/16 post and two bars (12-15/16, 6-9/16) toward fence neighbours.</summary>
    private static void Fence(IVoxelWorld w, int x, int y, int z, List<GeoQuad> dst)
    {
        TileFn t = _ => new(4);
        Box(dst, 0.375f, 0, 0.375f, 0.625f, 1, 0.625f, t, Blocks.Fence);
        bool wv = FenceLike(w.Block(x - 1, y, z)), ev = FenceLike(w.Block(x + 1, y, z)), nv = FenceLike(w.Block(x, y, z - 1)), sv = FenceLike(w.Block(x, y, z + 1));
        const float a = 0.4375f, b = 0.5625f;
        foreach (var (y0, y1) in new[] { (0.75f, 0.9375f), (0.375f, 0.5625f) })
        {
            if (wv || ev) Box(dst, wv ? 0 : a, y0, a, ev ? 1 : b, y1, b, t, Blocks.Fence);
            if (nv || sv) Box(dst, a, y0, nv ? 0 : a, b, y1, sv ? 1 : b, t, Blocks.Fence);
        }
    }

    private static void Gate(int meta, List<GeoQuad> dst)
    {
        TileFn t = _ => new(4);
        bool alongX = (meta & 3) is 0 or 2;
        bool open = (meta & 4) != 0;
        const float p0 = 7 / 16f, p1 = 9 / 16f;
        if (alongX)
        {
            Box(dst, 0, 5 / 16f, p0, 2 / 16f, 1, p1, t, Blocks.FenceGate);
            Box(dst, 14 / 16f, 5 / 16f, p0, 1, 1, p1, t, Blocks.FenceGate);
            if (!open) foreach (var (y0, y1) in new[] { (6 / 16f, 9 / 16f), (12 / 16f, 15 / 16f) }) Box(dst, 2 / 16f, y0, p0, 14 / 16f, y1, p1, t, Blocks.FenceGate);
        }
        else
        {
            Box(dst, p0, 5 / 16f, 0, p1, 1, 2 / 16f, t, Blocks.FenceGate);
            Box(dst, p0, 5 / 16f, 14 / 16f, p1, 1, 1, t, Blocks.FenceGate);
            if (!open) foreach (var (y0, y1) in new[] { (6 / 16f, 9 / 16f), (12 / 16f, 15 / 16f) }) Box(dst, p0, y0, 2 / 16f, p1, y1, 14 / 16f, t, Blocks.FenceGate);
        }
    }

    private static void Lever(int meta, List<GeoQuad> dst)
    {
        int m = meta & 7;
        bool on = (meta & 8) != 0;
        TileFn cob = _ => new(16);
        Matrix4x4 stick;
        switch (m)
        {
            case 1: Box(dst, 0, 0.3125f, 0.25f, 0.1875f, 0.6875f, 0.75f, cob, Blocks.Lever); stick = Matrix4x4.CreateRotationZ(on ? -0.7f : 0.7f) * Matrix4x4.CreateTranslation(0.19f, 0.5f, 0.5f); break;
            case 2: Box(dst, 0.8125f, 0.3125f, 0.25f, 1, 0.6875f, 0.75f, cob, Blocks.Lever); stick = Matrix4x4.CreateRotationZ(on ? 0.7f : -0.7f) * Matrix4x4.CreateTranslation(0.81f, 0.5f, 0.5f); break;
            case 3: Box(dst, 0.25f, 0.3125f, 0, 0.75f, 0.6875f, 0.1875f, cob, Blocks.Lever); stick = Matrix4x4.CreateRotationX(on ? 0.7f : -0.7f) * Matrix4x4.CreateTranslation(0.5f, 0.5f, 0.19f); break;
            case 4: Box(dst, 0.25f, 0.3125f, 0.8125f, 0.75f, 0.6875f, 1, cob, Blocks.Lever); stick = Matrix4x4.CreateRotationX(on ? -0.7f : 0.7f) * Matrix4x4.CreateTranslation(0.5f, 0.5f, 0.81f); break;
            default: Box(dst, 0.3125f, 0, 0.25f, 0.6875f, 0.1875f, 0.75f, cob, Blocks.Lever); stick = Matrix4x4.CreateRotationX(on ? 0.7f : -0.7f) * Matrix4x4.CreateTranslation(0.5f, 0.1f, 0.5f); break;
        }
        // The handle points out of the base (local +y), so put the stick on the base's outward axis.
        var align = m switch { 1 => Matrix4x4.CreateRotationZ(-MathF.PI / 2), 2 => Matrix4x4.CreateRotationZ(MathF.PI / 2), 3 => Matrix4x4.CreateRotationX(MathF.PI / 2), 4 => Matrix4x4.CreateRotationX(-MathF.PI / 2), _ => Matrix4x4.Identity };
        Box(dst, -1 / 16f, 0, -1 / 16f, 1 / 16f, 10 / 16f, 1 / 16f, _ => new(96), Blocks.Lever, RenderPass.Cutout, 0, align * stick);
    }

    private static void Bed(int meta, List<GeoQuad> dst, int skip)
    {
        bool head = (meta & 8) != 0;
        int from = dst.Count;
        Box(dst, 0, 3 / 16f, 0, 1, 9 / 16f, 1, f => f == Face.Up ? new(head ? 135 : 134) : f == Face.Down ? new(4) : new(head ? 151 : 150), Blocks.Bed, RenderPass.Cutout, skip & 1);
        RotateTopUv(dst, (meta & 3) switch { 0 => 2, 1 => 3, 2 => 0, _ => 1 }, from);
    }

    /// <summary>Glass panes / iron bars (render type 18): a centre post plus arms toward panes, glass and opaque cubes; a pane
    /// with no neighbour draws both full arms like vanilla.</summary>
    private static void Pane(IVoxelWorld w, int x, int y, int z, int id, List<GeoQuad> dst)
    {
        int tile = id == Blocks.IronBars ? 85 : 49;
        bool C(int n) => n == id || n == Blocks.Glass || Op(n);
        bool wv = C(w.Block(x - 1, y, z)), ev = C(w.Block(x + 1, y, z)), nv = C(w.Block(x, y, z - 1)), sv = C(w.Block(x, y, z + 1));
        if (!wv && !ev && !nv && !sv) wv = ev = nv = sv = true;
        const float a = 7 / 16f, b = 9 / 16f;
        TileFn t = _ => new(tile);
        if (wv || ev) Box(dst, wv ? 0 : a, 0, a, ev ? 1 : b, 1, b, t, id, RenderPass.Cutout);
        if (nv || sv) Box(dst, a, 0, nv ? 0 : a, b, 1, sv ? 1 : b, t, id, RenderPass.Cutout);
    }

    /// <summary>Vines (type 20): planes 1/20 in front of the faces their meta bits name (1 S, 2 W, 4 N, 8 E; 0 = ceiling).</summary>
    private static void Vine(int meta, List<GeoQuad> dst)
    {
        const float o = 0.05f;
        uint tint = BlockTextures.FoliageTint;
        if ((meta & 1) != 0) Plane(dst, new(0, 1, 1 - o), new(0, 0, 1 - o), new(1, 0, 1 - o), new(1, 1, 1 - o), 143, tint, Blocks.Vine);
        if ((meta & 2) != 0) Plane(dst, new(o, 1, 0), new(o, 0, 0), new(o, 0, 1), new(o, 1, 1), 143, tint, Blocks.Vine);
        if ((meta & 4) != 0) Plane(dst, new(1, 1, o), new(1, 0, o), new(0, 0, o), new(0, 1, o), 143, tint, Blocks.Vine);
        if ((meta & 8) != 0) Plane(dst, new(1 - o, 1, 1), new(1 - o, 0, 1), new(1 - o, 0, 0), new(1 - o, 1, 0), 143, tint, Blocks.Vine);
        if (meta == 0) Plane(dst, new(0, 1 - o, 0), new(0, 1 - o, 1), new(1, 1 - o, 1), new(1, 1 - o, 0), 143, tint, Blocks.Vine);
    }

    /// <summary>Signs (TileEntitySignRenderer + ModelSign): a 24×12×2 px board (and a 2×14 stick when standing) at 2/3 scale;
    /// standing rotation meta·22.5°, wall signs by meta 2..5. Planks texture (item/sign.png is not read).</summary>
    private static void Sign(int id, int meta, List<GeoQuad> dst)
    {
        const float s = 0.6666667f / 16f;
        Matrix4x4 m;
        if (id == Blocks.SignPost)
            m = Matrix4x4.CreateScale(s, -s, -s) * Matrix4x4.CreateRotationY(-meta * 360f / 16f * MathF.PI / 180f) * Matrix4x4.CreateTranslation(0.5f, 0.5f, 0.5f);
        else
        {
            float ang = meta switch { 2 => 180f, 4 => 90f, 5 => -90f, _ => 0f };
            m = Matrix4x4.CreateScale(s, -s, -s) * Matrix4x4.CreateTranslation(0, -0.3125f, -0.4375f)
                * Matrix4x4.CreateRotationY(-ang * MathF.PI / 180f) * Matrix4x4.CreateTranslation(0.5f, 0.5f, 0.5f);
        }
        Box(dst, -12, -14, -1, 12, -2, 1, _ => new(4), id, RenderPass.Opaque, 0, m);
        if (id == Blocks.SignPost) Box(dst, -1, -2, -1, 1, 12, 1, _ => new(20), id, RenderPass.Opaque, 0, m);
        // Box UVs came from model-space positions: re-map every face to the full tile.
        for (int i = dst.Count - (id == Blocks.SignPost ? 12 : 6); i < dst.Count; i++)
        {
            var q = dst[i];
            q.T0 = new(0, 0); q.T1 = new(0, 1); q.T2 = new(1, 1); q.T3 = new(1, 0);
            dst[i] = q;
        }
    }
}
