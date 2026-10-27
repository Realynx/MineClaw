using System.IO.Compression;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.GameTools;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class VisionTests
{
    // ------------------------------------------------------------------ PNG

    [Fact]
    public void Png_round_trips_indexed_and_rgb()
    {
        var small = new uint[] { 0xff0000, 0x00ff00, 0x0000ff, 0xffffff, 0x000000, 0x123456 };
        var png = Png.Encode(small, 3, 2);
        Assert.Equal((3, 2, 3), Png.Header(png));
        var (w, h, px) = Png.Decode(png);
        Assert.Equal((3, 2), (w, h));
        Assert.Equal(small, px.Select(p => p & 0xffffff).ToArray());

        // >256 colours → truecolour.
        var many = Enumerable.Range(0, 400).Select(i => (uint)(i * 40503)).Select(c => c & 0xffffff).ToArray();
        var png2 = Png.Encode(many, 20, 20);
        Assert.Equal(2, Png.Header(png2)!.Value.ColourType);
        Assert.Equal(many, Png.Decode(png2).Argb.Select(p => p & 0xffffff).ToArray());
    }

    // ------------------------------------------------------------------ tiles

    [Fact]
    public void Tile_mapping_matches_b181_terrain_png()
    {
        Assert.Equal(new BlockTextures.TileRef(0, BlockTextures.GrassTint), BlockTextures.For(Blocks.Grass, 0, Face.Up));
        Assert.Equal(3, BlockTextures.For(Blocks.Grass, 0, Face.North).Tile);
        Assert.Equal(BlockTextures.GrassSideOverlay, BlockTextures.For(Blocks.Grass, 0, Face.East).Overlay);
        Assert.Equal(2, BlockTextures.For(Blocks.Grass, 0, Face.Down).Tile);
        Assert.Equal(1, BlockTextures.For(Blocks.Stone, 0, Face.Up).Tile);
        Assert.Equal(16, BlockTextures.For(Blocks.Cobblestone, 0, Face.West).Tile);
        Assert.Equal(4, BlockTextures.For(Blocks.Planks, 0, Face.Up).Tile);
        Assert.Equal(21, BlockTextures.For(Blocks.Log, 0, Face.Up).Tile);
        Assert.Equal(20, BlockTextures.For(Blocks.Log, 0, Face.South).Tile);
        Assert.Equal(116, BlockTextures.For(Blocks.Log, 1, Face.South).Tile); // spruce
        Assert.Equal(117, BlockTextures.For(Blocks.Log, 2, Face.South).Tile); // birch
        Assert.Equal(64, BlockTextures.For(Blocks.Wool, 0, Face.Up).Tile);
        Assert.Equal(129, BlockTextures.For(Blocks.Wool, 14, Face.Up).Tile);  // red
        Assert.Equal(113, BlockTextures.For(Blocks.Wool, 15, Face.Up).Tile);  // black
        Assert.Equal(44, BlockTextures.For(Blocks.Furnace, 3, Face.South).Tile); // front faces south (meta 3)
        Assert.Equal(45, BlockTextures.For(Blocks.Furnace, 3, Face.North).Tile);
        Assert.Equal(61, BlockTextures.For(Blocks.LitFurnace, 4, Face.West).Tile);
        Assert.Equal(62, BlockTextures.For(Blocks.Furnace, 2, Face.Up).Tile);
        Assert.Equal(97, BlockTextures.For(Blocks.WoodenDoor, 0, Face.South).Tile); // lower half
        Assert.Equal(81, BlockTextures.For(Blocks.WoodenDoor, 8, Face.South).Tile); // upper half
        Assert.Equal(25, BlockTextures.For(Blocks.Chest, 0, Face.Up).Tile);
        Assert.Equal(43, BlockTextures.For(Blocks.Workbench, 0, Face.Up).Tile);
        Assert.Equal(6, BlockTextures.For(Blocks.Slab, 0, Face.Up).Tile);
        Assert.Equal(5, BlockTextures.For(Blocks.Slab, 0, Face.North).Tile);
        Assert.Equal(53, BlockTextures.For(Blocks.Leaves, 0, Face.Up).Tile);
        Assert.True(BlockTextures.For(Blocks.Air, 0, Face.Up).IsNone);
        Assert.True(BlockTextures.UsePalette(Blocks.Water) && BlockTextures.UsePalette(Blocks.Lava));
    }

    // ------------------------------------------------------------------ lighting

    [Fact]
    public void Lighting_follows_the_vanilla_table_and_day_cycle()
    {
        Assert.Equal(1f, Lighting.Brightness(15), 3);
        Assert.Equal(0f, Lighting.Brightness(0), 3);
        Assert.Equal(0.1f, Lighting.Brightness(0, nether: true), 3);
        Assert.Equal(0.5f, Lighting.Brightness(12), 3); // (0.8)/(0.2·3+1)
        Assert.Equal(0, Lighting.SkylightSubtracted(6000));  // noon
        Assert.Equal(11, Lighting.SkylightSubtracted(18000)); // midnight
        Assert.Equal(4, Lighting.Level(15, 4, 11));
        Assert.Equal(0.5f, Lighting.FaceShade(Face.Down));
        Assert.Equal(0.8f, Lighting.FaceShade(Face.North));
        Assert.Equal(0.6f, Lighting.FaceShade(Face.East));

        // Stale light: an air cell reading 0 after an edit borrows from its brightest neighbour − 1.
        var w = new SparseVoxels().Set(0, 63, 0, Blocks.Stone).Light(0, 64, 0, 0, 0).Light(0, 65, 0, 0, 0).Light(1, 64, 0, 15, 0);
        Assert.Equal(14, FaceLight.CellLevel(w, 0, 64, 0, 0));
        // Emitters render at full brightness whatever the light around them.
        Assert.Equal(1f, FaceLight.Of(new SparseVoxels().Light(0, 65, 0, 0, 0), Blocks.Torch, 0, 64, 0, Face.Up, 11, false));
    }

    // ------------------------------------------------------------------ raycaster

    [Fact]
    public void Raycaster_hits_a_known_block_at_the_expected_pixel()
    {
        // Eye at (0.5, 65.62, 0.5) facing south (yaw 0, pitch 0); a stone block 5 blocks south at eye height.
        var w = new SparseVoxels().Set(0, 65, 5, Blocks.Stone);
        var eye = new EyeRenderer { Width = 160, Height = 120 };
        var scene = new EyeScene { World = w, EyeX = 0.5, EyeY = 65.62, EyeZ = 0.5, Yaw = 0, Pitch = 0 };
        var f = eye.Raster(scene);
        int centre = 60 * 160 + 80;
        Assert.InRange(f.Depth[centre], 4.4f, 4.6f);                       // hits the north face at z=5
        Assert.Equal(Lighting.Scale(IsoPalette.Mat(Blocks.Stone)!.Side, 0.8f), f.Px[centre]); // palette side × N/S shade, full sky light
        Assert.True(float.IsPositiveInfinity(f.Depth[0]));                  // top-left corner: sky
        // The projection agrees with the raster: the block's face centre lands on a hit pixel.
        var p = eye.Project(scene, 0.5, 65.5, 5);
        Assert.NotNull(p);
        Assert.InRange(f.Depth[p!.Value.Y * 160 + p.Value.X], 4f, 5f);
        // Turned west (yaw 90), the block is out of view.
        var g = eye.Raster(new EyeScene { World = w, EyeX = 0.5, EyeY = 65.62, EyeZ = 0.5, Yaw = 90, Pitch = 0 });
        Assert.True(float.IsPositiveInfinity(g.Depth[centre]));
        Assert.Equal((0, 65, 5, Blocks.Stone), eye.CrosshairBlock(scene) is { } hit ? (hit.X, hit.Y, hit.Z, hit.Id) : default);
    }

    [Fact]
    public void Raycaster_dims_night_and_keeps_emitters_bright()
    {
        var w = new SparseVoxels().Set(0, 65, 3, Blocks.Stone).Set(1, 65, 3, Blocks.Glowstone);
        var eye = new EyeRenderer { Width = 80, Height = 60, MinBrightness = 0 };
        uint Lum(uint c) => ((c >> 16) & 0xff) + ((c >> 8) & 0xff) + (c & 0xff);
        var day = eye.Raster(new EyeScene { World = w, EyeX = 0.5, EyeY = 65.5, EyeZ = 0.5, Yaw = 0, Time = 6000 }).Px[30 * 80 + 40];
        var night = eye.Raster(new EyeScene { World = w, EyeX = 0.5, EyeY = 65.5, EyeZ = 0.5, Yaw = 0, Time = 18000 }).Px[30 * 80 + 40];
        Assert.True(Lum(night) * 4 < Lum(day), $"night {night:x6} vs day {day:x6}");
        var p = eye.Project(new EyeScene { World = w, EyeX = 0.5, EyeY = 65.5, EyeZ = 0.5, Yaw = 0 }, 1.5, 65.5, 3)!.Value;
        var glow = eye.Raster(new EyeScene { World = w, EyeX = 0.5, EyeY = 65.5, EyeZ = 0.5, Yaw = 0, Time = 18000 }).Px[p.Y * 80 + p.X];
        Assert.Equal(IsoPalette.Mat(Blocks.Glowstone)!.Side, glow);
    }

    [Fact]
    public void Eye_render_is_fast_enough_on_an_island()
    {
        var world = new WorldState();
        world.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -12; x <= 12; x++)
            for (int z = -12; z <= 12; z++) world.SetBlock(x, 63, z, (x + z) % 3 == 0 ? Blocks.Cobblestone : Blocks.Grass, 0);
        var eye = new EyeRenderer();
        var s = new EyeScene { World = new WorldVoxels(world), EyeX = 0.5, EyeY = 65.62, EyeZ = 0.5, Yaw = 30, Pitch = 20 };
        eye.Raster(s);
        var best = Enumerable.Range(0, 5).Select(_ => { eye.Raster(s); return eye.LastMs; }).Min();
        // ~10 ms on the dev box (320x240, parallel bands); the bound leaves room for a busy CI box running other tests.
        Assert.True(best < 120, $"{best:0} ms");
        var img = eye.Render(s);
        Assert.Equal((320, 240), (img.Width, img.Height));
        Assert.InRange(img.Tokens, 100, 110);
    }

    // ------------------------------------------------------------------ top view + map

    private static WorldState Island()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -4; x <= 4; x++)
            for (int z = -4; z <= 4; z++) w.SetBlock(x, 63, z, Blocks.Cobblestone, 0);
        w.SetBlock(2, 64, 0, Blocks.Chest, 0);
        w.SetBlock(-2, 64, 0, Blocks.WoodenDoor, 0);
        return w;
    }

    [Fact]
    public void Top_view_draws_void_grid_labels_and_caches()
    {
        var w = Island();
        var r = new VisionRenderer();
        var scene = new VisionScene
        {
            Radius = 8, Cx = 0, Cy = 64, Cz = 0, Blocks = w.PlanningView, Self = (0, 64, 0),
            Marks = [new VisionMark("house chest", 2, 64, 0, 2, 64, 0)], Pointed = (1, 63, 1), PointedBy = "kiwi",
            Entities = [new VisionEntity("player", "kiwi", 3.5, 64, 3.5)],
        };
        var img = r.Render(scene);
        Assert.True(img.Width <= VisionRenderer.MaxSide && img.Height <= VisionRenderer.MaxSide, $"{img.Width}x{img.Height}");
        Assert.Contains("house chest 2,64,0", img.Labels); // named: chip + caption (a plain "chest" only gets its C letter)
        Assert.Contains("N up", img.Legend);
        var (iw, _, px) = Png.Decode(img.Png);
        int cs = VisionRenderer.CellSize(8);
        // Void cell (x=-8, z=-8 is the top-left map cell) vs a floor cell: clearly different colours.
        int ml = img.Width - 12 - 17 * cs;
        uint voidPx = px[(11 + cs / 2 + 1) * iw + ml + cs / 2] & 0xffffff;
        uint floorPx = px[(11 + 8 * cs + 3) * iw + ml + 4 * cs + 3] & 0xffffff;
        Assert.NotEqual(voidPx, floorPx);
        Assert.True(Lum(voidPx) < 60, $"void {voidPx:x6}");
        Assert.Same(img, r.Render(scene)); // unchanged area: cached
        Assert.Equal(1, r.CacheHits);
        w.SetBlock(3, 63, 3, Blocks.Air, 0);
        Assert.NotSame(img, r.Render(scene)); // world changed: re-rendered

        static uint Lum(uint c) => (((c >> 16) & 0xff) + ((c >> 8) & 0xff) + (c & 0xff)) / 3;
    }

    [Fact]
    public void Iso_and_side_views_render_small_pngs()
    {
        var w = Island();
        var r = new VisionRenderer();
        foreach (var v in new[] { VisionView.Iso, VisionView.Side })
        {
            var img = r.Render(new VisionScene { View = v, Radius = 12, Cx = 0, Cy = 64, Cz = 0, Blocks = w.PlanningView, Self = (0, 64, 0), Light = new WorldVoxels(w) });
            Assert.True(img.Width <= 400 && img.Height <= 400, $"{v} {img.Width}x{img.Height}");
            Assert.True(img.Tokens <= 210, $"{v} {img.Tokens}");
            Assert.NotNull(Png.Header(img.Png));
        }
    }

    [Fact]
    public void Ascii_map_marks_walls_doors_floor_and_void()
    {
        var w = Island();
        var text = AsciiMap.Render(w.PlanningView, 0, 64, 0, 15, (0, 64, 0), [new VisionEntity("player", "kiwi", 3.5, 64, 3.5)]);
        var rows = text.Split('\n');
        Assert.StartsWith("map 15x15 x-7..7 z-7..7", rows[0]);
        string Row(int z) => rows[2 + z + 7][4..19];
        Assert.Equal("   ....@.C..   ", Row(0).Replace('D', '.')); // void, floor, @ at x=0, chest
        Assert.Equal('D', Row(0)[7 - 2]);
        Assert.Equal('d', Row(3)[7 + 3]);
        Assert.Contains("d=kiwi", text);
        Assert.Equal("               ", Row(-7));
    }

    // ------------------------------------------------------------------ textures: fallback + extraction + git hygiene

    [Fact]
    public void No_jar_means_no_atlas_and_flat_colours()
    {
        var dir = Directory.CreateTempSubdirectory("mc-noassets").FullName;
        try
        {
            var o = new TextureOptions { ClientJar = Path.Combine(dir, "missing.jar"), AssetsDir = dir, AllowDownload = false, SearchLauncherDirs = false };
            Assert.Null(TextureLocator.Load(o));
            var img = new VisionRenderer().Render(new VisionScene { Radius = 8, Cx = 0, Cy = 64, Cz = 0, Blocks = Island().PlanningView, Atlas = null });
            Assert.NotNull(Png.Header(img.Png)); // palette renderer
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Atlas_reads_only_the_pngs_from_a_jar_and_extracts_them()
    {
        var dir = Directory.CreateTempSubdirectory("mc-jar").FullName;
        try
        {
            // A synthetic "jar": a 256×256 terrain.png whose tile 1 (stone) is pure red, plus an unrelated class file.
            var terrain = new uint[256 * 256];
            for (int y = 0; y < 16; y++) for (int x = 16; x < 32; x++) terrain[y * 256 + x] = 0xff0000;
            var jar = Path.Combine(dir, "client.jar");
            using (var z = ZipFile.Open(jar, ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("terrain.png").Open()) s.Write(Png.Encode(terrain, 256, 256));
                using (var s = z.CreateEntry("net/minecraft/client/Minecraft.class").Open()) s.Write([0xca, 0xfe]);
            }
            var assets = Path.Combine(dir, "assets");
            var atlas = TextureLocator.Load(new TextureOptions { ClientJar = jar, AssetsDir = assets, AllowDownload = false, SearchLauncherDirs = false });
            Assert.NotNull(atlas);
            Assert.Equal(0xff0000u, atlas!.Sample(1, 0.5f, 0.5f) & 0xffffff);
            Assert.True(File.Exists(Path.Combine(assets, "terrain.png")));
            Assert.False(File.Exists(Path.Combine(assets, "Minecraft.class")));
            // Next time the extracted copy is enough (no jar needed).
            File.Delete(jar);
            Assert.NotNull(TextureLocator.Load(new TextureOptions { AssetsDir = assets, AllowDownload = false, SearchLauncherDirs = false }));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Texture_files_are_gitignored_and_not_in_the_source_tree()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MineClaw.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var ignore = File.ReadAllText(Path.Combine(root!.FullName, ".gitignore"));
        foreach (var rule in new[] { "/data/assets/", "**/terrain.png", "**/items.png", "*.jar" }) Assert.Contains(rule, ignore);
        foreach (var top in new[] { "src", "tests", "tools", "deploy", "docs" })
        {
            var d = Path.Combine(root.FullName, top);
            if (!Directory.Exists(d)) continue;
            var bad = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => Path.GetFileName(f) is "terrain.png" or "items.png" or "char.png" || f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(bad);
        }
    }

    // ------------------------------------------------------------------ tool + history

    [Fact]
    public async Task See_tool_returns_an_image_and_look_map_returns_text()
    {
        var bot = new BotOptions();
        bot.Vision.Textures = false;
        bot.Vision.ImagesPerHour = 2;
        var (h, c) = TestBot.Create(bot);
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) c.World.SetBlock(x, 64, z, Blocks.Cobblestone, 0);
        Assert.True(h.Runtime.Tools.TryGet("see", out var see));
        Assert.Equal(ToolTiers.Main | ToolTiers.Worker, h.Runtime.Tools.TiersOf(see));

        var r = await h.Run("see", new { view = "top", radius = 8 });
        Assert.True(r.Ok, r.Text);
        Assert.NotNull(r.Image);
        Assert.Contains("@=you 0,65,0", r.Text);
        Assert.StartsWith("[picture omitted: see top", r.Image!.Stub);
        var eye = await h.Run("see", new { view = "eye" });
        Assert.NotNull(eye.Image);
        // Budget: 2 per hour → the third answers with the text map instead.
        var third = await h.Run("see", new { view = "iso" });
        Assert.Null(third.Image);
        Assert.Contains("image budget", third.Text);

        var map = await h.Run("look", new { map = true });
        Assert.Contains("map 15x15", map.Text);
        Assert.Contains("@", map.Text);
    }

    [Fact]
    public void Images_leave_history_as_stubs_when_squeezed()
    {
        var img = new LlmImage([1, 2, 3], 384, 384, "[picture omitted: see top r12 at 0,64,0]");
        Assert.Equal(197, img.Tokens);
        var msgs = new List<LlmMessage> { LlmMessage.User(new LlmToolResult("t1", "top r12 ...") { Image = img }) };
        Assert.True(ContextManager.EstimateTokens(msgs) >= 197);
        ContextManager.StripImages(msgs);
        var r = msgs[0].ToolResults.Single();
        Assert.Null(r.Image);
        Assert.EndsWith("[picture omitted: see top r12 at 0,64,0]", r.Content);
    }
}
