using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>organize_chest</c>: with several chests, gives each one a category (its dominant one) and moves items between
/// them through the bot's free inventory space; then merges partial stacks and sorts every chest by category/item.
/// Only items taken out during this run are put back (the bot's own stacks may top up a matching return).
/// </summary>
public sealed class ChestOrganizer(IGameClient client)
{
    private readonly record struct Key(int Id, int Damage);

    public async Task<ActionResult> OrganizeAsync(IReadOnlyList<BlockPos> chests, CancellationToken ct)
    {
        chests = DedupeDoubleChests(chests);
        if (chests.Count == 0) return ActionResult.Fail("no chests");
        var run = new Run();

        if (chests.Count > 1 && await DistributeAsync(chests, run, ct).ConfigureAwait(false) is { } failed) return failed;
        if (await SortEachAsync(chests, run, ct).ConfigureAwait(false) is { } sortFailed) return sortFailed;

        var summary = chests.Count == 1
            ? $"sorted chest at {Locations.Fmt(chests[0])}"
            : $"organized {chests.Count} chests (" + string.Join(", ", chests.Select(c => $"{Locations.Fmt(c)}={run.Assigned.GetValueOrDefault(c, "mixed")}")) +
              $"), moved {run.Moved} items";
        return ActionResult.Success($"{summary}, {run.Clicks} clicks", run.Moved);
    }

    /// <summary>1. Survey + assign a category per chest; 2. move misplaced items, a bounded number of passes (inventory space
    /// limits each pass). Returns the failed open, else null.</summary>
    private async Task<ActionResult?> DistributeAsync(IReadOnlyList<BlockPos> chests, Run run, CancellationToken ct)
    {
        var failed = await AssignAsync(chests, run, ct).ConfigureAwait(false);
        if (failed != null) return failed;
        for (int pass = 0; pass < 3; pass++)
        {
            var carrying = new Dictionary<Key, int>();
            failed = await TakeMisplacedAsync(chests, run, carrying, ct).ConfigureAwait(false);
            if (failed != null) return failed;
            if (carrying.Count == 0) break;
            failed = await PutBackAsync(run, carrying, ct).ConfigureAwait(false);
            if (failed != null) return failed;
        }
        return null;
    }

    /// <summary>3. Merge + sort each chest in place. Returns the failed open, else null.</summary>
    private async Task<ActionResult?> SortEachAsync(IReadOnlyList<BlockPos> chests, Run run, CancellationToken ct)
    {
        foreach (var c in chests)
        {
            var o = await OpenAsync(c, ct).ConfigureAwait(false);
            if (!o.Ok) return o;
            run.Clicks += await SortOpenWindowAsync(ct).ConfigureAwait(false);
            await client.CloseWindowAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>One organize run's state: each chest's category, clicks used, items moved.</summary>
    private sealed class Run
    {
        public readonly Dictionary<BlockPos, string> Assigned = new();
        public int Clicks, Moved;
    }

    /// <summary>Surveys every chest and gives each its dominant category (biggest counts first, one chest per category).
    /// Returns the failed open, else null.</summary>
    private async Task<ActionResult?> AssignAsync(IReadOnlyList<BlockPos> chests, Run run, CancellationToken ct)
    {
        var hist = new List<(BlockPos Chest, string Cat, int Count)>();
        foreach (var c in chests)
        {
            var o = await OpenAsync(c, ct).ConfigureAwait(false);
            if (!o.Ok) return o;
            var w = client.World.Inventory.OpenWindow!;
            foreach (var g in Contents(w).GroupBy(s => ItemCategories.Of(s.Item.Id, s.Item.Damage)))
                hist.Add((c, g.Key, g.Sum(s => s.Item.Count)));
            await client.CloseWindowAsync(ct).ConfigureAwait(false);
        }
        foreach (var (chest, cat, _) in hist.OrderByDescending(h => h.Count))
            if (!run.Assigned.ContainsKey(chest) && !run.Assigned.ContainsValue(cat)) run.Assigned[chest] = cat;
        return null;
    }

    /// <summary>Takes items that belong to another chest's category into the inventory (while it has room), noting what was
    /// taken in <paramref name="carrying"/>. Returns the failed open, else null.</summary>
    private async Task<ActionResult?> TakeMisplacedAsync(IReadOnlyList<BlockPos> chests, Run run, Dictionary<Key, int> carrying, CancellationToken ct)
    {
        foreach (var c in chests)
        {
            if (client.World.Inventory.EmptySlotCount() == 0) break;
            var o = await OpenAsync(c, ct).ConfigureAwait(false);
            if (!o.Ok) return o;
            var w = client.World.Inventory.OpenWindow!;
            run.Assigned.TryGetValue(c, out var mine);
            foreach (var (slot, item) in Contents(w).ToList())
            {
                var cat = ItemCategories.Of(item.Id, item.Damage);
                if (cat == mine || !run.Assigned.ContainsValue(cat)) continue;
                if (client.World.Inventory.EmptySlotCount() == 0) break;
                await TakeAsync(w, slot, item, run, carrying, ct).ConfigureAwait(false);
            }
            await client.CloseWindowAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task TakeAsync(Window w, int slot, ItemStack item, Run run, Dictionary<Key, int> carrying, CancellationToken ct)
    {
        await client.ClickSlotAsync(slot, shift: true, ct: ct).ConfigureAwait(false);
        run.Clicks++;
        int took = item.Count - Left(w, slot);
        if (took > 0) carrying[new Key(item.Id, item.Damage)] = carrying.GetValueOrDefault(new Key(item.Id, item.Damage)) + took;
    }

    /// <summary>What's left in a slot after a click.</summary>
    private static int Left(Window w, int slot) => w.Get(slot).IsEmpty ? 0 : w.Get(slot).Count;

    /// <summary>Puts carried items into the chest of their category (only what this run took out). Returns the failed open, else null.</summary>
    private async Task<ActionResult?> PutBackAsync(Run run, Dictionary<Key, int> carrying, CancellationToken ct)
    {
        foreach (var (chest, cat) in run.Assigned)
        {
            if (!carrying.Any(kv => kv.Value > 0 && ItemCategories.Of(kv.Key.Id, kv.Key.Damage) == cat)) continue;
            var o = await OpenAsync(chest, ct).ConfigureAwait(false);
            if (!o.Ok) return o;
            await PutCategoryAsync(client.World.Inventory.OpenWindow!, cat, run, carrying, ct).ConfigureAwait(false);
            await client.CloseWindowAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Shift-clicks the carried stacks of <paramref name="cat"/> from the inventory part of the open chest until it's full.</summary>
    private async Task PutCategoryAsync(Window w, string cat, Run run, Dictionary<Key, int> carrying, CancellationToken ct)
    {
        for (int slot = w.MainStart; slot < w.SlotCount; slot++)
        {
            var s = w.Get(slot);
            var k = new Key(s.Id, s.Damage);
            if (s.IsEmpty || carrying.GetValueOrDefault(k) <= 0 || ItemCategories.Of(s.Id, s.Damage) != cat) continue;
            await client.ClickSlotAsync(slot, shift: true, ct: ct).ConfigureAwait(false);
            run.Clicks++;
            int put = s.Count - Left(w, slot);
            if (put <= 0) break; // chest full
            carrying[k] -= put;
            run.Moved += put;
        }
    }

    private Task<ActionResult> OpenAsync(BlockPos p, CancellationToken ct) => client.OpenContainerAsync(p.X, p.Y, p.Z, ct);

    private static IEnumerable<(int Slot, ItemStack Item)> Contents(Window w)
    {
        for (int i = 0; i < w.ContainerSlots; i++)
            if (!w.Get(i).IsEmpty) yield return (i, w.Get(i));
    }

    /// <summary>Merges partial stacks, then selection-sorts the container slots by (category, id, damage). Returns clicks used.</summary>
    public async Task<int> SortOpenWindowAsync(CancellationToken ct)
    {
        var w = client.World.Inventory.OpenWindow ?? throw new InvalidOperationException("no window open");
        int clicks = await MergePartialsAsync(w, ct).ConfigureAwait(false);
        clicks += await SortSlotsAsync(w, ct).ConfigureAwait(false);
        if (!client.World.Inventory.Cursor.IsEmpty && FirstEmpty(w) is { } k) clicks += await ClickAsync(k, ct).ConfigureAwait(false);
        return clicks;
    }

    private async Task<int> ClickAsync(int slot, CancellationToken ct)
    {
        await client.ClickSlotAsync(slot, ct: ct).ConfigureAwait(false);
        return 1;
    }

    /// <summary>Merge: pour later partial stacks of a type into the first partial one.</summary>
    private async Task<int> MergePartialsAsync(Window w, CancellationToken ct)
    {
        int clicks = 0;
        foreach (var group in Contents(w).Where(s => s.Item.Count < MaxStack(s.Item.Id)).GroupBy(s => new Key(s.Item.Id, s.Item.Damage)).ToList())
        {
            var slots = group.Select(g => g.Slot).OrderBy(s => s).ToList();
            int target = 0, src = slots.Count - 1;
            int max = MaxStack(group.Key.Id);
            while (target < src)
            {
                if (w.Get(slots[target]).Count >= max) { target++; continue; }
                clicks += await PourAsync(slots[src], slots[target], ct).ConfigureAwait(false);
                if (w.Get(slots[src]).IsEmpty) src--;
                else target++;
            }
        }
        return clicks;
    }

    /// <summary>Picks up <paramref name="from"/>, drops it on <paramref name="to"/>, and puts any remainder back. Returns clicks.</summary>
    private async Task<int> PourAsync(int from, int to, CancellationToken ct)
    {
        int clicks = await ClickAsync(from, ct).ConfigureAwait(false);              // pick up
        clicks += await ClickAsync(to, ct).ConfigureAwait(false);                   // merge / swap (remainder or old occupant on cursor)
        if (!client.World.Inventory.Cursor.IsEmpty) clicks += await ClickAsync(from, ct).ConfigureAwait(false); // back where it came from
        return clicks;
    }

    /// <summary>Sort: desired type order for occupied slots, compacted to the front.</summary>
    private async Task<int> SortSlotsAsync(Window w, CancellationToken ct)
    {
        var desired = Contents(w).Select(s => s.Item)
            .OrderBy(s => ItemCategories.Rank(ItemCategories.Of(s.Id, s.Damage))).ThenBy(s => s.Id).ThenBy(s => s.Damage).ThenByDescending(s => s.Count)
            .Select(s => new Key(s.Id, s.Damage)).ToList();
        int clicks = 0;
        for (int i = 0; i < desired.Count; i++)
        {
            if (Holds(w, i, desired[i])) continue;
            int j = NextHolding(w, i + 1, desired[i]);
            if (j < 0) continue;
            clicks += await PourAsync(j, i, ct).ConfigureAwait(false); // the wanted stack in place, the old occupant where it was
        }
        return clicks;
    }

    private static bool Holds(Window w, int slot, Key key)
    {
        var s = w.Get(slot);
        return !s.IsEmpty && new Key(s.Id, s.Damage) == key;
    }

    /// <summary>The first container slot from <paramref name="start"/> holding <paramref name="key"/>, else -1.</summary>
    private static int NextHolding(Window w, int start, Key key)
    {
        for (int k = start; k < w.ContainerSlots; k++)
            if (Holds(w, k, key)) return k;
        return -1;
    }

    private static int? FirstEmpty(Window w)
    {
        for (int k = 0; k < w.ContainerSlots; k++)
            if (w.Get(k).IsEmpty) return k;
        return null;
    }

    private static int MaxStack(int id) => Items.Exists(id) ? Math.Max(1, Items.Get(id).MaxStack) : 64;

    /// <summary>One position per window: both halves of a double chest open the same 54 slots (ChestPairs; a trapped pair too,
    /// was: only normal chests were paired, so a trapped double was sorted twice and counted as two chests).</summary>
    private List<BlockPos> DedupeDoubleChests(IReadOnlyList<BlockPos> chests) => ChestPairs.Distinct(client.World, chests);
}
