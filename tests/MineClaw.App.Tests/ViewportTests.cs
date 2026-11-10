using MineClaw.Agent.Telemetry;
using MineClaw.App.Viewport;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

public class ViewportTests
{
    /// <summary>Loaded, empty columns around the origin (so IsLoaded is true and everything is air).</summary>
    private static WorldState EmptyWorld(int chunks = 1)
    {
        var w = new WorldState();
        var air = new byte[16 * 128 * 16];
        for (int cx = -chunks; cx < chunks; cx++)
            for (int cz = -chunks; cz < chunks; cz++)
                w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, air, null, null, null);
        return w;
    }

    [Fact]
    public void TopDown_UsesTopmostBlockInWindow_WithLegendGlyphs()
    {
        var w = EmptyWorld();
        // Floor of grass at y=63 under a 5x5 area, feet at y=64.
        for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) w.SetBlock(x, 63, z, Blocks.Grass, 0);
        w.SetBlock(1, 64, 0, Blocks.Chest, 0);        // east of the bot
        w.SetBlock(0, 64, -1, Blocks.Furnace, 0);     // north
        w.SetBlock(-1, 64, 0, Blocks.Workbench, 0);   // west
        w.SetBlock(0, 63, 1, Blocks.Water, 0);        // south (replaces grass)
        w.SetBlock(2, 63, 2, Blocks.Lava, 0);
        w.SetBlock(-2, 63, 2, Blocks.Cobblestone, 0);
        w.SetBlock(-2, 64, -2, Blocks.Log, 0);
        w.SetBlock(2, 64, -2, Blocks.Torch, 0);       // not solid: see-through to the grass
        w.SetBlock(-2, 66, 1, Blocks.Leaves, 0);      // within Up=2
        w.SetBlock(2, 70, 1, Blocks.Leaves, 0);       // above the window: ignored
        w.SetBlock(1, 63, 2, Blocks.Bricks, 0);
        w.SetBlock(-1, 55, -1, Blocks.Air, 0);

        var g = ViewportBuilder.Build(w, new Vec3d(0.5, 64, 0.5), new ViewportOptions { Radius = 3 });
        Assert.Equal(7, g.Top.Length);
        Assert.All(g.Top, r => Assert.Equal(7, r.Length));
        string[] expected =
        [
            "       ",
            " T.... ", // log NW; torch NE is see-through to the grass
            " ..F.. ",
            " .W.C. ",
            " *.~.. ", // leaves at feet+2 count; leaves at y=70 are above the window
            " #..+! ",
            "       ",
        ];
        Assert.Equal(expected, g.Top);
    }

    [Fact]
    public void UnloadedColumns_AreBlank_AndSideViewSlicesThroughTheBot()
    {
        var w = EmptyWorld(chunks: 1); // x,z in [-16, 16)
        for (int x = 10; x <= 15; x++) w.SetBlock(x, 63, 0, Blocks.Stone, 0);
        w.SetBlock(14, 64, 0, Blocks.Log, 0);
        w.SetBlock(14, 65, 0, Blocks.Log, 0);
        var g = ViewportBuilder.Build(w, new Vec3d(14.5, 64, 0.5), new ViewportOptions { Radius = 3, SideUp = 2, SideDown = 2 });
        Assert.Equal("###T#  ", g.Top[3]); // x 11..15; the log is the topmost block at x=14; x>=16 unloaded
        // Side view rows: y 66..62. Bot at column 3 (x=14).
        Assert.Equal(5, g.Side.Length);
        Assert.Equal(66, g.SideTopY);
        Assert.Equal("   T   ", g.Side[1]); // y=65
        Assert.Equal("   T   ", g.Side[2]); // y=64
        Assert.Equal("#####  ", g.Side[3]); // y=63; x>=16 is unloaded → blank
    }

    [Fact]
    public void Markers_ExcludeSelf_AndRenderWithHiveDigits()
    {
        var w = EmptyWorld();
        w.Entities.SpawnPlayer(1, "maid_chan", new Vec3d(0.5, 64, 0.5), 0, 0);      // self (by name) → excluded
        w.Entities.SpawnPlayer(2, "maid_chan_69", new Vec3d(2.5, 64, 0.5), 0, 0);   // hive → '9'
        w.Entities.SpawnPlayer(3, "kiwi", new Vec3d(-1.5, 64, 0.5), 0, 0);          // player → 'k'
        w.Entities.SpawnMob(4, MobTypes.Zombie, new Vec3d(0.5, 64, -1.5), 0, 0);    // mob → 'm'
        w.Entities.SpawnMob(5, MobTypes.Cow, new Vec3d(0.5, 64, 2.5), 0, 0);        // passive: not shown
        w.Entities.SpawnItem(6, new ItemStack(4, 1, 0), new Vec3d(1.5, 64, 2.5));   // item → 'i'
        var g = ViewportBuilder.Build(w, new Vec3d(0.5, 64, 0.5), new ViewportOptions { Radius = 3 }, selfName: "maid_chan");
        Assert.DoesNotContain(g.Markers, m => m.Name == "maid_chan");
        Assert.Equal(4, g.Markers.Count);

        var roster = new HiveRoster(["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"]);
        var rows = ViewportRenderer.RenderPlain(g, ViewMode.Top, 7, 7, roster.Glyph);
        Assert.Equal("   m   ", rows[1]);
        Assert.Equal(" k @ 9 ", rows[3]);
        Assert.Equal("    i  ", rows[5]);
        var cells = ViewportRenderer.Render(g, ViewMode.Top, 7, 7, roster.Glyph);
        Assert.Equal(CellKind.Hive, cells[3][5].Kind);
        Assert.Equal(CellKind.Player, cells[3][1].Kind);
        Assert.Equal("kiwi", cells[3][1].Name);
    }

    [Fact]
    public void HiveRoster_GlyphsAreUniqueDigits_MainIsM()
    {
        var r = new HiveRoster(["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"]);
        Assert.Equal('M', r.Glyph("maid_chan"));
        Assert.Equal('6', r.Glyph("maid_chan_67"));
        Assert.Equal('9', r.Glyph("maid_chan_69"));
        Assert.Equal('4', r.Glyph("MAID_CHAN_420"));
        Assert.Equal('1', r.Glyph("maid_chan_1337"));
        Assert.Null(r.Glyph("kiwi"));
    }

    [Fact]
    public void Renderer_FitsPanel_CentresBot_AndDoublesWidthWhenRoomy()
    {
        var top = Enumerable.Range(0, 25).Select(i => new string(i % 2 == 0 ? '.' : '#', 25)).ToArray();
        var g = new ViewportGrid { Radius = 12, Cx = 0, Cy = 64, Cz = 0, Top = top, Side = top, SideTopY = 70 };

        var small = ViewportRenderer.RenderPlain(g, ViewMode.Top, 9, 5);
        Assert.Equal(5, small.Length);
        Assert.All(small, r => Assert.Equal(9, r.Length));
        Assert.Equal('@', small[2][4]);

        var wide = ViewportRenderer.RenderPlain(g, ViewMode.Top, 60, 30);
        Assert.Equal(25, wide.Length);
        Assert.All(wide, r => Assert.Equal(50, r.Length));
        Assert.Equal('@', wide[12][24]);
        Assert.Equal("..", wide[0][..2]);

        var side = ViewportRenderer.RenderPlain(g, ViewMode.Side, 25, 25);
        int feetRow = 70 - 64; // SideTopY - Cy
        Assert.Equal('@', side[feetRow][12]);
        Assert.Equal('@', side[feetRow - 1][12]);
        Assert.Empty(ViewportRenderer.Render(g, ViewMode.Top, 0, 10));
    }
}
