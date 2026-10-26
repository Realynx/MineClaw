namespace MineClaw.Agent.Llm;

/// <summary>Model/tier configuration. Bindable from appsettings ("Agent:Llm").</summary>
public sealed class LlmOptions
{
    /// <summary>Haiku-class model for triage, small talk and compaction.</summary>
    public string CheapModel { get; set; } = "claude-haiku-5-5";

    /// <summary>Sonnet-class model for planning and multi-step tasks.</summary>
    public string MainModel { get; set; } = "claude-sonnet-5-5";

    public int CheapMaxTokens { get; set; } = 400;
    public int MainMaxTokens { get; set; } = 1200;

    /// <summary>output_config.effort per tier ("low"|"medium"|"high"|""=omit). Low keeps replies terse.</summary>
    public string CheapEffort { get; set; } = "low";
    public string MainEffort { get; set; } = "medium";

    /// <summary>
    /// "off" (default: the cheapest setting the model accepts — disabled, or between_tools on Sonnet 5.5),
    /// "adaptive", or "omit" (send nothing).
    /// </summary>
    public string CheapThinking { get; set; } = "off";
    public string MainThinking { get; set; } = "off";

    /// <summary>Cache TTL for the static prefix (system + tools + knowledge): "1h" survives idle gaps; "5m" is cheaper to write.</summary>
    public string StaticPrefixTtl { get; set; } = "1h";

    /// <summary>API key; null means the SDK default (ANTHROPIC_API_KEY env var).</summary>
    public string? ApiKey { get; set; }

    public int MaxRetries { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 60;

    public string ModelFor(ModelTier tier) => tier == ModelTier.Cheap ? CheapModel : MainModel;
    public int MaxTokensFor(ModelTier tier) => tier == ModelTier.Cheap ? CheapMaxTokens : MainMaxTokens;
    public string EffortFor(ModelTier tier) => tier == ModelTier.Cheap ? CheapEffort : MainEffort;
    public string ThinkingFor(ModelTier tier) => tier == ModelTier.Cheap ? CheapThinking : MainThinking;
}

/// <summary>Per-model request-shape quirks (which thinking-off form is accepted, whether effort is supported).</summary>
public static class ModelProfiles
{
    public enum ThinkingOffStyle { Omit, Disabled, BetweenTools }

    public static ThinkingOffStyle OffStyle(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.StartsWith("claude-sonnet-5-5")) return ThinkingOffStyle.BetweenTools; // disabled → 400
        if (m.StartsWith("claude-opus-5-5") || m.StartsWith("claude-fable-5") || m.StartsWith("claude-mythos"))
            return ThinkingOffStyle.Omit; // thinking can't be disabled; low effort is the lever
        if (m.StartsWith("claude-haiku-5") || m.StartsWith("claude-sonnet-5") || m.StartsWith("claude-opus-5") ||
            m.StartsWith("claude-opus-4-") || m.StartsWith("claude-sonnet-4-6"))
            return ThinkingOffStyle.Disabled;
        return ThinkingOffStyle.Omit; // older models default to no thinking
    }

    public static bool SupportsEffort(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.StartsWith("claude-haiku-4") || m.StartsWith("claude-sonnet-4-5") || m.StartsWith("claude-3")) return false;
        return m.StartsWith("claude-");
    }
}
