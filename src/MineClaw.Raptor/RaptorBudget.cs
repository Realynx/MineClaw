namespace MineClaw.Raptor;

/// <summary>
/// Summary-spend cap for a build, rebuild or refresh (MineClaw extension). Each summarizer call is reserved up front
/// from an estimate (input tokens of the prompt + summarization_length output), in deterministic cluster order;
/// a cluster that does not fit gets an extractive placeholder marked <c>SummaryPending</c> for a later refresh.
/// Thread-safe.
/// </summary>
public sealed class RaptorBudget
{
    private readonly object _lock = new();

    public int? MaxSummaries { get; init; }
    /// <summary>Cap on summarizer input + output tokens.</summary>
    public long? MaxTokens { get; init; }
    public double? MaxCostUsd { get; init; }
    /// <summary>Used to estimate cost when the summarizer does not report it (Haiku-class defaults).</summary>
    public double InputUsdPerMTok { get; init; } = 1.0;
    public double OutputUsdPerMTok { get; init; } = 5.0;

    public int Summaries { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public double CostUsd { get; private set; }
    public int Denied { get; private set; }

    public static RaptorBudget Unlimited() => new();

    public bool IsExhausted
    {
        get
        {
            lock (_lock) return (MaxSummaries is { } m && Summaries >= m) || (MaxTokens is { } t && InputTokens + OutputTokens >= t) || (MaxCostUsd is { } c && CostUsd >= c);
        }
    }

    internal double Estimate(int inTok, int outTok) => inTok * InputUsdPerMTok / 1e6 + outTok * OutputUsdPerMTok / 1e6;

    /// <summary>Reserves one summary call; false (and counted as denied) when it would exceed a cap.</summary>
    internal bool TryReserve(int estInput, int estOutput)
    {
        lock (_lock)
        {
            double cost = Estimate(estInput, estOutput);
            if ((MaxSummaries is { } m && Summaries + 1 > m)
                || (MaxTokens is { } t && InputTokens + OutputTokens + estInput + estOutput > t)
                || (MaxCostUsd is { } c && CostUsd + cost > c))
            {
                Denied++;
                return false;
            }
            Summaries++;
            InputTokens += estInput;
            OutputTokens += estOutput;
            CostUsd += cost;
            return true;
        }
    }

    /// <summary>Replaces a reservation's estimate with the provider-reported usage.</summary>
    internal void Settle(int estInput, int estOutput, SummaryResult actual)
    {
        lock (_lock)
        {
            InputTokens += actual.InputTokens - estInput;
            OutputTokens += actual.OutputTokens - estOutput;
            CostUsd += (actual.CostUsd ?? Estimate(actual.InputTokens, actual.OutputTokens)) - Estimate(estInput, estOutput);
        }
    }

    /// <summary>Releases a reservation whose call failed.</summary>
    internal void Release(int estInput, int estOutput)
    {
        lock (_lock)
        {
            Summaries--;
            InputTokens -= estInput;
            OutputTokens -= estOutput;
            CostUsd -= Estimate(estInput, estOutput);
        }
    }

    public override string ToString() => $"summaries={Summaries} in={InputTokens} out={OutputTokens} cost=${CostUsd:F4} denied={Denied}";
}
