using System.Text.Json;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tools;

/// <summary>
/// Runs long-running tools as the bot's single "current activity" (it has one body). A chain of long-running
/// calls from one model turn runs sequentially; the caller waits up to an inline window, after which the chain
/// keeps going in the background and reports back with a task event (so the model isn't held — or billed —
/// while the bot mines). A new activity supersedes the old one; <see cref="Cancel"/> stops it.
/// </summary>
public sealed class ActivityManager
{
    private sealed class Activity
    {
        public required int Id;
        public required string Label;
        public required CancellationTokenSource Cts;
        public required DateTimeOffset Started;
        public required IReadOnlyList<(string Tool, JsonElement Input)> Steps;
        public string? CancelReason;
        public bool Detached;
        public bool Finished;
    }

    private readonly ToolRegistry _registry;
    private readonly EventQueue _events;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private Activity? _current;
    private int _nextId;

    public ActivityManager(ToolRegistry registry, EventQueue events, TimeProvider? time = null)
    {
        _registry = registry;
        _events = events;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Cancels everything when the agent shuts down.</summary>
    public CancellationToken Shutdown { get; set; }

    public bool IsBusy { get { lock (_lock) return _current is { Finished: false }; } }

    /// <summary>Short description for the state header, e.g. "mine #3 42s", or null when idle.</summary>
    public string? Describe()
    {
        lock (_lock)
        {
            if (_current is not { Finished: false } a) return null;
            return $"{a.Label} #{a.Id} {(int)(_time.GetUtcNow() - a.Started).TotalSeconds}s";
        }
    }

    /// <summary>The running activity (id, label, start, its steps), or null when idle.</summary>
    public RunningActivity? Current
    {
        get
        {
            lock (_lock)
                return _current is { Finished: false } a ? new RunningActivity(a.Id, a.Label, a.Started, a.Steps) : null;
        }
    }

    /// <summary>
    /// Raised after each step of a chain finishes: (tool, input, result, cancel reason or null). Cancelled/superseded steps
    /// carry the reason, so listeners can tell a real failure from a stop.
    /// </summary>
    public event Action<string, JsonElement, ToolResult, string?>? StepFinished;

    /// <summary>Cancels the current activity. Returns its label (or null if idle).</summary>
    public string? Cancel(string reason)
    {
        lock (_lock)
        {
            if (_current is not { Finished: false } a) return null;
            a.CancelReason = reason;
            try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
            return $"{a.Label} #{a.Id}";
        }
    }

    /// <summary>
    /// Starts a sequential chain of long-running calls and waits up to <paramref name="inlineWait"/>.
    /// Results for steps that haven't finished by then say they continue in the background.
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> RunAsync(IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps,
        TimeSpan inlineWait, CancellationToken ct)
    {
        if (steps.Count == 0) return [];
        var label = string.Join(">", steps.Select(s => s.Tool.Name));
        Activity act;
        lock (_lock)
        {
            if (_current is { Finished: false } old)
            {
                old.CancelReason = "superseded by " + label;
                try { old.Cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            act = new Activity
            {
                Id = ++_nextId,
                Label = label,
                Cts = CancellationTokenSource.CreateLinkedTokenSource(Shutdown),
                Started = _time.GetUtcNow(),
                Steps = steps.Select(s => (s.Tool.Name, s.Input.Clone())).ToArray(),
            };
            _current = act;
        }

        var results = new ToolResult?[steps.Count];
        var chain = Task.Run(async () =>
        {
            ToolResult? failure = null;
            int failedAt = -1;
            for (int i = 0; i < steps.Count; i++)
            {
                if (failure != null || act.Cts.IsCancellationRequested)
                {
                    results[i] = ToolResult.Error(act.Cts.IsCancellationRequested ? "cancelled" : "skipped (earlier step failed)");
                    continue;
                }
                var r = await _registry.ExecuteAsync(steps[i].Tool, steps[i].Input, act.Cts.Token).ConfigureAwait(false);
                if (act.Cts.IsCancellationRequested && r.Ok == false && act.CancelReason != null)
                    r = ToolResult.Error("cancelled: " + act.CancelReason);
                results[i] = r;
                try { StepFinished?.Invoke(steps[i].Tool.Name, steps[i].Input, r, act.Cts.IsCancellationRequested ? act.CancelReason ?? "cancelled" : null); }
                catch (Exception) { }
                if (!r.Ok) { failure = r; failedAt = i; }
            }
            Finish(act, steps, results, failure, failedAt);
        });

        try
        {
            await Task.WhenAny(chain, Task.Delay(inlineWait, _time, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        lock (_lock)
        {
            if (!act.Finished) act.Detached = true;
        }

        var output = new ToolResult[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            output[i] = Volatile.Read(ref results[i]) ?? ToolResult.Success(
                $"running in background as task #{act.Id} ({label}); a [task] event will report the result");
        }
        return output;
    }

    private void Finish(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results,
        ToolResult? failure, int failedAt)
    {
        bool post;
        lock (_lock)
        {
            act.Finished = true;
            post = act.Detached;
            if (ReferenceEquals(_current, act)) _current = null;
        }
        act.Cts.Dispose();
        if (!post) return;

        if (failure == null)
        {
            _events.Post(GameEvent.TaskDone($"#{act.Id} {act.Label}: {results[^1]?.Text}"));
            return;
        }

        var reason = act.CancelReason;
        // The model asked for this (stop / new activity): it already knows — keep as quiet context only.
        bool modelInitiated = reason != null && (reason == "stop" || reason.StartsWith("superseded", StringComparison.Ordinal));
        bool wakes = reason == null; // plain failures wake; interruptions are explained by the interrupting event
        if (modelInitiated) return;
        _events.Post(GameEvent.TaskFail($"#{act.Id} {steps[failedAt].Tool.Name}: {failure.Text}", wakes));
    }
}

/// <summary>Snapshot of the running activity (see <see cref="ActivityManager.Current"/>).</summary>
public sealed record RunningActivity(int Id, string Label, DateTimeOffset Started, IReadOnlyList<(string Tool, JsonElement Input)> Steps);
