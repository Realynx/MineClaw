using System.Diagnostics;

namespace MineClaw.Hive.Story;

/// <summary>
/// The director's own time, process-wide (every colony): picks (<see cref="StoryColony.Next"/>, the lock wait included) and watchdog
/// ticks. The supervisor's once-a-minute perf line reads and resets it, so the director's share of the CPU shows without a tracer.
/// </summary>
public static class StoryPerf
{
    internal static long Picks, PickTicks, Ticks, TickTicks;

    internal static void Note(ref long count, ref long ticks, long startedTimestamp)
    {
        Interlocked.Increment(ref count);
        Interlocked.Add(ref ticks, Stopwatch.GetElapsedTime(startedTimestamp).Ticks);
    }

    public readonly record struct Stats(long Picks, TimeSpan PickTime, long Ticks, TimeSpan TickTime);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> IdleReasons = new(StringComparer.Ordinal);

    /// <summary>A pawn idle in its work hours while there was work of its kinds (the idle audit, once a second per pawn).</summary>
    internal static void NoteIdle(string why) => IdleReasons.AddOrUpdate(why, 1, (_, n) => n + 1);

    /// <summary>"idle-with-work N: all targets leased 40, back-off rest 12" since the last call (pawn-seconds), or null; starts over.</summary>
    public static string? TakeIdle()
    {
        var snap = IdleReasons.ToArray();
        foreach (var (k, _) in snap) IdleReasons.TryRemove(k, out _);
        long n = snap.Sum(kv => kv.Value);
        if (n == 0) return null;
        // The six biggest reasons (a "waiting: build b3: need 24 block of iron" each is specific); the rest summed.
        var top = snap.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var text = string.Join(", ", top.Take(6).Select(kv => $"{kv.Key} {kv.Value}"));
        if (top.Count > 6) text += $", {top.Count - 6} more reasons {top.Skip(6).Sum(kv => kv.Value)}";
        return $"idle-with-work {n}: " + text;
    }

    /// <summary>The counters since the last call, and starts them over.</summary>
    public static Stats Take() => new(Interlocked.Exchange(ref Picks, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref PickTicks, 0)),
        Interlocked.Exchange(ref Ticks, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref TickTicks, 0)));
}
