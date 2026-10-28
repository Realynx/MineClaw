using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Telemetry;

/// <summary>
/// One script interrupt, from the moment it stopped the script to the moment the script went on (resumed, restarted, replaced,
/// cancelled) — the latency the patch turn exists to shrink. <see cref="Path"/>: declared (the script's onInterrupt) | learned (a
/// saved handler, no LLM call) | patch (one small Opus call) | full (the brain's normal turn) | escalated (a patch was tried first,
/// then the full turn). <see cref="Calls"/> / tokens / <see cref="Usd"/> = every LLM call made while it was open.
/// </summary>
public sealed record InterruptRecord
{
    public DateTimeOffset Ts { get; init; }
    public string Bot { get; init; } = "";
    public string? Body { get; init; }
    public string Script { get; init; } = "";
    /// <summary>danger | lowhp | void | died | stuck | failed | needs_input | chat | pause.</summary>
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>resume | restart_step | stop | replaced | cancelled | ended | escalated.</summary>
    public string? Then { get; init; }
    public bool Ok { get; init; }
    public long Ms { get; init; }
    public int Calls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public decimal Usd { get; init; }
    public string? Signature { get; init; }
    public string? Note { get; init; }
}

/// <summary>p50/p95 latency, mean calls and mean USD per path over the recent window ("interrupts" line of the TUI and reports).</summary>
public sealed record InterruptSummary(int Count, IReadOnlyDictionary<string, InterruptPathStats> Paths)
{
    public string Line() => Count == 0 ? "interrupts: none yet"
        : "interrupts " + string.Join(" · ", Paths.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key} {p.Value.N}x p50 {S(p.Value.P50Ms)} p95 {S(p.Value.P95Ms)} {p.Value.MeanCalls:0.#} calls ${p.Value.MeanUsd:0.000}"));

    private static string S(long? ms) => ms is { } v ? (v / 1000.0).ToString("0.0") + "s" : "-";
}

public sealed record InterruptPathStats(int N, long? P50Ms, long? P95Ms, double MeanCalls, decimal MeanUsd);

/// <summary>
/// Measures interrupt → resume per script interrupt (zero tokens): open on the interrupt, count every LLM call while open (the
/// usage tracker's <see cref="Budget.UsageTracker.Recorded"/>), close when the script goes on. Feeds <c>interrupts-YYYY-MM-DD.jsonl</c>.
/// </summary>
public sealed class InterruptTracker(TimeProvider? time = null, int keep = 200)
{
    private sealed class Pending
    {
        public required string Key, Script, Kind, Path;
        public required DateTimeOffset At;
        public string? Body, Signature, Note;
        public int Calls;
        public long In, Out, CacheRead, CacheWrite;
        public decimal Usd;
    }

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _open = new(StringComparer.Ordinal);
    private readonly LinkedList<InterruptRecord> _recent = new();

    public string Bot { get; set; } = "";
    public event Action<InterruptRecord>? Recorded;

    /// <summary>Opens (or re-labels) the measurement <paramref name="key"/> (one per paused session); the first open's time is kept.</summary>
    public void Open(string key, string script, string kind, string path, string? body = null, string? signature = null, DateTimeOffset? at = null)
    {
        lock (_lock)
        {
            if (_open.TryGetValue(key, out var o)) { o.Path = path; o.Signature ??= signature; return; }
            if (_open.Count > 32) _open.Remove(_open.Keys.First());
            _open[key] = new Pending { Key = key, Script = script, Kind = kind, Path = path, At = at ?? _time.GetUtcNow(), Body = body, Signature = signature };
        }
    }

    public bool IsOpen(string key) { lock (_lock) return _open.ContainsKey(key); }

    /// <summary>The path of an open measurement ("patch" → "escalated" when the brain takes over).</summary>
    public string? PathOf(string key) { lock (_lock) return _open.TryGetValue(key, out var o) ? o.Path : null; }

    public void Relabel(string key, string path, string? note = null)
    {
        lock (_lock) if (_open.TryGetValue(key, out var o)) { o.Path = path; if (note != null) o.Note = note; }
    }

    /// <summary>An LLM call finished: it counts toward every open interrupt (brain turns, the patch call, the author...).</summary>
    public void NoteCall(LlmUsage u, decimal usd)
    {
        lock (_lock)
            foreach (var o in _open.Values)
            {
                o.Calls++;
                o.In += u.InputTokens;
                o.Out += u.OutputTokens;
                o.CacheRead += u.CacheReadTokens;
                o.CacheWrite += u.CacheWriteTokens;
                o.Usd += usd;
            }
    }

    /// <summary>Closes <paramref name="key"/> (no-op when not open). Returns the record.</summary>
    public InterruptRecord? Close(string key, string then, bool ok, string? note = null)
    {
        InterruptRecord rec;
        lock (_lock)
        {
            if (!_open.Remove(key, out var o)) return null;
            rec = new InterruptRecord
            {
                Ts = o.At, Bot = Bot, Body = o.Body, Script = o.Script, Kind = o.Kind, Path = o.Path, Then = then, Ok = ok,
                Ms = Math.Max(0, (long)(_time.GetUtcNow() - o.At).TotalMilliseconds), Calls = o.Calls, InputTokens = o.In,
                OutputTokens = o.Out, CacheReadTokens = o.CacheRead, CacheWriteTokens = o.CacheWrite, Usd = Math.Round(o.Usd, 5),
                Signature = o.Signature, Note = note ?? o.Note,
            };
            _recent.AddLast(rec);
            while (_recent.Count > keep) _recent.RemoveFirst();
        }
        try { Recorded?.Invoke(rec); } catch (Exception) { }
        return rec;
    }

    public IReadOnlyList<InterruptRecord> Recent() { lock (_lock) return _recent.ToArray(); }

    public InterruptSummary Summary(int window = 100)
    {
        List<InterruptRecord> list;
        lock (_lock) list = _recent.TakeLast(window).ToList();
        return Summarize(list);
    }

    public static InterruptSummary Summarize(IReadOnlyList<InterruptRecord> list) =>
        new(list.Count, list.GroupBy(r => r.Path).ToDictionary(g => g.Key, g => new InterruptPathStats(g.Count(),
            Pct(g.Select(r => r.Ms), 0.5), Pct(g.Select(r => r.Ms), 0.95), g.Average(r => r.Calls), g.Average(r => r.Usd))));

    public static long? Pct(IEnumerable<long> values, double q)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v[Math.Clamp((int)Math.Ceiling(q * v.Count) - 1, 0, v.Count - 1)];
    }
}

/// <summary>Appends <see cref="InterruptRecord"/>s to <c>interrupts-YYYY-MM-DD.jsonl</c> next to the usage telemetry.</summary>
public sealed class InterruptJsonlWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _dir;
    private readonly object _lock = new();

    public InterruptJsonlWriter(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
    }

    public static string FileName(DateTimeOffset day) => $"interrupts-{day:yyyy-MM-dd}.jsonl";

    public void Write(InterruptRecord r)
    {
        try
        {
            var line = JsonSerializer.Serialize(r, Json);
            lock (_lock) File.AppendAllText(Path.Combine(_dir, FileName(r.Ts.ToLocalTime())), line + "\n");
        }
        catch (Exception) { /* telemetry never breaks the bot */ }
    }

    public static string Serialize(InterruptRecord r) => JsonSerializer.Serialize(r, Json);
}
