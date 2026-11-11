using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// The build queue (docs/web/story-mode.md "Construction", "Building fast"): items expand to ghost blocks; a pawn whose Build priority
/// is on gets a batch of them (one item, one stand spot, one fetch), each cell leased to it until it reports that cell. Order: queue
/// order, then bottom-up (support first), then blocks touching something solid, then nearest, away from other builders. A pawn
/// fetches the material from storage (or uses its own); none anywhere → the build is blocked with "need N item" and an alert until
/// some arrives. Never a block where a player (pawn or not), a monster or an animal stands.
/// </summary>
public sealed partial class StoryColony
{
    private GhostState? FindGhost(string? key) => FindCell(key)?.G;

    /// <summary>The ghost behind a cell id (<c>build|x,y,z</c>) and its build, or null; cells compared as numbers (a mine build's
    /// thousands of cells were each formatted as text per lookup).</summary>
    private (GhostState G, BuildState B)? FindCell(string? key)
    {
        if (key == null) return null;
        var parts = key.Split('|');
        if (parts.Length != 2 || ParseCell(parts[1]) is not { } at) return null;
        var b = Builds.FirstOrDefault(x => x.Id == parts[0]);
        if (b == null) return null;
        foreach (var g in b.Ghosts)
            if (g.At == at) return g.At.ToString() == parts[1] ? (g, b) : null; // the id as this cell writes it, not just the same numbers
        return null;
    }

    /// <summary>add {items} | remove {ids} | reorder {ids} | pause {ids} | resume {ids}.</summary>
    public (JsonNode? Result, string? Error) BuildsOp(string op, JsonArray? items, IReadOnlyList<string>? ids)
    {
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            MaterialsChanged(); // the queue changes: reservations and auto bills follow
            return op switch
            {
                "add" => AddBuilds(w, items),
                "preview" => LiquidPreview(w, items), // a water item's cells checked and its expected flow, nothing queued (StoryColony.Water.cs)
                "remove" or "pause" or "resume" => ToggleBuilds(op, ids),
                "reorder" => ReorderBuilds(ids),
                _ => (null, "op must be add|preview|remove|reorder|pause|resume"),
            };
        }
    }

    /// <summary>Each item expanded and checked (all or none), then queued after the builds there are.</summary>
    private (JsonNode? Result, string? Error) AddBuilds(IStoryWorld w, JsonArray? items)
    {
        if (items is not { Count: > 0 }) return (null, "items required");
        if (items.Count > 64) return (null, "at most 64 items per request");
        var made = new List<BuildState>();
        var warnings = new List<string>();
        foreach (var n in items)
            if (ExpandBuildItem(w, n, made, warnings) is { } err) return (null, err);
        if (PrepareDesignReplacement(w, made) is { } replaceError) return (null, replaceError);
        ReplaceOldDesignCells(made);
        var flow = QueueBuilds(w, made);
        ChestRuleCells(w, made.SelectMany(b => b.Ghosts.Select(g => (b, g))), warnings); // chests the game can't place: blocked now, said
        ShutChestsSaid(w, made, warnings);
        BuildConflictsPass();
        BuildsInQuarries(made, warnings); // a quarry digs them: blocked now, said (StoryColony.QuarryConflicts.cs)
        BuildsOverPreserved(made, warnings); // the build's while it stands, then kept as it leaves them (StoryColony.Preserve.cs)
        WakeIdle();
        var added = new JsonObject { ["ids"] = new JsonArray(made.Select(b => (JsonNode)b.Id!).ToArray()) };
        if (warnings.Count > 0) added["warnings"] = new JsonArray(warnings.Distinct().Select(x => (JsonNode)x!).ToArray());
        if (flow != null) added["flow"] = flow;
        return (added, null);
    }

    private string? ExpandBuildItem(IStoryWorld w, JsonNode? n, List<BuildState> made, List<string> warnings)
    {
        if (n is not JsonObject item) return "each item is an object {kind, area|blocks, material, ...}";
        var (ghosts, mat, err) = StoryConstruction.Expand(w.Data, item, w);
        if (err != null) return err;
        if (ghosts.Count > 4096) return "an item expands to at most 4096 blocks";
        // Liquids: the cells must take the water (open, or dug first by the item's group), lava only when allowed (StoryColony.Water.cs).
        if (ghosts.Count > 0 && StoryConstruction.IsLiquid(ghosts[0]) && CheckLiquid(w, item, ghosts, made, warnings) is { } lerr) return lerr;
        made.Add(new BuildState
        {
            Kind = item["kind"]!.GetValue<string>().ToLowerInvariant(), Spec = (JsonObject)item.DeepClone(), Ghosts = ghosts,
            Material = mat?.Name ?? "", Created = Now,
        });
        return null;
    }

    /// <summary>The new builds join the queue in order; the last liquid build's expected flow, or null.</summary>
    private JsonObject? QueueBuilds(IStoryWorld w, List<BuildState> made)
    {
        int order = Builds.Count == 0 ? 0 : Builds.Max(b => b.Order) + 1;
        JsonObject? flow = null;
        foreach (var b in made) flow = QueueBuild(w, b, order++, flow);
        return flow;
    }

    private JsonObject? QueueBuild(IStoryWorld w, BuildState b, int order, JsonObject? flow)
    {
        b.Id = NextId("b");
        b.Order = order;
        b.Label = $"{b.Kind} ({b.BlocksTotal} block{(b.BlocksTotal == 1 ? "" : "s")}{(b.Material.Length > 0 && b.Kind != "remove" ? ", " + b.Material : "")})";
        var r = Touch();
        b.Rev = r;
        var temps = ReplacementBuild(b) ? ActiveTempCells() : null;
        foreach (var g in b.Ghosts)
        {
            MarkBuiltAlready(w, g, r);
            if (!g.Remove && temps?.Contains(g.At) == true) g.State = "todo"; // the access owner still has to recover this block
        }
        Builds.Add(b);
        if (b.Ghosts.Count > 0 && StoryConstruction.IsLiquid(b.Ghosts[0])) flow = LiquidAdded(w, b, flow); // its label, the expected flow
        AlertUnplaceable(b);
        return flow;
    }

    /// <summary>Already there (a wall over a finished one; a dig where it is open): done at once.</summary>
    private static void MarkBuiltAlready(IStoryWorld w, GhostState g, long rev)
    {
        g.Rev = rev;
        var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (!g.Remove && id == g.Id && (g.Meta == meta || !g.Job)) g.State = "done";
        if (g.Remove && id == 0 && w.Loaded(g.At.X, g.At.Z)) g.State = "done";
    }

    /// <summary>Blocks the pawns can't place yet (crops, liquids, signs…): shown as ghosts and said once, never a stuck job.</summary>
    private void AlertUnplaceable(BuildState b)
    {
        if (b.Ghosts.Where(g => g.State == "unsupported").ToList() is not { Count: > 0 } odd) return;
        Alert("warn", $"Build {b.Id} ({b.Kind}): {odd.Count} block{(odd.Count == 1 ? "" : "s")} the pawns can't place yet, left as ghosts: "
                      + string.Join("; ", odd.Select(g => g.Reason ?? "special placement").Distinct().Take(3)));
    }

    /// <summary>
    /// The new chest cells (of <paramref name="cells"/>, with their builds) the game would refuse are blocked from the start with the
    /// rule's reason (Fox 11-10: such a design is still queued), said in <paramref name="warnings"/> and once per build as an alert; no
    /// pawn is sent to them. Other builds' chest cells still to come count as chests (StoryConstruction.Chests.cs).
    /// </summary>
    private void ChestRuleCells(IStoryWorld w, IEnumerable<(BuildState B, GhostState G)> cells, List<string>? warnings)
    {
        var mine = new Dictionary<GhostState, BuildState>(ReferenceEqualityComparer.Instance);
        var at = new HashSet<P3>();
        foreach (var (b, g) in cells)
        {
            at.Add(g.At);
            if (g.State == "todo" && StoryConstruction.IsChest(g)) mine[g] = b;
        }
        if (mine.Count == 0) return;
        var refused = StoryConstruction.UnbuildableChests(mine.Keys, w, ChestsToCome(new Dictionary<P3, int>(), at));
        foreach (var hit in refused.GroupBy(r => mine[r.Ghost]))
        {
            var b = hit.Key;
            foreach (var (g, clash) in hit)
            {
                g.State = "blocked";
                g.Reason = StoryConstruction.ChestRuleReason(clash.Words, g.Id, Modern);
                g.Rev = Touch();
            }
            int n = hit.Count();
            string where = string.Join("; ", hit.Take(3).Select(r => r.Ghost.At)) + (n > 3 ? "; …" : "");
            string text = $"{n} chest{(n == 1 ? "" : "s")} left blocked at {where}: {hit.First().Ghost.Reason}";
            warnings?.Add($"build {b.Id}: {text}");
            if (_cellAlerted.Add($"{b.Id}|{StoryConstruction.ChestRule}")) Alert("warn", $"Build {b.Id}: {text}");
        }
    }

    /// <summary>Every build's chest cells still to place, but not at <paramref name="except"/>, into <paramref name="into"/>: open, out
    /// with a pawn, or blocked for a reason a later try may mend (not the chest rule's, not a newer build's or a quarry's).</summary>
    private Dictionary<P3, int> ChestsToCome(Dictionary<P3, int> into, HashSet<P3>? except = null)
    {
        foreach (var b in Builds)
            foreach (var g in b.Ghosts)
                if (StoryConstruction.IsChest(g) && except?.Contains(g.At) != true
                    && (g.State is "todo" or "leased" || g.State == "blocked" && !Conflicted(g) && !StoryConstruction.ChestRuleHeld(g)))
                    into[g.At] = g.Id;
        return into;
    }

    /// <summary>The request's chests under a full opaque block won't open (both versions): said, nothing blocked.</summary>
    private static void ShutChestsSaid(IStoryWorld w, List<BuildState> made, List<string> warnings)
    {
        var shut = StoryConstruction.ShutChests(made.SelectMany(b => b.Ghosts), w);
        if (shut.Count == 0) return;
        warnings.Add($"{shut.Count} chest{(shut.Count == 1 ? "" : "s")} of this design sit under a solid block and will not open ("
                     + string.Join("; ", shut.Take(3)) + (shut.Count > 3 ? "; …" : "") + ")");
    }

    private (JsonNode? Result, string? Error) ToggleBuilds(string op, IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 }) return (null, "ids required");
        var hit = Builds.Where(b => ids.Contains(b.Id)).ToList();
        if (hit.Count == 0) return (null, "no such build");
        if (op == "remove" && hit.FirstOrDefault(b => b.Kind == "preserve") is { } keep)
            return (null, $"{keep.Id} repairs a preserved zone: switch Preserve off in that zone's settings instead");
        foreach (var b in hit)
        {
            if (op == "remove") { RemoveBuild(b); continue; }
            if (b.Status != "done" || b.Kind == "mine") b.Status = op == "pause" ? "paused" : "queued";
            b.Rev = Touch();
        }
        OreTick();
        BuildConflictsPass();
        return (new JsonObject { ["ids"] = new JsonArray(hit.Select(b => (JsonNode)b.Id!).ToArray()) }, null);
    }

    /// <summary>
    /// A removed build lets go of everything it held: the pawns out on its cells stop (their batch would go on digging or placing a
    /// build that is gone: live 11-11, a deleted 47,000-cell strip mine), its leases, its cells' rests and failure counts, its mine
    /// index, its blocked cells' probes.
    /// </summary>
    private void RemoveBuild(BuildState b)
    {
        Builds.Remove(b);
        _mineCells.Remove(b);
        Removed("build", b.Id);
        _buildAlerted.Remove(b.Id);
        ForgetProbes(b); // its blocked cells' reasons and tries go with it (StoryColony.Reconcile.cs)
        string lease = "g:" + b.Id + "|", cell = b.Id + "|";
        foreach (var (name, job) in _current.ToList())
            if (job.Source == "ghost" && job.SourceId?.StartsWith(cell, StringComparison.Ordinal) == true && Pawns.TryGetValue(name, out var p))
            {
                Supersede(p, "build removed");
                p.Rev = Touch();
            }
        ForgetKeys(_leases, lease);
        ForgetKeys(_skip, lease);
        ForgetKeys(_leaseFails, lease);
        ForgetKeys(_softFails, lease);
        ForgetKeys(_ghostFails, cell);
    }

    /// <summary>The entries of a keyed record whose key starts with <paramref name="prefix"/>, removed.</summary>
    private static void ForgetKeys<T>(Dictionary<string, T> map, string prefix)
    {
        if (map.Count == 0) return;
        List<string>? gone = null;
        foreach (var k in map.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) (gone ??= new()).Add(k);
        if (gone != null) foreach (var k in gone) map.Remove(k);
    }

    /// <summary>The named ones first, in that order; the rest keep their relative order after them.</summary>
    private (JsonNode? Result, string? Error) ReorderBuilds(IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 }) return (null, "ids required");
        if (ids.Any(i => Builds.All(b => b.Id != i))) return (null, "unknown build id");
        var rest = Builds.Where(b => !ids.Contains(b.Id)).OrderBy(b => b.Order).ToList();
        int o = 0;
        foreach (var id in ids.Distinct()) { var b = Builds.First(x => x.Id == id); b.Order = o++; b.Rev = Touch(); }
        foreach (var b in rest) { b.Order = o++; b.Rev = Touch(); }
        return (new JsonObject { ["ids"] = new JsonArray(Builds.OrderBy(b => b.Order).Select(b => (JsonNode)b.Id!).ToArray()) }, null);
    }

    private bool Solid(P3 p) => World is { } w && w.Block(p.X, p.Y, p.Z).Id is var id && !StoryBlocks.Replaceable.Contains(id);

    /// <summary>A body is in the cell (a player, a monster or animal, another pawn: the server refuses a block into any of them). The
    /// picking pawn's own body is not in the way: it places from its stand, and steps out of the cell first ("I'm in the way of").</summary>
    private bool Occupied(P3 at)
    {
        foreach (var (name, x, y, z) in Bodies())
            if (InBox(at, x, y, z) && !string.Equals(name, _pickPawn, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Cells one build job lays at most (docs/web/story-mode.md "Building fast").</summary>
    public const int BatchMax = 24;
    /// <summary>How far (eyes to a cell's centre) a batch's cells may be from its stand: well under the client's reach (4.3 to a
    /// support face, which can be farther than the centre), so the place tool never has to walk (FakeServer: one that did walked
    /// into a hut and was walled in).</summary>
    public const double BuildReach = 3.8;
    /// <summary>A batch's stand is chosen for the cells within this many blocks (horizontally) of its first.</summary>
    private const int StandRadius = 3;
    /// <summary>A batch's cells lie within this many blocks (horizontally) of its first (the pawn steps along for the farther ones).</summary>
    private const int WalkRadius = 6;

    /// <summary>
    /// The next build job for this pawn over the queue, or null; marks builds blocked when their material is nowhere. A job is a
    /// batch: up to <see cref="BatchMax"/> cells of one item around the first open cell, all reachable from one stand spot, ordered
    /// so each has support when its turn comes (bottom-up, outward from what is solid), with one fetch of what the batch needs.
    /// Several builders split a build into disjoint batches (each cell leased to one pawn); the batch size is the pawn's fair share
    /// of what is open, so ten pawns on a floor each get a part.
    /// </summary>
    /// <summary>The blocked reason each build last alerted (a build that turns active between gets no second alert for it).</summary>
    private readonly Dictionary<string, string> _buildBlockSaid = new(StringComparer.Ordinal);

    private ActiveJob? GhostJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        EnsureMaterials(); // what each build may take (reserved in queue order) and what is being crafted for it
        var from = live.Block;
        int builders = BuilderCount();
        int cap = BuildCap(p, live); // down in a quarry pit: the cells up top are a builder's up there (StoryColony.QuarryConflicts.cs)
        var queue = OpenBuildsInOrder();
        try
        {
            foreach (var (b, _) in queue)
                if (BuildJobFor(w, p, live, b, builders, from, cap) is { } job) return job;
            return null;
        }
        finally
        {
            queue.Clear();
            _buildQueue = queue;
        }
    }

    /// <summary>The list <see cref="OpenBuildsInOrder"/> fills (taken while in use).</summary>
    private List<(BuildState B, int At)>? _buildQueue = new();

    /// <summary>The builds not paused or done, in queue order (equal orders as they stand in the list), without a new list per pick.</summary>
    private List<(BuildState B, int At)> OpenBuildsInOrder()
    {
        var queue = _buildQueue ?? new();
        _buildQueue = null;
        for (int i = 0; i < Builds.Count; i++)
            if (Builds[i].Status is not ("paused" or "done")) queue.Add((Builds[i], i));
        CollectionsMarshal.AsSpan(queue).Sort(static (x, y) => x.B.Order != y.B.Order ? x.B.Order.CompareTo(y.B.Order) : x.At.CompareTo(y.At));
        return queue;
    }

    /// <summary>Pawns that build now (build work enabled, not drafted, in game): each builder's fair share is the open cells over them.</summary>
    private int BuilderCount()
    {
        int builders = 0;
        foreach (var o in Pawns.Values)
            if (o.Priorities.GetValueOrDefault(WorkTypes.Build) is >= 1 and <= 4 && !o.Drafted && Host.Live(o.Name).Online) builders++;
        return Math.Max(1, builders);
    }

    /// <summary>One build's scan for a pawn's next batch: the build, its open work, and what the cells tried so far found.</summary>
    private struct GhostScan
    {
        public BuildState Build;
        /// <summary>Its open work; null for a mine (<see cref="TodoOf"/>).</summary>
        public List<GhostState>? Todo;
        public int Builders;
        public P3 From;
        public bool Mine;
        /// <summary>Items found out (no stock, none carried): their other cells are skipped.</summary>
        public HashSet<string>? OutOf;
        /// <summary>The first material shortage: the build's status and reason, whether to alert, the blocked-work note.</summary>
        public (string Status, string Reason, bool Alert, string Note)? Held;
        /// <summary>Another builder carries this build's items (its batch is in flight).</summary>
        public bool InFlight;
        /// <summary>The build waits (for water): no more cells, and no blocked note of its own.</summary>
        public bool SkipBuild;
        /// <summary>Open cells of the level with nothing to place against yet (they wait for the blocks beside them).</summary>
        public int Unsupported;
        /// <summary>A floating level's seed and the spot its scaffold block goes (dug again once the cell stands).</summary>
        public (P3 Seed, P3 Spot)? Scaffold;
        /// <summary>The first free cell passed over for a forbidden zone, for a spot where a pawn got stuck, or for a pawn's
        /// temporary block standing in it (the blocked-work note names the hold).</summary>
        public P3? Forbid, Stuck, Temp;
    }

    /// <summary>This build's next job for the pawn, or null (and why it has none, as blocked work).</summary>
    private ActiveJob? BuildJobFor(IStoryWorld w, PawnState p, PawnLive live, BuildState b, int builders, P3 from, int cap = int.MaxValue)
    {
        if (b.Kind == "mine") return MineJobFor(w, p, live, b, builders, from, cap, digs: false); // its blocks; its digs are mining (StoryColony.MineCells.cs)
        var todo = TodoCells(b);
        if (todo.Count == 0) { FinishOrStuck(b); return null; }
        // A design placed with a basement: its blocks wait until the ground is dug out (dig, then place).
        if (DigFirst(b)) { Blocked($"build {b.Id}: its ground is dug out first"); return null; }
        var scan = new GhostScan { Build = b, Todo = todo, Builders = builders, From = from };
        var open = CappedCells(b, OpenCells(b, todo), cap);
        if (OutOfView(w, open, from) is { } site)
        {
            if (SiteSupplied(w, live, ref scan, open)) return SiteVisit(p, b, site);
            NoteBuildHeld(b, ref scan, open.Count); // nothing to place there yet: no walk, the material wait is said as in view
            return null;
        }
        foreach (var g in CellOrder(b, open, ref scan, from))
        {
            if (GhostCell(w, p, live, ref scan, g) is { } job) return job;
            if (scan.SkipBuild) return null;
        }
        NoteBuildHeld(b, ref scan, open.Count);
        return null;
    }

    /// <summary>The build's open work: its job cells to do or out with a pawn.</summary>
    private static List<GhostState> TodoCells(BuildState b) => b.Ghosts.Where(g => g.Job && g.State is "todo" or "leased").ToList();

    /// <summary>The scan's open work (a mine's search doesn't list it: made here when a pour or place job needs it).</summary>
    private static List<GhostState> TodoOf(in GhostScan scan) => scan.Todo ?? TodoCells(scan.Build);

    /// <summary>No open cell left: done, unless some are blocked (those can't be placed: said as blocked work). Blocked cells a newer
    /// build took over are not that: with nothing else left the build is done (StoryColony.BuildConflicts.cs).</summary>
    private void FinishOrStuck(BuildState b)
    {
        if (SupersededOnly(b)) return;
        if (b.Ghosts.Count(g => g.Job && g.State == "blocked" && !BuildConflicted(g)) is var stuckCells and > 0) { Blocked($"build {b.Id}: {stuckCells} blocks can't be placed"); return; }
        b.Status = "done";
        b.Rev = Touch();
    }

    /// <summary>
    /// The cells open now. The lowest unfinished level may need a scaffold; higher placements are independent once they already
    /// have a real support face. An unreachable or material-starved cell must not hold another completed floor's furnishings.
    /// Pure demolition stays top-down. A mine opens a cell once a neighbour is open, from its entry outward and down
    /// (<see cref="MineJobFor"/>).
    /// The level is that of the cells still to do (open or out with a pawn): a blocked cell never pins it, so the digs under a
    /// blocked one go on and a floating cell over a blocked level gets its scaffold. Only water or lava in a design's empty space
    /// (<see cref="LiquidOver"/>) keeps the digs below its level closed: a pit dug under standing water floods.
    /// </summary>
    private List<GhostState> OpenCells(BuildState b, List<GhostState> todo)
    {
        if (todo.Count == 0) return [];
        int floor = LiquidOver(b)?.At.Y ?? int.MinValue;
        var left = floor == int.MinValue ? todo : todo.Where(g => !(g.Remove && g.At.Y < floor)).ToList();
        if (left.Count == 0) return [];
        bool digs = left.All(g => g.Remove);
        int lowest = digs ? left.Max(g => g.At.Y) : left.Min(g => g.At.Y);
        if (digs) return left.Where(g => g.State == "todo" && g.At.Y == lowest).ToList();
        return left.Where(g => g.State == "todo" && (g.At.Y == lowest || !g.Remove && FaceReady(g))).ToList();
    }

    /// <summary>The highest cell of the build's dig work that holds water or lava it can't clear (a design's empty space under a
    /// river: blocked "cannot clear water"), or null.</summary>
    private static GhostState? LiquidOver(BuildState b)
    {
        GhostState? top = null;
        foreach (var g in b.Ghosts)
            if (g.Remove && g.State == "blocked" && StoryBlocks.Liquids.Contains(g.Id) && g.Reason?.StartsWith(CannotClearWords, StringComparison.Ordinal) == true
                && (top == null || g.At.Y > top.At.Y)) top = g;
        return top;
    }

    /// <summary>The nearest open cell, when every open cell is a placement in a column no body has loaded (the colony's view reads it
    /// as air: nothing to place against and no scaffold spot, so no job would ever be made there), else null. A dig is handed out
    /// unloaded and its pawn's arrival loads the site.</summary>
    private static GhostState? OutOfView(IStoryWorld w, List<GhostState> open, P3 from)
    {
        GhostState? near = null;
        foreach (var g in open)
        {
            if (g.Remove || w.Loaded(g.At.X, g.At.Z)) return null;
            if (near == null || g.At.Dist(from) < near.At.Dist(from)) near = g;
        }
        return near;
    }

    /// <summary>
    /// Some open cell of a build out of view could go in on arrival: its item is carried, or stored and not reserved for an earlier
    /// build (as <see cref="PlaceCell"/> asks it), or it is a pour. Else each item that is out is noted (<see cref="OutOfItem"/>) and
    /// nobody walks there: a far build short of its chests sent a pawn there and back every <see cref="SiteRest"/> until they came.
    /// </summary>
    private bool SiteSupplied(IStoryWorld w, PawnLive live, ref GhostScan scan, List<GhostState> open)
    {
        var b = scan.Build;
        foreach (var g in open)
        {
            if (StoryConstruction.IsLiquid(g)) return true;
            var item = g.Item ?? w.Data.Item(g.Id).Name;
            if (scan.OutOf?.Contains(item) == true) continue;
            var ik = KeyOfItem(w, item, g.Id);
            if (live.Inventory.Any(s => s.Id == ik.Id && s.Meta == ik.Meta && s.Count > 0)) return true;
            var mat = b.Materials.FirstOrDefault(m => m.Item == item);
            bool reservedAway = mat is { Have: 0 };
            if (!reservedAway && Stored(ik, scan.From).Sum(s => s.N) > 0) return true;
            OutOfItem(w, ref scan, item, ik, mat, reservedAway);
        }
        return false;
    }

    /// <summary>How long a site a pawn went to rests before another pawn is sent there (it loads while a body is by it).</summary>
    public static readonly TimeSpan SiteRest = TimeSpan.FromMinutes(2);

    /// <summary>A build out of everyone's view: one pawn goes to its nearest open cell (the site's lease), and the site loads with it.</summary>
    private ActiveJob? SiteVisit(PawnState p, BuildState b, GhostState site)
    {
        var key = "site:" + b.Id;
        if (!Open(key, p.Name, site.At)) { Blocked($"build {b.Id}: out of everyone's view: a pawn is on its way to load it"); return null; }
        return Job(WorkTypes.Build, "go", $"going to the site of {b.Label} (out of view)", site.At, key, "site", b.Id, "moving", f => f["dist"] = 8, xp: 0);
    }

    /// <summary>A site visit ended: its build is in view while the pawn is there; the site rests a little before the next one.</summary>
    private void SiteVisited(ActiveJob j)
    {
        if (j.Lease != null) _skip[j.Lease] = Now + SiteRest;
    }

    /// <summary>The open cells no higher than <paramref name="cap"/> (a pawn down in a quarry pit); the ones left out are said.</summary>
    private List<GhostState> CappedCells(BuildState b, List<GhostState> open, int cap)
    {
        if (cap == int.MaxValue || open.All(g => g.At.Y <= cap)) return open;
        LeftUpTop(b);
        return open.Where(g => g.At.Y <= cap).ToList();
    }

    private void LeftUpTop(BuildState b) => Blocked($"build {b.Id}: left to a builder up top (I'm down in a quarry pit)");

    /// <summary>A level (a mine orders its own: <see cref="MineOrder"/>): <see cref="Seeds"/>,
    /// spread out (a cell next to another pawn's batch comes after the rest: its neighbours are leased anyway). A level's cells with
    /// nothing to place against yet are left out (the client would say "nothing solid on any side"); when none of them has any and
    /// nothing else will give it, the level floats and its first cell goes on a scaffold (<see cref="FloatingSeed"/>).</summary>
    private List<GhostState> CellOrder(BuildState b, List<GhostState> open, ref GhostScan scan, P3 from)
    {
        var busy = b.Ghosts.Where(g => g.State == "leased").Select(g => g.At).ToList();
        var seeds = Seeds(open, busy, from).ToList();
        scan.Unsupported = open.Count - seeds.Count;
        if (seeds.Count == 0 && open.Count > 0 && FloatingSeed(b, open, from) is { } fl)
        {
            scan.Scaffold = (fl.Seed.At, fl.Spot);
            seeds.Add(fl.Seed);
        }
        return seeds;
    }

    /// <summary>One cell tried as the seed of a batch: a dig, a pour, or a place job; null = not this one (done, taken, short…).</summary>
    private ActiveJob? GhostCell(IStoryWorld w, PawnState p, PawnLive live, ref GhostScan scan, GhostState g, string? key = null)
    {
        var b = scan.Build;
        key ??= $"g:{b.Id}|{g.At}";
        if (!Open(key, p.Name, g.At)) { NoteCellHold(ref scan, key, p.Name, g.At); return null; }
        if (Occupied(g.At) || BuildConflicted(g) || QuarryHeld(b, g)) return null;
        if (ReplacementBuild(b) && ActiveTempCells().Contains(g.At)) { scan.Temp ??= g.At; return null; }
        if (g.Remove) return DigCell(w, p, live, b, g, key);
        // A liquid source: poured from a bucket filled at a still source (StoryColony.Water.cs).
        if (StoryConstruction.IsLiquid(g)) return PourCell(p, live, ref scan, g);
        var (cur, curMeta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (CellSettled(b, g, key, cur, curMeta)) return null;
        return PlaceCell(w, p, live, ref scan, g, key, cur);
    }

    private ActiveJob? DigCell(IStoryWorld w, PawnState p, PawnLive live, BuildState b, GhostState g, string key)
    {
        if (!Open("b:" + g.At, p.Name, g.At)) return null;
        if (w.Block(g.At.X, g.At.Y, g.At.Z).Id == 0 && w.Loaded(g.At.X, g.At.Z))
        {
            if (ReplacementAir(b, g)) CellDone(b, g); else { g.State = "done"; g.Rev = Touch(); CellFinished(b, g); }
            return null;
        }
        if (ReplacementAir(b, g) && ActiveTempCells().Contains(g.At)) return null;
        // A dug cell takes its material's tool (a mine design's ore: the right pickaxe), else the build waits for one.
        var dt = PlanTool(p, live, DigBlockAt(g.At, g.Id));
        if (dt is { Wait: { } why }) { NoteBuildWait(b, why); Blocked($"build {b.Id}: {b.Reason}"); return null; }
        ClearBuildWait(b);
        var job = Job(CellWork(b, g), "dig", $"removing a block ({b.Label})", g.At, key, "ghost", $"{b.Id}|{g.At}", xp: 10);
        if (b.Kind == "preserve" || b.Spec?["replacement_clear"]?.GetValue<bool>() == true) job.Json["ordered"] = true;
        if (ReplacementBuild(b)) job.Json["replace"] = true;
        return Claim(b, [g], p.Name, WithTool(dt, job));
    }

    private ActiveJob? PourCell(PawnState p, PawnLive live, ref GhostScan scan, GhostState g)
    {
        var b = scan.Build;
        var (pj, wait) = PourJob(p, live, b, g, TodoOf(scan), scan.Builders);
        if (pj != null) return pj;
        if (wait) { Blocked($"build {b.Id}: {b.Reason ?? "waiting for water"}"); scan.SkipBuild = true; }
        return null;
    }

    /// <summary>
    /// The cell already holds the ghost's block: done, never a job. There but turned the wrong way: taken back and placed again once
    /// (a clear), unless it was tried already or it holds things (a chest, a furnace: never broken open for its rotation); then the
    /// cell says so on the map. A placed design takes a container back only when the colony knows it empty (<see cref="SeenEmpty"/>).
    /// </summary>
    private bool CellSettled(BuildState b, GhostState g, string key, int cur, int curMeta)
    {
        if (cur == 0) return false;
        if (Matches(g, cur, curMeta, Modern)) { CellDone(b, g); return true; }
        if (!WrongTurn(g, cur, curMeta, Modern)) return false;
        if (_turnRedo.GetValueOrDefault(key) < 1 && (!KeepsItems(cur) || ReplacementBuild(b) && SeenEmpty(g.At))) return false;
        Misrotated(b, g, curMeta);
        return true;
    }

    /// <summary>
    /// The colony knows the container in this cell is empty: it is a storage whose record a body's look vouches for (not older than
    /// <see cref="SeenStale"/>: a player may have used it since) and that record holds nothing. A double chest counts as one: the
    /// record must cover both halves. No record (a furnace, a chest that is not storage), or one nobody has looked into, is not known
    /// empty: such a block is never dug for its rotation, since what it holds would spill.
    /// </summary>
    private bool SeenEmpty(P3 at)
    {
        if (StorageAt(at) is not { Missing: false, SeenAt: { } seen, Contents.Count: 0 } s || Now - seen > SeenStale) return false;
        return DoubleHalf(at) is not { } half || s.Chest == half || s.DoubleWith == half;
    }

    /// <summary>What a batch of one item has to place with: carried, and stored (its own share when an earlier build reserved the rest).</summary>
    private readonly record struct CellSupply(string Item, int Have, int Stored, List<(StorageState Store, Stack Stack, int N)> Stores);

    /// <summary>A place job seeded at the cell, unless its block in the way waits for a tool or its item is out.</summary>
    private ActiveJob? PlaceCell(IStoryWorld w, PawnState p, PawnLive live, ref GhostScan scan, GhostState g, string key, int cur)
    {
        var b = scan.Build;
        // A block in the way is dug first: with its tool (stone in a wall's line takes a pickaxe). The batch clears only cells of
        // that same block (one tool per job).
        var clearTool = !PlaceInto(cur) ? PlanTool(p, live, cur) : null;
        if (clearTool is { Wait: { } cwhy }) { NoteBuildWait(b, cwhy); Blocked($"build {b.Id}: {b.Reason}"); return null; }
        var item = g.Item ?? w.Data.Item(g.Id).Name;
        if (scan.OutOf?.Contains(item) == true) return null;
        var ik = KeyOfItem(w, item, g.Id);
        int have = live.Inventory.Where(s => s.Id == ik.Id && s.Meta == ik.Meta).Sum(s => s.Count);
        var mat = b.Materials.FirstOrDefault(m => m.Item == item);
        // What an earlier build reserved stays for it: this one takes from storage only its own share.
        bool reservedAway = mat is { Have: 0 };
        var stores = reservedAway ? [] : Stored(ik, scan.From);
        int stored = stores.Sum(s => s.N);
        if (have == 0 && stored == 0)
        {
            OutOfItem(w, ref scan, item, ik, mat, reservedAway);
            return null; // the level's other materials may still go in
        }
        if (b.Status != "active") { b.Status = "active"; b.Reason = CellReasonKept(b); b.Rev = Touch(); }
        return PlaceJob(w, p, scan, g, key, cur, clearTool, new CellSupply(item, have, stored, stores));
    }

    /// <summary>
    /// The item is out (none carried, none stored). A material that is out holds only its own cells: the level's other blocks go on
    /// (live 11-04: a design with one missing material stood still as a whole when the first seed was that material, and its
    /// reservations held the queue behind it).
    /// </summary>
    private void OutOfItem(IStoryWorld w, ref GhostScan scan, string item, (int Id, int Meta) ik, BuildMaterial? mat, bool reservedAway)
    {
        var b = scan.Build;
        (scan.OutOf ??= new(StringComparer.Ordinal)).Add(item);
        // Another builder carries this build's items (its batch is in flight): no job here now, and nothing is blocked.
        if (_current.Values.Any(j => j.Source == "ghost" && j.Cells is { Count: > 0 } && j.SourceId?.StartsWith(b.Id + "|", StringComparison.Ordinal) == true))
        {
            scan.InFlight = true;
            return;
        }
        var (status, reason, alert) = HeldReason(w, b, item, ik, mat, reservedAway, scan.From);
        scan.Held ??= (status, reason, alert, $"build {b.Id}: {reason}{ShortSource(mat)}");
    }

    /// <summary>Why a build holds for an item: being crafted, reserved for an earlier build (waiting), else short (blocked; alerted unless
    /// the materials pass already said what's short and why).</summary>
    private (string Status, string Reason, bool Alert) HeldReason(IStoryWorld w, BuildState b, string item, (int Id, int Meta) ik, BuildMaterial? mat, bool reservedAway, P3 from)
    {
        int need = b.Ghosts.Count(x => x.Job && x.State is "todo" or "leased" && (x.Item ?? "") == item);
        var display = w.Data.DisplayName(ik.Id, ik.Meta).ToLowerInvariant();
        if (mat is { Crafting: > 0 }) return ("waiting", $"waiting for {mat.Crafting} {display} (being crafted)", false);
        if (reservedAway && Stored(ik, from).Count > 0) return ("waiting", $"waiting: the {display} in storage is reserved for an earlier build", false);
        return ("blocked", $"need {need} {display}", mat is not { Short: > 0 });
    }

    /// <summary>The place job: the pawn's fair share of what is open (ten builders on one floor each get a part), no more than it can get
    /// items for, as one batch reachable from one stand, with its fetches and the cells it clears first.</summary>
    private ActiveJob PlaceJob(IStoryWorld w, PawnState p, GhostScan scan, GhostState g, string key, int cur, ToolPlan? clearTool, CellSupply s)
    {
        var b = scan.Build;
        int openCells = scan.Mine ? 1 : TodoOf(scan).Count(x => (x.Item ?? "") == s.Item); // open or in a batch now: every builder's share of the whole
        int cap = scan.Mine ? 1 : Math.Min(Math.Clamp((int)Math.Ceiling(openCells / (double)scan.Builders), 1, BatchMax), s.Have + s.Stored);
        var (cells, stand) = Batch(w, b, g, cap, scan.From, p.Name, PlaceInto(cur) ? -1 : cur);
        var scaffold = ScaffoldOf(scan, g); // a floating cell's: one more block, dug again
        var (fetch, takes) = BatchFetch(s.Stores, Math.Max(0, cells.Count + (scaffold != null ? 1 : 0) - s.Have));
        var want = w.Data.BlockExists(g.Id) ? w.Data.Block(g.Id).Name : null;
        var clear = cells.Where(c => !PlaceInto(w.Block(c.At.X, c.At.Y, c.At.Z).Id)).Select(c => c.At.ToString()).ToList();
        var label = cells.Count == 1 ? $"building {b.Label}" : $"building {b.Label}: {cells.Count} blocks";
        var item = s.Item;
        var job = Job(WorkTypes.Build, "place", label, g.At, key, "ghost", $"{b.Id}|{g.At}", "working",
            f => PlaceFields(f, item, want, cells, clear, g, stand, fetch), xp: Math.Min(60, 8 + 4 * cells.Count));
        job.Takes.AddRange(takes);
        if (ReplacementBuild(b)) job.Json["replace"] = true;
        if (ExactBuild(b))
        {
            job.Json["exact_state"] = true;
            job.Json["want_id"] = g.Id;
            job.Json["metas"] = new JsonArray(cells.Select(c => (JsonNode)c.Meta).ToArray());
        }
        if (scaffold is { } spot) job.Json["scaffold"] = new JsonObject { [g.At.ToString()] = spot.ToString() };
        return Claim(b, cells, p.Name, WithTool(clear.Count > 0 ? clearTool : null, job));
    }

    /// <summary>What a batch fetches: from the nearest stores first, as much as it lacks.</summary>
    private (JsonArray Fetch, List<(P3, Stack)> Takes) BatchFetch(List<(StorageState Store, Stack Stack, int N)> stores, int fetchN)
    {
        var fetch = new JsonArray();
        var takes = new List<(P3, Stack)>();
        foreach (var (st, stack, n) in stores)
        {
            if (fetchN <= 0) break;
            int t = Math.Min(n, fetchN);
            fetchN -= t;
            fetch.Add(new JsonObject { ["from"] = st.Chest.ToString(), ["item"] = ItemName(stack.Id, stack.Meta), ["n"] = t });
            takes.Add((st.Chest, stack with { Count = t }));
        }
        return (fetch, takes);
    }

    private void PlaceFields(JsonObject f, string item, string? want, List<GhostState> cells, List<string> clear, GhostState g, P3? stand, JsonArray fetch)
    {
        f["block"] = item;
        f["reach"] = true; // ordinary upper walls and roofs use the same temporary access as quarry remnants
        if (want != null) f["want"] = want; // what the cell shows once built: already there = done
        f["cells"] = new JsonArray(cells.Select(c => (JsonNode)c.At.ToString()).ToArray());
        if (clear.Count > 0) f["clear"] = new JsonArray(clear.Select(c => (JsonNode)c).ToArray());
        if (g.Facing is { } fc) f["facing"] = fc;
        // A design's turned blocks (a furnace's front, stairs, a log's axis, a wall torch): each cell's meta, placed with place_turned
        // (docs/web/story-mode.md "Rotation").
        if (cells.Any(c => MineClaw.World.BlockOrientation.Turns(c.Id, c.Meta, Modern)))
            f["metas"] = new JsonArray(cells.Select(c => (JsonNode)c.Meta).ToArray());
        if (stand is { } sp) f["stand"] = sp.ToString(); // where the whole batch is within reach
        if (fetch.Count > 0) f["fetch"] = fetch;
    }

    /// <summary>Nothing here for this pawn: a material is out, the open cells are another builder's, or the level below isn't built yet.</summary>
    private void NoteBuildHeld(BuildState b, ref GhostScan scan, int openCount)
    {
        if (scan.Held is { } h)
        {
            SayBuildHeld(b, h.Status, h.Reason, h.Alert);
            Blocked(h.Note);
        }
        else if (scan.InFlight) Blocked($"build {b.Id}: its last items are on their way with another builder");
        else if (openCount == 0 && UnderLiquid(b) is { } wet) Blocked($"build {b.Id}: {wet}");
        else if (openCount == 0) Blocked($"build {b.Id}: the level below is still being built");
        else if (scan.Unsupported == openCount) Blocked($"build {b.Id}: {openCount} blocks have nothing to place against yet (the blocks beside or under them go in first)");
        else if (scan.Forbid is { } fz) Blocked($"build {b.Id}: its open cells are in a forbidden zone ({fz})");
        else if (scan.Stuck is { } ss) Blocked($"build {b.Id}: its open cells are by a spot where a pawn got stuck ({ss})");
        else if (scan.Temp is { } tb) Blocked($"build {b.Id}: a pawn's temporary block stands in its cell at {tb} (taken back first)");
        else Blocked($"build {b.Id}: its open cells are taken or backing off");
    }

    /// <summary>"water at x,y,z can't be cleared: the N cells under it wait" when water or lava in the build's empty space holds its
    /// digs below (<see cref="OpenCells"/>), else null.</summary>
    private static string? UnderLiquid(BuildState b)
    {
        if (LiquidOver(b) is not { } top) return null;
        int under = b.Ghosts.Count(g => g.Job && g.Remove && g.State is "todo" or "leased" && g.At.Y < top.At.Y);
        if (under == 0) return null;
        return $"{(top.Id is 10 or 11 ? "lava" : "water")} at {top.At} can't be cleared: the {under} cell{(under == 1 ? " under it waits" : "s under it wait")}";
    }

    /// <summary>A free cell (not taken, not resting) the picker passed over: for a forbidden zone or for a stuck spot, kept for the note.</summary>
    private void NoteCellHold(ref GhostScan scan, string key, string pawn, P3 at)
    {
        if (!Free(key, pawn) || Skipped(key) || Skipped(PawnKey(key, pawn))) return; // taken or backing off: the catch-all says so
        if (Forbidden(at)) scan.Forbid ??= at;
        else if (NearHazard(at, pawn)) scan.Stuck ??= at;
    }

    /// <summary>The build's held status and reason; said once per reason (the build turns active between, while its other materials go in).</summary>
    private void SayBuildHeld(BuildState b, string status, string reason, bool alert)
    {
        if (b.Status == status && b.Reason == reason) return;
        b.Status = status; b.Reason = reason; b.Rev = Touch();
        if (alert && _buildBlockSaid.GetValueOrDefault(b.Id) != reason) Alert("warn", $"Build {b.Id} ({b.Kind}) is blocked: {reason}");
        _buildBlockSaid[b.Id] = reason;
    }

    /// <summary>Only cells with something to click against beside them (<see cref="SeedReady"/>), lower levels first, then away
    /// from other pawns' batches and nearest. A held lower seed does not hide ready cells on another floor.</summary>
    private IEnumerable<GhostState> Seeds(List<GhostState> open, List<P3> busy, P3 from) =>
        open.Where(SeedReady)
            .OrderBy(t => t.At.Y)
            .ThenBy(t => t.At.Dist(from) + (busy.Any(o => Math.Abs(o.X - t.At.X) <= StandRadius + 1 && Math.Abs(o.Z - t.At.Z) <= StandRadius + 1) ? 12 : 0));

    /// <summary>A cell holds what the ghost wants: the same block and variant, turned the same way where the placement decides it (a
    /// furnace's front, a stair's rise and half, a log's axis: <see cref="MineClaw.World.BlockOrientation.Same"/>); runtime bits
    /// (open, powered, lit) are not compared. What the block turns into on its own counts as it (<see cref="StoryDrift"/>: dirt that
    /// grew grass or was tilled, a lit furnace).</summary>
    internal static bool Matches(GhostState g, int id, int meta, bool modern)
    {
        if (StoryConstruction.IsLiquid(g)) return LiquidSource(g.Id, id, meta); // a source of its liquid, still or just poured (StoryColony.Water.cs)
        return MatchesBlock(g.Id, g.Meta, id, meta, modern);
    }

    /// <summary><see cref="Matches"/> for a wanted block (not a liquid): the ground family as one, else the same block and variant turned
    /// the same way where the placement decides it.</summary>
    internal static bool MatchesBlock(int wantId, int wantMeta, int id, int meta, bool modern)
    {
        if (StoryDrift.SameGround(wantId, id, modern)) return true;
        return SameVariant(wantId, wantMeta, id, meta) && MineClaw.World.BlockOrientation.Same(wantId, wantMeta, meta, modern);
    }

    /// <summary>The ghost's block and variant (a runtime twin, a lit furnace, is its block).</summary>
    private static bool SameVariant(GhostState g, int id, int meta) => SameVariant(g.Id, g.Meta, id, meta);

    private static bool SameVariant(int wantId, int wantMeta, int id, int meta)
    {
        if (StoryDrift.Canon(id) != StoryDrift.Canon(wantId)) return false;
        int mask = MineClaw.Bot.Blueprints.DesignFacing.VariantMask(wantId);
        return (meta & mask) == (wantMeta & mask);
    }

    /// <summary>The cell holds the ghost's block and variant, turned another way than the design has it.</summary>
    internal static bool WrongTurn(GhostState g, int id, int meta, bool modern) =>
        !g.Remove && !StoryConstruction.IsLiquid(g) && SameVariant(g, id, meta) && !MineClaw.World.BlockOrientation.Same(g.Id, g.Meta, meta, modern);

    /// <summary>The colony's server keeps 1.12.2's placement rules (else b1.8.1's).</summary>
    private bool Modern => World?.Data.VersionId != "b1.8.1";

    /// <summary>Blocks that hold items: never broken for their rotation (a wrong chest or furnace is said, not emptied onto the floor).</summary>
    internal static bool KeepsItems(int id) => ItemHolders.Contains(id) || id is >= 219 and <= 234; // + shulker boxes

    /// <summary>Chest, trapped chest, furnace and lit furnace, dispenser, dropper, hopper, brewing stand, jukebox.</summary>
    private static readonly HashSet<int> ItemHolders = [54, 146, 61, 62, 23, 158, 154, 117, 84];

    /// <summary>Cells reporting a verified wrong placement (<c>g:build|x,y,z</c>). Dispatch, fetch, approach and an untouched batch
    /// tail do not consume the repair attempt: only a placement result can leave a cell misrotated.</summary>
    private readonly Dictionary<string, int> _turnRedo = new(StringComparer.Ordinal);

    /// <summary>
    /// The cell stands, turned the wrong way: after one more try (or for a block that holds items) it is left that way, a
    /// <c>misrotated</c> ghost the map shows over it with the reason, said once in an alert; it counts as finished for the build.
    /// </summary>
    private void Misrotated(BuildState b, GhostState g, int got, string? why = null)
    {
        if (g.State is "misrotated" or "done") return;
        bool modern = Modern;
        string Words(int m) => MineClaw.World.BlockOrientation.Describe(g.Id, m, modern) ?? $"meta {m}";
        g.State = "misrotated";
        g.Reason = why ?? $"placed {Words(got)}, wants {Words(g.Meta)}";
        g.Rev = Touch();
        _leases.Remove($"g:{b.Id}|{g.At}");
        MaterialsChanged();
        var name = World?.Data is { } d && d.BlockExists(g.Id) ? d.Block(g.Id).DisplayName.ToLowerInvariant() : "block";
        Alert("warn", $"Build {b.Id} ({b.Kind}): the {name} at {g.At} was placed with the wrong rotation ({g.Reason})");
        if (b.Ghosts.Where(x => x.Job).All(x => x.State is "done" or "misrotated"))
        {
            b.Status = "done"; b.Reason = null; b.Rev = Touch();
            if (b.Kind != "preserve") Alert("info", $"Build {b.Id} ({b.Label}) is done");
        }
    }

    /// <summary>Whether the cell is built (see <see cref="View"/>).</summary>
    private bool Built(string pawn, GhostState g) => View(pawn, g) is var (id, meta) && id != 0 && Matches(g, id, meta, Modern);

    /// <summary>The cell's block as the pawn's own view sees it when that holds the cell, else as the colony's ((0, 0) with no view).</summary>
    private (int Id, int Meta) View(string pawn, GhostState g)
    {
        IStoryWorld? view = null;
        try { view = Host.ViewOf(pawn); } catch (Exception) { /* main's */ }
        if (view == null || !view.Loaded(g.At.X, g.At.Z)) view = World;
        return view?.Block(g.At.X, g.At.Y, g.At.Z) ?? (0, 0);
    }

    /// <summary>Storages holding an item, with what is left in each after the fetches other jobs are still on their way to make:
    /// chests nobody is fetching from first, then nearest (fetches spread over the chests instead of queueing at one).</summary>
    private List<(StorageState Store, Stack Stack, int N)> Stored((int Id, int Meta) key, P3 near)
    {
        var list = new List<(StorageState S, Stack St, int N, int Fetchers)>();
        foreach (var s in Storages)
        {
            if (!ChestOpen(s.Chest)) continue;
            var st = s.Contents.FirstOrDefault(c => c.Id == key.Id && c.Meta == key.Meta);
            if (st == null) continue;
            var (inFlight, fetchers) = InFlightFrom(s, key);
            int n =s.Contents.Where(c => c.Id == key.Id && c.Meta == key.Meta).Sum(c => c.Count) - inFlight;
            if (n > 0) list.Add((s, st, n, fetchers));
        }
        return list.OrderBy(x => x.Fetchers).ThenBy(x => x.S.Chest.Dist(near)).Select(x => (x.S, x.St, x.N)).ToList();
    }

    /// <summary>What the jobs at work are still on their way to take of an item from a storage, and how many jobs fetch from it.</summary>
    private (int InFlight, int Fetchers) InFlightFrom(StorageState s, (int Id, int Meta) key)
    {
        int inFlight = 0, fetchers = 0;
        foreach (var j in _current.Values)
        {
            bool any = false;
            foreach (var (chest, t) in j.Takes)
            {
                if (chest != s.Chest && chest != s.DoubleWith) continue;
                any = true;
                if (t.Id == key.Id && t.Meta == key.Meta) inFlight += t.Count;
            }
            if (any) fetchers++;
        }
        return (inFlight, fetchers);
    }

    private static double EyeDist(P3 stand, P3 cell)
    {
        double dx = cell.X - stand.X, dz = cell.Z - stand.Z, dy = cell.Y + 0.5 - (stand.Y + 1.62);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// The batch around <paramref name="seed"/>: open cells of the same item and facing near it (its level), a stand spot
    /// from which the seed and as many of the nearest of them as possible are in reach, then the cells in an order where each has
    /// support when its turn comes: those in reach of the stand first, then the ones a few steps on (cells left over between earlier
    /// batches are taken along instead of becoming one-block jobs); within each, touching something solid or a cell placed before it,
    /// nearest the last (along the row).
    /// </summary>
    private (List<GhostState> Cells, P3? Stand) Batch(IStoryWorld w, BuildState b, GhostState seed, int cap, P3 from, string pawn, int clearId)
    {
        var cand = new List<GhostState>();
        if (cap > 1)
            foreach (var x in b.Ghosts)
                if (BatchTakes(w, b, seed, x, pawn, clearId)) cand.Add(x);
        var stand = StandFor(w, b, seed, NearSeed(cand, seed), from);
        return (OrderBatch(b, seed, cand, stand, cap), stand);
    }

    /// <summary>The candidates within <see cref="StandRadius"/> of the seed (the ones a stand is chosen for).</summary>
    private static List<GhostState> NearSeed(List<GhostState> cand, GhostState seed)
    {
        var near = new List<GhostState>();
        foreach (var x in cand)
            if (Math.Abs(x.At.X - seed.At.X) <= StandRadius && Math.Abs(x.At.Z - seed.At.Z) <= StandRadius) near.Add(x);
        return near;
    }

    /// <summary>
    /// Whether an open cell joins the seed's batch: the same item and facing, its level (one level: the next goes up once this one
    /// stands; b1.8.1 FakeServer: a roof row laid early, over a wall still going up, had no visible support face, and its builder
    /// climbed onto the wall and was shut in), free, not already built (done, or left misrotated), nothing else to clear in it.
    /// </summary>
    private bool BatchTakes(IStoryWorld w, BuildState b, GhostState seed, GhostState x, string pawn, int clearId)
    {
        if (!BatchKin(seed, x) || !NearSeedLevel(seed, x) || QuarryConflict(x) != null) return false;
        if (ReplacementBuild(b) && ActiveTempCells().Contains(x.At)) return false;
        var key = $"g:{b.Id}|{x.At}";
        if (!Open(key, pawn, x.At) || Occupied(x.At)) return false;
        var (cur, curMeta) = w.Block(x.At.X, x.At.Y, x.At.Z);
        if (CellSettled(b, x, key, cur, curMeta)) return false;
        return PlaceInto(cur) || cur == clearId; // another block to clear: another tool, another job
    }

    /// <summary>Another open job cell of the seed's item and facing.</summary>
    private static bool BatchKin(GhostState seed, GhostState x) =>
        x != seed && x.Job && !x.Remove && x.State == "todo" && x.Item == seed.Item && x.Facing == seed.Facing;

    /// <summary>Within walking distance of the seed, on its level.</summary>
    private static bool NearSeedLevel(GhostState seed, GhostState x) =>
        Math.Abs(x.At.X - seed.At.X) <= WalkRadius && Math.Abs(x.At.Z - seed.At.Z) <= WalkRadius && x.At.Y == seed.At.Y;

    /// <summary>The batch's cells from the seed on, each with support when its turn comes (see <see cref="Batch"/>).</summary>
    private List<GhostState> OrderBatch(BuildState b, GhostState seed, List<GhostState> cand, P3? stand, int cap)
    {
        var inBatch = new HashSet<P3> { seed.At };
        var batch = new List<GhostState> { seed };
        // The stand may be an open cell of the batch's patch (the pawn stands in the middle of it): that cell is left for later.
        bool InReach(P3 c) => stand is { } s ? EyeDist(s, c) <= BuildReach : Math.Abs(c.X - seed.At.X) <= 1 && Math.Abs(c.Z - seed.At.Z) <= 1;
        // A wall's builder works from outside only: no cells it would have to walk to (it could end up inside and be walled in).
        bool walled = Footprint(b).Walled;
        var rest = cand.Where(c => (stand is not { } s || c.At != s && c.At != s.Add(0, 1, 0)) && (!walled || InReach(c.At))).ToList();
        while (batch.Count < cap && rest.Count > 0)
        {
            GhostState? next = null;
            double best = double.MaxValue;
            foreach (var c in rest)
            {
                if (!FaceReady(c, inBatch)) continue; // the client's own rule: its face's block stands or was laid before it
                double score = (InReach(c.At) ? 0 : 100_000) + c.At.Y * 1000 + c.At.Dist(batch[^1].At);
                if (score < best) { best = score; next = c; }
            }
            if (next == null) break;
            batch.Add(next);
            inBatch.Add(next.At);
            rest.Remove(next);
        }
        return batch;
    }

    /// <summary>
    /// A spot to stand on for a batch: feet and head open, solid ground under it, not the seed's cell and not on a batch cell (another
    /// cell of the patch can be the stand: it is then left out of the batch); the seed in reach, then the most of the batch in reach.
    /// A spot in a still-open cell of the build or another builder's spot costs extra. A wall or fence ring is built from outside only,
    /// never on or inside its footprint (builders never wall themselves in). Null when nothing near is standable (the place tool then
    /// walks into reach by itself).
    /// </summary>
    private P3? StandFor(IStoryWorld w, BuildState b, GhostState seed, List<GhostState> cand, P3 from)
    {
        var cells = cand.Select(c => c.At).ToHashSet();
        cells.Add(seed.At);
        var pending = b.Ghosts.Where(g => g.Job && g.State != "done").Select(g => g.At).ToHashSet();
        var fp = Footprint(b);
        var taken = TakenStands();
        P3? best = null;
        double bestScore = double.MaxValue;
        for (int dx = -StandRadius - 2; dx <= StandRadius + 2; dx++)
            for (int dz = -StandRadius - 2; dz <= StandRadius + 2; dz++)
                for (int dy = -3; dy <= 1; dy++) // a roof or a wall's top row is laid from the ground below it
                {
                    var s = seed.At.Add(dx, dy, dz);
                    if (!StandAllowed(w, seed.At, cells, fp, s) || pending.Contains(s.Add(0, -1, 0))) continue; // never on a cell to clear
                    double score = StandScore(s, cells, pending, taken, from);
                    if (score < bestScore) { bestScore = score; best = s; }
                }
        return best;
    }

    /// <summary>The stands other builders' batches are using now.</summary>
    private HashSet<P3> TakenStands()
    {
        var taken = new HashSet<P3>();
        foreach (var j in _current.Values)
            if (j.Source == "ghost" && j.Json["stand"] is JsonValue tv && tv.TryGetValue<string>(out var ts) && ParseCell(ts) is { } tp) taken.Add(tp);
        return taken;
    }

    /// <summary>A spot the seed can be built from: not the seed's cell nor on a batch cell, the seed in reach, outside a wall ring's
    /// footprint (never on or inside the walls), loaded and standable.</summary>
    private static bool StandAllowed(IStoryWorld w, P3 seed, HashSet<P3> cells, (bool Walled, int X1, int Z1, int X2, int Z2) fp, P3 s)
    {
        if (s == seed || s.Add(0, 1, 0) == seed || cells.Contains(s.Add(0, -1, 0))) return false;
        if (EyeDist(s, seed) > BuildReach || InsideFootprint(fp, s)) return false;
        return w.Loaded(s.X, s.Z) && Standable(w, s);
    }

    private static bool InsideFootprint((bool Walled, int X1, int Z1, int X2, int Z2) fp, P3 s) =>
        fp.Walled && s.X >= fp.X1 && s.X <= fp.X2 && s.Z >= fp.Z1 && s.Z <= fp.Z2;

    /// <summary>Lower is better: the most batch cells in reach, then near, avoiding still-open build cells and other builders' spots.</summary>
    private static double StandScore(P3 s, HashSet<P3> cells, HashSet<P3> pending, HashSet<P3> taken, P3 from)
    {
        var head = s.Add(0, 1, 0);
        int reach = 0;
        foreach (var c in cells) if (c != s && c != head && EyeDist(s, c) <= BuildReach) reach++;
        return -10.0 * reach + 0.05 * s.Dist(from)
            + (pending.Contains(s) || pending.Contains(head) ? 4 : 0)
            + (taken.Contains(s) ? 25 : 0);
    }

    /// <summary>A build's footprint (its cells' x/z box), and whether it encloses ground (a wall or fence ring): its builders stay out.</summary>
    private static (bool Walled, int X1, int Z1, int X2, int Z2) Footprint(BuildState b)
    {
        int x1 = int.MaxValue, x2 = int.MinValue, z1 = int.MaxValue, z2 = int.MinValue;
        foreach (var g in b.Ghosts)
        {
            if (!g.Job) continue;
            x1 = Math.Min(x1, g.At.X); x2 = Math.Max(x2, g.At.X); z1 = Math.Min(z1, g.At.Z); z2 = Math.Max(z2, g.At.Z);
        }
        return (b.Kind is "wall" or "fence" && x2 - x1 >= 2 && z2 - z1 >= 2, x1, z1, x2, z2);
    }

    /// <summary>Feet and head open (no liquid), solid ground under it.</summary>
    private static bool Standable(IStoryWorld w, P3 s)
    {
        int feet = w.Block(s.X, s.Y, s.Z).Id, head = w.Block(s.X, s.Y + 1, s.Z).Id, floor = w.Block(s.X, s.Y - 1, s.Z).Id;
        if (!OpenCell(feet) || !OpenCell(head) || StoryBlocks.Liquids.Contains(feet) || StoryBlocks.Liquids.Contains(head)) return false;
        return !OpenCell(floor) && !StoryBlocks.Liquids.Contains(floor);
    }

    private static P3? ParseCell(string s)
    {
        var parts = s.Split(',');
        return parts.Length == 3 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) && int.TryParse(parts[2], out var z) ? new P3(x, y, z) : null;
    }


    /// <summary>
    /// A build of a placed design (items sharing a <c>group</c>, the dig item queued first) whose ground isn't dug out yet: an
    /// earlier build of its group has a dig out with a pawn or open now (<see cref="DigsOpen"/>).
    /// </summary>
    private bool DigFirst(BuildState b)
    {
        static string? Group(BuildState x) => x.Spec?["group"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        // A mine orders its own cells (a block goes in once its cell is open).
        if (b.Kind == "mine" || Group(b) is not { } group || b.Ghosts.All(g => g.Remove)) return false;
        // A dig that keeps failing (blocked) doesn't hold the build forever: a placement digs its own cell anyway. Nor do digs that
        // wait under water: the placements go on beside it, each clearing its own cell.
        return Builds.Any(o => o != b && o.Order < b.Order && o.Status != "done" && Group(o) == group && DigsOpen(o));
    }

    /// <summary>Per dig build: whether it has a dig out with a pawn or open now. Kept while the colony's revision stands (every
    /// change to a cell touches it), so a pick asks once per build, not once per placement build of its group.</summary>
    private readonly Dictionary<BuildState, bool> _digsOpen = new(ReferenceEqualityComparer.Instance);
    private long _digsOpenRev = -1;

    private bool DigsOpen(BuildState o)
    {
        if (_digsOpenRev != _rev) { _digsOpen.Clear(); _digsOpenRev = _rev; }
        if (_digsOpen.TryGetValue(o, out bool open)) return open;
        var todo = o.Ghosts.Where(g => g.Job && g.State is "todo" or "leased").ToList();
        open = todo.Any(g => g.Remove && g.State == "leased") || OpenCells(o, todo).Any(g => g.Remove);
        return _digsOpen[o] = open;
    }

    /// <summary>A cell with an open neighbour (air or anything a pawn walks through): a mine cell a pawn can reach and dig.</summary>
    private bool Exposed(P3 p) =>
        !Solid(p.Add(0, 1, 0)) || !Solid(p.Add(1, 0, 0)) || !Solid(p.Add(-1, 0, 0)) || !Solid(p.Add(0, 0, 1)) || !Solid(p.Add(0, 0, -1)) || !Solid(p.Add(0, -1, 0));

    /// <summary>The job's cells are leased to the pawn and shown as being built once it is handed out (a probe claims nothing).</summary>
    private ActiveJob Claim(BuildState b, List<GhostState> cells, string pawn, ActiveJob j)
    {
        j.Cells = cells.Select(c => $"{b.Id}|{c.At}").ToList();
        j.Assign = () =>
        {
            var until = Now + LeaseTtl;
            _log.LogDebug("story {Colony}: {Pawn} batch {Job}: {Cells} stand {Stand}", Id, pawn, j.Id, string.Join(" ", cells.Select(c => c.At)), j.Json["stand"]?.ToString());
            foreach (var c in cells)
            {
                _leases[$"g:{b.Id}|{c.At}"] = (pawn, until);
                if (c.State == "todo") { c.State = "leased"; c.Rev = Touch(); }
                // A miss's text (MissSaid) is why the cell rested; taken again, it rests no more. A counted failure's reason stays.
                if (c.Reason != null && !_ghostFails.ContainsKey($"{b.Id}|{c.At}")) { c.Reason = null; c.Rev = Touch(); }
                TryHanded(c); // a blocked cell reopened for a try is handed out a few times at most (StoryColony.Reconcile.cs)
            }
        };
        return j;
    }

    /// <summary>The cells a job hasn't reported go back (it ended, failed or was cut): their leases and their "leased" state.</summary>
    private void ReleaseCells(ActiveJob j)
    {
        foreach (var id in JobCells(j))
        {
            var key = "g:" + id;
            if (j.Lease != key && _leases.TryGetValue(key, out var l) && _current.TryGetValue(l.Owner, out var holder) && holder != j && holder.Cells?.Contains(id) == true)
                continue; // another job holds it now
            _leases.Remove(key);
            if (FindGhost(id) is { State: "leased" } g) { g.State = "todo"; g.Rev = Touch(); }
        }
    }

    /// <summary>A batch at work keeps its cells (renewed with its own lease, by progress and by the tick).</summary>
    private void RenewCells(string pawn, ActiveJob j)
    {
        if (j.Cells == null) return;
        var until = Now + LeaseTtl;
        foreach (var id in j.Cells)
            if (_leases.TryGetValue("g:" + id, out var l) && string.Equals(l.Owner, pawn, StringComparison.OrdinalIgnoreCase)) _leases["g:" + id] = (pawn, until);
    }

    /// <summary>
    /// A build job's progress: <c>fetched</c> (its items left the chests: the ledger says so now, not when the batch ends), or
    /// <c>cell x,y,z done</c> | <c>cell x,y,z failed: why</c> (that cell is finished with: done, back to the queue to rest a little, or
    /// a failure that counts). The batch's other cells stay the pawn's.
    /// </summary>
    private void BuildProgress(PawnState p, ActiveJob j, string? note)
    {
        RenewCells(p.Name, j);
        if (note == null) return;
        if (note == "fetched") { BatchFetched(j); return; }
        if (note.StartsWith("fill failed at ", StringComparison.Ordinal)) { FillFailed(p, note); return; } // StoryColony.Water.cs
        if (ReportedCell(j, note) is not { } cell) return;
        j.Cells!.Remove(cell.Id);
        var key = "g:" + cell.Id;
        if (j.Lease != key && _leases.TryGetValue(key, out var l) && string.Equals(l.Owner, p.Name, StringComparison.OrdinalIgnoreCase)) _leases.Remove(key);
        CellReported(p, cell.B, cell.G, cell.Id, cell.Status);
    }

    /// <summary>The batch's items left the chests: the ledger says so now, not when the batch ends.</summary>
    private void BatchFetched(ActiveJob j)
    {
        foreach (var (chest, s) in j.Takes) if (StorageAt(chest) is { } st) TakeContents(st, s.Id, s.Meta, s.Count);
        j.Takes.Clear();
        MaterialsChanged();
    }

    /// <summary>A <c>cell x,y,z status</c> note about one of the job's own cells.</summary>
    private readonly record struct CellNote(string Id, GhostState G, BuildState B, string Status);

    private CellNote? ReportedCell(ActiveJob j, string note)
    {
        if (!note.StartsWith("cell ", StringComparison.Ordinal) || j.Cells == null) return null;
        int sp = note.IndexOf(' ', 5);
        if (sp < 0 || ParseCell(note[5..sp]) is not { } at) return null;
        var id = j.Cells.FirstOrDefault(c => c.EndsWith("|" + at, StringComparison.Ordinal));
        if (id == null || OpenGhost(id, anyState: true) is not (var g, var b)) return null;
        return new CellNote(id, g, b, note[(sp + 1)..]);
    }

    /// <summary>The ghost behind a cell id and its build; null when either is gone, or (unless <paramref name="anyState"/>) it is done.</summary>
    private (GhostState G, BuildState B)? OpenGhost(string id, bool anyState = false)
    {
        if (FindCell(id) is not (var g, var b) || !anyState && g.State == "done") return null;
        return (g, b);
    }

    /// <summary>One cell's report: built (done), turned the wrong way (misrotated), else a failure of that cell.</summary>
    private void CellReported(PawnState p, BuildState b, GhostState g, string id, string status)
    {
        if (ReportedBuilt(p.Name, g, status)) { CellDone(b, g); return; }
        // Placed, but turned the wrong way: the client already took it back and tried once more (or couldn't): the cell says so.
        // An exact-state cell (the plan editor's) says "wrong exact state" for the same thing; one that is not a turn stays a failure.
        if (View(p.Name, g) is var (tid, tmeta) && WrongTurn(g, tid, tmeta, Modern)
            && (status.Contains("wrong rotation", StringComparison.OrdinalIgnoreCase) || status.Contains("wrong exact state", StringComparison.OrdinalIgnoreCase)
                || status == "done"))
        {
            var key = "g:" + id;
            _turnRedo[key] = _turnRedo.GetValueOrDefault(key) + 1;
            Misrotated(b, g, tmeta);
            return;
        }
        if (status == "done") status = $"failed: {g.At} is occupied by {BlockNameOr(View(p.Name, g).Id, "another block")}";
        if (g.State == "leased") { g.State = "todo"; g.Rev = Touch(); }
        CellFailed(p, b, g, id, status.StartsWith("failed: ", StringComparison.Ordinal) ? status[8..] : status);
    }

    /// <summary>Done when the world agrees, or doesn't know yet (its copy of the block change can come a moment later); a cell that
    /// holds something else (another variant: the script saw the block's name) still has to be cleared and built.</summary>
    private bool ReportedBuilt(string pawn, GhostState g, string status)
    {
        if (Built(pawn, g)) return true;
        if (status != "done") return false;
        var vid = View(pawn, g).Id;
        return StoryConstruction.IsLiquid(g) ? vid == 0 : OpenCell(vid);
    }

    /// <summary>A block's id name in the colony's version, else <paramref name="fallback"/>.</summary>
    private string BlockNameOr(int id, string fallback) => World?.Data is { } d && d.BlockExists(id) ? d.Block(id).Name : fallback;

    /// <summary>The job ended "done": each cell it didn't report is checked against the world (built = done, else back to the queue, no
    /// failure). An older pawn script reports only the whole job (no <c>batch</c> note, no cell lines): its first cell is built.</summary>
    private void BatchDone(PawnState p, ActiveJob j, string? note)
    {
        if (j.Cells == null) return;
        bool legacy = !(note ?? "").StartsWith("batch", StringComparison.Ordinal) && (j.Json["cells"] as JsonArray)?.Count == j.Cells.Count;
        foreach (var id in j.Cells.ToList())
        {
            if (OpenGhost(id) is not (var g, var b)) continue;
            // A dig job is one cell, and its "done" is taken as before.
            if (g.Remove || Built(p.Name, g) || legacy && id == j.SourceId) CellDone(b, g);
        }
        j.Cells.Clear();
    }

    /// <summary>A job's cells: its batch, else its one cell.</summary>
    private static IEnumerable<string> JobCells(ActiveJob j) => (IEnumerable<string>?)j.Cells ?? (j.SourceId != null ? [j.SourceId] : []);

    /// <summary>The job failed as a whole (a fetch, a script error): cells that are built still count; the failure counts against its
    /// first open cell.</summary>
    private void BatchFailed(PawnState p, ActiveJob j, string why)
    {
        var first = FirstOpen(p, j);
        j.Cells?.Clear();
        if (first is (var fg, var fb, var fid)) CellFailed(p, fb, fg, fid, why, counted: true);
    }

    /// <summary>A failed job's cells that are built are done; the first that is not, or null.</summary>
    private (GhostState G, BuildState B, string Id)? FirstOpen(PawnState p, ActiveJob j)
    {
        (GhostState G, BuildState B, string Id)? first = null;
        foreach (var id in JobCells(j))
        {
            if (OpenGhost(id) is not (var g, var b)) continue;
            if (!g.Remove && Built(p.Name, g)) { CellDone(b, g); continue; }
            first ??= (g, b, id);
        }
        return first;
    }

    /// <summary>
    /// A build job failed as a whole: its first open cell's miss, sorted as a cell's report is (<see cref="MissKind"/>). True = handled,
    /// not the cell's failure. False = <c>Failed</c> goes on: a stuck pawn's miss and a reach miss rest that pawn (<c>PawnOnly</c>, which
    /// counts against the cell once every builder missed it), and a failure that counts is backed off and struck (<see cref="BatchFailed"/>).
    /// </summary>
    private bool BatchMissed(PawnState p, ActiveJob j, string why)
    {
        if (_stuck.ContainsKey(p.Name) || FirstOpen(p, j) is not (var g, var b, var id)) return false;
        why = RefusalWords(why);
        var kind = MissKind(g, why, whole: true);
        if (kind is CellMiss.Cell or CellMiss.Pawn || ReachOnly(why) || !CellMissed(p, b, g, id, why, kind)) return false;
        j.Cells?.Clear();
        return true;
    }

    /// <summary>Whose a build cell's miss is. Only <see cref="Cell"/> counts against it at once.</summary>
    private enum CellMiss
    {
        /// <summary>The cell's own: counts (three and it is blocked).</summary>
        Cell,
        /// <summary>Not about the cell at all (the pawn's pillar still coming down, its tool, its death, a hand on it, its job cut): rests
        /// a moment, never counts.</summary>
        NotCell,
        /// <summary>Not now (someone in the cell, another block in it since): rests a moment; the fifth in a row counts.</summary>
        NotNow,
        /// <summary>The pawn had no blocks to climb with: rests for that pawn, never counts; the build says so once.</summary>
        Supply,
        /// <summary>That pawn couldn't reach it: rests for that pawn; counts once every builder missed it.</summary>
        Pawn,
    }

    /// <summary>
    /// One classifier for a pawn's cell report and a build job's whole failure (<paramref name="whole"/>: also the job's end, a dead or
    /// hand-controlled body). Before 11-09 every text a pawn reported about a cell that was not a known "not now" counted a strike, so
    /// three pawns still taking their pillar down, short of dirt to climb with, or standing in the cell blocked it for good (C3).
    /// </summary>
    private CellMiss MissKind(GhostState g, string why, bool whole)
    {
        if (why.Contains("access pillar cleanup pending", StringComparison.Ordinal) || ToolFailure(why) || whole && JobCut(why)) return CellMiss.NotCell;
        if (why.Contains("block(s) needed", StringComparison.Ordinal)) return CellMiss.Supply;
        if (OutOfReach(why) || Unreachable(why)) return CellMiss.Pawn;
        if (Transient(why) || why.Contains("is occupied by", StringComparison.OrdinalIgnoreCase)) return CellMiss.NotNow;
        // The server took the block back: the vanilla rule for a body in the cell (one walked in after the pick), else the cell's.
        bool refused = why.Contains("server undid my placement", StringComparison.Ordinal) || why.Contains("didn't appear", StringComparison.Ordinal)
                       || why.StartsWith(ServerRefused, StringComparison.Ordinal);
        return refused && BodyIn(g.At) ? CellMiss.NotNow : CellMiss.Cell;
    }

    /// <summary>The start of the director's words for a placement the server took back (<see cref="RefusalWords"/>).</summary>
    private const string ServerRefused = "the server refused the block at ";

    /// <summary>The pawn client's "server undid my placement at x,y,z (protected area?)" or "stopped after 3 identical server refusals at
    /// x,y,z; …" in the director's words. The client guesses protection, but a body in the cell or a rule of the block (a chest beside a
    /// double chest) took it back as often, and the cell's reason pointed the owner the wrong way (C9). The client's text stays as it
    /// is: LLM bots read it.</summary>
    private static string RefusalWords(string why) =>
        ClientRefusal.Match(why) is { Success: true } m ? $"{ServerRefused}{m.Groups[1].Value} (something in the cell, or a rule of that block)" : why;

    private static readonly System.Text.RegularExpressions.Regex ClientRefusal =
        new(@"(?:server undid my placement|stopped after 3 identical server refusals) at (-?\d+,-?\d+,-?\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The body's action never ran or was cut (MinecraftClient.Actions.cs, the build batch's own stop): not the target's doing.</summary>
    private static bool JobCut(string why) =>
        why is "dead" or "not in game" or "stopped" || why.StartsWith("human is controlling", StringComparison.Ordinal)
        || why.StartsWith("cancelled", StringComparison.Ordinal) || why.Contains("interrupted", StringComparison.Ordinal);

    /// <summary>A cell's rest for a pawn that had no blocks to climb with.</summary>
    private static readonly TimeSpan SupplyRest = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A miss that is not (yet) the cell's failure rests it and says why; true = handled. Not about the cell: everyone waits
    /// <see cref="SoftRest"/>. Supply: that pawn waits <see cref="SupplyRest"/>, and the build says once what is missing. Its reach:
    /// that pawn waits longer each time, until every builder missed it (false: it counts). Not now: <see cref="SoftRest"/>, until the
    /// <see cref="SoftLimit"/>-th in a row (false: it counts).
    /// </summary>
    private bool CellMissed(PawnState p, BuildState b, GhostState g, string id, string why, CellMiss kind)
    {
        var key = "g:" + id;
        switch (kind)
        {
            case CellMiss.NotCell:
                _skip[key] = Now + SoftRest;
                break;
            case CellMiss.Supply:
                RestForPawn(p, key, SupplyRest);
                if (_cellAlerted.Add(b.Id + "|no blocks to climb with"))
                    Alert("warn", $"Build {b.Id}: pawns have no dirt or cobblestone to climb with: store some", p.Name);
                break;
            case CellMiss.Pawn:
                RestForPawn(p, key);
                if (!UnreachableForSome(p, key, CellWork(b, g))) return false;
                break;
            case CellMiss.NotNow:
                if (!CellRested(key)) return false;
                break;
            default:
                return false;
        }
        MissSaid(g, why);
        return true;
    }

    /// <summary>A build job's miss rested for its pawn (Rescue.cs <c>RestForPawn</c>): its seed cell says why too.</summary>
    private void CellMissSaid(string id, string why)
    {
        if (OpenGhost(id) is (var g, _)) MissSaid(g, why);
    }

    /// <summary>A miss that did not count: the cell is open again and its reason is the miss's text (the map's tooltip says why it rests:
    /// no cell waits without one), and, when the cell was reopened for a try, this was not one of its tries.</summary>
    private void MissSaid(GhostState g, string why)
    {
        if (g.State is not ("todo" or "leased")) return; // blocked or done meanwhile: its own reason stands
        bool changed = g.State == "leased" || g.Reason != why;
        if (g.State == "leased") g.State = "todo";
        g.Reason = why;
        if (changed) g.Rev = Touch();
        TryNotCounted(g); // StoryColony.Reconcile.cs
    }

    private void CellDone(BuildState b, GhostState g)
    {
        if (g.State == "done") return;
        g.State = "done";
        g.Holds = g.HoldsWords = null;
        g.Rev = Touch();
        CellReasonGone(b, g);
        MaterialsChanged();
        CellFinished(b, g); // blocked cells beside it, or of the reason it was reopened to try, get their turn (StoryColony.Reconcile.cs)
        if (_softFails.Count > 0 || _unreachBy.Count > 0 || _pawnFails.Count > 0)
        {
            // Its "not now"s in a row and the pawns that missed it end here, not when a batch ends (a batch always ends done), and
            // so do each pawn's misses of it (reopened, it starts fresh; kept, they piled up for every cell ever missed).
            var key = $"g:{b.Id}|{g.At}";
            _softFails.Remove(key);
            _unreachBy.Remove(key);
            if (_pawnFails.Count > 0) ForgetPawnFails(key);
        }
        if (ReplacementAir(b, g)) Removed("ghost", $"{b.Id}|{g.At}");
        if (b.Kind != "preserve") // a preserved zone's repair puts back what was there: it makes nothing new the colony's
        {
            StationBuilt(g); // a brewing stand, anvil or enchanting table the colony built is its own (StoryColony.Stations.cs)
            ChestBuilt(g); // and a chest it built is storage (StoryColony.Storage.cs)
        }
        // A door's / bed's other half came with it.
        foreach (var other in b.Ghosts.Where(o => !o.Job && o.State != "done" && Math.Abs(o.At.X - g.At.X) + Math.Abs(o.At.Y - g.At.Y) + Math.Abs(o.At.Z - g.At.Z) == 1))
        { other.State = "done"; other.Rev = g.Rev; }
        if (b.Ghosts.Where(x => x.Job).All(x => x.State is "done" or "misrotated"))
        {
            b.Status = "done"; b.Reason = null; b.Rev = Touch();
            if (b.Kind == "preserve") return; // a preserved zone's repairs come and go: the zone's status says it
            Alert("info", $"Build {b.Id} ({b.Label}) is done");
            // An infinite pool the colony made: pourers fill their buckets there from now on (StoryColony.Water.cs).
            if (LiquidOfBuild(b) == "water" && Renewable(b)) Alert("info", $"Build {b.Id} ({b.Label}) refills: pawns fill their buckets there from now on");
        }
    }

    /// <summary>
    /// One cell that didn't go in. A miss that is not the cell's (<see cref="MissKind"/>: its pawn's reach or supply, the pawn's own
    /// trouble, "not now") rests it and says why without counting (<see cref="CellMissed"/>); anything else counts, and the third
    /// failure blocks the cell with an alert. <paramref name="counted"/>: the job's own failure (sorted and rested already: Failed).
    /// </summary>
    private void CellFailed(PawnState p, BuildState b, GhostState g, string id, string why, bool counted = false)
    {
        var key = "g:" + id;
        // The pawn client refused a chest the game would refuse, before any click: the chest rule's reason (StoryConstruction.Chests.cs).
        if (StoryConstruction.ChestRuleOfRefusal(g, why, Modern) is { } rule) why = rule;
        else why = RefusalWords(why);
        // A missing prerequisite is not a failed build, even after many attempts or a whole-job failure.
        if (!g.Remove && !StoryConstruction.IsLiquid(g) && NoFaceYet(why))
        {
            Reopen(b, g);
            _skip[key] = Now + SoftRest;
            return;
        }
        if (!counted)
        {
            _log.LogInformation("story {Colony}: {Pawn} cell {At} failed: {Why}", Id, p.Name, g.At, why);
            if (CellMissed(p, b, g, id, why, MissKind(g, why, whole: false))) return;
        }
        int n = _ghostFails[id] = Math.Max(_ghostFails.GetValueOrDefault(id), BlockedAtOnce(g, why, counted) ? 2 : 0) + 1;
        if (!counted) _skip[key] = Now + Backoff(n);
        if (g.State is "leased" or "todo") g.State = n >= 3 ? "blocked" : "todo";
        g.Reason = why; // the cell's last failure: the map's marker says it, Retry clears it (StoryColony.BuildRetry.cs)
        g.Rev = Touch();
        if (n < 3) return;
        SayCellBlocked(p, b, g, why);
        if (g.State == "blocked") CellBlockedNow(b, g); // its reason is tried again later, a few cells at a time (StoryColony.Reconcile.cs)
    }

    /// <summary>
    /// A failure no retry will mend, so the cell is blocked at once: a pour cell with nothing to pour against (live 11-04: 371 of them,
    /// each tried 6 times), or a clear the pawn's dig refused for good (protected, a chest, a never-break mark: never tried again and
    /// again, live 11-05), or a chest the game's chest rule refuses (the pawn client's refusal before any click: no try mends it while
    /// the chest beside it stands; the reconcile pass reopens it when that is gone).
    /// </summary>
    private static bool BlockedAtOnce(GhostState g, string why, bool counted) =>
        StoryConstruction.IsLiquid(g) && why.Contains("nothing beside or under", StringComparison.Ordinal) || !counted && ClearRefused(why)
        || StoryConstruction.IsChestRule(why);

    /// <summary>"Nothing solid on any side": the pawn's view had no face to click yet (a neighbour placed a moment ago): not now.</summary>
    private static bool NoFaceYet(string why) => why.Contains("nothing solid on any side", StringComparison.Ordinal);

    /// <summary>A "not now" failure rests the cell a little instead of counting, up to <see cref="SoftLimit"/> in a row.</summary>
    private bool CellRested(string key)
    {
        int soft = _softFails[key] = _softFails.GetValueOrDefault(key) + 1;
        if (soft < SoftLimit) { _skip[key] = Now + SoftRest; return true; }
        _softFails.Remove(key);
        return false;
    }
}
