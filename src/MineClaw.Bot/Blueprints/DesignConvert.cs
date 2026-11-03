using System.Globalization;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>How a state carries over to another version.</summary>
public enum ConvertKind
{
    /// <summary>The same block there (the same id and meta, or its documented equivalent: b1.8.1's wooden slab = 1.12.2's oak slab).</summary>
    Same,
    /// <summary>The nearest block there, named in the report (spruce planks → planks, stained glass → glass).</summary>
    Substitute,
    /// <summary>Nothing like it there (concrete in b1.8.1): left out of a conversion, refused by a build.</summary>
    Missing,
}

/// <summary>What converting a block set did: counts per kind, the substitutions and the missing blocks by name.</summary>
public sealed class ConvertReport(string from, string to)
{
    public string From { get; } = from;
    public string To { get; } = to;
    public int Same { get; set; }
    /// <summary>"Spruce Planks → Wooden Planks" → cells.</summary>
    public SortedDictionary<string, int> Substituted { get; } = new(StringComparer.Ordinal);
    /// <summary>"White Concrete" → cells.</summary>
    public SortedDictionary<string, int> Missing { get; } = new(StringComparer.Ordinal);
    public bool Compatible => Missing.Count == 0;
    public bool Exact => Missing.Count == 0 && Substituted.Count == 0;

    /// <summary>"1.12.2 → b1.8.1: 120 the same; 20 Spruce Planks → Wooden Planks; not in b1.8.1: 12 White Concrete".</summary>
    public string Text(int max = 6)
    {
        var parts = new List<string> { $"{From} → {To}: {Same.ToString(CultureInfo.InvariantCulture)} the same" };
        if (Substituted.Count > 0)
            parts.Add(string.Join(", ", Substituted.OrderByDescending(kv => kv.Value).Take(max).Select(kv => $"{kv.Value} {kv.Key}")) + (Substituted.Count > max ? ", …" : ""));
        if (Missing.Count > 0)
            parts.Add($"not in {To}: " + string.Join(", ", Missing.OrderByDescending(kv => kv.Value).Take(max).Select(kv => $"{kv.Value} {kv.Key}")) + (Missing.Count > max ? ", …" : ""));
        return string.Join("; ", parts);
    }

    /// <summary>The missing blocks as "12 White Concrete, 3 Spruce Fence" (empty when none).</summary>
    public string MissingText(int max = 8) =>
        string.Join(", ", Missing.OrderByDescending(kv => kv.Value).Take(max).Select(kv => $"{kv.Value} {kv.Key}")) + (Missing.Count > max ? ", …" : "");
}

/// <summary>
/// The mapping between b1.8.1 and 1.12.2 design states (docs/web/api.md "Design Studio", "Version compatibility"). 1.12.2's ids
/// are a superset of b1.8.1's with the same numbers and metas, so a state carries over unchanged when the target's palette has it
/// (<see cref="DesignPalette.Valid"/>), except id 95 (b1.8.1 locked chest, 1.12.2 stained glass: never carried over as is) and
/// b1.8.1's wooden slab 44:2 = 1.12.2's oak slab 126:0. Going back to b1.8.1, newer blocks map to their nearest b1.8.1 block where
/// one is close (the table in <see cref="Map"/>: woods → oak, stone kinds → their plain block, stained glass/panes → glass, top
/// halves → bottom halves, wooden buttons → stone, trapped chests → chests, grass → dirt); everything else is missing.
/// </summary>
public static class DesignConvert
{
    public static (int Id, int Meta, ConvertKind Kind) Map(string from, string to, int id, int meta)
    {
        from = DesignVersions.Normalize(from) ?? from;
        to = DesignVersions.Normalize(to) ?? to;
        if (id <= 0 || from == to) return (id, meta, ConvertKind.Same);
        var dst = DesignPalette.For(to);
        if (from == DesignVersions.Beta)
        {
            if (id == 44 && (meta & 7) == 2) return (126, 0, ConvertKind.Same);
            if (id == 95) return (id, meta, ConvertKind.Missing); // b1.8.1's locked chest is never 1.12.2's stained glass
            return dst.Valid(id, meta) ? (id, meta, ConvertKind.Same) : (id, meta, ConvertKind.Missing);
        }
        // 1.12.2 → b1.8.1
        if (id == 95) return (20, 0, ConvertKind.Substitute);
        if (id == 126) return (44, 2, (meta & 7) == 0 && (meta & 8) == 0 ? ConvertKind.Same : ConvertKind.Substitute);
        if (id <= 109 && dst.Valid(id, meta)) return (id, meta, ConvertKind.Same);
        (int Id, int Meta)? sub = id switch
        {
            5 => (5, 0),
            17 => (17, (meta & 3) == 3 ? 0 : meta & 3),
            162 => (17, 0),
            134 or 135 or 136 or 163 or 164 => (53, meta & 3),
            53 or 67 or 108 or 109 => (id, meta & 3),
            44 when dst.Valid(44, meta & 7) => (44, meta & 7),
            188 or 189 or 190 or 191 or 192 => (85, 0),
            183 or 184 or 185 or 186 or 187 => (107, meta & 7),
            193 or 194 or 195 or 196 or 197 => (64, meta),
            24 => (24, 0),
            12 => (12, 0),
            98 => (98, 0),
            2 or 3 or 110 or 208 => (3, 0),
            38 => (38, 0),
            6 => (6, 0),
            160 => (102, 0),
            96 => (96, meta & 7),
            167 => (96, meta & 7),
            143 or 77 when (meta & 7) is >= 1 and <= 4 => (77, meta),
            146 => (54, meta),
            _ => null,
        };
        if (sub is { } s && dst.Valid(s.Id, s.Meta)) return (s.Id, s.Meta, ConvertKind.Substitute);
        return (id, meta, ConvertKind.Missing);
    }

    /// <summary>
    /// A block set converted from one version to another: the cells that carry over (missing ones left out) and the report.
    /// Names come from each version's palette.
    /// </summary>
    public static (Dictionary<BlockPos, (int Id, int Meta)> Cells, ConvertReport Report) Convert(IEnumerable<KeyValuePair<BlockPos, (int Id, int Meta)>> cells,
        string from, string to)
    {
        var report = new ConvertReport(DesignVersions.Normalize(from) ?? from, DesignVersions.Normalize(to) ?? to);
        var src = DesignPalette.For(from);
        var dst = DesignPalette.For(to);
        var outCells = new Dictionary<BlockPos, (int Id, int Meta)>();
        foreach (var (p, (id, meta)) in cells)
        {
            var (nid, nmeta, kind) = Map(from, to, id, meta);
            bool second = DesignFacing.SecondHalf(id, meta);
            switch (kind)
            {
                case ConvertKind.Same:
                    outCells[p] = (nid, nmeta);
                    if (!second) report.Same++;
                    break;
                case ConvertKind.Substitute:
                    outCells[p] = (nid, nmeta);
                    if (!second) Count(report.Substituted, $"{src.Name(id, meta)} → {dst.Name(nid, nmeta)}");
                    break;
                default:
                    if (!second) Count(report.Missing, src.Name(id, meta));
                    break;
            }
        }
        return (outCells, report);
    }

    private static void Count(SortedDictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
}
