using System.Diagnostics;
using System.Globalization;
using System.Text;
using MineClaw.Client;

namespace MineClaw.App.Supervisor;

/// <summary>
/// What the supervisor's own requests cost, and where the process's CPU goes, as one log line a minute (live 11-04 the explorer felt
/// laggy and there was no way to tell the viewer's handlers from the game's work without installing a tracer on the host): per route
/// (ids folded: <c>/api/story/colonies/*/architect</c>) the count and p50/p95/max handler time; the process CPU over the minute,
/// the thread pool (threads, queued work), the GC (collections, pause time, allocation rate), and the A* gate (searches, busy time,
/// the longest, time bodies waited for a slot). Thread-safe; recording is a lock and a list append.
/// </summary>
public sealed class SupervisorPerf
{
    private readonly object _lock = new();
    private Dictionary<string, List<double>> _routes = new(StringComparer.Ordinal);
    private TimeSpan _cpu = Process.GetCurrentProcess().TotalProcessorTime;
    private long _alloc = GC.GetTotalAllocatedBytes();
    private TimeSpan _pause = GC.GetTotalPauseDuration();
    private readonly int[] _gcs = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private long _at = Stopwatch.GetTimestamp();

    /// <summary>The route key of a request path (at most five segments): a segment past the second with a digit in it (an id, a
    /// body name, a colony) folds to <c>*</c>, words stay (<c>/api/story/colonies/*/architect</c>).</summary>
    public static string RouteOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length && i < 5; i++)
            sb.Append('/').Append(i >= 2 && parts[i].Any(char.IsAsciiDigit) ? "*" : parts[i]);
        return sb.Length == 0 ? "/" : sb.ToString();
    }

    public void Record(string path, double ms)
    {
        var key = RouteOf(path);
        lock (_lock)
        {
            if (!_routes.TryGetValue(key, out var l))
            {
                if (_routes.Count >= 200) return; // a scan of odd paths never grows this without bound
                _routes[key] = l = new List<double>();
            }
            if (l.Count < 100_000) l.Add(ms);
        }
    }

    /// <summary>The line for the time since the last call (and starts over); null when nothing ran and nobody asked.</summary>
    public string Summary()
    {
        Dictionary<string, List<double>> routes;
        lock (_lock)
        {
            routes = _routes;
            _routes = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        }
        long now = Stopwatch.GetTimestamp();
        double secs = Math.Max(0.001, Stopwatch.GetElapsedTime(_at, now).TotalSeconds);
        _at = now;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        double cpuPct = 100 * (cpu - _cpu).TotalSeconds / secs;
        _cpu = cpu;
        long alloc = GC.GetTotalAllocatedBytes();
        double mbs = (alloc - _alloc) / 1048576.0 / secs;
        _alloc = alloc;
        var pause = GC.GetTotalPauseDuration();
        double pauseMs = (pause - _pause).TotalMilliseconds;
        _pause = pause;
        var gcs = new int[3];
        for (int g = 0; g < 3; g++) { int c = GC.CollectionCount(g); gcs[g] = c - _gcs[g]; _gcs[g] = c; }
        var a = PathfindGate.TakeStats();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"perf {secs:0}s: cpu {cpuPct:0}% ({Environment.ProcessorCount} cores), pool {ThreadPool.ThreadCount} threads ");
        sb.Append(CultureInfo.InvariantCulture, $"{ThreadPool.PendingWorkItemCount} queued, gc {gcs[0]}/{gcs[1]}/{gcs[2]} pause {pauseMs:0} ms alloc {mbs:0.0} MB/s; ");
        sb.Append(CultureInfo.InvariantCulture, $"a* {a.Searches} searches busy {a.Busy.TotalSeconds:0.0}s ({100 * a.Busy.TotalSeconds / secs:0}% of a core) ");
        sb.Append(CultureInfo.InvariantCulture, $"longest {a.Longest.TotalMilliseconds:0} ms waited {a.Waited.TotalSeconds:0.0}s; ");
        var st = MineClaw.Hive.Story.StoryPerf.Take();
        sb.Append(CultureInfo.InvariantCulture, $"story {st.Picks} picks {st.PickTime.TotalMilliseconds:0} ms, {st.Ticks} ticks {st.TickTime.TotalMilliseconds:0} ms");
        foreach (var (route, list) in routes.OrderByDescending(r => r.Value.Sum()).Take(12))
        {
            list.Sort();
            double P(double q) => list[Math.Min(list.Count - 1, (int)(q * list.Count))];
            sb.Append(CultureInfo.InvariantCulture, $"; {route} {list.Count}x p50 {P(0.5):0} p95 {P(0.95):0} max {list[^1]:0} ms");
        }
        return sb.ToString();
    }
}
