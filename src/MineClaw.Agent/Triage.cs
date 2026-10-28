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

    /// <summary>A hand-over notice with nothing promised on it.</summary>
    public static bool IsPlainHandover(GameEvent e) =>
        e.Kind == EventKind.Notice && e.Player != null && e.Text.Contains(" gave you ", StringComparison.Ordinal)
        && !e.Text.Contains("you said you'd", StringComparison.Ordinal);

    public TriageDecision Choose(EventBatch batch, BudgetMode mode)
    {
        if (mode == BudgetMode.CheapOnly) return new(ModelTier.Cheap, false, "budget: cheap only");
        if (batch.HasDanger) return new(ModelTier.Main, false, "danger");
        // A plain idle check-in (nobody asked for anything) runs on the cheap tier; it escalates if there's real work.
        // On Opus with an expired cache these were the night's two costliest turns.
        if (_options.IdleCheckInsOnCheapTier && batch.Waking.Any() && batch.Waking.All(e => e.Kind == EventKind.IdleTick && e.Text == "idle check-in"))
            return new(ModelTier.Cheap, true, "idle check-in");
        if (!batch.IsConversational) return new(ModelTier.Main, false, "task event");
        // A plain hand-over ("PoofimaFox gave you 12 fence (thank them in one short line)"): one cheap thank-you; a kept promise
        // ("...; you said you'd make bread") needs the brain.
        if (batch.Waking.Any() && batch.Waking.All(IsPlainHandover)) return new(ModelTier.Cheap, true, "hand-over");
        if (!_options.CheapFirstForChat) return new(ModelTier.Main, false, "config");
        if (batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper && _action.IsMatch(e.Text)))
            return new(ModelTier.Main, false, "action words");
        return new(ModelTier.Cheap, true, "chat");
    }
}
