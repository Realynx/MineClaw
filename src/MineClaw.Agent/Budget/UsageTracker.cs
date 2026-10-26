using MineClaw.Agent.Llm;
using MineClaw.Agent.Runtime;

namespace MineClaw.Agent.Budget;

public enum BudgetMode
{
    /// <summary>Both tiers, all tools.</summary>
    Normal,
    /// <summary>Cheap tier only, chat only (no escalation, no game tools).</summary>
    CheapOnly,
    /// <summary>No model calls at all until the window rolls over.</summary>
    Paused,
}

public sealed class BudgetOptions
{
    /// <summary>Hard daily cap (USD). Reaching it pauses the agent until local midnight.</summary>
    public decimal DailyCapUsd { get; set; } = 10.00m;
    /// <summary>Hard monthly cap (USD). Reaching it pauses the agent until the 1st.</summary>
    public decimal MonthlyCapUsd { get; set; } = 190.00m;
    /// <summary>Fraction of either cap after which the agent drops to cheap-tier chat only.</summary>
    public double CheapOnlyFraction { get; set; } = 0.8;
    /// <summary>Max model calls in any rolling hour (pauses until the window frees up).</summary>
    public int MaxCallsPerHour { get; set; } = 90;
    /// <summary>File name (under AgentOptions.DataDirectory) for persisted daily/monthly totals.</summary>
    public string UsageFile { get; set; } = "usage.json";
}

public sealed class UsageTotals
{
    public decimal Usd { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }

    internal void Add(decimal usd, LlmUsage u)
    {
        Usd += usd;
        Calls++;
        InputTokens += u.InputTokens;
        OutputTokens += u.OutputTokens;
        CacheReadTokens += u.CacheReadTokens;
        CacheWriteTokens += u.CacheWriteTokens;
    }
}

/// <summary>
/// Prices every call from <see cref="PriceTable"/>, persists daily/monthly totals (atomic JSON), and derives the
/// <see cref="BudgetMode"/> from hard caps and a rolling per-hour call cap. Thread-safe.
/// </summary>
public sealed class UsageTracker
{
    private sealed class State
    {
        public Dictionary<string, UsageTotals> Days { get; set; } = new();
        public Dictionary<string, UsageTotals> Months { get; set; } = new();
    }

    private readonly BudgetOptions _options;
    private readonly PriceTable _prices;
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _recentCalls = new();
    private State _state;
    private BudgetMode _lastMode = BudgetMode.Normal;

    /// <param name="path">Persistence file; null keeps totals in memory only (tests).</param>
    public UsageTracker(BudgetOptions options, PriceTable? prices = null, TimeProvider? time = null, string? path = "")
    {
        _options = options;
        _prices = prices ?? new PriceTable();
        _time = time ?? TimeProvider.System;
        _path = path == "" ? options.UsageFile : path;
        _state = (_path != null ? AtomicFile.ReadJson<State>(_path) : null) ?? new State();
    }

    /// <summary>Raised when the mode changes (new mode, human-readable reason) — used for the in-game notice.</summary>
    public event Action<BudgetMode, string>? ModeChanged;

    public PriceTable Prices => _prices;

    private DateTimeOffset Now => _time.GetLocalNow();
    private string DayKey => Now.ToString("yyyy-MM-dd");
    private string MonthKey => Now.ToString("yyyy-MM");

    public UsageTotals Today { get { lock (_lock) return Get(_state.Days, DayKey); } }
    public UsageTotals ThisMonth { get { lock (_lock) return Get(_state.Months, MonthKey); } }

    public int CallsLastHour
    {
        get { lock (_lock) { Prune(); return _recentCalls.Count; } }
    }

    /// <summary>Records one API call and returns its estimated cost in USD.</summary>
    public decimal Record(string model, LlmUsage usage)
    {
        decimal cost = _prices.Cost(model, usage);
        lock (_lock)
        {
            Get(_state.Days, DayKey).Add(cost, usage);
            Get(_state.Months, MonthKey).Add(cost, usage);
            _recentCalls.Enqueue(Now);
            TrimOldDays();
            if (_path != null)
            {
                try { AtomicFile.WriteJson(_path, _state); } catch (IOException) { /* keep going; retried next call */ }
            }
        }
        CheckMode();
        return cost;
    }

    /// <summary>Current mode; also fires <see cref="ModeChanged"/> on transitions (day rollover, caps).</summary>
    public BudgetMode CheckMode()
    {
        BudgetMode mode;
        string reason;
        bool changed;
        lock (_lock)
        {
            (mode, reason) = Evaluate();
            changed = mode != _lastMode;
            _lastMode = mode;
        }
        if (changed) ModeChanged?.Invoke(mode, reason);
        return mode;
    }

    public BudgetMode Mode { get { lock (_lock) return Evaluate().Mode; } }

    public string Reason { get { lock (_lock) return Evaluate().Reason; } }

    public string Summary()
    {
        lock (_lock)
        {
            var d = Get(_state.Days, DayKey);
            var m = Get(_state.Months, MonthKey);
            Prune();
            return $"today ${d.Usd:0.000}/{_options.DailyCapUsd:0.00} ({d.Calls} calls), month ${m.Usd:0.00}/{_options.MonthlyCapUsd:0.00}, {_recentCalls.Count} calls/h";
        }
    }

    private (BudgetMode Mode, string Reason) Evaluate()
    {
        var d = Get(_state.Days, DayKey).Usd;
        var m = Get(_state.Months, MonthKey).Usd;
        Prune();
        if (_options.MonthlyCapUsd > 0 && m >= _options.MonthlyCapUsd) return (BudgetMode.Paused, "monthly budget used up");
        if (_options.DailyCapUsd > 0 && d >= _options.DailyCapUsd) return (BudgetMode.Paused, "daily budget used up");
        if (_options.MaxCallsPerHour > 0 && _recentCalls.Count >= _options.MaxCallsPerHour) return (BudgetMode.Paused, "hourly call limit hit");
        var f = (decimal)_options.CheapOnlyFraction;
        if (_options.MonthlyCapUsd > 0 && m >= _options.MonthlyCapUsd * f) return (BudgetMode.CheapOnly, "monthly budget nearly used");
        if (_options.DailyCapUsd > 0 && d >= _options.DailyCapUsd * f) return (BudgetMode.CheapOnly, "daily budget nearly used");
        return (BudgetMode.Normal, "ok");
    }

    private void Prune()
    {
        var cutoff = Now - TimeSpan.FromHours(1);
        while (_recentCalls.Count > 0 && _recentCalls.Peek() <= cutoff) _recentCalls.Dequeue();
    }

    private void TrimOldDays()
    {
        if (_state.Days.Count <= 62) return;
        foreach (var k in _state.Days.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(_state.Days.Count - 62).ToList())
            _state.Days.Remove(k);
    }

    private static UsageTotals Get(Dictionary<string, UsageTotals> map, string key)
    {
        if (!map.TryGetValue(key, out var t)) map[key] = t = new UsageTotals();
        return t;
    }
}
