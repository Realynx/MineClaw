using System.Numerics;

namespace MineClaw.Render;

/// <summary>
/// The render-assets seam (docs/design/multi-version.md §2.9) for block geometry: a version whose blocks come from data-driven
/// models (1.12.2 blockstates → models → textures) hangs one of these on its <see cref="TextureAtlas.Models"/>. Null = b1.8.1:
/// <see cref="BlockGeometry"/> + <see cref="BlockTextures"/> over <c>terrain.png</c>, exactly as before. The renderers
/// (<see cref="EyeRenderer"/>, the Explorer's GPU mesher) branch once on that null, never on a version id.
/// </summary>
public interface IBlockModels
{
    /// <summary>The game version the models describe ("1.12.2").</summary>
    string Version { get; }

    /// <summary>The raycaster's treatment of a block state.</summary>
    BlockShape Classify(int id, int meta);

    /// <summary>The texel (0xAARRGGBB, tinted) of a full-cube state's face at the block-local hit point (0..1).</summary>
    uint CubeTexel(IVoxelWorld w, int x, int y, int z, int id, int meta, Face face, Vector3 local);

    /// <summary>A full-cube state's face as a quad (block-local, <see cref="QuadTex.Block"/>), for a GPU mesher's cube fast path.</summary>
    GeoQuad CubeFace(IVoxelWorld w, int x, int y, int z, int id, int meta, Face face);

    /// <summary>The block's quads (block-local 0..1), neighbour-dependent states resolved from the world.</summary>
    void Build(IVoxelWorld w, int x, int y, int z, List<GeoQuad> dst);

    /// <summary>A texel of a <see cref="QuadTex.Block"/> quad at its uv: the tile, its tint and the overlay (grass sides).</summary>
    uint SampleQuad(in GeoQuad q, Vector2 uv);

    /// <summary>Drawn at full brightness (torches, glowstone, sea lanterns, lava…).</summary>
    bool Emissive(int id);

    /// <summary>The block textures (index = <see cref="GeoQuad.Tile"/> of <see cref="QuadTex.Block"/> quads), for a GPU upload.</summary>
    Models.ModelTextures Textures { get; }
}
