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
        public string? CleanupReport;
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

    /// <summary>
    /// One brain, many bodies: each body owns one ActivityManager, and this pushes that body's scope (the host's
    /// <c>BodyScope.Push(body)</c>) explicitly around every step, so body tools inside the chain, and any Task.Run they start,
    /// act on this body and never on whatever the caller had in scope. Null = no scope (tests, no body layer).
    /// </summary>
    public Func<IDisposable?>? StepScope { get; set; }

    /// <summary>Reclaim this body's self-scaffolding after a job. Never runs for a move or a superseded job's successor.</summary>
    public Func<CancellationToken, Task<string?>>? CleanupJob { get; set; }

    /// <summary>The body this slot belongs to (a clone's short name, "67"): stamped on its task events. Null = the main body.</summary>
    public string? Body { get; set; }

    private void Post(GameEvent e) => _events.Post(Body is { Length: > 0 } b ? e with { Body = b } : e);

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

    /// <summary>
    /// A finished activity's report text that is context only, not a reason to wake the brain (a standing-order pass that found
    /// nothing to do yet: C# backs off instead). Null = every result wakes as usual.
    /// </summary>
    public Func<string, bool>? QuietOutcome { get; set; }

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

    /// <summary>Atomically reserves an idle body's job slot for deterministic work; never supersedes a player's job.</summary>
    public bool TryStartIdleJob(IAgentTool tool, JsonElement input, string label)
    {
        IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps = [(tool, input)];
        Activity act;
        lock (_lock)
        {
            if (_current is { Finished: false }) return false;
            act = NewActivityLocked(steps, ActivityKind.Job, label);
            _current = act;
        }
        RaiseStarted(act);
        _ = RunChainAsync(act, steps, new ToolResult?[1]);
        return true;
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
        if (kind is ActivityKind.Speculative or ActivityKind.Social) return FillerResults(kind, steps);

        Placement p;
        lock (_lock) p = PlaceLocked(steps, kind, queueBehindCurrent);
        if (p.Resolved is { } r0) RaiseResolved(r0.Adopted, r0.Why);
        var act = p.Act;
        bool adoptedWalk = ReferenceEquals(act, p.Behind); // an adopted single walk: already running, no second activity
        if (!adoptedWalk) RaiseStarted(act);

        var results = new ToolResult?[steps.Count];
        Task chain = adoptedWalk ? act.Done.Task : RunChainAsync(act, steps, results, p.Behind, p.AdoptFirst);
        await WaitInlineAsync(chain, inlineWait, ct).ConfigureAwait(false);

        lock (_lock)
        {
            if (!act.Finished) act.Detached = true;
        }
        return adoptedWalk ? [AdoptedWalkResult(act)] : InlineResults(p, results);
    }

    private ToolResult[] FillerResults(ActivityKind kind, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps) =>
        TryStartFiller(kind, steps) ? steps.Select(_ => ToolResult.Success("started")).ToArray() : steps.Select(_ => ToolResult.Error("body busy")).ToArray();

    private async Task WaitInlineAsync(Task chain, TimeSpan inlineWait, CancellationToken ct)
    {
        try
        {
            await Task.WhenAny(chain, Task.Delay(inlineWait, _time, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private static ToolResult AdoptedWalkResult(Activity act)
    {
        var done = act.Finished ? act.LastResult : null;
        return done ?? ToolResult.Success($"already on the way ({act.Label} #{act.Id}); a [task] event reports arrival");
    }

    /// <summary>Each step's result if it finished inline, else what it is doing (on the way, queued, in the background).</summary>
    private static ToolResult[] InlineResults(Placement p, ToolResult?[] results)
    {
        var output = new ToolResult[results.Length];
        for (int i = 0; i < results.Length; i++)
            output[i] = Volatile.Read(ref results[i]) ?? ToolResult.Success(PendingText(p.Act, p.Behind, p.AdoptFirst, i));
        return output;
    }

    /// <summary>Where a new chain goes: its activity (or the adopted walk), what it waits behind, and the speculation outcome.</summary>
    private sealed class Placement
    {
        public required Activity Act;
        public Activity? Behind;
        public bool AdoptFirst;
        public (bool Adopted, string Why)? Resolved;
    }

    /// <summary>Places a new chain against what holds the body (under the lock): adopt, queue behind, or supersede it.</summary>
    private Placement PlaceLocked(IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ActivityKind kind, bool queueBehindCurrent)
    {
        var p = new Placement { Act = NewActivityLocked(steps, kind, null) };
        if (_current is { Finished: false } old) MeetLocked(p, old, steps, kind, queueBehindCurrent);
        var act = p.Act;
        act.Behind = p.Behind;
        act.RequireSuccess = p.AdoptFirst || p.Behind is { Kind: ActivityKind.Move } || !queueBehindCurrent;
        if (p.Behind == null || p.AdoptFirst) _current = p.AdoptFirst ? p.Behind : act;
        if (p.AdoptFirst && steps.Count == 1)
        {
            // The adopted walk IS the whole chain: no second activity.
            p.Behind!.Queued = null;
            p.Act = p.Behind;
        }
        return p;
    }

    private void MeetLocked(Placement p, Activity old, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ActivityKind kind, bool queueBehindCurrent)
    {
        bool startsWithMove = MovementTools.Contains(steps[0].Tool.Name);
        if (old.Kind == ActivityKind.Speculative) MeetSpeculationLocked(p, old, steps, kind, startsWithMove);
        else if (old.Queued is not { Finished: false } && !old.Silent && (queueBehindCurrent
            || (old.Kind == ActivityKind.Move && kind == ActivityKind.Job && QueueBehindMoves && !startsWithMove)))
        {
            p.Behind = old; // walk there first, then do it
            old.Queued = p.Act;
        }
        else SupersedeLocked(old, p.Act);
    }

    /// <summary>A speculative walk meets the brain's chain: adopted as its first step (same place, a move), else cancelled.</summary>
    private void MeetSpeculationLocked(Placement p, Activity old, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ActivityKind kind, bool startsWithMove)
    {
        var verdict = SpeculationVerdict.Cancel;
        try { verdict = SpeculationPolicy?.Invoke(old.Steps, p.Act.Steps) ?? SpeculationVerdict.Cancel; } catch (Exception) { }
        if (verdict == SpeculationVerdict.Adopt && startsWithMove)
        {
            // The walk is already under way: it becomes this chain's first step.
            old.Kind = steps.Count == 1 ? kind : ActivityKind.Move;
            old.Label = steps[0].Tool.Name;
            p.Behind = old;
            p.AdoptFirst = true;
            old.Queued = p.Act;
            p.Resolved = (true, "adopted");
            return;
        }
        old.CancelReason = "superseded by " + p.Act.Label;
        CancelQuietly(old);
        p.Resolved = verdict == SpeculationVerdict.Cancel ? (false, "superseded by " + p.Act.Label) : (true, "retargeted by " + p.Act.Label);
    }

    /// <summary>The new chain replaces what runs (and a job queued behind it, unless that is the new chain).</summary>
    private static void SupersedeLocked(Activity old, Activity act)
    {
        old.CancelReason = "superseded by " + act.Label;
        CancelQuietly(old);
        if (old.Queued is { Finished: false } q && !ReferenceEquals(q, act))
        {
            q.CancelReason = old.CancelReason;
            CancelQuietly(q);
        }
    }

    private static void CancelQuietly(Activity a)
    {
        try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>What a step that hasn't finished inline says: on the way, queued behind a move/job, or running in the background.</summary>
    private static string PendingText(Activity act, Activity? behind, bool adoptFirst, int i)
    {
        if (behind != null && i == 0 && adoptFirst) return $"already on the way (#{behind.Id})";
        return behind != null && !adoptFirst
            ? $"queued behind {behind.Label} #{behind.Id}: runs on arrival as task #{act.Id}; a [task] event will report the result"
            : $"running in background as task #{act.Id} ({act.Label}); a [task] event will report the result";
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
        int start = 0;
        if (behind != null)
        {
            start = await WaitBehindAsync(act, steps, results, behind, adoptFirst).ConfigureAwait(false);
            if (start < 0) return; // the move failed or this chain was cancelled: already finished
        }

        // Set in this frame (AsyncLocal): every step below, and the finish, runs with it.
        _runningKind.Value = act.Kind;
        var (failure, failedAt) = await RunStepsAsync(act, steps, results, start).ConfigureAwait(false);
        await FinishAsync(act, steps, results, failure, failedAt).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the move (or the adopted walk) this chain is queued behind; never runs the rest if it failed. Returns the first
    /// step to run, or -1 when the chain was finished here (failed move / cancelled).
    /// </summary>
    private async Task<int> WaitBehindAsync(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results,
        Activity behind, bool adoptFirst)
    {
        var moveFail = await MoveOutcomeAsync(act, behind).ConfigureAwait(false);
        int start = 0;
        if (adoptFirst)
        {
            results[0] = moveFail ?? behind.LastResult ?? ToolResult.Success("arrived");
            start = 1;
        }
        if (!act.RequireSuccess && !act.Cts.IsCancellationRequested) moveFail = null;
        if (moveFail != null || act.Cts.IsCancellationRequested)
        {
            await SkipAfterMoveAsync(act, steps, results, behind, moveFail, adoptFirst, start).ConfigureAwait(false);
            return -1;
        }
        TakeBodyFrom(act, behind);
        return start;
    }

    /// <summary>The failure of what this chain waits behind (null = it succeeded); a cancel while waiting is "cancelled".</summary>
    private static async Task<ToolResult?> MoveOutcomeAsync(Activity act, Activity behind)
    {
        try { return await behind.Done.Task.WaitAsync(act.Cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return ToolResult.Error("cancelled"); }
    }

    /// <summary>The chain now holds the body (unless it ended, or something else took the body meanwhile).</summary>
    private void TakeBodyFrom(Activity act, Activity behind)
    {
        lock (_lock)
        {
            if (act.Finished || act.Cts.IsCancellationRequested) { }
            else if (_current is not { Finished: false } || ReferenceEquals(_current, behind)) _current = act;
        }
    }

    /// <summary>The move failed (or this chain was cancelled while waiting): every remaining step says why, then the chain finishes.</summary>
    private Task SkipAfterMoveAsync(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results,
        Activity behind, ToolResult? moveFail, bool adoptFirst, int start)
    {
        var why = act.Cts.IsCancellationRequested ? "cancelled" + (act.CancelReason != null ? ": " + act.CancelReason : "")
            : $"not run: {behind.Steps[0].Tool} failed: {moveFail!.Text}";
        for (int i = start; i < steps.Count; i++) results[i] = ToolResult.Error(i == start ? why : "skipped (earlier step failed)");
        bool moveFailed = adoptFirst && moveFail != null;
        return FinishAsync(act, steps, results, moveFailed ? results[0] : results[start], moveFailed ? 0 : start);
    }

    /// <summary>Runs the steps in order from <paramref name="start"/>; after a failure or a cancel the rest are skipped.</summary>
    private async Task<(ToolResult? Failure, int FailedAt)> RunStepsAsync(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps,
        ToolResult?[] results, int start)
    {
        ToolResult? failure = null;
        int failedAt = -1;
        for (int i = start; i < steps.Count; i++)
        {
            if (failure != null || act.Cts.IsCancellationRequested)
            {
                results[i] = ToolResult.Error(act.Cts.IsCancellationRequested ? "cancelled" : "skipped (earlier step failed)");
                continue;
            }
            var r = await RunStepAsync(act, steps[i]).ConfigureAwait(false);
            results[i] = r;
            act.LastResult = r;
            if (!act.Silent) RaiseStepFinished(act, steps[i], r);
            if (!r.Ok) { failure = r; failedAt = i; }
        }
        if (failure == null && act.Cts.IsCancellationRequested)
        {
            failure = ToolResult.Error("cancelled: " + (act.CancelReason ?? "shutdown"));
            failedAt = start;
        }
        return (failure, failedAt);
    }

    private async Task<ToolResult> RunStepAsync(Activity act, (IAgentTool Tool, JsonElement Input) step)
    {
        ToolResult r;
        using (StepScope?.Invoke())
            r = await _registry.ExecuteAsync(step.Tool, step.Input, act.Cts.Token).ConfigureAwait(false);
        if (act.Cts.IsCancellationRequested && r.Ok == false && act.CancelReason != null)
            r = ToolResult.Error("cancelled: " + act.CancelReason + "; " + r.Text);
        return r;
    }

    private void RaiseStepFinished(Activity act, (IAgentTool Tool, JsonElement Input) step, ToolResult r)
    {
        try { StepFinished?.Invoke(step.Tool.Name, step.Input, r, act.Cts.IsCancellationRequested ? act.CancelReason ?? "cancelled" : null); }
        catch (Exception) { }
    }

    private async Task FinishAsync(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results,
        ToolResult? failure, int failedAt)
    {
        if (CleanupFor(act) is { } cleanup)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using (StepScope?.Invoke()) act.CleanupReport = await cleanup(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { act.CleanupReport = "temporary cleanup deferred: " + ex.Message; }
        }
        bool post;
        Activity? queued;
        lock (_lock)
        {
            post = act.Detached && !act.Silent;
            queued = act.Queued is { Finished: false } q ? q : null;
        }
        try { PublishOutcome(act, steps, results, failure, failedAt, post, queued); }
        finally
        {
            // Publish the outcome before exposing an idle body or releasing a queued successor.
            lock (_lock)
            {
                act.Finished = true;
                if (ReferenceEquals(_current, act)) _current = act.Queued is { Finished: false } q ? q : null;
            }
            act.Done.TrySetResult(failure);
            try { act.Cts.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>The job cleanup to run after a job that nothing is queued behind and that wasn't superseded, else null.</summary>
    private Func<CancellationToken, Task<string?>>? CleanupFor(Activity act) =>
        act.Kind == ActivityKind.Job && CleanupJob is { } cleanup && act.Queued == null
        && act.CancelReason?.StartsWith("superseded", StringComparison.Ordinal) != true ? cleanup : null;

    private void PublishOutcome(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results,
        ToolResult? failure, int failedAt, bool post, Activity? queued)
    {
        if (!post) return;
        // A job is queued behind this move: it reports the outcome (a failed move = "not run: goto failed: ...").
        // A job queued with do{queue:true} runs even when this one failed (it doesn't require success): then nobody would report
        // this failure, so it is posted here.
        if (queued != null && act.Kind == ActivityKind.Move && (failure == null || queued.RequireSuccess)) return;
        var report = StepReport(act, steps, results);
        if (failure == null) PublishSuccess(act, report);
        else PublishFailure(act, steps, failure, failedAt, report);
    }

    /// <summary>Every completed step matters: a final planting result must not hide the excavation/tilling counts.</summary>
    private static string StepReport(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult?[] results)
    {
        var report = string.Join("; ", results.Select((r, i) => r == null ? null : $"{steps[i].Tool.Name}: {r.Text}").Where(s => s != null));
        if (act.CleanupReport is { Length: > 0 } cr) report += "; " + cr;
        return report;
    }

    private void PublishSuccess(Activity act, string report)
    {
        var text = $"#{act.Id} {act.Label}: {report}";
        // An arrival nobody waits on is context, not a reason to wake the brain (it already said what it's doing).
        bool quiet = false;
        try { quiet = QuietOutcome?.Invoke(text) == true; } catch (Exception) { }
        Post(act.Kind == ActivityKind.Move || quiet
            ? new GameEvent(EventKind.TaskCompleted, text, priority: EventPriority.Background)
            : GameEvent.TaskDone(text));
    }

    private void PublishFailure(Activity act, IReadOnlyList<(IAgentTool Tool, JsonElement Input)> steps, ToolResult failure, int failedAt, string report)
    {
        var reason = act.CancelReason;
        // The model asked for this (stop / new activity): it already knows — keep as quiet context only.
        bool modelInitiated = reason != null && (reason == "stop" || reason.StartsWith("superseded", StringComparison.Ordinal));
        bool wakes = reason == null; // plain failures wake; interruptions are explained by the interrupting event
        if (modelInitiated) wakes = false;
        var tool = failedAt >= 0 && failedAt < steps.Count ? steps[failedAt].Tool.Name : act.Label;
        var failText = $"#{act.Id} {tool}: {failure.Text}" + (report.Length > 0 ? $"; results: {report}" : "");
        bool quietFail = false;
        try { quietFail = wakes && QuietOutcome?.Invoke(failText) == true; } catch (Exception) { }
        if (quietFail) wakes = false;
        Post(GameEvent.TaskFail(failText, wakes) with { MustWake = wakes });
    }
}

/// <summary>Snapshot of the running activity (see <see cref="ActivityManager.Current"/>).</summary>
public sealed record RunningActivity(int Id, string Label, DateTimeOffset Started, IReadOnlyList<(string Tool, JsonElement Input)> Steps)
{
    public ActivityKind Kind { get; init; } = ActivityKind.Job;
}
