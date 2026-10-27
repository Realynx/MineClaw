using MineClaw.Agent.Chat;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Human;

/// <summary>
/// The waiting part of watch_demo, shared by the runtime and game versions: holds the hive conversation thread with the
/// demonstrator, says the "show me" line only when the brain didn't already speak this turn, and ends on a natural
/// completion ("did it", "there", "like that", "yep"...), or on ANY line once the demonstrator did something and then went
/// idle (they're talking because they finished), or on the time limit. Other lines are kept and reported back.
/// </summary>
public static class DemoWait
{
    /// <summary>Idle time after the last demonstrated action that makes the player's next line a completion.</summary>
    public static TimeSpan IdleBeforeSpeech { get; set; } = TimeSpan.FromSeconds(4);

    public const string ToolDescription =
        "Watch a player demo a structure (punches point at blocks); ends when they say they're done. Steps get saved.";

    public sealed record Outcome(IReadOnlyList<string> Said, string Ended);

    public static async Task<Outcome> RunAsync(ChatRouter chat, IDemonstrationSession session, string player, int maxSeconds,
        TimeProvider? time, CancellationToken ct)
    {
        var clock = time ?? TimeProvider.System;
        using var thread = chat.HoldThread(player, TimeSpan.FromSeconds(maxSeconds + 5));
        if (!chat.ModelSpokeThisTurn)
            await chat.SayAsync($"{player}, show me! tell me when you're done", audience: [player], ct: ct).ConfigureAwait(false);
        var said = new List<string>();
        var deadline = clock.GetUtcNow().AddSeconds(maxSeconds);
        while (true)
        {
            var left = deadline - clock.GetUtcNow();
            if (left <= TimeSpan.Zero) return new(said, "time");
            var reply = await chat.WaitForReplyAsync(player, left, ct).ConfigureAwait(false);
            if (reply == null) return new(said, "time");
            var text = ReplyIntent.Strip(reply);
            if (ReplyIntent.IsCompletion(text)) return new(said, "said done");
            said.Add(text);
            if (session.ActionCount > 0 && session.LastActivity is { } last && clock.GetUtcNow() - last >= IdleBeforeSpeech)
                return new(said, "idle then spoke");
        }
    }

    /// <summary>" (they said: "...")" for the tool result, so the brain hears lines the wait consumed.</summary>
    public static string SaidSuffix(Outcome o) =>
        o.Said.Count == 0 ? "" : " (they said: " + string.Join(" / ", o.Said.TakeLast(3).Select(s => $"\"{TextUtil.Truncate(s, 80)}\"")) + ")";
}
