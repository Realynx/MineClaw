using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>A mine design's own lines: where it sat on the studio plot (its min corner, absolute y), its entry, seed and preference.</summary>
/// <param name="Min">The design's min corner: x,z on the studio plot, y = absolute world y of its layer 0.</param>
/// <param name="Entry">The top of the mine (the shaft's entry), in the same coordinates as <paramref name="Min"/>; null = none marked.</param>
public sealed record MineHeader(BlockPos Min, BlockPos? Entry, string? Seed, string? PreferredFor);

/// <summary>
/// Mine designs as DSL (docs/web/api.md "Mine designs"): <c>kind mine</c>, <c>anchor absolute X,Y,Z</c> (layer 0 at world y Y; X,Z
/// its place on the studio plot), <c>entry x,y,z</c> (local), <c>seed</c>, <c>preferred_for</c>; the placed blocks (torches, ladders,
/// rails…) as layers like any design, and the cells to dig as <c>clear</c> boxes (the dug cells merged greedily into boxes), so a
/// 64×64×100 strip mine is a few hundred short lines, not 400 KB of rows. Drawn, then parsed back and compared cell by cell.
/// </summary>
public static partial class MineDesign
{
    [GeneratedRegex(@"^\s*kind\s+mine\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex KindLine();

    [GeneratedRegex(@"^origin\s+[^\n]*\n", RegexOptions.Multiline)]
    private static partial Regex OriginLine();

    [GeneratedRegex(@"^# captured from the world at[^\n]*\n", RegexOptions.Multiline)]
    private static partial Regex CaptureComment();

    /// <summary>A mine design's DSL (<c>kind mine</c>).</summary>
    public static bool IsMine(string? dsl) => dsl != null && KindLine().IsMatch(dsl);

    /// <summary>The header of a mine DSL (null for anything else).</summary>
    public static MineHeader? HeaderOf(string? dsl)
    {
        if (!IsMine(dsl)) return null;
        var (bp, _) = BlueprintParser.Parse(dsl!);
        if (bp.AbsoluteY is not { } y) return null;
        var min = new BlockPos(bp.PlotAt?.X ?? 0, y, bp.PlotAt?.Z ?? 0);
        BlockPos? entry = bp.Entry is { } e ? new BlockPos(min.X + e.X, min.Y + e.Y, min.Z + e.Z) : null;
        return new MineHeader(min, entry, bp.Seed, bp.PreferredFor);
    }

    /// <summary>The cells as boxes: greedy merging along x, then z, then y (every cell in exactly one box).</summary>
    public static List<(BlockPos Lo, BlockPos Hi)> Boxes(IReadOnlySet<BlockPos> cells)
    {
        var left = new HashSet<BlockPos>(cells);
        var boxes = new List<(BlockPos, BlockPos)>();
        foreach (var p in cells.OrderBy(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.X))
        {
            if (!left.Contains(p)) continue;
            int x1 = p.X, z1 = p.Z, y1 = p.Y;
            while (left.Contains(new BlockPos(x1 + 1, p.Y, p.Z))) x1++;
            bool Row(int z, int y) { for (int x = p.X; x <= x1; x++) if (!left.Contains(new BlockPos(x, y, z))) return false; return true; }
            while (Row(z1 + 1, p.Y)) z1++;
            bool Slab(int y) { for (int z = p.Z; z <= z1; z++) if (!Row(z, y)) return false; return true; }
            while (Slab(y1 + 1)) y1++;
            for (int y = p.Y; y <= y1; y++) for (int z = p.Z; z <= z1; z++) for (int x = p.X; x <= x1; x++) left.Remove(new BlockPos(x, y, z));
            boxes.Add((p, new BlockPos(x1, y1, z1)));
        }
        return boxes;
    }

    /// <summary>
    /// The DSL of a mine: <paramref name="snap"/>'s blocks and its air (the cells to dig) in plot coordinates with absolute y.
    /// Null + why when it can't be drawn (over 64 wide or 128 high, below y 1, nothing in it).
    /// </summary>
    public static (string? Dsl, BlockPos Min, int Mismatched, string? Why) Draw(string name, DesignSnapshot snap, BlockPos? entry, string? seed,
        string? preferredFor, string? version, int height = MineTerrain.Height)
    {
        using var scope = DesignVersions.Use(version);
        var cells = snap.Blocks.Keys.Concat(snap.Air).ToList();
        if (cells.Count == 0) return (null, default, 0, "nothing to dig or place");
        var min = new BlockPos(cells.Min(p => p.X), cells.Min(p => p.Y), cells.Min(p => p.Z));
        var max = new BlockPos(cells.Max(p => p.X), cells.Max(p => p.Y), cells.Max(p => p.Z));
        int wx = max.X - min.X + 1, hy = max.Y - min.Y + 1, dz = max.Z - min.Z + 1;
        if (wx > BlueprintParser.MaxSide || dz > BlueprintParser.MaxSide || hy > BlueprintParser.MaxMineHeight)
            return (null, min, 0, string.Create(CultureInfo.InvariantCulture,
                $"it would be {wx}×{hy}×{dz} (a mine holds at most {BlueprintParser.MaxSide} a side and {BlueprintParser.MaxMineHeight} high)"));
        if (min.Y < 1) return (null, min, 0, "a mine can't dig below y 1 (y 0 is bedrock)");
        var r = new CaptureResult { Name = name, Min = min, Max = max, Version = DesignVersions.Normalize(version) };
        foreach (var (p, s) in snap.Blocks) r.Cells[p] = s;
        var dsl = StructureCapture.Dsl(r);
        dsl = OriginLine().Replace(dsl, "");
        dsl = CaptureComment().Replace(dsl, $"# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks\n", 1);
        var head = new StringBuilder();
        head.Append("kind mine\n");
        head.Append(CultureInfo.InvariantCulture, $"anchor absolute {min.X},{min.Y},{min.Z}\n");
        if (entry is { } e) head.Append(CultureInfo.InvariantCulture, $"entry {e.X - min.X},{e.Y - min.Y},{e.Z - min.Z}\n");
        if (!string.IsNullOrWhiteSpace(seed)) head.Append("seed ").Append(MineTerrain.NormalizeSeed(seed).Replace('\n', ' ').Replace('#', ' ')).Append('\n');
        if (DesignVersions.Normalize(preferredFor) is { } pf) head.Append("preferred_for ").Append(pf).Append('\n');
        int at = dsl.IndexOf("facing ", StringComparison.Ordinal);
        dsl = at >= 0 ? dsl.Insert(at, head.ToString()) : dsl + head;
        var sb = new StringBuilder(dsl);
        if (!sb.ToString().EndsWith('\n')) sb.Append('\n');
        var air = snap.Air.Where(p => !snap.Blocks.ContainsKey(p)).ToHashSet();
        foreach (var (lo, hi) in Boxes(air))
            sb.Append(CultureInfo.InvariantCulture, $"clear {lo.X - min.X},{lo.Y - min.Y},{lo.Z - min.Z} {hi.X - min.X},{hi.Y - min.Y},{hi.Z - min.Z}\n");
        dsl = sb.ToString();
        var (bp, issues) = BlueprintParser.Parse(dsl);
        if (issues.FirstOrDefault(i => i.Level == IssueLevel.Error) is { } perr) return (null, min, 0, "it doesn't draw as a design: " + perr);
        var (plan, more) = BlueprintNormalizer.Normalize(bp, min, Math.Max(height, min.Y + hy));
        if (plan == null || more.Any(i => i.Level == IssueLevel.Error))
            return (null, min, 0, "it doesn't normalize: " + string.Join("; ", more.Where(i => i.Level == IssueLevel.Error).Take(2)));
        int bad = snap.Blocks.Count(kv => !plan.Blocks.TryGetValue(kv.Key, out var b) || b.Id != kv.Value.Id || b.Meta != kv.Value.Meta)
                  + air.Count(p => !plan.Air.Contains(p));
        return (dsl, min, bad, null);
    }

    /// <summary>"mine 12×61×40 (y 11..71): 2 210 cells to dig, 9 torches".</summary>
    public static string Summary(DesignSnapshot snap)
    {
        var cells = snap.Blocks.Keys.Concat(snap.Air).ToList();
        if (cells.Count == 0) return "an empty mine";
        int x0 = cells.Min(p => p.X), x1 = cells.Max(p => p.X), y0 = cells.Min(p => p.Y), y1 = cells.Max(p => p.Y), z0 = cells.Min(p => p.Z), z1 = cells.Max(p => p.Z);
        var pal = DesignPalette.Current;
        var placed = snap.Blocks.Values.Where(s => !DesignFacing.SecondHalf(s.Id, s.Meta)).GroupBy(s => pal.Name(s.Id, s.Meta).ToLowerInvariant())
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}").Take(4).ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"mine {x1 - x0 + 1}×{z1 - z0 + 1}×{y1 - y0 + 1} (y {y0}..{y1}): {snap.Air.Count} cells to dig") + (placed.Count > 0 ? ", " + string.Join(", ", placed) : "");
    }
}
