using System.Globalization;
using System.Text;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>What the deterministic checks know about the site: the world, the protection policy and the materials at hand.</summary>
public sealed class SiteContext
{
    public required IBlockAccess World { get; init; }
    /// <summary>Protection verdict of an existing block (<see cref="Protection.Check"/>); null = nothing protected.</summary>
    public Func<BlockPos, int, ProtectionVerdict>? Verdict { get; init; }
    /// <summary>Inventory count of (item, damage; −1 any).</summary>
    public Func<int, int, int>? Inventory { get; init; }
    /// <summary>Count of (item, damage; −1 any) in known chests (shared chest memory): a stone slab (44:0) isn't a cobblestone slab (44:3).</summary>
    public Func<int, int, int>? Stored { get; init; }
    /// <summary>A cobble generator dig cell is known (missing cobble comes from it).</summary>
    public bool GeneratorKnown { get; init; }
    /// <summary>Trees the stewardship rule allows felling (missing wood comes from them).</summary>
    public int ChoppableTrees { get; init; }
    /// <summary>Lighting was asked for in the description ("lit", "torches").</summary>
    public bool WantsLight { get; init; }
}

/// <summary>One material of the bill: what the build needs, what is on hand / in storage, and how the rest is obtained.</summary>
public sealed record BillLine(int Item, int Damage, int Need, int Have, int Stored, int Missing, string Source, int Seconds)
{
    public string Name => BuildBlocks.HasVariants(Item) ? Items.VariantName(Item, Damage).Replace('_', ' ') : Items.Get(Item).Name.Replace('_', ' ');

    /// <summary>"256 cobblestone slab (44:3, 3 cobblestone → 3; short 159: craft from ...)": a variant names its id:meta and
    /// recipe so it can't be read as another kind (live 10-28: a cobblestone-slab bill made smooth stone slabs).</summary>
    public string Text(bool source = true)
    {
        var notes = new List<string>();
        if (BuildBlocks.HasVariants(Item)) notes.Add(Recipes.VariantTag(Item, Damage));
        if (Missing > 0) notes.Add(source && Source.Length > 0 ? $"short {Missing}: {Source}" : $"short {Missing}");
        return notes.Count == 0 ? $"{Need} {Name}" : $"{Need} {Name} ({string.Join("; ", notes)})";
    }
}

/// <summary>Result of the deterministic checks on one blueprint version.</summary>
public sealed class DesignEvaluation
{
    public List<BlueprintIssue> Issues { get; } = new();
    public List<BillLine> Bill { get; } = new();
    public int Blocks { get; set; }
    public int Interior { get; set; }
    public int Doors { get; set; }
    public int Windows { get; set; }
    /// <summary>World side each door opens to (what players see: the chat summary).</summary>
    public List<Dir> DoorSides { get; } = new();
    /// <summary>The same doors' sides in the author's drawing (local frame, like every coordinate in <see cref="Compact"/>).</summary>
    public List<Dir> DrawnDoorSides { get; } = new();
    /// <summary>Interior cells no walk from outside reaches.</summary>
    public int Unreachable { get; set; }
    /// <summary>Standing cells with only 1 block of headroom.</summary>
    public int LowHeadroom { get; set; }
    /// <summary>Existing blocks the build has to clear first.</summary>
    public int ToClear { get; set; }
    public bool Pass => !Issues.Any(i => i.Level == IssueLevel.Error);
    public int Errors => Issues.Count(i => i.Level == IssueLevel.Error);
    public int Warnings => Issues.Count(i => i.Level == IssueLevel.Warning);
    /// <summary>Warnings that say nothing about the design's quality (an inferred size line, unloaded columns, clearing grass).</summary>
    private static readonly HashSet<string> MinorWarnings = ["size-inferred", "unknown", "obstruction"];

    /// <summary>Warnings worth another version.</summary>
    public int RealWarnings => Issues.Count(i => i.Level == IssueLevel.Warning && !MinorWarnings.Contains(i.Code));

    /// <summary>
    /// Quality, lower is better: errors dominate; then the per-cell problems (unreachable interior, 1-high headroom), each other
    /// real warning, blocks to clear, closeness to the request (the footprint off the asked floor) and the reviewer's severity
    /// (a complaint plus each concrete issue). Live #8: v1 (1 headroom cell) must beat v3 (8 unreachable, 4 headroom, stray blocks).
    /// </summary>
    public int Score(Critique? critique = null)
    {
        int s = Errors * 1000 + Unreachable * 8 + LowHeadroom * 8 + ToClear;
        s += Issues.Count(i => i.Level == IssueLevel.Warning && !MinorWarnings.Contains(i.Code) && i.Code is not ("entrance" or "headroom")) * 10;
        s += Issues.Count(i => i.Code == "size-mismatch") * 30;
        if (critique is { Ok: false, Skipped: false } c) s += 15 + 10 * c.Issues.Count;
        return s;
    }
    public int MissingSeconds => Bill.Sum(b => b.Seconds);
    public bool MaterialsUnobtainable => Bill.Any(b => b.Missing > 0 && b.Source.StartsWith("ask", StringComparison.Ordinal));

    /// <summary>The ≈100-token evaluation the author sees (local blueprint coordinates).</summary>
    public string Compact(int version, string? critique = null, int maxChars = 520)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"v{version} {(Pass ? "PASS" : "FAIL")} {Blocks} blocks");
        if (Doors > 0) sb.Append(DoorsText());
        if (Windows > 0) sb.Append(CultureInfo.InvariantCulture, $", {Windows} window blocks");
        if (Interior > 0) sb.Append(CultureInfo.InvariantCulture, $", {Interior} interior cells");
        AppendIssues(sb, " | errors: ", IssueLevel.Error, 5);
        AppendIssues(sb, " | warn: ", IssueLevel.Warning, 3);
        if (Bill.Count > 0) sb.Append(" | bill: ").Append(BillText(4));
        if (!string.IsNullOrWhiteSpace(critique)) sb.Append(" | look: ").Append(critique.Trim());
        var s = sb.ToString();
        return s.Length <= maxChars ? s : s[..(maxChars - 1)] + "…";
    }

    /// <summary>", 2 doors south/north (faces north/south once turned)": the drawn sides, and the world's when the turn changes them.</summary>
    private string DoorsText()
    {
        var drawn = string.Join("/", (DrawnDoorSides.Count > 0 ? DrawnDoorSides : DoorSides).Distinct().Select(d => d.Name()));
        var world = string.Join("/", DoorSides.Distinct().Select(d => d.Name()));
        return string.Create(CultureInfo.InvariantCulture,
            $", {Doors} door{(Doors > 1 ? "s" : "")} {drawn}{(DrawnDoorSides.Count > 0 && drawn != world ? $" (faces {world} once turned)" : "")}");
    }

    /// <summary>The first <paramref name="max"/> issues of a level after a label, "+N" for the rest.</summary>
    private void AppendIssues(StringBuilder sb, string label, IssueLevel level, int max)
    {
        var list = Issues.Where(i => i.Level == level).Select(i => i.ToString()).ToList();
        if (list.Count > 0) sb.Append(label).Append(string.Join("; ", list.Take(max))).Append(list.Count > max ? $" +{list.Count - max}" : "");
    }

    public string BillText(int max = 6) => string.Join(", ", Bill.OrderByDescending(b => b.Need).Take(max).Select(b => b.Text()))
        + (Bill.Count > max ? $" +{Bill.Count - max} more" : "");
}

/// <summary>
/// Deterministic design checks (zero tokens): material bill vs inventory + storage with the acquisition route, floating /
/// unsupported blocks, falling sand/gravel, blocks over the void, overlap with protected or never-break blocks, obstructions,
/// liquids in the way, reachable entrances, interior headroom ≥ 2, lighting when asked for, door halves on a full block,
/// torches/ladders on a wall.
/// </summary>
public static partial class DesignChecks
{
    public static DesignEvaluation Evaluate(VoxelPlan plan, SiteContext site, IEnumerable<BlueprintIssue>? structural = null)
    {
        var e = new DesignEvaluation();
        if (structural != null) e.Issues.AddRange(structural.Where(i => i.Level != IssueLevel.Info));
        e.Blocks = plan.Blocks.Values.Count(b => !b.UpperDoor);
        var checks = new SiteChecks(plan, site, e);
        checks.Site();
        var placed = plan.Blocks.Values.ToList();
        checks.Support(placed);
        checks.Void(placed);
        checks.Doors(placed);
        checks.UpperDoorHalves(placed);
        checks.Attachments(placed);
        e.Windows = placed.Count(b => b.Id is Data.Blocks.Glass or Data.Blocks.GlassPane);

        // ---- interior, entrances, headroom, light
        Interior(plan, site.World, e, checks.L, checks.List, site.WantsLight || plan.Blueprint.Light, plan.Blueprint.Light);

        // ---- bill
        Bill(plan, site, e);
        if (e.MaterialsUnobtainable)
            checks.Add(IssueLevel.Warning, "materials", "some materials can't be gathered here: " + string.Join(", ", e.Bill.Where(b => b.Missing > 0 && b.Source.StartsWith("ask", StringComparison.Ordinal)).Select(b => b.Name)));
        return e;
    }

    internal static bool SameState(PlannedBlock want, int id, int meta) =>
        want.Id == id && (!BuildBlocks.HasVariants(id) || (want.Meta & 15) == (meta & 15));

    /// <summary>The door's outside: the horizontal side whose cell isn't covered by the build.</summary>
    private static Dir? DoorOutside(VoxelPlan plan, IBlockAccess w, PlannedBlock d)
    {
        foreach (var dir in new[] { Dir.North, Dir.East, Dir.South, Dir.West })
        {
            var (dx, dz) = dir.Offset();
            var n = new BlockPos(d.Pos.X + dx, d.Pos.Y, d.Pos.Z + dz);
            if (!plan.Inside(n)) return dir;
        }
        return null;
    }

    private static bool Passable(int id) => id == 0 || !Data.Blocks.Get(id).Solid || id is Data.Blocks.WoodenDoor or Data.Blocks.IronDoor or Data.Blocks.FenceGate or Data.Blocks.Trapdoor;

    private static void Interior(VoxelPlan plan, IBlockAccess w, DesignEvaluation e, Func<BlockPos, string> L, Func<IEnumerable<BlockPos>, string> list,
        bool light, bool lightRequired)
    {
        var scan = new InteriorScan(plan, w);
        var (interior, low) = scan.Cells();
        e.Interior = interior.Count;
        e.LowHeadroom = low.Count;
        if (low.Count > 0)
            e.Issues.Add(new(interior.Count == 0 ? IssueLevel.Error : IssueLevel.Warning, "headroom", $"headroom 1 at {low.Count} interior cell(s) ({list(low)}): players need 2"));
        if (interior.Count == 0) return;

        var reached = scan.Reachable();
        var cut = interior.Where(p => !reached.Contains(p)).ToList();
        e.Unreachable = cut.Count;
        if (cut.Count == interior.Count) e.Issues.Add(new(IssueLevel.Error, "entrance", $"no entrance: the {interior.Count} interior cells can't be reached from outside (add a door or a 2-high opening)"));
        else if (cut.Count > 0) e.Issues.Add(new(IssueLevel.Warning, "entrance", $"{cut.Count} interior cell(s) can't be reached ({list(cut)})"));

        if (light) DarkCells(plan, e, interior, list, lightRequired);
    }

    private static void DarkCells(VoxelPlan plan, DesignEvaluation e, List<BlockPos> interior, Func<IEnumerable<BlockPos>, string> list, bool lightRequired)
    {
        var lights = plan.Blocks.Values.Where(b => b.Id is Data.Blocks.Torch or Data.Blocks.Glowstone or Data.Blocks.JackOLantern)
            .Select(b => (b.Pos, Level: b.Id == Data.Blocks.Torch ? 14 : 15)).ToList();
        var dark = interior.Where(p => !lights.Any(l => l.Level - l.Pos.ManhattanTo(p) >= 8)).ToList();
        if (dark.Count > 0)
            e.Issues.Add(new(lightRequired ? IssueLevel.Error : IssueLevel.Warning, "light", $"{dark.Count} interior cell(s) stay dark (light < 8: mobs spawn) e.g. {list(dark)}: add torches"));
    }

    /// <summary>The items the plan still needs (item, damage) → count: every block not already standing as planned.</summary>
    private static Dictionary<(int Item, int Damage), int> Needs(VoxelPlan plan, IBlockAccess world)
    {
        var need = new Dictionary<(int Item, int Damage), int>();
        foreach (var b in plan.Blocks.Values)
        {
            if (b.UpperDoor) continue;
            int id = world.IsLoaded(b.Pos.X, b.Pos.Z) ? world.GetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z) : 0;
            if (id == b.Id && SameState(b, id, world.GetMeta(b.Pos.X, b.Pos.Y, b.Pos.Z))) continue; // already there
            var key = BuildBlocks.ItemFor(b.Id, b.Meta);
            need[key] = need.GetValueOrDefault(key) + 1;
        }
        return need;
    }

    /// <summary>Needs per item vs inventory + known chests; missing cobble from the gen, wood from logs/trees, crafted items from their recipe.</summary>
    public static void Bill(VoxelPlan plan, SiteContext site, DesignEvaluation e)
    {
        var need = Needs(plan, site.World);
        // Raw materials stretch across lines (planks for doors and stairs too): track what's left as lines are costed.
        int pool(int item) => Math.Max(0, (site.Inventory?.Invoke(item, -1) ?? 0) + (site.Stored?.Invoke(item, -1) ?? 0));
        var used = new Dictionary<int, int>();
        int Take(int item, int n)
        {
            int left = Math.Max(0, pool(item) - used.GetValueOrDefault(item));
            int t = Math.Min(left, n);
            used[item] = used.GetValueOrDefault(item) + t;
            return t;
        }
        foreach (var ((item, dmg), n) in need.OrderByDescending(kv => kv.Value))
        {
            int variant = BuildBlocks.HasVariants(item) ? dmg : -1;
            int have = site.Inventory?.Invoke(item, variant) ?? 0;
            int stored = site.Stored?.Invoke(item, variant) ?? 0;
            int got = Math.Min(n, have + Math.Max(0, stored));
            used[item] = used.GetValueOrDefault(item) + got;
            int missing = n - got;
            var (src, secs) = missing == 0 ? ("", 0) : Source(item, dmg, missing, site, Take);
            e.Bill.Add(new BillLine(item, dmg, n, have, stored, missing, src, secs));
        }
    }

    private static (string Source, int Seconds) Source(int item, int dmg, int missing, SiteContext site, Func<int, int, int> take)
    {
        if (item == Data.Blocks.Cobblestone)
            return site.GeneratorKnown ? ($"gen ~{(int)Math.Ceiling(missing * 1.6)}s", (int)Math.Ceiling(missing * 1.6)) : ("ask: no generator known", 0);
        if (item == Data.Blocks.Log)
            return site.ChoppableTrees > 0 ? ($"chop trees (~{(missing + 4) / 5})", 60 * ((missing + 4) / 5)) : ("ask: no tree to fell", 0);
        if (Recipes.ForOutput(item, dmg).Count == 0 && Smelting.ForOutput(item).Count == 0) return ("ask: can't be made here", 0);
        return CraftSource(item, dmg, missing, site, take);
    }

    /// <summary>Crafted (planks, stairs, slabs, doors, panes, torches...): what raw input is still short after what's on hand.</summary>
    private static (string Source, int Seconds) CraftSource(int item, int dmg, int missing, SiteContext site, Func<int, int, int> take)
    {
        var plan = RecipePlanner.Plan(item, missing, [], BuildBlocks.HasVariants(item) ? dmg : -1);
        var raws = new List<string>();
        int secs = 8 + plan.Steps.Count * 3;
        foreach (var m in plan.Missing) secs += RawShort(m.Id, m.Count, site, take, raws);
        var text = "craft" + (raws.Count > 0 ? " from " + string.Join(" + ", raws) : "");
        if (raws.Any(r => r.EndsWith("(ask)", StringComparison.Ordinal))) text = "ask: " + text;
        return (text, secs);
    }

    /// <summary>One missing recipe input: what's still short after the pool (added to <paramref name="raws"/>), and its seconds.</summary>
    private static int RawShort(int id, int count, SiteContext site, Func<int, int, int> take, List<string> raws)
    {
        int short_ = count - take(id, count);
        if (id == Data.Blocks.Planks || id == Items.Stick) return LogsFor(id, short_, site, take, raws);
        if (short_ <= 0) return 0;
        if (id == Data.Blocks.Log)
        {
            raws.Add(site.ChoppableTrees > 0 ? $"{short_} logs from trees" : $"{short_} logs (ask)");
            return 60 * ((short_ + 4) / 5);
        }
        if (id == Data.Blocks.Cobblestone)
        {
            raws.Add(site.GeneratorKnown ? $"{short_} gen cobble" : $"{short_} cobble (ask)");
            return (int)(short_ * 1.6);
        }
        raws.Add($"{short_} {Items.Get(id).Name.Replace('_', ' ')} (ask)");
        return 0;
    }

    /// <summary>planks/sticks: from logs (4 planks per log).</summary>
    private static int LogsFor(int id, int short_, SiteContext site, Func<int, int, int> take, List<string> raws)
    {
        int planks = id == Items.Stick ? (short_ + 1) / 2 : short_;
        int logs = (planks - take(Data.Blocks.Planks, planks) + 3) / 4;
        logs -= take(Data.Blocks.Log, logs);
        if (logs <= 0) return 0;
        raws.Add(site.ChoppableTrees > 0 ? $"{logs} logs from trees" : $"{logs} logs (ask)");
        return 60 * ((logs + 4) / 5);
    }
}
