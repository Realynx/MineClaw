using MineClaw.Bot.WorldIndex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// One brain, many bodies: one pass loop pumps every body's <see cref="WorldMemory"/> in turn (main's and each clone's), so N
/// bodies never write chunk rows from N timers at once against the one db write gate. Each memory batches its own writes
/// (short transactions, <see cref="SqliteWorldMemoryStore.PutBatch"/> rows each).
/// </summary>
public sealed class WorldMemoryFlusher : IDisposable
{
    private readonly object _lock = new();
    private readonly List<WorldMemory> _members = new();
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _every;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WorldMemoryFlusher(TimeSpan? every = null, ILogger? log = null, TimeProvider? time = null)
    {
        _every = every ?? TimeSpan.FromSeconds(1);
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public int Count { get { lock (_lock) return _members.Count; } }

    /// <summary>Takes over <paramref name="memory"/>'s passes (its own loop, if it had one, stops; it stays attached).</summary>
    public void Add(WorldMemory memory)
    {
        memory.StopLoop();
        memory.Attach();
        lock (_lock) if (!_members.Contains(memory)) _members.Add(memory);
    }

    /// <summary>Stops pumping <paramref name="memory"/> after a final flush (a body logged out).</summary>
    public void Remove(WorldMemory memory, bool flush = true)
    {
        lock (_lock) _members.Remove(memory);
        if (flush) try { memory.Flush(); } catch (Exception ex) { _log.LogDebug(ex, "world memory flush failed"); }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_loop != null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(_every, _time, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    PumpAll();
                }
            }, ct);
        }
    }

    /// <summary>One pass over every member (the loop calls it; tests call it directly).</summary>
    public void PumpAll()
    {
        WorldMemory[] all;
        lock (_lock) all = _members.ToArray();
        foreach (var m in all)
        {
            try { m.Pump(); }
            catch (Exception ex) { _log.LogWarning(ex, "world memory pass failed"); }
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_lock) { _cts?.Cancel(); loop = _loop; _loop = null; }
        try { loop?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        _cts?.Dispose();
    }
}
