using MineClaw.Hive.Ipc;
using MineClaw.World;

namespace MineClaw.Hive.Inventory;

/// <summary>
/// Main's side of a hand-off TO a clone: main's live inventory (and its held item), a toss of N of one item toward a spot,
/// and standing still meanwhile. Real: the main bot's client (App MainHive); tests: a fake.
/// </summary>
public interface IHiveDonor
{
    Vec3d? Position { get; }
    /// <summary>Main's live inventory.</summary>
    IReadOnlyList<ItemMsg> Items();
    /// <summary>Item id in main's hand (0 = nothing): main keeps the tool it is using.</summary>
    int HeldId { get; }
    /// <summary>Items main never gives away (its reserved set).</summary>
    IReadOnlySet<int> Reserved => new HashSet<int>();
    IDisposable? HoldStill(TimeSpan max);
    /// <summary>Looks at <paramref name="toward"/> and drops <paramref name="count"/> of item <paramref name="id"/>; returns how many left.</summary>
    Task<int> TossAsync(int id, int count, Vec3d toward, CancellationToken ct);
}

/// <summary>
/// Which of an item main (or another hive bot) can spare (live 3, 14:16:58: 67's tool-duty loadout came up "no pickaxe,
/// no axe" while main carried 2 stone pickaxes and a stone axe). An item is a loadout kind ("pickaxe": any tier, best first)
/// or an item name ("stone_pickaxe", "torch"). The giver always keeps one of the kind: the one in its hand, else its best.
/// </summary>
public static class HiveLending
{
    public sealed record Pick(int Id, int Count, string Name);

    /// <summary>Candidate item ids (best first) for a kind or an item name, or null when unknown.</summary>
    public static int[]? Resolve(string? item)
    {
        if (string.IsNullOrWhiteSpace(item)) return null;
        var key = item.Trim();
        if (MineClaw.Bot.GameTools.Loadout.Kinds.TryGetValue(key, out var k)) return k.Ids;
        var info = MineClaw.Data.Items.ByName(key.Replace(' ', '_'));
        return info == null ? null : [info.Id];
    }

    public static int Have(IEnumerable<ItemMsg>? items, int[] ids) => (items ?? []).Where(i => ids.Contains(i.Id)).Sum(i => i.Count);

    /// <summary>What the giver can hand over (one item id, up to <paramref name="count"/>), keeping one of the kind; null = nothing spare.</summary>
    /// <param name="only">Acceptable item ids, best first (e.g. stone tier or better); null = every id of the kind/item.</param>
    public static Pick? Spare(IReadOnlyList<ItemMsg> items, string item, int count, int heldId = 0, IReadOnlySet<int>? reserved = null, int[]? only = null)
    {
        if ((only is { Length: > 0 } ? only : Resolve(item)) is not { } ids) return null;
        ids = ids.Where(id => reserved == null || !reserved.Contains(id)).ToArray();
        int total = Have(items, ids);
        if (total < 2) return null;
        // The kept one: the item in hand when it is of this kind, else the best tier carried.
        int kept = ids.Contains(heldId) && Have(items, [heldId]) > 0 ? heldId : ids.First(id => Have(items, [id]) > 0);
        int want = Math.Clamp(count, 1, total - 1);
        foreach (var id in ids)
        {
            int avail = Have(items, [id]) - (id == kept ? 1 : 0);
            if (avail <= 0) continue;
            var name = items.First(i => i.Id == id).Name;
            return new Pick(id, Math.Min(want, avail), name);
        }
        return null;
    }
}
