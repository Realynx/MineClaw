namespace MineClaw.Client;

/// <summary>
/// One process, many bodies (docs/design/one-brain-many-bodies.md section 7): every body's A* search runs on the thread pool,
/// and a burst (three bodies replanning at once after a /tp) would take every core of the 4-core host from the tick and
/// reader threads. At most <see cref="MaxConcurrent"/> searches run at a time; the rest wait their turn (process-wide).
/// </summary>
public static class PathfindGate
{
    private static SemaphoreSlim _gate = new(2, 2);
    private static int _max = 2;
    private static long _waited;

    /// <summary>Concurrent A* searches allowed (default 2). Set once at startup, before any body moves.</summary>
    public static int MaxConcurrent
    {
        get => Volatile.Read(ref _max);
        set
        {
            int n = Math.Clamp(value, 1, 64);
            // Every in-process hive with bodies attaches its own InProcBodies, which sets this again: the same value keeps the gate
            // (and the searches waiting on it); only a real change swaps it.
            if (Volatile.Read(ref _max) == n) return;
            Volatile.Write(ref _max, n);
            Interlocked.Exchange(ref _gate, new SemaphoreSlim(n, n));
        }
    }

    /// <summary>Searches that had to wait for a free slot (telemetry).</summary>
    public static long Waited => Interlocked.Read(ref _waited);

    /// <summary>Runs <paramref name="search"/> on the thread pool once a slot is free.</summary>
    public static async Task<T> RunAsync<T>(Func<T> search, CancellationToken ct)
    {
        var gate = Volatile.Read(ref _gate);
        if (!gate.Wait(0))
        {
            Interlocked.Increment(ref _waited);
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        try { return await Task.Run(search, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
