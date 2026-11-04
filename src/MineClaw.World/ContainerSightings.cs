using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// What one body saw in a container: the container's own slots in window order (a double chest's 54, the west/north half first;
/// a furnace's input, fuel, output), when and by whom. <see cref="Pos"/> is the container's canonical cell (a double chest's
/// west/north half, <see cref="ChestPairs.Canonical"/>); <see cref="Cells"/> are all its blocks.
/// </summary>
public sealed record ContainerSighting(int Dim, BlockPos Pos, BlockPos[] Cells, int BlockId, ItemStack[] Slots, long At, string By)
{
    public int Size => Slots.Length;
}

/// <summary>
/// The hive's freshest look into every container any in-process body opened: a snapshot when a body's container window gets its
/// contents or a slot changes (throttled), and one when the window closes. While a window is still open its owner can be asked
/// for a live copy (<see cref="Record"/>'s <c>live</c>). Bounded (<see cref="Capacity"/> containers; the oldest looks go first),
/// in memory only: what persists across restarts is the world memory's own container rows (the indexer's item counts).
/// Thread-safe; nothing here runs on or waits for a game tick.
/// </summary>
public sealed class ContainerSightings
{
    /// <summary>This process's store (every in-process body's client feeds it).</summary>
    public static ContainerSightings Process { get; } = new();

    private sealed class Entry(ContainerSighting s, Func<ItemStack[]?>? live)
    {
        public ContainerSighting S = s;
        public Func<ItemStack[]?>? Live = live;
    }

    private readonly Dictionary<(int Dim, BlockPos Pos), Entry> _byPos = new();
    private readonly Dictionary<(int Dim, BlockPos Cell), BlockPos> _cells = new();
    private readonly object _lock = new();

    /// <summary>At most this many containers are remembered; past it the oldest tenth goes in one pass.</summary>
    public int Capacity { get; init; } = 4096;

    public int Count { get { lock (_lock) return _byPos.Count; } }

    /// <summary>
    /// Remembers a look. <paramref name="live"/> answers a fresh copy of the slots while the window is still open (null once it
    /// closed); a later look without one ends the live state. A look that covers a cell another container claimed (a single chest
    /// that became half of a double one) replaces that container.
    /// </summary>
    public void Record(ContainerSighting s, Func<ItemStack[]?>? live = null)
    {
        lock (_lock)
        {
            foreach (var c in s.Cells)
                if (_cells.TryGetValue((s.Dim, c), out var owner) && owner != s.Pos) Forget(s.Dim, owner);
            if (_byPos.TryGetValue((s.Dim, s.Pos), out var old))
                foreach (var c in old.S.Cells)
                    if (Array.IndexOf(s.Cells, c) < 0) _cells.Remove((s.Dim, c));
            _byPos[(s.Dim, s.Pos)] = new Entry(s, live);
            foreach (var c in s.Cells) _cells[(s.Dim, c)] = s.Pos;
            if (_byPos.Count > Capacity) Evict();
        }
    }

    /// <summary>The window of the container at <paramref name="pos"/> closed: its last look stays, no longer live.</summary>
    public void EndLive(int dim, BlockPos pos)
    {
        lock (_lock)
            if (_byPos.TryGetValue((dim, pos), out var e)) e.Live = null;
    }

    /// <summary>
    /// The container whose blocks include <paramref name="cell"/>: the live copy when a body has it open (<c>Live</c> true, stamped
    /// <paramref name="now"/>), else its last look. Null = nobody here ever looked.
    /// </summary>
    public (ContainerSighting Sighting, bool Live)? Get(int dim, BlockPos cell, long now)
    {
        Entry? e;
        lock (_lock)
        {
            if (!_cells.TryGetValue((dim, cell), out var pos) || !_byPos.TryGetValue((dim, pos), out e)) return null;
        }
        if (e.Live is { } live)
        {
            ItemStack[]? fresh = null;
            try { fresh = live(); } catch (Exception) { }
            if (fresh != null) return (e.S with { Slots = fresh, At = now }, true);
            lock (_lock) e.Live = null;
        }
        return (e.S, false);
    }

    /// <summary>Every remembered container with a block in the box (inclusive), newest look first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<ContainerSighting> InBox(int dim, BlockPos min, BlockPos max, int limit)
    {
        lock (_lock)
            return _byPos.Values.Select(e => e.S)
                .Where(s => s.Dim == dim && s.Cells.Any(c => c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && c.Z >= min.Z && c.Z <= max.Z))
                .OrderByDescending(s => s.At).Take(Math.Max(0, limit)).ToList();
    }

    private void Forget(int dim, BlockPos pos)
    {
        if (!_byPos.Remove((dim, pos), out var e)) return;
        foreach (var c in e.S.Cells)
            if (_cells.TryGetValue((dim, c), out var o) && o == pos) _cells.Remove((dim, c));
    }

    private void Evict()
    {
        int drop = Math.Max(1, _byPos.Count - Capacity + Capacity / 10);
        foreach (var k in _byPos.OrderBy(kv => kv.Value.S.At).Take(drop).Select(kv => kv.Key).ToList()) Forget(k.Dim, k.Pos);
    }

    /// <summary>Block id → the container kind the viewer names, null = not a container with contents to show.</summary>
    public static string? KindOf(int blockId, int size = 0) => blockId switch
    {
        54 => size > 27 ? "double_chest" : "chest",
        146 => size > 27 ? "double_chest" : "trapped_chest", // 1.12.2 trapped chest
        61 or 62 => "furnace",
        23 => "dispenser",
        158 => "dropper",       // 1.12.2
        154 => "hopper",        // 1.12.2
        130 => "ender_chest",   // 1.12.2: the opener's own ender inventory
        117 => "brewing_stand", // 1.12.2
        >= 219 and <= 234 => "shulker_box",
        _ => null,
    };

    /// <summary>The usual slot count of a kind (a double chest 54).</summary>
    public static int SizeOf(string kind) => kind switch
    {
        "double_chest" => 54,
        "furnace" => 3,
        "dispenser" or "dropper" => 9,
        "hopper" or "brewing_stand" => 5,
        _ => 27,
    };
}
