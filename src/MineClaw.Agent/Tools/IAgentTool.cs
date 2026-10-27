using System.Text.Json;

namespace MineClaw.Agent.Tools;

/// <summary>
/// Which model tiers may see and call a tool. Tool definitions are part of each tier's cached prefix, so each tier
/// gets a deliberately small, stable set: the brain (Main) keeps cheap single-shot tools plus <c>delegate</c>/<c>ask_memory</c>,
/// the worker gets the game macro tools, the cheap tier a handful of chat-safe ones.
/// </summary>
[Flags]
public enum ToolTiers
{
    None = 0,
    Main = 1,
    Worker = 2,
    Cheap = 4,
    /// <summary>Default for tools registered without a tier: brain and worker (a tier policy may narrow it later).</summary>
    Default = Main | Worker,
    All = Main | Worker | Cheap,
}

public static class ToolTierExtensions
{
    public static ToolTiers Flag(this Llm.ModelTier tier) => tier switch
    {
        Llm.ModelTier.Main => ToolTiers.Main,
        Llm.ModelTier.Worker => ToolTiers.Worker,
        _ => ToolTiers.Cheap,
    };
}

/// <summary>
/// A tool the model can call. Implementations do real work deterministically in C# and return a terse
/// one-line <see cref="ToolResult"/>; they must honour the cancellation token (the <c>stop</c> tool and
/// danger events cancel long-running tools).
/// </summary>
public interface IAgentTool
{
    /// <summary>snake_case tool name, unique within a <see cref="ToolRegistry"/>.</summary>
    string Name { get; }

    /// <summary>One or two short sentences. Every word costs tokens on every (cached) request.</summary>
    string Description { get; }

    /// <summary>JSON Schema object for the input (<c>{"type":"object","properties":{...},"required":[...]}</c>).</summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// True for tools that take game time (goto, mine, craft chains, routines). They run as the bot's single
    /// "current activity": a new long-running call supersedes the old one, <c>stop</c> cancels it, and if it
    /// outlives the inline wait it continues in the background and reports back via a task event.
    /// </summary>
    bool IsLongRunning => false;

    /// <summary>True if the cheap (triage/chat) tier may call this tool too.</summary>
    bool AvailableToCheapTier => false;

    /// <summary>Tiers that see this tool (before any <see cref="ToolRegistry.SetTiers"/> override).</summary>
    ToolTiers Tiers => ToolTiers.Default | (AvailableToCheapTier ? ToolTiers.Cheap : ToolTiers.None);

    /// <summary>Per-tool result cap (chars) overriding the registry default; null = default.</summary>
    int? MaxResultChars => null;

    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct);
}
