using MineClaw.Agent.Llm;
using MineClaw.Agent.Runtime;

namespace MineClaw.Agent.Budget;

/// <summary>
/// Budget fallback order (spend = today's or this month's total across ALL tiers, checked against each cap):
/// <list type="number">
/// <item><see cref="Normal"/>: Opus brain, Sonnet worker for delegated tasks, Haiku memory.</item>
/// <item><see cref="Economy"/> (≥ <see cref="BudgetOptions.WorkerBrainFraction"/> of a cap): the brain's turns run on the
/// worker model (same tools; delegation and ask_memory still work).</item>
/// <item><see cref="CheapOnly"/> (≥ <see cref="BudgetOptions.CheapOnlyFraction"/>): Haiku chat only — no game actions, no
/// delegation; running worker tasks stop at their next round.</item>
/// <item><see cref="Paused"/> (≥ a cap, or the hourly call cap): no model calls at all.</item>
/// </list>
/// </summary>
public enum BudgetMode
{
    /// <summary>All tiers, all tools.</summary>
    Normal,
    /// <summary>Cheap tier only, chat only (no escalation, no game tools).</summary>
    CheapOnly,
    /// <summary>No model calls at all until the window rolls over.</summary>
    Paused,
    /// <summary>First fallback: brain turns run on the worker model instead of the main (Opus) model.</summary>
    Economy,
}

public sealed class BudgetOptions
{
    /// <summary>Hard daily cap (USD). Reaching it pauses the agent until local midnight.</summary>
    public decimal DailyCapUsd { get; set; } = 10.00m;
    /// <summary>Hard monthly cap (USD). Reaching it pauses the agent until the 1st.</summary>
    public decimal MonthlyCapUsd { get; set; } = 190.00m;
    /// <summary>Fraction of either cap after which the brain runs on the worker model (first fallback; 0 = skip).</summary>
    public double WorkerBrainFraction { get; set; } = 0.6;
    /// <summary>Fraction of either cap after which the agent drops to cheap-tier chat only (second fallback).</summary>
    public double CheapOnlyFraction { get; set; } = 0.8;
    /// <summary>Max model calls in any rolling hour (pauses until the window frees up).</summary>
    public int MaxCallsPerHour { get; set; } = 90;
    /// <summary>File name (under AgentOptions.DataDirectory) for persisted daily/monthly totals.</summary>
    public string UsageFile { get; set; } = "usage.json";
}

/// <summary>Totals for one tier (opus / worker / memory).</summary>
public sealed class TierTotals
{
    public decimal Usd { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public double CacheHitRatio
    {
        get
        {
            long total = InputTokens + CacheReadTokens + CacheWriteTokens;
            return total == 0 ? 0 : (double)CacheReadTokens / total;
        }
    }

    internal TierTotals Clone() => (TierTotals)MemberwiseClone();

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

/// <summary>Per-purpose daily rollup (from prompt telemetry).</summary>
public sealed record PurposeTotals
{
    public int Calls { get; set; }
    public decimal Usd { get; set; }
    public long PromptTokens { get; set; }
    public long OutputTokens { get; set; }
    public long LatencyMs { get; set; }
}

public sealed class UsageTotals
{
    public decimal Usd { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }

    /// <summary>Per-tier breakdown keyed by <see cref="UsageTracker.TierLabel"/> ("opus", "worker", "memory"; "other" = unknown model).</summary>
    public Dictionary<string, TierTotals> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Totals for one tier label (empty totals if none yet).</summary>
    public TierTotals Tier(string label) => Tiers.TryGetValue(label, out var t) ? t : new TierTotals();

    public TierTotals Tier(ModelTier tier) => Tier(UsageTracker.TierLabel(tier));

    /// <summary>Embedding spend is a separate line item (it still counts toward the caps).</summary>
    public decimal EmbeddingUsd { get; set; }
    public long EmbeddingTokens { get; set; }
    public int EmbeddingCalls { get; set; }

    /// <summary>Fraction of prompt tokens served from cache: cache_read / (input + cache_read + cache_write).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double CacheHitRatio
    {
        get
        {
            long total = InputTokens + CacheReadTokens + CacheWriteTokens;
            return total == 0 ? 0 : (double)CacheReadTokens / total;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public decimal TotalUsd => Usd + EmbeddingUsd;

    /// <summary>Daily telemetry rollup: per call purpose (chat_turn, delegate_round, ask_memory, compaction, ...).</summary>
    public Dictionary<string, PurposeTotals>? Purposes { get; set; }

    /// <summary>Daily telemetry rollup: calibrated prompt tokens per request section (tools, system, history, memory, ...).</summary>
    public Dictionary<string, long>? Sections { get; set; }

    internal UsageTotals Copy()
    {
        var c = (UsageTotals)MemberwiseClone();
        c.Tiers = Tiers?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        c.Purposes = Purposes?.ToDictionary(kv => kv.Key, kv => kv.Value with { }, StringComparer.OrdinalIgnoreCase);
        c.Sections = Sections != null ? new Dictionary<string, long>(Sections, StringComparer.OrdinalIgnoreCase) : null;
        return c;
    }

    internal void AddEmbedding(decimal usd, long tokens)
    {
        EmbeddingUsd += usd;
        EmbeddingTokens += tokens;
        EmbeddingCalls++;
    }

    internal void Add(decimal usd, LlmUsage u, string tier)
    {
        Usd += usd;
        Calls++;
        InputTokens += u.InputTokens;
        OutputTokens += u.OutputTokens;
        CacheReadTokens += u.CacheReadTokens;
        CacheWriteTokens += u.CacheWriteTokens;
        Tiers ??= new(StringComparer.OrdinalIgnoreCase);
        if (!Tiers.TryGetValue(tier, out var t)) Tiers[tier] = t = new TierTotals();
        t.Add(usd, u);
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
    private double _lastCacheHit;

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

    /// <summary>Maps a model id to its tier for the per-tier breakdown (the runtime sets it from <c>LlmOptions.TierOfModel</c>).</summary>
    public Func<string, ModelTier?>? Classify { get; set; }

    /// <summary>Report label of a tier: the brain is "opus", the builder "worker", the Haiku tier "memory".</summary>
    public static string TierLabel(ModelTier tier) => tier switch
    {
        ModelTier.Main => "opus",
        ModelTier.Worker => "worker",
        _ => "memory",
    };

    public static readonly string[] TierLabels = ["opus", "worker", "memory", "other"];

    private DateTimeOffset Now => _time.GetLocalNow();
    private string DayKey => Now.ToString("yyyy-MM-dd");
    private string MonthKey => Now.ToString("yyyy-MM");

    public UsageTotals Today { get { lock (_lock) return Get(_state.Days, DayKey); } }
    public UsageTotals ThisMonth { get { lock (_lock) return Get(_state.Months, MonthKey); } }

    public int CallsLastHour
    {
        get { lock (_lock) { Prune(); return _recentCalls.Count; } }
    }

    /// <summary>Cache hit ratio of the most recent call (see <see cref="UsageTotals.CacheHitRatio"/>).</summary>
    public double LastCacheHitRatio { get { lock (_lock) return _lastCacheHit; } }

    public static double CacheHit(LlmUsage u)
    {
        long total = u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens;
        return total == 0 ? 0 : (double)u.CacheReadTokens / total;
    }

    /// <summary>Records embedding tokens (separate line item, counted toward the caps). Returns the cost.</summary>
    public decimal RecordEmbedding(string model, long tokens, decimal pricePerMTok)
    {
        decimal cost = tokens * pricePerMTok / 1_000_000m;
        lock (_lock)
        {
            Get(_state.Days, DayKey).AddEmbedding(cost, tokens);
            Get(_state.Months, MonthKey).AddEmbedding(cost, tokens);
            if (_path != null)
            {
                try { AtomicFile.WriteJson(_path, _state); } catch (IOException) { }
            }
        }
        return cost;
    }

    /// <summary>
    /// Records one API call and returns its estimated cost in USD. The tier bucket is <see cref="Classify"/> applied to the
    /// model that actually answered (so a brain turn on the worker model in <see cref="BudgetMode.Economy"/> is worker spend),
    /// else <paramref name="tier"/>, else "other".
    /// </summary>
    public decimal Record(string model, LlmUsage usage, ModelTier? tier = null)
    {
        decimal cost = _prices.Cost(model, usage);
        var t = Classify?.Invoke(model) ?? tier;
        string label = t is { } tt ? TierLabel(tt) : "other";
        lock (_lock)
        {
            _lastCacheHit = CacheHit(usage);
            Get(_state.Days, DayKey).Add(cost, usage, label);
            Get(_state.Months, MonthKey).Add(cost, usage, label);
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

    /// <summary>
    /// Adds one call to today's telemetry rollup (<see cref="UsageTotals.Purposes"/>, <see cref="UsageTotals.Sections"/>).
    /// In memory only: persisted with the next <see cref="Record"/> (which follows every call). Spend and caps are unaffected.
    /// </summary>
    public void AddRollup(string purpose, decimal usd, long promptTokens, long outputTokens, long latencyMs, IReadOnlyDictionary<string, long>? sections)
    {
        lock (_lock)
        {
            var d = Get(_state.Days, DayKey);
            d.Purposes ??= new(StringComparer.OrdinalIgnoreCase);
            if (!d.Purposes.TryGetValue(purpose, out var p)) d.Purposes[purpose] = p = new PurposeTotals();
            p.Calls++;
            p.Usd += usd;
            p.PromptTokens += promptTokens;
            p.OutputTokens += outputTokens;
            p.LatencyMs += latencyMs;
            if (sections == null) return;
            d.Sections ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in sections) d.Sections[k] = d.Sections.GetValueOrDefault(k) + v;
        }
    }

    /// <summary>Deep copies of today's and this month's totals (safe to read from another thread).</summary>
    public (UsageTotals Today, UsageTotals Month) SnapshotTotals()
    {
        lock (_lock) return (Get(_state.Days, DayKey).Copy(), Get(_state.Months, MonthKey).Copy());
    }

    public BudgetOptions Options => _options;

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
            return $"today ${d.TotalUsd:0.000}/{_options.DailyCapUsd:0.00} ({d.Calls} calls, cache {d.CacheHitRatio:P0}" +
                   (d.EmbeddingTokens > 0 ? $", emb ${d.EmbeddingUsd:0.0000}" : "") +
                   $"), month ${m.TotalUsd:0.00}/{_options.MonthlyCapUsd:0.00}, {_recentCalls.Count} calls/h" +
                   (d.Tiers is { Count: > 0 } ? " | " + TierSummary(d) : "");
        }
    }

    /// <summary>"opus $0.041 3c cache 82% | worker $0.012 5c cache 71% | memory $0.000 1c cache 0%" (tiers with calls only).</summary>
    public static string TierSummary(UsageTotals totals) =>
        string.Join(" | ", TierLabels.Where(l => totals.Tiers?.ContainsKey(l) == true).Select(l =>
        {
            var t = totals.Tiers[l];
            return $"{l} ${t.Usd:0.000} {t.Calls}c cache {t.CacheHitRatio:P0}";
        }));

    private (BudgetMode Mode, string Reason) Evaluate()
    {
        var d = Get(_state.Days, DayKey).TotalUsd;
        var m = Get(_state.Months, MonthKey).TotalUsd;
        Prune();
        if (_options.MonthlyCapUsd > 0 && m >= _options.MonthlyCapUsd) return (BudgetMode.Paused, "monthly budget used up");
        if (_options.DailyCapUsd > 0 && d >= _options.DailyCapUsd) return (BudgetMode.Paused, "daily budget used up");
        if (_options.MaxCallsPerHour > 0 && _recentCalls.Count >= _options.MaxCallsPerHour) return (BudgetMode.Paused, "hourly call limit hit");
        var f = (decimal)_options.CheapOnlyFraction;
        if (_options.MonthlyCapUsd > 0 && m >= _options.MonthlyCapUsd * f) return (BudgetMode.CheapOnly, "monthly budget nearly used");
        if (_options.DailyCapUsd > 0 && d >= _options.DailyCapUsd * f) return (BudgetMode.CheapOnly, "daily budget nearly used");
        var w = (decimal)_options.WorkerBrainFraction;
        if (w > 0 && w < f)
        {
            if (_options.MonthlyCapUsd > 0 && m >= _options.MonthlyCapUsd * w) return (BudgetMode.Economy, "monthly budget past the economy mark");
            if (_options.DailyCapUsd > 0 && d >= _options.DailyCapUsd * w) return (BudgetMode.Economy, "daily budget past the economy mark");
        }
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
