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

        bool IsWater(PlanStep s) => s.Column >= 0 || s.Action is StepAction.PourWater or StepAction.PickupWater;
        var finale = pending.Where(s => s.Action == StepAction.RemoveScaffold && !IsWater(s)).ToList();
        static bool IsStair(PlanStep s) => s.Action == StepAction.Scaffold && s.Technique == Technique.Stair;
        var water = pending.Where(IsWater).ToList();
        var stair = pending.Where(s => !IsWater(s) && IsStair(s)).ToList();
        var regular = pending.Where(s => !IsWater(s) && s.Action != StepAction.RemoveScaffold && !IsStair(s)).ToList();

        // Regions: sweep the footprint's columns along the longer axis, cut into contiguous runs of equal step count.
        bool alongX = plan.Max.X - plan.Min.X >= plan.Max.Z - plan.Min.Z;
        int lo = alongX ? plan.Min.X : plan.Min.Z, hi = alongX ? plan.Max.X : plan.Max.Z;
        int Main(BlockPos p) => Math.Clamp(alongX ? p.X : p.Z, lo, hi);
        int Cross(BlockPos p) => alongX ? p.Z : p.X;
        (int, int) Column(PlanStep s) => (Main(s.Target), Cross(s.Target));
        var sorted = regular.OrderBy(s => Main(s.Target)).ThenBy(s => Cross(s.Target)).ThenBy(s => s.Target.Y).ThenBy(s => s.Index).ToList();
        int n = Math.Clamp(Math.Min(crew.Count, sorted.Count), 1, crew.Count);
        var segments = new List<List<PlanStep>>();
        int at = 0;
        for (int k = 0; k < n; k++)
        {
            int end = k == n - 1 ? sorted.Count : (int)Math.Round((k + 1) * (double)sorted.Count / n);
            if (k < n - 1)
            {
                // Prefer a column boundary close to the ideal cut (a column split between lanes means one waits on the other).
                int tol = (int)Math.Floor(0.06 * sorted.Count / n), best = -1;
                for (int d = 0; d <= tol && best < 0; d++)
                    foreach (var j in new[] { end - d, end + d })
                        if (j > at && j < sorted.Count && Column(sorted[j - 1]) != Column(sorted[j])) { best = j; break; }
                if (best > 0) end = best;
                end = Math.Clamp(end, at + 1, sorted.Count - (n - 1 - k));
            }
            segments.Add(sorted.GetRange(at, Math.Max(0, end - at)));
            at = end;
        }

        // Bodies: the assignment whose slowest lane finishes soonest (travel from where each body stands + its lane).
        var cost = new double[crew.Count, segments.Count];
        for (int m = 0; m < crew.Count; m++)
            for (int g = 0; g < segments.Count; g++)
                cost[m, g] = LaneTicks(segments[g].OrderBy(s => s.Index), crew[m].Start);
        var pick = Choose(cost, crew.Count, segments.Count);

        var lanes = new List<PlanLane>();
        var laneOf = new Dictionary<int, int>();
        for (int g = 0; g < segments.Count; g++)
        {
            var lane = new PlanLane { Index = g, Body = crew[pick[g]].Name, Steps = segments[g].Select(s => s.Index).ToList() };
            lanes.Add(lane);
            foreach (var s in segments[g]) laneOf[s.Index] = g;
        }
        if (water.Count > 0)
        {
            // One body rides the columns (one bucket, one column at a time): the one carrying the bucket, else the lane nearest them.
            int w = -1;
            for (int g = 0; g < lanes.Count && w < 0; g++) if (crew[pick[g]].Water) w = g;
            if (w < 0)
            {
                var c = Centroid(water.Select(s => s.Target));
                w = Enumerable.Range(0, lanes.Count).OrderBy(g => segments[g].Count == 0 ? double.MaxValue
                    : Centroid(segments[g].Select(s => s.Target)).DistanceSquaredTo(c)).First();
            }
            foreach (var s in water) { lanes[w].Steps.Add(s.Index); laneOf[s.Index] = w; }
        }
        if (stair.Count > 0)
        {
            // The shared stair is one lane's: the strip it starts in (nearest by the long axis).
            int at0 = Main(stair[0].Target);
            int sl = Enumerable.Range(0, lanes.Count).OrderBy(g => segments[g].Count == 0 ? int.MaxValue : segments[g].Min(s => Math.Abs(Main(s.Target) - at0))).First();
            foreach (var s in stair) { lanes[sl].Steps.Add(s.Index); laneOf[s.Index] = sl; }
        }
        foreach (var l in lanes) l.Steps.Sort();
        foreach (var s in finale) s.Lane = -1;
        foreach (var (i, g) in laneOf) bp.Steps[i].Lane = g;

        Waits(bp, lanes, laneOf, plan.Min.Y);
        bp.Lanes = lanes;
        var starts = lanes.Select(l => crew.First(m => m.Name == l.Body).Start).ToList();
        bp.CrewTicks = Simulate(bp, starts, laneOf, lanes, out var perLane);
        for (int g = 0; g < lanes.Count; g++) lanes[g].EstTicks = perLane[g];
    }

    /// <summary>The lanes' cross-lane waits (only to earlier steps, so they never cycle).</summary>
    private static void Waits(BuildPlan bp, List<PlanLane> lanes, Dictionary<int, int> laneOf, int baseY)
    {
        var stairs = laneOf.Keys.Where(i => bp.Steps[i].Action == StepAction.Scaffold && bp.Steps[i].Technique == Technique.Stair).OrderBy(i => i).ToList();
        // The stair's walk space (above each tread: feet, head, the jump): a block there cuts the way up for whoever still walks it.
        var walkSpace = new HashSet<BlockPos>(stairs.SelectMany(j => new[] { bp.Steps[j].Target.Up(), bp.Steps[j].Target.Up(2), bp.Steps[j].Target.Up(3) }));
        var elevated = laneOf.Keys.Where(i => bp.Steps[i].Column < 0 && bp.Steps[i].Stand != default && bp.Steps[i].Stand.Y > baseY + 1).OrderBy(i => i).ToList();
        var byTarget = new Dictionary<BlockPos, List<int>>();
        var placedAt = new Dictionary<BlockPos, List<int>>();
        var clearedAt = new Dictionary<BlockPos, List<int>>();
        var standIn = new Dictionary<BlockPos, List<int>>();
        static void Add(Dictionary<BlockPos, List<int>> d, BlockPos p, int i) { if (!d.TryGetValue(p, out var l)) d[p] = l = new(); l.Add(i); }
        foreach (var i in laneOf.Keys)
        {
            var s = bp.Steps[i];
            Add(byTarget, s.Target, i);
            if (s.IsPlacement) Add(placedAt, s.Target, i);
            if (s.Action is StepAction.Clear or StepAction.RemoveScaffold) Add(clearedAt, s.Target, i);
            if (s.Column < 0 && s.Stand != default) { Add(standIn, s.Stand, i); Add(standIn, s.Stand.Up(), i); }
        }
        foreach (var lane in lanes)
            foreach (var i in lane.Steps)
            {
                var s = bp.Steps[i];
                var set = new SortedSet<int>();
                void From(IEnumerable<int>? js) { if (js != null) foreach (var j in js) if (j < i && laneOf.TryGetValue(j, out var g) && g != lane.Index) set.Add(j); }
                From(s.Deps);
                From(byTarget.GetValueOrDefault(s.Target));
                if (s.Column < 0 && s.Stand != default)
                {
                    From(placedAt.GetValueOrDefault(s.Stand.Down()));
                    From(clearedAt.GetValueOrDefault(s.Stand));
                    From(clearedAt.GetValueOrDefault(s.Stand.Up()));
                }
                if (s.IsPlacement) From(standIn.GetValueOrDefault(s.Target));
                // Up on the shared stair: its treads below this stand first (another lane builds them).
                if (s.Column < 0 && s.Stand != default && s.Stand.Y > baseY + 1)
                    From(stairs.Where(j => j < i && bp.Steps[j].Target.Y < s.Stand.Y));
                // Filling the stair's walk space: only once the other lanes' earlier steps up there are done.
                if (s.IsPlacement && walkSpace.Contains(s.Target)) From(elevated.Where(j => j < i));
                if (set.Count > 0) lane.Waits[i] = set.ToList();
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
