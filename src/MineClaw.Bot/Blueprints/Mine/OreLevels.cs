using System.Globalization;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>One feet level's yield of an ore: what a strip mine across the plot at that level finds by following its veins.</summary>
public sealed record LevelYield(int Y, int Blocks, int Veins);

/// <summary>
/// Which level to mine on THIS plot: for every feet level in the ores' bands, the default strip mine (<see cref="StripSpec.Default"/>
/// across the whole plot) is laid and its vein yield counted (<see cref="OreVeins"/>), so the AI designer picks the level by what
/// the terrain holds, not only by the table's band.
/// </summary>
public static class OreLevels
{
    /// <summary>The best <paramref name="top"/> feet levels of each ore (rarest ore first), most found first.</summary>
    public static IReadOnlyList<(OreBand Ore, IReadOnlyList<LevelYield> Best)> Best(MineTerrain terrain, int top = 3, string? only = null)
    {
        var ores = terrain.Table.Ores.Where(o => o.Veins > 0 && (only == null || o.Id.Equals(only, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => o.Veins).ThenBy(o => o.Size).ToList();
        if (ores.Count == 0) return [];
        int lo = Math.Max(terrain.Table.BedrockTop + 1, ores.Min(o => o.Min)), hi = Math.Min(MineTerrain.Height - 3, ores.Max(o => o.Max) + 1);
        var veins = OreVeins.For(terrain);
        var byLevel = Enumerable.Range(lo, hi - lo + 1).Select(y => (Y: y, Yield: veins.Yield(StripAcross(y)))).ToList();
        return ores.Select(o => (o, (IReadOnlyList<LevelYield>)byLevel
                .Select(l => (l.Y, Found: l.Yield.First(r => r.Ore.Id == o.Id)))
                .Where(l => l.Found.Blocks > 0).OrderByDescending(l => l.Found.Blocks).ThenBy(l => l.Y).Take(top)
                .Select(l => new LevelYield(l.Y, l.Found.Blocks, l.Found.Veins)).ToList()))
            .ToList();
    }

    /// <summary>"diamond y10 31 (7 veins), y11 29 (6), y9 28 (6); redstone …": the line the AI's <c>ore_levels</c> tool answers.</summary>
    public static string Text(MineTerrain terrain, int top = 3, string? only = null)
    {
        var best = Best(terrain, top, only);
        if (best.Count == 0) return only == null ? "no ores on this plot" : $"no ore '{only}' in {terrain.Version}";
        return "best feet levels for a 1x2 strip mine across this plot (ore found by following the veins it touches): " + string.Join("; ", best.Select(b =>
            b.Ore.Id + " " + (b.Best.Count == 0 ? "none" : string.Join(", ", b.Best.Select((l, i) =>
                string.Create(CultureInfo.InvariantCulture, $"y{l.Y} {l.Blocks}{(i == 0 ? $" ({l.Veins} veins)" : "")}"))))));
    }

    /// <summary>The default strip mine across the plot at feet <paramref name="y"/>: a corridor along x at z 31, branches both sides.</summary>
    private static IEnumerable<BlockPos> StripAcross(int y) =>
        MineTemplates.Strip(StripSpec.Default with { Y = y, Z = DesignerCanvas.Size / 2 - 1, Length = DesignerCanvas.Size, BranchLength = DesignerCanvas.Size / 2 })
            .Cells.Where(c => c.Id == 0 && c.Z is >= 0 and < DesignerCanvas.Size).Select(c => new BlockPos(c.X, c.Y, c.Z));
}
