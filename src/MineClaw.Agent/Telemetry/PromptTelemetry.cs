using System.Diagnostics;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Telemetry;

/// <summary>Prompt telemetry settings (host section "Telemetry").</summary>
public sealed class TelemetryOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>JSONL directory (null/empty = the host's log directory).</summary>
    public string? Directory { get; set; }
    /// <summary>Delete usage-*.jsonl files older than this many days (0 = keep all).</summary>
    public int KeepDays { get; set; } = 30;
    /// <summary>Records kept in memory for the TUI.</summary>
    public int RecentRecords { get; set; } = 200;
    public ThoughtCaps Thought { get; set; } = new();
}

/// <summary>
/// Per-bot telemetry hub: turns every LLM call into a <see cref="PromptUsageRecord"/> (via <see cref="Wrap"/>), writes
/// JSONL, adds the daily rollup to the <see cref="UsageTracker"/>, keeps recent records + calls/hour buckets and the
/// brain's <see cref="ThoughtTracker"/>, and raises <see cref="Recorded"/> as the in-process stream for the TUI.
/// </summary>
public sealed class PromptTelemetry : IDisposable
{
    private readonly AgentOptions _agent;
    private readonly TelemetryOptions _o;
    private readonly TimeProvider _time;
    private readonly UsageJsonlWriter? _writer;
    private readonly object _lock = new();
    private readonly LinkedList<PromptUsageRecord> _recent = new();
    private readonly Dictionary<long, int> _hours = new();
    private readonly List<TurnInput> _turns = new();
    private UsageTracker? _usage;
    private int _compactions;

    /// <param name="directory">JSONL directory (null = no files; in-memory only).</param>
    public PromptTelemetry(AgentOptions agent, TelemetryOptions? options = null, string? directory = null, TimeProvider? time = null)
    {
        _agent = agent;
        _o = options ?? new TelemetryOptions();
        _time = time ?? TimeProvider.System;
        Bot = agent.Chat.Username;
        Thoughts = new ThoughtTracker(_o.Thought, _time);
        if (directory != null && _o.Enabled) _writer = new UsageJsonlWriter(directory, _o.KeepDays, _time);
    }

    public string Bot { get; set; }
    /// <summary>
    /// The body in scope on the calling flow (the host sets it to <c>BodyScope.Current?.Name</c>; the Agent layer can't see
    /// bodies), recorded as <see cref="PromptUsageRecord.Body"/>. Null or returning null = brain-level.
    /// </summary>
    public Func<string?>? CurrentBody { get; set; }
    public ThoughtTracker Thoughts { get; }
    public string? Directory => _writer?.Directory;

    /// <summary>Every finished call (on the calling thread; handlers must be quick and must not throw into the agent).</summary>
    public event Action<PromptUsageRecord>? Recorded;

    /// <summary>Wraps a client so every call is recorded.</summary>
    public ILlmClient Wrap(ILlmClient inner) => new TelemetryLlmClient(inner, this);

    /// <summary>Hooks the runtime (turn input, compaction, commits, worker results, task text, usage rollup).</summary>
    public void Attach(AgentRuntime rt)
    {
        _usage = rt.Usage;
        Thoughts.TaskProvider = rt.Activities.Describe;
        rt.Agent.TurnBuilt += OnTurn;
        rt.Context.Compacted += (_, journal) => { Interlocked.Increment(ref _compactions); Thoughts.OnCompacted(journal); };
        rt.Context.Committed += e => Thoughts.OnCommitted(e.Messages);
        if (rt.Worker != null) rt.Worker.Finished += o => Thoughts.OnWorkerFinished(o.Ok, o.Summary, o.Calls);
        // Chat → reaction / first move / first action: p50s for the TUI, one latency-*.jsonl line per chat next to usage.
        Latency = rt.Latency;
        if (_writer != null)
        {
            var lw = new LatencyJsonlWriter(_writer.Directory);
            rt.Latency.Recorded += lw.Write;
        }
    }

    /// <summary>The runtime's chat-latency tracker (null until <see cref="Attach"/>).</summary>
    public LatencyTracker? Latency { get; private set; }

    /// <summary>Usage tracker that receives the daily rollup (set by <see cref="Attach"/>; hosts without a runtime set it directly).</summary>
    public void AttachUsage(UsageTracker usage) => _usage = usage;

    public void OnTurn(TurnInput t)
    {
        lock (_lock)
        {
            _turns.Add(t);
            if (_turns.Count > 4) _turns.RemoveAt(0);
        }
        Thoughts.OnTurn(t);
    }

    public PromptUsageRecord? Last { get { lock (_lock) return _recent.Last?.Value; } }

    public IReadOnlyList<PromptUsageRecord> Recent() { lock (_lock) return _recent.ToArray(); }

    /// <summary>Calls per clock hour for the last <paramref name="hours"/> hours, oldest first (last = current hour).</summary>
    public int[] CallsPerHour(int hours = 24)
    {
        long now = HourOf(_time.GetUtcNow());
        var a = new int[Math.Max(1, hours)];
        lock (_lock)
            for (int i = 0; i < a.Length; i++) a[i] = _hours.GetValueOrDefault(now - (a.Length - 1 - i));
        return a;
    }

    /// <summary>Loads today's and yesterday's JSONL (this bot only) so the sparkline and "last call" survive a restart.</summary>
    public int SeedFromLogs()
    {
        if (_writer == null) return 0;
        var today = _time.GetLocalNow();
        var records = _writer.Read(today.AddDays(-1)).Concat(_writer.Read(today))
            .Where(r => string.Equals(r.Bot, Bot, StringComparison.OrdinalIgnoreCase)).ToList();
        lock (_lock) foreach (var r in records) Remember(r);
        return records.Count;
    }

    internal void OnBefore(LlmRequest request)
    {
        var purpose = PromptPurpose.Normalize(request.Purpose);
        if (purpose == PromptPurpose.ChatTurn) Thoughts.OnBrainRequest(request);
        else if (purpose == PromptPurpose.DelegateRound)
            Thoughts.OnWorkerRound(request.Messages.Count(m => m.Role == LlmRole.Assistant), request);
    }

    internal PromptUsageRecord OnAfter(LlmRequest request, LlmResponse? response, Exception? error, long latencyMs)
    {
        var purpose = PromptPurpose.Normalize(request.Purpose);
        TurnInput? turn = purpose == PromptPurpose.ChatTurn ? FindTurn(request) : null;
        var (est, round) = PromptSections.Estimate(request, turn);
        string model = response?.Model ?? request.Model ?? _agent.Llm.ModelFor(request.Tier);
        var u = response?.Usage ?? LlmUsage.Zero;
        long prompt = u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens;
        decimal cost = response != null ? _agent.Prices.Cost(model, u) : 0;
        int maxOut = request.MaxTokens ?? _agent.Llm.MaxTokensFor(request.Tier);
        int limit = _agent.Context.MaxTokens;
        var sections = PromptSections.Calibrate(est, prompt);
        var record = new PromptUsageRecord
        {
            Ts = _time.GetUtcNow(),
            Bot = Bot,
            Body = BodyOf(),
            Tier = UsageTracker.TierLabel(request.Tier == ModelTier.Worker ? ModelTier.Worker : _agent.Llm.TierOfModel(model) ?? request.Tier),
            Model = model,
            Provider = response?.Provider ?? _agent.Llm.ProviderFor(request),
            Profile = request.Profile ?? _agent.Llm.ProfileOfModel(model),
            Purpose = purpose,
            Round = round,
            InputTokens = u.InputTokens,
            OutputTokens = u.OutputTokens,
            CacheReadTokens = u.CacheReadTokens,
            CacheWriteTokens = u.CacheWriteTokens,
            CacheWrite1hTokens = u.CacheWrite1hTokens,
            PromptTokens = prompt,
            CostUsd = Math.Round(cost, 6),
            CacheHit = Math.Round(UsageTracker.CacheHit(u), 4),
            Sections = sections,
            EstimatedTokens = est.Values.Sum(),
            ContextLimit = limit,
            ContextPct = limit > 0 ? Math.Round((double)(prompt + maxOut) / limit, 4) : 0,
            MaxOutputTokens = maxOut,
            Compactions = Interlocked.Exchange(ref _compactions, 0),
            LatencyMs = latencyMs,
            StopReason = response?.StopReason ?? "error",
            ToolCalls = response?.ToolUses.Select(t => t.Name).ToArray() ?? [],
            Error = error == null ? null : error.GetType().Name + ": " + Tools.TextUtil.Truncate(error.Message, 200),
        };
        if (response != null && purpose == PromptPurpose.ChatTurn) Thoughts.OnBrainResponse(response);
        Publish(record);
        return record;
    }

    /// <summary>Stores, writes and broadcasts a record (also used by fake/remote sources).</summary>
    public void Publish(PromptUsageRecord record)
    {
        lock (_lock) Remember(record);
        _writer?.Write(record);
        try
        {
            if (record.Error == null)
                _usage?.AddRollup(record.Purpose, record.CostUsd, record.PromptTokens, record.OutputTokens, record.LatencyMs, record.Sections);
        }
        catch (Exception) { /* telemetry never breaks a call */ }
        var handlers = Recorded;
        if (handlers == null) return;
        foreach (Action<PromptUsageRecord> h in handlers.GetInvocationList())
        {
            try { h(record); } catch (Exception) { }
        }
    }

    private void Remember(PromptUsageRecord r)
    {
        _recent.AddLast(r);
        while (_recent.Count > Math.Max(1, _o.RecentRecords)) _recent.RemoveFirst();
        long h = HourOf(r.Ts);
        _hours[h] = _hours.GetValueOrDefault(h) + 1;
        if (_hours.Count > 72)
            foreach (var k in _hours.Keys.Where(k => k < h - 48).ToList()) _hours.Remove(k);
    }

    private TurnInput? FindTurn(LlmRequest r)
    {
        TurnInput[] turns;
        lock (_lock) turns = _turns.ToArray();
        for (int t = turns.Length - 1; t >= 0; t--)
            for (int i = r.Messages.Count - 1; i >= 0; i--)
                if (r.Messages[i].Role == LlmRole.User && r.Messages[i].Content.Any(b => b is LlmText x && x.Text == turns[t].Text))
                    return turns[t];
        // Not found verbatim (the hard-cap guard shortened it): still a brain turn, so PromptSections uses its fallback split.
        return turns.Length > 0 ? turns[^1] : null;
    }

    private string? BodyOf()
    {
        try { return CurrentBody?.Invoke(); }
        catch (Exception) { return null; }
    }

    private static long HourOf(DateTimeOffset t) => t.ToUnixTimeSeconds() / 3600;

    public void Dispose() => _writer?.Dispose();
}

/// <summary><see cref="ILlmClient"/> decorator feeding <see cref="PromptTelemetry"/>; adds no behaviour to the call itself.</summary>
public sealed class TelemetryLlmClient(ILlmClient inner, PromptTelemetry telemetry) : ILlmClient
{
    public ILlmClient Inner => inner;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        try { telemetry.OnBefore(request); } catch (Exception) { }
        var sw = Stopwatch.StartNew();
        LlmResponse response;
        try { response = await inner.CompleteAsync(request, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { telemetry.OnAfter(request, null, ex, sw.ElapsedMilliseconds); } catch (Exception) { }
            throw;
        }
        try { telemetry.OnAfter(request, response, null, sw.ElapsedMilliseconds); } catch (Exception) { }
        return response;
    }
}
