using System.Text;
using System.Text.Json;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the mine templates (<see cref="MineTemplates.Spiral"/>, <see cref="MineTemplates.Strip"/>, <see cref="MineTemplates.FromStep"/>,
/// the ring geometry) over parameter grids and step spellings: every label, entry, path and cell hash. The browser's port must keep
/// the same cells, so a refactor must leave <see cref="MineTemplatesGoldenData.Expected"/> unchanged. Regenerate (only for an
/// intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class MineTemplatesGoldenTests
{
    private static void Cells(StringBuilder sb, MineCells m) =>
        sb.Append("  ").Append(m.Label).Append(" | entry ").Append(m.Entry).Append(" path ").Append(m.Path.Count).Append(' ')
            .Append(m.Path.Count == 0 ? "-" : $"{m.Path[0]}..{m.Path[^1]}").Append(" cells ").Append(m.Cells.Count).Append(' ')
            .Append(MineTemplates.Hash(m.Cells).ToString("x8")).Append('\n');

    private static readonly string[] Steps =
    [
        "{}",
        """{"template":"spiral"}""",
        """{"template":"Spiral_Shaft","x":5,"z":7,"size":5,"core":1,"top":40,"bottom":20,"dir":"CCW","torchEvery":3,"landings":[30,20,"x",99]}""",
        """{"template":"shaft","x":"12","z":"9","size":"6","top":"surface","bottom":"30","dir":"counterclockwise","torch_every":"4"}""",
        """{"template":"spiral","x":2.5,"z":true,"size":9,"core":9}""",
        """{"template":"strip"}""",
        """{"template":"strip_mine","x":3,"y":14,"z":8,"axis":"Z","length":"20","width":2,"height":3,"spacing":2,"branchLength":5,"sides":"LEFT","torchEvery":5}""",
        """{"template":"strip","axis":"  ","branch_length":3,"torch_every":2,"sides":"right","length":12,"spacing":0}""",
        """{"template":"strip","axis":"xyz","length":100,"width":5,"height":1,"spacing":20,"branchLength":-3,"sides":"none"}""",
        """{"template":"tunnel"}""",
        "[1,2]",
    ];

    internal static string Dump()
    {
        var sb = new StringBuilder();
        for (int n = 3; n <= 6; n++)
        {
            sb.Append("ring ").Append(n).Append(':');
            for (int lz = 0; lz < n; lz++)
                for (int lx = 0; lx < n; lx++) sb.Append(' ').Append(MineTemplates.EdgeOf(lx, lz, n)).Append('/').Append(MineTemplates.RingIndex(lx, lz, n));
            sb.Append(" outer");
            for (int i = 0; i < 4 * (n - 1); i++) sb.Append(' ').Append(MineTemplates.Outer(i, n));
            sb.Append('\n');
        }
        foreach (var size in new[] { 2, 3, 4, 5, 6, 7 })
            foreach (var core in new[] { 0, 1, 2, 4 })
                foreach (var cw in new[] { true, false })
                {
                    sb.Append("spiral ").Append(size).Append(' ').Append(core).Append(' ').Append(cw).Append('\n');
                    Cells(sb, MineTemplates.Spiral(new SpiralSpec(10, 12, 60, 40, size, core, cw, size, size % 2 == 0 ? [45, 40, 45, 70] : null)));
                }
        Cells(sb, MineTemplates.Spiral(new SpiralSpec(0, 0, 5, 9)));
        Cells(sb, MineTemplates.Spiral(new SpiralSpec(0, 0, 3, -4, TorchEvery: 1)));
        foreach (var axis in new[] { 'x', 'z', 'Z', 'q' })
            foreach (var sides in new[] { "both", "left", "RIGHT", "none", "weird" })
                foreach (var (w, h) in new[] { (1, 2), (2, 3), (0, 9) })
                    foreach (var torch in new[] { 0, 1, 4, 5 })
                    {
                        sb.Append("strip ").Append(axis).Append(' ').Append(sides).Append(' ').Append(w).Append('x').Append(h).Append(" t").Append(torch).Append('\n');
                        Cells(sb, MineTemplates.Strip(new StripSpec(4, 20, 6, axis, 17, w, h, 3, 4, sides, torch)));
                    }
        Cells(sb, MineTemplates.Strip(new StripSpec(0, 5, 0, Length: 0, Spacing: 0, BranchLength: 99, Sides: null!)));
        Cells(sb, MineTemplates.Strip(new StripSpec(0, 5, 0, Length: 99, Spacing: 99, TorchEvery: 3)));
        var terrain = MineTerrain.For("4242", "1.12.2");
        foreach (var json in Steps)
            foreach (var t in new[] { null, terrain })
                foreach (var template in new[] { null, "strip" })
                {
                    sb.Append("step ").Append(json).Append(t == null ? " no terrain" : " terrain").Append(template == null ? "" : " as strip").Append('\n');
                    try { Cells(sb, MineTemplates.FromStep(JsonDocument.Parse(json).RootElement, t, template)); }
                    catch (Exception e) { sb.Append("  THROW ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n'); }
                }
        return sb.ToString();
    }

    [Fact]
    public void Mine_templates_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(MineTemplatesGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
