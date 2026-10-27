using System.Globalization;

namespace MineClaw.World;

/// <summary>What fires a mechanism. b1.8.1 has exactly these (no wooden button before 1.4).</summary>
public enum TriggerKind { Button, Lever, StonePlate, WoodPlate }

public readonly record struct BlockState(int Id, int Meta)
{
    public override string ToString() => Meta == 0 ? Id.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{Id}:{Meta}");
}

/// <summary>
/// One block a trigger changes, learned empirically: its state while the trigger is off and while it is on. Doors,
/// trapdoors (and gates, which ignore redstone) compare only their open bit; any other block (piston bases, heads and the
/// blocks they move, a hidden wall that appears/disappears) compares its id.
/// </summary>
public sealed record MechanismEffect(BlockPos Pos, BlockState Off, BlockState On, int Seen = 1)
{
    /// <summary>A door/trapdoor whose open bit flips (harmless to stand in when it reverts).</summary>
    public bool IsOpenable => Off.Id == On.Id && Openables.IsOpenable(Off.Id);

    /// <summary>A block that moves/appears/disappears: never be inside its cell when it reverts (pistons push, blocks
    /// suffocate).</summary>
    public bool IsSolidChange => !IsOpenable;

    public BlockState Target(bool on) => on ? On : Off;

    /// <summary>The block at <see cref="Pos"/> is in the trigger's on (or off) state.</summary>
    public bool IsIn(IBlockAccess w, bool on)
    {
        var t = Target(on);
        int id = w.GetBlock(Pos.X, Pos.Y, Pos.Z);
        if (IsOpenable) return id == t.Id && ((w.GetMeta(Pos.X, Pos.Y, Pos.Z) ^ t.Meta) & Openables.OpenBit) == 0;
        if (id != t.Id) return false;
        // Piston bases: the extended bit is the state (facing stays).
        if (Mechanisms.IsPistonBase(id)) return ((w.GetMeta(Pos.X, Pos.Y, Pos.Z) ^ t.Meta) & 8) == 0;
        return true;
    }

    /// <summary>The (id, meta) the cell has in the trigger's on/off state, keeping the current facing bits of openables.</summary>
    public BlockState Apply(IBlockAccess w, bool on)
    {
        var t = Target(on);
        if (!IsOpenable) return t;
        int meta = w.GetBlock(Pos.X, Pos.Y, Pos.Z) == t.Id ? w.GetMeta(Pos.X, Pos.Y, Pos.Z) : t.Meta;
        return new BlockState(t.Id, (meta & ~Openables.OpenBit) | (t.Meta & Openables.OpenBit));
    }

    public string Describe() => IsOpenable
        ? $"{Openables.Name(On.Id)}@{Mechanisms.Fmt(Pos)} {(Openables.IsOpen(On.Meta) ? "opens" : "closes")}"
        : On.Id == 0 ? $"block@{Mechanisms.Fmt(Pos)} vanishes" : Off.Id == 0 ? $"block@{Mechanisms.Fmt(Pos)} appears"
        : $"{Off}->{On}@{Mechanisms.Fmt(Pos)}";
}

/// <summary>
/// A learned cause → effect link: activating <see cref="Trigger"/> changes <see cref="Effects"/>. No redstone simulation:
/// everything here was observed (or taught by a player) and is reinforced by repeats.
/// <para>Timing (b1.8.1, Bukkit mc-dev 218a2a71): a stone button stays pressed 20 ticks (BlockButton.c() = 20, one
/// scheduled tick); a pressure plate (stone: EntityLiving/players, wood: every entity; detection box x/z 0.125..0.875,
/// y 0..0.25) is re-checked every 20 ticks (BlockPressurePlate.c() = 20) and pops on the first check with nobody on it,
/// so it holds ≥ 20 ticks from the press and until the next check after stepping off; a lever latches (no tick).
/// Doors and trapdoors follow redstone at once (BlockDoor/BlockTrapdoor.doPhysics → setDoor, no tick); fence gates
/// ignore redstone; repeaters add 2/4/6/8 ticks (BlockDiode b[] = {1,2,3,4} × 2); pistons move via a 0x36 block
/// action and finish after ~3 ticks (TileEntityPiston progress += 0.5/tick), pushing at most 12 blocks.</para>
/// </summary>
public sealed record MechanismLink(BlockPos Trigger, TriggerKind Kind, IReadOnlyList<MechanismEffect> Effects)
{
    public int Dimension { get; init; }
    /// <summary>Observed ticks from the activation until the effects reverted (button/plate; shortest seen). Null =
    /// never seen revert: the b1.8.1 default (<see cref="DefaultPulseTicks"/>) applies.</summary>
    public int? PulseTicks { get; init; }
    /// <summary>Observed ticks between the activation and the first effect (repeaters, pistons).</summary>
    public int LagTicks { get; init; }
    public double Confidence { get; init; } = 0.5;
    public int Observations { get; init; } = 1;
    /// <summary>Activations that changed nothing (wiring broken, or a different link).</summary>
    public int Misses { get; init; }
    /// <summary>A player demonstrated or told it.</summary>
    public bool Taught { get; init; }
    public string? By { get; init; }
    /// <summary>Linked to TNT, fire or a dispenser (or one sits next to it): never used unless explicitly asked.</summary>
    public bool Dangerous { get; init; }
    public DateTimeOffset LastSeen { get; init; }

    public bool IsPulse => Kind != TriggerKind.Lever;
    public bool IsPlate => Kind is TriggerKind.StonePlate or TriggerKind.WoodPlate;
    public int TriggerId => Mechanisms.TriggerId(Kind);

    /// <summary>BlockButton.c() and BlockPressurePlate.c() are both 20 ticks.</summary>
    public const int DefaultPulseTicks = 20;

    /// <summary>Ticks the effects stay on after the activation (latching levers: effectively forever).</summary>
    public int WindowTicks => IsPulse ? Math.Max(1, PulseTicks ?? DefaultPulseTicks + LagTicks) : int.MaxValue / 4;

    public bool Has(BlockPos p) => Effects.Any(e => e.Pos == p);
    public bool EffectsIn(IBlockAccess w, bool on) => Effects.Count > 0 && Effects.All(e => e.IsIn(w, on));
    public bool HasSolidChanges => Effects.Any(e => e.IsSolidChange);
    public bool MovesPistons => Effects.Any(e => Mechanisms.IsPistonBase(e.On.Id) || Mechanisms.IsPistonBase(e.Off.Id));

    /// <summary>The trigger block is (still) there.</summary>
    public bool TriggerPresent(IBlockAccess w) => w.GetBlock(Trigger.X, Trigger.Y, Trigger.Z) == TriggerId;

    /// <summary>Trigger currently on (lever on, button pressed, plate down).</summary>
    public bool TriggerOn(IBlockAccess w) =>
        Openables.IsActuatorOn(w.GetBlock(Trigger.X, Trigger.Y, Trigger.Z), w.GetMeta(Trigger.X, Trigger.Y, Trigger.Z));

    public string Key => Mechanisms.Fmt(Trigger);

    /// <summary>"button@3,64,0 → iron door@5,64,0 opens, 4 blocks move (pulse 20t, seen 3x)".</summary>
    public string Describe()
    {
        var doors = Effects.Where(e => e.IsOpenable && !Openables.IsUpperHalf(e.On.Id, e.On.Meta)).Select(e => e.Describe()).ToList();
        int solid = Effects.Count(e => e.IsSolidChange && !Mechanisms.IsPistonPart(e.On.Id) && !Mechanisms.IsPistonPart(e.Off.Id));
        var parts = new List<string>(doors.Take(3));
        if (solid > 0) parts.Add($"{solid} block{(solid == 1 ? "" : "s")} move");
        if (MovesPistons) parts.Add("pistons");
        string timing = IsPulse ? $"pulse {WindowTicks}t" : "latches";
        return $"{Mechanisms.TriggerName(Kind)}@{Key} → {(parts.Count == 0 ? "?" : string.Join(", ", parts))} ({timing}, seen {Observations}x"
               + (Taught ? ", taught" : "") + (Dangerous ? ", DANGEROUS" : "") + ")";
    }
}

/// <summary>Helpers shared by the learner, the planner and the skills.</summary>
public static class Mechanisms
{
    public const int Tnt = 46, Dispenser = 23, Fire = 51, StickyPiston = BlockShapes.StickyPiston, Piston = BlockShapes.Piston,
        PistonHead = BlockShapes.PistonHead, PistonMoving = BlockShapes.PistonMoving, RedstoneWire = 55, TorchOff = 75, TorchOn = 76,
        RepeaterOff = BlockShapes.RepeaterOff, RepeaterOn = BlockShapes.RepeaterOn, Obsidian = 49;

    /// <summary>Radius around a trigger in which a TNT block or dispenser makes an unknown trigger untouchable.</summary>
    public const int ExplosiveRadius = 8;

    public static bool IsPistonBase(int id) => id is Piston or StickyPiston;
    public static bool IsPistonPart(int id) => id is Piston or StickyPiston or PistonHead or PistonMoving;
    public static bool IsWiring(int id) => id is RedstoneWire or TorchOff or TorchOn or RepeaterOff or RepeaterOn;
    public static bool IsExplosive(int id) => id is Tnt or Dispenser;

    public static TriggerKind? KindOf(int id) => id switch
    {
        Openables.StoneButton => TriggerKind.Button,
        Openables.Lever => TriggerKind.Lever,
        Openables.StonePlate => TriggerKind.StonePlate,
        Openables.WoodPlate => TriggerKind.WoodPlate,
        _ => null,
    };

    public static int TriggerId(TriggerKind k) => k switch
    {
        TriggerKind.Button => Openables.StoneButton,
        TriggerKind.Lever => Openables.Lever,
        TriggerKind.StonePlate => Openables.StonePlate,
        _ => Openables.WoodPlate,
    };

    public static string TriggerName(TriggerKind k) => k switch
    {
        TriggerKind.Button => "button", TriggerKind.Lever => "lever", TriggerKind.StonePlate => "stone plate", _ => "wood plate",
    };

    public static string Fmt(BlockPos p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}");

    /// <summary>A TNT block or dispenser within <paramref name="r"/> blocks (cube) of <paramref name="p"/>.</summary>
    public static bool ExplosivesNear(IBlockAccess w, BlockPos p, int r = ExplosiveRadius)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        {
            if (!w.IsLoaded(p.X + dx, p.Z + dz)) continue;
            for (int dy = -r; dy <= r; dy++)
            {
                int y = p.Y + dy;
                if (y < 0 || y >= WorldState.Height) continue;
                if (IsExplosive(w.GetBlock(p.X + dx, y, p.Z + dz))) return true;
            }
        }
        return false;
    }

    /// <summary>Linked to TNT/fire (an effect is one), or explosives next to the trigger or an effect.</summary>
    public static bool LooksDangerous(IBlockAccess w, BlockPos trigger, IEnumerable<MechanismEffect> effects)
    {
        foreach (var e in effects)
        {
            if (e.Off.Id is Tnt or Fire || e.On.Id is Tnt or Fire || IsExplosive(e.Off.Id) || IsExplosive(e.On.Id)) return true;
            if (ExplosivesNear(w, e.Pos, 3)) return true;
        }
        return ExplosivesNear(w, trigger, 5);
    }

    /// <summary>Piston facing (meta &amp; 7: 0 down, 1 up, 2 z−, 3 z+, 4 x−, 5 x+) as a unit offset.</summary>
    public static BlockPos Facing(int meta) => (meta & 7) switch
    {
        0 => new BlockPos(0, -1, 0), 1 => new BlockPos(0, 1, 0), 2 => new BlockPos(0, 0, -1),
        3 => new BlockPos(0, 0, 1), 4 => new BlockPos(-1, 0, 0), _ => new BlockPos(1, 0, 0),
    };

    private static readonly BlockPos[] Dirs =
        [new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1), new(-1, 0, 0), new(1, 0, 0)];

    /// <summary>Whether a b1.8.1 piston can push this block (BlockPiston.a(id,...): not obsidian, not unbreakable, not a
    /// tile entity, not an extended piston).</summary>
    public static bool Pushable(IBlockAccess w, int id, int meta)
    {
        if (id == 0 || BlockShapes.IsLiquid(id) || id == Obsidian || id == PistonHead || id == PistonMoving) return false;
        var info = w.Info(id);
        if (info.Hardness < 0 || info.Interactable) return false;
        if (IsPistonBase(id) && (meta & 8) != 0) return false;
        return BlockShapes.MaterialBlocksMovement(id);
    }

    /// <summary>
    /// The base of a retracted piston whose push path includes the empty cell <paramref name="c"/>: facing it directly
    /// (the head would extend into it) or through a contiguous chain of pushable blocks (they would be pushed into it).
    /// Chains longer than <paramref name="maxChain"/> are ignored.
    /// </summary>
    public static BlockPos? PistonThreat(IBlockAccess w, int x, int y, int z, int maxChain = 3)
    {
        for (int d = 0; d < 6; d++)
        {
            var dir = Dirs[d];
            for (int k = 1; k <= maxChain + 1; k++)
            {
                int bx = x - dir.X * k, by = y - dir.Y * k, bz = z - dir.Z * k;
                if (by < 0 || by >= WorldState.Height) break;
                int id = w.GetBlock(bx, by, bz);
                if (id == 0) break;
                int meta = w.GetMeta(bx, by, bz);
                if (IsPistonBase(id))
                {
                    if ((meta & 7) == d && (meta & 8) == 0) return new BlockPos(bx, by, bz);
                    break;
                }
                if (!Pushable(w, id, meta)) break;
            }
        }
        return null;
    }
}

/// <summary>Read-only snapshot of the links of one dimension (what a path search sees; never changes under it).</summary>
public sealed class MechanismView
{
    private readonly Dictionary<BlockPos, MechanismLink> _byTrigger = new();
    private readonly Dictionary<BlockPos, List<MechanismLink>> _byEffect = new();

    public static MechanismView Empty { get; } = new([], 0);

    public MechanismView(IEnumerable<MechanismLink> links, int dimension)
    {
        Dimension = dimension;
        Links = links.ToArray();
        foreach (var l in Links)
        {
            _byTrigger[l.Trigger] = l;
            foreach (var e in l.Effects)
            {
                Index(e.Pos, l);
                // A door effect covers its other half too (only one half may have been recorded).
                if (Openables.IsDoor(e.On.Id)) Index(e.Pos.Up(Openables.IsUpperHalf(e.On.Id, e.On.Meta) ? -1 : 1), l);
            }
        }
    }

    private void Index(BlockPos p, MechanismLink l)
    {
        if (!_byEffect.TryGetValue(p, out var list)) _byEffect[p] = list = new List<MechanismLink>(1);
        if (!list.Contains(l)) list.Add(l);
    }

    public int Dimension { get; }
    public IReadOnlyList<MechanismLink> Links { get; }
    public bool IsEmpty => Links.Count == 0;

    public MechanismLink? ForTrigger(BlockPos p) => _byTrigger.GetValueOrDefault(p);

    /// <summary>Links that change the block at <paramref name="p"/> (door halves count for both).</summary>
    public IReadOnlyList<MechanismLink> Affecting(BlockPos p) => _byEffect.TryGetValue(p, out var l) ? l : [];

    /// <summary>Links with the trigger within <paramref name="radius"/> (cube) of <paramref name="p"/>.</summary>
    public IEnumerable<MechanismLink> TriggersNear(BlockPos p, int radius)
    {
        foreach (var l in Links)
            if (Math.Abs(l.Trigger.X - p.X) <= radius && Math.Abs(l.Trigger.Y - p.Y) <= radius && Math.Abs(l.Trigger.Z - p.Z) <= radius)
                yield return l;
    }
}

/// <summary>
/// The bot's mechanism knowledge (all dimensions), filled by the learner (observations), players (demonstrations,
/// answers) and storage (persisted links). Thread-safe; planners take a <see cref="View"/> snapshot.
/// </summary>
public sealed class MechanismGraph
{
    private readonly object _lock = new();
    private readonly Dictionary<(int Dim, BlockPos Trigger), MechanismLink> _links = new();
    private readonly Dictionary<int, MechanismView> _views = new();

    /// <summary>A link was learned, reinforced, weakened or taught (persist it).</summary>
    public event Action<MechanismLink>? Changed;
    /// <summary>A link was forgotten (trigger gone, contradicted).</summary>
    public event Action<MechanismLink>? Removed;

    public int Count { get { lock (_lock) return _links.Count; } }

    /// <summary>The latest "how do I open this?" asked about a door/wall (subject block, dimension, when): a player's
    /// answer naming a trigger teaches the link (see the Bot's MechanismWatcher).</summary>
    public (BlockPos Subject, int Dim, DateTimeOffset At)? Question { get; set; }

    public MechanismLink? Get(BlockPos trigger, int dimension)
    {
        lock (_lock) return _links.GetValueOrDefault((dimension, trigger));
    }

    public IReadOnlyList<MechanismLink> All(int? dimension = null)
    {
        lock (_lock) return _links.Values.Where(l => dimension is null || l.Dimension == dimension).ToArray();
    }

    public MechanismView View(int dimension)
    {
        lock (_lock)
        {
            if (_views.TryGetValue(dimension, out var v)) return v;
            v = new MechanismView(_links.Values.Where(l => l.Dimension == dimension), dimension);
            _views[dimension] = v;
            return v;
        }
    }

    /// <summary>Adds or replaces a link (keyed by dimension + trigger).</summary>
    public void Put(MechanismLink link, bool notify = true)
    {
        lock (_lock)
        {
            _links[(link.Dimension, link.Trigger)] = link;
            _views.Remove(link.Dimension);
        }
        if (notify) Changed?.Invoke(link);
    }

    /// <summary>Bulk load from storage (no <see cref="Changed"/> events).</summary>
    public void Load(IEnumerable<MechanismLink> links)
    {
        foreach (var l in links) Put(l, notify: false);
    }

    public bool Remove(BlockPos trigger, int dimension)
    {
        MechanismLink? gone;
        lock (_lock)
        {
            if (!_links.Remove((dimension, trigger), out gone)) return false;
            _views.Remove(dimension);
        }
        Removed?.Invoke(gone);
        return true;
    }

    /// <summary>
    /// A player told us ("the button at 3,64,0 opens that door"): records the link from the current world. Doors flip their
    /// open bit (both halves), other blocks vanish (a hidden wall). The world is assumed to be in the trigger's current
    /// state (a lever that is on: what we see now is its on state).
    /// </summary>
    public MechanismLink Teach(IBlockAccess w, BlockPos trigger, IEnumerable<BlockPos> targets, int dimension, string? by,
        DateTimeOffset at, TriggerKind? kind = null)
    {
        var k = kind ?? Mechanisms.KindOf(w.GetBlock(trigger.X, trigger.Y, trigger.Z)) ?? TriggerKind.Button;
        bool on = k == TriggerKind.Lever && Openables.IsActuatorOn(Openables.Lever, w.GetMeta(trigger.X, trigger.Y, trigger.Z));
        var effects = new List<MechanismEffect>();
        void Add(BlockPos p)
        {
            if (effects.Any(e => e.Pos == p)) return;
            int id = w.GetBlock(p.X, p.Y, p.Z), meta = w.GetMeta(p.X, p.Y, p.Z);
            var now = new BlockState(id, meta);
            var other = Openables.IsOpenable(id) ? new BlockState(id, meta ^ Openables.OpenBit) : new BlockState(0, 0);
            effects.Add(on ? new MechanismEffect(p, other, now) : new MechanismEffect(p, now, other));
        }
        foreach (var t in targets)
        {
            var s = Openables.StateBlock(w, t);
            Add(s);
            int id = w.GetBlock(s.X, s.Y, s.Z);
            if (Openables.IsDoor(id) && w.GetBlock(s.X, s.Y + 1, s.Z) == id) Add(s.Up());
        }
        var old = Get(trigger, dimension);
        var link = new MechanismLink(trigger, k, old is null ? effects : Merge(old.Effects, effects))
        {
            Dimension = dimension, Taught = true, By = by, LastSeen = at,
            Confidence = Math.Max(old?.Confidence ?? 0, 0.6), Observations = old?.Observations ?? 0,
            PulseTicks = old?.PulseTicks, LagTicks = old?.LagTicks ?? 0,
            Dangerous = Mechanisms.LooksDangerous(w, trigger, effects),
        };
        Put(link);
        return link;
    }

    private static IReadOnlyList<MechanismEffect> Merge(IReadOnlyList<MechanismEffect> a, IReadOnlyList<MechanismEffect> b) =>
        a.Concat(b.Where(e => a.All(x => x.Pos != e.Pos))).ToArray();
}
