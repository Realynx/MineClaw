using System.Text.Json;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// How a mine blueprint is judged and chosen (docs/design/mine-blueprints.md): the dig estimate (<see cref="MineStats"/>, the same
/// numbers as the browser's <c>mineStats.ts</c>), the vein yield (<see cref="OreVeins"/>), the best levels of a plot
/// (<see cref="OreLevels"/>), the study's defaults and presets, and the AI designer's survey tools.
/// </summary>
public class MineBlueprintTests
{
    private static IEnumerable<BlockPos> Dug(MineCells m) => m.Cells.Where(c => c.Id == 0).Select(c => new BlockPos(c.X, c.Y, c.Z));

    /// <summary>The dig of web/apps/web/test/mineStatsGolden.test.ts (a spiral and a strip, 3 apart).</summary>
    private static List<BlockPos> GoldenDig() =>
        Dug(MineTemplates.Spiral(new SpiralSpec(10, 10, 70, 5, 5))).Concat(Dug(MineTemplates.Strip(new StripSpec(2, 12, 40, Length: 50, Spacing: 3, TorchEvery: 4))))
            .ToList();

    [Fact]
    public void The_dig_estimate_matches_the_browsers_numbers()
    {
        // The values of web/apps/web/test/__snapshots__/mineStatsGolden.test.ts.snap.
        var beta = MineStats.Of(MineTerrain.For("12345", "b1.8.1"), GoldenDig());
        Assert.Equal(934, beta.ToMine);
        Assert.Equal((5, 72), beta.YRange);
        Assert.Equal([("Stone", 848), ("Dirt", 45), ("Iron ore", 13), ("Gravel", 10), ("Coal ore", 8), ("Redstone ore", 5), ("Diamond ore", 3), ("Gold ore", 2)],
            beta.Materials);
        Assert.Equal([("coal", 8, 28), ("iron", 13, 23), ("gold", 2, 4), ("redstone", 5, 20), ("diamond", 3, 4), ("lapis", 0, 0)],
            beta.Ores.Select(o => (o.Ore.Id, o.Mined, o.Exposed)));
        var release = MineStats.Of(MineTerrain.For("hello", "1.12.2"), GoldenDig());
        Assert.Equal(931, release.ToMine);
        Assert.Equal([("Stone", 772), ("Diorite", 52), ("Granite", 43), ("Andesite", 30), ("Dirt", 13), ("Coal ore", 6), ("Iron ore", 6), ("Redstone ore", 6), ("Grass", 3)],
            release.Materials);
        Assert.Equal([("coal", 6, 14), ("iron", 6, 9), ("gold", 0, 4), ("redstone", 6, 15), ("diamond", 0, 0), ("lapis", 0, 5)],
            release.Ores.Select(o => (o.Ore.Id, o.Mined, o.Exposed)));
        var none = MineStats.Of(MineTerrain.For("hello", "1.12.2"), []);
        Assert.Equal((0, (int?)null), (none.ToMine, none.YRange?.Min));
        Assert.Equal(0, none.SeenPer100());
        Assert.Equal("nothing dug yet", none.Text());
        Assert.Equal(100.0 * beta.Seen / beta.ToMine, beta.SeenPer100(), 6);
        Assert.Contains("934 blocks to mine (848 stone, 45 dirt, 13 iron ore, 10 gravel, …)", beta.Text());
        Assert.Contains("diamond 3+4", beta.Text());
    }

    [Fact]
    public void Following_the_veins_finds_at_least_what_the_walls_show()
    {
        var terrain = MineTerrain.For("12345", "b1.8.1");
        var dug = GoldenDig();
        var stats = MineStats.Of(terrain, dug);
        var found = OreVeins.For(terrain).Yield(dug);
        Assert.Same(OreVeins.For(terrain), OreVeins.For(terrain)); // worked out once per terrain
        Assert.Equal(1, found[0].Ore.Veins); // rarest first (b1.8.1: lapis, one vein of 6, then diamond, one of 7)
        foreach (var y in found)
        {
            var seen = stats.Ores.Single(o => o.Ore.Id == y.Ore.Id).Seen;
            Assert.True(y.Blocks >= seen, $"{y.Ore.Id}: {y.Blocks} found < {seen} seen");
            Assert.InRange(y.Coverage, 0, 1);
            Assert.True(y.Veins == 0 == (y.Blocks == 0));
        }
        Assert.All(OreVeins.For(terrain).Yield([]), y => Assert.Equal(0, y.Blocks));
    }

    [Fact]
    public void The_best_levels_of_a_plot_are_in_each_ores_band()
    {
        var terrain = MineTerrain.For("4242", "1.12.2");
        var best = OreLevels.Best(terrain);
        var diamond = best.Single(b => b.Ore.Id == "diamond");
        Assert.Equal(3, diamond.Best.Count);
        Assert.All(diamond.Best, l => Assert.InRange(l.Y, 5, 16));
        Assert.True(diamond.Best[0].Blocks >= diamond.Best[1].Blocks);
        Assert.All(best.Single(b => b.Ore.Id == "iron").Best, l => Assert.InRange(l.Y, 5, 64));
        var text = OreLevels.Text(terrain, 2, "diamond");
        Assert.StartsWith("best feet levels for a 1x2 strip mine", text);
        Assert.Contains($"diamond y{diamond.Best[0].Y} {diamond.Best[0].Blocks} ({diamond.Best[0].Veins} veins)", text);
        Assert.DoesNotContain("redstone", text);
        Assert.Equal("no ore 'mithril' in 1.12.2", OreLevels.Text(terrain, 3, "mithril"));
    }

    [Fact]
    public void Strip_mines_default_to_the_studys_best_layout_and_every_version_has_an_efficient_and_a_thorough_preset()
    {
        Assert.Equal((11, 1, 2, 4), (StripSpec.Default.Y, StripSpec.Default.Width, StripSpec.Default.Height, StripSpec.Default.Spacing));
        Assert.Contains("4 apart", MineTemplates.FromStep(JsonDocument.Parse("""{"template":"strip"}""").RootElement, null).Label);
        foreach (var v in new[] { "1.12.2", "b1.8.1" })
        {
            var diamond = MinePresets.Get($"{v}-diamond-y11")!;
            var thorough = MinePresets.Get($"{v}-diamond-y11-thorough")!;
            var terrain = MineTerrain.For("777", v);
            var (a, b) = (MinePresets.Cells(diamond, terrain), MinePresets.Cells(thorough, terrain));
            Assert.Contains("4 apart", a.Label + " " + MineTemplates.FromStep(diamond.Steps[1], terrain).Label);
            Assert.Contains("1×2 × 30", MineTemplates.FromStep(diamond.Steps[1], terrain).Label); // a 1x2 corridor
            // The thorough one digs more for more ore, the efficient one finds more per block.
            var (sa, sb) = (MineStats.Of(terrain, Dug(a)), MineStats.Of(terrain, Dug(b)));
            Assert.True(sb.ToMine > sa.ToMine);
            Assert.Equal(v, thorough.Version);
        }
    }

    [Fact]
    public void The_ai_surveys_a_mine_canvas_and_refuses_a_build_one()
    {
        var mine = new DesignerCanvas { Kind = DesignerCanvas.KindMine, Seed = "4242", Version = "1.12.2" };
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        Assert.Equal("nothing dug yet", DesignerTools.Run(mine, "mine_stats", J("{}")).Text);
        DesignerTools.Run(mine, "strip_mine", J("""{"at":[2,11,31],"length":40}"""));
        var (stats, op, err) = DesignerTools.Run(mine, "mine_stats", J("{}"));
        Assert.False(err, stats);
        Assert.Null(op); // a survey changes nothing
        Assert.Contains("blocks to mine", stats);
        Assert.Contains("ore per 100 blocks mined", stats);
        Assert.Contains("following the veins:", stats);
        var (levels, _, lerr) = DesignerTools.Run(mine, "ore_levels", J("""{"ore":"diamond"}"""));
        Assert.False(lerr, levels);
        Assert.Contains("diamond y", levels);
        Assert.True(DesignerTools.Run(new DesignerCanvas(), "ore_levels", J("{}")).Error);
        Assert.Contains("4 solid blocks apart", DesignerTools.MineSystem("fox", mine));
        Assert.Contains(DesignerTools.Mine, t => t.Name == "mine_stats");
        Assert.DoesNotContain(DesignerTools.All, t => t.Name == "ore_levels"); // a build turn's tools are unchanged
    }
}
