using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the supervisor's plan page (<see cref="BuildPlanView.Html"/>, its SVG) and <see cref="BuildPlan.Summary"/> over plans of
/// the slicer (a hut with a gate, a pillared pole, a slab reached by water columns, a bridge over the void with its floor hint, an
/// empty plan), half done, with the gate waiting, a transform, a note, lanes and two parts. A refactor must leave
/// <see cref="BuildPlanViewGoldenData.Expected"/> unchanged. Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class BuildPlanViewGoldenTests
{
    private const string Pole = "size 1x9x1\npalette C=cobblestone\nlayer 0-8\nC";
    private const string Slab = "size 3x1x3\npalette P=planks\nlayer 0\nPPP\nPPP\nPPP";
    private const string Bridge = "size 9x1x1\npalette C=cobblestone\nlayer 0\nCCCCCCCCC";

    private static BuildPlan Plan(string dsl, BlockPos at, WorldState w, BlockPos start, BuildPlannerOptions o)
    {
        var (plan, _) = BlueprintNormalizer.FromText(dsl, at, worldHeight: 128);
        return BuildPlanner.Plan(plan!, w.PlanningView, start, null, o);
    }

    private static IEnumerable<(string Name, Func<List<BuildPlan>> Parts)> Cases()
    {
        var start = new BlockPos(0, 64, 5);
        yield return ("hut", () => [Plan(BlueprintTests.Hut, new BlockPos(-2, 64, -3), MassiveBuildTests.Field(8), start, new BuildPlannerOptions { GateBlocks = 4 })]);
        yield return ("hut half done, gate waiting, turned, noted, lanes", () =>
        {
            var bp = Plan(BlueprintTests.Hut, new BlockPos(-2, 64, -3), MassiveBuildTests.Field(8), start, new BuildPlannerOptions { GateBlocks = 4 });
            for (int i = 0; i < bp.Steps.Count / 2; i++) bp.Steps[i].Status = i % 7 == 3 ? StepStatus.Skipped : StepStatus.Done;
            bp.Gate.State = GateState.Waiting;
            bp.Gate.Question = "is this the right <spot> & way?";
            bp.Transform = new BuildTransform(1, true, 2, 0, -1);
            bp.Note = "resumed after a restart";
            bp.State = "building";
            bp.Lanes =
            [
                new PlanLane { Index = 0, Body = "Alice", Steps = bp.Steps.Where(s => s.Index % 2 == 0).Select(s => s.Index).ToList(), EstTicks = 1200, State = "building" },
                new PlanLane { Index = 1, Body = "Alice_67", Steps = bp.Steps.Where(s => s.Index % 2 == 1).Select(s => s.Index).ToList(), EstTicks = 900, State = "stuck" },
            ];
            return [bp];
        });
        yield return ("pole", () => [Plan(Pole, new BlockPos(0, 64, 0), MassiveBuildTests.Field(8), start, new BuildPlannerOptions { GateBlocks = 0 })]);
        yield return ("slab water", () =>
        {
            var bp = Plan(Slab, new BlockPos(-1, 70, -1), MassiveBuildTests.Field(8), start, new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = true, Buckets = 2 });
            if (bp.Columns.Count > 0) bp.Columns[0].Removed = true;
            return [bp];
        });
        yield return ("bridge over the void", () =>
            [Plan(Bridge, new BlockPos(4, 64, 0), BlueprintTests.Island(3), new BlockPos(0, 64, 2), new BuildPlannerOptions { GateBlocks = 3, MaxScaffold = 2 })]);
        yield return ("two parts", () =>
        {
            var a = Plan(BlueprintTests.Hut, new BlockPos(-2, 64, -3), MassiveBuildTests.Field(8), start, new BuildPlannerOptions { GateBlocks = 0 });
            var b = Plan(Pole, new BlockPos(0, 64, 0), MassiveBuildTests.Field(8), start, new BuildPlannerOptions { GateBlocks = 0 });
            b.Part = 1;
            b.Parts = 2;
            a.Parts = 2;
            return [a, b];
        });
        yield return ("void platform", () =>
            [Plan("size 7x1x7\npalette C=cobblestone\nlayer 0\n" + string.Join("\n", Enumerable.Repeat("CCCCCCC", 7)), new BlockPos(8, 64, -3), BlueprintTests.Island(3),
                new BlockPos(0, 64, 2), new BuildPlannerOptions { GateBlocks = 0, MaxScaffold = 1 })]);
        foreach (var water in new[] { false, true })
        {
            yield return ("under the island" + (water ? " water" : ""), () =>
                [Plan(Slab, new BlockPos(1, 57, -1), BlueprintTests.Island(3), new BlockPos(0, 64, 2), new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = water, MaxScaffold = 12 })]);
            yield return ("under the edge" + (water ? " water" : ""), () =>
                [Plan("size 1x2x1\npalette C=cobblestone\nlayer 0\nC\nlayer 1\nC", new BlockPos(3, 61, 0), BlueprintTests.Island(3), new BlockPos(0, 64, 2),
                    new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = water })]);
        }
        yield return ("tower", () => [Plan(ClimbTests.HollowTower, new BlockPos(-1, 64, -1), MassiveBuildTests.Field(8), start, new BuildPlannerOptions { GateBlocks = 2 })]);
        yield return ("empty", () => [new BuildPlan()]);
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (name, parts) in Cases())
        {
            var ps = parts();
            sb.Append("== ").Append(name).Append('\n');
            foreach (var bp in ps) sb.Append("summary ").Append(bp.Summary()).Append('\n');
            sb.Append(BuildPlanView.Html(ps, "Plan #7 <\"hut\">").Replace("<", "\n<")).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Pages_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(BuildPlanViewGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
