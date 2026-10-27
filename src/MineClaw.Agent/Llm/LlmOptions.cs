namespace MineClaw.Agent.Llm;

/// <summary>Model/tier configuration. Bindable from appsettings ("Agent:Llm").</summary>
public sealed class LlmOptions
{
    /// <summary>Haiku-class "memory" model: compaction, reflection, ask_memory, budget-fallback chat.</summary>
    public string CheapModel { get; set; } = "claude-haiku-5-5";

    /// <summary>Opus-class "frontal lobe": planning, decisions, all player conversation.</summary>
    public string MainModel { get; set; } = "claude-opus-5-5";

    /// <summary>Sonnet-class worker/builder for delegated multi-step tasks (and the brain's first budget fallback).</summary>
    public string WorkerModel { get; set; } = "claude-sonnet-5-5";

    public int CheapMaxTokens { get; set; } = 400;
    public int MainMaxTokens { get; set; } = 1200;
    public int WorkerMaxTokens { get; set; } = 800;

    /// <summary>output_config.effort per tier ("low"|"medium"|"high"|""=omit). Low keeps replies terse.</summary>
    public string CheapEffort { get; set; } = "low";
    public string MainEffort { get; set; } = "medium";
    public string WorkerEffort { get; set; } = "low";

    /// <summary>
    /// "off" (default: the cheapest setting the model accepts — disabled, or between_tools on Sonnet 5.5),
    /// "adaptive", or "omit" (send nothing).
    /// </summary>
    public string CheapThinking { get; set; } = "off";
    public string MainThinking { get; set; } = "off";
    public string WorkerThinking { get; set; } = "off";

    /// <summary>
    /// Cache TTL for the static prefix (tools + system + core knowledge): "5m" (default; 1.25x write, refreshed free by
    /// every hit), "1h" (2x write, survives idle gaps), or "auto" = 1h while a session is active (chat within
    /// <see cref="ActiveSessionMinutes"/>), else 5m. The identity/self block and history always use 5m.
    /// </summary>
    public string StaticPrefixTtl { get; set; } = "5m";

    /// <summary>For StaticPrefixTtl "auto": a session counts as active this long after the last chat to the bot.</summary>
    public int ActiveSessionMinutes { get; set; } = 30;

    /// <summary>API key; null means the SDK default (ANTHROPIC_API_KEY env var).</summary>
    public string? ApiKey { get; set; }

    public int MaxRetries { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 60;

    public string ModelFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapModel, ModelTier.Worker => WorkerModel, _ => MainModel };
    public int MaxTokensFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapMaxTokens, ModelTier.Worker => WorkerMaxTokens, _ => MainMaxTokens };
    public string EffortFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapEffort, ModelTier.Worker => WorkerEffort, _ => MainEffort };
    public string ThinkingFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapThinking, ModelTier.Worker => WorkerThinking, _ => MainThinking };

    /// <summary>Which tier a model id belongs to (for per-tier accounting); null for an unknown model.</summary>
    public ModelTier? TierOfModel(string model) =>
        string.Equals(model, MainModel, StringComparison.OrdinalIgnoreCase) ? ModelTier.Main
        : string.Equals(model, WorkerModel, StringComparison.OrdinalIgnoreCase) ? ModelTier.Worker
        : string.Equals(model, CheapModel, StringComparison.OrdinalIgnoreCase) ? ModelTier.Cheap
        : null;
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
