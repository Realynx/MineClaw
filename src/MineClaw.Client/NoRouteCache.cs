using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// A body's recent "no route" answers (live 11-04: 334 of 462 failed searches in 5 minutes were the same unreachable chest, each an A*
/// that ran to its 30k-node / 2 s budget through <see cref="PathfindGate"/>). The same search from the same cell to the same goal with
/// the same freedoms answers the same for a while: it is not run again for <see cref="Ttl"/>, unless a block changed near its start or
/// its goal (a door opened, a wall dug, a bridge placed).
/// </summary>
public sealed class NoRouteCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);
    /// <summary>A block change this close to an entry's start or goal cell drops it.</summary>
    public const int Near = 24;

    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTime Until, BlockPos Start, Goal Goal, string Why)> _misses = new(StringComparer.Ordinal);
    private long _hits;
    private readonly Func<DateTime> _now;

    public NoRouteCache(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Searches answered from the cache (never run) since start.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    public static string Key(BlockPos start, Goal goal, GotoOptions o) =>
        $"{start.X},{start.Y},{start.Z}|{goal}|{o.AllowBreak}|{o.AllowPlace}|{o.AllowOpenDoors}|{o.ScaffoldBudget}";

    /// <summary>A cached "no route" for this search (its reason), or null.</summary>
    public string? Get(string key)
    {
        lock (_lock)
        {
            if (!_misses.TryGetValue(key, out var e)) return null;
            if (e.Until <= _now()) { _misses.Remove(key); return null; }
            Interlocked.Increment(ref _hits);
            return e.Why;
        }
    }

    public void Put(string key, BlockPos start, Goal goal, string why)
    {
        lock (_lock)
        {
            if (_misses.Count > 256) _misses.Clear();
            _misses[key] = (_now() + Ttl, start, goal, why);
        }
    }

    /// <summary>A block changed: entries starting or ending near it are dropped.</summary>
    public void Changed(BlockPos at)
    {
        lock (_lock)
        {
            if (_misses.Count == 0) return;
            foreach (var k in _misses.Where(kv => Close(kv.Value.Start, at) || kv.Value.Goal.Distance(at.X, at.Y, at.Z) <= Near).Select(kv => kv.Key).ToList())
                _misses.Remove(k);
        }
    }

    public void Clear() { lock (_lock) _misses.Clear(); }

    private static bool Close(BlockPos a, BlockPos b) => Math.Abs(a.X - b.X) <= Near && Math.Abs(a.Y - b.Y) <= Near && Math.Abs(a.Z - b.Z) <= Near;
}
