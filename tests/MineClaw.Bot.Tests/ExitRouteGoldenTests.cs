using System.Text;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="ExitRoute.Plan"/> (walk, dig my own pillar, a stair down with supports and overshoot guards) over several worlds:
/// a platform on a post, a floating block, a pillar of my own, a tower wall, an island edge, water, limited blocks, a short reach
/// and refused cells. Every move, the end, and the planner's trace. A refactor must leave <see cref="ExitRouteGoldenData.Expected"/>
/// unchanged. Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class ExitRouteGoldenTests
{
    private sealed record Case(string Name, Func<WorldState> World, BlockPos Feet, BlockPos Home, Func<BlockPos, bool> Mine, Func<BlockPos, bool> MayPlace,
        int Blocks, double Reach = 4.5, int MaxLevels = 48);

    private static WorldState Post(int height, int platform = 1, int r = 3)
    {
        var w = BlueprintTests.Island(r);
        for (int y = 64; y < 64 + height; y++) w.SetBlock(0, y, 0, Blocks.Stone, 0);
        for (int x = -platform + 1; x <= platform - 1; x++) for (int z = -platform + 1; z <= platform - 1; z++) w.SetBlock(x, 64 + height, z, Blocks.Cobblestone, 0);
        return w;
    }

    private static WorldState Floating(int y)
    {
        var w = BlueprintTests.Island(4);
        w.SetBlock(1, y, 1, Blocks.Cobblestone, 0);
        return w;
    }

    private static WorldState Pillar(int top, int x = 1, int z = 1, int r = 3)
    {
        var w = BlueprintTests.Island(r);
        for (int y = 64; y <= top; y++) w.SetBlock(x, y, z, Blocks.Cobblestone, 0);
        return w;
    }

    private static WorldState Tower()
    {
        var w = BlueprintTests.Island(5);
        for (int y = 64; y <= 74; y++) for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++) w.SetBlock(x, y, z, Blocks.Stone, 0);
        w.SetBlock(2, 74, 0, Blocks.Cobblestone, 0);
        return w;
    }

    private static WorldState Wet()
    {
        var w = Post(6, 2);
        for (int x = 1; x <= 3; x++) for (int z = -3; z <= 3; z++) w.SetBlock(x, 64, z, Blocks.Water, 0);
        return w;
    }

    private static WorldState Walled()
    {
        var w = Post(7, 2);
        for (int y = 72; y <= 73; y++) { w.SetBlock(-1, y, 2, Blocks.Glass, 0); w.SetBlock(1, y, -2, Blocks.Glass, 0); w.SetBlock(2, y, 1, Blocks.Glass, 0); }
        return w;
    }

    private static readonly BlockPos Home = new(0, 64, 0);

    private static IEnumerable<Case> Cases()
    {
        bool All(BlockPos _) => true;
        bool None(BlockPos _) => false;
        yield return new("walk", () => Post(2, 2), new BlockPos(0, 67, 0), Home, None, All, 64);
        yield return new("already home", () => Post(0), Home, Home, None, All, 64);
        yield return new("post 7", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, All, 64);
        yield return new("post 7 corner", () => Post(7, 2), new BlockPos(1, 72, 1), Home, None, All, 64);
        yield return new("post 7 few blocks", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, All, 3);
        yield return new("post 7 no blocks", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, All, 0);
        yield return new("post 7 east only", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, p => p.X >= 1, 64);
        yield return new("post 7 low only", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, p => p.Y < 69, 64);
        yield return new("post 7 nothing", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, None, 64);
        yield return new("post 7 reach 3", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, All, 64, 3.0);
        yield return new("post 7 two levels", () => Post(7, 2), new BlockPos(0, 72, 0), Home, None, All, 64, 4.5, 2);
        yield return new("post 1x1", () => Post(8), new BlockPos(0, 73, 0), Home, None, All, 64);
        yield return new("post 12 edge", () => Post(12, 2, 2), new BlockPos(0, 77, 0), Home, None, All, 64);
        yield return new("floating", () => Floating(70), new BlockPos(1, 71, 1), Home, None, All, 64);
        yield return new("floating low", () => Floating(67), new BlockPos(1, 68, 1), Home, None, All, 64);
        yield return new("own pillar", () => Pillar(68), new BlockPos(1, 69, 1), Home, p => p.X == 1 && p.Z == 1, All, 64);
        yield return new("own pillar no place", () => Pillar(70), new BlockPos(1, 71, 1), Home, p => p.X == 1 && p.Z == 1, None, 0);
        yield return new("not my pillar", () => Pillar(68), new BlockPos(1, 69, 1), Home, None, All, 64);
        yield return new("pillar at the edge", () => Pillar(68, 3, 3), new BlockPos(3, 69, 3), Home, p => p.X == 3 && p.Z == 3, All, 64);
        yield return new("tower top", Tower, new BlockPos(0, 75, 0), Home with { X = 4, Z = 4 }, None, All, 64);
        yield return new("tower ledge", Tower, new BlockPos(2, 75, 0), Home with { X = 4, Z = 4 }, None, All, 64);
        yield return new("wet", Wet, new BlockPos(0, 71, 0), Home with { X = -2 }, None, All, 64);
        yield return new("walled", Walled, new BlockPos(0, 72, 0), Home, None, All, 64);
        yield return new("walled not glass", Walled, new BlockPos(0, 72, 0), Home, None, p => p.Y != 71, 64);
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var c in Cases())
        {
            var w = c.World();
            var trace = new List<string>();
            var plan = ExitRoute.Plan(w.PlanningView, c.Feet, c.Home, c.Mine, c.MayPlace, c.Blocks, c.MaxLevels, c.Reach, trace);
            sb.Append("== ").Append(c.Name).Append(" feet ").Append(c.Feet).Append(" home ").Append(c.Home).Append('\n');
            foreach (var t in trace) sb.Append("  trace ").Append(t).Append('\n');
            if (plan == null) { sb.Append("  stranded\n"); continue; }
            sb.Append("  end ").Append(plan.End).Append(" dug ").Append(plan.Dug).Append(" placed ").Append(plan.Placed).Append('\n');
            foreach (var m in plan.Moves) sb.Append("  ").Append(m).Append(" face ").Append(m.Face?.ToString() ?? "-").Append(m.Hidden ? " hidden" : "").Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Exit_routes_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(ExitRouteGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
