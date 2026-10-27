using System.Text;

namespace MineClaw.Agent.Events;

public sealed class EventQueueOptions
{
    /// <summary>After a waking event, wait this long for more (chat comes in bursts) before waking the model.</summary>
    public int CoalesceWindowMs { get; set; } = 1500;
    /// <summary>Upper bound on that debounce, measured from the first waking event.</summary>
    public int MaxBatchDelayMs { get; set; } = 4000;
    /// <summary>Identical low-value events (danger repeats, notices, joins) within this window are dropped.</summary>
    public int DedupeWindowSeconds { get; set; } = 30;
    public int MaxPending { get; set; } = 40;
    /// <summary>Non-addressed chat kept as background context (newest N).</summary>
    public int MaxBackgroundChat { get; set; } = 6;
}

/// <summary>A coalesced set of events handed to the model in one turn.</summary>
public sealed record EventBatch(IReadOnlyList<GameEvent> Events)
{
    public EventPriority MaxPriority => Events.Count == 0 ? EventPriority.Background : Events.Max(e => e.Priority);
    public bool Has(EventKind kind) => Events.Any(e => e.Kind == kind);
    public bool HasDanger => Events.Any(e => e.Kind is EventKind.LowHealth or EventKind.VoidFall or EventKind.Death);
    public IEnumerable<GameEvent> Waking => Events.Where(e => e.Wakes);

    /// <summary>True if any waking event was injected test chat (the episode is then tagged source=test).</summary>
    public bool IsTest => Waking.Any(e => e.IsTest);

    /// <summary>True when everything that woke us is chat/whisper/join/idle (no task or danger) — triage candidate.</summary>
    public bool IsConversational => Waking.All(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.PlayerJoined
        or EventKind.PlayerLeft or EventKind.IdleTick or EventKind.Notice or EventKind.Activity or EventKind.Emote);

    /// <summary>The single player to whisper back to, if every waking message was a whisper from them.</summary>
    public string? WhisperOnlyFrom
    {
        get
        {
            var w = Waking.ToList();
            if (w.Count == 0 || w.Any(e => e.Kind != EventKind.Whisper)) return null;
            var p = w[0].Player;
            return w.All(e => string.Equals(e.Player, p, StringComparison.OrdinalIgnoreCase)) ? p : null;
        }
    }

    /// <summary>Compact multi-line rendering; consecutive chat lines from the same speaker are merged.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        GameEvent? pendingChat = null;
        var chatParts = new List<string>();

        void Flush()
        {
            if (pendingChat == null) return;
            sb.AppendLine((pendingChat with { Text = string.Join(" / ", chatParts) }).Format());
            pendingChat = null;
            chatParts.Clear();
        }

        foreach (var e in Events)
        {
            bool chatLike = e.Kind is EventKind.Chat or EventKind.Whisper;
            if (chatLike && pendingChat != null && pendingChat.Kind == e.Kind && pendingChat.Priority == e.Priority &&
                string.Equals(pendingChat.Player, e.Player, StringComparison.OrdinalIgnoreCase) && e.Count == 1)
            {
                chatParts.Add(e.Text);
                continue;
            }
            Flush();
            if (chatLike && e.Count == 1)
            {
                pendingChat = e;
                chatParts.Add(e.Text);
            }
            else
            {
                sb.AppendLine(e.Format());
            }
        }
        Flush();
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Thread-safe queue that coalesces game events so the model is only woken for meaningful batches:
/// identical repeats merge (xN) or are dropped, progress/health updates keep only the latest, idle ticks
/// vanish when anything else is pending, non-addressed chat is capped background context, and chat bursts
/// are debounced into one batch.
/// </summary>
public sealed class EventQueue
{
    private readonly EventQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly List<GameEvent> _pending = new();
    private readonly Dictionary<string, DateTimeOffset> _recentlyTaken = new(StringComparer.OrdinalIgnoreCase); // keys carry player names
    private readonly object _lock = new();
    private TaskCompletionSource _signal = NewSignal();
    private DateTimeOffset _lastPost;

    public EventQueue(EventQueueOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new EventQueueOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised (outside the lock) for every accepted event — the agent uses it to interrupt activities on danger.</summary>
    public event Action<GameEvent>? Posted;

    public int Count { get { lock (_lock) return _pending.Count; } }

    public bool HasWaking { get { lock (_lock) return _pending.Any(e => e.Wakes); } }

    public IReadOnlyList<GameEvent> Snapshot() { lock (_lock) return _pending.ToArray(); }

    /// <summary>
    /// Optional intake filter applied to every posted event before anything else: return the event (possibly changed) to
    /// keep it, or null to drop it. A hive clone uses it so world noticing, joins and other players' deaths never reach
    /// (or wake) its brain.
    /// </summary>
    public Func<GameEvent, GameEvent?>? Intake { get; set; }

    /// <summary>
    /// Optional second filter, applied after <see cref="Intake"/>: the script engine's "brain sleeps" gate. While a script runs
    /// it keeps only real wakes (task end, danger, addressed chat, script wake) waking; the rest is kept as non-waking
    /// context for the next real wake (or dropped, e.g. idle check-ins). Separate from Intake so a clone's policy and the
    /// gate compose.
    /// </summary>
    public Func<GameEvent, GameEvent?>? Gate { get; set; }

    /// <summary>Adds an event, applying coalescing. Returns false if it was dropped as a low-value repeat (or by <see cref="Intake"/>/<see cref="Gate"/>).</summary>
    public bool Post(GameEvent e)
    {
        if (Intake is { } intake)
        {
            if (intake(e) is not { } kept) return false;
            e = kept;
        }
        if (Gate is { } gate)
        {
            if (gate(e) is not { } passed) return false;
            e = passed;
        }
        bool accepted;
        lock (_lock)
        {
            accepted = Coalesce(e);
            if (accepted)
            {
                _lastPost = _time.GetUtcNow();
                var s = _signal;
                _signal = NewSignal();
                s.TrySetResult();
            }
        }
        if (accepted) Posted?.Invoke(e);
        return accepted;
    }

    private bool Coalesce(GameEvent e)
    {
        var now = _time.GetUtcNow();
        e = e with { At = now };
        var window = TimeSpan.FromSeconds(_options.DedupeWindowSeconds);
        string key = Key(e);
        bool chatLike = e.Kind is EventKind.Chat or EventKind.Whisper;

        // Identical event already pending: bump its count.
        int dup = _pending.FindIndex(p => Key(p) == key);
        if (dup >= 0)
        {
            var p = _pending[dup];
            _pending[dup] = p with { Count = p.Count + 1, At = now, Wakes = p.Wakes || e.Wakes, Priority = Max(p.Priority, e.Priority) };
            return true;
        }

        // Low-value repeat of something the model has already seen recently: drop.
        if (!chatLike && e.Priority < EventPriority.Critical && e.Kind != EventKind.TaskCompleted && e.Kind != EventKind.TaskFailed &&
            _recentlyTaken.TryGetValue(RepeatKey(e), out var seen) && now - seen < window)
            return false;

        switch (e.Kind)
        {
            case EventKind.TaskProgress:
                _pending.RemoveAll(p => p.Kind == EventKind.TaskProgress);
                break;
            case EventKind.LowHealth:
                // Keep only the newest health warning; inherit wake status.
                _pending.RemoveAll(p => p.Kind == EventKind.LowHealth);
                break;
            case EventKind.VoidFall:
                _pending.RemoveAll(p => p.Kind == EventKind.VoidFall);
                break;
            case EventKind.IdleTick:
                if (_pending.Any(p => p.Wakes)) return false;
                _pending.RemoveAll(p => p.Kind == EventKind.IdleTick);
                break;
        }

        if (e.Wakes && e.Kind != EventKind.IdleTick) _pending.RemoveAll(p => p.Kind == EventKind.IdleTick);

        _pending.Add(e);

        if (e.Kind == EventKind.Chat && !e.Wakes)
        {
            var bg = _pending.Where(p => p.Kind == EventKind.Chat && !p.Wakes).ToList();
            foreach (var old in bg.Take(Math.Max(0, bg.Count - _options.MaxBackgroundChat))) _pending.Remove(old);
        }

        while (_pending.Count > _options.MaxPending)
        {
            var victim = _pending.OrderBy(p => p.Priority).ThenBy(p => p.At).First();
            _pending.Remove(victim);
        }
        return true;
    }

    /// <summary>Takes everything pending if at least one event wakes the model; otherwise null.</summary>
    public EventBatch? TryTakeBatch()
    {
        lock (_lock)
        {
            if (!_pending.Any(e => e.Wakes)) return null;
            return TakeAllLocked();
        }
    }

    /// <summary>Takes only events at or above <paramref name="min"/> that wake the model (used mid-episode).</summary>
    public EventBatch? TryTakeUrgent(EventPriority min)
    {
        lock (_lock)
        {
            if (!_pending.Any(e => e.Wakes && e.Priority >= min)) return null;
            return TakeAllLocked();
        }
    }

    private EventBatch TakeAllLocked()
    {
        var now = _time.GetUtcNow();
        var batch = _pending.OrderBy(e => e.At).ToList();
        _pending.Clear();
        foreach (var e in batch) _recentlyTaken[RepeatKey(e)] = now;
        if (_recentlyTaken.Count > 200)
        {
            var cutoff = now - TimeSpan.FromSeconds(_options.DedupeWindowSeconds);
            foreach (var k in _recentlyTaken.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) _recentlyTaken.Remove(k);
        }
        return new EventBatch(batch);
    }

    /// <summary>
    /// Waits until at least one waking event is pending, then debounces (unless it's High/Critical) so a burst
    /// of chat becomes one batch, and returns everything pending.
    /// </summary>
    public async Task<EventBatch> WaitBatchAsync(CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            DateTimeOffset firstWake = default;
            lock (_lock)
            {
                signal = _signal.Task;
                if (_pending.Any(e => e.Wakes)) firstWake = _time.GetUtcNow();
            }
            if (firstWake == default)
            {
                await signal.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            // Debounce.
            while (true)
            {
                DateTimeOffset until;
                lock (_lock)
                {
                    if (_pending.Any(e => e.Wakes && e.Priority >= EventPriority.High)) break;
                    signal = _signal.Task;
                    var a = _lastPost + TimeSpan.FromMilliseconds(_options.CoalesceWindowMs);
                    var b = firstWake + TimeSpan.FromMilliseconds(_options.MaxBatchDelayMs);
                    until = a < b ? a : b;
                }
                var remaining = until - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;
                await Task.WhenAny(signal, Task.Delay(remaining, _time, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }

            var batch = TryTakeBatch();
            if (batch != null) return batch;
        }
    }

    private static string Key(GameEvent e) => $"{(int)e.Kind}|{e.Player?.ToLowerInvariant()}|{e.Text}|{(e.Wakes ? 1 : 0)}";

    // Health warnings repeat with changing numbers; one per window is enough (Critical events always pass).
    private static string RepeatKey(GameEvent e) => e.Kind == EventKind.LowHealth ? "lowhealth" : Key(e);

    private static EventPriority Max(EventPriority a, EventPriority b) => a > b ? a : b;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
