using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Telemetry;

/// <summary>One brain tool call with its (terse) result, for display.</summary>
public sealed record ThoughtToolCall(DateTimeOffset At, string Name, string Args, string? Result = null, bool Ok = true);

/// <summary>
/// Read-only, size-capped picture of what the brain is working on: the latest turn input, its last text output, tool
/// calls, task/delegate status, the newest journal line and summarized thinking. Display only — never fed into prompts.
/// </summary>
public sealed record ThoughtSnapshot
{
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? TurnAt { get; init; }
    public string Header { get; init; } = "";
    public string Events { get; init; } = "";
    public string Digest { get; init; } = "";
    public string Memory { get; init; } = "";
    public string Output { get; init; } = "";
    public string Thinking { get; init; } = "";
    public IReadOnlyList<ThoughtToolCall> Tools { get; init; } = [];
    public string? Task { get; init; }
    public string? Delegate { get; init; }
    public string? Journal { get; init; }

    public static ThoughtSnapshot Empty { get; } = new();
}

/// <summary>Caps for <see cref="ThoughtTracker"/> (chars / counts).</summary>
public sealed class ThoughtCaps
{
    public int Events { get; set; } = 600;
    public int Digest { get; set; } = 300;
    public int Memory { get; set; } = 400;
    public int Output { get; set; } = 400;
    public int Thinking { get; set; } = 600;
    public int ToolArgs { get; set; } = 60;
    public int ToolResult { get; set; } = 120;
    public int MaxTools { get; set; } = 8;
    public int Journal { get; set; } = 160;
    public int Delegate { get; set; } = 160;
}

/// <summary>Builds <see cref="ThoughtSnapshot"/>s from telemetry hooks. Thread-safe; every write is capped.</summary>
public sealed class ThoughtTracker
{
    private readonly object _lock = new();
    private readonly ThoughtCaps _caps;
    private readonly TimeProvider _time;
    private ThoughtSnapshot _snap = ThoughtSnapshot.Empty;
    private readonly List<ThoughtToolCall> _tools = new();
    private readonly Dictionary<string, int> _pending = new(); // tool_use id → index in _tools
    private string? _delegateTask;

    public ThoughtTracker(ThoughtCaps? caps = null, TimeProvider? time = null)
    {
        _caps = caps ?? new ThoughtCaps();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Current task (e.g. the activity manager's description); read on every snapshot.</summary>
    public Func<string?>? TaskProvider { get; set; }

    public ThoughtSnapshot Snapshot()
    {
        string? task = null;
        try { task = TaskProvider?.Invoke(); } catch (Exception) { }
        lock (_lock) return _snap with { Tools = _tools.ToArray(), Task = task };
    }

    public void OnTurn(TurnInput t)
    {
        lock (_lock)
        {
            _tools.Clear();
            _pending.Clear();
            _snap = _snap with
            {
                UpdatedAt = _time.GetUtcNow(), TurnAt = t.At,
                Header = Cap(t.Header, 200), Events = Cap(t.Events, _caps.Events), Digest = Cap(t.Digest, _caps.Digest),
                Memory = Cap(t.Memory, _caps.Memory), Output = "", Thinking = "",
            };
        }
    }

    /// <summary>A brain request: picks up results for the tool calls of the previous response.</summary>
    public void OnBrainRequest(LlmRequest r)
    {
        if (r.Messages.Count == 0) return;
        var last = r.Messages[^1];
        if (last.Role != LlmRole.User) return;
        lock (_lock) ApplyResults(last.ToolResults);
    }

    public void OnBrainResponse(LlmResponse resp)
    {
        lock (_lock)
        {
            var text = resp.Text.Trim();
            var thinking = string.Join("\n", resp.Content.OfType<LlmThinking>().Select(t => t.Thinking.Trim()).Where(t => t.Length > 0));
            var upd = _snap with { UpdatedAt = _time.GetUtcNow() };
            if (text.Length > 0) upd = upd with { Output = Cap(text, _caps.Output) };
            if (thinking.Length > 0) upd = upd with { Thinking = Cap(thinking, _caps.Thinking) };
            _snap = upd;
            foreach (var u in resp.ToolUses)
            {
                _tools.Add(new ThoughtToolCall(_time.GetUtcNow(), u.Name, Cap(Args(u.Input), _caps.ToolArgs)));
                if (u.Name == Worker.WorkerAgent.ToolName) _delegateTask = Cap(u.Input.Str("task") ?? "", 100);
                while (_tools.Count > _caps.MaxTools)
                {
                    _tools.RemoveAt(0);
                    foreach (var k in _pending.Keys.ToList()) if (--_pending[k] < 0) _pending.Remove(k);
                }
                _pending[u.Id] = _tools.Count - 1;
            }
        }
    }

    /// <summary>A committed brain episode: fills any results the next request never carried (last round, stopped turns).</summary>
    public void OnCommitted(IEnumerable<LlmMessage> messages)
    {
        lock (_lock) ApplyResults(messages.SelectMany(m => m.ToolResults));
    }

    public void OnWorkerRound(int round, LlmRequest r)
    {
        lock (_lock)
        {
            var task = _delegateTask ?? FirstLine(r.Messages.Count > 0 ? r.Messages[0].Text : "");
            _snap = _snap with { UpdatedAt = _time.GetUtcNow(), Delegate = Cap($"running, round {round + 1}: {task}", _caps.Delegate) };
        }
    }

    public void OnWorkerFinished(bool ok, string summary, int calls)
    {
        lock (_lock)
        {
            _snap = _snap with { UpdatedAt = _time.GetUtcNow(), Delegate = Cap($"{(ok ? "done" : "ended")} after {calls} call(s): {TextUtil.OneLine(summary)}", _caps.Delegate) };
            _delegateTask = null;
        }
    }

    public void OnCompacted(string journal)
    {
        var line = journal.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        lock (_lock) _snap = _snap with { UpdatedAt = _time.GetUtcNow(), Journal = Cap(line, _caps.Journal) };
    }

    private void ApplyResults(IEnumerable<LlmToolResult> results)
    {
        foreach (var res in results)
        {
            if (!_pending.Remove(res.ToolUseId, out int i) || i < 0 || i >= _tools.Count) continue;
            _tools[i] = _tools[i] with { Result = Cap(TextUtil.OneLine(res.Content), _caps.ToolResult), Ok = !res.IsError };
        }
    }

    private static string Args(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        return string.Join(", ", input.EnumerateObject().Select(p =>
            p.Name + "=" + (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())));
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Replace("task:", "").Trim();

    private static string Cap(string? s, int max) => TextUtil.Truncate((s ?? "").Trim(), max);
}
