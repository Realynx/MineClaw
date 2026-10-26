using System.Text.Json;

namespace MineClaw.Agent.Tools;

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

    /// <summary>True if the cheap (triage/chat) tier may call this tool too. Main tier always gets every tool.</summary>
    bool AvailableToCheapTier => false;

    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct);
}
