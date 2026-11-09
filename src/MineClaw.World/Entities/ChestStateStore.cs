namespace MineClaw.World;

public readonly record struct ChestState(BlockPos Position, int OpenCount, DateTime At);

/// <summary>Last server opener count for each observed chest. Closed observations stay until unload so a
/// newer close wins over another connection's older open when the viewer merges their snapshots.</summary>
public sealed class ChestStateStore
{
    private readonly object _lock = new();
    private readonly Dictionary<BlockPos, ChestState> _states = new();
    public const int Capacity = 4096;

    public void Record(BlockPos position, int openCount, DateTime at)
    {
        lock (_lock)
        {
            _states[position] = new(position, Math.Clamp(openCount, 0, 255), at);
            if (_states.Count > Capacity)
                _states.Remove(_states.Values.MinBy(s => s.At).Position);
        }
    }

    public IReadOnlyList<ChestState> Snapshot() { lock (_lock) return _states.Values.ToArray(); }
    public void Forget(BlockPos position) { lock (_lock) _states.Remove(position); }
    public void Clear() { lock (_lock) _states.Clear(); }
    public void DropColumn(int x, int z)
    {
        lock (_lock)
            foreach (var p in _states.Keys.Where(p => p.X >> 4 == x && p.Z >> 4 == z).ToArray()) _states.Remove(p);
    }
}
