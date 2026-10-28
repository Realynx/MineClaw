using MineClaw.Data;

namespace MineClaw.Agent.Rag;

/// <summary>
/// Room in a remembered chest, from shared memory (WorldIndexer snapshots, dated by the last open). Only an ESTIMATE: memory
/// may be stale (another player took or added things since), so a chest memory calls full is still opened and re-checked
/// before anyone says "all chests full" (live 10-27 23:06: "Chest 14,64,-5 and all known chests full" while the two chests
/// upstairs held a few sticks and planks; 102 cobble then went in fine).
/// </summary>
public static class ChestRoom
{
    public const int SingleSlots = 27, DoubleSlots = 54;

    /// <summary>A pair of chest blocks is one 54-slot window (b1.8.1 InventoryLargeChest); bounds are inclusive block coords.</summary>
    public static bool IsDouble(Box b) => Math.Abs(b.MaxX - b.MinX) >= 1 || Math.Abs(b.MaxZ - b.MinZ) >= 1;

    public static int Capacity(Box b) => IsDouble(b) ? DoubleSlots : SingleSlots;

    private static int MaxStack(int id) => Items.Exists(id) ? Math.Max(1, Items.Get(id).MaxStack) : 64;

    /// <summary>
    /// (free slots, items of <paramref name="storing"/> that fit into partial stacks already there). Contents are per-item totals,
    /// so each item is assumed packed into full stacks plus one partial (what shift-clicking produces).
    /// </summary>
    public static (int FreeSlots, int PartialRoom) Estimate(Box bounds, IReadOnlyList<ItemCount> contents, IReadOnlyCollection<int>? storing = null)
    {
        int used = 0, partial = 0;
        foreach (var c in contents)
        {
            int max = MaxStack(c.ItemId);
            int slots = (c.Count + max - 1) / max;
            used += slots;
            if (storing == null || storing.Contains(c.ItemId)) partial += slots * max - c.Count;
        }
        return (Math.Max(0, Capacity(bounds) - used), partial);
    }

    /// <summary>Memory says this chest can take some of <paramref name="storing"/> (any item when null).</summary>
    public static bool HasRoom(Box bounds, IReadOnlyList<ItemCount> contents, IReadOnlyCollection<int>? storing = null)
    {
        var (free, partial) = Estimate(bounds, contents, storing);
        return free > 0 || partial > 0;
    }

    /// <summary>
    /// Every remembered chest worth trying for a deposit, nearest-with-room first, then the ones memory calls full (oldest
    /// memory first: the likeliest to have changed). Nothing is dropped for being "full" in memory: it gets opened and re-checked.
    /// </summary>
    public static IReadOnlyList<(ObservationRow Row, double Distance, bool RoomInMemory)> Ranked(ObservationStore obs, double x, double y, double z,
        IReadOnlyCollection<int>? storing = null, int limit = 32, double maxDistance = 96)
    {
        var rows = obs.Nearest("chest", x, y, z, limit, maxDistance);
        var list = new List<(ObservationRow Row, double Distance, bool RoomInMemory)>();
        foreach (var (row, d) in rows)
        {
            if (row.Removed != null || row.Bounds is not { } b) continue;
            bool room = true;
            try { room = HasRoom(b, obs.Contents(row.Id), storing); }
            catch (Exception) { /* unknown contents: worth a look */ }
            list.Add((row, d, room));
        }
        return list.OrderByDescending(t => t.RoomInMemory)
            .ThenBy(t => t.RoomInMemory ? t.Distance : 0)
            .ThenBy(t => t.RoomInMemory ? DateTimeOffset.MinValue : t.Row.ContentsAt ?? DateTimeOffset.MinValue)
            .ThenBy(t => t.Distance).ToList();
    }
}
