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
            switch (op)
            {
                case "add":
                {
                    if (items is not { Count: > 0 }) return (null, "items required");
                    if (items.Count > 64) return (null, "at most 64 items per request");
                    var made = new List<BuildState>();
                    var warnings = new List<string>();
                    JsonObject? flow = null;
                    foreach (var n in items)
                    {
                        if (n is not JsonObject item) return (null, "each item is an object {kind, area|blocks, material, ...}");
                        var (ghosts, mat, err) = StoryConstruction.Expand(w.Data, item, w);
                        if (err != null) return (null, err);
                        if (ghosts.Count > 4096) return (null, "an item expands to at most 4096 blocks");
                        // Liquids: the cells must take the water (open, or dug first by the item's group), lava only when allowed (StoryColony.Water.cs).
                        if (ghosts.Count > 0 && StoryConstruction.IsLiquid(ghosts[0]) && CheckLiquid(w, item, ghosts, made, warnings) is { } lerr) return (null, lerr);
                        made.Add(new BuildState
                        {
                            Kind = item["kind"]!.GetValue<string>().ToLowerInvariant(), Spec = (JsonObject)item.DeepClone(), Ghosts = ghosts,
                            Material = mat?.Name ?? "", Created = Now,
                        });
                    }
                    int order = Builds.Count == 0 ? 0 : Builds.Max(b => b.Order) + 1;
                    foreach (var b in made)
                    {
                        b.Id = NextId("b");
                        b.Order = order++;
                        b.Label = $"{b.Kind} ({b.BlocksTotal} block{(b.BlocksTotal == 1 ? "" : "s")}{(b.Material.Length > 0 && b.Kind != "remove" ? ", " + b.Material : "")})";
                        var r = Touch();
                        b.Rev = r;
                        foreach (var g in b.Ghosts)
                        {
                            g.Rev = r;
                            // Already there (a wall over a finished one): done at once.
                            var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
                            if (!g.Remove && id == g.Id && (g.Meta == meta || !g.Job)) g.State = "done";
                            if (g.Remove && id == 0 && w.Loaded(g.At.X, g.At.Z)) g.State = "done";
                        }
                        Builds.Add(b);
                        if (b.Ghosts.Count > 0 && StoryConstruction.IsLiquid(b.Ghosts[0])) flow = LiquidAdded(w, b, flow); // its label, the expected flow
                        // Blocks the pawns can't place yet (crops, liquids, signs…): shown as ghosts and said once, never a stuck job.
                        if (b.Ghosts.Where(g => g.State == "unsupported").ToList() is { Count: > 0 } odd)
                            Alert("warn", $"Build {b.Id} ({b.Kind}): {odd.Count} block{(odd.Count == 1 ? "" : "s")} the pawns can't place yet, left as ghosts: "
                                          + string.Join("; ", odd.Select(g => g.Reason ?? "special placement").Distinct().Take(3)));
                    }
                    WakeIdle();
                    var added = new JsonObject { ["ids"] = new JsonArray(made.Select(b => (JsonNode)b.Id!).ToArray()) };
                    if (warnings.Count > 0) added["warnings"] = new JsonArray(warnings.Distinct().Select(x => (JsonNode)x!).ToArray());
                    if (flow != null) added["flow"] = flow;
                    return (added, null);
                }
                case "preview":
                    return LiquidPreview(w, items); // a water item's cells checked and its expected flow, nothing queued (StoryColony.Water.cs)
                case "remove":
                case "pause":
                case "resume":
                {
                    if (ids is not { Count: > 0 }) return (null, "ids required");
                    var hit = Builds.Where(b => ids.Contains(b.Id)).ToList();
                    if (hit.Count == 0) return (null, "no such build");
                    foreach (var b in hit)
                    {
                        if (op == "remove")
                        {
                            Builds.Remove(b);
                            Removed("build", b.Id);
                            _buildAlerted.Remove(b.Id);
                            foreach (var k in _leases.Keys.Where(k => k.StartsWith("g:" + b.Id + "|", StringComparison.Ordinal)).ToList()) _leases.Remove(k);
                            continue;
                        }
                        if (b.Status != "done") b.Status = op == "pause" ? "paused" : "queued";
                        b.Rev = Touch();
                    }
                    return (new JsonObject { ["ids"] = new JsonArray(hit.Select(b => (JsonNode)b.Id!).ToArray()) }, null);
                }
                case "reorder":
                {
                    if (ids is not { Count: > 0 }) return (null, "ids required");
                    if (ids.Any(i => Builds.All(b => b.Id != i))) return (null, "unknown build id");
                    // The named ones first, in that order; the rest keep their relative order after them.
                    var rest = Builds.Where(b => !ids.Contains(b.Id)).OrderBy(b => b.Order).ToList();
                    int o = 0;
                    foreach (var id in ids.Distinct()) { var b = Builds.First(x => x.Id == id); b.Order = o++; b.Rev = Touch(); }
                    foreach (var b in rest) { b.Order = o++; b.Rev = Touch(); }
                    return (new JsonObject { ["ids"] = new JsonArray(Builds.OrderBy(b => b.Order).Select(b => (JsonNode)b.Id!).ToArray()) }, null);
                }
                default:
                    return (null, "op must be add|preview|remove|reorder|pause|resume");
            }
        }
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
    private ActiveJob? GhostJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        EnsureMaterials(); // what each build may take (reserved in queue order) and what is being crafted for it
        var from = live.Block;
        int builders = 0;
        foreach (var o in Pawns.Values)
            if (o.Priorities.GetValueOrDefault(WorkTypes.Build) is >= 1 and <= 4 && !o.Drafted && Host.Live(o.Name).Online) builders++;
        builders = Math.Max(1, builders);
        foreach (var b in Builds.Where(b => b.Status is not ("paused" or "done")).OrderBy(b => b.Order))
        {
            var todo = b.Ghosts.Where(g => g.Job && g.State is "todo" or "leased").ToList();
            if (todo.Count == 0)
            {
                if (b.Ghosts.Any(g => g.Job && g.State == "blocked")) continue;
                b.Status = "done"; b.Rev = Touch();
                continue;
            }
            // A design placed with a basement: its blocks wait until the ground is dug out (dig, then place).
            if (DigFirst(b)) continue;
            // Support first: a level waits for the one below, blocked blocks included (it never builds over a gap). Digging goes
            // the other way: top-down (a buried cell opens once the one above it is gone).
            var left = b.Ghosts.Where(g => g.Job && g.State is "todo" or "leased" or "blocked").ToList();
            bool digs = left.All(g => g.Remove);
            int lowest = digs ? left.Max(g => g.At.Y) : left.Min(g => g.At.Y);
            // A mine goes in reachable order: a cell opens once a neighbour is open (from the entry at the surface outward and down),
            // top-down first; its blocks (torches, ladders…) go in as their cells open, one per job.
            bool mine = b.Kind == "mine";
            var open = mine ? todo.Where(g => g.State == "todo" && Exposed(g.At)).ToList() : todo.Where(g => g.State == "todo" && g.At.Y == lowest).ToList();
            // Spread out: a cell next to another pawn's batch comes after the rest (its neighbours are leased anyway).
            var busy = b.Ghosts.Where(g => g.State == "leased").Select(g => g.At).ToList();
            foreach (var g in mine ? open.OrderByDescending(g => g.At.Y).ThenBy(g => g.Remove ? 0 : 1).ThenBy(g => g.At.Dist(from)) : Seeds(open, busy, from))
            {
                var key = $"g:{b.Id}|{g.At}";
                if (!Open(key, p.Name, g.At) || Occupied(g.At)) continue;
                if (g.Remove)
                {
                    if (w.Block(g.At.X, g.At.Y, g.At.Z).Id == 0 && w.Loaded(g.At.X, g.At.Z)) { g.State = "done"; g.Rev = Touch(); continue; }
                    // A dug cell takes its material's tool (a mine design's ore: the right pickaxe), else the build waits for one.
                    var dt = PlanTool(p, live, DigBlockAt(g.At, g.Id));
                    if (dt is { Wait: { } why }) { NoteBuildWait(b, why); continue; }
                    ClearBuildWait(b);
                    return Claim(b, [g], p.Name,
                        WithTool(dt, Job(WorkTypes.Build, "dig", $"removing a block ({b.Label})", g.At, key, "ghost", $"{b.Id}|{g.At}", xp: 10)));
                }
                // A liquid source: poured from a bucket filled at a still source (StoryColony.Water.cs).
                if (StoryConstruction.IsLiquid(g))
                {
                    var (pj, wait) = PourJob(p, live, b, g, todo, builders);
                    if (pj != null) return pj;
                    if (wait) goto nextBuild;
                    continue;
                }
                var (cur, curMeta) = w.Block(g.At.X, g.At.Y, g.At.Z);
                if (cur != 0 && Matches(g, cur, curMeta)) { CellDone(b, g); continue; } // already there: done, never a job
                // A block in the way is dug first: with its tool (stone in a wall's line takes a pickaxe). The batch clears only cells
                // of that same block (one tool per job).
                var clearTool = !StoryBlocks.Replaceable.Contains(cur) ? PlanTool(p, live, cur) : null;
                if (clearTool is { Wait: { } cwhy }) { NoteBuildWait(b, cwhy); continue; }
                var item = g.Item ?? w.Data.Item(g.Id).Name;
                var ik = KeyOfItem(w, item, g.Id);
                int have = live.Inventory.Where(s => s.Id == ik.Id && s.Meta == ik.Meta).Sum(s => s.Count);
                var mat = b.Materials.FirstOrDefault(m => m.Item == item);
                // What an earlier build reserved stays for it: this one takes from storage only its own share.
                bool reservedAway = mat is { Have: 0 };
                var stores = reservedAway ? [] : Stored(ik, from);
                int stored = stores.Sum(s => s.N);
                if (have == 0 && stored == 0)
                {
                    // Another builder carries this build's items (its batch is in flight): no job here now, and nothing is blocked.
                    if (_current.Values.Any(j => j.Source == "ghost" && j.Cells is { Count: > 0 } && j.SourceId?.StartsWith(b.Id + "|", StringComparison.Ordinal) == true))
                        goto nextBuild;
                    int need = b.Ghosts.Count(x => x.Job && x.State is "todo" or "leased" && (x.Item ?? "") == item);
                    var display = w.Data.DisplayName(ik.Id, ik.Meta).ToLowerInvariant();
                    string status = "blocked", reason;
                    bool alert = true;
                    if (mat is { Crafting: > 0 }) { status = "waiting"; alert = false; reason = $"waiting for {mat.Crafting} {display} (being crafted)"; }
                    else if (reservedAway && Stored(ik, from).Count > 0)
                    { status = "waiting"; alert = false; reason = $"waiting: the {display} in storage is reserved for an earlier build"; }
                    else
                    {
                        reason = $"need {need} {display}";
                        alert = mat is not { Short: > 0 }; // the materials pass already said what's short and why
                    }
                    if (b.Status != status || b.Reason != reason)
                    {
                        b.Status = status; b.Reason = reason; b.Rev = Touch();
                        if (alert) Alert("warn", $"Build {b.Id} ({b.Kind}) is blocked: {reason}");
                    }
                    goto nextBuild;
                }
                if (b.Status != "active") { b.Status = "active"; b.Reason = null; b.Rev = Touch(); }
                // The pawn's fair share of what is open (ten builders on one floor each get a part), no more than it can get items for.
                int openCells = todo.Count(x => (x.Item ?? "") == item); // open or in a batch now: every builder's share of the whole
                int cap = mine ? 1 : Math.Min(Math.Clamp((int)Math.Ceiling(openCells / (double)builders), 1, BatchMax), have + stored);
                var (cells, stand) = Batch(w, b, g, cap, from, p.Name, StoryBlocks.Replaceable.Contains(cur) ? -1 : cur);
                int fetchN = Math.Max(0, cells.Count - have);
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
                var want = w.Data.BlockExists(g.Id) ? w.Data.Block(g.Id).Name : null;
                var clear = cells.Where(c => !StoryBlocks.Replaceable.Contains(w.Block(c.At.X, c.At.Y, c.At.Z).Id)).Select(c => c.At.ToString()).ToList();
                var label = cells.Count == 1 ? $"building {b.Label}" : $"building {b.Label}: {cells.Count} blocks";
                var job = Job(WorkTypes.Build, "place", label, g.At, key, "ghost", $"{b.Id}|{g.At}", "working", f =>
                {
                    f["block"] = item;
                    if (want != null) f["want"] = want; // what the cell shows once built: already there = done
                    f["cells"] = new JsonArray(cells.Select(c => (JsonNode)c.At.ToString()).ToArray());
                    if (clear.Count > 0) f["clear"] = new JsonArray(clear.Select(c => (JsonNode)c).ToArray());
                    if (g.Facing is { } fc) f["facing"] = fc;
                    if (stand is { } sp) f["stand"] = sp.ToString(); // where the whole batch is within reach
                    if (fetch.Count > 0) f["fetch"] = fetch;
                }, xp: Math.Min(60, 8 + 4 * cells.Count));
                job.Takes.AddRange(takes);
                return Claim(b, cells, p.Name, WithTool(clear.Count > 0 ? clearTool : null, job));
            }
            nextBuild:;
        }
        return null;
    }

    /// <summary>The seeds of a level in order: support first (touching something solid), then away from other pawns' batches, then nearest.</summary>
    private IEnumerable<GhostState> Seeds(List<GhostState> open, List<P3> busy, P3 from) =>
        open.OrderBy(t => StoryConstruction.Supported(t.At, Solid) ? 0 : 1)
            .ThenBy(t => t.At.Dist(from) + (busy.Any(o => Math.Abs(o.X - t.At.X) <= StandRadius + 1 && Math.Abs(o.Z - t.At.Z) <= StandRadius + 1) ? 12 : 0));

    /// <summary>A cell holds what the ghost wants: the same block and variant (a facing is the placement's own).</summary>
    internal static bool Matches(GhostState g, int id, int meta)
    {
        if (StoryConstruction.IsLiquid(g)) return LiquidSource(g.Id, id, meta); // a source of its liquid, still or just poured (StoryColony.Water.cs)
        if (id != g.Id) return false;
        int mask = MineClaw.Bot.Blueprints.DesignFacing.VariantMask(id);
        return (meta & mask) == (g.Meta & mask);
    }

    /// <summary>Whether the cell is built (see <see cref="View"/>).</summary>
    private bool Built(string pawn, GhostState g) => View(pawn, g) is var (id, meta) && id != 0 && Matches(g, id, meta);

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
            int inFlight = 0, fetchers = 0;
            foreach (var j in _current.Values)
            {
                bool any = false;
                foreach (var (chest, t) in j.Takes)
                    if (chest == s.Chest || chest == s.DoubleWith)
                    {
                        any = true;
                        if (t.Id == key.Id && t.Meta == key.Meta) inFlight += t.Count;
                    }
                if (any) fetchers++;
            }
            int n = s.Contents.Where(c => c.Id == key.Id && c.Meta == key.Meta).Sum(c => c.Count) - inFlight;
            if (n > 0) list.Add((s, st, n, fetchers));
        }
        return list.OrderBy(x => x.Fetchers).ThenBy(x => x.S.Chest.Dist(near)).Select(x => (x.S, x.St, x.N)).ToList();
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
        // One level: the next goes up once this one stands (b1.8.1 FakeServer: a roof row laid early, over a wall still going up, had
        // no visible support face, and its builder climbed onto the wall and was shut in).
        var cand = new List<GhostState>();
        if (cap > 1)
            foreach (var x in b.Ghosts)
            {
                if (x == seed || !x.Job || x.Remove || x.State != "todo" || x.Item != seed.Item || x.Facing != seed.Facing) continue;
                if (Math.Abs(x.At.X - seed.At.X) > WalkRadius || Math.Abs(x.At.Z - seed.At.Z) > WalkRadius || x.At.Y != seed.At.Y) continue;
                if (!Open($"g:{b.Id}|{x.At}", pawn, x.At) || Occupied(x.At)) continue;
                var (cur, curMeta) = w.Block(x.At.X, x.At.Y, x.At.Z);
                if (cur != 0 && Matches(x, cur, curMeta)) { CellDone(b, x); continue; }
                if (!StoryBlocks.Replaceable.Contains(cur) && cur != clearId) continue; // another block to clear: another tool, another job
                cand.Add(x);
            }
        var stand = StandFor(w, b, seed, cand.Where(x => Math.Abs(x.At.X - seed.At.X) <= StandRadius && Math.Abs(x.At.Z - seed.At.Z) <= StandRadius).ToList(), from);
        var inBatch = new HashSet<P3> { seed.At };
        var batch = new List<GhostState> { seed };
        // The stand may be an open cell of the batch's patch (the pawn stands in the middle of it): that cell is left for later.
        bool InReach(P3 c) => stand is { } s ? EyeDist(s, c) <= BuildReach : Math.Abs(c.X - seed.At.X) <= 1 && Math.Abs(c.Z - seed.At.Z) <= 1;
        // A wall's builder works from outside only: no cells it would have to walk to (it could end up inside and be walled in).
        bool walled = Footprint(b).Walled;
        var rest = cand.Where(c => (stand is not { } s || c.At != s && c.At != s.Add(0, 1, 0)) && (!walled || InReach(c.At))).ToList();
        bool SolidOrPlaced(P3 c) => inBatch.Contains(c) || Solid(c);
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
        return (batch, stand);
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
        var (walled, x1, z1, x2, z2) = Footprint(b);
        var taken = new HashSet<P3>();
        foreach (var j in _current.Values)
            if (j.Source == "ghost" && j.Json["stand"] is JsonValue tv && tv.TryGetValue<string>(out var ts) && ParseCell(ts) is { } tp) taken.Add(tp);
        P3? best = null;
        double bestScore = double.MaxValue;
        for (int dx = -StandRadius - 2; dx <= StandRadius + 2; dx++)
            for (int dz = -StandRadius - 2; dz <= StandRadius + 2; dz++)
                for (int dy = -3; dy <= 1; dy++) // a roof or a wall's top row is laid from the ground below it
                {
                    var s = seed.At.Add(dx, dy, dz);
                    if (s == seed.At || s.Add(0, 1, 0) == seed.At || cells.Contains(s.Add(0, -1, 0))) continue;
                    if (EyeDist(s, seed.At) > BuildReach) continue;
                    if (walled && s.X >= x1 && s.X <= x2 && s.Z >= z1 && s.Z <= z2) continue; // never on or inside the walls
                    if (!w.Loaded(s.X, s.Z) || !Standable(w, s)) continue;
                    int reach = 0;
                    foreach (var c in cells) if (c != s && c != s.Add(0, 1, 0) && EyeDist(s, c) <= BuildReach) reach++;
                    double score = -10.0 * reach + 0.05 * s.Dist(from)
                        + (pending.Contains(s) || pending.Contains(s.Add(0, 1, 0)) ? 4 : 0)
                        + (taken.Contains(s) ? 25 : 0);
                    if (score < bestScore) { bestScore = score; best = s; }
                }
        return best;
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
        foreach (var id in (IEnumerable<string>?)j.Cells ?? (j.SourceId != null ? [j.SourceId] : []))
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
        if (note == "fetched")
        {
            foreach (var (chest, s) in j.Takes) if (StorageAt(chest) is { } st) TakeContents(st, s.Id, s.Meta, s.Count);
            j.Takes.Clear();
            MaterialsChanged();
            return;
        }
        if (!note.StartsWith("cell ", StringComparison.Ordinal) || j.Cells == null) return;
        int sp = note.IndexOf(' ', 5);
        if (sp < 0 || ParseCell(note[5..sp]) is not { } at) return;
        var id = j.Cells.FirstOrDefault(c => c.EndsWith("|" + at, StringComparison.Ordinal));
        if (id == null || FindGhost(id) is not { } g || Builds.FirstOrDefault(x => x.Ghosts.Contains(g)) is not { } b) return;
        j.Cells.Remove(id);
        if (j.Lease != "g:" + id && _leases.TryGetValue("g:" + id, out var l) && string.Equals(l.Owner, p.Name, StringComparison.OrdinalIgnoreCase)) _leases.Remove("g:" + id);
        var status = note[(sp + 1)..];
        // Done when the world agrees, or doesn't know yet (its copy of the block change can come a moment later); a cell that holds
        // something else (another variant: the script saw the block's name) still has to be cleared and built.
        if (Built(p.Name, g) || status == "done" && View(p.Name, g) is var (vid, _) && (StoryConstruction.IsLiquid(g) ? vid == 0 : OpenCell(vid))) { CellDone(b, g); return; }
        if (status == "done")
            status = $"failed: {g.At} is occupied by {(World?.Data is { } d && d.BlockExists(View(p.Name, g).Id) ? d.Block(View(p.Name, g).Id).Name : "another block")}";
        if (g.State == "leased") { g.State = "todo"; g.Rev = Touch(); }
        CellFailed(p, b, g, id, status.StartsWith("failed: ", StringComparison.Ordinal) ? status[8..] : status);
    }

    /// <summary>The job ended "done": each cell it didn't report is checked against the world (built = done, else back to the queue, no
    /// failure). An older pawn script reports only the whole job (no <c>batch</c> note, no cell lines): its first cell is built.</summary>
    private void BatchDone(PawnState p, ActiveJob j, string? note)
    {
        if (j.Cells == null) return;
        bool legacy = !(note ?? "").StartsWith("batch", StringComparison.Ordinal) && (j.Json["cells"] as JsonArray)?.Count == j.Cells.Count;
        foreach (var id in j.Cells.ToList())
        {
            if (FindGhost(id) is not { } g || g.State == "done" || Builds.FirstOrDefault(x => x.Ghosts.Contains(g)) is not { } b) continue;
            // A dig job is one cell, and its "done" is taken as before.
            if (g.Remove || Built(p.Name, g) || legacy && id == j.SourceId) CellDone(b, g);
        }
        j.Cells.Clear();
    }

    /// <summary>The job failed as a whole (a fetch, a script error): cells that are built still count; the failure counts against its
    /// first open cell.</summary>
    private void BatchFailed(PawnState p, ActiveJob j, string why)
    {
        GhostState? first = null;
        BuildState? fb = null;
        string? firstId = null;
        foreach (var id in (IEnumerable<string>?)j.Cells ?? (j.SourceId != null ? [j.SourceId] : []))
        {
            if (FindGhost(id) is not { } g || g.State == "done" || Builds.FirstOrDefault(x => x.Ghosts.Contains(g)) is not { } b) continue;
            if (!g.Remove && Built(p.Name, g)) { CellDone(b, g); continue; }
            if (first == null) { first = g; fb = b; firstId = id; }
        }
        j.Cells?.Clear();
        if (first != null) CellFailed(p, fb!, first, firstId!, why, counted: true);
    }

    private void CellDone(BuildState b, GhostState g)
    {
        if (g.State == "done") return;
        g.State = "done";
        g.Rev = Touch();
        MaterialsChanged();
        StationBuilt(g); // a brewing stand, anvil or enchanting table the colony built is its own (StoryColony.Stations.cs)
        // A door's / bed's other half came with it.
        foreach (var other in b.Ghosts.Where(o => !o.Job && o.State != "done" && Math.Abs(o.At.X - g.At.X) + Math.Abs(o.At.Y - g.At.Y) + Math.Abs(o.At.Z - g.At.Z) == 1))
        { other.State = "done"; other.Rev = g.Rev; }
        if (b.Ghosts.Where(x => x.Job).All(x => x.State == "done"))
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
        if (!counted) _log.LogInformation("story {Colony}: {Pawn} cell {At} failed: {Why}", Id, p.Name, g.At, why);
        if (!counted && (Transient(why) || why.Contains("is occupied by", StringComparison.OrdinalIgnoreCase)))
        {
            int soft = _softFails[key] = _softFails.GetValueOrDefault(key) + 1;
            if (soft < SoftLimit) { _skip[key] = Now + SoftRest; return; }
            _softFails.Remove(key);
        }
        int n = _ghostFails[id] = _ghostFails.GetValueOrDefault(id) + 1;
        if (!counted) _skip[key] = Now + Backoff(n);
        if (g.State is "leased" or "todo") g.State = n >= 3 ? "blocked" : "todo";
        g.Rev = Touch();
        if (n >= 3)
        {
            b.Reason = $"can't place at {g.At}: {why}";
            b.Rev = Touch();
            Alert("warn", $"Build {b.Id}: {b.Reason}", p.Name);
        }
    }
}
