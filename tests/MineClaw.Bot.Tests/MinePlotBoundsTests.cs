using System.Text.Json;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class MinePlotBoundsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mine_parser_accepts_256_sides_and_128_height_regardless_of_header_order(bool kindFirst)
    {
        var header = kindFirst ? "kind mine\nsize 256x128x256" : "size 256x128x256\nkind mine";
        var (bp, issues) = BlueprintParser.Parse($"blueprint edge\n{header}\nfacing south\nclear 0,0,0 0,0,0\nclear 255,127,255 255,127,255\n");
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        var (plan, more) = BlueprintNormalizer.Normalize(bp, new BlockPos(0, 1, 0), 256);
        Assert.DoesNotContain(more, i => i.Level == IssueLevel.Error);
        Assert.Equal(2, plan!.Air.Count);
        Assert.Contains(new BlockPos(255, 128, 255), plan.Air);
    }

    [Theory]
    [InlineData("mine", "257x1x1", true)]
    [InlineData("mine", "1x1x257", true)]
    [InlineData("mine", "1x129x1", true)]
    [InlineData("build", "65x1x1", true)]
    [InlineData("build", "1x1x65", true)]
    [InlineData("build", "1x33x1", true)]
    [InlineData("build", "64x32x64", false)]
    public void Mine_and_build_limits_remain_separate(string kind, string size, bool rejected)
    {
        var (_, issues) = BlueprintParser.Parse($"blueprint bounds\nsize {size}\nkind {kind}\nfacing south\nclear 0,0,0 0,0,0\n");
        Assert.Equal(rejected, issues.Any(i => i.Level == IssueLevel.Error && i.Code == "size"));
    }

    [Fact]
    public void Inferred_layer_width_uses_the_design_kind()
    {
        var rows = $"palette A=stone\nlayer 0\n{new string('A', 256)}\n";
        var (mine, issues) = BlueprintParser.Parse("blueprint wide\nkind mine\nfacing south\n" + rows);
        Assert.Equal(256, mine.W);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Contains(BlueprintParser.Parse("blueprint wide\n" + rows).Issues, i => i.Code == "size" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Canvas_persistence_and_replace_include_far_edge_cells()
    {
        var canvas = new DesignerCanvas { Kind = "mine", Seed = "4242", Version = "1.12.2" };
        canvas.Apply([new CanvasCell(255, 11, 255, 1, 0), new CanvasCell(200, 127, 200, 0, 0), new CanvasCell(256, 11, 0, 1, 0)], out var rejected);
        Assert.Equal(1, rejected);
        var decoded = DesignerCanvas.Decode(canvas.Encode(), canvas.Rev, "mine", "4242", "1.12.2");
        Assert.Equal((1, 0), decoded.Get(255, 11, 255));
        Assert.Contains(new BlockPos(200, 127, 200), decoded.Dug);
        var (text, _, error) = DesignerTools.Run(decoded, "replace", JsonSerializer.SerializeToElement(new { a = "stone", b = "cobblestone" }));
        Assert.False(error, text);
        Assert.Equal((4, 0), decoded.Get(255, 11, 255));
    }

    [Theory]
    [InlineData('x')]
    [InlineData('z')]
    public void Strip_templates_reach_192_cells_with_96_cell_branches(char axis)
    {
        var strip = MineTemplates.Strip(new StripSpec(axis == 'x' ? 32 : 96, 11, axis == 'x' ? 96 : 32, axis, Length: 999, BranchLength: 999));
        Assert.Equal(192, strip.Path.Count);
        Assert.Equal(axis == 'x' ? new BlockPos(223, 11, 96) : new BlockPos(96, 11, 223), strip.Path[^1]);
        var branch = axis == 'x' ? new CanvasCell(36, 11, 0, 0, 0) : new CanvasCell(0, 11, 36, 0, 0);
        Assert.Contains(branch, strip.Cells);
        Assert.Equal(0, strip.Cells.Min(c => axis == 'x' ? c.Z : c.X));
        Assert.Equal(192, strip.Cells.Max(c => axis == 'x' ? c.Z : c.X));
        var canvas = new DesignerCanvas { Kind = "mine", Seed = "4242", Version = "1.12.2" };
        canvas.Apply(strip.Cells, out var rejected);
        Assert.Equal(0, rejected);
    }

    [Fact]
    public void Authored_mines_follow_ore_beyond_the_64_cell_survey_sample()
    {
        var terrain = MineTerrain.For("4242", "1.12.2");
        var oreCell = (from x in Enumerable.Range(200, 8)
                       from z in Enumerable.Range(200, 8)
                       from y in Enumerable.Range(5, 30)
                       let block = terrain.Block(x, y, z)
                       where OreTable.OreOf(terrain.Version, block.Id, block.Meta) != null
                       select new BlockPos(x, y, z)).First();
        var found = OreVeins.Following(terrain, [oreCell]).Where(y => y.Blocks > 0).ToList();
        Assert.NotEmpty(found);
        Assert.All(found, y => Assert.True(y.Blocks >= y.Veins));
        var canvas = new DesignerCanvas { Kind = "mine", Seed = "4242", Version = "1.12.2" };
        canvas.Apply([new CanvasCell(oreCell.X, oreCell.Y, oreCell.Z, 0, 0)]);
        var (text, _, error) = DesignerTools.Run(canvas, "mine_stats", JsonSerializer.SerializeToElement(new { }));
        Assert.False(error, text);
        Assert.DoesNotContain("following the veins: nothing", text);
        Assert.DoesNotContain("of the plot's", text); // No false percentage from the unrelated 64×64 sample.
    }
}
