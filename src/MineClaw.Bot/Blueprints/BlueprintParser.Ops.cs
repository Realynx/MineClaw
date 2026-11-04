using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>The DSL's primitives (box, hollow, walls, floor, pillar, pyramid, door, stairs, roof…): one parser per op head.</summary>
public static partial class BlueprintParser
{
    /// <summary>An op line: its head, its whitespace-split arguments and where its errors go.</summary>
    private readonly record struct OpLine(string Head, string[] T, int Ln, List<BlueprintIssue> Issues)
    {
        public BlueprintOp? Err(string msg)
        {
            Issues.Add(new(IssueLevel.Error, "op", $"{Head}: {msg}", Ln));
            return null;
        }

        /// <summary>Argument k when it is a single character (a palette symbol), else '\0'.</summary>
        public char Sym(int k) => T.Length > k && T[k].Length == 1 ? T[k][0] : '\0';

        public BlueprintOp Op(string kind, char sym, (int X, int Y, int Z) a, (int X, int Y, int Z) b, Dir? facing = null, string arg = "") =>
            new(kind, sym, a, b, facing, arg, Ln);
    }

    private static readonly Dictionary<string, Func<OpLine, BlueprintOp?>> OpParsers = new()
    {
        ["box"] = BoxOp,
        ["fill"] = BoxOp,
        ["hollow"] = BoxOp,
        ["walls"] = BoxOp,
        ["wall"] = BoxOp,
        ["window"] = BoxOp,
        ["clear"] = ClearOp,
        ["air"] = ClearOp,
        ["floor"] = FloorOp,
        ["pillar"] = PillarOp,
        ["pyramid"] = PyramidOp,
        ["door"] = DoorOp,
        ["stairs"] = StairsOp,
        ["roof"] = RoofOp,
    };

    private static BlueprintOp? ParseOp(string head, string rest, int ln, List<BlueprintIssue> issues)
    {
        if (OpParsers.TryGetValue(head, out var parse)) return parse(new OpLine(head, rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries), ln, issues));
        issues.Add(new(IssueLevel.Error, "syntax", $"unknown statement '{head}'", ln));
        return null;
    }

    private static BlueprintOp? BoxOp(OpLine o)
    {
        var t = o.T;
        if (t.Length < 3 || o.Sym(0) == '\0' || P3(t[1]) is not { } a || P3(t[2]) is not { } b) return o.Err($"use '{o.Head} X x1,y1,z1 x2,y2,z2'");
        return o.Op(o.Head == "fill" ? "box" : o.Head, o.Sym(0), a, b);
    }

    private static BlueprintOp? ClearOp(OpLine o)
    {
        var t = o.T;
        if (t.Length < 2 || P3(t[0]) is not { } a || P3(t[1]) is not { } b) return o.Err("use 'clear x1,y1,z1 x2,y2,z2'");
        return o.Op("clear", '.', a, b);
    }

    /// <summary>floor X x1,y,z1 x2,y,z2 (flat) or floor X x1,z1 x2,z2 y.</summary>
    private static BlueprintOp? FloorOp(OpLine o)
    {
        var t = o.T;
        if (t.Length >= 3 && o.Sym(0) != '\0' && P3(t[1]) is { } a3 && P3(t[2]) is { } b3)
            return FlatFloor(o, a3, b3);
        if (t.Length >= 4 && o.Sym(0) != '\0' && P2(t[1]) is { } a && P2(t[2]) is { } b && int.TryParse(t[3], out var y))
            return o.Op("box", o.Sym(0), (a.X, y, a.Z), (b.X, y, b.Z));
        return o.Err("use 'floor X x1,y,z1 x2,y,z2' or 'floor X x1,z1 x2,z2 y'");
    }

    private static BlueprintOp? FlatFloor(OpLine o, (int X, int Y, int Z) a, (int X, int Y, int Z) b) =>
        a.Y != b.Y ? o.Err("a floor is flat (same y)") : o.Op("box", o.Sym(0), a, b);

    private static BlueprintOp? PillarOp(OpLine o)
    {
        var t = o.T;
        if (t.Length < 3 || o.Sym(0) == '\0' || P3(t[1]) is not { } a || !int.TryParse(t[2], out var h) || h < 1) return o.Err("use 'pillar X x,y,z height'");
        return o.Op("box", o.Sym(0), a, (a.X, a.Y + h - 1, a.Z));
    }

    /// <summary>pyramid X x,y,z base [core:Y]: the stepped pyramid whose base layer's min corner is x,y,z (side `base`, one row
    /// in per layer, (base+1)/2 layers); the optional core: fills everything deeper than two rows in with Y.</summary>
    private static BlueprintOp? PyramidOp(OpLine o)
    {
        var t = o.T;
        if (t.Length < 3 || o.Sym(0) == '\0' || P3(t[1]) is not { } a || !int.TryParse(t[2], out var b) || b < 1)
            return o.Err("use 'pyramid X x,y,z base [core:Y]' (base = side length of the bottom layer)");
        if (!TryPyramidCore(o, out var core)) return null;
        return o.Op("pyramid", o.Sym(0), a, (a.X + b - 1, a.Y + (b + 1) / 2 - 1, a.Z + b - 1), arg: core);
    }

    private static bool TryPyramidCore(OpLine o, out string core)
    {
        core = "";
        if (o.T.Length <= 3) return true;
        var c = o.T[3].Split(':');
        if (c.Length != 2 || !c[0].Equals("core", StringComparison.OrdinalIgnoreCase) || c[1].Length != 1) { o.Err($"'{o.T[3]}': use core:Y with one palette symbol"); return false; }
        core = c[1];
        return true;
    }

    private static BlueprintOp? DoorOp(OpLine o)
    {
        var t = o.T;
        if (t.Length < 2 || o.Sym(0) == '\0' || P3(t[1]) is not { } a) return o.Err("use 'door D x,y,z [north|east|south|west]'");
        Dir? f = t.Length > 2 ? Dirs.Parse(t[2]) : null;
        if (t.Length > 2 && f == null) return o.Err($"'{t[2]}' isn't a direction");
        return o.Op("door", o.Sym(0), a, (a.X, a.Y + 1, a.Z), f);
    }

    private static BlueprintOp? StairsOp(OpLine o)
    {
        var t = o.T;
        if (t.Length < 3 || o.Sym(0) == '\0' || P3(t[1]) is not { } a || Dirs.Parse(t[2]) is not { } f) return o.Err("use 'stairs S x,y,z north|east|south|west' (the way up)");
        var b = t.Length > 3 && P3(t[3]) is { } bb ? bb : a;
        return o.Op("stairs", o.Sym(0), a, b, f);
    }

    /// <summary>roof X flat|gable|slope:dir x1,y,z1 x2,z2 [ridge].</summary>
    private static BlueprintOp? RoofOp(OpLine o)
    {
        var t = o.T;
        if (t.Length < 4 || o.Sym(0) == '\0' || P3(t[2]) is not { } a || P2(t[3]) is not { } b) return o.Err("use 'roof X flat|gable|slope:north x1,y,z1 x2,z2 [ridge-symbol]'");
        if (!TryRoofStyle(o, out var style, out var f)) return null;
        return o.Op("roof", o.Sym(0), a, (b.X, a.Y, b.Z), f, style + (t.Length > 4 && t[4].Length == 1 ? "|" + t[4] : ""));
    }

    private static bool TryRoofStyle(OpLine o, out string style, out Dir? facing)
    {
        style = o.T[1].ToLowerInvariant();
        facing = null;
        if (style.StartsWith("slope", StringComparison.Ordinal))
        {
            facing = Dirs.Parse(style.Split(':').ElementAtOrDefault(1));
            if (facing == null) { o.Err("slope needs a direction: slope:north (rises toward north)"); return false; }
            style = "slope";
            return true;
        }
        if (style is "flat" or "gable") return true;
        o.Err($"roof style '{style}' (flat, gable, slope:dir)");
        return false;
    }
}
