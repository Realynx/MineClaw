using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Raptor;

namespace MineClaw.Agent.Rag.Raptor;

/// <summary>Thrown when a summarizer's own cost cap is reached (RAPTOR turns it into a pending placeholder).</summary>
public sealed class SummaryBudgetExceededException(string message) : Exception(message);

/// <summary>
/// RAPTOR <see cref="ISummarizer"/> on the cheap (Haiku) tier: the paper's prompt (system + user, pre-rendered by the
/// library), <c>max_tokens</c> = summarization_length, no history caching. Usage is recorded in
/// <see cref="UsageTracker"/> under the memory tier and priced from its <see cref="PriceTable"/>; the call is refused
/// once <see cref="MaxCostUsd"/> has been spent by this instance (a second guard behind <see cref="RaptorBudget"/>).
/// </summary>
public sealed class HaikuSummarizer : ISummarizer
{
    private readonly ILlmClient _llm;
    private readonly UsageTracker? _usage;
    private readonly PriceTable _prices;
    private readonly string _model;
    private readonly object _lock = new();

    public HaikuSummarizer(ILlmClient llm, LlmOptions llmOptions, UsageTracker? usage = null, decimal? maxCostUsd = null)
    {
        _llm = llm;
        _usage = usage;
        _prices = usage?.Prices ?? new PriceTable();
        _model = llmOptions.CheapModel;
        MaxCostUsd = maxCostUsd;
    }

    /// <summary>Lifetime cap for this instance (null = only the per-job <see cref="RaptorBudget"/> applies).</summary>
    public decimal? MaxCostUsd { get; set; }
    /// <summary>
    /// Appended to the paper's system line (the user prompt stays verbatim). Without it Haiku writes long, gets cut at
    /// max_tokens mid-sentence, and the next layer up then summarizes "fragments" (and says so). Null/empty = paper only.
    /// </summary>
    public string? LengthHint { get; set; } = "Reply with the summary only: plain prose, no headings, lists or preamble, at most 60 words.";
    /// <summary>max_tokens = summarization_length × this, so a summary can finish its sentence (trimmed to the last full one).</summary>
    public double MaxTokensHeadroom { get; set; } = 1.5;
    /// <summary>Per-job hard stop: no new call starts once <see cref="CostUsd"/> reaches this (set by the scheduler).</summary>
    public decimal? JobLimitUsd { get; set; }
    public decimal CostUsd { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public int Calls { get; private set; }

    /// <summary>USD per million input/output tokens of the cheap model (for <see cref="RaptorBudget"/> estimates).</summary>
    public (double Input, double Output) Prices
    {
        get
        {
            var p = _prices.For(_model);
            return ((double)p.InputPerM, (double)p.OutputPerM);
        }
    }

    /// <summary>A budget for one job whose estimates use the cheap model's real prices.</summary>
    public RaptorBudget Budget(decimal maxUsd)
    {
        var (i, o) = Prices;
        return new RaptorBudget { MaxCostUsd = (double)Math.Max(0, maxUsd), InputUsdPerMTok = i, OutputUsdPerMTok = o };
    }

    public async Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            if (MaxCostUsd is { } cap && CostUsd >= cap)
                throw new SummaryBudgetExceededException($"summary cost cap ${cap:F2} reached");
            if (JobLimitUsd is { } job && CostUsd >= job)
                throw new SummaryBudgetExceededException("job summary budget spent");
        }

        var response = await _llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Cheap,
            System = [new LlmSystemBlock(string.IsNullOrWhiteSpace(LengthHint) ? request.SystemPrompt : request.SystemPrompt + ". " + LengthHint)],
            Messages = [LlmMessage.User(request.UserPrompt)],
            MaxTokens = Math.Max(16, (int)Math.Round(request.MaxTokens * Math.Max(1.0, MaxTokensHeadroom))),
            CacheHistory = false,
            Purpose = "raptor",
        }, ct).ConfigureAwait(false);

        decimal cost = _usage?.Record(response.Model, response.Usage, ModelTier.Cheap) ?? _prices.Cost(response.Model, response.Usage);
        lock (_lock)
        {
            CostUsd += cost;
            InputTokens += response.Usage.InputTokens + response.Usage.CacheReadTokens + response.Usage.CacheWriteTokens;
            OutputTokens += response.Usage.OutputTokens;
            Calls++;
        }
        var text = response.Text.Trim();
        if (response.StopReason == StopReasons.MaxTokens) text = TrimToSentence(text);
        if (text.Length == 0) throw new InvalidOperationException("empty summary");
        return new SummaryResult(text, (int)(response.Usage.InputTokens + response.Usage.CacheReadTokens + response.Usage.CacheWriteTokens),
            (int)response.Usage.OutputTokens, (double)cost);
    }

    /// <summary>Drops a trailing unfinished sentence (keeps the text if no sentence end is past its first third).</summary>
    internal static string TrimToSentence(string text)
    {
        int cut = -1;
        for (int i = text.Length - 1; i >= text.Length / 3; i--)
            if (text[i] is '.' or '!' or '?' && (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1]))) { cut = i; break; }
        return cut < 0 ? text : text[..(cut + 1)];
    }
}
