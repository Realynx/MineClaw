using System.Text.RegularExpressions;
using MineClaw.Agent.Memory;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The stand spot a player taught for a generator dig cell. Live 10-27 (B12/B21): the demo was learned as "stood at 7,63,8
/// facing cobblestone; punched cobblestone at 6,64,8", but harvest_generator walked to its own "safe" spot next to the cell
/// (19 "goto: stuck, re-planning" against the water push), and a script mined the floor instead. The demo's stand is the
/// one place the cell is dug from.
/// </summary>
public static partial class GeneratorStand
{
    [GeneratedRegex(@"\bstood at ([+-]?\d+),\s*([+-]?\d+),\s*([+-]?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StoodRx();

    [GeneratedRegex(@"\b(?:punched|broke|mined|dug|digs?)\b[^;]*?\bat ([+-]?\d+),\s*([+-]?\d+),\s*([+-]?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigRx();

    [GeneratedRegex(@"relative to (-?\d+),\s*(-?\d+),\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorRx();

    /// <summary>
    /// From a demo's steps: the last "stood at" before a dig step at <paramref name="cell"/>. Coordinates written with a sign
    /// ("+0,+1,-1") are relative to the anchor named in the text ("relative to 4,63,6") or else <paramref name="origin"/>
    /// (the structure's min corner, which <c>DemoSummary</c> uses); without either they can't be placed and are skipped.
    /// </summary>
    public static BlockPos? FromSteps(IEnumerable<string> steps, BlockPos cell, BlockPos? origin = null)
    {
        // Steps may be one per line or joined with "; " (a single summary line).
        var parts = steps.SelectMany(s => (s ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
        BlockPos? anchor = origin;
        foreach (var p in parts)
            if (AnchorRx().Match(p) is { Success: true } a)
                anchor = new BlockPos(int.Parse(a.Groups[1].Value), int.Parse(a.Groups[2].Value), int.Parse(a.Groups[3].Value));
        BlockPos? stood = null, found = null;
        foreach (var p in parts)
        {
            if (StoodRx().Match(p) is { Success: true } sm) stood = Pos(sm, anchor);
            else if (DigRx().Match(p) is { Success: true } dm && Pos(dm, anchor) == cell && stood is { } st && Near(st, cell))
                found = st; // the latest demo of this cell wins
        }
        return found;
    }

    /// <summary>The taught stand for <paramref name="cell"/> from memory: generator structures' demo steps (nearest first),
    /// else a key block labelled "stand" within reach of the cell.</summary>
    public static BlockPos? Find(MemoryStore memory, BlockPos cell)
    {
        int dim = memory.CurrentDimension;
        var structures = memory.Read(d => d.Structures.Where(s => s.Dimension == dim && (s.Steps.Count > 0 || s.KeyBlocks.Count > 0)).ToList());
        foreach (var s in structures.OrderBy(s => s.Min is { } m ? Math.Abs(m.X - cell.X) + Math.Abs(m.Y - cell.Y) + Math.Abs(m.Z - cell.Z) : int.MaxValue))
        {
            var origin = s.Min is { } mn ? new BlockPos(mn.X, mn.Y, mn.Z) : (BlockPos?)null;
            if (FromSteps(s.Steps, cell, origin) is { } st) return st;
            if (s.KeyBlocks.FirstOrDefault(k => k.Label.Contains("stand", StringComparison.OrdinalIgnoreCase) && Near(new BlockPos(k.X, k.Y, k.Z), cell)) is { } kb)
                return new BlockPos(kb.X, kb.Y, kb.Z);
        }
        return null;
    }

    private static bool Near(BlockPos stand, BlockPos cell) =>
        Math.Max(Math.Abs(stand.X - cell.X), Math.Abs(stand.Z - cell.Z)) <= 3 && Math.Abs(stand.Y - cell.Y) <= 3 && stand != cell;

    private static BlockPos? Pos(Match m, BlockPos? anchor)
    {
        bool relative = m.Groups[1].Value.StartsWith('+') || m.Groups[2].Value.StartsWith('+') || m.Groups[3].Value.StartsWith('+');
        int x = int.Parse(m.Groups[1].Value), y = int.Parse(m.Groups[2].Value), z = int.Parse(m.Groups[3].Value);
        if (!relative) return new BlockPos(x, y, z);
        return anchor is { } a ? new BlockPos(a.X + x, a.Y + y, a.Z + z) : null;
    }
}
