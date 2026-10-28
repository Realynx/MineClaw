using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Blueprints;

public enum StepKind { Clear, Place, Scaffold, RemoveScaffold }

/// <summary>
/// One construction step. <see cref="Look"/> = the way the body must face while placing (stairs, doors, chests, gates take
/// their meta from the placer's yaw per mc-dev); <see cref="Face"/> = the face to click (doors: the top of the block below;
/// ladders: the wall).
/// </summary>
/// <para><see cref="Hidden"/>: the face may be clicked unseen (b1.8.1 checks distance only); <see cref="Yaw"/>: the look yaw to
/// send (facing metas). Both come from the build planner (Bot/Building), which also chose where to stand.</para>
public sealed record BuildStep(StepKind Kind, BlockPos Pos, int Id, int Meta, Dir? Look = null, BlockFace? Face = null, bool Hidden = false, float? Yaw = null)
{
    public (int Item, int Damage) Item => BuildBlocks.ItemFor(Id, Meta);
    public override string ToString() => $"{Kind} {BuildBlocks.Name(Id, Meta)} {Pos.X},{Pos.Y},{Pos.Z}";
}

/// <summary>A body's share of a build: the cells it places and the area it claims (disjoint from every other part).</summary>
public sealed record BuildPart(int Index, int Count, HashSet<BlockPos> Cells, ClaimKey Claim, int AxisLo, int AxisHi, char Axis)
{
    /// <summary>Scaffolding for this part stays inside its strip (the claim), so bodies never work in each other's cells.</summary>
    public bool Allows(BlockPos p) => Coord(p) >= AxisLo && Coord(p) <= AxisHi;
    public int Coord(BlockPos p) => Axis switch { 'x' => p.X, 'z' => p.Z, _ => p.Y };
    public string Label => Count <= 1 ? "whole build" : $"part {Index + 1}/{Count} ({(Axis == 'y' ? "layers" : "strip")} {AxisLo}..{AxisHi})";
    public static BuildPart Whole(VoxelPlan plan, int dim = 0) => new(0, 1, plan.Blocks.Keys.Concat(plan.Air).ToHashSet(),
        ClaimKey.Area(plan.Min.Offset(-3, -1, -3), plan.Max.Offset(3, 1, 3), dim), int.MinValue, int.MaxValue, 'x');
}

/// <summary>The ordered steps for one part, plus cells it couldn't plan (no support reachable within the scaffold limit). The
/// simple layer order; the executor runs the full slicer (<see cref="Building.BuildPlanner"/>) instead.</summary>
public sealed class ConstructionOrder
{
    public List<BuildStep> Steps { get; } = new();
    public List<BlockPos> Unreachable { get; } = new();
    public int Places => Steps.Count(s => s.Kind == StepKind.Place);
    public int Scaffolds => Steps.Count(s => s.Kind == StepKind.Scaffold);
}

/// <summary>
/// Construction order (zero tokens): obstructions cleared top-down first; then layer by layer, bottom-up: structural blocks,
/// then thin/attached blocks and doors, each next to something already standing (support-first, nearest next); a cell with no
/// support gets a temporary scaffold path from the nearest support (self-placed blocks, removable under the protection policy);
/// a cell no standing spot can reach gets a scaffold walkway on the floor first (never a stand over the void); scaffolding comes
/// down at the end, top first.
/// </summary>
public static class ConstructionPlanner
{
    public const int Scaffold = Blocks.Cobblestone; // the executor swaps in whatever clutch block it carries

    public static ConstructionOrder Plan(VoxelPlan plan, IBlockAccess world, BuildPart? part = null, int maxScaffold = 6, double reach = 4.5)
    {
        part ??= BuildPart.Whole(plan);
        var result = new ConstructionOrder();
        int height = world.WorldHeight;
        // Simulated world: what stands once the steps so far are done.
        var sim = new Dictionary<BlockPos, int>();
        int At(BlockPos p) => sim.TryGetValue(p, out var v) ? v : world.GetBlock(p.X, p.Y, p.Z);
        bool Solid(BlockPos p) => BuildBlocks.Supports(At(p));
        bool Free(BlockPos p) { int id = At(p); return id == 0 || !Blocks.Get(id).Solid && !Blocks.IsLava(id); }

        // 1. Clear obstructions (cells that must end air or hold another block), top-down.
        foreach (var p in part.Cells.Where(c => world.IsLoaded(c.X, c.Z)).OrderByDescending(c => c.Y))
        {
            int id = world.GetBlock(p.X, p.Y, p.Z);
            if (id == 0 || Blocks.IsWater(id) || Blocks.IsLava(id)) continue;
            if (plan.Blocks.TryGetValue(p, out var want) && DesignChecks.SameState(want, id, world.GetMeta(p.X, p.Y, p.Z))) continue;
            result.Steps.Add(new BuildStep(StepKind.Clear, p, id, 0));
            sim[p] = 0;
        }

        // 2. Placements, layer by layer.
        var todo = plan.Blocks.Values.Where(b => part.Cells.Contains(b.Pos) && !b.UpperDoor
            && !(world.IsLoaded(b.Pos.X, b.Pos.Z) && DesignChecks.SameState(b, world.GetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z), world.GetMeta(b.Pos.X, b.Pos.Y, b.Pos.Z)))).ToList();
        var scaffolds = new List<BlockPos>();
        BlockPos last = todo.Count > 0 ? todo.OrderBy(b => b.Pos.Y).First().Pos : plan.Min;
        foreach (var layer in todo.GroupBy(b => b.Pos.Y).OrderBy(g => g.Key))
            foreach (var cls in layer.GroupBy(b => Class(b)).OrderBy(g => g.Key))
            {
                var left = cls.ToList();
                while (left.Count > 0)
                {
                    var ready = left.Where(b => Supported(b)).OrderBy(b => b.Pos.DistanceSquaredTo(last)).ThenBy(b => b.Pos.X).ThenBy(b => b.Pos.Z).FirstOrDefault();
                    if (ready == null)
                    {
                        // Nothing touches support: scaffold toward the nearest remaining cell.
                        var target = left.OrderBy(b => b.Pos.DistanceSquaredTo(last)).First();
                        var path = ScaffoldPath(target);
                        if (path == null)
                        {
                            result.Unreachable.Add(target.Pos);
                            left.Remove(target);
                            continue;
                        }
                        foreach (var s in path) { Add(new BuildStep(StepKind.Scaffold, s, Scaffold, 0)); scaffolds.Add(s); }
                        continue;
                    }
                    left.Remove(ready);
                    // A stand within reach on a real floor before working there (walkway scaffold if none).
                    if (!StandWithin(ready.Pos) && Walkway(ready.Pos) is { } walk)
                        foreach (var s in walk) { Add(new BuildStep(StepKind.Scaffold, s, Scaffold, 0)); scaffolds.Add(s); }
                    Add(StepFor(ready));
                    if (ready.Kind == BuildKind.Door) sim[ready.Pos.Up()] = ready.Id;
                    last = ready.Pos;
                }
            }

        // 3. Scaffolding comes down, top first (then the farthest from the build's bottom first).
        foreach (var s in scaffolds.Distinct().OrderByDescending(p => p.Y).ThenByDescending(p => p.DistanceSquaredTo(plan.Min)))
            result.Steps.Add(new BuildStep(StepKind.RemoveScaffold, s, Scaffold, 0));
        return result;

        void Add(BuildStep s)
        {
            result.Steps.Add(s);
            sim[s.Pos] = s.Id;
        }

        bool Supported(PlannedBlock b) => b.Kind switch
        {
            BuildKind.Door => BuildBlocks.NormalCube(At(b.Pos.Down())) && Free(b.Pos.Up()),
            BuildKind.Gravity => Solid(b.Pos.Down()),
            BuildKind.Attached when b.Id == Blocks.Ladder => BuildBlocks.NormalCube(At(LadderWall(b))),
            _ => BlockFaceExtensions.All.Any(f => Solid(b.Pos.Offset(f))),
        };

        // BFS from the target through free cells (inside this part's strip) to a cell touching support.
        List<BlockPos>? ScaffoldPath(PlannedBlock target)
        {
            var start = target.Pos;
            var prev = new Dictionary<BlockPos, BlockPos>();
            var q = new Queue<(BlockPos P, int D)>();
            q.Enqueue((start, 0));
            prev[start] = start;
            bool gravity = target.Kind is BuildKind.Gravity or BuildKind.Door;
            while (q.Count > 0)
            {
                var (p, d) = q.Dequeue();
                if (d >= maxScaffold) continue;
                foreach (var f in gravity ? [BlockFace.Bottom] : BlockFaceExtensions.All)
                {
                    var n = p.Offset(f);
                    if (prev.ContainsKey(n) || n.Y < 1 || n.Y >= height || !Free(n) || !part.Allows(n)) continue;
                    if (plan.Blocks.ContainsKey(n) && !Solid(n)) continue; // a planned cell is the build's, not scaffolding
                    if (!world.IsLoaded(n.X, n.Z)) continue;
                    prev[n] = p;
                    if (BlockFaceExtensions.All.Any(g => Solid(n.Offset(g))))
                    {
                        var path = new List<BlockPos>();
                        for (var c = n; c != start; c = prev[c]) path.Add(c);
                        return path; // support side first, toward the target
                    }
                    q.Enqueue((n, d + 1));
                }
            }
            return null;
        }

        bool Standable(BlockPos s) => Free(s) && Free(s.Up()) && Solid(s.Down()) && At(s.Down()) is not (Blocks.Fence or Blocks.FenceGate)
                                      && world.IsLoaded(s.X, s.Z);

        bool StandWithin(BlockPos t)
        {
            var c = t.Center;
            for (int dy = -3; dy <= 1; dy++)
                for (int dz = -4; dz <= 4; dz++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        var s = new BlockPos(t.X + dx, t.Y + dy, t.Z + dz);
                        if (s == t || s.Up() == t) continue;
                        if (new Vec3d(s.X + 0.5, s.Y + 1.62, s.Z + 0.5).DistanceTo(c) > reach) continue;
                        if (Standable(s)) return true;
                    }
            return false;
        }

        // A floor walkway (scaffold) from the nearest standing spot toward the target until one is within reach.
        List<BlockPos>? Walkway(BlockPos t)
        {
            BlockPos? from = null;
            double best = double.MaxValue;
            for (int dy = -3; dy <= 1; dy++)
                for (int dz = -10; dz <= 10; dz++)
                    for (int dx = -10; dx <= 10; dx++)
                    {
                        var s = new BlockPos(t.X + dx, t.Y + dy, t.Z + dz);
                        if (!Standable(s)) continue;
                        double d = s.DistanceSquaredTo(t);
                        if (d < best) { best = d; from = s; }
                    }
            if (from is not { } f) return null;
            var path = new List<BlockPos>();
            var cur = f;
            for (int i = 0; i < maxScaffold; i++)
            {
                int sx = Math.Sign(t.X - cur.X), sz = Math.Sign(t.Z - cur.Z);
                if (sx == 0 && sz == 0) break;
                var next = Math.Abs(t.X - cur.X) >= Math.Abs(t.Z - cur.Z) ? cur.Offset(sx, 0, 0) : cur.Offset(0, 0, sz);
                var floor = next.Down();
                if (!part.Allows(floor) || plan.Blocks.ContainsKey(floor) || !Free(next) || !Free(next.Up())) break;
                if (!Solid(floor))
                {
                    if (!Free(floor)) break;
                    path.Add(floor);
                    sim[floor] = Scaffold;
                }
                cur = next;
                if (StandWithin(t)) return path;
            }
            foreach (var p in path) sim.Remove(p);
            return null;
        }
    }

    private static int Class(PlannedBlock b) => b.Kind switch
    {
        BuildKind.Solid or BuildKind.Gravity or BuildKind.Slab or BuildKind.Stairs or BuildKind.Station => 0,
        BuildKind.Thin => 1,
        BuildKind.Door => 2,
        _ => 3, // torches, ladders: after the walls they hang on
    };

    private static BlockPos LadderWall(PlannedBlock b) => b.Meta switch
    {
        2 => b.Pos.Offset(0, 0, 1), 3 => b.Pos.Offset(0, 0, -1), 4 => b.Pos.Offset(1, 0, 0), _ => b.Pos.Offset(-1, 0, 0),
    };

    /// <summary>The placement for a planned block: the direction to face (yaw-set metas) and the face to click.</summary>
    public static BuildStep StepFor(PlannedBlock b)
    {
        Dir? look = null;
        BlockFace? face = null;
        switch (b.Kind)
        {
            case BuildKind.Stairs: look = BuildBlocks.StairsAscend(b.Meta); break; // walking up = the way the placer faced
            case BuildKind.Door: look = BuildBlocks.DoorPlacerFacing(b.Meta); face = BlockFace.Top; break;
        }
        if (b.Id is Blocks.Chest or Blocks.Furnace or Blocks.Dispenser && b.Meta is >= 2 and <= 5)
            look = b.Meta switch { 2 => Dir.South, 3 => Dir.North, 4 => Dir.East, _ => Dir.West }; // the front faces the placer
        if (b.Id == Blocks.FenceGate) look = (b.Meta & 3) switch { 0 => Dir.South, 1 => Dir.West, 2 => Dir.North, _ => Dir.East };
        if (b.Id == Blocks.Ladder)
            face = b.Meta switch { 2 => BlockFace.North, 3 => BlockFace.South, 4 => BlockFace.West, _ => BlockFace.East }; // the wall's face toward the ladder
        return new BuildStep(StepKind.Place, b.Pos, b.Id, b.Meta & (b.Kind == BuildKind.Door ? 7 : 15), look, face);
    }

    // ------------------------------------------------------------------ split across bodies

    /// <summary>
    /// Splits the plan into <paramref name="bodies"/> disjoint strips along its longer horizontal axis, balanced by block count
    /// (doors stay whole: a column never splits). Each strip's claim area covers its full height; the outer strips extend 3
    /// blocks past the footprint for scaffolding. Areas never overlap, so bodies build at the same time without conflicts.
    /// </summary>
    public static List<BuildPart> Split(VoxelPlan plan, int bodies, int dim = 0)
    {
        bodies = Math.Max(1, bodies);
        bool alongX = plan.Max.X - plan.Min.X >= plan.Max.Z - plan.Min.Z;
        int lo = alongX ? plan.Min.X : plan.Min.Z, hi = alongX ? plan.Max.X : plan.Max.Z;
        bodies = Math.Min(bodies, hi - lo + 1);
        var perSlice = new int[hi - lo + 1];
        foreach (var b in plan.Blocks.Keys) perSlice[(alongX ? b.X : b.Z) - lo]++;
        int total = perSlice.Sum(), target = (total + bodies - 1) / bodies;
        var bounds = new List<(int A, int B)>();
        int start = lo, acc = 0;
        for (int s = lo; s <= hi; s++)
        {
            acc += perSlice[s - lo];
            int remainingSlices = hi - s, remainingParts = bodies - bounds.Count - 1;
            if (bounds.Count < bodies - 1 && (acc >= target || remainingSlices == remainingParts) && remainingSlices >= remainingParts && remainingParts > 0)
            {
                bounds.Add((start, s));
                start = s + 1;
                acc = 0;
            }
        }
        bounds.Add((start, hi));
        var parts = new List<BuildPart>();
        for (int i = 0; i < bounds.Count; i++)
        {
            var (a, b) = bounds[i];
            int axisLo = i == 0 ? a - 3 : a, axisHi = i == bounds.Count - 1 ? b + 3 : b;
            var cells = plan.Blocks.Keys.Concat(plan.Air).Where(p => (alongX ? p.X : p.Z) >= a && (alongX ? p.X : p.Z) <= b).ToHashSet();
            var min = alongX ? new BlockPos(axisLo, plan.Min.Y - 1, plan.Min.Z - 3) : new BlockPos(plan.Min.X - 3, plan.Min.Y - 1, axisLo);
            var max = alongX ? new BlockPos(axisHi, plan.Max.Y + 1, plan.Max.Z + 3) : new BlockPos(plan.Max.X + 3, plan.Max.Y + 1, axisHi);
            parts.Add(new BuildPart(i, bounds.Count, cells, ClaimKey.Area(min, max, dim), axisLo, axisHi, alongX ? 'x' : 'z'));
        }
        return parts;
    }

    /// <summary>
    /// Splits by layer bands (bottom-up, balanced by block count): part k+1 stands on part k, so these run one after another
    /// (a crew script hands each body the next band); claims are full-footprint boxes over disjoint y ranges.
    /// </summary>
    public static List<BuildPart> SplitLayers(VoxelPlan plan, int parts, int dim = 0)
    {
        int lo = plan.Min.Y, hi = plan.Max.Y;
        parts = Math.Clamp(parts, 1, hi - lo + 1);
        var per = new int[hi - lo + 1];
        foreach (var b in plan.Blocks.Keys) per[b.Y - lo]++;
        int target = (per.Sum() + parts - 1) / parts;
        var bounds = new List<(int A, int B)>();
        int start = lo, acc = 0;
        for (int y = lo; y <= hi; y++)
        {
            acc += per[y - lo];
            int remainingLayers = hi - y, remainingParts = parts - bounds.Count - 1;
            if (remainingParts > 0 && remainingLayers >= remainingParts && (acc >= target || remainingLayers == remainingParts))
            {
                bounds.Add((start, y));
                start = y + 1;
                acc = 0;
            }
        }
        bounds.Add((start, hi));
        return bounds.Select((b, i) =>
        {
            int a = i == 0 ? b.A - 1 : b.A, z = i == bounds.Count - 1 ? b.B + 1 : b.B;
            var cells = plan.Blocks.Keys.Concat(plan.Air).Where(p => p.Y >= b.A && p.Y <= b.B).ToHashSet();
            var claim = ClaimKey.Area(new BlockPos(plan.Min.X - 3, a, plan.Min.Z - 3), new BlockPos(plan.Max.X + 3, z, plan.Max.Z + 3), dim);
            return new BuildPart(i, bounds.Count, cells, claim, a, z, 'y');
        }).ToList();
    }
}
