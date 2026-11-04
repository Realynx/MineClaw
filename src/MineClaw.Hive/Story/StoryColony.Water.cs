using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// Water (docs/web/story-mode.md "Water"; Fox 11-04: "I need a way to make them pour water down in different blocks so that I can make
/// aquifers and hydroponics for different farming"). A <c>water</c> build item is a set of cells that each become a still source of its
/// liquid (water; lava only with the colony option <see cref="ColonyMeta.AllowLava"/>), queued like any build so a design's dig goes
/// first (a trench, then the water). A pawn with Build on gets a batch of them as one <c>pour</c> job: it brings a bucket (its own,
/// one from storage, else the materials pass crafts one: 3 iron ingots), fills it at the nearest still source that won't be used up (an
/// infinite pool first: one that refills, then a lake; never a lone puddle or water the colony placed and that doesn't refill), pours
/// each cell from one stand aimed at that exact cell, and the cell is done once the world shows a source there. Every other cell of a
/// channel first: the ones between two sources fill by themselves (the game's rule), so a canal or a 2×2 pool takes half the trips.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>Cells one pour job takes at most (each is a fill and a pour, or a fill per two cells with two buckets).</summary>
    public const int PourBatchMax = 12;
    /// <summary>Buckets a pourer carries at most (fewer trips to the source).</summary>
    public const int PourBuckets = 2;
    /// <summary>How far (horizontally) from a liquid cell a fill source is looked for.</summary>
    public const int SourceRadius = 24;
    /// <summary>How far water (lava) runs sideways from a source on flat ground, and how far it falls before it is counted as lost.</summary>
    private const int WaterReach = 7, LavaReach = 3, MaxFall = 12, MaxFlow = 512;

    /// <summary>Cells a poured liquid takes: air, and what water washes away (grass, flowers, snow, vines); flowing liquid too (a source
    /// poured into it replaces it).</summary>
    private static readonly HashSet<int> TakesLiquidIds = [0, 31, 32, 37, 38, 78, 106, 175];

    /// <summary>Blocks lava would set alight (and the ones it ruins).</summary>
    private static readonly HashSet<int> Flammable =
        [5, 17, 18, 35, 46, 47, 53, 54, 58, 85, 106, 31, 107, 125, 126, 134, 135, 136, 161, 162, 163, 164, 170, 171, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192];

    /// <summary>A source of the ghost's liquid: water (8 or 9, still or just poured) or lava (10 or 11), meta 0.</summary>
    internal static bool LiquidSource(int ghostId, int id, int meta) => meta == 0 && (ghostId is 8 or 9 ? id is 8 or 9 : id is 10 or 11);

    private static bool IsWaterId(int id) => id is 8 or 9;
    private static bool IsLavaId(int id) => id is 10 or 11;
    private static string LiquidName(GhostState g) => IsLavaId(g.Id) ? "lava" : "water";

    /// <summary>The liquid a build pours ("water" | "lava"), null for any other build.</summary>
    internal static string? LiquidOfBuild(BuildState b) => b.Ghosts.FirstOrDefault() is { } g && StoryConstruction.IsLiquid(g) ? LiquidName(g) : null;

    /// <summary>A water build whose poured sources refill (an infinite pool): pawns fill their buckets there first.</summary>
    internal bool Renewable(BuildState b) => World is { } w && b.Ghosts.Any(g => g.State == "done" && StoryConstruction.IsLiquid(g) && Refills(w, g.At));

    /// <summary>A water source that comes back after scooping (BlockFlowing): at least two horizontal source neighbours, over a solid
    /// block or a water source.</summary>
    internal static bool Refills(IStoryWorld w, P3 p)
    {
        if (!IsSourceAt(w, p, lava: false)) return false;
        int n = 0;
        foreach (var (dx, dz) in Sides) if (IsSourceAt(w, p.Add(dx, 0, dz), lava: false)) n++;
        if (n < 2) return false;
        var (below, bm) = w.Block(p.X, p.Y - 1, p.Z);
        return !StoryBlocks.Replaceable.Contains(below) || IsWaterId(below) && bm == 0;
    }

    private static readonly (int Dx, int Dz)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static bool IsSourceAt(IStoryWorld w, P3 p, bool lava)
    {
        var (id, meta) = w.Block(p.X, p.Y, p.Z);
        return meta == 0 && (lava ? IsLavaId(id) : IsWaterId(id));
    }

    /// <summary>A cell holding something liquid can't go in (a block to dig first).</summary>
    private static bool TakesLiquid(int id) => TakesLiquidIds.Contains(id) || StoryBlocks.Liquids.Contains(id);

    /// <summary>Indoors: in a house (a home zone) with a roof over it (something solid within 8 above).</summary>
    private bool Indoors(IStoryWorld w, P3 p)
    {
        if (HomeZoneAt(p.X, p.Z) == null) return false;
        for (int dy = 1; dy <= 8; dy++)
            if (!StoryBlocks.Replaceable.Contains(w.Block(p.X, p.Y + dy, p.Z).Id)) return true;
        return false;
    }

    private static P3? FlammableNear(IStoryWorld w, P3 p, int r = 2)
    {
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -1; dy <= r; dy++)
                for (int dz = -r; dz <= r; dz++)
                    if (Flammable.Contains(w.Block(p.X + dx, p.Y + dy, p.Z + dz).Id)) return p.Add(dx, dy, dz);
        return null;
    }

    private string BlockLabel(IStoryWorld w, int id) => w.Data.BlockExists(id) ? w.Data.Block(id).DisplayName.ToLowerInvariant() : $"block {id}";

    private static string? Group(JsonObject? spec) => spec?["group"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>The cells an item's group digs before it (earlier items of this request, queued builds of that group).</summary>
    private HashSet<P3> DugFirst(JsonObject item, IEnumerable<BuildState> made)
    {
        var dug = new HashSet<P3>();
        if (Group(item) is not { } group) return dug;
        foreach (var o in made.Concat(Builds))
            if (Group(o.Spec) == group)
                foreach (var g in o.Ghosts) if (g.Remove && g.State != "done") dug.Add(g.At);
        return dug;
    }

    /// <summary>
    /// A water item's cells checked as it is queued (null = fine, else why not): lava only with <c>allow_lava</c>, never at home or beside
    /// something that burns; each cell open (air, grass, flowers, flowing liquid) or dug first by its group; water never onto lava (or
    /// lava onto water); not indoors in a house unless the item says <c>indoor:true</c> (the Water source tool's explicit click). What
    /// only slows or surprises goes in <paramref name="warnings"/>: the liquid will spread beyond the drawn cells (place dams first).
    /// </summary>
    private string? CheckLiquid(IStoryWorld w, JsonObject item, List<GhostState> ghosts, IEnumerable<BuildState> made, List<string> warnings)
    {
        string liquid = LiquidName(ghosts[0]);
        bool lava = liquid == "lava";
        if (lava && !Meta.AllowLava) return "lava is off: turn on the colony option allow_lava first";
        bool indoor = item["indoor"] is JsonValue iv && iv.TryGetValue<bool>(out var ib) && ib;
        var dug = DugFirst(item, made);
        foreach (var g in ghosts)
        {
            if (!w.Loaded(g.At.X, g.At.Z)) return $"{g.At} isn't in the colony's view yet";
            var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
            if (LiquidSource(g.Id, id, meta)) continue; // there already: done at once
            if (lava ? IsWaterId(id) : IsLavaId(id))
                return $"{g.At} holds {(lava ? "water" : "lava")}: {liquid} there would turn it to {(meta == 0 ? "obsidian" : "cobblestone")}";
            if (!TakesLiquid(id) && !dug.Contains(g.At))
                return $"{g.At} holds {BlockLabel(w, id)}: dig it first (a remove item in the same group, or the channel tool's \"Dig the trench\")";
            if (HomeZoneAt(g.At.X, g.At.Z) is { } house)
            {
                if (lava) return $"{g.At} is in the home area ({house.Name ?? house.Id}): no lava at home";
                if (!indoor && Indoors(w, g.At)) return $"{g.At} is indoors ({house.Name ?? house.Id}): pour there with the Water source tool (indoor:true)";
            }
            if (lava && FlammableNear(w, g.At) is { } f) return $"lava at {g.At} would set the {BlockLabel(w, w.Block(f.X, f.Y, f.Z).Id)} at {f} alight";
        }
        var flow = Flow(w, ghosts.Select(g => g.At).ToHashSet(), lava, dug);
        if (flow.Spread.Count > 0)
            warnings.Add($"the {liquid} will spread to {flow.Spread.Count}{(flow.Spread.Count >= MaxFlow ? "+" : "")} cell{(flow.Spread.Count == 1 ? "" : "s")} beyond what was drawn (e.g. {flow.Spread[0]}): place dam blocks first, or dig the channel into the ground");
        if (flow.Touches is { } t) warnings.Add(t);
        return null;
    }

    /// <summary>
    /// Where poured sources run (a preview, after BlockFlowing): from each cell straight down through open cells (no sideways spread
    /// while it can fall), else sideways up to 7 (lava 3) over open cells; a fall lands and spreads again. <c>Spread</c> = the cells it
    /// reaches beyond <paramref name="cells"/>; <c>Touches</c> = it meets the other liquid (obsidian or cobblestone forms).
    /// </summary>
    internal static (List<P3> Spread, string? Touches) Flow(IStoryWorld w, HashSet<P3> cells, bool lava, IReadOnlySet<P3>? dug = null)
    {
        int reach = lava ? LavaReach : WaterReach;
        bool Open(P3 c)
        {
            if (cells.Contains(c)) return false;
            if (dug?.Contains(c) == true) return true;
            if (c.Y < 1 || !w.Loaded(c.X, c.Z)) return false;
            var (id, meta) = w.Block(c.X, c.Y, c.Z);
            if (lava ? IsLavaId(id) : IsWaterId(id)) return meta != 0; // flowing of its own kind: it runs there; a source: already full
            return TakesLiquidIds.Contains(id);
        }
        var seen = new HashSet<P3>();
        var spread = new List<P3>();
        string? touches = null;
        var q = new Queue<(P3 P, int D)>();
        foreach (var c in cells.OrderBy(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Z)) { seen.Add(c); q.Enqueue((c, 0)); }
        void Reach(P3 c)
        {
            spread.Add(c);
            if (touches != null) return;
            foreach (var n in new[] { c.Add(1, 0, 0), c.Add(-1, 0, 0), c.Add(0, 0, 1), c.Add(0, 0, -1), c.Add(0, -1, 0), c.Add(0, 1, 0) })
            {
                var (nid, nm) = w.Block(n.X, n.Y, n.Z);
                if (lava ? IsWaterId(nid) : IsLavaId(nid))
                    touches = lava ? $"the lava would reach water at {n} (stone or cobblestone forms)" : $"the water would reach lava at {n} ({(nm == 0 ? "obsidian" : "cobblestone")} forms)";
            }
        }
        while (q.Count > 0 && spread.Count < MaxFlow)
        {
            var (c, d) = q.Dequeue();
            var below = c.Add(0, -1, 0);
            if (Open(below))
            {
                var f = below;
                int fallen = 1;
                if (seen.Add(f)) Reach(f);
                while (fallen < MaxFall && Open(f.Add(0, -1, 0))) { f = f.Add(0, -1, 0); if (seen.Add(f)) Reach(f); fallen++; }
                if (fallen < MaxFall) q.Enqueue((f, 0)); // landed: it spreads again from there
                continue;
            }
            if (d >= reach) continue;
            foreach (var (dx, dz) in Sides)
            {
                var n = c.Add(dx, 0, dz);
                if (Open(n) && seen.Add(n)) { Reach(n); q.Enqueue((n, d + 1)); }
            }
        }
        return (spread, touches);
    }

    private static JsonObject FlowJson(List<P3> spread) => new()
    {
        ["spread"] = spread.Count,
        ["cells"] = new JsonArray(spread.Take(256).Select(c => (JsonNode)c.Json()).ToArray()),
    };

    /// <summary>A water build was queued: its label, the cells already holding the source are done, and its expected flow joins the
    /// answer's <c>flow</c>.</summary>
    private JsonObject LiquidAdded(IStoryWorld w, BuildState b, JsonObject? flow)
    {
        string liquid = LiquidOfBuild(b) ?? "water";
        bool infinite = b.Spec?["infinite"] is JsonValue fv && fv.TryGetValue<bool>(out var fi) && fi;
        int n = b.Ghosts.Count;
        b.Label = b.Spec?["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && name.Trim().Length > 0 ? name.Trim()
            : infinite ? $"infinite {liquid} source ({n} cells)"
            : n == 1 ? $"{liquid} source" : $"{liquid} channel ({n} sources)";
        foreach (var g in b.Ghosts)
        {
            var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
            if (LiquidSource(g.Id, id, meta)) g.State = "done";
        }
        var (spread, _) = Flow(w, b.Ghosts.Select(g => g.At).ToHashSet(), liquid == "lava", DugFirst(b.Spec ?? new JsonObject(), []));
        var all = (flow?["cells"] as JsonArray)?.Select(P3.Parse).Where(p => p != null).Select(p => p!.Value).ToList() ?? [];
        all.AddRange(spread);
        return FlowJson(all.Distinct().ToList());
    }

    /// <summary><c>POST …/builds {op:"preview", items}</c>: water items checked and their expected flow, nothing queued →
    /// <c>{cells, done, error, warnings, flow:{spread, cells}}</c> (the web's channel tool shows it while drawing).</summary>
    private (JsonNode? Result, string? Error) LiquidPreview(IStoryWorld w, JsonArray? items)
    {
        if (items is not { Count: > 0 }) return (null, "items required");
        if (items.Count > 64) return (null, "at most 64 items per request");
        var made = new List<BuildState>();
        var warnings = new List<string>();
        var spread = new List<P3>();
        int cells = 0, done = 0;
        string? error = null;
        foreach (var n in items)
        {
            if (n is not JsonObject item) return (null, "each item is an object {kind, area|blocks, material, ...}");
            var (ghosts, _, err) = StoryConstruction.Expand(w.Data, item, w);
            if (err != null) { error ??= err; continue; }
            if (ghosts.Count > 0 && StoryConstruction.IsLiquid(ghosts[0]))
            {
                cells += ghosts.Count;
                foreach (var g in ghosts) { var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z); if (LiquidSource(g.Id, id, meta)) done++; }
                if (CheckLiquid(w, item, ghosts, made, warnings) is { } lerr) error ??= lerr;
                else spread.AddRange(Flow(w, ghosts.Select(g => g.At).ToHashSet(), LiquidName(ghosts[0]) == "lava", DugFirst(item, made)).Spread);
            }
            made.Add(new BuildState { Kind = item["kind"]?.GetValue<string>() ?? "", Spec = item, Ghosts = ghosts });
        }
        return (new JsonObject
        {
            ["cells"] = cells, ["done"] = done, ["error"] = error,
            ["warnings"] = new JsonArray(warnings.Distinct().Select(x => (JsonNode)x!).ToArray()),
            ["flow"] = FlowJson(spread.Distinct().ToList()),
        }, null);
    }

    /// <summary>Does any pawn carry a bucket (empty or full), or a chest hold a full one? (Liquid builds share one bucket.)</summary>
    private bool BucketCarried()
    {
        foreach (var p in Pawns.Values)
        {
            var live = Host.Live(p.Name);
            if (live.Online && live.Inventory.Any(s => s.Id is Items.Bucket or Items.WaterBucket or Items.LavaBucket)) return true;
        }
        return Storages.Any(s => s.Contents.Any(c => c.Id is Items.WaterBucket or Items.LavaBucket));
    }

    /// <summary>The colony's buckets: every pawn's and every chest's, empty or full.</summary>
    private int BucketsHeld()
    {
        int n = Storages.Sum(s => s.Contents.Where(c => c.Id is Items.Bucket or Items.WaterBucket or Items.LavaBucket).Sum(c => c.Count));
        foreach (var p in Pawns.Values)
        {
            var live = Host.Live(p.Name);
            if (live.Online) n += live.Inventory.Where(s => s.Id is Items.Bucket or Items.WaterBucket or Items.LavaBucket).Sum(s => s.Count);
        }
        return n;
    }

    private readonly Dictionary<(bool Lava, int Rx, int Ry, int Rz), (DateTimeOffset At, List<P3> Found)> _sourceScan = new();

    /// <summary>Still sources of a liquid around <paramref name="near"/> (scanned at most every 10 s per region).</summary>
    private List<P3> SourcesNear(IStoryWorld w, P3 near, bool lava)
    {
        var key = (lava, near.X >> 3, near.Y >> 3, near.Z >> 3);
        if (_sourceScan.TryGetValue(key, out var hit) && Now - hit.At < TimeSpan.FromSeconds(10)) return hit.Found;
        var found = new List<P3>();
        for (int x = near.X - SourceRadius; x <= near.X + SourceRadius; x++)
            for (int z = near.Z - SourceRadius; z <= near.Z + SourceRadius; z++)
            {
                if (!w.Loaded(x, z)) continue;
                for (int y = Math.Min(w.Height - 1, near.Y + 6); y >= Math.Max(1, near.Y - 10); y--)
                {
                    var (id, meta) = w.Block(x, y, z);
                    if (meta == 0 && (lava ? IsLavaId(id) : IsWaterId(id))) found.Add(new P3(x, y, z));
                }
            }
        if (_sourceScan.Count > 64) _sourceScan.Clear();
        _sourceScan[key] = (Now, found);
        return found;
    }

    /// <summary>
    /// Where a pourer fills its bucket: water from a source that won't be used up (one that refills first, the colony's own infinite
    /// pools before a lake; else one with another source beside it), never a lone puddle, never water the colony poured that doesn't
    /// refill, never a cell of this batch; lava from any lava source the colony didn't pour. Nearest within each kind; null = none.
    /// </summary>
    internal P3? FillSource(IStoryWorld w, bool lava, P3 near, IReadOnlySet<P3> exclude)
    {
        var placed = new HashSet<P3>();
        var own = new HashSet<P3>();
        foreach (var b in Builds)
            foreach (var g in b.Ghosts)
                if (StoryConstruction.IsLiquid(g) && g.State == "done") (Refills(w, g.At) ? own : placed).Add(g.At);
        P3? best = null;
        double bestScore = double.MaxValue;
        foreach (var s in SourcesNear(w, near, lava))
        {
            if (exclude.Contains(s) || placed.Contains(s) || !IsSourceAt(w, s, lava)) continue; // the scan is up to 10 s old
            double score = s.Dist(near);
            if (!lava)
            {
                if (own.Contains(s)) score -= 1000;
                else if (Refills(w, s)) score -= 500;
                else if (!Sides.Any(o => IsSourceAt(w, s.Add(o.Dx, 0, o.Dz), false)) && !IsSourceAt(w, s.Add(0, 1, 0), false) && !IsSourceAt(w, s.Add(0, -1, 0), false))
                    continue; // a lone puddle: scooping it uses it up
            }
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    /// <summary>A channel's cells in pouring order: every other cell first (no two side by side), then the ones between, which a game
    /// rule fills by itself (two sources beside it, over ground) and the pourer then finds already done. A 2×2 pool: two corners.</summary>
    internal static List<GhostState> PourOrder(List<GhostState> cells)
    {
        var first = new List<GhostState>();
        var taken = new HashSet<P3>();
        foreach (var c in cells)
        {
            if (Sides.Any(o => taken.Contains(c.At.Add(o.Dx, 0, o.Dz)))) continue;
            first.Add(c);
            taken.Add(c.At);
        }
        return [.. first, .. cells.Where(c => !taken.Contains(c.At))];
    }

    /// <summary>
    /// A pour job for this pawn from a liquid build (see the type's summary), or why not: <c>Wait</c> = the build waits (no bucket, no
    /// source to fill from, lava off) and the picker goes on to the next build; neither = try this build's next cell.
    /// </summary>
    private (ActiveJob? Job, bool Wait) PourJob(PawnState p, PawnLive live, BuildState b, GhostState seed, List<GhostState> todo, int builders)
    {
        if (World is not { } w) return (null, true);
        bool lava = IsLavaId(seed.Id);
        string liquid = lava ? "lava" : "water";
        if (lava && !Meta.AllowLava) { NoteBuildWait(b, "lava is off (the colony option allow_lava)"); return (null, true); }
        var (cur, curMeta) = w.Block(seed.At.X, seed.At.Y, seed.At.Z);
        if (LiquidSource(seed.Id, cur, curMeta)) { CellDone(b, seed); return (null, false); }
        // A block in the cell (put there since it was queued): the water waits for it to go (a remove build, or by hand).
        if (!TakesLiquid(cur)) { NoteBuildWait(b, $"{seed.At} holds {BlockLabel(w, cur)}: the {liquid} goes in once it is dug out"); return (null, false); }
        // The pour is aimed at a face beside the cell: nothing solid around it, nothing to pour against (yet).
        if (!StoryConstruction.Supported(seed.At, Solid)) return (null, false);
        var from = live.Block;
        int fullId = lava ? Items.LavaBucket : Items.WaterBucket;
        int full = live.Inventory.Where(s => s.Id == fullId).Sum(s => s.Count);
        int empty = live.Inventory.Where(s => s.Id == Items.Bucket).Sum(s => s.Count);
        // Buckets: its own, else from storage (empty ones first, then full ones of this liquid), up to two.
        var fetch = new JsonArray();
        var takes = new List<(P3, Stack)>();
        int carried = full + empty;
        if (carried == 0)
        {
            foreach (var id in new[] { Items.Bucket, fullId })
                foreach (var (st, stack, n) in Stored((id, 0), from))
                {
                    int t = Math.Min(n, PourBuckets - carried);
                    if (t <= 0) break;
                    fetch.Add(new JsonObject { ["from"] = st.Chest.ToString(), ["item"] = ItemName(stack.Id, stack.Meta), ["n"] = t });
                    takes.Add((st.Chest, stack with { Count = t }));
                    carried += t;
                    if (id == fullId) full += t;
                }
            if (carried == 0)
            {
                // Another pawn is pouring with the colony's bucket: this build waits its turn, nothing is blocked.
                if (_current.Values.Any(j => j.Kind == "pour")) return (null, true);
                var mat = b.Materials.FirstOrDefault(m => m.Item == "bucket");
                string status = "blocked", reason;
                bool alert = mat is not { Short: > 0 }; // the materials pass already said what's short and why
                if (mat is { Crafting: > 0 }) { status = "waiting"; reason = "waiting for a bucket (being crafted)"; alert = false; }
                else reason = "needs a bucket (3 iron ingots at a crafting table)";
                if (b.Status != status || b.Reason != reason)
                {
                    b.Status = status; b.Reason = reason; b.Rev = Touch();
                    if (alert) Alert("warn", $"Build {b.Id} ({b.Label}) is {status}: {reason}");
                }
                return (null, true);
            }
        }
        int openCells = todo.Count(StoryConstruction.IsLiquid);
        int cap = Math.Clamp((int)Math.Ceiling(openCells / (double)builders), 1, PourBatchMax);
        var (batch, stand) = Batch(w, b, seed, cap, from, p.Name, -1);
        var cells = PourOrder(batch);
        // A source to fill from (unless every cell is already in its buckets).
        P3? source = null;
        if (full < cells.Count)
        {
            source = FillSource(w, lava, seed.At, cells.Select(c => c.At).ToHashSet());
            if (source == null && full == 0)
            {
                var reason = lava ? $"no lava source within {SourceRadius} blocks to fill a bucket from"
                    : $"no still water within {SourceRadius} blocks to fill a bucket from (a lake, a pool of 2 or more, or an infinite source; a lone puddle would be used up)";
                if (b.Status != "blocked" || b.Reason != reason)
                {
                    b.Status = "blocked"; b.Reason = reason; b.Rev = Touch();
                    Alert("warn", $"Build {b.Id} ({b.Label}) is blocked: {reason}");
                }
                return (null, true);
            }
            if (source == null) cells = cells.Take(Math.Max(1, full)).ToList(); // only what its full buckets pour
        }
        if (b.Status != "active") { b.Status = "active"; b.Reason = null; b.Rev = Touch(); }
        var key = $"g:{b.Id}|{seed.At}";
        // The last liquid cells: the buckets go back to storage after (else the pourer keeps them for the next channel).
        bool last = Builds.Where(Builds_Live).SelectMany(x => x.Ghosts).Count(g => StoryConstruction.IsLiquid(g) && g.Job && g.State is "todo" or "leased") <= cells.Count;
        var home = last ? BestChest(Items.Bucket, 0, from) : null;
        var label = $"pouring {liquid} ({b.Label}){(cells.Count > 1 ? $": {cells.Count} cells" : "")}";
        var job = Job(WorkTypes.Build, "pour", label, seed.At, key, "ghost", $"{b.Id}|{seed.At}", "working", f =>
        {
            f["liquid"] = liquid;
            f["block"] = "bucket"; // what the batch can't go on without (a failed fetch with one in hand goes on)
            f["want"] = liquid;
            f["cells"] = new JsonArray(cells.Select(c => (JsonNode)c.At.ToString()).ToArray());
            if (source is { } s) f["source"] = s.ToString();
            f["buckets"] = carried;
            if (stand is { } sp) f["stand"] = sp.ToString();
            if (fetch.Count > 0) f["fetch"] = fetch;
            if (home != null) f["stash"] = new JsonArray(new JsonObject { ["to"] = home.Chest.ToString(), ["items"] = new JsonArray("bucket") });
        }, xp: Math.Min(60, 8 + 4 * cells.Count));
        job.Takes.AddRange(takes);
        if (home != null) job.Puts.Add((home.Chest, [new Stack(Items.Bucket, 0, carried)]));
        return (Claim(b, cells, p.Name, job), false);
    }

    /// <summary>The colony options for water (<c>POST …/options {allow_lava}</c>).</summary>
    public JsonObject SetWaterOptions(bool? allowLava)
    {
        lock (_lock)
        {
            if (allowLava is { } a && a != Meta.AllowLava)
            {
                Meta.AllowLava = a;
                Touch();
                _architect = null; // the Lava source entry comes and goes
                WakeIdle();
            }
        }
        Save(force: true);
        lock (_lock) return OptionsJson();
    }

    /// <summary>
    /// The Architect's Water tab: Water source (one cell), Water channel (a drawn line or area), Infinite water source (a 2×2 pool that
    /// refills), and Lava source while <c>allow_lava</c> is on. Their tier is the bucket's: workbench (3 iron ingots at a crafting
    /// table); a colony with a bucket has them unlocked whatever its benches.
    /// </summary>
    private void WaterEntries(IStoryWorld w, JsonArray entries, bool hasTable)
    {
        var key = (Items.Bucket, 0);
        if (!w.Data.ItemExists(Items.Bucket)) return;
        var plan = PlanFor(key, 1, new Dictionary<(int, int), int>());
        const string tier = "workbench"; // a bucket is made at a crafting table (its iron is a material, like a wall's stone)
        int have = BucketsHeld();
        int can = have > 0 ? 0 : MostCraftable(key, 64, _freePool).N;
        var withStock = PlanFor(key, 1, _freePool);
        var shortList = have > 0 || can > 0 ? new List<string>() : withStock.Missing.Select(m => m.ToString()).ToList();
        string? reason = have == 0 && !hasTable ? "Needs a bucket — 3 iron ingots at a crafting table (place one or designate an existing one)" : null;
        void Add(string id, string label, string kind, int item, int block)
        {
            entries.Add(new JsonObject
            {
                ["id"] = id, ["label"] = label, ["tab"] = "water", ["tier"] = tier, ["unlocked"] = reason == null, ["reason"] = reason,
                ["item"] = new JsonArray(item, 0), ["material"] = new JsonArray(block, 0), ["kind"] = kind, ["kinds"] = new JsonArray(kind),
                ["chain"] = new JsonArray(plan.Steps.Select(s => (JsonNode)s.ToString()!).ToArray()),
                ["have"] = have, ["craftable"] = can,
                ["from"] = new JsonArray(withStock.Used.Select(u => (JsonNode)u.ToString()!).ToArray()),
                ["short"] = new JsonArray(shortList.Select(s => (JsonNode)s!).ToArray()),
            });
        }
        Add("water_source", "Water source", "water", Items.WaterBucket, 9);
        Add("water_channel", "Water channel", "water_channel", Items.WaterBucket, 9);
        Add("infinite_water", "Infinite water source", "infinite_water", Items.WaterBucket, 9);
        if (Meta.AllowLava) Add("lava_source", "Lava source", "lava", Items.LavaBucket, 11);
    }

    /// <summary>
    /// <c>POST …/zones {op:"irrigation", id}</c>: water channels a growing zone wants (docs/web/story-mode.md "Water"): farmland needs
    /// water within 4 blocks, so for a crop a straight channel at soil level along the zone's longer side, one every 9 rows (a centre
    /// line in a zone 9 or narrower, and one more by the far edge when it would be dry); sugar cane needs water right beside it, so one
    /// every 3 rows. Lines whose cells are all watered already are left out, and so are cells indoors. → <c>{id, cells, dig, axis,
    /// lines, note}</c>: <c>cells</c> the sources at soil level, <c>dig</c> those that hold soil now (dug first: queue them as a remove
    /// item in the water item's group). Nothing is queued.
    /// </summary>
    public (JsonNode? Result, string? Error) Irrigation(string? id)
    {
        lock (_lock)
        {
            if (World is not { } w) return (null, "no world view yet");
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } zone) return (null, "id: no such zone");
            if (zone.Kind != "farm") return (null, "irrigation is for growing zones");
            var plant = ActivePlant(w, zone) ?? StoryPlants.Get(w.Data, zone.Plant);
            bool cane = plant?.Block == StoryPlants.Cane;
            int spacing = cane ? 3 : 9, cover = cane ? 1 : 4;
            var (x1, z1, x2, z2) = zone.Cells.Bounds();
            bool alongX = x2 - x1 >= z2 - z1; // the lines run along the longer side
            int lo = alongX ? z1 : x1, hi = alongX ? z2 : x2, across = hi - lo + 1;
            var lines = new List<int>();
            if (across <= spacing) lines.Add(lo + across / 2);
            else
            {
                for (int k = lo + cover; k <= hi; k += spacing) lines.Add(k);
                if (lines[^1] + cover < hi) lines.Add(hi);
            }
            var cells = new List<P3>();
            var dig = new List<P3>();
            int kept = 0;
            foreach (var line in lines)
            {
                var row = new List<P3>();
                bool dry = false;
                foreach (var (x, z) in zone.Cells.Sorted())
                {
                    int a = alongX ? z : x;
                    if (Math.Abs(a - line) > cover) continue;
                    if (!w.Loaded(x, z) || SoilIn(w, x, z, zone.Area.Y1) is not { } soil) continue;
                    if (!(cane ? StoryPlants.WaterBeside(w, soil) : StoryPlants.WaterNear(w, soil))) dry = true;
                    if (a == line && !Indoors(w, soil)) row.Add(soil);
                }
                if (!dry || row.Count == 0) continue;
                kept++;
                foreach (var c in row)
                {
                    var (bid, bm) = w.Block(c.X, c.Y, c.Z);
                    if (IsWaterId(bid) && bm == 0) continue;
                    cells.Add(c);
                    if (!TakesLiquid(bid)) dig.Add(c);
                }
            }
            string what = cane ? "beside the sugar cane (every 3rd row)" : across <= spacing ? "down the middle" : "every 9th row";
            var note = cells.Count == 0 ? "the zone is watered already: every cell has water close enough"
                : $"{kept} channel{(kept == 1 ? "" : "s")} along {(alongX ? "x" : "z")} {what}: {cells.Count} water cell{(cells.Count == 1 ? "" : "s")}"
                  + (dig.Count > 0 ? $", {dig.Count} dug out first" : "");
            return (new JsonObject
            {
                ["id"] = zone.Id, ["axis"] = alongX ? "x" : "z", ["lines"] = kept,
                ["cells"] = new JsonArray(cells.Select(c => (JsonNode)c.Json()).ToArray()),
                ["dig"] = new JsonArray(dig.Select(c => (JsonNode)c.Json()).ToArray()),
                ["note"] = note,
            }, null);
        }
    }
}
