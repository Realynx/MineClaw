namespace MineClaw.World;

/// <summary>
/// Blocks the bot itself just dug or placed (from its own outbound 0x0E/0x0F), so observers of block changes (the
/// activity tracker, the replay harness) attribute them to the bot and never to a player standing nearby.
/// Thread-safe; tiny bounded ring. Time comes from <see cref="Clock"/> (ms, monotonic; replay sets recording time).
/// </summary>
public sealed class SelfEditLog
{
    private const int Capacity = 64;
    private readonly (BlockPos Pos, long At)[] _ring = new (BlockPos, long)[Capacity];
    private int _next, _count;
    private readonly object _lock = new();

    /// <summary>Milliseconds clock. Default: <see cref="Environment.TickCount64"/>.</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>How long after the packet a block change at that cell still counts as the bot's own.</summary>
    public long WindowMs { get; set; } = 2500;

    public void Note(BlockPos p)
    {
        long now = Clock();
        lock (_lock)
        {
            _ring[_next] = (p, now);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>True when the bot dug/placed at <paramref name="p"/> within <see cref="WindowMs"/>.</summary>
    public bool IsRecent(BlockPos p)
    {
        long now = Clock();
        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                var (pos, at) = _ring[i];
                if (pos == p && now - at <= WindowMs && now >= at - 50) return true;
            }
        }
        return false;
    }

    // ---- placements (which item went where), for the placed-block log

    private readonly (BlockPos Pos, int Item, long At)[] _places = new (BlockPos, int, long)[Capacity];
    private int _pNext, _pCount;

    /// <summary>The bot sent a placement of <paramref name="itemId"/> filling <paramref name="p"/> (also noted as an edit).</summary>
    public void NotePlace(BlockPos p, int itemId)
    {
        Note(p);
        long now = Clock();
        lock (_lock)
        {
            _places[_pNext] = (p, itemId, now);
            _pNext = (_pNext + 1) % Capacity;
            if (_pCount < Capacity) _pCount++;
        }
    }

    /// <summary>Item id of a placement the bot sent into <paramref name="p"/> within <see cref="WindowMs"/>, or null.</summary>
    public int? RecentPlace(BlockPos p)
    {
        long now = Clock();
        lock (_lock)
        {
            for (int i = 0; i < _pCount; i++)
            {
                var (pos, item, at) = _places[i];
                if (pos == p && now - at <= WindowMs && now >= at - 50) return item;
            }
        }
        return null;
    }

    public void Clear()
    {
        lock (_lock) { _count = 0; _next = 0; _pCount = 0; _pNext = 0; }
    }
}
