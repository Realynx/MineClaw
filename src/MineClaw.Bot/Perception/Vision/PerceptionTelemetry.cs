using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>
/// Perception tier telemetry: calls, cache hits, latency, tokens, $ and the hallucination rate (claims the voxels contradicted
/// / claims checked). One INF line per call; with a directory, one jsonl line per call in <c>perception-YYYY-MM-DD.jsonl</c>.
/// Thread-safe, never throws.
/// </summary>
public sealed class PerceptionTelemetry(ILogger? log = null, string? directory = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly List<long> _latency = new();

    public int Calls { get; private set; }
    public int CacheHits { get; private set; }
    public int Errors { get; private set; }
    public int Auto { get; private set; }
    public int Skipped { get; private set; }
    public int Checked { get; private set; }
    public int Mismatched { get; private set; }
    public int Dropped { get; private set; }
    public int Corrected { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public decimal Usd { get; private set; }

    public double HallucinationRate { get { lock (_lock) return Checked == 0 ? 0 : (double)Mismatched / Checked; } }

    public long P50LatencyMs
    {
        get { lock (_lock) return _latency.Count == 0 ? 0 : _latency.Order().ElementAt(_latency.Count / 2); }
    }

    public void Skip() { lock (_lock) Skipped++; }

    public void Record(VerifiedAnswer a, string source, decimal usd)
    {
        try
        {
            var r = a.Raw;
            lock (_lock)
            {
                if (a.Cached) { CacheHits++; }
                else
                {
                    Calls++;
                    if (a.Error != null) Errors++;
                    if (source.StartsWith("auto", StringComparison.Ordinal)) Auto++;
                    Checked += a.Checked;
                    Mismatched += a.Mismatched;
                    Dropped += a.Dropped;
                    Corrected += a.Corrected;
                    if (r != null)
                    {
                        InputTokens += r.Usage.InputTokens + r.Usage.CacheReadTokens + r.Usage.CacheWriteTokens;
                        OutputTokens += r.Usage.OutputTokens;
                        _latency.Add(r.LatencyMs);
                        if (_latency.Count > 500) _latency.RemoveAt(0);
                    }
                    Usd += usd;
                }
            }
            if (a.Cached) return;
            log?.LogInformation("perception ({Source}) {Model} {Ms}ms in {In} out {Out} ${Usd:0.0000}: {Checked} checked, {Bad} wrong, {Dropped} dropped, {Fixed} fixed; halluc {Rate:P0} (run {Run:P0}) | {Q}",
                source, r?.Model, r?.LatencyMs, r?.Usage.InputTokens, r?.Usage.OutputTokens, usd, a.Checked, a.Mismatched, a.Dropped, a.Corrected,
                a.HallucinationRate, HallucinationRate, a.Question);
            if (directory == null) return;
            var now = _time.GetUtcNow();
            var line = JsonSerializer.Serialize(new
            {
                at = now.ToString("O"), source, q = a.Question, model = r?.Model, provider = r?.Provider, ms = r?.LatencyMs,
                @in = r?.Usage.InputTokens, @out = r?.Usage.OutputTokens, usd, facts = a.Facts.Count, @checked = a.Checked,
                mismatched = a.Mismatched, dropped = a.Dropped, corrected = a.Corrected, error = a.Error, text = a.Text,
            });
            lock (_lock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"perception-{now:yyyy-MM-dd}.jsonl"), line + "\n");
            }
        }
        catch (Exception) { /* telemetry never breaks the bot */ }
    }

    /// <summary>"perception: 12 calls (3 auto, 4 cached), p50 1.8s, halluc 9%, $0.021".</summary>
    public string Summary()
    {
        lock (_lock)
            return string.Create(CultureInfo.InvariantCulture,
                $"perception: {Calls} calls ({Auto} auto, {CacheHits} cached, {Skipped} skipped, {Errors} failed), p50 {(_latency.Count == 0 ? 0 : _latency.Order().ElementAt(_latency.Count / 2)) / 1000.0:0.0}s, " +
                $"halluc {(Checked == 0 ? 0 : (double)Mismatched / Checked):P0} ({Mismatched}/{Checked}), {Dropped} dropped, {Corrected} fixed, ${Usd:0.000}");
    }
}
