using System.Text.RegularExpressions;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent;

public sealed record TriageDecision(ModelTier Tier, bool CanEscalate, string Why);

/// <summary>
/// Two-stage triage. Stage 1 (free, here): danger/task events and chat containing action words go straight to the
/// main tier; plain conversation goes to the cheap tier. Stage 2 (in <see cref="BotAgent"/>): the cheap tier gets
/// an <c>escalate</c> tool and hands the batch to the main tier if it turns out to need planning or game tools.
/// </summary>
public sealed class Triage
{
    private readonly TriageOptions _options;
    private readonly Regex _action;

    public Triage(TriageOptions options)
    {
        _options = options;
        _action = new Regex(@"(?<![A-Za-z])(?:" + string.Join("|", options.ActionWords.Select(Regex.Escape)) + @")(?![A-Za-z])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public TriageDecision Choose(EventBatch batch, BudgetMode mode)
    {
        if (mode == BudgetMode.CheapOnly) return new(ModelTier.Cheap, false, "budget: cheap only");
        if (batch.HasDanger) return new(ModelTier.Main, false, "danger");
        if (!batch.IsConversational) return new(ModelTier.Main, false, "task event");
        if (!_options.CheapFirstForChat) return new(ModelTier.Main, false, "config");
        if (batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper && _action.IsMatch(e.Text)))
            return new(ModelTier.Main, false, "action words");
        return new(ModelTier.Cheap, true, "chat");
    }
}
