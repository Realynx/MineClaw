using System.Numerics;

namespace MineClaw.Render;

/// <summary>
/// What every renderer draws for a block, in one place (the CPU raycaster <see cref="EyeRenderer"/> and the Explorer's GPU
/// section mesher both call it, so the two can't drift): the treatment (<see cref="Shape"/>: the 1.12.2 models' classification
/// when there are models, else b1.8.1's <see cref="BlockGeometry.Classify"/>), the quads of every non-cube block
/// (<see cref="Quads"/>: models first, the b1.8.1 shapes otherwise; neighbour-dependent states and metadata orientation resolved
/// from the world) and a b1.8.1 full cube's face (<see cref="CubeFace"/>, the texture the raycaster samples at a hit).
/// </summary>
public static class BlockMesh
{
    /// <summary>The block's treatment: full cube (opaque, cut-out, blended) or quads.</summary>
    public static BlockShape Shape(IBlockModels? models, int id, int meta) => models != null ? models.Classify(id, meta) : BlockGeometry.Classify(id);

    public static bool IsCube(BlockShape s) => s is BlockShape.Cube or BlockShape.CutoutCube or BlockShape.TranslucentCube;

    /// <summary>The block's quads (block-local 0..1); a shape that can't be built here (a malformed model) adds none.</summary>
    public static void Quads(IBlockModels? models, IVoxelWorld w, int x, int y, int z, List<GeoQuad> dst)
    {
        int n = dst.Count;
        try
        {
            if (models != null) models.Build(w, x, y, z, dst);
            else BlockGeometry.Build(w, x, y, z, dst);
        }
        catch (Exception) { dst.RemoveRange(n, dst.Count - n); }
    }

    /// <summary>One face of a b1.8.1 full cube as a quad: the tile <see cref="BlockGeometry.CubeTile"/> gives the raycaster at a hit
    /// (log tops, furnace fronts, chest halves, the grass side fringe), vanilla UVs, face shade, light from the cell it faces.</summary>
    public static GeoQuad CubeFace(IVoxelWorld w, int x, int y, int z, int id, int meta, Face f)
    {
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
        return q;
    }
}
