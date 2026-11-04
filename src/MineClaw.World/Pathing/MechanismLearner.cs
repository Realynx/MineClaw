namespace MineClaw.World;

public sealed record MechanismLearnerOptions
{
    /// <summary>Block changes this close (cube) to a trigger count as its effects.</summary>
    public int EffectRadius { get; init; } = 8;
    /// <summary>How long after a trigger changes its effects may show (redstone is instant, repeaters 2–8 ticks each,
    /// pistons ~3 ticks): 15 ticks.</summary>
    public TimeSpan EffectWindow { get; init; } = TimeSpan.FromMilliseconds(750);
    /// <summary>How long a button/plate activation is watched for its effects reverting (the pulse length).</summary>
    public TimeSpan PulseWatch { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Who counts as the activator: the nearest player within this distance of the trigger.</summary>
    public double ActivatorRadius { get; init; } = 5.5;
    /// <summary>A player's "watch this" makes their next activation within this time a demonstration.</summary>
    public TimeSpan DemoWindow { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>Below this confidence an untaught link that keeps missing is forgotten.</summary>
    public double ForgetBelow { get; init; } = 0.15;
}

/// <summary>One learning step, for logs/wake events.</summary>
public sealed record MechanismObservation(MechanismLink Link, string? By, bool IsNew, bool Miss, bool Demonstrated);

/// <summary>
/// Learns mechanisms empirically (no redstone simulation): when a trigger changes state (button pressed, lever flipped,
/// plate pressed — by anyone, the bot included), the block changes within <see cref="MechanismLearnerOptions.EffectWindow"/>
/// and <see cref="MechanismLearnerOptions.EffectRadius"/> are its effects (doors/trapdoors toggling, piston bases and the
/// blocks they move, blocks appearing/disappearing). Wiring (dust, torches, repeaters), liquids, fire and the bot's own
/// digs/placements are ignored. Button/plate activations are then watched until the effects revert: that is the pulse.
/// Repeats reinforce (confidence, per-effect support), activations without effect weaken. Results go to the
/// <see cref="MechanismGraph"/>.
/// <para>Threading: <see cref="WorldState.BlockChanged"/> arrives on the network thread; <see cref="Pump"/> (a timer, or
/// tests) closes windows. Both under one lock.</para>
/// </summary>
public sealed class MechanismLearner : IDisposable
{
    private sealed class Episode
    {
        public BlockPos Trigger;
        public TriggerKind Kind;
        public bool TurnedOn;
        public DateTimeOffset At;
        public int Dim;
        public string? By;
        public bool Demo;
        public readonly Dictionary<BlockPos, BlockState> Before = new();
        public DateTimeOffset? FirstEffect;
        public readonly HashSet<BlockPos> Pistons = new();
        public bool Closed;
        public IReadOnlyList<MechanismEffect>? Watch;
        public DateTimeOffset? Released;
    }

    private readonly WorldState _world;
    private readonly MechanismLearnerOptions _o;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<Episode> _episodes = new();
    private readonly Dictionary<string, DateTimeOffset> _demo = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Graph writes decided under the lock, applied by <see cref="Pump"/> outside it (their Changed/Removed
    /// handlers persist to the db: never on the network thread, never while holding the lock).</summary>
    private readonly List<Action> _deferred = new();
    private bool _attached;

    public MechanismLearner(WorldState world, MechanismGraph? graph = null, TimeProvider? time = null, MechanismLearnerOptions? options = null)
    {
        _world = world;
        Graph = graph ?? world.Mechanisms;
        _time = time ?? TimeProvider.System;
        _o = options ?? new MechanismLearnerOptions();
    }

    public MechanismGraph Graph { get; }
    /// <summary>The bot's name (activations it made itself are attributed to it).</summary>
    public string? SelfName { get; set; }
    public event Action<MechanismObservation>? Observed;

    public MechanismLearner Attach()
    {
        if (_attached) return this;
        _attached = true;
        _world.BlockChanged += OnBlockChanged;
        return this;
    }

    public void Dispose()
    {
        if (_attached) _world.BlockChanged -= OnBlockChanged;
        _attached = false;
    }

    /// <summary>Open episodes (tests/diagnostics).</summary>
    public int Pending { get { lock (_lock) return _episodes.Count; } }

    /// <summary>A player said "watch, this button opens the wall": their next activation is a demonstration.</summary>
    public void NoteDemonstration(string player)
    {
        lock (_lock) _demo[player] = _time.GetUtcNow();
    }

    /// <summary>0x36 block action: (piston base, 0 extend / 1 retract, facing). Evidence that a piston is involved.</summary>
    public void OnBlockAction(BlockPos p, int data1, int data2)
    {
        lock (_lock)
        {
            if (!Mechanisms.IsPistonBase(_world.GetBlock(p))) return;
            var now = _time.GetUtcNow();
            foreach (var e in _episodes)
                if (!e.Closed && Near(e.Trigger, p) && now - e.At <= _o.EffectWindow) e.Pistons.Add(p);
        }
    }

    private bool Near(BlockPos a, BlockPos b) =>
        Math.Abs(a.X - b.X) <= _o.EffectRadius && Math.Abs(a.Y - b.Y) <= _o.EffectRadius && Math.Abs(a.Z - b.Z) <= _o.EffectRadius;

    private void OnBlockChanged(BlockChange ch)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            int dim = _world.Dimension;
            var oldKind = Mechanisms.KindOf(ch.OldId);
            var newKind = Mechanisms.KindOf(ch.NewId);
            if (oldKind is { } ok && newKind is null && Graph.Get(ch.Pos, dim) is { } gone)
            {
                // Trigger removed: its link is history.
                _deferred.Add(() => Graph.Remove(gone.Trigger, dim));
                return;
            }
            if (newKind is { } k && ch.OldId == ch.NewId)
            {
                TriggerFlipped(ch, k, now, dim);
                return;
            }
            if (oldKind is not null || newKind is not null) return;
            if (Ignored(ch)) return;
            NoteEffect(ch, now, dim);
        }
    }

    /// <summary>A trigger changed state: switched on (or a lever either way) starts an episode; a button or plate popping marks
    /// its episodes released.</summary>
    private void TriggerFlipped(BlockChange ch, TriggerKind k, DateTimeOffset now, int dim)
    {
        bool before = Openables.IsActuatorOn(ch.OldId, ch.OldMeta), after = Openables.IsActuatorOn(ch.NewId, ch.NewMeta);
        if (before == after) return;
        if (k != TriggerKind.Lever && !after)
        {
            foreach (var e in _episodes) if (e.Trigger == ch.Pos && e.Released is null) e.Released = now;
            return;
        }
        StartEpisode(ch.Pos, k, after, now, dim);
    }

    /// <summary>Any other block change near an open episode (within its effect window) is a candidate effect: its state before
    /// the first change is kept.</summary>
    private void NoteEffect(BlockChange ch, DateTimeOffset now, int dim)
    {
        foreach (var e in _episodes)
        {
            if (e.Closed || e.Dim != dim || now - e.At > _o.EffectWindow || !Near(e.Trigger, ch.Pos)) continue;
            if (!e.Before.ContainsKey(ch.Pos)) e.Before[ch.Pos] = new BlockState(ch.OldId, ch.OldMeta);
            e.FirstEffect ??= now;
        }
    }

    private bool Ignored(BlockChange ch)
    {
        if (Mechanisms.IsWiring(ch.OldId) || Mechanisms.IsWiring(ch.NewId)) return true;
        if (BlockShapes.IsLiquid(ch.OldId) || BlockShapes.IsLiquid(ch.NewId)) return true;
        if (ch.OldId == Mechanisms.Fire && ch.NewId == 0) return true;
        return _world.SelfEdits.IsRecent(ch.Pos);
    }

    private void StartEpisode(BlockPos trigger, TriggerKind kind, bool turnedOn, DateTimeOffset now, int dim)
    {
        // A new activation of the same trigger ends the previous one's pulse watch.
        _episodes.RemoveAll(e => e.Trigger == trigger && e.Closed);
        var by = WhoNear(trigger, out bool self);
        bool demo = false;
        if (by != null && !self && _demo.TryGetValue(by, out var said) && now - said <= _o.DemoWindow)
        {
            demo = true;
            _demo.Remove(by);
        }
        _episodes.Add(new Episode { Trigger = trigger, Kind = kind, TurnedOn = turnedOn, At = now, Dim = dim, By = by, Demo = demo });
    }

    private string? WhoNear(BlockPos trigger, out bool self)
    {
        self = false;
        var c = trigger.Center;
        string? best = null;
        double bestD = _o.ActivatorRadius;
        foreach (var e in _world.Entities.Nearby(c, _o.ActivatorRadius, e => e.Kind == EntityKind.Player && e.Name != null))
        {
            double d = (e.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0)).DistanceTo(c);
            if (d <= bestD) { bestD = d; best = e.Name; }
        }
        double me = _world.Player.EyePosition.DistanceTo(c);
        if (me <= bestD && me <= _o.ActivatorRadius) { self = true; return SelfName ?? "me"; }
        return best;
    }

    /// <summary>Closes effect windows (→ links) and finishes pulse watches. Call every tick or so.</summary>
    public void Pump()
    {
        var events = new List<MechanismObservation>();
        List<Action> writes;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            int dim = _world.Dimension;
            for (int i = _episodes.Count - 1; i >= 0; i--)
                if (!Advance(_episodes[i], now, dim, events)) _episodes.RemoveAt(i);
            writes = _deferred.ToList();
            _deferred.Clear();
        }
        foreach (var w in writes) w();
        foreach (var ev in events) Observed?.Invoke(ev);
    }

    /// <summary>One episode on a pump: closed once its effect window is over (→ an observation), then its pulse watched until
    /// the effects revert (→ the pulse length) or the watch times out. False = it's done (or in another dimension now).</summary>
    private bool Advance(Episode e, DateTimeOffset now, int dim, List<MechanismObservation> events)
    {
        if (e.Dim != dim) return false;
        if (!e.Closed)
        {
            if (now - e.At < _o.EffectWindow) return true;
            if (!CloseEpisode(e, events)) return false;
        }
        // Pulse watch: the effects reverted → that's the pulse length.
        if (e.Watch is { } w && w.All(x => x.IsIn(_world, on: false)))
        {
            RecordPulse(e, now);
            return false;
        }
        return !(now - e.At > _o.PulseWatch);
    }

    /// <summary>Closes the effect window; true when its pulse is to be watched (a button or plate whose effects were seen).</summary>
    private bool CloseEpisode(Episode e, List<MechanismObservation> events)
    {
        e.Closed = true;
        var obs = Close(e);
        if (obs != null) events.Add(obs);
        return !(obs is null || obs.Miss || e.Kind == TriggerKind.Lever || e.Watch is null);
    }

    /// <summary>The observed pulse (plates: at most the default pulse plus the lag) goes to the link, keeping the shortest seen.</summary>
    private void RecordPulse(Episode e, DateTimeOffset now)
    {
        int ticks = Ticks(now - e.At);
        if (e.Kind != TriggerKind.Button) ticks = Math.Min(ticks, MechanismLink.DefaultPulseTicks + LagOf(e));
        var (trigger, d) = (e.Trigger, e.Dim);
        _deferred.Add(() =>
        {
            if (Graph.Get(trigger, d) is { } link)
                Graph.Put(link with { PulseTicks = link.PulseTicks is { } p ? Math.Min(p, ticks) : ticks });
        });
    }

    private static int Ticks(TimeSpan t) => (int)Math.Floor(t.TotalMilliseconds / 50 + 1e-6);
    private static int LagOf(Episode e) => e.FirstEffect is { } f ? Ticks(f - e.At) : 0;

    private MechanismObservation? Close(Episode e)
    {
        var effects = ObservedEffects(e);
        var old = Graph.Get(e.Trigger, e.Dim);
        if (effects.Count == 0) return Missed(e, old);
        var link = LearnedLink(e, old, effects);
        _deferred.Add(() => Graph.Put(link));
        if (e.Kind != TriggerKind.Lever) e.Watch = effects;
        return new MechanismObservation(link, e.By, old is null, false, e.Demo);
    }

    /// <summary>The cells near the trigger that changed and stayed changed (not mid-move), in the trigger's on direction.</summary>
    private List<MechanismEffect> ObservedEffects(Episode e)
    {
        var effects = new List<MechanismEffect>();
        foreach (var (pos, before) in e.Before)
        {
            var after = new BlockState(_world.GetBlock(pos), _world.GetMeta(pos));
            if (after.Id == Mechanisms.PistonMoving || before.Id == Mechanisms.PistonMoving) continue; // still mid-move
            if (Same(before, after)) continue;
            effects.Add(e.TurnedOn ? new MechanismEffect(pos, before, after) : new MechanismEffect(pos, after, before));
        }
        return effects;
    }

    /// <summary>An activation that changed nothing: a known link loses confidence (forgotten below the threshold unless taught).</summary>
    private MechanismObservation? Missed(Episode e, MechanismLink? old)
    {
        if (old is null) return null;
        var weak = old with { Misses = old.Misses + 1, Confidence = old.Confidence * 0.7, LastSeen = e.At };
        if (weak.Confidence < _o.ForgetBelow && !weak.Taught)
        {
            _deferred.Add(() => Graph.Remove(old.Trigger, old.Dimension));
            return new MechanismObservation(weak, e.By, false, true, false);
        }
        _deferred.Add(() => Graph.Put(weak));
        return new MechanismObservation(weak, e.By, false, true, false);
    }

    /// <summary>The link after this activation: the effects merged with what was seen before, confidence growing with the sightings
    /// (a demonstration or a taught link at least 0.85), the shortest lag.</summary>
    private MechanismLink LearnedLink(Episode e, MechanismLink? old, List<MechanismEffect> effects)
    {
        int lag = LagOf(e);
        int n = (old?.Observations ?? 0) + 1;
        var merged = Merge(old?.Effects, effects, n);
        double conf = Math.Min(0.97, 1 - 0.5 * Math.Pow(0.6, n - 1));
        if (e.Demo || old?.Taught == true) conf = Math.Max(conf, 0.85);
        return new MechanismLink(e.Trigger, e.Kind, merged)
        {
            Dimension = e.Dim,
            PulseTicks = old?.PulseTicks,
            LagTicks = old is null ? lag : Math.Min(old.LagTicks, lag),
            Confidence = conf,
            Observations = n,
            Misses = old?.Misses ?? 0,
            Taught = e.Demo || (old?.Taught ?? false),
            By = e.Demo ? e.By : old?.By ?? e.By,
            Dangerous = Mechanisms.LooksDangerous(_world, e.Trigger, merged),
            LastSeen = e.At,
        };
    }

    private static bool Same(BlockState a, BlockState b)
    {
        if (a.Id != b.Id) return false;
        if (Openables.IsOpenable(a.Id)) return ((a.Meta ^ b.Meta) & Openables.OpenBit) == 0;
        if (Mechanisms.IsPistonBase(a.Id)) return ((a.Meta ^ b.Meta) & 8) == 0;
        return true;
    }

    /// <summary>Union of old and new effects; repeat sightings add support, effects seen in under a third of the
    /// activations (after 3+) are dropped as noise.</summary>
    private static IReadOnlyList<MechanismEffect> Merge(IReadOnlyList<MechanismEffect>? old, List<MechanismEffect> now, int observations)
    {
        if (old is null) return now;
        var list = new List<MechanismEffect>();
        foreach (var o in old)
        {
            var hit = now.FirstOrDefault(x => x.Pos == o.Pos);
            list.Add(hit is null ? o : hit with { Seen = o.Seen + 1 });
        }
        list.AddRange(now.Where(x => old.All(o => o.Pos != x.Pos)));
        if (observations >= 3) list.RemoveAll(x => x.Seen * 3 < observations);
        return list;
    }
}
