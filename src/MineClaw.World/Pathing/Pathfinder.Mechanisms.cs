namespace MineClaw.World;

/// <summary>
/// Mechanisms in the search: Trigger steps (button/lever within reach, a known plate walked onto) put the link's effects
/// into the overlay; button/plate effects only last their pulse window, so every move that passes through an effect cell
/// is checked against the window with move-time estimates (<see cref="Node.T"/>) and rejected when it can't make it.
/// Levers latch (their effects become ordinary path mods). Plates and pistons on the way are screened, and a route
/// through a mechanism door must leave a known way back out (<see cref="CheckReturn"/>).
/// </summary>
public sealed partial class Pathfinder
{
    private readonly MechanismView? _view;
    /// <summary>Passages (iron door state blocks, link triggers) this search may not use: one-way traps.</summary>
    private readonly HashSet<BlockPos> _banned = new();
    /// <summary>Hypothetical world the search starts in (way-back check with levers left flipped).</summary>
    private Mod? _baseMods;
    private readonly Dictionary<BlockPos, bool> _explosiveCache = new();
    private readonly Dictionary<MechanismLink, (HashSet<BlockPos> Cells, HashSet<BlockPos> Solid)> _linkCells = new(ReferenceEqualityComparer.Instance);

    /// <param name="Activated">Estimated tick the trigger fired (margins grow with time since).</param>
    /// <param name="Deadline">Tick the effects revert (plates: extended while standing on <see cref="Plate"/>).</param>
    private sealed record PulseState(TriggerUse Use, Mod? Mods, double Activated, double Deadline, BlockPos? Plate,
        HashSet<BlockPos> Cells, HashSet<BlockPos> Solid);

    private sealed record LeverUse(TriggerUse Use, HashSet<BlockPos> Cells, HashSet<BlockPos> Solid, LeverUse? Next);

    /// <summary>Search-state part of a node key. A pulse's remaining time is part of it (3-tick buckets): reaching a cell
    /// with more of the window left is a different, not a worse, state (pressing later can be what makes it).</summary>
    private static int StateKey(PulseState? pulse, LeverUse? levers, double t)
    {
        int h = pulse is null ? 0 : HashCode.Combine(pulse.Use.Link.Trigger, Math.Clamp((int)Math.Floor((pulse.Deadline - t) / 3), -1, 200));
        for (var l = levers; l is not null; l = l.Next) h = HashCode.Combine(h, l.Use.Link.Trigger, l.Use.TurnOn);
        return h;
    }

    private static bool HasLever(LeverUse? levers, MechanismLink link)
    {
        for (var l = levers; l is not null; l = l.Next) if (ReferenceEquals(l.Use.Link, link) || l.Use.Link.Trigger == link.Trigger) return true;
        return false;
    }

    /// <summary>Standing in a cell a pulse's moved blocks come back to.</summary>
    private static bool InPulseSolid(Node n) =>
        n.Pulse is { } p && (p.Solid.Contains(new BlockPos(n.X, n.Y, n.Z)) || p.Solid.Contains(new BlockPos(n.X, n.Y + 1, n.Z)));

    private (HashSet<BlockPos> Cells, HashSet<BlockPos> Solid) CellsOf(MechanismLink link)
    {
        if (_linkCells.TryGetValue(link, out var c)) return c;
        var cells = new HashSet<BlockPos>();
        var solid = new HashSet<BlockPos>();
        foreach (var e in link.Effects)
        {
            cells.Add(e.Pos);
            if (Openables.IsDoor(e.On.Id)) cells.Add(e.Pos.Up(Openables.IsUpperHalf(e.On.Id, e.On.Meta) ? -1 : 1));
            if (e.IsSolidChange) solid.Add(e.Pos);
        }
        c = (cells, solid);
        _linkCells[link] = c;
        return c;
    }

    /// <summary>0: the move doesn't pass any of the cells; 1: an openable's cell; 2: a moved block's cell.</summary>
    private static int Touch(HashSet<BlockPos> cells, HashSet<BlockPos> solid, Node n, in Candidate m)
    {
        int x0 = Math.Min(n.X, m.X), x1 = Math.Max(n.X, m.X), z0 = Math.Min(n.Z, m.Z), z1 = Math.Max(n.Z, m.Z);
        int y0 = Math.Min(n.Y, m.Y) - 1, y1 = Math.Max(n.Y, m.Y) + 2;
        int r = 0;
        foreach (var c in cells)
        {
            if (c.X < x0 || c.X > x1 || c.Z < z0 || c.Z > z1 || c.Y < y0 || c.Y > y1) continue;
            if (solid.Contains(c)) return 2;
            r = 1;
        }
        return r;
    }

    private bool Usable(MechanismLink link) =>
        link.Confidence >= _o.MinMechanismConfidence && (!link.Dangerous || _o.AllowDangerousTriggers) && !_banned.Contains(link.Trigger)
        && (_o.CanOpen is not { } veto || veto(link.Trigger, link.TriggerId)) && link.TriggerPresent(_w);

    /// <summary>
    /// Mechanism bookkeeping for one candidate move from <paramref name="n"/>: a known plate walked onto becomes a Trigger
    /// step; an active pulse is carried on, extended (standing on its plate), expired, or — when the move passes one of its
    /// effect cells — checked against its window (false = can't make it in time); a new trigger starts a pulse or latches a
    /// lever. <paramref name="time"/> is the move's estimated duration (plus the mechanism's lag after a trigger).
    /// </summary>
    private bool MechanismStep(Node n, ref Candidate m, out PulseState? pulse, out LeverUse? levers, out TriggerUse? requires, out double time)
    {
        time = m.Time >= 0 ? m.Time : m.Cost;
        pulse = n.Pulse;
        levers = n.Levers;
        requires = null;

        if (m.Trigger is null && SteppedOnPlate(n, m, pulse) is { } plate)
            m = m with { Kind = MoveKind.Trigger, Trigger = new TriggerUse(plate, TurnOn: true) };

        // (With no pulse, no lever and no trigger nothing below changes anything.)
        if (pulse is not null && !CarryPulse(n, m, time, ref pulse, ref requires)) return false;
        requires ??= LeverRequired(n, m, levers);
        if (m.Trigger is { } tu) StartTrigger(n, tu, ref pulse, ref levers, ref time);
        return true;
    }

    /// <summary>A known (or presumed) plate the move walks onto that fires something now: it becomes a Trigger step.</summary>
    private MechanismLink? SteppedOnPlate(Node n, in Candidate m, PulseState? pulse)
    {
        if (!Openables.IsPlate(_w.GetBlock(m.X, m.Y, m.Z)) || (m.X == n.X && m.Z == n.Z && m.Y == n.Y)) return null;
        if (PlateLink(new BlockPos(m.X, m.Y, m.Z)) is not { } plate || !Usable(plate)) return null;
        return pulse?.Use.Link.Trigger != plate.Trigger && !plate.EffectsIn(_w, on: true) ? plate : null;
    }

    /// <summary>
    /// The active pulse through this move: extended while standing on its plate, checked against its window when the move passes
    /// one of its effect cells (false = can't make it in time; else the move requires it), or over (null) once the move ends past
    /// its deadline.
    /// </summary>
    private bool CarryPulse(Node n, in Candidate m, double time, ref PulseState? pulse, ref TriggerUse? requires)
    {
        var p = pulse!;
        double deadline = p.Deadline;
        // Standing on the plate holds it down: it only pops on a scheduled check after the body leaves.
        if (p.Plate is { } pp && pp.X == n.X && pp.Y == n.Y && pp.Z == n.Z) deadline = Math.Max(deadline, n.T + 0.6 * time);
        int touch = Touch(p.Cells, p.Solid, n, m);
        if (touch != 0)
        {
            if (!InPulseTime(n, p, touch, time, deadline)) return false;
            requires = p.Use;
        }
        else if (n.T + time > deadline) pulse = null; // over: the world is back to normal
        if (pulse is not null && deadline != pulse.Deadline) pulse = pulse with { Deadline = deadline };
        return true;
    }

    /// <summary>Openables: be past the panel's plane (about mid-move); moved blocks: be fully out of their cells; with the
    /// margin (growing with the time since the activation) before the deadline.</summary>
    private bool InPulseTime(Node n, PulseState p, int touch, double time, double deadline)
    {
        double need = n.T + (touch == 2 ? time : 0.6 * time);
        double margin = _o.PulseMargin + _o.PulseMarginFraction * Math.Max(0, need - p.Activated);
        return !(need > deadline - margin);
    }

    /// <summary>The first latched lever whose effect cells the move passes (the move requires it to stay on).</summary>
    private static TriggerUse? LeverRequired(Node n, in Candidate m, LeverUse? levers)
    {
        for (var l = levers; l is not null; l = l.Next)
            if (Touch(l.Cells, l.Solid, n, m) != 0) return l.Use;
        return null;
    }

    /// <summary>A trigger step starts a pulse (buttons and plates) or latches a lever; the mechanism's lag adds to the time.</summary>
    private void StartTrigger(Node n, TriggerUse tu, ref PulseState? pulse, ref LeverUse? levers, ref double time)
    {
        var (cells, solid) = CellsOf(tu.Link);
        if (tu.Link.IsPulse)
        {
            double act = tu.IsPlate ? n.T + 0.5 * time : n.T + _o.TriggerTicks;
            pulse = new PulseState(tu, EffectMods(tu, null), act, act + tu.Link.WindowTicks, tu.IsPlate ? tu.Pos : null, cells, solid);
        }
        else levers = new LeverUse(tu, cells, solid, levers);
        time += tu.Link.LagTicks;
    }

    /// <summary>Buttons and levers of known links within reach (and in sight) of the node's eye.</summary>
    private void TryTriggers(List<Candidate> output, Node n, CellInfo here)
    {
        if (here.Water) return;
        var at = new BlockPos(n.X, n.Y, n.Z);
        var eye = new Vec3d(n.X + 0.5, here.Floor + PlayerPhysics.EyeHeight, n.Z + 0.5);
        foreach (var link in _view!.TriggersNear(at, 6))
        {
            if (TriggerUseFrom(n, eye, link) is not { } use) continue;
            output.Add(new Candidate(n.X, n.Y, n.Z, MoveKind.Trigger, here.Floor, _o.TriggerCost, [], null, false,
                Time: _o.TriggerTicks + 2, Trigger: use));
        }
    }

    /// <summary>Using the button or lever of <paramref name="link"/> from here: usable, pressable, it would change something, and
    /// within reach and in sight of the eye; else null.</summary>
    private TriggerUse? TriggerUseFrom(Node n, Vec3d eye, MechanismLink link)
    {
        if (link.IsPlate || !Usable(link)) return null;
        bool lever = link.Kind == TriggerKind.Lever;
        if (!Pressable(n, link, lever)) return null;
        bool on = !lever || !link.TriggerOn(_w);
        if (link.EffectsIn(_w, on)) return null; // nothing would change
        if (!TriggerInReach(eye, link)) return null;
        return new TriggerUse(link, on, Restore: lever && _o.RestoreLevers);
    }

    /// <summary>A lever not already flipped on this path; a button only with no pulse running (one at a time) and not pressed
    /// (a pressed button can't be pressed).</summary>
    private bool Pressable(Node n, MechanismLink link, bool lever) =>
        lever ? !HasLever(n.Levers, link) : n.Pulse is null && !link.TriggerOn(_w);

    private bool TriggerInReach(Vec3d eye, MechanismLink link)
    {
        var c = link.Trigger.Center;
        if (eye.DistanceTo(c) > _o.ActuatorReach + 0.5) return false;
        return Aiming.FindDigAim(_w, eye, link.Trigger, _o.ActuatorReach + 0.5) is not null;
    }

    /// <summary>The link's effects (in the trigger state <see cref="TriggerUse.TurnOn"/>) on top of <paramref name="chain"/>,
    /// door halves kept in step; facing bits are read from the current overlay.</summary>
    private Mod? EffectMods(TriggerUse tu, Mod? chain)
    {
        foreach (var e in tu.Link.Effects)
        {
            var st = e.Apply(_w, tu.TurnOn);
            chain = new Mod(e.Pos, st.Id, chain, st.Meta);
            if (!e.IsOpenable || !Openables.IsDoor(e.On.Id)) continue;
            var partner = e.Pos.Up(Openables.IsUpperHalf(e.On.Id, e.On.Meta) ? -1 : 1);
            if (tu.Link.Has(partner) || _w.GetBlock(partner.X, partner.Y, partner.Z) != e.On.Id) continue;
            int pm = _w.GetMeta(partner.X, partner.Y, partner.Z);
            chain = new Mod(partner, e.On.Id, chain, (pm & ~Openables.OpenBit) | (st.Meta & Openables.OpenBit));
        }
        return chain;
    }

    private Mod? LeverMods(TriggerUse tu, Mod? mods)
    {
        var saved = _w.Mods;
        _w.Mods = mods;
        try
        {
            mods = EffectMods(tu, mods);
            var p = tu.Pos;
            int meta = _w.GetMeta(p.X, p.Y, p.Z);
            return new Mod(p, Openables.Lever, mods, tu.TurnOn ? meta | 0x8 : meta & ~0x8);
        }
        finally { _w.Mods = saved; }
    }

    private readonly Dictionary<BlockPos, MechanismLink?> _presumed = new();

    /// <summary>
    /// The link of the plate at <paramref name="p"/>: learned, or presumed for the classic setup — a plate right in front
    /// of an iron door we know nothing about opens it (b1.8.1 plates power the block they're on and its neighbours'
    /// doors), with the vanilla 20-tick pulse. Presumed links are low-confidence and only live in this search; the learner
    /// records the real one when the plate fires.
    /// </summary>
    private MechanismLink? PlateLink(BlockPos p)
    {
        if (_view?.ForTrigger(p) is { } known) return known;
        if (_presumed.TryGetValue(p, out var c)) return c;
        c = PresumedPlateLink(p);
        _presumed[p] = c;
        return c;
    }

    /// <summary>The classic setup: the plate opens the first iron door beside it that we know nothing about (never with
    /// explosives near).</summary>
    private MechanismLink? PresumedPlateLink(BlockPos p)
    {
        if (Mechanisms.KindOf(_world.GetBlock(p.X, p.Y, p.Z)) is not { } kind || Explosives(p)) return null;
        foreach (var (dx, dz) in Cardinals)
        {
            var q = new BlockPos(p.X + dx, p.Y, p.Z + dz);
            if (!UnknownIronDoor(q)) continue;
            return new MechanismLink(p, kind, IronDoorEffects(q)) { Confidence = 0.4, Dimension = _view?.Dimension ?? 0 };
        }
        return null;
    }

    /// <summary>The lower half of an iron door, not banned, that no known link affects.</summary>
    private bool UnknownIronDoor(BlockPos q)
    {
        if (_world.GetBlock(q.X, q.Y, q.Z) != Openables.IronDoor || Openables.IsUpperHalf(Openables.IronDoor, _world.GetMeta(q.X, q.Y, q.Z))) return false;
        return !(_banned.Contains(q) || _view?.Affecting(q).Count > 0);
    }

    /// <summary>Both halves of the iron door at <paramref name="q"/> opening (the open bit set).</summary>
    private List<MechanismEffect> IronDoorEffects(BlockPos q)
    {
        var effects = new List<MechanismEffect>(2);
        foreach (var h in new[] { q, q.Up() })
        {
            if (_world.GetBlock(h.X, h.Y, h.Z) != Openables.IronDoor) continue;
            int m = _world.GetMeta(h.X, h.Y, h.Z);
            effects.Add(new MechanismEffect(h, new BlockState(Openables.IronDoor, m & ~Openables.OpenBit),
                new BlockState(Openables.IronDoor, m | Openables.OpenBit)));
        }
        return effects;
    }

    private bool Explosives(BlockPos p)
    {
        if (_explosiveCache.TryGetValue(p, out bool v)) return v;
        v = Mechanisms.ExplosivesNear(_world, p);
        _explosiveCache[p] = v;
        return v;
    }

    /// <summary>A plate fires whatever it is wired to: never one linked to TNT/dispensers (or with explosives near), never
    /// a banned one (one-way passage).</summary>
    private bool PlateOk(int x, int y, int z)
    {
        var p = new BlockPos(x, y, z);
        if (_banned.Contains(p)) return false;
        if (_view?.ForTrigger(p) is { } link) return !link.Dangerous || _o.AllowDangerousTriggers;
        return !Explosives(p);
    }

    /// <summary>Unknown plates cost extra (unless it's the plate in front of an iron door: the classic setup).</summary>
    private double PlatePenalty(int x, int y, int z)
    {
        if (_view?.ForTrigger(new BlockPos(x, y, z)) is not null) return 0;
        foreach (var (dx, dz) in Cardinals)
            if (_w.GetBlock(x + dx, y, z + dz) == Openables.IronDoor) return 0;
        return _o.UnknownPlatePenalty;
    }

    /// <summary>A cell a retracted piston would push into: off limits unless that piston belongs to a known latched
    /// mechanism, or to a pulse the planner controls (its timing is checked per move). A known pulse that is running right
    /// now (button down) is about to move: off limits too.</summary>
    private bool InPistonPath(int x, int y, int z)
    {
        if (Mechanisms.PistonThreat(_w, x, y, z) is not { } pb) return false;
        if (_view is null) return true;
        var links = _view.Affecting(pb).Concat(_view.Affecting(new BlockPos(x, y, z))).ToList();
        if (links.Count == 0) return true;
        return links.Any(l => l.IsPulse && l.TriggerOn(_world));
    }

    // ------------------------------------------------------------------ the way back out

    private sealed record Passage(BlockPos Key, DoorToggle? Toggle, TriggerUse? Trigger);

    private sealed class NeverGoal : Goal
    {
        public override bool IsReached(int x, int y, int z) => false;
        public override double Distance(int x, int y, int z) => 0;
    }

    /// <summary>Iron doors (heuristic actuator) and Trigger steps of a path.</summary>
    private static List<Passage> Passages(IReadOnlyList<PathStep> steps)
    {
        var list = new List<Passage>();
        foreach (var st in steps)
        {
            foreach (var tg in st.Opens)
                if (tg.Actuator is not null && list.All(p => p.Key != tg.Pos)) list.Add(new Passage(tg.Pos, tg, null));
            if (st.Trigger is { } tu && list.All(p => p.Key != tu.Pos)) list.Add(new Passage(tu.Pos, null, tu));
        }
        return list;
    }

    /// <summary>
    /// Return-trip safety: a route through a mechanism door must not end in a space with no known way back out. From the
    /// path's end, a reverse search (current world: buttons popped, levers put back) must reach the start — or the far
    /// side must not be a pocket (it is at least as big as the near side, e.g. stepping out of a house). Otherwise: try
    /// leaving the levers flipped (the door stays open behind), else ban those passages and re-plan.
    /// </summary>
    private PathResult CheckReturn(PathResult r, BlockPos start, Goal goal, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var passages = Passages(r.Steps);
            if (passages.Count == 0) return r;
            var end = r.Steps[^1].Pos;
            if (Escapes(end, start, null, passages, ct)) return r;
            var levers = passages.Where(p => p.Trigger is { Link.Kind: TriggerKind.Lever, Restore: true }
                || p.Toggle is { Actuator: { } a } && _world.GetBlock(a.X, a.Y, a.Z) == Openables.Lever).ToList();
            if (levers.Count > 0 && Escapes(end, start, LeftOn(levers), passages, ct)) return KeepLevers(r, levers);
            r = BanAndReplan(start, goal, passages, ct, out bool final);
            if (final) return r;
        }
        return r;
    }

    /// <summary>The passages become one-way traps for this search and it plans again; <paramref name="final"/> when nothing was
    /// found (the reason then says why).</summary>
    private PathResult BanAndReplan(BlockPos start, Goal goal, List<Passage> passages, CancellationToken ct, out bool final)
    {
        foreach (var p in passages) _banned.Add(p.Key);
        _cellCache.Clear();
        _deadlyCache.Clear();
        var again = Search(start, goal, ct);
        string why = $"no known way back out past the {string.Join(", ", passages.Select(Describe))}";
        final = again.Steps.Count == 0;
        if (final) return again with { Reason = again.Reason is null ? why : $"{again.Reason}; {why}" };
        return again with { Reason = again.Reason ?? (again.Success ? null : why) };
    }

    private static string Describe(Passage p) => p.Toggle is { } t
        ? $"{Openables.Name(t.Id)} at {Mechanisms.Fmt(t.Pos)}"
        : $"{Mechanisms.TriggerName(p.Trigger!.Link.Kind)} door at {Mechanisms.Fmt(p.Trigger.Pos)}";

    private bool Escapes(BlockPos end, BlockPos start, Mod? baseMods, List<Passage> passages, CancellationToken ct)
    {
        var o = _o with
        {
            AllowPartial = false, AllowOneWay = true, MaxNodes = _o.ReturnCheckNodes,
            Timeout = TimeSpan.FromTicks(Math.Min(_o.Timeout.Ticks, TimeSpan.FromMilliseconds(600).Ticks)),
        };
        var rev = new Pathfinder(_world, o) { _baseMods = baseMods };
        foreach (var b in _banned) rev._banned.Add(b);
        var rr = rev.Search(end, new GoalNear(start, 1), ct);
        if (rr.Success) return true;
        if (rr.Reason is not "no route" and not "no route (mechanism pulses too short to make it)") return true; // budget: not a pocket
        int far = rr.NodesExplored;
        // Near side without the passages: if it's bigger than the far side, the far side is a pocket.
        var flood = new Pathfinder(_world, o with { MaxNodes = far + 2, AllowPartial = false });
        foreach (var b in _banned) flood._banned.Add(b);
        foreach (var p in passages) flood._banned.Add(p.Key);
        var fr = flood.Search(start, new NeverGoal(), ct);
        return fr.NodesExplored <= far;
    }

    /// <summary>The world after the path with its levers left flipped (doors stay open).</summary>
    private Mod? LeftOn(List<Passage> levers)
    {
        var saved = _w.Mods;
        var savedPulse = _w.Pulse;
        _w.Pulse = null;
        _w.Mods = null;
        try
        {
            Mod? mods = null;
            foreach (var p in levers)
            {
                if (p.Trigger is { } tu) mods = LeverMods(tu, mods);
                else if (p.Toggle is { Actuator: { } a } tg)
                {
                    mods = ToggleMods(tg, mods);
                    _w.Mods = mods;
                    mods = new Mod(a, Openables.Lever, mods, _w.GetMeta(a.X, a.Y, a.Z) ^ 0x8);
                }
                _w.Mods = mods;
            }
            return mods;
        }
        finally
        {
            _w.Mods = saved;
            _w.Pulse = savedPulse;
        }
    }

    private static PathResult KeepLevers(PathResult r, List<Passage> levers)
    {
        var keys = levers.Select(l => l.Key).ToHashSet();
        TriggerUse? Fix(TriggerUse? t) => t is not null && keys.Contains(t.Pos) ? t with { Restore = false } : t;
        var steps = r.Steps.Select(s => s with
        {
            Open = s.Open?.Select(t => keys.Contains(t.Pos) ? t with { Keep = true } : t).ToList(),
            Trigger = Fix(s.Trigger),
            Requires = Fix(s.Requires),
        }).ToList();
        return r with { Steps = steps };
    }
}
