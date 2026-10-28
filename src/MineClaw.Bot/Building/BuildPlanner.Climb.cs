using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>
/// Climbing: the slicer's way up when a block is above reach, in order of preference.
/// <list type="number">
/// <item>The design's own placed blocks: their tops are stands like any other (the flood walks up the design's stairs and
///   single step-ups), so nothing extra is planned while they reach.</item>
/// <item>A pillar (nerd pole): jump and place a scaffold block under myself, one per level (<see cref="Technique.Pillar"/>),
///   inside or beside the footprint, against a wall preferred. Every level keeps the void off all eight sides: the jump
///   isn't sneak-guarded, so the body is never on a 1×1 over the void.</item>
/// <item>A scaffold staircase (<see cref="Technique.Stair"/>: one block per step, two where nothing holds the tread) the
///   pathfinder walks both ways: repeated access, and the only climb in a crew plan (every lane walks a stair; a pillar top
///   holds the one body that pillared there). The crew slicer gives it to one lane; the others wait for it.</item>
/// <item>Down again: a pillar is dug back one block at a time from on top (a 1-block step onto solid footing: the client's
///   own-pillar rule), a stair taken apart from the top while walking down it, both top-first after the last block that
///   needs them. A stand on top of my own pillar counts as a way out (the wall-in check).</item>
/// </list>
/// Over the void the scaffold-distance rule is unchanged: a cell with no support within it stays "over the void past my
/// scaffold limit". "Above my reach" is reported only when no climb is possible, with why.
/// </summary>
public sealed partial class BuildPlanner
{
    private readonly BlockPos _home;
    private string? _climbWhy;
    private int _voidSpots;

    /// <summary>Centre on the cell, jump, place at the top of the jump, land.</summary>
    private const double PillarTicks = 14;

    private static readonly BlockPos[] Steps4 = [new(0, 0, -1), new(1, 0, 0), new(0, 0, 1), new(-1, 0, 0)];

    /// <summary>Gets a stand within reach of <paramref name="t"/> by climbing (a pillar, else a stair; from up on a pillar of my
    /// own that no longer helps: dug back down first). False with <see cref="_climbWhy"/> set when no climb is possible.</summary>
    private bool Climb(Target t)
    {
        _climbWhy = null;
        _voidSpots = 0;
        if (!_o.Climb) { _climbWhy = "climbing is off"; return false; }
        if (_s.Col >= 0) return false;
        var faces = Faces(t).ToList();
        if (faces.Count == 0) return false;
        if (ClimbFromHere(t, faces)) return true;
        if (_s.Pillar.Contains(_s.Land.Down()))
        {
            var saved = _s.Clone();
            if (DescendPillar() && ClimbFromHere(t, faces)) return true;
            _s = saved;
        }
        _climbWhy = _voidSpots > 0 ? "every pillar or stair spot near it is beside the void" : "no room for a pillar or a stair near it";
        return false;
    }

    private bool ClimbFromHere(Target t, List<BlockFace> faces) => !_o.SharedClimb && TryPillar(t, faces) || TryStair(t, faces);

    private (float? Yaw, Dir? Look) LookFor(Target t)
    {
        var look = t.Scaffold ? null : ConstructionPlanner.StepFor(new PlannedBlock(t.Pos, t.Id, t.Meta)).Look;
        return (look is { } l ? DirYaw(l) : null, look);
    }

    /// <summary>No void column next to the feet cell (all eight sides): standing there needs no edge guard.</summary>
    private static bool SafeLevel(IBlockAccess w, BlockPos feet) => Safety.VoidNeighbourCount(w, feet.X, feet.Y, feet.Z) == 0;

    private static bool SolidIn(IBlockAccess w, BlockPos p) { int id = w.GetBlock(p.X, p.Y, p.Z); return BuildBlocks.Supports(id) && !Blocks.Get(id).Interactable; }

    /// <summary>The body fits there: no collision box, no liquid.</summary>
    private static bool Open(IBlockAccess w, BlockPos p) =>
        !BlockShapes.HasCollision(w, p.X, p.Y, p.Z) && !BlockShapes.IsLiquid(w.GetBlock(p.X, p.Y, p.Z)) && !BlockShapes.IsLava(w.GetBlock(p.X, p.Y, p.Z));

    /// <summary>A cell temporary climbing scaffold may fill: air, not a design cell, in the part's strip and the build's claim (the
    /// footprint ±3), no water column through it, no lava next to it.</summary>
    private bool ClimbCell(IBlockAccess w, BlockPos c) =>
        c.Y >= 1 && c.Y < WorldState.Height - 3 && w.IsLoaded(c.X, c.Z) && w.GetBlock(c.X, c.Y, c.Z) == 0
        && !_plan.Blocks.ContainsKey(c) && _part.Allows(c) && !_o.Keep.Contains(c)
        && c.X >= _plan.Min.X - 3 && c.X <= _plan.Max.X + 3 && c.Z >= _plan.Min.Z - 3 && c.Z <= _plan.Max.Z + 3
        && !_s.Columns.Any(k => !k.Removed && k.Source.X == c.X && k.Source.Z == c.Z) && !Safety.LavaNear(w, c.X, c.Y, c.Z, 1);

    /// <summary>The cheapest face cost for <paramref name="t"/> from eyes at <paramref name="eye"/>, or null when none is in reach.</summary>
    private double? ReachCost(Target t, List<BlockFace> faces, Vec3d eye, float? yaw, Dir? look)
    {
        double? best = null;
        foreach (var o in FaceOptions(t, faces, eye, _o.Reach, yaw, look)) best = Math.Min(best ?? double.MaxValue, o.Cost);
        return best;
    }

    /// <summary><paramref name="t"/> can be placed now from a stand that keeps a way out (what <see cref="TryPlace"/> will accept).</summary>
    private bool PlaceableNow(Target t)
    {
        foreach (var pl in Placements(t).OrderBy(p => p.Cost).Take(12))
        {
            if (pl.Column >= 0 || !_escapeCheck || t.Scaffold) return true;
            var fork = _s.Sim.Fork();
            ApplyBlock(fork, t);
            if (CanLeave(fork, pl.Stand, _outside, t.Pos, placing: true)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ pillar

    /// <summary>
    /// A pillar from a walkable stand near <paramref name="t"/>: the lowest that reaches it, every level void-free on all sides
    /// and with headroom for the jump, scored by travel, height, how much of the stuck layer the top reaches and the walls
    /// beside it (a pillar against a wall or inside the footprint).
    /// </summary>
    private bool TryPillar(Target t, List<BlockFace> faces)
    {
        var (yaw, look) = LookFor(t);
        var options = new List<(BlockPos Base, int K, double Score)>();
        int walks = 0;
        foreach (var (b, travel) in _s.Flood)
        {
            if (Math.Abs(b.X - t.Pos.X) > 6 || Math.Abs(b.Z - t.Pos.Z) > 6 || b.Y > t.Pos.Y || t.Pos.Y - b.Y > _o.MaxPillar + 6) continue;
            if (!BuildBlocks.NormalCube(_s.Sim.Get(b.Down())) || !Open(_s.Sim, b) || !Open(_s.Sim, b.Up())) continue;
            if (!SafeLevel(_s.Sim, b)) { _voidSpots++; continue; }
            for (int k = 1; k <= _o.MaxPillar; k++)
            {
                BlockPos cell = b.Up(k - 1), top = b.Up(k);
                if (!ClimbCell(_s.Sim, cell) || cell == t.Pos || top == t.Pos || top.Up() == t.Pos || !Open(_s.Sim, top) || !Open(_s.Sim, top.Up())) break;
                if (!SafeLevel(_s.Sim, top)) { _voidSpots++; break; }
                var eye = new Vec3d(top.X + 0.5, top.Y + PlayerPhysics.EyeHeight, top.Z + 0.5);
                if (t.Pos.Y - eye.Y > _o.Reach) continue; // still far below it
                // From the top itself, else (a floor of the design's beside it) from where I can walk to up there.
                if ((ReachCost(t, faces, eye, yaw, look) ?? WalkOnCost(b, k, top, t, faces, yaw, look, ref walks)) is not { } cost)
                {
                    if (eye.Y > t.Pos.Y + 2) break; // past it: higher only gets farther
                    continue;
                }
                int covered = _cover.Count(p => eye.DistanceTo(p.Center) <= _o.Reach);
                int walls = Sides.Count(f => BlockShapes.HasCollision(_s.Sim, top.X + f.Normal().X, top.Y - 1, top.Z + f.Normal().Z));
                options.Add((b, k, travel + k * PillarTicks + cost - covered * 6 - walls * 4));
                break;
            }
        }
        foreach (var (b, k, _) in options.OrderBy(o => o.Score).Take(3))
        {
            var saved = _s.Clone();
            CommitPillar(b, k);
            if (PlaceableNow(t)) return true;
            _s = saved;
        }
        return false;
    }

    /// <summary>
    /// The pillar's top steps onto something walkable (the design's own floor, placed already): the cheapest stand reachable on
    /// foot from up there that places <paramref name="t"/> (walk + face cost), or null. A few floods per climb at most.
    /// </summary>
    private double? WalkOnCost(BlockPos b, int k, BlockPos top, Target t, List<BlockFace> faces, float? yaw, Dir? look, ref int walks)
    {
        bool Walkable(BlockFace f)
        {
            var n = top + f.Normal();
            return Open(_s.Sim, n) && Open(_s.Sim, n.Up()) && SolidIn(_s.Sim, n.Down())                    // level
                   || SolidIn(_s.Sim, n) && Open(_s.Sim, n.Up()) && Open(_s.Sim, n.Up(2)) && Open(_s.Sim, top.Up(2)); // a step up
        }
        if (walks >= 6 || !Sides.Any(Walkable))
            return null;
        walks++;
        var fork = _s.Sim.Fork();
        for (int i = 0; i < k; i++) fork.Set(b.Up(i), ConstructionPlanner.Scaffold);
        var block = AABB.Block(t.Pos.X, t.Pos.Y, t.Pos.Z);
        double? best = null;
        foreach (var (s, travel) in Flood(fork, top))
        {
            if (Math.Abs(s.X - t.Pos.X) > 5 || Math.Abs(s.Z - t.Pos.Z) > 5 || s.Y > t.Pos.Y + 3 || s.Y < t.Pos.Y - 6) continue;
            var eye = GoalUseBlock.EyeAt(fork, s.X, s.Y, s.Z);
            if (AABB.ForPlayer(eye with { Y = eye.Y - PlayerPhysics.EyeHeight }).Intersects(block) || t.Pos == s.Down()) continue;
            if (ReachCost(t, faces, eye, yaw, look) is { } c && (best == null || c + travel < best)) best = c + travel;
        }
        return best;
    }

    /// <summary>Pillars <paramref name="k"/> scaffold blocks up from the stand <paramref name="b"/>: each placed into the feet cell
    /// at the top of a jump, clicking the top of the block below; the body ends on top.</summary>
    private void CommitPillar(BlockPos b, int k)
    {
        double travel = _s.Flood.GetValueOrDefault(b);
        for (int i = 0; i < k; i++)
        {
            var cell = b.Up(i);
            var step = new PlanStep
            {
                Action = StepAction.Scaffold, Technique = Technique.Pillar, Target = cell, Id = ConstructionPlanner.Scaffold,
                Item = ConstructionPlanner.Scaffold, Stand = cell, Face = BlockFace.Top, Cost = (i == 0 ? travel : 0) + PillarTicks,
            };
            if (_s.PlacedBy.TryGetValue(cell.Down(), out var dep)) step.Deps.Add(dep);
            Add(step);
            _s.PlacedBy[cell] = _s.Steps.Count - 1;
            _s.Scaffold.Add(cell);
            _s.Pillar.Add(cell);
            _s.Sim.Set(cell, ConstructionPlanner.Scaffold);
        }
        MoveTo(b.Up(k), -1);
    }

    // ------------------------------------------------------------------ stair

    /// <summary>
    /// A scaffold staircase from a walkable stand near <paramref name="t"/> (an existing stair's top first: it grows by the steps
    /// the next layer needs): each step one up and one across, its tread held by something already there or by a block under
    /// it, headroom for the jump, never through a design cell or a cell the stair itself walks through, every step void-free on
    /// all sides. Searched depth-first (straight on first), the cheapest that reaches <paramref name="t"/> is built with the
    /// ordinary placements, then verified: the pathfinder walks to its top and <paramref name="t"/> is placeable from there.
    /// </summary>
    private bool TryStair(Target t, List<BlockFace> faces)
    {
        var (yaw, look) = LookFor(t);
        var found = new List<(List<BlockPos> Cells, BlockPos Top, double Score, BlockPos Base)>();
        // An existing stair's treads first (it grows by the steps the next layer needs: one stair for repeated access, not many).
        bool OnStair(BlockPos c) => _s.Stair.Contains(c.Down());
        var bases = _s.Flood.Where(kv => Math.Max(Math.Abs(kv.Key.X - t.Pos.X), Math.Abs(kv.Key.Z - t.Pos.Z)) <= (OnStair(kv.Key) ? 10 : 6) && kv.Key.Y <= t.Pos.Y
                && SolidIn(_s.Sim, kv.Key.Down()) && Open(_s.Sim, kv.Key) && Open(_s.Sim, kv.Key.Up()))
            .OrderByDescending(kv => OnStair(kv.Key)).ThenByDescending(kv => kv.Key.Y).ThenBy(kv => kv.Value).Take(12).ToList();
        int budget = 12000;
        foreach (var (b, travel) in bases)
        {
            if (!SafeLevel(_s.Sim, b)) { _voidSpots++; continue; }
            int rmin = Math.Max(1, (int)Math.Ceiling(t.Pos.Y - b.Y - PlayerPhysics.EyeHeight - _o.Reach + 0.5));
            int rmax = rmin + 4;
            var w = _s.Sim.Fork();
            var cells = new List<BlockPos>();
            var keepFree = new Dictionary<BlockPos, int>();
            int nodes = 0, before = found.Count;

            void Hold(BlockPos p, int d) { keepFree[p] = keepFree.GetValueOrDefault(p) + d; if (keepFree[p] <= 0) keepFree.Remove(p); }
            // A new flight never cuts the walk space of the stairs already up (they stay walkable, both ways).
            foreach (var c in _s.Stair) { Hold(c.Up(), 1); Hold(c.Up(2), 1); Hold(c.Up(3), 1); }

            // Rises (one up and one across) and landings (one across, a tread under it: around a corner to the far side).
            void Walk(BlockPos f, int rises, int flats, int last)
            {
                if (++nodes > 2500 || --budget < 0 || found.Count - before >= 4) return;
                if (rises + flats > 0 && rises >= rmin && f != t.Pos && f.Up() != t.Pos)
                {
                    var eye = new Vec3d(f.X + 0.5, f.Y + PlayerPhysics.EyeHeight, f.Z + 0.5);
                    if (ReachCost(t, faces, eye, yaw, look) is { } cost)
                    {
                        int covered = _cover.Count(p => eye.DistanceTo(p.Center) <= _o.Reach);
                        found.Add((cells.ToList(), f, travel + cells.Count * (_o.PlaceTicks + 4) + rises * 4 + flats * 6 + cost - covered * 6
                                                       + (OnStair(b) ? 0 : 60), b));
                        return;
                    }
                }
                foreach (int di in Order(last, f))
                {
                    var d = Steps4[di];
                    if (rises < rmax) Rise(f, d, di);
                    if (flats < 8 && rises > 0) Flat(f, d, di);
                }

                void Rise(BlockPos f, BlockPos d, int di)
                {
                    var tread = f + d;
                    var under = tread.Down();
                    var next = tread.Up();
                    if (tread == t.Pos || next == t.Pos || next.Up() == t.Pos || keepFree.ContainsKey(tread) || !ClimbCell(w, tread)) return;
                    if (!Open(w, f.Up(2)) || !Open(w, next) || !Open(w, next.Up())) return;
                    // Walking back down drops onto f; past it (one more across) must not be a deadly drop (the pathfinder's
                    // overshoot guard): a stair turns only where something stands beyond the step below.
                    var past = f - d;
                    if (Safety.IsDeadlyColumn(w, past.X, f.Y, past.Z, _path.MaxDrop)) return;
                    bool held = BlockFaceExtensions.All.Select(g => tread.Offset(g)).Any(n => n != f && n != next && SolidIn(w, n));
                    if (!held && (keepFree.ContainsKey(under) || !ClimbCell(w, under))) return;
                    if (!held) w.Set(under, ConstructionPlanner.Scaffold);
                    w.Set(tread, ConstructionPlanner.Scaffold);
                    if (SafeLevel(w, next))
                    {
                        int n0 = cells.Count;
                        if (!held) cells.Add(under);
                        cells.Add(tread);
                        Hold(f.Up(2), 1); Hold(next, 1); Hold(next.Up(), 1);
                        Walk(next, rises + 1, flats, di);
                        Hold(f.Up(2), -1); Hold(next, -1); Hold(next.Up(), -1);
                        cells.RemoveRange(n0, cells.Count - n0);
                    }
                    else _voidSpots++;
                    w.Set(tread, 0);
                    if (!held) w.Set(under, 0);
                }

                void Flat(BlockPos f, BlockPos d, int di)
                {
                    var next = f + d;
                    var tread = next.Down();
                    if (tread == t.Pos || next == t.Pos || next.Up() == t.Pos || keepFree.ContainsKey(tread) || !Open(w, next) || !Open(w, next.Up())) return;
                    bool solid = SolidIn(w, tread);
                    if (!solid && !ClimbCell(w, tread)) return;
                    if (!solid) w.Set(tread, ConstructionPlanner.Scaffold); // held by the block I stand on
                    if (SafeLevel(w, next))
                    {
                        if (!solid) cells.Add(tread);
                        Hold(next, 1); Hold(next.Up(), 1);
                        Walk(next, rises, flats + 1, di);
                        Hold(next, -1); Hold(next.Up(), -1);
                        if (!solid) cells.RemoveAt(cells.Count - 1);
                    }
                    else _voidSpots++;
                    if (!solid) w.Set(tread, 0);
                }
            }

            // Straight on first, then the turn toward the target, never back.
            IEnumerable<int> Order(int last, BlockPos f)
            {
                var all = Enumerable.Range(0, 4).Where(i => last < 0 || i != (last + 2) % 4)
                    .OrderBy(i => i == last ? 0 : 1).ThenBy(i => (f + Steps4[i]).DistanceSquaredTo(t.Pos));
                return all.ToList();
            }

            Hold(b.Up(), 1);
            Walk(b, 0, 0, -1);
            if (budget < 0) break;
        }
        foreach (var (stair, top, _, from) in found.OrderBy(f => f.Score).Take(3))
        {
            var saved = _s.Clone();
            bool ok = true;
            // Each tread waits for the one below it (walked up to it): a crew's other lanes wait for the whole flight.
            int prev = _s.Stair.Contains(from.Down()) && _s.PlacedBy.TryGetValue(from.Down(), out var b0) ? b0 : -1;
            foreach (var c in stair)
            {
                if (Solid(c)) continue;
                if (!TryPlace(new Target(c, ConstructionPlanner.Scaffold, 0, true), false)) { ok = false; break; }
                var st = _s.Steps[^1];
                st.Technique = Technique.Stair;
                if (prev >= 0 && !st.Deps.Contains(prev)) st.Deps.Add(prev);
                prev = st.Index;
                _s.Stair.Add(c);
            }
            // Walkable both ways: up to its top, and back down from there to where it starts.
            if (ok && _s.Flood.ContainsKey(top) && PlaceableNow(t)
                && Pathfinder.FindPath(_s.Sim, top, new GoalBlock(from), _path with { AllowPartial = false, AllowOneWay = true, MaxNodes = _o.FloodNodes }).Success)
                return true;
            _s = saved;
        }
        return false;
    }

    // ------------------------------------------------------------------ down again

    /// <summary>
    /// From <paramref name="from"/> I can still get to a cell <paramref name="exit"/> accepts: walking, or down a pillar of my own
    /// whose top I can walk to (dug back one block at a time onto solid footing, never beside the void). <paramref name="changed"/>:
    /// <paramref name="w"/> is the plan world with that one cell changed; <paramref name="placing"/>: by a placement (a block never
    /// opens a way, so where the current flood is shut in it stays shut: only the pillars are searched).
    /// </summary>
    private bool CanLeave(PlanWorld w, BlockPos from, Func<BlockPos, bool> exit, BlockPos? changed = null, bool placing = false)
    {
        bool known = ReferenceEquals(exit, _outside) || ReferenceEquals(exit, _grounded);
        bool shut = known && placing && _s.Flood.ContainsKey(from) && !FloodExits(exit);
        if (!shut && Reaches(w, from, exit)) return true;
        if (_s.Pillar.Count == 0) return false;
        foreach (var top in (shut ? _s.Flood.Keys : Flood(w, from).Keys).ToList())
        {
            if (!_s.Pillar.Contains(top.Down()) || !BuildBlocks.Supports(w.Get(top.Down())) || !Open(w, top) || !Open(w, top.Up())) continue;
            if (shut && top != from && !Pathfinder.FindPath(w, from, new GoalBlock(top), _path with { AllowPartial = false, AllowOneWay = true, MaxNodes = _o.FloodNodes }).Success) continue;
            var fork = w.Fork();
            var feet = top;
            for (var b = feet.Down(); _s.Pillar.Contains(b) && BuildBlocks.Supports(fork.Get(b)) && BuildBlocks.NormalCube(fork.Get(b.Down()))
                     && SafeLevel(fork, b); b = feet.Down())
            {
                fork.Set(b, 0);
                feet = b;
            }
            if (feet == top) continue;
            // The way on from the pillar's foot only changes with what is built down there (cached by those steps).
            var low = changed is { } q && q.Y <= feet.Y + 3 ? q : default;
            var key = (feet, ReferenceEquals(exit, _outside), LowStamp(feet.Y + 3), low);
            if (!known) { if (Reaches(fork, feet, exit)) return true; continue; }
            if (!_leaveCache.TryGetValue(key, out var ok)) _leaveCache[key] = ok = Reaches(fork, feet, exit);
            if (ok) return true;
        }
        return false;
    }

    private Dictionary<BlockPos, double>? _exitsOf;
    private bool _exitsOutside, _exitsGrounded;

    /// <summary>The current flood (from where the body is) holds a cell <paramref name="exit"/> accepts (cached per flood).</summary>
    private bool FloodExits(Func<BlockPos, bool> exit)
    {
        if (!ReferenceEquals(_exitsOf, _s.Flood))
        {
            _exitsOf = _s.Flood;
            _exitsOutside = _s.Flood.Keys.Any(Outside);
            _exitsGrounded = _s.Flood.Keys.Any(Grounded);
        }
        return ReferenceEquals(exit, _outside) ? _exitsOutside : _exitsGrounded;
    }

    private readonly Dictionary<(BlockPos, bool, long, BlockPos), bool> _leaveCache = new();

    /// <summary>A hash of every step so far that changes a cell at or below <paramref name="y"/>.</summary>
    private long LowStamp(int y)
    {
        long h = 17;
        foreach (var s in _s.Steps)
            if (s.Target.Y <= y) h = h * 31 + HashCode.Combine(s.Index, s.Target, s.Action);
        return h;
    }

    /// <summary>A walk (the flood's moves) from <paramref name="from"/> to a cell <paramref name="exit"/> accepts: a goal search that
    /// stops at the first one (the whole flood only when there is none).</summary>
    private bool Reaches(IBlockAccess w, BlockPos from, Func<BlockPos, bool> exit)
    {
        if (exit(from)) return true;
        var o = _path with { AllowPartial = false, AllowOneWay = true, MaxNodes = _o.FloodNodes, Timeout = TimeSpan.FromSeconds(5) };
        return Pathfinder.FindPath(w, from, new ExitGoal(exit, ReferenceEquals(exit, _outside) ? this : null), o).Success;
    }

    private Func<BlockPos, bool>? _outsideFn, _groundedFn;
    private Func<BlockPos, bool> _outside => _outsideFn ??= Outside;
    private Func<BlockPos, bool> _grounded => _groundedFn ??= Grounded;

    /// <summary>Any cell the predicate accepts; toward the outside of the footprint the distance to its edge guides the search.</summary>
    private sealed class ExitGoal(Func<BlockPos, bool> exit, BuildPlanner? box) : Goal
    {
        public override bool IsReached(int x, int y, int z) => exit(new BlockPos(x, y, z));
        public override double Distance(int x, int y, int z)
        {
            if (box == null) return 0;
            var (lo, hi) = (box._plan.Min, box._plan.Max);
            int d = Math.Min(Math.Min(x - (lo.X - 2), hi.X + 2 - x), Math.Min(z - (lo.Z - 2), hi.Z + 2 - z));
            return Math.Max(0, d);
        }
    }

    /// <summary>Where the body can get back down to: the level it started on (outside the footprint when it started out there).</summary>
    private bool Grounded(BlockPos c) => c.Y <= _home.Y + 1 && (!_escapeCheck || Outside(c) || c == _home);

    /// <summary>Digging <paramref name="p"/> from on top of it: the body stands on it (reachable), it's my own scaffold, and the
    /// body drops one block onto a full block with no void beside it.</summary>
    private Dig? DigUnder(BlockPos p)
    {
        var stand = p.Up();
        if (!_s.Flood.TryGetValue(stand, out var travel) || _plan.Blocks.ContainsKey(p) || !BuildBlocks.Supports(_s.Sim.Get(p))) return null;
        if (!BuildBlocks.NormalCube(_s.Sim.Get(p.Down())) || !SafeLevel(_s.Sim, p)) return null;
        return new Dig(stand, -1, travel);
    }

    private void CommitDig(BlockPos p, Dig d, bool under, string? note = null)
    {
        int id = _s.Sim.Get(p);
        Add(new PlanStep
        {
            Action = StepAction.RemoveScaffold, Target = p, Id = id, Stand = d.Stand, Column = d.Column,
            Technique = d.Column >= 0 ? Technique.Water : under ? Technique.Pillar : Technique.Normal,
            Cost = d.Cost + DigTicks(id, d.Column >= 0), Note = note,
            Deps = d.Column >= 0 ? [PourStepOf(d.Column)] : [],
        });
        _s.Sim.Set(p, 0);
        _s.Scaffold.RemoveAll(c => c == p);
        _s.Pillar.Remove(p);
        MoveTo(under ? p : d.Stand, under ? -1 : d.Column);
    }

    /// <summary>Digs the pillar I stand on back down to the ground it stands on (mid-build: a climb elsewhere starts down there).</summary>
    private bool DescendPillar()
    {
        bool moved = false;
        while (_s.Col < 0 && _s.Pillar.Contains(_s.Land.Down()) && DigUnder(_s.Land.Down()) is { } d)
        {
            CommitDig(_s.Land.Down(), d, under: true);
            moved = true;
        }
        return moved && !_s.Pillar.Contains(_s.Land.Down());
    }

    /// <summary>
    /// Scaffold down, top-first, keeping a way down at every step: a pillar is dug from on top of it (that is the way down), any
    /// other scaffold from a stand beside it from which the body can still get back to the level it started on; what no stand
    /// reaches that way is left (noted).
    /// </summary>
    private void RemoveScaffolding()
    {
        var left = _s.Scaffold.Where(p => !_plan.Blocks.ContainsKey(p) && BuildBlocks.Supports(_s.Sim.Get(p))).ToHashSet();
        while (left.Count > 0)
        {
            // Only while the body can get down now does each removal have to keep it so (it can't get worse otherwise).
            bool strict = CanLeave(_s.Sim, _s.Land, _grounded);
            bool done = false;
            Dig? Under(BlockPos p)
            {
                if (_s.Col >= 0 || DigUnder(p) is not { } u) return null;
                if (!strict) return u;
                var fork = _s.Sim.Fork();
                fork.Set(p, 0);
                return CanLeave(fork, p, _grounded, p) ? u : null;
            }

            Dig? Beside(BlockPos p)
            {
                foreach (var d in Digs(p).OrderBy(d => d.Cost).Take(6))
                {
                    if (!strict || d.Column >= 0) return d;
                    var fork = _s.Sim.Fork();
                    fork.Set(p, 0);
                    if (CanLeave(fork, d.Stand, _grounded, p)) return d;
                }
                return null;
            }

            foreach (var p in left.Where(p => !left.Contains(p.Up())).OrderByDescending(p => p.Y).ThenByDescending(p => p.DistanceSquaredTo(_plan.Min)).ToList())
            {
                if (_s.Pillar.Contains(p) && Under(p) is { } u) CommitDig(p, u, under: true);
                else if (Beside(p) is { } d) CommitDig(p, d, under: false);
                else if (Under(p) is { } u2) CommitDig(p, u2, under: true);
                else continue;
                left.Remove(p);
                done = true;
                break;
            }
            if (done) continue;
            foreach (var p in left.OrderByDescending(p => p.Y).ThenByDescending(p => p.DistanceSquaredTo(_plan.Min)))
            {
                int id = _s.Sim.Get(p);
                Add(new PlanStep
                {
                    Action = StepAction.RemoveScaffold, Target = p, Id = id, Stand = _s.Land, Cost = DigTicks(id, false), Note = "no stand sees it: may be left",
                });
                _s.Sim.Set(p, 0);
            }
            break;
        }
    }
}
