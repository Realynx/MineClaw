using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot;

/// <summary>
/// Protection rule 6: every refusal counted by reason ("structure", "unknown_build", "floor", "last_trees", ...), so we can see
/// what is still too strict. One INF log line per refusal with the running count; with a directory, one jsonl line per
/// refusal in <c>protection-YYYY-MM-DD.jsonl</c>. Thread-safe, never throws.
/// </summary>
public sealed class ProtectionTelemetry(ILogger? log = null, string? directory = null, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _fileLock = new();

    public IReadOnlyDictionary<string, int> Counts => _counts;

    public int Total => _counts.Values.Sum();

    public void Count(string reason, string? detail = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason)) reason = "unknown";
            int n = _counts.AddOrUpdate(reason, 1, (_, v) => v + 1);
            log?.LogInformation("protection refusal ({Reason} #{N}): {Detail}", reason, n, detail ?? "");
            if (directory == null) return;
            var now = _time.GetUtcNow();
            var line = JsonSerializer.Serialize(new { at = now.ToString("O"), reason, detail, count = n });
            lock (_fileLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"protection-{now:yyyy-MM-dd}.jsonl"), line + "\n");
            }
        }
        catch (Exception) { /* telemetry never breaks the bot */ }
    }

    /// <summary>"refusals: structure 3, unknown_build 1" (most frequent first).</summary>
    public string Summary() => _counts.IsEmpty ? "refusals: none"
        : "refusals: " + string.Join(", ", _counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"));
}
