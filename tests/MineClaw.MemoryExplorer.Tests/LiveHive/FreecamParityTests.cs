using System.Numerics;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.MemoryExplorer.Services;
using MineClaw.Render;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>
/// One of each special block (doors, trapdoors, fences/gates/walls, panes, stairs, slabs, torches, ladders, buttons, levers,
/// beds, chests, signs, crops, plants, fluids, cut-out and blended cubes, the 1.12.2-only models) on a lit fixture, built as a
/// server <see cref="WorldState"/> (what the AI's sheets render through <see cref="WorldVoxels"/>) and synced to the Explorer the
/// real way (<see cref="ColumnPayload"/> v2 with <see cref="LightSnapshot"/>s into a <see cref="HiveWorldCache"/>).
/// </summary>
internal static class ParityFixture
{
    public sealed record Spec(string Name, int Id, int Meta, params (int Dx, int Dy, int Dz, int Id, int Meta)[] Extra);

    public static readonly Spec[] Beta =
    [
        new("oak door", 64, 1, (0, 1, 0, 64, 9)), new("iron door", 71, 3, (0, 1, 0, 71, 11)),
        new("trapdoor", 96, 0), new("trapdoor open", 96, 5),
        new("fence", 85, 0, (1, 0, 0, 85, 0)), new("fence gate", 107, 1),
        new("glass pane", 102, 0, (1, 0, 0, 102, 0)), new("iron bars", 101, 0, (0, 0, 1, 101, 0)),
        new("wood stairs", 53, 2), new("cobble stairs", 67, 1), new("slab", 44, 0), new("double slab", 43, 0),
        new("torch", 50, 5), new("wall torch", 50, 1, (-1, 0, 0, 4, 0)), new("ladder", 65, 3, (0, 0, -1, 5, 0)),
        new("button", 77, 1, (-1, 0, 0, 1, 0)), new("lever", 69, 5),
        new("bed", 26, 0, (0, 0, 1, 26, 8)), new("chest", 54, 2), new("sign", 63, 4), new("wall sign", 68, 3, (0, 0, -1, 5, 0)),
        new("crops", 59, 7, (0, -1, 0, 60, 0)), new("dandelion", 37, 0), new("rose", 38, 0), new("tall grass", 31, 1),
        new("sapling", 6, 1), new("dead bush", 32, 0), new("sugar cane", 83, 0), new("cactus", 81, 0),
        new("water", 9, 0), new("flowing water", 8, 3), new("lava", 11, 0), new("flowing lava", 10, 2),
        new("glass", 20, 0, (1, 0, 0, 20, 0)), new("leaves", 18, 0, (0, 1, 0, 18, 0)), new("ice", 79, 0), new("spawner", 52, 0),
        new("rail", 66, 0), new("powered rail", 27, 0), new("snow layer", 78, 2), new("farmland", 60, 0), new("cake", 92, 0),
        new("pressure plate", 70, 0), new("redstone wire", 55, 0, (1, 0, 0, 55, 0)), new("repeater", 93, 0), new("web", 30, 0),
        new("fire", 51, 0, (0, -1, 0, 87, 0)), new("vine", 106, 1), new("furnace", 61, 2), new("lit furnace", 62, 3),
        new("workbench", 58, 0), new("log", 17, 0), new("grass", 2, 0), new("pumpkin", 86, 1), new("glowstone", 89, 0),
        new("bookshelf", 47, 0), new("tnt", 46, 0), new("piston", 33, 1),
    ];

    public static readonly Spec[] Modern =
    [
        new("oak door", 64, 1, (0, 1, 0, 64, 8)), new("spruce door", 193, 2, (0, 1, 0, 193, 9)), new("iron door", 71, 3, (0, 1, 0, 71, 8)),
        new("trapdoor bottom", 96, 0), new("trapdoor top", 96, 8), new("trapdoor open", 96, 5), new("iron trapdoor", 167, 8),
        new("fence", 85, 0, (1, 0, 0, 85, 0)), new("spruce fence", 188, 0, (0, 0, 1, 188, 0)), new("nether brick fence", 113, 0),
        new("fence gate", 107, 1), new("spruce gate", 183, 0), new("cobblestone wall", 139, 0, (1, 0, 0, 139, 0)),
        new("glass pane", 102, 0, (1, 0, 0, 102, 0)), new("stained pane", 160, 14, (0, 0, 1, 160, 14)), new("iron bars", 101, 0),
        new("oak stairs", 53, 2), new("spruce stairs upside-down", 134, 4), new("stone slab", 44, 0), new("stone slab top", 44, 8),
        new("spruce slab", 126, 1), new("double slab", 43, 0),
        new("torch", 50, 5), new("wall torch", 50, 1, (-1, 0, 0, 4, 0)), new("redstone torch", 76, 5), new("ladder", 65, 3, (0, 0, -1, 5, 0)),
        new("stone button", 77, 1, (-1, 0, 0, 1, 0)), new("wooden button", 143, 5), new("lever", 69, 5),
        new("bed", 26, 0, (0, 0, 1, 26, 8)), new("chest", 54, 2), new("trapped chest", 146, 3), new("ender chest", 130, 2),
        new("sign", 63, 4), new("wall sign", 68, 3, (0, 0, -1, 5, 0)),
        new("wheat", 59, 7, (0, -1, 0, 60, 0)), new("carrots", 141, 7, (0, -1, 0, 60, 0)), new("beetroots", 207, 3, (0, -1, 0, 60, 0)),
        new("nether wart", 115, 3, (0, -1, 0, 88, 0)), new("dandelion", 37, 0), new("azure bluet", 38, 3), new("tall grass", 31, 1),
        new("sunflower", 175, 0, (0, 1, 0, 175, 8)), new("sapling", 6, 1), new("sugar cane", 83, 0), new("cactus", 81, 0),
        new("water", 9, 0), new("flowing water", 8, 3), new("lava", 11, 0), new("lily pad", 111, 0, (0, -1, 0, 9, 0)),
        new("glass", 20, 0, (1, 0, 0, 20, 0)), new("stained glass", 95, 14), new("leaves", 18, 0, (0, 1, 0, 18, 0)), new("ice", 79, 0),
        new("carpet", 171, 14), new("flower pot", 140, 0), new("anvil", 145, 0), new("hopper", 154, 0), new("cauldron", 118, 0),
        new("brewing stand", 117, 0), new("enchanting table", 116, 0), new("end rod", 198, 1), new("rail", 66, 0),
        new("snow layer", 78, 2), new("redstone wire", 55, 0, (1, 0, 0, 55, 0)), new("repeater", 93, 0), new("vine", 106, 1),
        new("furnace", 61, 2), new("log", 17, 0), new("grass", 2, 0), new("glowstone", 89, 0),
    ];

    /// <summary>Slot <paramref name="i"/>'s anchor in chunk (0, 0): a 5 × 5 grid per layer, layers at y 64, 70 and 75 (floors at 69
    /// and 74 cover half the area, so the slots see sky, shade and torch light).</summary>
    public static (int X, int Y, int Z) Slot(int i) => (1 + 3 * (i % 5), (i / 25) switch { 0 => 64, 1 => 70, _ => 75 }, 1 + 3 * (i / 5 % 5));

    public static (WorldState World, Dictionary<(int, int, int), string> Names) Build(Spec[] specs, int height)
    {
        var w = new WorldState(height: height) { Dimension = 0 };
        var names = new Dictionary<(int, int, int), string>();
        for (int cx = -1; cx <= 1; cx++)
            for (int cz = -1; cz <= 1; cz++)
            {
                var ids = new byte[16 * height * 16];
                var meta = new byte[ids.Length];
                void Put(int x, int y, int z, int id, int m)
                {
                    if ((uint)x >= 16 || (uint)z >= 16) return;
                    ids[(x * 16 + z) * height + y] = (byte)id;
                    meta[(x * 16 + z) * height + y] = (byte)m;
                }
                for (int x = 0; x < 16; x++)
                    for (int z = 0; z < 16; z++)
                    {
                        for (int y = 1; y < 63; y++) Put(x, y, z, Blocks.Stone, 0);
                        Put(x, 63, z, Blocks.Stone, 0);
                        if (cx == 0 && cz == 0 && z < 8) { Put(x, 69, z, Blocks.Stone, 0); Put(x, 74, z, Blocks.Planks, 0); }
                    }
                if (cx == 0 && cz == 0)
                    for (int i = 0; i < specs.Length; i++)
                    {
                        var (sx, sy, sz) = Slot(i);
                        var s = specs[i];
                        Put(sx, sy, sz, s.Id, s.Meta);
                        names[(sx, sy, sz)] = s.Name;
                        foreach (var (dx, dy, dz, id, m) in s.Extra)
                        {
                            Put(sx + dx, sy + dy, sz + dz, id, m);
                            names.TryAdd((sx + dx, sy + dy, sz + dz), s.Name + " (part)");
                        }
                    }
                w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, height, 16, ids, meta, null, null);
            }
        return (w, names);
    }

    /// <summary>The world as the Explorer receives it: every column with its light (the live path, payload v2).</summary>
    public static HiveWorldCache Sync(WorldState w)
    {
        var list = new List<ColumnPayloadEntry>();
        for (int cx = -1; cx <= 1; cx++)
            for (int cz = -1; cz <= 1; cz++)
            {
                var col = w.GetColumn(cx, cz)!;
                var snap = ChunkSnapshot.Capture(col);
                var light = LightSnapshot.Capture(col);
                list.Add(new ColumnPayloadEntry(cx, cz, LightSnapshot.Combine(snap.Hash, light.Hash), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "maid_chan", true, snap.Encode(), light.Encode()));
            }
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, list));
        return cache;
    }
}

/// <summary>
/// The GPU freecam's mesh = the CPU raycaster's geometry and light, block by block, on both versions: for every block of the
/// fixture the GL vertices (positions, the texel the shader samples from the GL atlas with the vertex tint, the alpha test, the
/// per-vertex light) equal the quads and light <see cref="EyeRenderer"/> draws from the server's world (cube faces: the six
/// culled faces, their corner light and texel; shapes: <see cref="BlockMesh.Quads"/> with <see cref="FaceLight.Quad"/>).
/// </summary>
[Collection("avalonia")]
public class FreecamParityTests(ITestOutputHelper output)
{
    private static readonly Lazy<TextureAtlas?> BetaAtlas = new(() =>
    {
        var dir = Path.Combine(ExplorerPaths.DataDir, "assets");
        return Directory.Exists(dir) ? TextureLocator.Load(new TextureOptions { AssetsDir = dir, AllowDownload = false }) : null;
    });

    private sealed record Quad(GeoQuad Q, float L0, float L1, float L2, float L3, uint Texel);

    /// <summary>What the CPU raycaster draws for the block at (x, y, z) of <paramref name="w"/>, in the mesher's emit order.</summary>
    private static List<Quad> Expected(IVoxelWorld w, TextureAtlas? atlas, int x, int y, int z, int sub, bool smooth)
    {
        var models = atlas?.Models;
        int id = w.Block(x, y, z), meta = w.Meta(x, y, z);
        var shape = BlockMesh.Shape(models, id, meta);
        var list = new List<Quad>();
        var c = new float[4];
        Quad Cube(GeoQuad q, Face f, bool emissive, uint texel)
        {
            if (emissive) Array.Fill(c, 1f);
            else FaceLight.CubeCorners(w, id, x, y, z, f, sub, false, smooth, 0.12f, c);
            return new Quad(q, c[FaceLight.Corner(f, q.P0)], c[FaceLight.Corner(f, q.P1)], c[FaceLight.Corner(f, q.P2)], c[FaceLight.Corner(f, q.P3)], texel);
        }
        if (BlockMesh.IsCube(shape) && (models != null || shape == BlockShape.Cube))
        {
            for (int fi = 0; fi < 6; fi++)
            {
                var f = (Face)fi;
                var (dx, dy, dz) = Lighting.Normal(f);
                int n = w.Block(x + dx, y + dy, z + dz);
                // The raycaster never reaches a face behind an opaque cube, nor one between two cells of the same glass.
                if (n != 0 && BlockMesh.Shape(models, n, w.Meta(x + dx, y + dy, z + dz)) == BlockShape.Cube && (models != null || BlockGeometry.OpaqueCube(n))) continue;
                if (models != null && shape != BlockShape.Cube && n == id && id is not (18 or 161)) continue;
                if (models != null)
                {
                    var q = models.CubeFace(w, x, y, z, id, meta, f);
                    if (q.Tex != QuadTex.Block) continue;
                    var at = SamplePoint3(q);
                    list.Add(Cube(q, f, models.Emissive(id), models.CubeTexel(w, x, y, z, id, meta, f, at)));
                }
                else
                {
                    var q = BlockMesh.CubeFace(w, x, y, z, id, meta, f);
                    var at = SamplePoint3(q);
                    var (u, v) = FaceLight.CubeFaceUv(f, at.X, at.Y, at.Z);
                    uint texel = atlas != null
                        ? BlockTextures.Texel(atlas, BlockGeometry.CubeTile(w, x, y, z, id, meta, f), id, (float)u, (float)v) ?? (0xff000000 | BlockGeometry.FlatColour(id, f))
                        : 0xff000000 | BlockGeometry.FlatColour(id, f);
                    list.Add(Cube(q, f, false, texel));
                }
            }
            return list;
        }
        var quads = new List<GeoQuad>();
        BlockMesh.Quads(models, w, x, y, z, quads);
        foreach (var q in quads)
        {
            uint texel = EyeRenderer.SampleQuad(atlas, q, SamplePoint(q.T0, q.T1, q.T2, q.T3));
            if (BlockMesh.IsCube(shape) && q.LightFace >= 0) list.Add(Cube(q, (Face)q.LightFace, false, texel));
            else
            {
                float l = FaceLight.Quad(w, q, x, y, z, sub, false, 0.12f);
                list.Add(new Quad(q, l, l, l, l, texel));
            }
        }
        return list;
    }

    /// <summary>The colour the world shader produces for a vertex quad at its UV centroid (before light): atlas texel × vertex colour.</summary>
    private static uint GlTexel(ReadOnlySpan<GpuVertex> v, byte[] rgba, int aw, int ah, int rows)
    {
        var a = v[0];
        float r = (a.Rgba & 0xff) / 255f, g = (a.Rgba >> 8 & 0xff) / 255f, b = (a.Rgba >> 16 & 0xff) / 255f, al = (a.Rgba >> 24) / 255f;
        if ((a.Flags & GpuVertex.Textured) != 0)
        {
            var uv = SamplePoint(new(v[0].U, v[0].V), new(v[1].U, v[1].V), new(v[2].U, v[2].V), new(v[3].U, v[3].V));
            if ((a.Flags & GpuVertex.Wrap) != 0) uv = new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));
            uv = (a.Flags & GpuVertex.Span) != 0 ? Vector2.Clamp(uv, new(0.002f), new(3.998f, 1.998f)) : Vector2.Clamp(uv, new(0.002f), new(0.998f));
            int px = (int)((a.Col + uv.X) / GpuAtlas.Cols * aw), py = (int)((a.Row + uv.Y) / rows * ah);
            uint t = GpuAtlas.Texel(rgba, aw, Math.Clamp(px, 0, aw - 1), Math.Clamp(py, 0, ah - 1));
            r *= (t >> 16 & 0xff) / 255f; g *= (t >> 8 & 0xff) / 255f; b *= (t & 0xff) / 255f; al *= (t >> 24) / 255f;
        }
        return (uint)(al * 255 + 0.5f) << 24 | (uint)(r * 255 + 0.5f) << 16 | (uint)(g * 255 + 0.5f) << 8 | (uint)(b * 255 + 0.5f);
    }

    private static Vector3 SamplePoint3(in GeoQuad q) => q.P0 * 0.41f + q.P1 * 0.27f + q.P2 * 0.19f + q.P3 * 0.13f;

    /// <summary>A point inside the quad off the texel grid's centre lines (the same uv on both sides).</summary>
    private static Vector2 SamplePoint(Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3) => t0 * 0.41f + t1 * 0.27f + t2 * 0.19f + t3 * 0.13f;

    private static bool Near(uint a, uint b, int tol) =>
        Math.Abs((int)(a >> 16 & 0xff) - (int)(b >> 16 & 0xff)) <= tol && Math.Abs((int)(a >> 8 & 0xff) - (int)(b >> 8 & 0xff)) <= tol
        && Math.Abs((int)(a & 0xff) - (int)(b & 0xff)) <= tol;

    /// <summary>Every block of section 4 of chunk (0, 0): the mismatches ("name: what differs"), empty = parity.</summary>
    private static List<string> Compare(WorldState world, HiveWorldCache cache, Dictionary<(int, int, int), string> names, TextureAtlas? atlas,
        IBlockModels? meshModels, int sub, bool smooth, out int blocks, out int quads, bool geometryOnly = false)
    {
        var cpu = new WorldVoxels(world);
        cpu.Prepare(8, 8, 2);
        var hood = new ColumnNeighbourhood();
        hood.Set(cache, 0, 0, 0);
        var m = new SectionMesher { Textured = atlas != null, Models = meshModels, SkySubtracted = sub, Smooth = smooth, Spans = [] };
        m.Build(hood, 0, 4, 0);
        var (aw, ah, rgba) = GpuAtlas.Build(atlas);
        int rows = GpuAtlas.RowsFor(atlas);
        var bad = new List<string>();
        blocks = 0;
        int nq = 0;
        foreach (var sp in m.Spans!)
        {
            blocks++;
            string name = names.GetValueOrDefault((sp.X, sp.Y, sp.Z)) ?? $"block {cpu.Block(sp.X, sp.Y, sp.Z)}";
            if (hood.Block(sp.X, sp.Y, sp.Z) != cpu.Block(sp.X, sp.Y, sp.Z) || hood.Meta(sp.X, sp.Y, sp.Z) != cpu.Meta(sp.X, sp.Y, sp.Z)) { bad.Add($"{name}: synced block differs"); continue; }
            var exp = Expected(cpu, atlas, sp.X, sp.Y, sp.Z, sub, smooth);
            var solid = exp.Where(e => e.Q.Pass != RenderPass.Translucent).ToList();
            var trans = exp.Where(e => e.Q.Pass == RenderPass.Translucent).ToList();
            static int Gl(List<Quad> e, bool textured) => e.Sum(q => q.Q.Tex == QuadTex.Block && q.Q.Overlay >= 0 && textured ? 2 : 1);
            int gs = (sp.Solid1 - sp.Solid0) / 4, gt = (sp.Trans1 - sp.Trans0) / 4;
            if (gs != Gl(solid, atlas != null) || gt != Gl(trans, atlas != null)) { bad.Add($"{name}: {gs}+{gt} GL quads, the raycaster draws {solid.Count}+{trans.Count}"); continue; }
            var off = new Vector3(sp.X & 15, sp.Y & 15, sp.Z & 15);
            string? Check(List<Quad> e, GpuVertex[] verts, int start)
            {
                for (int i = 0, at = start; i < e.Count; i++, at += 4)
                {
                    nq++;
                    var v = verts.AsSpan(at, 4);
                    bool overlay = e[i].Q.Tex == QuadTex.Block && e[i].Q.Overlay >= 0 && atlas != null;
                    var q = e[i].Q;
                    Vector3[] ps = [q.P0, q.P1, q.P2, q.P3];
                    float[] ls = [e[i].L0, e[i].L1, e[i].L2, e[i].L3];
                    for (int k = 0; k < 4; k++)
                    {
                        if (Vector3.Distance(new Vector3(v[k].X, v[k].Y, v[k].Z), ps[k] + off) > 1e-4f) return $"quad {i} corner {k} at {v[k].X},{v[k].Y},{v[k].Z}, raycaster {ps[k] + off}";
                        if (!geometryOnly && Math.Abs(v[k].Light - QuadEmit.LightByte(ls[k])) > 1) return $"quad {i} corner {k} light {v[k].Light / 255f:0.000}, raycaster {ls[k]:0.000}";
                    }
                    if (geometryOnly) { if (overlay) at += 4; continue; }
                    uint gl = GlTexel(v, rgba, aw, ah, rows), want = e[i].Texel;
                    if (overlay)
                    {
                        // The overlay quad follows on the same corners; where its texel is opaque it is what the alpha test keeps.
                        at += 4;
                        var o = verts.AsSpan(at, 4);
                        for (int k = 0; k < 4; k++)
                            if (o[k].X != v[k].X || o[k].Y != v[k].Y || o[k].Z != v[k].Z || o[k].Light != v[k].Light) return $"quad {i}: overlay off its base";
                        uint ov = GlTexel(o, rgba, aw, ah, rows);
                        if (ov >> 24 >= 128) gl = ov | 0xff000000;
                    }
                    bool cutGl = gl >> 24 < 128 && q.Pass == RenderPass.Cutout, cutCpu = want >> 24 < 128 && q.Pass == RenderPass.Cutout;
                    if (cutGl != cutCpu) return $"quad {i} alpha test: GL {gl >> 24}, raycaster {want >> 24}";
                    if (!cutCpu && q.Pass != RenderPass.Translucent && !Near(gl, want, 4)) return $"quad {i} texel GL {gl & 0xffffff:x6}, raycaster {want & 0xffffff:x6}";
                }
                return null;
            }
            if ((Check(solid, m.Solid.V, sp.Solid0) ?? Check(trans, m.Translucent.V, sp.Trans0)) is { } why) bad.Add($"{name}: {why}");
        }
        quads = nq;
        return bad;
    }

    private void Run(ParityFixture.Spec[] specs, int height, TextureAtlas? atlas, IGameData data)
    {
        Assert.True(specs.Length <= 75, "three layers of 25 slots");
        using var _ = GameData.Override(data);
        var (world, names) = ParityFixture.Build(specs, height);
        var cache = ParityFixture.Sync(world);
        foreach (var (sub, smooth) in new[] { (0, true), (0, false), (11, true) })
        {
            var bad = Compare(world, cache, names, atlas, atlas?.Models, sub, smooth, out int blocks, out int quads);
            output.WriteLine($"{data.VersionId} sub {sub} smooth {smooth}: {blocks} blocks, {quads} quads, {bad.Count} mismatches");
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.True(blocks >= specs.Length, $"{blocks} blocks meshed");
        }
    }

    [Fact]
    public void Gl_mesh_equals_the_raycaster_on_b181()
    {
        Run(ParityFixture.Beta, 128, BetaAtlas.Value, Beta181GameData.Instance);
    }

    [Jar1122Fact]
    public void Gl_mesh_equals_the_raycaster_on_1122()
    {
        Run(ParityFixture.Modern, 256, Jar1122FactAttribute.Atlas.Value, Release1122GameData.Instance);
    }

    /// <summary>The reported bug: a 1.12.2 hive meshed with the b1.8.1 shapes (the Explorer's local version file said b1.8.1).
    /// Lists which blocks drew differently from the server's sheets; the fix (<see cref="VersionedAtlas"/>) meshes with the models.</summary>
    [Jar1122Fact]
    public void Which_blocks_the_old_freecam_drew_wrong_on_1122()
    {
        var atlas = Jar1122FactAttribute.Atlas.Value!;
        using var data = GameData.Override(Release1122GameData.Instance);
        var (world, names) = ParityFixture.Build(ParityFixture.Modern, 256);
        var cache = ParityFixture.Sync(world);
        // Shapes only (the b1.8.1 path also samples other textures: that alone would flag nearly everything).
        var bad = Compare(world, cache, names, atlas, meshModels: null, 0, true, out _, out _, geometryOnly: true);
        foreach (var b in bad) output.WriteLine("  " + b);
        var wrong = bad.Select(b => b[..b.IndexOf(':')].Replace(" (part)", "")).Distinct().ToList();
        output.WriteLine($"b1.8.1 shapes on a 1.12.2 world: {wrong.Count} of {ParityFixture.Modern.Length} blocks differ: {string.Join(", ", wrong)}");
        Assert.Contains("spruce door", wrong);
        Assert.Contains("oak door", wrong);
        Assert.Contains("spruce fence", wrong);
    }
}
