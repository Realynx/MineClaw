namespace MineClaw.Bot.Blueprints;

/// <summary>Limits of one Design Studio message ("Supervisor:Viewer:Designer").</summary>
public sealed class DesignerOptions
{
    /// <summary>Off = the <c>/api/designer/*</c> routes are 404 even with the viewer on.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Model calls per message (each tool round is one).</summary>
    public int MaxRounds { get; set; } = 8;
    /// <summary>Output ceiling of one call. Thinking and every tool call of a reply share it: a reply cut at it loses its last
    /// call (the model is told and retries smaller), so leave room for a few big calls.</summary>
    public int MaxTokens { get; set; } = 8000;
    /// <summary>A message stops calling the model once it has cost this much (USD).</summary>
    public decimal MaxUsdPerMessage { get; set; } = 1.50m;
    /// <summary>Earlier exchanges sent as plain text (older ones are dropped: the canvas summary carries the state).</summary>
    public int HistoryTurns { get; set; } = 6;
    /// <summary>output_config.effort of the studio's calls ("" = the tier's).</summary>
    public string Effort { get; set; } = "low";
    /// <summary>
    /// The whole request's ceiling (system + tools + history + this message's rounds + output), estimated: before each call older
    /// tool results are cut to fit, and a call that still can't fit is not made. An Ollama model's <c>num_ctx</c> lowers it.
    /// </summary>
    public int MaxContextTokens { get; set; } = 32_000;
    /// <summary>Rounds in a row whose tool calls all failed before the message gives up (the model is stuck).</summary>
    public int MaxFailedRounds { get; set; } = 3;
}
