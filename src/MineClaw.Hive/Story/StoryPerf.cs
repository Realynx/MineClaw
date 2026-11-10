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
    /// <summary>The colony written to the store (<see cref="StoryColony.Save"/>): its changed rows taken under the lock, the db write after it.</summary>
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

    // ---------------------------------------------------------------- inside the pick and the tick

    /// <summary>How long picks and ticks waited for the colony lock before their work began. A pick's time minus its wait is its hold.</summary>
    internal static readonly Meter PickLockWait = new(), TickLockWait = new();

    /// <summary>When the running pick on this thread started (its wait for the lock is noted once it has the lock).</summary>
    [ThreadStatic] internal static long PickStarted;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Meter> TickPhases = new(StringComparer.Ordinal);

    /// <summary>One phase of the tick ended: its time since <paramref name="since"/> is noted under <paramref name="name"/>. Returns now.</summary>
    internal static long Phase(string name, long since)
    {
        TickPhases.GetOrAdd(name, static _ => new Meter()).Note(since);
        return Stopwatch.GetTimestamp();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Stage, string? Kind), Meter> PickStages = new();

    /// <summary>
    /// One stage of a pick ended (it holds the colony lock): noted under <paramref name="stage"/>, or "stage:kind" for a work type's
    /// search. Stages nest (work holds the work:… searches), so they do not add up to the pick. Returns now.
    /// </summary>
    internal static long PickStage(string stage, long since, string? kind = null)
    {
        PickStages.GetOrAdd((stage, kind), static _ => new Meter()).Note(since);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// "pick lock-wait …, tick lock-wait …, pick stages: work 80x 51000 ms max 900 ms, work:mine …, tick phases: reconcile …" (each costliest
    /// first, the ten costliest pick stages), and starts over.
    /// </summary>
    public static string TakeInside()
    {
        static string S(string name, Sample s) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{name} {s.Count}x {s.Total.TotalMilliseconds:0} ms max {s.Longest.TotalMilliseconds:0} ms");
        static IEnumerable<string> Costliest<TKey>(IEnumerable<KeyValuePair<TKey, Meter>> meters, Func<TKey, string> name, int top) =>
            meters.Select(kv => (Name: name(kv.Key), S: kv.Value.Take())).Where(p => p.S.Count > 0)
                .OrderByDescending(p => p.S.Total).ThenBy(p => p.Name, StringComparer.Ordinal).Take(top).Select(p => S(p.Name, p.S));
        var stages = Costliest(PickStages, k => k.Kind == null ? k.Stage : $"{k.Stage}:{k.Kind}", 10);
        var phases = Costliest(TickPhases, k => k, int.MaxValue);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{S("pick lock-wait", PickLockWait.Take())}, {S("tick lock-wait", TickLockWait.Take())}, pick stages: {string.Join(", ", stages)}, tick phases: {string.Join(", ", phases)}");
    }

    /// <summary>The counters since the last call, and starts them over.</summary>
    public static Stats Take() => new(Interlocked.Exchange(ref Picks, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref PickTicks, 0)),
        Interlocked.Exchange(ref Ticks, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref TickTicks, 0)));
}
