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

    /// <summary>The counters since the last call, and starts them over.</summary>
    public static Stats Take() => new(Interlocked.Exchange(ref Picks, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref PickTicks, 0)),
        Interlocked.Exchange(ref Ticks, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref TickTicks, 0)));
}
