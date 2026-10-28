using System.Diagnostics;
using System.Globalization;
using System.Text;
using MineClaw.Bot;
using MineClaw.Bot.Perception;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

/// <summary>
/// The 3D vision samples and numbers (no API, no server):
///   dotnet run --project tools/VisionAB -- render3d [--db=data/alice-snap/memory-20261028-054550.db] [--out=docs/vision]
/// Writes the AI's sheet for the gen area, the main island and a player by the mob drop tower, close-ups (player model,
/// cobble gen, a 2×2 infinite-water pool, drops), the render-type chart, a block-id inventory of the islands, and the
/// token estimates + render times per sheet.
/// </summary>
static class Render3D
{
    public static int Run(string root, string outDir, Func<string, string?> arg)
    {
        var db = arg("db") ?? Path.Combine(root, "data", "alice-snap", "memory-20261028-054550.db");
        outDir = arg("out") is { } o ? (Path.IsPathRooted(o) ? o : Path.Combine(root, o)) : Path.Combine(root, "docs", "vision");
        Directory.CreateDirectory(outDir);
        var w = LiveGen.Load(db);
        var atlas = TextureLocator.Load(new TextureOptions { AssetsDir = Path.Combine(root, "data", "assets"), AllowDownload = false }, Console.WriteLine);
        Console.WriteLine($"{w.RememberedCount} columns; atlas {(atlas == null ? "none" : $"{atlas.Size}px, items {(atlas.Items != null)}, skin {(atlas.Skin != null)}, mob textures {(atlas.Mobs?.Count ?? 0)}")}");
        var report = new StringBuilder();

        Inventory(w, report);

        var dig = LiveGen.DigCells(db).ToHashSet();
        var (regions, marks) = LiveGen.Regions(db);
        var roles = new RoleClassifier
        {
            Blocks = w.PlanningView,
            IsGenCell = p => dig.Contains((p.X, p.Y, p.Z)),
            IsGenGeometry = p => BlockDescriber.IsGenCell(w, p),
            Verdict = (p, _) => regions.Any(r => p.X >= r.X0 && p.X <= r.X1 && p.Y >= r.Y0 && p.Y <= r.Y1 && p.Z >= r.Z0 && p.Z <= r.Z1) && !dig.Contains((p.X, p.Y, p.Z))
                ? new ProtectionVerdict(ProtectionLevel.Soft, "structure", "part of a remembered build") : ProtectionVerdict.None,
        };
        // Remembered columns carry no light: light them like a live day (sky above the top opaque block).
        var light = new BlockAccessVoxels(w.PlanningView);

        int Ground(int x, int z, int from = 90)
        {
            for (int y = from; y > 1; y--)
                if (w.PlanningView.GetBlock(x, y - 1, z) is var id and not 0 && !BlockGeometry.IsFluid(id) && BlockGeometry.Classify(id) is BlockShape.Cube or BlockShape.CutoutCube)
                    return y;
            return 64;
        }

        var sheets = new ContactSheet { EyeThreads = 8 };
        var sheets1 = new ContactSheet { EyeThreads = 1 };
        void Sheet(string name, (int X, int Y, int Z) focus, (double X, double Y, double Z, float Yaw, float Pitch) me, List<VisionEntity> ents, int radius = 6)
        {
            var self = ((int)Math.Floor(me.X), (int)Math.Floor(me.Y), (int)Math.Floor(me.Z));
            var eye = new EyeScene
            {
                World = light, Atlas = atlas, EyeX = me.X, EyeY = me.Y + 1.62, EyeZ = me.Z, Yaw = me.Yaw, Pitch = me.Pitch, Viewer = "maid_chan", Entities = ents,
            };
            var q = new ContactSheetRequest
            {
                Blocks = w.PlanningView, Focus = focus, Radius = radius, Self = self, Entities = ents, Marks = marks, Target = focus, Atlas = atlas, Light = light,
                Time = 6000, Roles = roles.Map, Eye = eye, SelfPose = new PerspectivePanel.Body(me.X, me.Y, me.Z, me.Yaw, me.Pitch, "maid_chan"),
            };
            sheets.Render(q); // warm-up (JIT, caches)
            var img = sheets.Render(q);
            double ms8 = sheets.LastMs;
            sheets1.Render(q);
            double ms1 = sheets1.LastMs;
            File.WriteAllBytes(Path.Combine(outDir, name + "-after.png"), img.Png);
            File.WriteAllText(Path.Combine(outDir, name + "-after.txt"), img.Legend);
            report.AppendLine(CultureInfo.InvariantCulture, $"{name}: {img.Width}x{img.Height} ~{img.Tokens} tok, render {ms8:0} ms (8 threads) / {ms1:0} ms (1 thread)");
            // The "before" pictures are the old renderer's (copied from data/alice-snap and data/visionab: that code is gone).
        }

        // 1. The gen area: the bot at the taught stand spot, the dual cobble gen.
        int gy = Ground(3, 4, 70);
        Sheet("gen", (5, 64, 7), (3.5, gy, 4.5, -30f, 35f), [], 5);
        // 2. The main island around the house, the bot by the door.
        int hy = Ground(6, 1, 70);
        Sheet("island", (7, 64, -6), (6.5, hy, 1.5, 180f, 15f), [], 10);
        // 3. A player standing by the mob drop tower.
        int py = Ground(-21, 13, 70), my = Ground(-16, 10, 70);
        var fox = new VisionEntity("player", "poofimafox", -20.5, py, 13.5) { Yaw = 140f, Pitch = 5f, HeldItem = 4, Seed = 7 };
        Sheet("tower", (-21, py, 16), (-15.5, my, 10.5, 50f, 10f), [fox], 7);

        var drops = new List<VisionEntity>
        {
            new("item", "sapling", 2.5, Ground(2, 3, 70) + 0.125, 3.4) { ItemId = 6, ItemCount = 2, Seed = 11 },
            new("item", "cobblestone", 3.6, Ground(3, 2, 70) + 0.125, 2.4) { ItemId = 4, ItemCount = 12, Seed = 12 },
            new("item", "coal", 1.4, Ground(1, 4, 70) + 0.125, 4.6) { ItemId = 263, ItemCount = 3, Seed = 13 },
        };
        // Reading aids up close: one large orbit panel of the gen with the grid, rulers, labels, gizmo and rings.
        {
            var gctx = new PanelContext
            {
                Blocks = w.PlanningView, Light = light, Focus = (5, 64, 7), FeetY = 64, Radius = 5, Roles = roles.Map, Marks = marks, Atlas = atlas,
                Entities = [.. drops, fox with { X = 1.5, Y = gy, Z = 7.5, Yaw = 270f }, new VisionEntity("mob", "zombie", 8.5, 64, 2.5) { Mob = "zombie", Yaw = 90, Burning = true, Seed = 3 }],
                Body = new PerspectivePanel.Body(3.5, gy, 4.5, -30f, 30f, "maid_chan"), Spots = [(6, 64, 8)],
            };
            var aidsImg = sheets.Compose([new PanelJob { Ctx = gctx, Rig = PerspectivePanel.For("orbit4", gctx.Focus, 64, 5, null, null)!.Value }], 300_000, "reading aids sample");
            File.WriteAllBytes(Path.Combine(outDir, "reading-aids.png"), aidsImg.Png);
            report.AppendLine("reading aids legend: " + aidsImg.Legend);

            // A composite: the gen from above, 69's eyes (a body by the farm), the tower from the NE, 3 orbits around main.
            var mainBody = new PerspectivePanel.Body(6.5, hy, 1.5, 180f, 15f, "maid_chan");
            var c69 = new PerspectivePanel.Body(-2.5, Ground(-2, -8, 70), -8.5, 200f, 25f, "69");
            var towerCtx = new PanelContext { Blocks = w.PlanningView, Light = light, Focus = (-21, py, 18), FeetY = py, Radius = 6, Roles = roles.Map, Marks = marks, Atlas = atlas, Entities = [fox], Staleness = "seen by 67 12 min ago" };
            var genCtx = gctx;
            var mainCtx = new PanelContext { Blocks = w.PlanningView, Light = light, Focus = (6, hy, 1), FeetY = hy, Radius = 5, Roles = roles.Map, Marks = marks, Atlas = atlas, Body = mainBody };
            var c69Ctx = new PanelContext { Blocks = w.PlanningView, Light = light, Focus = ((int)Math.Floor(c69.X), (int)c69.Y, (int)Math.Floor(c69.Z)), FeetY = (int)c69.Y, Radius = 6, Roles = roles.Map, Marks = marks, Atlas = atlas, Body = c69 };
            var jobs = new List<PanelJob>
            {
                new() { Ctx = genCtx, Rig = PerspectivePanel.Cam(genCtx.Focus, 64, "above", 13, "CAM @GEN") },
                new() { Ctx = c69Ctx, Rig = PerspectivePanel.EyeOf(c69, "EYE 69") },
                new() { Ctx = towerCtx, Rig = PerspectivePanel.Cam(towerCtx.Focus, py, "NE", 22, "CAM @TOWER", 6) },
            };
            jobs.AddRange(PerspectivePanel.Orbits(mainCtx.Focus, hy, 5, 3, "ORBIT @MAIN").Select(g => new PanelJob { Ctx = mainCtx, Rig = g }));
            sheets.Compose(jobs, ContactSheet.DefaultPixelBudget, "composite");
            var comp = sheets.Compose(jobs, ContactSheet.DefaultPixelBudget, "see composite (6 panels)");
            File.WriteAllBytes(Path.Combine(outDir, "composite.png"), comp.Png);
            File.WriteAllText(Path.Combine(outDir, "composite.txt"), comp.Legend);
            report.AppendLine(CultureInfo.InvariantCulture, $"composite: {comp.Width}x{comp.Height} ~{comp.Tokens} tok, {sheets.LastMs:0} ms (8 threads)");
        }

        // The "iso" view (see view=iso, supervisor/LiveHive iso tiles): the island from the SW, perspective.
        var vr = new VisionRenderer();
        var iso = vr.Render(new VisionScene
        {
            View = VisionView.Iso, Radius = 12, Cx = 4, Cy = 64, Cz = -3, Blocks = w.PlanningView, Self = (4, 64, -3), Marks = marks, Atlas = atlas, Light = light, Roles = roles.Map, Focus = (4, 64, -3),
        });
        File.WriteAllBytes(Path.Combine(outDir, "island-iso-after.png"), iso.Png);
        report.AppendLine(CultureInfo.InvariantCulture, $"iso view: {iso.Width}x{iso.Height} ~{iso.Tokens} tok");

        // Close-ups (the freecam renderer).
        var eyeR = new EyeRenderer { Width = 360, Height = 270, MaxDistance = 48 };
        var close = new EyeScene
        {
            World = light, Atlas = atlas, EyeX = fox.X + 1.9, EyeY = py + 1.5, EyeZ = fox.Z - 2.2, Entities = [fox], Fov = 55,
        };
        var (cy, cp) = EyeScene.LookAt(close.EyeX, close.EyeY, close.EyeZ, fox.X, py + 1.0, fox.Z);
        File.WriteAllBytes(Path.Combine(outDir, "player-closeup.png"), eyeR.Render(new EyeScene
        {
            World = light, Atlas = atlas, EyeX = close.EyeX, EyeY = close.EyeY, EyeZ = close.EyeZ, Yaw = cy, Pitch = cp, Entities = [fox, fox with { X = fox.X - 1.4, Name = "kiwi", Sneaking = true, HeldItem = 0, Yaw = 200f }], Fov = 55,
        }).Png);
        var (gyaw, gpitch) = EyeScene.LookAt(3.5, gy + 1.9, 4.2, 6.5, 63.5, 7.5);
        File.WriteAllBytes(Path.Combine(outDir, "gen-closeup.png"), eyeR.Render(new EyeScene
        {
            World = light, Atlas = atlas, EyeX = 3.5, EyeY = gy + 1.9, EyeZ = 4.2, Yaw = gyaw, Pitch = gpitch, Fov = 60,
        }).Png);

        // Drops by the gen: a sapling pair, a cobble stack, coal; the router text lists them.
        var (dyaw, dpitch) = EyeScene.LookAt(4.8, gy + 2.2, 6.2, 2.5, gy, 3.5);
        File.WriteAllBytes(Path.Combine(outDir, "drops-closeup.png"), eyeR.Render(new EyeScene
        {
            World = light, Atlas = atlas, EyeX = 4.8, EyeY = gy + 2.2, EyeZ = 6.2, Yaw = dyaw, Pitch = dpitch, Fov = 55, Entities = drops,
        }).Png);
        report.AppendLine("drops text: " + PerspectivePanel.DropsText(drops, (3, gy, 4)));

        // A 2×2 infinite-water pool with an overflow channel and a waterfall (synthetic, exact metas).
        var pool = Pool();
        var (pyaw, ppitch) = EyeScene.LookAt(9.5, 66.5, 6.5, 3, 61.5, 0.5);
        File.WriteAllBytes(Path.Combine(outDir, "water-pool.png"), eyeR.Render(new EyeScene
        {
            World = pool, Atlas = atlas, EyeX = 9.5, EyeY = 66.5, EyeZ = 6.5, Yaw = pyaw, Pitch = ppitch, Fov = 65,
        }).Png);

        RenderTypes(atlas, Path.Combine(outDir, "render-types.png"));
        Models(atlas, Path.Combine(outDir, "models.png"));

        // Timing of the old flat panels vs one 3D panel, for the report.
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        Console.WriteLine(report.ToString());
        return 0;
    }

    private static void Inventory(WorldState w, StringBuilder report)
    {
        var counts = new Dictionary<(int Id, int Meta), int>();
        for (int cx = -32; cx <= 32; cx++)
        for (int cz = -32; cz <= 32; cz++)
        {
            if (w.GetRemembered(cx, cz) is not { } col) continue;
            var s = col.Blocks;
            for (int i = 0; i < s.Ids.Length; i++)
                if (s.Ids[i] is var id and not 0) { var k = (id, (int)s.Meta[i]); counts[k] = counts.GetValueOrDefault(k) + 1; }
        }
        report.AppendLine("block ids on the islands (id name: render type, metas):");
        foreach (var g in counts.GroupBy(k => k.Key.Id).OrderBy(g => g.Key))
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {g.Key} {Blocks.Get(g.Key).Name}: type {BlockGeometry.RenderType(g.Key)} ({BlockGeometry.Classify(g.Key)}), {g.Sum(x => x.Value)} blocks, metas {string.Join(",", g.Select(x => x.Key.Meta).OrderBy(m => m))}");
    }

    /// <summary>The player and every mob model, facing the camera (SE), each on a grass block; plus a burning zombie.</summary>
    private static void Models(TextureAtlas? atlas, string path)
    {
        var cases = new List<(string Label, VisionEntity E, float Size)>
        {
            ("player", new VisionEntity("player", "steve", 0.5, 65, 0.5) { HeldItem = 276 }, 2.2f),
            ("sneaking", new VisionEntity("player", "robin", 0.5, 65, 0.5) { Sneaking = true }, 2.2f),
            ("zombie", new VisionEntity("mob", "zombie", 0.5, 65, 0.5) { Mob = "zombie" }, 2.2f),
            ("skeleton", new VisionEntity("mob", "skeleton", 0.5, 65, 0.5) { Mob = "skeleton" }, 2.2f),
            ("creeper", new VisionEntity("mob", "creeper", 0.5, 65, 0.5) { Mob = "creeper" }, 2f),
            ("spider", new VisionEntity("mob", "spider", 0.5, 65, 0.5) { Mob = "spider" }, 2f),
            ("pig", new VisionEntity("mob", "pig", 0.5, 65, 0.5) { Mob = "pig" }, 1.4f),
            ("cow", new VisionEntity("mob", "cow", 0.5, 65, 0.5) { Mob = "cow" }, 1.8f),
            ("sheep", new VisionEntity("mob", "sheep", 0.5, 65, 0.5) { Mob = "sheep" }, 1.8f),
            ("sheep red", new VisionEntity("mob", "sheep", 0.5, 65, 0.5) { Mob = "sheep", Variant = 14 }, 1.8f),
            ("sheared", new VisionEntity("mob", "sheep", 0.5, 65, 0.5) { Mob = "sheep", Variant = 16 }, 1.8f),
            ("chicken", new VisionEntity("mob", "chicken", 0.5, 65, 0.5) { Mob = "chicken" }, 1.1f),
            ("enderman", new VisionEntity("mob", "enderman", 0.5, 65, 0.5) { Mob = "enderman" }, 3.2f),
            ("slime", new VisionEntity("mob", "slime", 0.5, 65, 0.5) { Mob = "slime", Variant = 2 }, 1.6f),
            ("pigman", new VisionEntity("mob", "zombie_pigman", 0.5, 65, 0.5) { Mob = "zombie_pigman" }, 2.2f),
            ("wolf", new VisionEntity("mob", "wolf", 0.5, 65, 0.5) { Mob = "wolf" }, 1.3f),
            ("burning", new VisionEntity("mob", "zombie", 0.5, 65, 0.5) { Mob = "zombie", Burning = true }, 2.4f),
            ("walking N", new VisionEntity("mob", "zombie", 0.5, 65, 0.5) { Mob = "zombie", Yaw = 180, Vz = -0.1 }, 2.2f),
        };
        const int cw = 150, ch = 150, cols = 6, lab = 10;
        int rows = (cases.Count + cols - 1) / cols;
        var c = new Canvas(cols * cw, rows * (ch + lab), 0x1a1d24);
        var eye = new EyeRenderer { Width = cw, Height = ch, MaxDistance = 24, MaxThreads = 4 };
        for (int i = 0; i < cases.Count; i++)
        {
            var (label, e0, size) = cases[i];
            var e = e0.Yaw == null ? e0 with { Yaw = -35f } : e0; // facing the camera (south-east)
            var v = new SparseVoxels();
            for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++) v.Set(x, 64, z, Blocks.Grass);
            double ty = 65 + size * 0.42;
            double d = size * 1.25 + 1.4;
            double ex = 0.5 + d * 0.55, ey = ty + d * 0.3, ez = 0.5 + d * 0.8;
            if (label == "walking N") { ex = 0.5 - d * 0.8; ez = 0.5 + d * 0.2; }
            var (yaw, pitch) = EyeScene.LookAt(ex, ey, ez, 0.5, ty, 0.5);
            var fr = eye.Raster(new EyeScene { World = v, Atlas = atlas, EyeX = ex, EyeY = ey, EyeZ = ez, Yaw = yaw, Pitch = pitch, Fov = 50, Entities = [e] });
            int ox = i % cols * cw, oy = i / cols * (ch + lab);
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++) c.Set(ox + x, oy + y, fr.Px[y * cw + x]);
            c.Text(ox + 3, oy + ch + 2, label + (EntityTextures.IsReal(atlas, EntityGeometry.Create(EntityGeometry.ModelKind(e)).Texture) ? "" : " (flat)"), 0xe8ecf4);
        }
        File.WriteAllBytes(path, Png.Encode(c.Px, c.Width, c.Height));
    }

    internal static SparseVoxels Pool()
    {
        var v = new SparseVoxels();
        for (int x = -4; x <= 8; x++)
            for (int z = -3; z <= 6; z++)
                v.Set(x, 62, z, Blocks.Stone);
        // Basin walls around a 2×2 of sources at (0..1, 63, 0..1), open to the east into a channel.
        for (int x = -1; x <= 2; x++)
            for (int z = -1; z <= 2; z++)
                if (x is -1 or 2 || z is -1 or 2) v.Set(x, 63, z, Blocks.Cobblestone);
        v.Set(0, 63, 0, Blocks.Water); v.Set(1, 63, 0, Blocks.Water); v.Set(0, 63, 1, Blocks.Water); v.Set(1, 63, 1, Blocks.Water);
        v.Set(2, 63, 1, 0);
        // Channel east: flowing levels 1..4, then the edge: falling water down to the lower floor.
        for (int x = 2; x <= 5; x++) { v.Set(x, 63, 1, Blocks.FlowingWater, x - 1); v.Set(x, 63, 0, Blocks.Cobblestone); v.Set(x, 63, 2, Blocks.Cobblestone); }
        for (int x = 6; x <= 8; x++) for (int z = -3; z <= 6; z++) v.Set(x, 62, z, 0);
        for (int x = 6; x <= 8; x++) for (int z = -3; z <= 6; z++) v.Set(x, 59, z, Blocks.Stone);
        v.Set(6, 62, 1, Blocks.FlowingWater, 8); v.Set(6, 61, 1, Blocks.FlowingWater, 8); v.Set(6, 60, 1, Blocks.FlowingWater, 8);
        v.Set(6, 63, 1, Blocks.FlowingWater, 5);
        foreach (var (x, z, m) in new[] { (7, 1, 1), (8, 1, 2), (6, 0, 1), (6, 2, 1), (7, 0, 2), (7, 2, 2) }) v.Set(x, 60, z, Blocks.FlowingWater, m);
        // Lava pocket beside the pool for comparison.
        v.Set(-3, 63, 3, Blocks.Lava); v.Set(-2, 63, 3, Blocks.FlowingLava, 2);
        for (int x = -4; x <= -1; x++) for (int z = 2; z <= 4; z++) if (v.Block(x, 63, z) == 0) v.Set(x, 63, z, Blocks.Stone);
        v.Set(-1, 63, 3, Blocks.Stone);
        return v;
    }

    /// <summary>One of each render type the islands use (and the rest of the common ones) on a plain background, labelled.</summary>
    private static void RenderTypes(TextureAtlas? atlas, string path)
    {
        var cases = new List<(string Label, Action<SparseVoxels> Build)>
        {
            ("grass", v => v.Set(0, 64, 0, Blocks.Grass)),
            ("log", v => v.Set(0, 64, 0, Blocks.Log)),
            ("leaves", v => { v.Set(0, 64, 0, Blocks.Leaves); v.Set(1, 64, 0, Blocks.Leaves); }),
            ("glass", v => v.Set(0, 64, 0, Blocks.Glass)),
            ("furnace", v => v.Set(0, 64, 0, Blocks.Furnace, 3)),
            ("chest", v => v.Set(0, 64, 0, Blocks.Chest)),
            ("dbl chest", v => { v.Set(0, 64, 0, Blocks.Chest); v.Set(1, 64, 0, Blocks.Chest); }),
            ("bench", v => v.Set(0, 64, 0, Blocks.Workbench)),
            ("sapling", v => v.Set(0, 64, 0, Blocks.Sapling)),
            ("tall grass", v => v.Set(0, 64, 0, Blocks.TallGrass, 1)),
            ("flowers", v => { v.Set(0, 64, 0, Blocks.Rose); v.Set(1, 64, 0, Blocks.Dandelion); }),
            ("mushroom", v => v.Set(0, 64, 0, Blocks.RedMushroom)),
            ("sugar cane", v => { v.Set(0, 64, 0, Blocks.SugarCane); v.Set(0, 65, 0, Blocks.SugarCane); }),
            ("wheat 2", v => v.Set(0, 64, 0, Blocks.Crops, 2)),
            ("wheat 7", v => v.Set(0, 64, 0, Blocks.Crops, 7)),
            ("farmland", v => v.Set(0, 64, 0, Blocks.Farmland, 7)),
            ("torch", v => v.Set(0, 64, 0, Blocks.Torch, 5)),
            ("torch m1", v => { v.Set(-1, 64, 0, Blocks.Cobblestone); v.Set(0, 64, 0, Blocks.Torch, 1); }),
            ("torch m3", v => { v.Set(0, 64, -1, Blocks.Cobblestone); v.Set(0, 64, 0, Blocks.Torch, 3); }),
            ("redstone t", v => v.Set(0, 64, 0, Blocks.RedstoneTorch, 5)),
            ("ladder", v => { v.Set(0, 64, -1, Blocks.Planks); v.Set(0, 65, -1, Blocks.Planks); v.Set(0, 64, 0, Blocks.Ladder, 3); v.Set(0, 65, 0, Blocks.Ladder, 3); }),
            ("rails", v => { v.Set(0, 64, 0, Blocks.Rail, 0); v.Set(0, 64, 1, Blocks.Rail, 6); v.Set(1, 64, 1, Blocks.Rail, 1); }),
            ("door", v => { v.Set(0, 64, 0, Blocks.WoodenDoor, 0); v.Set(0, 65, 0, Blocks.WoodenDoor, 8); }),
            ("door open", v => { v.Set(0, 64, 0, Blocks.WoodenDoor, 4); v.Set(0, 65, 0, Blocks.WoodenDoor, 12); }),
            ("stairs", v => { v.Set(0, 64, 0, Blocks.WoodenStairs, 0); v.Set(1, 64, 0, Blocks.CobblestoneStairs, 2); }),
            ("slab", v => { v.Set(0, 64, 0, Blocks.Slab, 0); v.Set(1, 64, 0, Blocks.Slab, 3); }),
            ("fence", v => { v.Set(0, 64, 0, Blocks.Fence); v.Set(1, 64, 0, Blocks.Fence); v.Set(1, 64, 1, Blocks.Fence); }),
            ("fence gate", v => v.Set(0, 64, 0, Blocks.FenceGate, 0)),
            ("cactus", v => { v.Set(0, 64, 0, Blocks.Cactus); v.Set(0, 65, 0, Blocks.Cactus); }),
            ("sign", v => v.Set(0, 64, 0, Blocks.SignPost, 4)),
            ("wall sign", v => { v.Set(0, 64, -1, Blocks.Planks); v.Set(0, 64, 0, Blocks.WallSign, 3); }),
            ("bed", v => { v.Set(0, 64, 0, Blocks.Bed, 0); v.Set(0, 64, 1, Blocks.Bed, 8); }),
            ("glass pane", v => { v.Set(0, 64, 0, Blocks.GlassPane); v.Set(1, 64, 0, Blocks.GlassPane); }),
            ("wool", v => { v.Set(0, 64, 0, Blocks.Wool, 14); v.Set(1, 64, 0, Blocks.Wool, 11); }),
            ("snow", v => v.Set(0, 64, 0, Blocks.SnowLayer, 1)),
            ("plate+btn", v => { v.Set(0, 64, 0, Blocks.StonePressurePlate); v.Set(1, 64, -1, Blocks.Stone); v.Set(1, 64, 0, Blocks.StoneButton, 3); }),
            ("lever", v => v.Set(0, 64, 0, Blocks.Lever, 5)),
            ("trapdoor", v => v.Set(0, 64, 0, Blocks.Trapdoor, 0)),
            ("pumpkin", v => { v.Set(0, 64, 0, Blocks.Pumpkin, 0); v.Set(1, 64, 0, Blocks.JackOLantern, 0); }),
            ("vine", v => { v.Set(0, 64, -1, Blocks.Log); v.Set(0, 64, 0, Blocks.Vine, 4); }),
            ("water src", v => v.Set(0, 64, 0, Blocks.Water)),
            ("water flow", v => { v.Set(0, 64, 0, Blocks.Water); v.Set(1, 64, 0, Blocks.FlowingWater, 2); v.Set(1, 64, 1, Blocks.FlowingWater, 4); }),
            ("lava", v => { v.Set(0, 64, 0, Blocks.Lava); v.Set(1, 64, 0, Blocks.FlowingLava, 2); }),
            ("ice", v => v.Set(0, 64, 0, Blocks.Ice)),
            ("cake", v => v.Set(0, 64, 0, Blocks.Cake, 2)),
            ("spawner", v => v.Set(0, 64, 0, Blocks.MobSpawner)),
            ("iron bars", v => { v.Set(0, 64, 0, Blocks.IronBars); v.Set(1, 64, 0, Blocks.IronBars); }),
            ("bookshelf", v => v.Set(0, 64, 0, Blocks.Bookshelf)),
        };
        const int cw = 112, ch = 96, cols = 8, lab = 10;
        int rows = (cases.Count + cols - 1) / cols;
        var c = new Canvas(cols * cw, rows * (ch + lab), 0x1a1d24);
        var eye = new EyeRenderer { Width = cw, Height = ch, MaxDistance = 16, MaxThreads = 4 };
        for (int i = 0; i < cases.Count; i++)
        {
            var v = new SparseVoxels();
            cases[i].Build(v);
            double tx = 0.5, ty = 64.5, tz = 0.5;
            if (cases[i].Label is "dbl chest" or "flowers" or "stairs" or "slab" or "fence" or "glass pane" or "wool" or "pumpkin" or "lava" or "water flow" or "iron bars" or "plate+btn" or "rails" or "leaves") tx = 1;
            if (cases[i].Label is "sugar cane" or "door" or "door open" or "cactus" or "ladder") ty = 65;
            double ex = tx + 2.3, ey = ty + 1.6, ez = tz + 2.6;
            var (yaw, pitch) = EyeScene.LookAt(ex, ey, ez, tx, ty - 0.2, tz);
            var fr = eye.Raster(new EyeScene { World = v, Atlas = atlas, EyeX = ex, EyeY = ey, EyeZ = ez, Yaw = yaw, Pitch = pitch, Fov = 50 });
            int ox = i % cols * cw, oy = i / cols * (ch + lab);
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++) c.Set(ox + x, oy + y, fr.Px[y * cw + x]);
            c.Text(ox + 3, oy + ch + 2, cases[i].Label, 0xe8ecf4);
        }
        File.WriteAllBytes(path, Png.Encode(c.Px, c.Width, c.Height));
    }
}
