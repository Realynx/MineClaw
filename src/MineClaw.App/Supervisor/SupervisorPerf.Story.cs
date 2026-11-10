using System.Globalization;
using System.Text;
using MineClaw.Hive.Story;

namespace MineClaw.App.Supervisor;

/// <summary>
/// Story Mode on the perf line. Every colony command and state read waits on the colony's one lock, so the line says how long
/// the HTTP routes waited for it and what held it, and it always prints the story routes: a colony command is rare and cheap in
/// total, so it never made the costliest twelve, yet its wait is the lag the owner feels.
/// </summary>
public sealed partial class SupervisorPerf
{
    /// <summary>The routes printed whatever they cost: Story Mode's, and the container read behind a chest click.</summary>
    internal static bool AlwaysPrinted(string route) => route == "/api/world/containers" || SupervisorStoryRoutes.IsStoryPath(route);

    /// <summary>The twelve routes with the most total time, then the <see cref="AlwaysPrinted"/> ones that were not among them.</summary>
    private static IEnumerable<KeyValuePair<string, List<double>>> PrintedRoutes(Dictionary<string, List<double>> routes)
    {
        var byCost = routes.OrderByDescending(r => r.Value.Sum()).ToList();
        return byCost.Take(12).Concat(byCost.Skip(12).Where(r => AlwaysPrinted(r.Key)));
    }

    /// <summary>
    /// After the picks and ticks: the longest single pick and tick (one long hold shows among many short ones), the HTTP story
    /// routes' wait for the lock and how many gave up as busy, and three holders of the lock: state builds, saves and chest-memory
    /// reads. Every figure prints every minute, so a quiet minute reads as zeros.
    /// </summary>
    private static void AppendStoryLock(StringBuilder text)
    {
        var s = StoryPerf.TakeLocks();
        text.Append(CultureInfo.InvariantCulture, $", longest pick {s.LongestPick.TotalMilliseconds:0} ms tick {s.LongestTick.TotalMilliseconds:0} ms");
        AppendSample(text, "lock-wait", s.LockWait);
        text.Append(CultureInfo.InvariantCulture, $" (busy: {s.BusyCommands} commands {s.BusyReads} reads)");
        AppendSample(text, "state-build", s.StateBuilds);
        AppendSample(text, "save", s.Saves);
        AppendSample(text, "seen-chest", s.SeenChest);
    }

    private static void AppendSample(StringBuilder text, string name, StoryPerf.Sample s) => text.Append(CultureInfo.InvariantCulture,
        $", {name} {s.Count}x {s.Total.TotalMilliseconds:0} ms max {s.Longest.TotalMilliseconds:0} ms");
}
