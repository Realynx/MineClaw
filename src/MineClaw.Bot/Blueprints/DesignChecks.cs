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
        if (Doors > 0)
        {
            var drawn = string.Join("/", (DrawnDoorSides.Count > 0 ? DrawnDoorSides : DoorSides).Distinct().Select(d => d.Name()));
            var world = string.Join("/", DoorSides.Distinct().Select(d => d.Name()));
            sb.Append(CultureInfo.InvariantCulture, $", {Doors} door{(Doors > 1 ? "s" : "")} {drawn}{(DrawnDoorSides.Count > 0 && drawn != world ? $" (faces {world} once turned)" : "")}");
        }
        if (Windows > 0) sb.Append(CultureInfo.InvariantCulture, $", {Windows} window blocks");
        if (Interior > 0) sb.Append(CultureInfo.InvariantCulture, $", {Interior} interior cells");
        var errs = Issues.Where(i => i.Level == IssueLevel.Error).Select(i => i.ToString()).ToList();
        var warns = Issues.Where(i => i.Level == IssueLevel.Warning).Select(i => i.ToString()).ToList();
        if (errs.Count > 0) sb.Append(" | errors: ").Append(string.Join("; ", errs.Take(5))).Append(errs.Count > 5 ? $" +{errs.Count - 5}" : "");
        if (warns.Count > 0) sb.Append(" | warn: ").Append(string.Join("; ", warns.Take(3))).Append(warns.Count > 3 ? $" +{warns.Count - 3}" : "");
        if (Bill.Count > 0) sb.Append(" | bill: ").Append(BillText(4));
        if (!string.IsNullOrWhiteSpace(critique)) sb.Append(" | look: ").Append(critique.Trim());
        var s = sb.ToString();
        return s.Length <= maxChars ? s : s[..(maxChars - 1)] + "…";
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
public static class DesignChecks
{
    public static DesignEvaluation Evaluate(VoxelPlan plan, SiteContext site, IEnumerable<BlueprintIssue>? structural = null)
    {
        var e = new DesignEvaluation();
        if (structural != null) e.Issues.AddRange(structural.Where(i => i.Level != IssueLevel.Info));
        var w = site.World;
        e.Blocks = plan.Blocks.Values.Count(b => !b.UpperDoor);
        void Add(IssueLevel l, string code, string text) => e.Issues.Add(new BlueprintIssue(l, code, text));
        string L(BlockPos p) => plan.Local(p);
        string List(IEnumerable<BlockPos> ps)
        {
            var l = ps.ToList();
            return string.Join(" ", l.Take(3).Select(L)) + (l.Count > 3 ? $" +{l.Count - 3}" : "");
        }
        bool Solid(BlockPos p) => BuildBlocks.Supports(plan.Final(w, p));

        // ---- site loaded / overlap / obstruction / liquids
        var unloaded = plan.Blocks.Keys.Concat(plan.Air).Where(p => !w.IsLoaded(p.X, p.Z)).Select(p => (p.X, p.Z)).Distinct().Count();
        if (unloaded > 0) Add(IssueLevel.Warning, "unknown", $"{unloaded} columns of the site aren't loaded (unknown ground)");
        var never = new List<BlockPos>();
        var guarded = new Dictionary<string, List<BlockPos>>();
        var clear = new List<BlockPos>();
        var wet = new List<BlockPos>();
        foreach (var p in plan.Blocks.Keys.Concat(plan.Air))
        {
            if (!w.IsLoaded(p.X, p.Z)) continue;
            int id = w.GetBlock(p.X, p.Y, p.Z);
            if (id == 0) continue;
            if (plan.Blocks.TryGetValue(p, out var want) && SameState(want, id, w.GetMeta(p.X, p.Y, p.Z))) continue;
            if (Data.Blocks.IsWater(id) || Data.Blocks.IsLava(id)) { wet.Add(p); continue; }
            if (!Data.Blocks.Get(id).Solid && id is not (Data.Blocks.Torch or Data.Blocks.Ladder or Data.Blocks.WoodenDoor)) { clear.Add(p); continue; } // grass, flowers
            var v = site.Verdict?.Invoke(p, id) ?? ProtectionVerdict.None;
            if (v.Level == ProtectionLevel.Hard) never.Add(p);
            else if (v.Protected)
            {
                var why = string.IsNullOrWhiteSpace(v.Why) ? "a protected block" : v.Why;
                if (!guarded.TryGetValue(why, out var list)) guarded[why] = list = new();
                list.Add(p);
            }
            else clear.Add(p);
        }
        if (never.Count > 0) Add(IssueLevel.Error, "never_break", $"overlaps {never.Count} never-break block(s) at {List(never)}: move or shrink it");
        foreach (var (why, list) in guarded) Add(IssueLevel.Error, "protected", $"overlaps {list.Count} protected block(s) ({why}) at {List(list)}: move it");
        if (wet.Count > 0) Add(IssueLevel.Error, "liquid", $"water/lava in {wet.Count} cell(s) at {List(wet)}: move it away from the gen/fluids");
        e.ToClear = clear.Count;
        if (clear.Count > 0) Add(IssueLevel.Warning, "obstruction", $"{clear.Count} existing block(s) to clear at {List(clear)}");

        // ---- support: every planned block connects through solids to existing ground
        var placed = plan.Blocks.Values.ToList();
        var reached = new HashSet<BlockPos>();
        var queue = new Queue<BlockPos>();
        foreach (var b in placed)
            if (BlockFaceExtensions.All.Select(f => b.Pos.Offset(f)).Any(n => !plan.Blocks.ContainsKey(n) && !plan.Air.Contains(n) && BuildBlocks.Supports(w.GetBlock(n.X, n.Y, n.Z))))
                if (reached.Add(b.Pos)) queue.Enqueue(b.Pos);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!BuildBlocks.Supports(plan.Blocks[p].Id) && plan.Blocks[p].Kind != BuildKind.Door) continue; // torches/panes hold nothing up
            foreach (var f in BlockFaceExtensions.All)
            {
                var n = p.Offset(f);
                if (plan.Blocks.ContainsKey(n) && reached.Add(n)) queue.Enqueue(n);
            }
        }
        var floating = placed.Where(b => !reached.Contains(b.Pos)).Select(b => b.Pos).ToList();
        if (floating.Count > 0) Add(IssueLevel.Error, "floating", $"{floating.Count} block(s) float, touching nothing that stands: {List(floating)}");
        var falls = placed.Where(b => b.Kind == BuildKind.Gravity && !Solid(b.Pos.Down())).Select(b => b.Pos).ToList();
        if (falls.Count > 0) Add(IssueLevel.Error, "gravity", $"sand/gravel with nothing under it falls: {List(falls)}");

        // ---- void: planned blocks with nothing at all below them
        var overVoid = new List<BlockPos>();
        foreach (var col in placed.GroupBy(b => (b.Pos.X, b.Pos.Z)))
        {
            var low = col.OrderBy(b => b.Pos.Y).First().Pos;
            bool any = false;
            for (int y = low.Y - 1; y >= 0 && !any; y--) any = plan.Final(w, new BlockPos(low.X, y, low.Z)) != 0;
            if (!any && w.IsLoaded(low.X, low.Z)) overVoid.Add(low);
        }
        if (overVoid.Count > 0) Add(IssueLevel.Warning, "void", $"{overVoid.Count} column(s) hang over the void ({List(overVoid)}): needs scaffold from the edge");

        // ---- doors, attachments
        foreach (var d in placed.Where(b => b.Kind == BuildKind.Door && !b.UpperDoor))
        {
            e.Doors++;
            var up = d.Pos.Up();
            if (!plan.Blocks.TryGetValue(up, out var top) || top.Id != d.Id || (top.Meta & 8) == 0)
                Add(IssueLevel.Error, "door", $"door at {L(d.Pos)} has no upper half");
            if (!BuildBlocks.NormalCube(plan.Final(w, d.Pos.Down())))
                Add(IssueLevel.Error, "door", $"door at {L(d.Pos)} needs a full block under it (it is {Data.Blocks.Get(plan.Final(w, d.Pos.Down())).Name})");
            var side = DoorOutside(plan, w, d) ?? BuildBlocks.DoorPlacerFacing(d.Meta).Opposite();
            e.DoorSides.Add(side);
            e.DrawnDoorSides.Add(side.Turn(-plan.Blueprint.Quarters)); // the drawing → world turn undone (facing north: south ↔ north)
        }
        foreach (var u in placed.Where(b => b.UpperDoor))
            if (!plan.Blocks.TryGetValue(u.Pos.Down(), out var lo) || lo.Id != u.Id || (lo.Meta & 8) != 0)
                Add(IssueLevel.Error, "door", $"door top half at {L(u.Pos)} has no lower half");
        foreach (var t in placed.Where(b => b.Id == Data.Blocks.Torch))
            if (!new[] { t.Pos.Down(), t.Pos.Offset(1, 0, 0), t.Pos.Offset(-1, 0, 0), t.Pos.Offset(0, 0, 1), t.Pos.Offset(0, 0, -1) }.Any(n => BuildBlocks.NormalCube(plan.Final(w, n))))
                Add(IssueLevel.Error, "torch", $"torch at {L(t.Pos)} has no wall or floor to stand on");
        foreach (var l in placed.Where(b => b.Id == Data.Blocks.Ladder))
        {
            var wall = l.Meta switch { 2 => l.Pos.Offset(0, 0, 1), 3 => l.Pos.Offset(0, 0, -1), 4 => l.Pos.Offset(1, 0, 0), _ => l.Pos.Offset(-1, 0, 0) };
            if (!BuildBlocks.NormalCube(plan.Final(w, wall))) Add(IssueLevel.Error, "ladder", $"ladder at {L(l.Pos)} has no wall behind it");
        }
        e.Windows = placed.Count(b => b.Id is Data.Blocks.Glass or Data.Blocks.GlassPane);

        // ---- interior, entrances, headroom, light
        Interior(plan, w, e, L, List, site.WantsLight || plan.Blueprint.Light, plan.Blueprint.Light);

        // ---- bill
        Bill(plan, site, e);
        if (e.MaterialsUnobtainable)
            Add(IssueLevel.Warning, "materials", "some materials can't be gathered here: " + string.Join(", ", e.Bill.Where(b => b.Missing > 0 && b.Source.StartsWith("ask", StringComparison.Ordinal)).Select(b => b.Name)));
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
        var min = plan.Min;
        var max = plan.Max;
        bool Standable(BlockPos p) => Passable(plan.Final(w, p)) && Passable(plan.Final(w, p.Up())) && BuildBlocks.Supports(plan.Final(w, p.Down()))
                                      && plan.Final(w, p.Down()) is not (Data.Blocks.Fence or Data.Blocks.FenceGate) && w.IsLoaded(p.X, p.Z);
        bool InFoot(BlockPos p) => p.X >= min.X && p.X <= max.X && p.Z >= min.Z && p.Z <= max.Z;
        bool Covered(BlockPos p)
        {
            for (int y = p.Y + 1; y <= max.Y; y++)
                if (plan.Blocks.TryGetValue(new BlockPos(p.X, y, p.Z), out var b) && BuildBlocks.Supports(b.Id) && b.Kind != BuildKind.Door) return true;
            return false;
        }
        var interior = new List<BlockPos>();
        var low = new List<BlockPos>();
        for (int y = min.Y; y <= max.Y; y++)
            for (int z = min.Z; z <= max.Z; z++)
                for (int x = min.X; x <= max.X; x++)
                {
                    var p = new BlockPos(x, y, z);
                    if (plan.Blocks.TryGetValue(p, out var pb) && pb.Kind == BuildKind.Door) continue;
                    if (!Passable(plan.Final(w, p)) || !BuildBlocks.Supports(plan.Final(w, p.Down())) || !Covered(p)) continue;
                    if (Passable(plan.Final(w, p.Up()))) interior.Add(p);
                    // A stair overhead is the underside of a flight (live #8: 1,0,4 under the step at 1,1,4): walkers climb that
                    // step, nobody stands beneath it, so it isn't a headroom problem.
                    else if (plan.Blocks.TryGetValue(p.Up(), out var ceiling) && ceiling.Kind != BuildKind.Stairs) low.Add(p);
                }
        e.Interior = interior.Count;
        e.LowHeadroom = low.Count;
        if (low.Count > 0)
            e.Issues.Add(new(interior.Count == 0 ? IssueLevel.Error : IssueLevel.Warning, "headroom", $"headroom 1 at {low.Count} interior cell(s) ({list(low)}): players need 2"));
        if (interior.Count == 0) return;

        // Reachability: walk from standable cells outside the footprint (2-block ring) into the build.
        var reached = new HashSet<BlockPos>();
        var q = new Queue<BlockPos>();
        for (int y = min.Y - 1; y <= max.Y; y++)
            for (int z = min.Z - 2; z <= max.Z + 2; z++)
                for (int x = min.X - 2; x <= max.X + 2; x++)
                {
                    var p = new BlockPos(x, y, z);
                    if (InFoot(p) || !Standable(p)) continue;
                    if (reached.Add(p)) q.Enqueue(p);
                }
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                foreach (int dy in new[] { 0, 1, -1 })
                {
                    var n = new BlockPos(p.X + dx, p.Y + dy, p.Z + dz);
                    if (n.X < min.X - 2 || n.X > max.X + 2 || n.Z < min.Z - 2 || n.Z > max.Z + 2 || n.Y < min.Y - 1 || n.Y > max.Y) continue;
                    if (dy == 1 && !Passable(plan.Final(w, p.Up(2)))) continue; // jumping needs head room
                    if (dy == -1 && !Passable(plan.Final(w, n.Up(2)))) continue;
                    if (!Standable(n) || !reached.Add(n)) continue;
                    q.Enqueue(n);
                }
        }
        var cut = interior.Where(p => !reached.Contains(p)).ToList();
        e.Unreachable = cut.Count;
        if (cut.Count == interior.Count) e.Issues.Add(new(IssueLevel.Error, "entrance", $"no entrance: the {interior.Count} interior cells can't be reached from outside (add a door or a 2-high opening)"));
        else if (cut.Count > 0) e.Issues.Add(new(IssueLevel.Warning, "entrance", $"{cut.Count} interior cell(s) can't be reached ({list(cut)})"));

        if (!light) return;
        var lights = plan.Blocks.Values.Where(b => b.Id is Data.Blocks.Torch or Data.Blocks.Glowstone or Data.Blocks.JackOLantern)
            .Select(b => (b.Pos, Level: b.Id == Data.Blocks.Torch ? 14 : 15)).ToList();
        var dark = interior.Where(p => !lights.Any(l => l.Level - l.Pos.ManhattanTo(p) >= 8)).ToList();
        if (dark.Count > 0)
            e.Issues.Add(new(lightRequired ? IssueLevel.Error : IssueLevel.Warning, "light", $"{dark.Count} interior cell(s) stay dark (light < 8: mobs spawn) e.g. {list(dark)}: add torches"));
    }

    /// <summary>Needs per item vs inventory + known chests; missing cobble from the gen, wood from logs/trees, crafted items from their recipe.</summary>
    public static void Bill(VoxelPlan plan, SiteContext site, DesignEvaluation e)
    {
        var need = new Dictionary<(int Item, int Damage), int>();
        foreach (var b in plan.Blocks.Values)
        {
            if (b.UpperDoor) continue;
            int id = site.World.IsLoaded(b.Pos.X, b.Pos.Z) ? site.World.GetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z) : 0;
            if (id == b.Id && SameState(b, id, site.World.GetMeta(b.Pos.X, b.Pos.Y, b.Pos.Z))) continue; // already there
            var key = BuildBlocks.ItemFor(b.Id, b.Meta);
            need[key] = need.GetValueOrDefault(key) + 1;
        }
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
        // Crafted (planks, stairs, slabs, doors, panes, torches...): what raw input is still short after what's on hand.
        var plan = RecipePlanner.Plan(item, missing, [], BuildBlocks.HasVariants(item) ? dmg : -1);
        var raws = new List<string>();
        int secs = 8 + plan.Steps.Count * 3;
        foreach (var m in plan.Missing)
        {
            int short_ = m.Count - take(m.Id, m.Count);
            if (m.Id == Data.Blocks.Planks || m.Id == Items.Stick)
            {
                // planks/sticks: from logs (4 planks per log)
                int planks = m.Id == Items.Stick ? (short_ + 1) / 2 : short_;
                int logs = (planks - take(Data.Blocks.Planks, planks) + 3) / 4;
                logs -= take(Data.Blocks.Log, logs);
                if (logs > 0) { raws.Add(site.ChoppableTrees > 0 ? $"{logs} logs from trees" : $"{logs} logs (ask)"); secs += 60 * ((logs + 4) / 5); }
                continue;
            }
            if (short_ <= 0) continue;
            if (m.Id == Data.Blocks.Log) { raws.Add(site.ChoppableTrees > 0 ? $"{short_} logs from trees" : $"{short_} logs (ask)"); secs += 60 * ((short_ + 4) / 5); }
            else if (m.Id == Data.Blocks.Cobblestone) { raws.Add(site.GeneratorKnown ? $"{short_} gen cobble" : $"{short_} cobble (ask)"); secs += (int)(short_ * 1.6); }
            else raws.Add($"{short_} {Items.Get(m.Id).Name.Replace('_', ' ')} (ask)");
        }
        var text = "craft" + (raws.Count > 0 ? " from " + string.Join(" + ", raws) : "");
        if (raws.Any(r => r.EndsWith("(ask)", StringComparison.Ordinal))) text = "ask: " + text;
        return (text, secs);
    }
}
