using System.Text.RegularExpressions;
using MineClaw.Agent.Tools;

namespace MineClaw.Bot.WorldIndex;

/// <summary><c>activity</c>: what other players did (rollups + recent raw events), deterministic, zero LLM tokens.</summary>
public static partial class ActivityTools
{
    public const string ToolName = "activity";
    public const int MaxChars = 600;

    public static void Register(ToolRegistry tools, ActivityLog log, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        tools.Register(ToolName, "What players did. since: today|yesterday|1h|30m|2d.",
            ToolSchema.Create().Str("player", required: false).Str("since", required: false).Str("query", required: false).Build(),
            (i, _) => Task.FromResult(ToolResult.Success(Answer(log, i.Str("player"), i.Str("since"), i.Str("query"), time.GetUtcNow()))),
            cheapTier: true, tiers: ToolTiers.Cheap, maxResultChars: MaxChars); // the brain reaches it via ask_memory (brain tool budget)
    }

    [GeneratedRegex(@"^\s*(\d+)\s*([mhd])\w*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Span();

    /// <summary>Builds the question for <see cref="ActivityLog.Ask"/>; "since" accepts words or 30m/2h/3d.</summary>
    public static string Answer(ActivityLog log, string? player, string? since, string? query, DateTimeOffset now)
    {
        DateTimeOffset? from = null;
        string words = "";
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (Span().Match(since) is { Success: true } m)
            {
                int n = int.Parse(m.Groups[1].Value);
                from = now - (char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n) });
            }
            else words = " " + since.Trim();
        }
        var q = string.Join(" ", new[] { query, player is { Length: > 0 } ? player : null }.Where(s => !string.IsNullOrWhiteSpace(s))) + words;
        if (q.Trim().Length == 0) q = "today";
        return log.Ask(q, now, MaxChars, from);
    }
}
