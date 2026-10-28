using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The known chests a bot may take from (WorldIndexer snapshots in the shared db), nearest first, as the one view every
/// obtain/craft path plans on. Left out: a chest <see cref="AcquisitionSources.SkipChest"/> refuses (hard-protected, labelled
/// private) and one a withdraw just found stale. Never offered: the last sapling and the last dirt in storage (skyblock's
/// irreplaceable seeds stay put; the nearest copies are offered, the farthest one stays).
/// </summary>
public sealed class ChestStock
{
    public sealed record Chest(BlockPos Pos, double Dist, List<ItemCount> Items);

    /// <summary>Kept in storage: the last one of each is never planned or withdrawn.</summary>
    public static readonly int[] KeepLast = [Blocks.Sapling, Blocks.Dirt];

    public List<Chest> Chests { get; } = new();
    /// <summary>Chests known before the skip rules (for "none in the 3 known chests").</summary>
    public int Known { get; private set; }

    public static ChestStock Empty => new();

    /// <summary>Reads the chests near the bot; a busy db reads as no chests (the inventory alone then).</summary>
    public static ChestStock Read(IGameClient c, ObservationStore? obs, Func<BlockPos, string?>? skip, ISet<BlockPos>? exclude = null)
    {
        var s = new ChestStock();
        if (obs == null) return s;
        var me = c.World.Player.Position;
        try
        {
            foreach (var (row, dist) in obs.Nearest("chest", me.X, me.Y, me.Z, limit: 24, maxDistance: 160))
            {
                if (row.Bounds is not { } b) continue;
                var pos = new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ));
                s.Known++;
                if (exclude?.Contains(pos) == true || Private(row.Annotation) || skip?.Invoke(pos) != null) continue;
                var items = obs.Contents(row.Id).Where(i => i.Count > 0 && MineClaw.Data.Items.Exists(i.ItemId)).ToList();
                if (items.Count > 0) s.Chests.Add(new Chest(pos, dist, items));
            }
        }
        catch (Exception) { s.Chests.Clear(); return s; } // db busy
        foreach (var id in KeepLast)
        {
            var far = s.Chests.LastOrDefault(ch => ch.Items.Any(i => i.ItemId == id));
            if (far == null) continue;
            int at = far.Items.FindIndex(i => i.ItemId == id);
            var it = far.Items[at];
            if (it.Count <= 1) far.Items.RemoveAt(at); else far.Items[at] = it with { Count = it.Count - 1 };
        }
        return s;
    }

    /// <summary>A player's label that says hands off ("private", "Fox's, don't take").</summary>
    internal static bool Private(string? annotation) => annotation is { Length: > 0 } a
        && (a.Contains("private", StringComparison.OrdinalIgnoreCase) || a.Contains("don't take", StringComparison.OrdinalIgnoreCase)
            || a.Contains("do not take", StringComparison.OrdinalIgnoreCase) || a.Contains("personal", StringComparison.OrdinalIgnoreCase));

    private static int Key(ItemCount i) => MineClaw.Data.Items.Get(i.ItemId).MaxDamage > 0 ? 0 : i.Damage;

    /// <summary>All chest contents as planner stacks.</summary>
    public IEnumerable<ItemStack> Stacks() => Chests.SelectMany(ch => ch.Items).SelectMany(i => Split(i.ItemId, Key(i), i.Count));

    /// <summary>How many of (id, damage; -1 = any) the chests hold.</summary>
    public int Count(int id, int damage = -1) =>
        Chests.SelectMany(ch => ch.Items).Where(i => i.ItemId == id && (damage < 0 || Key(i) == damage)).Sum(i => i.Count);

    /// <summary>Spreads <paramref name="n"/> of (id, damage) over the chests nearest first: (chest, amount) pairs.</summary>
    public List<(Chest Chest, int N)> Allocate(int id, int damage, int n)
    {
        var plan = new List<(Chest, int)>();
        foreach (var ch in Chests)
        {
            if (n <= 0) break;
            int there = ch.Items.Where(i => i.ItemId == id && (damage < 0 || Key(i) == damage)).Sum(i => i.Count);
            int k = Math.Min(n, there);
            if (k <= 0) continue;
            plan.Add((ch, k));
            n -= k;
        }
        return plan;
    }

    /// <summary>A withdraw from <paramref name="pos"/> came up short: the chest's memory of the item is stale.</summary>
    public void Forget(BlockPos pos, int id)
    {
        foreach (var ch in Chests.Where(ch => ch.Pos == pos)) ch.Items.RemoveAll(i => i.ItemId == id);
    }

    public static IEnumerable<ItemStack> Split(int id, int damage, int count)
    {
        int max = Math.Max(1, (int)MineClaw.Data.Items.Get(id).MaxStack);
        for (int left = count; left > 0; left -= max) yield return new ItemStack((short)id, (byte)Math.Min(max, left), (short)Math.Max(0, damage));
    }
}
