using System.Numerics;
using System.Text.Json;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The unified 3D renderer behind every picture (sheet panels, "iso", freecam): textured perspective panels, b1.8.1 render
/// types and fluid heights, entity models with their skins, drops, fire, the reading aids and the composite. Golden-ish:
/// geometry and pixel classes, never exact images. Textures are synthetic (Mojang's are never in the repo).
/// </summary>
public class Vision3DTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ synthetic textures

    private const uint Skin = 0xc69680, Shirt = 0x00b0b0, Pants = 0x3030b0, HeadSide = 0x40c040;

    /// <summary>A 256×256 terrain atlas: each tile a noisy pattern of its own hue; fancy leaves (52) with alpha holes, the torch
    /// tile (80) only its 2 px stick, sugar cane (73) stripes; plus a 64×32 skin with known face/shirt/trouser colours.</summary>
    internal static TextureAtlas Atlas()
    {
        var px = new uint[256 * 256];
        var rnd = new Random(5);
        for (int t = 0; t < 256; t++)
        {
            uint baseC = (uint)((t * 2654435761) >> 8) & 0xffffff | 0x404040;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    uint c = Lighting.Scale(baseC, 0.75f + (float)rnd.NextDouble() * 0.25f) | 0xff000000;
                    if (t == 52 && (x + y) % 3 == 0) c &= 0x00ffffff;
                    if (t == 80 && !(x is 7 or 8 && y >= 6)) c &= 0x00ffffff;
                    if (t == 73 && x % 4 != 1) c &= 0x00ffffff;
                    if (t is >= 88 and <= 95 && x % 3 != 0) c &= 0x00ffffff;
                    px[(t / 16 * 16 + y) * 256 + t % 16 * 16 + x] = c;
                }
        }
        // Stone (tile 1) bright red: an easy "what's behind" colour.
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[y * 256 + 16 + x] = 0xffe02020;
        var skin = new uint[64 * 32];
        Array.Fill(skin, 0xff000000 | HeadSide);
        void Rect(int x0, int y0, int w, int h, uint c) { for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) skin[y * 64 + x] = 0xff000000 | c; }
        for (int y = 0; y < 16; y++) for (int x = 32; x < 64; x++) skin[y * 64 + x] = 0; // hat layer transparent
        Rect(8, 8, 8, 8, Skin);      // head front
        Rect(16, 16, 24, 16, Shirt); // body
        Rect(40, 16, 16, 16, Skin);  // arms
        Rect(0, 16, 16, 16, Pants);  // legs
        return new TextureAtlas(256, px, null, (64, 32, skin));
    }

    private static SparseVoxels Floor(int r = 6, int y = 63, int id = Blocks.Cobblestone)
    {
        var v = new SparseVoxels();
        for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++) v.Set(x, y, z, id);
        return v;
    }

    private static EyeScene Look(IVoxelWorld w, double ex, double ey, double ez, double tx, double ty, double tz, IReadOnlyList<VisionEntity>? ents = null, TextureAtlas? atlas = null, float fov = 60)
    {
        var (yaw, pitch) = EyeScene.LookAt(ex, ey, ez, tx, ty, tz);
        return new EyeScene { World = w, Atlas = atlas, EyeX = ex, EyeY = ey, EyeZ = ez, Yaw = yaw, Pitch = pitch, Entities = ents ?? [], Fov = fov };
    }

    // ------------------------------------------------------------------ the sheet

    [Fact]
    public void Default_sheet_is_six_textured_panels_within_the_token_budget()
    {
        var c = VisionEval.HoleFaces();
        var sheets = new ContactSheet();
        var sheet = sheets.Render(new ContactSheetRequest
        {
            Blocks = c.World.PlanningView, Focus = c.Focus, Radius = 6, Self = c.Self, Atlas = Atlas(), Light = new WorldVoxels(c.World),
            Eye = new EyeScene { World = new WorldVoxels(c.World), EyeX = c.Self.X + 0.5, EyeY = c.Self.Y + 1.62, EyeZ = c.Self.Z + 0.5, Yaw = 0, Pitch = 40 },
            Roles = VisionEval.Roles(c).Map,
        });
        output.WriteLine($"{sheet.Width}x{sheet.Height} ~{sheet.Tokens} tok, {sheets.LastMs:0} ms\n{sheet.Legend}");
        // Today's default sheet was 522×419 ≈ 292 tokens: at most ~20 % above.
        Assert.InRange(sheet.Tokens, 250, 352);
        var (cols, rows, pw, ph) = ContactSheet.Layout(6);
        Assert.Equal((3, 2), (cols, rows));
        var (w, _, px) = Png.Decode(sheet.Png);
        for (int i = 0; i < 5; i++)
        {
            int ox = i % 3 * (pw + 4), oy = i / 3 * (ph + 4);
            var colours = new HashSet<uint>();
            for (int y = oy + 12; y < oy + ph - 12; y += 2) for (int x = ox; x < ox + pw; x += 2) colours.Add(px[y * w + x] & 0xffffff);
            Assert.True(colours.Count > 60, $"panel {i} has a flat fill ({colours.Count} colours)");
        }
        foreach (var name in new[] { "EYE", "SHOULDER", "ORBIT from NW looking SE", "ORBIT from SE looking NW", "HIGH", "TOP (straight down" }) Assert.Contains(name, sheet.Legend);
        // The TOP panel is the 3D renderer too: textured, not a flat palette fill.
        int tox = 2 * (pw + 4), toy = ph + 4;
        var top = new HashSet<uint>();
        for (int y = toy + 12; y < toy + ph - 12; y += 2) for (int x = tox; x < tox + pw; x += 2) top.Add(px[y * w + x] & 0xffffff);
        Assert.True(top.Count > 60, $"TOP has {top.Count} colours");
    }

    [Fact]
    public void More_panels_means_smaller_panels_and_the_total_budget_holds()
    {
        int last = int.MaxValue;
        for (int n = 1; n <= ContactSheet.MaxPanels; n++)
        {
            var (cols, rows, w, h) = ContactSheet.Layout(n);
            long total = (long)(cols * w + 4 * (cols - 1)) * (rows * (h + 4) + 11);
            output.WriteLine($"{n}: {cols}x{rows} of {w}x{h} = {total} px ~{(total + 749) / 750} tok");
            Assert.InRange(total, 150_000, 262_000);
            Assert.True(w <= last);
            Assert.True(w >= ContactSheet.MinPanelWidth);
            last = w;
        }
    }

    // ------------------------------------------------------------------ entities

    [Fact]
    public void A_player_renders_where_it_stands_as_a_multi_part_biped_with_its_skin_on_the_right_faces()
    {
        var p = new VisionEntity("player", "fox", 0.5, 64, 0.5) { Yaw = 0 }; // facing south (+z), toward the camera
        var quads = new List<GeoQuad>();
        EntityGeometry.Build(p, quads, 0.5, 5);
        float Cy(GeoQuad q) => (q.P0.Y + q.P1.Y + q.P2.Y + q.P3.Y) / 4;
        Assert.True(quads.Count >= 6 * 7, "head, hat, body, 2 arms, 2 legs");
        Assert.Contains(quads, q => Cy(q) > 1.45 && Cy(q) < 1.95); // head
        Assert.Contains(quads, q => Cy(q) > 0.8 && Cy(q) < 1.4);   // torso
        Assert.Contains(quads, q => Cy(q) > 0.05 && Cy(q) < 0.7);  // legs
        Assert.InRange(quads.Max(q => Math.Max(Math.Max(q.P0.Y, q.P1.Y), Math.Max(q.P2.Y, q.P3.Y))), 1.8f, 1.95f); // 1.875 tall like RenderPlayer

        var w = Floor();
        var atlas = Atlas();
        var eye = new EyeRenderer { Width = 160, Height = 200, MaxThreads = 1 };
        var s = Look(w, 0.5, 65.2, 3.2, 0.5, 65.0, 0.5, [p], atlas, 50);
        var fr = eye.Raster(s);
        uint At(double x, double y, double z)
        {
            var pr = eye.Project(s, x, y, z)!.Value;
            Assert.Equal(EyeRenderer.NoCell, fr.Cells![pr.Y * fr.Width + pr.X]); // the entity, not a block
            return fr.Px[pr.Y * fr.Width + pr.X];
        }
        static string Class(uint c)
        {
            int r = (int)(c >> 16 & 255), g = (int)(c >> 8 & 255), b = (int)(c & 255);
            if (r > g && g > b && r > 70) return "skin";
            if (g > r + 30 && b > r + 30) return "shirt";
            if (b > r + 30 && b > g + 30) return "pants";
            return $"other {c:x6}";
        }
        Assert.Equal("skin", Class(At(0.5, 64 + 1.62, 0.5 + 0.25)));
        Assert.Equal("shirt", Class(At(0.5, 64 + 1.1, 0.5 + 0.13)));
        Assert.Equal("pants", Class(At(0.5 - 0.12, 64 + 0.35, 0.5 + 0.13)));
    }

    [Theory]
    [InlineData(180f, 0, -1)] // north
    [InlineData(0f, 0, 1)]    // south
    [InlineData(90f, -1, 0)]  // west
    [InlineData(270f, 1, 0)]  // east
    public void A_mob_faces_its_packet_yaw(float yaw, int dx, int dz)
    {
        var m = EntityGeometry.Create("zombie");
        var e = new VisionEntity("mob", "zombie", 0, 64, 0) { Mob = "zombie", Yaw = yaw };
        EntityGeometry.Pose(m, e);
        var quads = new List<GeoQuad>();
        EntityGeometry.Emit(m, EntityGeometry.Root(m, e), quads);
        // The head's front face: UVs inside the 8..16 × 8..16 rectangle of the 64×32 texture.
        var front = quads.First(q => { var c = (q.T0 + q.T2) / 2; return c.X > 8 / 64f && c.X < 16 / 64f && c.Y > 8 / 32f && c.Y < 16 / 32f; });
        var n = front.Normal;
        Assert.True(n.X * dx + n.Z * dz > 0.9, $"yaw {yaw}: face normal {n}");
    }

    [Fact]
    public void Burning_entities_draw_fire_and_drops_render_where_they_lie()
    {
        var w = Floor();
        var eye = new EyeRenderer { Width = 160, Height = 120, MaxThreads = 1 };
        var zombie = new VisionEntity("mob", "zombie", 0.5, 64, 0.5) { Mob = "zombie", Yaw = 0 };
        static int Fire(EyeRenderer.Frame f) => f.Px.Count(c => (c >> 16 & 255) > 200 && (c >> 8 & 255) > 90 && (c & 255) < 90);
        var s = Look(w, 0.5, 65.5, 4, 0.5, 64.9, 0.5, [zombie], Atlas());
        int calm = Fire(eye.Raster(s)), burning = Fire(eye.Raster(Look(w, 0.5, 65.5, 4, 0.5, 64.9, 0.5, [zombie with { Burning = true }], Atlas())));
        output.WriteLine($"fire pixels {calm} → {burning}");
        Assert.True(burning > calm + 150, $"{calm} → {burning}");

        var sapling = new VisionEntity("item", "sapling", 1.5, 64.125, 1.5) { ItemId = Blocks.Sapling, ItemCount = 2, Seed = 4 };
        var s2 = Look(w, 1.5, 65.6, 3.2, 1.5, 64.2, 1.5, [sapling], Atlas());
        var a = eye.Raster(s2).Px.ToArray();
        var b = eye.Raster(Look(w, 1.5, 65.6, 3.2, 1.5, 64.2, 1.5, [], Atlas())).Px;
        var pr = eye.Project(s2, 1.5, 64.3, 1.5)!.Value;
        int changed = 0;
        for (int y = pr.Y - 6; y <= pr.Y + 6; y++) for (int x = pr.X - 6; x <= pr.X + 6; x++) if (a[y * 160 + x] != b[y * 160 + x]) changed++;
        Assert.True(changed > 20, $"sprite pixels at the drop: {changed}");
        var facts = VisionRouter.Drops([sapling], (1, 64, 1), 6);
        Assert.Equal("1 drop: 2 sapling at 1,64,1 (0 m from the focus)", facts);
        Assert.Equal(VisionIntent.Drops, VisionRouter.Classify("any drops lying on the ground?"));
        Assert.Contains("1 zombie burning (daylight", VisionRouter.Mobs([zombie with { Burning = true }], (0, 64, 0), 6));
    }

    // ------------------------------------------------------------------ blocks

    [Fact]
    public void Leaves_are_alpha_cut_so_what_is_behind_shows_through()
    {
        var w = Floor(3, 62, Blocks.Stone);
        w.Set(0, 64, 0, Blocks.Leaves);
        for (int x = -2; x <= 2; x++) for (int y = 63; y <= 66; y++) w.Set(x, y, -3, Blocks.Stone);
        var eye = new EyeRenderer { Width = 120, Height = 120, MaxThreads = 1 };
        var s = Look(w, 0.5, 64.5, 2.2, 0.5, 64.5, 0.5, null, Atlas(), 40);
        var fr = eye.Raster(s);
        int leaf = 0, behind = 0;
        for (int y = 40; y < 80; y++)
            for (int x = 40; x < 80; x++)
            {
                float d = fr.Depth[y * 120 + x];
                if (d < 2.5) leaf++;
                else if (d > 4) behind++;
            }
        Assert.True(leaf > 300 && behind > 100, $"leaf {leaf}, seen through {behind}");
    }

    [Fact]
    public void A_slab_is_half_a_block_high()
    {
        var w = Floor(3, 63, Blocks.Stone);
        w.Set(0, 64, 0, Blocks.Slab, 0);
        var eye = new EyeRenderer { Width = 41, Height = 41, MaxThreads = 1 };
        float Centre(double y) => eye.Raster(new EyeScene { World = w, EyeX = 0.5, EyeY = y, EyeZ = 3.5, Yaw = 180, Pitch = 0 }).Depth[20 * 41 + 20];
        Assert.InRange(Centre(64.25), 2.4f, 2.6f); // hits the slab's south face at z = 1
        Assert.True(Centre(64.75) > 3.5f);         // passes over it
    }

    [Fact]
    public void Fluid_heights_follow_render_blocks()
    {
        // A source in a basin (stone all round, the cells sharing its corners solid): every corner at 8/9 ≈ 0.889.
        var w = new SparseVoxels();
        for (int x = -1; x <= 2; x++) for (int z = -1; z <= 1; z++) w.Set(x, 64, z, Blocks.Stone);
        w.Set(0, 64, 0, Blocks.Water, 0);
        Assert.Equal(8 / 9f, BlockGeometry.FluidHeight(0), 3);
        Assert.True(BlockGeometry.FluidHeight(3) < BlockGeometry.FluidHeight(0) - 0.3f);
        Assert.Equal(BlockGeometry.FluidHeight(0), BlockGeometry.FluidHeight(8), 3); // falling counts as a source
        // The source's corners: the solid neighbours are skipped, so they stay at the source height.
        var lone = BlockGeometry.FluidCorners(w, 0, 64, 0, Blocks.Water);
        Assert.Equal(8 / 9f, lone.H00, 3);
        Assert.Equal(8 / 9f, lone.H11, 3);
        // A flowing cell (meta 3) east of the source: its west corners average with the source (high), east corners stay low.
        w.Set(1, 64, 0, Blocks.FlowingWater, 3);
        var slope = BlockGeometry.FluidCorners(w, 1, 64, 0, Blocks.FlowingWater);
        Assert.True(slope.H00 > slope.H10 + 0.2f, $"west {slope.H00} vs east {slope.H10}");
        // Water above: the cell is full height.
        w.Set(1, 65, 0, Blocks.Water, 0);
        Assert.Equal(1f, BlockGeometry.FluidCorners(w, 1, 64, 0, Blocks.FlowingWater).H00);
        // And the rendered top of a source really sits below the block top: a ray just under y+1 passes over it.
        var pool = Floor(3, 63, Blocks.Stone);
        for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++) pool.Set(x, 64, z, Blocks.Stone);
        pool.Set(0, 64, 0, Blocks.Water, 0);
        var q = new List<GeoQuad>();
        BlockGeometry.Build(pool, 0, 64, 0, q);
        Assert.Contains(q, x => x.Tex == QuadTex.WaterStill && Math.Abs(x.P0.Y - 8 / 9f) < 1e-3 && x.Pass == RenderPass.Translucent);
    }

    [Fact]
    public void Render_types_have_their_b181_shapes()
    {
        List<GeoQuad> Q(Action<SparseVoxels> set, int x = 0, int y = 64, int z = 0)
        {
            var v = new SparseVoxels();
            set(v);
            var l = new List<GeoQuad>();
            BlockGeometry.Build(v, x, y, z, l);
            return l;
        }
        // Sugar cane / saplings: two diagonal two-sided planes, no cube; cane is untinted (tile 73 is already green in b1.8.1).
        var cane = Q(v => v.Set(0, 64, 0, Blocks.SugarCane));
        Assert.Equal(2, cane.Count);
        Assert.All(cane, c => { Assert.True(c.TwoSided); Assert.Equal(73, c.Tile); Assert.Equal(0u, c.Tint); Assert.True(Math.Abs(c.Normal.X) > 0.6 && Math.Abs(c.Normal.Z) > 0.6); });
        // Crops: four planes in a # at 0.25 / 0.75, stage tile 88 + meta, sunk 1/16.
        var crops = Q(v => v.Set(0, 64, 0, Blocks.Crops, 5));
        Assert.Equal(4, crops.Count);
        Assert.All(crops, c => Assert.Equal(93, c.Tile));
        Assert.Contains(crops, c => Math.Abs(c.P0.X - 0.25f) < 1e-4 && Math.Abs(c.P1.X - 0.25f) < 1e-4);
        Assert.Contains(crops, c => Math.Abs(c.P0.Z - 0.75f) < 1e-4 && Math.Abs(c.P1.Z - 0.75f) < 1e-4);
        Assert.Equal(-1 / 16f, crops.Min(c => Math.Min(c.P1.Y, c.P2.Y)), 4);
        // Torches: meta 5 upright; meta 1 on the west block's face, its foot at x≈0 and its head leaning east.
        var up = Q(v => v.Set(0, 64, 0, Blocks.Torch, 5));
        var side = up.Where(c => c.Tile == 80 && Math.Abs(c.Normal.Y) < 0.1).ToList();
        Assert.All(side, c => Assert.Equal(c.P0.X, c.P1.X, 3));
        var wall = Q(v => { v.Set(-1, 64, 0, Blocks.Cobblestone); v.Set(0, 64, 0, Blocks.Torch, 1); });
        var xs = wall.Where(c => Math.Abs(c.Normal.Y) < 0.95).SelectMany(c => new[] { (c.P0.Y, c.P0.X), (c.P1.Y, c.P1.X) }).ToList();
        float footX = xs.Where(p => p.Item1 < 0.25f).Average(p => p.Item2), headX = xs.Where(p => p.Item1 > 1f).Average(p => p.Item2);
        Assert.InRange(footX, -0.1f, 0.1f);
        Assert.InRange(headX, 0.3f, 0.5f);
        Assert.InRange(wall.Min(c => Math.Min(c.P1.Y, c.P2.Y)), 0.15f, 0.25f); // wall torches sit 0.2 up
        // Ladder: one plane 0.05 off the wall it hangs on (meta 3 = on the north block).
        var ladder = Q(v => { v.Set(0, 64, -1, Blocks.Planks); v.Set(0, 64, 0, Blocks.Ladder, 3); });
        Assert.Single(ladder);
        Assert.All(new[] { ladder[0].P0, ladder[0].P1, ladder[0].P2, ladder[0].P3 }, p => Assert.Equal(0.05f, p.Z, 3));
        // Rails: flat 1/16 up.
        Assert.All(Q(v => v.Set(0, 64, 0, Blocks.Rail, 0)), c => Assert.Equal(1 / 16f, c.P0.Y, 3));
        // Doors: closed meta 0 → a 3/16 slab on the west side (state 3); open meta 4 → the north side (state 0).
        var closed = Q(v => v.Set(0, 64, 0, Blocks.WoodenDoor, 0));
        Assert.Equal(0.1875f, closed.Max(c => Math.Max(c.P0.X, c.P2.X)), 3);
        var open = Q(v => v.Set(0, 64, 0, Blocks.WoodenDoor, 4));
        Assert.Equal(0.1875f, open.Max(c => Math.Max(c.P0.Z, c.P2.Z)), 3);
        // Stairs meta 0: the high half on the east.
        var stairs = Q(v => v.Set(0, 64, 0, Blocks.WoodenStairs, 0));
        Assert.Contains(stairs, c => c.Normal.Y > 0.9 && c.P0.Y > 0.99 && c.P0.X >= 0.5f && c.P2.X >= 0.5f);
        Assert.Contains(stairs, c => c.Normal.Y > 0.9 && Math.Abs(c.P0.Y - 0.5f) < 1e-3 && c.P0.X <= 0.5f && c.P2.X <= 0.5f);
        // Fences: a post, and bars only toward a fence neighbour.
        var lone = Q(v => v.Set(0, 64, 0, Blocks.Fence));
        var joined = Q(v => { v.Set(0, 64, 0, Blocks.Fence); v.Set(1, 64, 0, Blocks.Fence); });
        Assert.Equal(6, lone.Count);
        Assert.Equal(18, joined.Count);
        Assert.Contains(joined, c => c.P0.X >= 0.999f || c.P2.X >= 0.999f);
        // Cactus: sides 1/16 inside the cell.
        var cactus = Q(v => v.Set(0, 64, 0, Blocks.Cactus));
        Assert.Contains(cactus, c => c.Tile == 70 && Math.Abs(c.P0.Z - 1 / 16f) < 1e-4 && Math.Abs(c.P2.Z - 1 / 16f) < 1e-4);
        // Classification: what the raycaster's fast path may take.
        Assert.Equal(BlockShape.Cube, BlockGeometry.Classify(Blocks.Cobblestone));
        Assert.Equal(BlockShape.CutoutCube, BlockGeometry.Classify(Blocks.Leaves));
        Assert.Equal(BlockShape.Custom, BlockGeometry.Classify(Blocks.Slab));
    }

    // ------------------------------------------------------------------ reading aids

    private static (EyeRenderer Eye, PerspectivePanel.Rig Rig, PanelContext Ctx, ContactSheet Sheet) AidsScene(IReadOnlyList<VisionEntity>? ents = null)
    {
        var v = Floor(8);
        var ctx = new PanelContext
        {
            Blocks = new SparseAccess(v), Light = v, Focus = (2, 64, 3), FeetY = 64, Radius = 6, Entities = ents ?? [], Atlas = Atlas(),
        };
        var rig = PerspectivePanel.For("orbit4", ctx.Focus, 64, 6, null, null)!.Value;
        return (new EyeRenderer { Width = 320, Height = 240, MaxThreads = 1 }, rig, ctx, new ContactSheet());
    }

    /// <summary>An <see cref="IBlockAccess"/> over sparse voxels (everything loaded).</summary>
    private sealed class SparseAccess(SparseVoxels v) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => v.Block(x, y, z);
        public int GetMeta(int x, int y, int z) => v.Meta(x, y, z);
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }

    [Fact]
    public void Grid_lines_and_the_focus_crosshair_land_on_their_world_coordinates()
    {
        var (eye, rig, ctx, _) = AidsScene();
        var input = new PerspectivePanel.Input
        {
            World = ctx.World, Atlas = ctx.Atlas, Focus = ctx.Focus, Slab = ctx.Slab, Entities = [],
            Aids = new ReadingAids.Input { Focus = ctx.Focus, FeetY = 64, Radius = 6, Slab = ctx.Slab },
        };
        var img = PerspectivePanel.Render(eye, rig, input, out _, out var facts);
        var s = new EyeScene { World = ctx.World, EyeX = rig.X, EyeY = rig.Y, EyeZ = rig.Z, Yaw = rig.Yaw, Pitch = rig.Pitch, Fov = rig.Fov };
        var fr = eye.Raster(s);
        // Round trip: the x = 5 major grid line on the floor at several z: project, read the depth there, unproject.
        int checkedPts = 0;
        for (int z = -2; z <= 8; z += 2)
        {
            if (eye.Project(s, 5, 64, z) is not { } p) continue;
            float d = fr.Depth[p.Y * fr.Width + p.X];
            var (wx, wy, wz) = eye.Unproject(s, p.X + 0.5, p.Y + 0.5, d);
            Assert.True(Math.Abs(wx - 5) < 0.5 && Math.Abs(wy - 64) < 0.5 && Math.Abs(wz - z) < 0.5, $"({wx:0.00},{wy:0.00},{wz:0.00}) vs 5,64,{z}");
            checkedPts++;
        }
        Assert.True(checkedPts >= 3);
        // The focus crosshair: its centre pixel at the projected top-face centre of the focus block, in the focus colour.
        var fc = eye.Project(s, 2.5, 65.0, 3.5)!.Value;
        Assert.Equal(0x5fe8ffu, img.Pixels![fc.Y * img.Width + fc.X] & 0xffffff);
        Assert.Contains("focus 2,64,3", facts[0]);
    }

    [Fact]
    public void Labels_dodge_each_other_and_are_capped()
    {
        var placer = new VisionRenderer.Placer(0, 0, 200, 150);
        var rects = new List<(int X, int Y, int W, int H)>();
        for (int i = 0; i < 5; i++)
            if (placer.FindNear(80, 60, 40, 11) is { } at) rects.Add((at.X, at.Y, 40, 11));
        Assert.True(rects.Count >= 4);
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                Assert.False(a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H, "overlapping labels");
            }
        // Thirty drops in view: at most ReadingAids.MaxLabels labels (the focus first).
        var drops = Enumerable.Range(0, 30).Select(i => new VisionEntity("item", "cobblestone", -3 + i % 6, 64.125, -2 + i / 6) { ItemId = 4, ItemCount = i + 1, Seed = i }).ToList();
        var (eye, rig, ctx, _) = AidsScene(drops);
        var input = new PerspectivePanel.Input
        {
            World = ctx.World, Atlas = ctx.Atlas, Focus = ctx.Focus, Slab = ctx.Slab, Entities = drops,
            Aids = new ReadingAids.Input { Focus = ctx.Focus, FeetY = 64, Radius = 6, Slab = ctx.Slab },
        };
        PerspectivePanel.Render(eye, rig, input, out _, out var facts);
        Assert.InRange(facts.Count, 3, ReadingAids.MaxLabels);
        Assert.StartsWith("focus", facts[0]);
    }

    [Fact]
    public void A_composite_mixes_spots_with_captions_in_one_image()
    {
        var (_, _, ctx, sheet) = AidsScene();
        var body = new PerspectivePanel.Body(-2.5, 64, -1.5, 300, 20, "69");
        var jobs = new List<PanelJob>
        {
            new() { Ctx = ctx, Rig = PerspectivePanel.Cam(ctx.Focus, 64, "above", 12, "CAM @GEN") },
            new() { Ctx = ctx, Rig = PerspectivePanel.EyeOf(body, "EYE 69") },
            new() { Ctx = ctx, Rig = PerspectivePanel.Cam(ctx.Focus, 64, "NE", 14, "CAM @TOWER") },
        };
        jobs.AddRange(PerspectivePanel.Orbits(ctx.Focus, 64, 6, 3, "ORBIT @MAIN").Select(r => new PanelJob { Ctx = ctx, Rig = r }));
        var img = sheet.Compose(jobs, ContactSheet.DefaultPixelBudget, "see composite");
        output.WriteLine($"{img.Width}x{img.Height} ~{img.Tokens} tok\n{img.Legend}");
        Assert.InRange(img.Tokens, 250, 352);
        foreach (var cap in new[] { "[1] CAM CAM @GEN from above", "[2] EYE 69 (first person of 69", "[3] CAM CAM @TOWER from NE", "[4] ORBIT 1/3", "[6] ORBIT 3/3" })
            Assert.Contains(cap, img.Legend);
        // The eye panel looks from that body: its camera is the body's eye.
        var eyeRig = PerspectivePanel.EyeOf(body, "EYE 69");
        Assert.Equal((-2.5, -1.5, 300f), (eyeRig.X, eyeRig.Z, eyeRig.Yaw));
        Assert.InRange(eyeRig.Y, 65.61, 65.63);
        // Caption chips are drawn: the top-left of every panel isn't the background.
        var (w, _, px) = Png.Decode(img.Png);
        var (_, _, pw, ph) = ContactSheet.Layout(6);
        for (int i = 0; i < 6; i++) Assert.NotEqual(0x07080bu, px[(i / 3 * (ph + 4) + 5) * w + i % 3 * (pw + 4) + 5] & 0xffffff);
    }

    [Theory]
    [InlineData(VisionView.Iso)]
    [InlineData(VisionView.Top)]
    [InlineData(VisionView.Side)]
    [InlineData(VisionView.Layers)]
    public void Single_views_are_textured_3d_renders_not_flat_tiles(VisionView view)
    {
        var v = Floor(6, 63, Blocks.Cobblestone);
        for (int x = -2; x <= 2; x++) v.Set(x, 64, -1, Blocks.Planks);
        var img = new VisionRenderer().Render(new VisionScene
        {
            View = view, Radius = 6, Cx = 0, Cy = 64, Cz = 0, Blocks = new SparseAccess(v), Light = v, Atlas = Atlas(), Focus = (0, 64, 0),
        });
        Assert.Contains("rendered like the game", img.Legend);
        // Texture-sampled: many distinct colours inside one block face, not one palette colour per face.
        var r = new VisionRenderer();
        var s = new VisionScene { View = view, Radius = 6, Cx = 0, Cy = 64, Cz = 0, Blocks = new SparseAccess(v) };
        var (fx, fy, fz) = view switch { VisionView.Side => (1.5, 63.5, 1.0), VisionView.Layers => (0.5, 65.0, -0.5), _ => (3.5, 64.0, 3.5) };
        var (px, py) = r.Pixel(s, fx, fy, fz)!.Value;
        var colours = new HashSet<uint>();
        for (int y = py - 4; y <= py + 4; y++) for (int x = px - 4; x <= px + 4; x++) colours.Add(img.Pixels![y * img.Width + x] & 0xffffff);
        Assert.True(colours.Count >= 8, $"{view}: {colours.Count} colours in one face");
    }

    [Fact]
    public void Views_parse_from_strings_and_objects_and_refuse_too_many_panels()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var v = VisionHub.ParseViews(J("""{"views":["eye 67","cam mob chamber NE 14","orbit gen 2","follow robinchan","high"]}"""), out var err)!;
        Assert.Null(err);
        Assert.Equal(new VisionHub.PanelRequest("eye", "67"), v[0]);
        Assert.Equal(new VisionHub.PanelRequest("cam", "mob chamber", "NE", 14), v[1]);
        Assert.Equal(new VisionHub.PanelRequest("orbit", "gen", N: 2), v[2]);
        Assert.Equal(new VisionHub.PanelRequest("follow", "robinchan"), v[3]);
        Assert.Equal(new VisionHub.PanelRequest("high"), v[4]);
        var o = VisionHub.ParseViews(J("""{"views":[{"cam":{"at":"tower","from":"above","dist":20}},{"eye":"69"},{"orbit":{"at":"4,64,6","n":2}}]}"""), out err)!;
        Assert.Equal(new VisionHub.PanelRequest("cam", "tower", "above", 20), o[0]);
        Assert.Equal(new VisionHub.PanelRequest("eye", "69"), o[1]);
        Assert.Equal(2, o[2].Count);
        Assert.Null(VisionHub.ParseViews(J("""{"views":["orbit gen 4","eye 67","high","top"]}"""), out err));
        Assert.Contains("at most 6", err);
        Assert.Null(VisionHub.ParseViews(J("""{"views":["sideways"]}"""), out err));
        Assert.Contains("unknown view", err);
    }
}
