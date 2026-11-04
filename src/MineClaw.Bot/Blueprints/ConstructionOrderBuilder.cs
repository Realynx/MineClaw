using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// One run of <see cref="ConstructionPlanner.Plan"/>: obstructions cleared top-down, placements layer by layer (support first,
/// nearest next, scaffold paths and floor walkways where needed), scaffolding down at the end. Holds the simulated world (what
/// stands once the steps so far are done).
/// </summary>
internal sealed class ConstructionOrderBuilder(VoxelPlan plan, IBlockAccess world, BuildPart part, int maxScaffold, double reach)
{
    private const int Scaffold = ConstructionPlanner.Scaffold;

    private readonly ConstructionOrder _result = new();
    private readonly int _height = world.WorldHeight;
    private readonly Dictionary<BlockPos, int> _sim = new();
    private readonly List<BlockPos> _scaffolds = [];
    private BlockPos _last;

    public ConstructionOrder Build()
    {
        ClearObstructions();
        PlaceLayers();
        // 3. Scaffolding comes down, top first (then the farthest from the build's bottom first).
        foreach (var s in _scaffolds.Distinct().OrderByDescending(p => p.Y).ThenByDescending(p => p.DistanceSquaredTo(plan.Min)))
            _result.Steps.Add(new BuildStep(StepKind.RemoveScaffold, s, Scaffold, 0));
        return _result;
    }

    private int At(BlockPos p) => _sim.TryGetValue(p, out var v) ? v : world.GetBlock(p.X, p.Y, p.Z);
    private bool Solid(BlockPos p) => BuildBlocks.Supports(At(p));
    private bool Free(BlockPos p) { int id = At(p); return id == 0 || !Blocks.Get(id).Solid && !Blocks.IsLava(id); }

    private void Add(BuildStep s)
    {
        _result.Steps.Add(s);
        _sim[s.Pos] = s.Id;
    }

    private void AddScaffolds(List<BlockPos> cells)
    {
        foreach (var s in cells) { Add(new BuildStep(StepKind.Scaffold, s, Scaffold, 0)); _scaffolds.Add(s); }
    }

    /// <summary>1. Clear obstructions (cells that must end air or hold another block), top-down.</summary>
    private void ClearObstructions()
    {
        foreach (var p in part.Cells.Where(c => world.IsLoaded(c.X, c.Z)).OrderByDescending(c => c.Y))
        {
            int id = world.GetBlock(p.X, p.Y, p.Z);
            if (id == 0 || Blocks.IsWater(id) || Blocks.IsLava(id)) continue;
            if (plan.Blocks.TryGetValue(p, out var want) && DesignChecks.SameState(want, id, world.GetMeta(p.X, p.Y, p.Z))) continue;
            _result.Steps.Add(new BuildStep(StepKind.Clear, p, id, 0));
            _sim[p] = 0;
        }
    }

    /// <summary>2. Placements, layer by layer, each layer's classes in order (structure, thin, doors, attached).</summary>
    private void PlaceLayers()
    {
        var todo = plan.Blocks.Values.Where(b => part.Cells.Contains(b.Pos) && !b.UpperDoor && !StandsAlready(b)).ToList();
        _last = todo.Count > 0 ? todo.OrderBy(b => b.Pos.Y).First().Pos : plan.Min;
        foreach (var layer in todo.GroupBy(b => b.Pos.Y).OrderBy(g => g.Key))
            foreach (var cls in layer.GroupBy(b => ConstructionPlanner.Class(b)).OrderBy(g => g.Key))
                PlaceClass(cls.ToList());
    }

    private bool StandsAlready(PlannedBlock b) =>
        world.IsLoaded(b.Pos.X, b.Pos.Z) && DesignChecks.SameState(b, world.GetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z), world.GetMeta(b.Pos.X, b.Pos.Y, b.Pos.Z));

    /// <summary>The supported cell nearest the last one first; when nothing touches support, scaffold toward the nearest.</summary>
    private void PlaceClass(List<PlannedBlock> left)
    {
        while (left.Count > 0)
        {
            var ready = left.Where(Supported).OrderBy(b => b.Pos.DistanceSquaredTo(_last)).ThenBy(b => b.Pos.X).ThenBy(b => b.Pos.Z).FirstOrDefault();
            if (ready == null)
            {
                ScaffoldToward(left);
                continue;
            }
            left.Remove(ready);
            // A stand within reach on a real floor before working there (walkway scaffold if none).
            if (!StandWithin(ready.Pos) && Walkway(ready.Pos) is { } walk) AddScaffolds(walk);
            Add(ConstructionPlanner.StepFor(ready));
            if (ready.Kind == BuildKind.Door) _sim[ready.Pos.Up()] = ready.Id;
            _last = ready.Pos;
        }
    }

    /// <summary>Nothing touches support: scaffold toward the nearest remaining cell (unreachable when no path).</summary>
    private void ScaffoldToward(List<PlannedBlock> left)
    {
        var target = left.OrderBy(b => b.Pos.DistanceSquaredTo(_last)).First();
        if (ScaffoldPath(target) is { } path)
        {
            AddScaffolds(path);
            return;
        }
        _result.Unreachable.Add(target.Pos);
        left.Remove(target);
    }

    private bool Supported(PlannedBlock b) => b.Kind switch
    {
        BuildKind.Door => BuildBlocks.NormalCube(At(b.Pos.Down())) && Free(b.Pos.Up()),
        BuildKind.Gravity => Solid(b.Pos.Down()),
        BuildKind.Attached when b.Id == Blocks.Ladder => BuildBlocks.NormalCube(At(ConstructionPlanner.LadderWall(b))),
        _ => BlockFaceExtensions.All.Any(f => Solid(b.Pos.Offset(f))),
    };

    /// <summary>BFS from the target through free cells (inside this part's strip) to a cell touching support.</summary>
    private List<BlockPos>? ScaffoldPath(PlannedBlock target)
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
                if (prev.ContainsKey(n) || !ScaffoldCell(n)) continue;
                prev[n] = p;
                if (BlockFaceExtensions.All.Any(g => Solid(n.Offset(g)))) return PathBack(prev, n, start); // support side first, toward the target
                q.Enqueue((n, d + 1));
            }
        }
        return null;
    }

    /// <summary>A free cell in the world and the part's strip, loaded, not a planned cell (that is the build's, not
    /// scaffolding) unless something solid stands there already.</summary>
    private bool ScaffoldCell(BlockPos n) =>
        n.Y >= 1 && n.Y < _height && Free(n) && part.Allows(n) && !(plan.Blocks.ContainsKey(n) && !Solid(n)) && world.IsLoaded(n.X, n.Z);

    private static List<BlockPos> PathBack(Dictionary<BlockPos, BlockPos> prev, BlockPos end, BlockPos start)
    {
        var path = new List<BlockPos>();
        for (var c = end; c != start; c = prev[c]) path.Add(c);
        return path;
    }

    private bool Standable(BlockPos s) => Free(s) && Free(s.Up()) && Solid(s.Down()) && At(s.Down()) is not (Blocks.Fence or Blocks.FenceGate)
                                          && world.IsLoaded(s.X, s.Z);

    private bool StandWithin(BlockPos t)
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

    /// <summary>A floor walkway (scaffold) from the nearest standing spot toward the target until one is within reach.</summary>
    private List<BlockPos>? Walkway(BlockPos t)
    {
        if (NearestStandable(t) is not { } f) return null;
        var path = new List<BlockPos>();
        var cur = f;
        for (int i = 0; i < maxScaffold; i++)
        {
            if (WalkwayNext(cur, t) is not { } next) break;
            var floor = next.Down();
            if (!Solid(floor))
            {
                path.Add(floor);
                _sim[floor] = Scaffold;
            }
            cur = next;
            if (StandWithin(t)) return path;
        }
        foreach (var p in path) _sim.Remove(p);
        return null;
    }

    /// <summary>The standable cell nearest <paramref name="t"/> (3 below to 1 above, 10 sideways), or null.</summary>
    private BlockPos? NearestStandable(BlockPos t)
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
        return from;
    }

    /// <summary>The walkway's next cell toward <paramref name="t"/> (along the longer axis): room for the body, its floor in the
    /// part's strip, not planned, solid or free for a scaffold; null when there or blocked.</summary>
    private BlockPos? WalkwayNext(BlockPos cur, BlockPos t)
    {
        int sx = Math.Sign(t.X - cur.X), sz = Math.Sign(t.Z - cur.Z);
        if (sx == 0 && sz == 0) return null;
        var next = Math.Abs(t.X - cur.X) >= Math.Abs(t.Z - cur.Z) ? cur.Offset(sx, 0, 0) : cur.Offset(0, 0, sz);
        var floor = next.Down();
        if (!part.Allows(floor) || plan.Blocks.ContainsKey(floor) || !Free(next) || !Free(next.Up())) return null;
        return Solid(floor) || Free(floor) ? next : null;
    }
}
