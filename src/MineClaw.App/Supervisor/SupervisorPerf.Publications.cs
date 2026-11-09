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
    }

    /// <summary>One monitor per socket. Only scalar counters are kept; no per-frame samples or identities are retained.</summary>
    internal PublicationMonitor MonitorPublication(string stream) => new(this, stream);

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
    }

    private void RecordPublication(string stream, double gapMs, double gcMs, long queued)
    {
        lock (_lock)
        {
            if (!_publications.TryGetValue(stream, out var stats))
            {
                if (_publications.Count >= 4) return;
                _publications[stream] = stats = new PublicationStats();
            }
            stats.Samples++;
            stats.PeakQueued = Math.Max(stats.PeakQueued, queued);
            if (gapMs <= 250) return;
            stats.Gaps++;
            stats.GcInGapsMs += gcMs;
            if (gapMs > stats.MaxGapMs) { stats.MaxGapMs = gapMs; stats.GcAtMaxGapMs = gcMs; }
        }
    }

    private static void AppendPublications(StringBuilder text, Dictionary<string, PublicationStats> publications)
    {
        foreach (var (stream, stats) in publications.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(CultureInfo.InvariantCulture, $"; push {stream} {stats.Samples} samples, gaps>250ms {stats.Gaps}, max {stats.MaxGapMs:0} ms (GC {stats.GcAtMaxGapMs:0} ms), GC-in-gaps {stats.GcInGapsMs:0} ms, queued-peak-at-send {stats.PeakQueued}");
    }
}
