using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// The build queue (docs/web/story-mode.md "Construction", "Building fast"): items expand to ghost blocks; a pawn whose Build priority
/// is on gets a batch of them (one item, one stand spot, one fetch), each cell leased to it until it reports that cell. Order: queue
/// order, then bottom-up (support first), then blocks touching something solid, then nearest, away from other builders. A pawn
/// fetches the material from storage (or uses its own); none anywhere → the build is blocked with "need N item" and an alert until
/// some arrives. Never a block where a player (pawn or not) stands.
/// </summary>
public sealed partial class StoryColony
{
    private GhostState? FindGhost(string? key)
    {
        if (key == null) return null;
        var parts = key.Split('|');
        if (parts.Length != 2) return null;
        var b = Builds.FirstOrDefault(x => x.Id == parts[0]);
        return b?.Ghosts.FirstOrDefault(g => g.At.ToString() == parts[1]);
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
        var flow = QueueBuilds(w, made);
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
        foreach (var g in b.Ghosts) MarkBuiltAlready(w, g, r);
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

    private (JsonNode? Result, string? Error) ToggleBuilds(string op, IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 }) return (null, "ids required");
        var hit = Builds.Where(b => ids.Contains(b.Id)).ToList();
        if (hit.Count == 0) return (null, "no such build");
        foreach (var b in hit)
        {
            if (op == "remove") { RemoveBuild(b); continue; }
            if (b.Status != "done") b.Status = op == "pause" ? "paused" : "queued";
            b.Rev = Touch();
        }
        return (new JsonObject { ["ids"] = new JsonArray(hit.Select(b => (JsonNode)b.Id!).ToArray()) }, null);
    }

    private void RemoveBuild(BuildState b)
    {
        Builds.Remove(b);
        Removed("build", b.Id);
        _buildAlerted.Remove(b.Id);
        foreach (var k in _leases.Keys.Where(k => k.StartsWith("g:" + b.Id + "|", StringComparison.Ordinal)).ToList()) _leases.Remove(k);
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

    private bool Occupied(P3 at)
    {
        foreach (var e in Ents())
            if (e.Kind == "player" && (int)Math.Floor(e.X) == at.X && (int)Math.Floor(e.Z) == at.Z && (at.Y == (int)Math.Floor(e.Y) || at.Y == (int)Math.Floor(e.Y) + 1))
                return true;
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
        foreach (var b in Builds.Where(b => b.Status is not ("paused" or "done")).OrderBy(b => b.Order))
            if (BuildJobFor(w, p, live, b, builders, from) is { } job) return job;
        return null;
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
        public List<GhostState> Todo;
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
    }

    /// <summary>This build's next job for the pawn, or null (and why it has none, as blocked work).</summary>
    private ActiveJob? BuildJobFor(IStoryWorld w, PawnState p, PawnLive live, BuildState b, int builders, P3 from)
    {
        var todo = b.Ghosts.Where(g => g.Job && g.State is "todo" or "leased").ToList();
        if (todo.Count == 0) { FinishOrStuck(b); return null; }
        // A design placed with a basement: its blocks wait until the ground is dug out (dig, then place).
        if (DigFirst(b)) { Blocked($"build {b.Id}: its ground is dug out first"); return null; }
        var scan = new GhostScan { Build = b, Todo = todo, Builders = builders, From = from, Mine = b.Kind == "mine" };
        var open = OpenCells(b, todo, scan.Mine);
        foreach (var g in CellOrder(b, open, ref scan, from))
        {
            if (GhostCell(w, p, live, ref scan, g) is { } job) return job;
            if (scan.SkipBuild) return null;
        }
        NoteBuildHeld(b, ref scan, open.Count);
        return null;
    }

    /// <summary>No open cell left: done, unless some are blocked (those can't be placed: said as blocked work).</summary>
    private void FinishOrStuck(BuildState b)
    {
        if (b.Ghosts.Count(g => g.Job && g.State == "blocked") is var stuckCells and > 0) { Blocked($"build {b.Id}: {stuckCells} blocks can't be placed"); return; }
        b.Status = "done";
        b.Rev = Touch();
    }

    /// <summary>
    /// The cells open now. Support first: a level waits for the one below, blocked blocks included (it never builds over a gap).
    /// Digging goes the other way: top-down (a buried cell opens once the one above it is gone). A mine goes in reachable order: a
    /// cell opens once a neighbour is open (from the entry at the surface outward and down).
    /// </summary>
    private List<GhostState> OpenCells(BuildState b, List<GhostState> todo, bool mine)
    {
        var left = b.Ghosts.Where(g => g.Job && g.State is "todo" or "leased" or "blocked").ToList();
        bool digs = left.All(g => g.Remove);
        int lowest = digs ? left.Max(g => g.At.Y) : left.Min(g => g.At.Y);
        return mine ? todo.Where(g => g.State == "todo" && Exposed(g.At)).ToList() : todo.Where(g => g.State == "todo" && g.At.Y == lowest).ToList();
    }

    /// <summary>A mine: top-down first, digs before its blocks (torches, ladders… one per job), nearest; a level: <see cref="Seeds"/>,
    /// spread out (a cell next to another pawn's batch comes after the rest: its neighbours are leased anyway). A level's cells with
    /// nothing to place against yet are left out (the client would say "nothing solid on any side"); when none of them has any and
    /// nothing else will give it, the level floats and its first cell goes on a scaffold (<see cref="FloatingSeed"/>).</summary>
    private List<GhostState> CellOrder(BuildState b, List<GhostState> open, ref GhostScan scan, P3 from)
    {
        if (scan.Mine) return open.OrderByDescending(g => g.At.Y).ThenBy(g => g.Remove ? 0 : 1).ThenBy(g => g.At.Dist(from)).ToList();
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
    private ActiveJob? GhostCell(IStoryWorld w, PawnState p, PawnLive live, ref GhostScan scan, GhostState g)
    {
        var b = scan.Build;
        var key = $"g:{b.Id}|{g.At}";
        if (!Open(key, p.Name, g.At) || Occupied(g.At)) return null;
        if (g.Remove) return DigCell(w, p, live, b, g, key);
        // A liquid source: poured from a bucket filled at a still source (StoryColony.Water.cs).
        if (StoryConstruction.IsLiquid(g)) return PourCell(p, live, ref scan, g);
        var (cur, curMeta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (CellSettled(b, g, key, cur, curMeta)) return null;
        return PlaceCell(w, p, live, ref scan, g, key, cur);
    }

    private ActiveJob? DigCell(IStoryWorld w, PawnState p, PawnLive live, BuildState b, GhostState g, string key)
    {
        if (w.Block(g.At.X, g.At.Y, g.At.Z).Id == 0 && w.Loaded(g.At.X, g.At.Z)) { g.State = "done"; g.Rev = Touch(); return null; }
        // A dug cell takes its material's tool (a mine design's ore: the right pickaxe), else the build waits for one.
        var dt = PlanTool(p, live, DigBlockAt(g.At, g.Id));
        if (dt is { Wait: { } why }) { NoteBuildWait(b, why); Blocked($"build {b.Id}: {b.Reason}"); return null; }
        ClearBuildWait(b);
        return Claim(b, [g], p.Name,
            WithTool(dt, Job(WorkTypes.Build, "dig", $"removing a block ({b.Label})", g.At, key, "ghost", $"{b.Id}|{g.At}", xp: 10)));
    }

    private ActiveJob? PourCell(PawnState p, PawnLive live, ref GhostScan scan, GhostState g)
    {
        var b = scan.Build;
        var (pj, wait) = PourJob(p, live, b, g, scan.Todo, scan.Builders);
        if (pj != null) return pj;
        if (wait) { Blocked($"build {b.Id}: {b.Reason ?? "waiting for water"}"); scan.SkipBuild = true; }
        return null;
    }

    /// <summary>
    /// The cell already holds the ghost's block: done, never a job. There but turned the wrong way: taken back and placed again once
    /// (a clear), unless it was tried already or it holds things (a chest, a furnace: never broken open for its rotation); then the
    /// cell says so on the map.
    /// </summary>
    private bool CellSettled(BuildState b, GhostState g, string key, int cur, int curMeta)
    {
        if (cur == 0) return false;
        if (Matches(g, cur, curMeta, Modern)) { CellDone(b, g); return true; }
        if (!WrongTurn(g, cur, curMeta, Modern) || _turnRedo.GetValueOrDefault(key) < 1 && !KeepsItems(cur)) return false;
        Misrotated(b, g, curMeta);
        return true;
    }

    /// <summary>What a batch of one item has to place with: carried, and stored (its own share when an earlier build reserved the rest).</summary>
    private readonly record struct CellSupply(string Item, int Have, int Stored, List<(StorageState Store, Stack Stack, int N)> Stores);

    /// <summary>A place job seeded at the cell, unless its block in the way waits for a tool or its item is out.</summary>
    private ActiveJob? PlaceCell(IStoryWorld w, PawnState p, PawnLive live, ref GhostScan scan, GhostState g, string key, int cur)
    {
        var b = scan.Build;
        // A block in the way is dug first: with its tool (stone in a wall's line takes a pickaxe). The batch clears only cells of
        // that same block (one tool per job).
        var clearTool = !StoryBlocks.Replaceable.Contains(cur) ? PlanTool(p, live, cur) : null;
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
        if (b.Status != "active") { b.Status = "active"; b.Reason = null; b.Rev = Touch(); }
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
        int openCells = scan.Todo.Count(x => (x.Item ?? "") == s.Item); // open or in a batch now: every builder's share of the whole
        int cap = scan.Mine ? 1 : Math.Min(Math.Clamp((int)Math.Ceiling(openCells / (double)scan.Builders), 1, BatchMax), s.Have + s.Stored);
        var (cells, stand) = Batch(w, b, g, cap, scan.From, p.Name, StoryBlocks.Replaceable.Contains(cur) ? -1 : cur);
        var scaffold = ScaffoldOf(scan, g); // a floating cell's: one more block, dug again
        var (fetch, takes) = BatchFetch(s.Stores, Math.Max(0, cells.Count + (scaffold != null ? 1 : 0) - s.Have));
        var want = w.Data.BlockExists(g.Id) ? w.Data.Block(g.Id).Name : null;
        var clear = cells.Where(c => !StoryBlocks.Replaceable.Contains(w.Block(c.At.X, c.At.Y, c.At.Z).Id)).Select(c => c.At.ToString()).ToList();
        var label = cells.Count == 1 ? $"building {b.Label}" : $"building {b.Label}: {cells.Count} blocks";
        var item = s.Item;
        var job = Job(WorkTypes.Build, "place", label, g.At, key, "ghost", $"{b.Id}|{g.At}", "working",
            f => PlaceFields(f, item, want, cells, clear, g, stand, fetch), xp: Math.Min(60, 8 + 4 * cells.Count));
        job.Takes.AddRange(takes);
        if (scaffold is { } spot) job.Json["scaffold"] = new JsonObject { [g.At.ToString()] = spot.ToString() };
        var claimed = Claim(b, cells, p.Name, WithTool(clear.Count > 0 ? clearTool : null, job));
        CountRedo(w, b, cells, claimed);
        return claimed;
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

    /// <summary>A wrong-turned cell this batch takes back and places again: its one second chance, counted when handed out.</summary>
    private void CountRedo(IStoryWorld w, BuildState b, List<GhostState> cells, ActiveJob claimed)
    {
        var redo = cells.Where(c => w.Block(c.At.X, c.At.Y, c.At.Z) is var (rid, rmeta) && WrongTurn(c, rid, rmeta, Modern))
            .Select(c => $"g:{b.Id}|{c.At}").ToList();
        if (redo.Count > 0 && claimed.Assign is { } assign)
            claimed.Assign = () => { assign(); foreach (var k in redo) _turnRedo[k] = _turnRedo.GetValueOrDefault(k) + 1; };
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
        else if (openCount == 0) Blocked($"build {b.Id}: the level below is still being built");
        else if (scan.Unsupported == openCount) Blocked($"build {b.Id}: {openCount} blocks have nothing to place against yet (the blocks beside or under them go in first)");
        else Blocked($"build {b.Id}: its open cells are taken or backing off");
    }

    /// <summary>The build's held status and reason; said once per reason (the build turns active between, while its other materials go in).</summary>
    private void SayBuildHeld(BuildState b, string status, string reason, bool alert)
    {
        if (b.Status == status && b.Reason == reason) return;
        b.Status = status; b.Reason = reason; b.Rev = Touch();
        if (alert && _buildBlockSaid.GetValueOrDefault(b.Id) != reason) Alert("warn", $"Build {b.Id} ({b.Kind}) is blocked: {reason}");
        _buildBlockSaid[b.Id] = reason;
    }

    /// <summary>The seeds of a level in order: only cells with something to click against beside them (<see cref="SeedReady"/>), away
    /// from other pawns' batches, then nearest.</summary>
    private IEnumerable<GhostState> Seeds(List<GhostState> open, List<P3> busy, P3 from) =>
        open.Where(SeedReady)
            .OrderBy(t => t.At.Dist(from) + (busy.Any(o => Math.Abs(o.X - t.At.X) <= StandRadius + 1 && Math.Abs(o.Z - t.At.Z) <= StandRadius + 1) ? 12 : 0));

    /// <summary>A cell holds what the ghost wants: the same block and variant, turned the same way where the placement decides it (a
    /// furnace's front, a stair's rise and half, a log's axis: <see cref="MineClaw.World.BlockOrientation.Same"/>); runtime bits
    /// (open, powered, lit) are not compared. What the block turns into on its own counts as it (<see cref="StoryDrift"/>: dirt that
    /// grew grass or was tilled, a lit furnace).</summary>
    internal static bool Matches(GhostState g, int id, int meta, bool modern)
    {
        if (StoryConstruction.IsLiquid(g)) return LiquidSource(g.Id, id, meta); // a source of its liquid, still or just poured (StoryColony.Water.cs)
        if (StoryDrift.SameGround(g.Id, id, modern)) return true;
        return SameVariant(g, id, meta) && MineClaw.World.BlockOrientation.Same(g.Id, g.Meta, meta, modern);
    }

    /// <summary>The ghost's block and variant (a runtime twin, a lit furnace, is its block).</summary>
    private static bool SameVariant(GhostState g, int id, int meta)
    {
        if (StoryDrift.Canon(id) != StoryDrift.Canon(g.Id)) return false;
        int mask = MineClaw.Bot.Blueprints.DesignFacing.VariantMask(g.Id);
        return (meta & mask) == (g.Meta & mask);
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

    /// <summary>Cells taken back once for a wrong rotation (<c>g:build|x,y,z</c>): the second time the cell is left misrotated.</summary>
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
            Alert("info", $"Build {b.Id} ({b.Label}) is done");
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
        if (!BatchKin(seed, x) || !NearSeedLevel(seed, x)) return false;
        var key = $"g:{b.Id}|{x.At}";
        if (!Open(key, pawn, x.At) || Occupied(x.At)) return false;
        var (cur, curMeta) = w.Block(x.At.X, x.At.Y, x.At.Z);
        if (CellSettled(b, x, key, cur, curMeta)) return false;
        return StoryBlocks.Replaceable.Contains(cur) || cur == clearId; // another block to clear: another tool, another job
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
        bool SolidOrPlaced(P3 c) => inBatch.Contains(c) || Clickable(c); // the client's own support rule
        while (batch.Count < cap && rest.Count > 0)
        {
            GhostState? next = null;
            double best = double.MaxValue;
            foreach (var c in rest)
            {
                if (!StoryConstruction.Supported(c.At, SolidOrPlaced)) continue;
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
    /// earlier build of its group still has cells to remove.
    /// </summary>
    private bool DigFirst(BuildState b)
    {
        static string? Group(BuildState x) => x.Spec?["group"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        // A mine orders its own cells (a block goes in once its cell is open).
        if (b.Kind == "mine" || Group(b) is not { } group || b.Ghosts.All(g => g.Remove)) return false;
        // A dig that keeps failing (blocked) doesn't hold the build forever: a placement digs its own cell anyway.
        return Builds.Any(o => o != b && o.Order < b.Order && o.Status != "done" && Group(o) == group
                               && o.Ghosts.Any(g => g.Remove && g.State is "todo" or "leased"));
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
        if (FindGhost(id) is not { } g || !anyState && g.State == "done") return null;
        return Builds.FirstOrDefault(x => x.Ghosts.Contains(g)) is { } b ? (g, b) : null;
    }

    /// <summary>One cell's report: built (done), turned the wrong way (misrotated), else a failure of that cell.</summary>
    private void CellReported(PawnState p, BuildState b, GhostState g, string id, string status)
    {
        if (ReportedBuilt(p.Name, g, status)) { CellDone(b, g); return; }
        // Placed, but turned the wrong way: the client already took it back and tried once more (or couldn't): the cell says so.
        if (View(p.Name, g) is var (tid, tmeta) && WrongTurn(g, tid, tmeta, Modern)
            && (status.Contains("wrong rotation", StringComparison.OrdinalIgnoreCase) || status == "done"))
        { Misrotated(b, g, tmeta); return; }
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
        (GhostState G, BuildState B, string Id)? first = null;
        foreach (var id in JobCells(j))
        {
            if (OpenGhost(id) is not (var g, var b)) continue;
            if (!g.Remove && Built(p.Name, g)) { CellDone(b, g); continue; }
            first ??= (g, b, id);
        }
        j.Cells?.Clear();
        if (first is (var fg, var fb, var fid)) CellFailed(p, fb, fg, fid, why, counted: true);
    }

    private void CellDone(BuildState b, GhostState g)
    {
        if (g.State == "done") return;
        g.State = "done";
        g.Holds = g.HoldsWords = null;
        g.Rev = Touch();
        MaterialsChanged();
        StationBuilt(g); // a brewing stand, anvil or enchanting table the colony built is its own (StoryColony.Stations.cs)
        // A door's / bed's other half came with it.
        foreach (var other in b.Ghosts.Where(o => !o.Job && o.State != "done" && Math.Abs(o.At.X - g.At.X) + Math.Abs(o.At.Y - g.At.Y) + Math.Abs(o.At.Z - g.At.Z) == 1))
        { other.State = "done"; other.Rev = g.Rev; }
        if (b.Ghosts.Where(x => x.Job).All(x => x.State is "done" or "misrotated"))
        {
            b.Status = "done"; b.Reason = null; b.Rev = Touch();
            Alert("info", $"Build {b.Id} ({b.Label}) is done");
            // An infinite pool the colony made: pourers fill their buckets there from now on (StoryColony.Water.cs).
            if (LiquidOfBuild(b) == "water" && Renewable(b)) Alert("info", $"Build {b.Id} ({b.Label}) refills: pawns fill their buckets there from now on");
        }
    }

    /// <summary>
    /// One cell that didn't go in. "Not now" (out of reach from where the pawn got to, someone in the cell, the cell taken by something
    /// else since: the next pick clears it) rests it a little and doesn't count, up to <see cref="SoftLimit"/> in a row; anything else
    /// counts, and the third failure blocks the cell with an alert. <paramref name="counted"/>: the job's own failure (the picker
    /// already rested it and handled its soft case).
    /// </summary>
    private void CellFailed(PawnState p, BuildState b, GhostState g, string id, string why, bool counted = false)
    {
        var key = "g:" + id;
        if (!counted)
        {
            _log.LogInformation("story {Colony}: {Pawn} cell {At} failed: {Why}", Id, p.Name, g.At, why);
            if (CellRested(key, why)) return;
        }
        int n = _ghostFails[id] = Math.Max(_ghostFails.GetValueOrDefault(id), BlockedAtOnce(g, why, counted) ? 2 : 0) + 1;
        if (!counted) _skip[key] = Now + Backoff(n);
        if (g.State is "leased" or "todo") g.State = n >= 3 ? "blocked" : "todo";
        g.Reason = why; // the cell's last failure: the map's marker says it, Retry clears it (StoryColony.BuildRetry.cs)
        g.Rev = Touch();
        if (n >= 3) SayCellBlocked(p, b, g, why);
    }

    /// <summary>
    /// A failure no retry will mend, so the cell is blocked at once: a pour cell with nothing to pour against (live 11-04: 371 of them,
    /// each tried 6 times), or a clear the pawn's dig refused for good (protected, a chest, a never-break mark: never tried again and
    /// again, live 11-05).
    /// </summary>
    private static bool BlockedAtOnce(GhostState g, string why, bool counted) =>
        StoryConstruction.IsLiquid(g) && why.Contains("nothing beside or under", StringComparison.Ordinal) || !counted && ClearRefused(why);

    /// <summary>"Nothing solid on any side": the pawn's view had no face to click yet (a neighbour placed a moment ago): not now.</summary>
    private static bool NoFaceYet(string why) => why.Contains("nothing solid on any side", StringComparison.Ordinal);

    /// <summary>A "not now" failure rests the cell a little instead of counting, up to <see cref="SoftLimit"/> in a row.</summary>
    private bool CellRested(string key, string why)
    {
        if (!Transient(why) && !why.Contains("is occupied by", StringComparison.OrdinalIgnoreCase) && !NoFaceYet(why)) return false;
        int soft = _softFails[key] = _softFails.GetValueOrDefault(key) + 1;
        if (soft < SoftLimit) { _skip[key] = Now + SoftRest; return true; }
        _softFails.Remove(key);
        return false;
    }
}
