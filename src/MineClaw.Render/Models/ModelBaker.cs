using System.Numerics;
using System.Text.Json;

namespace MineClaw.Render.Models;

/// <summary>One baked face of a block model: the shared <see cref="GeoQuad"/> (block-local 0..1, <see cref="QuadTex.Block"/>)
/// plus the model facts the block layer resolves (tint index, cull face, the final facing).</summary>
public struct ModelQuad
{
    public GeoQuad Q;
    /// <summary>The face's <c>tintindex</c> (−1 = untinted).</summary>
    public sbyte TintIndex;
    /// <summary>The (rotated) <c>cullface</c> as a <see cref="Face"/>, −1 = none.</summary>
    public sbyte Cull;
    /// <summary>The quad's final facing when axis-aligned (a <see cref="Face"/>), −1 otherwise (a rotated element).</summary>
    public sbyte Dir;
}

/// <summary>
/// The 1.12.2 block model format (<c>assets/minecraft/models/block/*.json</c>), ported from the client's <c>ModelBlock</c>,
/// <c>BlockPart</c>, <c>BlockPartFace</c>, <c>BlockFaceUV</c>, <c>FaceBakery</c> and <c>ModelRotation</c>: parent inheritance
/// (textures merge child-first, the nearest model with elements supplies them), <c>#variable</c> texture references, elements
/// (from/to in 0..16, an optional rotation about an origin on one axis by ±22.5/45° with <c>rescale</c>), faces with a uv
/// rectangle (default: the element's projection, <c>BlockPart.getFaceUvs</c>), a uv rotation (0/90/180/270), <c>cullface</c>,
/// <c>tintindex</c> and <c>shade</c>; then the blockstate variant's x/y rotation about the block centre (x first, then y,
/// as <c>ModelRotation</c>) and <c>uvlock</c> (approximated: the texture is re-projected from the rotated face, which is what
/// uvlock achieves for the default uvs every vanilla uvlock model uses). <c>ambientocclusion</c> and display transforms are ignored.
/// </summary>
public sealed class ModelBaker
{
    private sealed record FaceDef(float[]? Uv, string Texture, int Cull, int Rotation, int TintIndex);
    private sealed record ElementDef(Vector3 From, Vector3 To, Vector3 Origin, char Axis, float Angle, bool Rescale, bool Shade, FaceDef?[] Faces);
    private sealed record ModelDef(string? Parent, Dictionary<string, string> Textures, List<ElementDef>? Elements);

    private readonly Dictionary<string, ModelDef> _defs = new(StringComparer.Ordinal);
    private readonly ModelTextures _tex;

    public ModelBaker(ModelTextures textures) => _tex = textures;

    /// <summary>Number of model files known.</summary>
    public int Count => _defs.Count;

    /// <summary>Adds a model file ("block/stairs" → its JSON).</summary>
    public void Add(string name, string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var r = doc.RootElement;
        string? parent = r.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r.TryGetProperty("textures", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var kv in t.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String) textures[kv.Name] = kv.Value.GetString()!;
        List<ElementDef>? elements = null;
        if (r.TryGetProperty("elements", out var els) && els.ValueKind == JsonValueKind.Array)
        {
            elements = [];
            foreach (var e in els.EnumerateArray()) elements.Add(Element(e));
        }
        _defs[Normalize(name)] = new ModelDef(parent is null ? null : Normalize(parent), textures, elements);
    }

    public bool Has(string name) => _defs.ContainsKey(Normalize(name));

    /// <summary>"oak_stairs" / "block/oak_stairs" / "minecraft:block/oak_stairs" → "block/oak_stairs".</summary>
    public static string Normalize(string name)
    {
        if (name.StartsWith("minecraft:", StringComparison.Ordinal)) name = name[10..];
        return name.Contains('/') ? name : "block/" + name;
    }

    private static ElementDef Element(JsonElement e)
    {
        var from = Vec(e.GetProperty("from"));
        var to = Vec(e.GetProperty("to"));
        Vector3 origin = new(8, 8, 8);
        char axis = 'y';
        float angle = 0;
        bool rescale = false;
        if (e.TryGetProperty("rotation", out var rot) && rot.ValueKind == JsonValueKind.Object)
        {
            if (rot.TryGetProperty("origin", out var o)) origin = Vec(o);
            if (rot.TryGetProperty("axis", out var a)) axis = a.GetString() is { Length: > 0 } s ? s[0] : 'y';
            if (rot.TryGetProperty("angle", out var an)) angle = an.GetSingle();
            rescale = rot.TryGetProperty("rescale", out var rs) && rs.ValueKind == JsonValueKind.True;
        }
        bool shade = !(e.TryGetProperty("shade", out var sh) && sh.ValueKind == JsonValueKind.False);
        var faces = new FaceDef?[6];
        if (e.TryGetProperty("faces", out var fs))
            foreach (var f in fs.EnumerateObject())
            {
                int dir = FaceOf(f.Name);
                if (dir < 0) continue;
                var v = f.Value;
                float[]? uv = v.TryGetProperty("uv", out var u) ? u.EnumerateArray().Select(x => x.GetSingle()).ToArray() : null;
                int cull = v.TryGetProperty("cullface", out var c) && c.GetString() is { } cs ? FaceOf(cs) : -1;
                int rotation = v.TryGetProperty("rotation", out var r) ? r.GetInt32() : 0;
                int tint = v.TryGetProperty("tintindex", out var ti) ? ti.GetInt32() : -1;
                faces[dir] = new FaceDef(uv is { Length: 4 } ? uv : null, v.TryGetProperty("texture", out var tx) ? tx.GetString() ?? "" : "", cull, rotation, tint);
            }
        return new ElementDef(from, to, origin, axis, angle, rescale, shade, faces);
    }

    private static Vector3 Vec(JsonElement a)
    {
        var v = a.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        return new Vector3(v[0], v[1], v[2]);
    }

    /// <summary>Face name → <see cref="Face"/> index (down, up, north, south, west, east; "bottom" as down).</summary>
    public static int FaceOf(string n) => n switch
    {
        "down" or "bottom" => 0, "up" => 1, "north" => 2, "south" => 3, "west" => 4, "east" => 5, _ => -1,
    };

    /// <summary>The model's merged texture map and its elements, following the parent chain (cycles stop).</summary>
    private (Dictionary<string, string> Textures, List<ElementDef> Elements) Resolve(string name)
    {
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        List<ElementDef>? elements = null;
        var seen = new HashSet<string>();
        string? cur = Normalize(name);
        while (cur != null && seen.Add(cur) && _defs.TryGetValue(cur, out var d))
        {
            foreach (var (k, v) in d.Textures) textures.TryAdd(k, v);
            elements ??= d.Elements;
            cur = d.Parent;
        }
        return (textures, elements ?? []);
    }

    private static string ResolveTexture(Dictionary<string, string> textures, string reference)
    {
        string r = reference;
        for (int i = 0; i < 8 && r.StartsWith('#'); i++)
            r = textures.TryGetValue(r[1..], out var next) ? next : "missingno";
        return r.StartsWith('#') ? "missingno" : r;
    }

    // ------------------------------------------------------------------ baking

    // EnumFaceDirection vertex order per face (x: W=0/E=1, y: D=0/U=1, z: N=0/S=1).
    private static readonly (int X, int Y, int Z)[][] Corners =
    [
        [(0, 0, 1), (0, 0, 0), (1, 0, 0), (1, 0, 1)], // down
        [(0, 1, 0), (0, 1, 1), (1, 1, 1), (1, 1, 0)], // up
        [(1, 1, 0), (1, 0, 0), (0, 0, 0), (0, 1, 0)], // north
        [(0, 1, 1), (0, 0, 1), (1, 0, 1), (1, 1, 1)], // south
        [(0, 1, 0), (0, 0, 0), (0, 0, 1), (0, 1, 1)], // west
        [(1, 1, 1), (1, 0, 1), (1, 0, 0), (1, 1, 0)], // east
    ];

    private static readonly Vector3[] Normals = [new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1), new(-1, 0, 0), new(1, 0, 0)];

    /// <summary>BlockPart.getFaceUvs: the default uv rectangle of a face, from the element's bounds.</summary>
    private static float[] DefaultUv(int face, Vector3 f, Vector3 t) => face switch
    {
        0 => [f.X, 16 - t.Z, t.X, 16 - f.Z],
        1 => [f.X, f.Z, t.X, t.Z],
        2 => [16 - t.X, 16 - t.Y, 16 - f.X, 16 - f.Y],
        3 => [f.X, 16 - t.Y, t.X, 16 - f.Y],
        4 => [f.Z, 16 - t.Y, t.Z, 16 - f.Y],
        _ => [16 - t.Z, 16 - t.Y, 16 - f.Z, 16 - f.Y],
    };

    /// <summary>The uv a point gets projected onto a face (pixels), the same mapping as <see cref="DefaultUv"/>.</summary>
    private static Vector2 Project(int face, Vector3 p) => face switch
    {
        0 => new(p.X, 16 - p.Z),
        1 => new(p.X, p.Z),
        2 => new(16 - p.X, 16 - p.Y),
        3 => new(p.X, 16 - p.Y),
        4 => new(p.Z, 16 - p.Y),
        _ => new(16 - p.Z, 16 - p.Y),
    };

    /// <summary>The variant rotation as a matrix on 0..16 positions about the centre (ModelRotation: x first, then y).</summary>
    private static Matrix4x4 VariantRotation(int x, int y) =>
        Matrix4x4.CreateTranslation(-8, -8, -8)
        * Matrix4x4.CreateRotationX(-x * MathF.PI / 180f)
        * Matrix4x4.CreateRotationY(-y * MathF.PI / 180f)
        * Matrix4x4.CreateTranslation(8, 8, 8);

    private static int DirOf(Vector3 n)
    {
        for (int i = 0; i < 6; i++)
            if (Vector3.Dot(n, Normals[i]) > 0.999f) return i;
        return -1;
    }

    private static int NearestDir(Vector3 n)
    {
        int best = 1;
        float bd = float.MinValue;
        for (int i = 0; i < 6; i++)
        {
            float d = Vector3.Dot(n, Normals[i]);
            if (d > bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>FaceBakery.getFaceShadeColor: up 1.0, down 0.5, north/south 0.8, west/east 0.6.</summary>
    public static float FaceShade(int dir) => dir switch { 0 => 0.5f, 1 => 1f, 2 or 3 => 0.8f, _ => 0.6f };

    /// <summary>Bakes one model under a variant rotation into <paramref name="dst"/>.</summary>
    public void Bake(string model, int xRot, int yRot, bool uvlock, List<ModelQuad> dst)
    {
        var (textures, elements) = Resolve(model);
        var vr = VariantRotation(xRot, yRot);
        bool rotated = xRot % 360 != 0 || yRot % 360 != 0;
        Span<Vector3> pos = stackalloc Vector3[4];
        Span<Vector2> uv = stackalloc Vector2[4];
        foreach (var el in elements)
        {
            var erot = ElementRotation(el, out var scale);
            for (int face = 0; face < 6; face++)
            {
                if (el.Faces[face] is not { } fd) continue;
                var r = fd.Uv ?? DefaultUv(face, el.From, el.To);
                for (int i = 0; i < 4; i++)
                {
                    var c = Corners[face][i];
                    var p = new Vector3(c.X == 0 ? el.From.X : el.To.X, c.Y == 0 ? el.From.Y : el.To.Y, c.Z == 0 ? el.From.Z : el.To.Z);
                    if (el.Angle != 0) p = Vector3.Transform(p - el.Origin, erot) * scale + el.Origin;
                    pos[i] = rotated ? Vector3.Transform(p, vr) : p;
                    int k = (i + fd.Rotation / 90) % 4;
                    uv[i] = new Vector2(k is 0 or 1 ? r[0] : r[2], k is 0 or 3 ? r[1] : r[3]);
                }
                // EnumFaceDirection's vertex order: (P1 − P0) × (P3 − P0) points out of the face (as GeoQuad.Normal).
                var n = Vector3.Normalize(Vector3.Cross(pos[1] - pos[0], pos[3] - pos[0]));
                if (float.IsNaN(n.X)) n = rotated ? Vector3.TransformNormal(Normals[face], vr) : Normals[face];
                int dir = DirOf(n);
                if (uvlock && rotated && dir >= 0 && el.Angle == 0)
                    for (int i = 0; i < 4; i++) uv[i] = Project(dir, pos[i]);
                int cull = fd.Cull;
                if (cull >= 0 && rotated) cull = DirOf(Vector3.TransformNormal(Normals[cull], vr));
                var q = GeoQuad.Make(pos[0] / 16f, pos[1] / 16f, pos[2] / 16f, pos[3] / 16f, uv[0] / 16f, uv[1] / 16f, uv[2] / 16f, uv[3] / 16f);
                q.Tex = QuadTex.Block;
                q.Tile = (short)_tex.IndexOf(ResolveTexture(textures, fd.Texture));
                q.Shade = el.Shade ? FaceShade(dir >= 0 ? dir : NearestDir(n)) : 1f;
                q.LightFace = (sbyte)(dir >= 0 && OnBoundary(dir, pos) ? dir : -1);
                dst.Add(new ModelQuad { Q = q, TintIndex = (sbyte)fd.TintIndex, Cull = (sbyte)cull, Dir = (sbyte)dir });
            }
        }
    }

    private static bool OnBoundary(int dir, ReadOnlySpan<Vector3> p)
    {
        const float e = 1e-3f;
        for (int i = 0; i < 4; i++)
        {
            float c = dir switch { 0 => p[i].Y, 1 => 16 - p[i].Y, 2 => p[i].Z, 3 => 16 - p[i].Z, 4 => p[i].X, _ => 16 - p[i].X };
            if (Math.Abs(c) > e) return false;
        }
        return true;
    }

    /// <summary>FaceBakery.rotatePart: the element rotation and the rescale vector (1/cos on the two other axes).</summary>
    private static Matrix4x4 ElementRotation(ElementDef el, out Vector3 scale)
    {
        scale = Vector3.One;
        if (el.Angle == 0) return Matrix4x4.Identity;
        float rad = el.Angle * MathF.PI / 180f;
        Matrix4x4 m;
        Vector3 s;
        switch (el.Axis)
        {
            case 'x': m = Matrix4x4.CreateRotationX(rad); s = new(0, 1, 1); break;
            case 'z': m = Matrix4x4.CreateRotationZ(rad); s = new(1, 1, 0); break;
            default: m = Matrix4x4.CreateRotationY(rad); s = new(1, 0, 1); break;
        }
        if (el.Rescale)
        {
            float k = Math.Abs(el.Angle) == 22.5f ? 1f / MathF.Cos(MathF.PI / 8f) - 1f : 1f / MathF.Cos(MathF.PI / 4f) - 1f;
            scale = s * k + Vector3.One;
        }
        return m;
    }
}
