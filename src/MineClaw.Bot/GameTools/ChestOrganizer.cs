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
        int clicks = 0, moved = 0;

        // 1. Survey + assign a category per chest.
        var assigned = new Dictionary<BlockPos, string>();
        if (chests.Count > 1)
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
                if (!assigned.ContainsKey(chest) && !assigned.ContainsValue(cat)) assigned[chest] = cat;

            // 2. Move misplaced items, a bounded number of passes (inventory space limits each pass).
            for (int pass = 0; pass < 3; pass++)
            {
                var carrying = new Dictionary<Key, int>();
                foreach (var c in chests)
                {
                    if (client.World.Inventory.EmptySlotCount() == 0) break;
                    var o = await OpenAsync(c, ct).ConfigureAwait(false);
                    if (!o.Ok) return o;
                    var w = client.World.Inventory.OpenWindow!;
                    assigned.TryGetValue(c, out var mine);
                    foreach (var (slot, item) in Contents(w).ToList())
                    {
                        var cat = ItemCategories.Of(item.Id, item.Damage);
                        if (cat == mine || !assigned.ContainsValue(cat)) continue;
                        if (client.World.Inventory.EmptySlotCount() == 0) break;
                        await client.ClickSlotAsync(slot, shift: true, ct: ct).ConfigureAwait(false);
                        clicks++;
                        int took = item.Count - (w.Get(slot).IsEmpty ? 0 : w.Get(slot).Count);
                        if (took > 0) carrying[new Key(item.Id, item.Damage)] = carrying.GetValueOrDefault(new Key(item.Id, item.Damage)) + took;
                    }
                    await client.CloseWindowAsync(ct).ConfigureAwait(false);
                }
                if (carrying.Count == 0) break;
                foreach (var (chest, cat) in assigned)
                {
                    if (!carrying.Any(kv => kv.Value > 0 && ItemCategories.Of(kv.Key.Id, kv.Key.Damage) == cat)) continue;
                    var o = await OpenAsync(chest, ct).ConfigureAwait(false);
                    if (!o.Ok) return o;
                    var w = client.World.Inventory.OpenWindow!;
                    for (int slot = w.MainStart; slot < w.SlotCount; slot++)
                    {
                        var s = w.Get(slot);
                        var k = new Key(s.Id, s.Damage);
                        if (s.IsEmpty || carrying.GetValueOrDefault(k) <= 0 || ItemCategories.Of(s.Id, s.Damage) != cat) continue;
                        await client.ClickSlotAsync(slot, shift: true, ct: ct).ConfigureAwait(false);
                        clicks++;
                        int put = s.Count - (w.Get(slot).IsEmpty ? 0 : w.Get(slot).Count);
                        if (put <= 0) break; // chest full
                        carrying[k] -= put;
                        moved += put;
                    }
                    await client.CloseWindowAsync(ct).ConfigureAwait(false);
                }
            }
        }

        // 3. Merge + sort each chest in place.
        foreach (var c in chests)
        {
            var o = await OpenAsync(c, ct).ConfigureAwait(false);
            if (!o.Ok) return o;
            clicks += await SortOpenWindowAsync(ct).ConfigureAwait(false);
            await client.CloseWindowAsync(ct).ConfigureAwait(false);
        }

        var summary = chests.Count == 1
            ? $"sorted chest at {Locations.Fmt(chests[0])}"
            : $"organized {chests.Count} chests (" + string.Join(", ", chests.Select(c => $"{Locations.Fmt(c)}={assigned.GetValueOrDefault(c, "mixed")}")) +
              $"), moved {moved} items";
        return ActionResult.Success($"{summary}, {clicks} clicks", moved);
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
        int clicks = 0;
        async Task Click(int slot) { await client.ClickSlotAsync(slot, ct: ct).ConfigureAwait(false); clicks++; }

        // Merge: pour later partial stacks of a type into the first partial one.
        foreach (var group in Contents(w).Where(s => s.Item.Count < MaxStack(s.Item.Id)).GroupBy(s => new Key(s.Item.Id, s.Item.Damage)).ToList())
        {
            var slots = group.Select(g => g.Slot).OrderBy(s => s).ToList();
            int target = 0, src = slots.Count - 1;
            while (target < src)
            {
                int max = MaxStack(group.Key.Id);
                if (w.Get(slots[target]).Count >= max) { target++; continue; }
                await Click(slots[src]);            // pick up
                await Click(slots[target]);         // merge (remainder stays on cursor)
                if (!client.World.Inventory.Cursor.IsEmpty) await Click(slots[src]); // put remainder back
                if (w.Get(slots[src]).IsEmpty) src--;
                else target++;
            }
        }

        // Sort: desired type order for occupied slots, compacted to the front.
        var desired = Contents(w).Select(s => s.Item)
            .OrderBy(s => ItemCategories.Rank(ItemCategories.Of(s.Id, s.Damage))).ThenBy(s => s.Id).ThenBy(s => s.Damage).ThenByDescending(s => s.Count)
            .Select(s => new Key(s.Id, s.Damage)).ToList();
        for (int i = 0; i < desired.Count; i++)
        {
            var cur = w.Get(i);
            if (!cur.IsEmpty && new Key(cur.Id, cur.Damage) == desired[i]) continue;
            int j = -1;
            for (int k = i + 1; k < w.ContainerSlots; k++)
            {
                var s = w.Get(k);
                if (!s.IsEmpty && new Key(s.Id, s.Damage) == desired[i]) { j = k; break; }
            }
            if (j < 0) continue;
            await Click(j);                          // pick up the wanted stack
            await Click(i);                          // drop it in place (swaps with what was there)
            if (!client.World.Inventory.Cursor.IsEmpty) await Click(j); // old occupant goes where the wanted one was
        }
        if (!client.World.Inventory.Cursor.IsEmpty)
        {
            for (int k = 0; k < w.ContainerSlots; k++)
                if (w.Get(k).IsEmpty) { await Click(k); break; }
        }
        return clicks;
    }

    private static int MaxStack(int id) => Items.Exists(id) ? Math.Max(1, Items.Get(id).MaxStack) : 64;

    /// <summary>Drops the second half of a double chest (both halves open the same window).</summary>
    private List<BlockPos> DedupeDoubleChests(IReadOnlyList<BlockPos> chests)
    {
        var result = new List<BlockPos>();
        foreach (var c in chests.Distinct())
        {
            if (result.Any(r => r.Y == c.Y && Math.Abs(r.X - c.X) + Math.Abs(r.Z - c.Z) == 1 &&
                                client.World.GetBlock(r) == Blocks.Chest && client.World.GetBlock(c) == Blocks.Chest)) continue;
            result.Add(c);
        }
        return result;
    }
}
