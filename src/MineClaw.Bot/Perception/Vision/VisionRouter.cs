using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>What a spatial question needs: an exact answer from voxels + roles, or the perception model's eyes.</summary>
public enum VisionIntent
{
    /// <summary>Visual / relational (the middle one, shapes, a build's look, pointing): the perception model.</summary>
    Model,
    GenCells,
    GenFluids,
    Faces,
    FocusBlock,
    Void,
    Portal,
    Things,
    /// <summary>Dropped items lying around (pickup entities): names, counts, positions from the entity tracker.</summary>
    Drops,
}

/// <summary>
/// Deterministic-first routing for vision questions (rules, zero tokens). Which blocks are gen cells / floor / walls, which
/// fluids touch a gen cell, the placeable faces of a cell, void under or toward an edge, what block and role sits at the focus,
/// where a portal is and what is beside it, and where trees, saplings, crops, chests, doors, stations and ores are: all known
/// exactly from the voxels and the role layer, so they never go to a model (live 10-27: 5 of 6 Sonnet claims about a roofed
/// dual gen were wrong, 7 s and 876 output tokens for facts the world already had). Only genuinely visual or relational
/// questions (which one is the middle, what a build looks like, shapes, pointing) fall through to perception.
/// </summary>
public static partial class VisionRouter
{
    public const string Prefix = "[vision] exact: ";

    [GeneratedRegex(@"\b(middle|left|right|between|looks? like|looking like|shape|pattern|pointed|pointing|punched|this one|that one|which one|complete|gaps?|missing|design|style|pretty|ugly|holding|wearing|says?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelationalRx();

    [GeneratedRegex(@"(lava|water)\b.{0,50}\b(touch\w*|next to|flow\w*|adjacent|reach\w*|beside)|\b(touch\w*|next to|flow\w*|adjacent|beside)\b.{0,50}\b(lava|water)|stop(ped|s)? (making|producing|regen\w*)|(not|isn't|stopped) regenerat|regenerat\w* cobble|\bgen\b.{0,20}\bbroken|\bbroken\b.{0,20}\bgen", RegexOptions.IgnoreCase)]
    private static partial Regex FluidsRx();

    [GeneratedRegex(@"\bplace\w*\b.{0,30}\bagainst\b|\bfaces?\b|\bis\b.{0,30}\bfree\b|\bplace a block\b|\bplaceable\b", RegexOptions.IgnoreCase)]
    private static partial Regex FacesRx();

    [GeneratedRegex(@"\bwhat is (the block )?at\b|\bwhat block\b|\bits role\b|\bwhat('s| is) (this|that|the focus)", RegexOptions.IgnoreCase)]
    private static partial Regex FocusRx();

    [GeneratedRegex(@"\bportal\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortalRx();

    [GeneratedRegex(@"\bgen(erator)?s?\b|\bmine here\b|\bdig cells?\b|\bwhich (exact )?(block|cell)s?\b.{0,30}\b(mine|dig)\b|\bisland floor\b|\bgen walls?\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenRx();

    [GeneratedRegex(@"\bvoid\b|\bedge\b|\bfall\b|\bhow far\b", RegexOptions.IgnoreCase)]
    private static partial Regex VoidRx();

    [GeneratedRegex(@"\b(trees?|logs?|trunks?|canopy|saplings?|crops?|wheat|farm(land)?|chests?|doors?|ores?|coal|iron|diamonds?|gold|lapis|redstone|furnaces?|workbench|crafting tables?|benches?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThingsRx();

    [GeneratedRegex(@"\bdrops?\b|\bdropped\b|\bitems? (on|lying on) the (ground|floor)\b|\b(lying|floating) (on the )?(ground|floor|around)\b|\bpick(ed)?[ -]?up\b|\bloot\b", RegexOptions.IgnoreCase)]
    private static partial Regex DropsRx();

    [GeneratedRegex(@"\b(ripe|unripe|grown|mature|harvest\w*|wheat|crops?|farmland|replant\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CropRx();

    [GeneratedRegex(@"\b(pointed|pointing|punched|this one|that one|looks? like|holding|wearing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PointingRx();

    /// <summary>Rules first: what the question needs. <see cref="VisionIntent.Model"/> = only a picture can answer it.</summary>
    public static VisionIntent Classify(string? question)
    {
        var q = question ?? "";
        // Ripeness is block metadata (wheat stage 0-7), never a picture question, even with "middle rows" in it (live 10-28:
        // "which wheat is fully grown (ripe)... middle rows" went to Sonnet, 2 of 2 claims wrong, and the job gave up).
        if (q.Length > 0 && CropRx().IsMatch(q) && !PointingRx().IsMatch(q)) return VisionIntent.Things;
        if (q.Length > 0 && DropsRx().IsMatch(q) && !PointingRx().IsMatch(q)) return VisionIntent.Drops;
        if (q.Length == 0 || RelationalRx().IsMatch(q)) return VisionIntent.Model;
        if (FluidsRx().IsMatch(q)) return VisionIntent.GenFluids;
        if (FacesRx().IsMatch(q)) return VisionIntent.Faces;
        if (FocusRx().IsMatch(q)) return VisionIntent.FocusBlock;
        if (PortalRx().IsMatch(q)) return VisionIntent.Portal;
        if (GenRx().IsMatch(q)) return VisionIntent.GenCells;
        if (ThingsRx().IsMatch(q)) return VisionIntent.Things;
        if (VoidRx().IsMatch(q)) return VisionIntent.Void;
        return VisionIntent.Model;
    }

    /// <summary>The exact answer for a deterministic question, else null (a model question).</summary>
    public static string? Answer(string? question, IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int radius,
        IReadOnlyList<VisionEntity>? entities = null)
    {
        var intent = Classify(question);
        return intent == VisionIntent.Model ? null : Answer(intent, question ?? "", w, roles, f, radius, entities);
    }

    /// <param name="entities">Entities around the spot (drops are listed exactly from them); null = unknown.</param>
    public static string Answer(VisionIntent intent, string question, IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int radius,
        IReadOnlyList<VisionEntity>? entities = null)
    {
        var parts = new List<string>();
        switch (intent)
        {
            case VisionIntent.GenCells:
            case VisionIntent.GenFluids:
                parts.Add(Gen(w, roles, f, radius));
                break;
            case VisionIntent.Faces:
                parts.Add(Focus(w, roles, f));
                parts.Add(Faces(w, f));
                parts.Add(VoidUnder(w, f));
                break;
            case VisionIntent.FocusBlock:
                parts.Add(Focus(w, roles, f));
                parts.Add(VoidUnder(w, f));
                break;
            case VisionIntent.Void:
                parts.Add(Edges(w, f, question));
                break;
            case VisionIntent.Portal:
                parts.Add(Portal(w, roles, f, radius));
                break;
            case VisionIntent.Things:
                {
                    // The area the question names ("within 64 of 152,63,288"), else around the focus. Live 10-29: "trees within 64
                    // of log_chest" was answered "1 grown tree" from a 12-block scan around the asking body; the hive brain told
                    // its 20 bodies the forest was gone while dozens of trees stood.
                    var (c, ar) = AskedArea(question, f, Math.Max(radius, 12));
                    parts.Add(Things(w, c, ar, question));
                    if (entities != null && Drops(entities, f, radius) is { } d) parts.Add(d);
                    break;
                }
            case VisionIntent.Drops:
                parts.Add(entities == null ? "drops: not tracked here" : Drops(entities, f, radius) ?? $"no drops within {radius + 4} blocks");
                if (entities != null && Mobs(entities, f, radius) is { } mobs) parts.Add(mobs);
                break;
        }
        // Roles that decide what to mine / where not to walk, when the section didn't already name them.
        if (intent is not (VisionIntent.GenCells or VisionIntent.GenFluids or VisionIntent.Things)
            && PerceptionService.Grounding(w, roles, f, radius, []) is { } g)
            foreach (var part in g.Split(" | "))
                if (part.StartsWith("roles: ", StringComparison.Ordinal)) parts.Add(part);
        var text = Prefix + string.Join(" | ", parts.Where(p => p.Length > 0));
        return text.Length <= PerceptionVerifier.MaxChars ? text : text[..(PerceptionVerifier.MaxChars - 1)] + "…";
    }

    /// <summary>
    /// Dropped items near the focus, from the entity tracker (0x15 pickup spawns until 0x1D destroy): "3 drops: 2 sapling at
    /// 4,64,9 (2 m from the focus); ...", nearest first, with the distance so the brain can walk to them without guessing.
    /// </summary>
    public static string? Drops(IReadOnlyList<VisionEntity> entities, (int X, int Y, int Z) f, int radius)
    {
        var drops = entities.Where(e => e.Kind == "item" && Math.Abs(e.X - f.X - 0.5) <= radius + 4 && Math.Abs(e.Z - f.Z - 0.5) <= radius + 4)
            .Select(e => (E: e, D: Math.Sqrt(Math.Pow(e.X - f.X - 0.5, 2) + Math.Pow(e.Y - f.Y, 2) + Math.Pow(e.Z - f.Z - 0.5, 2))))
            .OrderBy(t => t.D).ToList();
        if (drops.Count == 0) return null;
        var parts = drops.Take(6).Select(t => string.Create(CultureInfo.InvariantCulture,
            $"{EyeRenderer.DropLabel(t.E)} at {(int)Math.Floor(t.E.X)},{(int)Math.Floor(t.E.Y)},{(int)Math.Floor(t.E.Z)} ({t.D:0} m from the focus)"));
        return $"{drops.Count} drop{(drops.Count == 1 ? "" : "s")}: " + string.Join("; ", parts) + (drops.Count > 6 ? "; ..." : "");
    }

    /// <summary>
    /// Mobs near the focus from the entity tracker, burning ones called out (metadata flag 0x01: in daylight undead burn and
    /// die soon, so they need no fighting): "mobs: 2 zombie burning (daylight: dying, no need to fight); creeper 5 m".
    /// </summary>
    public static string? Mobs(IReadOnlyList<VisionEntity> entities, (int X, int Y, int Z) f, int radius)
    {
        var mobs = entities.Where(e => e.Kind == "mob" && Math.Abs(e.X - f.X - 0.5) <= radius + 8 && Math.Abs(e.Z - f.Z - 0.5) <= radius + 8).ToList();
        if (mobs.Count == 0) return null;
        static string Name(VisionEntity e) => (e.Mob ?? e.Name ?? "mob").Replace('_', ' ');
        double Dist(VisionEntity e) => Math.Sqrt(Math.Pow(e.X - f.X - 0.5, 2) + Math.Pow(e.Y - f.Y, 2) + Math.Pow(e.Z - f.Z - 0.5, 2));
        var parts = new List<string>();
        foreach (var g in mobs.Where(m => m.Burning).GroupBy(Name))
            parts.Add($"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")} burning ({(g.Key is "zombie" or "skeleton" ? "daylight: dying soon, no need to fight" : "on fire")}), nearest {Dist(g.MinBy(Dist)!):0} m");
        foreach (var g in mobs.Where(m => !m.Burning).GroupBy(Name))
        {
            var n = g.MinBy(Dist)!;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}, nearest at {(int)Math.Floor(n.X)},{(int)Math.Floor(n.Y)},{(int)Math.Floor(n.Z)} ({Dist(n):0} m)"));
        }
        return "mobs: " + string.Join("; ", parts);
    }

    /// <summary>The deterministic fallback when a model question can't go to the model (perception paused, low value).</summary>
    public static string Fallback(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int radius, string why)
    {
        var parts = new List<string> { Focus(w, roles, f), VoidUnder(w, f) };
        if (PerceptionService.Grounding(w, roles, f, radius, []) is { } g) parts.Add(g);
        var text = Prefix + string.Join(" | ", parts) + $" ({why})";
        return text.Length <= PerceptionVerifier.MaxChars ? text : text[..(PerceptionVerifier.MaxChars - 1)] + "…";
    }

    // ------------------------------------------------------------------ sections

    /// <summary>"N 5, E 8, S 6, W 4": how many of <paramref name="cs"/> lie on each side of <paramref name="f"/> (north = -z).</summary>
    internal static string Sides(IEnumerable<(int X, int Y, int Z)> cs, (int X, int Y, int Z) f)
    {
        int n = 0, e = 0, s = 0, wst = 0;
        foreach (var c in cs)
        {
            int dx = c.X - f.X, dz = c.Z - f.Z;
            if (Math.Abs(dx) >= Math.Abs(dz)) { if (dx >= 0) e++; else wst++; }
            else if (dz < 0) n++; else s++;
        }
        return string.Join(", ", new[] { ("N", n), ("E", e), ("S", s), ("W", wst) }.Where(t => t.Item2 > 0).Select(t => $"{t.Item1} {t.Item2}"));
    }

    private static string P((int X, int Y, int Z) c) => string.Create(CultureInfo.InvariantCulture, $"{c.X},{c.Y},{c.Z}");

    private static string Name(int id) => id == 0 ? "air" : Blocks.Get(id).Name.Replace('_', ' ');

    private static readonly (string Name, int Dx, int Dy, int Dz)[] Dirs6 =
        [("E", 1, 0, 0), ("W", -1, 0, 0), ("S", 0, 0, 1), ("N", 0, 0, -1), ("below", 0, -1, 0), ("above", 0, 1, 0)];

    private static readonly (string Name, int Dx, int Dy, int Dz)[] FlowIn =
        [("E", 1, 0, 0), ("W", -1, 0, 0), ("S", 0, 0, 1), ("N", 0, 0, -1), ("above", 0, 1, 0)];

    private static BlockRole Role(RoleMap roles, int x, int y, int z)
    {
        try { return roles(x, y, z); } catch (Exception) { return BlockRole.None; }
    }

    /// <summary>Gen cells near the focus (role layer), nearest first.</summary>
    public static List<(int X, int Y, int Z)> GenCells(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int radius)
    {
        var list = new List<(int X, int Y, int Z)>();
        int r = Math.Clamp(radius, 2, 8), h = w.WorldHeight;
        for (int y = f.Y - 2; y <= f.Y + 2; y++)
            for (int z = f.Z - r; z <= f.Z + r; z++)
                for (int x = f.X - r; x <= f.X + r; x++)
                    if (w.IsLoaded(x, z) && (uint)y < h && Role(roles, x, y, z) == BlockRole.GenCell) list.Add((x, y, z));
        return list.OrderBy(c => Math.Abs(c.X - f.X) + Math.Abs(c.Y - f.Y) + Math.Abs(c.Z - f.Z)).ToList();
    }

    /// <summary>"gen cells (G, mine only these): 4,64,6 water E lava S; ... | not gen: floor y63 under them; walls 3,64,6 +5".</summary>
    private static string Gen(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int radius)
    {
        var cells = GenCells(w, roles, f, radius);
        if (cells.Count == 0) return string.Create(CultureInfo.InvariantCulture, $"no gen cell within {Math.Clamp(radius, 2, 8)} of {P(f)}");
        var sb = new StringBuilder("gen cells (G, mine only these): ");
        var cellSet = cells.ToHashSet();
        var walls = new List<(int X, int Y, int Z)>();
        var parts = new List<string>();
        foreach (var c in cells.Take(4))
        {
            var water = new List<string>();
            var lava = new List<string>();
            foreach (var (n, dx, dy, dz) in FlowIn)
            {
                int id = w.GetBlock(c.X + dx, c.Y + dy, c.Z + dz);
                if (Blocks.IsWater(id)) water.Add(n);
                else if (Blocks.IsLava(id)) lava.Add(n);
            }
            int self = w.GetBlock(c.X, c.Y, c.Z);
            string state = self == 0 ? " (air: refilling)" : self is Blocks.Cobblestone or Blocks.Stone ? "" : $" ({Name(self)})";
            string flow = (water.Count > 0 ? "water " + string.Join(",", water) : "NO water next to it")
                + " " + (lava.Count > 0 ? "lava " + string.Join(",", lava) : "NO lava next to it");
            parts.Add(P(c) + state + " " + flow);
            foreach (var (_, dx, dy, dz) in Dirs6)
            {
                var q = (c.X + dx, c.Y + dy, c.Z + dz);
                if (dy != 0 || cellSet.Contains(q)) continue;
                int id = w.GetBlock(q.Item1, q.Item2, q.Item3);
                if (id != 0 && w.Info(id).Solid && !walls.Contains(q)) walls.Add(q);
            }
        }
        sb.Append(string.Join("; ", parts));
        if (cells.Count > 4) sb.Append(CultureInfo.InvariantCulture, $" +{cells.Count - 4} more");
        var floors = cells.Select(c => c.Y - 1).Distinct().OrderBy(y => y).ToList();
        sb.Append(" | not gen cells: floor y").Append(string.Join("/y", floors)).Append(" under them");
        if (walls.Count > 0)
            sb.Append("; walls ").Append(string.Join(" ", walls.Take(3).Select(P))).Append(walls.Count > 3 ? $" +{walls.Count - 3}" : "");
        if (!cellSet.Contains(f)) sb.Append(" | focus ").Append(P(f)).Append(' ').Append(Name(w.GetBlock(f.X, f.Y, f.Z))).Append(" is not a gen cell");
        return sb.ToString();
    }

    private static string Focus(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f)
    {
        int id = w.GetBlock(f.X, f.Y, f.Z);
        var role = Role(roles, f.X, f.Y, f.Z);
        string roleText = role switch
        {
            BlockRole.GenCell => "gen cell (MINE HERE)",
            BlockRole.CropRipe or BlockRole.CropGrowing => RoleClassifier.CropText(id, w.GetMeta(f.X, f.Y, f.Z)) ?? BlockRoles.Name(role),
            BlockRole.None => id == 0 ? "free" : "",
            _ => BlockRoles.Name(role),
        };
        int under = w.GetBlock(f.X, f.Y - 1, f.Z);
        return $"{P(f)} {Name(id)}{(roleText.Length > 0 ? ", " + roleText : "")}; under {P((f.X, f.Y - 1, f.Z))} {Name(under)}";
    }

    private static string Faces(IBlockAccess w, (int X, int Y, int Z) f)
    {
        var faces = new List<string>();
        foreach (var (n, dx, dy, dz) in Dirs6)
        {
            int id = w.GetBlock(f.X + dx, f.Y + dy, f.Z + dz);
            if (id != 0 && w.Info(id).Solid) faces.Add($"{n} {P((f.X + dx, f.Y + dy, f.Z + dz))}");
        }
        return faces.Count == 0 ? "faces to place against: none (nothing solid next to it)" : "faces to place against: " + string.Join(", ", faces);
    }

    private static string VoidUnder(IBlockAccess w, (int X, int Y, int Z) f)
    {
        if (!w.IsLoaded(f.X, f.Z)) return "void under: unknown (not loaded)";
        for (int y = f.Y - 1; y >= 0; y--)
            if (w.GetBlock(f.X, y, f.Z) != 0) return y == f.Y - 1 ? "void under: no" : $"void under: no (drop to y{y})";
        return "void under: YES (fall)";
    }

    private static bool VoidColumn(IBlockAccess w, int x, int top, int z)
    {
        for (int y = top; y >= 0; y--) if (w.GetBlock(x, y, z) != 0) return false;
        return true;
    }

    /// <summary>"to the void: E 3 floor then void at 5,62,0; W 5 ...": the asked direction first.</summary>
    private static string Edges(IBlockAccess w, (int X, int Y, int Z) f, string question)
    {
        var dirs = new List<(string Name, string Word, int Dx, int Dz)> { ("E", "east", 1, 0), ("W", "west", -1, 0), ("S", "south", 0, 1), ("N", "north", 0, -1) };
        var asked = dirs.Where(d => Regex.IsMatch(question, $@"\b({d.Word}|{d.Name})\b", RegexOptions.IgnoreCase)).ToList();
        var order = asked.Concat(dirs.Except(asked)).ToList();
        var parts = new List<string>();
        foreach (var (n, _, dx, dz) in order)
        {
            int floor = 0;
            string? end = null;
            for (int k = 1; k <= 16; k++)
            {
                int x = f.X + dx * k, z = f.Z + dz * k;
                if (!w.IsLoaded(x, z)) { end = $"{floor} then unknown"; break; }
                if (VoidColumn(w, x, f.Y + 1, z)) { end = $"{floor} floor then void at {P((x, f.Y - 1, z))}"; break; }
                floor++;
            }
            parts.Add($"{n} {end ?? $"{floor}+ (no void within 16)"}");
        }
        return "to the void edge (blocks of floor from " + P(f) + "): " + string.Join("; ", parts);
    }

    private static string Portal(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int radius)
    {
        var sheet = new List<(int X, int Y, int Z)>();
        int r = Math.Clamp(radius, 2, 12), h = w.WorldHeight;
        for (int y = f.Y - 6; y <= f.Y + 6; y++)
            for (int z = f.Z - r; z <= f.Z + r; z++)
                for (int x = f.X - r; x <= f.X + r; x++)
                    if ((uint)y < h && w.GetBlock(x, y, z) == Blocks.Portal) sheet.Add((x, y, z));
        if (sheet.Count == 0) return $"no portal sheet within {r} of {P(f)}";
        int x0 = sheet.Min(c => c.X), x1 = sheet.Max(c => c.X), y0 = sheet.Min(c => c.Y), y1 = sheet.Max(c => c.Y), z0 = sheet.Min(c => c.Z), z1 = sheet.Max(c => c.Z);
        static string R(string axis, int a, int b) => a == b ? $"{axis}={a}" : $"{axis}={a}..{b}";
        var sb = new StringBuilder($"portal sheet {R("x", x0, x1)} {R("y", y0, y1)} {R("z", z0, z1)} (obsidian frame around it)");
        // Stepping out of the sheet: the floor level beside its bottom row, per side it opens to.
        var outs = new List<string>();
        bool alongZ = x0 == x1;
        var sides = alongZ ? new[] { ("E", 1, 0), ("W", -1, 0) } : new[] { ("S", 0, 1), ("N", 0, -1) };
        foreach (var (n, dx, dz) in sides)
        {
            int x = (alongZ ? x0 : (x0 + x1) / 2) + dx, z = (alongZ ? (z0 + z1) / 2 : z0) + dz;
            int fy = y0 - 1;
            int id = w.GetBlock(x, fy, z);
            string what = !w.IsLoaded(x, z) ? "unknown" : VoidColumn(w, x, y0, z) ? "VOID (fall)" : id != 0 && w.Info(id).Solid ? $"floor {Name(id)} {P((x, fy, z))}" : "a drop";
            outs.Add($"{n} {what}");
        }
        sb.Append("; stepping out: ").Append(string.Join(", ", outs));
        return sb.ToString();
    }

    /// <summary>
    /// The field from metadata: "wheat 6: 4 ripe (7/7) 1,64,2 2,64,2 +2; 2 growing (stage 3-5/7, leave them); empty farmland 1
    /// (plant seeds): 1,63,3". Ripeness is always known (the crop's block meta), never guessed from a picture.
    /// </summary>
    private static string Crops(List<(int X, int Y, int Z, int Meta)> crops, List<(int X, int Y, int Z)> emptySoil,
        Func<IEnumerable<(int X, int Y, int Z)>, int, string> list)
    {
        if (crops.Count == 0 && emptySoil.Count == 0) return "no crops";
        var sb = new StringBuilder();
        var ripe = crops.Where(c => c.Meta >= 7).ToList();
        var growing = crops.Where(c => c.Meta < 7).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"wheat {crops.Count}: {ripe.Count} ripe (7/7)");
        if (ripe.Count > 0) sb.Append(' ').Append(list(ripe.Select(c => (c.X, c.Y, c.Z)), 4));
        if (growing.Count > 0)
            sb.Append(CultureInfo.InvariantCulture, $"; {growing.Count} growing (stage {growing.Min(c => c.Meta)}-{growing.Max(c => c.Meta)}/7, leave them)");
        if (emptySoil.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"; empty farmland {emptySoil.Count} (plant seeds): ").Append(list(emptySoil, 3));
        sb.Append("; crops are always harvestable when ripe (skill harvest_wheat)");
        return sb.ToString();
    }

    /// <summary>Widest area a Things question scans (a body's own view distance 4 = 64 blocks; the crew's view is wider).</summary>
    public const int MaxThingsRadius = 64;

    [GeneratedRegex(@"\b(?:within|in|inside|radius(?: of)?|up to)\s+(\d{1,3})\b|\b(\d{1,3})\s*(?:blocks?|m)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadiusRx();

    [GeneratedRegex(@"(-?\d{1,7})\s*,\s*(-?\d{1,3})\s*,\s*(-?\d{1,7})")]
    private static partial Regex CoordRx();

    /// <summary>The centre and radius a question names ("within 64 of 152,63,288", "trees in 40 blocks"), else the focus and
    /// <paramref name="radius"/>. The radius is capped at <see cref="MaxThingsRadius"/>.</summary>
    public static ((int X, int Y, int Z) Center, int Radius) AskedArea(string? question, (int X, int Y, int Z) focus, int radius)
    {
        var q = question ?? "";
        var c = focus;
        if (CoordRx().Match(q) is { Success: true } cm
            && int.TryParse(cm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(cm.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            && int.TryParse(cm.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
            c = (x, y, z);
        int r = radius;
        // Coordinates removed first so "152,63,288" never reads as a radius.
        if (RadiusRx().Match(CoordRx().Replace(q, " ")) is { Success: true } rm
            && int.TryParse(rm.Groups[1].Success ? rm.Groups[1].Value : rm.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked)
            && asked > 0)
            r = Math.Max(radius, asked);
        return (c, Math.Clamp(r, 1, MaxThingsRadius));
    }

    /// <summary>Trees (TreeScan: logs, leaves, soil), saplings, crops, chests, doors, stations and ores near the focus.</summary>
    private static string Things(IBlockAccess w, (int X, int Y, int Z) f, int r, string question)
    {
        var q = question.ToLowerInvariant();
        bool all = !ThingsRx().IsMatch(q);
        bool Asked(params string[] words) => all || words.Any(word => Regex.IsMatch(q, $@"\b{word}"));
        var logs = new List<BlockPos>();
        var saplings = new List<(int X, int Y, int Z)>();
        var crops = new List<(int X, int Y, int Z, int Meta)>();
        var farmland = new List<(int X, int Y, int Z)>();
        var named = new Dictionary<string, List<(int X, int Y, int Z)>>();
        void Add(string k, (int, int, int) p) { if (!named.TryGetValue(k, out var l)) named[k] = l = new(); l.Add(p); }
        // A wide area spans hills and valleys (1.12.2 terrain): a taller band than a 12-block look.
        int below = r > 12 ? 20 : 8, above = r > 12 ? 28 : 12;
        for (int y = Math.Max(0, f.Y - below), y1 = Math.Min(w.WorldHeight - 1, f.Y + above); y <= y1; y++)
            for (int z = f.Z - r; z <= f.Z + r; z++)
                for (int x = f.X - r; x <= f.X + r; x++)
                {
                    int id = w.GetBlock(x, y, z);
                    switch (id)
                    {
                        case 0: continue;
                        case Blocks.Log or Wood.Log2: logs.Add(new BlockPos(x, y, z)); break;
                        case Blocks.Sapling: saplings.Add((x, y, z)); break;
                        case Blocks.Crops: crops.Add((x, y, z, w.GetMeta(x, y, z))); break;
                        case Blocks.Farmland: if (w.GetBlock(x, y + 1, z) == 0) farmland.Add((x, y, z)); break;
                        case Blocks.Chest: Add("chest", (x, y, z)); break;
                        case Blocks.WoodenDoor or Blocks.IronDoor: if ((w.GetMeta(x, y, z) & 8) == 0) Add("door", (x, y, z)); break;
                        case Blocks.Furnace or Blocks.LitFurnace: Add("furnace", (x, y, z)); break;
                        case Blocks.Workbench: Add("bench", (x, y, z)); break;
                        case Blocks.CoalOre: Add("coal ore", (x, y, z)); break;
                        case Blocks.IronOre: Add("iron ore", (x, y, z)); break;
                        case Blocks.GoldOre: Add("gold ore", (x, y, z)); break;
                        case Blocks.DiamondOre: Add("diamond ore", (x, y, z)); break;
                        case Blocks.LapisOre: Add("lapis ore", (x, y, z)); break;
                        case Blocks.RedstoneOre or Blocks.LitRedstoneOre: Add("redstone ore", (x, y, z)); break;
                    }
                }
        (int X, int Y, int Z) T(BlockPos p) => (p.X, p.Y, p.Z);
        int D((int X, int Y, int Z) c) => Math.Abs(c.X - f.X) + Math.Abs(c.Z - f.Z) + Math.Abs(c.Y - f.Y);
        string List(IEnumerable<(int X, int Y, int Z)> cs, int max = 4)
        {
            var l = cs.OrderBy(D).ToList();
            return string.Join(" ", l.Take(max).Select(P)) + (l.Count > max ? $" +{l.Count - max}" : "");
        }
        var parts = new List<string>();
        if (Asked("tree", "log", "trunk", "canopy", "wood"))
        {
            var trees = new Dictionary<BlockPos, TreeInfo>();
            var seen = new HashSet<BlockPos>();
            foreach (var l in logs.OrderBy(p => p.Y))
            {
                if (seen.Contains(l) || TreeScan.Analyze(w, l) is not { } t) continue;
                seen.UnionWith(t.Logs);
                trees.TryAdd(t.Base, t);
            }
            var grown = trees.Values.Where(TreeStewardship.IsLiving).OrderBy(t => D(T(t.Base))).ToList();
            var bare = trees.Values.Where(t => !TreeStewardship.IsLiving(t)).OrderBy(t => D(T(t.Base))).ToList();
            // A remnant of our own chop (it stopped part-way): ours to finish with chop_tree, not "someone's build".
            var ours = bare.Where(t => TreeScan.Unfinished(w, t) is not null).ToList();
            bare = bare.Except(ours).ToList();
            if (ours.Count > 0)
                parts.Add("our unfinished tree" + (ours.Count == 1 ? "" : "s") + " (chop_tree at= finishes it): "
                          + string.Join("; ", ours.Take(3).Select(t => $"{t.WoodName} {P(T(t.Base))} {t.Logs.Count} logs left")));
            parts.Add(grown.Count == 0 ? $"no grown tree within {r}"
                : $"{grown.Count} grown tree{(grown.Count == 1 ? "" : "s")}: " + string.Join("; ", grown.Take(4).Select(t => $"{t.WoodName} {P(T(t.Base))} {t.Logs.Count} logs {t.Leaves.Count} leaves"))
                  + (grown.Count > 4 ? $" +{grown.Count - 4} ({Sides(grown.Select(t => T(t.Base)), f)})" : ""));
            if (bare.Count > 0) parts.Add("log columns without a crown/soil: " + List(bare.Select(t => T(t.Base)), 3));
        }
        if (Asked("sapling", "tree"))
        {
            var planted = saplings.Where(s => w.GetBlock(s.X, s.Y - 1, s.Z) is Blocks.Dirt or Blocks.Grass).ToList();
            parts.Add(planted.Count == 0 ? "no planted sapling" : $"{planted.Count} sapling{(planted.Count == 1 ? "" : "s")}: " + List(planted));
        }
        if (Asked("crop", "wheat", "farm", "ripe", "unripe", "grown", "mature", "harvest", "replant"))
            parts.Add(Crops(crops, farmland, List));
        foreach (var (key, words) in new[]
        {
            ("chest", new[] { "chest" }), ("door", new[] { "door" }), ("furnace", new[] { "furnace" }), ("bench", new[] { "bench", "workbench", "crafting" }),
            ("coal ore", new[] { "ore", "coal" }), ("iron ore", new[] { "ore", "iron" }), ("gold ore", new[] { "ore", "gold" }),
            ("diamond ore", new[] { "ore", "diamond" }), ("lapis ore", new[] { "ore", "lapis" }), ("redstone ore", new[] { "ore", "redstone" }),
        })
        {
            if (!Asked(words)) continue;
            if (named.TryGetValue(key, out var l)) parts.Add($"{key}{(l.Count == 1 ? "" : "s")} {List(l, 3)}");
            else if (!all && !words.Contains("ore")) parts.Add($"no {key}");
            else if (!all && key == "coal ore" && !named.Keys.Any(k => k.EndsWith(" ore"))) parts.Add("no ore");
        }
        if (q.Contains("chest") && (q.Contains("item") || q.Contains("in it") || q.Contains("contain")))
            parts.Add("contents aren't visible from outside: open it or check the chest memory");
        return parts.Count == 0 ? $"nothing of that within {r} of {P(f)}" : string.Join(" | ", parts) + $" (within {r} of {P(f)})";
    }
}
