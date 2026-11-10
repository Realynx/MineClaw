using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;

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

    /// <summary>The storage holding this cell (its chest or its double half): the first such in <see cref="Storages"/>' order.</summary>
    public StorageState? StorageAt(P3 at)
    {
        // Asked for every chest of every stock scan (ChestOpen), so a list walk per call made each scan quadratic in the chests
        // (live 11-10: 374 chests). The cell map is made again whenever the list or any storage's cells changed.
        int version = ListVersion<StorageState>.Of(Storages);
        long cells = StorageState.CellsVersion;
        var map = _storageCells;
        if (map == null || map.List != Storages || map.Version != version || map.Count != Storages.Count || map.Cells != cells)
            _storageCells = map = MapStorageCells(version, cells);
        return map.At.GetValueOrDefault(at);
    }

    /// <summary>StorageAt's map and what it was made from (published whole, so a reader never sees one half made).</summary>
    private sealed record StorageCellMap(Dictionary<P3, StorageState> At, List<StorageState> List, int Version, int Count, long Cells);

    private StorageCellMap? _storageCells;

    private StorageCellMap MapStorageCells(int version, long cells)
    {
        var at = new Dictionary<P3, StorageState>(Storages.Count * 2);
        foreach (var s in Storages)
        {
            at.TryAdd(s.Chest, s);
            if (s.DoubleWith is { } d) at.TryAdd(d, s);
        }
        return new StorageCellMap(at, Storages, version, Storages.Count, cells);
    }

    /// <summary>List&lt;T&gt;'s own change count (every add, remove, insert, set, clear and sort moves it).</summary>
    private static class ListVersion<T>
    {
        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_version")]
        public static extern ref int Of(List<T> list);
    }

    private bool Accepts(StorageState s, int id, int meta) => Cats is { } c && c.Accepts(s.EffectiveFilter, id, meta);

    private static int StorageRank(StorageState s) => s.Signs.Count > 0 ? 5 : StoragePriority.Rank(s.Priority);

    private bool HasRoom(StorageState s, int id, int meta) =>
        s.FreeSlots > 0 || s.Contents.Any(c => c.Id == id && c.Meta == meta && c.Count % MaxStack(id) != 0);

    /// <summary>The chest a stack goes to: accepting, room, highest priority, then nearest. Null = nothing accepts it.</summary>
    public StorageState? BestChest(int id, int meta, P3 from, StorageState? except = null)
    {
        lock (_lock)
        {
            ReconcileStorageSigns();
            return Storages.Where(s => s != except && !ChestDeconstructing(s.Chest) && ChestOpen(s.Chest) && Accepts(s, id, meta) && HasRoom(s, id, meta))
                .OrderByDescending(StorageRank).ThenBy(s => s.Chest.Dist(from)).FirstOrDefault();
        }
    }

    private void Recount(StorageState s)
    {
        int used = s.Contents.Sum(c => (c.Count + MaxStack(c.Id) - 1) / MaxStack(c.Id));
        s.FreeSlots = s.Missing ? 0 : Math.Max(0, s.Slots - used);
        s.KnownAt = Now;
        s.Rev = Touch();
        MaterialsChanged(); // what the builds can count on changed
        _stockChanged = true; // the waiting bills try again (StoryColony.StockCheck.cs)
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
        foreach (var s in Storages.Where(s => s.Chest.Dist(near) <= radius && ChestOpen(s.Chest)).OrderBy(s => s.Chest.Dist(near)))
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

    // ---------------------------------------------------------------- the storage route

    /// <summary>set | remove | copy | assign_area. Returns the affected chests, or an error.</summary>
    public (JsonNode? Result, string? Error) StorageOp(string op, IReadOnlyList<P3>? chests, StorageFilter? filter, string? priority, string? label,
        P3? from = null, Area? area = null)
    {
        lock (_lock)
        {
            if (filter != null && World is { } w)
            {
                var entries = filter.Allow.Concat(filter.Deny).Select(e => (Raw: e, Key: StoryStorageKeywords.Normalize(e, w.Data))).ToList();
                if (entries.FirstOrDefault(e => e.Key == null) is { Raw: not null } invalid) return (null, $"unknown filter entry '{invalid.Raw}' (a category id, *, id or id:meta)");
                filter = new StorageFilter { Allow = entries.Take(filter.Allow.Count).Select(e => e.Key!).Distinct().ToList(),
                    Deny = entries.Skip(filter.Allow.Count).Select(e => e.Key!).Distinct().ToList() };
            }
            if (StorageArgsError(filter, priority) is { } err) return (null, err);
            ReconcileStorageSigns(force: true);
            (JsonNode? Result, string? Error) done = op switch
            {
                "set" => SetChests(chests, filter, priority, label),
                "add" or "remove_entries" => PatchChestFilters(op, chests, filter, priority, label),
                "remove" => RemoveChests(chests),
                "copy" => CopyChests(from, chests),
                "assign_area" => AssignChestArea(area, filter, priority, label),
                _ => (null, "op must be set|add|remove_entries|remove|copy|assign_area"),
            };
            PublishLedger(); // the chest panel shows the change at once, not at the next tick (StoryColony.Containers.cs)
            return done;
        }
    }

    private string? StorageArgsError(StorageFilter? filter, string? priority)
    {
        if (priority != null && !StoragePriority.Known(priority)) return "priority must be low|normal|preferred|important|critical";
        if (filter != null && Cats is { } cats)
            foreach (var e in filter.Allow.Concat(filter.Deny))
                if (!cats.Valid(e)) return $"unknown filter entry '{e}' (a category id, *, id or id:meta)";
        return null;
    }

    private static JsonNode ChestsAnswer(IEnumerable<P3> cs) => new JsonObject { ["chests"] = new JsonArray(cs.Select(c => (JsonNode)c.Json()).ToArray()) };

    private (JsonNode? Result, string? Error) SetChests(IReadOnlyList<P3>? chests, StorageFilter? filter, string? priority, string? label)
    {
        if (chests is not { Count: > 0 }) return (null, "chests required");
        foreach (var c in chests) Set(c, filter, priority, label);
        WakeIdle();
        return (ChestsAnswer(chests), null);
    }

    private (JsonNode? Result, string? Error) RemoveChests(IReadOnlyList<P3>? chests)
    {
        if (chests is not { Count: > 0 }) return (null, "chests required");
        foreach (var c in chests)
            if (StorageAt(c) is { } s)
            {
                if (s.Signs.Count > 0) { RequestStorageFilter(s, new()); s.PendingUnregister = true; }
                else { Storages.Remove(s); Removed("storage", s.Chest.ToString()); }
            }
        return (ChestsAnswer(chests), null);
    }

    /// <summary>The <paramref name="from"/> chest's filter and priority onto each chest.</summary>
    private (JsonNode? Result, string? Error) CopyChests(P3? from, IReadOnlyList<P3>? chests)
    {
        if (from is not { } f || StorageAt(f) is not { } src) return (null, "from must be a registered chest");
        if (chests is not { Count: > 0 }) return (null, "to required");
        var filter = CloneFilter(src.EffectiveFilter);
        foreach (var c in chests) Set(c, filter, src.Signs.Count > 0 ? "critical" : src.Priority, null);
        return (ChestsAnswer(chests), null);
    }

    /// <summary>Every chest (and trapped chest) in the area becomes storage.</summary>
    private (JsonNode? Result, string? Error) AssignChestArea(Area? area, StorageFilter? filter, string? priority, string? label)
    {
        if (area is not { } a) return (null, "area required");
        if (a.Volume > 200_000) return (null, "area too large (max 200000 blocks)");
        if (World is not { } w) return (null, "no world view yet");
        var found = a.Blocks().Where(p => w.Block(p.X, p.Y, p.Z).Id is 54 or 146).ToList();
        foreach (var c in found) Set(c, filter, priority, label);
        return (ChestsAnswer(found.Select(c => StorageAt(c)?.Chest ?? c).Distinct().ToList()), null);
    }

    /// <summary>Registers (or updates) a chest; its double half is found from the world and treated as the same storage.</summary>
    private void Set(P3 c, StorageFilter? filter, string? priority, string? label)
    {
        var s = StorageAt(c);
        if (s == null)
        {
            s = new StorageState { Chest = c, DoubleWith = DoubleHalf(c) };
            if (Host.KnownChest(c) is { } known) { s.Contents = known.ToList(); }
            try { if (Host.SeenChest(c) is { } seen) s.SeenAt = seen.At; } catch (Exception) { /* no memory */ }
            Storages.Add(s);
            Recount(s);
        }
        if (filter != null || priority != null && s.Signs.Count > 0) RequestStorageFilter(s, filter ?? s.PendingFilter ?? s.EffectiveFilter);
        if (priority != null)
        {
            if (s.Signs.Count > 0) s.PendingPriority = priority;
            else s.Priority = priority;
        }
        if (label != null) s.Label = label.Length > 40 ? label[..40] : label;
        s.Rev = Touch();
    }

    private (JsonNode? Result, string? Error) PatchChestFilters(string op, IReadOnlyList<P3>? chests, StorageFilter? entries, string? priority, string? label)
    {
        if (chests is not { Count: > 0 }) return (null, "chests required");
        if (entries == null || entries.Allow.Count + entries.Deny.Count == 0) return (null, "filter entries required");
        foreach (var c in chests.Select(c => StorageAt(c)?.Chest ?? c).Distinct())
        {
            var storage = StorageAt(c);
            var filter = CloneFilter(storage?.PendingFilter ?? storage?.EffectiveFilter ?? new());
            foreach (var entry in entries.Allow)
            {
                if (op == "add") { filter.Deny.Remove(entry); if (!filter.Allow.Contains(entry)) filter.Allow.Add(entry); }
                else { filter.Allow.Remove(entry); if (!filter.Deny.Contains(entry)) filter.Deny.Add(entry); }
            }
            foreach (var entry in entries.Deny)
            {
                if (op == "add") { filter.Allow.Remove(entry); if (!filter.Deny.Contains(entry)) filter.Deny.Add(entry); }
                else filter.Deny.Remove(entry);
            }
            Set(c, filter, priority, label);
        }
        WakeIdle();
        return (ChestsAnswer(chests), null);
    }

    /// <summary>
    /// A chest the colony's own build placed (the Architect's chest, a design's chests) is storage at once, taking anything, like a
    /// station it built (live 11-06: Fox placed 14 chests because storage was full; built, none would have been used until each was
    /// set as storage by hand). A chest built beside a single storage chest makes it a double chest.
    /// </summary>
    private void ChestBuilt(GhostState g)
    {
        if (g.Remove || g.Id is not (54 or 146) || StorageAt(g.At) != null) return;
        foreach (var n in new[] { g.At.Add(1, 0, 0), g.At.Add(-1, 0, 0), g.At.Add(0, 0, 1), g.At.Add(0, 0, -1) })
            if (StorageAt(n) is { DoubleWith: null } pair && pair.Chest == n && World?.Block(n.X, n.Y, n.Z).Id == g.Id)
            {
                pair.DoubleWith = g.At;
                Recount(pair);
                WakeIdle();
                return;
            }
        Set(g.At, null, null, null);
        Alert("info", $"The chest built at {g.At} is storage now: it takes anything (its inspector sets a filter)");
        WakeIdle();
    }

    // ---------------------------------------------------------------- lids (live 11-06: "chest at 216,64,400 didn't open (blocked above)"
    // ×146 and at 223,64,400 ×95 in 7 hours: oak planks lay on one half of each double chest, and every seed fetch, food fetch and
    // delivery that picked them failed at the chest)

    private readonly Dictionary<P3, (DateTimeOffset At, P3? Lid)> _lids = new();
    private readonly Dictionary<P3, DateTimeOffset> _lidSaid = new();

    /// <summary>
    /// The block that keeps a storage chest shut, or null: it opens. Both versions refuse to open a chest (either half of a double one)
    /// under a full opaque block (mc-dev BlockChest.interact; 1.12.2 BlockChest.isBlocked). Read from the world at most every 5 s per
    /// chest; a shut chest is left out of every plan (<see cref="ChestOpen"/>) and said once (every 30 minutes at most).
    /// </summary>
    internal P3? LidBlocked(P3 chest)
    {
        if (_lids.TryGetValue(chest, out var c) && Now - c.At < TimeSpan.FromSeconds(5)) return c.Lid; // asked per chest per pick
        var s = StorageAt(chest);
        var key = s?.Chest ?? chest;
        P3? lid = World is { } w ? LidOn(w, key) ?? (s?.DoubleWith is { } d ? LidOn(w, d) : null) : null;
        _lids[chest] = (Now, lid);
        if (lid is { } at) LidAlert(key, at);
        return lid;
    }

    /// <summary>The block on top of this chest when it shuts the lid (the chest itself must be in view as a chest).</summary>
    private static P3? LidOn(IStoryWorld w, P3 c)
    {
        if (!w.Loaded(c.X, c.Z) || w.Block(c.X, c.Y, c.Z).Id is not (54 or 146)) return null;
        var above = c.Add(0, 1, 0);
        return ShutsLid(w.Data, w.Block(above.X, above.Y, above.Z).Id) ? above : null;
    }

    /// <summary>A full opaque block (planks, stone, dirt, a crafting table…): a chest under it doesn't open. Chests, glass, leaves,
    /// slabs, stairs, fences, liquids and plants don't shut it.</summary>
    internal static bool ShutsLid(IGameData d, int id)
    {
        if (id is 0 or 54 or 146 or 130 || !d.BlockExists(id)) return false;
        var b = d.Block(id);
        return b.Solid && b.CollisionHeight >= 1 && !b.Transparent && !b.Liquid;
    }

    private void LidAlert(P3 chest, P3 lid)
    {
        if (_lidSaid.TryGetValue(chest, out var said) && Now - said < TimeSpan.FromMinutes(30)) return;
        _lidSaid[chest] = Now;
        string what = World is { } w ? BlockIdName(w, w.Block(lid.X, lid.Y, lid.Z).Id).Replace('_', ' ') : "a block";
        Alert("warn", $"Chest at {chest} can't open: {what} on top of it at {lid}. Pawns leave it alone until that block is gone");
    }

    private P3? DoubleHalf(P3 c, IStoryWorld? view = null)
    {
        if ((view ?? World) is not { } w) return null;
        int id = w.Block(c.X, c.Y, c.Z).Id;
        if (id is not (54 or 146)) return null;
        foreach (var n in new[] { c.Add(1, 0, 0), c.Add(-1, 0, 0), c.Add(0, 0, 1), c.Add(0, 0, -1) })
            if (w.Block(n.X, n.Y, n.Z).Id == id) return n;
        return null;
    }
}
