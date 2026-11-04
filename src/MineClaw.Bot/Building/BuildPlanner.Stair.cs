using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>The scaffold staircase climb (see <see cref="BuildPlanner"/>'s climbing notes).</summary>
public sealed partial class BuildPlanner
{
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
        var search = new StairSearch(this, t, faces, yaw, look);
        foreach (var (b, travel) in StairBases(t))
        {
            if (!SafeLevel(_s.Sim, b)) { _voidSpots++; continue; }
            search.From(b, travel);
            if (search.Budget < 0) break;
        }
        foreach (var (stair, top, _, from) in search.Found.OrderBy(f => f.Score).Take(3))
            if (TryBuildStair(t, stair, top, from)) return true;
        return false;
    }

    /// <summary>A stand on top of a stair tread of mine.</summary>
    private bool OnStair(BlockPos c) => _s.Stair.Contains(c.Down());

    /// <summary>Where a stair may start: an existing stair's treads first (it grows by the steps the next layer needs: one stair for
    /// repeated access, not many), then the highest, nearest walkable stands on solid ground with headroom.</summary>
    private List<KeyValuePair<BlockPos, double>> StairBases(Target t) =>
        FloodAll.Where(kv => Math.Max(Math.Abs(kv.Key.X - t.Pos.X), Math.Abs(kv.Key.Z - t.Pos.Z)) <= (OnStair(kv.Key) ? 10 : 6) && kv.Key.Y <= t.Pos.Y
                             && SolidIn(_s.Sim, kv.Key.Down()) && Open(_s.Sim, kv.Key) && Open(_s.Sim, kv.Key.Up()))
            .OrderByDescending(kv => OnStair(kv.Key)).ThenByDescending(kv => kv.Key.Y).ThenBy(kv => kv.Value).Take(12).ToList();

    /// <summary>Builds a found stair with the ordinary placements and verifies it (walkable both ways, <paramref name="t"/>
    /// placeable from the top); the plan is restored when it fails.</summary>
    private bool TryBuildStair(Target t, List<BlockPos> stair, BlockPos top, BlockPos from)
    {
        var saved = _s.Clone();
        // Walkable both ways: up to its top, and back down from there to where it starts.
        if (PlaceStairTreads(stair, from) && FloodAll.ContainsKey(top) && PlaceableNow(t) && WalksTo(_s.Sim, top, from))
            return true;
        _s = saved;
        return false;
    }

    /// <summary>Each tread waits for the one below it (walked up to it): a crew's other lanes wait for the whole flight.</summary>
    private bool PlaceStairTreads(List<BlockPos> stair, BlockPos from)
    {
        int prev = _s.Stair.Contains(from.Down()) && _s.PlacedBy.TryGetValue(from.Down(), out var b0) ? b0 : -1;
        foreach (var c in stair)
        {
            if (Solid(c)) continue;
            if (!TryPlace(new Target(c, ConstructionPlanner.Scaffold, 0, true), false)) return false;
            var st = _s.Steps[^1];
            st.Technique = Technique.Stair;
            if (prev >= 0 && !st.Deps.Contains(prev)) st.Deps.Add(prev);
            prev = st.Index;
            _s.Stair.Add(c);
        }
        return true;
    }

    /// <summary>
    /// The depth-first stair search over all bases of one <see cref="TryStair"/>: rises (one up and one across) and landings (one
    /// across, a tread under it: around a corner to the far side), straight on first, at most four stairs per base, 2500 nodes per
    /// base and 12000 in all.
    /// </summary>
    private sealed class StairSearch(BuildPlanner p, Target t, List<BlockFace> faces, float? yaw, Dir? look)
    {
        public readonly List<(List<BlockPos> Cells, BlockPos Top, double Score, BlockPos Base)> Found = [];
        public int Budget = 12000;

        private PlanWorld _w = null!;
        private List<BlockPos> _cells = [];
        private Dictionary<BlockPos, int> _keepFree = [];
        private int _nodes, _before, _rmin, _rmax;
        private BlockPos _base;
        private double _travel;

        /// <summary>Searches the stairs that start at <paramref name="b"/>.</summary>
        public void From(BlockPos b, double travel)
        {
            _rmin = Math.Max(1, (int)Math.Ceiling(t.Pos.Y - b.Y - PlayerPhysics.EyeHeight - p._o.Reach + 0.5));
            _rmax = _rmin + 4;
            _w = p._s.Sim.Fork();
            _cells = [];
            _keepFree = [];
            _nodes = 0;
            _before = Found.Count;
            _base = b;
            _travel = travel;
            // A new flight never cuts the walk space of the stairs already up (they stay walkable, both ways).
            foreach (var c in p._s.Stair) { Hold(c.Up(), 1); Hold(c.Up(2), 1); Hold(c.Up(3), 1); }
            Hold(b.Up(), 1);
            Walk(b, 0, 0, -1);
        }

        private void Hold(BlockPos c, int d)
        {
            _keepFree[c] = _keepFree.GetValueOrDefault(c) + d;
            if (_keepFree[c] <= 0) _keepFree.Remove(c);
        }

        private void Walk(BlockPos f, int rises, int flats, int last)
        {
            if (++_nodes > 2500 || --Budget < 0 || Found.Count - _before >= 4) return;
            if (Reached(f, rises, flats)) return;
            foreach (int di in Order(last, f))
            {
                var d = Steps4[di];
                if (rises < _rmax) Rise(f, d, di, rises, flats);
                if (flats < 8 && rises > 0) Flat(f, d, di, rises, flats);
            }
        }

        /// <summary>High enough and <see cref="t"/> in reach from <paramref name="f"/>: the stair so far is found (scored).</summary>
        private bool Reached(BlockPos f, int rises, int flats)
        {
            if (rises + flats == 0 || rises < _rmin || f == t.Pos || f.Up() == t.Pos) return false;
            var eye = new Vec3d(f.X + 0.5, f.Y + PlayerPhysics.EyeHeight, f.Z + 0.5);
            if (p.ReachCost(t, faces, eye, yaw, look) is not { } cost) return false;
            int covered = p._cover.Count(c => eye.DistanceTo(c.Center) <= p._o.Reach);
            Found.Add((_cells.ToList(), f, _travel + _cells.Count * (p._o.PlaceTicks + 4) + rises * 4 + flats * 6 + cost - covered * 6
                                           + (p.OnStair(_base) ? 0 : 60), _base));
            return true;
        }

        /// <summary>One up and one across: the tread (and a block under it when nothing holds it), then on from the step.</summary>
        private void Rise(BlockPos f, BlockPos d, int di, int rises, int flats)
        {
            var tread = f + d;
            var under = tread.Down();
            var next = tread.Up();
            if (!CanRise(f, d, tread, next)) return;
            bool held = BlockFaceExtensions.All.Select(g => tread.Offset(g)).Any(n => n != f && n != next && SolidIn(_w, n));
            if (!held && (_keepFree.ContainsKey(under) || !p.ClimbCell(_w, under))) return;
            if (!held) _w.Set(under, ConstructionPlanner.Scaffold);
            _w.Set(tread, ConstructionPlanner.Scaffold);
            if (SafeLevel(_w, next)) StepUp(f, next, tread, held ? null : under, di, rises, flats);
            else p._voidSpots++;
            _w.Set(tread, 0);
            if (!held) _w.Set(under, 0);
        }

        /// <summary>The rise's cells are free (not the target, not kept free, a climb cell), headroom for the jump, and walking back
        /// down past the step below is no deadly drop (the pathfinder's overshoot guard: a stair turns only where something
        /// stands beyond the step below).</summary>
        private bool CanRise(BlockPos f, BlockPos d, BlockPos tread, BlockPos next)
        {
            if (tread == t.Pos || next == t.Pos || next.Up() == t.Pos || _keepFree.ContainsKey(tread) || !p.ClimbCell(_w, tread)) return false;
            if (!Open(_w, f.Up(2)) || !Open(_w, next) || !Open(_w, next.Up())) return false;
            var past = f - d;
            return !Safety.IsDeadlyColumn(_w, past.X, f.Y, past.Z, p._path.MaxDrop);
        }

        private void StepUp(BlockPos f, BlockPos next, BlockPos tread, BlockPos? under, int di, int rises, int flats)
        {
            int n0 = _cells.Count;
            if (under is { } u) _cells.Add(u);
            _cells.Add(tread);
            Hold(f.Up(2), 1); Hold(next, 1); Hold(next.Up(), 1);
            Walk(next, rises + 1, flats, di);
            Hold(f.Up(2), -1); Hold(next, -1); Hold(next.Up(), -1);
            _cells.RemoveRange(n0, _cells.Count - n0);
        }

        /// <summary>One across on the level: a landing, its tread held by the block I stand on.</summary>
        private void Flat(BlockPos f, BlockPos d, int di, int rises, int flats)
        {
            var next = f + d;
            var tread = next.Down();
            if (!CanLand(next, tread)) return;
            bool solid = SolidIn(_w, tread);
            if (!solid && !p.ClimbCell(_w, tread)) return;
            if (!solid) _w.Set(tread, ConstructionPlanner.Scaffold); // held by the block I stand on
            if (SafeLevel(_w, next)) StepAcross(next, solid ? null : tread, di, rises, flats);
            else p._voidSpots++;
            if (!solid) _w.Set(tread, 0);
        }

        /// <summary>The landing's cells are clear of the target, its tread not kept free, room for the body.</summary>
        private bool CanLand(BlockPos next, BlockPos tread) =>
            tread != t.Pos && next != t.Pos && next.Up() != t.Pos && !_keepFree.ContainsKey(tread) && Open(_w, next) && Open(_w, next.Up());

        private void StepAcross(BlockPos next, BlockPos? tread, int di, int rises, int flats)
        {
            if (tread is { } placed) _cells.Add(placed);
            Hold(next, 1); Hold(next.Up(), 1);
            Walk(next, rises, flats + 1, di);
            Hold(next, -1); Hold(next.Up(), -1);
            if (tread != null) _cells.RemoveAt(_cells.Count - 1);
        }

        /// <summary>Straight on first, then the turn toward the target, never back.</summary>
        private List<int> Order(int last, BlockPos f) =>
            Enumerable.Range(0, 4).Where(i => last < 0 || i != (last + 2) % 4)
                .OrderBy(i => i == last ? 0 : 1).ThenBy(i => (f + Steps4[i]).DistanceSquaredTo(t.Pos)).ToList();
    }
}
