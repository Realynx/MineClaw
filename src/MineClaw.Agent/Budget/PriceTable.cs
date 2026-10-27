using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Budget;

/// <summary>USD per million tokens for one model.</summary>
public sealed class ModelPrice
{
    public decimal InputPerM { get; set; }
    public decimal OutputPerM { get; set; }
    public decimal CacheReadPerM { get; set; }
    public decimal CacheWrite5mPerM { get; set; }
    public decimal CacheWrite1hPerM { get; set; }

    public static ModelPrice Standard(decimal input, decimal output, decimal? cacheRead = null) => new()
    {
        InputPerM = input,
        OutputPerM = output,
        CacheReadPerM = cacheRead ?? input * 0.1m,
        CacheWrite5mPerM = input * 1.25m,
        CacheWrite1hPerM = input * 2m,
    };
}

/// <summary>Configurable price table (bindable as a dictionary keyed by model id). Unknown models are priced
/// at <see cref="Fallback"/> (deliberately expensive, so a typo can't silently blow the budget).</summary>
public sealed class PriceTable
{
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // Anthropic first-party list prices (per MTok) as of 2026-10.
        ["claude-haiku-5-5"] = ModelPrice.Standard(0.10m, 0.50m),          // ≤100K-token prompts
        ["claude-sonnet-5-5"] = ModelPrice.Standard(2.00m, 10.00m, 0.20m),
        ["claude-opus-5-5"] = ModelPrice.Standard(4.00m, 20.00m, 0.20m),
        ["claude-haiku-4-5"] = ModelPrice.Standard(1.00m, 5.00m),
        ["claude-sonnet-4-6"] = ModelPrice.Standard(3.00m, 15.00m),
    };

    public ModelPrice Fallback { get; set; } = ModelPrice.Standard(5.00m, 25.00m);

    /// <summary>Price of models served by the local Ollama server ("Agent:Prices:Ollama"; default $0 — local/proxied calls are
    /// tracked but free). A model listed in <see cref="Models"/> uses that entry instead.</summary>
    public ModelPrice Ollama { get; set; } = new();

    /// <summary>Model ids routed to Ollama (filled from the LLM options at startup by <see cref="RegisterProviders"/>).</summary>
    public HashSet<string> OllamaModels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records which configured models (tiers and profiles) are served by Ollama.</summary>
    public PriceTable RegisterProviders(LlmOptions llm)
    {
        foreach (var t in new[] { ModelTier.Main, ModelTier.Worker, ModelTier.Cheap })
            if (llm.ProviderFor(t) == LlmProviders.Ollama && !string.IsNullOrWhiteSpace(llm.ModelFor(t))) OllamaModels.Add(llm.ModelFor(t).Trim());
        foreach (var p in llm.Profiles.Values)
            if (LlmProviders.Normalize(p.Provider) == LlmProviders.Ollama && !string.IsNullOrWhiteSpace(p.Model)) OllamaModels.Add(p.Model.Trim());
        return this;
    }

    /// <summary>"ollama" for a registered Ollama model, else "anthropic".</summary>
    public string ProviderOf(string model) => OllamaModels.Contains(model) ? LlmProviders.Ollama : LlmProviders.Anthropic;

    public ModelPrice For(string model) => Models.TryGetValue(model, out var p) ? p : OllamaModels.Contains(model) ? Ollama : Fallback;

    public decimal Cost(string model, LlmUsage u)
    {
        var p = For(model);
        return (u.InputTokens * p.InputPerM
              + u.OutputTokens * p.OutputPerM
              + u.CacheReadTokens * p.CacheReadPerM
              + u.CacheWrite5mTokens * p.CacheWrite5mPerM
              + u.CacheWrite1hTokens * p.CacheWrite1hPerM) / 1_000_000m;
    }
}
