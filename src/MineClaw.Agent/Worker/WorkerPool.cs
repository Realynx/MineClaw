namespace MineClaw.Agent.Worker;

/// <summary>
/// One brain, many bodies (docs/design/one-brain-many-bodies.md section 4): the cheap worker as the brain's fallback for long
/// jobs, one <see cref="WorkerAgent"/> per body (created lazily, all sharing the primary's cached worker prefix and caps), at
/// most <see cref="MaxConcurrent"/> runs at once across bodies. A delegate beyond the cap waits for a slot and says so
/// ("worker queued for 69, 1 ahead"). A run is bound to its body: the body is the one in scope when <c>delegate</c> runs (its
/// activity slot pushed it), and the body's worker pushes it again around every tool call. The worker never authors scripts
/// (the script tool's policy refuses code from any non-brain caller); its tool set is the primary's.
/// </summary>
public sealed class WorkerPool
{
    private readonly WorkerAgent _primary;
    private readonly Action<string, WorkerAgent>? _configure;
    private readonly Dictionary<string, WorkerAgent> _byBody = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _slots;
    private int _waiting, _running;

    /// <param name="primary">The runtime's worker: main's (and every unbound run's).</param>
    /// <param name="currentBody">The body in scope (short name; null/"main" = the main body).</param>
    /// <param name="configure">Wires a new body's worker (its body scope, state header, ledger), once.</param>
    public WorkerPool(WorkerAgent primary, int maxConcurrent, Func<string?> currentBody, Action<string, WorkerAgent>? configure = null)
    {
        _primary = primary;
        MaxConcurrent = Math.Max(1, maxConcurrent);
        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        CurrentBody = currentBody;
        _configure = configure;
    }

    public int MaxConcurrent { get; }
    public Func<string?> CurrentBody { get; }

    /// <summary>Runs in progress / waiting for a slot.</summary>
    public int Running => Volatile.Read(ref _running);
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>(body, "worker queued for 69, 1 ahead") when a run has to wait for a slot.</summary>
    public event Action<string, string>? Queued;

    /// <summary>The worker bound to <paramref name="body"/> (null/"main" = the primary).</summary>
    public WorkerAgent For(string? body)
    {
        if (body is null || body.Equals("main", StringComparison.OrdinalIgnoreCase)) return _primary;
        lock (_byBody)
        {
            if (_byBody.TryGetValue(body, out var w)) return w;
            w = _primary.Sibling();
            _configure?.Invoke(body, w);
            _byBody[body] = w;
            return w;
        }
    }

    /// <summary>The worker of the body in scope.</summary>
    public WorkerAgent Current() => For(CurrentBody());

    /// <summary>The bodies that have a worker of their own so far.</summary>
    public IReadOnlyList<string> Bodies { get { lock (_byBody) return _byBody.Keys.ToList(); } }

    /// <summary>Runs <paramref name="run"/> in a pool slot, waiting (and saying so once) while every slot is taken.</summary>
    public async Task<T> RunAsync<T>(string? body, Func<Task<T>> run, CancellationToken ct)
    {
        if (!_slots.Wait(0))
        {
            int ahead = Interlocked.Increment(ref _waiting); // the waiters before this one, plus a running run that must end
            var who = body ?? "main";
            try { Queued?.Invoke(who, $"worker queued for {who}, {ahead} ahead"); } catch (Exception) { /* telemetry only */ }
            try { await _slots.WaitAsync(ct).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _waiting); }
        }
        Interlocked.Increment(ref _running);
        try { return await run().ConfigureAwait(false); }
        finally
        {
            Interlocked.Decrement(ref _running);
            _slots.Release();
        }
    }
}
