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
        var reserved = c.ReservedItems;
        var pick = filter ?? MinecraftClient.DefaultDepositFilter;
        bool Storable(ItemStack s) => !s.IsEmpty && pick(s) && !reserved.Contains(s.Id);
        Dictionary<int, int> Loose() => c.World.Inventory.PlayerSlots().Skip(9).Where(Storable)
            .GroupBy(s => (int)s.Id).ToDictionary(g => g.Key, g => g.Sum(s => (int)s.Count));
        int Total(Dictionary<int, int> d) => d.Values.Sum();

        var storing = Loose().Keys.ToHashSet();
        var skip = Acquisition.Of(c)?.SkipChest;
        var off = OffLimits(c, obs);
        var first = asked ?? NearestChest(c);
        var tried = new HashSet<BlockPos>();
        var went = new List<(BlockPos Chest, int N, bool NowFull, string Text)>();
        var full = new List<BlockPos>();
        var other = new List<string>();
        string? firstFail = null;
        int stored = 0, opened = 0;
        var storedItems = new Dictionary<int, int>();
        bool done = false;
        foreach (var chest in GeneratorLoop.Candidates(c, obs, first, storing))
        {
            if (tried.Contains(chest)) continue;
            // Only a chest the caller named is opened whatever its marks; fallbacks honour never-touch and "private".
            if (chest != asked && (off.Contains(chest) || skip?.Invoke(chest) != null)) continue;
            if (++opened > MaxChests) break;
            // b1.8.1: touching chest blocks are always one window (a pair); never open the same window twice.
            tried.Add(chest);
            tried.Add(chest.Offset(1, 0, 0)); tried.Add(chest.Offset(-1, 0, 0)); tried.Add(chest.Offset(0, 0, 1)); tried.Add(chest.Offset(0, 0, -1));
            var before = Loose();
            var r = await c.DepositAllAsync(chest, filter, ct).ConfigureAwait(false);
            var after = Loose();
            int moved = Math.Max(0, Total(before) - Total(after));
            foreach (var (id, n) in before) if (n - after.GetValueOrDefault(id) is > 0 and var m) storedItems[id] = storedItems.GetValueOrDefault(id) + m;
            if (r.Ok && moved == 0) moved = r.Count;
            if (r.Ok || moved > 0)
            {
                bool nowFull = !r.Ok || r.Message.Contains("chest full", StringComparison.Ordinal);
                went.Add((chest, moved, nowFull, r.Message));
                stored += moved;
                if (!nowFull) { done = true; break; }
                full.Add(chest);
                continue;
            }
            if (r.Message.StartsWith("nothing to deposit", StringComparison.Ordinal))
            {
                if (went.Count == 0) return ToolResult.Success(r.Message);
                done = true;
                break;
            }
            firstFail ??= r.Message;
            if (r.Message.Contains("full", StringComparison.OrdinalIgnoreCase)) full.Add(chest);
            else other.Add($"{P(chest)}: {(r.Message.Length > 40 ? r.Message[..40] : r.Message)}");
        }

        if (done)
        {
            // One chest took it all: the client's own line + where ("deposited 32 cobblestone in chest 5,64,4 (14,64,-5 is full)").
            if (went.Count == 1)
                return ToolResult.Success($"{went[0].Text} in chest {P(went[0].Chest)}" +
                    (asked is { } a && full.Contains(a) && went[0].Chest != a ? $" ({P(a)} is full)" : ""));
            return ToolResult.Success(StoredLine(stored, storedItems, went));
        }

        // Nothing more fits anywhere (or nowhere is known): one actionable line; the caller decides what to do.
        var left = Loose();
        // The only chest there is refused for another reason (claimed, no path): the client's own words, as before.
        if (opened == 1 && went.Count == 0 && full.Count == 0 && firstFail != null) return ToolResult.Error(firstFail);
        if (Total(left) == 0)
            return went.Count > 0 ? ToolResult.Success(StoredLine(stored, storedItems, went))
                : tried.Count == 0 ? ToolResult.Error("no chest nearby") : ToolResult.Success("nothing to deposit");
        string head = tried.Count == 0 ? "no known chest"
            : other.Count == 0 ? $"all {full.Count} known chest{(full.Count == 1 ? "" : "s")} full ({Coords(full)})"
            : $"no known chest has room ({(full.Count > 0 ? Coords(full) + " full; " : "")}{string.Join("; ", other.Take(3))})";
        var parts = new List<string> { head };
        if (went.Count > 0) parts.Add(StoredLine(stored, storedItems, went));
        parts.Add(Bag(left) + " in bag");
        parts.Add(Advice(c) + " or keep carrying");
        return ToolResult.Error(string.Join("; ", parts));
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
