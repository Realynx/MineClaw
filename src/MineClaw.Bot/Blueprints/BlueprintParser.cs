using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// The blueprint DSL (one statement per line, <c>#</c> comments; written by the Opus brain only):
/// <code>
/// blueprint small cobble hut
/// size 5x4x5            # W (x) × H (y) × D (z)
/// origin 10,64,-3       # optional world min corner (else the site decides)
/// ground 3              # optional: layers 0-2 are dug into the ground (a basement); layer 3 stands on the site's surface
/// facing south          # where the front points; drawings put the front on the bottom row (south)
/// light                 # optional: the interior must be lit
/// palette C=cobblestone P=planks G=glass_pane D=door:south S=cobblestone_stairs:north T=torch W=wool:red
/// layer 0               # or "layer 1-2" for repeated rows; D rows of W chars, north row first
/// CCCCC
/// C...C                 # '.' = must be air, '_' = leave as is
/// layer 1-2
/// ...
/// box C 0,0,0 4,0,4     # also: hollow, walls, wall, floor, clear, pillar C x,y,z h, door D x,y,z [dir],
/// window G 1,2,0 3,2,0  # stairs S x,y,z dir, roof C flat|gable|slope:dir x1,y,z1 x2,z2 [ridge-symbol],
///                       # pyramid S 0,0,0 21 [core:D] (stepped, base 21, 11 layers; size 21x11x21)
/// </code>
/// Statements apply in order; later ones win. Block names are b1.8.1 names/aliases (MineClaw.Data); a palette value may add
/// a facing (north/east/south/west), a variant (wool:red, log:birch, slab:cobble) or a raw meta (slab:3).
/// </summary>
public static partial class BlueprintParser
{
    // 64 a side (a base-63 pyramid has 32 layers); a capture stays 32 (StructureCapture); the author is asked for ≤ 24
    public const int MaxSide = 64, MaxHeight = 32;
    /// <summary>A mine design's height (<c>kind mine</c>: bedrock to over the surface; b1.8.1's whole world).</summary>
    public const int MaxMineHeight = 128;

    /// <summary>The height limit of a design of this kind.</summary>
    public static int HeightLimit(Blueprint bp) => bp.Mine ? MaxMineHeight : MaxHeight;

    [GeneratedRegex(@"^(\d+)\s*[x×*]\s*(\d+)\s*[x×*]\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRx();

    /// <summary>A size anywhere on the line: "size 10x18x10", "size: 10 x 18 x 10", "size = 10×18×10", "size WxHxD = 10x18x10" (live
    /// 10-28: designs #6/#7 failed 3× each on the size line), or a bare "10x18x10" line.</summary>
    [GeneratedRegex(@"(?<![\d.])(\d{1,3})\s*[x×*]\s*(\d{1,3})\s*[x×*]\s*(\d{1,3})(?![\d.])", RegexOptions.IgnoreCase)]
    private static partial Regex SizeAnywhereRx();

    [GeneratedRegex(@"^(?:size|dimensions?|dims?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeHeadRx();

    /// <summary>The size on a size line (or a bare WxHxD line), else null.</summary>
    public static (int W, int H, int D)? SizeOf(string line)
    {
        var t = line.Trim();
        bool sizeLine = SizeHeadRx().IsMatch(t) || SizeRx().IsMatch(t.Replace(" ", ""));
        if (!sizeLine) return null;
        // "size WxHxD = 10x18x10": the placeholder letters never match digits, so the first numeric triple is the size.
        var m = SizeAnywhereRx().Match(t);
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) : null;
    }

    [GeneratedRegex(@"^(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)$")]
    private static partial Regex Point3Rx();

    [GeneratedRegex(@"^(-?\d+)\s*,\s*(-?\d+)$")]
    private static partial Regex Point2Rx();

    [GeneratedRegex(@"^(\d+)(?:\s*-\s*(\d+))?$")]
    private static partial Regex LayerRx();

    private static readonly string[] SlabTypes = ["stone", "sandstone", "wood", "cobble", "brick", "stonebrick"];

    /// <summary>Parses DSL text. Never throws: problems come back as issues (errors with line numbers).</summary>
    public static (Blueprint Blueprint, List<BlueprintIssue> Issues) Parse(string text)
    {
        var bp = new Blueprint { Source = text ?? "", Version = DesignVersions.OfDsl(text) };
        // A versioned design (the Design Studio's) resolves its palette in its own version's names and opens the whole design
        // palette of that version; a DSL without a version line is the server's and keeps the buildable-blocks palette.
        using var scope = DesignVersions.Use(bp.Version);
        var run = new ParseRun(bp, (text ?? "").Replace("\r", "").Split('\n'));
        run.ReadAll();
        run.Finish();
        return (bp, run.Issues);
    }

    private static readonly HashSet<string> Statements = new(StringComparer.OrdinalIgnoreCase)
    {
        "blueprint", "name", "size", "origin", "ground", "facing", "front", "light", "lit", "palette", "version", "kind", "anchor", "entry", "seed",
        "preferred_for", "layer", "layers", "box", "fill", "hollow", "walls", "wall",
        "floor", "clear", "air", "pillar", "door", "window", "stairs", "roof", "pyramid",
    };

    private static bool IsStatement(string line)
    {
        int sp = line.IndexOf(' ');
        var head = (sp < 0 ? line : line[..sp]).TrimEnd(':');
        return Statements.Contains(head) && (sp > 0 || head.Equals("light", StringComparison.OrdinalIgnoreCase) || head.Equals("lit", StringComparison.OrdinalIgnoreCase));
    }

    private static string StripComment(string s)
    {
        int h = s.IndexOf('#');
        return h < 0 ? s : s[..h];
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    internal static (int X, int Y, int Z)? P3(string s)
    {
        var m = Point3Rx().Match(s.Trim());
        return m.Success ? (I(m.Groups[1].Value), I(m.Groups[2].Value), I(m.Groups[3].Value)) : null;
    }

    private static (int X, int Z)? P2(string s)
    {
        var m = Point2Rx().Match(s.Trim());
        return m.Success ? (I(m.Groups[1].Value), I(m.Groups[2].Value)) : null;
    }

    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>"C=cobblestone", "D=door:south", "W=wool:red", "S=slab:cobble", "L=log:2".</summary>
    internal static void ParsePalette(Blueprint bp, string tok, int ln, List<BlueprintIssue> issues)
    {
        int eq = tok.IndexOf('=');
        if (eq != 1)
        {
            issues.Add(new(IssueLevel.Error, "palette", $"palette entry '{tok}' must be X=block (one character before '=')", ln));
            return;
        }
        char sym = tok[0];
        if (sym is '.' or '_' or ' ')
        {
            issues.Add(new(IssueLevel.Error, "palette", $"'{sym}' is reserved ('.' air, '_' leave as is)", ln));
            return;
        }
        var val = tok[2..];
        var parts = val.Split(':', 2);
        var (entry, why) = Resolve(sym, parts[0], parts.Length > 1 ? parts[1] : null, val, bp.Version != null);
        if (entry == null) issues.Add(new(IssueLevel.Error, "block", $"{sym}={val}: {why}", ln));
        else bp.Palette[sym] = entry;
    }

    /// <summary>A block name (+ facing/variant/meta) → palette entry, or why not. <paramref name="full"/> = a versioned design: any
    /// block of the version's design palette (<see cref="DesignPalette"/>), else only what <see cref="BuildBlocks"/> builds.</summary>
    public static (PaletteEntry? Entry, string? Why) Resolve(char sym, string name, string? arg, string source, bool full = false)
    {
        if (full) return ResolveDesignBlock(sym, name, arg, source);
        // "cobblestone_slab"-style names resolve through Items with their damage; plain "stairs" means wooden stairs.
        if (!TryBuildableBlock(name, out int id, out int meta)) return (null, $"unknown {MineClaw.Data.Versioning.GameData.Current.VersionId} block '{name}'");
        if (BuildBlocks.Refusal(id) is { } refusal) return (null, refusal);
        Dir? facing = null;
        if (arg != null && ApplyPaletteArg(id, arg, ref meta, ref facing) is { } why) return (null, why);
        if (BuildBlocks.KindOf(id) == BuildKind.Door) meta &= 7; // the palette names the lower half; the upper is derived
        if (BuildBlocks.MetaProblem(id, meta) is { } mp) return (null, mp);
        return (new PaletteEntry(sym, id, meta, facing, source), null);
    }
}
