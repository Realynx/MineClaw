using System.Globalization;
using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the crew slicer (<see cref="CrewSlicer.Slice"/>: strips for big designs, the one-plan lane cut, waits, estimates) over a
/// few designs and crews of 1..4: every step's signature, each lane's body, steps, waits and estimate. A refactor must leave
/// <see cref="CrewSlicerGoldenData.Expected"/> unchanged. Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class CrewSlicerGoldenTests
{
    private static readonly (int X, int Z)[] Starts = [(0, 5), (-7, 6), (7, 6), (-7, -6)];
    private static readonly string[] Names = ["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420"];

    private static string Sig(PlanStep s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s.Index} {s.Action} {s.Technique} {s.Target} {s.Id}:{s.Meta} {s.Stand} {s.Face} {s.Column} [{string.Join(",", s.Deps)}] {s.Gate} L{s.Lane}");

    private static void DumpPlan(StringBuilder sb, BuildPlan bp)
    {
        sb.Append(CultureInfo.InvariantCulture, $"steps {bp.Steps.Count} infeasible {bp.Infeasible.Count} solo {bp.SoloTicks:0.##} crew {bp.CrewTicks:0.##} home {bp.Home}\n");
        foreach (var s in bp.Steps) sb.Append("  ").Append(Sig(s)).Append('\n');
        foreach (var l in bp.Lanes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  lane {l.Index} {l.Body} est {l.EstTicks:0.##} steps [{string.Join(",", l.Steps)}]\n");
            foreach (var (i, w) in l.Waits.OrderBy(kv => kv.Key)) sb.Append($"    wait {i} <- [{string.Join(",", w)}]\n");
        }
    }

    internal static string Dump()
    {
        var designs = new (string Name, string Dsl, BlockPos At, int Field, BuildPlannerOptions O)[]
        {
            ("hut", BlueprintTests.Hut, new BlockPos(-2, 64, -3), 8, new BuildPlannerOptions { GateBlocks = 0 }),
            ("hall", CrewBuildTests.Hall, CrewBuildTests.HallAt, 8, new BuildPlannerOptions { GateBlocks = 0 }),
            ("hall gated", CrewBuildTests.Hall, CrewBuildTests.HallAt, 8, new BuildPlannerOptions { GateBlocks = 4 }),
            ("tower", ClimbTests.HollowTower, new BlockPos(-1, 64, -1), 8, new BuildPlannerOptions { GateBlocks = 0 }),
            ("pyramid strips", MassiveBuildTests.Pyramid(11, "S=cobblestone"), new BlockPos(-5, 64, -5), 14, new BuildPlannerOptions { GateBlocks = 0, StripsAbove = 50 }),
            ("pyramid one plan", MassiveBuildTests.Pyramid(11, "S=cobblestone"), new BlockPos(-5, 64, -5), 14, new BuildPlannerOptions { GateBlocks = 0 }),
        };
        var sb = new StringBuilder();
        foreach (var (name, dsl, at, field, o) in designs)
            for (int n = 1; n <= 4; n++)
            {
                var w = MassiveBuildTests.Field(field);
                var plan = MassiveBuildTests.PlanOf(dsl.ReplaceLineEndings("\n"), at);
                var crew = Enumerable.Range(0, n).Select(i => new CrewMember(Names[i], new BlockPos(Starts[i].X, 64, Starts[i].Z))).ToList();
                sb.Append("== ").Append(name).Append(" x").Append(n).Append('\n');
                DumpPlan(sb, CrewSlicer.Slice(plan, w.PlanningView, crew, null, o));
            }
        return sb.ToString();
    }

    [Fact]
    public void Slices_match_the_golden_dump()
    {
        var actual = Dump();
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        var expected = CrewSlicerGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n');
        actual = actual.TrimEnd('\n');
        if (expected == actual) return;
        var a = actual.Split('\n');
        var e = expected.Split('\n');
        int i = 0;
        while (i < Math.Min(a.Length, e.Length) && a[i] == e[i]) i++;
        Assert.Fail($"slice differs at line {i + 1}:\n expected: {(i < e.Length ? e[i] : "<end>")}\n actual:   {(i < a.Length ? a[i] : "<end>")}");
    }
}
