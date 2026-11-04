using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The one way items are put away (the <c>deposit</c> tool, so the SDK's <c>store</c>, <c>depositWhenFull</c> and <c>room</c>):
/// the requested chest first (else the nearest), then every other known chest with room (<see cref="GeneratorLoop.Candidates"/>:
/// shared memory ranked by room, then chests in sight near the job), skipping never-touch marked and "private"-labelled chests.
/// Partial fits go on to the next chest. The result says where things went ("stored 64 cobblestone: 30 → 14,64,-5 (now full),
/// 34 → 5,64,4"). When nothing more fits anywhere it fails with one actionable line starting "all N known chests full" or "no
/// known chest" ("all 3 known chests full (…); 141 cobblestone in bag; craft+place a chest (have 300 logs) or keep carrying"):
/// the SDK turns it into <c>{full:true, need}</c>, StandingOrders.StorageFull treats it as "wait", not a failure. Placing a new chest stays the
/// brain's (or the script's) decision. Live 10-28 11:10/12:05: gen_loop failed "store: chest is full" on 14,64,-5 every run
/// while 5,64,4 and 11,68,-5 had room.
/// </summary>
public static class ChestStore
{
    /// <summary>Most chests one store opens.</summary>
    public const int MaxChests = 12;

    public static async Task<ToolResult> StoreAsync(IGameClient c, ObservationStore? obs, BlockPos? asked, Func<ItemStack, bool>? filter,
        CancellationToken ct)
    {
        var run = new StoreRun(c, obs, asked, filter);
        if (await run.VisitCandidatesAsync(ct).ConfigureAwait(false) is { } nothing) return nothing;
        // One chest took it all: the client's own line + where ("deposited 32 cobblestone in chest 5,64,4 (14,64,-5 is full)").
        if (run.Done) return run.DoneResult();
        // Nothing more fits anywhere (or nowhere is known): one actionable line; the caller decides what to do.
        return await run.OverflowOrFailAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One <see cref="StoreAsync"/> call: the chests tried and what each took or why it refused.</summary>
    private sealed class StoreRun(IGameClient c, ObservationStore? obs, BlockPos? asked, Func<ItemStack, bool>? filter)
    {
        private readonly IReadOnlySet<int> _reserved = c.ReservedItems;
        private readonly Func<ItemStack, bool> _pick = filter ?? MinecraftClient.DefaultDepositFilter;
        private readonly HashSet<BlockPos> _tried = new();
        private readonly List<(BlockPos Chest, int N, bool NowFull, string Text)> _went = new();
        private readonly List<BlockPos> _full = new();
        private readonly List<BlockPos> _busy = new();
        private readonly List<string> _other = new();
        private readonly Dictionary<int, int> _storedItems = new();
        private string? _firstFail;
        private int _stored, _opened;

        public bool Done { get; private set; }

        private bool Storable(ItemStack s) => !s.IsEmpty && _pick(s) && !_reserved.Contains(s.Id);

        private Dictionary<int, int> Loose() => c.World.Inventory.PlayerSlots().Skip(9).Where(Storable)
            .GroupBy(s => (int)s.Id).ToDictionary(g => g.Key, g => g.Sum(s => (int)s.Count));

        private static int Total(Dictionary<int, int> d) => d.Values.Sum();

        /// <summary>Tries the candidates in order until one takes the rest; "nothing to deposit" when nothing went anywhere.</summary>
        public async Task<ToolResult?> VisitCandidatesAsync(CancellationToken ct)
        {
            var storing = Loose().Keys.ToHashSet();
            var skip = Acquisition.Of(c)?.SkipChest;
            var off = OffLimits(c, obs);
            foreach (var chest in GeneratorLoop.Candidates(c, obs, asked ?? NearestChest(c), storing))
            {
                if (!ShouldOpen(chest, off, skip)) continue;
                if (++_opened > MaxChests) break;
                // Both halves of a double chest are one window (ChestPairs): never open it twice. Only the real other half: a trapped
                // chest beside a normal one (1.12.2) is a chest of its own (was: every side was marked tried).
                foreach (var cell in ChestPairs.Cells(c.World, chest)) _tried.Add(cell);
                if (await TryChestAsync(chest, ct).ConfigureAwait(false) is { } nothing) return nothing;
                if (Done) break;
            }
            return null;
        }

        /// <summary>Only a chest the caller named is opened whatever its marks; fallbacks honour never-touch and "private".</summary>
        private bool ShouldOpen(BlockPos chest, HashSet<BlockPos> off, Func<BlockPos, string?>? skip) =>
            !_tried.Contains(chest) && (chest == asked || !off.Contains(chest) && skip?.Invoke(chest) == null);

        /// <summary>One deposit: what the chest took (<see cref="Done"/> when it had room for the rest), or why it refused.
        /// "nothing to deposit" before anything went anywhere is the result itself.</summary>
        private async Task<ToolResult?> TryChestAsync(BlockPos chest, CancellationToken ct)
        {
            var (r, moved, _) = await DepositIntoAsync(chest, ct).ConfigureAwait(false);
            if (r.Ok && moved == 0) moved = r.Count;
            if (r.Ok || moved > 0) { Done = Took(chest, moved, r); return null; }
            if (!r.Message.StartsWith("nothing to deposit", StringComparison.Ordinal)) { NoteRefusal(chest, r.Message); return null; }
            if (_went.Count == 0) return ToolResult.Success(r.Message);
            Done = true;
            return null;
        }

        private async Task<(ActionResult R, int Moved, Dictionary<int, int> After)> DepositIntoAsync(BlockPos chest, CancellationToken ct)
        {
            var before = Loose();
            var r = await c.DepositAllAsync(chest, filter, ct).ConfigureAwait(false);
            var after = Loose();
            foreach (var (id, n) in before) if (n - after.GetValueOrDefault(id) is > 0 and var m) _storedItems[id] = _storedItems.GetValueOrDefault(id) + m;
            return (r, Math.Max(0, Total(before) - Total(after)), after);
        }

        /// <summary>The chest took <paramref name="moved"/>: true when it still has room (so the rest went in too).</summary>
        private bool Took(BlockPos chest, int moved, ActionResult r)
        {
            bool nowFull = !r.Ok || r.Message.Contains("chest full", StringComparison.Ordinal);
            _went.Add((chest, moved, nowFull, r.Message));
            _stored += moved;
            if (!nowFull) return true;
            _full.Add(chest);
            return false;
        }

        private void NoteRefusal(BlockPos chest, string message)
        {
            _firstFail ??= message;
            if (message.Contains("full", StringComparison.OrdinalIgnoreCase)) { _full.Add(chest); return; }
            if (message.StartsWith("busy", StringComparison.Ordinal)) _busy.Add(chest);
            _other.Add($"{P(chest)}: {(message.Length > 40 ? message[..40] : message)}");
        }

        public ToolResult DoneResult()
        {
            if (_went.Count != 1) return ToolResult.Success(StoredLine(_stored, _storedItems, _went));
            return ToolResult.Success($"{_went[0].Text} in chest {P(_went[0].Chest)}" +
                (asked is { } a && _full.Contains(a) && _went[0].Chest != a ? $" ({P(a)} is full)" : ""));
        }

        private bool Stuck(BlockPos p) => _full.Contains(p) || _busy.Contains(p);

        /// <summary>
        /// Full, or still crowded after the queue (busy): the chest to put another one down right beside (live 10-29: "deposits to
        /// log_chest keep failing on occupied", 20 bodies waited holding logs), when the bag can make one. A chest the caller named
        /// that is out of reach for another reason stays the caller's problem.
        /// </summary>
        private BlockPos? OverflowBeside(Dictionary<int, int> left)
        {
            if (Total(left) <= 0 || _full.Count == 0 && _busy.Count == 0 || !CanMakeChest(c)) return null;
            var near = asked is { } an && Stuck(an) ? an : _full.Count > 0 ? _full[0] : _busy.FirstOrDefault();
            return Stuck(near) ? near : null;
        }

        /// <summary>Spreads onto a new chest beside a full/busy one (saying so), else the one actionable failure line.</summary>
        public async Task<ToolResult> OverflowOrFailAsync(CancellationToken ct)
        {
            var left = Loose();
            string? overflow = null;
            if (OverflowBeside(left) is { } near && await PlaceBesideAsync(c, obs, near, ct).ConfigureAwait(false) is { } placed)
            {
                overflow = $"chest {P(near)} {(_full.Contains(near) ? "full" : "busy")}: placed a new chest at {P(placed)}";
                var (r, moved, after) = await DepositIntoAsync(placed, ct).ConfigureAwait(false);
                if (moved > 0)
                {
                    _went.Add((placed, moved, !r.Ok || r.Message.Contains("chest full", StringComparison.Ordinal), r.Message));
                    _stored += moved;
                }
                left = after;
                if (Total(left) == 0) return ToolResult.Success(overflow + "; " + StoredLine(_stored, _storedItems, _went));
            }
            return Unstored(left, overflow);
        }

        private ToolResult Unstored(Dictionary<int, int> left, string? overflow)
        {
            // The only chest there is refused for another reason (claimed, no path): the client's own words, as before.
            if (overflow == null && _opened == 1 && _went.Count == 0 && _full.Count == 0 && _firstFail != null) return ToolResult.Error(_firstFail);
            if (Total(left) == 0) return AllAway();
            var parts = new List<string> { FailHead() };
            if (overflow != null) parts.Add(overflow);
            if (_went.Count > 0) parts.Add(StoredLine(_stored, _storedItems, _went));
            parts.Add(Bag(left) + " in bag");
            parts.Add(Advice(c) + " or keep carrying");
            return ToolResult.Error(string.Join("; ", parts));
        }

        /// <summary>Nothing storable left in the bag.</summary>
        private ToolResult AllAway() =>
            _went.Count > 0 ? ToolResult.Success(StoredLine(_stored, _storedItems, _went))
            : _tried.Count == 0 ? ToolResult.Error("no chest nearby") : ToolResult.Success("nothing to deposit");

        private string FailHead() =>
            _tried.Count == 0 ? "no known chest"
            : _other.Count == 0 ? $"all {_full.Count} known chest{(_full.Count == 1 ? "" : "s")} full ({Coords(_full)})"
            : $"no known chest has room ({(_full.Count > 0 ? Coords(_full) + " full; " : "")}{string.Join("; ", _other.Take(3))})";
    }

    /// <summary>"stored 64 cobblestone: 30 → 14,64,-5 (now full), 34 → 5,64,4".</summary>
    private static string StoredLine(int stored, Dictionary<int, int> items, List<(BlockPos Chest, int N, bool NowFull, string Text)> went)
    {
        var what = items.Count > 0 ? Bag(items) : $"{stored} item(s)";
        return $"stored {what}: " + string.Join(", ", went.Select(w => $"{w.N} → {P(w.Chest)}{(w.NowFull ? " (now full)" : "")}"));
    }

    private static string Bag(Dictionary<int, int> items) =>
        string.Join(", ", items.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value} {Names.Item(kv.Key)}")) +
        (items.Count > 3 ? $" +{items.Count - 3} more" : "");

    /// <summary>What a new chest would take, from the bag: "place a chest (have 1)", "craft+place a chest (have 300 logs)".</summary>
    private static string Advice(IGameClient c)
    {
        var inv = c.World.Inventory;
        int chests = inv.CountItem(Blocks.Chest), planks = inv.CountItem(Blocks.Planks), logs = inv.CountItem(Blocks.Log) + inv.CountItem(Ids1122.Log2);
        if (chests > 0) return $"place a chest (have {chests})";
        if (planks >= 8) return $"craft+place a chest (have {planks} planks)";
        if (logs >= 2) return $"craft+place a chest (have {logs} logs)";
        return "get 2 logs to craft+place a chest";
    }

    /// <summary>A chest in the bag, or what makes one (8 planks, or 2 logs of any wood).</summary>
    private static bool CanMakeChest(IGameClient c)
    {
        var inv = c.World.Inventory;
        return inv.CountItem(Blocks.Chest) > 0 || inv.CountItem(Blocks.Planks) >= 8 || inv.CountItem(Blocks.Log) + inv.CountItem(Ids1122.Log2) >= 2;
    }

    /// <summary>
    /// Crafts a chest if none is carried and places it two blocks from <paramref name="near"/> (a gap, so it is its own chest and
    /// its own standing room, never a pair with a neighbour), on solid ground, not in fluid, not protected, nearest to the bot.
    /// </summary>
    internal static async Task<BlockPos?> PlaceBesideAsync(IGameClient c, ObservationStore? obs, BlockPos near, CancellationToken ct)
    {
        var w = c.World;
        var spots = new List<BlockPos>();
        for (int dy = 0; dy >= -1; dy--)
            foreach (var (dx, dz) in new[] { (2, 0), (-2, 0), (0, 2), (0, -2), (2, 2), (-2, -2), (2, -2), (-2, 2), (3, 0), (-3, 0), (0, 3), (0, -3) })
            {
                var p = new BlockPos(near.X + dx, near.Y + dy, near.Z + dz);
                if (ChestSpot(c, p)) spots.Add(p);
            }
        if (spots.Count == 0 || !await HaveChestAsync(c, obs, ct).ConfigureAwait(false)) return null;
        var me = w.Player.Position;
        foreach (var p in spots.OrderBy(s => s.Center.DistanceSquaredTo(me)).Take(4))
        {
            if (w.Player.Position.DistanceTo(p.Center) > 4) await c.GotoAsync(new GoalNear(p.X, p.Y, p.Z, 2), null, ct).ConfigureAwait(false);
            var r = await c.PlaceBlockAsync(p.X, p.Y, p.Z, Blocks.Chest, ct).ConfigureAwait(false);
            if (r.Ok && Ids1122.IsChest(w.GetBlock(p))) return p;
        }
        return null;
    }

    /// <summary>A chest carried, or crafted now.</summary>
    private static async Task<bool> HaveChestAsync(IGameClient c, ObservationStore? obs, CancellationToken ct)
    {
        if (c.World.Inventory.CountItem(Blocks.Chest) > 0) return true;
        var made = await new Crafter(c) { Observations = obs }.CraftAsync("chest", 1, ct).ConfigureAwait(false);
        return made.Ok && c.World.Inventory.CountItem(Blocks.Chest) > 0;
    }

    /// <summary>Two free cells on solid ground, no chest beside (it would pair), no fluid beside or under, not protected.</summary>
    private static bool ChestSpot(IGameClient c, BlockPos p)
    {
        var w = c.World;
        if (w.GetBlock(p.X, p.Y, p.Z) != 0 || w.GetBlock(p.X, p.Y + 1, p.Z) != 0) return false;
        if (!BlockShapes.HasCollision(w, p.X, p.Y - 1, p.Z)) return false;
        var sides = new[] { p.Offset(1, 0, 0), p.Offset(-1, 0, 0), p.Offset(0, 0, 1), p.Offset(0, 0, -1) };
        if (sides.Any(n => Ids1122.IsChest(w.GetBlock(n)))) return false;
        if (sides.Append(p.Down()).Any(n => Blocks.IsLava(w.GetBlock(n)) || Blocks.IsWater(w.GetBlock(n)))) return false;
        return c.IsProtected?.Invoke(p, Blocks.Chest) != true;
    }

    private static string Coords(List<BlockPos> list) => string.Join(", ", list.Take(2).Select(P)) + (list.Count > 2 ? ", …" : "");

    private static string P(BlockPos p) => $"{p.X},{p.Y},{p.Z}";

    /// <summary>Remembered chests a player labelled hands-off ("private", "Fox's, don't take"): both halves of a pair.</summary>
    private static HashSet<BlockPos> OffLimits(IGameClient c, ObservationStore? obs)
    {
        var set = new HashSet<BlockPos>();
        if (obs == null) return set;
        var me = c.World.Player.Position;
        try
        {
            foreach (var (row, _) in obs.Nearest("chest", me.X, me.Y, me.Z, limit: 32, maxDistance: 96))
            {
                if (row.Bounds is not { } b || !ChestStock.Private(row.Annotation)) continue;
                set.Add(new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ)));
                set.Add(new BlockPos((int)Math.Floor(b.MaxX), (int)Math.Floor(b.MaxY), (int)Math.Floor(b.MaxZ)));
            }
        }
        catch (Exception) { /* db busy: no labels known */ }
        return set;
    }

    private static BlockPos? NearestChest(IGameClient c) =>
        c.World.FindNearestBlock(c.World.Player.Position, c.StationSearchRadius, Blocks.Chest, Ids1122.TrappedChest)?.Pos;
}
