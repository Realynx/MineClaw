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
        or EventKind.PlayerLeft or EventKind.IdleTick or EventKind.Notice or EventKind.Activity or EventKind.Emote or EventKind.Gesture);

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

    /// <summary>True when a waking chat/whisper/emote line from a player is in the batch (a person typed to us).</summary>
    public bool HasTypedChat => Waking.Any(EventQueue.IsTyped);

    /// <summary>
    /// Compact multi-line rendering. Each speaker's chat lines (same kind and priority) are merged into one line at their first
    /// line's place, in order ("[chat] fox: mine the gen / actually the left cell / and bring me 10"); repeats show "(xN)".
    /// <paramref name="fresh"/> = mid-turn injection: addressed chat is tagged "[new chat]".
    /// </summary>
    public string Format(bool fresh = false)
    {
        static bool ChatLine(GameEvent e) => e.Kind is EventKind.Chat or EventKind.Whisper && e.Player != null;
        static string GroupKey(GameEvent e) => $"{(int)e.Kind}|{(int)e.Priority}|{e.Player!.ToLowerInvariant()}";

        var groups = new Dictionary<string, List<string>>();
        foreach (var e in Events)
        {
            if (!ChatLine(e)) continue;
            var k = GroupKey(e);
            if (!groups.TryGetValue(k, out var parts)) groups[k] = parts = new List<string>();
            parts.Add(e.Count > 1 ? $"{e.Text} (x{e.Count})" : e.Text);
        }

        // One brain, many bodies: task results from two or more bodies read as one line at the first one's place
        // ("[bodies] 67 ok: 64 cobble; 69 failed: no free cell"), so N bodies finishing is one thing to read, not N.
        var bodyTasks = Events.Where(BodyTask).ToList();
        bool coalesce = bodyTasks.Select(e => GameEvent.BodyTag(e.Body)).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;

        var sb = new StringBuilder();
        var done = new HashSet<string>();
        foreach (var e in Events)
        {
            string line;
            if (coalesce && BodyTask(e))
            {
                if (!done.Add("\u0001bodies")) continue;
                line = BodiesLine(bodyTasks);
            }
            else if (ChatLine(e))
            {
                var k = GroupKey(e);
                if (!done.Add(k)) continue;
                line = (e with { Text = string.Join(" / ", groups[k]), Count = 1 }).Format();
            }
            else line = e.Format();
            if (fresh && e.Wakes && e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote && line.StartsWith('['))
                line = "[new " + line[1..];
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A task result of a non-main body (the [bodies] coalescing unit).</summary>
    public static bool BodyTask(GameEvent e) => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed && GameEvent.BodyTag(e.Body) != null;

    /// <summary>"[bodies] 67 ok: 64 cobble; 69 failed: no free cell" (each result ≤ ~90 chars, in arrival order).</summary>
    public static string BodiesLine(IEnumerable<GameEvent> tasks) =>
        "[bodies] " + string.Join("; ", tasks.Select(e =>
        {
            // "#4 harvest_generator: dug 64 ..." → "ok: dug 64 ..."; a text that starts with its own verdict keeps it.
            var text = System.Text.RegularExpressions.Regex.Replace(e.Text.Trim(), @"^#\d+\s+[\w ]+?:\s*", "");
            bool hasVerdict = text.StartsWith("ok", StringComparison.OrdinalIgnoreCase) || text.StartsWith("fail", StringComparison.OrdinalIgnoreCase);
            var verdict = hasVerdict ? "" : e.Kind == EventKind.TaskCompleted ? "ok: " : "failed: ";
            if (text.Length > 90) text = text[..87] + "...";
            return $"{GameEvent.BodyTag(e.Body)} {verdict}{text}" + (e.Count > 1 ? $" (x{e.Count})" : "");
        }));
}


/// <summary>
/// Thread-safe queue that coalesces game events so the model is only woken for meaningful batches:
/// identical repeats merge (xN) or are dropped, progress/health updates keep only the latest, idle ticks
/// vanish when anything else is pending, non-addressed chat is capped background context, and chat bursts
/// are debounced into one batch: per player (wait <see cref="ChatDebounceOptions.QuietMs"/> after their last line, capped at
/// <see cref="ChatDebounceOptions.MaxMs"/>), with "stop"/"wait"/"no", a lone question and danger firing at once.
/// </summary>
public sealed class EventQueue
{
    private readonly EventQueueOptions _options;
    private readonly ChatDebounceOptions? _debounce;
    private readonly TimeProvider _time;
    private readonly List<GameEvent> _pending = new();
    private readonly Dictionary<string, DateTimeOffset> _recentlyTaken = new(StringComparer.OrdinalIgnoreCase); // keys carry player names
    // Typing debounce state for the pending batch (cleared on take).
    private readonly Dictionary<string, DateTimeOffset> _firstLine = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastLine = new(StringComparer.OrdinalIgnoreCase);
    // Whether each player's last pending line looks complete (shorter quiet window).
    private readonly Dictionary<string, bool> _lastComplete = new(StringComparer.OrdinalIgnoreCase);
    // Chat lines already handed to the brain (player|text -> when), for in-turn repeat drops.
    private readonly Dictionary<string, DateTimeOffset> _takenChat = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private TaskCompletionSource _signal = NewSignal();
    private DateTimeOffset _lastPost;
    private DateTimeOffset? _firstWake;
    private bool _fireNow;
    private bool _turnActive;

    /// <param name="debounce">Per-player typing debounce; null = the legacy global window for everything.</param>
    public EventQueue(EventQueueOptions? options = null, TimeProvider? time = null, ChatDebounceOptions? debounce = null)
    {
        _options = options ?? new EventQueueOptions();
        _time = time ?? TimeProvider.System;
        _debounce = debounce;
    }

    /// <summary>The typing-debounce settings in force (null = legacy coalescing).</summary>
    public ChatDebounceOptions? Debounce => _debounce;

    /// <summary>Counters for tuning the chat windows (debounce, restarts, injections...).</summary>
    public ChatFlowStats Stats { get; } = new();

    /// <summary>Raised (outside the lock) for every accepted event — the agent uses it to interrupt activities on danger.</summary>
    public event Action<GameEvent>? Posted;

    public int Count { get { lock (_lock) return _pending.Count; } }

    public bool HasWaking { get { lock (_lock) return _pending.Any(e => e.Wakes); } }

    /// <summary>True when addressed chat from a player is pending.</summary>
    public bool HasTypedChat { get { lock (_lock) return _pending.Any(IsTyped); } }

    public IReadOnlyList<GameEvent> Snapshot() { lock (_lock) return _pending.ToArray(); }

    /// <summary>Drops pending events that match (a background line re-posted as addressed); returns how many went.</summary>
    public int RemovePending(Func<GameEvent, bool> match) { lock (_lock) return _pending.RemoveAll(e => match(e)); }

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

    /// <summary>A waking chat/whisper/emote from a player: someone typed to us (the debounce's unit).</summary>
    public static bool IsTyped(GameEvent e) =>
        e.Wakes && e.Player != null && e.Priority >= EventPriority.Low && e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote;

    /// <summary>
    /// The brain is handling a turn (BotAgent): until <see cref="EndTurn"/>, a repeat of a chat line it already took within
    /// <see cref="ChatDebounceOptions.RepeatWindowSeconds"/> is dropped instead of waking/restarting anything.
    /// </summary>
    public void BeginTurn() { lock (_lock) _turnActive = true; }

    public void EndTurn() { lock (_lock) _turnActive = false; }

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
        // Debounced chat: a typed line while the chat debounce is on.
        var d = IsTyped(e) ? ActiveDebounce : null;
        var repeatWindow = RepeatWindow;

        if (!_pending.Any(p => p.Wakes)) ResetChatWindow();

        // A repeat of a line the running turn already has adds nothing: never wake/restart/inject for it.
        if (d != null && IsTakenRepeat(e, now, repeatWindow))
        {
            Stats.Repeat();
            return false;
        }
        if (BumpDuplicate(e, d != null, now, repeatWindow)) return true;
        if (SeenRecently(e, now) || !KeepNewestOnly(e)) return false;
        Admit(e, d, now);
        return true;
    }

    private ChatDebounceOptions? ActiveDebounce => _debounce is { Enabled: true } dd ? dd : null;

    private TimeSpan RepeatWindow => TimeSpan.FromSeconds(Math.Max(0, ActiveDebounce?.RepeatWindowSeconds ?? 0));

    private bool IsTakenRepeat(GameEvent e, DateTimeOffset now, TimeSpan repeatWindow) =>
        _turnActive && _takenChat.TryGetValue(ChatKey(e), out var taken) && now - taken <= repeatWindow;

    /// <summary>Adds an accepted event: a waking one clears idle check-ins, debounced chat marks its player, then the caps apply.</summary>
    private void Admit(GameEvent e, ChatDebounceOptions? debounce, DateTimeOffset now)
    {
        if (e.Wakes && e.Kind != EventKind.IdleTick) _pending.RemoveAll(p => p.Kind == EventKind.IdleTick);
        if (debounce != null) NoteTypedLine(e, debounce, now);
        if (e.Wakes) _firstWake ??= now;
        _pending.Add(e);
        TrimPending(e);
    }

    /// <summary>Nothing waking is pending: the chat debounce windows start over.</summary>
    private void ResetChatWindow()
    {
        _firstWake = null;
        _firstLine.Clear();
        _lastLine.Clear(); _lastComplete.Clear();
        _fireNow = false;
    }

    /// <summary>Identical event already pending: bump its count (chat only within the repeat window when debouncing).</summary>
    private bool BumpDuplicate(GameEvent e, bool debouncedChat, DateTimeOffset now, TimeSpan repeatWindow)
    {
        string key = Key(e);
        int dup = _pending.FindIndex(p => Key(p) == key && (!debouncedChat || now - p.At <= repeatWindow));
        if (dup < 0) return false;
        var p = _pending[dup];
        _pending[dup] = p with { Count = p.Count + 1, At = now, Wakes = p.Wakes || e.Wakes, Priority = Max(p.Priority, e.Priority) };
        if (debouncedChat)
        {
            Stats.Repeat();
            _lastLine[e.Player!] = now;
        }
        return true;
    }

    /// <summary>Low-value repeat of something the model has already seen recently (not chat, danger or a task result).</summary>
    private bool SeenRecently(GameEvent e, DateTimeOffset now)
    {
        if (e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.TaskCompleted or EventKind.TaskFailed || e.Priority >= EventPriority.Critical) return false;
        return _recentlyTaken.TryGetValue(RepeatKey(e), out var seen) && now - seen < TimeSpan.FromSeconds(_options.DedupeWindowSeconds);
    }

    /// <summary>Kinds where only the newest pending one counts ("keep only the newest" is per body: 67's hp warning never
    /// replaces 69's). False = drop <paramref name="e"/> (an idle check-in while something waking is pending).</summary>
    private bool KeepNewestOnly(GameEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.TaskProgress or EventKind.LowHealth or EventKind.VoidFall:
                // Keep only the newest health warning; inherit wake status.
                _pending.RemoveAll(p => p.Kind == e.Kind && SameBody(p, e));
                break;
            case EventKind.IdleTick:
                if (_pending.Any(p => p.Wakes)) return false;
                _pending.RemoveAll(p => p.Kind == EventKind.IdleTick);
                break;
        }
        return true;
    }

    /// <summary>A typed line from a player: fire-now triggers and the per-player first/last/complete marks of the debounce.</summary>
    private void NoteTypedLine(GameEvent e, ChatDebounceOptions d, DateTimeOffset now)
    {
        bool hadFromPlayer = _pending.Any(p => IsTyped(p) && string.Equals(p.Player, e.Player, StringComparison.OrdinalIgnoreCase));
        if (d.IsImmediate(e.Text) || (d.ImmediateOnQuestion && !hadFromPlayer && e.Text.TrimEnd().EndsWith('?')))
        {
            if (!_fireNow) Stats.Immediate();
            _fireNow = true;
        }
        _firstLine.TryAdd(e.Player!, now);
        _lastLine[e.Player!] = now;
        _lastComplete[e.Player!] = d.CompleteQuietMs > 0 && d.LooksComplete(e.Text);
    }

    /// <summary>Caps background chat and the whole queue (lowest priority, oldest first).</summary>
    private void TrimPending(GameEvent e)
    {
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

    private EventBatch TakeAllLocked(bool afterWait = false)
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

        var typed = batch.Where(IsTyped).ToList();
        if (_debounce is { Enabled: true } d && typed.Count > 0)
        {
            foreach (var e in typed) _takenChat[ChatKey(e)] = now;
            if (_takenChat.Count > 100)
            {
                var cutoff = now - TimeSpan.FromSeconds(Math.Max(1, d.RepeatWindowSeconds));
                foreach (var k in _takenChat.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) _takenChat.Remove(k);
            }
            int speakers = typed.Select(e => e.Player!.ToLowerInvariant()).Distinct().Count();
            Stats.Merged(typed.Count - speakers);
            if (afterWait && _firstWake is { } fw && !_fireNow) Stats.Debounced(now - fw);
        }
        _firstWake = null;
        _firstLine.Clear();
        _lastLine.Clear(); _lastComplete.Clear();
        _fireNow = false;
        return new EventBatch(batch);
    }

    /// <summary>
    /// When the pending batch should be handed to the brain (null = nothing waking). Danger and immediate lines: now. Typed
    /// chat: when every speaker has been quiet for <see cref="ChatDebounceOptions.QuietMs"/> (each capped at MaxMs from their
    /// first line), and at most MaxMs after the first waking event; other players' pending lines ride in the same turn.
    /// Anything else: the legacy window (<see cref="EventQueueOptions.CoalesceWindowMs"/> after the last post, capped).
    /// </summary>
    private DateTimeOffset? ReadyAtLocked(DateTimeOffset now)
    {
        if (!_pending.Any(e => e.Wakes)) return null;
        if (_pending.Any(e => e.Wakes && e.Priority >= EventPriority.High)) return now;
        var first = _firstWake ?? now;
        if (_debounce is { Enabled: true } d)
        {
            if (_fireNow) return now;
            var players = _pending.Where(IsTyped).Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (players.Count > 0)
            {
                var quiet = TimeSpan.FromMilliseconds(Math.Max(0, d.QuietMs));
                var max = TimeSpan.FromMilliseconds(Math.Max(0, d.MaxMs));
                var ready = DateTimeOffset.MinValue;
                foreach (var p in players)
                {
                    var last = _lastLine.TryGetValue(p, out var l) ? l : first;
                    var pf = _firstLine.TryGetValue(p, out var f) ? f : first;
                    var q = _lastComplete.TryGetValue(p, out var complete) && complete
                        ? TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(d.QuietMs, d.CompleteQuietMs))) : quiet;
                    var r = Min(last + q, pf + max);
                    if (r > ready) ready = r;
                }
                return Min(ready, first + max);
            }
        }
        return Min(_lastPost + TimeSpan.FromMilliseconds(_options.CoalesceWindowMs), first + TimeSpan.FromMilliseconds(_options.MaxBatchDelayMs));
    }

    /// <summary>Waits until a waking batch is pending and its debounce window has closed (see <see cref="ReadyAtLocked"/>).</summary>
    public async Task WaitReadyAsync(CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            DateTimeOffset? at;
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                signal = _signal.Task;
                at = ReadyAtLocked(now);
            }
            if (at == null)
            {
                await signal.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }
            var remaining = at.Value - now;
            if (remaining <= TimeSpan.Zero) return;
            await Task.WhenAny(signal, Task.Delay(remaining, _time, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Waits until at least one waking event is pending, then debounces (danger and "stop"-like lines skip it) so a burst
    /// of chat becomes one batch, and returns everything pending.
    /// </summary>
    public async Task<EventBatch> WaitBatchAsync(CancellationToken ct)
    {
        while (true)
        {
            await WaitReadyAsync(ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (_pending.Any(e => e.Wakes)) return TakeAllLocked(afterWait: true);
            }
        }
    }

    // Every dedupe key carries the body (null = brain-level / main before stamping): the same "[task ok] 64 cobble" or hp warning
    // from two bodies is two events, never a merge or a drop.
    internal static string Key(GameEvent e) => $"{(int)e.Kind}|{e.Player?.ToLowerInvariant()}|{e.Text}|{(e.Wakes ? 1 : 0)}|{BodyKey(e)}";

    internal static string ChatKey(GameEvent e) => $"{e.Player?.ToLowerInvariant()}|{e.Text.Trim().ToLowerInvariant()}|{BodyKey(e)}";

    // Health warnings repeat with changing numbers; one per window per body is enough (Critical events always pass).
    internal static string RepeatKey(GameEvent e) => e.Kind == EventKind.LowHealth ? "lowhealth|" + BodyKey(e) : Key(e);

    private static string BodyKey(GameEvent e) => e.Body?.ToLowerInvariant() ?? "";

    private static bool SameBody(GameEvent a, GameEvent b) => string.Equals(a.Body, b.Body, StringComparison.OrdinalIgnoreCase);

    private static EventPriority Max(EventPriority a, EventPriority b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
