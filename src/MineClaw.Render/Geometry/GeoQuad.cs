using System.Numerics;

namespace MineClaw.Render;

/// <summary>Which pass a quad draws in (vanilla b1.8.1: pass 0 = opaque + alpha-tested cutouts, pass 1 = blended).</summary>
public enum RenderPass : byte
{
    /// <summary>Fully opaque texels.</summary>
    Opaque,
    /// <summary>Alpha-tested at 0.5 (leaves, glass, plants, torches, ladders, crops, rails, doors' windows).</summary>
    Cutout,
    /// <summary>Alpha-blended (water, ice, portal, slime): composite front to back.</summary>
    Translucent,
}

/// <summary>Which texture a quad samples. UVs are normalised to the texture (terrain/items: to one 16×16 tile).</summary>
public enum QuadTex : byte
{
    /// <summary>terrain.png tile <see cref="GeoQuad.Tile"/> (u, v in 0..1 of that tile).</summary>
    Terrain,
    /// <summary>Procedural b1.8.1 still/flowing water and lava (TextureWaterFX/LavaFX and the flow variants); UVs wrap.</summary>
    WaterStill, WaterFlow, LavaStill, LavaFlow,
    /// <summary>An entity texture (<see cref="EntityTextures"/> id in <see cref="GeoQuad.Tile"/>; u, v in 0..1 of the whole image).</summary>
    Entity,
    /// <summary>gui/items.png tile <see cref="GeoQuad.Tile"/>.</summary>
    Item,
    /// <summary>No texture: <see cref="GeoQuad.Flat"/> only.</summary>
    Flat,
    /// <summary>Procedural b1.8.1 fire (TextureFlamesFX; terrain tiles 31/47 are placeholders in the file); Tile 0 or 1.</summary>
    Fire,
}

/// <summary>
/// One textured quad of renderer-agnostic geometry: block shapes (<see cref="BlockGeometry"/>, block-local 0..1 coordinates)
/// and entity models (<see cref="EntityGeometry"/>, coordinates relative to the entity's feet). Corners go around the quad
/// (P0→P1→P2→P3); a GPU mesher emits the triangles (P0,P1,P2) + (P0,P2,P3), the CPU raycaster intersects the same two.
/// </summary>
public struct GeoQuad
{
    public Vector3 P0, P1, P2, P3;
    public Vector2 T0, T1, T2, T3;
    /// <summary>terrain/items tile, or the entity texture id; −1 = flat colour.</summary>
    public short Tile;
    public QuadTex Tex;
    public RenderPass Pass;
    /// <summary>Multiply tint for grey tiles (grass, leaves, redstone wire); 0 = none.</summary>
    public uint Tint;
    /// <summary>Second tile drawn over the first where opaque (the grass side fringe); −1 = none.</summary>
    public short Overlay;
    public uint OverlayTint;
    /// <summary>Directional shade (RenderBlocks: top 1.0, N/S 0.8, E/W 0.6, bottom 0.5; plants, torches and entities 1).</summary>
    public float Shade;
    /// <summary>Light from the neighbouring cell in this direction (a face on the block boundary), or −1 = the block's own cell.</summary>
    public sbyte LightFace;
    /// <summary>Full brightness (torches, lava, glowstone, fire).</summary>
    public bool Emissive;
    /// <summary>Visible from both sides (plants, ladders, rails, torches, fluid tops); a GPU mesher disables culling for it.</summary>
    public bool TwoSided;
    /// <summary>Opaque fallback colour when the texture is missing (no atlas) and the base colour for flat entity parts.</summary>
    public uint Flat;
    /// <summary>Constant alpha for translucent quads without a texture alpha (portal, ice), 0 = from the texel.</summary>
    public float Alpha;

    public static GeoQuad Make(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3) => new()
    {
        P0 = p0, P1 = p1, P2 = p2, P3 = p3, T0 = t0, T1 = t1, T2 = t2, T3 = t3, Tile = -1, Overlay = -1, Shade = 1, LightFace = -1,
    };

    /// <summary>The quad's normal (P1−P0 × P3−P0), normalised; for a GPU's lighting and back-face decisions.</summary>
    public readonly Vector3 Normal => Vector3.Normalize(Vector3.Cross(P1 - P0, P3 - P0));

    /// <summary>Moves every corner by <paramref name="d"/>.</summary>
    public void Offset(Vector3 d) { P0 += d; P1 += d; P2 += d; P3 += d; }
}

/// <summary>Ray/triangle maths shared by the CPU raycaster (Möller–Trumbore, both sides).</summary>
public static class QuadHit
{
    /// <summary>Nearest intersection of the ray with the quad (two triangles), t ≥ <paramref name="tMin"/>; the UV at the hit.</summary>
    public static bool Intersect(in GeoQuad q, Vector3 o, Vector3 d, float tMin, out float t, out Vector2 uv)
    {
        if (Tri(q.P0, q.P1, q.P2, o, d, out t, out float a, out float b) && t >= tMin)
        {
            uv = q.T0 * (1 - a - b) + q.T1 * a + q.T2 * b;
            return true;
        }
        if (Tri(q.P0, q.P2, q.P3, o, d, out t, out a, out b) && t >= tMin)
        {
            uv = q.T0 * (1 - a - b) + q.T2 * a + q.T3 * b;
            return true;
        }
        uv = default;
        t = 0;
        return false;
    }

    private static bool Tri(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 o, Vector3 d, out float t, out float u, out float v)
    {
        t = u = v = 0;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (det > -1e-9f && det < 1e-9f) return false;
        float inv = 1f / det;
        var s = o - v0;
        u = Vector3.Dot(s, p) * inv;
        if (u < -1e-5f || u > 1 + 1e-5f) return false;
        var qv = Vector3.Cross(s, e1);
        v = Vector3.Dot(d, qv) * inv;
        if (v < -1e-5f || u + v > 1 + 1e-5f) return false;
        t = Vector3.Dot(e2, qv) * inv;
        return t > 0;
    }
}
