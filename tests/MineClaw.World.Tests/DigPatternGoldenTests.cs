using System.Text;

namespace MineClaw.World.Tests;

/// <summary>
/// Dig pattern golden (complexity refactor WP04, checkpoint 3), taken before <see cref="DigPattern.Parse"/> and
/// <see cref="DigPattern.Learn"/> were reshaped: a corpus of rule texts (every keyword, alias, separator and error) with the parsed
/// pattern's rule, repeats, depth and turned copy, and the patterns learned back from their dug cells (whole, cut short, with stray
/// cells, missing cells) and from random cell sets.
/// </summary>
public class DigPatternGoldenTests
{
    private static readonly string[] Rules =
    [
        "column 3 step 0,-1,0 turn -90 pivot 0.5,0.5", "column 3 step 1,-1,0", "column 2 step 1,0,0", "unit 0,0,0 0,1,0 step 1,0,0",
        "unit 0,0,0 1,0,0 0,-1,0 1,-1,0 0,-2,0 1,-2,0 step 0,-1,0 turn 90 pivot 1,1", "COLUMN 4; STEP 0,-1,0; TURN 180 PIVOT 0.5,0",
        "column=3 step=0,-1,0 turn=-90° about=0.5,0.5", "column 3 step 0,-1,0 turn 270 pivot 0.5,0.5", "column 3 step 0,-1,0 turn 450 pivot 0,0",
        "column 3 step 0,1,0 turn 90 pivot 0.5,0.5", "column 0 step 0,-1,0", "column 17 step 0,-1,0", "column x step 0,-1,0", "column",
        "unit step 0,-1,0", "unit 0,0,0 0,0,0 0,-1,0 step 0,-1,0", "column 3 step", "column 3 step 0,-1", "column 3 step a,b,c",
        "column 3 step 0,-1,0 turn 45", "column 3 step 0,-1,0 turn", "column 3 step 0,-1,0 turn 90 pivot 0.5", "column 3 step 0,-1,0 turn 90 pivot x,y",
        "column 3 step 0,-1,0 turn 90 pivot 0.5,0", "column 3 step 0,-1,0 turn 90 pivot 0.25,0.25", "column 3 step 1,0,0 turn 90 pivot 0.5,0.5",
        "column 3 step 0,0,0", "column 3 step 0,0,0 turn 360", "column 3 step 0,0,0 turn 90 pivot 0.5,0.5", "column 3", "step 0,-1,0",
        "dig column 3 step 0,-1,0", "column 3 step 0,-1,0 depth 5", "", "   ", "column 2 step 2,-1,1 turn 0", "column 1 step 0,-2,0 turn 180 pivot 1.5,-0.5",
        "unit 0,0,0 0,-1,0 1,0,0 step 1,-1,0 pivot 3,3", "unit 1,2,3 4,5,6 step -1,-1,-1 turn -90 pivot -0.5,-0.5", "column 16 step 0,-1,0",
        "column 1 step 0,-1,0 turn -180 pivot 0,0", "unit 0,0 step 0,-1,0", "column 3 step 0,-1,0 turn 90 pivot 1e1,2E1",
    ];

    [Fact]
    public void Patterns_match_the_golden() => Golden.Check("digpattern", "9A836DB053B93540E79C0F8F2633F3BDEE8A9943B5CAFC356F5D2AB507664348", sb =>
    {
        var anchor = new BlockPos(10, 60, -4);
        foreach (var rule in Rules)
        {
            var p = DigPattern.Parse(rule, anchor, out var error);
            sb.Append('[').Append(rule).Append("] ");
            if (p is null) { sb.Append("error ").Append(error).Append('\n'); continue; }
            sb.Append(p).Append(" turn=").Append(p.Turn).Append(" pivot=").Append(Golden.R(p.PivotX)).Append(',').Append(Golden.R(p.PivotZ))
                .Append(" to30=").Append(p.RepeatsTo(30, 64)?.ToString() ?? "-").Append(" at=").Append(p.At(new BlockPos(-3, 70, 5), 90)).Append('\n');
            for (int k = 0; k < 3; k++) sb.Append("  r").Append(k).Append(' ').Append(string.Join(' ', p.Repeat(k))).Append('\n');
            int reps = 5 + rule.Length % 4;
            var cells = p.Cells(0, reps);
            Learned(sb, "whole", cells);
            Learned(sb, "short", p.Cells(0, 3));
            var noisy = new HashSet<BlockPos>(cells) { new(anchor.X + 5, anchor.Y, anchor.Z + 5), new(anchor.X - 4, anchor.Y - 2, anchor.Z) };
            Learned(sb, "noisy", noisy);
            var holes = new HashSet<BlockPos>(cells);
            int drop = 0;
            foreach (var c in p.Indexed(reps - 1, 1)) if (drop++ % 2 == 0) holes.Remove(c.Pos);
            Learned(sb, "holes", holes);
        }
        var rng = new Random(42);
        for (int i = 0; i < 12; i++)
        {
            var set = new HashSet<BlockPos>();
            int n = rng.Next(1, 30);
            for (int j = 0; j < n; j++) set.Add(new BlockPos(rng.Next(-3, 4), rng.Next(50, 60), rng.Next(-3, 4)));
            Learned(sb, $"random{i}", set);
        }
        // Lists in order (no set): the learner's own HashSet.
        Learned(sb, "list", new List<BlockPos> { new(0, 64, 0), new(0, 63, 0), new(0, 62, 0), new(1, 62, 0), new(1, 61, 0), new(1, 60, 0), new(2, 60, 0), new(2, 59, 0) });
        sb.Append("floors ").Append(string.Join(' ', DigPattern.Floors(new HashSet<BlockPos> { new(0, 5, 0), new(0, 4, 0), new(1, 4, 0) }))).Append('\n');
    });

    private static void Learned(StringBuilder sb, string label, IReadOnlyCollection<BlockPos> cells)
    {
        var l = DigPattern.Learn(cells);
        sb.Append("  learn ").Append(label).Append(' ').Append(cells.Count).Append(": ");
        if (l is null) { sb.Append("-\n"); return; }
        sb.Append(l.Summary).Append(" | ").Append(l.Pattern).Append(" | out=").Append(string.Join(' ', l.Outliers)).Append(" missing=").Append(l.Missing)
            .Append(" pivot=").Append(Golden.R(l.Pattern.PivotX)).Append(',').Append(Golden.R(l.Pattern.PivotZ)).Append('\n');
    }
}
