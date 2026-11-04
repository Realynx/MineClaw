using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>One builder body as the slicer sees it: who, where it stands now, whether it carries a water bucket, its reach.</summary>
public sealed record CrewMember(string Name, BlockPos Start, bool Water = false, double Reach = 4.5);

/// <summary>
/// The build slicer for a crew of 1..N bodies (zero tokens). The design is sliced once, in dependency order, exactly as for one
/// body (<see cref="BuildPlanner"/>, from the first member's feet: N=1 is today's plan, unchanged); the steps still to do are then
/// cut into one <see cref="PlanLane"/> per body:
/// <list type="bullet">
/// <item>regions: the footprint's (x, z) columns are swept along its longer axis and cut into contiguous runs balanced by step
///   count (a cut moves to the nearest column boundary when that stays within a few percent), so each body works its own
///   strip of the footprint, layer by layer, away from the others' stands and reach;</item>
/// <item>bodies: the runs go to the members that finish them soonest (travel from where each stands + the lane's own walking and
///   placing), the slowest lane minimised;</item>
/// <item>water: every water-column step (pour, descents, the take-back) is one lane's, the member with the bucket, so a column is
///   only ever ridden by one body;</item>
/// <item>climbing: a crew climbs by scaffold staircases only (a pillar top holds the one body that pillared there); the stair
///   is one lane's (the strip it starts in), and every other lane's step that stands above the build's base waits for the
///   stair's treads below its stand, so one lane builds it and the others walk it;</item>
/// <item>finale: scaffold removal is nobody's lane: whoever finishes last takes it down (it may hold up anyone's blocks);</item>
/// <item>waits: a lane waits only for the specific steps of other lanes it needs: the support it clicks, a cell another lane
///   clears or places first, the floor its stand rests on, and a stand another lane uses before this lane fills that cell.</item>
/// </list>
/// The plan carries the one-body and the crew estimate (travel + placements, lanes in parallel with their waits), so the brain
/// and the player see the benefit before anything is built.
/// </summary>
public static class CrewSlicer
{
    /// <summary>Walking: 4.3 blocks/s ≈ 4.6 ticks per block.</summary>
    public const double TicksPerBlock = 4.6;

    /// <summary>
    /// Slices <paramref name="plan"/> for <paramref name="crew"/> (1..N bodies; the first one's feet seed the dependency order and,
    /// for N=1, everything is exactly <see cref="BuildPlanner.Plan"/>). With <paramref name="previous"/> (resume, re-balance,
    /// crew change) its finished steps are kept as done history and only what remains is sliced and split again.
    /// </summary>
    public static BuildPlan Slice(VoxelPlan plan, IBlockAccess world, IReadOnlyList<CrewMember> crew, BuildPart? part = null,
        BuildPlannerOptions? options = null, BuildPlan? previous = null)
    {
        if (crew.Count == 0) throw new ArgumentException("a crew needs at least one body", nameof(crew));
        var o = options;
        if (crew.Count > 1 && Strips(plan, world, crew, part ?? BuildPart.Whole(plan), options ?? new BuildPlannerOptions(), previous) is { } strips) return strips;
        if (crew.Count > 1)
        {
            o ??= new BuildPlannerOptions();
            // Any body may end up on any lane after a re-balance: plan for the shortest reach; water when anyone has a bucket;
            // climbing only by stairs every lane can walk.
            o = o with { Reach = Math.Min(o.Reach, crew.Min(m => m.Reach)), WaterAvailable = o.WaterAvailable || crew.Any(m => m.Water), SharedClimb = true };
        }
        var bp = BuildPlanner.Plan(plan, world, crew[0].Start, part, o, previous);
        Assign(bp, plan, crew);
        return bp;
    }

    // ------------------------------------------------------------------ strips planned each on its own

    /// <summary>A strip at least this wide (cells along the long axis): narrower strips give one body too little room to work in.</summary>
    public const int MinStripWidth = 2;

    /// <summary>
    /// A big design (<see cref="BuildPlannerOptions.StripsAbove"/> blocks or more) for a crew: the footprint's columns cut into
    /// contiguous strips along the long axis, balanced by the blocks still to place, one per body, and each strip planned on its
    /// own (in parallel), from its body's own feet, standing only inside its strip (or beyond the footprint at its ends) and never
    /// walking through another strip's design cells. The lanes then share nothing (no stand, no cell, no support), so nobody waits
    /// on anybody and a 63-wide pyramid is built by every body at once. One plan cut into lanes (the slicer below) was planned from
    /// one body's feet: its stands wandered into the neighbours' strips, the lanes waited on each other, timed out and fell back to
    /// one builder (b=15, 4 bodies: 3 re-balances, 55% of the blocks placed by the finale). Null when it doesn't apply or a strip
    /// can't stand on its own (a cell it can't place, a water descent, a gate still to show): then the one-plan slicer decides.
    /// </summary>
    public static BuildPlan? Strips(VoxelPlan plan, IBlockAccess world, IReadOnlyList<CrewMember> crew, BuildPart part, BuildPlannerOptions o,
        BuildPlan? previous)
    {
        if (!StripsMayApply(o, previous)) return null;
        var todo = ToPlace(plan, world, part);
        if (plan.Blocks.Count < o.StripsAbove || todo.Count == 0) return null;
        var axis = new LongAxis(plan);
        if (StripBounds(todo, axis, crew.Count) is not { } bounds) return null;
        int n = bounds.Count;
        var parts = bounds.Select((r, i) => axis.Strip(part, r, i, n)).ToList();
        var blocks = parts.Select(p => todo.Count(b => p.Cells.Contains(b.Pos))).ToArray();
        // Bodies: whoever reaches a strip and finishes it soonest (the slowest strip minimised).
        var pick = Choose(StripCosts(plan, crew, bounds, blocks, axis), crew.Count, n);
        var plans = PlanStrips(plan, world, crew, part, o, previous, parts, pick);
        if (plans.Any(p => p.Infeasible.Count > 0 || p.Columns.Count > 0)) return null;
        return MergeStrips(plans, plan, crew, part, previous, pick);
    }

    /// <summary>Strips only when asked for, with no gate still to show and no water column still standing.</summary>
    private static bool StripsMayApply(BuildPlannerOptions o, BuildPlan? previous)
    {
        if (o.StripsAbove <= 0 || o.GateBlocks > 0 && previous?.Gate.State is null or GateState.Pending) return false;
        return previous?.Columns.Any(c => c.Poured && !c.Removed) != true;
    }

    /// <summary>The part's design blocks that don't stand as planned yet.</summary>
    private static List<PlannedBlock> ToPlace(VoxelPlan plan, IBlockAccess world, BuildPart part) =>
        plan.Blocks.Values.Where(b => part.Cells.Contains(b.Pos) && !b.UpperDoor
            && !(world.IsLoaded(b.Pos.X, b.Pos.Z) && DesignChecks.SameState(b, world.GetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z), world.GetMeta(b.Pos.X, b.Pos.Y, b.Pos.Z)))).ToList();

    /// <summary>The footprint's longer axis (x on a tie): where a cell lies along it, and across it.</summary>
    private readonly record struct LongAxis
    {
        public LongAxis(VoxelPlan plan)
        {
            AlongX = plan.Max.X - plan.Min.X >= plan.Max.Z - plan.Min.Z;
            (Lo, Hi) = AlongX ? (plan.Min.X, plan.Max.X) : (plan.Min.Z, plan.Max.Z);
        }

        public bool AlongX { get; }
        public int Lo { get; }
        public int Hi { get; }
        public int Of(BlockPos p) => AlongX ? p.X : p.Z;
        public int Main(BlockPos p) => Math.Clamp(Of(p), Lo, Hi);
        public int Cross(BlockPos p) => AlongX ? p.Z : p.X;

        /// <summary>Strip <paramref name="i"/> of <paramref name="n"/>: its cells; the end strips reach on past the footprint.</summary>
        public BuildPart Strip(BuildPart part, (int A, int B) r, int i, int n)
        {
            var self = this;
            int axisLo = i == 0 ? int.MinValue : r.A, axisHi = i == n - 1 ? int.MaxValue : r.B;
            var cells = part.Cells.Where(p => self.Of(p) >= r.A && self.Of(p) <= r.B).ToHashSet();
            return new BuildPart(i, n, cells, part.Claim, axisLo, axisHi, AlongX ? 'x' : 'z');
        }
    }

    /// <summary>Contiguous runs of columns balanced by what is left to place; the most strips (one per body at most) whose balanced
    /// cut leaves every one at least <see cref="MinStripWidth"/> wide (a pyramid's middle columns hold the most blocks, so its
    /// middle strips are the narrow ones). Null = fewer than two.</summary>
    private static List<(int A, int B)>? StripBounds(List<PlannedBlock> todo, LongAxis axis, int bodies)
    {
        var per = new int[axis.Hi - axis.Lo + 1];
        foreach (var b in todo) per[axis.Main(b.Pos) - axis.Lo]++;
        for (int n = Math.Min(bodies, (axis.Hi - axis.Lo + 1) / MinStripWidth); n >= 2; n--)
        {
            var cut = Balanced(per, axis.Lo, n);
            if (cut.Count == n && cut.All(r => r.B - r.A + 1 >= MinStripWidth)) return cut;
        }
        return null;
    }

    /// <summary>Member × strip: the walk to the strip's nearest column plus its blocks.</summary>
    private static double[,] StripCosts(VoxelPlan plan, IReadOnlyList<CrewMember> crew, List<(int A, int B)> bounds, int[] blocks, LongAxis axis)
    {
        var cost = new double[crew.Count, bounds.Count];
        for (int m = 0; m < crew.Count; m++)
            for (int g = 0; g < bounds.Count; g++)
            {
                var s = crew[m].Start;
                int a = Math.Clamp(axis.Of(s), bounds[g].A, bounds[g].B);
                var near = axis.AlongX ? new BlockPos(a, s.Y, Math.Clamp(s.Z, plan.Min.Z, plan.Max.Z)) : new BlockPos(Math.Clamp(s.X, plan.Min.X, plan.Max.X), s.Y, a);
                cost[m, g] = Walk(s, near) + blocks[g] * 8.0;
            }
        return cost;
    }

    /// <summary>Every strip planned on its own world (the real one + its own steps), so none counts on another's blocks either way:
    /// from its body's feet, standing only inside its strip, never walking through another strip's design cells.</summary>
    private static BuildPlan[] PlanStrips(VoxelPlan plan, IBlockAccess world, IReadOnlyList<CrewMember> crew, BuildPart part, BuildPlannerOptions o,
        BuildPlan? previous, List<BuildPart> parts, int[] pick)
    {
        var designCells = plan.Blocks.Keys.Where(part.Cells.Contains).ToList();
        var plans = new BuildPlan[parts.Count];
        var tasks = Enumerable.Range(0, parts.Count).Select(g => Task.Run(() =>
        {
            var mine = parts[g];
            var avoid = designCells.Where(p => !mine.Cells.Contains(p)).ToHashSet();
            var lane = o with
            {
                Avoid = avoid, StandOk = mine.Allows, SharedClimb = false, WaterAvailable = false, GateBlocks = 0,
                Reach = Math.Min(o.Reach, crew[pick[g]].Reach),
            };
            plans[g] = BuildPlanner.Plan(plan, world, crew[pick[g]].Start, mine, lane, ForStrip(previous, mine, g == 0));
        })).ToArray();
        Task.WaitAll(tasks);
        return plans;
    }

    /// <summary>One plan: the strips' steps one after another (the first keeps the history), each strip a lane.</summary>
    private static BuildPlan MergeStrips(BuildPlan[] plans, VoxelPlan plan, IReadOnlyList<CrewMember> crew, BuildPart part, BuildPlan? previous, int[] pick)
    {
        var bp = StripsPlan(plans[0], plan, crew, part, previous);
        var lanes = new List<PlanLane>();
        for (int g = 0; g < plans.Length; g++) lanes.Add(AppendStrip(bp, plans[g], g, crew[pick[g]]));
        bp.Lanes = lanes;
        bp.CrewTicks = lanes.Max(l => l.EstTicks);
        // One body alone: every strip in turn, walking from the end of one to the start of the next.
        bp.SoloTicks = LaneTicks(lanes.SelectMany(l => l.Steps).Select(i => bp.Steps[i]), crew[0].Start);
        return bp;
    }

    /// <summary>The merged plan's head: the first strip's format, the last plan's ledger, gate, corrections and exit.</summary>
    private static BuildPlan StripsPlan(BuildPlan first, VoxelPlan plan, IReadOnlyList<CrewMember> crew, BuildPart part, BuildPlan? previous) => new()
    {
        Part = part.Index, Parts = part.Count, Version = first.Version, Format = first.Format, Origin = first.Origin, Facing = first.Facing,
        Gate = previous?.Gate ?? first.Gate, Home = BuildPlanner.HomeFor(plan, crew[0].Start, previous), BlueprintId = previous?.BlueprintId ?? 0,
        Placed = previous?.Placed.Distinct().ToList() ?? new(), Transform = previous?.Transform ?? BuildTransform.None,
        Replans = previous != null ? previous.Replans + 1 : 0, Exit = previous?.Exit.ToList() ?? new(),
        SliceId = Guid.NewGuid().ToString("N")[..12],
    };

    /// <summary>A strip's steps appended to the merged plan (indices and dependencies shifted), its unfinished ones as lane
    /// <paramref name="g"/>, its standing scaffold added.</summary>
    private static PlanLane AppendStrip(BuildPlan bp, BuildPlan strip, int g, CrewMember body)
    {
        int offset = bp.Steps.Count;
        var lane = new PlanLane { Index = g, Body = body.Name };
        foreach (var st in strip.Steps)
        {
            st.Index += offset;
            for (int k = 0; k < st.Deps.Count; k++) st.Deps[k] += offset;
            st.Lane = st.Finished ? 0 : g;
            if (!st.Finished) lane.Steps.Add(st.Index);
            bp.Steps.Add(st);
        }
        foreach (var c in strip.ScaffoldUp) if (!bp.ScaffoldUp.Contains(c)) bp.ScaffoldUp.Add(c);
        lane.EstTicks = LaneTicks(lane.Steps.Select(i => bp.Steps[i]), body.Start);
        return lane;
    }

    /// <summary>Cuts columns <paramref name="lo"/>.. (counts <paramref name="per"/>) into <paramref name="n"/> contiguous runs of about
    /// equal count (a run never empty).</summary>
    internal static List<(int A, int B)> Balanced(int[] per, int lo, int n)
    {
        var bounds = new List<(int A, int B)>();
        int hi = lo + per.Length - 1, start = lo;
        long total = per.Sum(), acc = 0, done = 0;
        for (int c = lo; c <= hi && bounds.Count < n - 1; c++)
        {
            acc += per[c - lo];
            int left = n - bounds.Count - 1;
            double target = (double)(total - done) / (n - bounds.Count);
            // Cut where the run is closest to its share (before or after this column), keeping a column for every run left.
            bool over = acc >= target, room = hi - c >= left;
            if (!room) break;
            if (over || hi - c == left)
            {
                bool back = over && c > start && CloserBefore(acc, per[c - lo], target);
                int end = back ? c - 1 : c;
                bounds.Add((start, end));
                long run = back ? acc - per[c - lo] : acc; // the run cut off; this column carries over when it ends before it
                done += run;
                acc -= run;
                start = end + 1;
            }
        }
        bounds.Add((start, hi));
        return bounds;
    }

    /// <summary>The run ending before this column (its <paramref name="col"/> blocks) is non-empty and closer to the share.</summary>
    private static bool CloserBefore(long acc, int col, double target) => acc - col > 0 && target - (acc - col) < acc - target;

    /// <summary>What a strip's own plan carries over from the last one: the ledger, gate and corrections for all; the finished
    /// steps (history) once; the scaffold still standing in that strip.</summary>
    private static BuildPlan? ForStrip(BuildPlan? prev, BuildPart strip, bool history)
    {
        if (prev == null) return null;
        return new BuildPlan
        {
            BlueprintId = prev.BlueprintId, Part = prev.Part, Parts = prev.Parts, Version = prev.Version, Format = prev.Format, Origin = prev.Origin,
            Facing = prev.Facing, Dim = prev.Dim, Transform = prev.Transform, Gate = prev.Gate, Placed = prev.Placed, Replans = prev.Replans, Home = prev.Home,
            Steps = history ? prev.Steps.Where(s => s.Finished).ToList() : new(),
            ScaffoldUp = prev.ScaffoldUp.Where(strip.Allows).ToList(), Exit = prev.Exit.Where(strip.Allows).ToList(),
        };
    }

    /// <summary>Cuts the plan's unfinished steps into lanes for <paramref name="crew"/> (see the class remarks) and estimates both ways.</summary>
    public static void Assign(BuildPlan bp, VoxelPlan plan, IReadOnlyList<CrewMember> crew)
    {
        bp.SliceId = Guid.NewGuid().ToString("N")[..12];
        bp.Lanes = new List<PlanLane>();
        foreach (var s in bp.Steps) s.Lane = 0;
        var pending = bp.Steps.Where(s => !s.Finished).ToList();
        var first = crew.Count > 0 ? crew[0] : new CrewMember("", default);
        bp.SoloTicks = Simulate(bp, [first.Start], pending.ToDictionary(s => s.Index, _ => 0), null, out _);
        if (crew.Count <= 1)
        {
            bp.Lanes.Add(new PlanLane { Index = 0, Body = first.Name, Steps = pending.Select(s => s.Index).ToList(), EstTicks = bp.SoloTicks });
            bp.CrewTicks = bp.SoloTicks;
            return;
        }

        var finale = pending.Where(s => s.Action == StepAction.RemoveScaffold && !IsWaterStep(s)).ToList();
        var water = pending.Where(IsWaterStep).ToList();
        var stair = pending.Where(s => !IsWaterStep(s) && IsStairStep(s)).ToList();
        var regular = pending.Where(s => !IsWaterStep(s) && s.Action != StepAction.RemoveScaffold && !IsStairStep(s)).ToList();

        var axis = new LongAxis(plan);
        var segments = Segments(regular, axis, crew.Count);
        // Bodies: the assignment whose slowest lane finishes soonest (travel from where each body stands + its lane).
        var pick = Choose(SegmentCosts(segments, crew), crew.Count, segments.Count);
        var lanes = new List<PlanLane>();
        var laneOf = new Dictionary<int, int>();
        for (int g = 0; g < segments.Count; g++)
        {
            lanes.Add(new PlanLane { Index = g, Body = crew[pick[g]].Name });
            AddToLane(lanes, laneOf, g, segments[g]);
        }
        // One body rides the columns (one bucket, one column at a time); the shared stair is one lane's.
        if (water.Count > 0) AddToLane(lanes, laneOf, WaterLane(water, segments, crew, pick), water);
        if (stair.Count > 0) AddToLane(lanes, laneOf, StairLane(stair, segments, axis), stair);
        Seal(bp, lanes, laneOf, finale);

        Waits(bp, lanes, laneOf, plan.Min.Y);
        bp.Lanes = lanes;
        var starts = lanes.Select(l => crew.First(m => m.Name == l.Body).Start).ToList();
        bp.CrewTicks = Simulate(bp, starts, laneOf, lanes, out var perLane);
        for (int g = 0; g < lanes.Count; g++) lanes[g].EstTicks = perLane[g];
    }

    /// <summary>Each lane in plan order; every step marked with its lane (scaffold removal: nobody's, the finale's).</summary>
    private static void Seal(BuildPlan bp, List<PlanLane> lanes, Dictionary<int, int> laneOf, List<PlanStep> finale)
    {
        foreach (var l in lanes) l.Steps.Sort();
        foreach (var s in finale) s.Lane = -1;
        foreach (var (i, g) in laneOf) bp.Steps[i].Lane = g;
    }

    private static bool IsWaterStep(PlanStep s) => s.Column >= 0 || s.Action is StepAction.PourWater or StepAction.PickupWater;

    private static bool IsStairStep(PlanStep s) => s.Action == StepAction.Scaffold && s.Technique == Technique.Stair;

    private static void AddToLane(List<PlanLane> lanes, Dictionary<int, int> laneOf, int g, List<PlanStep> steps)
    {
        foreach (var s in steps) { lanes[g].Steps.Add(s.Index); laneOf[s.Index] = g; }
    }

    /// <summary>Regions: the footprint's columns swept along the longer axis, cut into contiguous runs of equal step count.</summary>
    private static List<List<PlanStep>> Segments(List<PlanStep> regular, LongAxis axis, int bodies)
    {
        var sorted = regular.OrderBy(s => axis.Main(s.Target)).ThenBy(s => axis.Cross(s.Target)).ThenBy(s => s.Target.Y).ThenBy(s => s.Index).ToList();
        int n = Math.Clamp(Math.Min(bodies, sorted.Count), 1, bodies);
        var segments = new List<List<PlanStep>>();
        int at = 0;
        for (int k = 0; k < n; k++)
        {
            int end = k == n - 1 ? sorted.Count : CutAt(sorted, at, k, n, axis);
            segments.Add(sorted.GetRange(at, Math.Max(0, end - at)));
            at = end;
        }
        return segments;
    }

    /// <summary>Where run <paramref name="k"/> ends: the ideal cut, moved to a column boundary close to it (a column split between
    /// lanes means one waits on the other), leaving a step for every run after it.</summary>
    private static int CutAt(List<PlanStep> sorted, int at, int k, int n, LongAxis axis)
    {
        int end = (int)Math.Round((k + 1) * (double)sorted.Count / n);
        int tol = (int)Math.Floor(0.06 * sorted.Count / n);
        for (int d = 0; d <= tol; d++)
            foreach (var j in new[] { end - d, end + d })
                if (j > at && j < sorted.Count && ColumnOf(sorted[j - 1], axis) != ColumnOf(sorted[j], axis))
                    return Math.Clamp(j, at + 1, sorted.Count - (n - 1 - k));
        return Math.Clamp(end, at + 1, sorted.Count - (n - 1 - k));
    }

    private static (int, int) ColumnOf(PlanStep s, LongAxis axis) => (axis.Main(s.Target), axis.Cross(s.Target));

    private static double[,] SegmentCosts(List<List<PlanStep>> segments, IReadOnlyList<CrewMember> crew)
    {
        var cost = new double[crew.Count, segments.Count];
        for (int m = 0; m < crew.Count; m++)
            for (int g = 0; g < segments.Count; g++)
                cost[m, g] = LaneTicks(segments[g].OrderBy(s => s.Index), crew[m].Start);
        return cost;
    }

    /// <summary>The water lane: the one whose body carries the bucket, else the lane nearest the columns.</summary>
    private static int WaterLane(List<PlanStep> water, List<List<PlanStep>> segments, IReadOnlyList<CrewMember> crew, int[] pick)
    {
        for (int g = 0; g < segments.Count; g++) if (crew[pick[g]].Water) return g;
        var c = Centroid(water.Select(s => s.Target));
        return Enumerable.Range(0, segments.Count).OrderBy(g => segments[g].Count == 0 ? double.MaxValue
            : Centroid(segments[g].Select(s => s.Target)).DistanceSquaredTo(c)).First();
    }

    /// <summary>The stair's lane: the strip it starts in (nearest by the long axis).</summary>
    private static int StairLane(List<PlanStep> stair, List<List<PlanStep>> segments, LongAxis axis)
    {
        int at0 = axis.Main(stair[0].Target);
        return Enumerable.Range(0, segments.Count).OrderBy(g => segments[g].Count == 0 ? int.MaxValue : segments[g].Min(s => Math.Abs(axis.Main(s.Target) - at0))).First();
    }

    /// <summary>The lanes' cross-lane waits (only to earlier steps, so they never cycle).</summary>
    private static void Waits(BuildPlan bp, List<PlanLane> lanes, Dictionary<int, int> laneOf, int baseY)
    {
        var index = new WaitIndex(bp, laneOf, baseY);
        foreach (var lane in lanes)
            foreach (var i in lane.Steps)
                if (index.For(lane.Index, i) is { Count: > 0 } set) lane.Waits[i] = set.ToList();
    }

    /// <summary>What each step of the crew plan may have to wait for: who targets, places, clears or stands in each cell, the
    /// shared stair's treads and walk space, the steps standing above the base.</summary>
    private sealed class WaitIndex
    {
        private readonly BuildPlan _bp;
        private readonly Dictionary<int, int> _laneOf;
        private readonly int _baseY;
        private readonly List<int> _stairs, _elevated;
        private readonly HashSet<BlockPos> _walkSpace;
        private readonly Dictionary<BlockPos, List<int>> _byTarget = new(), _placedAt = new(), _clearedAt = new(), _standIn = new();

        public WaitIndex(BuildPlan bp, Dictionary<int, int> laneOf, int baseY)
        {
            (_bp, _laneOf, _baseY) = (bp, laneOf, baseY);
            _stairs = laneOf.Keys.Where(i => IsStairStep(bp.Steps[i])).OrderBy(i => i).ToList();
            // The stair's walk space (above each tread: feet, head, the jump): a block there cuts the way up for whoever still walks it.
            _walkSpace = new HashSet<BlockPos>(_stairs.SelectMany(j => new[] { bp.Steps[j].Target.Up(), bp.Steps[j].Target.Up(2), bp.Steps[j].Target.Up(3) }));
            _elevated = laneOf.Keys.Where(i => Elevated(bp.Steps[i])).OrderBy(i => i).ToList();
            foreach (var i in laneOf.Keys) Note(i, bp.Steps[i]);
        }

        private static bool Stands(PlanStep s) => s.Column < 0 && s.Stand != default;
        private bool Elevated(PlanStep s) => Stands(s) && s.Stand.Y > _baseY + 1;

        private static void Add(Dictionary<BlockPos, List<int>> d, BlockPos p, int i)
        {
            if (!d.TryGetValue(p, out var l)) d[p] = l = new();
            l.Add(i);
        }

        private void Note(int i, PlanStep s)
        {
            Add(_byTarget, s.Target, i);
            if (s.IsPlacement) Add(_placedAt, s.Target, i);
            if (s.Action is StepAction.Clear or StepAction.RemoveScaffold) Add(_clearedAt, s.Target, i);
            if (Stands(s)) { Add(_standIn, s.Stand, i); Add(_standIn, s.Stand.Up(), i); }
        }

        /// <summary>The other lanes' earlier steps step <paramref name="i"/> of lane <paramref name="lane"/> waits for.</summary>
        public SortedSet<int> For(int lane, int i)
        {
            var s = _bp.Steps[i];
            var set = new SortedSet<int>();
            void From(IEnumerable<int>? js)
            {
                if (js == null) return;
                foreach (var j in js) if (j < i && _laneOf.TryGetValue(j, out var g) && g != lane) set.Add(j);
            }
            From(s.Deps);
            From(_byTarget.GetValueOrDefault(s.Target));
            if (Stands(s))
            {
                From(_placedAt.GetValueOrDefault(s.Stand.Down()));
                From(_clearedAt.GetValueOrDefault(s.Stand));
                From(_clearedAt.GetValueOrDefault(s.Stand.Up()));
            }
            if (s.IsPlacement) From(_standIn.GetValueOrDefault(s.Target));
            // Up on the shared stair: its treads below this stand first (another lane builds them).
            if (Elevated(s)) From(_stairs.Where(j => j < i && _bp.Steps[j].Target.Y < s.Stand.Y));
            // Filling the stair's walk space: only once the other lanes' earlier steps up there are done.
            if (s.IsPlacement && _walkSpace.Contains(s.Target)) From(_elevated.Where(j => j < i));
            return set;
        }
    }

    /// <summary>Lane → member: the permutation with the smallest slowest lane (ties: the smallest total); greedy past 8 bodies.</summary>
    private static int[] Choose(double[,] cost, int members, int lanes)
    {
        var best = new int[lanes];
        if (members <= 8)
        {
            double bestMax = double.MaxValue, bestSum = double.MaxValue;
            var cur = new int[lanes];
            var used = new bool[members];
            void Rec(int g, double max, double sum)
            {
                if (max > bestMax) return;
                if (g == lanes)
                {
                    if (max < bestMax - 1e-9 || Math.Abs(max - bestMax) <= 1e-9 && sum < bestSum) { bestMax = max; bestSum = sum; Array.Copy(cur, best, lanes); }
                    return;
                }
                for (int m = 0; m < members; m++)
                {
                    if (used[m]) continue;
                    used[m] = true;
                    cur[g] = m;
                    Rec(g + 1, Math.Max(max, cost[m, g]), sum + cost[m, g]);
                    used[m] = false;
                }
            }
            Rec(0, 0, 0);
            return best;
        }
        var free = Enumerable.Range(0, members).ToHashSet();
        foreach (var g in Enumerable.Range(0, lanes).OrderByDescending(g => Enumerable.Range(0, members).Min(m => cost[m, g])))
        {
            var m = free.OrderBy(x => cost[x, g]).First();
            best[g] = m;
            free.Remove(m);
        }
        return best;
    }

    private static BlockPos Centroid(IEnumerable<BlockPos> ps)
    {
        long x = 0, y = 0, z = 0;
        int n = 0;
        foreach (var p in ps) { x += p.X; y += p.Y; z += p.Z; n++; }
        return n == 0 ? default : new BlockPos((int)(x / n), (int)(y / n), (int)(z / n));
    }

    /// <summary>One lane on its own: walking from stand to stand plus each action.</summary>
    public static double LaneTicks(IEnumerable<PlanStep> steps, BlockPos from)
    {
        double t = 0;
        var pos = from;
        foreach (var s in steps)
        {
            if (s.Stand != default) { t += Walk(pos, s.Stand); pos = s.Stand; }
            t += ActionTicks(s);
        }
        return t;
    }

    private static double Walk(BlockPos a, BlockPos b) => a == default ? 0 : Math.Sqrt(a.DistanceSquaredTo(b)) * TicksPerBlock;

    /// <summary>What a step itself takes (the planner's estimates: a placement 8 ticks, unseen faces +6, digging ≈ a stone pickaxe on cobble).</summary>
    public static double ActionTicks(PlanStep s) => s.Action switch
    {
        StepAction.Place or StepAction.Scaffold => 8 + (s.HiddenFace ? 6 : 0) + (s.Column >= 0 ? 12 : 0),
        StepAction.Clear or StepAction.RemoveScaffold => 30 + (s.Column >= 0 ? 60 : 0),
        StepAction.PourWater => 40,
        StepAction.PickupWater => 20,
        _ => 8,
    };

    /// <summary>
    /// The lanes run in parallel: each step starts when its lane got there and every step it waits for is done (waits point
    /// to earlier steps only, so plan order is a valid schedule); the finale goes to the lane that ends last. Returns the
    /// makespan in ticks (<paramref name="perLane"/>: each lane's own end).
    /// </summary>
    private static double Simulate(BuildPlan bp, IReadOnlyList<BlockPos> starts, Dictionary<int, int> laneOf, List<PlanLane>? lanes, out double[] perLane)
    {
        int n = Math.Max(1, starts.Count);
        var time = new double[n];
        var pos = Enumerable.Range(0, n).Select(k => k < starts.Count ? starts[k] : default).ToArray();
        var finish = new Dictionary<int, double>();
        foreach (var i in laneOf.Keys.OrderBy(i => i))
        {
            var s = bp.Steps[i];
            int k = Math.Clamp(laneOf[i], 0, n - 1);
            double ready = time[k] + (s.Stand != default ? Walk(pos[k], s.Stand) : 0);
            if (lanes != null && lanes[k].Waits.TryGetValue(i, out var ws))
                foreach (var w in ws) if (finish.TryGetValue(w, out var f)) ready = Math.Max(ready, f);
            finish[i] = time[k] = ready + ActionTicks(s);
            if (s.Stand != default) pos[k] = s.Stand;
        }
        perLane = time.ToArray();
        int last = Enumerable.Range(0, n).OrderByDescending(k => time[k]).First();
        var rest = bp.Steps.Where(s => !s.Finished && !laneOf.ContainsKey(s.Index)).OrderBy(s => s.Index);
        return time[last] + LaneTicks(rest, pos[last]);
    }
}
