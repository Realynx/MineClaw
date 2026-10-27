using System.Text.Json;

namespace MineClaw.Agent.Memory;

/// <summary>
/// An ongoing instruction from a player ("mine the gen all night", "keep chopping trees"): the job a bot returns to whenever
/// it is idle (after answering chat, after a question times out, after a restart, after a clone respawn). One active order
/// per bot. Stored in the shared db (<c>standing_orders</c>), so hive clones and the supervisor read the same rows.
/// </summary>
public sealed class StandingOrder
{
    /// <summary>Login of the bot that carries it out (main or a clone).</summary>
    public string Bot { get; set; } = "";
    /// <summary>Who gave it.</summary>
    public string? Player { get; set; }
    /// <summary>Short label for the turn header ("mine gen cell B").</summary>
    public string Job { get; set; } = "";
    /// <summary>The tool that runs it (script with a saved name, delegate with task/done_when, run_routine...).</summary>
    public string Tool { get; set; } = "";
    /// <summary>That tool's input, re-issued as-is by C# on resume (zero tokens).</summary>
    public JsonElement Input { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>Who/what ended it (kept as a trace, never deleted silently).</summary>
    public string? EndedBy { get; set; }
    public int Resumes { get; set; }
    public DateTimeOffset? LastResumed { get; set; }
    /// <summary>Resumed runs that failed in a row (resume pauses after a few).</summary>
    public int FailuresInRow { get; set; }
    public string? LastOutcome { get; set; }

    /// <summary>"[standing: mine gen cell B (fox)]".</summary>
    public string HeaderTag => $"[standing: {Job}{(Player != null ? $" ({Player})" : "")}]";
}
