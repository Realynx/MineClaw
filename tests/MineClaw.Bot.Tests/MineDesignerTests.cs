using System.Text.Json;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The Design Studio's mine terrain, ore table, templates and presets (docs/web/api.md "Mine designs"). The golden values are
/// shared with the browser's port (web/apps/web/test/mineTerrain.test.ts): both languages must generate the same world.
/// </summary>
public class MineDesignerTests
{
    // Golden values: the same constants are asserted by the browser's tests (mineTerrain.test.ts / mineTemplates.test.ts).
    private const uint GoldenPlot1122 = 1021586989u, GoldenPlotBeta = 1802239221u, GoldenSpiral = 2749961926u, GoldenStrip = 2072293685u;

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Authored_cave_air_exposes_neighbor_ore_without_counting_air_as_mined(string version)
    {
        var terrain = MineTerrain.For("4242", version);
        bool BesideOre(BlockPos p) => OreIdentity.Neighbors.Any(d =>
        {
            var (id, meta) = terrain.Block(p.X + d.Dx, p.Y + d.Dy, p.Z + d.Dz);
            return OreTable.OreOf(version, id, meta) != null;
        });
        var cave = (from x in Enumerable.Range(0, 64)
                    from z in Enumerable.Range(0, 64)
                    from y in Enumerable.Range(10, 40)
                    let p = new BlockPos(x, y, z)
                    where !terrain.Solid(x, y, z) && BesideOre(p)
                    select p).First();
        Assert.Equal(0, MineStats.Of(terrain, []).Seen);
        var stats = MineStats.Of(terrain, [cave]);
        Assert.Equal(0, stats.ToMine);
        Assert.Empty(stats.Materials);
        Assert.All(stats.Ores, ore => Assert.Equal(0, ore.Mined));
        Assert.True(stats.Seen > 0);
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Lit_redstone_uses_the_same_ore_band_as_unlit_redstone(string version)
    {
        var unlit = OreTable.OreOf(version, 73, 0);
        Assert.NotNull(unlit);
        Assert.Same(unlit, OreTable.OreOf(version, 74, 0));
        Assert.Equal("redstone", unlit.Id);
        Assert.Null(OreTable.OreOf(version, 1, 0));
    }

    [Fact]
    public void The_ore_table_has_every_band_per_version_with_its_sources()
    {
        var r = OreTable.For("1.12.2");
        Assert.Equal(new[] { "coal", "iron", "gold", "redstone", "diamond", "lapis", "emerald" }, r.Ores.Select(o => o.Id));
        var diamond = r.Ores.Single(o => o.Id == "diamond");
        Assert.Equal((0, 15, 1, 8, 11), (diamond.Min, diamond.Max, diamond.Veins, diamond.Size, diamond.Best));
        Assert.Equal((16, 16), (r.Ores.Single(o => o.Id == "lapis").Center, r.Ores.Single(o => o.Id == "lapis").Spread));
        Assert.Contains(r.Pockets, p => p.Id == "granite" && p.Block == 1 && p.Meta == 1);
        var b = OreTable.For("b1.8.1");
        Assert.DoesNotContain(b.Ores, o => o.Id == "emerald");
        Assert.DoesNotContain(b.Pockets, p => p.Id == "granite");
        Assert.Equal(7, b.Ores.Single(o => o.Id == "diamond").Size);
        Assert.NotEmpty(OreTable.Sources);
        Assert.Contains("diamond y0-15 best 11", OreTable.Compact("1.12.2"));
        Assert.Contains("lava lakes y≤10", OreTable.Compact("b1.8.1"));
    }

    [Fact]
    public void Seeds_read_numbers_as_they_are_and_text_like_the_game()
    {
        Assert.Equal((5u, 0u), MineTerrain.SeedBits("5"));
        Assert.Equal((0xFFFFFFFFu, 0xFFFFFFFFu), MineTerrain.SeedBits("-1"));
        Assert.Equal((0x89ABCDEFu, 0x01234567u), MineTerrain.SeedBits("81985529216486895"));
        // "hello".hashCode() = 99162322 in Java.
        Assert.Equal((99162322u, 0u), MineTerrain.SeedBits("hello"));
    }

    [Fact]
    public void The_terrain_is_deterministic_per_seed_and_version_and_rolls()
    {
        var a = MineTerrain.For("12345", "1.12.2");
        var again = MineTerrain.For("12345 ", "1.12.2");
        Assert.Same(a, again);
        var fresh = typeof(MineTerrain).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(string), typeof(string)])!
            .Invoke(["12345", "1.12.2"]) as MineTerrain;
        Assert.Equal(a.PlotHash(), fresh!.PlotHash());
        Assert.NotEqual(a.PlotHash(), MineTerrain.For("12346", "1.12.2").PlotHash());
        Assert.NotEqual(a.PlotHash(), MineTerrain.For("12345", "b1.8.1").PlotHash());
        var (lo, hi) = a.SurfaceRange();
        Assert.InRange(lo, 58, 81);
        Assert.InRange(hi, 58, 81);
        Assert.True(hi - lo >= 4, $"hills: surface {lo}..{hi}");
        // Bedrock floor, patchy up to y 4, never above.
        for (int x = 0; x < 64; x += 7)
            for (int z = 0; z < 64; z += 5)
            {
                Assert.Equal(MineTerrain.Bedrock, a.Block(x, 0, z).Id);
                Assert.NotEqual(MineTerrain.Bedrock, a.Block(x, 5, z).Id);
                int top = a.Surface(x, z);
                Assert.Equal(MineTerrain.Grass, a.Block(x, top, z).Id);
                Assert.Equal(0, a.Block(x, top + 1, z).Id);
            }
        // The goldens shared with the browser's port.
        Assert.Equal(GoldenPlot1122, MineTerrain.For("12345", "1.12.2").PlotHash());
        Assert.Equal(GoldenPlotBeta, MineTerrain.For("hello", "b1.8.1").PlotHash());
    }

    [Theory]
    [InlineData("1.12.2")]
    [InlineData("b1.8.1")]
    public void Ores_land_in_their_bands_in_the_expected_amounts(string version)
    {
        var t = MineTerrain.For("777", version);
        var table = OreTable.For(version);
        var counts = table.Ores.ToDictionary(o => o.Id, _ => 0);
        var outside = new List<string>();
        for (int y = 0; y < MineTerrain.Height; y++)
            for (int z = 0; z < 64; z++)
                for (int x = 0; x < 64; x++)
                {
                    var (id, meta) = t.Block(x, y, z);
                    if (OreTable.OreOf(version, id, meta) is not { } ore) continue;
                    counts[ore.Id]++;
                    // A vein walks at most size-1 blocks from its start.
                    if (y < ore.Min - ore.Size || y > ore.Max + ore.Size) outside.Add($"{ore.Id}@{y}");
                }
        Assert.Empty(outside);
        // 16 chunks: veins × chunks × (a walk covers roughly half its steps) give the expected counts, with a wide margin.
        foreach (var o in table.Ores.Where(o => o.Veins > 0))
        {
            double expected = o.Veins * 16 * o.Size * 0.5;
            Assert.InRange(counts[o.Id], (int)(expected * 0.3), (int)(expected * 2.2));
        }
        Assert.True(counts["iron"] > counts["gold"] && counts["gold"] > counts["diamond"] && counts["redstone"] > counts["diamond"], string.Join(", ", counts));
        if (version == "1.12.2") Assert.Equal(0, counts["emerald"]);
    }

    [Fact]
    public void A_spiral_shaft_is_walkable_step_by_step()
    {
        foreach (var (size, core, cw) in new[] { (4, 2, true), (3, 1, false), (5, 1, true), (6, 2, false), (4, 1, true) })
        {
            var m = MineTemplates.Spiral(new SpiralSpec(10, 10, 70, 11, size, core, cw, 6, [40, 11]));
            var dug = m.Cells.Where(c => c.Id == 0).Select(c => new BlockPos(c.X, c.Y, c.Z)).ToHashSet();
            Assert.Equal(70 - 11 + 1, m.Path.Count);
            Assert.Equal(m.Path[0], m.Entry);
            for (int k = 0; k < m.Path.Count; k++)
            {
                var p = m.Path[k];
                // Headroom 2 where the feet are, a solid floor under them (no landing there).
                Assert.Contains(p, dug);
                Assert.Contains(p.Up(), dug);
                if (p.Y - 1 is not (40 or 41 or 11 or 12)) Assert.DoesNotContain(p.Down(), dug);
                if (k == 0) continue;
                var q = m.Path[k - 1];
                // Each step is one block over and at most one block down/up, with the headroom a step needs.
                Assert.Equal(1, Math.Abs(p.X - q.X) + Math.Abs(p.Z - q.Z));
                Assert.True(Math.Abs(p.Y - q.Y) <= 1);
                Assert.Contains(new BlockPos(p.X, q.Y + 1, p.Z), dug);
                Assert.Contains(new BlockPos(q.X, p.Y + 2, q.Z), dug);
            }
            // The core stands (except at the landings).
            int off = (size - core) / 2;
            Assert.DoesNotContain(new BlockPos(10 + off, 30, 10 + off), dug);
            Assert.Contains(new BlockPos(10 + off, 40, 10 + off), dug);
            Assert.Contains(m.Cells, c => c.Id == MineClaw.Data.Blocks.Torch);
        }
        Assert.Equal(GoldenSpiral, MineTemplates.Hash(MineTemplates.Spiral(new SpiralSpec(30, 30, 72, 11, 4, 2, true, 6, [11, 40])).Cells));
    }

    [Fact]
    public void A_strip_mine_spaces_its_branches()
    {
        var m = MineTemplates.Strip(new StripSpec(10, 11, 20, 'x', 30, 2, 3, 3, 10, "both", 8));
        var dug = m.Cells.Where(c => c.Id == 0).Select(c => new BlockPos(c.X, c.Y, c.Z)).ToHashSet();
        // Branch columns at 3, 7, 11, …: 3 solid blocks between two.
        var cols = dug.Where(p => p.Z < 20).Select(p => p.X - 10).Distinct().Order().ToList();
        Assert.Equal(new[] { 3, 7, 11, 15, 19, 23, 27 }, cols);
        Assert.All(cols.Zip(cols.Skip(1)), p => Assert.Equal(4, p.Second - p.First));
        Assert.Contains(new BlockPos(13, 12, 10), dug); // the left branch, 2 high, 10 long
        Assert.DoesNotContain(new BlockPos(13, 13, 10), dug);
        Assert.Contains(new BlockPos(13, 11, 31), dug); // the right one starts past the 2-wide corridor
        Assert.Contains(new BlockPos(39, 13, 21), dug); // the 2×3 corridor
        Assert.Equal(new BlockPos(10, 11, 20), m.Entry);
        Assert.All(m.Cells.Where(c => c.Id == MineClaw.Data.Blocks.Torch), c => Assert.DoesNotContain(c.X - 10, cols));
        Assert.Equal(GoldenStrip, MineTemplates.Hash(MineTemplates.Strip(new StripSpec(34, 11, 31, 'x', 30, 2, 3, 3, 14, "both", 8)).Cells));
    }

    [Fact]
    public void Version_preferred_presets_load_and_build_on_the_terrain()
    {
        Assert.Contains(MinePresets.All, p => p.Id == "1.12.2-diamond-y11" && p.Version == "1.12.2" && p.Y == 11);
        Assert.Contains(MinePresets.For("b1.8.1"), p => p.Name == "b1.8.1 diamond strip mine at y 11");
        Assert.All(MinePresets.For("b1.8.1"), p => Assert.Equal("b1.8.1", p.Version));
        var t = MineTerrain.For("42", "1.12.2");
        var p = MinePresets.Get("1.12.2-diamond-y11")!;
        var cells = MinePresets.Cells(p, t);
        // The shaft starts at the ground over its square and reaches the strip's level.
        Assert.Equal(t.SurfaceOver(30, 30, 33, 33), cells.Entry.Y);
        Assert.Contains(cells.Cells, c => c.Id == 0 && c.Y == 11 && c.X == 60);
        Assert.DoesNotContain(cells.Cells, c => c.Y < 11);
        var step = JsonSerializer.SerializeToElement(new { template = "strip", x = 1, y = 12, z = 2, axis = "z", length = 5, branch_length = 2, spacing = 1 });
        var s = MineTemplates.FromStep(step, t);
        Assert.Contains(s.Cells, c => c.X == 1 && c.Z == 6 && c.Y == 12);
    }
}
