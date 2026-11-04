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
        var issues = new List<BlueprintIssue>();
        void Err(int line, string code, string msg) => issues.Add(new BlueprintIssue(IssueLevel.Error, code, msg, line));
        var lines = (text ?? "").Replace("\r", "").Split('\n');
        int i = 0;
        bool sized = false, sizeTried = false;
        int sizeLine = 0;
        while (i < lines.Length)
        {
            int ln = i + 1;
            var raw = StripComment(lines[i]).Trim();
            i++;
            if (raw.Length == 0 || raw.StartsWith("```", StringComparison.Ordinal)) continue;
            if (!sized && (SizeHeadRx().IsMatch(raw) || SizeRx().IsMatch(raw.Replace(" ", ""))))
            {
                if (SizeOf(raw) is not { } sz) { Err(ln, "size", "size must be WxHxD with numbers, e.g. size 5x4x5"); sizeTried = true; continue; }
                (bp.W, bp.H, bp.D) = sz;
                if (bp.W is < 1 or > MaxSide || bp.D is < 1 or > MaxSide || bp.H is < 1 or > MaxMineHeight)
                    Err(ln, "size", $"size {bp.W}x{bp.H}x{bp.D} out of range (1..{MaxSide} wide/deep, 1..{MaxHeight} high)");
                sized = true;
                sizeLine = ln;
                continue;
            }
            var sp = raw.IndexOf(' ');
            var head = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant().TrimEnd(':');
            var rest = sp < 0 ? "" : raw[(sp + 1)..].Trim();
            switch (head)
            {
                case "blueprint" or "name":
                    bp.Name = rest.Trim('"', '\'', ' ').Length > 0 ? Truncate(rest.Trim('"', '\'', ' '), 48) : bp.Name;
                    break;
                case "size":
                    {
                        var m = SizeRx().Match(rest.Replace(" ", ""));
                        if (!m.Success) { Err(ln, "size", "size must be WxHxD, e.g. 5x4x5"); break; }
                        bp.W = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        bp.H = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        bp.D = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                        if (bp.W is < 1 or > MaxSide || bp.D is < 1 or > MaxSide || bp.H is < 1 or > MaxMineHeight)
                            Err(ln, "size", $"size {bp.W}x{bp.H}x{bp.D} out of range (1..{MaxSide} wide/deep, 1..{MaxHeight} high)");
                        sized = true;
                        sizeLine = ln;
                        break;
                    }
                case "origin":
                    if (P3(rest) is { } o) bp.Origin = new BlockPos(o.X, o.Y, o.Z);
                    else Err(ln, "origin", "origin must be x,y,z");
                    break;
                case "ground":
                    if (int.TryParse(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) && g >= 0 && g < MaxHeight)
                        bp.Ground = g;
                    else Err(ln, "ground", $"ground must be the number of layers below the surface (0..{MaxHeight - 1})");
                    break;
                case "facing" or "front":
                    if (Dirs.Parse(rest) is { } f) bp.Facing = f;
                    else Err(ln, "facing", "facing must be north, east, south or west");
                    break;
                case "light" or "lit":
                    bp.Light = true;
                    break;
                case "version":
                    if (DesignVersions.Normalize(rest) == null) Err(ln, "version", $"version must be {string.Join(" or ", DesignVersions.All)}");
                    break;
                case "kind":
                    if (rest.Trim().ToLowerInvariant() is var kind && kind is "mine" or "build") bp.Kind = kind;
                    else Err(ln, "kind", "kind must be build or mine");
                    break;
                case "anchor":
                    {
                        // anchor absolute X,Y,Z (or anchor absolute Y): a mine's layer 0 at world y Y (X,Z: its place on the studio plot).
                        var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        bool abs = t.Length == 2 && t[0].Equals("absolute", StringComparison.OrdinalIgnoreCase);
                        if (abs && P3(t[1]) is { } at)
                        {
                            bp.AbsoluteY = at.Y;
                            bp.PlotAt = (at.X, at.Z);
                        }
                        else if (abs && int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ay)) bp.AbsoluteY = ay;
                        else if (t.Length >= 1 && t[0].ToLowerInvariant() is "surface" or "ground") bp.AbsoluteY = null;
                        else Err(ln, "anchor", "anchor must be 'absolute X,Y,Z' (a mine at world y Y) or 'surface'");
                        if (bp.AbsoluteY is < 1) Err(ln, "anchor", $"anchor absolute y {bp.AbsoluteY} must be 1 or more (y 0 is bedrock)");
                        break;
                    }
                case "entry":
                    if (P3(rest) is { } en) bp.Entry = en;
                    else Err(ln, "entry", "entry must be x,y,z (the mine's top, in the design's own coordinates)");
                    break;
                case "seed":
                    bp.Seed = rest.Trim().Length is > 0 and <= 32 ? rest.Trim() : null;
                    break;
                case "preferred_for" or "preferred":
                    if (DesignVersions.Normalize(rest) is { } pv) bp.PreferredFor = pv;
                    else Err(ln, "preferred_for", $"preferred_for must be {string.Join(" or ", DesignVersions.All)}");
                    break;
                case "palette":
                    foreach (var tok in rest.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)) ParsePalette(bp, tok, ln, issues);
                    break;
                case "layer" or "layers":
                    {
                        var m = LayerRx().Match(rest);
                        if (!m.Success) { Err(ln, "layer", "layer needs a number (layer 0) or a range (layer 1-3)"); break; }
                        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : a;
                        var rows = new List<string>();
                        while (i < lines.Length)
                        {
                            var r = StripComment(lines[i]).Trim();
                            if (r.Length == 0) { i++; if (rows.Count > 0) break; continue; }
                            if (IsStatement(r)) break;
                            rows.Add(r);
                            i++;
                        }
                        for (int y = Math.Min(a, b); y <= Math.Max(a, b); y++)
                        {
                            if (bp.Layers.ContainsKey(y)) Err(ln, "layer", $"layer {y} written twice");
                            bp.Layers[y] = (rows, ln);
                            bp.Order.Add(("layer", y));
                        }
                        break;
                    }
                default:
                    if (ParseOp(head, rest, ln, issues) is { } op)
                    {
                        bp.Ops.Add(op);
                        bp.Order.Add(("op", bp.Ops.Count - 1));
                    }
                    break;
            }
        }
        if (!sized && !sizeTried)
        {
            // No size line: infer it from the drawn layers (widest row, row count, highest layer) when there are any.
            if (bp.Layers.Count > 0 && bp.Layers.Values.Any(l => l.Rows.Count > 0))
            {
                bp.W = bp.Layers.Values.SelectMany(l => l.Rows).Max(r => r.Length);
                bp.D = bp.Layers.Values.Max(l => l.Rows.Count);
                bp.H = bp.Layers.Keys.Max() + 1;
                issues.Add(new BlueprintIssue(IssueLevel.Warning, "size-inferred", $"no size line: inferred {bp.W}x{bp.H}x{bp.D} from the layers"));
                if (bp.W > MaxSide || bp.D > MaxSide || bp.H > HeightLimit(bp))
                    Err(0, "size", $"size {bp.W}x{bp.H}x{bp.D} out of range (1..{MaxSide} wide/deep, 1..{HeightLimit(bp)} high)");
            }
            else Err(0, "size", "missing 'size WxHxD'");
        }
        if (bp.Ground > 0 && bp.H > 0 && bp.Ground > bp.H) Err(0, "ground", $"ground {bp.Ground} is deeper than the design (size is {bp.H} high)");
        // Only a mine reaches past 32 high (the kind line may come after the size line).
        if (sized && !bp.Mine && bp.H > MaxHeight && bp.W <= MaxSide && bp.D <= MaxSide)
            Err(sizeLine, "size", $"size {bp.W}x{bp.H}x{bp.D} out of range (1..{MaxSide} wide/deep, 1..{MaxHeight} high)");
        // Settle the drawn front now (doors drawn on the facing side = not turned) so the footprint is right before normalizing.
        if (bp.Facing != Dir.South && bp.W > 0 && bp.H > 0 && bp.D > 0 && bp.W <= MaxSide && bp.D <= MaxSide && bp.H <= HeightLimit(bp))
            BlueprintNormalizer.Normalize(bp);
        return (bp, issues);
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
        if (full)
        {
            if (!DesignPalette.Current.TryResolve(arg == null ? name : name + ":" + arg, out var st, out var bad)) return (null, bad);
            int m = BuildBlocks.KindOf(st.Id) == BuildKind.Door ? st.Meta & 7 : st.Meta; // the palette names the lower half
            return (new PaletteEntry(sym, st.Id, m, null, source), null);
        }
        int id, meta = 0;
        if (Items.ByNameWithDamage(name) is { } it && (it.Item.Id < 256 || it.Item.Id is Items.WoodenDoor or Items.IronDoor))
        {
            id = it.Item.Id switch { Items.WoodenDoor => Blocks.WoodenDoor, Items.IronDoor => Blocks.IronDoor, var x => x };
            meta = Math.Max(0, it.Damage);
        }
        else if (Blocks.ByName(name) is { } b) id = b.Id;
        else return (null, $"unknown {MineClaw.Data.Versioning.GameData.Current.VersionId} block '{name}'");
        // "cobblestone_slab"-style names resolve through Items with their damage; plain "stairs" means wooden stairs.
        if (BuildBlocks.Refusal(id) is { } refusal) return (null, refusal);
        Dir? facing = null;
        if (arg != null)
        {
            var a = arg.Trim().ToLowerInvariant();
            if (Dirs.Parse(a) is { } d)
            {
                if (!BuildBlocks.Directional(id)) return (null, $"{Blocks.Get(id).Name} has no facing");
                facing = d;
            }
            else if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) meta = m;
            else if (id == Blocks.Wool && Array.FindIndex(Items.WoolColors, c => c.Replace(" ", "").Equals(a.Replace("_", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) is >= 0 and var wc) meta = wc;
            else if (id == Blocks.Log && a is "oak" or "spruce" or "birch") meta = a switch { "spruce" => 1, "birch" => 2, _ => 0 };
            else if (id == Blocks.Slab && Array.IndexOf(SlabTypes, a.Replace("_", "").Replace("cobblestone", "cobble").Replace("wooden", "wood").Replace("planks", "wood")) is >= 0 and var st) meta = st;
            else if (id == Blocks.StoneBricks && a is "mossy" or "cracked" or "plain") meta = a switch { "mossy" => 1, "cracked" => 2, _ => 0 };
            else return (null, $"'{arg}' isn't a facing, variant or meta for {Blocks.Get(id).Name}");
        }
        if (BuildBlocks.KindOf(id) == BuildKind.Door) meta &= 7; // the palette names the lower half; the upper is derived
        if (BuildBlocks.MetaProblem(id, meta) is { } mp) return (null, mp);
        return (new PaletteEntry(sym, id, meta, facing, source), null);
    }

    private static BlueprintOp? ParseOp(string head, string rest, int ln, List<BlueprintIssue> issues)
    {
        void Err(string msg) => issues.Add(new(IssueLevel.Error, "op", $"{head}: {msg}", ln));
        var t = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        char Sym(int k) => t.Length > k && t[k].Length == 1 ? t[k][0] : '\0';
        switch (head)
        {
            case "box" or "fill" or "hollow" or "walls" or "wall" or "window":
                {
                    if (t.Length < 3 || Sym(0) == '\0' || P3(t[1]) is not { } a || P3(t[2]) is not { } b) { Err($"use '{head} X x1,y1,z1 x2,y2,z2'"); return null; }
                    return new BlueprintOp(head == "fill" ? "box" : head, Sym(0), a, b, null, "", ln);
                }
            case "clear" or "air":
                {
                    if (t.Length < 2 || P3(t[0]) is not { } a || P3(t[1]) is not { } b) { Err("use 'clear x1,y1,z1 x2,y2,z2'"); return null; }
                    return new BlueprintOp("clear", '.', a, b, null, "", ln);
                }
            case "floor":
                {
                    if (t.Length >= 3 && Sym(0) != '\0' && P3(t[1]) is { } a3 && P3(t[2]) is { } b3)
                    {
                        if (a3.Y != b3.Y) { Err("a floor is flat (same y)"); return null; }
                        return new BlueprintOp("box", Sym(0), a3, b3, null, "", ln);
                    }
                    if (t.Length >= 4 && Sym(0) != '\0' && P2(t[1]) is { } a && P2(t[2]) is { } b && int.TryParse(t[3], out var y))
                        return new BlueprintOp("box", Sym(0), (a.X, y, a.Z), (b.X, y, b.Z), null, "", ln);
                    Err("use 'floor X x1,y,z1 x2,y,z2' or 'floor X x1,z1 x2,z2 y'");
                    return null;
                }
            case "pillar":
                {
                    if (t.Length < 3 || Sym(0) == '\0' || P3(t[1]) is not { } a || !int.TryParse(t[2], out var h) || h < 1) { Err("use 'pillar X x,y,z height'"); return null; }
                    return new BlueprintOp("box", Sym(0), a, (a.X, a.Y + h - 1, a.Z), null, "", ln);
                }
            case "pyramid":
                {
                    // pyramid X x,y,z base [core:Y]: the stepped pyramid whose base layer's min corner is x,y,z (side `base`, one row
                    // in per layer, (base+1)/2 layers); the optional core: fills everything deeper than two rows in with Y.
                    if (t.Length < 3 || Sym(0) == '\0' || P3(t[1]) is not { } a || !int.TryParse(t[2], out var b) || b < 1)
                    { Err("use 'pyramid X x,y,z base [core:Y]' (base = side length of the bottom layer)"); return null; }
                    string core = "";
                    if (t.Length > 3)
                    {
                        var c = t[3].Split(':');
                        if (c.Length != 2 || !c[0].Equals("core", StringComparison.OrdinalIgnoreCase) || c[1].Length != 1) { Err($"'{t[3]}': use core:Y with one palette symbol"); return null; }
                        core = c[1];
                    }
                    return new BlueprintOp("pyramid", Sym(0), a, (a.X + b - 1, a.Y + (b + 1) / 2 - 1, a.Z + b - 1), null, core, ln);
                }
            case "door":
                {
                    if (t.Length < 2 || Sym(0) == '\0' || P3(t[1]) is not { } a) { Err("use 'door D x,y,z [north|east|south|west]'"); return null; }
                    Dir? f = t.Length > 2 ? Dirs.Parse(t[2]) : null;
                    if (t.Length > 2 && f == null) { Err($"'{t[2]}' isn't a direction"); return null; }
                    return new BlueprintOp("door", Sym(0), a, (a.X, a.Y + 1, a.Z), f, "", ln);
                }
            case "stairs":
                {
                    if (t.Length < 3 || Sym(0) == '\0' || P3(t[1]) is not { } a || Dirs.Parse(t[2]) is not { } f) { Err("use 'stairs S x,y,z north|east|south|west' (the way up)"); return null; }
                    var b = t.Length > 3 && P3(t[3]) is { } bb ? bb : a;
                    return new BlueprintOp("stairs", Sym(0), a, b, f, "", ln);
                }
            case "roof":
                {
                    // roof X flat|gable|slope:dir x1,y,z1 x2,z2 [ridge]
                    if (t.Length < 4 || Sym(0) == '\0' || P3(t[2]) is not { } a || P2(t[3]) is not { } b) { Err("use 'roof X flat|gable|slope:north x1,y,z1 x2,z2 [ridge-symbol]'"); return null; }
                    var style = t[1].ToLowerInvariant();
                    Dir? f = null;
                    if (style.StartsWith("slope", StringComparison.Ordinal))
                    {
                        f = Dirs.Parse(style.Split(':').ElementAtOrDefault(1));
                        if (f == null) { Err("slope needs a direction: slope:north (rises toward north)"); return null; }
                        style = "slope";
                    }
                    else if (style is not ("flat" or "gable")) { Err($"roof style '{style}' (flat, gable, slope:dir)"); return null; }
                    return new BlueprintOp("roof", Sym(0), a, (b.X, a.Y, b.Z), f, style + (t.Length > 4 && t[4].Length == 1 ? "|" + t[4] : ""), ln);
                }
        }
        issues.Add(new(IssueLevel.Error, "syntax", $"unknown statement '{head}'", ln));
        return null;
    }
}
