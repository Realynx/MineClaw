namespace MineClaw.Agent.Context;

/// <summary>
/// Which brain "cache variant" is warm. The Anthropic message cache is keyed by the request's effort/thinking settings as well
/// as its bytes: the same history sent at <c>low</c> and at <c>medium</c> effort are two separate cache entries (tools and
/// system stay shared). A variant's entry is found again only within its TTL (5m) AND within ~20 content blocks of the new
/// request's breakpoint (the API's lookback). Live 10-27 15:55 (usage JSONL): a simple chat turn (low) came 3 min after the
/// last low call, but an 8-round medium turn had added ~40 blocks since, so it read only tools + system (8,978) and re-wrote
/// all 38K history tokens; the restart for a new line did it again (77K cache-write tokens, $0.42 for one episode; 15:45 the
/// same at 22K). 15:48 the same switch read 31K from cache because only ~10 blocks had been added. So a switch to a cold
/// variant is only taken while history is small, or when it raises the effort (a planning turn keeps its quality); a
/// cheaper-effort turn otherwise stays on the warm variant.
/// </summary>
public sealed class CacheWarmth
{
    private readonly Dictionary<string, (DateTimeOffset At, int Blocks)> _lastUse = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private string? _last;

    /// <summary>A variant used this recently still has its history cached (5m TTL minus a safety margin).</summary>
    public TimeSpan WarmFor { get; set; } = TimeSpan.FromSeconds(270);

    /// <summary>Content blocks the API looks back from a breakpoint for an earlier cache entry (20, minus a margin).</summary>
    public int LookbackBlocks { get; set; } = 18;

    /// <summary>History (calibrated tokens) up to which a cold switch is cheap enough to just take.</summary>
    public int ColdSwitchMaxTokens { get; set; } = 6_000;

    /// <summary>The variant of the most recent call (null = none yet).</summary>
    public string? Last { get { lock (_lock) return _last; } }

    /// <summary>When the last call went out (any variant); null = never.</summary>
    public DateTimeOffset? LastCallAt { get { lock (_lock) return _last != null && _lastUse.TryGetValue(_last, out var t) ? t.At : null; } }

    public static string Key(string? model, string? effort, string? thinking) => $"{model ?? "-"}|{effort ?? "-"}|{thinking ?? "-"}";

    /// <summary>A call went out with this variant; <paramref name="blocks"/> = content blocks in its messages (its breakpoint).</summary>
    public void Note(string key, DateTimeOffset now, int blocks)
    {
        lock (_lock)
        {
            _lastUse[key] = (now, blocks);
            _last = key;
        }
    }

    /// <summary>Null when <paramref name="key"/>'s history cache would be found by a request of <paramref name="blocks"/> blocks; else why not.</summary>
    public string? ColdReason(string key, DateTimeOffset now, int blocks)
    {
        lock (_lock)
        {
            if (!_lastUse.TryGetValue(key, out var u)) return "never used";
            var age = now - u.At;
            if (age > WarmFor) return $"last used {Ago(age)} ago (5m TTL)";
            int back = blocks - u.Blocks;
            if (back > LookbackBlocks) return $"last used {Ago(age)} ago but {back} blocks back (the API looks back 20)";
            if (back < 0) return "history shrank since (compaction)";
            return null;
        }
    }

    public bool IsWarm(string key, DateTimeOffset now, int blocks) => ColdReason(key, now, blocks) == null;

    /// <summary>
    /// True = keep the last (warm) variant instead of switching to <paramref name="desired"/>: the desired variant's cache is
    /// cold, history is big, and the switch only lowers effort (<paramref name="downgrade"/>).
    /// </summary>
    public bool ShouldStay(string desired, bool downgrade, int historyTokens, DateTimeOffset now, int blocks)
    {
        string? last;
        lock (_lock) last = _last;
        if (last == null || last == desired || !downgrade) return false;
        if (IsWarm(desired, now, blocks) || historyTokens <= ColdSwitchMaxTokens) return false;
        return IsWarm(last, now, blocks); // only worth staying on a variant that is itself still warm
    }

    private static string Ago(TimeSpan t) => t.TotalSeconds < 90 ? $"{(int)t.TotalSeconds}s" : $"{(int)t.TotalMinutes}m";
}
