using MineClaw.Agent.Admin;

namespace MineClaw.MemoryExplorer.ViewModels;

/// <summary>How a rebuild's progress reads on the Edits &amp; audit and Shards pages: "summarize 3/4 · L1" and its percentage.</summary>
internal static class RebuildText
{
    public static string Stage(RebuildProgress p) => $"{p.Stage} {p.Done}/{p.Total}" + (p.Message != null ? " · " + p.Message : "");

    public static double Percent(RebuildProgress p) => p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
}
