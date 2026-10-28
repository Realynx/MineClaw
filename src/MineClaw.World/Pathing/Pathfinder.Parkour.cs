namespace MineClaw.World;

/// <summary>
/// Gap-jump neighbour generation. From a standing node, every one of the 8 directions with a gap within
/// <see cref="Parkour.MaxRunCells"/> flat cells is a jump direction; landing candidates are the near-edge cells of the
/// landing area within ±20° of it (slight off-axis angles), Δy −3..+1 (floors include slab/stair tops), at most 6 cells
/// away, inside a reach bound measured from the physics. Each candidate is then validated by
/// <see cref="Parkour.Plan"/> (forward simulation with perturbations) against the overlay world — cached per search and,
/// with <see cref="PathOptions.ParkourCache"/>, across searches.
/// </summary>
public sealed partial class Pathfinder
{
    /// <summary>Directions by angle (atan2(dz, dx) = i·45°).</summary>
    private static readonly (int Dx, int Dz)[] ParkourDirs = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
    private const int ParkourRadius = 6;
    private const double MaxOffAxis = 20 * Math.PI / 180;

    private readonly Dictionary<(BlockPos, BlockPos, bool), ParkourPlan?> _parkourMemo = new();

    private void TryParkour(List<Candidate> output, CellInfo here, int x, int y, int z)
    {
        // Hypothetical edits (scaffold, digs, opened doors) near the jump: skip it (never jump onto/over blocks not yet
        // there). Edits elsewhere don't matter: evaluate against the real world so cell and jump caches apply.
        var mods = _w.Mods;
        for (var m = mods; m is not null; m = m.Next)
            if (Math.Abs(m.Pos.X - x) <= ParkourRadius + 1 && Math.Abs(m.Pos.Z - z) <= ParkourRadius + 1 && m.Pos.Y >= y - 5 && m.Pos.Y <= y + 3)
                return;
        _w.Mods = null;
        try { TryParkourCore(output, here, x, y, z); }
        finally { _w.Mods = mods; }
    }

    private void TryParkourCore(List<Candidate> output, CellInfo here, int x, int y, int z)
    {
        Span<int> run = stackalloc int[8];
        bool any = false;
        for (int i = 0; i < 8; i++)
        {
            run[i] = GapAhead(here, x, y, z, ParkourDirs[i].Dx, ParkourDirs[i].Dz);
            any |= run[i] >= 0;
        }
        if (!any) return;

        var from = new BlockPos(x, y, z);
        var cands = new List<(double Dist, BlockPos To, CellInfo C, int Run, int Dir)>();
        for (int ox = -ParkourRadius; ox <= ParkourRadius; ox++)
        for (int oz = -ParkourRadius; oz <= ParkourRadius; oz++)
        {
            if (Math.Max(Math.Abs(ox), Math.Abs(oz)) < 2) continue;
            double dist = Math.Sqrt(ox * ox + oz * oz);
            if (dist > ParkourRadius + 0.5) continue;
            double ang = Math.Atan2(oz, ox);
            int di = ((int)Math.Round(ang / (Math.PI / 4)) % 8 + 8) % 8;
            double dev = Math.Abs(Math.IEEERemainder(ang - di * Math.PI / 4, 2 * Math.PI));
            if (dev > MaxOffAxis || run[di] < 0) continue;
            var (dx, dz) = ParkourDirs[di];
            double proj = (ox * dx + oz * dz) / (double)(dx * dx + dz * dz);
            if (proj < run[di] + 1.5) continue; // must land past the gap
            int tx = x + ox, tz = z + oz;
            for (int dy = 1; dy >= -3; dy--)
            {
                int ty = y + dy;
                var c = Cell(tx, ty, tz);
                if (!c.Ok || !c.Ground || c.Water || c.Climb) continue;
                double rise = c.Floor - here.Floor;
                if (rise > Parkour.MaxRise + 1e-6 || rise < -3 - 1e-6 || -rise + 1.25 > _o.ParkourMaxFall + 1e-6) continue;
                var to = new BlockPos(tx, ty, tz);
                if (_o.Avoid is { } avoid && avoid.Contains(to)) continue;
                if (_o.AvoidJumps is { } aj && aj.Contains((from, to))) continue;
                int dyb = Math.Clamp((int)Math.Floor(rise + 1e-6), -3, 1);
                double step = dx != 0 && dz != 0 ? Math.Sqrt(2) : 1;
                double bound = (run[di] + 0.5) * step + 0.3 + Parkour.AirTravel(dyb, _o.CanSprint, _world.Physics) + 1.2;
                if (dist > bound) continue;
                if (!NearEdgeOfLanding(tx, ty, tz, c, ox, oz)) continue;
                if (!GapOnLine(x, y, z, here, tx, ty, tz, c)) continue;
                cands.Add((dist, to, c, run[di], di));
            }
        }
        if (cands.Count == 0) return;
        cands.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        bool allowVoid = _o.AllowVoidParkour && _o.HasClutchBlock;
        int tried = 0;
        foreach (var (dist, to, c, r, di) in cands)
        {
            if (tried++ >= _o.MaxParkourCandidates) break;
            var (dx, dz) = ParkourDirs[di];
            double step = dx != 0 && dz != 0 ? Math.Sqrt(2) : 1;
            int dyb = Math.Clamp((int)Math.Floor(c.Floor - here.Floor + 1e-6), -3, 1);
            ParkourPlan? plan = null;
            // A plain jump is calmer (no sprint state to keep): try it first when it can possibly reach.
            if (dist <= (r + 0.5) * step + 0.3 + Parkour.AirTravel(dyb, false, _world.Physics) + 1.2)
                plan = PlanJump(from, here.Floor, to, c.Floor, sprint: false, allowVoid);
            if (plan is null && _o.CanSprint) plan = PlanJump(from, here.Floor, to, c.Floor, sprint: true, allowVoid);
            if (plan is null) continue;
            double cost = plan.LandTick + 8 + _o.ParkourPenalty + (plan.OverVoid ? _o.VoidParkourPenalty : 0)
                          + Math.Max(0, 0.3 - plan.Margin) * 30 + HazardPenalty(to.X, to.Y, to.Z);
            output.Add(new Candidate(to.X, to.Y, to.Z, plan.Kind, c.Floor, cost, [], null, false, null, plan));
        }
    }

    private ParkourPlan? PlanJump(BlockPos from, double fromFloor, BlockPos to, double toFloor, bool sprint, bool allowVoid)
    {
        var key = (from, to, sprint);
        if (_parkourMemo.TryGetValue(key, out var memo)) return memo;
        var plan = _o.ParkourCache is { } cache
            ? cache.GetOrPlan(_world, from, fromFloor, to, toFloor, sprint, allowVoid, _o.ParkourMaxFall, _o.MaxDrop)
            : Parkour.Plan(_world, from, fromFloor, to, toFloor, sprint, allowVoid, _o.ParkourMaxFall, _o.MaxDrop, out _);
        _parkourMemo[key] = plan;
        return plan;
    }

    /// <summary>
    /// Flat run-up cells (0..MaxRunCells) ahead of (x,y,z) in direction (dx,dz) before a gap: a cell the body passes at
    /// the run height that has no floor there. −1 when there is no gap that way within range (or a wall/step instead).
    /// </summary>
    private int GapAhead(CellInfo here, int x, int y, int z, int dx, int dz)
    {
        for (int i = 1; i <= Parkour.MaxRunCells + 1; i++)
        {
            int cx = x + i * dx, cz = z + i * dz;
            var c = Cell(cx, y, cz);
            bool flat = c.Ok && c.Ground && !c.Water && !c.Climb && Math.Abs(c.Floor - here.Floor) < 0.01;
            if (flat && dx != 0 && dz != 0)
            {
                var a = Cell(cx - dx, y, cz);
                var b = Cell(cx, y, cz - dz);
                flat = a.Ok && b.Ok && Math.Abs(a.Floor - here.Floor) < 0.01 && Math.Abs(b.Floor - here.Floor) < 0.01;
                if (!flat) return -1; // a corner in the way: no straight diagonal run
            }
            if (flat)
            {
                if (i == Parkour.MaxRunCells + 1) return -1;
                continue;
            }
            if (c.Ok && Math.Abs(c.Floor - here.Floor) <= 0.5 + 1e-6) return -1; // a step, not a gap
            return BodyClear(cx, cz, here.Floor) ? i - 1 : -1;
        }
        return -1;
    }

    /// <summary>The landing cell is the first standable one of its area when coming from (−ox,−oz): the cell one block
    /// back toward the takeoff is not a floor at the same height (else land there instead).</summary>
    private bool NearEdgeOfLanding(int tx, int ty, int tz, CellInfo c, int ox, int oz)
    {
        double len = Math.Sqrt(ox * ox + oz * oz);
        int bx = (int)Math.Floor(tx + 0.5 - ox / len), bz = (int)Math.Floor(tz + 0.5 - oz / len);
        if (bx == tx && bz == tz) return true;
        var b = Cell(bx, ty, bz);
        return !(b.Ok && b.Ground && Math.Abs(b.Floor - c.Floor) < 0.01);
    }

    /// <summary>Some column strictly between the two cells is standable at neither level (there is a gap to jump).</summary>
    private bool GapOnLine(int x, int y, int z, CellInfo here, int tx, int ty, int tz, CellInfo t)
    {
        double ax = x + 0.5, az = z + 0.5, bx = tx + 0.5, bz = tz + 0.5;
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        for (double d = 0.5; d <= len - 0.5 + 1e-9; d += 0.25)
        {
            int cx = (int)Math.Floor(ax + (bx - ax) * d / len), cz = (int)Math.Floor(az + (bz - az) * d / len);
            if ((cx == x && cz == z) || (cx == tx && cz == tz)) continue;
            var a = Cell(cx, y, cz);
            var b = Cell(cx, ty, cz);
            bool standA = a.Ok && Math.Abs(a.Floor - here.Floor) <= 0.5 + 1e-6;
            bool standB = b.Ok && Math.Abs(b.Floor - t.Floor) <= 0.5 + 1e-6;
            if (!standA && !standB) return true;
        }
        return false;
    }
}
