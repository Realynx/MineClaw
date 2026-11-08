using System.Diagnostics;

namespace MineClaw.World;

/// <summary>
/// A* over standable foot cells with void-safe moves. Nodes are cells where a 0.6×1.8 player centered in the cell
/// has support under its footprint (or is in water / on a ladder) and no collision. Moves: walk (4-dir +
/// diagonal without corner cutting), jump up 1, drop ≤ MaxDrop (larger into water), ladder climb, swim, and
/// optionally break (mine obstacles) and place (bridge / pillar with a scaffold budget).
/// <para>Void safety: cells over the void are never nodes; a cell next to a void/deadly drop is an "edge" (higher
/// cost; the step is flagged <see cref="PathStep.Sneak"/>); diagonals require both orthogonal cells to be safe floor;
/// drops and jumps are cardinal only. A dangerous runout rejects a drop except a controlled one-block descent between
/// full cubes over a floored pit; void/lava runouts remain refused. Unloaded chunks are treated as void.</para>
/// </summary>
public sealed partial class Pathfinder
{
    private readonly IBlockAccess _world;
    private readonly PathOptions _o;
    private readonly Overlay _w;
    private readonly List<AABB> _boxes = new(16);
    /// <summary>The version's rules, captured once per search (multi-version P3): step-up height (b1.8.1 0.5, 1.12.2 0.6), body
    /// height (1.8) and world height (128 / 256).</summary>
    private readonly double _step, _height;
    /// <summary>The version tramples farmland under a walker (b1.8.1): careful moves sneak; else they are walked gently.</summary>
    private readonly bool _walkTramples;
    private readonly int _worldHeight;
    private readonly Dictionary<long, CellInfo> _cellCache = new();
    private readonly Dictionary<long, bool> _deadlyCache = new();
    private static readonly double[] FallTicks = BuildFallTicks();

    private Pathfinder(IBlockAccess world, PathOptions options)
    {
        _world = world;
        _o = options;
        _w = new Overlay(world);
        var rules = world.Physics;
        _step = rules.StepHeight;
        _walkTramples = rules.WalkingTramplesFarmland;
        _height = rules.Height;
        _worldHeight = world.WorldHeight;
        _view = options.Mechanisms is { IsEmpty: false } v ? v : null;
    }

    /// <summary>Finds a path from foot cell <paramref name="start"/> to <paramref name="goal"/>. Routes through mechanism
    /// doors are kept only when there is a known way back out (see <see cref="PathOptions.AllowOneWay"/>).</summary>
    public static PathResult FindPath(IBlockAccess world, BlockPos start, Goal goal, PathOptions? options = null,
        CancellationToken ct = default)
    {
        var o = options ?? PathOptions.Default;
        var pf = new Pathfinder(world, o);
        // One budget for the whole search, the return checks' re-plans and probes included (live 11-04: a 12.7 s search with a 2 s budget:
        // each re-plan had its own 2 s).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(o.Timeout + TimeSpan.FromMilliseconds(250));
        var r = pf.Search(start, goal, budget.Token);
        return pf.WalkPits(o.AllowOneWay || r.Steps.Count == 0 ? r : pf.CheckReturn(r, start, goal, budget.Token), start);
    }

    /// <summary>Finds a path from a feet position (see <see cref="StartCellFor"/>).</summary>
    public static PathResult FindPath(IBlockAccess world, Vec3d feet, Goal goal, PathOptions? options = null,
        CancellationToken ct = default)
    {
        var cell = StartCellFor(world, feet);
        if (cell is null)
            return new PathResult(PathStatus.InvalidStart, [], 0, 0, TimeSpan.Zero, 0, "not standing on anything");
        return FindPath(world, cell.Value, goal, options, ct);
    }

    /// <summary>
    /// The foot cell to start pathing from. When the player stands at an edge with its center over air (common while
    /// sneaking), picks the overlapped standable cell closest to the center. Null when not standing anywhere valid.
    /// </summary>
    public static BlockPos? StartCellFor(IBlockAccess world, Vec3d feet)
    {
        var pf = new Pathfinder(world, PathOptions.Default);
        int y = (int)Math.Floor(feet.Y + 0.001);
        var box = AABB.ForPlayer(feet);
        BlockPos? best = null;
        double bestD = double.MaxValue;
        for (int x = (int)Math.Floor(box.MinX); x <= (int)Math.Floor(box.MaxX); x++)
        for (int z = (int)Math.Floor(box.MinZ); z <= (int)Math.Floor(box.MaxZ); z++)
        foreach (int yy in new[] { y, y + 1, y - 1 })
        {
            var c = pf.Cell(x, yy, z);
            if (!c.Ok || Math.Abs(c.Floor - feet.Y) > 0.6) continue;
            double d = Math.Pow(x + 0.5 - feet.X, 2) + Math.Pow(z + 0.5 - feet.Z, 2) + Math.Abs(c.Floor - feet.Y);
            if (d < bestD) { bestD = d; best = new BlockPos(x, yy, z); }
        }
        return best;
    }

    // ------------------------------------------------------------------ search

    private sealed class Node(int x, int y, int z)
    {
        public readonly int X = x, Y = y, Z = z;
        public double G = double.MaxValue;
        public double H;
        /// <summary>Estimated elapsed ticks (physics move times, not costs): pulse windows are checked against it.</summary>
        public double T;
        public Node? Parent;
        /// <summary>The move that reached it (its <see cref="PathStep"/> is made only for the path built: a search relaxes many
        /// more nodes than it returns, and a step per relaxation was most of a long search's garbage).</summary>
        public Candidate Move;
        public TriggerUse? Requires;
        public Mod? Mods;
        public int Scaffold;
        public bool Closed;
        /// <summary>Active button/plate pulse (its effects are in the world until the deadline).</summary>
        public PulseState? Pulse;
        /// <summary>Levers flipped so far on this path (their effects are in <see cref="Mods"/>).</summary>
        public LeverUse? Levers;
        /// <summary>Consecutive steps on this path with the head under water (0 = breathing here).</summary>
        public int Under;
        public BlockPos Pos => new(X, Y, Z);
    }

    /// <summary>The working state of one A* search, passed by reference through its steps.</summary>
    private struct SearchState
    {
        public Dictionary<(long, int), Node> Nodes;
        public PriorityQueue<(Node N, double G), double> Open;
        public List<Candidate> Moves;
        public Node Start, Best;
        public int Explored, TooSlow;
        public string? Reason;
        public Stopwatch Sw;
        public Goal Goal;
        public CancellationToken Ct;
    }

    private PathResult Search(BlockPos start, Goal goal, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _goal = goal;
        _w.Mods = _baseMods;
        _w.Pulse = null;
        var startCell = Cell(start.X, start.Y, start.Z);
        if (!startCell.Ok)
            return new PathResult(PathStatus.InvalidStart, [], 0, 0, sw.Elapsed, 0, $"start {start} is not a standable cell");
        _fromSurface = _o.SkyLight is { } sky && Math.Max(sky(start.X, start.Y, start.Z), sky(start.X, start.Y + 1, start.Z)) >= 8;

        var st = NewSearch(start, startCell, goal, ct, sw);
        return RunSearch(ref st) ?? Unfinished(ref st);
    }

    private SearchState NewSearch(BlockPos start, CellInfo startCell, Goal goal, CancellationToken ct, Stopwatch sw)
    {
        var s = new Node(start.X, start.Y, start.Z)
        {
            G = 0, H = goal.Distance(start.X, start.Y, start.Z), Mods = _baseMods,
            Under = Breath.HeadUnderAt(_w, start.X, startCell.Floor, start.Z) ? 1 : 0,
        };
        var st = new SearchState
        {
            Nodes = new Dictionary<(long, int), Node>(), Open = new PriorityQueue<(Node N, double G), double>(), Moves = new List<Candidate>(32),
            Start = s, Best = s, Sw = sw, Goal = goal, Ct = ct,
        };
        st.Nodes[(Key(start.X, start.Y, start.Z), 0)] = s;
        st.Open.Enqueue((s, 0), s.H * PathOptions.WalkCost);
        return st;
    }

    /// <summary>The A* loop: the path once the goal is closed, or null when the open set ran dry or a budget stopped it
    /// (<see cref="SearchState.Reason"/>).</summary>
    private PathResult? RunSearch(ref SearchState st)
    {
        while (st.Open.Count > 0)
        {
            var (n, g) = st.Open.Dequeue();
            if (n.Closed || g > n.G) continue;
            if (Close(ref st, n) is { } found) return found;
            if (StopReason(ref st) is { } why) { st.Reason = why; return null; }
            st.Moves.Clear();
            _w.Mods = n.Mods;
            _w.Pulse = n.Pulse?.Mods;
            Expand(n, st.Moves);
            foreach (var m in st.Moves) Relax(ref st, n, m);
        }
        return null;
    }

    /// <summary>Closes a node: the path when it is the goal; else it may be the best partial end so far.</summary>
    private PathResult? Close(ref SearchState st, Node n)
    {
        n.Closed = true;
        st.Explored++;
        if (_collect is { } col && !col.ContainsKey(n.Pos)) col[n.Pos] = n.G;
        if (st.Goal.IsReached(n.X, n.Y, n.Z) && !InPulseSolid(n))
            return Build(PathStatus.Found, n, st.Explored, st.Sw.Elapsed, null);
        if (n.H < st.Best.H || (n.H == st.Best.H && n.G < st.Best.G)) st.Best = n;
        return null;
    }

    private string? StopReason(ref SearchState st)
    {
        if (st.Explored >= _o.MaxNodes) return "node budget exhausted";
        // Often enough that a slow expansion (a GC pause, a busy world lock) can't run far past the budget.
        if ((st.Explored & 63) != 0) return null;
        if (st.Sw.Elapsed > _o.Timeout) return "timeout";
        if (st.Ct.IsCancellationRequested) return "cancelled";
        return null;
    }

    /// <summary>Relaxes the move <paramref name="m"/> out of <paramref name="n"/>: a cheaper way into its node (with the
    /// world edits, mechanism state and breath it carries) re-opens that node.</summary>
    private void Relax(ref SearchState st, Node n, Candidate m)
    {
        if (Avoided(n, m)) return;
        int scaffold = ScaffoldAfter(n, m);
        if (scaffold < 0) return;
        // Mechanisms: pulse windows, plates walked onto, lever/button effects.
        if (!MechanismStep(n, ref m, out var pulse, out var levers, out var requires, out double time)) { st.TooSlow++; return; }
        var t = NodeFor(ref st, m, StateKey(pulse, levers, n.T + time));
        if (t.Closed) return;
        // Breath: each consecutive step with the head under water costs more than the last, and a plan never holds the
        // breath longer than MaxHeadUnderSteps (air lasts 15 s): the surface route wins (live 10-29, main drowned on a
        // ravine lake's bed at 126,52,295).
        int under = Breath.HeadUnderAt(_w, m.X, m.Floor, m.Z) ? n.Under + 1 : 0;
        if (under > _o.MaxHeadUnderSteps && m.Y <= n.Y) return; // rising toward the air is always allowed
        double ng = n.G + m.Cost + under * _o.HeadUnderStepCost + TerrainPenalty(m.X, m.Y, m.Z);
        if (ng >= t.G) return;
        t.G = ng;
        t.Under = under;
        t.T = n.T + time;
        t.Parent = n;
        t.Scaffold = scaffold;
        t.Mods = ModsAfter(n.Mods, m);
        t.Pulse = pulse;
        t.Levers = levers;
        t.Move = m;
        t.Requires = requires;
        st.Open.Enqueue((t, ng), ng + t.H * PathOptions.WalkCost);
    }

    /// <summary>The caller's vetoes: avoided cells, jumps and moves.</summary>
    private bool Avoided(Node n, Candidate m)
    {
        if (_o.Avoid is { } avoid && avoid.Contains(new BlockPos(m.X, m.Y, m.Z))) return true;
        if (m.Kind is MoveKind.Jump or MoveKind.SprintJump or MoveKind.JumpGap && _o.AvoidJumps is { } aj
            && aj.Contains((new BlockPos(n.X, n.Y, n.Z), new BlockPos(m.X, m.Y, m.Z)))) return true;
        return _o.AvoidMoves is { } am && am.Contains((new BlockPos(n.X, n.Y, n.Z), new BlockPos(m.X, m.Y, m.Z)));
    }

    /// <summary>Scaffold blocks placed so far with this move; −1 when a placement would go over the budget.</summary>
    private int ScaffoldAfter(Node n, Candidate m)
    {
        int scaffold = n.Scaffold + (m.Place is null ? 0 : 1);
        return scaffold > _o.ScaffoldBudget && m.Place is not null ? -1 : scaffold;
    }

    private static Node NodeFor(ref SearchState st, Candidate m, int state)
    {
        var key = (Key(m.X, m.Y, m.Z), state);
        if (!st.Nodes.TryGetValue(key, out var t))
        {
            t = new Node(m.X, m.Y, m.Z) { H = st.Goal.Distance(m.X, m.Y, m.Z) };
            st.Nodes[key] = t;
        }
        return t;
    }

    /// <summary>The world edits after the move: blocks broken, the scaffold placed, openables toggled, a lever flipped.</summary>
    private Mod? ModsAfter(Mod? mods, Candidate m)
    {
        foreach (var b in m.Break) mods = new Mod(b, 0, mods);
        if (m.Place is { } p) mods = new Mod(p, _o.ScaffoldBlockId, mods);
        if (m.Open is { } opens) foreach (var tg in opens) mods = ToggleMods(tg, mods);
        if (m.Trigger is { Link.IsPulse: false } lever) mods = LeverMods(lever, mods);
        return mods;
    }

    /// <summary>No path to the goal: the best partial path when allowed, else none (with why).</summary>
    private PathResult Unfinished(ref SearchState st)
    {
        st.Reason ??= st.TooSlow > 0 && _view is not null ? "no route (mechanism pulses too short to make it)" : "no route";
        if (_o.AllowPartial && st.Best != st.Start)
            return Build(PathStatus.Partial, st.Best, st.Explored, st.Sw.Elapsed, st.Reason);
        return new PathResult(PathStatus.NoPath, [], 0, st.Explored, st.Sw.Elapsed, 0, st.Reason);
    }

    private static PathResult Build(PathStatus status, Node end, int explored, TimeSpan elapsed, string? reason)
    {
        var steps = new List<PathStep>();
        for (var n = end; n.Parent is not null; n = n.Parent)
        {
            var m = n.Move;
            steps.Add(new PathStep(m.X, m.Y, m.Z, m.Kind, m.Floor, m.Cost, m.Break, m.Place, m.Sneak, m.Open, m.Jump, m.Trigger, n.Requires, m.Gentle));
        }
        steps.Reverse();
        // A plate walked over that no later step needs is just a walk (the follower doesn't wait on it).
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not { Kind: MoveKind.Trigger, Trigger: { IsPlate: true } tu }) continue;
            bool needed = false;
            for (int j = i + 1; j < steps.Count && !needed; j++) needed = steps[j].Requires == tu;
            if (!needed) steps[i] = steps[i] with { Kind = MoveKind.Walk, Trigger = null };
        }
        return new PathResult(status, steps, end.G, explored, elapsed, end.Scaffold, reason);
    }

    private static long Key(int x, int y, int z) =>
        ((long)(x & 0x3FFFFFF) << 38) | ((long)(z & 0x3FFFFFF) << 12) | (long)(y & 0xFFF);

    // ------------------------------------------------------------------ moves

    /// <param name="Time">Estimated ticks the move takes (negative = use <see cref="Cost"/>, which never underestimates).</param>
    private readonly record struct Candidate(int X, int Y, int Z, MoveKind Kind, double Floor, double Cost,
        IReadOnlyList<BlockPos> Break, BlockPos? Place, bool Sneak, IReadOnlyList<DoorToggle>? Open = null, ParkourPlan? Jump = null,
        double Time = -1, TriggerUse? Trigger = null, bool Gentle = false);

    private static readonly (int Dx, int Dz)[] Cardinals = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int Dx, int Dz)[] Diagonals = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>
    /// The moves out of node <paramref name="n"/>, in a fixed order (the search's tie-breaking depends on it): the four cardinal
    /// neighbours (walk / step up / jump, then for a blocked or open one: open through, drop, mine, bridge), the diagonals, up and
    /// down (ladders, water, a hatch), a pillar, gap jumps and known mechanisms.
    /// </summary>
    private void Expand(Node n, List<Candidate> output)
    {
        int x = n.X, y = n.Y, z = n.Z;
        var here = Cell(x, y, z);
        if (!here.Ok) return; // e.g. a broken/placed block invalidated it
        bool hereEdge = IsEdge(x, y, z);
        _hereFarmland = OnFarmland(x, y, z);
        _hereCareful = Careful(x, y, z);

        foreach (var (dx, dz) in Cardinals) ExpandCardinal(output, here, hereEdge, x, y, z, dx, dz);
        ExpandDiagonals(output, here, hereEdge, x, y, z);
        // Vertical: ladders and water.
        ExpandUp(output, here, x, y, z);
        ExpandDown(output, here, x, y, z);

        // Pillar: jump and place a block in the current foot cell.
        if (CanPillar(here, x, y, z))
            output.Add(new Candidate(x, y + 1, z, MoveKind.Pillar, y + 1, _o.PlaceCost + PathOptions.JumpExtra + 4, [],
                new BlockPos(x, y, z), hereEdge));

        // Gap jumps (sprint / plain), each validated by forward physics simulation.
        if (_o.AllowParkour && here.Ground && !here.Water && !here.Climb) TryParkour(output, here, x, y, z);

        // Known mechanisms: press a button / flip a lever within reach.
        if (_view is not null) TryTriggers(output, n, here);
    }

    /// <summary>What a cardinal level move did: nothing, a walk/step (no other way into that cell is tried), or a blocked
    /// transit that rules the direction out altogether.</summary>
    private enum LevelMove : byte { None, Walked, Skip }

    private void ExpandCardinal(List<Candidate> output, CellInfo here, bool hereEdge, int x, int y, int z, int dx, int dz)
    {
        int tx = x + dx, tz = z + dz;
        var t = WalkCell(tx, y, tz, here.Floor, out int ty);

        // Doors and trapdoors are thin panels on a cell edge: the centred boxes never touch them, so the move
        // between the cells is checked too (closed = obstacle, or an Open* step when allowed).
        List<DoorToggle>? opens = null;
        bool transit = !t.Ok || TransitOk(x, z, tx, tz, y, Math.Max(here.Floor, t.Floor), out opens);

        var level = AddLevelMove(output, here, hereEdge, x, z, tx, ty, tz, t, transit, opens);
        if (level == LevelMove.Skip) return;
        AddJumpUp(output, here, hereEdge, x, y, z, tx, tz);
        if (level == LevelMove.Walked) return;
        AddBlockedMoves(output, here, hereEdge, x, y, z, dx, dz, t.Ok);
    }

    /// <summary>
    /// Same level (with ≤0.5 step-ups, or a &lt;1 block step down e.g. slab → ground), or a higher partial block that needs a jump.
    /// The body crosses the cell edge at the higher of the two floors, so both columns must be clear there: stepping north off the
    /// gen slab 7,64,6 (feet 64.5, head 66.3) the head hit a leaf block at 7,66,5 that the target cell's own check (floor 64) never
    /// saw (live 10-28: three bots stuck at 7.5,64.5,6.3, every re-plan chose the same step).
    /// </summary>
    private LevelMove AddLevelMove(List<Candidate> output, CellInfo here, bool hereEdge, int x, int z, int tx, int ty, int tz, CellInfo t,
        bool transit, List<DoorToggle>? opens)
    {
        if (LevelWalkable(here, t, x, z, tx, tz))
        {
            if (transit) AddWalk(output, here, hereEdge, tx, ty, tz, t, MoveKind.Walk, 1.0, [], opens);
            return LevelMove.Walked;
        }
        if (t.Ok && t.Floor - here.Floor <= 1.25 && BodyClear(x, z, t.Floor))
        {
            if (!transit || opens != null) return LevelMove.Skip;
            // Higher partial block (including a farmland bank beside a water trench): needs a jump.
            AddJump(output, here, hereEdge, tx, ty, tz, t, []);
            return LevelMove.Walked;
        }
        return LevelMove.None;
    }

    private bool LevelWalkable(CellInfo here, CellInfo t, int x, int z, int tx, int tz)
    {
        double edge = Math.Max(here.Floor, t.Floor);
        return t.Ok && t.Floor - here.Floor <= _step + 1e-6 && BodyClear(x, z, edge) && BodyClear(tx, tz, edge);
    }

    /// <summary>Jump up one block (or mine the way up when that cell is solid).</summary>
    private void AddJumpUp(List<Candidate> output, CellInfo here, bool hereEdge, int x, int y, int z, int tx, int tz)
    {
        var up = Cell(tx, y + 1, tz);
        if (up.Ok && up.Floor - here.Floor > _step && up.Floor - here.Floor <= 1.25 + 1e-6 && BodyClear(x, z, up.Floor)
            && TransitOk(x, z, tx, tz, y, up.Floor, out var jumpOpens) && jumpOpens == null)
            AddJump(output, here, hereEdge, tx, y + 1, tz, up, []);
        else if (!up.Ok && _o.AllowBreak && !here.Water)
            TryBreakJump(output, here, hereEdge, x, y, z, tx, tz);
    }

    /// <summary>A cardinal neighbour that can't be walked into: open through it, drop down, mine through, bridge over.</summary>
    private void AddBlockedMoves(List<Candidate> output, CellInfo here, bool hereEdge, int x, int y, int z, int dx, int dz, bool targetOk)
    {
        int tx = x + dx, tz = z + dz;
        // A closed fence gate in the target cell, or a closed trapdoor at head height: open it and walk through.
        if (!targetOk && _o.AllowOpenDoors) TryOpenThrough(output, here, hereEdge, x, y, z, tx, tz);
        TryDrop(output, here, x, y, z, dx, dz);
        if (!targetOk && _o.AllowBreak) TryBreakWalk(output, here, hereEdge, x, y, z, tx, tz);
        if (!targetOk && _o.AllowPlace) TryBridge(output, here, x, y, z, tx, tz);
    }

    private void ExpandDiagonals(List<Candidate> output, CellInfo here, bool hereEdge, int x, int y, int z)
    {
        if (!_o.AllowDiagonal) return;
        foreach (var (dx, dz) in Diagonals)
        {
            int tx = x + dx, tz = z + dz;
            var t = Cell(tx, y, tz);
            if (!t.Ok || Math.Abs(t.Floor - here.Floor) > 0.01 || t.Water || t.Climb || here.Climb) continue;
            if (!DiagonalCornersClear(here, x, y, z, tx, tz)) continue;
            AddWalk(output, here, hereEdge, tx, y, tz, t, MoveKind.Diagonal, Math.Sqrt(2), []);
        }
    }

    /// <summary>No corner cutting: both orthogonal cells must be safe, clear floor at the same height, and no door/trapdoor
    /// panel on the corner.</summary>
    private bool DiagonalCornersClear(CellInfo here, int x, int y, int z, int tx, int tz)
    {
        var a = Cell(tx, y, z);
        var b = Cell(x, y, tz);
        if (!a.Ok || !b.Ok || Math.Abs(a.Floor - here.Floor) > 0.01 || Math.Abs(b.Floor - here.Floor) > 0.01) return false;
        if (IsDeadly(tx, y, z) || IsDeadly(x, y, tz)) return false;
        return !(HasPanels(x, z, tx, tz, y, y + 2) || HasPanels(tx, z, x, tz, y, y + 2));
    }

    private void ExpandUp(List<Candidate> output, CellInfo here, int x, int y, int z)
    {
        var above = Cell(x, y + 1, z);
        if (above.Ok && BodyClear(x, z, y + 1)) AddRise(output, here, above, x, y, z);
        else if (_o.AllowOpenDoors && _o.AllowClimb && here.Climb && y + 2 < _worldHeight)
            TryClimbHatch(output, x, y, z);
    }

    private void AddRise(List<Candidate> output, CellInfo here, CellInfo above, int x, int y, int z)
    {
        if (_o.AllowClimb && here.Climb)
            output.Add(new Candidate(x, y + 1, z, MoveKind.ClimbUp, above.Floor, PathOptions.ClimbUpCost, [], null, false));
        else if (_o.AllowSwim && here.Water && (above.Water || above.Climb))
            output.Add(new Candidate(x, y + 1, z, MoveKind.Swim, above.Floor, PathOptions.SwimCost, [], null, false));
    }

    private void ExpandDown(List<Candidate> output, CellInfo here, int x, int y, int z)
    {
        var below = Cell(x, y - 1, z);
        if (!below.Ok || !(Math.Abs(here.Floor - y) < 1e-6)) return;
        if (_o.AllowClimb && below.Climb)
            output.Add(new Candidate(x, y - 1, z, MoveKind.ClimbDown, below.Floor, PathOptions.ClimbDownCost, [], null, false));
        else if (_o.AllowSwim && below.Water && here.Water)
            output.Add(new Candidate(x, y - 1, z, MoveKind.Swim, below.Floor, PathOptions.SwimCost, [], null, false));
    }

    private bool CanPillar(CellInfo here, int x, int y, int z) =>
        _o.AllowPlace && !here.Water && !here.Climb && Math.Abs(here.Floor - y) < 1e-6
        && CanPlaceScaffold(new BlockPos(x, y, z))
        && Aiming.IsReplaceable(_w, _w.GetBlock(x, y, z)) && BodyClear(x, z, y + 1) && y + 2 < _worldHeight;

    private CellInfo WalkCell(int x, int y, int z, double floor, out int ty)
    {
        ty = y;
        var c = Cell(x, y, z);
        if (c.Ok) return c;
        // Foot cells use floor(floor height): a 15/16 top and the neighbouring full block are in different rows.
        // Resolve a normal step by its physical rise, not by matching the integer foot-cell Y.
        for (int sy = y - 1; sy <= y + 1; sy += 2)
        {
            var step = Cell(x, sy, z);
            double rise = step.Floor - floor;
            if (!step.Ok || rise < -0.5 - 1e-6 || rise > _step + 1e-6) continue;
            ty = sy;
            return step;
        }
        return c;
    }

    /// <summary>The node being expanded stands on farmland (set per <see cref="Expand"/>).</summary>
    private bool _hereFarmland;
    /// <summary>The node being expanded is a planted cell of a growing zone, or beside one (<see cref="Careful"/>).</summary>
    private bool _hereCareful;

    /// <summary>Plants a walker in a growing zone sneaks past: sapling, wheat, sugar cane, pumpkin/melon stems, nether wart, cocoa,
    /// carrots, potatoes, beetroot.</summary>
    private static bool IsPlant(int id) => id is 6 or 59 or 83 or 104 or 105 or 115 or 127 or 141 or 142 or 207;

    /// <summary>
    /// Foot cell (x,y,z) is in a growing zone (<see cref="PathOptions.GrowZone"/>) and a plant is in the body's cells there or beside
    /// them: the pawn crouch-walks (Fox 11-04). The zone is only the cheap filter; the live blocks decide, so a harvested or untilled
    /// patch is walked at full speed.
    /// </summary>
    private bool Careful(int x, int y, int z)
    {
        if (_o.GrowZone is not { } zone || !zone(x, y, z)) return false;
        for (int yy = y; yy <= y + 1; yy++)
        {
            if (IsPlant(_w.GetBlock(x, yy, z))) return true;
            foreach (var (dx, dz) in Cardinals)
                if (IsPlant(_w.GetBlock(x + dx, yy, z + dz))) return true;
        }
        return false;
    }

    /// <summary>The goal of the running search (farmland is walked cheaply only near it).</summary>
    private Goal? _goal;

    /// <summary>Cell (x,y,z) is within <see cref="PathOptions.FarmlandGoalRange"/> of the goal: the target is on this field
    /// (planting, harvesting), so walking it is the point of the trip.</summary>
    private bool NearGoal(int x, int y, int z) => _goal is { } g && g.Distance(x, y, z) <= PathOptions.FarmlandGoalRange;

    /// <summary>
    /// Farmland under the feet of foot cell (x,y,z). mc-dev Entity.move calls Block.b(world, x, y, z, entity) on each step of a
    /// non-sneaking walker, and BlockSoil.b reverts farmland to dirt 1 time in 4: walking a field tramples it, sneaking doesn't.
    /// </summary>
    private bool OnFarmland(int x, int y, int z) => y > 0 && (_w.GetBlock(x, y - 1, z) == BlockShapes.Farmland || _w.GetBlock(x, y, z) == BlockShapes.Farmland);

    private void AddWalk(List<Candidate> output, CellInfo here, bool hereEdge, int tx, int ty, int tz, CellInfo t,
        MoveKind kind, double dist, IReadOnlyList<BlockPos> breaks, IReadOnlyList<DoorToggle>? open = null)
    {
        bool targetEdge = IsEdge(tx, ty, tz);
        bool water = here.Water || t.Water;
        double cost = MoveCost(here, t, water, dist);
        bool sneak = (hereEdge || targetEdge) && !water;
        if (sneak) cost *= _o.EdgeCostMultiplier;
        bool gentle = false;
        FieldCare(water, tx, ty, tz, ref sneak, ref gentle, ref cost);
        double time = cost + (breaks.Count > 0 ? DigCosts(breaks) : 0);
        cost += HazardPenalty(tx, ty, tz) + DigCosts(breaks);
        ApplyOpens(ref open, ref cost, ref time, ref kind);
        output.Add(new Candidate(tx, ty, tz, water && kind == MoveKind.Walk ? MoveKind.Swim : kind, t.Floor, cost, breaks, null, sneak, open, Time: time,
            Gentle: gentle));
    }

    /// <summary>A walk's or swim's cost over <paramref name="dist"/>, + 0.5 for a step up.</summary>
    private static double MoveCost(CellInfo here, CellInfo t, bool water, double dist)
    {
        double cost = water ? PathOptions.SwimCost * dist : PathOptions.WalkCost * dist;
        if (t.Floor > here.Floor + 1e-6) cost += 0.5;
        return cost;
    }

    /// <summary>Fields and growing zones: the care a step on or off them takes and what it costs.</summary>
    private void FieldCare(bool water, int tx, int ty, int tz, ref bool sneak, ref bool gentle, ref double cost)
    {
        FarmCare(water, tx, ty, tz, ref sneak, ref gentle, ref cost);
        // Past the plants of a growing zone: the same care (no extra cost beyond a sneak's slower pace).
        if (!water && (_hereCareful || Careful(tx, ty, tz))) TakeCare(ref sneak, ref gentle, ref cost);
    }

    /// <summary>
    /// Across a field: prefer the paths around it. Only a target ON the field (planting, harvesting) makes it worth walking:
    /// everywhere else crossing costs PathOptions.FarmlandWalkPenalty a step (a detour of ~9 blocks). How it is walked is the
    /// version's: b1.8.1 sneaks (a walker tramples), 1.12.2 walks without sprinting (only a landing tramples; Gentle).
    /// </summary>
    private void FarmCare(bool water, int tx, int ty, int tz, ref bool sneak, ref bool gentle, ref double cost)
    {
        bool toFarm = OnFarmland(tx, ty, tz);
        if (!water && (_hereFarmland || toFarm))
        {
            TakeCare(ref sneak, ref gentle, ref cost);
            cost += NearGoal(tx, ty, tz) ? PathOptions.FarmlandPenalty : PathOptions.FarmlandWalkPenalty;
        }
        // Out of the water onto a field (the trench rim): the swimmer jumps out, and a jump lands on the soil.
        else if (water && toFarm && !NearGoal(tx, ty, tz)) cost += PathOptions.FarmlandJumpPenalty;
    }

    /// <summary>Doors and gates opened on the way: their cost and time, and the move becomes the opening kind.</summary>
    private void ApplyOpens(ref IReadOnlyList<DoorToggle>? open, ref double cost, ref double time, ref MoveKind kind)
    {
        if (open is { Count: > 0 })
        {
            cost += OpenCosts(open);
            time += open.Count * 6;
            kind = OpenKind(open);
        }
        else open = null;
    }

    /// <summary>A move over farmland or past a growing zone's plants, the version's way (<see cref="Versioning.IPhysicsRules.WalkingTramplesFarmland"/>):
    /// sneaked (the slower pace costed once), or walked without sprinting.</summary>
    private void TakeCare(ref bool sneak, ref bool gentle, ref double cost)
    {
        if (!_walkTramples) { gentle = true; return; }
        if (!sneak) cost *= PathOptions.SneakFactor;
        sneak = true;
    }

    private void AddJump(List<Candidate> output, CellInfo here, bool hereEdge, int tx, int ty, int tz, CellInfo t,
        IReadOnlyList<BlockPos> breaks)
    {
        bool targetEdge = IsEdge(tx, ty, tz);
        double cost = PathOptions.WalkCost + PathOptions.JumpExtra;
        bool sneak = hereEdge || targetEdge;
        if (sneak) cost *= _o.EdgeCostMultiplier;
        // Jumping onto/off a field: sneak through it like a walk (steps on farmland trample it), and avoid it when possible.
        // Landing on one is what tramples in 1.12 (BlockFarmland.onFallenUpon): costed so high that any walk round wins.
        bool careful = false;
        if (OnFarmland(tx, ty, tz)) { careful = true; cost += PathOptions.FarmlandJumpPenalty; }
        else if (_hereFarmland) { careful = true; cost += PathOptions.FarmlandPenalty * 2; }
        else if (_hereCareful || Careful(tx, ty, tz)) careful = true;
        sneak |= careful && _walkTramples;
        cost += HazardPenalty(tx, ty, tz) + DigCosts(breaks);
        output.Add(new Candidate(tx, ty, tz, MoveKind.Jump, t.Floor, cost, breaks, null, sneak && !here.Water, Gentle: careful && !_walkTramples));
    }

    private bool TryDrop(List<Candidate> output, CellInfo here, int x, int y, int z, int dx, int dz)
    {
        int tx = x + dx, tz = z + dz;
        if (!CanStepOff(here, x, y, z, tx, tz)) return false;
        int maxScan = Math.Max(_o.MaxDrop, _o.AllowSwim ? _o.MaxWaterDrop : 0) + 1;
        for (int d = 1; d <= maxScan && y - d >= 0; d++)
        {
            int ty = y - d;
            var c = Cell(tx, ty, tz);
            if (c.Ok) return TryLanding(output, here, tx, ty, tz, dx, dz, c);
            if (HasCollisionAt(tx, ty, tz) || HazardInColumn(tx, ty, ty, tz)) return false;
        }
        return false;
    }

    /// <summary>The body can step off into the target column at the current height (not hanging on a ladder or vine).</summary>
    private bool CanStepOff(CellInfo here, int x, int y, int z, int tx, int tz)
    {
        if (here.Climb && !here.Ground) return false;
        if (!BodyClear(tx, tz, Math.Max(here.Floor, y))) return false;
        if (!TransitOk(x, z, tx, tz, y, Math.Max(here.Floor, y), out var dropOpens) || dropOpens != null) return false;
        return !HazardInColumn(tx, y, y + 1, tz);
    }

    /// <summary>The drop's landing cell (the first cell down the column): a drop (or a swim into water) when the fall is safe.</summary>
    private bool TryLanding(List<Candidate> output, CellInfo here, int tx, int ty, int tz, int dx, int dz, CellInfo c)
    {
        double fall = here.Floor - c.Floor;
        if (fall <= 0.5) return false; // that's a walk, not a drop
        bool soft = c.Water;
        if (soft && !CheckWaterLanding(here, tx, ty, tz, ref soft, ref fall)) return false;
        if (LandingRefused(c, tx, ty, tz, dx, dz, soft, fall)) return false;
        bool careful = !soft && IsDeadly(tx + dx, ty, tz + dz);
        double cost = DropCost(tx, ty, tz, fall);
        if (careful) cost += 12; // stop, align, then ease onto the single landing cell
        output.Add(new Candidate(tx, ty, tz, c.Water ? MoveKind.Swim : MoveKind.Drop, c.Floor, cost, [], null, false, Gentle: careful));
        return true;
    }

    /// <summary>Water only cancels the fall if a tick samples the feet inside it (Safety.WaterLanding): shallow water under a
    /// long drop is a hard landing on its floor (then no longer soft, and the fall is the one to the floor); false when that
    /// fall is too far.</summary>
    private bool CheckWaterLanding(CellInfo here, int tx, int ty, int tz, ref bool soft, ref double fall)
    {
        int top = ty;
        while (top + 1 < _worldHeight && BlockShapes.IsWater(_w.GetBlock(tx, top + 1, tz))) top++;
        var landing = Safety.WaterLanding(_w, tx, tz, here.Floor, top);
        if (landing.BreaksFall) return true;
        if (landing.FallToFloor > _o.MaxDrop + 1e-6) return false;
        soft = false;
        fall = landing.FallToFloor;
        return true;
    }

    private bool LandingRefused(CellInfo c, int tx, int ty, int tz, int dx, int dz, bool soft, double fall)
    {
        if (!soft && fall > _o.MaxDrop + 1e-6) return true;
        // A fall of more than half a block onto farmland turns it to dirt (1.12 BlockFarmland.onFallenUpon): never planned.
        if (!c.Water && OnFarmland(tx, ty, tz)) return true;
        if (soft && (!_o.AllowSwim || fall > _o.MaxWaterDrop + 1e-6)) return true;
        // Taller drops still need a safe runout. A single full-block descent above a floored pit can instead
        // stop and enter slowly; the follower brakes before leaving support and settles on the landing.
        return !soft && IsDeadly(tx + dx, ty, tz + dz) && !CarefulDrop(c, tx, ty, tz, dx, dz, fall);
    }

    private bool CarefulDrop(CellInfo c, int x, int y, int z, int dx, int dz, double fall) =>
        fall <= 1 + 1e-6 && c.Ground && !c.Water && !c.Climb && Math.Abs(c.Floor - y) < 1e-6
        && BlockShapes.IsFullCube(_w, x, y - 1, z) && BlockShapes.IsFullCube(_w, x - dx, y, z - dz)
        && _w.Physics.Slipperiness(_w.GetBlock(x, y - 1, z)) <= 0.600001
        && _w.Physics.Slipperiness(_w.GetBlock(x - dx, y, z - dz)) <= 0.600001
        && !IsFatalEdge(x, y, z) && !IsFatalEdge(x - dx, y + 1, z - dz);

    private double DropCost(int tx, int ty, int tz, double fall)
    {
        double cost = PathOptions.WalkCost + FallTicks[Math.Min((int)Math.Ceiling(fall), FallTicks.Length - 1)]
                      + HazardPenalty(tx, ty, tz);
        if (IsEdge(tx, ty, tz)) cost *= 1.5;
        // Down into a ditch (a 1-deep trench or hole, dry or a water channel): it takes a jump to get back out.
        if (fall <= 1.25 + 1e-6 && InDitch(tx, ty, tz)) cost += _o.DitchPenalty;
        return cost;
    }

    /// <summary>
    /// Foot cell (x,y,z) is the bottom of a ditch: along x or z there is a bank on both sides within 2 cells (a wall at the feet whose
    /// top is a standable cell), so the body came down a block into it and must jump to get out (Story water's trenches and channels).
    /// </summary>
    private bool InDitch(int x, int y, int z) =>
        (Bank(x, y, z, 1, 0) && Bank(x, y, z, -1, 0)) || (Bank(x, y, z, 0, 1) && Bank(x, y, z, 0, -1));

    private bool Bank(int x, int y, int z, int dx, int dz)
    {
        for (int i = 1; i <= 2; i++)
        {
            int bx = x + dx * i, bz = z + dz * i;
            if (HasCollisionAt(bx, y, bz)) return Cell(bx, y + 1, bz).Ok;
            if (!Cell(bx, y, bz).Ok) return false;
        }
        return false;
    }

    private bool CanPlaceScaffold(BlockPos p)
    {
        // Protect the original field even if an earlier hypothetical edit removed a crop. Never replace crops or cover
        // farmland/crops with scaffold in either of the two cells above them (bridging and pillaring share this veto).
        for (int y = p.Y; y >= Math.Max(0, p.Y - 2); y--)
            if (ScaffoldVeto.Contains(_world.GetBlock(p.X, y, p.Z))) return false;
        // Never on a container's lid or a workstation (a chest under a block won't open: live 11-06, planks on two storage chests).
        if (p.Y > 0 && LidVeto.Contains(_world.GetBlock(p.X, p.Y - 1, p.Z))) return false;
        return _o.CanPlace is null || _o.CanPlace(p);
    }

    /// <summary>Blocks a scaffold never stands on: chests (and trapped, ender), furnaces, the crafting table, dispenser, dropper,
    /// hopper, brewing stand, enchanting table, anvil, shulker boxes.</summary>
    internal static readonly IdSet LidVeto = IdSet.Of([54, 146, 130, 61, 62, 58, 23, 158, 154, 117, 116, 145, 219, 220, 221, 222, 223, 224, 225, 226,
        227, 228, 229, 230, 231, 232, 233, 234]);

    /// <summary>Farmland and crops scaffold never replaces or covers: wheat, cactus, cane, fruit/stems, nether wart, cocoa,
    /// carrots, potatoes, beetroot.</summary>
    private static readonly IdSet ScaffoldVeto = IdSet.Of([BlockShapes.Farmland, 59, 81, 83, 86, 103, 104, 105, 115, 127, 141, 142, 207]);

    private void TryBridge(List<Candidate> output, CellInfo here, int x, int y, int z, int tx, int tz)
    {
        if (!BridgeFrom(here, y)) return;
        int fy = y - 1;
        if (!BridgeTarget(x, y, z, tx, tz, fy)) return;
        // Evaluate the target with the scaffold in place.
        var saved = _w.Mods;
        _w.Mods = new Mod(new BlockPos(tx, fy, tz), _o.ScaffoldBlockId, saved);
        var c = CellUncached(tx, y, tz);
        _w.Mods = saved;
        if (!c.Ok || Math.Abs(c.Floor - y) > 1e-6) return;
        double cost = _o.PlaceCost + PathOptions.WalkCost * PathOptions.SneakFactor + HazardPenalty(tx, y, tz);
        output.Add(new Candidate(tx, y, tz, MoveKind.Bridge, y, cost, [], new BlockPos(tx, fy, tz), true));
    }

    /// <summary>Bridging starts standing on the ground at the cell's own level (not swimming), above the world's floor.</summary>
    private static bool BridgeFrom(CellInfo here, int y) => here.Ground && !here.Water && !(Math.Abs(here.Floor - y) > 1e-6) && y - 1 >= 0;

    /// <summary>The scaffold cell is empty and may take a block, the block we stand on is its placement support, and the body
    /// fits over it with no hazard.</summary>
    private bool BridgeTarget(int x, int y, int z, int tx, int tz, int fy)
    {
        if (!Aiming.IsReplaceable(_w, _w.GetBlock(tx, fy, tz))) return false;
        if (!CanPlaceScaffold(new BlockPos(tx, fy, tz))) return false;
        if (!HasCollisionAt(x, fy, z) || _w.Info(_w.GetBlock(x, fy, z)).Interactable) return false; // block we stand on = placement support
        return BodyClear(tx, tz, y) && !HazardInColumn(tx, y, y + 1, tz);
    }

    private void TryBreakWalk(List<Candidate> output, CellInfo here, bool hereEdge, int x, int y, int z, int tx, int tz)
    {
        if (here.Water) return;
        var breaks = WalkBreaks(tx, y, tz);
        if (breaks == null) return;
        var c = WithBroken(breaks, () => (CellUncached(tx, y, tz), BodyClear(x, z, y) && BodyClear(tx, tz, Math.Max(here.Floor, y))));
        if (!c.Item1.Ok || !c.Item2 || c.Item1.Floor - here.Floor > _step + 1e-6 || c.Item1.Floor < here.Floor - 0.5 - 1e-6) return;
        AddWalk(output, here, hereEdge, tx, y, tz, c.Item1, MoveKind.Walk, 1.0, breaks);
    }

    /// <summary>The blocks to break for a walk into column (tx, tz): head first so nothing falls on us; null when nothing is
    /// in the way or something can't be broken (the list is made only when something is in the way: this is tried for every
    /// neighbour of every node).</summary>
    private List<BlockPos>? WalkBreaks(int tx, int y, int tz)
    {
        List<BlockPos>? breaks = null;
        for (int yy = y + 1; yy >= y; yy--)
        {
            if (!InTheWay(tx, yy, tz)) continue;
            if (!Breakable(tx, yy, tz)) return null;
            (breaks ??= new List<BlockPos>(2)).Add(new BlockPos(tx, yy, tz));
        }
        return breaks;
    }

    /// <summary>A block the body collides with, or a hazard.</summary>
    private bool InTheWay(int x, int y, int z) => HasCollisionAt(x, y, z) || Safety.IsHazard(_w, _w.GetBlock(x, y, z));

    private void TryBreakJump(List<Candidate> output, CellInfo here, bool hereEdge, int x, int y, int z, int tx, int tz)
    {
        if (!here.Ground || Math.Abs(here.Floor - y) > 1e-6) return;
        // Target (tx, y+1, tz) needs support from (tx, y, tz), which must stay.
        if (!HasCollisionAt(tx, y, tz) || !IsFullSupport(tx, y, tz)) return;
        var breaks = JumpBreaks(x, y, z, tx, tz);
        if (breaks == null) return;
        var r = WithBroken(breaks, () => (CellUncached(tx, y + 1, tz), BodyClear(x, z, y + 1)));
        if (!r.Item1.Ok || !r.Item2) return;
        AddJump(output, here, hereEdge, tx, y + 1, tz, r.Item1, breaks);
    }

    /// <summary>The blocks to break for a jump up into (tx, y+1, tz): above my head, above the target and the target's feet
    /// cell, in that order; null when nothing is in the way or something can't be broken.</summary>
    private List<BlockPos>? JumpBreaks(int x, int y, int z, int tx, int tz)
    {
        List<BlockPos>? breaks = null;
        for (int i = 0; i < 3; i++)
        {
            var p = i == 0 ? new BlockPos(x, y + 2, z) : i == 1 ? new BlockPos(tx, y + 2, tz) : new BlockPos(tx, y + 1, tz);
            if (!InTheWay(p.X, p.Y, p.Z)) continue;
            if (!Breakable(p.X, p.Y, p.Z)) return null;
            (breaks ??= new List<BlockPos>(3)).Add(p);
        }
        return breaks;
    }

    private T WithBroken<T>(List<BlockPos> breaks, Func<T> f)
    {
        var saved = _w.Mods;
        foreach (var b in breaks) _w.Mods = new Mod(b, 0, _w.Mods);
        try { return f(); }
        finally { _w.Mods = saved; }
    }

    private bool Breakable(int x, int y, int z)
    {
        int id = _w.GetBlock(x, y, z);
        if (id == 0 || BlockShapes.IsLiquid(id)) return false;
        var info = _w.Info(id);
        if (info.Hardness < 0 || info.Interactable) return false;
        int meta = _w.GetMeta(x, y, z);
        if (_o.CanBreak is { } veto && !veto(id, meta, new BlockPos(x, y, z))) return false;
        // Never open a hole that liquid would flow through (lava = death, water floods the island).
        foreach (var f in BlockFaceExtensions.All)
        {
            var n = f.Normal();
            if (f == BlockFace.Bottom) continue;
            if (BlockShapes.IsLiquid(_w.GetBlock(x + n.X, y + n.Y, z + n.Z))) return false;
        }
        return true;
    }

    private double DigCosts(IReadOnlyList<BlockPos> breaks)
    {
        double c = 0;
        foreach (var b in breaks)
        {
            int id = _w.GetBlock(b.X, b.Y, b.Z), meta = _w.GetMeta(b.X, b.Y, b.Z);
            c += _o.DigCost?.Invoke(id, meta) ?? Math.Max(0, _w.Info(id).Hardness) * 30 + 5;
        }
        return c;
    }

    /// <summary>The start was under the sky (<see cref="PathOptions.SkyLight"/>): cave cells cost more on this search.</summary>
    private bool _fromSurface;

    /// <summary>
    /// What a step into foot cell x,y,z costs on top of its move: flowing water in the body's cells (a stream is crossed, a waterfall
    /// column avoided: live 11-04, pawns stuck in waterfalls), and for a path from the surface, a cell the sky doesn't reach (a cave:
    /// the surface route wins when there is one; live 11-04, pawns walked into caves and couldn't get back out).
    /// </summary>
    private double TerrainPenalty(int x, int y, int z)
    {
        double p = FlowingWaterPenalty(x, y, z);
        if (_fromSurface && _o.SkyLight is { } sky && sky(x, y, z) <= 3 && sky(x, y + 1, z) <= 3) p += _o.UndergroundPenalty;
        if (_o.Covered is { } covered && covered(x, y, z)) p += _o.CoveredPenalty; // a way up: fewer steps under ground win
        return p;
    }

    /// <summary>Flowing water in the body's cells: a falling column (a waterfall) costs the most, a stream less.</summary>
    private double FlowingWaterPenalty(int x, int y, int z)
    {
        double p = 0;
        for (int yy = y; yy <= y + 1; yy++)
        {
            int id = _w.GetBlock(x, yy, z);
            if (id is not (BlockShapes.Water or 9)) continue;
            int meta = _w.GetMeta(x, yy, z);
            if (meta >= 8) { p = Math.Max(p, _o.FallingWaterPenalty); break; }
            if (meta != 0) p = Math.Max(p, _o.FlowingWaterPenalty);
        }
        return p;
    }

    private double HazardPenalty(int x, int y, int z)
    {
        if (Openables.IsPlate(_w.GetBlock(x, y, z))) return PlatePenalty(x, y, z);
        foreach (var (dx, dz) in Cardinals)
            if (Safety.IsHazard(_w, _w.GetBlock(x + dx, y, z + dz)) || Safety.IsHazard(_w, _w.GetBlock(x + dx, y + 1, z + dz)))
                return _o.HazardAdjacentPenalty;
        return Safety.IsHazard(_w, _w.GetBlock(x, y - 1, z)) ? _o.HazardAdjacentPenalty : 0;
    }

    // ------------------------------------------------------------------ cells

    private readonly record struct CellInfo(bool Ok, double Floor, bool Ground, bool Water, bool Climb)
    {
        public static readonly CellInfo Invalid = new(false, 0, false, false, false);
    }

    private CellInfo Cell(int x, int y, int z)
    {
        if (_w.Mods is not null || _w.Pulse is not null) return CellUncached(x, y, z);
        long k = Key(x, y, z);
        if (_cellCache.TryGetValue(k, out var c)) return c;
        c = CellUncached(x, y, z);
        _cellCache[k] = c;
        return c;
    }

    /// <summary>Whether a player can occupy foot cell (x,y,z): supported (or swimming/climbing), clear, no hazards,
    /// not over the void.</summary>
    private CellInfo CellUncached(int x, int y, int z)
    {
        if (y < 0 || y + 1 >= _worldHeight) return CellInfo.Invalid;
        if (!_w.IsLoaded(x, z)) return CellInfo.Invalid;
        int feet = _w.GetBlock(x, y, z), head = _w.GetBlock(x, y + 1, z);
        if (CellBlocked(x, y, z, feet, head)) return CellInfo.Invalid;
        bool water = BlockShapes.IsWater(feet);
        bool climb = BlockShapes.IsClimbableAt(_w, x, y, z);

        double? support = SupportHeight(x, y, z);
        // A closed trapdoor is a floor anyone can open: never stand on one over the void or a deadly drop.
        if (support is { } sh && UnsafeTrapdoor(x, y, z, feet, sh)) return CellInfo.Invalid;
        bool ground = support is not null;
        if (!CanOccupy(ground, water, climb)) return CellInfo.Invalid;
        double floor = support ?? y;
        if (!BodyFits(x, y, z, floor, ground, water)) return CellInfo.Invalid;
        return new CellInfo(true, floor, ground, water, climb);
    }

    /// <summary>A hazard at the feet or head, a plate we may not step on, or (when avoided) a piston's push path.</summary>
    private bool CellBlocked(int x, int y, int z, int feet, int head)
    {
        if (Safety.IsHazard(_w, feet) || Safety.IsHazard(_w, head)) return true;
        if (Openables.IsPlate(feet) && !PlateOk(x, y, z)) return true;
        return _o.AvoidPistonPaths && (InPistonPath(x, y, z) || InPistonPath(x, y + 1, z));
    }

    private bool UnsafeTrapdoor(int x, int y, int z, int feet, double support) =>
        Openables.IsTrapdoor(feet) && !Openables.IsOpen(_w.GetMeta(x, y, z)) && support - y < 0.25 && TrapdoorOverDrop(x, y, z);

    /// <summary>Standing, swimming (when allowed) or climbing (when allowed).</summary>
    private bool CanOccupy(bool ground, bool water, bool climb) => ground || (water && _o.AllowSwim) || (climb && _o.AllowClimb);

    /// <summary>The body fits at <paramref name="floor"/> with no hazard at its head, and a swimmer isn't in water hanging over
    /// the void (it would carry us down).</summary>
    private bool BodyFits(int x, int y, int z, double floor, bool ground, bool water)
    {
        if (!BodyClear(x, z, floor)) return false;
        if (Safety.IsHazard(_w, _w.GetBlock(x, (int)Math.Floor(floor + 1.8), z))) return false;
        return ground || !water || !Safety.IsOverVoid(_w, x, y, z);
    }

    /// <summary>Highest collision top in [y, y+1) under the centered player footprint, from cells y-1 and y.</summary>
    private double? SupportHeight(int x, int y, int z)
    {
        double? best = null;
        var foot = new AABB(x + 0.2, 0, z + 0.2, x + 0.8, 0, z + 0.8);
        for (int yy = y - 1; yy <= y; yy++)
        {
            if (yy < 0) continue;
            int id = _w.GetBlock(x, yy, z);
            if (id == 0) continue;
            _boxes.Clear();
            BlockShapes.AddCollisionBoxes(_w, id, _w.GetMeta(x, yy, z), x, yy, z, _boxes);
            best = HighestUnderFoot(foot, y, best);
        }
        return best;
    }

    /// <summary>The highest top in [y, y+1) of the boxes in <see cref="_boxes"/> that overlap the footprint, or
    /// <paramref name="best"/> when higher.</summary>
    private double? HighestUnderFoot(AABB foot, int y, double? best)
    {
        foreach (var b in _boxes)
        {
            if (b.MaxX <= foot.MinX || b.MinX >= foot.MaxX || b.MaxZ <= foot.MinZ || b.MinZ >= foot.MaxZ) continue;
            if (b.MaxY < y - 1e-9 || b.MaxY >= y + 1 - 1e-9) continue;
            if (best is null || b.MaxY > best) best = b.MaxY;
        }
        return best;
    }

    private bool IsFullSupport(int x, int y, int z) => BlockShapes.CollisionTop(_w, x, y, z) >= 1 - 1e-9;

    /// <summary>A centered player box at feet height <paramref name="floor"/> in column (x,z) collides with nothing.</summary>
    private bool BodyClear(int x, int z, double floor)
    {
        var box = new AABB(x + 0.2, floor, z + 0.2, x + 0.8, floor + _height, z + 0.8);
        BlockShapes.GetCollidingBoxes(_w, box, _boxes);
        return _boxes.Count == 0;
    }

    private bool HasCollisionAt(int x, int y, int z) => BlockShapes.HasCollision(_w, x, y, z);

    private bool HazardInColumn(int x, int y0, int y1, int z)
    {
        for (int y = y0; y <= y1; y++)
            if (Safety.IsHazard(_w, _w.GetBlock(x, y, z))) return true;
        return false;
    }

    /// <summary>Stepping into column (x,z) at foot level y means falling into the void or further than MaxDrop
    /// (without water to land in).</summary>
    private bool IsDeadly(int x, int y, int z)
    {
        bool cache = _w.Mods is null && _w.Pulse is null;
        long k = Key(x, y, z);
        if (cache && _deadlyCache.TryGetValue(k, out bool v)) return v;
        v = ComputeDeadly(x, y, z);
        if (cache) _deadlyCache[k] = v;
        return v;
    }

    private bool ComputeDeadly(int x, int y, int z)
    {
        if (!_w.IsLoaded(x, z)) return true;
        if (HasCollisionAt(x, y, z) || HasCollisionAt(x, y - 1, z)) return false; // wall or floor
        for (int yy = y - 2; yy >= 0; yy--)
        {
            int id = _w.GetBlock(x, yy, z);
            if (BlockShapes.IsWater(id)) return DropIntoWaterDeadly(x, y, z, yy);
            if (Safety.IsHazard(_w, id) && BlockShapes.IsLava(id)) return true;
            if (HasCollisionAt(x, yy, z)) return y - (yy + 1) > _o.MaxDrop;
        }
        return true;
    }

    /// <summary>A drop from foot level <paramref name="y"/> into water topped at <paramref name="waterY"/>: deadly over the void,
    /// or when the water doesn't break the fall and its floor is further down than the max drop.</summary>
    private bool DropIntoWaterDeadly(int x, int y, int z, int waterY)
    {
        if (Safety.IsOverVoid(_w, x, waterY, z)) return true;
        var landing = Safety.WaterLanding(_w, x, z, y, waterY);
        return !landing.BreaksFall && landing.FallToFloor > _o.MaxDrop + 1e-6;
    }

    /// <summary>Any of the 8 neighbours at this level is a deadly drop.</summary>
    private bool IsEdge(int x, int y, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            if ((dx != 0 || dz != 0) && IsDeadly(x + dx, y, z + dz)) return true;
        return false;
    }

    /// <summary>Next to a deadly drop that is also fatal (the void, lava, water over the void: <see cref="Safety.IsFatalColumn"/>).</summary>
    private bool IsFatalEdge(int x, int y, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            if ((dx != 0 || dz != 0) && IsDeadly(x + dx, y, z + dz) && Safety.IsFatalColumn(_w, x + dx, y, z + dz)) return true;
        return false;
    }

    /// <summary>
    /// The path as walked: a step sneaked only for its edge (no field, no plants) beside a pit with a floor is walked normally (live
    /// 11-04: "they crouch-walk when the stairs get high on the outside wall" of a quarry). Edges still cost what they did, so routes
    /// don't change; the void and lava stay sneaked, and the edge guard's simulation still never lets the body step off.
    /// </summary>
    private PathResult WalkPits(PathResult r, BlockPos start)
    {
        if (r.Steps.Count == 0) return r;
        var steps = r.Steps.ToList();
        for (int i = 0; i < steps.Count; i++)
        {
            var prev = i == 0 ? start : steps[i - 1].Pos;
            if (steps[i].Sneak && PitEdgeOnly(prev) && PitEdgeOnly(steps[i].Pos)) steps[i] = steps[i] with { Sneak = false };
        }
        return r with { Steps = steps };
    }

    /// <summary>Sneaking at this cell would only be for a pit with a floor: no fatal edge, no field, no growing zone's plants.</summary>
    private bool PitEdgeOnly(BlockPos c) =>
        !IsFatalEdge(c.X, c.Y, c.Z) && !OnFarmland(c.X, c.Y, c.Z) && !Careful(c.X, c.Y, c.Z);

    private static double[] BuildFallTicks()
    {
        var t = new double[64];
        double v = 0, fallen = 0;
        int ticks = 0;
        for (int d = 1; d < t.Length; d++)
        {
            while (fallen < d)
            {
                v = (v - PlayerPhysics.Gravity) * PlayerPhysics.VerticalDrag;
                fallen -= v;
                ticks++;
            }
            t[d] = ticks;
        }
        return t;
    }

    // ------------------------------------------------------------------ overlay

    // ------------------------------------------------------------------ doors, gates, trapdoors

    private static bool IsPanel(int id) => Openables.IsDoor(id) || Openables.IsTrapdoor(id);

    /// <summary>Any door/trapdoor in columns (x,z) or (tx,tz) between y0 and y1.</summary>
    private bool HasPanels(int x, int z, int tx, int tz, int y0, int y1)
    {
        for (int yy = Math.Max(0, y0); yy <= Math.Min(_worldHeight - 1, y1); yy++)
            if (IsPanel(_w.GetBlock(x, yy, z)) || IsPanel(_w.GetBlock(tx, yy, tz))) return true;
        return false;
    }

    private static AABB Sweep(int x, int z, int tx, int tz, double floor) =>
        new(Math.Min(x, tx) + 0.2, floor, Math.Min(z, tz) + 0.2, Math.Max(x, tx) + 0.8, floor + PlayerPhysics.Height, Math.Max(z, tz) + 0.8);

    /// <summary>
    /// Whether the body can move from column (x,z) to (tx,tz) at <paramref name="floor"/> past door/trapdoor panels.
    /// True + null toggles: nothing in the way. True + toggles: passable after flipping them (AllowOpenDoors only).
    /// False: a closed panel blocks and can't (or may not) be opened.
    /// </summary>
    private bool TransitOk(int x, int z, int tx, int tz, int y, double floor, out List<DoorToggle>? toggles)
    {
        toggles = null;
        if (!HasPanels(x, z, tx, tz, y - 1, y + 2)) return true;
        var sweep = Sweep(x, z, tx, tz, floor);
        BlockShapes.GetCollidingBoxes(_w, sweep, _boxes);
        if (_boxes.Count == 0) return true;
        if (!_o.AllowOpenDoors) return false;

        var list = PanelToggles(x, z, tx, tz, y, sweep);
        if (list is null || list.Count == 0) return false; // a panel that can't be opened, or something other than a panel in the gap
        bool clear = WithToggles(list, () => BlockShapes.GetCollidingBoxes(_w, sweep, _boxes).Count == 0);
        if (!clear) return false;
        toggles = list;
        return true;
    }

    /// <summary>The toggles of every panel in either column that the sweep hits (each once); null when one can't be opened.</summary>
    private List<DoorToggle>? PanelToggles(int x, int z, int tx, int tz, int y, AABB sweep)
    {
        var hereCell = new BlockPos(x, y, z);
        var list = new List<DoorToggle>(2);
        var tmp = new List<AABB>(2);
        foreach (var (cx, cz) in new[] { (x, z), (tx, tz) })
        for (int yy = Math.Max(0, y - 1); yy <= Math.Min(_worldHeight - 1, y + 2); yy++)
        {
            if (!PanelInSweep(cx, yy, cz, sweep, tmp)) continue;
            var tg = MakeToggle(new BlockPos(cx, yy, cz), hereCell, new BlockPos(tx, y, tz));
            if (tg is null) return null;
            if (!list.Any(o => o.Pos == tg.Pos)) list.Add(tg);
        }
        return list;
    }

    /// <summary>A door or trapdoor panel at (x, y, z) whose collision boxes the sweep hits.</summary>
    private bool PanelInSweep(int x, int y, int z, AABB sweep, List<AABB> tmp)
    {
        int id = _w.GetBlock(x, y, z);
        if (!IsPanel(id)) return false;
        tmp.Clear();
        BlockShapes.AddCollisionBoxes(_w, id, _w.GetMeta(x, y, z), x, y, z, tmp);
        return tmp.Any(b => b.Intersects(sweep));
    }

    /// <summary>The toggle that flips the openable at <paramref name="p"/>, or null when it can't be opened from
    /// <paramref name="from"/> (iron door without a known actuator on the near side, vetoed, not an openable).</summary>
    private DoorToggle? MakeToggle(BlockPos p, BlockPos from, BlockPos to)
    {
        var s = Openables.StateBlock(_w, p);
        int id = _w.GetBlock(s.X, s.Y, s.Z), meta = _w.GetMeta(s.X, s.Y, s.Z);
        if (!Openables.IsOpenable(id)) return null;
        if (_o.CanOpen is { } veto && !veto(s, id)) return null;
        if (Openables.OpensByHand(id)) return new DoorToggle(s, id, meta);
        if (id != Openables.IronDoor || _banned.Contains(s)) return null;
        // Known triggers of an iron door work through their Trigger steps (timing-aware); the near-side heuristic only
        // considers actuators we know nothing about.
        return FindActuator(s, from, to) is { } a ? new DoorToggle(s, id, meta, a) : null;
    }

    /// <summary>A pressure plate in the near-side cell, else a lever/button within reach of the near-side cell and
    /// closer to it than to the far side. Iron doors with neither are obstacles.</summary>
    private BlockPos? FindActuator(BlockPos door, BlockPos from, BlockPos to)
    {
        // Safety: never fire an unknown trigger next to TNT/dispensers.
        if (Openables.IsPlate(_w.GetBlock(from.X, from.Y, from.Z)) && _view?.ForTrigger(from) is null)
            return Explosives(from) ? null : from;
        var eye = new Vec3d(from.X + 0.5, from.Y + PlayerPhysics.EyeHeight, from.Z + 0.5);
        BlockPos? best = null;
        double bestD = double.MaxValue;
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        for (int dy = -1; dy <= 2; dy++)
        {
            var ap = new BlockPos(door.X + dx, door.Y + dy, door.Z + dz);
            if (!UnknownActuator(ap, eye, from, to, out double d)) continue;
            if (d < bestD) { bestD = d; best = ap; }
        }
        return best;
    }

    /// <summary>A lever or button we know nothing about at <paramref name="ap"/>, within reach of the near side's eye and nearer
    /// the near side than the far side, with no explosives around (and no unknown lever in someone's build unless allowed);
    /// <paramref name="d"/> is its distance from the eye.</summary>
    private bool UnknownActuator(BlockPos ap, Vec3d eye, BlockPos from, BlockPos to, out double d)
    {
        d = 0;
        int ax = ap.X, ay = ap.Y, az = ap.Z;
        int id = _w.GetBlock(ax, ay, az);
        if (id is not (Openables.Lever or Openables.StoneButton)) return false;
        double ex = ax + 0.5 - eye.X, ey = ay + 0.5 - eye.Y, ez = az + 0.5 - eye.Z;
        d = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        double dFrom = Math.Pow(ax - from.X, 2) + Math.Pow(az - from.Z, 2);
        double dTo = Math.Pow(ax - to.X, 2) + Math.Pow(az - to.Z, 2);
        if (d > _o.ActuatorReach || dFrom >= dTo) return false;
        if (_view?.ForTrigger(ap) is not null || Explosives(ap)) return false;
        // An unknown lever inside someone's build might run anything: only levers we learned (or were allowed).
        return !(id == Openables.Lever && !_o.AllowUnknownLevers && _o.IsBuild is { } build && build(ap, id));
    }

    private Mod? ToggleMods(DoorToggle tg, Mod? mods)
    {
        var saved = _w.Mods;
        _w.Mods = mods;
        try
        {
            var upper = new BlockPos(tg.Pos.X, tg.Pos.Y + 1, tg.Pos.Z);
            bool hasUpper = Openables.IsDoor(tg.Id) && _w.GetBlock(upper.X, upper.Y, upper.Z) == tg.Id;
            int upperMeta = hasUpper ? _w.GetMeta(upper.X, upper.Y, upper.Z) : 0;
            mods = new Mod(tg.Pos, tg.Id, mods, Openables.Toggled(tg.FromMeta));
            if (hasUpper) mods = new Mod(upper, tg.Id, mods, Openables.Toggled(upperMeta));
            return mods;
        }
        finally { _w.Mods = saved; }
    }

    private T WithToggles<T>(IReadOnlyList<DoorToggle> toggles, Func<T> f)
    {
        var saved = _w.Mods;
        var mods = saved;
        foreach (var tg in toggles) mods = ToggleMods(tg, mods);
        _w.Mods = mods;
        try { return f(); }
        finally { _w.Mods = saved; }
    }

    private double OpenCosts(IReadOnlyList<DoorToggle> open) =>
        open.Sum(o => _o.OpenCost + (o.Actuator is null ? 0 : _o.ActuatorCost));

    private static MoveKind OpenKind(IReadOnlyList<DoorToggle> open) =>
        Openables.IsFenceGate(open[0].Id) ? MoveKind.OpenGate
        : Openables.IsTrapdoor(open[0].Id) ? MoveKind.OpenTrapdoor
        : MoveKind.OpenDoor;

    /// <summary>Closed gate in the target cell (feet or head) or closed trapdoor at head height: open, then walk.</summary>
    private void TryOpenThrough(List<Candidate> output, CellInfo here, bool hereEdge, int x, int y, int z, int tx, int tz)
    {
        if (here.Water) return;
        var list = ThroughToggles(x, y, z, tx, tz);
        if (list is null || list.Count == 0) return;
        var r = WithToggles(list, () =>
        {
            var c = CellUncached(tx, y, tz);
            if (!c.Ok || c.Floor - here.Floor > _step + 1e-6 || c.Floor < here.Floor - 0.5 - 1e-6) return (c, false);
            double f = Math.Max(here.Floor, c.Floor);
            bool ok = BodyClear(x, z, f) && TransitOk(x, z, tx, tz, y, f, out var more) && more == null;
            return (c, ok);
        });
        if (!r.Item2) return;
        AddWalk(output, here, hereEdge, tx, y, tz, r.Item1, MoveKind.Walk, 1.0, [], list);
    }

    /// <summary>The toggles for the closed gate (feet or head) and the closed trapdoor at head height in the target column;
    /// null when one of them can't be opened.</summary>
    private List<DoorToggle>? ThroughToggles(int x, int y, int z, int tx, int tz)
    {
        var list = new List<DoorToggle>(2);
        for (int yy = y; yy <= y + 1; yy++)
        {
            if (!ClosedInTheWay(tx, yy, tz, y)) continue;
            var tg = MakeToggle(new BlockPos(tx, yy, tz), new BlockPos(x, y, z), new BlockPos(tx, y, tz));
            if (tg is null) return null;
            list.Add(tg);
        }
        return list;
    }

    private bool ClosedInTheWay(int tx, int yy, int tz, int y)
    {
        int id = _w.GetBlock(tx, yy, tz);
        bool closed = !Openables.IsOpen(_w.GetMeta(tx, yy, tz));
        bool gate = Openables.IsFenceGate(id) && closed;
        bool headTrapdoor = id == Openables.Trapdoor && yy == y + 1 && closed;
        return gate || headTrapdoor;
    }

    /// <summary>Ladder up into a hatch: a closed trapdoor at the head of the cell above.</summary>
    private void TryClimbHatch(List<Candidate> output, int x, int y, int z)
    {
        int id = _w.GetBlock(x, y + 2, z);
        if (id != Openables.Trapdoor || Openables.IsOpen(_w.GetMeta(x, y + 2, z))) return;
        // Hinged on the ladder's own wall, the opened panel hangs exactly where the climber's head goes (the body presses
        // into that wall to climb): impassable in b1.8.1 as well.
        if (HatchHingeOnLadderWall(_w.GetMeta(x, y + 2, z), _w.GetBlock(x, y + 1, z) == BlockShapes.Ladder ? _w.GetMeta(x, y + 1, z) : _w.GetMeta(x, y, z))) return;
        var tg = MakeToggle(new BlockPos(x, y + 2, z), new BlockPos(x, y, z), new BlockPos(x, y + 1, z));
        if (tg is null) return;
        var r = WithToggles([tg], () => (CellUncached(x, y + 1, z), BodyClear(x, z, y + 1)));
        if (!r.Item1.Ok || !r.Item2) return;
        output.Add(new Candidate(x, y + 1, z, MoveKind.ClimbUp, r.Item1.Floor, PathOptions.ClimbUpCost + OpenCosts([tg]), [], null, false, [tg]));
    }

    /// <summary>Trapdoor hinge (meta bits 0-1: 0 z+1, 1 z−1, 2 x+1, 3 x−1) on the same side as the ladder's wall
    /// (ladder meta 2 z+1, 3 z−1, 4 x+1, 5 x−1).</summary>
    private static bool HatchHingeOnLadderWall(int trapMeta, int ladderMeta) => (trapMeta & 3) switch
    {
        0 => ladderMeta == 2, 1 => ladderMeta == 3, 2 => ladderMeta == 4, _ => ladderMeta == 5,
    };

    private bool TrapdoorOverDrop(int x, int y, int z)
    {
        int below = Safety.SupportBelow(_w, x, y - 1, z);
        return below < 0 || y - (below + 1) > _o.MaxDrop || BlockShapes.IsLava(_w.GetBlock(x, below + 1, z));
    }

    /// <summary>Persistent list of hypothetical block changes along a path (breaks → air, places → scaffold,
    /// toggles → same id with the open bit flipped).</summary>
    private sealed record Mod(BlockPos Pos, int Id, Mod? Next, int Meta = 0);

    private sealed class Overlay(IBlockAccess inner) : IBlockAccess
    {
        public Mod? Mods;
        /// <summary>Effects of the active button/plate pulse (on top of <see cref="Mods"/>).</summary>
        public Mod? Pulse;

        public int GetBlock(int x, int y, int z)
        {
            for (var m = Pulse; m is not null; m = m.Next)
                if (m.Pos.X == x && m.Pos.Y == y && m.Pos.Z == z) return m.Id;
            for (var m = Mods; m is not null; m = m.Next)
                if (m.Pos.X == x && m.Pos.Y == y && m.Pos.Z == z) return m.Id;
            return inner.GetBlock(x, y, z);
        }

        public int GetMeta(int x, int y, int z)
        {
            for (var m = Pulse; m is not null; m = m.Next)
                if (m.Pos.X == x && m.Pos.Y == y && m.Pos.Z == z) return m.Meta;
            for (var m = Mods; m is not null; m = m.Next)
                if (m.Pos.X == x && m.Pos.Y == y && m.Pos.Z == z) return m.Meta;
            return inner.GetMeta(x, y, z);
        }

        public bool IsLoaded(int x, int z) => inner.IsLoaded(x, z);
        public MineClaw.Data.BlockInfo Info(int id) => inner.Info(id);
        public int WorldHeight => inner.WorldHeight;
        public Versioning.IPhysicsRules Physics => inner.Physics;
    }
}
