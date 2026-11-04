using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MineClaw.Data;

namespace MineClaw.Hive.Story;

/// <summary>
/// The pour job (docs/web/story-mode.md "Water"): which cells a pourer takes, the buckets it brings, and where it fills them. Liquid
/// builds go in queue order (a canal waits for the pool queued before it: live 11-04 and the FakeServer canal, where a pawn holding two
/// full buckets poured the canal first while the pool's dug cells still looked solid, then had nowhere to fill but the canal itself).
/// Water still being built is never a fill source: only finished builds' cells that refill, the colony's infinite pools first.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>What a pourer carries or fetches: full buckets of the liquid, buckets in all, the fetch and the ledger's takes.</summary>
    private sealed record PourKit(int Full, int Carried, JsonArray Fetch, List<(P3, Stack)> Takes);

    /// <summary>The colony's own water as a fill source: cells it must never scoop (unfinished builds, or poured water that doesn't
    /// refill), its finished refilling cells, and those of its infinite pools.</summary>
    private sealed record ColonyWater(HashSet<P3> Never, HashSet<P3> Own, HashSet<P3> Pools);

    /// <summary>
    /// A pour job for this pawn from a liquid build, or why not: <c>Wait</c> = the build waits (an earlier water build first, no bucket,
    /// no source to fill from, lava off) and the picker goes on to the next build; neither = try this build's next cell.
    /// </summary>
    private (ActiveJob? Job, bool Wait) PourJob(PawnState p, PawnLive live, BuildState b, GhostState seed, List<GhostState> todo, int builders)
    {
        if (World is not { } w) return (null, true);
        if (PourBlocker(w, b, seed) is { } blocked) return blocked;
        bool lava = IsLavaId(seed.Id);
        var kit = BucketsFor(live, lava);
        if (kit.Carried == 0) return (null, NoBucket(b));
        var (cells, stand) = PourBatch(w, b, seed, todo, builders, p.Name, live.Block);
        P3? source = null;
        if (kit.Full < cells.Count)
        {
            source = FillSource(w, lava, seed.At, cells.Select(c => c.At).ToHashSet(), p.Name);
            if (source == null && kit.Full == 0) { NoFillSource(b, p, lava); return (null, true); }
            if (source == null) cells = cells.Take(kit.Full).ToList(); // only what its full buckets pour
        }
        if (b.Status != "active") { b.Status = "active"; b.Reason = null; b.Rev = Touch(); }
        return (Claim(b, cells, p.Name, NewPourJob(b, seed, cells, stand, source, kit, live.Block)), false);
    }

    /// <summary>Why this build gives no pour job for this seed now, or null: an earlier water build first (wait), lava off (wait), the
    /// cell already a source (done), a block in it, or nothing solid beside it to pour against (the next seed).</summary>
    private (ActiveJob?, bool)? PourBlocker(IStoryWorld w, BuildState b, GhostState seed)
    {
        if (EarlierWaterBuild(b) is { } first)
        {
            NoteBuildWait(b, $"waits for {first.Label} ({first.Id}): water builds go in queue order");
            return (null, true);
        }
        string liquid = LiquidName(seed);
        if (liquid == "lava" && !Meta.AllowLava) { NoteBuildWait(b, "lava is off (the colony option allow_lava)"); return (null, true); }
        var (cur, curMeta) = w.Block(seed.At.X, seed.At.Y, seed.At.Z);
        if (LiquidSource(seed.Id, cur, curMeta)) { CellDone(b, seed); return (null, false); }
        // A block in the cell (a dig the view hasn't caught up with, or put there since): the water waits for it to go.
        if (!TakesLiquid(cur)) { NoteBuildWait(b, $"{seed.At} holds {BlockLabel(w, cur)}: the {liquid} goes in once it is dug out"); return (null, false); }
        // The pour is aimed at a face beside the cell: nothing solid around it, nothing to pour against (yet).
        return StoryConstruction.Supported(seed.At, Solid) ? null : (null, false);
    }

    /// <summary>The first live water build queued before <paramref name="b"/> that still has cells to pour (a blocked one doesn't hold
    /// the others up), or null.</summary>
    private BuildState? EarlierWaterBuild(BuildState b) =>
        Builds.Where(o => o != b && o.Order < b.Order && Builds_Live(o) && o.Status != "blocked" && LiquidOfBuild(o) != null
                          && o.Ghosts.Any(g => g.Job && g.State is "todo" or "leased"))
            .OrderBy(o => o.Order).FirstOrDefault();

    /// <summary>The pawn's buckets (empty and full of this liquid); with none, up to two from storage, empty ones first.</summary>
    private PourKit BucketsFor(PawnLive live, bool lava)
    {
        int fullId = lava ? Items.LavaBucket : Items.WaterBucket;
        int full = live.Inventory.Where(s => s.Id == fullId).Sum(s => s.Count);
        int carried = full + live.Inventory.Where(s => s.Id == Items.Bucket).Sum(s => s.Count);
        var kit = new PourKit(full, carried, new JsonArray(), new List<(P3, Stack)>());
        return carried > 0 ? kit : FetchBuckets(kit, fullId, live.Block);
    }

    private PourKit FetchBuckets(PourKit kit, int fullId, P3 from)
    {
        int full = kit.Full, carried = 0;
        foreach (var id in new[] { Items.Bucket, fullId })
            foreach (var (st, stack, n) in Stored((id, 0), from))
            {
                int t = Math.Min(n, PourBuckets - carried);
                if (t <= 0) break;
                kit.Fetch.Add(new JsonObject { ["from"] = st.Chest.ToString(), ["item"] = ItemName(stack.Id, stack.Meta), ["n"] = t });
                kit.Takes.Add((st.Chest, stack with { Count = t }));
                carried += t;
                if (id == fullId) full += t;
            }
        return kit with { Full = full, Carried = carried };
    }

    /// <summary>No bucket anywhere: another pawn pouring with the colony's (wait its turn), one being crafted (waiting), else blocked
    /// with "needs a bucket" (said once; the materials pass already said what is short). Always a wait.</summary>
    private bool NoBucket(BuildState b)
    {
        if (_current.Values.Any(j => j.Kind == "pour")) return true;
        var mat = b.Materials.FirstOrDefault(m => m.Item == "bucket");
        bool crafting = mat is { Crafting: > 0 };
        SetBuildStatus(b, crafting ? "waiting" : "blocked", crafting ? "waiting for a bucket (being crafted)" : "needs a bucket (3 iron ingots at a crafting table)",
            alert: !crafting && mat is not { Short: > 0 });
        return true;
    }

    private void NoFillSource(BuildState b, PawnState p, bool lava)
    {
        bool missed = _fillMisses.Any(m => m.Key.Pawn == p.Name && m.Value > Now);
        var reason = lava ? $"no lava source within {SourceRadius} blocks to fill a bucket from"
            : $"no still water within {SourceRadius} blocks {(missed ? "that the pawns can reach " : "")}to fill a bucket from (a lake or pool with dry ground beside it, or an infinite source; a lone puddle would be used up)";
        SetBuildStatus(b, "blocked", reason, alert: true);
    }

    private void SetBuildStatus(BuildState b, string status, string reason, bool alert)
    {
        if (b.Status == status && b.Reason == reason) return;
        b.Status = status;
        b.Reason = reason;
        b.Rev = Touch();
        if (alert) Alert("warn", $"Build {b.Id} ({b.Label}) is {status}: {reason}");
    }

    /// <summary>The pawn's share of the build's open cells around the seed, in pouring order, with one stand.</summary>
    private (List<GhostState> Cells, P3? Stand) PourBatch(IStoryWorld w, BuildState b, GhostState seed, List<GhostState> todo, int builders, string pawn, P3 from)
    {
        int open = todo.Count(StoryConstruction.IsLiquid);
        int cap = Math.Clamp((int)Math.Ceiling(open / (double)builders), 1, PourBatchMax);
        var (batch, stand) = Batch(w, b, seed, cap, from, pawn, -1);
        return (PourOrder(batch), stand);
    }

    private ActiveJob NewPourJob(BuildState b, GhostState seed, List<GhostState> cells, P3? stand, P3? source, PourKit kit, P3 from)
    {
        string liquid = LiquidName(seed);
        // The last liquid cells: the buckets go back to storage after (else the pourer keeps them for the next channel).
        bool last = Builds.Where(Builds_Live).SelectMany(x => x.Ghosts).Count(g => StoryConstruction.IsLiquid(g) && g.Job && g.State is "todo" or "leased") <= cells.Count;
        var home = last ? BestChest(Items.Bucket, 0, from) : null;
        var label = $"pouring {liquid} ({b.Label}){(cells.Count > 1 ? $": {cells.Count} cells" : "")}";
        var job = Job(WorkTypes.Build, "pour", label, seed.At, $"g:{b.Id}|{seed.At}", "ghost", $"{b.Id}|{seed.At}", "working", f =>
        {
            f["liquid"] = liquid;
            f["block"] = "bucket"; // what the batch can't go on without (a failed fetch with one in hand goes on)
            f["want"] = liquid;
            f["cells"] = new JsonArray(cells.Select(c => (JsonNode)c.At.ToString()).ToArray());
            if (source is { } s) f["source"] = s.ToString();
            f["buckets"] = kit.Carried;
            if (stand is { } sp) f["stand"] = sp.ToString();
            if (kit.Fetch.Count > 0) f["fetch"] = kit.Fetch;
            if (home != null) f["stash"] = new JsonArray(new JsonObject { ["to"] = home.Chest.ToString(), ["items"] = new JsonArray("bucket") });
        }, xp: Math.Min(60, 8 + 4 * cells.Count));
        job.Takes.AddRange(kit.Takes);
        if (home != null) job.Puts.Add((home.Chest, [new Stack(Items.Bucket, 0, kit.Carried)]));
        return job;
    }

    // ------------------------------------------------------------------ where a pourer fills its bucket

    /// <summary>
    /// Where a pourer fills its bucket: water from a source that won't be used up (the colony's finished infinite pools first, then its
    /// finished channels that refill, then any that refills, then one with another source beside it), never a lone puddle, never water
    /// the colony is still building or poured and doesn't refill, never a cell of this batch, never a pond this pawn just failed at, and
    /// only with dry ground beside it; lava from any lava source the colony didn't pour. Nearest (and nearest the cells' height) within
    /// each kind; null = none.
    /// </summary>
    internal P3? FillSource(IStoryWorld w, bool lava, P3 near, IReadOnlySet<P3> exclude, string? pawn = null)
    {
        var colony = ColonyWaterNow(w);
        var missed = pawn == null ? [] : _fillMisses.Where(m => m.Key.Pawn == pawn && m.Value > Now).Select(m => m.Key.At).ToList();
        P3? best = null;
        double bestScore = double.MaxValue;
        foreach (var s in SourcesNear(w, near, lava))
        {
            if (exclude.Contains(s) || colony.Never.Contains(s) || NearAny(missed, s)) continue;
            if (SourceScore(w, s, near, lava, colony) is { } score && score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    /// <summary>The colony's own water (see <see cref="ColonyWater"/>): a liquid build's cells are a source only once the build is done.</summary>
    private ColonyWater ColonyWaterNow(IStoryWorld w)
    {
        var water = new ColonyWater(new HashSet<P3>(), new HashSet<P3>(), new HashSet<P3>());
        foreach (var b in Builds.Where(b => LiquidOfBuild(b) != null))
        {
            bool done = b.Status == "done", pool = IsInfinite(b);
            foreach (var g in b.Ghosts.Where(StoryConstruction.IsLiquid))
                AddColonyWater(water, g.At, done && Refills(w, g.At), pool);
        }
        return water;
    }

    private static void AddColonyWater(ColonyWater water, P3 at, bool usable, bool pool)
    {
        if (!usable) { water.Never.Add(at); return; }
        water.Own.Add(at);
        if (pool) water.Pools.Add(at);
    }

    private static bool IsInfinite(BuildState b) => b.Spec?["infinite"] is JsonValue v && v.TryGetValue<bool>(out var inf) && inf;

    /// <summary>Within a pond a pawn failed at (its other cells too).</summary>
    private static bool NearAny(List<P3> missed, P3 s) =>
        missed.Any(u => Math.Abs(u.X - s.X) <= 6 && Math.Abs(u.Z - s.Z) <= 6 && Math.Abs(u.Y - s.Y) <= 3);

    /// <summary>A source's score (lower is better), or null when it isn't one to fill at: no dry ground beside it (live 11-04: "no path
    /// to any reach (227,54,406)", a pond down a pit; the colony's own water is where its pawns work), or a lone puddle.</summary>
    private static double? SourceScore(IStoryWorld w, P3 s, P3 near, bool lava, ColonyWater colony)
    {
        if (!IsSourceAt(w, s, lava)) return null; // the scan is up to 10 s old
        bool own = colony.Own.Contains(s);
        if (!own && !HasBank(w, s)) return null;
        double score = s.Dist(near) + 3 * Math.Abs(s.Y - near.Y);
        if (lava) return score;
        if (colony.Pools.Contains(s)) return score - 2000;
        if (own) return score - 1000;
        if (Refills(w, s)) return score - 500;
        return HasSourceNeighbour(w, s) ? score : null; // a lone puddle: scooping it uses it up
    }

    private static bool HasSourceNeighbour(IStoryWorld w, P3 s) =>
        Sides.Any(o => IsSourceAt(w, s.Add(o.Dx, 0, o.Dz), false)) || IsSourceAt(w, s.Add(0, 1, 0), false) || IsSourceAt(w, s.Add(0, -1, 0), false);

    private readonly Dictionary<(bool Lava, int Rx, int Ry, int Rz), (DateTimeOffset At, List<P3> Found)> _sourceScan = new();

    /// <summary>Still sources of a liquid around <paramref name="near"/> (scanned at most every 10 s per region).</summary>
    private List<P3> SourcesNear(IStoryWorld w, P3 near, bool lava)
    {
        var key = (lava, near.X >> 3, near.Y >> 3, near.Z >> 3);
        if (_sourceScan.TryGetValue(key, out var hit) && Now - hit.At < TimeSpan.FromSeconds(10)) return hit.Found;
        var found = new List<P3>();
        for (int x = near.X - SourceRadius; x <= near.X + SourceRadius; x++)
            for (int z = near.Z - SourceRadius; z <= near.Z + SourceRadius; z++)
                if (w.Loaded(x, z)) ScanColumn(w, x, z, near.Y, lava, found);
        if (_sourceScan.Count > 64) _sourceScan.Clear();
        _sourceScan[key] = (Now, found);
        return found;
    }

    private static void ScanColumn(IStoryWorld w, int x, int z, int nearY, bool lava, List<P3> found)
    {
        for (int y = Math.Min(w.Height - 1, nearY + 6); y >= Math.Max(1, nearY - 10); y--)
            if (IsSourceAt(w, new P3(x, y, z), lava)) found.Add(new P3(x, y, z));
    }

    /// <summary>Somewhere dry to stand beside a source, looking down at it: a side neighbour that is solid ground with two open, dry
    /// cells over it (or open ground one step up).</summary>
    private static bool HasBank(IStoryWorld w, P3 s)
    {
        bool Dry(P3 c) { int id = w.Block(c.X, c.Y, c.Z).Id; return OpenCell(id) && !StoryBlocks.Liquids.Contains(id); }
        bool Ground(P3 c) { int id = w.Block(c.X, c.Y, c.Z).Id; return !OpenCell(id) && !StoryBlocks.Liquids.Contains(id); }
        foreach (var (dx, dz) in Sides)
        {
            var n = s.Add(dx, 0, dz);
            if (Ground(n) && Dry(n.Add(0, 1, 0)) && Dry(n.Add(0, 2, 0))) return true;           // the bank at water level
            if (Dry(n) && Dry(n.Add(0, 1, 0)) && Ground(n.Add(0, -1, 0))) return true;          // a shallow step beside it
        }
        return false;
    }

    /// <summary>Ponds a pawn failed to fill at (unreachable, protected, nothing visible), until when: skipped for that pawn.</summary>
    private readonly Dictionary<(string Pawn, P3 At), DateTimeOffset> _fillMisses = new();

    /// <summary>A pour job's <c>fill failed at x,y,z: why</c>: that pond rests 10 minutes for this pawn, the colony's own water 30 s (the batch's other cells went
    /// back without a failure); the next pour job fills elsewhere.</summary>
    private void FillFailed(PawnState p, string note)
    {
        const string head = "fill failed at ";
        int colon = note.IndexOf(':', head.Length);
        if (ParseCell(colon > 0 ? note[head.Length..colon] : note[head.Length..]) is not { } at) return;
        // The colony's own water is where its pawns work: a miss there (a bad angle, a lagging view) rests 30 s, a pond 10 minutes.
        bool own = World is { } w && ColonyWaterNow(w).Own.Contains(at);
        _fillMisses[(p.Name, at)] = Now + (own ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
        foreach (var k in _fillMisses.Where(kv => kv.Value <= Now).Select(kv => kv.Key).ToList()) _fillMisses.Remove(k);
        _log.LogInformation("story {Colony}: {Pawn} couldn't fill a bucket at {At}: {Why}", Id, p.Name, at, colon > 0 ? note[(colon + 1)..].Trim() : "");
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
}
