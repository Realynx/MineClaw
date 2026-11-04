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

    /// <summary>The takeoff cell and, per direction (<see cref="ParkourDirs"/>), its flat run-up before a gap (−1 none) and the
    /// width (0 none) and far-bank row of a shallow ditch there.</summary>
    private readonly ref struct ParkourFan(CellInfo here, int x, int y, int z, Span<int> run, Span<int> ditch, Span<int> ditchDy)
    {
        public readonly CellInfo Here = here;
        public readonly int X = x, Y = y, Z = z;
        public readonly Span<int> Run = run, Ditch = ditch, DitchDy = ditchDy;
    }

    private void TryParkourCore(List<Candidate> output, CellInfo here, int x, int y, int z)
    {
        Span<int> run = stackalloc int[8];
        Span<int> ditch = stackalloc int[8], ditchDy = stackalloc int[8];
        var fan = new ParkourFan(here, x, y, z, run, ditch, ditchDy);
        if (!JumpDirections(fan)) return;

        var cands = new List<(double Dist, BlockPos To, CellInfo C, int Run, int Dir)>();
        for (int ox = -ParkourRadius; ox <= ParkourRadius; ox++)
        for (int oz = -ParkourRadius; oz <= ParkourRadius; oz++)
            AddLandings(fan, ox, oz, cands);
        if (cands.Count == 0) return;
        cands.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        PlanJumps(output, fan, cands);
    }

    /// <summary>Fills the fan's run-ups and ditches; false when no direction has a gap to jump.</summary>
    private bool JumpDirections(ParkourFan fan)
    {
        bool any = false;
        for (int i = 0; i < 8; i++)
        {
            ClassifyDirection(fan, i);
            any |= fan.Run[i] >= 0;
        }
        return any;
    }

    /// <summary>
    /// A shallow ditch ahead (a 1-deep trench or water channel): only the one straight jump from its edge to the far bank, 1 or 2
    /// wide. Every other way across it is walked (down and up). A field cut by Story water channels put every node within 3 cells
    /// of one through the whole candidate fan, ~16 simulations each: 2-3 s searches, past the 2 s plan budget.
    /// </summary>
    private void ClassifyDirection(ParkourFan fan, int i)
    {
        var (dx, dz) = ParkourDirs[i];
        fan.Run[i] = GapAhead(fan.Here, fan.X, fan.Y, fan.Z, dx, dz);
        fan.Ditch[i] = 0;
        if (fan.Run[i] < 0) return;
        int wd = DitchAhead(fan.Here, fan.X, fan.Y, fan.Z, dx, dz, fan.Run[i], out int bdy);
        if (wd < 0) return;
        bool straight = dx == 0 || dz == 0;
        if (wd > 0 && straight && fan.Run[i] == 0) { fan.Ditch[i] = wd; fan.DitchDy[i] = bdy; }
        else fan.Run[i] = -1;
    }

    /// <summary>The landing candidates at offset (ox, oz) from the takeoff, top row first.</summary>
    private void AddLandings(ParkourFan fan, int ox, int oz, List<(double Dist, BlockPos To, CellInfo C, int Run, int Dir)> cands)
    {
        if (Math.Max(Math.Abs(ox), Math.Abs(oz)) < 2) return;
        double dist = Math.Sqrt(ox * ox + oz * oz);
        if (dist > ParkourRadius + 0.5) return;
        int di = JumpDirection(fan, ox, oz);
        if (di < 0) return;
        for (int dy = 1; dy >= -3; dy--) TryLanding(fan, ox, oz, dy, dist, di, cands);
    }

    /// <summary>The jump direction (within ±20°) offset (ox, oz) lies in, when that way has a gap and the offset lands past it
    /// (straight across a ditch: only its far bank); −1 otherwise.</summary>
    private static int JumpDirection(ParkourFan fan, int ox, int oz)
    {
        double ang = Math.Atan2(oz, ox);
        int di = ((int)Math.Round(ang / (Math.PI / 4)) % 8 + 8) % 8;
        double dev = Math.Abs(Math.IEEERemainder(ang - di * Math.PI / 4, 2 * Math.PI));
        if (dev > MaxOffAxis || fan.Run[di] < 0) return -1;
        var (dx, dz) = ParkourDirs[di];
        int ditch = fan.Ditch[di];
        if (ditch > 0 && (ox != dx * (ditch + 1) || oz != dz * (ditch + 1))) return -1;
        double proj = (ox * dx + oz * dz) / (double)(dx * dx + dz * dz);
        return proj < fan.Run[di] + 1.5 ? -1 : di; // must land past the gap
    }

    private void TryLanding(ParkourFan fan, int ox, int oz, int dy, double dist, int di,
        List<(double Dist, BlockPos To, CellInfo C, int Run, int Dir)> cands)
    {
        int tx = fan.X + ox, ty = fan.Y + dy, tz = fan.Z + oz;
        if (!LandingCell(fan, di, dy, tx, ty, tz, out var c)) return;
        var to = new BlockPos(tx, ty, tz);
        if (JumpAvoided(new BlockPos(fan.X, fan.Y, fan.Z), to)) return;
        var (dx, dz) = ParkourDirs[di];
        int dyb = Math.Clamp((int)Math.Floor(c.Floor - fan.Here.Floor + 1e-6), -3, 1);
        double step = dx != 0 && dz != 0 ? Math.Sqrt(2) : 1;
        double bound = (fan.Run[di] + 0.5) * step + 0.3 + Parkour.AirTravel(dyb, _o.CanSprint, _world.Physics) + 1.2;
        if (dist > bound) return;
        if (!NearEdgeOfLanding(tx, ty, tz, c, ox, oz)) return;
        if (!GapOnLine(fan.X, fan.Y, fan.Z, fan.Here, tx, ty, tz, c)) return;
        cands.Add((dist, to, c, fan.Run[di], di));
    }

    /// <summary>The caller vetoed the landing cell or this jump.</summary>
    private bool JumpAvoided(BlockPos from, BlockPos to) =>
        (_o.Avoid is { } avoid && avoid.Contains(to)) || (_o.AvoidJumps is { } aj && aj.Contains((from, to)));

    /// <summary>A standable dry landing in reach of the takeoff's height (a ditch jump: only its far bank's row), not farmland.</summary>
    private bool LandingCell(ParkourFan fan, int di, int dy, int tx, int ty, int tz, out CellInfo c)
    {
        c = default;
        if (fan.Ditch[di] > 0 && dy != fan.DitchDy[di]) return false;
        c = Cell(tx, ty, tz);
        if (!StandableLanding(c)) return false;
        if (OnFarmland(tx, ty, tz)) return false; // a gap jump lands with a fall of 1.25+: it would trample the field
        return LandingRiseOk(c.Floor - fan.Here.Floor);
    }

    private static bool StandableLanding(CellInfo c) => c.Ok && c.Ground && !c.Water && !c.Climb;

    private bool LandingRiseOk(double rise) =>
        !(rise > Parkour.MaxRise + 1e-6 || rise < -3 - 1e-6 || -rise + 1.25 > _o.ParkourMaxFall + 1e-6);

    /// <summary>Simulates the nearest candidates (up to <see cref="PathOptions.MaxParkourCandidates"/>) and adds the jumps that work.</summary>
    private void PlanJumps(List<Candidate> output, ParkourFan fan, List<(double Dist, BlockPos To, CellInfo C, int Run, int Dir)> cands)
    {
        var from = new BlockPos(fan.X, fan.Y, fan.Z);
        bool allowVoid = _o.AllowVoidParkour && _o.HasClutchBlock;
        int tried = 0;
        foreach (var (dist, to, c, r, di) in cands)
        {
            if (tried++ >= _o.MaxParkourCandidates) break;
            if (PlanCandidate(fan.Here, from, dist, to, c, r, di, allowVoid) is { } plan)
                output.Add(JumpCandidate(fan, to, c, plan));
        }
    }

    /// <summary>A plain jump is calmer (no sprint state to keep): tried first when it can possibly reach, then a sprint jump.</summary>
    private ParkourPlan? PlanCandidate(CellInfo here, BlockPos from, double dist, BlockPos to, CellInfo c, int r, int di, bool allowVoid)
    {
        var (dx, dz) = ParkourDirs[di];
        double step = dx != 0 && dz != 0 ? Math.Sqrt(2) : 1;
        int dyb = Math.Clamp((int)Math.Floor(c.Floor - here.Floor + 1e-6), -3, 1);
        ParkourPlan? plan = null;
        if (dist <= (r + 0.5) * step + 0.3 + Parkour.AirTravel(dyb, false, _world.Physics) + 1.2)
            plan = PlanJump(from, here.Floor, to, c.Floor, sprint: false, allowVoid);
        if (plan is null && _o.CanSprint) plan = PlanJump(from, here.Floor, to, c.Floor, sprint: true, allowVoid);
        return plan;
    }

    /// <summary>Over a ditch (a level jump across a 1-deep trench or water channel): a miss lands a block down and climbs out.</summary>
    private Candidate JumpCandidate(ParkourFan fan, BlockPos to, CellInfo c, ParkourPlan plan)
    {
        double risk = !plan.OverVoid && OverDitch(fan.X, fan.Y, fan.Z, fan.Here, to.X, to.Z, c)
            ? Math.Min(_o.ParkourPenalty, _o.DitchJumpPenalty) : _o.ParkourPenalty;
        double cost = plan.LandTick + 8 + risk + (plan.OverVoid ? _o.VoidParkourPenalty : 0)
                      + Math.Max(0, 0.3 - plan.Margin) * 30 + HazardPenalty(to.X, to.Y, to.Z);
        return new Candidate(to.X, to.Y, to.Z, plan.Kind, c.Floor, cost, [], null, false, null, plan);
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
            // A partial-height walk into the adjacent foot-cell row is a step, not a gap to simulate jumps across.
            var c = WalkCell(cx, y, cz, here.Floor, out _);
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

    /// <summary>
    /// The gap <paramref name="run"/> flat cells ahead in direction (dx,dz) is a shallow ditch: its first cell's floor is 0.5..1.25 below
    /// (standable, or water). Returns its width (1 or 2) when the far bank is a standable cell at this floor (± 0.1: farmland beside
    /// grass) right after it, with that cell's foot row offset in <paramref name="bankDy"/>; 0 for a ditch that isn't jumped (wider,
    /// or the far side not level); −1 when the gap isn't a ditch (a real drop: the usual gap-jump fan).
    /// </summary>
    private int DitchAhead(CellInfo here, int x, int y, int z, int dx, int dz, int run, out int bankDy)
    {
        bankDy = 0;
        for (int i = 1; i <= 3; i++)
        {
            int cx = x + (run + i) * dx, cz = z + (run + i) * dz;
            if (i > 1 && LevelBank(here, cx, y, cz) is int b) { bankDy = b; return i - 1; }
            if (!DitchFloor(here, cx, y, cz)) return i == 1 ? -1 : 0;
        }
        return 0;
    }

    /// <summary>Column (cx,cz) holds a ditch's floor seen from <paramref name="here"/>: a standable or water cell 0.5..1.25 below it.</summary>
    private bool DitchFloor(CellInfo here, int cx, int y, int cz)
    {
        for (int yy = y; yy >= y - 1; yy--)
        {
            var c = Cell(cx, yy, cz);
            if (c.Ok && c.Floor < here.Floor - 0.5 && c.Floor >= here.Floor - 1.25 - 1e-6) return true;
        }
        return false;
    }

    /// <summary>The foot-row offset (−1, 0, +1) of a standable cell in column (cx,cz) level with <paramref name="here"/> (± 0.1).</summary>
    private static readonly int[] BankRows = [0, 1, -1];

    private int? LevelBank(CellInfo here, int cx, int y, int cz)
    {
        foreach (int dy in BankRows)
        {
            var c = Cell(cx, y + dy, cz);
            if (c.Ok && c.Ground && !c.Water && Math.Abs(c.Floor - here.Floor) <= 0.1) return dy;
        }
        return null;
    }

    /// <summary>A level jump whose gap columns are all a ditch's floor (one block down, standable or water): a miss is a step
    /// down into it.</summary>
    private bool OverDitch(int x, int y, int z, CellInfo here, int tx, int tz, CellInfo t)
    {
        if (Math.Abs(t.Floor - here.Floor) > 0.1) return false;
        double ax = x + 0.5, az = z + 0.5, bx = tx + 0.5, bz = tz + 0.5;
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        for (double d = 0.5; d <= len - 0.5 + 1e-9; d += 0.25)
        {
            int cx = (int)Math.Floor(ax + (bx - ax) * d / len), cz = (int)Math.Floor(az + (bz - az) * d / len);
            if ((cx == x && cz == z) || (cx == tx && cz == tz)) continue;
            var a = Cell(cx, y, cz);
            if (a.Ok && Math.Abs(a.Floor - here.Floor) <= 0.5 + 1e-6) continue;
            if (!DitchFloor(here, cx, y, cz)) return false;
        }
        return true;
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
