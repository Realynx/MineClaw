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
    private readonly Queue<bool> _window = new();
    private bool _paused;

    /// <summary>Rolling hallucination guard: over the last <see cref="WindowSize"/> checked claims, a refuted share above
    /// <see cref="PauseRate"/> switches auto-vision to deterministic-only (explicit <c>see</c> still asks the model).</summary>
    public const int WindowSize = 10;
    public const double PauseRate = 0.40;

    /// <summary>Questions answered exactly from voxels (no model call).</summary>
    public int Exact { get; private set; }
    /// <summary>Model answers with nothing checkable (0 checked claims): low value, the question shape goes deterministic next time.</summary>
    public int NoClaims { get; private set; }

    /// <summary>Refuted share of the last <see cref="WindowSize"/> checked claims (0 until the window is full).</summary>
    public double RollingRate
    {
        get { lock (_lock) return _window.Count < WindowSize ? 0 : (double)_window.Count(b => b) / _window.Count; }
    }

    /// <summary>Auto-vision answers deterministically only while the rolling hallucination rate is above <see cref="PauseRate"/>.</summary>
    public bool DeterministicOnly { get { lock (_lock) return _paused; } }

    /// <summary>An exact (voxel) answer: zero tokens, never a hallucination.</summary>
    public void RecordExact(string source, string question)
    {
        lock (_lock) Exact++;
        log?.LogDebug("perception ({Source}) exact from voxels, no model call | {Q}", source, question);
    }

    private void Window(int checkedN, int mismatched)
    {
        // Called under _lock. Refuted claims first, then the good ones (order inside one answer doesn't matter).
        for (int i = 0; i < checkedN; i++)
        {
            _window.Enqueue(i < mismatched);
            while (_window.Count > WindowSize) _window.Dequeue();
        }
        double rate = _window.Count < WindowSize ? 0 : (double)_window.Count(b => b) / _window.Count;
        bool pause = _window.Count >= WindowSize && rate > PauseRate;
        if (pause == _paused) return;
        _paused = pause;
        if (pause)
            log?.LogWarning("perception: rolling hallucination rate {Rate:P0} over the last {N} checked claims > {Max:P0}: auto-vision is deterministic-only (see still asks the model)",
                rate, _window.Count, PauseRate);
        else log?.LogInformation("perception: rolling hallucination rate back to {Rate:P0}: auto-vision may ask the model again", rate);
    }

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
            lock (_lock)
            {
                if (a.Cached) CacheHits++;
                else Count(a, source, usd);
            }
            if (!a.Cached) Report(a, source, usd);
        }
        catch (Exception) { /* telemetry never breaks the bot */ }
    }

    /// <summary>Counts one model call (caller holds _lock).</summary>
    private void Count(VerifiedAnswer a, string source, decimal usd)
    {
        Calls++;
        if (a.Error != null) Errors++;
        if (source.StartsWith("auto", StringComparison.Ordinal)) Auto++;
        Checked += a.Checked;
        Mismatched += a.Mismatched;
        Dropped += a.Dropped;
        Corrected += a.Corrected;
        if (a.Error == null && a.Checked == 0) NoClaims++;
        if (a.Error == null) Window(a.Checked, a.Mismatched);
        if (a.Raw is { } r)
        {
            InputTokens += r.Usage.InputTokens + r.Usage.CacheReadTokens + r.Usage.CacheWriteTokens;
            OutputTokens += r.Usage.OutputTokens;
            _latency.Add(r.LatencyMs);
            if (_latency.Count > 500) _latency.RemoveAt(0);
        }
        Usd += usd;
    }

    /// <summary>The call's log line, and (with a directory) its jsonl record.</summary>
    private void Report(VerifiedAnswer a, string source, decimal usd)
    {
        var r = a.Raw;
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

    /// <summary>"perception: 12 calls (3 auto, 4 cached), p50 1.8s, halluc 9%, $0.021".</summary>
    public string Summary()
    {
        lock (_lock)
            return string.Create(CultureInfo.InvariantCulture,
                $"perception: {Calls} calls ({Auto} auto, {CacheHits} cached, {Skipped} skipped, {Errors} failed, {Exact} exact, {NoClaims} no-claim), p50 {(_latency.Count == 0 ? 0 : _latency.Order().ElementAt(_latency.Count / 2)) / 1000.0:0.0}s, " +
                $"halluc {(Checked == 0 ? 0 : (double)Mismatched / Checked):P0} ({Mismatched}/{Checked}), {Dropped} dropped, {Corrected} fixed, ${Usd:0.000}");
    }
}
