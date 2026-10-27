namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    private readonly object _holdLock = new();
    private readonly Dictionary<int, long> _holds = new();
    private int _nextHold;

    /// <summary>
    /// Stand still for at most <paramref name="max"/> while someone hands this bot items (night review B24: 67 tossed 29
    /// items at main's feet while main walked on chopping trees; main collected 28). The running driver (walk, follow, chop
    /// path) is paused, not aborted, and resumes when every hold is disposed or expired. Safety, combat and physics still run.
    /// </summary>
    public IDisposable HoldStill(TimeSpan max)
    {
        long ticks = Math.Max(1, (long)Math.Ceiling(max.TotalMilliseconds / Math.Max(1, Options.TickInterval.TotalMilliseconds)));
        int id;
        lock (_holdLock)
        {
            id = ++_nextHold;
            _holds[id] = TickCount + ticks;
        }
        return new Hold(this, id);
    }

    /// <summary>True while a <see cref="HoldStill"/> is active (expired holds drop out on their own).</summary>
    public bool IsHeldStill
    {
        get
        {
            lock (_holdLock)
            {
                if (_holds.Count == 0) return false;
                long now = TickCount;
                foreach (var k in _holds.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _holds.Remove(k);
                return _holds.Count > 0;
            }
        }
    }

    private void ReleaseHold(int id)
    {
        lock (_holdLock) _holds.Remove(id);
    }

    private sealed class Hold(MinecraftClient client, int id) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) client.ReleaseHold(id);
        }
    }
}
