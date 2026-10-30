using System.Numerics;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.Render.Models;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-29, Fox on the viewer (the Explorer and the AI's pictures): "chests don't combine into one big chest… so the bot can't
/// tell", "when the bot's in the tunnel… there's no x-ray on the blocks in front of the camera for things like orbit and ISO",
/// "I also didn't see the weather". Double chests (<see cref="LargeChest"/>, the 1.12.2 large-chest model, b1.8.1's half tiles),
/// the outside cameras' x-ray (<see cref="ViewCut"/>) and the weather (<see cref="Weather"/>: sky, light, streaks, captions).
/// </summary>
public class ViewerChestXRayWeatherTests(ITestOutputHelper output)
{
    /// <summary>The same sparse blocks as an <see cref="IBlockAccess"/> (the bot's side of the pairing rule).</summary>
    private sealed class SparseBlocks(SparseVoxels v) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => v.Block(x, y, z);
        public int GetMeta(int x, int y, int z) => v.Meta(x, y, z);
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }

    private const int Chest = 54, Trapped = 146, Ender = 130, Stone = 1;

    /// <summary>Set MINECLAW_VIEWER_DUMP to a folder to look at the pictures these tests render.</summary>
    private static void Dump(string name, byte[] png)
    {
        if (Environment.GetEnvironmentVariable("MINECLAW_VIEWER_DUMP") is { Length: > 0 } dir) File.WriteAllBytes(Path.Combine(dir, name), png);
    }

    // ------------------------------------------------------------------ double chests

    [Fact]
    public void Adjacent_chests_of_one_kind_pair_horizontally_and_nothing_else_does()
    {
        var w = new SparseVoxels();
        w.Set(0, 64, 0, Chest, 2).Set(1, 64, 0, Chest, 2);         // a pair along x facing north
        w.Set(5, 64, 0, Chest, 2).Set(6, 64, 0, Trapped, 2);       // a chest and a trapped chest never pair
        w.Set(10, 64, 0, Ender, 2).Set(11, 64, 0, Ender, 2);       // ender chests never pair
        w.Set(15, 64, 0, Chest, 2).Set(15, 65, 0, Chest, 2);       // stacked: two singles
        w.Set(20, 64, 0, Chest, 2).Set(21, 64, 1, Chest, 2);       // diagonal: two singles
        w.Set(30, 64, 0, Chest, 2).Set(31, 64, 0, Chest, 2).Set(32, 64, 0, Chest, 2); // a third chest never steals a half
        w.Set(40, 64, 0, Trapped, 4).Set(40, 64, 1, Trapped, 4);   // trapped pair along z facing west
        Assert.Equal((1, 0), LargeChest.Partner(w, 0, 64, 0));
        Assert.Equal((-1, 0), LargeChest.Partner(w, 1, 64, 0));
        Assert.Null(LargeChest.Partner(w, 5, 64, 0));
        Assert.Null(LargeChest.Partner(w, 6, 64, 0));
        Assert.Null(LargeChest.Partner(w, 10, 64, 0));
        Assert.Null(LargeChest.Partner(w, 15, 64, 0));
        Assert.Null(LargeChest.Partner(w, 15, 65, 0));
        Assert.Null(LargeChest.Partner(w, 20, 64, 0));
        // Three in a row (unbuildable by placement): every block a single, the answer the bot's chest memory gives (World.ChestPairs).
        Assert.Null(LargeChest.Partner(w, 30, 64, 0));
        Assert.Null(LargeChest.Partner(w, 31, 64, 0));
        Assert.Null(LargeChest.Partner(w, 32, 64, 0));
        foreach (var x in new[] { 0, 1, 5, 6, 10, 15, 20, 30, 31, 32, 40 })
            Assert.Equal(ChestPairs.Partner(new SparseBlocks(w), new BlockPos(x, 64, 0)) is { } q ? (q.X - x, q.Z) : ((int, int)?)null, LargeChest.Partner(w, x, 64, 0));
        Assert.Equal((0, 1), LargeChest.Partner(w, 40, 64, 0));
        Assert.Equal((0, -1), LargeChest.Partner(w, 40, 64, 1));
    }

    [Fact]
    public void A_pair_faces_its_west_or_north_half_and_a_facing_along_the_pair_is_turned_like_BlockChest()
    {
        var w = new SparseVoxels().Set(0, 64, 0, Chest, 3).Set(1, 64, 0, Chest, 2); // halves disagree: the west one wins (south)
        Assert.Equal(3, LargeChest.PairFacing(w, 0, 64, 0, (1, 0)));
        Assert.Equal(3, LargeChest.PairFacing(w, 1, 64, 0, (-1, 0)));
        var edit = new SparseVoxels().Set(0, 64, 0, Chest, 4).Set(1, 64, 0, Chest, 5); // both face along x (a world edit)
        Assert.Equal(3, LargeChest.PairFacing(edit, 0, 64, 0, (1, 0)));
        var z = new SparseVoxels().Set(0, 64, 0, Chest, 2).Set(0, 64, 1, Chest, 4); // along z: the north half points along it, the other across
        Assert.Equal(4, LargeChest.PairFacing(z, 0, 64, 0, (0, 1)));
        // The model half: facing north, the west cell is the model's west half; facing south (turned 180°) it is the east half.
        Assert.Equal("west", LargeChest.ModelHalf(2, (1, 0)));
        Assert.Equal("east", LargeChest.ModelHalf(2, (-1, 0)));
        Assert.Equal("east", LargeChest.ModelHalf(3, (1, 0)));
        Assert.Equal("west", LargeChest.ModelHalf(5, (0, 1)));  // facing east (y 90): model east → world south
        Assert.Equal("west", LargeChest.ModelHalf(4, (0, -1))); // facing west (y 270): model east → world north
    }

    [Fact]
    public void The_1122_state_of_a_paired_chest_is_its_half_of_the_large_model()
    {
        var w = new SparseVoxels().Set(0, 64, 0, Chest, 2).Set(1, 64, 0, Chest, 2).Set(5, 64, 0, Trapped, 3).Set(5, 64, 1, Trapped, 3).Set(9, 64, 0, Chest, 2);
        static bool Solid(int id, int meta) => id == Stone;
        var a = BlockStates1122.Resolve(Chest, 2, w, 0, 64, 0, Solid);
        var b = BlockStates1122.Resolve(Chest, 2, w, 1, 64, 0, Solid);
        Assert.Equal(("mineclaw_chest_normal_double", "north", "west"), (a.File, a.Get("facing"), a.Get("half")));
        Assert.Equal(("mineclaw_chest_normal_double", "north", "east"), (b.File, b.Get("facing"), b.Get("half")));
        // A trapped pair along z facing south (y 180): world north cell → model east half.
        var t = BlockStates1122.Resolve(Trapped, 3, w, 5, 64, 0, Solid);
        // South points along a z pair (a world edit): turned east like BlockChest; facing east (y 90) the north cell is the model's west half.
        Assert.Equal(("mineclaw_chest_trapped_double", "east", "west"), (t.File, t.Get("facing"), t.Get("half")));
        Assert.Equal("mineclaw_chest_normal", BlockStates1122.Resolve(Chest, 2, w, 9, 64, 0, Solid).File);
        Assert.Equal("mineclaw_chest_normal", BlockStates1122.Resolve(Chest, 2, null, 0, 64, 0, Solid).File); // no world: single
    }

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<GeoQuad> quads, Vector3 at)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var q in quads)
            foreach (var p in new[] { q.P0, q.P1, q.P2, q.P3 }) { min = Vector3.Min(min, p + at); max = Vector3.Max(max, p + at); }
        return (min, max);
    }

    [Theory]
    [InlineData(2, 1, 0)] [InlineData(3, 1, 0)] [InlineData(4, 0, 1)] [InlineData(5, 0, 1)]
    public void Two_1122_chests_render_as_one_large_chest_spanning_both_cells_with_no_seam_face(int facing, int dx, int dz)
    {
        if (!Jar1122.Present) { output.WriteLine("no 1.12.2 jar: skipped"); return; }
        var m = Jar1122.Models;
        var w = new SparseVoxels().Set(0, 64, 0, Chest, facing).Set(dx, 64, dz, Chest, facing);
        var qa = new List<GeoQuad>(); m.Build(w, 0, 64, 0, qa);
        var qb = new List<GeoQuad>(); m.Build(w, dx, 64, dz, qb);
        Assert.NotEmpty(qa);
        Assert.NotEmpty(qb);
        var (min, max) = Bounds(qa, Vector3.Zero);
        var (bmin, bmax) = Bounds(qb, new Vector3(dx, 0, dz));
        min = Vector3.Min(min, bmin); max = Vector3.Max(max, bmax);
        // One 30 × 14 × 14 px box across the pair: 1/16 in from the outer ends, 14/16 high, 14/16 deep.
        float along(Vector3 v) => dx != 0 ? v.X : v.Z;
        float across(Vector3 v) => dx != 0 ? v.Z : v.X;
        Assert.Equal(1 / 16f, along(min), 3);
        Assert.Equal(2 - 1 / 16f, along(max), 3);
        Assert.Equal(14 / 16f, max.Y, 3);
        Assert.Equal(14 / 16f, across(max) - across(min), 3);
        // No face on the seam (x or z = 1 in the pair's frame): the halves join into one chest.
        bool OnSeam(GeoQuad q, Vector3 at) => new[] { q.P0, q.P1, q.P2, q.P3 }.All(p => Math.Abs(along(p + at) - 1) < 1e-3);
        Assert.DoesNotContain(qa, q => OnSeam(q, Vector3.Zero));
        Assert.DoesNotContain(qb, q => OnSeam(q, new Vector3(dx, 0, dz)));
        // A single chest stays the small one.
        var single = new List<GeoQuad>(); m.Build(new SparseVoxels().Set(0, 64, 0, Chest, facing), 0, 64, 0, single);
        var (smin, smax) = Bounds(single, Vector3.Zero);
        Assert.Equal(14 / 16f, along(smax) - along(smin), 3);
    }

    [Fact]
    public void B181_pairs_show_the_left_and_right_halves_of_the_wide_front()
    {
        // BlockChest.getBlockTexture: a pair along x faces south (no walls), the two cells take the front halves 41 and 42.
        var w = new SparseVoxels().Set(0, 64, 0, Chest).Set(1, 64, 0, Chest);
        int a = BlockGeometry.ChestTile(w, 0, 64, 0, 3), b = BlockGeometry.ChestTile(w, 1, 64, 0, 3);
        Assert.Equal(new[] { 41, 42 }, new[] { a, b }.Order().ToArray());
        Assert.Equal(new[] { 57, 58 }, new[] { BlockGeometry.ChestTile(w, 0, 64, 0, 2), BlockGeometry.ChestTile(w, 1, 64, 0, 2) }.Order().ToArray());
        Assert.Equal(27, BlockGeometry.ChestTile(new SparseVoxels().Set(0, 64, 0, Chest), 0, 64, 0, 3)); // a single: the small front
    }

    // ------------------------------------------------------------------ x-ray

    /// <summary>Solid stone from y 50 to 67 over a 41×41 area with a 1×2 tunnel along z at x 0, y 60-61 (the body stands at
    /// 0.5, 60, 0.5): what Fox saw — orbit/iso cameras outside the rock, the bot inside it.</summary>
    private static WorldState Tunnel(bool open = false)
    {
        var w = new WorldState();
        for (int cx = -2; cx < 2; cx++)
            for (int cz = -2; cz < 2; cz++)
            {
                var ids = new byte[16 * 128 * 16];
                for (int x = 0; x < 16; x++)
                    for (int z = 0; z < 16; z++)
                    {
                        int wx = cx * 16 + x, wz = cz * 16 + z;
                        int top = open ? 59 : 67; // 5 blocks of rock over the tunnel roof; the cameras are above ground
                        for (int y = 50; y <= top; y++)
                        {
                            bool tunnel = !open && wx == 0 && y is 60 or 61 && wz is >= -8 and <= 8;
                            if (!tunnel) ids[(x * 16 + z) * 128 + y] = Stone;
                        }
                    }
                w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, ids, null, null, null);
            }
        return w;
    }

    private static readonly (double X, double Y, double Z) Body = (0.5, 60, 0.5);

    [Fact]
    public void The_cut_turns_on_only_when_the_body_is_hidden_from_the_camera()
    {
        var rig = PerspectivePanel.For("orbit", (0, 60, 0), 60, 6, null, null)!.Value;
        var tunnel = new BlockAccessVoxels(Tunnel().PlanningView);
        var open = new BlockAccessVoxels(Tunnel(open: true).PlanningView);
        Assert.True(ViewCut.Blocked(tunnel, Body.X, Body.Y + 1, Body.Z, rig.X, rig.Y, rig.Z));
        Assert.False(ViewCut.Blocked(open, Body.X, Body.Y + 1, Body.Z, rig.X, rig.Y, rig.Z));
        var cut = ViewCut.For(tunnel, rig.X, rig.Y, rig.Z, Body, (0, 60, 0), PerspectivePanel.XRayRadius, 60);
        Assert.NotNull(cut);
        Assert.Null(ViewCut.For(open, rig.X, rig.Y, rig.Z, Body, (0, 60, 0), PerspectivePanel.XRayRadius, 60));
        Assert.NotNull(ViewCut.For(open, rig.X, rig.Y, rig.Z, Body, (0, 60, 0), PerspectivePanel.XRayRadius, 60, force: true));
        // It never cuts the floor the body stands on, nor anything behind the body, nor far off the view line.
        Assert.False(cut!.Hides(0, 59, 0));
        Assert.True(cut.Hides(0, 62, 0));               // the tunnel roof right over the head
        Assert.False(cut.Hides(4, 61, 4));              // behind the body (the camera is NW, up)
        Assert.False(cut.Hides(-9, 70, 9));             // well off the line to the camera
        // Every cell on the segment camera → waist is cut (the hole reaches the camera whatever the depth).
        for (double f = 0.05; f < 0.95; f += 0.05)
        {
            double x = rig.X + (Body.X - rig.X) * f, y = rig.Y + (Body.Y + 0.9 - rig.Y) * f, z = rig.Z + (Body.Z - rig.Z) * f;
            if (y < 60) continue;
            Assert.True(cut.Hides((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)), $"cell at {x:0.0},{y:0.0},{z:0.0}");
        }
    }

    [Theory]
    [InlineData("orbit")] [InlineData("orbit2")] [InlineData("iso")] [InlineData("high")]
    public void An_outside_camera_sees_the_body_in_a_tunnel_through_the_cut(string view)
    {
        var world = Tunnel();
        var vox = new BlockAccessVoxels(world.PlanningView);
        var rig = PerspectivePanel.For(view, (0, 60, 0), 60, 6, null, null)!.Value;
        var ents = new[] { new VisionEntity("hive", "maid_chan", Body.X, Body.Y, Body.Z) };
        var eye = new EyeRenderer { Width = 200, Height = 150, MaxThreads = 2 };
        EyeScene Scene(ViewCut? cut) => new()
        {
            World = vox, EyeX = rig.X, EyeY = rig.Y, EyeZ = rig.Z, Yaw = rig.Yaw, Pitch = rig.Pitch, Fov = rig.Fov, MaxDistance = rig.MaxDistance,
            Entities = ents, XRay = cut,
        };
        var cutNone = Scene(null);
        var p = eye.Project(cutNone, Body.X, Body.Y + 1.0, Body.Z)!.Value;
        var exact = eye.Raster(cutNone);
        float before = exact.Depth[p.Y * 200 + p.X];
        Assert.True(before < p.Dist - 2, $"{view}: without the cut, rock hides the body ({before:0.0} vs {p.Dist:0.0})");
        var cut = ViewCut.For(vox, rig.X, rig.Y, rig.Z, Body, (0, 60, 0), PerspectivePanel.XRayRadius, 60)!;
        var xr = eye.Raster(Scene(cut));
        float after = xr.Depth[p.Y * 200 + p.X];
        Assert.InRange(after, p.Dist - 0.8, p.Dist + 0.8);
        // The tunnel floor two blocks toward the camera shows too (the floor is never cut).
        int fz = rig.Z < Body.Z ? -2 : 2;
        var f = eye.Project(cutNone, 0.5, 60.0, fz + 0.5)!.Value;
        Assert.Equal(EyeRenderer.CellKey(0, 59, fz), xr.Cells![f.Y * 200 + f.X]);
    }

    [Fact]
    public void Sheets_cut_automatically_in_a_tunnel_and_stay_byte_identical_in_the_open()
    {
        var sheets = new ContactSheet();
        VisionImage Sheet(WorldState w, XRay x) => sheets.Render(new ContactSheetRequest
        {
            Blocks = w.PlanningView, Focus = (0, 60, 0), Radius = 6, Self = (0, 60, 0), Views = ["orbit", "iso", "high", "top"], XRay = x,
            SelfPose = new PerspectivePanel.Body(Body.X, Body.Y, Body.Z, 0, 20, "maid_chan"),
        });
        var tunnel = Tunnel();
        var auto = Sheet(tunnel, XRay.Auto);
        Assert.True(auto.XRay);
        Assert.Contains("X-ray", auto.Legend);
        Assert.Contains("x-ray: blocks between the camera", auto.Legend);
        var off = Sheet(tunnel, XRay.Off);
        Dump("tunnel-xray.png", auto.Png);
        Dump("tunnel-exact.png", off.Png);
        Assert.False(off.XRay);
        Assert.DoesNotContain("X-ray", off.Legend);
        Assert.False(auto.Png.AsSpan().SequenceEqual(off.Png));
        var open = Tunnel(open: true);
        var a = Sheet(open, XRay.Auto);
        var o = Sheet(open, XRay.Off);
        Assert.False(a.XRay);
        Assert.True(a.Png.AsSpan().SequenceEqual(o.Png), "nothing hidden: the picture is exactly the old one");
        Assert.True(Sheet(open, XRay.On).XRay);
    }

    [Fact]
    public void The_cut_keeps_render_cost_close_to_the_exact_render()
    {
        var sheets = new ContactSheet();
        var tunnel = Tunnel();
        double Time(XRay x)
        {
            var req = new ContactSheetRequest
            {
                Blocks = tunnel.PlanningView, Focus = (0, 60, 0), Radius = 6, Self = (0, 60, 0), Views = ["orbit", "orbit2", "iso", "high"], XRay = x,
                SelfPose = new PerspectivePanel.Body(Body.X, Body.Y, Body.Z, 0, 20, "maid_chan"),
            };
            sheets.Render(req);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 3; i++) sheets.Render(req);
            return sw.Elapsed.TotalMilliseconds / 3;
        }
        double off = Time(XRay.Off), on = Time(XRay.Auto);
        output.WriteLine($"tunnel sheet: exact {off:0} ms, x-ray {on:0} ms");
        Assert.True(on < off * 2 + 50, $"x-ray {on:0} ms vs exact {off:0} ms");
    }

    // ------------------------------------------------------------------ weather

    [Fact]
    public void Weather_light_maths_match_the_world_engine_at_every_hour()
    {
        foreach (float rain in new[] { 0f, 0.4f, 1f })
            foreach (float thunder in new[] { 0f, 1f })
                for (long t = 0; t < 24000; t += 500)
                    Assert.Equal(LightEngine.SkylightSubtracted(t, rain, thunder), Lighting.SkylightSubtracted(t, rain, thunder));
        Assert.Equal(0, Lighting.SkylightSubtracted(6000));
        Assert.Equal(3, Lighting.SkylightSubtracted(6000, 1, 0));
        Assert.Equal(5, Lighting.SkylightSubtracted(6000, 1, 1));
        Assert.Equal(Lighting.SkylightSubtracted(6000), Lighting.SkylightSubtracted(6000, 0, 1)); // thunder counts only while it rains
    }

    private static float Saturation(uint c)
    {
        float r = (c >> 16 & 0xff) / 255f, g = (c >> 8 & 0xff) / 255f, b = (c & 0xff) / 255f;
        float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        return mx <= 0 ? 0 : (mx - mn) / mx;
    }

    private static float Luma(uint c) => ((c >> 16 & 0xff) * 0.3f + (c >> 8 & 0xff) * 0.59f + (c & 0xff) * 0.11f) / 255f;

    [Fact]
    public void Rain_greys_the_sky_and_a_storm_darkens_it()
    {
        var clear = EyeRenderer.SkyColours(0, false);
        Assert.Equal(clear, EyeRenderer.SkyColours(0, false, Weather.Clear));
        var rain = EyeRenderer.SkyColours(Lighting.SkylightSubtracted(6000, 1, 0), false, new Weather(1, 0));
        var storm = EyeRenderer.SkyColours(Lighting.SkylightSubtracted(6000, 1, 1), false, new Weather(1, 1));
        Assert.True(Saturation(rain.Top) < Saturation(clear.Top) * 0.5f);
        Assert.True(Luma(rain.Top) < Luma(clear.Top));
        Assert.True(Luma(storm.Top) < Luma(rain.Top) * 0.6f);
        Assert.True(Luma(storm.Horizon) < Luma(rain.Horizon));
        Assert.Equal(EyeRenderer.SkyColours(0, true), EyeRenderer.SkyColours(0, true, new Weather(1, 1))); // the Nether: no weather
    }

    [Fact]
    public void Snow_falls_in_cold_biomes_and_high_up_none_in_deserts_rain_elsewhere()
    {
        Assert.Equal(Precip.Rain, Weather.At(-1, 64));  // b1.8.1 / unknown biome
        Assert.Equal(Precip.Rain, Weather.At(1, 64));   // plains
        Assert.Equal(Precip.Snow, Weather.At(12, 64));  // ice plains
        Assert.Equal(Precip.Snow, Weather.At(30, 70));  // cold taiga
        Assert.Equal(Precip.None, Weather.At(2, 64));   // desert
        Assert.Equal(Precip.None, Weather.At(35, 64));  // savanna
        Assert.Equal(Precip.Rain, Weather.At(3, 64));   // extreme hills at sea level…
        Assert.Equal(Precip.Snow, Weather.At(3, 120));  // …snow on the peaks
        Assert.Equal("thunderstorm", new Weather(1, 1).Text());
        Assert.Equal("light rain", new Weather(0.3f, 0).Text());
        Assert.Equal("snow", new Weather(1, 0).Text(Precip.Snow));
        Assert.Null(Weather.Clear.Text());
    }

    [Fact]
    public void Streaks_fall_under_the_open_sky_and_never_under_a_roof()
    {
        var open = new SparseVoxels();
        for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++) open.Set(x, 63, z, Stone);
        var eye = new EyeRenderer { Width = 160, Height = 120, MaxThreads = 1 };
        var (yaw, pitch) = EyeScene.LookAt(0.5, 66, -6, 0.5, 63, 4);
        EyeScene S(IVoxelWorld w, PrecipStyle p) => new()
        {
            World = w, EyeX = 0.5, EyeY = 66, EyeZ = -6, Yaw = yaw, Pitch = pitch, Weather = new Weather(1, 0), Precip = p,
        };
        uint[] Px(IVoxelWorld w, PrecipStyle p) => (uint[])eye.Raster(S(w, p)).Px.Clone();
        int Diff(uint[] a, uint[] b) => a.Zip(b).Count(t => t.First != t.Second);
        int streaks = Diff(Px(open, PrecipStyle.None), Px(open, PrecipStyle.Streaks));
        output.WriteLine($"open sky: {streaks} streak pixels");
        Assert.True(streaks > 60, $"only {streaks} pixels of rain under the open sky");
        // Deterministic: the same still picture twice is identical.
        Assert.Equal(0, Diff(Px(open, PrecipStyle.Streaks), Px(open, PrecipStyle.Streaks)));
        // A closed stone room (no sky light inside): no rain indoors.
        var room = new SparseVoxels();
        for (int x = -12; x <= 12; x++)
            for (int z = -12; z <= 12; z++)
                for (int y = 62; y <= 70; y++)
                {
                    bool wall = x is -12 or 12 || z is -12 or 12 || y is 62 or 70;
                    if (wall) room.Set(x, y, z, Stone); else room.Light(x, y, z, 0, 0);
                }
        Assert.Equal(0, Diff(Px(room, PrecipStyle.None), Px(room, PrecipStyle.Streaks)));
        // Iso and the maps get a wash instead of streaks.
        Assert.True(Diff(Px(open, PrecipStyle.None), Px(open, PrecipStyle.Tint)) > 160 * 120 / 2);
    }

    [Fact]
    public void Sheets_say_the_weather_in_the_caption_and_the_legend_and_darken()
    {
        var sheets = new ContactSheet();
        var world = Tunnel(open: true);
        VisionImage Sheet(Weather wx) => sheets.Render(new ContactSheetRequest
        {
            Blocks = world.PlanningView, Focus = (0, 60, 0), Radius = 6, Self = (0, 60, 0), Views = ["orbit"], Weather = wx,
        });
        var clear = Sheet(Weather.Clear);
        var storm = Sheet(new Weather(1, 1));
        Dump("storm.png", storm.Png);
        Assert.Null(clear.Weather);
        Assert.DoesNotContain("Weather:", clear.Legend);
        Assert.Equal("thunderstorm", storm.Weather);
        Assert.Contains("Weather: thunderstorm", storm.Legend);
        Assert.True(storm.Pixels!.Average(c => Luma(c)) < clear.Pixels!.Average(c => Luma(c)) - 0.03);
    }
}
