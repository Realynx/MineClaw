using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Storage = chests with genre-style settings (docs/web/story-mode.md "Storage"): a category/item filter (allow, deny), a priority
/// (low … critical) and the last known contents. Deliveries go to the accepting chest with the highest priority, then the nearest,
/// that has room; a full chest is skipped; nothing accepts → an alert and the pawn keeps it (loot is never dropped on the ground).
/// Higher-priority chests pull matching items out of lower-priority ones over time (a background haul job).
/// </summary>
public sealed partial class StoryColony
{
    private StoryCategories? Cats => World is { } w ? StoryCategories.For(w.Data) : null;

    private int MaxStack(int id) => World?.Data is { } d && d.ItemExists(id) ? Math.Max(1, d.Item(id).MaxStack) : 64;

    public StorageState? StorageAt(P3 at) => Storages.FirstOrDefault(s => s.Chest == at || s.DoubleWith == at);

    private bool Accepts(StorageState s, int id, int meta) => Cats is { } c && c.Accepts(s.Filter, id, meta);

    private bool HasRoom(StorageState s, int id, int meta) =>
        s.FreeSlots > 0 || s.Contents.Any(c => c.Id == id && c.Meta == meta && c.Count % MaxStack(id) != 0);

    /// <summary>The chest a stack goes to: accepting, room, highest priority, then nearest. Null = nothing accepts it.</summary>
    public StorageState? BestChest(int id, int meta, P3 from, StorageState? except = null)
    {
        lock (_lock)
            return Storages.Where(s => s != except && Accepts(s, id, meta) && HasRoom(s, id, meta))
                .OrderByDescending(s => StoragePriority.Rank(s.Priority)).ThenBy(s => s.Chest.Dist(from)).FirstOrDefault();
    }

    private void Recount(StorageState s)
    {
        int used = s.Contents.Sum(c => (c.Count + MaxStack(c.Id) - 1) / MaxStack(c.Id));
        s.FreeSlots = Math.Max(0, s.Slots - used);
        s.KnownAt = Now;
        s.Rev = Touch();
    }

    internal void AddContents(StorageState s, Stack add)
    {
        var i = s.Contents.FindIndex(c => c.Id == add.Id && c.Meta == add.Meta);
        if (i >= 0) s.Contents[i] = s.Contents[i] with { Count = s.Contents[i].Count + add.Count };
        else s.Contents.Add(add);
        Recount(s);
    }

    internal void TakeContents(StorageState s, int id, int meta, int n)
    {
        var i = s.Contents.FindIndex(c => c.Id == id && (meta < 0 || c.Meta == meta));
        if (i < 0) return;
        var left = s.Contents[i].Count - n;
        if (left <= 0) s.Contents.RemoveAt(i); else s.Contents[i] = s.Contents[i] with { Count = left };
        Recount(s);
    }

    /// <summary>The nearest stored stack matching <paramref name="pick"/> (within <paramref name="radius"/> of <paramref name="near"/>).</summary>
    internal (StorageState Store, Stack Stack)? FindStored(Func<Stack, bool> pick, P3 near, int radius = int.MaxValue)
    {
        foreach (var s in Storages.Where(s => s.Chest.Dist(near) <= radius).OrderBy(s => s.Chest.Dist(near)))
            if (s.Contents.FirstOrDefault(pick) is { } st) return (s, st);
        return null;
    }

    /// <summary>How many of an item the colony has: every storage plus every pawn's inventory.</summary>
    public int CountItem(int id, int meta)
    {
        lock (_lock)
        {
            int n = Storages.Sum(s => s.Contents.Where(c => c.Id == id && (meta < 0 || c.Meta == meta)).Sum(c => c.Count));
            foreach (var p in Pawns.Keys) n += Host.Live(p).Inventory.Where(c => c.Id == id && (meta < 0 || c.Meta == meta)).Sum(c => c.Count);
            return n;
        }
    }

    /// <summary>
    /// Background haul: a stack in a chest that doesn't accept it any more, or that a higher-priority chest accepts and has room for,
    /// moves there (withdraw, then deposit).
    /// </summary>
    internal ActiveJob? RebalanceJob(PawnState p, P3 from)
    {
        foreach (var src in Storages.OrderBy(s => StoragePriority.Rank(s.Priority)).ThenBy(s => s.Chest.Dist(from)))
            foreach (var st in src.Contents)
            {
                var dst = BestChest(st.Id, st.Meta, src.Chest, except: src);
                if (dst == null) continue;
                bool misfiled = !Accepts(src, st.Id, st.Meta);
                if (!misfiled && StoragePriority.Rank(dst.Priority) <= StoragePriority.Rank(src.Priority)) continue;
                var key = $"move:{src.Chest}:{st.Id}:{st.Meta}";
                if (!Open(key, p.Name, null)) continue;
                int n = Math.Min(st.Count, MaxStack(st.Id) * 4);
                var name = ItemName(st.Id, st.Meta);
                var j = Job(WorkTypes.Haul, "go", $"moving {name} to {(dst.Label ?? dst.Priority + " storage")}", dst.Chest, key, "move", null, "working", f =>
                {
                    f["dist"] = 2;
                    f["fetch"] = new JsonArray(new JsonObject { ["from"] = src.Chest.ToString(), ["item"] = name, ["n"] = n });
                    f["stash"] = new JsonArray(new JsonObject { ["to"] = dst.Chest.ToString(), ["items"] = new JsonArray(name) });
                }, xp: 5);
                j.Takes.Add((src.Chest, st with { Count = n }));
                j.Puts.Add((dst.Chest, [st with { Count = n }]));
                return j;
            }
        return null;
    }

    // ---------------------------------------------------------------- the storage route

    /// <summary>set | remove | copy | assign_area. Returns the affected chests, or an error.</summary>
    public (JsonNode? Result, string? Error) StorageOp(string op, IReadOnlyList<P3>? chests, StorageFilter? filter, string? priority, string? label,
        P3? from = null, Area? area = null)
    {
        if (priority != null && !StoragePriority.Known(priority)) return (null, "priority must be low|normal|preferred|important|critical");
        if (filter != null && Cats is { } cats)
            foreach (var e in filter.Allow.Concat(filter.Deny))
                if (!cats.Valid(e)) return (null, $"unknown filter entry '{e}' (a category id, *, id or id:meta)");
        lock (_lock)
        {
            switch (op)
            {
                case "set":
                    if (chests is not { Count: > 0 }) return (null, "chests required");
                    foreach (var c in chests) Set(c, filter, priority, label);
                    return (Done(chests), null);
                case "remove":
                    if (chests is not { Count: > 0 }) return (null, "chests required");
                    foreach (var c in chests)
                        if (StorageAt(c) is { } s) { Storages.Remove(s); Removed("storage", s.Chest.ToString()); }
                    return (Done(chests), null);
                case "copy":
                    if (from is not { } f || StorageAt(f) is not { } src) return (null, "from must be a registered chest");
                    if (chests is not { Count: > 0 }) return (null, "to required");
                    foreach (var c in chests) Set(c, new StorageFilter { Allow = [.. src.Filter.Allow], Deny = [.. src.Filter.Deny] }, src.Priority, null);
                    return (Done(chests), null);
                case "assign_area":
                {
                    if (area is not { } a) return (null, "area required");
                    if (a.Volume > 200_000) return (null, "area too large (max 200000 blocks)");
                    if (World is not { } w) return (null, "no world view yet");
                    var found = a.Blocks().Where(p => w.Block(p.X, p.Y, p.Z).Id is 54 or 146).ToList();
                    foreach (var c in found) Set(c, filter, priority, label);
                    return (Done(found.Select(c => StorageAt(c)?.Chest ?? c).Distinct().ToList()), null);
                }
                default:
                    return (null, "op must be set|remove|copy|assign_area");
            }
        }
        static JsonNode Done(IEnumerable<P3> cs) => new JsonObject { ["chests"] = new JsonArray(cs.Select(c => (JsonNode)c.Json()).ToArray()) };
    }

    /// <summary>Registers (or updates) a chest; its double half is found from the world and treated as the same storage.</summary>
    private void Set(P3 c, StorageFilter? filter, string? priority, string? label)
    {
        var s = StorageAt(c);
        if (s == null)
        {
            s = new StorageState { Chest = c, DoubleWith = DoubleHalf(c) };
            if (Host.KnownChest(c) is { } known) { s.Contents = known.ToList(); }
            Storages.Add(s);
            Recount(s);
        }
        if (filter != null) s.Filter = filter;
        if (priority != null) s.Priority = priority;
        if (label != null) s.Label = label.Length > 40 ? label[..40] : label;
        s.Rev = Touch();
    }

    private P3? DoubleHalf(P3 c)
    {
        if (World is not { } w) return null;
        int id = w.Block(c.X, c.Y, c.Z).Id;
        if (id is not (54 or 146)) return null;
        foreach (var n in new[] { c.Add(1, 0, 0), c.Add(-1, 0, 0), c.Add(0, 0, 1), c.Add(0, 0, -1) })
            if (w.Block(n.X, n.Y, n.Z).Id == id) return n;
        return null;
    }
}
