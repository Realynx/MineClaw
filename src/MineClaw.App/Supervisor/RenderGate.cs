using System.Collections.Concurrent;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The supervisor's CPU cap: every render runs on ONE dedicated below-normal background thread, one at a time, at least
/// <c>MinRenderIntervalMs</c> apart, with a bounded queue (full = <see cref="RenderBusyException"/>, answered 503). The bot's
/// 20 TPS tick thread and the brain's thread-pool work never wait on it.
/// </summary>
public sealed class RenderGate : IDisposable
{
    private readonly BlockingCollection<Action> _queue;
    private readonly Thread _thread;
    private readonly TimeSpan _gap;
    private long _rendered;

    public RenderGate(int minIntervalMs = 120, int maxQueued = 12)
    {
        _gap = TimeSpan.FromMilliseconds(Math.Max(0, minIntervalMs));
        _queue = new BlockingCollection<Action>(Math.Max(1, maxQueued));
        _thread = new Thread(Loop) { IsBackground = true, Name = "supervisor-render", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Renders done so far (tests, cache checks).</summary>
    public long Rendered => Interlocked.Read(ref _rendered);

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { Interlocked.Increment(ref _rendered); }
        }
        bool added;
        try { added = _queue.TryAdd(Run); }
        catch (InvalidOperationException) { added = false; } // disposed
        if (!added) tcs.TrySetException(new RenderBusyException());
        return tcs.Task;
    }

    private void Loop()
    {
        var last = DateTime.MinValue;
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                var wait = last + _gap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                last = DateTime.UtcNow;
                work();
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

public sealed class RenderBusyException() : Exception("renderer busy");

/// <summary>Short-lived cache of renders by key with in-flight sharing (ten tiles asking for one bot's view = one render).</summary>
public sealed class RenderCache<T>(TimeProvider time, int capacity = 64)
{
    private readonly Dictionary<string, (DateTimeOffset At, Task<T> Task)> _map = new();
    private readonly object _lock = new();

    public Task<T> GetAsync(string key, TimeSpan ttl, Func<Task<T>> make)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var e) && (!e.Task.IsCompleted || (e.Task.IsCompletedSuccessfully && now - e.At < ttl)))
                return e.Task;
            if (_map.Count >= capacity)
                foreach (var old in _map.Where(x => x.Value.Task.IsCompleted).OrderBy(x => x.Value.At).Take(_map.Count - capacity + 1).Select(x => x.Key).ToList())
                    _map.Remove(old);
            var t = make();
            _map[key] = (now, t);
            return t;
        }
    }
}
