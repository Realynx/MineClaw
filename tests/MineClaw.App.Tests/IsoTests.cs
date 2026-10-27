using System.Diagnostics;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Hosting;
using MineClaw.App.Sources;
using MineClaw.App.Tui;
using MineClaw.App.Viewport;
using MineClaw.Data;
using MineClaw.World;
using Spectre.Console;

namespace MineClaw.App.Tests;

public class IsoTests
{
    private static readonly string[] Names = ["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private static WorldState EmptyWorld()
    {
        var w = new WorldState();
        var air = new byte[16 * 128 * 16];
        for (int cx = -2; cx < 2; cx++)
            for (int cz = -2; cz < 2; cz++)
                w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, air, null, null, null);
        return w;
    }

    /// <summary>A volume of radius r / 4 levels with the bot's feet at (0, 64, 0); cells set via <paramref name="set"/>.</summary>
    private static VoxelVolume Vol(int r, Action<Action<int, int, int, int>> set, int[]? target = null)
    {
        int n = 2 * r + 1, sy = 4;
        var cells = new byte[n * n * sy];
        void Set(int x, int y, int z, int id) => cells[((y - 62) * n + (z + r)) * n + (x + r)] = (byte)id;
        set(Set);
        return VoxelVolume.Create(-r, 62, -r, n, sy, n, 0, 64, 0, cells, 1, target);
    }

    // ------------------------------------------------------------------ projection

    [Fact]
    public void Rotation_TurnsNorthClockwiseThroughTheFourCorners()
    {
        // North = (0, -1). rot 0: v = -1 → screen up-right; each step turns the compass clockwise.
        var s = IsoProjection.Sprite(1);
        (int, int) Screen(int dx, int dz, int rot) { var (u, v) = IsoProjection.Rotate(dx, dz, rot); var p = IsoProjection.Project(u, v, 0, s); return (Math.Sign(p.X), Math.Sign(p.Y)); }
        Assert.Equal((1, -1), Screen(0, -1, 0));  // N ↗
        Assert.Equal((1, 1), Screen(0, -1, 1));   // N ↘
        Assert.Equal((-1, 1), Screen(0, -1, 2));  // N ↙
        Assert.Equal((-1, -1), Screen(0, -1, 3)); // N ↖
        Assert.Equal((1, 1), Screen(1, 0, 0));    // E ↘ at rot 0
        for (int rot = 0; rot < 4; rot++)
        {
            Assert.Equal(IsoProjection.Corner(0, rot), new[] { (1, -1), (1, 1), (-1, 1), (-1, -1) }.ToList().IndexOf(Screen(0, -1, rot)));
            // Rotation is a bijection that keeps distances, and four steps are the identity.
            var (u, v) = (3, -2);
            for (int k = 0; k < 4; k++) (u, v) = IsoProjection.Rotate(u, v, 1);
            Assert.Equal((3, -2), (u, v));
        }
        Assert.Equal((0, -s.C), IsoProjection.Project(0, 0, 1, s)); // one block up = C pixels up
        Assert.Equal((s.A, s.B), IsoProjection.Project(1, 0, 0, s));
        Assert.Equal((-s.A, s.B), IsoProjection.Project(0, 1, 0, s));
    }

    [Fact]
    public void Sprites_AreHexagonsWithThreeFaces_AndFloorTopsTessellate()
    {
        for (int z = 0; z <= IsoView.MaxZoom; z++)
        {
            var s = IsoProjection.Sprite(z);
            Assert.Contains(s.Full, p => p.Face == IsoFace.Top);
            Assert.Contains(s.Full, p => p.Face == IsoFace.Left);
            Assert.Contains(s.Full, p => p.Face == IsoFace.Right);
            Assert.All(s.Full, p => Assert.InRange(p.X, 0, s.W - 1));
            Assert.All(s.Full, p => Assert.InRange(p.Y, 0, s.H - 1));
            // A flat floor of tops covers every pixel of an inner window exactly once or more (no holes).
            var covered = new HashSet<(int, int)>();
            for (int u = -6; u <= 6; u++)
                for (int v = -6; v <= 6; v++)
                {
                    var (x, y) = IsoProjection.Project(u, v, 0, s);
                    foreach (var p in s.Full.Where(p => p.Face == IsoFace.Top)) covered.Add((x + p.X, y + p.Y));
                }
            for (int x = -s.W; x <= s.W; x++)
                for (int y = -s.H; y <= s.H; y++)
                    Assert.Contains((x, y), covered);
        }
        var z1 = IsoProjection.Sprite(1);
        Assert.Equal((4, 4), (z1.W, z1.H)); // 4 chars × 2 rows
    }

    // ------------------------------------------------------------------ painter

    [Fact]
    public void PaintOrder_IsBackToFront_AndNearBlocksCoverFarOnes()
    {
        var vol = Vol(3, set => { for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) set(x, 63, z, Blocks.Stone); });
        var r = new IsoRenderer();
        for (int rot = 0; rot < 4; rot++)
            foreach (var zoom in new[] { 0, 1, 2 })
            {
                var s = IsoProjection.Sprite(zoom);
                var order = r.PaintOrder(vol, rot, s);
                Assert.Equal(vol.Count, order.Length);
                int last = int.MinValue;
                foreach (int i in order)
                {
                    int x = i % vol.SizeX + vol.X0, z = i / vol.SizeX % vol.SizeZ + vol.Z0, y = i / (vol.SizeX * vol.SizeZ) + vol.Y0;
                    var (u, v) = IsoProjection.Rotate(x - vol.Cx, z - vol.Cz, rot);
                    int d = IsoProjection.Depth(u, v, y - vol.Cy, s);
                    Assert.True(d >= last, "painter order must not go back in depth");
                    last = d;
                }
            }

        // A gold block right in front of (south-east of) a stone block, one level up: its pixels win where they overlap.
        var two = Vol(3, set => { set(0, 63, 0, Blocks.Stone); set(1, 64, 1, Blocks.GoldBlock); });
        var view = new IsoView { Zoom = 1 };
        var frame = new IsoRenderer().Render(new IsoInput(two, [], view, IsoColours.TrueColor, Unicode: true), 40, 20);
        Assert.Equal(2, frame.BlocksDrawn);
        var gold = IsoPalette.Mat(Blocks.GoldBlock)!.Top;
        Assert.Contains(frame.Cells, c => c.Fg == gold || c.Bg == gold);
    }

    // ------------------------------------------------------------------ cutaway

    [Fact]
    public void Cutaway_HidesBlocksAboveHeadLevel()
    {
        // Feet at 64, head 65: a roof at y=65 stays (= head level); with Y0=62 and 4 levels the volume tops out at 65.
        int n = 7, sy = 6;
        var cells = new byte[n * n * sy];
        void Set(int x, int y, int z, int id) => cells[((y - 62) * n + (z + 3)) * n + (x + 3)] = (byte)id;
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) { Set(x, 63, z, Blocks.Stone); Set(x, 67, z, Blocks.Planks); }
        Set(-2, 65, -2, Blocks.Log); // behind the bot: doesn't hide it
        var vol = VoxelVolume.Create(-3, 62, -3, n, sy, n, 0, 64, 0, cells, 1);
        var r = new IsoRenderer();
        var open = r.Render(new IsoInput(vol, [], new IsoView(), IsoColours.TrueColor, true), 50, 24);
        int all = open.BlocksDrawn;
        bool hiddenUnderRoof = open.Marks.Single(m => m.Kind == "self").Hidden; // (frames are reused by the renderer)
        var cut = r.Render(new IsoInput(vol, [], new IsoView { Cutaway = true }, IsoColours.TrueColor, true), 50, 24);
        Assert.Equal(65, cut.CutY);
        Assert.Equal(all - 49, cut.BlocksDrawn); // the plank roof (7×7 at y=67) is gone, the log at head level stays
        // The bot under the roof is hidden without the cutaway and visible with it.
        Assert.True(hiddenUnderRoof);
        Assert.False(cut.Marks.Single(m => m.Kind == "self").Hidden);
    }

    // ------------------------------------------------------------------ palette

    [Theory]
    [InlineData(IsoColours.Ansi256)]
    [InlineData(IsoColours.Ansi16)]
    public void PaletteFallbacks_KeepTheThreeFacesDistinct(IsoColours c)
    {
        foreach (var id in new[] { Blocks.Grass, Blocks.Stone, Blocks.Cobblestone, Blocks.Log, Blocks.Leaves, Blocks.Water, Blocks.Lava, Blocks.Chest, Blocks.Sand, Blocks.Planks })
        {
            var m = IsoPalette.Mat(id)!;
            var idx = c == IsoColours.Ansi256 ? m.C256 : m.C16;
            Assert.Equal(3, idx.Distinct().Count());
            Assert.All(idx, i => Assert.InRange(i, 0, c == IsoColours.Ansi16 ? 15 : 255));
        }
        // Quantisers round-trip exact palette colours.
        for (int i = 16; i < 256; i++) Assert.Equal(IsoPalette.Rgb256(i), IsoPalette.Rgb256(IsoPalette.To256(IsoPalette.Rgb256(i))));
        for (int i = 0; i < 16; i++) Assert.Equal(i, IsoPalette.To16(IsoPalette.Ansi16Rgb[i]));
    }

    [Fact]
    public void Colours_TopBrightestLeftMidRightDarkest_AndFallbackModesEmitIndexes()
    {
        var vol = Vol(3, set => set(0, 63, 0, Blocks.Stone));
        var s = IsoProjection.Sprite(1);
        var m = IsoPalette.Mat(Blocks.Stone)!;
        static int Lum(uint c) => (int)((c >> 16) & 0xff) + (int)((c >> 8) & 0xff) + (int)(c & 0xff);
        var top = s.Full.First(p => p.Face == IsoFace.Top);
        var left = s.Full.First(p => p.Face == IsoFace.Left);
        var right = s.Full.First(p => p.Face == IsoFace.Right);
        // Average over many blocks (stone has a little noise).
        double Avg(SpritePx p) => Enumerable.Range(0, 50).Average(i => Lum(IsoRenderer.Shade(m, p, i, 63, i * 3, 1f, IsoColours.TrueColor, s)));
        Assert.True(Avg(top) > Avg(left) && Avg(left) > Avg(right));

        var r = new IsoRenderer();
        foreach (var mode in new[] { IsoColours.Ansi256, IsoColours.Ansi16 })
        {
            var f = r.Render(new IsoInput(vol, [], new IsoView(), mode, true), 30, 12);
            Assert.All(f.Cells, c => Assert.True((c.Flags & IsoCellFlags.Bg) == 0 || (c.Bg & IsoPalette.Indexed) != 0));
        }
        // No unicode → ASCII glyphs only; no colour at all → plain glyphs, no styles.
        var ascii = r.Render(new IsoInput(vol, [], new IsoView(), IsoColours.TrueColor, Unicode: false), 30, 12);
        Assert.All(ascii.Cells, c => Assert.True(c.Ch < 128));
        Assert.Contains(ascii.Cells, c => c.Ch == '#');
        var plain = r.Render(new IsoInput(vol, [], new IsoView(), IsoColours.None, Unicode: true), 30, 12);
        Assert.All(plain.Cells, c => Assert.True((c.Flags & (IsoCellFlags.Fg | IsoCellFlags.Bg)) == 0));
        Assert.DoesNotContain(plain.Cells, c => c.Ch == '▀');
        Assert.Contains("#", string.Concat(plain.PlainRows()));
    }

    // ------------------------------------------------------------------ payload

    [Fact]
    public void VoxelPayload_RoundTrips_AndStaysSmall()
    {
        var rng = new Random(7);
        foreach (var n in new[] { 1, 100, 25 * 25 * 16 })
        {
            var cells = new byte[n];
            for (int i = 0; i < n; i++) cells[i] = (byte)(rng.Next(4) == 0 ? rng.Next(110) : 0);
            var enc = VoxelCodec.Encode(cells);
            Assert.Equal(cells, VoxelCodec.Decode(enc, n));
            Assert.True(enc.Length <= VoxelCodec.MaxEncodedSize(n));
        }
        // Worst case: every cell differs from its neighbour.
        var noisy = Enumerable.Range(0, 10_000).Select(i => (byte)(i % 2 == 0 ? 1 : 2 + i % 100)).ToArray();
        Assert.True(VoxelCodec.Encode(noisy).Length <= VoxelCodec.MaxEncodedSize(noisy.Length));
        Assert.Equal(noisy, VoxelCodec.Decode(VoxelCodec.Encode(noisy), noisy.Length));
        // Long runs use multi-byte lengths; all-air is a handful of bytes.
        Assert.True(VoxelCodec.Encode(new byte[10_000]).Length <= 6);

        // The demo island (25×25×16 around a bot) is well under 1 KB, and survives the JSON snapshot round trip.
        using var demo = new DemoHive(Names, 3);
        for (int i = 0; i < 10; i++) demo.Tick();
        var snap = demo.Sources[0].Snapshot(full: true);
        var vox = snap.Voxels!;
        Assert.Equal((25, 16, 25), (vox.SizeX, vox.SizeY, vox.SizeZ));
        Assert.True(vox.Data.Length < 1024, $"payload {vox.Data.Length} bytes");
        var back = BotSnapshot.FromJson(snap.ToJson())!;
        Assert.Equal(vox.Cells(), back.Voxels!.Cells());
        Assert.Equal(vox.Hash, back.Voxels.Hash);
        Assert.Equal(Blocks.Chest, back.Voxels.At(-3, 64, 1));
    }

    [Fact]
    public void VoxelCache_RebuildsAtMostTwiceASecond_AndKeepsSeqWhenUnchanged()
    {
        var w = EmptyWorld();
        w.SetBlock(0, 63, 0, Blocks.Stone, 0);
        var cache = new VoxelCache();
        var o = new ViewportOptions();
        var t0 = DateTime.UtcNow;
        var a = cache.Get(w, new Vec3d(0.5, 64, 0.5), o, now: t0);
        w.SetBlock(1, 63, 0, Blocks.Stone, 0);
        Assert.Same(a, cache.Get(w, new Vec3d(0.5, 64, 0.5), o, now: t0.AddMilliseconds(200))); // rate-limited
        var b = cache.Get(w, new Vec3d(0.5, 64, 0.5), o, now: t0.AddMilliseconds(600));
        Assert.NotEqual(a.Seq, b.Seq);
        Assert.Same(b, cache.Get(w, new Vec3d(0.5, 64, 0.5), o, now: t0.AddMilliseconds(1300))); // rebuilt, unchanged → same

        // IPC: unchanged volumes are elided by the sender and restored by the receiver.
        var gate = new VoxelSendGate();
        var s1 = new BotSnapshot { Name = "x", Voxels = b };
        var sent1 = gate.Thin(s1, DateTimeOffset.UnixEpoch);
        Assert.NotNull(sent1.Voxels);
        var sent2 = gate.Thin(s1, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Null(sent2.Voxels);
        Assert.Equal(b.Seq, sent2.VoxelsSeq);
        Assert.True(sent2.ToJson().Length < sent1.ToJson().Length);
        var recv = VoxelSendGate.Merge(BotSnapshot.FromJson(sent1.ToJson()), BotSnapshot.FromJson(sent2.ToJson())!);
        Assert.Equal(b.Hash, recv.Voxels!.Hash);
        Assert.NotNull(gate.Thin(s1, DateTimeOffset.UnixEpoch.AddSeconds(6)).Voxels); // keyframe
        Assert.Null(VoxelSendGate.Merge(null, sent2).Voxels); // nothing to restore from
    }

    // ------------------------------------------------------------------ entities

    [Fact]
    public void Entities_UseTheirGlyphs_AndHiddenOnesGetMarkers()
    {
        // A stone pillar right in front of (south-east of, 2 high) a zombie hides it; the player in the open stays visible.
        var vol = Vol(4, set =>
        {
            for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) set(x, 63, z, Blocks.Grass);
            set(3, 64, 3, Blocks.Stone); set(3, 65, 3, Blocks.Stone);
        });
        var roster = new HiveRoster(Names);
        ViewMarker[] markers =
        [
            new("mob", "zombie", 2, 0, 2), new("mob", "creeper", -2, 0, 0), new("mob", "skeleton", 0, 0, -2), new("mob", "spider", -3, 0, -3),
            new("player", "kiwi", 2, 0, -2), new("player", "maid_chan_69", -2, 0, 2), new("item", null, 0, 0, 2),
        ];
        var f = new IsoRenderer().Render(new IsoInput(vol, markers, new IsoView { Zoom = 1 }, IsoColours.TrueColor, true, false,
            roster.Glyph, roster.ColourOf, roster.ColourOf("maid_chan")), 60, 24);
        char G(string? name, string kind) => f.Marks.Single(m => m.Kind == kind && m.Name == name).Glyph;
        Assert.Equal('z', G("zombie", "mob"));
        Assert.Equal('c', G("creeper", "mob"));
        Assert.Equal('s', G("skeleton", "mob"));
        Assert.Equal('S', G("spider", "mob"));
        Assert.Equal('d', G("kiwi", "player"));
        Assert.Equal('9', G("maid_chan_69", "player"));
        Assert.Equal('i', G(null, "item"));
        Assert.Equal('@', G(null, "self"));
        var zombie = f.Marks.Single(m => m.Name == "zombie");
        Assert.True(zombie.Hidden);
        Assert.Equal(IsoCellFlags.Hidden, f.At(zombie.Col, zombie.Row).Flags & IsoCellFlags.Hidden);
        Assert.Equal('z', f.At(zombie.Col, zombie.Row).Ch); // still drawn, on top of the occluder
        Assert.False(f.Marks.Single(m => m.Name == "kiwi").Hidden);
        Assert.False(f.Marks.Single(m => m.Kind == "self").Hidden);
        Assert.Equal(0x3ce83cu, IsoRenderer.MarkerLook(new ViewMarker("mob", "creeper", 0, 0, 0), new IsoInput(vol, [], new IsoView(), IsoColours.TrueColor, true)).Colour);
        // Rotating 180° brings the zombie in front of the pillar.
        var back = new IsoRenderer().Render(new IsoInput(vol, markers, new IsoView { Zoom = 1, Rotation = 2 }, IsoColours.TrueColor, true), 60, 24);
        Assert.False(back.Marks.Single(m => m.Name == "zombie").Hidden);
    }

    [Fact]
    public void Compass_FollowsRotation_AndTargetIsHighlighted()
    {
        var vol = Vol(3, set => { set(0, 63, 0, Blocks.Stone); set(2, 63, 0, Blocks.Cobblestone); }, target: [2, 63, 0]);
        var r = new IsoRenderer();
        for (int rot = 0; rot < 4; rot++)
        {
            var f = r.Render(new IsoInput(vol, [], new IsoView { Rotation = rot }, IsoColours.TrueColor, true), 40, 16);
            (int X, int Y)[] corners = [(2, 0), (2, 2), (0, 2), (0, 0)];
            var (x, y) = corners[IsoProjection.Corner(0, rot)];
            Assert.Equal('N', f.At(40 - 4 + x, y).Ch);
        }
        // Zoom 2 outlines the target in the highlight colour; smaller sprites tint it.
        var big = r.Render(new IsoInput(vol, [], new IsoView { Zoom = 2 }, IsoColours.TrueColor, true), 70, 24);
        Assert.Equal(2, big.Zoom);
        Assert.Contains(big.Cells, c => c.Fg == IsoRenderer.HighlightColour || c.Bg == IsoRenderer.HighlightColour);
        string Colours(IsoFrame f) => string.Join(",", f.Cells.Select(c => c.Fg + ":" + c.Bg));
        var tinted = Colours(r.Render(new IsoInput(vol, [], new IsoView(), IsoColours.TrueColor, true), 40, 16));
        var none = Colours(r.Render(new IsoInput(vol with { Target = null }, [], new IsoView(), IsoColours.TrueColor, true), 40, 16));
        Assert.NotEqual(none, tinted);
    }

    // ------------------------------------------------------------------ TUI integration

    private static (TuiApp App, DemoHive Demo) App(ViewMode? view = ViewMode.Iso)
    {
        var roster = new HiveRoster(Names);
        var demo = new DemoHive(roster.Names, 3);
        for (int i = 0; i < 40; i++) demo.Tick();
        return (new TuiApp(demo.Sources, roster, new RingLoggerProvider(), new TuiSettings(), _ => { }, view: view), demo);
    }

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    [Fact]
    public void Keys_CycleViews_Rotate_Zoom_Cut_AndTitleSaysSo()
    {
        var (app, demo) = App(ViewMode.Top);
        using var _d = demo;
        app.HandleKey(Key('v'));
        Assert.Equal(ViewMode.Side, app.State.View);
        app.HandleKey(Key('v'));
        Assert.Equal(ViewMode.Iso, app.State.View);
        var text = app.RenderOnce(140, 45, ansi: false);
        Assert.Contains("Viewport — maid_chan (@ ", text);
        Assert.Contains("· iso N↗", text);
        app.HandleKey(Key('.'));
        app.HandleKey(Key('c'));
        app.HandleKey(Key('+'));
        Assert.Equal((1, true, 2), (app.State.Iso.Rotation, app.State.Iso.Cutaway, app.State.Iso.Zoom));
        text = app.RenderOnce(140, 45, ansi: false);
        Assert.Matches(@"iso N↘ · cut y≤\d+ · zoom 2", text);
        app.HandleKey(Key(','));
        app.HandleKey(Key(','));
        Assert.Equal(3, app.State.Iso.Rotation);
        app.HandleKey(Key('3')); // bot switch works in iso too
        Assert.Contains("Viewport — maid_chan_69 (@ ", app.RenderOnce(140, 45, ansi: false));
        app.HandleKey(Key('v'));
        Assert.Equal(ViewMode.Top, app.State.View);
        Assert.Equal(ViewMode.Iso, TuiApp.DefaultView(ColorSystemSupport.TrueColor));
        Assert.Equal(ViewMode.Top, TuiApp.DefaultView(ColorSystemSupport.EightBit));
        Assert.Equal(ViewMode.Iso, TuiApp.ParseView("iso"));
        Assert.Null(TuiApp.ParseView(null));
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(140, 45)]
    [InlineData(80, 24)]
    [InlineData(50, 16)]
    public void Frame_Snapshot_FillsTheTerminal_InAnsiAndPlain(int w, int h)
    {
        var (app, demo) = App();
        using var _d = demo;
        var plain = app.RenderOnce(w, h, ansi: false).Split('\n');
        Assert.Equal(h, plain.Length);
        Assert.All(plain, l => Assert.True(l.Length <= w, $"line longer than {w}: {l.Length}"));
        var all = string.Join("\n", plain);
        Assert.Contains("Viewport — maid_chan (@ ", all);
        Assert.Contains("@", all);
        var ansi = app.RenderOnce(w, h, ansi: true);
        Assert.Contains("\x1b[38;2;", ansi); // 24-bit colour
        Assert.Contains("▀", ansi);
        var visible = System.Text.RegularExpressions.Regex.Replace(ansi, "\x1b\\[[0-9;]*m", "").Split('\n');
        Assert.Equal(h, visible.Length);
        Assert.All(visible, l => Assert.True(l.Length <= w));
        Assert.Contains("\x1b[38;5;", app.RenderOnce(w, h, ansi: true, ColorSystemSupport.EightBit));
    }

    [Fact]
    public void Frame_120x40_MatchesTheSampleShape()
    {
        var (app, demo) = App();
        using var _d = demo;
        var text = app.RenderOnce(120, 40, ansi: false);
        // The island is drawn: grass tops, the log, leaves, the chest, water and lava of the generator.
        foreach (var g in new[] { "\"", "H", "*", "C", "~", "!", "%" }) Assert.Contains(g, text);
        Assert.Contains("6", text); // a hive clone on the island

        // Nether: the title says so and the void is tinted red.
        var roster = new HiveRoster(Names);
        var snap = demo.Sources[0].Snapshot(full: true) with { Dimension = -1 };
        var state = new TuiState { View = ViewMode.Iso };
        var input = new FrameInput(state, [snap], snap, [], roster, true, DateTimeOffset.Now, IsoColours.TrueColor);
        var ansi = FrameRenderer.Render(TuiFrame.Build(input, 140, 45), 140, 45, ansi: true);
        Assert.Contains("· nether", ansi);
        uint top = IsoPalette.VoidTop(nether: true);
        Assert.Contains($"8;2;{top >> 16};{(top >> 8) & 0xff};{top & 0xff}", ansi); // as fg (▀ top half) or bg
    }

    [Fact]
    public void RenderTime_IsUnderFiveMillisecondsPerFrame()
    {
        // Worst-ish case: a full 25×25×16 volume with a solid lower half (much denser than any island).
        int n = 25, sy = 16;
        var cells = new byte[n * n * sy];
        var rng = new Random(3);
        for (int y = 0; y < 9; y++) for (int i = 0; i < n * n; i++) cells[y * n * n + i] = (byte)(y == 8 ? Blocks.Grass : rng.Next(3) == 0 ? Blocks.Cobblestone : Blocks.Stone);
        var vol = VoxelVolume.Create(-12, 56, -12, n, sy, n, 0, 64, 0, cells, 1);
        var r = new IsoRenderer();
        var input = new IsoInput(vol, [new ViewMarker("player", "kiwi", 2, 0, 2)], new IsoView(), IsoColours.TrueColor, true);
        for (int i = 0; i < 5; i++) r.Render(input, 80, 30); // warm up
        var styles = new Dictionary<(uint, uint, IsoCellFlags), Style>();
        var sw = Stopwatch.StartNew();
        const int frames = 40;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) r.Render(input, 80, 30);
        long allocPerFrame = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / frames;
        double ms = sw.Elapsed.TotalMilliseconds / frames;
        Assert.True(ms < 5, $"iso render {ms:0.00} ms/frame");
        Assert.True(allocPerFrame < 4096, $"iso render allocates {allocPerFrame} B/frame");
        // Segments for Spectre (the canvas → terminal step) stay cheap as well.
        sw.Restart();
        for (int i = 0; i < frames; i++)
            _ = new CanvasRenderable(r.Render(input, 80, 30), styles).Render(new Spectre.Console.Rendering.RenderOptions(AnsiConsole.Console.Profile.Capabilities, new Size(80, 30)), 80).Count();
        Assert.True(sw.Elapsed.TotalMilliseconds / frames < 5, $"iso render + segments {sw.Elapsed.TotalMilliseconds / frames:0.00} ms/frame");
    }
}
