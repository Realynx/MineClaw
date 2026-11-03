using System.Collections.Concurrent;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// A build's sign of life for the stall watchdog (<c>BodyFactory.StallSignature</c>), per coordinating body: every step a crew
/// lane finishes beats it, and so does the slicer while it plans (seconds to a minute for a band of thousands of blocks). The
/// coordinating body itself may stand still for minutes, parked while the others finish its band, or waiting on a plan: without
/// this, each such spell was a "stalled: build idle 45s" wake of the brain (a paid turn) in the middle of a healthy build. A real
/// stall (no lane moves, nothing is planned) still beats nothing, so it still wakes the brain once.
/// </summary>
public static class BuildPulse
{
    private static readonly ConcurrentDictionary<string, long> Beats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often <see cref="While"/> beats.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    public static void Beat(string body)
    {
        if (!string.IsNullOrEmpty(body)) Beats.AddOrUpdate(body, 1, (_, n) => n + 1);
    }

    /// <summary>Beats so far for <paramref name="body"/> (0 = none).</summary>
    public static long Of(string body) => string.IsNullOrEmpty(body) ? 0 : Beats.GetValueOrDefault(body);

    /// <summary>Beats every <see cref="Interval"/> until disposed: around a long planning call that is work, not a stall.</summary>
    public static IDisposable While(string body)
    {
        Beat(body);
        return new Timer(_ => Beat(body), null, Interval, Interval);
    }
}
