using System.Diagnostics;
using System.Globalization;
using MineClaw.Agent.Budget;
using MineClaw.Raptor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Rag.Raptor;

public enum RaptorJob { None, Sync, Refresh, Rebuild }

public sealed record RaptorStepReport(RaptorSyncReport Sync, RefreshReport? Refresh, BuildStats? Rebuild, decimal CostUsd, string? Skipped);

/// <summary>
/// Background RAPTOR maintenance, off the game loop and cancellable:
/// <list type="bullet">
/// <item>every <see cref="RaptorMemoryOptions.PollSeconds"/> (or when signalled): incremental insert/remove (zero tokens);</item>
/// <item><c>RefreshDirty</c> on the cheap tier after <see cref="RaptorMemoryOptions.RefreshEveryLeaves"/> new leaves or
/// <see cref="RaptorMemoryOptions.RefreshEveryMinutes"/> minutes, capped per job and by the daily summary budget;</item>
/// <item>a full rebuild when <see cref="RaptorIndex.ShouldRebuild"/> fires, only off-peak (or while the tree is small).</item>
/// </list>
/// Summary jobs are skipped while the global budget is in CheapOnly/Paused. Each job logs its cost. The daily spend is
/// kept in <c>meta('raptor_spend')</c>.
/// </summary>
public sealed class RaptorScheduler : IDisposable
{
    public const string SpendMetaKey = "raptor_spend";

    private readonly RaptorMemory _memory;
    private readonly HaikuSummarizer _summarizer;
    private readonly RaptorMemoryOptions _o;
    private readonly AgentDb _db;
    private readonly UsageTracker? _usage;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _step = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private DateTimeOffset _lastRefresh;
    private int _leavesSinceRefresh;

    /// <param name="summarizer">The tree's summarizer: prices the job budgets and hard-stops calls once a job's cap is spent.</param>
    public RaptorScheduler(RaptorMemory memory, HaikuSummarizer summarizer, AgentDb db, UsageTracker? usage = null,
        TimeProvider? time = null, ILogger? log = null)
    {
        _memory = memory;
        _summarizer = summarizer;
        _o = memory.Options;
        _db = db;
        _usage = usage;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _lastRefresh = _time.GetUtcNow();
    }

    public int LeavesSinceRefresh => _leavesSinceRefresh;
    public bool Running => _loop is { IsCompleted: false };

    /// <summary>Loads the tree and starts the loop in the background (returns immediately).</summary>
    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Wakes the loop early (e.g. after a burst of new episodes).</summary>
    public void Signal() => _signal.Release();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!_memory.Ready) await _memory.StartLoading(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { _log.LogWarning(ex, "raptor tree load failed; starting empty"); }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StepAsync(ct).ConfigureAwait(false);
                Errors.Recovered(_log);
                await _signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _o.PollSeconds)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Errors.Failed(_log, ex);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _o.PollSeconds)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>The maintenance loop's error log (one warning per distinct failure, then a count when it recovers).</summary>
    internal RepeatedErrorLog Errors { get; } = new("raptor maintenance");

    // ======================= budget =======================

    private string Today => _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Summary spend recorded today (local day).</summary>
    public decimal SpentToday
    {
        get
        {
            var v = _db.GetMeta(SpendMetaKey);
            if (v == null) return 0;
            var parts = v.Split('|');
            return parts.Length == 2 && parts[0] == Today && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
    }

    public decimal RemainingToday => Math.Max(0, _o.DailySummaryBudgetUsd - SpentToday);

    private void AddSpend(decimal usd)
    {
        if (usd <= 0) return;
        _db.SetMeta(SpendMetaKey, $"{Today}|{(SpentToday + usd).ToString(CultureInfo.InvariantCulture)}");
    }

    private bool OffPeak
    {
        get
        {
            int h = _time.GetLocalNow().Hour, s = _o.OffPeakStartHour, e = _o.OffPeakEndHour;
            return s == e || (s < e ? h >= s && h < e : h >= s || h < e);
        }
    }

    private string? SummaryGate()
    {
        if (_usage?.Mode is BudgetMode.CheapOnly or BudgetMode.Paused) return "global budget " + _usage.Mode;
        if (RemainingToday <= 0) return "daily summary budget spent";
        return null;
    }

    // ======================= one step =======================

    /// <summary>
    /// One maintenance step (sync, then at most one summary job). Public for tests and the console; serialized with the
    /// background loop.
    /// </summary>
    public async Task<RaptorStepReport> StepAsync(CancellationToken ct = default, bool forceRefresh = false, bool forceRebuild = false)
    {
        await _step.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sync = await _memory.SyncAsync(ct).ConfigureAwait(false);
            _leavesSinceRefresh += sync.Inserted + sync.Reinserted;
            if (sync.Any) _log.LogDebug("raptor sync: +{Inserted} ~{Reinserted} -{Removed}", sync.Inserted, sync.Reinserted, sync.Removed);
            var index = _memory.Index;
            var now = _time.GetUtcNow();

            var decision = index.ShouldRebuild();
            bool small = index.LeafCount < _o.RebuildAnytimeBelowLeaves;
            bool wantRebuild = forceRebuild || (decision.Rebuild && (OffPeak || small));
            int dirty = index.DirtyCount;
            bool wantRefresh = forceRefresh || (dirty > 0 && (_leavesSinceRefresh >= Math.Max(1, _o.RefreshEveryLeaves)
                || now - _lastRefresh >= TimeSpan.FromMinutes(Math.Max(1, _o.RefreshEveryMinutes))));
            if (!wantRebuild && !wantRefresh) return new RaptorStepReport(sync, null, null, 0, null);

            if (SummaryGate() is { } why)
            {
                _log.LogDebug("raptor {Job} skipped: {Why}", wantRebuild ? "rebuild" : "refresh", why);
                return new RaptorStepReport(sync, null, null, 0, why);
            }

            var sw = Stopwatch.StartNew();
            decimal before = _summarizer.CostUsd;
            if (wantRebuild)
            {
                decimal cap = Math.Min(_o.RebuildMaxUsd, RemainingToday);
                var budget = _summarizer.Budget(cap);
                _summarizer.JobLimitUsd = before + cap;
                BuildStats stats;
                try { stats = await index.RebuildAsync(budget, ct).ConfigureAwait(false); }
                finally { _summarizer.JobLimitUsd = null; AddSpend(_summarizer.CostUsd - before); }
                decimal cost = _summarizer.CostUsd - before;
                _leavesSinceRefresh = 0;
                _lastRefresh = now;
                _log.LogInformation(
                    "raptor rebuild ({Reason}): {Leaves} leaves, layers [{Layers}], {Summaries} summaries ({Reused} reused, {Pending} pending), " +
                    "${Cost:F4} ({In}+{Out} tok), {Embed} embed tok, {Ms} ms",
                    forceRebuild ? "forced" : decision.Reason, stats.Leaves, string.Join(",", stats.NodesPerLayer), stats.Summaries, stats.ReusedSummaries,
                    stats.Placeholders, cost, budget.InputTokens, budget.OutputTokens, stats.EmbeddingTokens, sw.ElapsedMilliseconds);
                return new RaptorStepReport(sync, null, stats, cost, null);
            }
            else
            {
                decimal cap = Math.Min(_o.RefreshMaxUsd, RemainingToday);
                var budget = _summarizer.Budget(cap);
                _summarizer.JobLimitUsd = before + cap;
                RefreshReport report;
                try { report = await index.RefreshDirtyAsync(budget, ct).ConfigureAwait(false); }
                finally { _summarizer.JobLimitUsd = null; AddSpend(_summarizer.CostUsd - before); }
                decimal cost = _summarizer.CostUsd - before;
                _leavesSinceRefresh = 0;
                _lastRefresh = now;
                _log.LogInformation("raptor refresh: {Refreshed} refreshed, {Splits} splits, {Remaining} still dirty, ${Cost:F4} ({In}+{Out} tok), {Embed} embed tok, {Ms} ms",
                    report.Refreshed, report.Splits, report.RemainingDirty, cost, budget.InputTokens, budget.OutputTokens, report.EmbeddingTokens, sw.ElapsedMilliseconds);
                return new RaptorStepReport(sync, report, null, cost, null);
            }
        }
        finally { _step.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}

/// <summary>
/// Logs a background loop's failures once each: the first occurrence of a failure (same root exception type, message and
/// throwing frame) is a warning with the full stack and its root cause; repeats are counted at debug level only, and the
/// next success logs one "recovered after N failures" line. Live 10-29: the same FOREIGN KEY stack every 30 s for hours.
/// </summary>
internal sealed class RepeatedErrorLog(string what)
{
    private string? _last;
    private int _repeats;

    public int Repeats => _repeats;
    public string? Last => _last;

    /// <summary>A failure's identity: the innermost exception's type, message and the frame that threw it.</summary>
    public static string Signature(Exception ex)
    {
        var root = ex.GetBaseException();
        var frame = root.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Contains("MineClaw.", StringComparison.Ordinal)) ?? "";
        return $"{root.GetType().Name}: {root.Message} {frame}";
    }

    public void Failed(ILogger log, Exception ex)
    {
        var sig = Signature(ex);
        if (sig == _last)
        {
            _repeats++;
            log.LogDebug("{What} error again ({Count}x): {Root}", what, _repeats + 1, ex.GetBaseException().Message);
            return;
        }
        _last = sig;
        _repeats = 0;
        var root = ex.GetBaseException();
        log.LogWarning(ex, "{What} error (root cause {Type}: {Message}; repeats are logged once, at recovery)", what, root.GetType().Name, root.Message);
    }

    public void Recovered(ILogger log)
    {
        if (_last == null) return;
        log.LogInformation("{What} recovered after {Count} failure(s): {Last}", what, _repeats + 1, _last);
        _last = null;
        _repeats = 0;
    }
}
