using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Render;
using MineClaw.Render.Models;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Atlas;

/// <summary>
/// The block texture atlas the browser's world viewer draws with (<c>/v1/world/atlas.png</c> + <c>/v1/world/atlas.json</c>),
/// built at runtime from MineClaw.Render: the textures come from the local client jar / extracted PNGs (nothing Mojang is
/// committed or shipped), the procedural liquids and fire from FluidTextures, the cells laid out exactly like the explorer's
/// GPU atlas (16 columns: terrain rows 0-15, items 16-31, extras row 32, entity skins from row 33, then the 1.12.2 model
/// textures), and every block state's geometry from <see cref="BlockMesh"/> over a lone block (models for 1.12.2, the b1.8.1
/// shapes otherwise). JSON format: web/packages/world-viewer/src/atlas/types.ts (<c>AtlasMap</c>) — see ATLAS-OUTPUT.md.
/// Built once per version, cached with a content ETag.
/// </summary>
public sealed class AtlasService(BridgeConfig config, ILogger<AtlasService> log)
{
    public sealed record Built(string Version, byte[] Png, byte[] Json, string PngTag, string JsonTag, string Source);

    private readonly ConcurrentDictionary<string, Lazy<Built>> _cache = new(StringComparer.Ordinal);

    public Built Get(string version) => _cache.GetOrAdd(version, v => new Lazy<Built>(() => Build(v), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public TextureAtlas? Load(string version)
    {
        var assets = config.Bridge.AssetsDir is { Length: > 0 } a ? a : Path.Combine(config.DataDir, "assets");
        var o = new TextureOptions
        {
            AssetsDir = assets, AllowDownload = config.Bridge.AllowAssetDownload, SearchLauncherDirs = true, Version = version,
            ClientJar = version == Release1122Assets.Version ? config.Bridge.ModernJar ?? config.Raw["Render:Assets:Jar"] : null,
        };
        try { return TextureLocator.Load(o, m => log.LogInformation("atlas {Version}: {Message}", version, m)); }
        catch (Exception ex)
        {
            log.LogWarning("atlas {Version}: textures unavailable ({Message}); flat colours", version, ex.Message);
            return null;
        }
    }

    private Built Build(string version)
    {
        var atlas = Load(version);
        var (w, h, argb) = AtlasImage.Build(atlas);
        var png = Png.Encode(argb, w, h);
        using var _ = version == "1.12.2" ? GameData.Override(Release1122GameData.Instance) : null;
        var json = AtlasJson.Build(version, atlas, w, h).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        string Tag(byte[] b) => $"\"{version}-{Convert.ToHexStringLower(SHA256.HashData(b))[..24]}\"";
        log.LogInformation("atlas {Version}: {W}x{H} px, {Kb} KB json ({Source})", version, w, h, jsonBytes.Length / 1024, atlas?.Source is { Length: > 0 } ? "textures" : "flat colours");
        return new Built(version, png, jsonBytes, Tag(png), Tag(jsonBytes), atlas == null ? "none" : atlas.Models != null ? "1.12.2 client jar models" : "b1.8.1 terrain");
    }
}

/// <summary>The atlas image, cell for cell like the explorer's GpuAtlas (tools/MemoryExplorer/LiveHive/Gpu/GpuMesh.cs).</summary>
public static class AtlasImage
{
    public const int Cols = 16, ItemRow = 16, ExtraRow = 32, EntityRow = 33, EntitySlots = 20, Rows = EntityRow + EntitySlots / 4 * 2;
    public const int White = 0, GrassSide = 1, Shadow = 2, WaterStill = 3, WaterFlow = 4, LavaStill = 5, LavaFlow = 6, FireA = 7, FireB = 8;
    public const int BlockRow = Rows;

    public static int RowsFor(TextureAtlas? atlas) => atlas?.Models is { } m ? BlockRow + (m.Textures.Count + Cols - 1) / Cols : Rows;

    public static (int Col, int Row) EntityCell(int id) => (id % 4 * 4, EntityRow + id / 4 * 2);

    /// <summary>Width, height and 0xAARRGGBB pixels.</summary>
    public static (int W, int H, uint[] Argb) Build(TextureAtlas? atlas)
    {
        int t = atlas?.Tile ?? 16;
        int w = Cols * t, h = RowsFor(atlas) * t;
        var px = new uint[w * h];
        void Put(int x, int y, uint argb) => px[y * w + x] = argb;
        void Cell(int col, int row, int cw, int ch, int sw, int sh, uint[] src)
        {
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
        Cell(FireA, ExtraRow, 1, 1, 16, 16, FluidTextures.FireA);
        Cell(FireB, ExtraRow, 1, 1, 16, 16, FluidTextures.FireB);
        for (int id = 0; id < EntitySlots; id++)
        {
            var (ew, eh, epx) = EntityTextures.Get(atlas, id);
            if (ew <= 0 || eh <= 0 || epx.Length < ew * eh) continue;
            var (c, r) = EntityCell(id);
            Cell(c, r, 4, 2, ew, eh, epx);
        }
        if (atlas?.Models is { } models)
        {
            var mt = models.Textures;
            for (int i = 0; i < mt.Count; i++) Cell(i % Cols, BlockRow + i / Cols, 1, 1, 16, 16, mt.Pixels(i));
        }
        return (w, h, px);
    }
}

/// <summary>
/// The <c>AtlasMap</c> JSON: per block state (<c>"id"</c> when every meta is the same, else <c>"id:meta"</c>) its shape, pass,
/// six cube faces or its quads (block-local, from <see cref="BlockMesh"/> over a lone block in air), emissive and the flat colour.
/// </summary>
public static class AtlasJson
{
    public const int FlagWrap = 2, FlagSpan = 4;
    private static readonly Face[] FaceOrder = [Face.Down, Face.Up, Face.North, Face.South, Face.West, Face.East];

    public static JsonObject Build(string version, TextureAtlas? atlas, int width, int height)
    {
        var models = atlas?.Models;
        bool textured = atlas != null;
        int tile = atlas?.Tile ?? 16;
        var states = new JsonObject();
        for (int id = 0; id < 256; id++)
        {
            var perMeta = new string?[16];
            var nodes = new JsonObject?[16];
            for (int meta = 0; meta < 16; meta++)
            {
                JsonObject? entry;
                try { entry = State(models, textured, id, meta); }
                catch (Exception) { entry = null; }
                nodes[meta] = entry;
                perMeta[meta] = entry?.ToJsonString();
            }
            if (perMeta.All(s => s == null)) continue;
            if (perMeta.All(s => s == perMeta[0]) && nodes[0] != null) states[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = nodes[0];
            else
            {
                var common = perMeta.Where(s => s != null).GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key;
                int baseMeta = Array.IndexOf(perMeta, common);
                states[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = nodes[baseMeta]!.DeepClone();
                for (int meta = 0; meta < 16; meta++)
                    if (nodes[meta] != null && perMeta[meta] != common) states[$"{id}:{meta}"] = nodes[meta];
            }
        }
        var root = new JsonObject
        {
            ["format"] = "mineclaw-atlas",
            ["format_version"] = 1,
            ["version"] = version,
            ["width"] = width,
            ["height"] = height,
            ["tile_size"] = tile,
            ["columns"] = AtlasImage.Cols,
            ["rows"] = AtlasImage.RowsFor(atlas),
            ["textured"] = textured,
            ["special"] = new JsonObject
            {
                ["white"] = Cell(AtlasImage.White, AtlasImage.ExtraRow), ["grassSide"] = Cell(AtlasImage.GrassSide, AtlasImage.ExtraRow),
                ["shadow"] = Cell(AtlasImage.Shadow, AtlasImage.ExtraRow), ["waterStill"] = Cell(AtlasImage.WaterStill, AtlasImage.ExtraRow),
                ["waterFlow"] = Cell(AtlasImage.WaterFlow, AtlasImage.ExtraRow), ["lavaStill"] = Cell(AtlasImage.LavaStill, AtlasImage.ExtraRow),
                ["lavaFlow"] = Cell(AtlasImage.LavaFlow, AtlasImage.ExtraRow), ["fireA"] = Cell(AtlasImage.FireA, AtlasImage.ExtraRow),
                ["fireB"] = Cell(AtlasImage.FireB, AtlasImage.ExtraRow),
            },
            ["states"] = states,
        };
        if (models != null)
        {
            root["default_tints"] = new JsonObject { ["grass"] = BiomeTints.Grass(), ["foliage"] = BiomeTints.Foliage(), ["water"] = BiomeTints.Water() };
            var biomes = new JsonObject();
            for (int b = 0; b < 256; b++)
                if (BiomeTints.BiomeName(b) != null)
                    biomes[b.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    { ["grass"] = BiomeTints.Grass(b), ["foliage"] = BiomeTints.Foliage(b), ["water"] = BiomeTints.Water(b) };
            root["biomes"] = biomes;
        }
        return root;
    }

    private static JsonArray Cell(int c, int r) => [c, r];

    private static JsonObject? State(IBlockModels? models, bool textured, int id, int meta)
    {
        if (id == 0 || !MineClaw.Data.Blocks.Exists(id)) return null; // ids the version does not have are left out
        if (models != null && id is 166 or 217) return new JsonObject { ["shape"] = "none", ["layer"] = "opaque" }; // barrier, structure void
        var w = new LoneWorld(id, meta, models != null ? 256 : 128);
        const int x = 0, y = LoneWorld.Y, z = 0;
        var shape = BlockMesh.Shape(models, id, meta);
        if (shape == BlockShape.Air) return id is 8 or 9 or 10 or 11 ? Fluid(id) : null;
        bool emissive = models?.Emissive(id) ?? BlockTextures.Emissive(id);
        uint flat = BlockGeometry.FlatColour(id, Face.Up);
        var o = new JsonObject();
        string? procedural = Procedural(models != null, id);
        if (procedural == "fluid") return Fluid(id);
        if (BlockMesh.IsCube(shape) || procedural != null)
        {
            o["shape"] = procedural ?? "cube";
            o["layer"] = shape switch { BlockShape.CutoutCube => "cutout", BlockShape.TranslucentCube => "translucent", _ => procedural is "pane" ? "cutout" : "opaque" };
            var faces = new JsonArray();
            List<GeoQuad>? quads = null;
            foreach (var f in FaceOrder)
            {
                GeoQuad q;
                if (BlockMesh.IsCube(shape)) q = models != null ? models.CubeFace(w, x, y, z, id, meta, f) : BlockMesh.CubeFace(w, x, y, z, id, meta, f);
                else
                {
                    if (quads == null) { quads = []; BlockMesh.Quads(models, w, x, y, z, quads); }
                    // ATLAS.md: the first quad whose LightFace is this side, else any quad.
                    var best = quads.Where(qq => qq.LightFace == (sbyte)f).Cast<GeoQuad?>().FirstOrDefault()
                        ?? quads.Where(qq => FaceOf(qq.Normal) == f).Cast<GeoQuad?>().FirstOrDefault() ?? quads.Cast<GeoQuad?>().FirstOrDefault();
                    if (best is not { } b) { faces.Add(null); continue; }
                    q = b;
                }
                faces.Add(FaceNode(q, f, textured, BlockMesh.IsCube(shape), id));
            }
            o["faces"] = faces;
        }
        else
        {
            var quads = new List<GeoQuad>();
            BlockMesh.Quads(models, w, x, y, z, quads);
            if (quads.Count == 0) return new JsonObject { ["shape"] = "none", ["layer"] = "opaque" };
            o["shape"] = "quads";
            var passes = quads.GroupBy(q => q.Pass).OrderByDescending(g => g.Count()).First().Key;
            o["layer"] = Layer(passes);
            var arr = new JsonArray();
            foreach (var q in quads) arr.Add(QuadNode(q, textured, id));
            o["quads"] = arr;
        }
        if (emissive) o["emissive"] = true;
        if (flat != 0) o["flat"] = flat;
        return o;
    }

    /// <summary>Neighbour-dependent shapes the viewer builds itself (textured from <c>faces</c>).</summary>
    private static string? Procedural(bool modern, int id) => id switch
    {
        8 or 9 or 10 or 11 => "fluid",
        54 => "chest",
        146 or 130 when modern => "chest",
        95 when !modern => "chest", // b1.8.1 locked chest (95 is stained glass in 1.12.2)
        85 => "fence",
        113 or 188 or 189 or 190 or 191 or 192 when modern => "fence",
        101 or 102 => "pane",
        160 when modern => "pane",
        139 when modern => "wall",
        _ => null,
    };

    private static JsonObject Fluid(int id)
    {
        bool water = id is 8 or 9;
        int still = water ? AtlasImage.WaterStill : AtlasImage.LavaStill, flow = water ? AtlasImage.WaterFlow : AtlasImage.LavaFlow;
        JsonObject F(int c) => new() { ["c"] = c, ["r"] = AtlasImage.ExtraRow, ["flags"] = FlagWrap };
        var faces = new JsonArray(F(still), F(still), F(flow), F(flow), F(flow), F(flow));
        var o = new JsonObject { ["shape"] = "fluid", ["layer"] = water ? "translucent" : "opaque", ["faces"] = faces };
        if (!water) o["emissive"] = true;
        o["flat"] = BlockGeometry.FlatColour(id, Face.Up);
        return o;
    }

    private static string Layer(RenderPass p) => p switch { RenderPass.Cutout => "cutout", RenderPass.Translucent => "translucent", _ => "opaque" };

    private static Face? FaceOf(Vector3 n)
    {
        if (n.Y > 0.9f) return Face.Up;
        if (n.Y < -0.9f) return Face.Down;
        if (n.Z < -0.9f) return Face.North;
        if (n.Z > 0.9f) return Face.South;
        if (n.X < -0.9f) return Face.West;
        if (n.X > 0.9f) return Face.East;
        return null;
    }

    /// <summary>The quad's atlas cell, tint, flags and UV scale (QuadEmit.Cell of the explorer's GPU mesher); false = flat colour.</summary>
    public static bool CellOf(in GeoQuad q, bool textured, out int col, out int row, out uint tint, out float uvScaleX, out float uvScaleY, out int flags)
    {
        tint = q.Tint;
        col = row = 0;
        uvScaleX = uvScaleY = 1;
        flags = 0;
        switch (q.Tex)
        {
            case QuadTex.Terrain when textured && q.Tile >= 0:
                if (q.Overlay == BlockTextures.GrassSideOverlay && q.Tile == 3) { col = AtlasImage.GrassSide; row = AtlasImage.ExtraRow; tint = 0; return true; }
                col = q.Tile & 15; row = q.Tile >> 4 & 15;
                return true;
            case QuadTex.Block when textured && q.Tile >= 0:
                col = q.Tile % AtlasImage.Cols; row = AtlasImage.BlockRow + q.Tile / AtlasImage.Cols;
                return true;
            case QuadTex.Item when textured && q.Tile >= 0:
                col = q.Tile & 15; row = AtlasImage.ItemRow + (q.Tile >> 4 & 15);
                return true;
            case QuadTex.WaterStill or QuadTex.WaterFlow or QuadTex.LavaStill or QuadTex.LavaFlow:
                col = q.Tex switch { QuadTex.WaterStill => AtlasImage.WaterStill, QuadTex.WaterFlow => AtlasImage.WaterFlow, QuadTex.LavaStill => AtlasImage.LavaStill, _ => AtlasImage.LavaFlow };
                row = AtlasImage.ExtraRow;
                flags = FlagWrap;
                return true;
            case QuadTex.Fire:
                col = q.Tile == 1 ? AtlasImage.FireB : AtlasImage.FireA; row = AtlasImage.ExtraRow;
                return true;
            case QuadTex.Entity when q.Tile >= 0 && q.Tile < AtlasImage.EntitySlots:
                (col, row) = AtlasImage.EntityCell(q.Tile);
                uvScaleX = 4; uvScaleY = 2;
                flags = FlagSpan;
                return true;
            default:
                return false;
        }
    }

    private static void Tint(JsonObject o, uint tint, string key = "tint")
    {
        if (tint == 0 || tint == 0xffffff) return;
        o[key] = tint & 0xffffff;
    }

    /// <summary>The ids BlockModels1122.BiomeTinted covers (grass 2, 31, 175, 83; foliage 18, 161, 106), and only when the quad carries
    /// the default (plains) tint: spruce/birch/lily-pad colours are fixed.</summary>
    private static string? Biome(uint tint, bool modern, int id)
    {
        if (!modern || tint == 0) return null;
        if (id is 2 or 31 or 175 or 83 && tint == BiomeTints.Grass()) return "grass";
        if (id is 18 or 161 or 106 && tint == BiomeTints.Foliage()) return "foliage";
        return null;
    }

    private static JsonObject? Overlay(in GeoQuad q, bool textured)
    {
        if (!textured || q.Tex != QuadTex.Block || q.Overlay < 0) return null;
        var ov = new JsonObject { ["c"] = q.Overlay % AtlasImage.Cols, ["r"] = AtlasImage.BlockRow + q.Overlay / AtlasImage.Cols };
        Tint(ov, q.OverlayTint);
        return ov;
    }

    /// <summary>ATLAS.md canonical corners per face (BlockMesh.CubeFace's), in Face order.</summary>
    private static readonly Vector3[][] Canonical =
    [
        [new(0, 0, 1), new(0, 0, 0), new(1, 0, 0), new(1, 0, 1)],
        [new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0)],
        [new(1, 1, 0), new(1, 0, 0), new(0, 0, 0), new(0, 1, 0)],
        [new(0, 1, 1), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1)],
        [new(0, 1, 0), new(0, 0, 0), new(0, 0, 1), new(0, 1, 1)],
        [new(1, 1, 1), new(1, 0, 1), new(1, 0, 0), new(1, 1, 0)],
    ];

    /// <summary>A face: null when it has no texture (the viewer draws the palette colour), else its cell, tint, overlay, flags, biome and
    /// per-corner uv (cell units, canonical corner order) when the texture does not follow the vanilla mapping.</summary>
    private static JsonNode? FaceNode(in GeoQuad q, Face f, bool textured, bool cube, int id)
    {
        bool modern = q.Tex == QuadTex.Block;
        if (!CellOf(q, textured, out int c, out int r, out uint tint, out float sx, out float sy, out int flags)) return null;
        var o = new JsonObject { ["c"] = c, ["r"] = r };
        Tint(o, tint);
        if (Overlay(q, textured) is { } ov) o["ov"] = ov;
        if (flags != 0) o["flags"] = flags;
        if (Biome(tint, modern, id) is { } b) o["biome"] = b;
        if (cube && !VanillaUv(q, f) && CanonicalUv(q, f, sx, sy) is { } uv) o["uv"] = uv;
        return o;
    }

    /// <summary>For each canonical corner, the T × uvScale of the quad corner at the same position (null when the quad isn't the full face).</summary>
    private static JsonArray? CanonicalUv(in GeoQuad q, Face f, float sx, float sy)
    {
        var ps = new[] { q.P0, q.P1, q.P2, q.P3 };
        var ts = new[] { q.T0, q.T1, q.T2, q.T3 };
        var arr = new JsonArray();
        foreach (var corner in Canonical[(int)f])
        {
            int i = Array.FindIndex(ps, p => Vector3.DistanceSquared(p, corner) < 1e-6f);
            if (i < 0) return null;
            arr.Add(R(ts[i].X * sx));
            arr.Add(R(ts[i].Y * sy));
        }
        return arr;
    }

    private static bool VanillaUv(in GeoQuad q, Face f)
    {
        static bool Near(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) < 1e-6f;
        return Near(q.T0, BlockGeometry.FaceUv(f, q.P0)) && Near(q.T1, BlockGeometry.FaceUv(f, q.P1)) && Near(q.T2, BlockGeometry.FaceUv(f, q.P2)) && Near(q.T3, BlockGeometry.FaceUv(f, q.P3));
    }

    private static JsonArray Uv(in GeoQuad q, float sx, float sy) =>
        [R(q.T0.X * sx), R(q.T0.Y * sy), R(q.T1.X * sx), R(q.T1.Y * sy), R(q.T2.X * sx), R(q.T2.Y * sy), R(q.T3.X * sx), R(q.T3.Y * sy)];

    private static double R(float v) => Math.Round(v, 5);

    private static JsonObject QuadNode(in GeoQuad q, bool textured, int id)
    {
        bool tex = CellOf(q, textured, out int c, out int r, out uint tint, out float sx, out float sy, out int flags);
        var o = new JsonObject
        {
            ["p"] = new JsonArray(R(q.P0.X), R(q.P0.Y), R(q.P0.Z), R(q.P1.X), R(q.P1.Y), R(q.P1.Z), R(q.P2.X), R(q.P2.Y), R(q.P2.Z), R(q.P3.X), R(q.P3.Y), R(q.P3.Z)),
            ["uv"] = Uv(q, sx, sy),
            ["c"] = tex ? c : AtlasImage.White,
            ["r"] = tex ? r : AtlasImage.ExtraRow,
        };
        if (tex) Tint(o, tint);
        if (Overlay(q, textured) is { } ov) o["ov"] = ov;
        if (flags != 0) o["flags"] = flags;
        if (q.LightFace >= 0) o["face"] = (int)q.LightFace;
        if (Math.Abs(q.Shade - 1) > 1e-4) o["shade"] = R(q.Shade);
        if (q.Pass != RenderPass.Opaque) o["pass"] = Layer(q.Pass);
        if (q.TwoSided) o["two_sided"] = true;
        if (q.Alpha > 0) o["alpha"] = R(q.Alpha);
        if (q.Flat != 0) o["flat"] = q.Flat & 0xffffff;
        if (tex && Biome(tint, q.Tex == QuadTex.Block, id) is { } b) o["biome"] = b;
        if (!tex) o["untextured"] = true;
        return o;
    }

    /// <summary>One block at (0, 64, 0) in air, full sky light: the lone-block geometry of a state.</summary>
    private sealed class LoneWorld(int id, int meta, int height) : IVoxelWorld
    {
        public const int Y = 64;
        public int Block(int x, int y, int z) => x == 0 && y == Y && z == 0 ? id : 0;
        public int Meta(int x, int y, int z) => x == 0 && y == Y && z == 0 ? meta : 0;
        public int SkyLight(int x, int y, int z) => 15;
        public int BlockLight(int x, int y, int z) => 0;
        public ColumnState State(int x, int z) => ColumnState.Live;
        public int WorldHeight => height;
    }
}

public static class AtlasEndpoints
{
    public static void Map(WebApplication app)
    {
        static string Version(HttpContext ctx, BridgeConfig cfg)
        {
            var v = ctx.Request.Query["version"].ToString();
            if (v.Length == 0) return cfg.ServerVersion;
            if (v is not ("b1.8.1" or "1.12.2")) throw new ArgumentException("version must be b1.8.1 or 1.12.2");
            return v;
        }
        static async Task Send(HttpContext ctx, byte[] body, string tag, string type)
        {
            ctx.Response.Headers.ETag = tag;
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            if (ctx.Request.Headers.IfNoneMatch.ToString().Split(',').Any(t => t.Trim() == tag)) { ctx.Response.StatusCode = 304; return; }
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength = body.Length;
            await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
        }
        app.MapGet("/v1/world/atlas.png", async (HttpContext ctx, BridgeConfig cfg, AtlasService atlas) =>
        {
            var b = await Task.Run(() => atlas.Get(Version(ctx, cfg)), ctx.RequestAborted);
            await Send(ctx, b.Png, b.PngTag, "image/png");
            return Results.Empty;
        });
        app.MapGet("/v1/world/atlas.json", async (HttpContext ctx, BridgeConfig cfg, AtlasService atlas) =>
        {
            var b = await Task.Run(() => atlas.Get(Version(ctx, cfg)), ctx.RequestAborted);
            await Send(ctx, b.Json, b.JsonTag, "application/json; charset=utf-8");
            return Results.Empty;
        });
    }
}
