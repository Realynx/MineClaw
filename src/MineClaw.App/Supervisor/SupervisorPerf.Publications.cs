using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MineClaw.App.Supervisor;

public sealed partial class SupervisorPerf
{
    private Dictionary<string, PublicationStats> _publications = new(StringComparer.Ordinal);
    private sealed class PublicationStats
    {
        public long Samples, Gaps, PeakQueued;
        public double MaxGapMs, GcAtMaxGapMs, GcInGapsMs;
        // Cadence below the 250 ms gap line, and socket churn: a reconnect starts a new monitor and so leaves no gap behind.
        public long Gaps150, Opens, Failed;
        public double LongestMs;
    }

    /// <summary>One monitor per socket. Only scalar counters are kept; no per-frame samples or identities are retained.</summary>
    internal PublicationMonitor MonitorPublication(string stream)
    {
        lock (_lock)
            if (StatsOf(stream) is { } stats) stats.Opens++;
        return new(this, stream);
    }

    internal sealed class PublicationMonitor(SupervisorPerf owner, string stream)
    {
        private bool _sampled;
        private long _lastAt;
        private double _lastGcMs;

        /// <summary>Called after a state frame is handed to the socket. Includes timer/worker delay, snapshot work and send wait.</summary>
        internal void Sent() => Sample(Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration().TotalMilliseconds, ThreadPool.PendingWorkItemCount);

        internal void Sample(long at, double totalGcMs, long queued)
        {
            double elapsed = _sampled ? Stopwatch.GetElapsedTime(_lastAt, at).TotalMilliseconds : 0;
            double gc = _sampled ? Math.Max(0, totalGcMs - _lastGcMs) : 0;
            _lastAt = at;
            _lastGcMs = totalGcMs;
            _sampled = true;
            owner.RecordPublication(stream, elapsed, gc, queued);
        }

        /// <summary>The socket ended on a snapshot error or a busy lane (not a client leaving).</summary>
        internal void Failed()
        {
            lock (owner._lock)
                if (owner.StatsOf(stream) is { } stats) stats.Failed++;
        }
    }

    // caller holds _lock; null once four streams are tracked
    private PublicationStats? StatsOf(string stream)
    {
        if (_publications.TryGetValue(stream, out var stats)) return stats;
        if (_publications.Count >= 4) return null;
        return _publications[stream] = new PublicationStats();
    }

    private void RecordPublication(string stream, double gapMs, double gcMs, long queued)
    {
        lock (_lock)
        {
            if (StatsOf(stream) is not { } stats) return;
            stats.Samples++;
            stats.PeakQueued = Math.Max(stats.PeakQueued, queued);
            stats.LongestMs = Math.Max(stats.LongestMs, gapMs);
            if (gapMs > 150) stats.Gaps150++;
            if (gapMs <= 250) return;
            stats.Gaps++;
            stats.GcInGapsMs += gcMs;
            if (gapMs > stats.MaxGapMs) { stats.MaxGapMs = gapMs; stats.GcAtMaxGapMs = gcMs; }
        }
    }

    private static void AppendPublications(StringBuilder text, Dictionary<string, PublicationStats> publications)
    {
        foreach (var (stream, stats) in publications.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"; push {stream} {stats.Samples} samples, gaps>250ms {stats.Gaps}, max {stats.MaxGapMs:0} ms (GC {stats.GcAtMaxGapMs:0} ms), GC-in-gaps {stats.GcInGapsMs:0} ms, queued-peak-at-send {stats.PeakQueued}");
            AppendCadence(text, stats);
        }
    }

    /// <summary>Appended after the original fields, which stay as they were (log readers match them): the true longest interval,
    /// intervals over 150 ms (already a visible stop at Fast), sockets opened and sockets that ended on an error.</summary>
    private static void AppendCadence(StringBuilder text, PublicationStats stats) =>
        text.Append(CultureInfo.InvariantCulture, $", longest {stats.LongestMs:0} ms, gaps>150ms {stats.Gaps150}, opens {stats.Opens}, failed {stats.Failed}");
}
