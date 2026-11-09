using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>
/// One container for the viewer's container panel (<c>GET /api/world/containers?dim&amp;x&amp;y&amp;z</c>). <paramref name="Slots"/> are the
/// non-empty slots by container slot index (a chest 0–26, a double chest 0–53 with the west/north half first; a furnace 0 input,
/// 1 fuel, 2 output; a brewing stand 0–2 bottles, 3 ingredient, 4 fuel). <paramref name="Source"/>: <c>live</c> = a body has it open
/// right now; <c>memory</c> = the last look any body took (exact slots) or the world memory's item counts; <c>story</c> = Story
/// Mode's storage ledger (item counts); <c>unknown</c> = nobody looked yet. <paramref name="Exact"/> false = the slots were laid out
/// from item counts (the real slot order is not known). <paramref name="Partial"/> = a double chest of which only one half was seen.
/// </summary>
public sealed record ContainerView(int[] Pos, int Dim, string Kind, int Size, IReadOnlyList<InventorySlot> Slots, long? SeenAt, string? SeenBy,
    string Source, string? Version, bool Exact, IReadOnlyList<int[]> Cells, bool? Partial = null, bool? Estimated = null);

/// <summary>An item total of a container summary.</summary>
public sealed record ContainerItemCount(int Id, int Meta, string Name, int Count);

/// <summary>A known container for map badges (<c>GET /api/world/containers?dim&amp;x0..z1</c>): its biggest five item totals, the item
/// total, slots used of <paramref name="Size"/>, and the same freshness fields as <see cref="ContainerView"/>.</summary>
public sealed record ContainerSummary(int[] Pos, int Dim, string Kind, int Size, int Used, int Total, IReadOnlyList<ContainerItemCount> Top,
    long? SeenAt, string? SeenBy, string Source, IReadOnlyList<int[]> Cells);

/// <summary>Story Mode's storage chests (the colonies' ledgers), for <see cref="ContainerKnowledge"/>.</summary>
public interface IStoryChests
{
    StoryChestView? At(int dim, BlockPos cell);
    IReadOnlyList<StoryChestView> All(int dim);
}

/// <summary>Every colony's storage over main's world (Story Mode plays in main's world: other dimensions have none).</summary>
public sealed class StoryChests(StoryColonies colonies, Func<int> dimension) : IStoryChests
{
    public StoryChestView? At(int dim, BlockPos c)
    {
        if (dim != dimension()) return null;
        foreach (var col in colonies.All)
            if (col.ChestView(new P3(c.X, c.Y, c.Z)) is { } v) return v;
        return null;
    }

    public IReadOnlyList<StoryChestView> All(int dim) => dim != dimension() ? [] : colonies.All.SelectMany(c => c.ChestViews()).ToList();
}

/// <summary>
/// What the hive knows of a container's contents, freshest first:
/// <list type="number">
/// <item><b>live</b>: an in-process body has its window open now (a copy of the window, <see cref="ContainerSightings"/>).</item>
/// <item>else the newest of: the last look any in-process body took (exact slots: on open, on slot changes, on close), the world
/// memory's row (the indexer's item counts with when/by whom, persisted across restarts), and Story Mode's storage ledger (item
/// counts, kept by the director after each deposit and withdrawal). A count-only source beats an exact look only when it is newer by
/// more than <see cref="Grace"/> (they usually stem from the same look a moment apart), or when it differs and the look is older.</item>
/// <item>else <b>unknown</b> (200, no slots).</item>
/// </list>
/// A double chest answers from either half with its 54 slots; two looks taken while each half was a single chest are joined.
/// Read-only; the viewer can show a known look immediately while slower memory/ledger reads refresh in the background.
/// </summary>
public sealed class ContainerKnowledge(ContainerSightings store, Func<IReadOnlyList<WorldState>> worlds, Func<ObservationStore?> memory,
    Func<string?> version, TimeProvider? time = null, Func<IStoryChests?>? story = null)
{
    /// <summary>Story Mode's chests in this process (set when Story Mode is wired).</summary>
    public static IStoryChests? Story { get; set; }

    /// <summary>A count-only source must be this much newer than an exact look to win.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private static readonly string[] MemoryKinds = ["chest", "furnace", "dispenser", "dropper", "hopper", "ender_chest", "brewing_stand", "shulker_box"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<IStoryChests?> _story = story ?? (() => Story);
    private readonly object _refreshGate = new();
    private readonly Dictionary<(int Dim, BlockPos Pos, int Id), (long At, List<Candidate> Items)> _refreshed = [];
    private readonly HashSet<(int Dim, BlockPos Pos, int Id)> _refreshing = [];
    private const int RefreshLimit = 128, RefreshConcurrency = 2;
    private const long RefreshMs = 3000;

    /// <summary>One candidate answer (cells first-half first).</summary>
    private sealed record Candidate(BlockPos[] Cells, int BlockId, string? MemKind, ItemStack[]? Slots, IReadOnlyList<(int Id, int Meta, int Count)>? Counts,
        int Size, long? At, string? By, string Source, bool Partial = false)
    {
        public bool Exact => Slots != null;
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private WorldState? WorldAt(int dim, BlockPos p)
    {
        try
        {
            foreach (var w in worlds())
                if (w.Dimension == dim && w.IsLoaded(p.X, p.Z)) return w;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The container with a block at <paramref name="pos"/>; null = a live world shows a block there that holds nothing.</summary>
    public ContainerView? Get(int dim, BlockPos pos)
    {
        var w = WorldAt(dim, pos);
        int id = w?.GetBlock(pos) ?? -1;
        if (w != null && ContainerSightings.KindOf(id) == null) return null;
        BlockPos[] cells = w != null && ChestPairs.IsPairable(id) ? ChestPairs.Cells(w, pos) : [pos];
        var best = Choose(CandidatesAt(dim, pos, cells, id));
        return best == null ? Unknown(dim, id, cells) : View(dim, id, cells, best);
    }

    /// <summary>
    /// The interactive viewer never waits on the observation database or colony lock when an in-process look is already
    /// available. Its original timestamp/source remain visible; bounded, coalesced refreshes reconcile newer count-only
    /// sources on subsequent polls. A first persisted/offline answer is cached too, preserving its original freshness.
    /// </summary>
    public ContainerView? GetForViewer(int dim, BlockPos pos)
    {
        var w = WorldAt(dim, pos);
        int id = w?.GetBlock(pos) ?? -1;
        if (w != null && ContainerSightings.KindOf(id) == null) return null;
        BlockPos[] cells = w != null && ChestPairs.IsPairable(id) ? ChestPairs.Cells(w, pos) : [pos];
        var look = Looked(dim, pos, cells);
        if (look != null && id > 0 && !Fits(id, look)) look = null;
        if (look?.Source == "live") return View(dim, id, cells, look);

        var key = (dim, look?.Cells[0] ?? cells[0], id);
        List<Candidate> previous;
        lock (_refreshGate)
            previous = _refreshed.TryGetValue(key, out var previousEntry) ? previousEntry.Items : [];
        var candidates = previous.Where(c => id <= 0 || Fits(id, c)).ToList();
        if (look != null) candidates.Add(look);
        var best = Choose(candidates);
        if (best == null)
        {
            // No honest cached answer: keep the first-read behavior, including a genuinely unknown container.
            var stored = StoredCandidates(dim, pos).Where(c => id <= 0 || Fits(id, c)).ToList();
            best = Choose(stored);
            if (best == null) return Unknown(dim, id, cells);
            CacheStored(key, stored);
            return View(dim, id, cells, best);
        }

        bool refresh;
        lock (_refreshGate)
        {
            bool found = _refreshed.TryGetValue(key, out var cached);
            refresh = (!found || Now - cached.At >= RefreshMs) && _refreshing.Count < RefreshConcurrency && _refreshing.Add(key);
        }
        if (refresh) _ = Task.Run(() => Refresh(key));
        return View(dim, id, cells, best);
    }

    private void CacheStored((int Dim, BlockPos Pos, int Id) key, List<Candidate> items)
    {
        lock (_refreshGate)
        {
            // Sources can be temporarily unavailable; retain the last known answer with its original SeenAt.
            // A known empty inventory still has a timestamped candidate and replaces the old counts normally.
            if (items.Count == 0 && _refreshed.TryGetValue(key, out var previous)) items = previous.Items;
            if (_refreshed.Count >= RefreshLimit && !_refreshed.ContainsKey(key))
                _refreshed.Remove(_refreshed.MinBy(kv => kv.Value.At).Key);
            _refreshed[key] = (Now, items);
        }
    }

    private void Refresh((int Dim, BlockPos Pos, int Id) key)
    {
        try
        {
            var items = StoredCandidates(key.Dim, key.Pos).Where(c => key.Id <= 0 || Fits(key.Id, c)).ToList();
            CacheStored(key, items);
        }
        catch (Exception) { /* Retain the known look if a source is shutting down/unavailable. */ }
        finally { lock (_refreshGate) _refreshing.Remove(key); }
    }

    /// <summary>The looks, the world memory's row and Story Mode's ledger for the container; with a block there, only those of its kind.</summary>
    private List<Candidate> CandidatesAt(int dim, BlockPos pos, BlockPos[] cells, int id)
    {
        var cands = new List<Candidate>();
        if (Looked(dim, pos, cells) is { } look && (id <= 0 || Fits(id, look)))
        {
            if (look.Source == "live") return [look]; // authoritative; slower sources can never win
            cands.Add(look);
        }
        cands.AddRange(StoredCandidates(dim, pos));
        if (id > 0) cands.RemoveAll(c => !Fits(id, c)); // the block was replaced since (a furnace where a chest was): not its contents
        return cands;
    }

    private List<Candidate> StoredCandidates(int dim, BlockPos pos)
    {
        var cands = new List<Candidate>();
        if (Remembered(dim, pos) is { } mem) cands.Add(mem);
        if (Ledger(dim, pos) is { } led) cands.Add(led);
        return cands;
    }

    /// <summary>Nobody looked yet: the block's kind (a chest without one), no slots.</summary>
    private ContainerView Unknown(int dim, int id, BlockPos[] cells)
    {
        var kind0 = ContainerSightings.KindOf(id, cells.Length > 1 ? 54 : 27) ?? "chest";
        return new ContainerView(P(cells[0]), dim, kind0, ContainerSightings.SizeOf(kind0), [], null, null, "unknown", version(), true, cells.Select(P).ToList());
    }

    private ContainerView View(int dim, int id, BlockPos[] cells, Candidate best)
    {
        if (best.Cells.Length > cells.Length) cells = best.Cells;
        int blockId = id > 0 ? id : best.BlockId;
        int size = Math.Max(best.Size, cells.Length > 1 && ChestPairs.IsPairable(blockId < 0 ? ChestPairs.Chest : blockId) ? ChestPairs.DoubleSlots : 0);
        string kind = Kind(blockId, best.MemKind, size);
        var (estimated, slots) = Estimate(kind, best, SlotsOf(best, size), id);
        var list = new List<InventorySlot>();
        for (int i = 0; i < slots.Length; i++)
            if (BodyInventories.Slot(i, slots[i]) is { } s) list.Add(s);
        return new ContainerView(P(cells[0]), dim, kind, size, list, best.At, best.By, best.Source, version(), best.Exact, cells.Select(P).ToList(),
            best.Partial ? true : null, estimated ? true : null);
    }

    /// <summary>A furnace smelts on unseen: between looks its slots are estimated (an item every 10 s while fuelled), marked as such.</summary>
    private (bool Estimated, ItemStack[] Slots) Estimate(string kind, Candidate best, ItemStack[] slots, int id)
    {
        if (kind == "furnace" && best.Source != "live" && best.Exact && best.At is { } seenAt)
            return EstimateFurnace(slots, Now - seenAt, id);
        return (false, slots);
    }

    /// <summary>A look's slots padded to <paramref name="size"/>, or item counts laid out in slot order.</summary>
    private static ItemStack[] SlotsOf(Candidate best, int size)
    {
        if (best.Slots is not { } slots) return Pack(best.Counts ?? [], size);
        return slots.Length < size ? [.. slots, .. Enumerable.Repeat(ItemStack.Empty, size - slots.Length)] : slots;
    }

    /// <summary>
    /// A furnace's slots <paramref name="elapsedMs"/> after a look: the input goes down and the output up by an item every 10 s (200
    /// ticks), as far as the fuel goes (what was in the fuel slot; a furnace lit now burns on), the output never past a stack or onto a
    /// different item. Unchanged (false) when nothing would have smelted.
    /// </summary>
    internal static (bool Estimated, ItemStack[] Slots) EstimateFurnace(ItemStack[] slots, long elapsedMs, int blockNow)
    {
        if (slots.Length < 3 || slots[0].IsEmpty || elapsedMs < 10_000) return (false, slots);
        if (Smelting.ResultFor(slots[0].Id, slots[0].Damage) is not { } rec) return (false, slots);
        var output = slots[2];
        if (!output.IsEmpty && (output.Id != rec.OutputId || output.Damage != rec.OutputDamage)) return (false, slots);
        var fuel = slots[1];
        double per = fuel.IsEmpty ? 0 : Smelting.ItemsPerFuel(fuel.Id);
        int n = Smelted(slots[0], output, fuel, per, elapsedMs, blockNow);
        if (n <= 0) return (false, slots);
        return (true, AfterSmelting(slots, rec, n, per));
    }

    /// <summary>How many items smelted: one per 10 s, as far as the input, the fuel and the output stack allow.</summary>
    private static int Smelted(ItemStack input, ItemStack output, ItemStack fuel, double per, long elapsedMs, int blockNow)
    {
        double byFuel = blockNow == Blocks.LitFurnace ? double.MaxValue : fuel.Count * per + 8; // a unit (a coal) may have been burning
        return (int)Math.Min(Math.Min(input.Count, elapsedMs / 10_000), Math.Min(byFuel, 64 - (output.IsEmpty ? 0 : output.Count)));
    }

    /// <summary>The slots after <paramref name="n"/> items smelted: input down, output up, the fuel burnt for them.</summary>
    private static ItemStack[] AfterSmelting(ItemStack[] slots, SmeltingRecipe rec, int n, double per)
    {
        var (output, fuel) = (slots[2], slots[1]);
        var next = (ItemStack[])slots.Clone();
        next[0] = slots[0].Count - n <= 0 ? ItemStack.Empty : slots[0] with { Count = (byte)(slots[0].Count - n) };
        next[2] = new ItemStack((short)rec.OutputId, (byte)((output.IsEmpty ? 0 : output.Count) + n), (short)rec.OutputDamage);
        if (!fuel.IsEmpty && per > 0)
        {
            int used = Math.Min(fuel.Count, (int)Math.Ceiling(Math.Max(0, n - 1) / per));
            next[1] = fuel.Count - used <= 0 ? ItemStack.Empty : fuel with { Count = (byte)(fuel.Count - used) };
        }
        return next;
    }

    /// <summary>Every known container with a block in the box (inclusive), newest look first, at most <paramref name="limit"/>.</summary>
    public (IReadOnlyList<ContainerSummary> Items, bool Capped) InBox(int dim, BlockPos min, BlockPos max, int limit)
    {
        var cands = new List<Candidate>();
        AddLooksInBox(cands, dim, min, max, limit);
        if (memory() is { } obs)
        {
            try { AddMemoryInBox(cands, obs, dim, min, max, limit); }
            catch (Exception) { /* memory unavailable: the looks alone */ }
        }
        try { AddLedgersInBox(cands, dim, min, max); }
        catch (Exception) { }
        var outList = new List<ContainerSummary>();
        foreach (var (gc, list) in GroupByContainer(cands))
            if (Summary(dim, gc, list) is { } summary) outList.Add(summary);
        var sorted = outList.OrderByDescending(s => s.SeenAt ?? 0).ToList();
        return (sorted.Take(Math.Max(0, limit)).ToList(), sorted.Count > limit);
    }

    private static bool In(BlockPos c, BlockPos min, BlockPos max) =>
        c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && c.Z >= min.Z && c.Z <= max.Z;

    /// <summary>The bodies' looks in the box (live while a window is open).</summary>
    private void AddLooksInBox(List<Candidate> cands, int dim, BlockPos min, BlockPos max, int limit)
    {
        long now = Now;
        foreach (var s in store.InBox(dim, min, max, limit * 2))
            cands.Add(store.Get(dim, s.Pos, now) is { } g
                ? new Candidate(g.Sighting.Cells, g.Sighting.BlockId, null, g.Sighting.Slots, null, g.Sighting.Size, g.Sighting.At, g.Sighting.By, g.Live ? "live" : "memory")
                : new Candidate(s.Cells, s.BlockId, null, s.Slots, null, s.Size, s.At, s.By, "memory"));
    }

    /// <summary>The world memory's containers with contents near the box's centre that have a cell in the box, at most twice the limit.</summary>
    private static void AddMemoryInBox(List<Candidate> cands, ObservationStore obs, int dim, BlockPos min, BlockPos max, int limit)
    {
        double cx = (min.X + max.X) / 2.0, cy = (min.Y + max.Y) / 2.0, cz = (min.Z + max.Z) / 2.0;
        double r = Math.Sqrt(Math.Pow(max.X - min.X, 2) + Math.Pow(max.Y - min.Y, 2) + Math.Pow(max.Z - min.Z, 2)) / 2 + 1;
        int n = 0;
        foreach (var (row, _) in obs.Nearest(null, cx, cy, cz, 4000, r, dim))
        {
            if (!HasContents(row, out var b)) continue;
            var rc = CellsOf(b);
            if (!rc.Any(c => In(c, min, max))) continue;
            if (++n > limit * 2) break;
            cands.Add(FromRow(obs, row, rc));
        }
    }

    /// <summary>A container row of a kind the memory tracks, with bounds and a contents stamp.</summary>
    private static bool HasContents(ObservationRow row, out Box bounds)
    {
        bounds = default;
        if (Array.IndexOf(MemoryKinds, row.Kind) < 0 || row.Bounds is not { } b || row.ContentsAt is null) return false;
        bounds = b;
        return true;
    }

    private void AddLedgersInBox(List<Candidate> cands, int dim, BlockPos min, BlockPos max)
    {
        foreach (var v in _story()?.All(dim) ?? [])
        {
            var vc = StoryCells(v);
            if (vc.Any(c => In(c, min, max))) cands.Add(FromLedger(v, vc));
        }
    }

    /// <summary>One group per container (overlapping cells), in the order the candidates first name it.</summary>
    private static List<(HashSet<BlockPos> Cells, List<Candidate> List)> GroupByContainer(List<Candidate> cands)
    {
        var groups = new List<(HashSet<BlockPos> Cells, List<Candidate> List)>();
        foreach (var c in cands)
        {
            var g = groups.FirstOrDefault(g => c.Cells.Any(g.Cells.Contains));
            if (g.List == null) groups.Add((new HashSet<BlockPos>(c.Cells), [c]));
            else { g.List.Add(c); g.Cells.UnionWith(c.Cells); }
        }
        return groups;
    }

    /// <summary>A group's best candidate as a summary; null when none fits or the block is gone.</summary>
    private ContainerSummary? Summary(int dim, HashSet<BlockPos> gc, List<Candidate> list)
    {
        if (Choose(list) is not { } best) return null;
        var cells = gc.OrderBy(c => c.X).ThenBy(c => c.Z).ThenBy(c => c.Y).ToArray();
        if (cells.Length > 2) cells = best.Cells;
        var w = WorldAt(dim, cells[0]);
        int id = w?.GetBlock(cells[0]) ?? best.BlockId;
        if (w != null && ContainerSightings.KindOf(id) == null) return null; // the block is gone
        int size = Math.Max(best.Size, cells.Length > 1 ? ChestPairs.DoubleSlots : 0);
        var (totals, used) = ItemTotals(best.Slots ?? Pack(best.Counts ?? [], size));
        var top = totals.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => new ContainerItemCount(kv.Key.Item1, kv.Key.Item2, BodyInventories.Slot(0, new ItemStack((short)kv.Key.Item1, 1, (short)kv.Key.Item2))?.Name ?? $"unknown_{kv.Key.Item1}", kv.Value))
            .ToList();
        return new ContainerSummary(P(cells[0]), dim, Kind(id, best.MemKind, size), size, used, totals.Values.Sum(), top, best.At, best.By, best.Source,
            cells.Select(P).ToList());
    }

    /// <summary>Item totals by id and meta (a tool's damage isn't its meta) and the slots used.</summary>
    private static (Dictionary<(int, int), int> Totals, int Used) ItemTotals(ItemStack[] stacks)
    {
        var totals = new Dictionary<(int, int), int>();
        int used = 0;
        foreach (var st in stacks)
        {
            if (st.IsEmpty) continue;
            used++;
            int meta = Damageable(st.Id) ? 0 : st.Damage;
            totals[(st.Id, meta)] = totals.GetValueOrDefault((st.Id, meta)) + st.Count;
        }
        return (totals, used);
    }

    // ---------------------------------------------------------------- sources

    /// <summary>The bodies' looks: live or the last one; a double chest whose halves were looked at as singles is joined.</summary>
    private Candidate? Looked(int dim, BlockPos pos, BlockPos[] cells)
    {
        var found = LooksAt(dim, pos, cells);
        if (found.Count == 0) return null;
        var whole = found.FirstOrDefault(f => cells.All(f.S.Cells.Contains));
        if (whole.S != null) return FromLook(whole.S, whole.Live);
        if (cells.Length == 2) return JoinHalves(cells, found);
        return FromLook(found[0].S, found[0].Live);
    }

    /// <summary>The looks at the container's cells (and the asked cell), one per sighting.</summary>
    private List<(ContainerSighting S, bool Live)> LooksAt(int dim, BlockPos pos, BlockPos[] cells)
    {
        long now = Now;
        var found = new List<(ContainerSighting S, bool Live)>();
        foreach (var c in cells.Contains(pos) ? cells : [pos, .. cells])
            if (store.Get(dim, c, now) is { } g && !found.Any(f => f.S.Pos == g.Sighting.Pos)) found.Add((g.Sighting, g.Live));
        return found;
    }

    private static Candidate FromLook(ContainerSighting s, bool live) =>
        new(s.Cells, s.BlockId, null, s.Slots, null, s.Size, s.At, s.By, live ? "live" : "memory");

    /// <summary>The pair as two single looks (or one): first half's 27 slots, then the second's; dated by the older look.</summary>
    private static Candidate JoinHalves(BlockPos[] cells, List<(ContainerSighting S, bool Live)> found)
    {
        var slots = new ItemStack[ChestPairs.DoubleSlots];
        Array.Fill(slots, ItemStack.Empty);
        ContainerSighting? older = null;
        bool partial = false, allLive = true;
        for (int h = 0; h < 2; h++)
        {
            var f = found.FirstOrDefault(x => x.S.Cells.Contains(cells[h]));
            if (f.S == null) { partial = true; allLive = false; continue; }
            allLive &= f.Live;
            Array.Copy(f.S.Slots, 0, slots, h * ChestPairs.HalfSlots, Math.Min(ChestPairs.HalfSlots, f.S.Slots.Length));
            if (older == null || f.S.At < older.At) older = f.S;
        }
        return new Candidate(cells, older!.BlockId, null, slots, null, ChestPairs.DoubleSlots, older.At, older.By, allLive ? "live" : "memory", partial);
    }

    /// <summary>The world memory's row for the container (the indexer's item counts, when and by whom).</summary>
    private Candidate? Remembered(int dim, BlockPos pos)
    {
        if (memory() is not { } obs) return null;
        try
        {
            foreach (var (row, _) in obs.Nearest(null, pos.X, pos.Y, pos.Z, 8, 0.01, dim))
                if (Array.IndexOf(MemoryKinds, row.Kind) >= 0 && row.Bounds is { } b && row.ContentsAt != null)
                    return FromRow(obs, row, CellsOf(b));
        }
        catch (Exception) { }
        return null;
    }

    private static Candidate FromRow(ObservationStore obs, ObservationRow row, BlockPos[] cells)
    {
        var counts = obs.Contents(row.Id).Select(c => (c.ItemId, c.Damage, c.Count)).ToList();
        int size = row.Kind == "chest" && cells.Length > 1 ? ChestPairs.DoubleSlots : ContainerSightings.SizeOf(row.Kind);
        return new Candidate(cells, -1, row.Kind, null, counts, size, row.ContentsAt?.ToUnixTimeMilliseconds(), row.ContentsBy, "memory");
    }

    private Candidate? Ledger(int dim, BlockPos pos)
    {
        try { return _story()?.At(dim, pos) is { } v ? FromLedger(v, StoryCells(v)) : null; }
        catch (Exception) { return null; }
    }

    private static Candidate FromLedger(StoryChestView v, BlockPos[] cells) =>
        new(cells, ChestPairs.Chest, "chest", null, v.Contents.Select(c => (c.Id, c.Meta, c.Count)).ToList(), v.Slots, v.KnownAt?.ToUnixTimeMilliseconds(),
            v.Colony, "story");

    private static BlockPos[] StoryCells(StoryChestView v)
    {
        var a = new BlockPos(v.Chest.X, v.Chest.Y, v.Chest.Z);
        if (v.DoubleWith is not { } d) return [a];
        var b = new BlockPos(d.X, d.Y, d.Z);
        return ChestPairs.IsFirst(a, b) ? [a, b] : [b, a];
    }

    private static BlockPos[] CellsOf(Box b)
    {
        var list = new List<BlockPos>();
        for (int x = (int)Math.Floor(b.MinX); x <= (int)Math.Floor(b.MaxX) && list.Count < 4; x++)
            for (int y = (int)Math.Floor(b.MinY); y <= (int)Math.Floor(b.MaxY) && list.Count < 4; y++)
                for (int z = (int)Math.Floor(b.MinZ); z <= (int)Math.Floor(b.MaxZ) && list.Count < 4; z++)
                    list.Add(new BlockPos(x, y, z));
        return list.ToArray();
    }

    // ---------------------------------------------------------------- choosing and shaping

    /// <summary>Live first; else the newest, an exact look winning unless a count-only source is newer by more than <see cref="Grace"/>
    /// and says something else.</summary>
    private static Candidate? Choose(IReadOnlyList<Candidate> cands)
    {
        if (cands.FirstOrDefault(c => c.Source == "live") is { } live) return live;
        var exact = cands.Where(c => c.Exact).OrderByDescending(c => c.At ?? 0).FirstOrDefault();
        var counted = cands.Where(c => !c.Exact && (c.At != null || (c.Counts?.Count ?? 0) > 0)).OrderByDescending(c => c.At ?? 0).FirstOrDefault();
        if (exact == null) return counted;
        if (counted == null) return exact;
        long e = exact.At ?? 0, k = counted.At ?? 0;
        if (k <= e + (long)Grace.TotalMilliseconds) return exact;
        return SameCounts(exact, counted) ? exact : counted;
    }

    private static bool SameCounts(Candidate exact, Candidate counted)
    {
        var a = Totals(exact.Slots!.Where(s => !s.IsEmpty).Select(s => ((int)s.Id, (int)s.Damage, (int)s.Count)));
        var b = Totals(counted.Counts ?? []);
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    private static Dictionary<(int, int), int> Totals(IEnumerable<(int Id, int Meta, int Count)> items)
    {
        var d = new Dictionary<(int, int), int>();
        foreach (var (id, meta, n) in items) d[(id, meta)] = d.GetValueOrDefault((id, meta)) + n;
        return d;
    }

    /// <summary>The candidate describes the kind of block that stands there now (a chest look for a chest, a furnace's for a
    /// lit or unlit furnace, any shulker colour for a shulker box).</summary>
    private static bool Fits(int worldId, Candidate c)
    {
        string? now = Family(ContainerSightings.KindOf(worldId));
        return c.BlockId > 0 ? Family(ContainerSightings.KindOf(c.BlockId)) == now : Family(c.MemKind ?? "chest") == now;
    }

    private static string? Family(string? kind) => kind is "double_chest" or "trapped_chest" ? "chest" : kind;

    private static string Kind(int blockId, string? memKind, int size)
    {
        if (ContainerSightings.KindOf(blockId, size) is { } k) return k;
        return memKind switch
        {
            null or "chest" => size > ChestPairs.HalfSlots ? "double_chest" : "chest",
            _ => memKind,
        };
    }

    private static bool Damageable(int id)
    {
        try { return Items.Get(id).MaxDamage > 0; } catch (Exception) { return false; }
    }

    private static int MaxStack(int id)
    {
        try { return Math.Clamp(Items.Get(id).MaxStack, 1, 64); } catch (Exception) { return 64; }
    }

    /// <summary>Item totals laid out in slot order (full stacks, biggest totals first), as far as <paramref name="size"/> holds them.</summary>
    internal static ItemStack[] Pack(IReadOnlyList<(int Id, int Meta, int Count)> counts, int size)
    {
        var slots = new ItemStack[Math.Max(0, size)];
        Array.Fill(slots, ItemStack.Empty);
        int i = 0;
        foreach (var (id, meta, total) in counts.Where(c => c.Count > 0).OrderByDescending(c => c.Count))
        {
            int left = total, max = MaxStack(id);
            while (left > 0 && i < slots.Length)
            {
                int n = Math.Min(left, max);
                slots[i++] = new ItemStack((short)id, (byte)n, (short)meta);
                left -= n;
            }
        }
        return slots;
    }

    private static int[] P(BlockPos p) => [p.X, p.Y, p.Z];
}
