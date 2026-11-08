using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Quarry zones (docs/web/story-mode.md "Quarries"). Fox 11-04: "The quarry zone didn't make sense — nothing happened." A mine zone
/// in <c>quarry</c> mode (the default for new ones; <c>ores</c> keeps the old ore-only behaviour) digs everything in its columns,
/// layer by layer from its top down to bedrock (never bedrock, never y 0), leaving the perimeter stair (<see cref="StoryQuarry"/>)
/// standing: a step that is air, liquid or would fall (sand, gravel) gets a cheap block placed first. A layer is done only when
/// every cell of it is gone, resting ones too (live 11-04: "no pawn wants to mine in it any more after about the 7th layer": cells
/// that rested after failures were left behind as pillars, the floor went uneven, paths dropped 2–3 blocks onto cells being dug
/// and pawns got stranded). The whole stair down to the working layer is checked every scan and repaired before more digging.
/// Cells beside lava or water are left standing (a seal), with an alert. Mine work; exposed cells only; the pickaxe its material
/// needs (StoryColony.Gear.cs); loot is batched like any gathering. Why nothing is offered goes to the status and the idle audit.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>What a quarry has now (rescanned at most every 5 s, and after each of its jobs).</summary>
    internal sealed class QuarryScan
    {
        public DateTimeOffset At;
        public int Top, Bottom, Layer;
        public bool Done;
        public List<(int X, int Z)>? Ring;
        public readonly Dictionary<(int X, int Z), int> Bottoms = new();
        /// <summary>The working layer's cells left to dig (resting ones too).</summary>
        public readonly List<P3> Dig = new();
        /// <summary>Cells under a step still waiting for its block (dug once the step stands).</summary>
        public int Held;
        /// <summary>Steps from the top down to one under the working layer that need a block (placed first, top-most first).</summary>
        public readonly List<(P3 At, bool Clear)> StepFix = new();
        /// <summary>Cells to place a sealing block in (lava or water in the zone, a breached cave's opening), from the layers looked at.</summary>
        public readonly List<(P3 At, string Kind)> SealFix = new();
        /// <summary>The hazards the look found (left standing or to seal), by kind: cave, lava, water, sand, danger.</summary>
        public readonly List<(P3 At, string Kind, P3 Source)> Found = new();
        /// <summary>A monster spawner in or beside the working layer: the quarry stops until it is gone.</summary>
        public P3? Spawner;
        public int Hazards;
        public int MaxK => Top - Bottom;
        public int Y => Top - Layer;
    }

    private enum QuarryCell { Skip, Hazard, Dig, Seal }

    private readonly Dictionary<string, QuarryScan> _quarryScan = new();
    private readonly Dictionary<string, DateTimeOffset> _quarryFull = new();
    private readonly Dictionary<string, DateTimeOffset> _quarryAlert = new();

    internal static bool IsQuarry(ZoneState z) => z.Kind == "mine" && z.Mode == "quarry";

    /// <summary>The ground under open air in a column near the zone's level (no air, plant, log, leaf or liquid).</summary>
    private static int? GroundTop(IStoryWorld w, int x, int z, int level)
    {
        if (!w.Loaded(x, z)) return null;
        for (int y = Math.Min(w.Height - 1, level + 16); y >= Math.Max(1, level - 16); y--)
            if (Ground(w.Block(x, y, z).Id)) return y;
        return null;
    }

    private static bool Ground(int id) =>
        !OpenCell(id) && !StoryBlocks.Liquids.Contains(id) && !StoryBlocks.Logs.Contains(id) && !StoryBlocks.Leaves.Contains(id);

    /// <summary>The lowest y a column is dug to: one over its highest bedrock (y 0..8), else 1.</summary>
    private static int ColumnBottom(IStoryWorld w, int x, int z)
    {
        for (int y = 8; y >= 0; y--)
            if (w.Block(x, y, z).Id == StoryQuarry.Bedrock) return y + 1;
        return 1;
    }

    internal QuarryScan? Quarry(ZoneState z)
    {
        if (World is not { } w) return null;
        if (_quarryScan.TryGetValue(z.Id, out var c) && Now - c.At < TimeSpan.FromSeconds(5)) return c;
        var q = new QuarryScan { At = Now, Ring = StoryQuarry.Ring(z.Cells) };
        _quarryScan[z.Id] = q;
        if (!QuarryBounds(w, z, q)) { q.Done = true; return q; } // nothing seen yet
        FindLayer(w, z, q);
        if (!q.Done) CheckStair(w, q);
        if (!q.Done) q.Spawner = SpawnerNear(w, z, q); // StoryColony.QuarryHazards.cs
        QuarryHazardNews(z, q);
        return q;
    }

    /// <summary>The quarry's top (its highest ground when first seen, kept) and each column's bottom.</summary>
    private bool QuarryBounds(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        var cols = z.Cells.Sorted();
        if (z.QuarryTop is null)
        {
            var tops = cols.Select(cc => GroundTop(w, cc.X, cc.Z, z.Area.Y2)).OfType<int>().ToList();
            if (tops.Count == 0) return false;
            z.QuarryTop = tops.Max();
            z.Rev = Touch();
        }
        q.Top = z.QuarryTop.Value;
        foreach (var cc in cols) q.Bottoms[cc] = ColumnBottom(w, cc.X, cc.Z);
        q.Bottom = q.Bottoms.Count > 0 ? q.Bottoms.Values.Min() : 1;
        return true;
    }

    /// <summary>
    /// The working layer: the top-most one with anything left (resting cells and cells held under a step included). From the top
    /// every 30 s (a cell refilled or left behind above is found again), else from the last working layer.
    /// </summary>
    private void FindLayer(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        bool full = !_quarryFull.TryGetValue(z.Id, out var last) || Now - last > TimeSpan.FromSeconds(30);
        if (full) _quarryFull[z.Id] = Now;
        int k = full ? 0 : Math.Clamp(z.QuarryLayer, 0, q.MaxK + 1);
        for (; k <= q.MaxK; k++)
        {
            ScanLayer(w, z, q, k);
            if (q.Dig.Count > 0 || q.Held > 0 || SealPending(q)) break;
        }
        q.Layer = k;
        q.Done = k > q.MaxK;
        if (z.QuarryLayer == k) return;
        z.QuarryLayer = k;
        z.Rev = Touch();
    }

    /// <summary>One layer: its cells to dig (not its step, not bedrock or below, not a seal: StoryColony.QuarryHazards.cs), and what to seal.</summary>
    private void ScanLayer(IStoryWorld w, ZoneState z, QuarryScan q, int k)
    {
        q.Dig.Clear();
        q.Held = 0;
        q.Hazards = 0;
        P3? step = q.Ring is { } ring ? StoryQuarry.Step(ring, q.Top, k) : null;
        P3? held = q.Ring is { } r && k > 0 && StepNeedsBlock(w, StoryQuarry.Step(r, q.Top, k - 1)) is not null
            ? StoryQuarry.Step(r, q.Top, k - 1).Add(0, -1, 0) : null;
        foreach (var (x, zz) in z.Cells.Sorted())
        {
            var at = new P3(x, q.Top - k, zz);
            var kind = ClassifyQuarryCell(w, z, q, at, step);
            if (kind == QuarryCell.Hazard) q.Hazards++;
            else if (kind == QuarryCell.Dig && at == held) q.Held++;
            else if (kind == QuarryCell.Dig) q.Dig.Add(at);
        }
        PruneWalledIn(w, q); // StoryColony.QuarryHazards.cs
    }

    /// <summary>A cell of the layer: skipped (its step, bedrock or under, open: dug or a cave's air), left standing as a seal, a
    /// block to place first (lava or water in it, a cave opening), or dug.</summary>
    private QuarryCell ClassifyQuarryCell(IStoryWorld w, ZoneState z, QuarryScan q, P3 at, P3? step)
    {
        if (at.Y < q.Bottoms[(at.X, at.Z)] || at == step || !w.Loaded(at.X, at.Z)) return QuarryCell.Skip;
        int id = w.Block(at.X, at.Y, at.Z).Id;
        if (StoryBlocks.Liquids.Contains(id)) return NoteHazard(q, at, LiquidKind(id), at, seal: at); // covered: a block placed in it
        if (OpenCell(id)) return OpenQuarryCell(w, z, q, at);
        return id == StoryQuarry.Bedrock ? QuarryCell.Skip : SolidQuarryCell(w, z, q, at);
    }

    /// <summary>Does a step need a block: null = it stands; false = place one; true = dig what would fall first, then place.</summary>
    private static bool? StepNeedsBlock(IStoryWorld w, P3 s)
    {
        int id = w.Block(s.X, s.Y, s.Z).Id;
        if (StoryQuarry.Falls(id)) return true;
        return OpenCell(id) || StoryBlocks.Liquids.Contains(id) ? false : null;
    }

    /// <summary>Every step from the top down to the one under the working layer, as the world has it now: the missing ones to place.</summary>
    private static void CheckStair(IStoryWorld w, QuarryScan q)
    {
        if (q.Ring is not { } ring) return;
        for (int j = 0; j <= Math.Min(q.Layer + 1, q.MaxK); j++)
        {
            var s = StoryQuarry.Step(ring, q.Top, j);
            if (StepNeedsBlock(w, s) is { } clear) q.StepFix.Add((s, clear));
        }
    }

    /// <summary>The liquid beside the cell (6 sides: lava flowing down from over it too), or null.</summary>
    private static P3? LiquidBeside(IStoryWorld w, P3 at)
    {
        foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0), (0, -1, 0) })
            if (StoryBlocks.Liquids.Contains(w.Block(at.X + dx, at.Y + dy, at.Z + dz).Id)) return at.Add(dx, dy, dz);
        return null;
    }

    /// <summary>
    /// The quarry's next job for this pawn: a stair step to place (top-most first), else, while the stair down to the working layer
    /// stands, the nearest exposed cell of that layer. Nothing: why goes to the idle audit.
    /// </summary>
    private ActiveJob? QuarryJob(PawnState p, PawnLive live, ZoneState z)
    {
        if (World is not { } w || Quarry(z) is not { Done: false } q) return null;
        if (q.Spawner != null) { Blocked($"quarry {ZoneName(z)}: {QuarryWaitReason(q)}"); return null; } // stopped (StoryColony.QuarryHazards.cs)
        var job = StairJob(p, live, z, q) ?? SealJob(p, live, z, q);
        if (job == null && !StairBroken(q) && !SealPending(q)) job = LayerJob(p, live, w, z, q);
        if (job == null && QuarryWaitReason(q, anyResting: true) is { } why) Blocked($"quarry {ZoneName(z)}: {why}");
        return job;
    }

    /// <summary>A step at or above the working layer is missing: nobody gets down there until it is placed.</summary>
    private static bool StairBroken(QuarryScan q) => q.StepFix.Any(f => f.At.Y >= q.Y);

    private ActiveJob? StairJob(PawnState p, PawnLive live, ZoneState z, QuarryScan q)
    {
        var from = live.Block;
        foreach (var (at, clear) in q.StepFix.OrderByDescending(f => f.At.Y))
        {
            if (!Open("b:" + at, p.Name, at)) continue;
            return StepBlockJob(p, live, z, at, clear, from);
        }
        return null;
    }

    private ActiveJob? StepBlockJob(PawnState p, PawnLive live, ZoneState z, P3 at, bool clear, P3 from) =>
        World is { } w && IsSourceAt(w, at, lava: true) ? QuarryLavaJob(p, live, z, at)
            : QuarryBlockJob(live, z, at, clear, from, PillarAny, $"building the quarry stair (y {at.Y})");

    /// <summary>A block placed for the quarry (a step, a seal) from what the pawn carries, else fetched; none: an alert.</summary>
    private ActiveJob? QuarryBlockJob(PawnLive live, ZoneState z, P3 at, bool clear, P3 from, int[] ids, string label)
    {
        var block = live.Inventory.FirstOrDefault(s => ids.Contains(s.Id));
        (StorageState Store, Stack Stack)? stored = block == null ? FindStored(s => ids.Contains(s.Id), from) : null;
        if (block == null && stored == null) { StairMaterialAlert(z); return null; }
        var item = block ?? stored!.Value.Stack;
        var j = Job(WorkTypes.Mine, "place", label, at, "b:" + at, "zone", z.Id, "working", f =>
        {
            f["block"] = ItemName(item.Id, item.Meta);
            f["reach"] = true; // an old cave opening may be high above the current quarry floor
            if (clear) f["clear"] = true;
            if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 16);
        }, xp: 5);
        if (stored is { } st2) j.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(16, st2.Stack.Count) }));
        return j;
    }

    private void StairMaterialAlert(ZoneState z)
    {
        Blocked($"quarry {ZoneName(z)}: needs cobblestone or dirt for its stair");
        if (_quarryAlert.TryGetValue("stair:" + z.Id, out var t) && Now - t < TimeSpan.FromMinutes(10)) return;
        _quarryAlert["stair:" + z.Id] = Now;
        Alert("warn", $"Quarry {ZoneName(z)} needs a block for its stair (cobblestone or dirt): none carried or stored");
    }

    /// <summary>The nearest cell of the working layer this pawn can dig now (its own floor last: it steps off first).</summary>
    private ActiveJob? LayerJob(PawnState p, PawnLive live, IStoryWorld w, ZoneState z, QuarryScan q)
    {
        var from = live.Block;
        var mine = live.Online ? Support(live.X, live.Y, live.Z).ToHashSet() : [];
        var stoodOn = StoodOnByOthers(p.Name);
        foreach (var at in q.Dig.OrderBy(c => c.Dist(from) + (mine.Contains(c) ? 1000 : 0)))
        {
            if (!QuarryCellOpen(p, w, at, stoodOn)) continue;
            int id = w.Block(at.X, at.Y, at.Z).Id;
            var tool = PlanTool(p, live, id);
            if (tool is { Wait: not null }) continue;
            P3? stand = mine.Contains(at) ? StandBeside(w, at, c => c == at || q.Dig.Contains(c)) : null;
            var job = Job(WorkTypes.Mine, "dig", $"quarrying (y {at.Y})", at, "b:" + at, "zone", z.Id, "working",
                f => { if (stand is { } s) f["stand"] = s.ToString(); });
            return WithTool(tool, QuarryDigBatch(p, live, w, z, q, job, mine, stoodOn));
        }
        return null;
    }

    /// <summary>Free, not resting, still there (not dug since the scan), exposed, and nobody else stands on it.</summary>
    private bool QuarryCellOpen(PawnState p, IStoryWorld w, P3 at, HashSet<P3> stoodOn) =>
        Open("b:" + at, p.Name, at) && !OpenCell(w.Block(at.X, at.Y, at.Z).Id) && Exposed(w, at) && !stoodOn.Contains(at)
        && !(Meta.TempBlocks?.Any(t => t.At == at && (t.Cancelled || !LeftBehind(t))) ?? false);

    /// <summary>Why a quarry with work left offers none (status and idle audit), or null. <paramref name="anyResting"/>: a pawn that found
    /// nothing there (its idle audit) hears of resting cells even when others are still open to other pawns.</summary>
    private string? QuarryWaitReason(QuarryScan q, bool anyResting = false)
    {
        if (HazardWait(q) is { } hw) return hw; // a spawner, a seal to place (StoryColony.QuarryHazards.cs)
        if (StairBroken(q)) return $"stair broken at y {q.StepFix.Max(f => f.At.Y)}: placing a step";
        if (q.Dig.Count == 0 && q.Held > 0) return $"waiting for the stair step over y {q.Y}";
        var resting = q.Dig.Where(QuarryResting).ToList();
        if (resting.Count == 0 || (!anyResting && resting.Count < q.Dig.Count)) return null;
        return RestingNote(q, resting);
    }

    /// <summary>
    /// The resting cells, named with their last failure (live 11-04 story-1: "quarry z17860: 1 cell(s) on y 57 resting after failures"
    /// for half an hour said neither which cell nor why: 181,57,366, "can't reach stone", under a stair step): "181,57,366 resting after
    /// failures (can't reach stone)", "3 cell(s) on y 57 resting after failures (e.g. 181,57,366: can't reach stone)".
    /// </summary>
    private string RestingNote(QuarryScan q, List<P3> resting)
    {
        var named = resting.Select(c => (At: c, Why: _quarryMiss.GetValueOrDefault("b:" + c))).FirstOrDefault(m => m.Why != null);
        string why = named.Why?.Replace(" at " + named.At, "", StringComparison.Ordinal) ?? "";
        if (resting.Count == 1) return $"{resting[0]} resting after failures" + (why.Length > 0 ? $" ({why})" : "");
        return $"{resting.Count} cell(s) on y {q.Y} resting after failures" + (why.Length > 0 ? $" (e.g. {named.At}: {why})" : "");
    }

    /// <summary>A quarry cell's last failure (its lease → why), for <see cref="RestingNote"/>; forgotten once the cell is done.</summary>
    private readonly Dictionary<string, string> _quarryMiss = new(StringComparer.Ordinal);

    /// <summary>Keeps why a quarry cell's job failed.</summary>
    private void NoteQuarryMiss(ActiveJob j, string why)
    {
        if (j.Source != "zone" || j.Lease is not { } lease || !lease.StartsWith("b:", StringComparison.Ordinal)) return;
        if (Zones.FirstOrDefault(z => z.Id == j.SourceId) is not { } zone || !IsQuarry(zone)) return;
        _quarryMiss[lease] = why;
        NoteQuarryDanger(lease, why); // in lava, suffocating: left standing (StoryColony.QuarryHazards.cs)
    }

    /// <summary>A cell resting after failures, for everyone or for any pawn (a "no path" rests it for that pawn only).</summary>
    private bool QuarryResting(P3 c) => Skipped("b:" + c) || Pawns.Keys.Any(n => Skipped(PawnKey("b:" + c, n)));

    /// <summary><c>update {id, mode: ores | quarry}</c> on a mine zone.</summary>
    private (JsonNode? Result, string? Error) MineModeOp(ZoneState z, string? mode)
    {
        if (mode is not ("ores" or "quarry")) return (null, "mode must be ores|quarry");
        if (mode == "quarry" && StoryQuarry.Ring(z.Cells) == null) return (null, QuarryTooNarrow);
        z.Mode = mode;
        z.Rev = Touch();
        StopInvalidQuarryBatches();
        _quarryScan.Remove(z.Id);
        _zoneScan.Remove(z.Id);
        WakeIdle();
        var o = new JsonObject { ["id"] = z.Id, ["mode"] = mode };
        var warnings = new List<string>();
        QuarryOverBuilds(z, warnings); // over builds' cells: the overlap is said (StoryColony.QuarryConflicts.cs)
        QuarryOverPreserved(z, warnings); // over preserved zones: the quarry's columns are its own (StoryColony.Preserve.cs)
        AddWarnings(o, warnings);
        return (o, null);
    }

    internal const string QuarryTooNarrow = "a quarry needs at least 2×2 (a 1-wide strip is a trench: paint it as Ores only, or designate Mine)";

    /// <summary>The state's quarry fields: mode, and for a quarry its progress and stair.</summary>
    private void QuarryJson(ZoneState z, JsonObject o)
    {
        o["mode"] = z.Mode ?? "ores";
        if (!IsQuarry(z) || Quarry(z) is not { } q || z.QuarryTop == null) return;
        o["quarry"] = new JsonObject
        {
            ["top"] = q.Top, ["bottom"] = q.Bottom, ["depth"] = q.MaxK + 1, ["layer"] = q.Layer, ["y"] = q.Y,
            ["cells_left"] = q.Dig.Count + q.Held, ["stair_fixes"] = q.StepFix.Count, ["sealed"] = q.Hazards, ["done"] = q.Done,
            ["stair"] = StairJson(q), ["status"] = QuarryStatus(z, q), ["hazards"] = HazardsJson(z, q),
        };
    }

    private static JsonArray StairJson(QuarryScan q)
    {
        var steps = new JsonArray();
        if (q.Ring is not { } ring) return steps;
        for (int k = 0; k <= q.MaxK && steps.Count < 512; k++) steps.Add(StoryQuarry.Step(ring, q.Top, k).Json());
        return steps;
    }

    private string QuarryStatus(ZoneState z, QuarryScan q)
    {
        if (q.Done) return "done: down to bedrock" + HazardSummary(z);
        if (q.Ring == null) return "no stair: the zone is too narrow";
        var state = $"layer {q.Layer + 1} of {q.MaxK + 1} (y {q.Y}), {q.Dig.Count + q.Held} cell(s) left";
        state = QuarryWaitReason(q) is { } why ? $"{state}: {why}" : state;
        return state + HazardSummary(z);
    }
}
