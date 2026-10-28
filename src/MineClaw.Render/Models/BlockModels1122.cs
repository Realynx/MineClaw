using System.Collections.Concurrent;
using System.Numerics;

namespace MineClaw.Render.Models;

/// <summary>
/// 1.12.2 block geometry for the shared renderers (<see cref="IBlockModels"/>): (id, meta) → <see cref="BlockStates1122"/>
/// state (neighbours resolved from the world) → blockstate file → baked <see cref="ModelBaker"/> quads, cached per state.
/// Tints follow <c>BlockColors</c> (grass/tall grass/double grass/reeds: biome grass; leaves: spruce and birch fixed, others
/// biome foliage; vines: foliage; lily pads; stems by age; redstone wire by power) with the plains biome (the world model has
/// no chunk biomes yet). Full cubes take the raycaster's DDA fast path; everything else is intersected quad by quad. Fluids,
/// fire, signs and beds keep the procedural b1.8.1 geometry (its terrain tiles come from the 1.12.2 textures, see
/// <see cref="Release1122Assets"/>); chests and shulker boxes are small synthetic models over textures cut from their entity
/// textures. Thread-safe (the renderer's bands share one instance).
/// </summary>
public sealed class BlockModels1122 : IBlockModels
{
    private readonly ModelBaker _baker;
    private readonly Dictionary<string, BlockStateFile> _states;
    private readonly ConcurrentDictionary<string, GeoQuad[]> _baked = new(StringComparer.Ordinal);
    private readonly byte[] _class = new byte[256 * 16]; // BlockShape + 1; 0 = not yet classified
    private readonly GeoQuad[]?[] _cubes = new GeoQuad[]?[256 * 16 * 2];

    public ModelTextures Textures { get; }
    public string Version => "1.12.2";
    /// <summary>Blockstate files known.</summary>
    public int StateFiles => _states.Count;

    public BlockModels1122(ModelTextures textures, ModelBaker baker, Dictionary<string, BlockStateFile> states)
    {
        Textures = textures;
        _baker = baker;
        _states = states;
    }

    /// <summary>Blocks whose state needs the neighbours (never on the cube fast path).</summary>
    private static bool WorldDependent(int id) => BlockStates1122.IsStairs(id) || BlockStates1122.IsFence(id) || BlockStates1122.IsFenceGate(id)
        || BlockStates1122.IsPane(id) || BlockStates1122.IsDoor(id) || id is 139 or 106 or 175 or 104 or 105 or 55;

    /// <summary>Block.getBlockLayer() == TRANSLUCENT: ice, stained glass (+ panes), slime, frosted ice, portals.</summary>
    private static bool TranslucentLayer(int id) => id is 79 or 95 or 160 or 165 or 212 or 90;

    public bool Emissive(int id) => id is 50 or 76 or 89 or 91 or 124 or 138 or 169 or 198 or 213 or 10 or 11 or 51 or 62 or 74 or 90 or 119 or 209;

    private bool SolidFull(int id, int meta) => (uint)id < 256 && Classify(id, meta & 15) == BlockShape.Cube;

    public StateRef StateOf(int id, int meta, IVoxelWorld? w = null, int x = 0, int y = 0, int z = 0) =>
        BlockStates1122.Resolve(id, meta & 15, w, x, y, z, SolidFull);

    public BlockShape Classify(int id, int meta)
    {
        if (id <= 0) return BlockShape.Air;
        if ((uint)id >= 256) return BlockShape.Custom;
        int i = id << 4 | (meta & 15);
        int c = _class[i];
        if (c != 0) return (BlockShape)(c - 1);
        var shape = ClassifyNow(id, meta & 15);
        _class[i] = (byte)(shape + 1);
        return shape;
    }

    private BlockShape ClassifyNow(int id, int meta)
    {
        var s = StateOf(id, meta);
        if (s.Builtin || WorldDependent(id)) return BlockShape.Custom;
        var faces = CubeFaces(Bake(id, s));
        if (faces == null) return BlockShape.Custom;
        if (TranslucentLayer(id)) return BlockShape.TranslucentCube;
        foreach (var f in faces)
            if (Textures.Coverage(f.Tile) < 0.999f) return BlockShape.CutoutCube;
        return BlockShape.Cube;
    }

    /// <summary>The six faces of a full-cube model by <see cref="Face"/>, or null when it isn't one (any partial or extra quad).</summary>
    private static GeoQuad[]? CubeFaces(GeoQuad[] quads)
    {
        if (quads.Length != 6) return null;
        var faces = new GeoQuad[6];
        var seen = new bool[6];
        foreach (var q in quads)
        {
            int f = q.LightFace;
            if (f < 0 || seen[f] || Math.Abs(Area(q) - 1f) > 1e-3f) return null;
            seen[f] = true;
            faces[f] = q;
        }
        return faces;
    }

    private static float Area(in GeoQuad q) => Vector3.Cross(q.P1 - q.P0, q.P3 - q.P0).Length();

    private GeoQuad[] Cube(IVoxelWorld w, int x, int y, int z, int id, int meta)
    {
        bool snowy = (id is 2 or 110 || id == 3 && meta % 3 == 2) && w.Block(x, y + 1, z) is 78 or 80;
        int i = (id << 4 | (meta & 15)) * 2 + (snowy ? 1 : 0);
        if (_cubes[i] is { } hit) return hit;
        var s = snowy ? StateOf(id, meta, w, x, y, z) : StateOf(id, meta);
        var faces = CubeFaces(Bake(id, s)) ?? CubeFaces(Bake(id, StateOf(id, meta))) ?? new GeoQuad[6];
        _cubes[i] = faces;
        return faces;
    }

    public GeoQuad CubeFace(IVoxelWorld w, int x, int y, int z, int id, int meta, Face face) => Cube(w, x, y, z, id, meta)[(int)face];

    public uint CubeTexel(IVoxelWorld w, int x, int y, int z, int id, int meta, Face face, Vector3 local)
    {
        ref readonly var q = ref Cube(w, x, y, z, id, meta)[(int)face];
        if (q.Tex != QuadTex.Block) return 0xffff00ff;
        // An axis-aligned rectangle: the uv is affine in the two edge parameters.
        var e1 = q.P1 - q.P0;
        var e3 = q.P3 - q.P0;
        var d = local - q.P0;
        float s = Vector3.Dot(d, e1) / e1.LengthSquared(), t = Vector3.Dot(d, e3) / e3.LengthSquared();
        return SampleQuad(q, q.T0 + (q.T1 - q.T0) * s + (q.T3 - q.T0) * t);
    }

    public uint SampleQuad(in GeoQuad q, Vector2 uv)
    {
        float u = Math.Clamp(uv.X, 0, 0.999f);
        float v = Math.Clamp(uv.Y, 0, 0.999f);
        uint p = BlockTextures.Tinted(Textures.Sample(q.Tile, u, v), q.Tint);
        if (q.Overlay >= 0)
        {
            uint ov = Textures.Sample(q.Overlay, u, v);
            if (ov >> 24 >= 128) p = BlockTextures.Tinted(ov, q.OverlayTint) | 0xff000000;
        }
        return p;
    }

    public void Build(IVoxelWorld w, int x, int y, int z, List<GeoQuad> dst)
    {
        int id = w.Block(x, y, z);
        if (id <= 0) return;
        int meta = w.Meta(x, y, z);
        var s = StateOf(id, meta, w, x, y, z);
        if (s.Builtin)
        {
            // Fluids, fire, signs and beds: the b1.8.1 shapes (same ids and metas), textured from the synthesized terrain atlas.
            if (id is 8 or 9 or 10 or 11 or 51 or 63 or 68 or 26) BlockGeometry.Build(w, x, y, z, dst);
            return;
        }
        dst.AddRange(Bake(id, s));
    }

    /// <summary>The baked quads of a resolved state (cached).</summary>
    public GeoQuad[] Bake(int id, StateRef s) =>
        _baked.GetOrAdd(id + "|" + s.CacheKey, _ => BakeNow(id, s));

    /// <summary>The model references a state draws with (for tests and diagnostics).</summary>
    public IReadOnlyList<VariantRef> Models(StateRef s) => _states.TryGetValue(s.File, out var f) ? f.Select(s.Props, s.Key) : [];

    private GeoQuad[] BakeNow(int id, StateRef s)
    {
        if (s.Builtin || !_states.TryGetValue(s.File, out var file)) return [];
        var mq = new List<ModelQuad>();
        foreach (var r in file.Select(s.Props, s.Key)) _baker.Bake(r.Model, r.X, r.Y, r.UvLock, mq);
        uint tint = TintOf(id, s);
        bool translucent = TranslucentLayer(id), emissive = Emissive(id);
        var list = new List<GeoQuad>(mq.Count);
        foreach (var m in mq)
        {
            var q = m.Q;
            q.Tint = m.TintIndex >= 0 ? tint : 0;
            bool cut = Textures.Coverage(q.Tile) < 0.999f;
            q.Pass = translucent ? RenderPass.Translucent : cut ? RenderPass.Cutout : RenderPass.Opaque;
            q.TwoSided = cut || m.Dir < 0;
            q.Emissive = emissive;
            q.Flat = Textures.Average(q.Tile);
            // A second element over the same face (the grass side fringe) becomes the first one's overlay.
            int host = list.FindIndex(o => o.Overlay < 0 && Same(o, q));
            if (host >= 0)
            {
                var h = list[host];
                h.Overlay = q.Tile;
                h.OverlayTint = q.Tint;
                list[host] = h;
                continue;
            }
            list.Add(q);
        }
        return list.ToArray();
    }

    private static bool Same(in GeoQuad a, in GeoQuad b)
    {
        const float e = 1e-4f;
        return Vector3.DistanceSquared(a.P0, b.P0) < e && Vector3.DistanceSquared(a.P1, b.P1) < e
            && Vector3.DistanceSquared(a.P2, b.P2) < e && Vector3.DistanceSquared(a.P3, b.P3) < e;
    }

    /// <summary>BlockColors.colorMultiplier for the tinted faces (0 = none), plains biome.</summary>
    private static uint TintOf(int id, StateRef s)
    {
        switch (id)
        {
            case 2 or 31 or 83: return BiomeTints.Grass();
            case 175: return s.File is "double_grass" or "double_fern" ? BiomeTints.Grass() : 0;
            case 18: return s.File switch { "spruce_leaves" => BiomeTints.Spruce, "birch_leaves" => BiomeTints.Birch, _ => BiomeTints.Foliage() };
            case 161 or 106: return BiomeTints.Foliage();
            case 111: return BiomeTints.LilyPad;
            case 104 or 105:
            {
                int age = s.Props.TryGetValue("age", out var a) ? int.Parse(a) : 7;
                return (uint)(age * 32) << 16 | (uint)(255 - age * 8) << 8 | (uint)(age * 4);
            }
            case 55: return WireColour(s.Props.TryGetValue("power", out var pw) ? int.Parse(pw) : 0);
            default: return 0;
        }
    }

    /// <summary>BlockRedstoneWire.colorMultiplier(power).</summary>
    public static uint WireColour(int power)
    {
        float f = power / 15f;
        float r = f * 0.6f + (power == 0 ? 0.3f : 0.4f);
        float g = Math.Clamp(f * f * 0.7f - 0.5f, 0, 1), b = Math.Clamp(f * f * 0.6f - 0.7f, 0, 1);
        return (uint)(Math.Clamp(r, 0, 1) * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
    }
}
