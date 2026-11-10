using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot.Perception.Vision.Eval;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.Render.Models;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins MineClaw.Render byte for byte (WP08 complexity refactor): the lookup tables over every input (block textures, 1.12.2
/// block states, b1.8.1 geometry quads, entity models, roles, palettes, weather, glyphs) and the pictures the model sees (eye,
/// sheet panels, flat maps, the TUI iso and viewport text) as hashes over seeded worlds, cameras and entities, without and with
/// textures (synthetic; the installed b1.8.1 / 1.12.2 jars when present). Only the render time in the eye legend is masked.
/// Set RENDER_GOLDEN_OUT to a directory to write the dumps out (to diff after a change).
/// </summary>
public class RenderGoldenTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ hashing

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b))[..20];
    private static string Hash(string s) => Hash(Encoding.UTF8.GetBytes(s));
    private static string Hash(uint[] px) => Hash(MemoryMarshal.AsBytes(px.AsSpan()).ToArray());
    private static readonly Regex Ms = new(@"\d+ms", RegexOptions.Compiled);

    private static void Quad(StringBuilder sb, in GeoQuad q)
    {
        static void V(StringBuilder sb, Vector3 v) => sb.Append(BitConverter.SingleToInt32Bits(v.X)).Append(',').Append(BitConverter.SingleToInt32Bits(v.Y)).Append(',').Append(BitConverter.SingleToInt32Bits(v.Z)).Append(' ');
        static void T(StringBuilder sb, Vector2 v) => sb.Append(BitConverter.SingleToInt32Bits(v.X)).Append(',').Append(BitConverter.SingleToInt32Bits(v.Y)).Append(' ');
        V(sb, q.P0); V(sb, q.P1); V(sb, q.P2); V(sb, q.P3);
        T(sb, q.T0); T(sb, q.T1); T(sb, q.T2); T(sb, q.T3);
        sb.Append(q.Tile).Append(' ').Append(q.Tex).Append(' ').Append(q.Pass).Append(' ').Append(q.Tint).Append(' ').Append(q.Overlay).Append(' ')
            .Append(q.OverlayTint).Append(' ').Append(BitConverter.SingleToInt32Bits(q.Shade)).Append(' ').Append(q.LightFace).Append(' ')
            .Append(q.Emissive).Append(' ').Append(q.TwoSided).Append(' ').Append(q.Flat).Append(' ').Append(BitConverter.SingleToInt32Bits(q.Alpha)).Append('|');
    }

    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (Exception ex) { return "throws " + ex.GetType().Name; }
    }

    private void Compare(string name, List<(string Name, string Dump)> rows, string expected)
    {
        var outDir = Environment.GetEnvironmentVariable("RENDER_GOLDEN_OUT");
        var actual = new StringBuilder();
        foreach (var (n, dump) in rows)
        {
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, $"{name}-{Regex.Replace(n, "[^A-Za-z0-9_.-]", "_")}.txt"), dump);
            actual.Append(n).Append(' ').Append(Hash(dump)).Append('\n');
        }
        var want = string.Join("\n", expected.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0)) + "\n";
        if (want != actual.ToString()) output.WriteLine(actual.ToString());
        Assert.Equal(want, actual.ToString());
    }

    // ------------------------------------------------------------------ neighbourhoods

    /// <summary>Neighbour blocks that change a state, a shape or a texture (connections, corners, halves, snow, fruit, wire).</summary>
    private static readonly int[] NeighbourPalette =
    [
        0, 0, 0, 0, 1, 2, 3, 8, 9, 10, 11, 17, 18, 20, 27, 31, 43, 44, 50, 53, 54, 55, 64, 65, 66, 67, 69, 70, 71, 75, 76, 77, 78, 80, 85, 86, 93, 94,
        98, 101, 102, 103, 104, 105, 107, 108, 113, 130, 131, 139, 143, 146, 149, 152, 160, 175, 183, 188, 193, 218, 79, 89,
    ];

    /// <summary>A block at the origin cell (0, 64, 0) with seeded neighbours in the 5×3×5 box around it (k = 0: alone).</summary>
    private static SparseVoxels Hood(int id, int meta, int k)
    {
        var w = new SparseVoxels();
        if (k > 0)
        {
            var rnd = new Random(id * 131 + meta * 7 + k * 100_003);
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        int pick = rnd.Next(NeighbourPalette.Length);
                        int m = rnd.Next(16);
                        // k = 1: copies of the block itself around (fences, panes, stairs, chests, doors, wire meet their own kind).
                        int nid = k == 1 && rnd.Next(2) == 0 ? id : NeighbourPalette[pick];
                        if (nid != 0) w.Set(dx, 64 + dy, dz, nid, m);
                    }
        }
        w.Set(0, 64, 0, id, meta);
        return w;
    }

    private const int Hoods = 4;

    // ------------------------------------------------------------------ lookup tables

    [Fact]
    public void Lookup_tables_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>();
        var sb = new StringBuilder();
        string Done() { var s = sb.ToString(); sb.Clear(); return s; }

        // BlockTextures.For: every id × meta × face × fancy.
        for (int id = -2; id <= 300; id++)
            for (int meta = -2; meta <= 17; meta++)
                for (int f = 0; f < 6; f++)
                    for (int fancy = 0; fancy < 2; fancy++)
                        sb.Append(BlockTextures.For(id, meta, (Face)f, fancy == 1)).Append('\n');
        rows.Add(("BlockTextures.For", Done()));
        for (int id = -2; id <= 300; id++) sb.Append(BlockTextures.UsePalette(id)).Append(BlockTextures.Emissive(id)).Append('\n');
        rows.Add(("BlockTextures.flags", Done()));

        // BlockStates1122.Resolve: every id × meta, without a world and in seeded neighbourhoods.
        static bool SolidFull(int b, int m) => (b * 7 + m) % 5 != 0;
        for (int id = -1; id <= 300; id++)
            for (int meta = -1; meta <= 16; meta++)
            {
                sb.Append(id).Append(':').Append(meta).Append(' ')
                    .Append(Try(() => { var s0 = BlockStates1122.Resolve(id, meta, null, 0, 64, 0, SolidFull); return s0.CacheKey + (s0.Builtin ? " B" : ""); }));
                for (int k = 0; k < Hoods; k++)
                {
                    var w = Hood(id, meta, k);
                    var s = Try(() => { var r = BlockStates1122.Resolve(id, meta, w, 0, 64, 0, SolidFull); return r.CacheKey + (r.Builtin ? " B" : ""); });
                    sb.Append(" | ").Append(s);
                }
                sb.Append('\n');
            }
        rows.Add(("BlockStates1122.Resolve", Done()));
        for (int id = -1; id <= 300; id++)
            sb.Append(BlockStates1122.IsStairs(id)).Append(BlockStates1122.IsFence(id)).Append(BlockStates1122.IsWoodFence(id)).Append(BlockStates1122.IsFenceGate(id))
                .Append(BlockStates1122.IsPane(id)).Append(BlockStates1122.IsDoor(id)).Append('\n');
        for (int i = -7; i <= 20; i++) sb.Append(BlockStates1122.Facing(i)).Append(BlockStates1122.HFacing(i)).Append(BlockStates1122.StairFacing(i)).Append(BlockStates1122.StairHalf(i)).Append('\n');
        rows.Add(("BlockStates1122.helpers", Done()));

        // b1.8.1 geometry: render type, classification, flat colours, cube faces and the quads of every id × meta in each neighbourhood.
        for (int id = -1; id <= 300; id++)
        {
            sb.Append(id).Append(' ').Append(BlockGeometry.RenderType(id)).Append(' ').Append(BlockGeometry.Classify(id)).Append(' ').Append(BlockGeometry.OpaqueCube(id));
            for (int f = 0; f < 6; f++) sb.Append(' ').Append(BlockGeometry.FlatColour(id, (Face)f));
            sb.Append('\n');
        }
        rows.Add(("BlockGeometry.classes", Done()));
        var quads = new List<GeoQuad>();
        for (int id = 0; id <= 255; id++)
            for (int meta = 0; meta <= 15; meta++)
                for (int k = 0; k < Hoods; k++)
                {
                    var w = Hood(id, meta, k);
                    quads.Clear();
                    sb.Append(id).Append(':').Append(meta).Append(':').Append(k).Append(' ');
                    sb.Append(Try(() => { BlockGeometry.Build(w, 0, 64, 0, quads); return quads.Count.ToString(CultureInfo.InvariantCulture); })).Append(' ');
                    foreach (var q in quads) Quad(sb, q);
                    for (int f = 0; f < 6; f++) Quad(sb, BlockMesh.CubeFace(w, 0, 64, 0, id, meta, (Face)f));
                    if (BlockGeometry.IsFluid(id))
                    {
                        sb.Append(BlockGeometry.FluidCorners(w, 0, 64, 0, id)).Append(BlockGeometry.FlowVector(w, 0, 64, 0, id)).Append(BlockGeometry.FlowAngle(w, 0, 64, 0, id));
                    }
                    sb.Append('\n');
                }
        rows.Add(("BlockGeometry.Build", Done()));
        for (int k = 0; k < 64; k++)
        {
            var w = Hood(Blocks.Chest, k & 3, k);
            for (int l = 0; l < 6; l++) sb.Append(BlockGeometry.ChestTile(w, 0, 64, 0, l)).Append(' ');
            sb.Append('\n');
        }
        rows.Add(("BlockGeometry.ChestTile", Done()));

        // Entities: model kinds, rest models, posed quads (every kind, poses, variants, items), procedural textures, item icons.
        string[] kinds = ["player", "hive", "self", "item", "zombie", "giant", "husk", "zombie_villager", "vindication_illager", "evocation_illager",
            "illusion_illager", "witch", "villager", "zombie_pigman", "pigzombie", "pig_zombie", "skeleton", "stray", "wither_skeleton", "magma_cube",
            "endermite", "mooshroom", "ocelot", "polar_bear", "guardian", "elder_guardian", "creeper", "spider", "cave_spider", "cavespider", "pig",
            "cow", "sheep", "chicken", "enderman", "slime", "wolf", "squid", "ghast", "silverfish", "mob_42", "blaze", "Zombie", "unknown", ""];
        foreach (var k in kinds)
        {
            var e = new VisionEntity(k is "player" or "hive" or "self" or "item" ? k : "mob", k, 0.5, 64, 0.5) { Mob = k.Length > 0 ? k : null };
            var kind = EntityGeometry.ModelKind(e);
            sb.Append(k).Append(" → ").Append(kind ?? "null").Append(' ');
            if (kind != null)
            {
                sb.Append(Try(() =>
                {
                    var m = EntityGeometry.Create(kind);
                    var s = new StringBuilder();
                    foreach (var p in m.Parts) s.Append(p.Name).Append(p.Px).Append(p.Py).Append(p.Pz).Append(p.Texture).Append(string.Join(",", p.Boxes)).Append(";");
                    s.Append(m.Texture).Append(m.TexW).Append(m.TexH).Append(m.Scale).Append(m.Shadow).Append(m.Height).Append(m.Translucent);
                    return s.ToString();
                }));
            }
            for (int v = 0; v < 3; v++) sb.Append(' ').Append(BitConverter.SingleToInt32Bits(EntityGeometry.Width(kind ?? "", v)));
            sb.Append('\n');
        }
        rows.Add(("EntityGeometry.kinds", Done()));
        foreach (var e in Zoo(0.5, 0.5))
        {
            quads.Clear();
            sb.Append(e.Kind).Append(' ').Append(e.Name).Append(' ');
            sb.Append(Try(() => { EntityGeometry.Build(e, quads, 3.5, 6.5); return quads.Count.ToString(CultureInfo.InvariantCulture); })).Append(' ');
            foreach (var q in quads) Quad(sb, q);
            quads.Clear();
            sb.Append(Try(() => { EntityGeometry.Build(e, quads, -4.5, 2.5, null, 70.0); return quads.Count.ToString(CultureInfo.InvariantCulture); })).Append(' ');
            foreach (var q in quads) Quad(sb, q);
            sb.Append(BitConverter.SingleToInt32Bits(EntityGeometry.ShadowSize(e))).Append(' ').Append(BitConverter.SingleToInt32Bits(EntityGeometry.BodyYaw(e))).Append('\n');
        }
        rows.Add(("EntityGeometry.Build", Done()));
        for (int id = -1; id <= 20; id++)
        {
            var (tw, th, tpx) = EntityTextures.Get(null, id);
            sb.Append(id).Append(' ').Append(EntityTextures.Name(id)).Append(' ').Append(tw).Append('x').Append(th).Append(' ').Append(Hash(tpx)).Append('\n');
        }
        for (int c = 0; c < 17; c++) sb.Append(EntityTextures.Fleece(c)).Append(' ');
        sb.Append(Hash(FluidTextures.WaterStill)).Append(Hash(FluidTextures.WaterFlow)).Append(Hash(FluidTextures.LavaStill)).Append(Hash(FluidTextures.LavaFlow)).Append('\n');
        rows.Add(("EntityTextures", Done()));
        for (int id = -1; id <= 2300; id++)
            for (int d = 0; d < 17; d += 4)
                sb.Append(ItemIcons.Of(id, d)).Append(ItemIcons.Colour(id)).Append(EntityGeometry.ItemIn3d(id)).Append('\n');
        rows.Add(("ItemIcons", Done()));

        // Roles, palettes, weather, glyphs.
        foreach (BlockRole r in Enum.GetValues<BlockRole>().Append((BlockRole)200))
            sb.Append(r).Append(' ').Append(BlockRoles.Highlight(r)).Append(BlockRoles.Glyph(r)?.ToString() ?? "-").Append(BlockRoles.Colour(r)).Append(BlockRoles.Name(r)).Append('\n');
        sb.Append(BlockRoles.Legend(Enum.GetValues<BlockRole>())).Append('\n');
        string[] roleWords = ["", " ", null!, "never break", "dark spawn", "SPAWN", "planned", "blueprint", "unripe", "not ripe", "growing wheat", "stem", "ripe",
            "crop", "wheat", "grown", "gen_cell", "generator cell", "MINE HERE", "dig-cell", "portal", "obsidian", "void", "abyss", "lava", "water",
            "door", "chest", "station", "furnace", "bench", "table", "own", "placed", "self", "build", "player build", "protected", "floor", "ground",
            "wall", "solid", "none", "air", "empty", "cobblestone", "dirt"];
        foreach (var s in roleWords) sb.Append(s).Append(" → ").Append(BlockRoles.Parse(s)?.ToString() ?? "null").Append('\n');
        rows.Add(("BlockRoles", Done()));
        for (int id = -1; id <= 300; id++)
        {
            sb.Append(id).Append(' ').Append(ViewportBuilder.Glyph(id)?.ToString() ?? "-").Append(' ').Append(IsoPalette.Mat(id)?.ToString() ?? "null").Append('\n');
        }
        foreach (IsoColours c in Enum.GetValues<IsoColours>())
            for (uint rgb = 0; rgb < 0x1000000; rgb += 0x0b3f17)
                sb.Append(Hash(IsoPalette.FaceIndices(rgb, rgb ^ 0x404040, c))).Append(IsoPalette.Quantize(rgb, c)).Append(' ');
        rows.Add(("IsoPalette", Done()));
        for (int b = -2; b <= 300; b++)
            sb.Append(b).Append(' ').Append(Weather.Dry(b)).Append(BitConverter.SingleToInt32Bits(Weather.Temperature(b))).Append(Weather.At(b, 64)).Append(Weather.At(b, 120)).Append(Weather.At(b, 200)).Append('\n');
        rows.Add(("Weather", Done()));

        // TUI iso shading over every material, face, sprite pixel kind and colour mode.
        for (int zoom = 0; zoom <= IsoView.MaxZoom; zoom++)
        {
            var sprite = IsoProjection.Sprite(zoom);
            for (int id = 0; id <= 255; id++)
            {
                if (IsoPalette.Mat(id) is not { } mat) continue;
                foreach (IsoColours c in Enum.GetValues<IsoColours>())
                    foreach (var px in new[] { sprite.Full, sprite.Small, sprite.Flat })
                        for (int i = 0; i < px.Length; i++)
                        {
                            var p = px[i];
                            sb.Append(IsoRenderer.Shade(mat, p, id * 3 + i, 64 - i % 5, id - i, i % 7 / 7f, c, sprite)).Append(' ');
                        }
                sb.Append('\n');
            }
        }
        rows.Add(("IsoRenderer.Shade", Done()));

        // The b1.8.1 ascii map cell glyphs in each neighbourhood.
        for (int id = 0; id <= 255; id++)
            for (int k = 0; k < Hoods; k++)
            {
                var w = Hood(id, id & 15, k);
                sb.Append(AsciiMap.Cell(new VoxelBlocks(w), 0, 64, 0)).Append(AsciiMap.Cell(new VoxelBlocks(w), 0, 65, 0)).Append(AsciiMap.Cell(new VoxelBlocks(w), 1, 64, 0));
            }
        rows.Add(("AsciiMap.Cell", Done()));

        Compare("lookup", rows, LookupGolden);
    }

    /// <summary>An <see cref="IBlockAccess"/> over a sparse voxel world (the ascii map reads blocks only).</summary>
    private sealed class VoxelBlocks(IVoxelWorld w) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => w.Block(x, y, z);
        public int GetMeta(int x, int y, int z) => w.Meta(x, y, z);
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }

    // ------------------------------------------------------------------ scenes

    /// <summary>Every entity kind and pose the renderers draw.</summary>
    private static List<VisionEntity> Zoo(double x0, double z0)
    {
        var list = new List<VisionEntity>();
        string[] mobs = ["zombie", "skeleton", "creeper", "spider", "cave_spider", "pig", "cow", "sheep", "chicken", "enderman", "slime", "wolf",
            "squid", "ghast", "silverfish", "pigzombie", "giant", "magma_cube", "mooshroom", "villager"];
        int i = 0;
        foreach (var m in mobs)
        {
            double x = x0 + (i % 5) * 2.5, z = z0 + (i / 5) * 2.5;
            list.Add(new VisionEntity("mob", m, x, 64, z) { Mob = m, Yaw = i * 37 % 360, Pitch = i * 7 % 40 - 20, Vx = i % 3 * 0.05, Vz = i % 2 * 0.07,
                Variant = i * 5 % 20, Seed = i * 977, Burning = i % 6 == 1, Sneaking = i % 4 == 3 });
            i++;
        }
        list.Add(new VisionEntity("player", "fox", x0 - 2, 64, z0) { Yaw = 30, Pitch = 10, HeldItem = 276, Vx = 0.1 });
        list.Add(new VisionEntity("player", "robin", x0 - 2, 64, z0 + 2.5) { Yaw = 200, Sneaking = true, HeldItem = 4, Burning = true });
        list.Add(new VisionEntity("hive", "alice", x0 - 2, 64, z0 + 5, 'a') { Yaw = 95, HeldItem = 50 });
        list.Add(new VisionEntity("self", "you", x0 - 2, 64, z0 + 7.5, '@') { Yaw = 270, Pitch = -30 });
        int[] items = [1, 4, 50, 53, 85, 6, 37, 263, 264, 276, 351, 2256, 9999, 54, 65, 26, 355];
        int j = 0;
        foreach (var id in items)
        {
            list.Add(new VisionEntity("item", null, x0 + 12 + (j % 4) * 0.9, 64, z0 + (j / 4) * 0.9)
                { ItemId = id, ItemCount = j * 9 % 40 + 1, ItemDamage = j % 5, Seed = j * 131, Age = j * 17.5 });
            j++;
        }
        list.Add(new VisionEntity("item", null, x0 + 12, 64, z0 + 5) { ItemId = 264, ItemCount = 3, Seed = 99 }); // age from the seed
        return list;
    }

    /// <summary>A b1.8.1 world: a stone floor, every block id (1..maxId) with a seeded meta in a museum grid, connector rows (fences,
    /// panes, stairs corners, chest pairs, doors, wire, rails), pools and flows, a roofed bunker with the body inside, a tunnel.</summary>
    private static WorldState World(int dim = 0, int maxId = 122, int biome = -1)
    {
        var w = new WorldState { Dimension = dim };
        w.ApplyChunkRegion(-48, 0, -48, 96, 128, 96, new byte[96 * 128 * 96], null, null, null);
        if (biome >= 0)
            for (int cx = -3; cx < 3; cx++) for (int cz = -3; cz < 3; cz++) w.SetBiomes(cx, cz, Enumerable.Repeat((byte)biome, 256).ToArray());
        for (int x = -24; x <= 24; x++) for (int z = -24; z <= 30; z++) w.SetBlock(x, 63, z, (x + z) % 7 == 0 ? Blocks.Grass : Blocks.Stone, 0);
        var rnd = new Random(1181);
        for (int k = 0; k < maxId; k++)
        {
            int id = k + 1, x = k % 12 * 2 - 12, z = k / 12 * 2 - 12, meta = rnd.Next(16);
            w.SetBlock(x, 64, z, id, meta);
            if (id is Blocks.WoodenDoor or Blocks.IronDoor) w.SetBlock(x, 65, z, id, meta | 8);
            if (id is 175) w.SetBlock(x, 65, z, id, 8);
        }
        // Connector rows at z = 14..22.
        for (int x = -10; x <= -4; x++) { w.SetBlock(x, 64, 14, Blocks.Fence, 0); w.SetBlock(x, 64, 16, Blocks.GlassPane, 0); w.SetBlock(x, 64, 18, Blocks.RedstoneWire, x & 15); }
        w.SetBlock(-7, 64, 15, Blocks.Fence, 0); w.SetBlock(-6, 64, 14, Blocks.FenceGate, 1); w.SetBlock(-3, 64, 16, 101, 0);
        w.SetBlock(-3, 64, 18, Blocks.RedstoneTorch, 5); w.SetBlock(-11, 64, 18, Blocks.RepeaterOff, 1); w.SetBlock(-7, 65, 19, Blocks.RedstoneWire, 3); w.SetBlock(-7, 64, 19, Blocks.Stone, 0);
        w.SetBlock(0, 64, 14, Blocks.WoodenStairs, 0); w.SetBlock(1, 64, 14, Blocks.WoodenStairs, 3); w.SetBlock(0, 64, 15, Blocks.CobblestoneStairs, 2);
        w.SetBlock(-1, 64, 15, Blocks.CobblestoneStairs, 1); w.SetBlock(1, 65, 15, Blocks.BrickStairs, 4); w.SetBlock(2, 64, 15, Blocks.StoneBrickStairs, 6);
        w.SetBlock(4, 64, 14, Blocks.Chest, 0); w.SetBlock(5, 64, 14, Blocks.Chest, 0); w.SetBlock(4, 64, 16, Blocks.Chest, 0); w.SetBlock(4, 64, 17, Blocks.Chest, 0);
        w.SetBlock(5, 64, 13, Blocks.Stone, 0); w.SetBlock(7, 64, 14, Blocks.Chest, 3); w.SetBlock(7, 64, 13, Blocks.Cobblestone, 0);
        w.SetBlock(9, 64, 14, Blocks.WoodenDoor, 1); w.SetBlock(9, 65, 14, Blocks.WoodenDoor, 8); w.SetBlock(10, 64, 14, Blocks.WoodenDoor, 4 | 2); w.SetBlock(10, 65, 14, Blocks.WoodenDoor, 9);
        for (int i = 0; i < 6; i++) { w.SetBlock(12 + i, 64, 14, Blocks.Rail, i); w.SetBlock(12 + i, 64, 16, Blocks.Rail, 6 + i % 4); w.SetBlock(12 + i, 64, 18, Blocks.PoweredRail, i | (i & 1) << 3); }
        // Pools and flows at z = 20..26: still water, a falling column, flowing levels, lava, ice.
        for (int x = -12; x <= -6; x++) for (int z = 20; z <= 24; z++) w.SetBlock(x, 63, z, Blocks.Water, 0);
        for (int i = 0; i < 8; i++) { w.SetBlock(-5 + i, 64, 22, Blocks.FlowingWater, i); w.SetBlock(-5 + i, 64, 24, Blocks.FlowingLava, i * 2 % 8); }
        w.SetBlock(-2, 66, 26, Blocks.Water, 0); w.SetBlock(-2, 65, 26, Blocks.FlowingWater, 8); w.SetBlock(-2, 64, 26, Blocks.FlowingWater, 8);
        for (int x = 4; x <= 8; x++) for (int z = 20; z <= 24; z++) w.SetBlock(x, 63, z, (x + z) % 3 == 0 ? Blocks.Ice : Blocks.Lava, 0);
        w.SetBlock(6, 64, 22, Blocks.Fire, 0); w.SetBlock(7, 64, 26, Blocks.Glowstone, 0); w.SetBlock(8, 64, 26, Blocks.Torch, 5);
        // A roofed bunker (cut-away, x-ray): walls 14..20 × 20..26, roof at y 67; the body stands inside.
        for (int x = 14; x <= 20; x++)
            for (int z = 20; z <= 26; z++)
                for (int y = 64; y <= 67; y++)
                    if (y == 67 || x is 14 or 20 || z is 20 or 26) w.SetBlock(x, y, z, y == 67 ? Blocks.Planks : Blocks.Cobblestone, 0);
        w.SetBlock(17, 64, 23, Blocks.Workbench, 0); w.SetBlock(15, 64, 21, Blocks.Furnace, 3); w.SetBlock(16, 64, 21, Blocks.LitFurnace, 2);
        // A tunnel under a hill (x-ray from outside): stone 20..24 × -10..-2, y 64..70, a 1×2 tunnel along z at x 22.
        for (int x = 18; x <= 24; x++) for (int z = -10; z <= -2; z++) for (int y = 64; y <= 70; y++) w.SetBlock(x, y, z, x == 22 && y <= 65 ? 0 : Blocks.Stone, 0);
        return w;
    }

    private static RoleMap Roles() => (x, y, z) => ((x * 31 + z * 17 + y * 5) & 63) is var r && r < 19 && y >= 63 && y <= 66 ? (BlockRole)r : BlockRole.None;

    private static readonly (int X, int Y, int Z) Focus = (0, 64, 2), Bunker = (17, 64, 23), Tunnel = (22, 64, -6);

    private static EyeScene Look(IVoxelWorld w, double ex, double ey, double ez, double tx, double ty, double tz, TextureAtlas? atlas, Action<EyeSceneBuilder>? tweak = null)
    {
        var (yaw, pitch) = EyeScene.LookAt(ex, ey, ez, tx, ty, tz);
        var b = new EyeSceneBuilder { World = w, Atlas = atlas, X = ex, Y = ey, Z = ez, Yaw = yaw, Pitch = pitch };
        tweak?.Invoke(b);
        return b.Build();
    }

    /// <summary>Mutable stand-in for the init-only <see cref="EyeScene"/>.</summary>
    private sealed class EyeSceneBuilder
    {
        public required IVoxelWorld World;
        public TextureAtlas? Atlas;
        public double X, Y, Z;
        public float Yaw, Pitch;
        public long Time = 6000;
        public bool Nether, Smooth;
        public IReadOnlyList<VisionEntity> Entities = [];
        public IReadOnlyList<VisionMark> Marks = [];
        public (int X, int Y, int Z)? Target, Pointed;
        public string? Viewer;
        public float? Fov;
        public int? MaxDistance;
        public CutAway? Cut;
        public ViewCut? XRay;
        public Weather Weather;
        public PrecipStyle Precip;
        public float PrecipDensity = 1f;
        public long PrecipTick;
        public float? MinBrightness;
        public double Ortho;
        public ClipBox? Clip;

        public EyeScene Build() => new()
        {
            World = World, Atlas = Atlas, EyeX = X, EyeY = Y, EyeZ = Z, Yaw = Yaw, Pitch = Pitch, Time = Time, Nether = Nether, Smooth = Smooth,
            Entities = Entities, Marks = Marks, Target = Target, Pointed = Pointed, Viewer = Viewer, Fov = Fov, MaxDistance = MaxDistance, Cut = Cut,
            XRay = XRay, Weather = Weather, Precip = Precip, PrecipDensity = PrecipDensity, PrecipTick = PrecipTick, MinBrightness = MinBrightness,
            Ortho = Ortho, Clip = Clip,
        };
    }

    /// <summary>Eye renders of one world: day, night, smooth, weather, ortho + clip, cut-away, x-ray, entities, marks.</summary>
    private static IEnumerable<(string Name, EyeScene Scene)> EyeScenes(IVoxelWorld w, TextureAtlas? atlas, bool nether)
    {
        var zoo = Zoo(-6.5, -9.5);
        VisionMark[] marks = [new("gen", 2, 64, 2, 2, 64, 2, true), new("cobble generator area", -4, 63, -4, 0, 65, 0), new("far", 40, 64, 40, 40, 64, 40)];
        yield return ("overview", Look(w, -20, 80, 30, 0, 64, 0, atlas, b => b.Nether = nether));
        yield return ("museum-low", Look(w, -14.5, 66.2, -14.5, 4, 64, 4, atlas, b => { b.Nether = nether; b.Fov = 80; }));
        yield return ("night-smooth", Look(w, 8, 72, 30, 0, 64, 18, atlas, b => { b.Time = 18000; b.Smooth = true; b.Nether = nether; }));
        yield return ("dusk", Look(w, -16, 70, 28, -6, 63, 22, atlas, b => { b.Time = 12500; b.Nether = nether; b.MinBrightness = 0.3f; }));
        yield return ("rain-streaks", Look(w, 0, 70, 34, 0, 64, 16, atlas, b => { b.Weather = new Weather(1, 0); b.PrecipTick = 37; b.Nether = nether; }));
        yield return ("storm-tint", Look(w, 0, 90, 2, 0, 64, 1.9, atlas, b => { b.Weather = new Weather(0.8f, 1); b.Precip = PrecipStyle.Tint; b.Ortho = 14; b.Nether = nether; }));
        yield return ("light-rain-sparse", Look(w, 20, 75, 40, 10, 64, 18, atlas, b => { b.Weather = new Weather(0.3f, 0); b.PrecipDensity = 0.4f; b.Precip = PrecipStyle.Streaks; b.Nether = nether; }));
        yield return ("top-ortho-clip", Look(w, 0.5, 72, 2.5, 0.5, 64, 2.49, atlas, b => { b.Ortho = 10; b.Clip = new ClipBox(-10, 0, -8, 10, 65, 12); b.Nether = nether; }));
        yield return ("side-ortho", Look(w, 0.5, 64.5, 30, 0.5, 64.5, 0, atlas, b => { b.Ortho = 8; b.Clip = new ClipBox(-12, 58, 10, 12, 70, 16); b.Nether = nether; }));
        var cut = new CutAway(10, 60, 16, 16, 12, 16);
        for (int x = 14; x <= 20; x++) for (int z = 20; z <= 26; z++) cut.Hide(x, 67, z);
        yield return ("bunker-cut", Look(w, 26, 76, 32, 17, 64, 23, atlas, b => { b.Cut = cut; b.Nether = nether; b.Entities = [new VisionEntity("self", "you", 17.5, 64, 23.5, '@') { Yaw = 45 }]; }));
        var xray = ViewCut.For(w, 26, 76, 32, (17.5, 64, 23.5), (17, 64, 23), 2.5, 64);
        yield return ("bunker-xray", Look(w, 26, 76, 32, 17, 64, 23, atlas, b => { b.XRay = xray; b.Nether = nether; }));
        var xray2 = ViewCut.For(w, 30, 72, -6, (22.5, 64, -5.5), (22, 64, -6), 2.5, 64, force: true);
        yield return ("tunnel-xray", Look(w, 30, 72, -6, 22.5, 64.5, -5.5, atlas, b => { b.XRay = xray2; b.Nether = nether; b.Entities = [new VisionEntity("hive", "bob", 22.5, 64, -5.5, 'b')]; }));
        yield return ("zoo", Look(w, -3, 69, 4, -2, 64, -6, atlas, b => { b.Entities = zoo; b.Nether = nether; b.Fov = 75; }));
        yield return ("zoo-close", Look(w, -9, 65.6, -11, -6, 64.5, -9.5, atlas, b => { b.Entities = zoo; b.Nether = nether; b.Time = 13000; }));
        yield return ("marks", Look(w, 6, 70, 10, 1, 64, 1, atlas, b =>
        {
            b.Marks = marks; b.Target = (2, 64, 2); b.Pointed = (-1, 64, 0); b.Viewer = "alice"; b.Entities = zoo; b.Nether = nether; b.MaxDistance = 40;
        }));
    }

    private static List<(string Name, string Dump)> RenderEyes(IVoxelWorld w, TextureAtlas? atlas, bool nether, string prefix)
    {
        var rows = new List<(string, string)>();
        var eye = new EyeRenderer { Width = 192, Height = 144, MaxThreads = 2 };
        foreach (var (name, s) in EyeScenes(w, atlas, nether))
        {
            var img = eye.Render(s);
            var fr = eye.Raster(s);
            var cells = fr.Cells is { } c ? Hash(MemoryMarshal.AsBytes(c.AsSpan()).ToArray()) : "-";
            var depth = Hash(MemoryMarshal.AsBytes(fr.Depth.AsSpan()).ToArray());
            bool streaks = s.Weather.Wet && !s.Nether && s.Precip is PrecipStyle.Auto or PrecipStyle.Streaks && s.Ortho == 0; // see Image()
            rows.Add(($"{prefix}{name}", $"{img.Width}x{img.Height} {(streaks ? "-" : Hash(img.Png))} {depth} {cells}\n{Ms.Replace(img.Legend, "Nms")}\n{string.Join("|", img.Labels)}\n{eye.CrosshairBlock(s)}"));
        }
        return rows;
    }

    /// <summary>A picture's dump. A sheet's cache key and blocks hash mix in <see cref="HashCode"/> (seeded per process), and so do
    /// the rain streaks' positions: <paramref name="streaks"/> pictures pin their text only.</summary>
    private static string Image(VisionImage img, bool streaks = false) =>
        $"{img.Width}x{img.Height} {(streaks ? "-" : Hash(img.Png))} {img.XRay} {img.Weather}\n{img.Legend}\n{string.Join("|", img.Labels)}";

    private static List<(string Name, string Dump)> RenderSheets(WorldState world, TextureAtlas? atlas, string prefix)
    {
        var rows = new List<(string, string)>();
        var light = new WorldVoxels(world);
        var sheets = new ContactSheet { EyeThreads = 2 };
        var zoo = Zoo(-6.5, -9.5);
        bool nether = world.Dimension == -1;
        var eyeAt = new EyeScene { World = light, Atlas = atlas, EyeX = 0.5, EyeY = 65.62, EyeZ = 8.5, Yaw = 180, Pitch = 25, Entities = zoo, Nether = nether };
        ContactSheetRequest Req(IReadOnlyList<string> views, Action<ContactSheetRequestBuilder>? tweak = null)
        {
            var b = new ContactSheetRequestBuilder { Blocks = world.PlanningView, Light = light, Atlas = atlas, Focus = Focus, Views = views, Nether = nether, Entities = zoo,
                Self = (0, 64, 8), Eye = eyeAt, Roles = Roles() };
            tweak?.Invoke(b);
            return b.Build();
        }
        rows.Add(($"{prefix}default", Image(sheets.Render(Req(ContactSheet.DefaultViews)))));
        rows.Add(($"{prefix}flat", Image(sheets.Render(Req(["top", "side", "layers", "iso"], b => { b.Axis = SideAxis.LookEast; b.Radius = 9; })))));
        rows.Add(($"{prefix}orbits", Image(sheets.Render(Req(["orbit", "orbit2", "orbit3", "orbit4", "high", "shoulder"], b =>
        {
            b.Marks = [new("gen", 2, 64, 2, 2, 64, 2, true)]; b.Target = (2, 64, 2); b.Pointed = (1, 64, 1); b.PointedBy = "fox"; b.Spots = [(3, 64, 3), (-4, 63, 0)];
        })))));
        rows.Add(($"{prefix}night-rain", Image(sheets.Render(Req(["eye", "orbit", "top"], b => { b.Time = 18000; b.Weather = new Weather(1, 1); b.Stamp = "seen 3 min ago"; })), streaks: !nether)));
        rows.Add(($"{prefix}night-storm-top", Image(sheets.Render(Req(["top", "layers"], b => { b.Time = 18000; b.Weather = new Weather(1, 1); })))));
        rows.Add(($"{prefix}noaids-nocut", Image(sheets.Render(Req(["orbit", "top", "side"], b => { b.Aids = false; b.CutAway = false; b.Roles = null; b.Self = null; b.Eye = null; })))));
        rows.Add(($"{prefix}bunker-xray", Image(sheets.Render(Req(["orbit", "high", "iso"], b =>
        {
            b.Focus = Bunker; b.Self = Bunker; b.Eye = null; b.XRay = XRay.On; b.SelfPose = new PerspectivePanel.Body(17.5, 64, 23.5, 90, 10, "you");
        })))));
        rows.Add(($"{prefix}tunnel-auto", Image(sheets.Render(Req(["orbit", "orbit2", "high", "shoulder"], b =>
        {
            b.Focus = Tunnel; b.Self = Tunnel; b.Eye = null; b.Radius = 4; b.SelfPose = new PerspectivePanel.Body(22.5, 64, -5.5, 180, 0, "you");
        })))));
        rows.Add(($"{prefix}params", Image(sheets.Render(Req(["iso", "top", "side", "orbit"], b =>
        {
            b.ViewParams = new Dictionary<string, CamParams>
            {
                ["iso"] = new(Yaw: 200, Pitch: 50, Zoom: 1.3), ["top"] = new(Zoom: 2, Dx: 3, Dz: -2), ["side"] = new(Axis: "east", Zoom: 0.8), ["orbit"] = new(Dist: 30, Dy: 4),
            };
            b.PixelBudget = 150_000;
        })))));
        // A composite of two spots and a free camera.
        var ctxA = new PanelContext { Blocks = world.PlanningView, Light = light, Atlas = atlas, Focus = Focus, FeetY = 64, Radius = 6, Entities = zoo, Nether = nether, Roles = Roles() };
        var ctxB = new PanelContext { Blocks = world.PlanningView, Light = light, Atlas = atlas, Focus = Bunker, FeetY = 64, Radius = 5, Nether = nether, Staleness = "seen 2 h ago", Smooth = true };
        var jobs = new List<PanelJob>
        {
            new() { Ctx = ctxA, Rig = PerspectivePanel.Cam(Focus, 64, "NW", 18, "CAM NW", 6) },
            new() { Ctx = ctxB, Rig = PerspectivePanel.Cam(Bunker, 64, "above", null, "CAM above", 5) },
            new() { Ctx = ctxB, Flat = "top" },
            new() { Ctx = ctxA, Flat = "layers" },
        };
        rows.Add(($"{prefix}composite", Image(sheets.Compose(jobs, ContactSheet.DefaultPixelBudget, "composite test", ["extra one.", "extra two."], (0, 64, 8)))));
        return rows;
    }

    private sealed class ContactSheetRequestBuilder
    {
        public required IBlockAccess Blocks;
        public IVoxelWorld? Light;
        public TextureAtlas? Atlas;
        public (int X, int Y, int Z) Focus;
        public int Radius = 6;
        public IReadOnlyList<string> Views = ContactSheet.DefaultViews;
        public (int X, int Y, int Z)? Self, Target, Pointed;
        public PerspectivePanel.Body? SelfPose;
        public IReadOnlyList<VisionEntity> Entities = [];
        public IReadOnlyList<VisionMark> Marks = [];
        public string? PointedBy, Stamp;
        public bool Nether, CutAway = true, Aids = true;
        public long Time = 6000;
        public RoleMap? Roles;
        public SideAxis Axis;
        public EyeScene? Eye;
        public int PixelBudget = ContactSheet.DefaultPixelBudget;
        public IReadOnlyList<(int X, int Y, int Z)> Spots = [];
        public IReadOnlyDictionary<string, CamParams>? ViewParams;
        public XRay XRay;
        public Weather Weather;

        public ContactSheetRequest Build() => new()
        {
            Blocks = Blocks, Light = Light, Atlas = Atlas, Focus = Focus, Radius = Radius, Views = Views, Self = Self, Target = Target, Pointed = Pointed,
            SelfPose = SelfPose, Entities = Entities, Marks = Marks, PointedBy = PointedBy, Stamp = Stamp, Nether = Nether, CutAway = CutAway, Aids = Aids,
            Time = Time, Roles = Roles, Axis = Axis, Eye = Eye, PixelBudget = PixelBudget, Spots = Spots, ViewParams = ViewParams, XRay = XRay, Weather = Weather,
        };
    }

    private static List<(string Name, string Dump)> RenderMaps(WorldState world, TextureAtlas? atlas, string prefix)
    {
        var rows = new List<(string, string)>();
        var renderer = new VisionRenderer { CacheCapacity = 0 };
        var light = new WorldVoxels(world);
        var zoo = Zoo(-6.5, -9.5);
        foreach (VisionView v in Enum.GetValues<VisionView>())
            foreach (var (r, cut) in new[] { (12, false), (5, true) })
            {
                var img = renderer.Render(new VisionScene
                {
                    View = v, Radius = r, Cx = Focus.X, Cy = Focus.Y, Cz = Focus.Z, Blocks = world.PlanningView, Self = (0, 64, 8), Entities = zoo,
                    Marks = [new("gen", 2, 64, 2, 2, 64, 2, true)], Target = (2, 64, 2), Path = [(0, 64, 7), (0, 64, 6), (1, 64, 5)], Pointed = (1, 64, 1),
                    PointedBy = "fox", Rotation = r % 4, Nether = world.Dimension == -1, Atlas = atlas, Light = light, Roles = Roles(), Focus = Focus,
                    Axis = cut ? SideAxis.LookEast : SideAxis.LookNorth, CutAway = cut, Time = cut ? 18000 : 6000, Weather = cut ? new Weather(1, 0) : default,
                });
                rows.Add(($"{prefix}{v}-r{r}", Image(img) + $"\n{img.Key} {img.BlocksHash}"));
            }
        return rows;
    }

    private static List<(string Name, string Dump)> RenderText(WorldState world, string prefix)
    {
        var rows = new List<(string, string)>();
        var zoo = Zoo(-6.5, -9.5);
        var sb = new StringBuilder();
        foreach (var size in new[] { 5, 15, 25 })
            sb.Append(AsciiMap.Render(world.PlanningView, Focus.X, Focus.Y, Focus.Z, size, (0, 64, 8), zoo, [new("gen", 2, 64, 2, 2, 64, 2, true)], size == 15 ? Roles() : null)).Append("\n----\n");
        rows.Add(($"{prefix}ascii", sb.ToString()));
        sb.Clear();
        foreach (var feet in new[] { new Vec3d(0.5, 64, 2.5), new Vec3d(17.5, 64, 23.5), new Vec3d(22.5, 64, -5.5) })
        {
            var grid = ViewportBuilder.Build(world, feet, new ViewportOptions { Radius = 10 }, "fox");
            sb.Append(System.Text.Json.JsonSerializer.Serialize(grid)).Append('\n');
        }
        rows.Add(($"{prefix}viewport", sb.ToString()));
        sb.Clear();
        // The TUI iso: every zoom × rotation × colour mode × unicode, cut-away and labels, markers, plus the pixel buffer.
        var vol = VoxelBuilder.Build(world, new Vec3d(0.5, 64, 2.5), new ViewportOptions { VoxelRadius = 10 }, 7, [2, 64, 2], [0, 64, 7, 0, 64, 6, 1, 64, 5]);
        ViewMarker[] markers = [new("player", "fox", 2, 0, -3), new("mob", "zombie", -3, 0, 4), new("hive", "alice", 1, 1, 1), new("item", null, 0, 0, 2), new("mob", "creeper", 4, -2, 4)];
        var iso = new IsoRenderer();
        foreach (IsoColours c in Enum.GetValues<IsoColours>())
            for (int zoom = 0; zoom <= IsoView.MaxZoom; zoom++)
                for (int rot = 0; rot < 4; rot++)
                {
                    var view = new IsoView { Zoom = zoom, Rotation = rot, Cutaway = rot % 2 == 1, Labels = zoom == 1 };
                    var input = new IsoInput(vol, markers, view, c, Unicode: (rot + zoom) % 2 == 0, Nether: world.Dimension == -1,
                        HiveGlyph: n => n == "alice" ? 'a' : null, NameColour: n => (uint)n.Length * 0x101010u);
                    foreach (var (wd, ht) in new[] { (80, 30), (50, 16), (36, 10) })
                    {
                        var f = iso.Render(input, wd, ht);
                        sb.Append(c).Append(zoom).Append(rot).Append(wd).Append(' ').Append(f.Zoom).Append(f.Rotation).Append(f.CutY).Append(' ').Append(f.BlocksDrawn).Append(' ');
                        foreach (var cell in f.Cells) sb.Append(cell.Ch).Append((char)(cell.Fg & 0xffff)).Append((char)(cell.Fg >> 16)).Append((char)(cell.Bg & 0xffff)).Append((char)(cell.Bg >> 16)).Append((char)cell.Flags);
                        foreach (var m in f.Marks) sb.Append(m).Append(';');
                        sb.Append('\n');
                    }
                }
        rows.Add(($"{prefix}iso-tui", sb.ToString()));
        sb.Clear();
        for (int zoom = 0; zoom <= IsoView.MaxZoom; zoom++)
            for (int rot = 0; rot < 4; rot++)
            {
                var input = new IsoInput(vol, markers, new IsoView { Zoom = zoom, Rotation = rot, Cutaway = rot == 2 }, IsoColours.TrueColor, true, world.Dimension == -1);
                var s = IsoProjection.Sprite(zoom);
                var px = iso.RenderPixels(input, s, 8, 160, 120, 80, 50, out int drawn);
                sb.Append(zoom).Append(rot).Append(' ').Append(drawn).Append(' ').Append(Hash(px)).Append(' ').Append(iso.PixelAnchor(2, 0, -3, rot, s, 80, 50, 160, 120)).Append('\n');
                foreach (var m in markers) sb.Append(IsoRenderer.MarkerLook(m, input)).Append(' ');
                sb.Append('\n');
            }
        rows.Add(($"{prefix}iso-pixels", sb.ToString()));
        return rows;
    }

    // ------------------------------------------------------------------ pictures

    [Fact]
    public void Pictures_without_textures_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>();
        var world = World();
        rows.AddRange(RenderEyes(new WorldVoxels(world), null, false, "eye-flat-"));
        rows.AddRange(RenderSheets(world, null, "sheet-flat-"));
        rows.AddRange(RenderMaps(world, null, "map-flat-"));
        rows.AddRange(RenderText(world, "text-"));
        var nether = World(dim: -1, maxId: 96);
        rows.AddRange(RenderEyes(new WorldVoxels(nether), null, true, "eye-nether-").Take(4));
        rows.AddRange(RenderSheets(nether, null, "sheet-nether-").Take(2));
        rows.AddRange(RenderText(nether, "text-nether-").Skip(2));
        foreach (var c in VisionEval.Cases(null)) rows.Add(($"eval-{c.Id}", Image(VisionEval.Sheet(c))));
        Compare("flat", rows, FlatGolden);
    }

    [Fact]
    public void Pictures_with_synthetic_textures_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>();
        var atlas = Vision3DTests.Atlas();
        var world = World();
        rows.AddRange(RenderEyes(new WorldVoxels(world), atlas, false, "eye-tex-"));
        rows.AddRange(RenderSheets(world, atlas, "sheet-tex-"));
        rows.AddRange(RenderMaps(world, atlas, "map-tex-"));
        // Snow: a cold biome turns the rain into flakes.
        var cold = World(biome: 12);
        rows.AddRange(RenderEyes(new WorldVoxels(cold), atlas, false, "eye-snow-").Where(r => r.Name.Contains("rain") || r.Name.Contains("storm")));
        rows.AddRange(RenderSheets(cold, atlas, "sheet-snow-").Where(r => r.Name.Contains("night-rain")));
        Compare("synthetic", rows, SyntheticGolden);
    }

    private static readonly Lazy<TextureAtlas?> Beta = new(() => TextureLocator.Load(new TextureOptions { Version = "b1.8.1", AllowDownload = false }));

    [Fact]
    public void Pictures_with_the_b181_jar_match_the_golden_hashes()
    {
        if (Beta.Value is not { } atlas) { output.WriteLine("no b1.8.1 textures on this machine: skipped"); return; }
        var rows = new List<(string, string)>();
        var world = World();
        rows.AddRange(RenderEyes(new WorldVoxels(world), atlas, false, "eye-beta-"));
        rows.AddRange(RenderSheets(world, atlas, "sheet-beta-").Take(4));
        Compare("beta", rows, BetaGolden);
    }

    [Jar1122Fact]
    public void Models_and_pictures_with_the_1122_jar_match_the_golden_hashes()
    {
        var models = Jar1122.Models;
        var atlas = Jar1122.Atlas;
        var rows = new List<(string, string)>();
        var sb = new StringBuilder();
        var quads = new List<GeoQuad>();
        for (int id = 0; id <= 255; id++)
            for (int meta = 0; meta <= 15; meta++)
            {
                sb.Append(id).Append(':').Append(meta).Append(' ').Append(models.Classify(id, meta)).Append(models.Emissive(id)).Append(' ').Append(models.StateOf(id, meta).CacheKey);
                for (int k = 0; k < Hoods; k++)
                {
                    var w = Hood(id, meta, k);
                    quads.Clear();
                    sb.Append(" | ").Append(Try(() => { BlockMesh.Quads(models, w, 0, 64, 0, quads); return quads.Count.ToString(CultureInfo.InvariantCulture); })).Append(' ');
                    foreach (var q in quads) Quad(sb, q);
                }
                sb.Append('\n');
            }
        rows.Add(("models", sb.ToString()));
        // A 1.12.2 museum (ids 1..255) in a sparse world.
        var v = new SparseVoxels { WorldHeight = 256 };
        for (int x = -24; x <= 24; x++) for (int z = -24; z <= 30; z++) v.Set(x, 63, z, 1, (x + z) & 7);
        var rnd = new Random(1181);
        for (int k = 0; k < 255; k++)
        {
            int id = k + 1, x = k % 16 * 2 - 16, z = k / 16 * 2 - 16, meta = rnd.Next(16);
            v.Set(x, 64, z, id, meta);
            if (BlockStates1122.IsDoor(id) || id == 175) v.Set(x, 65, z, id, 8 | rnd.Next(4));
        }
        rows.AddRange(RenderEyes(v, atlas, false, "eye-1122-"));
        Compare("1122", rows, Release1122Golden);
    }

    // ------------------------------------------------------------------ goldens (captured from the code before the WP08 refactor)

    private const string LookupGolden = """
        BlockTextures.For 6614235529F1E9E2C2E4
        BlockTextures.flags C0264BB5BDF85EFDAD29
        BlockStates1122.Resolve 187AF4B48483531C0FE1
        BlockStates1122.helpers 36D065A0EBCCF745E3BB
        BlockGeometry.classes 882063E335CA11185112
        BlockGeometry.Build C41DFF08D9F87CD18228
        BlockGeometry.ChestTile F26F2A3DF1A8DF39F17C
        EntityGeometry.kinds 4C9FCA290EFA05FD5AC4
        EntityGeometry.Build 3225F0EB185A9CF46D11
        EntityTextures F553361FA6CABC545F56
        ItemIcons D162B57F75A83DEC9487
        BlockRoles ACDB55C8CB9CC615F8C3
        IsoPalette 824F8F8AA7A60982EB09
        Weather 4A20842145C7BFCA6A78
        IsoRenderer.Shade 6A1286F0DD52D2ECBF41
        AsciiMap.Cell 3F66EDF5A3107B116B79
        """;

    private const string FlatGolden = """
        eye-flat-overview EDBD7215EBFAD3DB4D46
        eye-flat-museum-low EC414E09DEE724526DFB
        eye-flat-night-smooth 1E6809369DE774B1D549
        eye-flat-dusk 3C05D042FC8CD246A06D
        eye-flat-rain-streaks 7919F2DEB45993A6FAE9
        eye-flat-storm-tint 915892EF6848234FFF52
        eye-flat-light-rain-sparse A01153CF60081EAC95FF
        eye-flat-top-ortho-clip 9602082F6B31CF955F68
        eye-flat-side-ortho 0576C08C192D21843CBA
        eye-flat-bunker-cut A1D7DA3942B87AC5D5C5
        eye-flat-bunker-xray 8544AD1496478EDF438A
        eye-flat-tunnel-xray 5071F382A6A2B3C4DF17
        eye-flat-zoo DBDE27A0ED202D4B922D
        eye-flat-zoo-close 1B2ED12411378B8D3202
        eye-flat-marks D76BDFCD4518A9FF4467
        sheet-flat-default 1D06AF6E54F868BC6FEC
        sheet-flat-flat 45A3FE9071D0245BB7AD
        sheet-flat-orbits 96AF41C62F5A89D29AA4
        sheet-flat-night-rain EFB62495F3504B01AC80
        sheet-flat-night-storm-top 1450683F2BD132DBE6E5
        sheet-flat-noaids-nocut 0EF73A4313BB8EE8D25A
        sheet-flat-bunker-xray 8C350F11512F75B001C8
        sheet-flat-tunnel-auto 1F33F63BD93239ABBC86
        sheet-flat-params 117A48C2958B47A6B936
        sheet-flat-composite 1B7D857EA651057B1BA7
        map-flat-Top-r12 96102917C54D380DF123
        map-flat-Top-r5 431B64D87BCABA1E772F
        map-flat-Side-r12 AEC091A1FA77F1BE8D75
        map-flat-Side-r5 09AC0DEE7A17CB776DF2
        map-flat-Iso-r12 8E3FDA052933D2FA92B5
        map-flat-Iso-r5 4B5C93233134EA4C0C93
        map-flat-Layers-r12 AC1DF4570FF2A812A40C
        map-flat-Layers-r5 8CCD5963BFD25160BF31
        text-ascii 26E70FC5713E591FC852
        text-viewport 5B90860BEA419C524A59
        text-iso-tui 800EEE14AF769E54BDD8
        text-iso-pixels 0E8D40C63B45B1D69D20
        eye-nether-overview 1DBF8594D72D9549EFB8
        eye-nether-museum-low 98CE2708C76073A4EA8A
        eye-nether-night-smooth 3591A47B38C282C1FA2B
        eye-nether-dusk 01D53C0839949E4E5E45
        sheet-nether-default 0A87DB58F9C9D95C7AD7
        sheet-nether-flat 60143D5A1348A958FA22
        text-nether-iso-tui C7B8E2178A72B7F53FC5
        text-nether-iso-pixels 7913D4E2ABEE43DB585D
        eval-floor_vs_gen 3E162CB3F6AE0FAE7558
        eval-hole_faces 8C5F01CC9428F1E45749
        eval-edge_distance 4973505A89D30B6673D1
        eval-middle_one 7AB5E83A70D25B08F10E
        """;

    private const string SyntheticGolden = """
        eye-tex-overview 14CD25F2788F8EACAE0E
        eye-tex-museum-low F02A884C9E92D5423F23
        eye-tex-night-smooth FF21ACD11A568BABFA5C
        eye-tex-dusk 4FEDD1646805DB30E8A8
        eye-tex-rain-streaks F16D32E5D4C872C3337F
        eye-tex-storm-tint 0AF71B26824DB573BA20
        eye-tex-light-rain-sparse 5043250D0138C08CCF58
        eye-tex-top-ortho-clip 9CBF421F684AA92CF312
        eye-tex-side-ortho 9039D2CA1BB5F5AAE087
        eye-tex-bunker-cut 795F82E5A3E985CF9736
        eye-tex-bunker-xray 19915CEB8B2FE08F7B7E
        eye-tex-tunnel-xray 0D97A6F7BA4766653F1E
        eye-tex-zoo C71B8738583E51FAEA46
        eye-tex-zoo-close C2BBE700B47E29A0DAB0
        eye-tex-marks 1CE1436622CA47004B0C
        sheet-tex-default BDAB6188D41E829A64EA
        sheet-tex-flat 2E8CFCE6423CF1827D54
        sheet-tex-orbits E91C6FBECC07E7AB835D
        sheet-tex-night-rain EFB62495F3504B01AC80
        sheet-tex-night-storm-top BD1AE2A4B4E8536EBE30
        sheet-tex-noaids-nocut 592D7E8A72A774C968F6
        sheet-tex-bunker-xray 07646086054D4927C724
        sheet-tex-tunnel-auto E6A40A797C6890946B2E
        sheet-tex-params 43E3924FF7AEEEA4A8E0
        sheet-tex-composite 1302A685993D09436102
        map-tex-Top-r12 965EBBD35FE754BEC376
        map-tex-Top-r5 1671B19404F9954904A3
        map-tex-Side-r12 4B796A8143EE2EF84476
        map-tex-Side-r5 317FB3DF690FB3A67982
        map-tex-Iso-r12 26DEB9F7DBB991117BBD
        map-tex-Iso-r5 D3AE6257CAF131AC8C62
        map-tex-Layers-r12 FE889B71F875EC04E8A9
        map-tex-Layers-r5 E7EA35775F187BD66160
        eye-snow-rain-streaks F16D32E5D4C872C3337F
        eye-snow-storm-tint 0AF71B26824DB573BA20
        eye-snow-light-rain-sparse 5043250D0138C08CCF58
        sheet-snow-night-rain C2FD344FD05C21DB456A
        """;

    private const string BetaGolden = """
        eye-beta-overview 84F0FCFCABD41BD2F3C8
        eye-beta-museum-low CD25EA76788141F4FF59
        eye-beta-night-smooth 10C3C1C11EE3031F5C70
        eye-beta-dusk 0CF0C28A714E7A9EDD68
        eye-beta-rain-streaks 04ACD2AA0093712EB3D6
        eye-beta-storm-tint 3135DDA86C7F16031AAF
        eye-beta-light-rain-sparse FA8B69D37AB5239B0875
        eye-beta-top-ortho-clip 6AC0399EA9C3DD435E9A
        eye-beta-side-ortho F5E7B74B2CBF4A0FFE05
        eye-beta-bunker-cut 92E46DF20842D0E8D9F5
        eye-beta-bunker-xray 326226AA3A4A2E178BAD
        eye-beta-tunnel-xray 12124D4BF37447422E01
        eye-beta-zoo 1153B1CC7D2C14A29099
        eye-beta-zoo-close 0E5E0CA6C05A50DE9C1F
        eye-beta-marks 24C3B0D233CA4BD591E3
        sheet-beta-default 7025D6970363CE793A48
        sheet-beta-flat A8CDF3066470AFF62910
        sheet-beta-orbits A50E873217513D12807D
        sheet-beta-night-rain EFB62495F3504B01AC80
        """;

    private const string Release1122Golden = """
        models 80482C4D443F8B66B395
        eye-1122-overview 0CE7E4CCA355D4173995
        eye-1122-museum-low F63AE4D7E72CA39BE0D8
        eye-1122-night-smooth 2A1B25E05BBC8DE0FDF8
        eye-1122-dusk FC0AFB2D08C34FEEAD58
        eye-1122-rain-streaks 5D05CC4D360030062E4F
        eye-1122-storm-tint 7D109896F233873ED19C
        eye-1122-light-rain-sparse 84BA8C4663FAC537386D
        eye-1122-top-ortho-clip 688B7A324DC47B4D6243
        eye-1122-side-ortho 0B826080C30FBEE0ADD1
        eye-1122-bunker-cut 7A9467307675A5AA4F0D
        eye-1122-bunker-xray F96B6CD62A0B13CE1570
        eye-1122-tunnel-xray 9C360795A418E2DEDD59
        eye-1122-zoo 2786D4579BCAA3EBF688
        eye-1122-zoo-close B72D36BDD193AB5FC1F0
        eye-1122-marks 34CB0C893F4600DCE8BA
        """;
}
