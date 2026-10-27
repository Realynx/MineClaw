using System.Text.Json;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tools;

/// <summary>Who started an activity: its place on the body's priority stack (see <see cref="Brainstem.PriorityStack"/>).</summary>
public enum ActivityKind
{
    /// <summary>A player-commanded job (brain/worker/script/standing order): the normal case.</summary>
    Job,
    /// <summary>A brain-issued movement that runs non-blocking ("moving to the gen, ~6s"): job actions queue behind it.</summary>
    Move,
    /// <summary>The brainstem's speculative first step (walk/look only) while the brain plans: adopted or cancelled by it.</summary>
    Speculative,
    /// <summary>A social idle behaviour (loose follow): anything else preempts it.</summary>
    Social,
}

/// <summary>What happens to a running speculative step when the brain's own chain arrives.</summary>
public enum SpeculationVerdict
{
    /// <summary>Same destination: the running walk becomes the chain's first step (no restart).</summary>
    Adopt,
    /// <summary>Close but different (another gen cell) or an action that paths there itself: replace it; progress is kept.</summary>
    Retarget,
    /// <summary>Unrelated: cancel it.</summary>
    Cancel,
}

/// <summary>
/// Runs long-running tools as the bot's single "current activity" (it has one body). A chain of long-running
/// calls from one model turn runs sequentially; the caller waits up to an inline window, after which the chain
/// keeps going in the background and reports back with a task event (so the model isn't held — or billed —
/// while the bot mines). A new activity supersedes the old one; <see cref="Cancel"/> stops it.
/// <para>Kinds (<see cref="ActivityKind"/>): a job started while a brain <see cref="ActivityKind.Move"/> runs queues behind
/// it (never runs if the move fails); a speculative step is adopted, retargeted or cancelled by the next job/move
/// (<see cref="SpeculationPolicy"/>); speculative and social activities are silent (no task events) and invisible to
/// <see cref="Current"/>/<see cref="IsBusy"/>, and they only start while the body is free.</para>
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
        public required ActivityKind Kind;
        public string? CancelReason;
        public bool Detached;
        public bool Finished;
        /// <summary>The first failure (null = all ok) once finished; completes when the chain ends.</summary>
        public readonly TaskCompletionSource<ToolResult?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The last step's result (the adopted move's answer for an adopting chain).</summary>
        public ToolResult? LastResult;
        /// <summary>A job queued behind this move: it reports the move's failure itself.</summary>
        public Activity? Queued;
        /// <summary>This activity waits for <see cref="Activity"/> (a move or an adopted speculative step) first.</summary>
        public Activity? Behind;
        /// <summary>Run only if <see cref="Behind"/> succeeded (behind a move: it needs the destination). Behind a busy
        /// script/job (queue) it runs once that ends, whatever the outcome (unless this one is cancelled).</summary>
        public bool RequireSuccess = true;
        public bool Silent => Kind is ActivityKind.Speculative or ActivityKind.Social;
    }

    private readonly ToolRegistry _registry;
    private readonly EventQueue _events;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private Activity? _current;
    private int _nextId;
    private static readonly AsyncLocal<ActivityKind?> _runningKind = new();

    public ActivityManager(ToolRegistry registry, EventQueue events, TimeProvider? time = null)
    {
        _registry = registry;
        _events = events;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Cancels everything when the agent shuts down.</summary>
    public CancellationToken Shutdown { get; set; }

    /// <summary>Kind of the activity whose step is executing on this async flow (null = not inside an activity).</summary>
    public static ActivityKind? RunningKind => _runningKind.Value;

    /// <summary>Movement tools: a chain starting with one of these is a move; other jobs queue behind a running move.</summary>
    public static readonly IReadOnlySet<string> MovementTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "goto", "follow" };

    /// <summary>Queue job chains behind a running brain move instead of superseding it (move-first, think-while-walking).</summary>
    public bool QueueBehindMoves { get; set; } = true;

    /// <summary>
    /// Decides what a running speculative step becomes when a job/move chain arrives: (speculative steps, new steps) →
    /// verdict. Null = always cancel.
    /// </summary>
    public Func<IReadOnlyList<(string Tool, JsonElement Input)>, IReadOnlyList<(string Tool, JsonElement Input)>, SpeculationVerdict>? SpeculationPolicy { get; set; }

    /// <summary>A speculative step ended: (adopted — kept/retargeted — or cancelled, why). Telemetry.</summary>
    public event Action<bool, string>? SpeculationResolved;

    /// <summary>An activity started: (id, kind, steps). Telemetry (first move / first action).</summary>
    public event Action<int, ActivityKind, IReadOnlyList<(string Tool, JsonElement Input)>>? Started;

    /// <summary>A job or move is running or queued (speculative/social fillers don't count).</summary>
    public bool IsBusy { get { lock (_lock) return _current is { Finished: false, Silent: false }; } }

    /// <summary>Anything holds the body, fillers included.</summary>
    public bool HasAnyActivity { get { lock (_lock) return _current is { Finished: false }; } }

    /// <summary>Kind of whatever holds the body right now (null = free).</summary>
    public ActivityKind? CurrentKind { get { lock (_lock) return _current is { Finished: false } a ? a.Kind : null; } }

    /// <summary>Short description for the state header, e.g. "mine #3 42s" or "goto #4 3s, then mine", or null when idle.</summary>
    public string? Describe()
    {
        lock (_lock)
        {
            if (_current is not { Finished: false, Silent: false } a) return null;
            var s = $"{a.Label} #{a.Id} {(int)(_time.GetUtcNow() - a.Started).TotalSeconds}s";
            return a.Queued is { Finished: false } q ? s + ", then " + q.Label : s;
        }
    }

    /// <summary>The running job/move (id, label, start, its steps), or null when idle or only a filler runs.</summary>
    public RunningActivity? Current
    {
        get
        {
            lock (_lock)
                return _current is { Finished: false, Silent: false } a ? new RunningActivity(a.Id, a.Label, a.Started, a.Steps) { Kind = a.Kind } : null;
        }
    }

    /// <summary>The running speculative/social filler, or null.</summary>
    public RunningActivity? Filler
    {
        get
        {
            lock (_lock)
                return _current is { Finished: false, Silent: true } a ? new RunningActivity(a.Id, a.Label, a.Started, a.Steps) { Kind = a.Kind } : null;
        }
    }

    /// <summary>
    /// Raised after each step of a chain finishes: (tool, input, result, cancel reason or null). Cancelled/superseded steps
    /// carry the reason, so listeners can tell a real failure from a stop.
    /// </summary>
    public event Action<string, JsonElement, ToolResult, string?>? StepFinished;

    /// <summary>Cancels the current activity (and a job queued behind it). Returns its label (or null if idle).</summary>
    public string? Cancel(string reason)
    {
        Activity a;
        bool spec;
        lock (_lock)
        {
            if (_current is not { Finished: false } cur) return null;
            a = cur;
            spec = a.Kind == ActivityKind.Speculative;
            CancelLocked(a, reason);
        }
        if (spec) RaiseResolved(false, reason);
        return $"{a.Label} #{a.Id}";
    }

    /// <summary>Cancels a running speculative step (the brain's turn ended without adopting it). False if none.</summary>
    public bool CancelSpeculative(string reason)
    {
        lock (_lock)
        {
            if (_current is not { Finished: false, Kind: ActivityKind.Speculative } a) return false;
            CancelLocked(a, reason);
        }
        RaiseResolved(false, reason);
        return true;
    }

    private static void CancelLocked(Activity a, string reason)
    {
        a.CancelReason = reason;
        try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
        if (a.Queued is { Finished: false } q)
        {
            q.CancelReason = reason;
            try { q.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void RaiseResolved(bool adopted, string why)
    {
        try { SpeculationResolved?.Invoke(adopted, why); } catch (Exception) { }
    }

    /// <summary>
    /// Starts a speculative or social filler (silent, no inline wait) only if the body is free. False = something holds it.
    /// </summary>
    public bool TryStartFiller(ActivityKind kind, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, string? label = null)
    {
        if (kind is not (ActivityKind.Speculative or ActivityKind.Social) || steps.Count == 0) return false;
        Activity act;
        lock (_lock)
        {
            if (_current is { Finished: false }) return false;
            act = NewActivityLocked(steps, kind, label);
            _current = act;
        }
        RaiseStarted(act);
        _ = RunChainAsync(act, steps, new ToolResult?[steps.Count]);
        return true;
    }

    /// <summary>
    /// Starts a sequential chain of long-running calls and waits up to <paramref name="inlineWait"/>.
    /// Results for steps that haven't finished by then say they continue in the background.
    /// <paramref name="queueBehindCurrent"/> = run after the current job/script instead of superseding it (never if it fails).
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> RunAsync(IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps,
        TimeSpan inlineWait, CancellationToken ct, ActivityKind kind = ActivityKind.Job, bool queueBehindCurrent = false)
    {
        if (steps.Count == 0) return [];
        if (kind is ActivityKind.Speculative or ActivityKind.Social)
            return TryStartFiller(kind, steps) ? steps.Select(_ => ToolResult.Success("started")).ToArray() : steps.Select(_ => ToolResult.Error("body busy")).ToArray();

        Activity act;
        Activity? behind = null;
        bool adoptFirst = false;
        (bool Adopted, string Why)? resolved = null;
        lock (_lock)
        {
            act = NewActivityLocked(steps, kind, null);
            if (_current is { Finished: false } old)
            {
                bool startsWithMove = MovementTools.Contains(steps[0].Tool.Name);
                if (old.Kind == ActivityKind.Speculative)
                {
                    var verdict = SpeculationVerdict.Cancel;
                    try { verdict = SpeculationPolicy?.Invoke(old.Steps, act.Steps) ?? SpeculationVerdict.Cancel; } catch (Exception) { }
                    if (verdict == SpeculationVerdict.Adopt && startsWithMove)
                    {
                        // The walk is already under way: it becomes this chain's first step.
                        old.Kind = steps.Count == 1 ? kind : ActivityKind.Move;
                        old.Label = steps[0].Tool.Name;
                        behind = old;
                        adoptFirst = true;
                        old.Queued = act;
                        resolved = (true, "adopted");
                    }
                    else
                    {
                        old.CancelReason = "superseded by " + act.Label;
                        try { old.Cts.Cancel(); } catch (ObjectDisposedException) { }
                        resolved = verdict == SpeculationVerdict.Cancel ? (false, "superseded by " + act.Label) : (true, "retargeted by " + act.Label);
                    }
                }
                else if (old.Queued is not { Finished: false } && !old.Silent && (queueBehindCurrent
                    || (old.Kind == ActivityKind.Move && kind == ActivityKind.Job && QueueBehindMoves && !startsWithMove)))
                {
                    behind = old; // walk there first, then do it
                    old.Queued = act;
                }
                else
                {
                    old.CancelReason = "superseded by " + act.Label;
                    try { old.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    if (old.Queued is { Finished: false } q && !ReferenceEquals(q, act))
                    {
                        q.CancelReason = old.CancelReason;
                        try { q.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            act.Behind = behind;
            act.RequireSuccess = adoptFirst || behind is { Kind: ActivityKind.Move } || !queueBehindCurrent;
            if (behind == null || adoptFirst) _current = adoptFirst ? behind : act;
            if (adoptFirst && steps.Count == 1)
            {
                // The adopted walk IS the whole chain: no second activity.
                behind!.Queued = null;
                act = behind;
            }
        }
        if (resolved is { } r0) RaiseResolved(r0.Adopted, r0.Why);
        if (!ReferenceEquals(act, behind)) RaiseStarted(act);

        var results = new ToolResult?[steps.Count];
        Task chain;
        if (ReferenceEquals(act, behind)) chain = act.Done.Task; // adopted single walk: already running
        else chain = RunChainAsync(act, steps, results, behind, adoptFirst);

        try
        {
            await Task.WhenAny(chain, Task.Delay(inlineWait, _time, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        lock (_lock)
        {
            if (!act.Finished) act.Detached = true;
        }

        if (ReferenceEquals(act, behind))
        {
            var done = act.Finished ? act.LastResult : null;
            return [done ?? ToolResult.Success($"already on the way ({act.Label} #{act.Id}); a [task] event reports arrival")];
        }

        var output = new ToolResult[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            output[i] = Volatile.Read(ref results[i]) ?? ToolResult.Success(behind != null && i == 0 && adoptFirst
                ? $"already on the way (#{behind.Id})"
                : behind != null && !adoptFirst
                    ? $"queued behind {behind.Label} #{behind.Id}: runs on arrival as task #{act.Id}; a [task] event will report the result"
                    : $"running in background as task #{act.Id} ({act.Label}); a [task] event will report the result");
        }
        return output;
    }

    private Activity NewActivityLocked(IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ActivityKind kind, string? label) => new()
    {
        Id = ++_nextId,
        Label = label ?? string.Join(">", steps.Select(s => s.Tool.Name)),
        Cts = CancellationTokenSource.CreateLinkedTokenSource(Shutdown),
        Started = _time.GetUtcNow(),
        Steps = steps.Select(s => (s.Tool.Name, s.Input.Clone())).ToArray(),
        Kind = kind,
    };

    private void RaiseStarted(Activity a)
    {
        try { Started?.Invoke(a.Id, a.Kind, a.Steps); } catch (Exception) { }
    }

    private async Task RunChainAsync(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results,
        Activity? behind = null, bool adoptFirst = false)
    {
        await Task.Yield();
        ToolResult? failure = null;
        int failedAt = -1;
        int start = 0;
        if (behind != null)
        {
            // Wait for the move (or the adopted walk); never run the rest if it failed.
            ToolResult? moveFail;
            try { moveFail = await behind.Done.Task.WaitAsync(act.Cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { moveFail = ToolResult.Error("cancelled"); }
            if (adoptFirst)
            {
                results[0] = moveFail ?? behind.LastResult ?? ToolResult.Success("arrived");
                start = 1;
            }
            if (!act.RequireSuccess && !act.Cts.IsCancellationRequested) moveFail = null;
            if (moveFail != null || act.Cts.IsCancellationRequested)
            {
                var why = act.Cts.IsCancellationRequested ? "cancelled" + (act.CancelReason != null ? ": " + act.CancelReason : "")
                    : $"not run: {behind.Steps[0].Tool} failed: {moveFail!.Text}";
                for (int i = start; i < steps.Count; i++) results[i] = ToolResult.Error(i == start ? why : "skipped (earlier step failed)");
                failure = adoptFirst && moveFail != null ? results[0] : results[start];
                failedAt = adoptFirst && moveFail != null ? 0 : start;
                Finish(act, steps, results, failure, failedAt);
                return;
            }
            lock (_lock)
            {
                if (act.Finished || act.Cts.IsCancellationRequested) { }
                else if (_current is not { Finished: false } || ReferenceEquals(_current, behind)) _current = act;
            }
        }

        _runningKind.Value = act.Kind;
        for (int i = start; i < steps.Count; i++)
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
            act.LastResult = r;
            if (!act.Silent)
            {
                try { StepFinished?.Invoke(steps[i].Tool.Name, steps[i].Input, r, act.Cts.IsCancellationRequested ? act.CancelReason ?? "cancelled" : null); }
                catch (Exception) { }
            }
            if (!r.Ok) { failure = r; failedAt = i; }
        }
        Finish(act, steps, results, failure, failedAt);
    }

    private void Finish(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results,
        ToolResult? failure, int failedAt)
    {
        bool post;
        Activity? queued;
        lock (_lock)
        {
            act.Finished = true;
            post = act.Detached && !act.Silent;
            queued = act.Queued is { Finished: false } q ? q : null;
            if (ReferenceEquals(_current, act)) _current = queued; // the queued job takes the body next
        }
        act.Done.TrySetResult(failure);
        try { act.Cts.Dispose(); } catch (Exception) { }
        if (!post) return;
        // A job is queued behind this move: it reports the outcome (a failed move = "not run: goto failed: ...").
        if (queued != null) return;

        if (failure == null)
        {
            var text = act.Kind == ActivityKind.Move ? $"#{act.Id} {act.Label}: {results[^1]?.Text ?? act.LastResult?.Text}" : $"#{act.Id} {act.Label}: {results[^1]?.Text}";
            // An arrival nobody waits on is context, not a reason to wake the brain (it already said what it's doing).
            _events.Post(act.Kind == ActivityKind.Move
                ? new GameEvent(EventKind.TaskCompleted, text, priority: EventPriority.Background)
                : GameEvent.TaskDone(text));
            return;
        }

        var reason = act.CancelReason;
        // The model asked for this (stop / new activity): it already knows — keep as quiet context only.
        bool modelInitiated = reason != null && (reason == "stop" || reason.StartsWith("superseded", StringComparison.Ordinal));
        bool wakes = reason == null; // plain failures wake; interruptions are explained by the interrupting event
        if (modelInitiated) return;
        var tool = failedAt >= 0 && failedAt < steps.Count ? steps[failedAt].Tool.Name : act.Label;
        _events.Post(GameEvent.TaskFail($"#{act.Id} {tool}: {failure.Text}", wakes));
    }
}

/// <summary>Snapshot of the running activity (see <see cref="ActivityManager.Current"/>).</summary>
public sealed record RunningActivity(int Id, string Label, DateTimeOffset Started, IReadOnlyList<(string Tool, JsonElement Input)> Steps)
{
    public ActivityKind Kind { get; init; } = ActivityKind.Job;
}
