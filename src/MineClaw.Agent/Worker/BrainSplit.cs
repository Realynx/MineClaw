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
            var tiers = current & ToolTiers.Cheap;
            bool b = brain.Contains(t.Name), w = worker.Contains(t.Name);
            if (b) tiers |= ToolTiers.Main;
            if (w) tiers |= ToolTiers.Worker;
            if (!b && !w) tiers |= current.HasFlag(ToolTiers.Worker) ? ToolTiers.Worker : current & ToolTiers.Main;
            if (tiers != current) tools.SetTiers(t.Name, tiers);
        }
    }
}
