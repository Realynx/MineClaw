using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the zero-token construction order (<see cref="ConstructionPlanner.Plan"/>: clears, support-first placements, scaffold
/// paths and walkways, scaffold removal) over designs and worlds (a hut on a wide and a tiny island, an arm over the void, a pole,
/// an arch, a bridge off the edge, obstructions in the cells, per strip), the strips (<see cref="ConstructionPlanner.Split"/>,
/// <see cref="ConstructionPlanner.SplitLayers"/>) and <see cref="ConstructionPlanner.StepFor"/> over every block kind and meta.
/// A refactor must leave <see cref="ConstructionPlannerGoldenData.Expected"/> unchanged. Regenerate (only for an intended
/// change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class ConstructionPlannerGoldenTests
{
    private const string Arm = "size 4x3x1\npalette C=cobblestone\nlayer 0\nC___\nlayer 2\nC__C";
    private const string Pole = "size 1x9x1\npalette C=cobblestone\nlayer 0-8\nC";
    private const string Arch = "size 5x5x1\npalette C=cobblestone\nlayer 0-3\nC...C\nlayer 4\nCCCCC";
    private const string Bridge = "size 9x1x1\npalette C=cobblestone\nlayer 0\nCCCCCCCCC";
    private const string Floating = "size 7x4x7\npalette P=planks\nlayer 3\nPPPPPPP\nPPPPPPP\nPPPPPPP\nPPPPPPP\nPPPPPPP\nPPPPPPP\nPPPPPPP";

    private static WorldState Cluttered()
    {
        var w = BlueprintTests.Island();
        w.SetBlock(-1, 64, -2, Blocks.Dirt, 0);
        w.SetBlock(0, 65, -1, Blocks.Log, 0);
        w.SetBlock(1, 64, 0, Blocks.Glass, 0);
        w.SetBlock(0, 64, 1, Blocks.Cobblestone, 0);
        w.SetBlock(2, 64, 2, Blocks.Water, 0);
        return w;
    }

    private static IEnumerable<(string Name, string Dsl, BlockPos At, Func<WorldState> World, int MaxScaffold, double Reach)> Cases() =>
    [
        ("hut", BlueprintTests.Hut, new BlockPos(-2, 64, -2), () => BlueprintTests.Island(), 6, 4.5),
        ("hut tiny island", BlueprintTests.Hut, new BlockPos(-2, 64, -2), () => BlueprintTests.Island(1), 6, 4.5),
        ("hut cluttered", BlueprintTests.Hut, new BlockPos(-2, 64, -2), Cluttered, 6, 4.5),
        ("hut reach 3", BlueprintTests.Hut, new BlockPos(-2, 64, -2), () => BlueprintTests.Island(), 6, 3),
        ("arm", Arm, new BlockPos(0, 64, 0), () => BlueprintTests.Island(1), 6, 4.5),
        ("arm scaffold 1", Arm, new BlockPos(0, 64, 0), () => BlueprintTests.Island(1), 1, 4.5),
        ("pole", Pole, new BlockPos(0, 64, 0), () => BlueprintTests.Island(), 6, 4.5),
        ("arch", Arch, new BlockPos(-2, 64, 0), () => BlueprintTests.Island(), 6, 4.5),
        ("bridge", Bridge, new BlockPos(4, 64, 0), () => BlueprintTests.Island(), 6, 4.5),
        ("bridge scaffold 2", Bridge, new BlockPos(4, 64, 0), () => BlueprintTests.Island(), 2, 4.5),
        ("bridge reach 2.5", Bridge, new BlockPos(4, 64, 0), () => BlueprintTests.Island(), 6, 2.5),
        ("bridge reach 2.5 scaffold 2", Bridge, new BlockPos(4, 64, 0), () => BlueprintTests.Island(), 2, 2.5),
        ("floating reach 2", Floating, new BlockPos(-3, 64, -3), () => BlueprintTests.Island(), 6, 2),
        ("floating off the edge reach 2", Floating, new BlockPos(4, 64, -3), () => BlueprintTests.Island(), 6, 2),
        ("floating off the edge reach 3 scaffold 9", Floating, new BlockPos(5, 64, -3), () => BlueprintTests.Island(), 9, 3),
        ("floating", Floating, new BlockPos(-3, 64, -3), () => BlueprintTests.Island(), 6, 4.5),
        ("floating off the edge", Floating, new BlockPos(4, 64, -3), () => BlueprintTests.Island(), 6, 4.5),
    ];

    private static void DumpOrder(StringBuilder sb, ConstructionOrder o)
    {
        sb.Append("  steps ").Append(o.Steps.Count).Append(" scaffolds ").Append(o.Scaffolds).Append(" unreachable [")
            .Append(string.Join(" ", o.Unreachable)).Append("]\n");
        foreach (var s in o.Steps) sb.Append("  ").Append(s).Append('\n');
    }

    private static void DumpParts(StringBuilder sb, string what, List<BuildPart> parts)
    {
        foreach (var p in parts)
            sb.Append("  ").Append(what).Append(' ').Append(p.Label).Append(" cells ").Append(p.Cells.Count).Append(' ').Append(p).Append('\n');
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var c in Cases())
        {
            var (plan, issues) = BlueprintNormalizer.FromText(c.Dsl, c.At);
            sb.Append("== ").Append(c.Name).Append('\n');
            if (plan == null)
            {
                sb.Append("  no plan: ").Append(string.Join("; ", issues.Select(i => i.ToString()))).Append('\n');
                continue;
            }
            var w = c.World();
            DumpOrder(sb, ConstructionPlanner.Plan(plan, w.PlanningView, null, c.MaxScaffold, c.Reach));
            foreach (int n in new[] { 1, 2, 3, 5 })
            {
                var parts = ConstructionPlanner.Split(plan, n, 0);
                DumpParts(sb, $"split {n}", parts);
                if (n == 2)
                    foreach (var part in parts)
                    {
                        sb.Append(" part ").Append(part.Label).Append('\n');
                        DumpOrder(sb, ConstructionPlanner.Plan(plan, c.World().PlanningView, part, c.MaxScaffold, c.Reach));
                    }
            }
            foreach (int n in new[] { 1, 2, 4 }) DumpParts(sb, $"layers {n}", ConstructionPlanner.SplitLayers(plan, n, -1));
        }
        sb.Append("== step for\n");
        int[] ids =
        [
            Blocks.Cobblestone, Blocks.WoodenStairs, Blocks.CobblestoneStairs, Blocks.WoodenDoor, Blocks.IronDoor, Blocks.Chest, Blocks.Furnace,
            Blocks.LitFurnace, Blocks.Dispenser, Blocks.FenceGate, Blocks.Ladder, Blocks.Torch, Blocks.Pumpkin, Blocks.Bed, Blocks.Slab, Blocks.Sand,
            Blocks.GlassPane, Blocks.Lever,
        ];
        foreach (int id in ids)
            for (int meta = 0; meta < 16; meta++)
                sb.Append("  ").Append(ConstructionPlanner.StepFor(new PlannedBlock(new BlockPos(1, 2, 3), id, meta))).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Orders_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(ConstructionPlannerGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
