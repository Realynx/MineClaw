using System.Text.RegularExpressions;

namespace MineClaw.Agent.Events;

/// <summary>
/// "Agent:ChatDebounce": how the brain copes with players typing several lines in a row, or adding a line while it is already
/// thinking or acting. Typing debounce (per player) before a turn, at most one cancel-and-restart while the first response is
/// still generating, injection at round boundaries after that, and an "amend, don't restart" hint while a task runs.
/// </summary>
public sealed class ChatDebounceOptions
{
    /// <summary>Off = the legacy global coalesce window (<see cref="EventQueueOptions.CoalesceWindowMs"/>) for everything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wake the brain this long after a player's LAST addressed line (they may still be typing).</summary>
    public int QuietMs { get; set; } = 1800;

    /// <summary>
    /// Shorter quiet window when the player's last line looks complete (<see cref="LooksComplete"/>: ends with sentence
    /// punctuation / an emoticon, or starts with a command verb). 0 = always <see cref="QuietMs"/>.
    /// </summary>
    public int CompleteQuietMs { get; set; } = 1200;

    /// <summary>First words that make a line an imperative command ("grab some cobble", "come here").</summary>
    public string[] CommandVerbs { get; set; } =
    [
        "stop", "come", "follow", "go", "get", "grab", "bring", "give", "drop", "toss", "mine", "chop", "cut", "craft", "make", "build",
        "dig", "place", "put", "take", "deposit", "store", "withdraw", "harvest", "collect", "gather", "fetch", "jump", "spin", "wave",
        "crouch", "look", "eat", "sleep", "open", "close", "kill", "attack", "smelt", "cook", "plant", "farm", "work", "help",
    ];

    private HashSet<string>? _verbs;

    /// <summary>True when a line reads as finished: ends with . ! ? ) or an emoticon (:3 &lt;3 xd uwu), or starts with a command verb.</summary>
    public bool LooksComplete(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.EndsWith(',') || t.EndsWith("...") || t.EndsWith(" and", StringComparison.OrdinalIgnoreCase) || t.EndsWith(" then", StringComparison.OrdinalIgnoreCase))
            return false;
        if (".!?)".Contains(t[^1]) || Regex.IsMatch(t, @"(?:[:;]\)|:3|<3|:d|xd|uwu|owo|\^\^)$", RegexOptions.IgnoreCase)) return true;
        _verbs ??= new HashSet<string>(CommandVerbs, StringComparer.OrdinalIgnoreCase);
        var first = Regex.Match(t, @"[A-Za-z']+").Value;
        return first.Length > 0 && _verbs.Contains(first);
    }

    /// <summary>Hard cap on that wait, measured from the first pending waking event.</summary>
    public int MaxMs { get; set; } = 6000;

    /// <summary>
    /// A line whose first word is one of these (or a ≤3-word line containing one: "clawd stop", "no no") fires at once.
    /// Danger events (High/Critical) always fire at once.
    /// </summary>
    public string[] ImmediateWords { get; set; } = ["stop", "wait", "no", "halt", "cancel", "hold"];

    /// <summary>A line ending in '?' fires at once when nothing else is pending from that player.</summary>
    public bool ImmediateOnQuestion { get; set; } = true;

    /// <summary>
    /// Identical lines from one player within this window collapse ("hello? (x3)"); while a turn is running, a repeat of a
    /// line that turn already has is dropped (it adds nothing and must not restart or wake anything).
    /// </summary>
    public int RepeatWindowSeconds { get; set; } = 10;

    /// <summary>
    /// New addressed chat while the brain's FIRST call of a turn is still generating (no tool has run, nothing said):
    /// cancel that call and restart the turn with the merged messages. The prefix is cached, so a restart costs about one
    /// cache read plus the partial output.
    /// </summary>
    public bool RestartOnNewChat { get; set; } = true;

    /// <summary>Max restarts per turn, so a stream of messages can't loop forever. Later lines are injected instead.</summary>
    public int MaxRestartsPerTurn { get; set; } = 1;

    /// <summary>Chat arriving after a tool ran is appended as "[new chat] ..." to the next tool_result message.</summary>
    public bool InjectMidTurn { get; set; } = true;

    /// <summary>Chat that wakes the brain while a delegate/script runs gets a "[busy ...]" line: amend, don't restart.</summary>
    public bool BusyHint { get; set; } = true;

    /// <summary>
    /// ask_player / ask_stand / watch_demo / script asks: after the first line of a reply, wait this long for more lines from
    /// the same player and hand the waiter all of them ("the left / one near the lava"). 0 = first line only.
    /// </summary>
    public int ReplyQuietMs { get; set; } = 1200;

    private Regex? _immediate;

    /// <summary>True when the line should skip the typing window ("stop", "wait", "no", ...).</summary>
    public bool IsImmediate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || ImmediateWords.Length == 0) return false;
        _immediate ??= new Regex(@"^(?:" + string.Join("|", ImmediateWords.Select(Regex.Escape)) + @")$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var words = Regex.Matches(text, @"[A-Za-z0-9_']+").Select(m => m.Value.Trim('\'')).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return false;
        if (_immediate.IsMatch(words[0])) return true;
        return words.Count <= 3 && words.Any(w => _immediate.IsMatch(w));
    }
}

/// <summary>
/// Counters for tuning <see cref="ChatDebounceOptions"/> (thread-safe; one instance per <see cref="EventQueue"/>). Tokens are
/// what the API reported for cancelled calls when it could (streaming), else estimates.
/// </summary>
public sealed class ChatFlowStats
{
    private long _turns, _mergedTurns, _linesMerged, _immediate, _repeats, _restarts, _restartSkipped, _wastedIn, _wastedOut,
        _wastedCacheRead, _savedEst, _injections, _injectedEvents, _busyWakes, _replyMerges, _debounceMs, _wastedMicroUsd;

    /// <summary>Turns that waited for the typing window (and how long in total, ms).</summary>
    public long DebouncedTurns => Interlocked.Read(ref _turns);
    public long DebounceWaitMs => Interlocked.Read(ref _debounceMs);
    /// <summary>Turns where one player's several lines were merged into one event line.</summary>
    public long MergedTurns => Interlocked.Read(ref _mergedTurns);
    /// <summary>Extra lines folded into those turns (lines − speakers).</summary>
    public long LinesMerged => Interlocked.Read(ref _linesMerged);
    public long ImmediateFires => Interlocked.Read(ref _immediate);
    public long RepeatsCollapsed => Interlocked.Read(ref _repeats);
    public long Restarts => Interlocked.Read(ref _restarts);
    /// <summary>New chat during a first call that could not restart (restart budget used up): injected/next turn instead.</summary>
    public long RestartsSkipped => Interlocked.Read(ref _restartSkipped);
    public long WastedInputTokens => Interlocked.Read(ref _wastedIn);
    public long WastedCacheReadTokens => Interlocked.Read(ref _wastedCacheRead);
    public long WastedOutputTokens => Interlocked.Read(ref _wastedOut);
    public decimal WastedUsd => Interlocked.Read(ref _wastedMicroUsd) / 1_000_000m;
    /// <summary>Estimated tokens a restart saved: the stale answer's output plus the follow-up call it would have needed.</summary>
    public long SavedTokensEstimate => Interlocked.Read(ref _savedEst);
    public long MidTurnInjections => Interlocked.Read(ref _injections);
    public long InjectedEvents => Interlocked.Read(ref _injectedEvents);
    public long BusyWakes => Interlocked.Read(ref _busyWakes);
    /// <summary>Waiter replies (ask_player & co.) assembled from more than one line.</summary>
    public long ReplyMerges => Interlocked.Read(ref _replyMerges);

    internal void Debounced(TimeSpan waited) { Interlocked.Increment(ref _turns); Interlocked.Add(ref _debounceMs, (long)Math.Max(0, waited.TotalMilliseconds)); }
    internal void Merged(int extraLines) { if (extraLines <= 0) return; Interlocked.Increment(ref _mergedTurns); Interlocked.Add(ref _linesMerged, extraLines); }
    internal void Immediate() => Interlocked.Increment(ref _immediate);
    internal void Repeat() => Interlocked.Increment(ref _repeats);
    internal void Injected(int events) { Interlocked.Increment(ref _injections); Interlocked.Add(ref _injectedEvents, events); }
    internal void BusyWake() => Interlocked.Increment(ref _busyWakes);
    internal void ReplyMerged() => Interlocked.Increment(ref _replyMerges);
    internal void RestartSkipped() => Interlocked.Increment(ref _restartSkipped);

    internal void Restarted(long input, long cacheRead, long output, decimal usd, long savedEstimate)
    {
        Interlocked.Increment(ref _restarts);
        Interlocked.Add(ref _wastedIn, input);
        Interlocked.Add(ref _wastedCacheRead, cacheRead);
        Interlocked.Add(ref _wastedOut, output);
        Interlocked.Add(ref _wastedMicroUsd, (long)(usd * 1_000_000m));
        Interlocked.Add(ref _savedEst, Math.Max(0, savedEstimate));
    }

    /// <summary>One line for logs / the console.</summary>
    public string Summary() =>
        $"chat flow: debounced {DebouncedTurns} turns (avg wait {(DebouncedTurns == 0 ? 0 : DebounceWaitMs / DebouncedTurns)}ms), " +
        $"merged {LinesMerged} lines in {MergedTurns} turns, immediate {ImmediateFires}, repeats {RepeatsCollapsed}, " +
        $"restarts {Restarts} (skipped {RestartsSkipped}; wasted in {WastedInputTokens} cr {WastedCacheReadTokens} out {WastedOutputTokens} " +
        $"${WastedUsd:0.0000}; saved ~{SavedTokensEstimate} tok), injections {MidTurnInjections} ({InjectedEvents} events), " +
        $"busy wakes {BusyWakes}, reply merges {ReplyMerges}";
}
