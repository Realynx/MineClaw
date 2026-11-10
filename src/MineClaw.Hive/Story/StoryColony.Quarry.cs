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
        public int Top, Bottom, Layer, AccessLayer;
        public int? WorkFeet;
        public bool Done;
        public List<(int X, int Z)>? Ring;
        public readonly Dictionary<(int X, int Z), int> Bottoms = new();
        /// <summary>The working layer's cells left to dig (resting ones too).</summary>
        public readonly List<P3> Dig = new();
        /// <summary>Cells under a step still waiting for its block (dug once the step stands).</summary>
        public int Held;
        /// <summary>Steps down to the work's access floor that need a block (placed first, top-most first).</summary>
        public readonly List<(P3 At, bool Clear)> StepFix = new();
        /// <summary>Obstructions over already excavated stair steps, ordered by the step they block.</summary>
        public readonly List<(P3 At, int Layer)> StairClear = new();
        /// <summary>Cells to place a sealing block in (lava or water in the zone, a breached cave's opening), from the layers looked at.</summary>
        public readonly List<(P3 At, string Kind)> SealFix = new();
        /// <summary>The seals among them behind the stair's head room (liquid on a side of or over one of its blocks): no pawn has a
        /// face of them in view, so their block is placed without <c>reach</c> (StoryColony.QuarryAccess.cs).</summary>
        public readonly HashSet<P3> Plugs = new();
        /// <summary>What the stair's top blocked head-room cell needs, worked out once per look, when first asked.</summary>
        public (P3 Cell, HeadFix Fix)? Head;
        /// <summary>The hazards the look found (left standing or to seal), by kind: cave, lava, water, sand, danger.</summary>
        public readonly List<(P3 At, string Kind, P3 Source)> Found = new();
        /// <summary>A monster spawner in or beside the working layer: the quarry stops until it is gone.</summary>
        public P3? Spawner;
        public int Hazards;
        /// <summary>The stair's first step: the leading steps over lower ground are not part of it (<see cref="FirstStep"/>).</summary>
        public int FirstStep;
        /// <summary>Open cells outside the zone this look flooded: true = a cave, false = a closed pocket (StoryColony.QuarryHazards.cs).</summary>
        public Dictionary<P3, bool>? Caves;
        /// <summary>The zone's columns no pawn has in view (they read as air), and the looked-at layer's cells in them.</summary>
        public int Unseen, UnseenCells;
        /// <summary>The trunks standing on the quarry, found once per look (StoryColony.QuarryTrees.cs).</summary>
        public List<P3>? Trees;
        /// <summary>A full look only: every cell it leaves standing, by kind, top-most first (StoryColony.QuarrySweep.cs).</summary>
        public List<(P3 At, string Kind)>? Left;
        public int MaxK => Top - Bottom;
        public int Y => Top - Layer;
    }

    private enum QuarryCell { Skip, Hazard, Dig, Seal }

    private readonly Dictionary<string, QuarryScan> _quarryScan = new();
    private readonly Dictionary<string, DateTimeOffset> _quarryFull = new();
    private readonly Dictionary<string, DateTimeOffset> _quarryAlert = new();

    internal static bool IsQuarry(ZoneState z) => z.Kind == "mine" && z.Mode == "quarry";

    /// <summary>The ground under open air in a column (no air, plant, log, leaf or liquid), looked for from 16 over to 16 under the zone's
    /// level first. The web paints a zone at the Controller's working level, not at the ground: a hill more than 16 higher read as "done:
    /// down to bedrock" at once (its top was a block inside the hill), ground more than 16 lower as nothing seen. So ground at the window's
    /// top is followed up to the first open cell, and a window with no ground is followed down.</summary>
    private static int? GroundTop(IStoryWorld w, int x, int z, int level)
    {
        if (!w.Loaded(x, z)) return null;
        int high = Math.Min(w.Height - 1, level + 16);
        for (int y = high; y >= Math.Max(1, level - 16); y--)
        {
            if (!Ground(w.Block(x, y, z).Id)) continue;
            if (y == high) while (y < w.Height - 1 && Ground(w.Block(x, y + 1, z).Id)) y++;
            return y;
        }
        for (int y = level - 17; y >= 1; y--)
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
        // No ground seen: with columns out of view it is not done (top 0) and a pawn goes to look (QuarryViewJob); all in view, it has none.
        if (!QuarryBounds(w, z, q)) { q.Done = q.Unseen == 0; return q; }
        q.FirstStep = FirstStep(w, q, z.QuarryLayer);
        FindLayer(w, z, q);
        if (!q.Done) { QuarryAccess(w, z, q); CheckStair(w, q); }
        if (!q.Done) q.Spawner = SpawnerNear(w, z, q); // StoryColony.QuarryHazards.cs
        QuarryHazardNews(z, q);
        ForgetLavaHold(w, z, q); // StoryColony.QuarryLiquids.cs
        return q;
    }

    /// <summary>The quarry's top (its highest ground once every column of it was seen, kept) and each column's bottom. While a column
    /// is not loaded the highest ground seen so far is this look's top, and nothing is kept: a top kept from part of the zone stayed
    /// wrong for good when the higher ground was in a column nobody had loaded yet. That top never goes down (the highest ground seen
    /// so far, remembered): digging the highest cell lowered it and moved the stair a block down, its steps dug and placed again.
    /// A column out of view has no bedrock to read: it takes the bottom kept when all were seen, else the lowest of those in view. Read
    /// as 1, it lowered the bottom of a finished quarry once its pawns left, and its miners were sent back to look, over and over.</summary>
    private bool QuarryBounds(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        var cols = z.Cells.Sorted();
        q.Unseen = cols.Count(cc => !w.Loaded(cc.X, cc.Z));
        if (z.QuarryTop is { } kept) q.Top = kept;
        else
        {
            var tops = cols.Select(cc => GroundTop(w, cc.X, cc.Z, z.Area.Y2)).OfType<int>().ToList();
            int? seen = _quarryTopSeen.TryGetValue(z.Id, out var s) && s.Cells == z.Cells.Count ? s.Top : null;
            if (tops.Count == 0 && seen == null) return false;
            q.Top = Math.Max(seen ?? int.MinValue, tops.Count > 0 ? tops.Max() : int.MinValue);
            _quarryTopSeen[z.Id] = (z.Cells.Count, q.Top);
            if (q.Unseen == 0) { z.QuarryTop = q.Top; z.Rev = Touch(); _quarryTopSeen.Remove(z.Id); }
        }
        foreach (var cc in cols) if (w.Loaded(cc.X, cc.Z)) q.Bottoms[cc] = ColumnBottom(w, cc.X, cc.Z);
        int known = q.Bottoms.Count > 0 ? q.Bottoms.Values.Min() : 1;
        if (q.Unseen == 0) z.QuarryBottom = known; // a hint, not shown: saved with the next change, no rev of its own
        foreach (var cc in cols) if (!q.Bottoms.ContainsKey(cc)) q.Bottoms[cc] = z.QuarryBottom ?? known;
        q.Bottom = q.Bottoms.Count > 0 ? q.Bottoms.Values.Min() : 1;
        return true;
    }

    /// <summary>A quarry's highest ground seen so far while a column of it is out of view (no top kept yet), with its cell count then.</summary>
    private readonly Dictionary<string, (int Cells, int Top)> _quarryTopSeen = new(StringComparer.Ordinal);

    /// <summary>
    /// The working layer: the top-most one with anything left (resting cells and cells held under a step included). From the top
    /// every 30 s (a cell refilled or left behind above is found again), else from the last working layer. A column out of view reads
    /// as air, so it read as dug: with no body near, every layer was empty and the quarry "done: down to bedrock" (and a done quarry
    /// sends nobody to load it). Now its cells hold the layer from the one reached on (never passed, never done); above it they were
    /// passed in view before.
    /// </summary>
    private void FindLayer(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        bool full = !_quarryFull.TryGetValue(z.Id, out var last) || Now - last > TimeSpan.FromSeconds(30);
        bool wasDone = z.QuarryLayer > q.MaxK;
        int k = ScanDown(w, z, q, full ? 0 : Math.Clamp(z.QuarryLayer, 0, q.MaxK + 1), full);
        // Done on a look from the working layer: once more from the top, so what it leaves is listed whole (StoryColony.QuarrySweep.cs).
        if (!full && !wasDone && k > q.MaxK)
        {
            q.Found.Clear();
            q.SealFix.Clear();
            k = ScanDown(w, z, q, 0, full = true);
        }
        if (full) _quarryFull[z.Id] = Now;
        q.Layer = k;
        q.Done = k > q.MaxK;
        if (full) QuarryLeftLook(w, z, q, becameDone: q.Done && !wasDone);
        if (z.QuarryLayer == k) return;
        z.QuarryLayer = k;
        z.Rev = Touch();
    }

    /// <summary>The layers from <paramref name="k"/> down to the first with work left (past the bottom: done); a full one keeps what stays.</summary>
    private int ScanDown(IStoryWorld w, ZoneState z, QuarryScan q, int k, bool full)
    {
        q.Left = full ? new() : null;
        for (; k <= q.MaxK; k++)
        {
            ScanLayer(w, z, q, k);
            if (q.Dig.Count > 0 || q.Held > 0 || SealPending(q) || q.UnseenCells > 0 && k >= z.QuarryLayer) break;
        }
        return k;
    }

    /// <summary>One layer: its cells to dig (not its step, not bedrock or below, not a seal: StoryColony.QuarryHazards.cs), and what to seal.</summary>
    private void ScanLayer(IStoryWorld w, ZoneState z, QuarryScan q, int k)
    {
        q.Dig.Clear();
        q.Held = 0;
        q.Hazards = 0;
        q.UnseenCells = 0;
        P3? step = q.Ring is { } ring && k >= q.FirstStep ? StoryQuarry.Step(ring, q.Top, k) : null;
        P3? held = q.Ring is { } r && k > q.FirstStep && StepNeedsBlock(w, StoryQuarry.Step(r, q.Top, k - 1)) is not null
            ? StoryQuarry.Step(r, q.Top, k - 1).Add(0, -1, 0) : null;
        foreach (var (x, zz) in z.Cells.Sorted())
        {
            var at = new P3(x, q.Top - k, zz);
            if (q.Unseen > 0 && !w.Loaded(x, zz))
            {
                if (at != step && at.Y >= q.Bottoms[(x, zz)]) q.UnseenCells++; // unknown, so not dug (FindLayer)
                continue;
            }
            var kind = ClassifyQuarryCell(w, z, q, at, step);
            if (q.Left != null) NoteLeft(w, q, at, step, kind); // a full look: what stays (StoryColony.QuarrySweep.cs)
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
        // Covered (a block placed in it, a lava source collected) while an open cell touches it; closed in, it is counted and left.
        if (StoryBlocks.Liquids.Contains(id)) return NoteHazard(q, at, LiquidKind(id), at, seal: LiquidExposed(w, at) ? at : null);
        if (OpenCell(id))
        {
            if (_quarryRefused.Count > 0) _quarryRefused.Remove(at); // gone after all: a cell refused before is forgotten
            return OpenQuarryCell(w, z, q, at);
        }
        return id == StoryQuarry.Bedrock ? QuarryCell.Skip : SolidQuarryCell(w, z, q, at);
    }

    /// <summary>Does a step need a block: null = it stands; false = place one; true = dig what would fall first, then place.</summary>
    private static bool? StepNeedsBlock(IStoryWorld w, P3 s)
    {
        int id = w.Block(s.X, s.Y, s.Z).Id;
        if (StoryQuarry.Falls(id)) return true;
        return OpenCell(id) || StoryBlocks.Liquids.Contains(id) ? false : null;
    }

    /// <summary>
    /// A step to repair now. A liquid in a step cell under rock is no missing step yet: nobody walks there while the cell over it
    /// stands, and no pawn has a face of it in view to place the block (or fill the bucket) by. Asked for anyway, the repair failed
    /// for ever and held the whole quarry ("stair broken at y N: placing a step"). The cell over it is the stair's head room, so it
    /// is dug (StoryColony.QuarryAccess.cs), and the liquid then gets its block or its bucket from above.
    /// </summary>
    private static bool? StepMissing(IStoryWorld w, P3 s)
    {
        if (StepNeedsBlock(w, s) is not { } clear) return null;
        if (clear || !StoryBlocks.Liquids.Contains(w.Block(s.X, s.Y, s.Z).Id)) return clear;
        int over = w.Block(s.X, s.Y + 1, s.Z).Id;
        return OpenCell(over) && !StoryBlocks.Liquids.Contains(over) ? clear : null;
    }

    /// <summary>Every step down to the actual access floor: missing blocks to place and headroom to clear.</summary>
    private static void CheckStair(IStoryWorld w, QuarryScan q)
    {
        if (q.Ring is not { } ring) return;
        for (int j = q.FirstStep; j <= q.AccessLayer; j++)
        {
            var s = StoryQuarry.Step(ring, q.Top, j);
            if (!w.Loaded(s.X, s.Z)) continue; // out of view it reads as air: no block is placed there blind
            if (StepMissing(w, s) is { } clear) q.StepFix.Add((s, clear));
            // A future step is still inside the intact layer. Clear deeper headroom only when a remnant's real floor requires it.
            if (q.AccessLayer <= q.Layer + 1 && j >= q.Layer) continue;
            for (int d = 1; d <= 3; d++)
            {
                var at = s.Add(0, d, 0);
                if (at.Y <= q.Top && !StoryWander.Passable(w.Block(at.X, at.Y, at.Z).Id)
                    && !q.StairClear.Any(c => c.At == at)) q.StairClear.Add((at, j));
            }
        }
    }

    /// <summary>
    /// The stair's first step. The top is the zone's highest ground, so where the ring starts lower (a bump of 2 anywhere in the zone)
    /// its first steps were air: one block lower got a block placed on the surface, two lower found no support and the stair stayed
    /// "broken" before the quarry ever started. Leading steps open to the sky up to the top, with ground 1 or 2 under them, are no
    /// part of it: a pawn walks from that ground onto the next step. A step over a deeper hole is placed as before, and only leading
    /// steps count, so one lost in the middle of the stair is still repaired. Ground the quarry dug since (a layer above
    /// <paramref name="passed"/>, the working layer the last look found) still counts: the step's column is dug out under it later.
    /// </summary>
    private static int FirstStep(IStoryWorld w, QuarryScan q, int passed)
    {
        if (q.Ring is not { } ring) return 0;
        int k = 0;
        for (; k <= q.MaxK && k < ring.Count; k++)
        {
            var s = StoryQuarry.Step(ring, q.Top, k);
            if (!w.Loaded(s.X, s.Z) || !SkyOver(w, s, q.Top) || !GroundUnder(w, q, s, passed)) break;
        }
        return k;
    }

    /// <summary>The cell and every cell over it up to the top are open (no liquid).</summary>
    private static bool SkyOver(IStoryWorld w, P3 s, int top)
    {
        for (int y = s.Y; y <= top; y++)
        {
            int id = w.Block(s.X, y, s.Z).Id;
            if (!OpenCell(id) || StoryBlocks.Liquids.Contains(id)) return false;
        }
        return true;
    }

    /// <summary>Ground 1 or 2 under the step's cell, or open cells there the quarry dug (layers over <paramref name="passed"/>).</summary>
    private static bool GroundUnder(IStoryWorld w, QuarryScan q, P3 s, int passed)
    {
        for (int d = 1; d <= 2; d++)
        {
            int id = w.Block(s.X, s.Y - d, s.Z).Id;
            if (Ground(id) || q.Top - (s.Y - d) < passed) return true;
            if (!OpenCell(id) || StoryBlocks.Liquids.Contains(id)) return false;
        }
        return false;
    }

    /// <summary>The top and the stair's first step its look uses (with nothing seen yet: the top as known, the ring's first cell).</summary>
    private (int Top, int First) QuarryStairStart(ZoneState z) =>
        Quarry(z) is { Top: > 0 } q ? (q.Top, q.FirstStep) : (QuarryTopOf(z), 0);

    /// <summary>The quarry's way in: the y of a pawn's feet on the stair's first step.</summary>
    private int QuarryEntry(ZoneState z)
    {
        var (top, first) = QuarryStairStart(z);
        return top - first + 1;
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
        if (q.Top == 0) return QuarryViewJob(p, live, z, q); // nothing of it seen yet
        if (q.Spawner != null) { Blocked($"quarry {ZoneName(z)}: {QuarryWaitReason(z, q)}"); return null; } // stopped (StoryColony.QuarryHazards.cs)
        var job = QuarryTreeJob(p, live, w, z, q); // trees on it are felled first (StoryColony.QuarryTrees.cs)
        bool felling = job != null || TreesStanding(w, z, q) > 0;
        if (job == null && !felling) job = StairJob(p, live, z, q);
        if (job == null && !felling && !StairBroken(q)) job = SealJob(p, live, z, q);
        if (job == null && !felling && !StairBroken(q) && !SealPending(q)) job = LayerJob(p, live, w, z, q);
        if (job == null && !felling && HeldUnseen(q)) return QuarryViewJob(p, live, z, q);
        // No free slot: its dig batch would dig nothing and ask again at once. Steps and seals are still placed (StoryColony.StorageFull.cs).
        if (job is { Kind: "dig" } && live.FreeSlots <= 0) { PocketsFullNote(p, live, $"quarry {ZoneName(z)}"); return null; }
        if (job == null && QuarryWaitReason(z, q, anyResting: true) is { } why) Blocked($"quarry {ZoneName(z)}: {why}");
        return job;
    }

    /// <summary>The work's access stair is incomplete or obstructed: repair it before entering to dig or seal.</summary>
    private static bool StairBroken(QuarryScan q) => q.StepFix.Count > 0 || q.StairClear.Count > 0;

    /// <summary>Nothing left in view on the working layer, but cells of it in columns out of view (FindLayer).</summary>
    private bool HeldUnseen(QuarryScan q) =>
        q.UnseenCells > 0 && q.Dig.Count == 0 && q.Held == 0 && !StairBroken(q) && !SealPending(q);

    private static string UnseenWords(QuarryScan q) => $"{q.Unseen} column(s) not in view";

    /// <summary>
    /// Columns of the quarry out of every pawn's view hold it: a pawn walks to the top of its stair (a <c>go</c>), and the columns load
    /// around it. One pawn at a time (its lease), and none that stands there already, so the walk never repeats in place; a failed one
    /// rests like any job. Nothing seen of it at all: to its ring's first cell at the level it was painted at.
    /// </summary>
    private ActiveJob? QuarryViewJob(PawnState p, PawnLive live, ZoneState z, QuarryScan q)
    {
        int top = q.Top > 0 ? q.Top : z.Area.Y2;
        var at = q.Ring is { } ring ? StoryQuarry.Step(ring, top, q.FirstStep).Add(0, 1, 0)
            : z.Cells.Sorted() is [var c, ..] ? new P3(c.X, top + 1, c.Z) : (P3?)null;
        string key = "qview:" + z.Id;
        if (at is not { } to || live.Block.Dist(to) <= 4 || !Open(key, p.Name, null))
        {
            Blocked($"quarry {ZoneName(z)}: {UnseenWords(q)}");
            return null;
        }
        return Job(WorkTypes.Mine, "go", $"looking over quarry {ZoneName(z)} ({UnseenWords(q)})", to, key, "zone", z.Id, "moving", f =>
        {
            f["dist"] = 2;
            f["quarry_access_y"] = to.Y; // the stair's top: no walk down it first (StoryColony.QuarryTravel.cs)
        }, xp: 0);
    }

    private ActiveJob? StairJob(PawnState p, PawnLive live, ZoneState z, QuarryScan q)
    {
        return QuarryStairRepair(p, live, z, q);
    }

    private ActiveJob? StepBlockJob(PawnState p, PawnLive live, ZoneState z, P3 at, bool clear, P3 from) =>
        World is { } w && IsSourceAt(w, at, lava: true) ? QuarryLavaJob(p, live, z, at)
            : QuarryBlockJob(p, live, z, at, clear, from, PillarAny, $"building the quarry stair (y {at.Y})");

    /// <summary>A block placed for the quarry (a step, a seal) from what the pawn carries, else fetched; none: an alert.
    /// <paramref name="reach"/> off: a plug behind the stair's head room, whose every face is hidden from the pit. The stationary
    /// placement of a <c>reach</c> job needs a face in view; the ordinary one walks up and clicks a hidden face.</summary>
    private ActiveJob? QuarryBlockJob(PawnState p, PawnLive live, ZoneState z, P3 at, bool clear, P3 from, int[] ids, string label, bool reach = true)
    {
        var block = live.Inventory.FirstOrDefault(s => ids.Contains(s.Id));
        (StorageState Store, Stack Stack)? stored = block == null ? FindStored(s => ids.Contains(s.Id), from) : null;
        if (block == null && stored == null) { StairMaterialAlert(z); return null; }
        // A fetch into full pockets takes nothing ("inventory full") and fails, resting the cell for every pawn: wait, and say why.
        if (stored != null && live.FreeSlots <= 0)
        {
            PocketsFullNote(p, live, $"quarry {ZoneName(z)}: a block for y {at.Y}", "fetching blocks");
            return null;
        }
        var item = block ?? stored!.Value.Stack;
        var j = Job(WorkTypes.Mine, "place", label, at, "b:" + at, "zone", z.Id, "working", f =>
        {
            f["block"] = ItemName(item.Id, item.Meta);
            if (reach) f["reach"] = true; // an old cave opening may be high above the current quarry floor
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
        int tools = 0;
        string? need = null, temp = null;
        foreach (var at in q.Dig.OrderBy(c => c.Dist(from) + (mine.Contains(c) ? 1000 : 0)))
        {
            if (!QuarryCellOpen(p, w, at, stoodOn)) { temp ??= TempWait(p, at); continue; }
            int id = w.Block(at.X, at.Y, at.Z).Id;
            var tool = PlanTool(p, live, id);
            if (tool is { Wait: { } wait }) { tools++; need ??= wait; continue; }
            P3? stand = mine.Contains(at) ? StandBeside(w, at, c => c == at || q.Dig.Contains(c)) : null;
            var job = Job(WorkTypes.Mine, "dig", $"quarrying (y {at.Y})", at, "b:" + at, "zone", z.Id, "working",
                f => { if (stand is { } s) f["stand"] = s.ToString(); });
            _quarryLayerWait.Remove(z.Id);
            return WithTool(tool, QuarryDigBatch(p, live, w, z, q, job, mine, stoodOn));
        }
        // Nothing for this pawn: cells waiting for a tool, or for another pawn's temporary block, are named (status and idle audit).
        string? why = tools > 0 ? $"{tools} cell(s) waiting for a tool ({WaitText(need)})" : temp;
        if (why != null) _quarryLayerWait[z.Id] = why; else _quarryLayerWait.Remove(z.Id);
        return null;
    }

    /// <summary>
    /// Per quarry: why the last pawn that asked got no cell of the layer while cells were left that weren't resting: "2 cell(s) waiting
    /// for a tool (needs an iron pickaxe — …)" (the colony makes one when it can: StoryColony.Gear.cs), "waiting for Bob's temporary
    /// block at …". Such cells held the layer with only "1 cell(s) left" said. Forgotten when a pawn gets a cell or nothing waits.
    /// </summary>
    private readonly Dictionary<string, string> _quarryLayerWait = new(StringComparer.Ordinal);

    /// <summary>Free, not resting, still there (not dug since the scan), exposed, nobody else stands on it, and no temporary block
    /// there holds it (<see cref="TempHeld"/>).</summary>
    private bool QuarryCellOpen(PawnState p, IStoryWorld w, P3 at, HashSet<P3> stoodOn) =>
        Open("b:" + at, p.Name, at) && !OpenCell(w.Block(at.X, at.Y, at.Z).Id) && Exposed(w, at) && !stoodOn.Contains(at)
        && TempHeld(p, at) == null;

    /// <summary>
    /// The temporary block at the cell that keeps it from this pawn, or null. A cancelled one is the owner's (it stands: the scan keeps
    /// it, <see cref="KeptTemp"/>); another pawn's is its own until it is left behind (StoryColony.TempBlocks.cs); the pawn's own is
    /// held only while it stands on that pillar. Its own reach pillar's stub held a small pit's layer for good: it never got 8 blocks
    /// away from it while it worked there, so the block was never "left behind", and nobody else was offered it either.
    /// </summary>
    private TempBlockState? TempHeld(PawnState p, P3 at)
    {
        if (Meta.TempBlocks is not { Count: > 0 } temps) return null;
        foreach (var t in temps)
        {
            if (t.At != at) continue;
            if (t.Cancelled || (t.Pawn == p.Name ? OnOwnPillar(t, Host.Live(p.Name)) : !LeftBehind(t))) return t;
        }
        return null;
    }

    /// <summary>"waiting for Bob's temporary block at x,y,z" while another pawn's block there isn't left behind yet, else null.</summary>
    private string? TempWait(PawnState p, P3 at) =>
        TempHeld(p, at) is { Cancelled: false } t && t.Pawn != p.Name ? $"waiting for {PawnDisplay(t.Pawn)}'s temporary block at {at}" : null;

    /// <summary>A temporary block whose cleanup the owner cancelled: his to keep, so the quarry leaves it standing.</summary>
    private bool KeptTemp(P3 at)
    {
        if (Meta.TempBlocks is not { Count: > 0 } temps) return false;
        foreach (var t in temps) if (t.At == at && t.Cancelled) return true;
        return false;
    }

    /// <summary>
    /// A quarry dig refused "not now": what <see cref="Transient"/> says for every target, and for a quarry cell also a fight that cut
    /// the dig short, low health, a fall not landed yet, a death or a body out of game. The cell rests 15 s instead of 30 s, 1 min, then
    /// 10 min, which held the whole layer (StoryColony.Picker.cs SoftFailure). Kept here: the other callers of Transient stay as they are.
    /// </summary>
    private bool QuarryDigTransient(ActiveJob j, string why) =>
        j.Source == "zone" && j.Kind == "dig" && Zones.FirstOrDefault(z => z.Id == j.SourceId) is { } zone && IsQuarry(zone)
        && (Transient(why) || QuarryNotNow.Any(w => why.Contains(w, StringComparison.OrdinalIgnoreCase)));

    private static readonly string[] QuarryNotNow = ["interrupted", "critical hp", "low health", "have not landed", "died", "dead", "not in game"];

    /// <summary>Why a quarry with work left offers none (status and idle audit), or null. <paramref name="anyResting"/>: a pawn that found
    /// nothing there (its idle audit) hears of resting cells even when others are still open to other pawns.</summary>
    private string? QuarryWaitReason(ZoneState z, QuarryScan q, bool anyResting = false)
    {
        if (q.Spawner != null) return HazardWait(z, q);
        if (FellingWait(z, q) is { } felling) return felling; // StoryColony.QuarryTrees.cs
        // The stair's top-most trouble, as its repair takes them (StoryColony.QuarryAccess.cs): what it does about it, or what stands there.
        if (BlockedHeadroom(q) is { } head) return $"stair blocked at {head}: {HeadroomWait(z, q, head)}";
        if (q.StepFix.Count > 0) return $"stair broken at y {q.StepFix.Max(f => f.At.Y)}: {StepWait(z, q)}";
        if (HazardWait(z, q) is { } hw) return hw; // a seal to place (StoryColony.QuarryHazards.cs)
        if (q.Dig.Count == 0 && q.Held > 0) return $"waiting for the stair step over y {q.Y}";
        if (HeldUnseen(q)) return UnseenWords(q);
        if (q.Dig.Count > 0 && _quarryLayerWait.TryGetValue(z.Id, out var layerWait)) return layerWait; // a tool, a temporary block
        var resting = q.Dig.Where(QuarryResting).ToList();
        if (resting.Count == 0 || (!anyResting && resting.Count < q.Dig.Count)) return SealRest(q);
        return RestingNote(q, resting);
    }

    /// <summary>A seal resting after failures (it doesn't hold the quarry), shown with its last failure as a resting cell is; else null.</summary>
    private string? SealRest(QuarryScan q)
    {
        foreach (var (at, kind) in q.SealFix)
            if (QuarryResting(at))
                return (World is { } w && KeptLava(w, at) ? $"collecting a lava source at {at}" : $"sealing {SealWords(kind)} at {at}") + RepairRest(at);
        return null;
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
        NoteQuarryRefused(zone, lease, why); // protected, preserved, unbreakable: left standing, said (StoryColony.QuarryHazards.cs)
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

    /// <summary>
    /// A paint or an erase (<paramref name="change"/>, tried on a copy of the zones with its own ids) that would leave a quarry with no
    /// stair ring, or merge a quarry with an ores-only zone, is refused and nothing changes; null = allowed. An L, a T or a notch has
    /// no ring (its walk ends at the inner corner): every cell was then dug, the old steps too, and no stair was kept ("no stair: the
    /// zone is too narrow"). A merged zone keeps one mode, so the other zone's columns silently switched to it. The web's preview and
    /// mock say the same (contracts storyZones.ts <c>quarryPaintRefusal</c>).
    /// </summary>
    private string? QuarryShapeRefusal(ZoneCells cells, Func<List<ZoneState>, Func<string>, ZonePaintResult?> change)
    {
        if (!Zones.Any(z => IsQuarry(z) && z.Cells.Touches(cells))) return null;
        var trial = Zones.Select(z => { var t = z.CopySettings(z.Id); t.SetCells(z.Cells.Clone(), z.Area.Y1, z.Area.Y2); return t; }).ToList();
        int n = 0;
        if (change(trial, () => "~trial" + ++n) is not { } r) return null;
        ZoneState? Old(string id) => Zones.FirstOrDefault(z => z.Id == id);
        foreach (var (into, from) in r.Merged)
        {
            var merged = from.Append(into).Select(Old).OfType<ZoneState>().ToList();
            if (merged.FirstOrDefault(IsQuarry) is { } qz && merged.FirstOrDefault(z => z.Kind == "mine" && !IsQuarry(z)) is { } oz)
                return QuarryMergeRefused(ZoneName(qz), ZoneName(oz));
        }
        foreach (var t in r.Changed)
        {
            if (!IsQuarry(t) || StoryQuarry.Ring(t.Cells) != null) continue;
            var was = Old(t.Id) ?? r.Split.Where(s => s.Into.Contains(t.Id)).Select(s => Old(s.From)).FirstOrDefault();
            if (was?.Id == t.Id && StoryQuarry.Ring(was.Cells) == null) continue; // it had none before: nothing is lost
            return QuarryStairRefused(ZoneName(was ?? t), grew: t.Cells.Count > (was?.Cells.Count ?? 0));
        }
        return null;
    }

    internal static string QuarryStairRefused(string name, bool grew) =>
        $"Quarry {name} would lose its stair: a quarry's edge must run all the way round, with no notch, L or T. Nothing changed: "
        + (grew ? "paint the new part as a quarry of its own, a column apart, or erase this one and paint the whole new shape"
            : "only whole rows or columns can come off its sides (or erase the whole quarry)");

    internal static string QuarryMergeRefused(string quarry, string ores) =>
        $"Quarry {quarry} and ores-only zone {ores} would merge into one. Nothing changed: leave a column between them, or give both the same mode first";

    /// <summary>The state's quarry fields: mode, and for a quarry its progress and stair.</summary>
    private void QuarryJson(ZoneState z, JsonObject o)
    {
        o["mode"] = z.Mode ?? "ores";
        // A column out of view keeps the top unkept: the look's progress and its "not in view" status are shown all the same.
        if (!IsQuarry(z) || Quarry(z) is not { } q || z.QuarryTop == null && q.Top <= 0 && q.Unseen == 0) return;
        var left = _quarryLeft.GetValueOrDefault(z.Id); // the last full look's (StoryColony.QuarrySweep.cs)
        if (q.Top <= 0) { UnseenQuarryJson(z, q, o); return; }
        o["quarry"] = new JsonObject
        {
            ["top"] = q.Top, ["bottom"] = q.Bottom, ["depth"] = q.MaxK + 1, ["layer"] = q.Layer, ["y"] = q.Y,
            ["cells_left"] = q.Dig.Count + q.Held, ["stair_fixes"] = q.StepFix.Count, ["sealed"] = left?.Sealed ?? q.Hazards, ["done"] = q.Done,
            ["stair"] = StairJson(q), ["status"] = QuarryStatus(z, q), ["hazards"] = HazardsJson(z, q),
            ["left"] = LeftJson(left), ["leftovers"] = LeftoversJson(left),
        };
    }

    /// <summary>Nothing of the quarry seen yet (its columns out of view): the level it was painted at, no stair, and why.</summary>
    private static void UnseenQuarryJson(ZoneState z, QuarryScan q, JsonObject o) => o["quarry"] = new JsonObject
    {
        ["top"] = z.Area.Y2, ["bottom"] = z.Area.Y2, ["depth"] = 0, ["layer"] = 0, ["y"] = z.Area.Y2, ["cells_left"] = 0,
        ["stair_fixes"] = 0, ["sealed"] = 0, ["done"] = false, ["stair"] = new JsonArray(), ["status"] = UnseenWords(q),
    };

    private static JsonArray StairJson(QuarryScan q)
    {
        var steps = new JsonArray();
        if (q.Ring is not { } ring) return steps;
        for (int k = q.FirstStep; k <= q.MaxK && steps.Count < 512; k++) steps.Add(StoryQuarry.Step(ring, q.Top, k).Json());
        return steps;
    }

    private string QuarryStatus(ZoneState z, QuarryScan q)
    {
        if (q.Done) return "done: down to bedrock" + HazardSummary(z);
        if (q.Ring == null) return "no stair: the zone is too narrow";
        var state = $"layer {q.Layer + 1} of {q.MaxK + 1} (y {q.Y}), {q.Dig.Count + q.Held} cell(s) left";
        state = QuarryWaitReason(z, q) is { } why ? $"{state}: {why}" : state;
        return state + HazardSummary(z);
    }
}
