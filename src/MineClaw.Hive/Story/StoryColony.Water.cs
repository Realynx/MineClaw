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

    /// <summary>What is under a liquid cell holds it: a block that stays (not dug first), another cell of the item, or a source of the
    /// same liquid.</summary>
    private static bool Held(IStoryWorld w, P3 below, int ghostId, HashSet<P3> own, IReadOnlySet<P3> dug)
    {
        if (own.Contains(below)) return true;
        var (id, meta) = w.Block(below.X, below.Y, below.Z);
        if (LiquidSource(ghostId, id, meta)) return true;
        return !dug.Contains(below) && !TakesLiquid(id);
    }

    /// <summary>
    /// A pour is aimed at a solid face beside or under the cell (the bucket's ray passes liquids): one that stays (not dug first, not
    /// the item's own cell). None: on a source of the same liquid ("the channel there is full": live 11-04, a channel drawn one above a
    /// filled trench failed 371 times as "nothing beside or under … to pour against"), else nothing to pour against. Null = fine.
    /// </summary>
    private static string? PourFaceProblem(IStoryWorld w, P3 at, int ghostId, string liquid, HashSet<P3> own, IReadOnlySet<P3> dug)
    {
        foreach (var n in new[] { at.Add(0, -1, 0), at.Add(1, 0, 0), at.Add(-1, 0, 0), at.Add(0, 0, 1), at.Add(0, 0, -1), at.Add(0, 1, 0) })
            if (!own.Contains(n) && !dug.Contains(n) && !TakesLiquid(w.Block(n.X, n.Y, n.Z).Id)) return null;
        var (bid, bmeta) = w.Block(at.X, at.Y - 1, at.Z);
        return LiquidSource(ghostId, bid, bmeta)
            ? $"{at} sits on {liquid} already (the channel under it is full): draw it one block lower, in the trench"
            : $"nothing beside or under {at} to pour against: the {liquid} needs a solid block next to it or under it";
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
        bool lava = LiquidName(ghosts[0]) == "lava";
        if (lava && !Meta.AllowLava) return "lava is off: turn on the colony option allow_lava first";
        var ctx = new LiquidCheck(w, LiquidName(ghosts[0]), item["indoor"] is JsonValue iv && iv.TryGetValue<bool>(out var ib) && ib,
            DugFirst(item, made), ghosts.Select(g => g.At).ToHashSet());
        foreach (var g in ghosts)
            if (LiquidCellProblem(ctx, g) is { } why) return why;
        FlowWarnings(w, ctx, warnings);
        return null;
    }

    /// <summary>What a water item's cells are checked against: the world, its liquid, the explicit indoor click, the cells its group digs
    /// first, its own cells.</summary>
    private sealed record LiquidCheck(IStoryWorld W, string Liquid, bool Indoor, HashSet<P3> Dug, HashSet<P3> Own)
    {
        public bool Lava => Liquid == "lava";
    }

    /// <summary>Why one cell can't take the liquid (null = fine, or a source there already).</summary>
    private string? LiquidCellProblem(LiquidCheck c, GhostState g)
    {
        var w = c.W;
        if (!w.Loaded(g.At.X, g.At.Z)) return $"{g.At} isn't in the colony's view yet";
        var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (LiquidSource(g.Id, id, meta)) return null; // there already: done at once
        if (OtherLiquidProblem(c, g.At, id, meta) is { } other) return other;
        if (!TakesLiquid(id) && !c.Dug.Contains(g.At))
            return $"{g.At} holds {BlockLabel(w, id)}: dig it first (a remove item in the same group, or the channel tool's \"Dig the trench\")";
        // Nothing under it: the source would pour down (and a pawn has no floor face to aim at). Live 11-04: "no block face to pour
        // against" cell after cell; said once here instead.
        if (!Held(w, g.At.Add(0, -1, 0), g.Id, c.Own, c.Dug))
            return $"nothing under {g.At} to hold the {c.Liquid}: it would pour down. Dig the channel into the ground, or lay a floor under it first";
        if (PourFaceProblem(w, g.At, g.Id, c.Liquid, c.Own, c.Dug) is { } face) return face;
        return HouseProblem(c, g.At) ?? FireProblem(c, g.At);
    }

    /// <summary>The other liquid in the cell: the two would make obsidian (a source) or cobblestone.</summary>
    private static string? OtherLiquidProblem(LiquidCheck c, P3 at, int id, int meta)
    {
        if (!(c.Lava ? IsWaterId(id) : IsLavaId(id))) return null;
        return $"{at} holds {(c.Lava ? "water" : "lava")}: {c.Liquid} there would turn it to {(meta == 0 ? "obsidian" : "cobblestone")}";
    }

    /// <summary>Lava beside something that burns.</summary>
    private string? FireProblem(LiquidCheck c, P3 at) =>
        c.Lava && FlammableNear(c.W, at) is { } f ? $"lava at {at} would set the {BlockLabel(c.W, c.W.Block(f.X, f.Y, f.Z).Id)} at {f} alight" : null;

    /// <summary>A cell in a house: never lava; water indoors only with the Water source tool's explicit click.</summary>
    private string? HouseProblem(LiquidCheck c, P3 at)
    {
        if (HomeZoneAt(at.X, at.Z) is not { } house) return null;
        if (c.Lava) return $"{at} is in the home area ({house.Name ?? house.Id}): no lava at home";
        return !c.Indoor && Indoors(c.W, at) ? $"{at} is indoors ({house.Name ?? house.Id}): pour there with the Water source tool (indoor:true)" : null;
    }

    /// <summary>What only surprises: the liquid will spread beyond the drawn cells (place dams first), or meet the other liquid.</summary>
    private static void FlowWarnings(IStoryWorld w, LiquidCheck c, List<string> warnings)
    {
        var flow = Flow(w, c.Own, c.Lava, c.Dug);
        int n = flow.Spread.Count;
        if (n > 0)
            warnings.Add($"the {c.Liquid} will spread to {n}{(n >= MaxFlow ? "+" : "")} cell{(n == 1 ? "" : "s")} beyond what was drawn (e.g. {flow.Spread[0]}): place dam blocks first, or dig the channel into the ground");
        if (flow.Touches is { } t) warnings.Add(t);
    }

    /// <summary>
    /// Where poured sources run (a preview, after BlockFlowing): from each cell straight down through open cells (no sideways spread
    /// while it can fall), else sideways up to 7 (lava 3) over open cells; a fall lands and spreads again. <c>Spread</c> = the cells it
    /// reaches beyond <paramref name="cells"/>; <c>Touches</c> = it meets the other liquid (obsidian or cobblestone forms).
    /// </summary>
    internal static (List<P3> Spread, string? Touches) Flow(IStoryWorld w, HashSet<P3> cells, bool lava, IReadOnlySet<P3>? dug = null)
    {
        var walk = new FlowWalk(w, cells, lava, dug);
        walk.Run();
        return (walk.Spread, walk.Touches);
    }

    /// <summary>The flow preview's breadth-first walk (see <see cref="Flow"/>).</summary>
    private sealed class FlowWalk(IStoryWorld w, HashSet<P3> cells, bool lava, IReadOnlySet<P3>? dug)
    {
        private readonly HashSet<P3> _seen = [.. cells];
        private readonly Queue<(P3 P, int D)> _queue = new();
        public List<P3> Spread { get; } = new();
        public string? Touches { get; private set; }

        public void Run()
        {
            foreach (var c in cells.OrderBy(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Z)) _queue.Enqueue((c, 0));
            while (_queue.Count > 0 && Spread.Count < MaxFlow)
            {
                var (c, d) = _queue.Dequeue();
                if (Open(c.Add(0, -1, 0))) Fall(c.Add(0, -1, 0));
                else if (d < (lava ? LavaReach : WaterReach)) Sideways(c, d);
            }
        }

        /// <summary>Straight down while it can (no sideways spread while falling); where it lands it spreads again.</summary>
        private void Fall(P3 f)
        {
            int fallen = 1;
            Reach(f);
            while (fallen < MaxFall && Open(f.Add(0, -1, 0))) { f = f.Add(0, -1, 0); Reach(f); fallen++; }
            if (fallen < MaxFall) _queue.Enqueue((f, 0));
        }

        private void Sideways(P3 c, int d)
        {
            foreach (var (dx, dz) in Sides)
            {
                var n = c.Add(dx, 0, dz);
                if (Open(n) && Reach(n)) _queue.Enqueue((n, d + 1));
            }
        }

        private bool Open(P3 c)
        {
            if (cells.Contains(c)) return false;
            if (dug?.Contains(c) == true) return true;
            if (c.Y < 1 || !w.Loaded(c.X, c.Z)) return false;
            var (id, meta) = w.Block(c.X, c.Y, c.Z);
            if (lava ? IsLavaId(id) : IsWaterId(id)) return meta != 0; // flowing of its own kind: it runs there; a source: already full
            return TakesLiquidIds.Contains(id);
        }

        /// <summary>A newly reached cell (false when seen before), and whether the other liquid is beside it.</summary>
        private bool Reach(P3 c)
        {
            if (!_seen.Add(c)) return false;
            Spread.Add(c);
            Touches ??= OtherLiquidBeside(c);
            return true;
        }

        private string? OtherLiquidBeside(P3 c)
        {
            foreach (var n in new[] { c.Add(1, 0, 0), c.Add(-1, 0, 0), c.Add(0, 0, 1), c.Add(0, 0, -1), c.Add(0, -1, 0), c.Add(0, 1, 0) })
            {
                var (nid, nm) = w.Block(n.X, n.Y, n.Z);
                if (lava && IsWaterId(nid)) return $"the lava would reach water at {n} (stone or cobblestone forms)";
                if (!lava && IsLavaId(nid)) return $"the water would reach lava at {n} ({(nm == 0 ? "obsidian" : "cobblestone")} forms)";
            }
            return null;
        }
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
        b.Label = LiquidLabel(b, liquid);
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

    /// <summary>The item's name, else "infinite water source (4 cells)", "water source", "lava channel (3 sources)".</summary>
    private static string LiquidLabel(BuildState b, string liquid)
    {
        bool infinite = b.Spec?["infinite"] is JsonValue fv && fv.TryGetValue<bool>(out var fi) && fi;
        int n = b.Ghosts.Count;
        return b.Spec?["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && name.Trim().Length > 0 ? name.Trim()
            : infinite ? $"infinite {liquid} source ({n} cells)"
            : n == 1 ? $"{liquid} source" : $"{liquid} channel ({n} sources)";
    }

    /// <summary><c>POST …/builds {op:"preview", items}</c>: water items checked and their expected flow, nothing queued →
    /// <c>{cells, done, error, warnings, flow:{spread, cells}}</c> (the web's channel tool shows it while drawing).</summary>
    private (JsonNode? Result, string? Error) LiquidPreview(IStoryWorld w, JsonArray? items)
    {
        if (items is not { Count: > 0 }) return (null, "items required");
        if (items.Count > 64) return (null, "at most 64 items per request");
        var acc = new LiquidPreviewAcc();
        foreach (var n in items)
        {
            if (n is not JsonObject item) return (null, "each item is an object {kind, area|blocks, material, ...}");
            PreviewItem(w, item, acc);
        }
        return (new JsonObject
        {
            ["cells"] = acc.Cells, ["done"] = acc.Done, ["error"] = acc.Error,
            ["warnings"] = new JsonArray(acc.Warnings.Distinct().Select(x => (JsonNode)x!).ToArray()),
            ["flow"] = FlowJson(acc.Spread.Distinct().ToList()),
        }, null);
    }

    /// <summary>A preview's running totals: the items so far (a later one's group digs for it), warnings, the flow, cells, done cells,
    /// the first error.</summary>
    private sealed class LiquidPreviewAcc
    {
        public readonly List<BuildState> Made = new();
        public readonly List<string> Warnings = new();
        public readonly List<P3> Spread = new();
        public int Cells, Done;
        public string? Error;
    }

    /// <summary>One preview item: expanded (an error is kept, the item skipped); a liquid one checked, its done cells and flow counted.</summary>
    private void PreviewItem(IStoryWorld w, JsonObject item, LiquidPreviewAcc acc)
    {
        var (ghosts, _, err) = StoryConstruction.Expand(w.Data, item, w);
        if (err != null) { acc.Error ??= err; return; }
        if (ghosts.Count > 0 && StoryConstruction.IsLiquid(ghosts[0])) PreviewLiquid(w, item, ghosts, acc);
        acc.Made.Add(new BuildState { Kind = item["kind"]?.GetValue<string>() ?? "", Spec = item, Ghosts = ghosts });
    }

    private void PreviewLiquid(IStoryWorld w, JsonObject item, List<GhostState> ghosts, LiquidPreviewAcc acc)
    {
        acc.Cells += ghosts.Count;
        foreach (var g in ghosts) { var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z); if (LiquidSource(g.Id, id, meta)) acc.Done++; }
        if (CheckLiquid(w, item, ghosts, acc.Made, acc.Warnings) is { } lerr) acc.Error ??= lerr;
        else acc.Spread.AddRange(Flow(w, ghosts.Select(g => g.At).ToHashSet(), LiquidName(ghosts[0]) == "lava", DugFirst(item, acc.Made)).Spread);
    }

    /// <summary>The colony's buckets: every pawn's and every chest's, empty or full (live 11-04: a water bucket put in a chest still
    /// had "Make Bucket" asking for iron; any of them is the one bucket liquid builds share).</summary>
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
        SaveSoon();
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
}
