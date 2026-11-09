using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// Leaves workers available for control/viewer timers and socket continuations while legacy synchronous handlers wait
/// on colony/SQLite locks and bounded path searches run. Production correlated a 1.2s publication gap with only 115ms
/// of GC and 305 queued callbacks at 30 workers. Raising the minimum avoids slow starvation recovery; it neither creates
/// all workers eagerly nor raises the runtime's maximum. This is process capacity, independent of the pawn roster.
/// </summary>
internal static class RuntimeWorkerPool
{
    internal static (int Workers, int Io) Minimums(int processors, int workers, int io, int maximumWorkers) =>
        (Math.Max(workers, Math.Min(maximumWorkers, Math.Min(Math.Max(processors, 1), 16) * 4)), io);

    internal static void Configure(ILogger log)
    {
        ThreadPool.GetMinThreads(out int before, out int io);
        ThreadPool.GetMaxThreads(out int maximum, out _);
        var wanted = Minimums(Environment.ProcessorCount, before, io, maximum);
        if (wanted.Workers != before && !ThreadPool.SetMinThreads(wanted.Workers, wanted.Io))
            log.LogWarning("runtime worker minimum could not be raised from {Before} to {Wanted}", before, wanted.Workers);
        ThreadPool.GetMinThreads(out int actual, out int actualIo);
        log.LogInformation("runtime workers: {Threads} current, minimum {Minimum} (was {Before}), maximum {Maximum}, I/O minimum {Io}, {Cores} cores",
            ThreadPool.ThreadCount, actual, before, maximum, actualIo, Environment.ProcessorCount);
    }
}
