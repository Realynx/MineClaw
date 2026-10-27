using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Inventory;

public readonly record struct Click(int Slot, bool Right = false, bool Shift = false)
{
    public override string ToString() => (Shift ? "S" : Right ? "R" : "L") + Slot;
}

/// <summary>Which slots of a window a move reads from / writes to.</summary>
public enum Region { Container, Player, Hotbar, Main }

/// <summary>
/// A throw-away copy of a window used for planning: clicks are simulated with the World's own b1.8.1
/// <see cref="InventoryManager.ApplyClick"/> (same merge/swap/shift routing the client predicts with), so a plan is
/// exactly what the server will do — or the client gets a 0x6A reject and the caller re-plans from the resync.
/// </summary>
public sealed class WindowModel
{
    private readonly InventoryManager _mgr = new();
    private readonly int _id;

    public WindowType Type { get; }
    public int ContainerSlots { get; }
    public int SlotCount { get; }

    private WindowModel(WindowType type, int containerSlots, IReadOnlyList<ItemStack> slots, ItemStack cursor)
    {
        Type = type;
        ContainerSlots = type == WindowType.Player ? 0 : containerSlots;
        _id = type == WindowType.Player ? 0 : 1;
        if (type != WindowType.Player) _mgr.Open(_id, type, "", containerSlots);
        _mgr.SetWindowItems(_id, slots);
        _mgr.SetSlot(InventoryManager.CursorWindow, InventoryManager.CursorSlot, cursor);
        SlotCount = Window.SlotCount;
    }

    private Window Window => _mgr.GetWindow(_id)!;

    public static WindowModel Of(Window w, ItemStack cursor) =>
        new(w.Type, w.ContainerSlots, Enumerable.Range(0, w.SlotCount).Select(w.Get).ToArray(), cursor);

    public static WindowModel Create(WindowType type, int containerSlots, IReadOnlyList<ItemStack> slots, ItemStack? cursor = null) =>
        new(type, containerSlots, slots, cursor ?? ItemStack.Empty);

    public WindowModel Clone() => new(Type, ContainerSlots, Slots(), Cursor);

    public ItemStack this[int slot] => Window.Get(slot);
    public ItemStack Cursor => _mgr.Cursor;
    public ItemStack[] Slots() => Enumerable.Range(0, SlotCount).Select(Window.Get).ToArray();

    public void Apply(Click c) => _mgr.ApplyClick(c.Slot, c.Right, c.Shift);
    public void Apply(IEnumerable<Click> clicks) { foreach (var c in clicks) Apply(c); }

    public int MainStart => Window.MainStart;
    public int HotbarStart => Window.HotbarStart;

    public IEnumerable<int> SlotsOf(Region r) => r switch
    {
        Region.Container => Enumerable.Range(0, ContainerSlots),
        Region.Hotbar => Enumerable.Range(HotbarStart, 9),
        Region.Main => Enumerable.Range(MainStart, 27),
        _ => Enumerable.Range(MainStart, 36),
    };

    public int Count(int id, int? damage, IEnumerable<int> slots) =>
        slots.Select(s => this[s]).Where(s => Same(s, id, damage)).Sum(s => s.Count);

    public static bool Same(ItemStack s, int id, int? damage) => !s.IsEmpty && s.Id == id && (damage is null || s.Damage == damage);
    public static int Max(int id) => Math.Max(1, Items.Get(id).MaxStack);
}

/// <summary>
/// Plans the fewest clicks for a transfer: shift-click whole stacks when b1.8.1 shift routing lands them where wanted,
/// pick-up/place for directed moves, and half-pickups / right-click singles for exact counts, merging into partial stacks
/// first. Every candidate is verified on a cloned <see cref="WindowModel"/>, and plans always end with an empty cursor.
/// </summary>
public static class ClickPlanner
{
    /// <summary>Clicks that move exactly <paramref name="count"/> (≤ available) of item (id, damage) from
    /// <paramref name="from"/> to <paramref name="to"/> slots. <paramref name="dest"/> pins the destination slot.</summary>
    public static List<Click> Transfer(WindowModel m, int id, int? damage, int count, IReadOnlyCollection<int> from,
        IReadOnlyCollection<int> to, int? dest = null)
    {
        var plan = new List<Click>();
        var sim = m.Clone();
        int moved = 0;
        for (int guard = 0; guard < 256 && moved < count; guard++)
        {
            int left = count - moved;
            var best = Candidates(sim, id, damage, left, from, to, dest)
                .Select(c => (Clicks: c, Moved: Delta(sim, c, id, damage, to)))
                .Where(x => x.Moved > 0 && x.Moved <= left)
                .OrderByDescending(x => x.Moved == left).ThenBy(x => x.Clicks.Count / (double)x.Moved).ThenBy(x => x.Clicks.Count)
                .FirstOrDefault();
            if (best.Clicks is null) break;
            sim.Apply(best.Clicks);
            plan.AddRange(best.Clicks);
            moved += best.Moved;
        }
        return plan;
    }

    /// <summary>Items of (id, damage) that land in <paramref name="to"/> if <paramref name="clicks"/> run on <paramref name="m"/>
    /// (−1 when the cursor isn't empty afterwards).</summary>
    private static int Delta(WindowModel m, List<Click> clicks, int id, int? damage, IReadOnlyCollection<int> to)
    {
        var c = m.Clone();
        int before = c.Count(id, damage, to);
        c.Apply(clicks);
        return c.Cursor.IsEmpty ? c.Count(id, damage, to) - before : -1;
    }

    private static IEnumerable<List<Click>> Candidates(WindowModel m, int id, int? damage, int want, IReadOnlyCollection<int> from,
        IReadOnlyCollection<int> to, int? dest)
    {
        var sources = from.Where(s => WindowModel.Same(m[s], id, damage)).OrderBy(s => m[s].Count == want ? 0 : 1).ThenBy(s => m[s].Count).ToList();
        bool crossSection = from.All(s => s < m.ContainerSlots) != to.All(s => s < m.ContainerSlots) && m.ContainerSlots > 0;
        foreach (int s in sources.Take(6))
        {
            int n = m[s].Count;
            // Whole stack by shift-click (routing is the server's: other section, merge first, then empties).
            if (dest is null && crossSection && n <= want) yield return [new Click(s, Shift: true)];
            var dests = Destinations(m, id, damage, to, dest).ToList();
            if (dests.Count == 0) yield break;
            // Whole stack (or as much as fits) by pick-up + place into partial/empty destinations.
            if (n <= want) yield return PickPlace(m, s, false, dests, n);
            // Exact counts from a bigger stack.
            int half = (n + 1) / 2;
            if (half == want) yield return PickPlace(m, s, true, dests, half);
            if (n > want)
            {
                // a) take all, drop `want` singles on the destination, put the rest back
                var a = new List<Click> { new(s) };
                a.AddRange(Enumerable.Repeat(new Click(dests[0], Right: true), want));
                a.Add(new Click(s));
                yield return a;
                // b) take all, put (n-want) singles back, place the rest
                if (n - want < want)
                {
                    var b = new List<Click> { new(s) };
                    b.AddRange(Enumerable.Repeat(new Click(s, Right: true), n - want));
                    b.AddRange(PlaceAll(m, dests, want));
                    yield return b;
                }
                // c) take half, then trim/extend with singles
                if (half > want)
                {
                    var c = new List<Click> { new(s, Right: true) };
                    c.AddRange(Enumerable.Repeat(new Click(s, Right: true), half - want));
                    c.AddRange(PlaceAll(m, dests, want));
                    yield return c;
                }
            }
        }
    }

    private static List<Click> PickPlace(WindowModel m, int src, bool half, List<int> dests, int amount)
    {
        var l = new List<Click> { new(src, Right: half) };
        l.AddRange(PlaceAll(m, dests, amount));
        return l;
    }

    /// <summary>Left-clicks destinations (partials first) until <paramref name="amount"/> is placed.</summary>
    private static IEnumerable<Click> PlaceAll(WindowModel m, List<int> dests, int amount)
    {
        int left = amount;
        foreach (int d in dests)
        {
            if (left <= 0) yield break;
            var s = m[d];
            int room = s.IsEmpty ? int.MaxValue : WindowModel.Max(s.Id) - s.Count;
            if (room <= 0) continue;
            yield return new Click(d);
            left -= Math.Min(room, left);
        }
    }

    /// <summary>Destination slots: the pinned one, else partial stacks of the item, then empty slots, in slot order.</summary>
    private static IEnumerable<int> Destinations(WindowModel m, int id, int? damage, IReadOnlyCollection<int> to, int? dest)
    {
        if (dest is { } d) { yield return d; yield break; }
        foreach (int s in to) if (WindowModel.Same(m[s], id, damage) && m[s].Count < WindowModel.Max(id)) yield return s;
        foreach (int s in to) if (m[s].IsEmpty) yield return s;
    }

    /// <summary>Clicks that put whatever is on the cursor down (merge, else empty slot) — run before any close.</summary>
    public static List<Click> ClearCursor(WindowModel m, IEnumerable<int> prefer)
    {
        var plan = new List<Click>();
        var sim = m.Clone();
        var order = prefer.Concat(Enumerable.Range(0, m.SlotCount)).Distinct().ToList();
        for (int guard = 0; guard < 64 && !sim.Cursor.IsEmpty; guard++)
        {
            var cur = sim.Cursor;
            int slot = order.FirstOrDefault(s => WindowModel.Same(sim[s], cur.Id, cur.Damage) && sim[s].Count < WindowModel.Max(cur.Id), -1);
            if (slot < 0) slot = order.FirstOrDefault(s => sim[s].IsEmpty && (m.Type != WindowType.Player || s >= 9), -1);
            if (slot < 0) break;
            var c = new Click(slot);
            sim.Apply(c);
            plan.Add(c);
        }
        return plan;
    }

    // ------------------------------------------------------------------ permutations (arrange)

    /// <summary>
    /// Clicks that move stacks so container slot i ends up holding the stack now in <c>source[i]</c> (−1 = empty).
    /// Decomposes the slot mapping into cycles and chains and walks each with the cursor (left-clicking a different
    /// item swaps): a cycle or chain of L moves costs L+1 clicks, fixed points cost nothing — the minimum for
    /// cursor-only moves. Same-item stacks must already be matched to stay put (merges would break swaps).
    /// </summary>
    public static List<Click> Permute(IReadOnlyList<int> source)
    {
        int n = source.Count;
        var next = new int[n]; // where the stack in slot j goes
        Array.Fill(next, -1);
        for (int i = 0; i < n; i++) if (source[i] >= 0 && source[i] != i) next[source[i]] = i;
        var hasIncoming = new bool[n];
        for (int j = 0; j < n; j++) if (next[j] >= 0) hasIncoming[next[j]] = true;
        var done = new bool[n];
        var plan = new List<Click>();
        // Chains: start where nothing comes in (the slot ends empty), walk forward to an empty slot.
        for (int start = 0; start < n; start++)
        {
            if (next[start] < 0 || hasIncoming[start] || done[start]) continue;
            plan.Add(new Click(start));
            done[start] = true;
            for (int j = next[start]; j >= 0; j = next[j])
            {
                plan.Add(new Click(j));
                done[j] = true;
                if (next[j] < 0) break;
            }
        }
        // Cycles.
        for (int start = 0; start < n; start++)
        {
            if (next[start] < 0 || done[start]) continue;
            plan.Add(new Click(start));
            done[start] = true;
            int j = next[start];
            while (j != start)
            {
                plan.Add(new Click(j));
                done[j] = true;
                j = next[j];
            }
            plan.Add(new Click(start));
        }
        return plan;
    }

    /// <summary>Merges partial stacks of the same item inside <paramref name="slots"/> (fewest clicks: 2 per merge, +1 when
    /// the receiving stack overflows).</summary>
    public static List<Click> Consolidate(WindowModel m, IReadOnlyList<int> slots)
    {
        var plan = new List<Click>();
        var sim = m.Clone();
        foreach (var g in slots.Where(s => !sim[s].IsEmpty).GroupBy(s => (sim[s].Id, sim[s].Damage)).ToList())
        {
            int max = WindowModel.Max(g.Key.Id);
            if (max <= 1) continue;
            var partial = g.Where(s => sim[s].Count < max).ToList();
            while (partial.Count >= 2)
            {
                // Pour the smallest stack into the largest partial one.
                partial = partial.OrderBy(s => sim[s].Count).ToList();
                int from = partial[0], into = partial[^1];
                var step = new List<Click> { new(from), new(into) };
                var probe = sim.Clone();
                probe.Apply(step);
                if (!probe.Cursor.IsEmpty) step.Add(new Click(from));
                sim.Apply(step);
                plan.AddRange(step);
                partial = g.Where(s => !sim[s].IsEmpty && sim[s].Count < max).ToList();
            }
        }
        return plan;
    }
}
