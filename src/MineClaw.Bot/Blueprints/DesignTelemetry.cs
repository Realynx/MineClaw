using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Blueprints;

/// <summary>Per-design telemetry: versions, author tokens, $ (author + perception), time, outcome → <c>design-YYYY-MM-DD.jsonl</c>.</summary>
public sealed class DesignTelemetry(string? directory, ILogger? log = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    public int Designs { get; private set; }
    public decimal TotalUsd { get; private set; }

    public void Record(BlueprintRecord rec, DesignOutcome o, bool revision)
    {
        lock (_lock) { Designs++; TotalUsd += o.Usd; }
        var best = o.Best;
        log?.LogInformation("design #{Id} {Kind}: {N} version(s), best v{Best} {Pass}, ${Usd:0.000} (author ${A:0.000}, perception ${P:0.000}), {In}/{Out} tok, {Ms} ms, stop: {Stop}",
            rec.Id, revision ? "revision" : "new", o.Versions.Count, best?.N, best?.Passing == true ? "passing" : "failing", o.Usd, o.AuthorUsd, o.PerceptionUsd,
            o.InputTokens, o.OutputTokens, o.Ms, o.Stop);
        if (directory == null) return;
        try
        {
            var now = _time.GetUtcNow();
            var line = JsonSerializer.Serialize(new
            {
                at = now, id = rec.Id, name = rec.Name, revision, versions = o.Versions.Count, best = best?.N, passing = best?.Passing == true,
                score = best?.Score, errors = best?.Eval?.Errors, warnings = best?.Eval?.Warnings, critique = best?.Critique?.Text,
                usd = o.Usd, author_usd = o.AuthorUsd, perception_usd = o.PerceptionUsd, input_tokens = o.InputTokens, output_tokens = o.OutputTokens,
                image = o.ImageShown, ms = o.Ms, stop = o.Stop,
            });
            Directory.CreateDirectory(directory);
            lock (_lock) File.AppendAllText(Path.Combine(directory, $"design-{now:yyyy-MM-dd}.jsonl"), line + "\n");
        }
        catch (Exception ex) { log?.LogDebug(ex, "design telemetry not written"); }
    }
}
