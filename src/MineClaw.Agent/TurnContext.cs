namespace MineClaw.Agent;

/// <summary>Who the current model turn is for. Flows into tools (and background tasks started from the turn).</summary>
/// <param name="Speaker">The player whose message woke the bot (first waking chat), if any.</param>
/// <param name="Players">Everyone addressed in the batch.</param>
/// <param name="IsTest">The turn was driven by injected test chat (console /as).</param>
public sealed record TurnInfo(string? Speaker, IReadOnlyList<string> Players, bool IsTest);

/// <summary>
/// Ambient per-turn info (AsyncLocal): set by <see cref="BotAgent"/> for each episode so tools can phrase results relative
/// to the asking player and memory/self tools can tell test-driven turns from real ones.
/// </summary>
public static class TurnContext
{
    private static readonly AsyncLocal<TurnInfo?> _current = new();

    public static TurnInfo? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>True inside a turn driven by injected test chat.</summary>
    public static bool IsTest => _current.Value?.IsTest == true;
}
