using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Chat;

/// <summary>
/// The cheap tie-breaker for "is this unnamed line for the bot?" (<see cref="ChatRouter.Classifier"/>): ONE Haiku call with a
/// tiny prompt (the recent lines + the line), answering yes/no. The router only asks for unsure lines from regulars while the
/// bot is near them or in an active thread, at most <see cref="AddressClassifierOptions.MaxPerHour"/> times an hour.
/// </summary>
public static class AddressClassifier
{
    internal const string SystemText =
        "You sort Minecraft chat. Several humans and one bot are online. Say whether the LAST line is meant for the bot " +
        "(an answer to it, an order or request for it, about its items/tasks) or for another human. Reply with one word: yes or no.";

    public static Func<ChatMessage, IReadOnlyList<string>, CancellationToken, Task<bool?>> Create(ILlmClient llm, AgentOptions options,
        UsageTracker usage, ILogger log) => async (msg, recent, ct) =>
    {
        if (usage.CheckMode() is BudgetMode.Paused) return null;
        var bot = options.Chat.DisplayName is { Length: > 0 } dn ? $"{dn} (login {options.Chat.Username})" : options.Chat.Username;
        var lines = string.Join("\n", recent.TakeLast(6).Select(l => TextUtil.Truncate(TextUtil.OneLine(l), 120)));
        var user = $"bot: {bot}\nrecent:\n{lines}\nLAST {msg.Player}: {TextUtil.Truncate(TextUtil.OneLine(msg.Text), 160)}\nFor the bot?";
        var response = await llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Cheap,
            System = [new LlmSystemBlock(SystemText)],
            Messages = [LlmMessage.User(user)],
            MaxTokens = 4,
            CacheHistory = false,
            Purpose = "address_classify",
        }, ct).ConfigureAwait(false);
        usage.Record(response.Model, response.Usage, ModelTier.Cheap);
        var answer = response.Text.Trim().ToLowerInvariant();
        bool? yes = answer.StartsWith("yes") ? true : answer.StartsWith("no") ? false : null;
        log.LogDebug("address classifier: {Player} \"{Text}\" → {Answer}", msg.Player, TextUtil.Truncate(msg.Text, 60), answer);
        return yes;
    };
}
