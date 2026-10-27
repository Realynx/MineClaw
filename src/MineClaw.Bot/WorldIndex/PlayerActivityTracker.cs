using System.Collections.Concurrent;
using System.Globalization;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// The bot's "subconscious" for other players: turns block changes, swings, entity spawns/removals, signs, chat lines
/// and presence into attributed <see cref="ActivityEvent"/>s with a confidence, merges them into per-player sessions,
/// writes raw events (<see cref="ActivityLog"/>, FTS only) and rollups (embedded episodes), feeds the
/// <see cref="ActivityDigest"/> and posts the rare wake-worthy events. Deterministic templates, no LLM.
/// <para>Threading: event handlers capture a tiny snapshot and enqueue; everything else runs in <see cref="Pump"/>.</para>
/// </summary>
public sealed class PlayerActivityTracker : IDisposable
{
    private abstract record Input(DateTimeOffset At);
    /// <param name="Self">The bot's own dig/placement (its outbound 0x0E/0x0F, <see cref="WorldState.SelfEdits"/>): never a player's.</param>
    private sealed record BlockIn(DateTimeOffset At, BlockChange Ch, PlayerSample[] Near, PlayerSample? Bot, bool BotBusy, bool Self = false) : Input(At);
    private sealed record AnimIn(DateTimeOffset At, int Entity, int Anim) : Input(At);
    private sealed record BlockActionIn(DateTimeOffset At, BlockPos Pos, PlayerSample[] Near) : Input(At);
    private sealed record ChatIn(DateTimeOffset At, string Raw) : Input(At);
    private sealed record PresenceIn(DateTimeOffset At, string Name, bool Joined) : Input(At);
    private sealed record CollectIn(DateTimeOffset At, int ItemEntity, int Collector, ItemStack Item) : Input(At);
    private sealed record EquipIn(DateTimeOffset At, int Entity, int Slot, int Item) : Input(At);
    private sealed record StatusIn(DateTimeOffset At, int Entity, int Status) : Input(At);
    private sealed record BedIn(DateTimeOffset At, int Entity, BlockPos Pos) : Input(At);
    private sealed record AttachIn(DateTimeOffset At, int Entity, int Vehicle) : Input(At);
    private sealed record SignIn(DateTimeOffset At, BlockPos Pos, string[] Lines, PlayerSample[] Near) : Input(At);
    private sealed record SpawnIn(DateTimeOffset At, Entity E, PlayerSample[] Near) : Input(At);
    private sealed record RemovedIn(DateTimeOffset At, Entity E, PlayerSample[] Near) : Input(At);
    private sealed record SnapshotIn(DateTimeOffset At, string Kind, string Key, BlockPos Pos, IReadOnlyList<ItemCount> Items, bool First) : Input(At);

    private sealed class PlayerState
    {
        public int EntityId = -1;
        public PlayerSample Last;
        public PlaceRef? Place;
        public PlaceRef? Candidate;
        public DateTimeOffset CandidateSince, PlaceSince;
        public int HeldItem, VehicleId = -1;
        public readonly Queue<(DateTimeOffset At, Vec3d Pos)> Trail = new();
        public readonly Queue<DateTimeOffset> Rate = new();
        public readonly HashSet<int> HeldBefore = new();
        public bool LingerFlagged;
    }

    private sealed class Session
    {
        public string Player = "";
        public PlaceRef? Place;
        public DateTimeOffset Start, Last;
        public readonly List<ActivityEvent> Events = new();
        public BlockPos Min, Max;
        public bool HasBounds;
        public int Dimension;
    }

    private readonly WorldState _world;
    private readonly IPlayerEventSource _src;
    private readonly ActivityLog? _log;
    private readonly WorldIndexer? _index;
    private readonly ActivityOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _self;
    private readonly Func<bool>? _botBusy;
    private readonly ChatLineClassifier _classifier;
    private readonly DeathLineParser _deaths;
    private readonly WakeGate _gate;
    private readonly object _pumpLock = new();
    private readonly ConcurrentQueue<Input> _in = new();
    private readonly List<Action> _unsub = new();

    private readonly Dictionary<string, PlayerState> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DateTimeOffset> _swings = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ActivityEvent> _pendingWrites = new();
    private readonly List<ActivityEvent> _recent = new();
    private readonly List<ActivityRollup> _rollups = new();
    private readonly HashSet<int> _collected = new();
    private readonly List<(DateTimeOffset At, BlockPos Pos)> _recentBreaks = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<ItemCount> Items)> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<BlockPos, (string Player, DateTimeOffset At, double Conf)> _signPlacers = new();
    private readonly HashSet<string> _seenFirst = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _globalRate = new();
    private DateTimeOffset _lastPrune;
    private readonly IDimensionSource _dims;
    private volatile int _dim;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PlayerActivityTracker(WorldState world, IPlayerEventSource events, ActivityLog? log = null, WorldIndexer? indexer = null,
        ActivityOptions? options = null, string? selfName = null, Func<IEnumerable<string>>? botNames = null, Func<bool>? botBusy = null,
        TimeProvider? time = null, ILogger? logger = null, ChatLineClassifier? classifier = null, IDimensionSource? dimensions = null)
    {
        _world = world;
        _dims = dimensions ?? new WorldDimensionSource(world);
        _dim = _dims.Dimension;
        if (log != null) log.CurrentDimension = () => _dim;
        _src = events;
        _log = log;
        _index = indexer;
        _o = options ?? new ActivityOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _self = selfName ?? "";
        BotNames = botNames ?? (() => _self.Length > 0 ? [_self] : []);
        _botBusy = botBusy;
        _classifier = classifier ?? new ChatLineClassifier();
        _deaths = new DeathLineParser(_o.DeathPatterns);
        _gate = new WakeGate(_time);
        Danger = new DangerMonitor(world, _o, _time, _gate,
            (x, z) => indexer?.IslandBottom(x, z),
            p => indexer?.PlaceAt(p, 3));
        Danger.Wake += e => Wake?.Invoke(e);
        Danger.Activity += e => Record(e);
        Digest = new ActivityDigest(this, _o, _time);
    }

    public ActivityOptions Options => _o;
    public DangerMonitor Danger { get; }
    public ActivityDigest Digest { get; }
    public ActivityLog? Log => _log;
    /// <summary>Names the bot answers to (signs mentioning them wake the brain).</summary>
    public Func<IEnumerable<string>> BotNames { get; set; }
    /// <summary>Optional: the taught structure (memory) at a position, for how-to evidence.</summary>
    public Func<BlockPos, string?>? TaughtStructureAt { get; set; }

    /// <summary>Wake-worthy (or context) events for the agent's EventQueue.</summary>
    public event Action<GameEvent>? Wake;
    /// <summary>Every recorded raw event (the demonstration recorder can subscribe instead of re-deriving block events).</summary>
    public event Action<ActivityEvent>? EventRecorded;
    /// <summary>Closed sessions.</summary>
    public event Action<ActivityRollup>? RollupClosed;
    /// <summary>A rollup at a taught structure: evidence for its how-to steps (structure name, rollup).</summary>
    public event Action<string, ActivityRollup>? StructureEvidence;

    /// <summary>The dimension sessions/digest describe (the bot's; b1.8 shows players only in your own dimension).</summary>
    public int Dimension => _dim;
    public long DimensionSwitches { get; private set; }

    public long Dropped { get; private set; }
    /// <summary>Block changes skipped because the bot itself made them.</summary>
    public long SelfEdits { get; private set; }

    // ------------------------------------------------------------------ lifecycle

    public PlayerActivityTracker Attach()
    {
        if (_unsub.Count > 0) return this;
        Action<BlockChange> onBlock = OnBlockChanged;
        Action<Entity> onSpawn = e =>
        {
            if (e.Kind is EntityKind.Item or EntityKind.Player) _in.Enqueue(new SpawnIn(_time.GetUtcNow(), e, NearPlayers(e.Position, 3)));
        };
        Action<Entity> onRemoved = e =>
        {
            if (e.Kind is EntityKind.Item or EntityKind.Player) _in.Enqueue(new RemovedIn(_time.GetUtcNow(), e, e.Kind == EntityKind.Item ? NearPlayers(e.Position, 2.5) : []));
        };
        Action<int, int> onAnim = (id, a) => _in.Enqueue(new AnimIn(_time.GetUtcNow(), id, a));
        Action<BlockPos, int, int> onAction = (p, _, _) => _in.Enqueue(new BlockActionIn(_time.GetUtcNow(), p, NearPlayers(p.Center, _o.Reach + 1)));
        Action<string> onChat = raw => _in.Enqueue(new ChatIn(_time.GetUtcNow(), raw));
        Action<string> onJoin = n => _in.Enqueue(new PresenceIn(_time.GetUtcNow(), n, true));
        Action<string> onLeave = n => _in.Enqueue(new PresenceIn(_time.GetUtcNow(), n, false));
        Action<int, int> onCollect = (item, who) =>
            _in.Enqueue(new CollectIn(_time.GetUtcNow(), item, who, _world.Entities.Get(item)?.Item ?? ItemStack.Empty));
        Action<int, int, int, int> onEquip = (id, slot, item, _) => _in.Enqueue(new EquipIn(_time.GetUtcNow(), id, slot, item));
        Action<int, int> onStatus = (id, st) => _in.Enqueue(new StatusIn(_time.GetUtcNow(), id, st));
        Action<int, BlockPos> onBed = (id, p) => _in.Enqueue(new BedIn(_time.GetUtcNow(), id, p));
        Action<int, int> onAttach = (id, v) => _in.Enqueue(new AttachIn(_time.GetUtcNow(), id, v));
        Action<BlockPos, string[]> onSign = (p, l) => _in.Enqueue(new SignIn(_time.GetUtcNow(), p, l, NearPlayers(p.Center, 8)));

        _world.BlockChanged += onBlock;
        _world.Entities.Spawned += onSpawn;
        _world.Entities.Removed += onRemoved;
        _src.EntityAnimated += onAnim;
        _src.BlockAction += onAction;
        _src.ChatReceived += onChat;
        _src.PlayerJoined += onJoin;
        _src.PlayerLeft += onLeave;
        _src.ItemCollected += onCollect;
        _src.EquipmentChanged += onEquip;
        _src.EntityStatus += onStatus;
        _src.UsedBed += onBed;
        _src.EntityAttached += onAttach;
        _src.SignUpdated += onSign;
        _unsub.Add(() =>
        {
            _world.BlockChanged -= onBlock;
            _world.Entities.Spawned -= onSpawn;
            _world.Entities.Removed -= onRemoved;
            _src.EntityAnimated -= onAnim;
            _src.BlockAction -= onAction;
            _src.ChatReceived -= onChat;
            _src.PlayerJoined -= onJoin;
            _src.PlayerLeft -= onLeave;
            _src.ItemCollected -= onCollect;
            _src.EquipmentChanged -= onEquip;
            _src.EntityStatus -= onStatus;
            _src.UsedBed -= onBed;
            _src.EntityAttached -= onAttach;
            _src.SignUpdated -= onSign;
        });
        if (_index != null)
        {
            Action<BlockPos, string[]> polledSign = (p, l) => _in.Enqueue(new SignIn(_time.GetUtcNow(), p, l, NearPlayers(p.Center, 8)));
            Action<string, string, BlockPos, IReadOnlyList<ItemCount>, bool> snap = (k, key, p, items, first) =>
                _in.Enqueue(new SnapshotIn(_time.GetUtcNow(), k, key, p, items, first));
            Action<UnknownStructure> unknown = OnUnknownStructure;
            _index.SignChanged += polledSign;
            _index.ContainerSnapshot += snap;
            _index.UnknownStructureFound += unknown;
            _unsub.Add(() => { _index.SignChanged -= polledSign; _index.ContainerSnapshot -= snap; _index.UnknownStructureFound -= unknown; });
        }
        return this;
    }

    public void Start()
    {
        if (_loop != null || !_o.Enabled) return;
        Attach();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(50, _o.PassIntervalMs));
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(interval, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { Pump(); }
                catch (Exception ex) { _logger.LogWarning(ex, "activity tracker pass failed"); }
            }
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancelled */ }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        lock (_pumpLock) { CloseSessions(force: true); FlushWrites(); }
        foreach (var u in _unsub) u();
        _unsub.Clear();
        (_src as IDisposable)?.Dispose();
    }

    // ------------------------------------------------------------------ handlers (network thread)

    private static readonly int[] Replaceable = [0, Blocks.TallGrass, Blocks.SnowLayer, Blocks.DeadBush, Blocks.Fire];

    private PlayerSample[] NearPlayers(Vec3d at, double radius)
    {
        var list = _world.Entities.Nearby(at, radius, e => e.Kind == EntityKind.Player && e.Name != null);
        if (list.Count == 0) return [];
        var arr = new PlayerSample[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = Sample(list[i]);
        return arr;
    }

    private static PlayerSample Sample(Entity e) => new(e.Id, e.Name!, e.Position, e.Yaw, e.Pitch, e.IsCrouched, e.VehicleId, e.HeldItem);

    private void OnBlockChanged(BlockChange ch)
    {
        if (ch.OldId == ch.NewId) return; // growth stages, doors, levers: not attributed
        bool oldFluid = Blocks.IsWater(ch.OldId) || Blocks.IsLava(ch.OldId), newFluid = Blocks.IsWater(ch.NewId) || Blocks.IsLava(ch.NewId);
        if ((oldFluid || ch.OldId == 0) && (newFluid || ch.NewId == 0)) return; // fluid flow
        var near = NearPlayers(ch.Pos.Center, _o.Reach + 2);
        var me = _world.Player.Snapshot;
        PlayerSample? bot = me.Position.DistanceTo(ch.Pos.Center) <= _o.Reach + 2
            ? new PlayerSample(me.EntityId, _self, me.Position, me.Yaw, me.Pitch, false, -1, 0) : null;
        if (near.Length == 0) return;
        _in.Enqueue(new BlockIn(_time.GetUtcNow(), ch, near, bot, _botBusy?.Invoke() ?? false, _world.SelfEdits.IsRecent(ch.Pos)));
    }

    private void OnUnknownStructure(UnknownStructure u)
    {
        if (!_o.Wake.NewUnknownStructure) return;
        if (!_gate.TryWake("*", "unknown:" + u.Key, DangerLevel.Watch, TimeSpan.FromMinutes(10), _o.Wake.MaxWakesPer5Min)) return;
        Wake?.Invoke(GameEvent.Activity("new unknown build: " + Trim(u.Description, 140), null, EventPriority.Low) with { At = _time.GetUtcNow() });
    }

    // ------------------------------------------------------------------ the pass

    public bool HasPendingWork => !_in.IsEmpty;

    public void Pump()
    {
        lock (_pumpLock)
        {
            var now = _time.GetUtcNow();
            if (_dims.Dimension != _dim) SwitchDimension(_dims.Dimension);
            int n = 0;
            while (n++ < 20_000 && _in.TryDequeue(out var input))
            {
                try { Handle(input); }
                catch (Exception ex) { _logger.LogDebug(ex, "activity input failed"); }
            }
            SamplePlayers(now);
            Danger.Tick();
            CloseSessions(force: false);
            FlushWrites();
            if (_log != null && now - _lastPrune > TimeSpan.FromHours(1))
            {
                _lastPrune = now;
                try { _log.Prune(now); } catch (Exception ex) { _logger.LogDebug(ex, "activity prune failed"); }
            }
        }
    }

    /// <summary>
    /// The bot changed dimension: sessions of the old one close now (stored with their dimension), and everything tied to
    /// entities/blocks of the old world is dropped. Queued inputs are discarded: they were sampled in a world that is gone.
    /// </summary>
    private void SwitchDimension(int to)
    {
        while (_in.TryDequeue(out _)) { }
        CloseSessions(force: true);
        FlushWrites();
        _players.Clear();
        _swings.Clear();
        _recentBreaks.Clear();
        _containers.Clear();
        _signPlacers.Clear();
        _collected.Clear();
        Danger.ResetTracks();
        _dim = to;
        DimensionSwitches++;
    }

    private void Handle(Input input)
    {
        switch (input)
        {
            case AnimIn a:
                _swings[a.Entity] = a.At;
                if (a.Anim == 2 && NameOf(a.Entity) is { } hurt) Danger.Hurt(hurt, _world.Entities.Get(a.Entity)?.Position);
                break;
            case BlockIn b: HandleBlock(b); break;
            case BlockActionIn ba:
                if (_world.GetBlock(ba.Pos) == Blocks.Chest && Attribute(ba.Pos, ba.Near, null, false, ba.At, needSwing: false) is { } who)
                    Record(new ActivityEvent(who.Name, ActivityKinds.Opened, ba.At, who.Conf) { BlockId = Blocks.Chest, Pos = ba.Pos, Place = Place(ba.Pos, 1) });
                break;
            case ChatIn c: HandleChat(c); break;
            case PresenceIn p:
                if (p.Name.Equals(_self, StringComparison.OrdinalIgnoreCase)) break;
                Record(new ActivityEvent(p.Name, p.Joined ? ActivityKinds.Joined : ActivityKinds.Left, p.At, 1.0));
                break;
            case CollectIn ci:
                _collected.Add(ci.ItemEntity);
                if (NameOf(ci.Collector) is { } col && !ci.Item.IsEmpty)
                    Record(new ActivityEvent(col, ActivityKinds.PickedUp, ci.At, 1.0) { ItemId = ci.Item.Id, Count = ci.Item.Count });
                break;
            case EquipIn eq:
                if (eq.Slot == 0 && NameOf(eq.Entity) is { } eqn) HeldChanged(eqn, eq.Item, eq.At);
                break;
            case StatusIn st:
                if (NameOf(st.Entity) is { } stn)
                {
                    if (st.Status == 2) Danger.Hurt(stn, _world.Entities.Get(st.Entity)?.Position);
                    else if (st.Status == 3) Danger.DeathStatus(stn);
                }
                break;
            case BedIn bed:
                if (NameOf(bed.Entity) is { } bn) Record(new ActivityEvent(bn, ActivityKinds.Slept, bed.At, 1.0) { Pos = bed.Pos, Place = Place(bed.Pos, 2) });
                break;
            case AttachIn at:
                if (NameOf(at.Entity) is { } an) VehicleChanged(an, at.Vehicle, at.At);
                break;
            case SignIn si: HandleSign(si); break;
            case SpawnIn sp: HandleSpawn(sp); break;
            case RemovedIn rm: HandleRemoved(rm); break;
            case SnapshotIn sn: HandleSnapshot(sn); break;
        }
    }

    private string? NameOf(int entityId)
    {
        var e = _world.Entities.Get(entityId);
        if (e is { Kind: EntityKind.Player, Name: { } n }) return n;
        foreach (var (name, st) in _players) if (st.EntityId == entityId) return name;
        return null;
    }

    private PlaceRef? Place(BlockPos p, double radius = 3) => _index?.PlaceAt(p, radius);

    // ------------------------------------------------------------------ blocks (attribution with confounders)

    /// <summary>Changes the world makes by itself: never a player action.</summary>
    private bool IsPhysics(BlockChange ch)
    {
        var p = ch.Pos;
        int o = ch.OldId, n = ch.NewId;
        if (Blocks.IsWater(o) || Blocks.IsLava(o) || Blocks.IsWater(n) || Blocks.IsLava(n)) return true;
        if (o is Blocks.Fire || n is Blocks.Fire || o is Blocks.PistonMoving or Blocks.PistonHead || n is Blocks.PistonMoving or Blocks.PistonHead) return true;
        // Generator refill / lava meeting water.
        if (n is Blocks.Cobblestone or Blocks.Stone or Blocks.Obsidian && Replaceable.Contains(o))
        {
            if (_index?.IsGeneratorCell(p) == true) return true;
            bool lava = false, water = false;
            foreach (var d in new[] { new BlockPos(1, 0, 0), new BlockPos(-1, 0, 0), new BlockPos(0, 0, 1), new BlockPos(0, 0, -1), new BlockPos(0, 1, 0), new BlockPos(0, -1, 0) })
            {
                int id = _world.GetBlock(p + d);
                lava |= Blocks.IsLava(id);
                water |= Blocks.IsWater(id);
            }
            if (lava && water) return true;
        }
        // Gravity: sand/gravel leaving an unsupported cell or landing.
        if (o is Blocks.Sand or Blocks.Gravel && n == 0 && Replaceable.Contains(_world.GetBlock(p.Down()))) return true;
        if (n is Blocks.Sand or Blocks.Gravel && o == 0 && _world.Entities.Nearby(p.Center, 2, e => e.Kind == EntityKind.Object && e.TypeId is 70 or 71).Count > 0) return true;
        // Natural spread/decay/growth.
        if ((o, n) is (Blocks.Grass, Blocks.Dirt) or (Blocks.Dirt, Blocks.Grass) or (Blocks.Farmland, Blocks.Dirt)
            or (Blocks.Ice, _) or (Blocks.SnowLayer, 0) or (0, Blocks.SnowLayer) or (Blocks.Leaves, 0)) return true;
        if (n is Blocks.SugarCane or Blocks.Cactus && o == 0 && _world.GetBlock(p.Down()) == n) return true;   // growing
        if (n is Blocks.Melon or Blocks.Pumpkin && o == 0 && HasNeighbour(p, n == Blocks.Melon ? Blocks.MelonStem : Blocks.PumpkinStem)) return true;
        if (n == Blocks.Log && o is Blocks.Sapling) return true; // a sapling grew
        if (n is Blocks.Leaves && o is 0 or Blocks.Sapling) return true;
        // Cascades: things that pop when their support goes.
        if (n == 0 && o is Blocks.SugarCane or Blocks.Cactus && _world.GetBlock(p.Down()) is 0) return true;
        if (n == 0 && o is Blocks.Torch or Blocks.Crops or Blocks.Sapling or Blocks.Dandelion or Blocks.Rose or Blocks.TallGrass or Blocks.RedstoneWire
                or Blocks.BrownMushroom or Blocks.RedMushroom or Blocks.SignPost or Blocks.WoodenDoor or Blocks.Rail
            && _world.GetBlock(p.Down()) is 0 && RecentlyBroken(p.Down(), ch)) return true;
        if (o == Blocks.LitFurnace && n == Blocks.Furnace) return true; // burned out
        return false;
    }

    private bool HasNeighbour(BlockPos p, int id) =>
        _world.GetBlock(p.Offset(1, 0, 0)) == id || _world.GetBlock(p.Offset(-1, 0, 0)) == id ||
        _world.GetBlock(p.Offset(0, 0, 1)) == id || _world.GetBlock(p.Offset(0, 0, -1)) == id;

    private bool RecentlyBroken(BlockPos p, BlockChange _) => _recentBreaks.Any(b => b.Pos == p);

    private (string Name, double Conf)? Attribute(BlockPos target, PlayerSample[] near, PlayerSample? bot, bool botBusy, DateTimeOffset at, bool needSwing)
    {
        var center = target.Center;
        double Score(PlayerSample s, bool swungOverride)
        {
            var eye = s.Pos + new Vec3d(0, PlayerPhysics.EyeHeight, 0);
            double d = eye.DistanceTo(center);
            if (d > _o.Reach) return 0;
            var look = PlayerPhysics.LookVector(s.Yaw, s.Pitch);
            double cos = look.Dot((center - eye).Normalized());
            double facing = d < 1.2 ? 1 : Math.Clamp((cos - 0.55) / 0.4, 0, 1);
            bool swung = swungOverride || (_swings.TryGetValue(s.EntityId, out var sw) && at - sw <= TimeSpan.FromSeconds(_o.SwingWindowSeconds) && at >= sw - TimeSpan.FromMilliseconds(200));
            if (needSwing && !swung) return 0;
            double score = 0.5 * facing + 0.3 * (swung ? 1 : 0) + 0.2 * (1 - d / _o.Reach);
            if (facing == 0 && !swung) score *= 0.3;
            return score;
        }
        (string Name, double Score)? best = null, second = null;
        foreach (var s in near)
        {
            if (s.Name.Equals(_self, StringComparison.OrdinalIgnoreCase)) continue;
            double sc = Score(s, false);
            if (sc <= 0) continue;
            if (best == null || sc > best.Value.Score) { second = best; best = (s.Name, sc); }
            else if (second == null || sc > second.Value.Score) second = (s.Name, sc);
        }
        if (best == null) return null;
        if (bot is { } b && botBusy && Score(b, true) >= best.Value.Score) return null; // the bot did it
        double conf = best.Value.Score;
        if (second is { } s2)
        {
            double margin = best.Value.Score - s2.Score;
            conf *= margin < 0.15 ? 0.4 : margin < 0.3 ? 0.8 : 1.0; // ambiguous → low: kept out of the digest
        }
        return conf < _o.MinLogConfidence ? null : (best.Value.Name, Math.Round(conf, 2));
    }

    private void HandleBlock(BlockIn b)
    {
        var ch = b.Ch;
        if (IsPhysics(ch)) return;
        bool placed = Replaceable.Contains(ch.OldId) && ch.NewId != 0;
        bool broke = ch.OldId != 0 && !Replaceable.Contains(ch.OldId) && Replaceable.Contains(ch.NewId);
        bool lit = ch.OldId == Blocks.Furnace && ch.NewId == Blocks.LitFurnace;
        if (!placed && !broke && !lit) return;
        if (broke) { _recentBreaks.Add((b.At, ch.Pos)); if (_recentBreaks.Count > 64) _recentBreaks.RemoveRange(0, 32); }
        if (b.Self) { SelfEdits++; return; } // the bot did it (kept above only so its drops/cascades aren't blamed on anyone)
        // Blocks that change by themselves too often need a swing to count.
        bool needSwing = ch.OldId is Blocks.Leaves || ch.NewId is Blocks.Sand or Blocks.Gravel;
        var who = Attribute(ch.Pos, b.Near, b.Bot, b.BotBusy, b.At, needSwing);
        if (who is not { } w) return;
        string kind = lit ? ActivityKinds.LitFurnace
            : broke ? (ActivityKinds.IsCrop(ch.OldId) ? ActivityKinds.Harvested : ActivityKinds.Broke)
            : (ActivityKinds.IsCrop(ch.NewId) ? ActivityKinds.Planted : ActivityKinds.Placed);
        int block = broke ? ch.OldId : ch.NewId;
        if (block is Blocks.SignPost or Blocks.WallSign && placed) { _signPlacers[ch.Pos] = (w.Name, b.At, w.Conf); return; } // text follows
        Danger.BlockWork(w.Name, ch.Pos);
        Record(new ActivityEvent(w.Name, kind, b.At, w.Conf) { BlockId = block, Pos = ch.Pos, Place = Place(ch.Pos) });
    }

    // ------------------------------------------------------------------ chat, signs, items, containers

    private void HandleChat(ChatIn c)
    {
        var line = _classifier.Classify(c.Raw);
        switch (line.Kind)
        {
            case ChatLineKind.Player or ChatLineKind.Emote or ChatLineKind.Whisper when line.Sender is { } s && !s.Equals(_self, StringComparison.OrdinalIgnoreCase):
                if (line.Kind == ChatLineKind.Whisper) break; // private: not logged
                var text = _o.ChatChars <= 0 ? null : Trim(line.Message ?? "", _o.ChatChars);
                Record(new ActivityEvent(s, ActivityKinds.Chat, c.At, 1.0) { Detail = text });
                break;
            case ChatLineKind.System:
                if (_deaths.Parse(line) is { } d && !d.Player.Equals(_self, StringComparison.OrdinalIgnoreCase)) Danger.DeathLine(d.Player, d.Cause);
                break;
        }
    }

    private void HandleSign(SignIn s)
    {
        var label = WorldIndexer.SignLabel(s.Lines);
        if (label.Length == 0) return;
        if (_seenFirst.Contains("sign:" + s.Pos + label)) return;
        _seenFirst.Add("sign:" + s.Pos + label);
        // Prefer whoever placed the sign block; else the nearest player facing it (an edit, or text arriving late).
        (string Name, double Conf)? who = null;
        if (_signPlacers.Remove(s.Pos, out var placer) && s.At - placer.At <= TimeSpan.FromMinutes(2)) who = (placer.Player, placer.Conf);
        else if (s.Near.Length > 0 && Attribute(s.Pos, s.Near, null, false, s.At, needSwing: false) is { } a) who = (a.Name, a.Conf * 0.8);
        if (who is not { } w) return; // pre-existing sign seen on chunk load: the indexer labels it, nobody "wrote" it now
        Record(new ActivityEvent(w.Name, ActivityKinds.WroteSign, s.At, w.Conf) { Detail = label, Pos = s.Pos, Place = Place(s.Pos, 4) });
        if (_o.Wake.SignMentionsBot && BotNames().Any(n => n.Length >= 3 && label.Contains(n, StringComparison.OrdinalIgnoreCase))
            && _gate.TryWake(w.Name, "sign", DangerLevel.Danger, TimeSpan.FromSeconds(_o.Wake.CooldownSeconds), _o.Wake.MaxWakesPer5Min))
            Wake?.Invoke(GameEvent.Activity($"{w.Name} placed a sign naming you: \"{label}\"{ActivityText.At(Place(s.Pos, 4), s.Pos)}", w.Name, EventPriority.Normal) with { At = s.At });
    }

    private void HandleSpawn(SpawnIn s)
    {
        if (s.E.Kind == EntityKind.Player && s.E.Name is { } pn)
        {
            Danger.Appeared(pn, s.E.Position);
            return;
        }
        if (s.E.Item.IsEmpty || s.Near.Length == 0) return;
        // Block drops spawn at the broken block; a thrown item spawns at the thrower's eye height.
        if (_recentBreaks.Any(b => s.At - b.At <= TimeSpan.FromSeconds(1.5) && b.Pos.Center.DistanceTo(s.E.Position) <= 1.5)) return;
        var cands = s.Near.Where(p => !p.Name.Equals(_self, StringComparison.OrdinalIgnoreCase)
                                      && Math.Sqrt(Math.Pow(p.Pos.X - s.E.Position.X, 2) + Math.Pow(p.Pos.Z - s.E.Position.Z, 2)) <= 1.6
                                      && s.E.Position.Y - p.Pos.Y is > 0.7 and < 2.0).ToList();
        if (cands.Count != 1) return;
        var c = Cell(s.E.Position);
        Record(new ActivityEvent(cands[0].Name, ActivityKinds.Dropped, s.At, 0.75) { ItemId = s.E.Item.Id, Count = s.E.Item.Count, Pos = c, Place = Place(c) });
    }

    private void HandleRemoved(RemovedIn r)
    {
        if (r.E.Kind == EntityKind.Player && r.E.Name is { } pn)
        {
            Danger.Vanished(pn, r.E.Position);
            return;
        }
        if (_collected.Remove(r.E.Id) || r.E.Item.IsEmpty) return; // exact 0x16 already recorded
        if (DateTime.UtcNow - r.E.SpawnedAt > TimeSpan.FromMinutes(4.5)) return; // despawn
        var cands = r.Near.Where(p => !p.Name.Equals(_self, StringComparison.OrdinalIgnoreCase) && p.Pos.DistanceTo(r.E.Position) <= 1.8).ToList();
        if (cands.Count != 1) return;
        Record(new ActivityEvent(cands[0].Name, ActivityKinds.PickedUp, r.At, 0.6) { ItemId = r.E.Item.Id, Count = r.E.Item.Count });
    }

    private void HandleSnapshot(SnapshotIn s)
    {
        var id = s.Kind + "|" + s.Key;
        _containers.TryGetValue(id, out var prev);
        _containers[id] = (s.At, s.Items);
        if (!s.First || prev.Items == null) return;
        // Contents changed between the bot's last look and this one: someone else used it.
        var before = prev.Items.GroupBy(i => (i.ItemId, i.Damage)).ToDictionary(g => g.Key, g => g.Sum(i => i.Count));
        var after = s.Items.GroupBy(i => (i.ItemId, i.Damage)).ToDictionary(g => g.Key, g => g.Sum(i => i.Count));
        var diffs = before.Keys.Union(after.Keys)
            .Select(k => (Key: k, Delta: after.GetValueOrDefault(k) - before.GetValueOrDefault(k))).Where(d => d.Delta != 0)
            .OrderByDescending(d => Math.Abs(d.Delta)).Take(3).ToList();
        if (diffs.Count == 0) return;
        var visitors = _players.Where(kv => kv.Value.Trail.Any(t => t.At >= prev.At && t.Pos.DistanceTo(s.Pos.Center) <= 5)).Select(kv => kv.Key).ToList();
        var place = Place(s.Pos, 1);
        string who = visitors.Count == 1 ? visitors[0] : "someone";
        double conf = visitors.Count == 1 ? 0.75 : 0.4;
        if (visitors.Count == 1)
            foreach (var (k, delta) in diffs)
                Record(new ActivityEvent(who, delta < 0 ? ActivityKinds.Took : ActivityKinds.Stored, s.At, conf) { ItemId = k.ItemId, Count = Math.Abs(delta), Pos = s.Pos, Place = place });
        if (_o.Wake.ManagedChestChanged && _gate.TryWake(who, "chest:" + s.Key, DangerLevel.Watch, TimeSpan.FromSeconds(_o.Wake.CooldownSeconds * 5), _o.Wake.MaxWakesPer5Min))
        {
            var parts = string.Join(", ", diffs.Select(d => $"{(d.Delta < 0 ? "-" : "+")}{Math.Abs(d.Delta)} {ActivityText.Item(d.Key.ItemId)}"));
            Wake?.Invoke(GameEvent.Activity($"{(visitors.Count == 1 ? "probably " + who : "someone")} changed the {place?.Name ?? "chest"} you manage: {parts}",
                visitors.Count == 1 ? who : null, EventPriority.Low) with { At = s.At });
        }
    }

    // ------------------------------------------------------------------ per-pass player sampling (presence, places, held items, danger)

    private void SamplePlayers(DateTimeOffset now)
    {
        foreach (var e in _world.Entities.All())
        {
            if (e.Kind != EntityKind.Player || e.Name is not { } name || name.Equals(_self, StringComparison.OrdinalIgnoreCase)) continue;
            var s = Sample(e);
            if (!_players.TryGetValue(name, out var st))
            {
                _players[name] = st = new PlayerState { EntityId = e.Id, HeldItem = e.HeldItem, VehicleId = e.VehicleId, PlaceSince = now };
                if (e.HeldItem > 0) st.HeldBefore.Add(e.HeldItem);
            }
            st.EntityId = e.Id;
            st.Last = s;
            Danger.Sample(s);
            if (st.Trail.Count == 0 || now - st.Trail.Last().At >= TimeSpan.FromSeconds(1)) st.Trail.Enqueue((now, s.Pos));
            while (st.Trail.Count > 0 && now - st.Trail.Peek().At > TimeSpan.FromMinutes(15)) st.Trail.Dequeue();
            if (e.HeldItem != st.HeldItem) HeldChanged(name, e.HeldItem, now);   // polled 0x05 slot 0
            if (e.VehicleId != st.VehicleId) VehicleChanged(name, e.VehicleId, now); // polled 0x27
            TrackPlace(name, st, s, now);
        }
    }

    private void HeldChanged(string name, int item, DateTimeOffset now)
    {
        if (!_players.TryGetValue(name, out var st)) _players[name] = st = new PlayerState();
        if (st.HeldItem == item) return;
        st.HeldItem = item;
        if (item <= 0) return;
        var pos = Cell(st.Last.Pos);
        // A never-held craftable/smeltable result in hand right next to a crafting table/furnace: probably just made it.
        if (!st.HeldBefore.Contains(item) && st.Last.Name != null)
        {
            bool nearTable = _world.FindNearestBlock(st.Last.Pos, 3, Blocks.Workbench) != null;
            bool nearFurnace = _world.FindNearestBlock(st.Last.Pos, 3, Blocks.Furnace, Blocks.LitFurnace) != null;
            bool craftable = Recipes.ForOutput(item).Count > 0;
            bool smelted = Smelting.All.Any(r => r.OutputId == item);
            if ((nearTable && craftable) || (nearFurnace && smelted))
                Record(new ActivityEvent(name, ActivityKinds.Crafted, now, 0.5) { ItemId = item, Pos = pos, Place = Place(pos) });
        }
        st.HeldBefore.Add(item);
        Record(new ActivityEvent(name, ActivityKinds.Equipped, now, 0.95) { ItemId = item });
    }

    private void VehicleChanged(string name, int vehicle, DateTimeOffset now)
    {
        if (!_players.TryGetValue(name, out var st)) _players[name] = st = new PlayerState();
        if (st.VehicleId == vehicle) return;
        st.VehicleId = vehicle;
        if (vehicle < 0) return;
        var v = _world.Entities.Get(vehicle);
        Record(new ActivityEvent(name, ActivityKinds.Rode, now, 1.0) { Detail = v != null ? ObjectTypes.Name(v.TypeId) : "vehicle" });
    }

    private void TrackPlace(string name, PlayerState st, PlayerSample s, DateTimeOffset now)
    {
        if (_index == null) return;
        var pos = Cell(s.Pos);
        var place = _index.PlaceAt(pos, 3);
        if (!SamePlace(place, st.Candidate)) { st.Candidate = place; st.CandidateSince = now; }
        // Hysteresis: a place must hold ≥10 s (islands 5 s) before it counts as arrived.
        var hold = TimeSpan.FromSeconds(place?.Kind == IndexKinds.Island ? 5 : 10);
        if (!SamePlace(st.Candidate, st.Place) && now - st.CandidateSince >= hold)
        {
            if (st.Place != null && st.Place.Kind != IndexKinds.Island)
                Record(new ActivityEvent(name, ActivityKinds.Departed, now, 0.9) { Place = st.Place });
            st.Place = st.Candidate;
            st.PlaceSince = st.CandidateSince;
            st.LingerFlagged = false;
            if (st.Place != null) Record(new ActivityEvent(name, ActivityKinds.Arrived, now, 0.9) { Place = st.Place, Pos = pos });
        }
        if (st.Place is { Kind: IndexKinds.UnknownStructure } u && !st.LingerFlagged && now - st.PlaceSince >= TimeSpan.FromSeconds(_o.LingerSeconds))
        {
            st.LingerFlagged = true;
            _index.NoteStructureUse(u.Key, name);
            if (_o.Wake.LingerAtUnknownStructure && _gate.TryWake(name, "linger:" + u.Key, DangerLevel.Watch, TimeSpan.FromMinutes(30), _o.Wake.MaxWakesPer5Min))
                Wake?.Invoke(GameEvent.Activity($"{name} keeps using an {u.Name} you don't understand{ActivityText.At(null, pos)}; ask what it is", name, EventPriority.Low) with { At = now });
        }
    }

    private static bool SamePlace(PlaceRef? a, PlaceRef? b) => a?.Kind == b?.Kind && a?.Key == b?.Key;

    private static BlockPos Cell(Vec3d v) => new((int)Math.Floor(v.X), (int)Math.Floor(v.Y + 0.001), (int)Math.Floor(v.Z));

    // ------------------------------------------------------------------ recording, rate limits, sessions

    private void Record(ActivityEvent e)
    {
        if (e.Confidence < _o.MinLogConfidence) return;
        e = e with { Dimension = _dim };
        var now = e.At;
        // Merge an identical event moments ago (picking up a stack in pieces, a line of placements).
        var last = _pendingWrites.LastOrDefault(x => x.Player == e.Player && x.Kind == e.Kind && x.BlockId == e.BlockId && x.ItemId == e.ItemId
                                                       && x.Place?.Key == e.Place?.Key && e.At - x.At <= TimeSpan.FromSeconds(_o.MergeWindowSeconds)
                                                       && e.Kind is not (ActivityKinds.Chat or ActivityKinds.WroteSign or ActivityKinds.Died));
        if (last != null)
        {
            var merged = last with { Count = last.Count + e.Count, Confidence = Math.Min(last.Confidence, e.Confidence), At = e.At, Pos = e.Pos ?? last.Pos };
            _pendingWrites[_pendingWrites.LastIndexOf(last)] = merged;
            ReplaceInSession(last, merged);
            EventRecorded?.Invoke(e);
            return;
        }
        if (!Allow(e.Player, now)) { Dropped++; return; }
        _pendingWrites.Add(e);
        _recent.Add(e);
        if (_recent.Count > 500) _recent.RemoveRange(0, 100);
        AddToSession(e);
        EventRecorded?.Invoke(e);
    }

    private bool Allow(string player, DateTimeOffset now)
    {
        if (!_players.TryGetValue(player, out var st)) _players[player] = st = new PlayerState();
        while (st.Rate.Count > 0 && now - st.Rate.Peek() > TimeSpan.FromMinutes(1)) st.Rate.Dequeue();
        while (_globalRate.Count > 0 && now - _globalRate.Peek() > TimeSpan.FromMinutes(1)) _globalRate.Dequeue();
        if (st.Rate.Count >= _o.MaxEventsPerPlayerPerMinute || _globalRate.Count >= _o.MaxEventsPerMinute) return false;
        st.Rate.Enqueue(now);
        _globalRate.Enqueue(now);
        return true;
    }

    private static bool IsWork(ActivityEvent e) => e.Category is not (ActivityCategory.Social or ActivityCategory.Travelled) && e.Kind != ActivityKinds.Equipped;

    private void AddToSession(ActivityEvent e)
    {
        _sessions.TryGetValue(e.Player, out var s);
        if (s != null)
        {
            bool gap = e.At - s.Last > TimeSpan.FromSeconds(_o.SessionGapSeconds);
            bool tooLong = e.At - s.Start > TimeSpan.FromMinutes(_o.SessionMaxMinutes);
            bool moved = IsWork(e) && e.Place != null && s.Place != null && !SamePlace(e.Place, s.Place);
            if (gap || tooLong || moved) { Close(s); s = null; }
        }
        if (s == null)
        {
            if (!IsWork(e)) return; // chat/travel alone never opens a session
            _sessions[e.Player] = s = new Session { Player = e.Player, Start = e.At, Place = e.Place, Dimension = e.Dimension };
        }
        s.Place ??= IsWork(e) ? e.Place : null;
        s.Events.Add(e);
        s.Last = e.At;
        if (e.Pos is { } p)
        {
            if (!s.HasBounds) { s.Min = s.Max = p; s.HasBounds = true; }
            else
            {
                s.Min = new BlockPos(Math.Min(s.Min.X, p.X), Math.Min(s.Min.Y, p.Y), Math.Min(s.Min.Z, p.Z));
                s.Max = new BlockPos(Math.Max(s.Max.X, p.X), Math.Max(s.Max.Y, p.Y), Math.Max(s.Max.Z, p.Z));
            }
        }
    }

    private void ReplaceInSession(ActivityEvent old, ActivityEvent merged)
    {
        if (_sessions.TryGetValue(old.Player, out var s))
        {
            int i = s.Events.IndexOf(old);
            if (i >= 0) { s.Events[i] = merged; s.Last = merged.At; }
        }
        int r = _recent.IndexOf(old);
        if (r >= 0) _recent[r] = merged;
    }

    private void CloseSessions(bool force)
    {
        var now = _time.GetUtcNow();
        foreach (var s in _sessions.Values.ToList())
            if (force || now - s.Last > TimeSpan.FromSeconds(_o.SessionGapSeconds) || now - s.Start > TimeSpan.FromMinutes(_o.SessionMaxMinutes))
                Close(s);
    }

    private void Close(Session s)
    {
        _sessions.Remove(s.Player);
        if (Rollup(s, includeTimes: true) is not { } r) return;
        // Unusual = the first time this player did this kind of thing here (this session of the bot).
        if (_seenFirst.Add($"first:{s.Player}:{s.Place?.Kind}:{s.Place?.Key}:{string.Join(",", r.Counts.Keys.OrderBy(k => k))}"))
            r = r with { Salience = r.Salience + 1 };
        FlushWrites(); // raw events need ids before linking
        try
        {
            if (_log != null) r = _log.AddRollup(r, s.Events.Select(e => _ids.GetValueOrDefault(e)).Where(id => id > 0));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "rollup write failed"); }
        _rollups.Add(r);
        if (_rollups.Count > 200) _rollups.RemoveRange(0, 50);
        RollupClosed?.Invoke(r);
        if (_index != null && s.HasBounds && s.Events.Count(e => e.Kind == ActivityKinds.Placed) >= 6)
            _index.NoteStructureBuilder(new BlockPos((s.Min.X + s.Max.X) / 2, (s.Min.Y + s.Max.Y) / 2, (s.Min.Z + s.Max.Z) / 2), s.Player);
        if (TaughtStructureAt != null && s.HasBounds && TaughtStructureAt(s.Min) is { } taught) StructureEvidence?.Invoke(taught, r);
    }

    private readonly Dictionary<ActivityEvent, long> _ids = new(ReferenceEqualityComparer.Instance);

    private void FlushWrites()
    {
        if (_pendingWrites.Count == 0) return;
        var batch = _pendingWrites.ToList();
        _pendingWrites.Clear();
        if (_log == null) return;
        try
        {
            var stored = _log.Append(batch);
            for (int i = 0; i < batch.Count; i++) _ids[batch[i]] = stored[i].Id;
            if (_ids.Count > 5000) _ids.Clear();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "activity log write failed ({Count})", batch.Count); }
    }

    // ------------------------------------------------------------------ rollup text (deterministic)

    internal ActivityRollup? Rollup(IReadOnlyList<ActivityEvent> events, string player, PlaceRef? place, DateTimeOffset start, DateTimeOffset end,
        BlockPos min, BlockPos max, bool includeTimes) =>
        RollupCore(events, player, place, start, end, min, max, includeTimes) is { } r
            ? r with { Dimension = events.Count > 0 ? events[0].Dimension : _dim } : null;

    private ActivityRollup? RollupCore(IReadOnlyList<ActivityEvent> events, string player, PlaceRef? place, DateTimeOffset start, DateTimeOffset end,
        BlockPos min, BlockPos max, bool includeTimes)
    {
        var parts = new List<(string Text, double Conf, double Salience, string Cat, int N)>();
        var usable = events.Where(e => e.Confidence >= _o.MediumConfidence).ToList();
        void Group(string kind, string verb1, string verbN, Func<ActivityEvent, int?> idOf, Func<int, string> name, string cat, double sal)
        {
            foreach (var g in usable.Where(e => e.Kind == kind).GroupBy(idOf).OrderByDescending(g => g.Sum(e => e.Count)))
            {
                int n = g.Sum(e => e.Count);
                var nm = g.Key is { } id ? name(id) : "blocks";
                parts.Add((n > 1 ? $"{verbN} {ActivityText.Qty(n)} {nm}" : $"{verb1} {nm}", g.Average(e => e.Confidence), sal * (n >= 20 ? 2 : 1), cat, n));
            }
        }
        Group(ActivityKinds.Broke, "broke", "mined", e => e.BlockId, ActivityText.Block, ActivityCategory.Mined, 1);
        Group(ActivityKinds.Harvested, "harvested", "harvested", e => e.BlockId, ActivityText.Block, ActivityCategory.Farmed, 1);
        Group(ActivityKinds.Planted, "planted", "planted", e => e.BlockId, ActivityText.Block, ActivityCategory.Farmed, 1);
        Group(ActivityKinds.Placed, "placed", "placed", e => e.BlockId, ActivityText.Block, ActivityCategory.Built, 1.2);
        foreach (var e in usable.Where(e => e.Kind == ActivityKinds.WroteSign))
            parts.Add(($"placed sign \"{e.Detail}\"", e.Confidence, 2.5, ActivityCategory.Built, 1));
        var opened = usable.Where(e => e.Kind == ActivityKinds.Opened).ToList();
        int chests = opened.Select(e => e.Place?.Key ?? e.Pos?.ToString()).Distinct().Count();
        if (chests == 1) parts.Add(($"opened the {opened[0].Place?.Name ?? "chest"}", opened.Average(e => e.Confidence), 1.5, ActivityCategory.Container, 1));
        else if (chests > 1) parts.Add(($"{(chests >= 3 ? "reorganized" : "used")} {chests} chests", opened.Average(e => e.Confidence), 1.5, ActivityCategory.Container, chests));
        Group(ActivityKinds.Took, "took", "took", e => e.ItemId, ActivityText.Item, ActivityCategory.Container, 1.5);
        Group(ActivityKinds.Stored, "stored", "stored", e => e.ItemId, ActivityText.Item, ActivityCategory.Container, 1.2);
        foreach (var e in usable.Where(e => e.Kind == ActivityKinds.LitFurnace).Take(1)) parts.Add(("lit the furnace", e.Confidence, 1, ActivityCategory.Crafted, 1));
        Group(ActivityKinds.Crafted, "crafted", "crafted", e => e.ItemId, ActivityText.Item, ActivityCategory.Crafted, 1.2);
        Group(ActivityKinds.PickedUp, "picked up", "picked up", e => e.ItemId, ActivityText.Item, ActivityCategory.Items, 0.5);
        foreach (var e in usable.Where(e => e.Kind == ActivityKinds.Fought))
            parts.Add(($"fought a {e.Detail} ({e.Count} hit{(e.Count == 1 ? "" : "s")} taken)", e.Confidence, 3, ActivityCategory.Fought, 1));
        foreach (var e in usable.Where(e => e.Kind == ActivityKinds.Died))
            parts.Add((e.Detail ?? "died", e.Confidence, 5, ActivityCategory.Died, 1));
        if (parts.Count == 0) return null;

        string Phrase((string Text, double Conf, double Sal, string Cat, int N) p) => (p.Conf < _o.HighConfidence ? "probably " : "") + p.Text;
        var ordered = parts.OrderByDescending(p => p.Salience * Math.Log2(1 + p.N)).Take(5).Select(Phrase).ToList();
        var list = ordered.Count == 1 ? ordered[0] : string.Join(", ", ordered.Take(ordered.Count - 1)) + " and " + ordered[^1];
        var at = place == null || place.Kind == IndexKinds.Island && parts.All(p => p.Cat == ActivityCategory.Died) ? "" : ActivityText.At(place, null);
        var loc = start.ToLocalTime();
        var times = string.Create(CultureInfo.InvariantCulture, $" ({loc:MM-dd HH:mm}-{end.ToLocalTime():HH:mm})");
        var shortText = $"{player} {list}{at}";
        double sal = parts.Max(p => p.Salience) + (place?.Kind == IndexKinds.UnknownStructure ? 2 : 0);
        return new ActivityRollup(player, start, end, shortText + (includeTimes ? times : ""), parts.Min(p => p.Conf))
        {
            Place = place, Min = min, Max = max, Events = events.Count, Short = shortText, Salience = sal,
            Counts = parts.GroupBy(p => p.Cat).ToDictionary(g => g.Key, g => g.Sum(p => p.N)),
        };
    }

    private ActivityRollup? Rollup(Session s, bool includeTimes) =>
        Rollup(s.Events, s.Player, s.Place, s.Start, s.Last, s.HasBounds ? s.Min : default, s.HasBounds ? s.Max : default, includeTimes);

    // ------------------------------------------------------------------ digest inputs (under the pump lock)

    internal (List<ActivityRollup> Closed, List<ActivityRollup> Open, List<ActivityEvent> Events, Vec3d Bot) DigestInputs(DateTimeOffset since)
    {
        lock (_pumpLock)
        {
            // Only the current dimension's activity (placeless events like chat death lines are global).
            int dim = _dim;
            var closed = _rollups.Where(r => r.End >= since && r.Dimension == dim).ToList();
            if (_log != null && (closed.Count == 0 || closed.Min(r => r.Start) > since))
                try
                {
                    var stored = _log.Rollups(null, since, null, 30, dim);
                    closed = closed.Concat(stored.Where(x => closed.All(c => c.Player != x.Player || c.Start != x.Start))).ToList();
                }
                catch { /* memory only */ }
            var open = _sessions.Values.Where(s => s.Last >= since && s.Dimension == dim)
                .Select(s => Rollup(s.Events.Where(e => e.At >= since).ToList(), s.Player, s.Place, s.Start, s.Last, s.Min, s.Max, false))
                .Where(r => r != null).Select(r => r!).ToList();
            var events = _recent.Where(e => e.At >= since && (e.Dimension == dim || !e.IsSpatial)).ToList();
            return (closed, open, events, _world.Player.Position);
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
