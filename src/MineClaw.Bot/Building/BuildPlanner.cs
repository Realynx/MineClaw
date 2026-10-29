using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>Planner knobs (zero tokens: everything here is C#).</summary>
public sealed record BuildPlannerOptions
{
    /// <summary>The client's reach from the eyes (the server allows feet within 8 of the clicked block).</summary>
    public double Reach { get; init; } = 4.5;
    /// <summary>Longest temporary scaffold chain to give an unsupported cell a neighbour (building down: hanging chains).</summary>
    public int MaxScaffold { get; init; } = 10;
    /// <summary>Click faces the bot can't see (verified: b1.8.1 checks distance only; 1.12.2's processTryUseItemOnBlock too).</summary>
    public bool AllowHidden { get; init; } = true;
    /// <summary>The version's slab/stair halves follow the click (1.12.2; <c>IGameData.HalvesFollowCursor</c>): a bottom half is
    /// never planned from an underside click (that places a top half), nor a top half from a top-face click.</summary>
    public bool HalvesFollowCursor { get; init; }
    /// <summary>A water bucket is on hand (or the acquisition can get one): water-column descents are possible.</summary>
    public bool WaterAvailable { get; init; }
    /// <summary>Confirmation gate: ask after this many placements (0 = off).</summary>
    public int GateBlocks { get; init; } = 5;
    public int FloodNodes { get; init; } = 6000;
    /// <summary>Ready blocks evaluated per step (nearest first).</summary>
    public int Candidates { get; init; } = 8;
    public Func<BlockPos, int, bool>? IsProtected { get; init; }
    public double PlaceTicks { get; init; } = 8;
    /// <summary>Held tool for dig-time estimates (stone pickaxe).</summary>
    public int DigTool { get; init; } = Items.StonePickaxe;
    /// <summary>Cells to leave exactly as they are (someone else built there): neither cleared nor planned.</summary>
    public IReadOnlySet<BlockPos> Keep { get; init; } = new HashSet<BlockPos>();
    /// <summary>Items not on hand (a shortfall): their blocks wait, and nothing else is planned to rest on them.</summary>
    public IReadOnlySet<(int Item, int Damage)> Hold { get; init; } = new HashSet<(int, int)>();
    /// <summary>Water buckets on hand: how many columns may stand at once (one bucket = one column at a time).</summary>
    public int Buckets { get; init; } = 1;
    /// <summary>Climbing when a block is above reach: the design's own blocks as stands (always), then a pillar (jump-place under
    /// myself), then a scaffold staircase; the temporary scaffold comes down top-first at the end. Off: report "above my reach".</summary>
    public bool Climb { get; init; } = true;
    /// <summary>A crew plan (several bodies on one plan): climb only by scaffold staircases, which every lane can walk (a pillar
    /// top holds one body that got there by pillaring); one lane builds the stair, the others wait for it.</summary>
    public bool SharedClimb { get; init; }
    /// <summary>Highest pillar per climb (blocks).</summary>
    public int MaxPillar { get; init; } = 16;
}

/// <summary>
/// The build slicer (like a 3D printer's): splits a blueprint's voxels into placement steps and orders them so each one can
/// really be done. Every block needs a solid neighbour to click (the world, an earlier step, or a planned scaffold block); the
/// order is support-first and bottom-up, nearest by real path cost (the <see cref="Pathfinder"/> flood from where the previous
/// step ends, doors and void rules included), never walling the body in, the first few placements showing the build's
/// position and direction (the confirmation gate), directional blocks placed with the yaw their meta needs. Each step gets a
/// stand, a clicked face and a technique; a step with no stand gets a scaffold chain, a floor walkway or a water column
/// (building down); what still can't be done is reported as infeasible with the reason before anything is built.
/// </summary>
public sealed partial class BuildPlanner
{
    private readonly VoxelPlan _plan;
    private readonly BuildPart _part;
    private readonly BuildPlannerOptions _o;
    private readonly PathOptions _path = new() { AllowOpenDoors = true, AllowParkour = false, MaxDrop = 3 };
    private St _s;
    private bool _escapeCheck;
    private readonly BuildPlan _out;

    /// <summary>Mutable planning state (cloned to try a technique and roll back).</summary>
    private sealed class St
    {
        public required PlanWorld Sim;
        public required Dictionary<BlockPos, double> Flood;
        public BlockPos Land;          // where the body is (or exits to, from a column)
        public int Col = -1;           // column the body is in
        public int ColY;               // its hover feet y there
        public List<PlanStep> Steps = new();
        public List<PlanColumn> Columns = new();
        public Dictionary<BlockPos, int> PlacedBy = new();
        public List<BlockPos> Scaffold = new();
        public HashSet<BlockPos> Drained = new(); // column cells cut off by a placement
        public HashSet<BlockPos> Pillar = new();  // scaffold cells placed by pillaring (dug back down from on top)
        public HashSet<BlockPos> Stair = new();   // scaffold stair treads and their supports
        public int Placements;

        public St Clone() => new()
        {
            Sim = Sim.Fork(), Flood = new(Flood), Land = Land, Col = Col, ColY = ColY, Steps = Steps.Select(Copy).ToList(),
            Columns = Columns.Select(c => new PlanColumn
            {
                Index = c.Index, Source = c.Source, Face = c.Face, Entry = c.Entry, Depth = c.Depth, DescendTicks = c.DescendTicks,
                AscendTicks = c.AscendTicks, Poured = c.Poured, Removed = c.Removed,
            }).ToList(),
            PlacedBy = new(PlacedBy), Scaffold = new(Scaffold), Drained = new(Drained), Pillar = new(Pillar), Stair = new(Stair), Placements = Placements,
        };
    }

    private static PlanStep Copy(PlanStep s) => new()
    {
        Index = s.Index, Action = s.Action, Technique = s.Technique, Target = s.Target, Id = s.Id, Meta = s.Meta, Item = s.Item,
        Damage = s.Damage, Stand = s.Stand, Face = s.Face, HiddenFace = s.HiddenFace, Yaw = s.Yaw, Column = s.Column, Deps = new(s.Deps),
        Part = s.Part, Lane = s.Lane, Gate = s.Gate, Status = s.Status, Note = s.Note, Cost = s.Cost,
    };

    private BuildPlanner(VoxelPlan plan, BuildPart part, BuildPlannerOptions o, PlanWorld sim, BlockPos start, BuildPlan? previous = null)
    {
        _plan = plan;
        _part = part;
        _o = o;
        _s = new St { Sim = sim, Flood = new(), Land = start };
        _home = HomeFor(plan, start, previous);
        _out = new BuildPlan
        {
            Part = part.Index, Parts = part.Count, Version = plan.Hash(), Format = BuildPlan.CurrentFormat, Origin = plan.Origin, Facing = plan.Blueprint.Facing,
            Gate = new GateInfo { After = o.GateBlocks, State = o.GateBlocks > 0 ? GateState.Pending : GateState.Off },
            Home = _home,
        };
    }

    /// <summary>
    /// The level "a way down" leads back to: the first slicing's start (<see cref="BuildPlan.Home"/>, carried); without one, the
    /// start, unless the body is up above the design's bottom layer (a resume or a crew finale sliced from the platform): then
    /// the design's bottom layer under it, so the climb's scaffold is never "safe" to take down from above.
    /// </summary>
    internal static BlockPos HomeFor(VoxelPlan plan, BlockPos start, BuildPlan? previous)
    {
        if (previous?.Home is { } h) return h;
        return start.Y > plan.Min.Y + 1 ? new BlockPos(start.X, plan.Min.Y, start.Z) : start;
    }

    /// <summary>
    /// Slices <paramref name="plan"/> (one <paramref name="part"/>) against <paramref name="world"/> from the body's foot cell
    /// <paramref name="start"/>. With <paramref name="previous"/> (resume, deviation re-plan) its finished steps that still hold
    /// in the world are kept as done history, its gate, placed-block ledger, poured columns and scaffolding carry over, and
    /// only what remains is planned again from the current state.
    /// </summary>
    public static BuildPlan Plan(VoxelPlan plan, IBlockAccess world, BlockPos start, BuildPart? part = null, BuildPlannerOptions? options = null,
        BuildPlan? previous = null)
    {
        var p = new BuildPlanner(plan, part ?? BuildPart.Whole(plan), options ?? new BuildPlannerOptions(), new PlanWorld(world), start, previous);
        var bp = p.Run(previous);
        // Mostly hanging over the void past the scaffold limit: name the floor that would carry it (the design's own footprint).
        bp.Foundation = BuildSkips.FloorHint(bp.Infeasible.Select(i => (i.Pos, (string?)i.Why)).ToList(), BuildSkips.Footprint(plan.Blocks.Keys), world);
        return bp;
    }

    // ------------------------------------------------------------------ the run

    private BuildPlan Run(BuildPlan? previous)
    {
        if (previous != null) Carry(previous);
        _s.Flood = Flood(_s.Sim, _s.Land);
        _escapeCheck = CanLeave(_s.Sim, _s.Land, _outside);
        if (_s.Flood.Count == 0)
            foreach (var b in Todo()) Fail(b.Pos, b.Id, $"I'm not standing anywhere walkable ({_s.Land.X},{_s.Land.Y},{_s.Land.Z})");
        else
        {
            ClearObstructions();
            PlaceAll();
            BlameSupports();
            ClearUnderfoot();
        }
        RemoveScaffolding();
        PickupColumns();
        _out.Steps = _s.Steps;
        _out.Columns = _s.Columns;
        for (int i = 0; i < _out.Steps.Count; i++) _out.Steps[i].Index = i;
        // Only what still stands of the last run's scaffold (a cell dug since, by a clear or by hand, would keep the build from
        // ever being done).
        _out.ScaffoldUp = _scaffoldStanding;
        return _out;
    }

    /// <summary>Keeps the finished steps whose effect still stands, the gate, the ledger and live columns/scaffold.</summary>
    private void Carry(BuildPlan prev)
    {
        _out.BlueprintId = prev.BlueprintId;
        _out.Gate = prev.Gate;
        _out.Placed = prev.Placed.Distinct().ToList();
        _out.Transform = prev.Transform;
        _out.Replans = prev.Replans + 1;
        foreach (var st in prev.Steps.Where(s => s.Finished && Holds(s)))
        {
            var c = Copy(st);
            c.Status = StepStatus.Verified;
            c.Deps.Clear();
            c.Column = -1;
            _s.Steps.Add(c);
            if (c.IsPlacement) { _s.PlacedBy[c.Target] = _s.Steps.Count - 1; _s.Placements++; }
        }
        // Water still poured: keep the column (its stands and its pickup).
        foreach (var col in prev.Columns.Where(c => c.Poured && !c.Removed && BlockShapes.IsWater(_s.Sim.Get(c.Source)) && _s.Sim.GetMeta(c.Source.X, c.Source.Y, c.Source.Z) == 0))
        {
            var k = new PlanColumn
            {
                Index = _s.Columns.Count, Source = col.Source, Face = col.Face, Entry = col.Entry, Depth = col.Depth,
                DescendTicks = col.DescendTicks, AscendTicks = col.AscendTicks, Poured = true,
            };
            _s.Columns.Add(k);
        }
        // Scaffold left standing comes down at the end.
        // A way down a run left (ExitRoute's stair treads) is scaffold again for this run: the climb, taken down last keeping a way down.
        _scaffoldStanding = prev.ScaffoldUp.Concat(prev.Exit).Where(p => BuildBlocks.Supports(_s.Sim.Get(p))).Distinct().ToList();
        foreach (var p in _scaffoldStanding.Where(p => !_plan.Blocks.ContainsKey(p))) _s.Scaffold.Add(p);
        // A pillar still standing is dug back down from on top (its steps say which scaffold was pillared).
        foreach (var st in prev.Steps.Where(s => s.Action == StepAction.Scaffold && s.Technique == Technique.Pillar && _s.Scaffold.Contains(s.Target))) _s.Pillar.Add(st.Target);
    }

    private List<BlockPos> _scaffoldStanding = new();

    private bool Holds(PlanStep s) => s.Action switch
    {
        StepAction.Place => _plan.Blocks.TryGetValue(s.Target, out var want) && DesignChecks.SameState(want, _s.Sim.Get(s.Target), _s.Sim.GetMeta(s.Target.X, s.Target.Y, s.Target.Z)),
        StepAction.Scaffold => BuildBlocks.Supports(_s.Sim.Get(s.Target)),
        StepAction.Clear or StepAction.RemoveScaffold => !BuildBlocks.Supports(_s.Sim.Get(s.Target)),
        StepAction.PourWater => BlockShapes.IsWater(_s.Sim.Get(s.Target)),
        StepAction.PickupWater => !BlockShapes.IsWater(_s.Sim.Get(s.Target)),
        _ => false,
    };

    private List<PlannedBlock> Todo() => _plan.Blocks.Values.Where(b => _part.Cells.Contains(b.Pos) && !b.UpperDoor && !_o.Keep.Contains(b.Pos)
        && !_o.Hold.Contains(BuildBlocks.ItemFor(b.Id, b.Meta))
        && !(_s.Sim.IsLoaded(b.Pos.X, b.Pos.Z) && DesignChecks.SameState(b, _s.Sim.Get(b.Pos), _s.Sim.GetMeta(b.Pos.X, b.Pos.Y, b.Pos.Z)))).ToList();

    // ------------------------------------------------------------------ obstructions

    /// <summary>Obstructions the body stood on when they came up (no stand digs them from beside): cleared after the placements,
    /// from wherever the body has got to by then.</summary>
    private readonly List<BlockPos> _underfoot = new();

    private void ClearObstructions()
    {
        foreach (var p in _part.Cells.Where(c => _s.Sim.IsLoaded(c.X, c.Z)).OrderByDescending(c => c.Y))
        {
            int id = _s.Sim.Get(p);
            if (id == 0 || BlockShapes.IsLiquid(id) || _o.Keep.Contains(p)) continue;
            if (_plan.Blocks.TryGetValue(p, out var want) && DesignChecks.SameState(want, id, _s.Sim.GetMeta(p.X, p.Y, p.Z))) continue;
            // A cell whose block is held back (shortfall) keeps what stands there until the block can be placed.
            if (_plan.Blocks.TryGetValue(p, out var held) && _o.Hold.Contains(BuildBlocks.ItemFor(held.Id, held.Meta))) continue;
            // My own scaffold still up in one of the design's air cells (a pillar in the shaft from the last run): it is the
            // climb, not an obstruction; it comes down top-first at the end with a way down (live 10-28: the drop tower's
            // re-plan dug its own pillar from under the body first and stranded it in the shaft: "won't dig the cobblestone I'm
            // standing on").
            if (_s.Scaffold.Contains(p)) continue;
            var dig = BestDig(p);
            if (dig == null && StandsOn(_s.Land, p)) { _underfoot.Add(p); continue; }
            Add(new PlanStep
            {
                Action = StepAction.Clear, Target = p, Id = id, Stand = dig?.Stand ?? _s.Land, Column = dig?.Column ?? -1,
                Technique = dig is { Column: >= 0 } ? Technique.Water : Technique.Normal, Cost = (dig?.Cost ?? 0) + DigTicks(id, false),
                Deps = dig is { Column: >= 0 } d0 ? [PourStepOf(d0.Column)] : [],
            });
            _s.Sim.Set(p, 0);
            if (dig is { } d) MoveTo(d.Stand, d.Column);
        }
        _s.Flood = Flood(_s.Sim, _s.Land);
    }

    /// <summary>The body standing in <paramref name="feet"/> is held up by <paramref name="p"/>.</summary>
    private bool StandsOn(BlockPos feet, BlockPos p)
    {
        if (p == feet.Down()) return true;
        var eye = GoalUseBlock.EyeAt(_s.Sim, feet.X, feet.Y, feet.Z);
        return Safety.SupportBlocks(_s.Sim, eye with { Y = eye.Y - PlayerPhysics.EyeHeight }).Contains(p);
    }

    /// <summary>The obstructions put off because the body stood on them: from a stand beside them now, else dug from wherever
    /// the client can aim (never from on top of them: the client refuses that over a drop).</summary>
    private void ClearUnderfoot()
    {
        foreach (var p in _underfoot.OrderByDescending(c => c.Y))
        {
            int id = _s.Sim.Get(p);
            if (id == 0 || BlockShapes.IsLiquid(id)) continue;
            var dig = BestDig(p);
            Add(new PlanStep
            {
                Action = StepAction.Clear, Target = p, Id = id, Stand = dig?.Stand ?? _s.Land, Column = dig?.Column ?? -1,
                Technique = dig is { Column: >= 0 } ? Technique.Water : Technique.Normal, Cost = (dig?.Cost ?? 0) + DigTicks(id, false),
                Deps = dig is { Column: >= 0 } d0 ? [PourStepOf(d0.Column)] : [],
                Note = dig == null ? "no stand beside it: the client aims from where it can" : null,
            });
            _s.Sim.Set(p, 0);
            if (dig is { } d) MoveTo(d.Stand, d.Column);
        }
    }

    // ------------------------------------------------------------------ placements

    /// <summary>The blocks the stuck layer still needs: a column is placed to reach as many as it can.</summary>
    private List<BlockPos> _cover = new();

    private sealed record Target(BlockPos Pos, int Id, int Meta, bool Scaffold)
    {
        public BuildKind Kind => BuildBlocks.KindOf(Id);
    }

    private sealed record Placement(BlockPos Stand, int Column, BlockFace Face, bool Hidden, float? Yaw, double Cost, bool Edge);

    private void PlaceAll()
    {
        var todo = Todo();
        var gate = GateCells(todo);
        var tries = new Dictionary<BlockPos, int>();
        var helps = new Dictionary<BlockPos, int>(); // techniques tried on a block for another stuck one's sake
        while (todo.Count > 0)
        {
            int layer = todo.Min(b => b.Pos.Y);
            int cls = todo.Where(b => b.Pos.Y == layer).Min(Class);
            var group = todo.Where(b => b.Pos.Y == layer && Class(b) == cls).ToList();
            bool gating = _out.Gate.State == GateState.Pending && _s.Placements < _o.GateBlocks;
            var ready = group.Where(b => Supported(T(b))).ToList();
            var order = gating ? ready.Where(b => gate.Contains(b.Pos)).OrderBy(b => gate.IndexOf(b.Pos)).Concat(ready.Where(b => !gate.Contains(b.Pos))).ToList()
                : ready.OrderBy(b => Near(b.Pos)).ToList();
            bool placed = false;
            foreach (var b in order.Take(gating ? order.Count : _o.Candidates))
            {
                if (TryPlace(T(b), gating && _s.Placements < _o.GateBlocks))
                {
                    todo.Remove(b);
                    placed = true;
                    break;
                }
                if (gating && gate.Contains(b.Pos)) continue; // the next gate cell, else any ready block
            }
            if (placed) continue;

            // Stuck: resolve the nearest remaining block with a technique, else report it.
            var stuck = group.OrderBy(b => Supported(T(b)) ? 0 : 1).ThenBy(b => Near(b.Pos)).First();
            int n = tries[stuck.Pos] = tries.GetValueOrDefault(stuck.Pos) + 1;
            _cover = group.Select(b => b.Pos).ToList();
            if (n <= 2 && Resolve(T(stuck), out _)) continue;
            // Before giving up on it: a ready block of the layer farther away that can be placed now goes first (it may be the
            // floor this one is reached from: a platform bridged out from its middle).
            if (!gating && order.Skip(_o.Candidates).FirstOrDefault(b => b.Pos != stuck.Pos && TryPlace(T(b), false)) is { } other)
            {
                todo.Remove(other);
                continue;
            }
            // Or another ready block's technique (a climb up beside it) brings this one into reach later.
            if (!gating && ready.Where(b => b.Pos != stuck.Pos).OrderBy(b => Near(b.Pos)).Take(3)
                    .FirstOrDefault(b => tries.GetValueOrDefault(b.Pos) < 2 && (helps[b.Pos] = helps.GetValueOrDefault(b.Pos) + 1) <= 2 && Resolve(T(b), out _)) != null)
                continue;
            Resolve(T(stuck), out var why, dryRun: true);
            Fail(stuck.Pos, stuck.Id, why ?? "no stand reaches a support face");
            todo.Remove(stuck);
        }
    }

    private static Target T(PlannedBlock b) => new(b.Pos, b.Id, b.Meta & (b.Kind == BuildKind.Door ? 7 : 15), false);

    private double Near(BlockPos p)
    {
        var from = _s.Col >= 0 ? new BlockPos(_s.Columns[_s.Col].Source.X, _s.ColY, _s.Columns[_s.Col].Source.Z) : _s.Land;
        return p.DistanceSquaredTo(from);
    }

    private static int Class(PlannedBlock b) => b.Kind switch
    {
        BuildKind.Solid or BuildKind.Gravity or BuildKind.Slab or BuildKind.Stairs or BuildKind.Station => 0,
        BuildKind.Thin => 1,
        BuildKind.Door => 2,
        _ => 3,
    };

    /// <summary>The block has something it can be clicked onto now (doors: a full cube under; gravity blocks: solid under).</summary>
    private bool Supported(Target t) => t.Kind switch
    {
        BuildKind.Door => BuildBlocks.NormalCube(_s.Sim.Get(t.Pos.Down())) && Free(t.Pos.Up()),
        BuildKind.Gravity => Solid(t.Pos.Down()),
        _ => Faces(t).Any(),
    };

    private bool Solid(BlockPos p) => BuildBlocks.Supports(_s.Sim.Get(p)) && !Blocks.Get(_s.Sim.Get(p)).Interactable;
    private bool Free(BlockPos p) { int id = _s.Sim.Get(p); return id == 0 || !Blocks.Get(id).Solid && !BlockShapes.IsLava(id); }

    /// <summary>Faces that may be clicked for this block (on the neighbour at target − normal).</summary>
    private IEnumerable<BlockFace> Faces(Target t)
    {
        if (t.Kind == BuildKind.Door) { if (BuildBlocks.NormalCube(_s.Sim.Get(t.Pos.Down()))) yield return BlockFace.Top; yield break; }
        if (t.Id == Blocks.Ladder)
        {
            var f = ConstructionPlanner.StepFor(new PlannedBlock(t.Pos, t.Id, t.Meta)).Face!.Value;
            if (BuildBlocks.NormalCube(_s.Sim.Get(t.Pos - f.Normal()))) yield return f;
            yield break;
        }
        if (t.Id == Blocks.Torch && t.Meta is >= 1 and <= 5)
        {
            var f = t.Meta switch { 1 => BlockFace.East, 2 => BlockFace.West, 3 => BlockFace.South, 4 => BlockFace.North, _ => BlockFace.Top };
            if (Solid(t.Pos - f.Normal())) yield return f;
            yield break;
        }
        bool halves = _o.HalvesFollowCursor && PlaceCursor.HasHalves(t.Id);
        foreach (var f in BlockFaceExtensions.All)
            if (Solid(t.Pos - f.Normal()) && (!halves || PlaceCursor.FaceAllows(f, PlaceCursor.HalfOf(t.Id, t.Meta)))) yield return f;
    }

    /// <summary>Picks the cheapest (stand, face) for <paramref name="t"/> that keeps a way out, and commits it.</summary>
    private bool TryPlace(Target t, bool gateStep)
    {
        foreach (var pl in Placements(t).OrderBy(p => p.Cost).Take(12))
        {
            if (pl.Column < 0 && _escapeCheck && !t.Scaffold)
            {
                var fork = _s.Sim.Fork();
                ApplyBlock(fork, t);
                if (!CanLeave(fork, pl.Stand, _outside, t.Pos, placing: true)) continue; // would wall me in: another stand (or block) first
            }
            Commit(t, pl, gateStep);
            return true;
        }
        return false;
    }

    private void Commit(Target t, Placement pl, bool gateStep)
    {
        var item = BuildBlocks.ItemFor(t.Id, t.Meta);
        var against = t.Pos - pl.Face.Normal();
        var step = new PlanStep
        {
            Action = t.Scaffold ? StepAction.Scaffold : StepAction.Place, Target = t.Pos, Id = t.Id, Meta = t.Meta,
            Item = t.Scaffold ? ConstructionPlanner.Scaffold : item.Item, Damage = t.Scaffold ? 0 : item.Damage,
            Stand = pl.Stand, Column = pl.Column, Face = pl.Face, HiddenFace = pl.Hidden, Yaw = pl.Yaw, Cost = pl.Cost,
            Technique = pl.Column >= 0 ? Technique.Water : pl.Hidden ? (pl.Edge ? Technique.Edge : Technique.Hidden) : pl.Edge ? Technique.Edge : Technique.Normal,
            Gate = gateStep && !t.Scaffold,
        };
        if (_s.PlacedBy.TryGetValue(against, out var dep)) step.Deps.Add(dep);
        // Stands before use: a stand on scaffold of mine (a stair tread, a walkway) waits for the step that put it there.
        if (pl.Column < 0 && _s.PlacedBy.TryGetValue(pl.Stand.Down(), out var floor) && _s.Steps[floor].Action == StepAction.Scaffold && !step.Deps.Contains(floor))
            step.Deps.Add(floor);
        if (pl.Column >= 0) step.Deps.Add(PourStepOf(pl.Column));
        Add(step);
        _s.PlacedBy[t.Pos] = _s.Steps.Count - 1;
        if (!t.Scaffold) _s.Placements++;
        else _s.Scaffold.Add(t.Pos);
        ApplyBlock(_s.Sim, t);
        CutColumns(t.Pos);
        MoveTo(pl.Stand, pl.Column);
    }

    private int PourStepOf(int column) => _s.Steps.FindIndex(s => s.Action == StepAction.PourWater && s.Column == column) is var i and >= 0 ? i : 0;

    private void ApplyBlock(PlanWorld w, Target t)
    {
        w.Set(t.Pos, t.Id, t.Meta);
        if (t.Kind == BuildKind.Door) w.Set(t.Pos.Up(), t.Id, t.Meta | 8);
    }

    /// <summary>A placement inside a column's water cuts the column there: the cells below it drain (no more stands there).</summary>
    private void CutColumns(BlockPos p)
    {
        foreach (var c in _s.Columns.Where(c => !c.Removed && c.Source.X == p.X && c.Source.Z == p.Z && p.Y < c.Source.Y))
            for (int y = p.Y - 1; y >= 0 && BlockShapes.IsWater(_s.Sim.GetBlock(p.X, y, p.Z)); y--)
            {
                var cell = new BlockPos(p.X, y, p.Z);
                _s.Sim.Set(cell, 0);
                _s.Drained.Add(cell);
            }
    }

    private void MoveTo(BlockPos stand, int column)
    {
        if (column >= 0)
        {
            _s.Col = column;
            _s.ColY = stand.Y;
            _s.Land = _s.Columns[column].Entry;
        }
        else
        {
            _s.Col = -1;
            _s.Land = stand;
        }
        _s.Flood = Flood(_s.Sim, _s.Land);
    }

    /// <summary>Every feasible (stand, face) for a block: walkable stands from the flood, and hover cells of poured columns.</summary>
    private IEnumerable<Placement> Placements(Target t)
    {
        var faces = Faces(t).ToList();
        if (faces.Count == 0) yield break;
        var look = t.Scaffold ? null : ConstructionPlanner.StepFor(new PlannedBlock(t.Pos, t.Id, t.Meta)).Look;
        float? yaw = look is { } l ? DirYaw(l) : null;
        double fromColumn = _s.Col >= 0 ? AscendCost(_s.Columns[_s.Col], _s.ColY) : 0;
        var block = AABB.Block(t.Pos.X, t.Pos.Y, t.Pos.Z);
        // Walkable stands near the target: feet up to 6 below (eyes 1.62 up, the bottom face within 4.5: a floor at y63 reaches
        // the y70 layer; live 10-28 the drop tower's -27,70,16 was cut off by a 5-below window) and 3 above.
        for (int dy = -(int)Math.Floor(_o.Reach + PlayerPhysics.EyeHeight); dy <= 3; dy++)
        for (int dz = -5; dz <= 5; dz++)
        for (int dx = -5; dx <= 5; dx++)
        {
            var s = new BlockPos(t.Pos.X + dx, t.Pos.Y + dy, t.Pos.Z + dz);
            if (!_s.Flood.TryGetValue(s, out var travel)) continue;
            var eye = GoalUseBlock.EyeAt(_s.Sim, s.X, s.Y, s.Z);
            var feet = eye with { Y = eye.Y - PlayerPhysics.EyeHeight };
            if (AABB.ForPlayer(feet).Intersects(block)) continue;
            if (t.Pos == s.Down() || Safety.SupportBlocks(_s.Sim, feet).Contains(t.Pos)) continue;
            bool edge = EdgeOf(s) != StandEdge.None;
            foreach (var pl in FaceOptions(t, faces, eye, _o.Reach, yaw, look))
                yield return pl with { Stand = s, Column = -1, Cost = travel + fromColumn + pl.Cost + (edge ? 1 : 0), Edge = edge };
        }
        // Hover cells of poured columns.
        foreach (var c in _s.Columns.Where(c => c.Poured && !c.Removed))
        {
            if (c.Source.X == t.Pos.X && c.Source.Z == t.Pos.Z) continue; // never in my own column
            for (int y = c.Source.Y - 1; y >= Math.Max(1, t.Pos.Y - 4); y--)
            {
                var cell = new BlockPos(c.Source.X, y, c.Source.Z);
                if (!BlockShapes.IsWater(_s.Sim.Get(cell)) || !BlockShapes.IsWater(_s.Sim.GetBlock(cell.X, y - 1, cell.Z)) && !Solid(cell.Down())) break;
                if (y > t.Pos.Y + 3) continue;
                var eye = new Vec3d(cell.X + 0.5, y + PlayerPhysics.EyeHeight, cell.Z + 0.5);
                var box = new AABB(cell.X + 0.2, y - SwimSim.HoverBand - 0.05, cell.Z + 0.2, cell.X + 0.8, y + PlayerPhysics.Height + SwimSim.HoverBand, cell.Z + 0.8);
                if (box.Intersects(block)) continue;
                double travel = _s.Col == c.Index ? Math.Abs(_s.ColY - y) * 12
                    : (_s.Col >= 0 ? fromColumn : 0) + (_s.Flood.TryGetValue(c.Entry, out var e) ? e : 200) + 30 + (c.Source.Y - y) * 12;
                foreach (var pl in FaceOptions(t, faces, eye, _o.Reach - 0.3, yaw, look))
                    yield return pl with { Stand = cell, Column = c.Index, Cost = travel + pl.Cost, Edge = false };
            }
        }
    }

    /// <summary>What lies beside a stand: nothing deadly; a drop past the safe fall onto solid ground (the design's own platform at
    /// height over the floor: allowed, the client's EdgeGuard sneaks there and cancels unverified jumps, so the body never walks
    /// off); or the void (today's rules: bridging out sneaking, never a pillar or stair level).</summary>
    internal enum StandEdge { None, SolidDrop, Void }

    private StandEdge EdgeOf(BlockPos s)
    {
        if (Safety.VoidNeighbourCount(_s.Sim, s.X, s.Y, s.Z) > 0) return StandEdge.Void;
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            if ((dx != 0 || dz != 0) && Safety.IsDeadlyColumn(_s.Sim, s.X + dx, s.Y, s.Z + dz, _path.MaxDrop)) return StandEdge.SolidDrop;
        return StandEdge.None;
    }

    private IEnumerable<Placement> FaceOptions(Target t, List<BlockFace> faces, Vec3d eye, double reach, float? yaw, Dir? look)
    {
        foreach (var f in faces)
        {
            var n = f.Normal();
            var against = t.Pos - n;
            var point = new Vec3d(t.Pos.X + 0.5 - n.X * 0.5, t.Pos.Y + 0.5 - n.Y * 0.5, t.Pos.Z + 0.5 - n.Z * 0.5);
            double d = eye.DistanceTo(point);
            if (d > reach) continue;
            bool seen = Raycast.Cast(_s.Sim, eye, point - eye, d + 0.01) is { } h && h.Pos == against && h.Face == f;
            if (!seen && !_o.AllowHidden) continue;
            double cost = _o.PlaceTicks + (seen ? 0 : 6);
            if (look is { } l && Quarter(PlayerPhysics.YawPitchTo(eye, point).Yaw) != l) cost += 3; // prefer the side that faces right
            yield return new Placement(default, -1, f, !seen, yaw, cost, false);
        }
    }

    // ------------------------------------------------------------------ techniques when stuck

    /// <summary>Gets <paramref name="t"/> placeable: a scaffold chain to support, a floor walkway into reach, or a water column.</summary>
    private bool Resolve(Target t, out string? why, bool dryRun = false)
    {
        why = null;
        var saved = _s.Clone();
        bool ok = ResolveCore(t, ref why);
        if (!ok || dryRun) _s = saved;
        // Resting on a block that can't be placed (or is held back) is that block's problem, not this one's: say so, so the
        // reasons name the few root cells instead of hundreds of "no support" (live 10-28: 615 of 689).
        if (!ok && !Supported(t) && WaitingOn(t.Pos) is { } wait) why = wait;
        return ok && !dryRun;
    }

    private readonly HashSet<BlockPos> _failed = new();

    private static BlockPos? RestsOn(string why)
    {
        int i = why.IndexOf("rests on ", StringComparison.Ordinal);
        if (i < 0) return null;
        var c = why[(i + 9)..].Split([',', ' '], 4);
        return c.Length >= 3 && int.TryParse(c[0], out var x) && int.TryParse(c[1], out var y) && int.TryParse(c[2], out var z) ? new BlockPos(x, y, z) : null;
    }

    /// <summary>
    /// A "no support" cell (or a wait that ends in one, or loops) next to a cell whose wait leads to a real root (out of reach,
    /// held back) only waits on that one: it was tried before its neighbour (nearest first). Repeated until nothing changes, so
    /// every reason points at the few root cells; a patch of cells that all hang over the void keeps its own reason.
    /// </summary>
    private void BlameSupports()
    {
        var at = new Dictionary<BlockPos, int>();
        for (int k = 0; k < _out.Infeasible.Count; k++) at.TryAdd(_out.Infeasible[k].Pos, k);
        static bool NoSupport(string w) => w.StartsWith("no support within", StringComparison.Ordinal);
        static bool Waits(string w) => w.StartsWith(BuildSkips.Waiting, StringComparison.Ordinal);
        // The root reason behind a cell: follow "rests on" links; null when the chain comes back to `self` or loops. A link to a
        // cell that isn't infeasible is a held-back block (no item yet): a real root.
        string? Root(BlockPos? from, BlockPos self)
        {
            var seen = new HashSet<BlockPos> { self };
            for (var p = from; p is { } q && seen.Add(q);)
            {
                if (!at.TryGetValue(q, out var j)) return "held";
                var w = _out.Infeasible[j].Why;
                if (!Waits(w)) return w;
                p = RestsOn(w);
            }
            return null;
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int k = 0; k < _out.Infeasible.Count; k++)
            {
                var i = _out.Infeasible[k];
                if (!NoSupport(i.Why) && !Waits(i.Why)) continue; // a root of its own (out of reach, protected…)
                var mine = Waits(i.Why) ? Root(RestsOn(i.Why), i.Pos) : i.Why;
                if (mine != null && !NoSupport(mine)) continue; // already leads to a real root
                foreach (var f in BlockFaceExtensions.All)
                {
                    var n = i.Pos.Offset(f);
                    if (!at.ContainsKey(n) || Root(n, i.Pos) is not { } root || NoSupport(root)) continue;
                    _out.Infeasible[k] = i with { Why = $"{BuildSkips.Waiting}; rests on {n.X},{n.Y},{n.Z}, which can't be placed" };
                    changed = true;
                    break;
                }
            }
        }
    }

    private void Fail(BlockPos p, int id, string why)
    {
        _out.Infeasible.Add(new Infeasible(p, id, why));
        _failed.Add(p);
    }

    /// <summary>"waiting on supports; rests on -27,75,16" when a neighbour it would rest on is a planned block that can't be
    /// placed (already infeasible) or is held back for a missing item; null otherwise.</summary>
    private string? WaitingOn(BlockPos p)
    {
        foreach (var f in (BlockFace[])[BlockFace.Bottom, .. BlockFaceExtensions.All.Where(f => f != BlockFace.Bottom && f != BlockFace.Top)])
        {
            var n = p.Offset(f);
            if (_failed.Contains(n)) return $"{BuildSkips.Waiting}; rests on {n.X},{n.Y},{n.Z}, which can't be placed";
            if (_plan.Blocks.TryGetValue(n, out var b) && b.Id != 0 && _o.Hold.Contains(BuildBlocks.ItemFor(b.Id, b.Meta)) && !Solid(n))
                return $"{BuildSkips.Waiting}; rests on {n.X},{n.Y},{n.Z} (no {BuildBlocks.Name(b.Id, b.Meta)} on hand yet)";
        }
        return null;
    }

    /// <summary>Why no walkable stand reaches <paramref name="p"/>: none near it at all, the nearest ones too far below (I can't
    /// climb), or just none in reach of a support face.</summary>
    private int HighestStandNear(BlockPos p)
    {
        int top = int.MinValue;
        foreach (var s in _s.Flood.Keys)
            if (Math.Abs(s.X - p.X) <= 5 && Math.Abs(s.Z - p.Z) <= 5 && s.Y > top) top = s.Y;
        return top;
    }

    private string NoStandWhy(BlockPos p)
    {
        int top = HighestStandNear(p);
        if (top == int.MinValue) return BuildSkips.NotNear;
        // Wholly above the eyes of every stand near it, and none reaches: only a way up (a pillar, stairs) would.
        if (p.Y >= top + PlayerPhysics.EyeHeight)
            return $"{BuildSkips.AboveReach} (I can't climb: {_climbWhy ?? (_o.Climb ? "no room for a pillar or a stair near it" : "climbing is off")}); {p.Y - top} above the highest stand near it (feet y{top})";
        return BuildSkips.NoStand;
    }

    private bool ResolveCore(Target t, ref string? why)
    {
        bool below = t.Pos.Y < LowestStandNear(t.Pos) - 1;
        if (!Supported(t))
        {
            if (ScaffoldPath(t.Pos, t.Kind is BuildKind.Gravity or BuildKind.Door) is not { } chain)
            {
                why = $"no support within {_o.MaxScaffold} scaffold blocks"
                      + (MineClaw.Data.Versioning.WorldMode.Current.HasVoidBelow ? " (over the void)" : " (nothing to build up from)");
                return false;
            }
            if (Chain(chain, ref why)) return true;
            if (!_o.WaterAvailable) { why = (why ?? "no stand reaches the scaffold chain") + (below ? "; below the floor: a water bucket would let me descend" : ""); return false; }
            if (!Column(chain.Last(), ref why)) return false;
            return Chain(chain, ref why);
        }
        if (Placements(t).Any()) { why = "every stand that reaches it would wall me in"; return true; } // the main loop retries later
        // Wholly above every stand near it: climb first (the design's own blocks are stands already; else a pillar, a stair);
        // level with them but out of reach: a floor walkway first.
        bool above = !below && t.Pos.Y >= HighestStandNear(t.Pos) + PlayerPhysics.EyeHeight;
        if (above && Climb(t)) return true;
        if (!below && Walkway(t.Pos) is { } walk && Chain(walk, ref why)) return true;
        if (!below && !above && Climb(t)) return true;
        if (!_o.WaterAvailable) { why = below ? "below the floor and out of reach: needs a water bucket to descend" : NoStandWhy(t.Pos); return false; }
        return Column(t.Pos, ref why) && Placements(t).Any();
    }

    private int LowestStandNear(BlockPos p)
    {
        int best = int.MaxValue;
        foreach (var s in _s.Flood.Keys)
            if (Math.Abs(s.X - p.X) <= 4 && Math.Abs(s.Z - p.Z) <= 4 && s.Y < best) best = s.Y;
        return best == int.MaxValue ? p.Y : best;
    }

    /// <summary>Places a scaffold chain (support side first); fails as a whole.</summary>
    private bool Chain(List<BlockPos> cells, ref string? why)
    {
        foreach (var c in cells)
        {
            if (Solid(c)) continue;
            var st = new Target(c, ConstructionPlanner.Scaffold, 0, true);
            if (!TryPlace(st, false) && !(Climb(st) && TryPlace(st, false)))
            {
                why = $"no stand reaches scaffold cell {c.X},{c.Y},{c.Z}; " + NoStandWhy(c);
                return false;
            }
        }
        return true;
    }

    /// <summary>BFS from the target through free cells (inside the part's strip, not planned cells) to one touching support.</summary>
    private List<BlockPos>? ScaffoldPath(BlockPos start, bool straightDown)
    {
        int height = _s.Sim.WorldHeight;
        var prev = new Dictionary<BlockPos, BlockPos> { [start] = start };
        var q = new Queue<(BlockPos P, int D)>();
        q.Enqueue((start, 0));
        while (q.Count > 0)
        {
            var (p, d) = q.Dequeue();
            if (d >= _o.MaxScaffold) continue;
            foreach (var f in straightDown ? [BlockFace.Bottom] : BlockFaceExtensions.All)
            {
                var n = p.Offset(f);
                if (prev.ContainsKey(n) || n.Y < 1 || n.Y >= height || !Free(n) || !_part.Allows(n)) continue;
                if (_plan.Blocks.ContainsKey(n) || !_s.Sim.IsLoaded(n.X, n.Z) || BlockShapes.IsLava(_s.Sim.Get(n))) continue;
                if (_s.Columns.Any(c => !c.Removed && c.Source.X == n.X && c.Source.Z == n.Z)) continue; // keep columns open
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

    /// <summary>A floor walkway of scaffold from the nearest walkable cell toward the target until a stand is in reach.</summary>
    private List<BlockPos>? Walkway(BlockPos t)
    {
        BlockPos? from = null;
        double best = double.MaxValue;
        foreach (var s in _s.Flood.Keys)
        {
            if (s.Y < t.Y - 3 || s.Y > t.Y + 1) continue;
            double d = s.DistanceSquaredTo(t);
            if (d < best) { best = d; from = s; }
        }
        if (from is not { } f) return null;
        var path = new List<BlockPos>();
        var cur = f;
        for (int i = 0; i < _o.MaxScaffold; i++)
        {
            int sx = Math.Sign(t.X - cur.X), sz = Math.Sign(t.Z - cur.Z);
            if (sx == 0 && sz == 0) break;
            var next = Math.Abs(t.X - cur.X) >= Math.Abs(t.Z - cur.Z) ? cur.Offset(sx, 0, 0) : cur.Offset(0, 0, sz);
            var floor = next.Down();
            if (!_part.Allows(floor) || _plan.Blocks.ContainsKey(floor) || !Free(next) || !Free(next.Up())) break;
            if (!Solid(floor)) { if (!Free(floor)) break; path.Add(floor); }
            cur = next;
            if (new Vec3d(cur.X + 0.5, cur.Y + 1.62, cur.Z + 0.5).DistanceTo(t.Center) <= _o.Reach - 0.5) return path;
        }
        return null;
    }

    /// <summary>
    /// Pours a water column at an edge near <paramref name="near"/> so hover stands reach down there: the source beside the edge
    /// floor block (only falls over open space, never spreads on the island), the column continuous below the needed depth,
    /// no lava near it, and a simulated descent to the depth plus the climb back out that both succeed.
    /// </summary>
    private bool Column(BlockPos near, ref string? why)
    {
        int depth = near.Y - 1;
        var options = new List<(BlockPos Entry, BlockPos Source, BlockFace Face, double Score)>();
        foreach (var (e, cost) in _s.Flood)
        {
            if (Math.Abs(e.X - near.X) > 6 || Math.Abs(e.Z - near.Z) > 6 || e.Y <= near.Y) continue;
            var floor = e.Down();
            if (!Solid(floor)) continue;
            foreach (var f in Sides)
            {
                var src = floor.Offset(f);
                var above = e.Offset(f);
                if (_s.Sim.Get(src) != 0 || !Free(above) || !Free(above.Up())) continue;
                if (_plan.Blocks.ContainsKey(src) || _s.Columns.Any(c => c.Source == src)) continue;
                double h = Math.Sqrt((src.X - near.X) * (src.X - near.X) + (src.Z - near.Z) * (src.Z - near.Z));
                if (h > 3.6) continue;
                if (!ColumnOpen(src, depth - 2)) continue;
                int covered = _cover.Count(c => (c.X - src.X) * (c.X - src.X) + (c.Z - src.Z) * (c.Z - src.Z) <= 3.6 * 3.6);
                options.Add((e, src, f, h * 10 + cost / 20 - covered * 15));
            }
        }
        if (options.Count == 0) { why = "no edge with open space below to pour a water column"; return false; }
        foreach (var (entry, src, face, _) in options.OrderBy(o => o.Score).Take(4))
        {
            var col = new SwimColumn(src.X, src.Y, src.Z);
            var wet = _s.Sim.Fork();
            foreach (var (p, m) in WaterColumn.Cells(_s.Sim, src)) wet.Set(p, m == 0 ? Blocks.Water : Blocks.FlowingWater, m);
            int hoverY = Math.Max(depth, WaterColumn.Extent(_s.Sim, src).BottomY + 1);
            var down = SwimSim.SimulateDescent(wet, entry, col, hoverY);
            if (!down.Ok) { why = $"water descent at {src.X},{src.Y},{src.Z}: {down.Why}"; continue; }
            var up = SwimSim.SimulateAscent(wet, down.End, col, entry);
            if (!up.Ok) { why = $"climbing back up at {src.X},{src.Y},{src.Z}: {up.Why}"; continue; }
            var pc = new PlanColumn
            {
                Index = _s.Columns.Count, Source = src, Face = face, Entry = entry, Depth = hoverY, DescendTicks = down.Ticks, AscendTicks = up.Ticks,
                Poured = true,
            };
            // One bucket per standing column: take the oldest back first (it may be poured again later).
            while (_s.Columns.Count(c => c.Poured && !c.Removed) >= Math.Max(1, _o.Buckets)) PickupColumn(_s.Columns.First(c => c.Poured && !c.Removed));
            // Get out of any other column first; the pour is done from the edge stand (leaning out).
            double travel = (_s.Col >= 0 ? AscendCost(_s.Columns[_s.Col], _s.ColY) : 0) + (_s.Flood.TryGetValue(entry, out var tc) ? tc : 0);
            _s.Columns.Add(pc);
            Add(new PlanStep
            {
                Action = StepAction.PourWater, Technique = Technique.Water, Target = src, Id = Blocks.Water, Item = Items.WaterBucket,
                Stand = entry, Face = face, Column = -1, Cost = travel + 40,
                Note = $"descent {down.Ticks} ticks, climb {up.Ticks}",
            });
            foreach (var (p, m) in WaterColumn.Cells(_s.Sim, src)) _s.Sim.Set(p, m == 0 ? Blocks.Water : Blocks.FlowingWater, m);
            MoveTo(entry, -1);
            return true;
        }
        return false;
    }

    private static readonly BlockFace[] Sides = [BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East];

    /// <summary>The column under a source stays open (water can fall) down to <paramref name="downTo"/>, with no lava near it.</summary>
    private bool ColumnOpen(BlockPos src, int downTo)
    {
        for (int y = src.Y - 1; y >= Math.Max(0, downTo); y--)
        {
            if (!WaterColumn.CanFlowInto(_s.Sim, src.X, y, src.Z)) return false;
            if (_plan.Blocks.ContainsKey(new BlockPos(src.X, y, src.Z))) return false;
        }
        for (int y = src.Y; y >= Math.Max(0, downTo); y--)
            if (Safety.LavaNear(_s.Sim, src.X, y, src.Z, 2)) return false;
        return true;
    }

    private static double AscendCost(PlanColumn c, int fromY) => Math.Max(20, c.AscendTicks * Math.Max(1, c.Source.Y - fromY) / (double)Math.Max(1, c.Source.Y - c.Depth));

    // ------------------------------------------------------------------ scaffold down, water back

    private void PickupColumns()
    {
        foreach (var c in _s.Columns.Where(c => c.Poured && !c.Removed).ToList()) PickupColumn(c);
    }

    /// <summary>Climb out (if in it), take the source back: the column dries (BlockFlowing: unfed water evaluates to −1).</summary>
    private void PickupColumn(PlanColumn c)
    {
        double travel = (_s.Col >= 0 ? AscendCost(_s.Columns[_s.Col], _s.ColY) : 0) + (_s.Flood.TryGetValue(c.Entry, out var e) ? e : 0);
        Add(new PlanStep
        {
            Action = StepAction.PickupWater, Technique = Technique.Water, Target = c.Source, Id = Blocks.Water, Item = Items.Bucket,
            Stand = c.Entry, Column = -1, Cost = travel + 20, Deps = [PourStepOf(c.Index)],
        });
        c.Removed = true;
        foreach (var (p, _) in WaterColumn.Cells(_s.Sim.Inner, c.Source)) if (BlockShapes.IsWater(_s.Sim.Get(p))) _s.Sim.Set(p, 0);
        if (BlockShapes.IsWater(_s.Sim.Get(c.Source))) _s.Sim.Set(c.Source, 0);
        _s.Col = -1;
        _s.Land = c.Entry;
        _s.Flood = Flood(_s.Sim, _s.Land);
    }

    private sealed record Dig(BlockPos Stand, int Column, double Cost);

    /// <summary>Where to stand to dig <paramref name="p"/>: a visible face within reach, never standing on it.</summary>
    private Dig? BestDig(BlockPos p)
    {
        Dig? best = null;
        foreach (var d in Digs(p)) if (best == null || d.Cost < best.Cost) best = d;
        return best;
    }

    /// <summary>Every stand that can dig <paramref name="p"/> (walkable stands, then hover cells of poured columns).</summary>
    private IEnumerable<Dig> Digs(BlockPos p)
    {
        double fromColumn = _s.Col >= 0 ? AscendCost(_s.Columns[_s.Col], _s.ColY) : 0;
        var block = AABB.Block(p.X, p.Y, p.Z);
        foreach (var (s, travel) in _s.Flood)
        {
            if (Math.Abs(s.X - p.X) > 5 || Math.Abs(s.Z - p.Z) > 5 || s.Y < p.Y - 6 || s.Y > p.Y + 4) continue;
            var eye = GoalUseBlock.EyeAt(_s.Sim, s.X, s.Y, s.Z);
            var feet = eye with { Y = eye.Y - PlayerPhysics.EyeHeight };
            if (AABB.ForPlayer(feet).Intersects(block) || Safety.SupportBlocks(_s.Sim, feet).Contains(p)) continue;
            if (Aiming.FindDigAim(_s.Sim, eye, p, _o.Reach) is null) continue;
            yield return new Dig(s, -1, travel + fromColumn);
        }
        foreach (var c in _s.Columns.Where(c => c.Poured && !c.Removed))
            for (int y = c.Source.Y - 1; y >= Math.Max(1, p.Y - 4); y--)
            {
                var cell = new BlockPos(c.Source.X, y, c.Source.Z);
                if (!BlockShapes.IsWater(_s.Sim.Get(cell))) break;
                if (y > p.Y + 3) continue;
                var eye = new Vec3d(cell.X + 0.5, y + PlayerPhysics.EyeHeight, cell.Z + 0.5);
                if (Aiming.FindDigAim(_s.Sim, eye, p, _o.Reach - 0.3) is null) continue;
                double cost = (_s.Col == c.Index ? Math.Abs(_s.ColY - y) * 12 : fromColumn + 30 + (c.Source.Y - y) * 12) + 200; // digging afloat is slow
                yield return new Dig(cell, c.Index, cost);
            }
    }

    private double DigTicks(int id, bool afloat) => id == 0 ? 0 : Math.Min(2000, Tools.DigTicks(id, _o.DigTool, onGround: !afloat, inWater: afloat));

    // ------------------------------------------------------------------ gate, flood, helpers

    /// <summary>
    /// The first placements, chosen so position and direction are unmistakable: the bottom-layer cell nearest the body (the
    /// start edge), then up to three more along the build's longer axis, then one across it (shows a mirror too).
    /// </summary>
    private List<BlockPos> GateCells(List<PlannedBlock> todo)
    {
        var cells = new List<BlockPos>();
        if (_o.GateBlocks <= 0 || todo.Count == 0) return cells;
        int bottom = todo.Min(b => b.Pos.Y);
        var layer = todo.Where(b => b.Pos.Y == bottom).Select(b => b.Pos).ToHashSet();
        var anchor = layer.OrderBy(p => p.DistanceSquaredTo(_s.Land)).ThenBy(p => p.X).ThenBy(p => p.Z).First();
        bool alongX = _plan.Max.X - _plan.Min.X >= _plan.Max.Z - _plan.Min.Z;
        var (cx, _, cz) = _plan.Center;
        int dirMain = alongX ? (anchor.X <= cx ? 1 : -1) : (anchor.Z <= cz ? 1 : -1);
        int dirCross = alongX ? (anchor.Z <= cz ? 1 : -1) : (anchor.X <= cx ? 1 : -1);
        cells.Add(anchor);
        for (int k = 1; k < Math.Max(1, _o.GateBlocks - 1); k++)
        {
            var p = alongX ? anchor.Offset(dirMain * k, 0, 0) : anchor.Offset(0, 0, dirMain * k);
            if (!layer.Contains(p)) break;
            cells.Add(p);
        }
        var across = alongX ? anchor.Offset(0, 0, dirCross) : anchor.Offset(dirCross, 0, 0);
        if (layer.Contains(across) && cells.Count < _o.GateBlocks) cells.Add(across);
        return cells;
    }

    private Dictionary<BlockPos, double> Flood(IBlockAccess w, BlockPos from) => Pathfinder.Explore(w, from, _path, _o.FloodNodes);

    /// <summary>Outside the build's footprint (a way out of anything it encloses).</summary>
    private bool Outside(BlockPos p) => p.X < _plan.Min.X - 1 || p.X > _plan.Max.X + 1 || p.Z < _plan.Min.Z - 1 || p.Z > _plan.Max.Z + 1;

    private void Add(PlanStep s)
    {
        s.Index = _s.Steps.Count;
        s.Part = _part.Index;
        _s.Steps.Add(s);
    }

    /// <summary>The yaw (vanilla degrees) that faces <paramref name="d"/>: south 0, west 90, north 180, east 270.</summary>
    public static float DirYaw(Dir d) => d switch { Dir.South => 0f, Dir.West => 90f, Dir.North => 180f, _ => 270f };

    /// <summary>mc-dev BlockStairs/BlockFurnace: MathHelper.floor(yaw * 4 / 360 + 0.5) &amp; 3 → south, west, north, east.</summary>
    public static Dir Quarter(float yaw) => (((int)Math.Floor(yaw * 4.0 / 360.0 + 0.5)) & 3) switch { 0 => Dir.South, 1 => Dir.West, 2 => Dir.North, _ => Dir.East };
}
