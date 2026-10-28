using System.Numerics;
using MineClaw.Render;
using MineClaw.Render.Models;
using Xunit;

namespace MineClaw.Bot.Tests;

/// <summary>The installed 1.12.2 client jar (read-only, never committed), loaded once; null on machines without it (CI).</summary>
internal static class Jar1122
{
    private static readonly Lazy<TextureAtlas?> _atlas = new(() =>
    {
        var o = new TextureOptions { Version = Release1122Assets.Version, AllowDownload = false };
        var jar = TextureLocator.CandidateJars(o).FirstOrDefault(j => File.Exists(j) && Release1122Assets.IsModernJar(j));
        return jar == null ? null : Release1122Assets.FromJar(jar);
    });

    public static bool Present => TextureLocator.CandidateJars(new TextureOptions { Version = Release1122Assets.Version })
        .Any(j => File.Exists(j) && Release1122Assets.IsModernJar(j));

    public static TextureAtlas Atlas => _atlas.Value ?? throw new InvalidOperationException("no 1.12.2 jar");
    public static BlockModels1122 Models => (BlockModels1122)Atlas.Models!;
}

/// <summary>A test that needs the user's 1.12.2 client jar: skipped where it isn't installed (CI).</summary>
public sealed class Jar1122FactAttribute : FactAttribute
{
    public Jar1122FactAttribute()
    {
        if (!Jar1122.Present) Skip = "the 1.12.2 client jar is not installed on this machine";
    }
}

/// <summary>Multi-version P5 render: the 1.12.2 blockstate + model loader, the meta → state mapping and the renderer seam.</summary>
public class Render1122Tests
{
    private const int OakStairs = 53, Fence = 85, Stone = 1, StoneSlab = 44, Torch = 50;

    private static BlockModels1122 M => Jar1122.Models;

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<GeoQuad> quads)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var q in quads)
            foreach (var p in new[] { q.P0, q.P1, q.P2, q.P3 }) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return (min, max);
    }

    private static List<GeoQuad> Build(SparseVoxels w, int x, int y, int z)
    {
        var l = new List<GeoQuad>();
        M.Build(w, x, y, z, l);
        return l;
    }

    [Jar1122Fact]
    public void Loads_the_jar_assets()
    {
        Assert.True(M.StateFiles > 350);
        Assert.True(M.Textures.Count > 400);
        Assert.NotNull(Jar1122.Atlas.Skin);
        Assert.Equal(32, Jar1122.Atlas.Skin!.Value.H); // steve.png cut to the b1.8.1 64×32 layout
        Assert.True(Jar1122.Atlas.Mobs!.ContainsKey("zombie"));
    }

    [Jar1122Fact]
    public void Straight_stairs_facing_east_and_south()
    {
        var w = new SparseVoxels().Set(0, 64, 0, OakStairs, 0); // meta 0: facing east, bottom
        var s = M.StateOf(OakStairs, 0, w, 0, 64, 0);
        Assert.Equal(("east", "bottom", "straight"), (s.Get("facing"), s.Get("half"), s.Get("shape")));
        Assert.Equal(new VariantRef("oak_stairs", 0, 0, false), M.Models(s).Single());
        var quads = Build(w, 0, 64, 0);
        // The step's top (y = 1) is on the east half.
        Assert.All(quads.Where(q => q.P0.Y > 0.99f && q.P1.Y > 0.99f && q.P2.Y > 0.99f), q => Assert.True(Bounds([q]).Min.X >= 0.499f));
        Assert.All(quads, q => Assert.Equal(QuadTex.Block, q.Tex));
        Assert.Equal("blocks/planks_oak", M.Textures.Name(quads[0].Tile));

        var south = new SparseVoxels().Set(0, 64, 0, OakStairs, 2); // meta 2: facing south
        var ss = M.StateOf(OakStairs, 2, south, 0, 64, 0);
        Assert.Equal("south", ss.Get("facing"));
        Assert.Equal(new VariantRef("oak_stairs", 0, 90, true), M.Models(ss).Single());
        Assert.All(Build(south, 0, 64, 0).Where(q => q.P0.Y > 0.99f && q.P1.Y > 0.99f && q.P2.Y > 0.99f), q => Assert.True(Bounds([q]).Min.Z >= 0.499f));
    }

    [Jar1122Fact]
    public void Upside_down_stairs_have_the_step_below()
    {
        var w = new SparseVoxels().Set(0, 64, 0, OakStairs, 4); // top half, facing east
        var s = M.StateOf(OakStairs, 4, w, 0, 64, 0);
        Assert.Equal("top", s.Get("half"));
        Assert.Equal(180, M.Models(s).Single().X);
        var quads = Build(w, 0, 64, 0);
        // The full slab is on top; the step hangs below on the east half.
        Assert.All(quads.Where(q => q.P0.Y < 0.01f && q.P1.Y < 0.01f && q.P2.Y < 0.01f), q => Assert.True(Bounds([q]).Min.X >= 0.499f));
        Assert.Contains(quads, q => Bounds([q]).Min.Y > 0.99f && Bounds([q]).Min.X < 0.01f);
    }

    [Jar1122Fact]
    public void Stair_corners_from_the_neighbours()
    {
        // Facing east with stairs facing north in front (east): an outer corner on the left.
        var outer = new SparseVoxels().Set(0, 64, 0, OakStairs, 0).Set(1, 64, 0, OakStairs, 3);
        var so = M.StateOf(OakStairs, 0, outer, 0, 64, 0);
        Assert.Equal("outer_left", so.Get("shape"));
        Assert.Equal("oak_outer_stairs", M.Models(so).Single().Model);
        // Only the north-east quarter rises to the top.
        var top = Build(outer, 0, 64, 0).Where(q => Bounds([q]).Min.Y > 0.99f).ToList();
        Assert.NotEmpty(top);
        Assert.All(top, q => { var b = Bounds([q]); Assert.True(b.Min.X >= 0.499f && b.Max.Z <= 0.501f); });

        // Facing east with stairs facing north behind (west): an inner corner on the left.
        var inner = new SparseVoxels().Set(0, 64, 0, OakStairs, 0).Set(-1, 64, 0, OakStairs, 3);
        var si = M.StateOf(OakStairs, 0, inner, 0, 64, 0);
        Assert.Equal("inner_left", si.Get("shape"));
        Assert.Equal("oak_inner_stairs", M.Models(si).Single().Model);
        // A different half in front is no corner.
        var mixed = new SparseVoxels().Set(0, 64, 0, OakStairs, 0).Set(1, 64, 0, OakStairs, 3 | 4);
        Assert.Equal("straight", M.StateOf(OakStairs, 0, mixed, 0, 64, 0).Get("shape"));
    }

    [Jar1122Fact]
    public void Fence_multipart_connects_to_fences_and_solid_blocks()
    {
        var w = new SparseVoxels().Set(0, 64, 0, Fence).Set(1, 64, 0, Fence).Set(0, 64, -1, Stone).Set(-1, 64, 0, 20 /* glass: no */);
        var s = M.StateOf(Fence, 0, w, 0, 64, 0);
        Assert.Equal(("true", "true", "false", "false"), (s.Get("north"), s.Get("east"), s.Get("south"), s.Get("west")));
        var models = M.Models(s);
        Assert.Equal(3, models.Count); // the post + the north and east sides
        Assert.Contains(new VariantRef("oak_fence_post", 0, 0, false), models);
        Assert.Contains(models, m => m.Model == "oak_fence_side" && m.Y == 0 && m.UvLock);
        Assert.Contains(models, m => m.Model == "oak_fence_side" && m.Y == 90);
        var (min, max) = Bounds(Build(w, 0, 64, 0));
        Assert.Equal(1f, max.X, 3);  // the east bars reach the neighbour
        Assert.Equal(0f, min.Z, 3);  // the north bars reach the stone
        Assert.True(min.X >= 0.37f);  // nothing toward the glass
        Assert.True(max.Z <= 0.63f);
        Assert.Equal(BlockShape.Custom, M.Classify(Fence, 0));
    }

    [Jar1122Fact]
    public void A_top_slab()
    {
        var w = new SparseVoxels().Set(0, 64, 0, StoneSlab, 8);
        var s = M.StateOf(StoneSlab, 8, w, 0, 64, 0);
        Assert.Equal(("stone_slab", "top"), (s.File, s.Get("half")));
        Assert.Equal("upper_slab_stone", M.Models(s).Single().Model);
        var (min, max) = Bounds(Build(w, 0, 64, 0));
        Assert.Equal(0.5f, min.Y, 3);
        Assert.Equal(1f, max.Y, 3);
        Assert.Equal(BlockShape.Custom, M.Classify(StoneSlab, 8));
        // The bottom half and a cobblestone slab (variant 3).
        Assert.Equal(0.5f, Bounds(Build(new SparseVoxels().Set(0, 64, 0, StoneSlab, 0), 0, 64, 0)).Max.Y, 3);
        Assert.Equal("cobblestone_slab", M.StateOf(StoneSlab, 3).File);
    }

    [Jar1122Fact]
    public void A_torch_on_a_wall_leans_out_of_it()
    {
        // meta 1 = facing east: on the west neighbour's east face, leaning east.
        var w = new SparseVoxels().Set(0, 64, 0, Torch, 1).Set(-1, 64, 0, Stone);
        var s = M.StateOf(Torch, 1, w, 0, 64, 0);
        Assert.Equal("east", s.Get("facing"));
        Assert.Equal(new VariantRef("normal_torch_wall", 0, 0, false), M.Models(s).Single());
        var quads = Build(w, 0, 64, 0);
        // The stick's 2×2 cap (tilted −22.5° about z) sits further from the wall than its foot, which touches the wall.
        static Vector3 Centre(GeoQuad q) => (q.P0 + q.P1 + q.P2 + q.P3) / 4;
        var cap = quads.Single(q => q.Normal.Y > 0.8f);
        var foot = quads.Single(q => q.Normal.Y < -0.8f);
        Assert.True(Centre(foot).X < 0.1f, $"foot against the wall: {Centre(foot).X}");
        Assert.True(Centre(cap).X > Centre(foot).X + 0.2f, $"cap {Centre(cap).X} vs foot {Centre(foot).X}");
        Assert.True(cap.Normal.X > 0.3f); // leaning east, out of the wall
        Assert.Equal(1f, cap.Shade);       // "shade": false
        // Facing south (meta 3): the same model turned 90°, the base on the north side.
        var south = new SparseVoxels().Set(0, 64, 0, Torch, 3);
        Assert.Equal(90, M.Models(M.StateOf(Torch, 3, south, 0, 64, 0)).Single().Y);
        var southQuads = Build(south, 0, 64, 0);
        Assert.True(Centre(southQuads.Single(q => q.Normal.Y < -0.8f)).Z < 0.1f);
        Assert.True(southQuads.Single(q => q.Normal.Y > 0.8f).Normal.Z > 0.3f);
        // Standing (meta 5): upright, centred.
        var upQuads = Build(new SparseVoxels().Set(0, 64, 0, Torch, 5), 0, 64, 0);
        var upCap = Centre(upQuads.Single(q => q.Normal.Y > 0.99f));
        Assert.Equal(0.5f, upCap.X, 2);
        Assert.Equal(0.5f, upCap.Z, 2);
    }

    [Jar1122Fact]
    public void Cubes_tints_and_variants()
    {
        Assert.Equal(BlockShape.Cube, M.Classify(Stone, 0));
        Assert.Equal(BlockShape.Cube, M.Classify(2, 0));            // grass (top tint + side overlay merged)
        Assert.Equal(BlockShape.CutoutCube, M.Classify(20, 0));     // glass
        Assert.Equal(BlockShape.CutoutCube, M.Classify(18, 0));     // leaves
        Assert.Equal(BlockShape.TranslucentCube, M.Classify(79, 0)); // ice
        Assert.Equal(BlockShape.Custom, M.Classify(8, 0));          // water: the procedural path
        Assert.Equal("granite", M.StateOf(Stone, 1).File);
        Assert.Equal("birch_planks", M.StateOf(5, 2).File);
        Assert.Equal("red_wool", M.StateOf(35, 14).File);

        var w = new SparseVoxels().Set(0, 64, 0, 2);
        var top = M.CubeFace(w, 0, 64, 0, 2, 0, Face.Up);
        Assert.Equal(BiomeTints.Grass(), top.Tint);
        Assert.Equal(0x91BD59u, BiomeTints.Grass());
        var side = M.CubeFace(w, 0, 64, 0, 2, 0, Face.North);
        Assert.Equal("blocks/grass_side", M.Textures.Name(side.Tile));
        Assert.Equal("blocks/grass_side_overlay", M.Textures.Name(side.Overlay));
        // Snow on top: the snowy side.
        w.Set(0, 65, 0, 78);
        Assert.Equal("blocks/grass_side_snowed", M.Textures.Name(M.CubeFace(w, 0, 64, 0, 2, 0, Face.North).Tile));
        // A log lying east-west (meta 4): its rings face east and west.
        var log = new SparseVoxels().Set(0, 64, 0, 17, 4);
        Assert.Equal("blocks/log_oak_top", M.Textures.Name(M.CubeFace(log, 0, 64, 0, 17, 4, Face.East).Tile));
        Assert.Equal("blocks/log_oak", M.Textures.Name(M.CubeFace(log, 0, 64, 0, 17, 4, Face.Up).Tile));
        // Spruce leaves keep their fixed colour.
        Assert.Equal(BiomeTints.Spruce, M.CubeFace(log, 0, 64, 0, 18, 1, Face.Up).Tint);
        // A furnace facing south shows its front on the south face.
        Assert.Equal("blocks/furnace_front_off", M.Textures.Name(M.CubeFace(log, 0, 64, 0, 61, 3, Face.South).Tile));
    }

    [Jar1122Fact]
    public void Doors_combine_their_halves()
    {
        // Lower: facing (meta & 3 = 0 → east), closed; upper: hinge right (bit 1).
        var w = new SparseVoxels().Set(0, 64, 0, 64, 0).Set(0, 65, 0, 64, 8 | 1);
        var lower = M.StateOf(64, 0, w, 0, 64, 0);
        var upper = M.StateOf(64, 9, w, 0, 65, 0);
        Assert.Equal(("east", "right", "lower"), (lower.Get("facing"), lower.Get("hinge"), lower.Get("half")));
        Assert.Equal(("east", "right", "upper"), (upper.Get("facing"), upper.Get("hinge"), upper.Get("half")));
        Assert.NotEmpty(Build(w, 0, 65, 0));
    }

    /// <summary>A small 1.12.2 fixture chunk rendered by the shared raycaster through the 1.12.2 assets.</summary>
    [Jar1122Fact]
    public void Renders_a_fixture_chunk()
    {
        var w = new SparseVoxels();
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
            {
                w.Set(x, 62, z, Stone);
                w.Set(x, 63, z, 2);
            }
        for (int y = 64; y < 68; y++) w.Set(4, y, 8, 17, 0); // an oak trunk
        for (int x = 3; x <= 5; x++) for (int z = 7; z <= 9; z++) w.Set(x, 68, z, 18, 4);
        w.Set(7, 64, 6, OakStairs, 2).Set(8, 64, 6, OakStairs, 2).Set(9, 64, 6, StoneSlab, 8).Set(10, 64, 6, StoneSlab, 0);
        for (int x = 6; x <= 10; x++) w.Set(x, 64, 10, Fence);
        w.Set(11, 64, 8, 54, 2).Set(12, 64, 8, 61, 3).Set(12, 65, 8, Torch, 5).Set(6, 64, 8, 35, 14).Set(2, 64, 4, 31, 1).Set(9, 63, 3, 9, 0);
        w.Set(5, 64, 5, 38, 0).Set(13, 64, 4, 89, 0).Set(13, 64, 5, 20, 0);

        var eye = new EyeRenderer { Width = 160, Height = 120, MaxThreads = 2 };
        var (yaw, pitch) = EyeScene.LookAt(8, 70, 0, 8, 64, 8);
        var frame = eye.Raster(new EyeScene { World = w, Atlas = Jar1122.Atlas, EyeX = 8, EyeY = 70, EyeZ = -1, Yaw = yaw, Pitch = pitch });
        int hit = frame.Depth.Count(d => !float.IsInfinity(d));
        Assert.True(hit > frame.Px.Length / 3, $"only {hit} of {frame.Px.Length} pixels hit a block");
        var colours = frame.Px.Where((_, i) => !float.IsInfinity(frame.Depth[i])).Distinct().Count();
        Assert.True(colours > 200, $"{colours} distinct colours: textures not sampled?");
        // No missing-texture magenta anywhere.
        Assert.DoesNotContain(frame.Px, p => (p & 0xffffff) == 0xff00ff);
        // The grass ground reads green (biome tint on the top faces).
        var ground = Enumerable.Range(0, frame.Px.Length).Where(i => EyeRenderer.FromCellKey(frame.Cells![i]) is var c && frame.Cells[i] != EyeRenderer.NoCell && c.Y == 63)
            .Select(i => frame.Px[i]).ToList();
        Assert.NotEmpty(ground);
        double g = ground.Average(p => (p >> 8) & 0xff), r = ground.Average(p => (p >> 16) & 0xff), b = ground.Average(p => p & 0xff);
        Assert.True(g > r && g > b, $"ground r{r:0} g{g:0} b{b:0}");

        // MINECLAW_RENDER_DUMP=<dir>: also write the picture (with a player and a few mobs) for a look by eye.
        if (Environment.GetEnvironmentVariable("MINECLAW_RENDER_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            VisionEntity Mob(string mob, double x, double z) => new("mob", mob, x, 64, z) { Mob = mob, Yaw = 180 };
            var ents = new[]
            {
                new VisionEntity("player", "fox", 9.5, 64, 4.5) { Yaw = 200 }, Mob("zombie", 6.5, 3.5), Mob("creeper", 11.5, 4.5), Mob("pig", 3.5, 6.5),
                Mob("zombie_pigman", 13.5, 6.5), Mob("sheep", 2.5, 11.5),
                new VisionEntity("item", "planks", 7.5, 64, 4) { ItemId = 5, ItemCount = 1 }, new VisionEntity("item", "stick", 8, 64, 3.5) { ItemId = 280, ItemCount = 1 },
            };
            var big = new EyeRenderer { Width = 640, Height = 480 };
            File.WriteAllBytes(Path.Combine(dump, "fixture-1122.png"),
                big.Render(new EyeScene { World = w, Atlas = Jar1122.Atlas, EyeX = 8, EyeY = 70, EyeZ = -1, Yaw = yaw, Pitch = pitch, Entities = ents }).Png);
        }

        // The same scene through the b1.8.1 path (no models) still renders: the seam is the atlas's Models only.
        var flat = new EyeRenderer { Width = 64, Height = 48, MaxThreads = 1 }.Raster(new EyeScene { World = w, EyeX = 8, EyeY = 70, EyeZ = -1, Yaw = yaw, Pitch = pitch });
        Assert.Contains(flat.Depth, d => !float.IsInfinity(d));
    }
}
