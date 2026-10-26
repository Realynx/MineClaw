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

    public ModelPrice For(string model) => Models.TryGetValue(model, out var p) ? p : Fallback;

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
