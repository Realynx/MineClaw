using MineClaw.Agent.Memory;
using MineClaw.Data;
using MineClaw.World;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Perception;

/// <summary>
/// The generator's taught layout (the lava and water that fed each cell when it was taught, saved as "gen layout: lava/water"
/// key blocks next to its dig keys) compared with the world when the yield drops: the concrete cause ("the lava at 4,64,7 is
/// gone: now obsidian") instead of a guess (live 10-27 21:16-21:53: four "isn't regenerating cobble for me" retractions of
/// cells that were right, while the gen itself had changed).
/// </summary>
public static class GenLayout
{
    public const string LavaLabel = "gen layout: lava", WaterLabel = "gen layout: water";

    public static string Label(bool lava) => lava ? LavaLabel : WaterLabel;

    public static bool IsLayoutLabel(string? label) => label is LavaLabel or WaterLabel;

    private static readonly WPos[] FlowIn = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1), new(0, 1, 0)];

    /// <summary>Lava/water touching <paramref name="cell"/> from a side or above (what feeds it).</summary>
    public static IReadOnlyList<(WPos Pos, bool Lava)> FluidsAround(IBlockAccess w, WPos cell)
    {
        var list = new List<(WPos, bool)>();
        foreach (var o in FlowIn)
        {
            var q = cell + o;
            int id = w.GetBlock(q.X, q.Y, q.Z);
            if (Blocks.IsLava(id)) list.Add((q, true));
            else if (Blocks.IsWater(id)) list.Add((q, false));
        }
        return list;
    }

    private static string Name(int id) => id == 0 ? "air" : Blocks.Exists(id) ? Blocks.Get(id).Name : "block " + id;

    private static string Fmt(WPos p) => $"{p.X},{p.Y},{p.Z}";

    /// <summary>
    /// Why <paramref name="cell"/> stopped yielding, from the taught layout vs the world: missing/replaced lava or water, the cell
    /// itself turned to something else, or (no layout saved) what the geometry lacks now. Null = everything is as taught (the
    /// cell itself is likely the wrong one).
    /// </summary>
    public static string? Diagnose(WorldState w, MemoryStore? memory, int dim, WPos cell)
    {
        var causes = new List<string>();
        if (CellCause(cell, w.GetBlock(cell.X, cell.Y, cell.Z)) is { } changed) causes.Add(changed);

        var layout = TaughtLayout(memory, dim, cell);
        if (layout.Count > 0)
        {
            foreach (var k in layout)
                if (FluidGone(w, k) is { } gone) causes.Add(gone);
            // Something new in the way: fluid of the other kind where the layout had none is fine; a solid next to the cell where
            // the layout had fluid is already reported above.
        }
        else if (BlockDescriber.GenCellRejection(w, cell) is { } why && !why.StartsWith("it's", StringComparison.Ordinal))
            causes.Add($"{Fmt(cell)}: {why} (no taught layout to compare)");
        return causes.Count == 0 ? null : string.Join("; ", causes);
    }

    /// <summary>The taught lava/water key blocks right next to the cell (none when memory is busy or no gen is saved).</summary>
    private static List<KeyBlock> TaughtLayout(MemoryStore? memory, int dim, WPos cell)
    {
        Structure? gen = null;
        try { gen = memory == null ? null : DigCells.GeneratorNear(memory, dim, cell); }
        catch (Exception) { /* memory busy: geometry only */ }
        return gen?.KeyBlocks.Where(k => IsLayoutLabel(k.Label)
            && Math.Abs(k.X - cell.X) + Math.Abs(k.Y - cell.Y) + Math.Abs(k.Z - cell.Z) == 1).ToList() ?? [];
    }

    /// <summary>The cell turned to obsidian or to something that isn't output, air or fluid; else null.</summary>
    private static string? CellCause(WPos cell, int here)
    {
        if (here == Blocks.Obsidian) return $"the cell {Fmt(cell)} is obsidian now (water reached the lava source)";
        if (here != 0 && !(here is Blocks.Cobblestone or Blocks.Stone) && !Blocks.IsLava(here) && !Blocks.IsWater(here))
            return $"the cell {Fmt(cell)} is {Name(here)} now";
        return null;
    }

    /// <summary>A taught lava/water block that isn't that fluid any more.</summary>
    private static string? FluidGone(WorldState w, KeyBlock k)
    {
        bool lava = k.Label == LavaLabel;
        int id = w.GetBlock(k.X, k.Y, k.Z);
        bool ok = lava ? Blocks.IsLava(id) : Blocks.IsWater(id);
        return ok ? null : $"the {(lava ? "lava" : "water")} at {k.X},{k.Y},{k.Z} is gone (now {Name(id)}; taught layout had it)";
    }
}
