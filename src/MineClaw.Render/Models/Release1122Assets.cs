using System.IO.Compression;

namespace MineClaw.Render.Models;

/// <summary>
/// Loads the 1.12.2 render assets from the user's installed client jar at runtime (read-only zip; nothing in it runs and
/// nothing is extracted or committed: Mojang's textures stay on this machine): every <c>blockstates/*.json</c>,
/// <c>models/block/*.json</c> and <c>textures/blocks/*.png</c> → <see cref="BlockModels1122"/>, plus the textures the shared
/// b1.8.1-shaped code paths still sample, rebuilt from 1.12.2 files so one <see cref="TextureAtlas"/> serves both:
/// <list type="bullet">
/// <item>a <c>terrain.png</c>-layout atlas (each b1.8.1 tile filled with the 1.12.2 texture of the same block; the name map was
/// derived once by matching the two versions' tiles pixel by pixel, names only) for fluids-adjacent legacy shapes, signs, beds
/// and dropped block items; chest and bed tiles are cut from the 1.12.2 entity textures;</item>
/// <item>an <c>items.png</c>-layout atlas from <c>textures/items</c> the same way (dropped and held items);</item>
/// <item>the player skin (<c>entity/steve.png</c>) and mob textures (<c>entity/zombie/zombie.png</c>…) under the b1.8.1 names
/// <see cref="EntityGeometry"/> asks for. The 64×64 biped textures (steve, zombie, zombie pigman) are cut to their top 64×32,
/// whose layout is the b1.8.1 one (head, hat, body, right arm, right leg); the left limbs stay mirrored copies of the right
/// ones as in b1.8.1's ModelBiped, so a 1.12.2 skin's own left arm/leg and its overlay layers (jacket, sleeves, pants) are not drawn.</item>
/// </list>
/// </summary>
public static class Release1122Assets
{
    public const string Version = "1.12.2";
    private const string Root = "assets/minecraft/";

    /// <summary>True when the zip is a 1.12.2-style client jar (has blockstates + block models).</summary>
    public static bool IsModernJar(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            return zip.GetEntry(Root + "blockstates/stone.json") != null && zip.GetEntry(Root + "models/block/cube.json") != null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Reads the jar into a ready atlas (models attached), or null when it isn't a 1.12.2 jar.</summary>
    public static TextureAtlas? FromJar(string jarPath, Action<string>? log = null)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        if (zip.GetEntry(Root + "blockstates/stone.json") == null) return null;
        var jar = new JarAssets();
        int bad = 0;
        foreach (var e in zip.Entries)
        {
            string n = e.FullName;
            if (!n.StartsWith(Root, StringComparison.Ordinal) || e.Length > 1 << 20) continue;
            try { jar.Read(n[Root.Length..], e); }
            catch (Exception) { bad++; }
        }
        if (bad > 0) log?.Invoke($"1.12.2 assets: {bad} unreadable entries skipped");
        AddChests(jar.Tex, jar.Baker, jar.States, jar.Entity);
        AddShulkers(jar.Baker, jar.States);
        var models = new BlockModels1122(jar.Tex, jar.Baker, jar.States);
        var terrain = Terrain(jar.Tex, jar.Entity);
        var itemAtlas = Items(jar.Items);
        var skin = jar.Entity.TryGetValue("steve", out var st) ? TopHalf(st) : ((int, int, uint[])?)null;
        var mobs = MobTextures(jar.Entity);
        log?.Invoke($"1.12.2 assets from {jarPath}: {jar.States.Count} blockstates, {jar.Baker.Count} models, {jar.Tex.Count} textures, {mobs.Count} mob textures");
        return new TextureAtlas(256, terrain, itemAtlas, skin) { Source = jarPath, Mobs = mobs.Count == 0 ? null : mobs, Models = models };
    }

    /// <summary>The b1.8.1 mob textures the jar has (zombies cut to the old 64×32 layout).</summary>
    private static Dictionary<string, (int W, int H, uint[] Px)> MobTextures(Dictionary<string, (int W, int H, uint[] Px)> entity)
    {
        var mobs = new Dictionary<string, (int W, int H, uint[] Px)>();
        foreach (var (old, path) in MobPaths)
            if (entity.TryGetValue(path, out var img)) mobs[old] = old is "zombie" or "pigzombie" ? TopHalf(img) : img;
        return mobs;
    }

    /// <summary>What the jar's assets/minecraft entries hold: blockstates, block models, block/item/entity textures.</summary>
    private sealed class JarAssets
    {
        public readonly ModelTextures Tex = new();
        public readonly ModelBaker Baker;
        public readonly Dictionary<string, BlockStateFile> States = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (int W, int H, uint[] Px)> Entity = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (int W, int H, uint[] Px)> Items = new(StringComparer.Ordinal);

        public JarAssets() => Baker = new ModelBaker(Tex);

        private static bool Under(string rel, string dir, string ext) => rel.StartsWith(dir, StringComparison.Ordinal) && rel.EndsWith(ext, StringComparison.Ordinal);

        /// <summary>Takes one entry (path relative to assets/minecraft) if it is one of the kinds read; throws on a bad file.</summary>
        public void Read(string rel, ZipArchiveEntry e)
        {
            if (Under(rel, "blockstates/", ".json")) States[rel[12..^5]] = BlockStateFile.Parse(Text(e));
            else if (Under(rel, "models/block/", ".json")) Baker.Add("block/" + rel[13..^5], Text(e));
            else if (Under(rel, "textures/blocks/", ".png"))
            {
                var (w, h, px) = Png.Decode(Bytes(e));
                Tex.Add("blocks/" + rel[16..^4], w, h, px);
            }
            else if (Under(rel, "textures/items/", ".png")) Items[rel[15..^4]] = Png.Decode(Bytes(e));
            else if (Under(rel, "textures/entity/", ".png") && WantedEntity(rel[16..^4])) Entity[rel[16..^4]] = Png.Decode(Bytes(e));
        }
    }

    /// <summary>b1.8.1 mob texture name → the 1.12.2 <c>textures/entity</c> path.</summary>
    private static readonly (string Old, string Path)[] MobPaths =
    [
        ("zombie", "zombie/zombie"), ("pigzombie", "zombie_pigman"), ("skeleton", "skeleton/skeleton"), ("creeper", "creeper/creeper"),
        ("spider", "spider/spider"), ("pig", "pig/pig"), ("cow", "cow/cow"), ("sheep", "sheep/sheep"), ("sheep_fur", "sheep/sheep_fur"),
        ("chicken", "chicken"), ("enderman", "enderman/enderman"), ("slime", "slime/slime"), ("wolf", "wolf/wolf"), ("squid", "squid"),
        ("ghast", "ghast/ghast"), ("silverfish", "silverfish"),
    ];

    private static bool WantedEntity(string p) => p is "steve" or "chest/normal" or "chest/trapped" or "chest/ender" or "chest/normal_double" or "chest/trapped_double" or "bed/red"
        || MobPaths.Any(m => m.Path == p);

    private static byte[] Bytes(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Text(ZipArchiveEntry e) => System.Text.Encoding.UTF8.GetString(Bytes(e));

    private static (int W, int H, uint[] Px) TopHalf((int W, int H, uint[] Px) img)
    {
        if (img.H <= img.W / 2) return img;
        int h = img.W / 2;
        return (img.W, h, img.Px.AsSpan(0, img.W * h).ToArray());
    }

    // ------------------------------------------------------------------ chests and shulker boxes (tile-entity renderers in 1.12.2)

    /// <summary>A 16×16 tile from a region of an image (nearest), optionally stacking a second region below the first.</summary>
    private static uint[] Cut((int W, int H, uint[] Px) img, params (int X, int Y, int W, int H)[] parts)
    {
        int total = parts.Sum(p => p.H);
        var dst = new uint[16 * 16];
        for (int y = 0; y < 16; y++)
        {
            int sy = y * total / 16, k = 0;
            while (k < parts.Length - 1 && sy >= parts[k].H) { sy -= parts[k].H; k++; }
            var p = parts[k];
            for (int x = 0; x < 16; x++)
            {
                int sx = p.X + x * p.W / 16, yy = p.Y + Math.Min(sy, p.H - 1);
                dst[y * 16 + x] = (uint)sx < (uint)img.W && (uint)yy < (uint)img.H ? img.Px[yy * img.W + sx] | 0xff000000 : 0xff6b4a2a;
            }
        }
        return dst;
    }

    /// <summary>ModelChest (64×64): the lid box (0,0) 14×5×14 and the base (0,19) 14×10×14, the latch (0,0) 2×4×1. Each chest
    /// becomes a 14×14×14 box (1..15, 0..14) with the latch painted on its front, rotated like a furnace.</summary>
    private static void AddChests(ModelTextures tex, ModelBaker baker, Dictionary<string, BlockStateFile> states, Dictionary<string, (int W, int H, uint[] Px)> entity)
    {
        foreach (var kind in new[] { "normal", "trapped", "ender" })
        {
            if (!entity.TryGetValue("chest/" + kind, out var img)) continue;
            var front = Cut(img, (14, 14, 14, 5), (14, 33, 14, 10));
            // The latch: its front face (1,1) 2×4 at the lid's lower edge.
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 2; x++)
                    if ((uint)(1 + x) < (uint)img.W && (uint)(1 + y) < (uint)img.H) front[(3 + y) * 16 + 7 + x] = img.Px[(1 + y) * img.W + 1 + x] | 0xff000000;
            tex.Add($"mineclaw/chest_{kind}_front", 16, 16, front);
            tex.Add($"mineclaw/chest_{kind}_side", 16, 16, Cut(img, (0, 14, 14, 5), (0, 33, 14, 10)));
            tex.Add($"mineclaw/chest_{kind}_top", 16, 16, Cut(img, (14, 0, 14, 14)));
            tex.Add($"mineclaw/chest_{kind}_bottom", 16, 16, Cut(img, (28, 19, 14, 14)));
            baker.Add($"block/mineclaw_chest_{kind}", $$"""
                { "textures": { "f": "mineclaw/chest_{{kind}}_front", "s": "mineclaw/chest_{{kind}}_side", "t": "mineclaw/chest_{{kind}}_top", "b": "mineclaw/chest_{{kind}}_bottom" },
                  "elements": [ { "from": [1, 0, 1], "to": [15, 14, 15], "faces": {
                    "north": { "uv": [0, 0, 16, 16], "texture": "#f" }, "south": { "uv": [0, 0, 16, 16], "texture": "#s" },
                    "west": { "uv": [0, 0, 16, 16], "texture": "#s" }, "east": { "uv": [0, 0, 16, 16], "texture": "#s" },
                    "up": { "uv": [0, 0, 16, 16], "texture": "#t" }, "down": { "uv": [0, 0, 16, 16], "texture": "#b" } } } ] }
                """);
            states[$"mineclaw_chest_{kind}"] = BlockStateFile.Parse($$"""
                { "variants": { "facing=north": { "model": "mineclaw_chest_{{kind}}" }, "facing=south": { "model": "mineclaw_chest_{{kind}}", "y": 180 },
                  "facing=west": { "model": "mineclaw_chest_{{kind}}", "y": 270 }, "facing=east": { "model": "mineclaw_chest_{{kind}}", "y": 90 } } }
                """);
            if (kind != "ender") AddLargeChest(tex, baker, states, kind, entity.TryGetValue($"chest/{kind}_double", out var big) ? big : null, img);
        }
    }

    /// <summary>
    /// ModelLargeChest (128×64): lid (0,0) 30×5×14, base (0,19) 30×10×14, the latch (0,0) 2×4×1 on the seam. Each cell of a pair
    /// (<see cref="LargeChest"/>) draws its half of one 30×14×14 box: the north-facing model's west half spans x 1..16, the east
    /// half 0..15, no face on the seam, and the front, back, top and bottom textures split at u 15 so the pair reads as one
    /// wide chest with one latch across the middle. Without the double texture (an old pack) the halves reuse the single one.
    /// </summary>
    private static void AddLargeChest(ModelTextures tex, ModelBaker baker, Dictionary<string, BlockStateFile> states, string kind,
        (int W, int H, uint[] Px)? big, (int W, int H, uint[] Px) single)
    {
        string T(string part) => $"mineclaw/chest_{kind}_double_{part}";
        if (big is { } b)
        {
            // North face: u 0 at the element's east edge, so the viewer's left (east) half shows u 0..15 of the wide front.
            var frontE = Cut(b, (14, 14, 15, 5), (14, 33, 15, 10));
            var frontW = Cut(b, (29, 14, 15, 5), (29, 33, 15, 10));
            for (int y = 0; y < 4; y++)
            {
                // The latch straddles the seam: its left column ends the east half's tile, its right column starts the west half's.
                if ((uint)(1 + y) >= (uint)b.H) continue;
                frontE[(3 + y) * 16 + 15] = b.Px[(1 + y) * b.W + 1] | 0xff000000;
                frontW[(3 + y) * 16] = b.Px[(1 + y) * b.W + 2] | 0xff000000;
            }
            tex.Add(T("front_e"), 16, 16, frontE);
            tex.Add(T("front_w"), 16, 16, frontW);
            tex.Add(T("back_w"), 16, 16, Cut(b, (58, 14, 15, 5), (58, 33, 15, 10)));
            tex.Add(T("back_e"), 16, 16, Cut(b, (73, 14, 15, 5), (73, 33, 15, 10)));
            tex.Add(T("end_w"), 16, 16, Cut(b, (0, 14, 14, 5), (0, 33, 14, 10)));
            tex.Add(T("end_e"), 16, 16, Cut(b, (44, 14, 14, 5), (44, 33, 14, 10)));
            tex.Add(T("top_w"), 16, 16, Cut(b, (14, 0, 15, 14)));
            tex.Add(T("top_e"), 16, 16, Cut(b, (29, 0, 15, 14)));
            tex.Add(T("bottom_w"), 16, 16, Cut(b, (44, 19, 15, 14)));
            tex.Add(T("bottom_e"), 16, 16, Cut(b, (59, 19, 15, 14)));
        }
        else
        {
            var s = single;
            foreach (var h in new[] { "w", "e" })
            {
                tex.Add(T("front_" + h), 16, 16, Cut(s, (14, 14, 14, 5), (14, 33, 14, 10)));
                tex.Add(T("back_" + h), 16, 16, Cut(s, (0, 14, 14, 5), (0, 33, 14, 10)));
                tex.Add(T("end_" + h), 16, 16, Cut(s, (0, 14, 14, 5), (0, 33, 14, 10)));
                tex.Add(T("top_" + h), 16, 16, Cut(s, (14, 0, 14, 14)));
                tex.Add(T("bottom_" + h), 16, 16, Cut(s, (28, 19, 14, 14)));
            }
        }
        foreach (var (h, from, to, end) in new[] { ("w", "[1, 0, 1]", "[16, 14, 15]", "west"), ("e", "[0, 0, 1]", "[15, 14, 15]", "east") })
            baker.Add($"block/mineclaw_chest_{kind}_double_{h}", $$"""
                { "textures": { "f": "{{T("front_" + h)}}", "k": "{{T("back_" + h)}}", "s": "{{T("end_" + h)}}", "t": "{{T("top_" + h)}}", "b": "{{T("bottom_" + h)}}" },
                  "elements": [ { "from": {{from}}, "to": {{to}}, "faces": {
                    "north": { "uv": [0, 0, 16, 16], "texture": "#f" }, "south": { "uv": [0, 0, 16, 16], "texture": "#k" },
                    "{{end}}": { "uv": [0, 0, 16, 16], "texture": "#s" },
                    "up": { "uv": [0, 0, 16, 16], "texture": "#t" }, "down": { "uv": [0, 0, 16, 16], "texture": "#b" } } } ] }
                """);
        var variants = new List<string>();
        foreach (var (facing, rot) in new[] { ("north", 0), ("south", 180), ("west", 270), ("east", 90) })
            foreach (var h in new[] { "west", "east" })
                variants.Add($"\"facing={facing},half={h}\": {{ \"model\": \"mineclaw_chest_{kind}_double_{h[0]}\"{(rot == 0 ? "" : $", \"y\": {rot}")} }}");
        states[$"mineclaw_chest_{kind}_double"] = BlockStateFile.Parse("{ \"variants\": { " + string.Join(", ", variants) + " } }");
    }

    /// <summary>Shulker boxes: a cube of their <c>shulker_top_&lt;colour&gt;</c> texture.</summary>
    private static void AddShulkers(ModelBaker baker, Dictionary<string, BlockStateFile> states)
    {
        foreach (var c in BlockStates1122.Colors)
        {
            baker.Add($"block/mineclaw_shulker_{c}", $$"""{ "parent": "block/cube_all", "textures": { "all": "blocks/shulker_top_{{c}}" } }""");
            states[$"mineclaw_shulker_{c}"] = BlockStateFile.Parse($$"""{ "variants": { "normal": { "model": "mineclaw_shulker_{{c}}" } } }""");
        }
    }

    // ------------------------------------------------------------------ the b1.8.1-layout atlases

    private static uint[] Terrain(ModelTextures tex, Dictionary<string, (int W, int H, uint[] Px)> entity)
    {
        const int size = 256;
        var px = new uint[size * size];
        Array.Fill(px, 0xff808080u);
        void Put(int tile, uint[] src)
        {
            int ox = tile % 16 * 16, oy = tile / 16 * 16;
            for (int y = 0; y < 16; y++) Array.Copy(src, y * 16, px, (oy + y) * size + ox, 16);
        }
        foreach (var (tile, name) in TerrainNames)
            if (tex.Has("blocks/" + name)) Put(tile, tex.Pixels(tex.IndexOf("blocks/" + name)));
        if (tex.Has("mineclaw/chest_normal_front"))
        {
            Put(25, tex.Pixels(tex.IndexOf("mineclaw/chest_normal_top")));
            foreach (int t in new[] { 26, 57, 58 }) Put(t, tex.Pixels(tex.IndexOf("mineclaw/chest_normal_side")));
            foreach (int t in new[] { 27, 41, 42 }) Put(t, tex.Pixels(tex.IndexOf("mineclaw/chest_normal_front")));
            // The b1.8.1 double-chest tiles (front halves 41/42, back halves 57/58) from the wide 1.12.2 texture.
            if (tex.Has("mineclaw/chest_normal_double_front_e"))
            {
                Put(41, tex.Pixels(tex.IndexOf("mineclaw/chest_normal_double_front_e")));
                Put(42, tex.Pixels(tex.IndexOf("mineclaw/chest_normal_double_front_w")));
                Put(57, tex.Pixels(tex.IndexOf("mineclaw/chest_normal_double_back_w")));
                Put(58, tex.Pixels(tex.IndexOf("mineclaw/chest_normal_double_back_e")));
            }
        }
        if (entity.TryGetValue("bed/red", out var bed))
        {
            // ModelBed (64×64): head box (0,0) and foot box (0,22), 16×16×6; the mattress is each box's 16×16 face, the sides 6×16.
            Put(135, Cut(bed, (6, 6, 16, 16)));
            Put(134, Cut(bed, (6, 28, 16, 16)));
            Put(151, SideStrip(bed, 0, 6));
            Put(150, SideStrip(bed, 0, 28));
        }
        return px;
    }

    /// <summary>A bed side: the box's 6×16 side turned to a 16×6 band at rows 7..12 (the b1.8.1 bed side tile's visible rows).</summary>
    private static uint[] SideStrip((int W, int H, uint[] Px) img, int x0, int y0)
    {
        var dst = new uint[256];
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 16; x++)
            {
                int sx = x0 + y, sy = y0 + x;
                dst[(7 + y) * 16 + x] = (uint)sx < (uint)img.W && (uint)sy < (uint)img.H ? img.Px[sy * img.W + sx] | 0xff000000 : 0xff8b1a1a;
            }
        return dst;
    }

    private static uint[] Items(Dictionary<string, (int W, int H, uint[] Px)> items)
    {
        const int size = 256;
        var px = new uint[size * size];
        foreach (var (tile, name) in ItemNames)
        {
            if (!items.TryGetValue(name, out var img) || img.W < 16) continue;
            int ox = tile % 16 * 16, oy = tile / 16 * 16, f = Math.Min(img.W, img.H);
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    px[(oy + y) * size + ox + x] = img.Px[y * f / 16 * img.W + x * f / 16];
        }
        return px;
    }

    /// <summary>b1.8.1 <c>terrain.png</c> tile → 1.12.2 <c>textures/blocks</c> name (derived by matching the tiles; names only).</summary>
    private static readonly (int Tile, string Name)[] TerrainNames =
    [
        (0, "grass_top"), (1, "stone"), (2, "dirt"), (3, "grass_side"), (4, "planks_oak"), (5, "stone_slab_side"),
        (6, "stone_slab_top"), (7, "brick"), (8, "tnt_side"), (9, "tnt_top"), (10, "tnt_bottom"), (11, "web"),
        (12, "flower_rose"), (13, "flower_dandelion"), (15, "sapling_oak"), (16, "cobblestone"), (17, "bedrock"), (18, "sand"),
        (19, "gravel"), (20, "log_oak"), (21, "log_oak_top"), (22, "iron_block"), (23, "gold_block"), (24, "diamond_block"),
        (28, "mushroom_red"), (29, "mushroom_brown"), (32, "gold_ore"), (33, "iron_ore"), (34, "coal_ore"), (35, "bookshelf"),
        (36, "cobblestone_mossy"), (37, "obsidian"), (38, "grass_side_overlay"), (39, "tallgrass"), (40, "grass_top"),
        (43, "crafting_table_top"), (44, "furnace_front_off"), (45, "furnace_side"), (46, "dispenser_front_horizontal"),
        (48, "sponge"), (49, "glass"), (50, "diamond_ore"), (51, "redstone_ore"), (52, "leaves_oak"), (53, "leaves_oak"),
        (54, "stonebrick"), (55, "deadbush"), (56, "fern"), (59, "crafting_table_side"), (60, "crafting_table_front"),
        (61, "furnace_front_on"), (62, "furnace_top"), (63, "sapling_spruce"), (64, "wool_colored_white"), (65, "mob_spawner"),
        (66, "snow"), (67, "ice"), (68, "grass_side_snowed"), (69, "cactus_top"), (70, "cactus_side"), (71, "cactus_bottom"),
        (72, "clay"), (73, "reeds"), (74, "jukebox_side"), (75, "jukebox_top"), (79, "sapling_birch"), (80, "torch_on"),
        (81, "door_wood_upper"), (82, "door_iron_upper"), (83, "ladder"), (84, "trapdoor"), (85, "iron_bars"), (86, "farmland_wet"),
        (87, "farmland_dry"), (88, "wheat_stage_0"), (89, "wheat_stage_1"), (90, "wheat_stage_2"), (91, "wheat_stage_3"),
        (92, "wheat_stage_4"), (93, "wheat_stage_5"), (94, "wheat_stage_6"), (95, "wheat_stage_7"), (96, "lever"),
        (97, "door_wood_lower"), (98, "door_iron_lower"), (99, "redstone_torch_on"), (100, "stonebrick_mossy"),
        (101, "stonebrick_cracked"), (102, "pumpkin_top"), (103, "netherrack"), (104, "soul_sand"), (105, "glowstone"),
        (106, "piston_top_sticky"), (107, "piston_top_normal"), (108, "piston_side"), (109, "furnace_top"), (110, "piston_inner"),
        (112, "rail_normal_turned"), (113, "wool_colored_black"), (114, "wool_colored_gray"), (115, "redstone_torch_off"),
        (116, "log_spruce"), (117, "log_birch"), (118, "pumpkin_side"), (119, "pumpkin_face_off"), (120, "pumpkin_face_on"),
        (121, "cake_top"), (122, "cake_side"), (123, "cake_inner"), (124, "cake_bottom"), (125, "mushroom_block_skin_red"),
        (126, "mushroom_block_skin_brown"), (128, "rail_normal"), (129, "wool_colored_red"), (130, "wool_colored_pink"),
        (131, "repeater_off"), (132, "leaves_spruce"), (136, "melon_side"), (137, "melon_top"), (141, "mushroom_block_skin_stem"),
        (142, "mushroom_block_inside"), (143, "vine"), (144, "lapis_block"), (145, "wool_colored_green"),
        (146, "wool_colored_lime"), (147, "repeater_on"), (148, "glass_pane_top"), (160, "lapis_ore"), (161, "wool_colored_brown"),
        (162, "wool_colored_yellow"), (163, "rail_golden"), (176, "sandstone_top"), (177, "wool_colored_blue"),
        (178, "wool_colored_light_blue"), (179, "rail_golden_powered"), (192, "sandstone_normal"), (193, "wool_colored_purple"),
        (194, "wool_colored_magenta"), (195, "rail_detector"), (208, "sandstone_bottom"), (209, "wool_colored_cyan"),
        (210, "wool_colored_orange"), (225, "wool_colored_silver"), (240, "destroy_stage_0"), (241, "destroy_stage_1"),
        (242, "destroy_stage_2"), (243, "destroy_stage_3"), (244, "destroy_stage_4"), (245, "destroy_stage_5"),
        (246, "destroy_stage_6"), (247, "destroy_stage_7"), (248, "destroy_stage_8"), (249, "destroy_stage_9"),
    ];

    /// <summary>b1.8.1 <c>gui/items.png</c> icon → 1.12.2 <c>textures/items</c> name (derived the same way).</summary>
    private static readonly (int Tile, string Name)[] ItemNames =
    [
        (0, "leather_helmet"), (1, "chainmail_helmet"), (2, "iron_helmet"), (3, "diamond_helmet"), (4, "gold_helmet"),
        (5, "flint_and_steel"), (6, "flint"), (7, "coal"), (8, "string"), (9, "seeds_wheat"), (10, "apple"), (11, "apple_golden"),
        (12, "egg"), (13, "sugar"), (14, "snowball"), (16, "leather_chestplate"), (17, "chainmail_chestplate"),
        (18, "iron_chestplate"), (19, "diamond_chestplate"), (20, "gold_chestplate"), (21, "bow_standby"), (22, "brick"),
        (23, "iron_ingot"), (24, "feather"), (25, "wheat"), (26, "painting"), (27, "reeds"), (28, "bone"), (29, "cake"),
        (30, "slimeball"), (32, "leather_leggings"), (33, "chainmail_leggings"), (34, "iron_leggings"), (35, "diamond_leggings"),
        (36, "gold_leggings"), (37, "arrow"), (39, "gold_ingot"), (40, "gunpowder"), (41, "bread"), (42, "sign"), (43, "door_wood"),
        (44, "door_iron"), (48, "leather_boots"), (49, "chainmail_boots"), (50, "iron_boots"), (51, "diamond_boots"),
        (52, "gold_boots"), (53, "stick"), (54, "compass_00"), (55, "diamond"), (56, "redstone_dust"), (57, "clay_ball"),
        (58, "paper"), (59, "book_normal"), (60, "map_filled"), (61, "seeds_pumpkin"), (62, "seeds_melon"), (64, "wood_sword"),
        (65, "stone_sword"), (66, "iron_sword"), (67, "diamond_sword"), (68, "gold_sword"), (69, "fishing_rod_uncast"),
        (70, "clock_00"), (71, "bowl"), (72, "mushroom_stew"), (73, "glowstone_dust"), (74, "bucket_empty"), (75, "bucket_water"),
        (76, "bucket_lava"), (77, "bucket_milk"), (78, "dye_powder_black"), (79, "dye_powder_gray"), (80, "wood_shovel"),
        (81, "stone_shovel"), (82, "iron_shovel"), (83, "diamond_shovel"), (84, "gold_shovel"), (85, "fishing_rod_cast"),
        (86, "repeater"), (87, "porkchop_raw"), (88, "porkchop_cooked"), (89, "fish_cod_raw"), (90, "fish_cod_cooked"),
        (91, "rotten_flesh"), (92, "cookie"), (93, "shears"), (94, "dye_powder_red"), (95, "dye_powder_pink"), (96, "wood_pickaxe"),
        (97, "stone_pickaxe"), (98, "iron_pickaxe"), (99, "diamond_pickaxe"), (100, "gold_pickaxe"), (101, "bow_pulling_0"),
        (103, "leather"), (104, "saddle"), (105, "beef_raw"), (106, "beef_cooked"), (107, "ender_pearl"), (109, "melon"),
        (110, "dye_powder_green"), (111, "dye_powder_lime"), (112, "wood_axe"), (113, "stone_axe"), (114, "iron_axe"),
        (115, "diamond_axe"), (116, "gold_axe"), (117, "bow_pulling_1"), (121, "chicken_raw"), (122, "chicken_cooked"),
        (126, "dye_powder_brown"), (127, "dye_powder_yellow"), (128, "wood_hoe"), (129, "stone_hoe"), (130, "iron_hoe"),
        (131, "diamond_hoe"), (132, "gold_hoe"), (133, "bow_pulling_2"), (135, "minecart_normal"), (136, "oak_boat"),
        (142, "dye_powder_blue"), (143, "dye_powder_light_blue"), (151, "minecart_chest"), (158, "dye_powder_purple"),
        (159, "dye_powder_magenta"), (167, "minecart_furnace"), (174, "dye_powder_cyan"), (175, "dye_powder_orange"),
        (190, "dye_powder_silver"), (191, "dye_powder_white"), (240, "record_13"), (241, "record_cat"),
    ];
}
