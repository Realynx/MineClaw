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
/// Read-only; the store's reads are brief locks and the memory reads one indexed query each.
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
        var cands = new List<Candidate>();
        if (Looked(dim, pos, cells) is { } look) cands.Add(look);
        if (Remembered(dim, pos) is { } mem) cands.Add(mem);
        if (Ledger(dim, pos) is { } led) cands.Add(led);
        if (id > 0) cands.RemoveAll(c => !Fits(id, c)); // the block was replaced since (a furnace where a chest was): not its contents
        var best = Choose(cands);
        if (best == null)
        {
            var kind0 = ContainerSightings.KindOf(id, cells.Length > 1 ? 54 : 27) ?? "chest";
            return new ContainerView(P(cells[0]), dim, kind0, ContainerSightings.SizeOf(kind0), [], null, null, "unknown", version(), true, cells.Select(P).ToList());
        }
        if (best.Cells.Length > cells.Length) cells = best.Cells;
        int blockId = id > 0 ? id : best.BlockId;
        int size = Math.Max(best.Size, cells.Length > 1 && ChestPairs.IsPairable(blockId < 0 ? ChestPairs.Chest : blockId) ? ChestPairs.DoubleSlots : 0);
        string kind = Kind(blockId, best.MemKind, size);
        var slots = best.Slots ?? Pack(best.Counts ?? [], size);
        if (best.Slots != null && slots.Length < size) slots = [.. slots, .. Enumerable.Repeat(ItemStack.Empty, size - slots.Length)];
        // A furnace smelts on unseen: between looks its slots are estimated (an item every 10 s while fuelled), marked as such.
        bool estimated = false;
        if (kind == "furnace" && best.Source != "live" && best.Exact && best.At is { } seenAt)
            (estimated, slots) = EstimateFurnace(slots, Now - seenAt, id);
        var list = new List<InventorySlot>();
        for (int i = 0; i < slots.Length; i++)
            if (BodyInventories.Slot(i, slots[i]) is { } s) list.Add(s);
        return new ContainerView(P(cells[0]), dim, kind, size, list, best.At, best.By, best.Source, version(), best.Exact, cells.Select(P).ToList(),
            best.Partial ? true : null, estimated ? true : null);
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
        double byFuel = blockNow == Blocks.LitFurnace ? double.MaxValue : fuel.Count * per + 8; // a unit (a coal) may have been burning
        int n = (int)Math.Min(Math.Min(slots[0].Count, elapsedMs / 10_000), Math.Min(byFuel, 64 - (output.IsEmpty ? 0 : output.Count)));
        if (n <= 0) return (false, slots);
        var next = (ItemStack[])slots.Clone();
        next[0] = slots[0].Count - n <= 0 ? ItemStack.Empty : slots[0] with { Count = (byte)(slots[0].Count - n) };
        next[2] = new ItemStack((short)rec.OutputId, (byte)((output.IsEmpty ? 0 : output.Count) + n), (short)rec.OutputDamage);
        if (!fuel.IsEmpty && per > 0)
        {
            int used = Math.Min(fuel.Count, (int)Math.Ceiling(Math.Max(0, n - 1) / per));
            next[1] = fuel.Count - used <= 0 ? ItemStack.Empty : fuel with { Count = (byte)(fuel.Count - used) };
        }
        return (true, next);
    }

    /// <summary>Every known container with a block in the box (inclusive), newest look first, at most <paramref name="limit"/>.</summary>
    public (IReadOnlyList<ContainerSummary> Items, bool Capped) InBox(int dim, BlockPos min, BlockPos max, int limit)
    {
        bool In(BlockPos c) => c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && c.Z >= min.Z && c.Z <= max.Z;
        var cands = new List<Candidate>();
        long now = Now;
        foreach (var s in store.InBox(dim, min, max, limit * 2))
            cands.Add(store.Get(dim, s.Pos, now) is { } g
                ? new Candidate(g.Sighting.Cells, g.Sighting.BlockId, null, g.Sighting.Slots, null, g.Sighting.Size, g.Sighting.At, g.Sighting.By, g.Live ? "live" : "memory")
                : new Candidate(s.Cells, s.BlockId, null, s.Slots, null, s.Size, s.At, s.By, "memory"));
        if (memory() is { } obs)
        {
            try
            {
                double cx = (min.X + max.X) / 2.0, cy = (min.Y + max.Y) / 2.0, cz = (min.Z + max.Z) / 2.0;
                double r = Math.Sqrt(Math.Pow(max.X - min.X, 2) + Math.Pow(max.Y - min.Y, 2) + Math.Pow(max.Z - min.Z, 2)) / 2 + 1;
                int n = 0;
                foreach (var (row, _) in obs.Nearest(null, cx, cy, cz, 4000, r, dim))
                {
                    if (Array.IndexOf(MemoryKinds, row.Kind) < 0 || row.Bounds is not { } b || row.ContentsAt is null) continue;
                    var rc = CellsOf(b);
                    if (!rc.Any(In)) continue;
                    if (++n > limit * 2) break;
                    cands.Add(FromRow(obs, row, rc));
                }
            }
            catch (Exception) { /* memory unavailable: the looks alone */ }
        }
        try
        {
            foreach (var v in _story()?.All(dim) ?? [])
            {
                var vc = StoryCells(v);
                if (vc.Any(In)) cands.Add(FromLedger(v, vc));
            }
        }
        catch (Exception) { }
        // one group per container (overlapping cells), the best candidate of each
        var groups = new List<(HashSet<BlockPos> Cells, List<Candidate> List)>();
        foreach (var c in cands)
        {
            var g = groups.FirstOrDefault(g => c.Cells.Any(g.Cells.Contains));
            if (g.List == null) groups.Add((new HashSet<BlockPos>(c.Cells), [c]));
            else { g.List.Add(c); g.Cells.UnionWith(c.Cells); }
        }
        var outList = new List<ContainerSummary>();
        foreach (var (gc, list) in groups)
        {
            if (Choose(list) is not { } best) continue;
            var cells = gc.OrderBy(c => c.X).ThenBy(c => c.Z).ThenBy(c => c.Y).ToArray();
            if (cells.Length > 2) cells = best.Cells;
            var w = WorldAt(dim, cells[0]);
            int id = w?.GetBlock(cells[0]) ?? best.BlockId;
            if (w != null && ContainerSightings.KindOf(id) == null) continue; // the block is gone
            int size = Math.Max(best.Size, cells.Length > 1 ? ChestPairs.DoubleSlots : 0);
            var stacks = best.Slots ?? Pack(best.Counts ?? [], size);
            var totals = new Dictionary<(int, int), int>();
            int used = 0;
            foreach (var st in stacks)
            {
                if (st.IsEmpty) continue;
                used++;
                int meta = Damageable(st.Id) ? 0 : st.Damage;
                totals[(st.Id, meta)] = totals.GetValueOrDefault((st.Id, meta)) + st.Count;
            }
            var top = totals.OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => new ContainerItemCount(kv.Key.Item1, kv.Key.Item2, BodyInventories.Slot(0, new ItemStack((short)kv.Key.Item1, 1, (short)kv.Key.Item2))?.Name ?? $"unknown_{kv.Key.Item1}", kv.Value))
                .ToList();
            outList.Add(new ContainerSummary(P(cells[0]), dim, Kind(id, best.MemKind, size), size, used, totals.Values.Sum(), top, best.At, best.By, best.Source,
                cells.Select(P).ToList()));
        }
        var sorted = outList.OrderByDescending(s => s.SeenAt ?? 0).ToList();
        return (sorted.Take(Math.Max(0, limit)).ToList(), sorted.Count > limit);
    }

    // ---------------------------------------------------------------- sources

    /// <summary>The bodies' looks: live or the last one; a double chest whose halves were looked at as singles is joined.</summary>
    private Candidate? Looked(int dim, BlockPos pos, BlockPos[] cells)
    {
        long now = Now;
        var found = new List<(ContainerSighting S, bool Live)>();
        foreach (var c in cells.Contains(pos) ? cells : [pos, .. cells])
            if (store.Get(dim, c, now) is { } g && !found.Any(f => f.S.Pos == g.Sighting.Pos)) found.Add((g.Sighting, g.Live));
        if (found.Count == 0) return null;
        var whole = found.FirstOrDefault(f => cells.All(f.S.Cells.Contains));
        if (whole.S != null)
            return new Candidate(whole.S.Cells, whole.S.BlockId, null, whole.S.Slots, null, whole.S.Size, whole.S.At, whole.S.By, whole.Live ? "live" : "memory");
        if (cells.Length == 2)
        {
            // the pair as two single looks (or one): first half's 27 slots, then the second's
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
        var one = found[0];
        return new Candidate(one.S.Cells, one.S.BlockId, null, one.S.Slots, null, one.S.Size, one.S.At, one.S.By, one.Live ? "live" : "memory");
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
