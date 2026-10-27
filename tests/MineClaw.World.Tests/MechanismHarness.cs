using MineClaw.World;

namespace MineClaw.World.Tests;

/// <summary>Hand-advanced clock (one tick = 50 ms).</summary>
public sealed class TickClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Tick(int n = 1) => _now += TimeSpan.FromMilliseconds(50 * n);
}

/// <summary>
/// A tiny b1.8.1 "server" for mechanisms: hand-opened doors/gates/trapdoors (BlockDoor/BlockTrapdoor/BlockFenceGate
/// .interact), and wires from triggers to effects with the vanilla timings — a stone button pops 20 ticks after the press
/// (plus an optional pulse extender), a pressure plate presses when a body overlaps its detection box (x/z 0.125..0.875,
/// y 0..0.25) and is re-checked every 20 ticks, popping on the first check with nobody on it, a lever latches. Piston
/// effects pass through block 36 (moving) for 2 ticks. Bodies (the bot, other players) press plates; a solid block
/// placed into a body marks <see cref="Crushed"/>.
/// </summary>
public sealed class FakeRedstone
{
    public sealed class Wire
    {
        public BlockPos Trigger;
        public readonly List<(BlockPos Pos, BlockState Off, BlockState On)> Effects = new();
        public int Lag;
        /// <summary>Extra ticks the effects stay on after the trigger pops (a pulse extender).</summary>
        public int Extend;
        public bool Piston;
        public bool Pressed;
        public int? CheckAt;
        public int Activations;
    }

    private readonly WorldState _w;
    private readonly List<(int At, Action Do)> _queue = new();
    public readonly List<Wire> Wires = new();
    public readonly List<Func<AABB>> Bodies = new();
    public int Now { get; private set; }
    public bool Crushed { get; private set; }
    public int Clicks { get; private set; }

    public FakeRedstone(WorldState w) => _w = w;

    public Wire Add(BlockPos trigger, int lag = 0, int extend = 0, bool piston = false)
    {
        var wire = new Wire { Trigger = trigger, Lag = lag, Extend = extend, Piston = piston };
        Wires.Add(wire);
        return wire;
    }

    /// <summary>An iron (or any) door at <paramref name="lower"/>: on = open bit flipped from its current state.</summary>
    public Wire Door(BlockPos trigger, BlockPos lower, int lag = 0, int extend = 0)
    {
        var wire = Add(trigger, lag, extend);
        DoorEffect(wire, lower);
        return wire;
    }

    public static void DoorEffect(Wire wire, BlockPos lower)
    {
        foreach (var p in new[] { lower, lower.Up() })
        {
            var off = new BlockState(0, 0);
            wire.Effects.Add((p, off, off)); // filled in lazily (needs the world)
        }
    }

    private void Resolve(Wire wire)
    {
        for (int i = 0; i < wire.Effects.Count; i++)
        {
            var (p, off, on) = wire.Effects[i];
            if (off.Id != 0 || on.Id != 0) continue;
            int id = _w.GetBlock(p), meta = _w.GetMeta(p);
            wire.Effects[i] = (p, new BlockState(id, meta), new BlockState(id, meta ^ Openables.OpenBit));
        }
    }

    private void At(int delay, Action a) => _queue.Add((Now + Math.Max(0, delay), a));

    /// <summary>A right-click (by the bot or a player).</summary>
    public void Click(BlockPos p)
    {
        Clicks++;
        int id = _w.GetBlock(p), meta = _w.GetMeta(p);
        if (Openables.OpensByHand(id))
        {
            var s = Openables.StateBlock(_w, p);
            int m = _w.GetMeta(s);
            _w.SetBlock(s.X, s.Y, s.Z, id, m ^ Openables.OpenBit);
            if (Openables.IsDoor(id) && _w.GetBlock(s.X, s.Y + 1, s.Z) == id)
                _w.SetBlock(s.X, s.Y + 1, s.Z, id, (m ^ Openables.OpenBit) | Openables.UpperBit);
            return;
        }
        var wire = Wires.FirstOrDefault(x => x.Trigger == p);
        if (id == Openables.Lever)
        {
            _w.SetBlock(p.X, p.Y, p.Z, id, meta ^ 8);
            if (wire != null) Fire(wire, (meta & 8) == 0);
        }
        else if (id == Openables.StoneButton && (meta & 8) == 0)
        {
            _w.SetBlock(p.X, p.Y, p.Z, id, meta | 8);
            if (wire != null) { wire.Activations++; Fire(wire, true); }
            At(20, () =>
            {
                _w.SetBlock(p.X, p.Y, p.Z, id, _w.GetMeta(p) & ~8);
                if (wire != null) At(wire.Extend, () => Fire(wire, false));
            });
        }
    }

    private void Fire(Wire wire, bool on)
    {
        Resolve(wire);
        At(wire.Lag, () =>
        {
            foreach (var (p, off, onState) in wire.Effects)
            {
                var t = on ? onState : off;
                bool openable = off.Id == onState.Id && Openables.IsOpenable(off.Id);
                if (openable)
                {
                    int m = _w.GetMeta(p);
                    _w.SetBlock(p.X, p.Y, p.Z, t.Id, (m & ~Openables.OpenBit) | (t.Meta & Openables.OpenBit));
                }
                else if (wire.Piston && t.Id != 0 && !Mechanisms.IsPistonBase(t.Id))
                {
                    _w.SetBlock(p.X, p.Y, p.Z, Mechanisms.PistonMoving, 0);
                    var pos = p;
                    At(2, () => Place(pos, t));
                }
                else Place(p, t);
            }
        });
    }

    private void Place(BlockPos p, BlockState t)
    {
        if (t.Id != 0 && Bodies.Any(b => b().Intersects(AABB.Block(p.X, p.Y, p.Z)))) Crushed = true;
        _w.SetBlock(p.X, p.Y, p.Z, t.Id, t.Meta);
    }

    /// <summary>One server tick: scheduled actions, then plates (BlockPressurePlate entity collision + scheduled check).</summary>
    public void Tick()
    {
        Now++;
        var due = _queue.Where(q => q.At <= Now).ToList();
        _queue.RemoveAll(q => q.At <= Now);
        foreach (var (_, a) in due) a();
        foreach (var wire in Wires)
        {
            var p = wire.Trigger;
            if (!Openables.IsPlate(_w.GetBlock(p))) continue;
            var detect = new AABB(p.X + 0.125, p.Y, p.Z + 0.125, p.X + 0.875, p.Y + 0.25, p.Z + 0.875);
            bool occupied = Bodies.Any(b => b().Intersects(detect));
            if (occupied && !wire.Pressed)
            {
                wire.Pressed = true;
                wire.Activations++;
                _w.SetBlock(p.X, p.Y, p.Z, _w.GetBlock(p), 1);
                Fire(wire, true);
                wire.CheckAt ??= Now + 20;
            }
            if (wire.CheckAt == Now)
            {
                if (occupied) wire.CheckAt = Now + 20;
                else
                {
                    wire.CheckAt = null;
                    wire.Pressed = false;
                    _w.SetBlock(p.X, p.Y, p.Z, _w.GetBlock(p), 0);
                    At(wire.Extend, () => Fire(wire, false));
                }
            }
        }
    }
}

/// <summary>
/// Closed loop: Pathfinder (doors + mechanisms) → PathFollower → EdgeGuard → PlayerPhysics against <see cref="FakeRedstone"/>,
/// with the Client's re-plan rules (an openable/trigger that didn't respond is vetoed; a missed pulse just re-plans) and a
/// <see cref="MechanismLearner"/> watching every block change.
/// </summary>
public sealed class MechanismLoop
{
    public sealed record Outcome(bool Reached, PhysicsState State, int Replans, List<MoveKind> Kinds, string? LastFailure, List<PathStep> Steps,
        PathFollower? Last);

    public WorldState World { get; }
    public FakeRedstone Server { get; }
    public TickClock Clock { get; } = new();
    public MechanismLearner Learner { get; }
    public PhysicsState Bot { get; private set; } = new();
    public HashSet<BlockPos> Vetoed { get; } = new();
    public Func<PathOptions, PathOptions>? Tune { get; set; }
    public int Ticks { get; private set; }

    public MechanismLoop(WorldState world, MechanismLearnerOptions? learner = null)
    {
        World = world;
        Server = new FakeRedstone(world);
        Learner = new MechanismLearner(world, world.Mechanisms, Clock, learner).Attach();
        Server.Bodies.Add(() => Bot.BoundingBox);
    }

    public static WorldState NewWorld()
    {
        var w = new WorldState();
        int size = 64;
        w.ApplyChunkRegion(-32, 0, -32, size, 128, size, new byte[size * 128 * size], null, null, null);
        return w;
    }

    /// <summary>Advance the server, clock and learner without the bot doing anything.</summary>
    public void Idle(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Clock.Tick();
            Server.Tick();
            Learner.Pump();
        }
    }

    public PathOptions Options()
    {
        var o = PathOptions.Default with
        {
            AllowOpenDoors = true, AllowPartial = false, Mechanisms = World.Mechanisms.View(World.Dimension),
            CanOpen = (p, _) => !Vetoed.Contains(p),
        };
        return Tune?.Invoke(o) ?? o;
    }

    public PathResult Plan(Vec3d from, Goal goal) => Pathfinder.FindPath(World, from, goal, Options());

    public Outcome Run(Vec3d start, Goal goal, int maxReplans = 4, int maxTicks = 3000, bool place = true)
    {
        if (place) Bot = TestWorld.Standing(start.X, start.Y, start.Z);
        var guard = new EdgeGuard();
        var kinds = new List<MoveKind>();
        var steps = new List<PathStep>();
        int replans = 0;
        string? lastFailure = null;
        PathFollower? last = null;
        while (true)
        {
            var cell = Pathfinder.StartCellFor(World, Bot.Position) ?? Bot.Position.ToBlockPos();
            if (goal.IsReached(cell)) return new(true, Bot, replans, kinds, lastFailure, steps, last);
            var path = Pathfinder.FindPath(World, Bot.Position, goal, Options());
            if (path.Status != PathStatus.Found || path.Steps.Count == 0)
                return new(false, Bot, replans, kinds, path.Reason ?? path.Status.ToString(), steps, last);
            kinds.AddRange(path.Steps.Select(p => p.Kind));
            steps.AddRange(path.Steps);
            var f = last = new PathFollower(path.Steps);
            FollowerOutput o;
            while (true)
            {
                if (++Ticks > maxTicks) return new(false, Bot, replans, kinds, "tick budget", steps, last);
                o = f.Tick(World, Bot);
                if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
                if (o.Status == FollowerStatus.NeedsAction && o.Action is { Kind: FollowerActionKind.Activate } a) Server.Click(a.Pos);
                var d = guard.Filter(World, Bot, o.Input, f.CurrentStep?.Kind == MoveKind.Drop);
                PlayerPhysics.Tick(World, Bot, d.Input);
                Clock.Tick();
                Server.Tick();
                Learner.Pump();
            }
            if (o.Status == FollowerStatus.Failed)
            {
                lastFailure = o.Reason;
                if (f.FailedTrigger is { } ft) Vetoed.Add(ft);
                else if (f.PulseMissed is null && f.CurrentStep is { Opens.Count: > 0 } st)
                    foreach (var tg in st.Opens.Where(t => !t.Done(World))) Vetoed.Add(tg.Pos);
                if (++replans > maxReplans) return new(false, Bot, replans, kinds, lastFailure, steps, last);
            }
            else
            {
                // Let restores (lever back, doors closed behind) finish.
                for (int i = 0; i < 40 && f.Tick(World, Bot) is { Status: FollowerStatus.NeedsAction, Action: { Kind: FollowerActionKind.Activate } ra }; i++)
                {
                    Server.Click(ra.Pos);
                    Idle(2);
                }
                if (!goal.IsReached(Pathfinder.StartCellFor(World, Bot.Position) ?? Bot.Position.ToBlockPos()))
                    return new(false, Bot, replans, kinds, "finished off goal", steps, last);
            }
        }
    }
}
