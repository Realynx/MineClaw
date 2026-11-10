using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>
/// Builds reconcile with the world (docs/web/story-mode.md "Builds reconcile with the world"; Fox 11-05: "We're not even detecting the
/// stairs … rotated the wrong way … it's saying there's no way to place blocks, but there definitely is"). Every few seconds each
/// build cell is compared with the live block (id and the meta bits the placement decides, as <see cref="Matches"/> does): the design's
/// block → done; air → to place; its block turned another way → misrotated (taken back and placed again when it can be); another block
/// (natural stone or dirt, a block put there by mistake) → to clear first. A cell once done that no longer matches reopens, and its
/// build with it. The support rule is the pawn client's own (<see cref="PlaceRules.IsSupport"/>), so a cell is offered once something
/// beside it can be clicked; a build's cells over another build's unplaced ones wait for those, and a truly floating cell gets a
/// scaffold block under it that is dug again once it stands.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>How often every build's cells are compared with the world.</summary>
    public static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(5);

    private DateTimeOffset _reconciledAt = DateTimeOffset.MinValue;

    /// <summary>Done cells that didn't match on the last pass (<c>build|x,y,z</c>): reopened when the next pass agrees (the view's copy
    /// of a fresh placement can come a moment after the pawn's report).</summary>
    private HashSet<string> _driftSeen = new(StringComparer.Ordinal);

    /// <summary>Blocked-cell alerts said per build and reason (coordinates left out): a hundred cells failing alike are one alert.</summary>
    private readonly HashSet<string> _cellAlerted = new(StringComparer.Ordinal);

    /// <summary>One reason of one build's blocked cells: when a few of them are next tried again, and how many tries it has had.</summary>
    private readonly record struct Probe(DateTimeOffset NextAt, int Round);

    /// <summary>Per build and reason (coordinates and numbers left out). In memory only: after a restart every reason has no entry, so
    /// cells blocked before it are tried on the first pass.</summary>
    private readonly Dictionary<(string Build, string Reason), Probe> _probe = new();

    /// <summary>A blocked cell reopened for a try: the build and reason it is a try of, how often it was handed to a pawn, and the
    /// reason's text it was blocked with (a miss that did not count says its own text meanwhile).</summary>
    private readonly record struct Try(string Build, string Reason, int Handed, string? Said = null)
    {
        public bool Of((string Build, string Reason) cls) => Build == cls.Build && Reason == cls.Reason;
    }

    /// <summary>The blocked cells reopened for a try, neither finished nor blocked again yet.</summary>
    private readonly Dictionary<GhostState, Try> _sampled = new(ReferenceEqualityComparer.Instance);

    /// <summary>Cells finished since the last pass began (a blocked cell beside one has its support or wall now), and the set the pass
    /// now running reads.</summary>
    private HashSet<P3> _doneSince = new(), _doneBefore = new();

    /// <summary>One build's blocked cells that may be tried again, as its pass meets them (kept between passes: nothing is allocated).</summary>
    private readonly List<GhostState> _blockedNow = new();

    /// <summary>The pass reopened a cell: materials and idle pawns are told once, at its end.</summary>
    private bool _reopenedNow;

    /// <summary>What a pass found changed in one build.</summary>
    private struct Drift
    {
        public int Turned, Other, Missing;
        public readonly int Total => Turned + Other + Missing;
    }

    private void ReconcileTick()
    {
        if (World is not { } w || Now - _reconciledAt < ReconcileEvery) return;
        _reconciledAt = Now;
        Reconcile(w);
    }

    /// <summary>One pass over every unpaused build (done ones too: a finished house that changed reopens).</summary>
    internal void Reconcile(IStoryWorld w)
    {
        ReconcileChestDeconstruction(w);
        BuildConflictsPass();
        PreservePass(w); // preserved zones: their changed cells become their repair builds' ghosts (StoryColony.PreserveRepair.cs)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var temps = ActiveTempCells(); // one left to the owner is an ordinary block in the cell (to clear), not a pawn's access
        _chestsAheadRead = false;
        _botPlacedRead = false;
        BeginSampling();
        foreach (var b in Builds.ToList())
            if (b.Status != "paused") ReconcileBuild(w, b, seen, temps);
        _driftSeen = seen;
        if (_reopenedNow) { MaterialsChanged(); WakeIdle(); }
    }

    private void ReconcileBuild(IStoryWorld w, BuildState b, HashSet<string> seen, HashSet<P3> temps)
    {
        var drift = new Drift();
        _blockedNow.Clear();
        foreach (var g in b.Ghosts)
        {
            if (BuildConflicted(g) || !w.Loaded(g.At.X, g.At.Z) || QuarryHeld(b, g)) continue;
            if (ReplacementBuild(b) && temps.Contains(g.At)) continue;
            if (ReplacementAir(b, g) && g.State != "leased")
            {
                ReconcileAir(w, b, g, seen, ref drift);
            }
            else if (Reconcilable(g)) ReconcileCell(w, b, g, seen, ref drift);
            // After the branches (a dig of a plain remove build takes neither), with what they just found: still blocked, and worth a try.
            if (g.State == "blocked" && g.Job && !NeverSampled(g)) _blockedNow.Add(g);
        }
        if (_blockedNow.Count > 0) SampleBlocked(b);
        // Only cells a newer build took over are left: done (StoryColony.BuildConflicts.cs). Else a quarry may dig what is left
        // (StoryColony.QuarryConflicts.cs).
        if (!SupersededOnly(b)) ConflictStatus(b);
        if (drift.Total > 0) Reopened(b, drift);
        else if (b.Status is not ("done" or "paused") && AllBuilt(b)) { b.Status = "done"; b.Reason = null; b.Rev = Touch(); }
    }

    /// <summary>Every cell shows the design (built by hand, or the view caught up), or is left misrotated.</summary>
    private static bool AllBuilt(BuildState b) => b.Ghosts.Any(g => g.Job) && b.Ghosts.Where(g => g.Job).All(g => g.State is "done" or "misrotated");

    /// <summary>A placed block of the design (not a dig, a pour, a second half, one at work now or one pawns can't place).</summary>
    private static bool Reconcilable(GhostState g) =>
        g.Job && !g.Remove && !StoryConstruction.IsLiquid(g) && g.State is not ("leased" or "unsupported");

    /// <summary>A canceled builder's last atomic placement can arrive after replacement was queued. Empty-room ownership catches
    /// that late block, but never competes with a pawn's registered temporary access or a newer construction directive.</summary>
    private void ReconcileAir(IStoryWorld w, BuildState b, GhostState g, HashSet<string> seen, ref Drift drift)
    {
        var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (id == 0) { Settled(b, g); return; }
        if (g.State == "done")
        {
            if (!BotPlaced(g.At, id)) return; // the owner's sign on a chest, a torch, a bench he put there: his, left alone (C11)
            var key = $"{b.Id}|{g.At}";
            if (!_driftSeen.Contains(key)) { seen.Add(key); return; }
            Reopen(b, g);
            drift.Other++;
        }
        if (g.Id != id || g.Meta != meta) { g.Id = id; g.Meta = meta; g.Rev = Touch(); }
        if (StoryBlocks.Undiggable.Contains(id))
        {
            string why = "cannot clear " + BlockLabel(w, id);
            if (g.State != "blocked" || g.Reason != why) { g.State = "blocked"; g.Reason = why; g.Rev = Touch(); }
        }
        else if (g.State == "blocked" && g.Reason?.StartsWith("cannot clear ", StringComparison.Ordinal) == true) Reopen(b, g);
    }

    /// <summary>The cells the hive's placed-block log puts a block of a hive bot's on, read only when a finished empty-space cell holds
    /// a block (null until then), at most once per pass, and for blocks already asked about at most every <see cref="BotPlacedEvery"/>.</summary>
    private Dictionary<P3, int>? _botPlaced;

    /// <summary>When <see cref="_botPlaced"/> was read, and whether this pass read it.</summary>
    private DateTimeOffset _botPlacedAt;

    private bool _botPlacedRead;

    /// <summary>The cells asked about, with the block they held: the same block again is answered from the last read.</summary>
    private readonly Dictionary<P3, int> _botAsked = new();

    /// <summary>How long a read of the placed-block log answers for blocks already asked about. The owner's block in a finished room's
    /// empty space stands for good (C11): before, every 5 s pass copied every body's log (thousands of rows) under the colony lock.
    /// A block new to its cell still reads the log at once (a canceled builder's late placement is taken out on the next passes).</summary>
    public static readonly TimeSpan BotPlacedEvery = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A hive bot put this block here (the placed-block log: a canceled builder's late placement, a pawn's pillar): a finished design's
    /// empty space takes it out again. Anything else is the owner's or grew there and stays (owner's default, C11: before, every block in
    /// a done empty-space cell was dug out, a storage sign on a chest included, and a bench the colony placed was dug out and placed
    /// again). An unreadable log removes nothing.
    /// </summary>
    private bool BotPlaced(P3 at, int id)
    {
        bool known = _botAsked.TryGetValue(at, out int asked) && asked == id;
        if (_botPlaced == null || !_botPlacedRead && (!known || Now - _botPlacedAt >= BotPlacedEvery))
        {
            (_botPlaced ??= new()).Clear();
            if (_botAsked.Count >= 4096) _botAsked.Clear(); // kept across reads (a fresher read answers them), cleared only when large
            _botPlacedAt = Now;
            _botPlacedRead = true;
            try
            {
                foreach (var (p, placed, _) in Host.PlacedByBots()) _botPlaced[p] = placed;
            }
            catch (Exception) { }
        }
        _botAsked[at] = id;
        return _botPlaced.TryGetValue(at, out int was) && SameTempBlock(was, id);
    }

    private void ReconcileCell(IStoryWorld w, BuildState b, GhostState g, HashSet<string> seen, ref Drift drift)
    {
        var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (Matches(g, id, meta, Modern)) { Settled(b, g); return; }
        if (g.State == "done") { DoneDrifted(b, g, id, meta, seen, ref drift); return; }
        // Recover saved dependency failures from older runtimes; the support-aware picker decides when they are ready.
        if (g.State == "blocked" && g.Reason is { } reason && NoFaceYet(reason))
        {
            Reopen(b, g);
            if (b.Status == "blocked") { b.Status = "queued"; b.Reason = null; b.Rev = Touch(); }
            else if (b.Reason is { } held && NoFaceYet(held)) { b.Reason = null; b.Rev = Touch(); }
        }
        if (g.State == "misrotated" && StoryBlocks.Replaceable.Contains(id)) Reopen(b, g); // its block is gone: placed anew
        // A clear refused for good is never tried again; with the block in the way gone there is nothing left to clear.
        if (g.State == "blocked" && g.Reason is { } refused && ClearRefused(refused) && StoryBlocks.Replaceable.Contains(id))
        {
            Reopen(b, g);
            _reopenedNow = true;
        }
        if (g.State == "blocked" && StoryConstruction.IsChest(g) && PlaceInto(id)) ChestRuleNow(w, b, g);
        MarkHolds(w, g, id, meta);
    }

    /// <summary>Every build's chest cells still to come, gathered once per pass when a blocked chest cell first asks.</summary>
    private readonly Dictionary<P3, int> _chestsAhead = new();

    private bool _chestsAheadRead;

    /// <summary>
    /// A blocked chest cell and the game's chest rule (StoryConstruction.Chests.cs). Blocked by the server taking it back (or by the
    /// pawn's refusal) and the world's chests explain it: it gets the rule's reason (the storehouse's cells blocked before 11-10 said
    /// "server undid my placement (protected area?)"). Blocked by the rule, and the rule allows it now over the world and every build's
    /// chest cells still to come (the chest beside it is gone): open again.
    /// </summary>
    private void ChestRuleNow(IStoryWorld w, BuildState b, GhostState g)
    {
        bool rule = StoryConstruction.IsChestRule(g.Reason);
        if (!rule && !ServerTookBack(g.Reason)) return;
        if (rule && !_chestsAheadRead) { _chestsAhead.Clear(); ChestsToCome(_chestsAhead); _chestsAheadRead = true; }
        _ruleWorld = w;
        _ruleAhead = rule;
        if (!ChestPairs.CanPlace(_ruleAt ??= RuleAt, new BlockPos(g.At.X, g.At.Y, g.At.Z), g.Id, out var clash))
        {
            // The same clash as the last pass with the reason it gave still on the cell: nothing to build (a storehouse's
            // rule-blocked chests stay so for good, and each pass asked again).
            if (_ruleSaid.TryGetValue(g, out var last) && last.Clash == clash && ReferenceEquals(last.Why, g.Reason)) return;
            var why = StoryConstruction.ChestRuleReason(clash.Words, g.Id, Modern);
            if (g.Reason != why) { g.Reason = why; g.Rev = Touch(); }
            if (_ruleSaid.Count >= 4096) _ruleSaid.Clear();
            _ruleSaid[g] = (clash, g.Reason);
            return;
        }
        _ruleSaid.Remove(g);
        if (!rule) return;
        Reopen(b, g);
        _chestsAhead[g.At] = g.Id; // a chest to come now: a blocked cell beside it, met later in this pass, sees it
        _reopenedNow = true;
        if (b.Status == "blocked") { b.Status = "queued"; b.Reason = null; b.Rev = Touch(); }
    }

    /// <summary>What <see cref="ChestRuleNow"/> asks the rule over: the world, and with <see cref="_ruleAhead"/> every build's chest
    /// cells still to come. Fields and one delegate, so a pass allocates nothing per blocked chest cell.</summary>
    private IStoryWorld? _ruleWorld;

    private bool _ruleAhead;
    private Func<BlockPos, int>? _ruleAt;

    private int RuleAt(BlockPos q) =>
        _ruleAhead && _chestsAhead.TryGetValue(new P3(q.X, q.Y, q.Z), out int id) ? id
        : _ruleWorld is { } w && w.Loaded(q.X, q.Z) ? w.Block(q.X, q.Y, q.Z).Id : -1;

    /// <summary>Per refused chest cell: the clash its reason was built from and that reason.</summary>
    private readonly Dictionary<GhostState, (ChestPairs.Clash Clash, string? Why)> _ruleSaid = new(ReferenceEqualityComparer.Instance);

    /// <summary>A placement the server took back (or the pawn refused as the server would): the chest rule may be why.</summary>
    private static bool ServerTookBack(string? why) =>
        why != null && (why.Contains("server undid my placement", StringComparison.Ordinal) || why.Contains("didn't appear", StringComparison.Ordinal)
                        || why.Contains("identical server refusals", StringComparison.Ordinal) || why.Contains(ChestPairs.RefusalStart, StringComparison.Ordinal)
                        || why.StartsWith(ServerRefused, StringComparison.Ordinal));

    /// <summary>The cell shows the design's block: done (built by hand, or the view caught up), nothing to clear.</summary>
    private void Settled(BuildState b, GhostState g)
    {
        SetHolds(g, null, null);
        if (g.State != "done") CellDone(b, g);
    }

    /// <summary>A done cell that no longer matches: seen twice in a row, it reopens (a block turned wrong that holds items is said instead).</summary>
    private void DoneDrifted(BuildState b, GhostState g, int id, int meta, HashSet<string> seen, ref Drift drift)
    {
        var key = $"{b.Id}|{g.At}";
        if (!_driftSeen.Contains(key)) { seen.Add(key); return; }
        bool turned = WrongTurn(g, id, meta, Modern);
        if (turned) drift.Turned++;
        else if (StoryBlocks.Replaceable.Contains(id)) drift.Missing++;
        else drift.Other++;
        Reopen(b, g);
        if (turned && KeepsItems(id)) Misrotated(b, g, meta); // a chest or furnace is never broken open for its rotation
        else if (World is { } w) MarkHolds(w, g, id, meta);
    }

    /// <summary>The cell is to be built again: todo, its failures, rest and take-back forgotten; a second half (a door's top) with it.</summary>
    private void Reopen(BuildState b, GhostState g)
    {
        ForgetCellFailures(b, g);
        g.State = "todo";
        g.Rev = Touch();
        CellReasonGone(b, g);
        foreach (var o in b.Ghosts)
            if (!o.Job && o.State == "done" && Math.Abs(o.At.X - g.At.X) + Math.Abs(o.At.Y - g.At.Y) + Math.Abs(o.At.Z - g.At.Z) == 1)
            { o.State = "todo"; o.Rev = g.Rev; }
    }

    /// <summary>A build some of whose finished cells changed: queued again and said once.</summary>
    private void Reopened(BuildState b, Drift d)
    {
        if (b.Status is "done" or "blocked") { b.Status = "queued"; b.Reason = null; }
        b.Rev = Touch();
        MaterialsChanged();
        WakeIdle();
        SayReopened(b, d);
    }

    /// <summary>How often one build's reopening is alerted; between, the cells are counted and said with the next alert.</summary>
    public static readonly TimeSpan ReopenAlertEvery = TimeSpan.FromMinutes(10);

    /// <summary>Per build: when its reopening was last alerted, and the cells reopened since.</summary>
    private readonly Dictionary<string, (DateTimeOffset At, int Since)> _reopenSaid = new(StringComparer.Ordinal);

    /// <summary>One alert per build per <see cref="ReopenAlertEvery"/> (live 11-05: ×22 in 7 minutes), with a running count of the rest.</summary>
    private void SayReopened(BuildState b, Drift d)
    {
        var parts = new List<string>();
        if (d.Turned > 0) parts.Add($"{d.Turned} turned the wrong way");
        if (d.Other > 0) parts.Add($"{d.Other} hold another block");
        if (d.Missing > 0) parts.Add($"{d.Missing} missing");
        var text = $"Build {b.Id} ({b.Label}): {d.Total} block{(d.Total == 1 ? "" : "s")} no longer match the design ({string.Join(", ", parts)}): reopened";
        if (_reopenSaid.TryGetValue(b.Id, out var said) && Now - said.At < ReopenAlertEvery)
        {
            _reopenSaid[b.Id] = (said.At, said.Since + d.Total);
            _log.LogInformation("story {Colony}: {Text}", Id, text);
            return;
        }
        if (said.Since > 0) text += $" ({said.Since} more reopened since the last alert)";
        _reopenSaid[b.Id] = (Now, 0);
        Alert("warn", text);
    }

    /// <summary>What an open cell holds when it isn't the design's block: another block (clear) or its block turned wrong (misrotated).</summary>
    private void MarkHolds(IStoryWorld w, GhostState g, int id, int meta)
    {
        if (PlaceInto(id) || g.State is not ("todo" or "blocked")) { SetHolds(g, null, null); return; }
        if (WrongTurn(g, id, meta, Modern))
        {
            string Words(int m) => BlockOrientation.Describe(g.Id, m, Modern) ?? $"meta {m}";
            SetHolds(g, "misrotated", $"placed {Words(meta)}, wants {Words(g.Meta)}");
            return;
        }
        SetHolds(g, "clear", $"holds {BlockLabel(w, id)}: dug out first");
    }

    private void SetHolds(GhostState g, string? kind, string? words)
    {
        if (g.Holds == kind && g.HoldsWords == words) return;
        g.Holds = kind;
        g.HoldsWords = words;
        g.Rev = Touch();
    }

    // ------------------------------------------------------------------ support: the client's own rule

    /// <summary>A block a placement can click against, by the pawn client's rule (<see cref="PlaceRules.IsSupport"/>: anything with a
    /// collision box that doesn't open when clicked, or a 1.12.2 container the pawn sneak-clicks), in the colony's view.</summary>
    private bool Clickable(P3 p) => World is { } w && PlaceRules.IsSupport(AccessOf(w), new BlockPos(p.X, p.Y, p.Z), allowSneakInteraction: true);

    /// <summary>Something beside the cell can be clicked now (the client won't say "nothing solid on any side").</summary>
    private bool HasSupport(P3 p) => StoryConstruction.Supported(p, Clickable);

    /// <summary>A block goes into a cell holding this without digging it first, by the pawn client's rule (<see cref="Aiming.IsReplaceable"/>:
    /// air, liquids, fire, snow layer, tall grass). A dead bush, a vine or a two-tall plant is in the way: the client refuses ("is
    /// occupied by"), so the cell clears it first. <see cref="StoryBlocks.Replaceable"/> keeps its wider meaning for water, mines and
    /// station spots.</summary>
    private bool PlaceInto(int id) => id == 0 || World is { } w && Aiming.IsReplaceable(AccessOf(w), id);

    /// <summary>The cell's block has the face it is placed against (<see cref="BlockOrientation.PlaceFaces"/>): a wall torch its wall, a
    /// door the block under it; a chest or a block that does not turn any neighbour. <paramref name="placed"/>: cells laid earlier in
    /// the same batch count as there. Six block reads at most, nothing allocated.</summary>
    private bool FaceReady(GhostState g, HashSet<P3>? placed = null)
    {
        int mask = BlockOrientation.PlaceFaces(g.Id, g.Meta, Modern);
        for (int f = 0; f < 6; f++)
        {
            if ((mask & 1 << f) == 0) continue;
            var n = Against(g.At, f);
            if (placed?.Contains(n) == true || Clickable(n)) return true;
        }
        return false;
    }

    /// <summary>The block a click on face <paramref name="face"/> (D0 U1 N2 S3 W4 E5) places against: the cell minus the face's normal.</summary>
    private static P3 Against(P3 at, int face) => face switch
    {
        0 => at.Add(0, 1, 0),
        1 => at.Add(0, -1, 0),
        2 => at.Add(0, 0, 1),
        3 => at.Add(0, 0, -1),
        4 => at.Add(1, 0, 0),
        _ => at.Add(-1, 0, 0),
    };

    /// <summary>Torches, ladders, buttons and levers hang on the block they are placed against: never on a scaffold, which is dug back
    /// (the block would drop and its cell reopen: a place-and-remove loop).</summary>
    private static bool Hangs(int id) => PlaceRules.IsAttachable(id) || id is 75 or 143;

    /// <summary>A seed a pawn can start on: a dig, a pour, or a block with the face it is placed against (<see cref="FaceReady"/>).
    /// One without it waits, said as "nothing to place against yet": no failure, no trip.</summary>
    private bool SeedReady(GhostState g) => g.Remove || StoryConstruction.IsLiquid(g) || FaceReady(g);

    /// <summary>
    /// None of the level's open cells has anything to place against. When one of them touches a cell another build (or another pawn's
    /// batch) is still to place, they wait for it (a house's planks over its unbuilt stone footing). Else the level floats: the nearest
    /// cell with a spot beside it (under it first) that can hold a scaffold block → (cell, spot); null = it waits.
    /// </summary>
    private (GhostState Seed, P3 Spot)? FloatingSeed(BuildState b, List<GhostState> open, P3 from)
    {
        var cells = PendingPlaceCells(b);
        foreach (var g in open)
            foreach (var n in Neighbours(g.At))
                if (cells.Contains(n)) return null;
        // No pending neighbour can provide support. Reuse this search's set for every reserved build cell, including
        // finished/paused builds, digs and non-job cells. Rebuilding it per candidate made large floating levels allocate MBs.
        // A design's empty space already cleared may hold the scaffold: it is registered as temporary, and the reconcile pass
        // leaves a pawn's temporary block there alone (inside a placed design every cell is a ghost: nowhere else to stand it).
        foreach (var owner in Builds)
            foreach (var ghost in owner.Ghosts)
                if (!(ghost.State == "done" && ReplacementAir(owner, ghost))) cells.Add(ghost.At);
        // Never a cell that merely lacks its own face (something beside it can be clicked, not the face its meta needs): it waits for
        // that face; and a hanging block is never scaffolded.
        foreach (var g in open.Where(g => !g.Remove && !Hangs(g.Id) && (FaceReady(g) || !HasSupport(g.At))).OrderBy(g => g.At.Y).ThenBy(g => g.At.Dist(from)))
            if (ScaffoldSpot(g.At, cells, BlockOrientation.PlaceFaces(g.Id, g.Meta, Modern)) is { } spot) return (g, spot);
        return null;
    }

    /// <summary>The scaffold spot when this seed is the floating level's (<see cref="FloatingSeed"/>), else null.</summary>
    private static P3? ScaffoldOf(GhostScan scan, GhostState seed) => scan.Scaffold is { } sc && sc.Seed == seed.At ? sc.Spot : null;

    /// <summary>Cells still to be placed that will give support once in: other builds' open ones (and blocked ones a later try may
    /// place: not one no try is spent on, <see cref="NeverSampled"/>, such as a chest the game refuses), and this build's leased ones.</summary>
    private HashSet<P3> PendingPlaceCells(BuildState b)
    {
        var set = new HashSet<P3>();
        foreach (var o in Builds)
        {
            if (o.Status is "done") continue;
            foreach (var g in o.Ghosts)
                if (g.Job && !g.Remove && !Conflicted(g)
                    && (o == b ? g.State == "leased" : g.State is "todo" or "leased" || g.State == "blocked" && !NeverSampled(g))) set.Add(g.At);
        }
        return set;
    }

    /// <summary>Where a scaffold block can go for a floating cell: an open spot beside it (under it first), no build's cell, never on a
    /// container's lid or a workstation (it would shut it), with something to click against itself. A turned block's spot is one it
    /// can be placed against (a face of <paramref name="faces"/>, <see cref="BlockOrientation.PlaceFaces"/>).</summary>
    private P3? ScaffoldSpot(P3 cell, HashSet<P3> cells, int faces)
    {
        if (World is not { } w) return null;
        foreach (int f in ScaffoldFaces)
        {
            if ((faces & 1 << f) == 0) continue;
            var n = Against(cell, f);
            var (id, _) = w.Block(n.X, n.Y, n.Z);
            if (id == 0 && w.Loaded(n.X, n.Z) && !cells.Contains(n) && !OnALid(w, n) && HasSupport(n)) return n;
        }
        return null;
    }

    /// <summary>The faces a scaffold spot is tried on, in <see cref="Neighbours"/>' order: under first (the top face), then +x, −x, +z,
    /// −z, over.</summary>
    private static readonly int[] ScaffoldFaces = [1, 4, 5, 2, 3, 0];

    /// <summary>Containers and workstations a temporary block never stands on: a chest under a block won't open.</summary>
    internal static readonly HashSet<int> LidBlocks = [54, 146, 130, 61, 62, 58, 23, 158, 154, 117, 116, 145];

    private static bool OnALid(IStoryWorld w, P3 n) => LidBlocks.Contains(w.Block(n.X, n.Y - 1, n.Z).Id);

    /// <summary>The six neighbours, under first.</summary>
    private static IEnumerable<P3> Neighbours(P3 p)
    {
        yield return p.Add(0, -1, 0);
        yield return p.Add(1, 0, 0);
        yield return p.Add(-1, 0, 0);
        yield return p.Add(0, 0, 1);
        yield return p.Add(0, 0, -1);
        yield return p.Add(0, 1, 0);
    }

    // ------------------------------------------------------------------ failures said once

    private static readonly Regex Coords = new(@"-?\d+,-?\d+,-?\d+", RegexOptions.Compiled);

    /// <summary>
    /// A clear the pawn's dig refused for good (a protected block, a chest, a never-break mark: never just "occupied" again): the cell
    /// is blocked at once with that reason, said once per build and reason. A clear that is only "not now" (out of reach, busy, the
    /// pawn stood on it) or waits for a tool is not one.
    /// </summary>
    private static bool ClearRefused(string why) =>
        why.StartsWith("can't clear the cell", StringComparison.Ordinal) && !Transient(why)
        && !why.Contains("I'm standing on", StringComparison.Ordinal) && !why.Contains("would lose the drop", StringComparison.Ordinal);

    /// <summary>The build's reason and its alert, once per build and kind of failure (live 11-05: 116 "occupied by stone" warnings for
    /// one build in an hour).</summary>
    private void SayCellBlocked(PawnState p, BuildState b, GhostState g, string why)
    {
        b.Reason = $"{CantPlaceAt}{g.At}: {why}";
        b.Rev = Touch();
        _cellSaid[b.Id] = g.At;
        // The chest rule is one alert per build, whichever chests it names (the queue said it too: StoryColony.Builds.cs ChestRuleCells).
        var kind = StoryConstruction.IsChestRule(why) ? StoryConstruction.ChestRule : Coords.Replace(why, "…");
        if (_cellAlerted.Add($"{b.Id}|{kind}")) Alert("warn", $"Build {b.Id}: {b.Reason}", p.Name);
        else _log.LogInformation("story {Colony}: build {Build} cell {At} blocked: {Why}", Id, b.Id, g.At, why);
    }

    /// <summary>The start of a build's reason that names one of its blocked cells (<see cref="SayCellBlocked"/>).</summary>
    private const string CantPlaceAt = "can't place at ";

    /// <summary>Per build: the cell its last "can't place at" named (<see cref="SayCellBlocked"/>), while that cell stays blocked.</summary>
    private readonly Dictionary<string, P3> _cellSaid = new(StringComparer.Ordinal);

    /// <summary>The build's reason as it turns active again: "can't place at x,y,z: …" while the cell it last named is still blocked, else
    /// null. Turning active (its other cells going in) dropped that reason, and a material wait before it replaced it, while the cell
    /// stayed blocked: the queue row stopped saying why (C9).</summary>
    private string? CellReasonKept(BuildState b)
    {
        if (!_cellSaid.TryGetValue(b.Id, out var at)) return null;
        foreach (var g in b.Ghosts)
            if (g.Job && g.At == at && g.State == "blocked" && g.Reason is { } why) return $"{CantPlaceAt}{at}: {why}";
        _cellSaid.Remove(b.Id);
        return null;
    }

    /// <summary>The cell the build's reason names is no longer blocked (built, or open again): the reason goes with it.</summary>
    private void CellReasonGone(BuildState b, GhostState g)
    {
        if (!_cellSaid.TryGetValue(b.Id, out var at) || at != g.At) return;
        _cellSaid.Remove(b.Id);
        if (b.Reason?.StartsWith(CantPlaceAt, StringComparison.Ordinal) != true) return;
        b.Reason = null;
        b.Rev = Touch();
    }

    // ------------------------------------------------------------------ blocked cells are tried again, a few per reason
    //
    // A blocked cell used to stay blocked until the owner pressed Retry, whatever became of its cause (Fox: "Even now, there's some
    // blocked blocks on the storehouse build"): the wall behind a torch stood by then, the fix for its failure was deployed, the
    // pawn in the way had walked off. Now each pass looks at a build's blocked cells by reason. A reason that is due (never tried
    // in this process, or its wait is over) gets up to ProbeCells cells reopened with one failure left: a cell that fails again is
    // blocked at once and its reason waits longer (10 minutes, 30, then 60 each time); one that goes in reopens every cell blocked
    // with that reason. So a cause that is gone costs no more than a wait, and one that still holds costs a few trips per wait
    // instead of three per cell. The reason's alert was said when the cells first blocked and is not repeated. A reopened cell is
    // handed to a pawn ProbeCells times at most without an outcome; a miss that is not the cell's (its pawn's reach, "not now")
    // is no outcome but never blocks it again either: those have their own bounds (StoryColony.Builds.cs MissKind).

    /// <summary>Cells of one reason reopened per try.</summary>
    public const int ProbeCells = 4;

    /// <summary>How long a reason waits after its n-th try (the first wait starts when its cells block): 10 minutes, 30, then 60.</summary>
    internal static TimeSpan ProbeWait(int round) => TimeSpan.FromMinutes(round switch { 0 => 10, 1 => 30, _ => 60 });

    private const string CannotClearWords = "cannot clear ";

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Reason → the same with its coordinates and numbers left out (the cells of one build that failed alike share it). Kept so
    /// a pass costs one lookup per blocked cell; dropped whole when it grows large.</summary>
    private readonly Dictionary<string, string> _reasonKinds = new(StringComparer.Ordinal);

    private string ReasonKind(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return ""; // blocked by an older runtime, which kept no reason
        if (_reasonKinds.TryGetValue(reason, out var kind)) return kind;
        if (_reasonKinds.Count >= 4096) _reasonKinds.Clear();
        return _reasonKinds[reason] = Digits.Replace(Coords.Replace(reason, "…"), "#");
    }

    /// <summary>
    /// Blocked cells no try can mend as things stand, so none is spent on them: a newer build's or a quarry's (they come back when
    /// that leaves), empty space holding what can't be dug (it reopens when that is gone), a clear refused for good (said once, not
    /// tried again: live 11-05; it reopens when the block in the way is gone), a pour (Retry only: live 11-04, 371 cells), and a cell
    /// holding its own block turned wrong (the rotation rules decide those: the pass marks them as it reads the cell).
    /// </summary>
    private static bool NeverSampled(GhostState g) =>
        Conflicted(g) || StoryConstruction.IsLiquid(g) || g.Holds == "misrotated"
        || g.Reason is { } why && (why.StartsWith(CannotClearWords, StringComparison.Ordinal) || ClearRefused(why)
            || StoryConstruction.IsChestRule(why)); // the chest rule: the pass reopens it once the chest beside it is gone (ChestRuleNow)

    /// <summary>
    /// A pass begins: the cells finished since the last one began are this pass's. A try that left the open states by another road
    /// (left misrotated, blocked by a rule) is no longer out. One handed to a pawn <see cref="ProbeCells"/> times and still open had
    /// its tries (the batch ended before the pawn got to it): it is blocked again, with its reason, until that reason's next try. So a
    /// try costs a few trips at most, whatever kept it from an outcome. A miss that is not the cell's gives its handing back
    /// (<see cref="TryNotCounted"/>).
    /// </summary>
    private void BeginSampling()
    {
        (_doneBefore, _doneSince) = (_doneSince, _doneBefore);
        _doneSince.Clear();
        _reopenedNow = false;
        foreach (var (g, t) in _sampled)
        {
            if (g.State == "leased" || g.State == "todo" && t.Handed < ProbeCells) continue;
            // Blocked again with the reason it was blocked with, so that reason's wait holds it (a miss's text has no wait of its own).
            if (g.State == "todo") { g.State = "blocked"; g.Reason = t.Said ?? g.Reason; g.Rev = Touch(); }
            _sampled.Remove(g);
        }
    }

    /// <summary>A job with this cell goes out to a pawn: counted when the cell is a try (see <see cref="BeginSampling"/>).</summary>
    private void TryHanded(GhostState g)
    {
        if (_sampled.Count > 0 && _sampled.TryGetValue(g, out var t)) _sampled[g] = t with { Handed = t.Handed + 1 };
    }

    /// <summary>The try's pawn missed it for a reason that is not the cell's (StoryColony.Builds.cs <c>MissKind</c>: its reach, its
    /// supply, its own trouble, "not now"): that handing was no try, so such misses never block a try again. Each kind has its own
    /// bound instead (every builder's miss, the fifth "not now" in a row).</summary>
    private void TryNotCounted(GhostState g)
    {
        if (_sampled.Count > 0 && _sampled.TryGetValue(g, out var t) && t.Handed > 0) _sampled[g] = t with { Handed = t.Handed - 1 };
    }

    /// <summary>
    /// One build's blocked cells (<see cref="_blockedNow"/>), by reason. A reason that is due and has no try out gets its lowest cells
    /// reopened (y, then x, z) and its next wait set. A cell beside one finished since the last pass (its support or wall just
    /// arrived) is tried now whatever its reason's wait, without moving that wait.
    /// </summary>
    private void SampleBlocked(BuildState b)
    {
        Dictionary<string, List<GhostState>>? due = null;
        foreach (var g in _blockedNow)
        {
            if (g.State != "blocked") continue; // reopened meanwhile: a try of its reason went in during this pass
            var kind = ReasonKind(g.Reason);
            // A try still out holds only the timed ones: a cell beside one just finished goes whatever its reason's other tries do
            // (before, a try no pawn could be handed held it for good).
            if (!Timed((b.Id, kind)) && !BesideDone(g.At)) continue;
            due ??= new(StringComparer.Ordinal);
            if (!due.TryGetValue(kind, out var cells)) due[kind] = cells = new();
            cells.Add(g);
        }
        if (due == null) return;
        foreach (var (kind, cells) in due)
        {
            var cls = (b.Id, kind);
            if (Timed(cls))
            {
                int round = _probe.TryGetValue(cls, out var probe) ? probe.Round : 0;
                _probe[cls] = new Probe(Now + ProbeWait(round), round + 1);
            }
            cells.Sort(SampleOrder);
            int n = Math.Min(cells.Count, ProbeCells);
            for (int i = 0; i < n; i++) Sample(b, cells[i], cls);
            _log.LogInformation("story {Colony}: build {Build}: {N} of {Of} blocked cell(s) tried again: {Reason}", Id, b.Id, n, cells.Count, kind);
        }
        _reopenedNow = true;
    }

    /// <summary>The reason was never tried in this process (its cells were blocked before it started), or its wait is over.</summary>
    private bool TimeDue((string Build, string Reason) cls) => !_probe.TryGetValue(cls, out var probe) || Now >= probe.NextAt;

    /// <summary>The reason's timed tries are due: its wait is over and none of its tries is still out.</summary>
    private bool Timed((string Build, string Reason) cls) => TimeDue(cls) && !TryOut(cls);

    /// <summary>A try of this reason is still out (reopened, neither finished nor blocked again).</summary>
    private bool TryOut((string Build, string Reason) cls)
    {
        foreach (var t in _sampled.Values)
            if (t.Of(cls)) return true;
        return false;
    }

    /// <summary>A cell finished since the last pass lies right beside this one.</summary>
    private bool BesideDone(P3 at) =>
        _doneBefore.Count > 0
        && (_doneBefore.Contains(at.Add(0, -1, 0)) || _doneBefore.Contains(at.Add(1, 0, 0)) || _doneBefore.Contains(at.Add(-1, 0, 0))
            || _doneBefore.Contains(at.Add(0, 0, 1)) || _doneBefore.Contains(at.Add(0, 0, -1)) || _doneBefore.Contains(at.Add(0, 1, 0)));

    /// <summary>The cells beside one just finished first, then the lowest (y, then x, z).</summary>
    private int SampleOrder(GhostState a, GhostState b)
    {
        int c = BesideDone(b.At).CompareTo(BesideDone(a.At));
        if (c == 0) c = a.At.Y.CompareTo(b.At.Y);
        if (c == 0) c = a.At.X.CompareTo(b.At.X);
        return c != 0 ? c : a.At.Z.CompareTo(b.At.Z);
    }

    /// <summary>One blocked cell is open again for a try: its rests are over and one more counted failure blocks it again at once. Its
    /// reason stays (the map still says why it was blocked) and nothing is alerted.</summary>
    private void Sample(BuildState b, GhostState g, (string Build, string Reason) cls)
    {
        var id = $"{b.Id}|{g.At}";
        var key = "g:" + id;
        g.State = "todo";
        g.Rev = Touch();
        _ghostFails[id] = 2;
        _softFails.Remove(key);
        _leaseFails.Remove(key);
        _skip.Remove(key);
        ForgetPawnFails(key);
        _sampled[g] = new Try(cls.Build, cls.Reason, 0, g.Reason);
    }

    /// <summary>A cell is finished (built, dug, found so): the blocked cells beside it are looked at on the next pass, and when it was
    /// a try, what blocked its reason is gone, so every cell blocked with that reason starts over.</summary>
    private void CellFinished(BuildState b, GhostState g)
    {
        _doneSince.Add(g.At);
        if (_sampled.Count == 0 || !_sampled.Remove(g, out var tried)) return;
        var cls = (tried.Build, tried.Reason);
        _probe.Remove(cls);
        int reopened = 0;
        foreach (var x in b.Ghosts)
        {
            if (!x.Job || x.State != "blocked" || NeverSampled(x) || ReasonKind(x.Reason) != cls.Reason) continue;
            Reopen(b, x);
            reopened++;
        }
        // The reason's other tries still out start over too (no failure on record, no reason on the map).
        foreach (var (other, t) in _sampled)
        {
            if (!t.Of(cls)) continue;
            ForgetCellFailures(b, other);
            other.Rev = Touch();
        }
        _log.LogInformation("story {Colony}: build {Build}: {At} is done on another try: {N} cell(s) blocked alike start over ({Reason})",
            Id, b.Id, g.At, reopened, cls.Reason);
        MaterialsChanged();
        WakeIdle();
    }

    /// <summary>A cell was just blocked by its failures. A try that failed again goes back to waiting (its reason's next try is set);
    /// a cell blocked afresh starts its reason's first wait, unless that reason is waiting already.</summary>
    private void CellBlockedNow(BuildState b, GhostState g)
    {
        bool tried = _sampled.Remove(g, out var was);
        if (NeverSampled(g)) return;
        var cls = (b.Id, ReasonKind(g.Reason));
        if (!TimeDue(cls)) return;
        // A try that was out longer than its reason's wait (no builder got to it) starts that wait again from its failure.
        _probe[cls] = tried && was.Of(cls) && _probe.TryGetValue(cls, out var probe)
            ? probe with { NextAt = Now + ProbeWait(probe.Round - 1) }
            : new Probe(Now + ProbeWait(0), 1);
    }

    /// <summary>A build's reasons start over (it was retried as a whole) or go with it (it was removed).</summary>
    private void ForgetProbes(BuildState b)
    {
        if (_probe.Count > 0)
            foreach (var cls in _probe.Keys)
                if (cls.Build == b.Id) _probe.Remove(cls);
        if (_sampled.Count > 0)
            foreach (var (g, t) in _sampled)
                if (t.Build == b.Id) _sampled.Remove(g);
    }
}
