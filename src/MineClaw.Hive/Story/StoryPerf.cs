using System.Diagnostics;

namespace MineClaw.Hive.Story;

/// <summary>
/// The director's own time, process-wide (every colony): picks (<see cref="StoryColony.Next"/>, the lock wait included) and watchdog
/// ticks. The supervisor's once-a-minute perf line reads and resets it, so the director's share of the CPU shows without a tracer.
/// Every colony command and state read waits on the colony's one lock, so it also keeps how long the HTTP routes waited for that
/// lock and what held it (<see cref="TakeLocks"/>).
/// </summary>
public static class StoryPerf
{
    internal static long Picks, PickTicks, Ticks, TickTicks;
    /// <summary>The longest single pick and tick: one long hold of the colony lock shows even among many short ones.</summary>
    internal static long PickMax, TickMax;

    internal static void Note(ref long count, ref long ticks, ref long max, long startedTimestamp)
    {
        long elapsed = Stopwatch.GetElapsedTime(startedTimestamp).Ticks;
        Interlocked.Increment(ref count);
        Interlocked.Add(ref ticks, elapsed);
        Raise(ref max, elapsed);
    }

    private static void Raise(ref long max, long value)
    {
        long seen;
        while (value > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, value, seen) != seen) { }
    }

    public readonly record struct Stats(long Picks, TimeSpan PickTime, long Ticks, TimeSpan TickTime);

    // ---------------------------------------------------------------- the colony lock: who waits for it, what holds it

    /// <summary>One kind of work since the last perf line: how often, its time in all, the longest once.</summary>
    public readonly record struct Sample(long Count, TimeSpan Total, TimeSpan Longest);

    /// <summary>Count, total and longest of one kind of work. Pawn threads, the tick and the HTTP workers all write it.</summary>
    internal sealed class Meter
    {
        private long _count, _ticks, _max;

        public void Note(long startedTimestamp) => StoryPerf.Note(ref _count, ref _ticks, ref _max, startedTimestamp);

        public Sample Take() => new(Interlocked.Exchange(ref _count, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref _ticks, 0)),
            TimeSpan.FromTicks(Interlocked.Exchange(ref _max, 0)));
    }

    /// <summary>How long the HTTP story routes waited for the colony lock (<see cref="StoryColony.TryCommand"/>, <see cref="StoryColony.TryRead"/>).</summary>
    internal static readonly Meter LockWait = new();
    /// <summary>State builds, under the lock (<see cref="StoryColony.State"/>).</summary>
    internal static readonly Meter StateBuilds = new();
    /// <summary>The colony written to the store, under the lock (<see cref="StoryColony.Save"/>).</summary>
    internal static readonly Meter Saves = new();
    /// <summary>Chest-memory reads, under the lock (the storage record kept honest: StoryColony.StockCheck.cs).</summary>
    internal static readonly Meter SeenChest = new();
    /// <summary>Waits that ended without the lock: a command answered busy (not applied), a read answered busy.</summary>
    internal static long BusyCommands, BusyReads;

    /// <summary>
    /// The colony lock since the last perf line: the longest single pick and tick, the HTTP routes' wait for it and how many gave up,
    /// and three of its holders (state builds, saves, chest-memory reads).
    /// </summary>
    public readonly record struct LockStats(TimeSpan LongestPick, TimeSpan LongestTick, Sample LockWait, long BusyCommands, long BusyReads,
        Sample StateBuilds, Sample Saves, Sample SeenChest);

    /// <summary>The lock figures since the last call, and starts them over.</summary>
    public static LockStats TakeLocks() => new(TimeSpan.FromTicks(Interlocked.Exchange(ref PickMax, 0)), TimeSpan.FromTicks(Interlocked.Exchange(ref TickMax, 0)),
        LockWait.Take(), Interlocked.Exchange(ref BusyCommands, 0), Interlocked.Exchange(ref BusyReads, 0), StateBuilds.Take(), Saves.Take(), SeenChest.Take());

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
