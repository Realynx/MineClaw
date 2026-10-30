using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// Cells a dug design says must stay air (a stair shaft's steps, a mine room, a strip corridor), by owner label, per dimension.
/// Every placement that isn't the excavation's own torch or sign asks it: the client's place/pillar/shelter cores refuse a
/// colliding block there and the pathfinder never plans a bridge or pillar block into one (<c>PathOptions.CanPlace</c>).
/// Live 10-30 CT 114 (Fox's spiral stair shaft, 02:07-02:21): "you keep filling in the stairs": a dig_box script "sealed" the
/// room's walls into the stair at 154,29..31,275, goto bridging put cobble on the stair step 154,15,276, and the brain had to
/// send a worker to "Unblock Fox's stairs. These cells must be AIR: …". In-process bodies share main's log (one hive).
/// Torches, signs, levers and buttons (no collision box) stay allowed: they light and label the cells without blocking a step.
/// </summary>
public sealed class KeepAirLog
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Dim, HashSet<BlockPos> Cells)> _byOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Dim, BlockPos Pos), string> _owner = new();

    /// <summary>Raised after any owner's cells change (stores persist, tests observe).</summary>
    public event Action<string>? Changed;

    /// <summary>Cells across every owner.</summary>
    public int Count { get { lock (_lock) return _owner.Count; } }

    public IReadOnlyList<string> Owners { get { lock (_lock) return _byOwner.Keys.ToArray(); } }

    /// <summary>Replaces <paramref name="owner"/>'s cells (an empty set removes it). A cell another owner holds stays theirs.</summary>
    public void Set(string owner, int dim, IEnumerable<BlockPos> cells)
    {
        lock (_lock)
        {
            RemoveLocked(owner);
            var set = new HashSet<BlockPos>(cells);
            if (set.Count == 0) return;
            _byOwner[owner] = (dim, set);
            foreach (var c in set) _owner.TryAdd((dim, c), owner);
        }
        Changed?.Invoke(owner);
    }

    /// <summary>Adds cells to <paramref name="owner"/> (an excavation growing as it goes).</summary>
    public void Add(string owner, int dim, IEnumerable<BlockPos> cells)
    {
        lock (_lock)
        {
            if (!_byOwner.TryGetValue(owner, out var e)) _byOwner[owner] = e = (dim, new HashSet<BlockPos>());
            foreach (var c in cells)
                if (e.Cells.Add(c)) _owner.TryAdd((e.Dim, c), owner);
        }
        Changed?.Invoke(owner);
    }

    public bool Remove(string owner)
    {
        bool had;
        lock (_lock) had = RemoveLocked(owner);
        if (had) Changed?.Invoke(owner);
        return had;
    }

    private bool RemoveLocked(string owner)
    {
        if (!_byOwner.Remove(owner, out var e)) return false;
        foreach (var c in e.Cells)
            if (_owner.TryGetValue((e.Dim, c), out var o) && string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)) _owner.Remove((e.Dim, c));
        // A cell two owners shared now belongs to the other one.
        foreach (var (name, other) in _byOwner)
            foreach (var c in other.Cells)
                _owner.TryAdd((other.Dim, c), name);
        return true;
    }

    public bool Contains(BlockPos p, int dim) { lock (_lock) return _owner.ContainsKey((dim, p)); }

    /// <summary>The owner of a must-be-air cell, or null.</summary>
    public string? Owner(BlockPos p, int dim) { lock (_lock) return _owner.TryGetValue((dim, p), out var o) ? o : null; }

    /// <summary>A copy of one owner's cells (empty when unknown).</summary>
    public IReadOnlySet<BlockPos> Cells(string owner)
    {
        lock (_lock) return _byOwner.TryGetValue(owner, out var e) ? new HashSet<BlockPos>(e.Cells) : new HashSet<BlockPos>();
    }

    /// <summary>
    /// Why placing <paramref name="itemId"/> into <paramref name="p"/> is refused ("154,15,276 must stay air (spiral stair)"),
    /// or null: not a must-be-air cell, or the item makes no collision box (torch, sign, lever, button).
    /// </summary>
    public string? Refusal(BlockPos p, int dim, int itemId)
    {
        var owner = Owner(p, dim);
        if (owner is null || !Collides(itemId)) return null;
        return $"{p.X},{p.Y},{p.Z} must stay air ({owner})";
    }

    /// <summary>Whether a placed item makes a block with a collision box: full blocks, slabs, fences, doors; never torches,
    /// signs, levers, buttons, redstone or rails. Item ids ≥ 256 only as doors (both versions name them "…door").</summary>
    public static bool Collides(int itemId)
    {
        if (itemId <= 0) return false;
        if (itemId < 256) return Blocks.Exists(itemId) && Blocks.Get(PlaceRules.BlockOf(itemId)).Solid;
        try { return Items.Get(itemId).Name.Contains("door", StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
