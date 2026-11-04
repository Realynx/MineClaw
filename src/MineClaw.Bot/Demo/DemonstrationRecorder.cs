using MineClaw.Agent.Human;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Demo;

/// <summary>
/// Records what a teaching player does, from what the client can see: their position (polled), what they face,
/// arm swings (0x12) resolved to the block they look at plus their held item, block changes near them (classified as
/// theirs when they just swung nearby, otherwise as the world reacting: "cobblestone appeared"), and container opens
/// (a swing at a chest/furnace/table, or a 0x36 chest block action).
/// </summary>
public sealed class DemonstrationRecorder(IGameClient client, BotOptions? options = null, TimeProvider? time = null) : IDemonstrationSource
{
    private readonly BotOptions _options = options ?? new BotOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Position poll interval (tests shorten it).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>When set, solid block place/break steps use the tracker's attribution (facing + swing + bystanders +
    /// physics filtering) instead of the recorder's own swing heuristic, and its sign/craft events become steps.</summary>
    public PlayerActivityTracker? Activity { get; set; }

    public Task<IDemonstrationSession> StartAsync(string player, (MPos Min, MPos Max)? area, CancellationToken ct)
    {
        (BlockPos, BlockPos)? a = area is { } r ? (new BlockPos(r.Min.X, r.Min.Y, r.Min.Z), new BlockPos(r.Max.X, r.Max.Y, r.Max.Z)) : null;
        var s = new Session(client, player, a, _options.DemoRadius, _time, PollInterval, Activity);
        s.Start();
        return Task.FromResult<IDemonstrationSession>(s);
    }

    internal static readonly HashSet<int> Containers = [Blocks.Chest, Blocks.Furnace, Blocks.LitFurnace, Blocks.Workbench, Blocks.Dispenser];

    /// <summary>
    /// First non-air block along an entity's look ray. <paramref name="solidOnly"/> = what a punch/dig hits: vanilla's
    /// left-click ray passes through liquids (b1.8 rayTrace without liquids), so lava/water are skipped.
    /// </summary>
    public static (BlockPos Pos, int Id)? LookTarget(WorldState w, Vec3d feet, float yaw, float pitch, double reach = 5, bool solidOnly = false)
    {
        var eye = feet + new Vec3d(0, PlayerPhysics.EyeHeight, 0);
        var dir = PlayerPhysics.LookVector(yaw, pitch);
        BlockPos? last = null;
        for (double d = 0.1; d <= reach; d += 0.1)
        {
            var p = (eye + dir * d).ToBlockPos();
            if (last == p) continue;
            last = p;
            int id = w.GetBlock(p);
            if (id != 0 && !(solidOnly && (BlockShapes.IsLiquid(id) || id == Blocks.Fire))) return (p, id);
        }
        return null;
    }

    private sealed class Session : IDemonstrationSession
    {
        private sealed record Raw(DemoAction A, int Held = 0, bool PlayerCaused = false);

        private readonly IGameClient _c;
        private readonly string _player;
        private readonly (BlockPos Min, BlockPos Max)? _area;
        private readonly double _radius;
        private readonly TimeProvider _time;
        private readonly TimeSpan _poll;
        private readonly List<Raw> _raw = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _pollTask = Task.CompletedTask;
        private int _entityId = int.MinValue;
        private BlockPos? _lastCell;
        private string? _lastFacing;
        private (DateTimeOffset At, BlockPos? Target, int Held) _lastSwing = (DateTimeOffset.MinValue, null, 0);
        private bool _stopped;
        private DateTimeOffset? _lastActivity;
        private int _actionCount;
        private readonly PlayerActivityTracker? _activity;
        private readonly List<ActivityEvent> _attributed = new();

        public Session(IGameClient c, string player, (BlockPos, BlockPos)? area, double radius, TimeProvider time, TimeSpan poll,
            PlayerActivityTracker? activity = null)
        {
            _c = c; _player = player; _area = area; _radius = radius; _time = time; _poll = poll; _activity = activity;
        }

        private void OnActivity(ActivityEvent e)
        {
            if (!e.Player.Equals(_player, StringComparison.OrdinalIgnoreCase)) return;
            lock (_lock) if (!_stopped) _attributed.Add(e);
        }

        private DateTimeOffset Now => _time.GetUtcNow();

        public DateTimeOffset? LastActivity { get { lock (_lock) return _lastActivity; } }
        public int ActionCount { get { lock (_lock) return _actionCount; } }

        // caller holds _lock
        private void Touch(bool action)
        {
            _lastActivity = Now;
            if (action) _actionCount++;
        }

        public void Start()
        {
            _c.World.BlockChanged += OnBlockChanged;
            _c.EntityAnimated += OnAnimated;
            _c.BlockAction += OnBlockAction;
            if (_activity != null) _activity.EventRecorded += OnActivity;
            Sample();
            _pollTask = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        await Task.Delay(_poll, _cts.Token).ConfigureAwait(false);
                        Sample();
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private Entity? Player() => _c.World.Entities.FindPlayer(_player);

        /// <summary>Records a Move when the player's cell changes; updates the facing of a trailing Move.</summary>
        internal void Sample()
        {
            var e = Player();
            if (e == null) return;
            _entityId = e.Id;
            var cell = e.Position.ToBlockPos();
            var facing = LookTarget(_c.World, e.Position, e.Yaw, e.Pitch) is { } t ? "facing " + Names.Block(t.Id) : null;
            lock (_lock)
            {
                if (_stopped) return;
                if (cell != _lastCell)
                {
                    if (_lastCell != null) Touch(false);
                    _raw.Add(new Raw(new DemoAction(Now, DemoActionKind.Move, cell.X, cell.Y, cell.Z, Detail: facing)));
                    _lastCell = cell;
                    _lastFacing = facing;
                }
                else if (facing != _lastFacing && _raw.Count > 0 && _raw[^1].A.Kind == DemoActionKind.Move)
                {
                    _raw[^1] = _raw[^1] with { A = _raw[^1].A with { Detail = facing } };
                    _lastFacing = facing;
                }
            }
        }

        private void OnAnimated(int entityId, int animation)
        {
            if (animation != 1) return;
            var e = Player();
            if (e == null || e.Id != entityId) return;
            Sample();
            // A right-click with a usable item (bucket, flint, seeds, a block) can target a liquid; a punch never does.
            bool use = Perception.Gestures.IsUseItem(e.HeldItem);
            var target = LookTarget(_c.World, e.Position, e.Yaw, e.Pitch, solidOnly: !use);
            lock (_lock)
            {
                if (_stopped) return;
                _lastSwing = (Now, target?.Pos, e.HeldItem);
                if (target is not { } t) return;
                Touch(true);
                _raw.Add(new Raw(SwingAction(t.Pos, t.Id, e.HeldItem, use), e.HeldItem, true));
            }
        }

        /// <summary>A swing at a block: opening a container, using the held item on it, or (no item use) a punch. "Punch the
        /// regenerating brick" is a valid teaching action: a swing that isn't an item use is pointing.</summary>
        private DemoAction SwingAction(BlockPos pos, int id, int heldItem, bool use)
        {
            var kind = Containers.Contains(id) ? DemoActionKind.OpenContainer : DemoActionKind.UseBlock;
            string? held = heldItem > 0 && Items.Exists(heldItem) ? Names.Item(heldItem) : null;
            string? detail = kind != DemoActionKind.UseBlock ? null : use ? held : Perception.Gestures.Punched;
            return new DemoAction(Now, kind, pos.X, pos.Y, pos.Z, Names.Block(id), detail);
        }

        private void OnBlockAction(BlockPos pos, int d1, int d2)
        {
            int id = _c.World.GetBlock(pos);
            if (id != Blocks.Chest || d2 <= 0 || !Near(pos)) return;
            lock (_lock)
            {
                if (_stopped) return;
                _raw.Add(new Raw(new DemoAction(Now, DemoActionKind.OpenContainer, pos.X, pos.Y, pos.Z, Names.Block(id)), 0, true));
            }
        }

        private void OnBlockChanged(BlockChange ch)
        {
            if (ch.OldId == ch.NewId) return; // meta-only (crops, liquid levels, levers): not a step
            if (!Near(ch.Pos)) return;
            bool swungRecently;
            lock (_lock) swungRecently = Now - _lastSwing.At < TimeSpan.FromSeconds(1.5) &&
                                         (_lastSwing.Target is not { } st || st.DistanceSquaredTo(ch.Pos) <= 4);
            var (a, mine) = ActionFor(ch, swungRecently);
            if (a == null) return;
            lock (_lock)
            {
                if (_stopped) return;
                if (mine) Touch(true);
                _raw.Add(new Raw(a, 0, mine));
            }
        }

        /// <summary>The step a block change is, and whether the player did it (a swing just before): a poured/scooped bucket
        /// (source blocks only, after a swing), a broken block ("vanished" without one), a placed block ("appeared" without one,
        /// and always for stone/cobble/obsidian a liquid made).</summary>
        private (DemoAction? A, bool Mine) ActionFor(BlockChange ch, bool swung)
        {
            var p = ch.Pos;
            if (BlockShapes.IsLiquid(ch.NewId))
                return (swung && ch.NewMeta == 0 ? new DemoAction(Now, DemoActionKind.BlockPlaced, p.X, p.Y, p.Z, Names.Block(ch.NewId), "poured") : null, swung);
            if (ch.NewId == 0) return (Emptied(ch, swung), swung);
            // Lava meeting water makes stone/cobble/obsidian: that is the structure working, not the player placing.
            bool mine = swung && !(ch.NewId is Blocks.Cobblestone or Blocks.Stone or Blocks.Obsidian && TouchesLiquid(p));
            return (new DemoAction(Now, DemoActionKind.BlockPlaced, p.X, p.Y, p.Z, Names.Block(ch.NewId), mine ? null : "appeared"), mine);
        }

        /// <summary>A block gone to air: broken (a solid; "vanished" without a swing), or a liquid source scooped after a swing.</summary>
        private DemoAction? Emptied(BlockChange ch, bool swung)
        {
            var p = ch.Pos;
            if (!BlockShapes.IsLiquid(ch.OldId)) return new DemoAction(Now, DemoActionKind.BlockBroken, p.X, p.Y, p.Z, Names.Block(ch.OldId), swung ? null : "vanished");
            return swung && ch.OldMeta == 0 ? new DemoAction(Now, DemoActionKind.BlockBroken, p.X, p.Y, p.Z, Names.Block(ch.OldId), "scooped") : null;
        }

        private bool TouchesLiquid(BlockPos p) =>
            BlockFaceExtensions.All.Any(f => BlockShapes.IsLiquid(_c.World.GetBlock(p.Offset(f))));

        private bool Near(BlockPos p)
        {
            if (_area is { } a && p.X >= a.Min.X && p.X <= a.Max.X && p.Y >= a.Min.Y && p.Y <= a.Max.Y && p.Z >= a.Min.Z && p.Z <= a.Max.Z) return true;
            var e = Player();
            return e != null && e.Position.DistanceTo(p.Center) <= _radius;
        }

        public async Task<IReadOnlyList<DemoAction>> StopAsync(CancellationToken ct)
        {
            _activity?.Pump(); // let the tracker attribute the last changes first
            Detach();
            await _pollTask.ConfigureAwait(false);
            List<Raw> raw;
            List<ActivityEvent> attributed;
            lock (_lock) { raw = _raw.ToList(); attributed = _attributed.ToList(); }
            if (_activity != null) raw = Reattribute(raw, attributed, _activity.Options.MediumConfidence);
            return PostProcess(raw);
        }

        private void Detach()
        {
            lock (_lock)
            {
                if (_stopped) return;
                _stopped = true;
            }
            _c.World.BlockChanged -= OnBlockChanged;
            _c.EntityAnimated -= OnAnimated;
            _c.BlockAction -= OnBlockAction;
            if (_activity != null) _activity.EventRecorded -= OnActivity;
            _cts.Cancel();
        }

        /// <summary>
        /// Tracker attribution decides whether a solid place/break was the teacher's ("" detail) or the world's
        /// ("appeared"/"vanished"); liquids keep the recorder's own pour/scoop rule. Sign and craft events become steps.
        /// </summary>
        private static List<Raw> Reattribute(List<Raw> raw, List<ActivityEvent> attributed, double minConfidence)
        {
            static bool Solid(DemoAction a) => a.Detail is null or "appeared" or "vanished";
            var mine = attributed.Where(e => e.Confidence >= minConfidence && e.Pos != null).ToList();
            var list = new List<Raw>(raw.Count);
            foreach (var r in raw)
                list.Add(r.A.Kind is DemoActionKind.BlockPlaced or DemoActionKind.BlockBroken && Solid(r.A) ? Attributed(r, mine) : r);
            foreach (var e in attributed.Where(e => e.Confidence >= minConfidence && e.Kind is ActivityKinds.WroteSign or ActivityKinds.Crafted))
                list.Add(new Raw(StepOf(e), 0, true));
            return list.OrderBy(x => x.A.At).ToList(); // stable

        }

        /// <summary>A solid place/break: the teacher's (no detail) when the tracker saw them do it there within 3 s, else the
        /// world's ("appeared"/"vanished").</summary>
        private static Raw Attributed(Raw r, List<ActivityEvent> mine)
        {
            var a = r.A;
            bool placed = a.Kind == DemoActionKind.BlockPlaced;
            bool theirs = mine.Any(e => e.Pos is { } p && p.X == a.X && p.Y == a.Y && p.Z == a.Z
                                        && (placed ? e.Kind is ActivityKinds.Placed or ActivityKinds.Planted or ActivityKinds.WroteSign
                                                   : e.Kind is ActivityKinds.Broke or ActivityKinds.Harvested)
                                        && (e.At - a.At).Duration() <= TimeSpan.FromSeconds(3));
            return r with { A = a with { Detail = theirs ? null : placed ? "appeared" : "vanished" }, PlayerCaused = theirs };
        }

        /// <summary>A sign written or an item crafted, as a step.</summary>
        private static DemoAction StepOf(ActivityEvent e)
        {
            var p = e.Pos ?? default;
            return e.Kind == ActivityKinds.WroteSign
                ? new DemoAction(e.At, DemoActionKind.UseBlock, p.X, p.Y, p.Z, "sign", $"wrote \"{e.Detail}\"")
                : new DemoAction(e.At, DemoActionKind.UseBlock, p.X, p.Y, p.Z, "crafting_table", "crafted " + ActivityText.Item(e.ItemId ?? 0));
        }

        /// <summary>
        /// Drops swings that are explained by a later change (digging swings before a break, the placing swing before a
        /// placed block) and duplicate container opens.
        /// </summary>
        private static List<DemoAction> PostProcess(List<Raw> raw)
        {
            var keep = new List<DemoAction>();
            for (int i = 0; i < raw.Count; i++)
            {
                var r = raw[i];
                var a = r.A;
                if (a.Kind == DemoActionKind.UseBlock)
                {
                    if (ExplainedByLater(raw, i)) continue;
                    a = AsPunch(r, a);
                }
                if (a.Kind == DemoActionKind.OpenContainer && IsRepeatOpen(keep, a)) continue;
                keep.Add(a);
            }
            return keep;
        }

        /// <summary>A swing explained by a later change: the break of that block within 15 s, or (block in hand) a block placed
        /// next to it within 1.5 s.</summary>
        private static bool ExplainedByLater(List<Raw> raw, int i)
        {
            var r = raw[i];
            var a = r.A;
            for (int j = i + 1; j < raw.Count; j++)
            {
                var b = raw[j].A;
                if (BrokeIt(a, b) || (HoldsBlock(r) && PlacedBeside(a, b))) return true;
            }
            return false;
        }

        /// <summary>The teacher broke the swung-at block within 15 s.</summary>
        private static bool BrokeIt(DemoAction a, DemoAction b) =>
            b.Kind == DemoActionKind.BlockBroken && b.Detail == null && b.X == a.X && b.Y == a.Y && b.Z == a.Z && b.At - a.At <= TimeSpan.FromSeconds(15);

        /// <summary>The teacher placed a block next to the swung-at one within 1.5 s.</summary>
        private static bool PlacedBeside(DemoAction a, DemoAction b) =>
            b.Kind == DemoActionKind.BlockPlaced && b.Detail == null && b.At - a.At <= TimeSpan.FromSeconds(1.5)
            && Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) + Math.Abs(b.Z - a.Z) <= 1;

        private static bool HoldsBlock(Raw r) => r.Held > 0 && r.Held < 256;

        /// <summary>Live 10-27: "right-clicked cobblestone on cobblestone" were two PUNCHES with cobblestone in hand. A swing with a
        /// block in hand that placed nothing is a left-click (pointing/digging), not a use (a liquid target stays a use).</summary>
        private static DemoAction AsPunch(Raw r, DemoAction a)
        {
            if (!HoldsBlock(r) || a.Detail == Perception.Gestures.Punched) return a;
            if (a.Block is { } bn && Blocks.ByName(bn) is { } bi && BlockShapes.IsLiquid(bi.Id)) return a;
            return a with { Detail = Perception.Gestures.Punched };
        }

        /// <summary>The same container opened again within 3 s of the last kept open.</summary>
        private static bool IsRepeatOpen(List<DemoAction> keep, DemoAction a) =>
            keep.Count > 0 && keep.FindLast(k => k.Kind == DemoActionKind.OpenContainer) is { } prev &&
            prev.X == a.X && prev.Y == a.Y && prev.Z == a.Z && a.At - prev.At < TimeSpan.FromSeconds(3);

        public ValueTask DisposeAsync()
        {
            Detach();
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
