using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Worker;

/// <summary>
/// Splits the registered tools between the brain (main tier) and the worker so each tier's cached prefix carries only
/// what it uses. Lists come from <see cref="WorkerOptions"/>. Cheap-tier flags are kept as they are. Unlisted tools go to
/// the worker if they could run there (multi-step game work), otherwise keep their own tiers. Call once at startup after
/// every tool is registered (tool definitions are part of the cached prefix).
/// </summary>
public static class BrainSplit
{
    public static void Apply(ToolRegistry tools, WorkerOptions options)
    {
        if (!options.Enabled) return;
        var brain = new HashSet<string>(options.BrainTools, StringComparer.OrdinalIgnoreCase);
        var worker = new HashSet<string>(options.WorkerTools, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools.All)
        {
            if (t.Name is WorkerAgent.ToolName or MemoryAgent.ToolName) continue; // brain-only by construction
            var current = tools.TiersOf(t);
            var tiers = TiersFor(t.Name, current, brain, worker);
            if (tiers != current) tools.SetTiers(t.Name, tiers);
        }
    }

    /// <summary>
    /// The cheap flag as it was, plus the listed tiers; an unlisted tool keeps the worker if it had it, else its main flag — except
    /// a brain macro reached through <c>do</c> (follow, act): no definition of its own for anyone (do runs it).
    /// </summary>
    private static ToolTiers TiersFor(string name, ToolTiers current, HashSet<string> brain, HashSet<string> worker)
    {
        var tiers = current & ToolTiers.Cheap;
        bool b = brain.Contains(name), w = worker.Contains(name);
        if (b) tiers |= ToolTiers.Main;
        if (w) tiers |= ToolTiers.Worker;
        if (b || w || ViaDo(name, brain)) return tiers;
        return tiers | (current.HasFlag(ToolTiers.Worker) ? ToolTiers.Worker : current & ToolTiers.Main);
    }

    private static bool ViaDo(string name, HashSet<string> brain) =>
        brain.Contains(DoTool.ToolName) && DoTool.Actions.Contains(name, StringComparer.OrdinalIgnoreCase);
}
