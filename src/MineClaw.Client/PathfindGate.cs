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

    private static long _searches, _searchTicks, _maxTicks, _waitTicks;

    /// <summary>Searches run, their total and longest run time and the total time spent waiting for a slot, since the last
    /// <see cref="TakeStats"/> (the supervisor's once-a-minute perf line: is the pool busy with A*, and do bodies queue for it?).</summary>
    public readonly record struct Stats(long Searches, TimeSpan Busy, TimeSpan Longest, TimeSpan Waited);

    /// <summary>The counters since the last call, and starts them over.</summary>
    public static Stats TakeStats() => new(Interlocked.Exchange(ref _searches, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref _searchTicks, 0)),
        TimeSpan.FromTicks(Interlocked.Exchange(ref _maxTicks, 0)), TimeSpan.FromTicks(Interlocked.Exchange(ref _waitTicks, 0)));

    /// <summary>Runs <paramref name="search"/> on the thread pool once a slot is free.</summary>
    public static async Task<T> RunAsync<T>(Func<T> search, CancellationToken ct)
    {
        var gate = Volatile.Read(ref _gate);
        if (!gate.Wait(0))
        {
            Interlocked.Increment(ref _waited);
            long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
            await gate.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Add(ref _waitTicks, System.Diagnostics.Stopwatch.GetElapsedTime(w0).Ticks);
        }
        try
        {
            return await Task.Run(() =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { return search(); }
                finally
                {
                    long dt = System.Diagnostics.Stopwatch.GetElapsedTime(t0).Ticks;
                    Interlocked.Increment(ref _searches);
                    Interlocked.Add(ref _searchTicks, dt);
                    long m;
                    while (dt > (m = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, dt, m) != m) { }
                }
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
