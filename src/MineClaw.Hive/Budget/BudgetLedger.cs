using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Runtime;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Budget;

/// <summary>Result of a clone's spend-authorisation request.</summary>
public sealed record SpendGrant(bool Ok, decimal GrantUsd, BudgetMode Mode, string? Reason)
{
    public SpendGrantMsg ToMsg() => new(Ok, GrantUsd, Mode.ToString(), Reason);
}

/// <summary>
/// The hive's budget ledger, kept by the main process. The caps are the main bot's Agent:Budget caps, shared by the whole
/// hive (main + clones); each clone also has a sub-cap. Clones ask for authorisation before calls (granted in chunks,
/// "batches", of at least <see cref="HiveBudgetConfig.GrantUsd"/>), report actual spend after each call, and are refused
/// when their sub-cap or the hive's recall line would be crossed. Main's own <see cref="UsageTracker"/> adds the clones'
/// spend via <see cref="UsageTracker.ExternalSpend"/> so its mode reflects the hive total. Persisted (atomic JSON) per day.
/// Never calls back into the main tracker while holding its lock (the tracker calls <see cref="ClonesSpend"/> under its own).
/// </summary>
public sealed class BudgetLedger
{
    private sealed class Day
    {
        private Dictionary<string, decimal> _clones = new(StringComparer.OrdinalIgnoreCase);
        // JSON replaces the dictionary on load: re-wrap it so "Maid_Chan_67" and "maid_chan_67" stay one entry.
        public Dictionary<string, decimal> Clones
        {
            get => _clones;
            set => _clones = Merge(value);
        }

        private static Dictionary<string, decimal> Merge(Dictionary<string, decimal>? src)
        {
            var d = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (src != null) foreach (var (k, v) in src) d[k] = d.GetValueOrDefault(k) + v;
            return d;
        }
    }
    private sealed class State
    {
        public Dictionary<string, Day> Days { get; set; } = new();
        public Dictionary<string, Day> Months { get; set; } = new();
    }

    private readonly object _lock = new();
    private readonly BudgetOptions _caps;
    private readonly HiveBudgetConfig _o;
    private readonly Func<(decimal Today, decimal Month)> _mainSpend;
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly Dictionary<string, decimal> _outstanding = new(StringComparer.OrdinalIgnoreCase);
    private State _state;

    /// <param name="mainSpend">The main bot's own spend (today, month) — read OUTSIDE the ledger lock.</param>
    public BudgetLedger(BudgetOptions caps, HiveBudgetConfig options, Func<(decimal Today, decimal Month)> mainSpend, TimeProvider? time = null, string? path = null)
    {
        _caps = caps;
        _o = options;
        _mainSpend = mainSpend;
        _time = time ?? TimeProvider.System;
        _path = path;
        _state = (path != null ? AtomicFile.ReadJson<State>(path) : null) ?? new State();
    }

    public HiveBudgetConfig Options => _o;

    /// <summary>Raised after recorded spend (supervisor re-checks the recall line).</summary>
    public event Action? Spent;

    private string DayKey => _time.GetLocalNow().ToString("yyyy-MM-dd");
    private string MonthKey => _time.GetLocalNow().ToString("yyyy-MM");

    private static Day Get(Dictionary<string, Day> map, string k) => map.TryGetValue(k, out var d) ? d : map[k] = new Day();

    /// <summary>Total spend of all clones (today, month). Safe to call from the main tracker's lock.</summary>
    public (decimal Today, decimal Month) ClonesSpend()
    {
        lock (_lock) return (Get(_state.Days, DayKey).Clones.Values.Sum(), Get(_state.Months, MonthKey).Clones.Values.Sum());
    }

    public (decimal Today, decimal Month) CloneSpend(string clone)
    {
        lock (_lock) return (Get(_state.Days, DayKey).Clones.GetValueOrDefault(clone), Get(_state.Months, MonthKey).Clones.GetValueOrDefault(clone));
    }

    /// <summary>Hive totals (main + clones).</summary>
    public (decimal Today, decimal Month) HiveSpend()
    {
        var (md, mm) = _mainSpend();
        var (cd, cm) = ClonesSpend();
        return (md + cd, mm + cm);
    }

    /// <summary>Mode of the whole hive by the shared caps (same thresholds as <see cref="UsageTracker"/>).</summary>
    public BudgetMode HiveMode()
    {
        var (d, m) = HiveSpend();
        decimal Frac(double f) => (decimal)f;
        bool Past(decimal f) => (_caps.MonthlyCapUsd > 0 && m >= _caps.MonthlyCapUsd * f) || (_caps.DailyCapUsd > 0 && d >= _caps.DailyCapUsd * f);
        if (Past(1)) return BudgetMode.Paused;
        if (_caps.CheapOnlyFraction > 0 && Past(Frac(_caps.CheapOnlyFraction))) return BudgetMode.CheapOnly; // 0 = no cheap-only stage
        if (_caps.WorkerBrainFraction > 0 && Past(Frac(_caps.WorkerBrainFraction))) return BudgetMode.Economy;
        return BudgetMode.Normal;
    }

    /// <summary>True when hive spend (plus outstanding grants) is at the recall line: no clones should run.</summary>
    public bool AtRecallLine(out string reason)
    {
        var (d, m) = HiveSpend();
        decimal outstanding;
        lock (_lock) outstanding = _outstanding.Values.Sum();
        var f = (decimal)_o.RecallFraction;
        if (_caps.DailyCapUsd > 0 && d + outstanding >= _caps.DailyCapUsd * f) { reason = $"hive spend ${d:0.00} at the recall line (${_caps.DailyCapUsd * f:0.00} today)"; return true; }
        if (_caps.MonthlyCapUsd > 0 && m + outstanding >= _caps.MonthlyCapUsd * f) { reason = $"hive spend ${m:0.00} at the recall line (${_caps.MonthlyCapUsd * f:0.00} this month)"; return true; }
        var mode = HiveMode();
        if (mode != BudgetMode.Normal) { reason = "hive budget mode " + mode; return true; }
        reason = "ok";
        return false;
    }

    /// <summary>A clone asks to spend about <paramref name="estimateUsd"/>; granted in chunks so not every call needs a round trip.</summary>
    public SpendGrant Authorize(string clone, decimal estimateUsd)
    {
        var (hd, hm) = HiveSpend(); // outside the lock (reads the main tracker)
        var mode = HiveMode();
        lock (_lock)
        {
            var grant = Math.Max(estimateUsd, _o.GrantUsd);
            var cd = Get(_state.Days, DayKey).Clones.GetValueOrDefault(clone);
            var cm = Get(_state.Months, MonthKey).Clones.GetValueOrDefault(clone);
            var mine = _outstanding.GetValueOrDefault(clone);
            var all = _outstanding.Values.Sum();
            if (_o.CloneDailyCapUsd > 0 && cd + mine + estimateUsd > _o.CloneDailyCapUsd)
                return new SpendGrant(false, 0, mode, $"clone daily sub-cap ${_o.CloneDailyCapUsd:0.00} reached");
            if (_o.CloneMonthlyCapUsd > 0 && cm + mine + estimateUsd > _o.CloneMonthlyCapUsd)
                return new SpendGrant(false, 0, mode, $"clone monthly sub-cap ${_o.CloneMonthlyCapUsd:0.00} reached");
            if (mode != BudgetMode.Normal) return new SpendGrant(false, 0, mode, "hive budget mode " + mode);
            var f = (decimal)_o.RecallFraction;
            if (_caps.DailyCapUsd > 0 && hd + all + estimateUsd > _caps.DailyCapUsd * f)
                return new SpendGrant(false, 0, mode, "hive daily budget at the recall line");
            if (_caps.MonthlyCapUsd > 0 && hm + all + estimateUsd > _caps.MonthlyCapUsd * f)
                return new SpendGrant(false, 0, mode, "hive monthly budget at the recall line");
            // Clip the chunk so outstanding grants never cross the sub-cap / recall line.
            if (_o.CloneDailyCapUsd > 0) grant = Math.Min(grant, _o.CloneDailyCapUsd - cd - mine);
            if (_caps.DailyCapUsd > 0) grant = Math.Min(grant, _caps.DailyCapUsd * f - hd - all);
            grant = Math.Max(grant, estimateUsd);
            _outstanding[clone] = mine + grant;
            return new SpendGrant(true, grant, mode, null);
        }
    }

    /// <summary>Actual spend of one finished clone call (consumes its outstanding grant).</summary>
    public void Record(string clone, decimal usd)
    {
        if (usd <= 0) return;
        lock (_lock)
        {
            var d = Get(_state.Days, DayKey);
            d.Clones[clone] = d.Clones.GetValueOrDefault(clone) + usd;
            var m = Get(_state.Months, MonthKey);
            m.Clones[clone] = m.Clones.GetValueOrDefault(clone) + usd;
            _outstanding[clone] = Math.Max(0, _outstanding.GetValueOrDefault(clone) - usd);
            if (_state.Days.Count > 62) foreach (var k in _state.Days.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(_state.Days.Count - 62).ToList()) _state.Days.Remove(k);
            if (_path != null) { try { AtomicFile.WriteJson(_path, _state); } catch (IOException) { } }
        }
        Spent?.Invoke();
    }

    /// <summary>A clone logged out or died: its unspent grant is returned.</summary>
    public void Close(string clone) { lock (_lock) _outstanding.Remove(clone); }

    public string Summary()
    {
        var (d, m) = HiveSpend();
        var (cd, _) = ClonesSpend();
        return $"hive today ${d:0.000}/{_caps.DailyCapUsd:0.00} (clones ${cd:0.000}), month ${m:0.00}/{_caps.MonthlyCapUsd:0.00}, mode {HiveMode()}";
    }

    /// <summary>Estimate used to size an authorisation request.</summary>
    public static decimal Estimate(HiveBudgetConfig o, ModelTier tier) => tier switch
    {
        ModelTier.Main => o.EstimateMainUsd,
        ModelTier.Worker => o.EstimateWorkerUsd,
        _ => o.EstimateCheapUsd,
    };
}

/// <summary>Raised inside a clone when the hive ledger refused a call.</summary>
public sealed class HiveBudgetDeniedException(string reason) : Exception("hive budget: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Clone side: wraps the LLM client so every call is authorised by the main process's ledger first (one round trip per
/// grant chunk, not per call) and its actual cost is reported afterwards. A refusal throws
/// <see cref="HiveBudgetDeniedException"/> and raises <see cref="Denied"/> (the clone host then deposits and logs out).
/// </summary>
public sealed class GatedLlmClient(ILlmClient inner, PriceTable prices, HiveBudgetConfig options, Func<HiveLink?> link) : ILlmClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private decimal _remaining;

    public event Action<string>? Denied;
    public decimal Remaining => _remaining;
    public decimal Spent { get; private set; }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var estimate = BudgetLedger.Estimate(options, request.Tier);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_remaining < estimate)
            {
                var l = link();
                if (l is not { IsOpen: true }) { Denied?.Invoke("main process gone"); throw new HiveBudgetDeniedException("main process gone"); }
                SpendGrantMsg? g;
                try { g = await l.RequestAsync<SpendGrantMsg>(Msg.SpendAuth, new SpendAuthMsg(estimate, request.Tier.ToString()), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
                catch (HiveIpcException ex) { Denied?.Invoke(ex.Message); throw new HiveBudgetDeniedException(ex.Message); }
                if (g is not { Ok: true }) { var why = g?.Reason ?? "refused"; Denied?.Invoke(why); throw new HiveBudgetDeniedException(why); }
                _remaining += g.GrantUsd;
            }
        }
        finally { _gate.Release(); }

        var resp = await inner.CompleteAsync(request, ct).ConfigureAwait(false);
        var cost = prices.Cost(resp.Model, resp.Usage);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { _remaining -= cost; Spent += cost; }
        finally { _gate.Release(); }
        if (link() is { IsOpen: true } lk)
            await lk.SendAsync(Msg.Spend, new SpendMsg(cost, request.Tier.ToString(), resp.Model), CancellationToken.None).ConfigureAwait(false);
        return resp;
    }
}
