using System.Collections.Concurrent;
using MineClaw.Data;

namespace MineClaw.World;

/// <summary>One block a hive bot placed itself (pillar, bridge, scaffolding, a misplaced block): who and when.</summary>
public sealed record PlacedBlock(BlockPos Pos, int Dimension, int BlockId, string Bot, DateTimeOffset At);

/// <summary>
/// Every block this bot (or any hive bot, loaded from the shared db) placed and that still stands. Protection policy rule 1:
/// a block in here overrides every SOFT protection, so the bot can always take its own pillars, bridges and scaffolding back.
/// An entry counts only while the world still holds that block there (<see cref="IsPlaced"/>): a player who dug it and put
/// something else in its place owns that cell now. Lock-free reads (the pathfinder's inner loop asks).
/// <para>Filled by the client from its own confirmed placements (<see cref="SelfEditLog"/> + the block change that followed);
/// persisted and shared across the hive by the Bot layer (<see cref="Added"/>/<see cref="Removed"/> → db, <see cref="Merge"/> ← db).</para>
/// </summary>
public sealed class PlacedBlockLog
{
    /// <summary>Bound on remembered cells (oldest dropped first).</summary>
    public const int Capacity = 8192;

    private readonly ConcurrentDictionary<(int Dim, BlockPos Pos), PlacedBlock> _cells = new();

    /// <summary>Raised for a placement this process recorded (not for <see cref="Merge"/>d rows).</summary>
    public event Action<PlacedBlock>? Added;
    /// <summary>Raised when a recorded cell no longer holds the placed block (dug, replaced, reverted).</summary>
    public event Action<int, BlockPos>? Removed;

    public int Count => _cells.Count;

    public PlacedBlock? Get(BlockPos p, int dim) => _cells.GetValueOrDefault((dim, p));

    /// <summary>True when a hive bot placed <paramref name="currentId"/> (or its lit/grown form) at <paramref name="p"/>.</summary>
    public bool IsPlaced(BlockPos p, int dim, int currentId) =>
        currentId != 0 && _cells.TryGetValue((dim, p), out var e) && SameBlock(e.BlockId, currentId);

    /// <summary>Same block for ownership purposes: a placed furnace that got lit, dirt that grew grass, a redstone torch toggled.</summary>
    public static bool SameBlock(int placed, int now) => placed == now || Family(placed) == Family(now);

    private static int Family(int id) => id switch
    {
        Blocks.LitFurnace => Blocks.Furnace,
        Blocks.Grass => Blocks.Dirt,
        Blocks.RedstoneTorchOff => Blocks.RedstoneTorch,
        Blocks.RepeaterOn => Blocks.RepeaterOff,
        Blocks.FlowingWater => Blocks.Water,
        Blocks.FlowingLava => Blocks.Lava,
        _ => id,
    };

    /// <summary>Records a confirmed own placement (raises <see cref="Added"/>).</summary>
    public void Note(BlockPos p, int dim, int blockId, string bot, DateTimeOffset at)
    {
        var e = new PlacedBlock(p, dim, blockId, bot, at);
        _cells[(dim, p)] = e;
        Trim();
        try { Added?.Invoke(e); } catch (Exception) { /* persistence is best-effort */ }
    }

    /// <summary>Forgets a cell (raises <see cref="Removed"/> when it was known).</summary>
    public bool Forget(BlockPos p, int dim)
    {
        if (!_cells.TryRemove((dim, p), out _)) return false;
        try { Removed?.Invoke(dim, p); } catch (Exception) { }
        return true;
    }

    /// <summary>Adopts rows from the shared db (restart, other hive bots). No events.</summary>
    public void Merge(IEnumerable<PlacedBlock> rows, bool replace = false)
    {
        if (replace)
        {
            var keep = rows.ToList();
            var keys = keep.Select(r => (r.Dimension, r.Pos)).ToHashSet();
            foreach (var k in _cells.Keys)
                if (!keys.Contains(k)) _cells.TryRemove(k, out _);
            foreach (var r in keep) _cells[(r.Dimension, r.Pos)] = r;
        }
        else
            foreach (var r in rows) _cells[(r.Dimension, r.Pos)] = r;
        Trim();
    }

    public IReadOnlyList<PlacedBlock> All() => _cells.Values.ToList();

    /// <summary>
    /// A block change in the world: a cell this log knows that now holds something else isn't ours any more. Returns true
    /// when it forgot one.
    /// </summary>
    public bool Observe(BlockChange c, int dim) =>
        _cells.TryGetValue((dim, c.Pos), out var e) && !SameBlock(e.BlockId, c.NewId) && Forget(c.Pos, dim);

    private void Trim()
    {
        if (_cells.Count <= Capacity) return;
        foreach (var old in _cells.Values.OrderBy(v => v.At).Take(_cells.Count - Capacity).ToList())
            _cells.TryRemove((old.Dimension, old.Pos), out _);
    }
}
