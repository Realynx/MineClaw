using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// Tool execution for the brain loop: instant tools in parallel; long-running ones (and <c>do</c> chains) as the bot's
/// activity; the body lock (a running script answers body tools "body busy" unless queued); non-blocking moves
/// (goto returns at once, jobs queue behind it); streaming execution (a movement or read-only lookup starts the moment its
/// tool_use block is complete, before the message ends).
/// </summary>
public sealed partial class BotAgent
{
    /// <summary>Read-only lookups: safe to start early (streaming) and always free while a script holds the body.</summary>
    public static readonly IReadOnlySet<string> ReadOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "status", "inventory", "look", "where_is", "recall", "recall_detail", "list_memory", "see", "window", "combat_status",
        "activity", "describe_structure",
    };

    /// <summary>
    /// Tools that drive the body (movement, dig/place, containers, combat, gestures, worker jobs). While a script holds the
    /// body they answer "body busy" unless queued; everything else (chat, memory, look/see/status, hive, ask_player, script
    /// ops, profiles) stays free.
    /// </summary>
    public static bool IsBodyTool(string name) =>
        LatencyTracker.BodyTools.Contains(name) || name is "delegate" or "smelt" or "collect_drops" or "stop_spar";

    /// <summary>
    /// A clone spawned for work (<c>hive op=spawn|handover</c>) walks out like a job: it goes through the same body-safety check
    /// (live 10-29 night: three clones died with no food, main re-spawned 67, 420 and 1337 into the same night, all pinned at
    /// once by "night + no food").
    /// </summary>
    public static bool IsCloneSpawn(string tool, JsonElement input) =>
        tool == "hive" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("op", out var op)
        && op.ValueKind == JsonValueKind.String && op.GetString() is "spawn" or "handover";

    /// <summary>
    /// Who holds the body when it isn't free for brain body tools, e.g. "script cobble_loop 40%" (null = free). A running
    /// (not paused) script; the brain sees it as "[body: ...]" in each turn header.
    /// </summary>
    public Func<string?>? BodyHolder { get; set; }

    /// <summary>
    /// The body reflex's OK for a new brain body job (zero tokens): given the call's tools and inputs, a one-line refusal or
    /// null. Live 10-28 16:21: respawned at night with no food, main walked west to the tower floor and died again; the bot
    /// layer refuses jobs that leave home ground at night (or with hostiles near) while there is no food on hand, and starts
    /// the food reflex ("night + no food: getting food/staying in first …").
    /// </summary>
    public Func<IReadOnlyList<(string Tool, JsonElement Input)>, string?>? BodySafety { get; set; }

    private string? BodyHolderNow()
    {
        try { return BodyHolder?.Invoke() is { Length: > 0 } h ? h : null; }
        catch (Exception ex) { _log.LogDebug(ex, "body holder failed"); return null; }
    }

    /// <summary>Who holds <paramref name="body"/> (null/main = <see cref="BodyHolderNow"/>, as before).</summary>
    private string? HolderOf(string? body)
    {
        if (IsMainBody(body) || ActiveBodies is not { } dir) return BodyHolderNow();
        try { return dir.Holder(body!) is { Length: > 0 } h ? h : null; }
        catch (Exception ex) { _log.LogDebug(ex, "body holder failed"); return null; }
    }

    private static bool IsMainBody(string? body) => body == null || body.Equals("main", StringComparison.OrdinalIgnoreCase);

    private sealed class Planned
    {
        public required LlmToolUse Use;
        public ToolResult? Result;
        public readonly List<(IAgentTool Tool, JsonElement Input)> Steps = new();
        /// <summary>A <c>do</c> (or a direct macro call): its step results are combined into one.</summary>
        public bool Compound;
        public bool Queue;
        public string? Prefix;
        /// <summary>One brain, many bodies: the body this call acts on (short name; null = unrouted, the main body as before).</summary>
        public string? Body;
        public bool AllInstant => Steps.Count > 0 && Steps.All(s => !s.Tool.IsLongRunning);
    }

    private readonly ConcurrentDictionary<string, (Task<ToolResult> Result, bool Movement)> _early = new();
    private int _earlyChains;

    private Planned Plan(LlmToolUse u, ModelTier tier, bool retry, bool stopping, ICollection<string> queuedSigs, string? body = null)
    {
        var p = new Planned { Use = u, Body = body };
        if (!PlanSteps(p, u, tier)) return p;
        if (BodyRefusal(p, body) is { } refusal)
        {
            p.Result = ToolResult.Error(refusal);
            return p;
        }
        if (p.AllInstant) return p;
        HoldBackRepeat(p, body, retry, stopping, queuedSigs);
        return p;
    }

    /// <summary>Resolves the call's tools into <see cref="Planned.Steps"/> (a <c>do</c> chain or a direct macro, else one tool). False = refused.</summary>
    private bool PlanSteps(Planned p, LlmToolUse u, ModelTier tier)
    {
        bool isDo = u.Name.Equals(DoTool.ToolName, StringComparison.OrdinalIgnoreCase);
        // A brain macro called directly (its definition lives in `do` now): run it the same way.
        bool direct = !isDo && tier == ModelTier.Main && DoTool.Actions.Contains(u.Name, StringComparer.OrdinalIgnoreCase)
            && _tools.TryGet(u.Name, out var dt) && !_tools.Allows(dt, tier);
        return isDo || direct ? PlanCompound(p, u, tier, isDo) : PlanSingle(p, u, tier);
    }

    private bool PlanCompound(Planned p, LlmToolUse u, ModelTier tier, bool isDo)
    {
        if (isDo && (!_tools.TryGet(DoTool.ToolName, out var doTool) || !_tools.Allows(doTool, tier)))
            return Refuse(p, tier == ModelTier.Cheap ? "not available in quick mode" : "'do' is not available here");
        string? err = null;
        var steps = isDo ? DoTool.Expand(u.Input, out err) : [(u.Name, u.Input)];
        if (err != null) return Refuse(p, err);
        p.Compound = true;
        p.Queue = isDo && DoTool.Queue(u.Input);
        foreach (var (name, input) in steps)
        {
            if (!_tools.TryGet(name, out var t)) return Refuse(p, $"do: '{name}' isn't available here");
            p.Steps.Add((t, input));
        }
        return true;
    }

    private bool PlanSingle(Planned p, LlmToolUse u, ModelTier tier)
    {
        if (!_tools.TryGet(u.Name, out var tool))
            return Refuse(p, u.Name == EscalateToolName ? "can't escalate right now" : $"unknown tool '{u.Name}'");
        if (!_tools.Allows(tool, tier))
            return Refuse(p, tier == ModelTier.Cheap ? "not available in quick mode" : $"'{u.Name}' is a worker tool: use do or delegate");
        p.Steps.Add((tool, u.Input));
        return true;
    }

    private static bool Refuse(Planned p, string error)
    {
        p.Result = ToolResult.Error(error);
        return false;
    }

    /// <summary>
    /// Body lock: a running script owns the body; the brain decides explicitly (pause/replace it, or queue behind it). Then the
    /// host's body-safety check on the main body. Null = the body may act.
    /// </summary>
    private string? BodyRefusal(Planned p, string? body)
    {
        if (!p.Queue && p.Steps.Any(s => IsBodyTool(s.Tool.Name)) && HolderOf(body) is { } holder)
            return $"body busy: {holder}. Use script op=pause or op=replace, or queue it: do{{...,queue:true}} runs after it";
        if (!IsMainBody(body) || BodySafety is not { } safety || !p.Steps.Any(s => IsBodyTool(s.Tool.Name) || IsCloneSpawn(s.Tool.Name, s.Input)))
            return null;
        string? no = null;
        try { no = safety(p.Steps.Select(s => (s.Tool.Name, s.Input)).ToList()); } catch (Exception ex) { _log.LogDebug(ex, "body safety check failed"); }
        return no;
    }

    /// <summary>Same call (or same target) already running, or it just failed there: say so instead of re-issuing.</summary>
    private void HoldBackRepeat(Planned p, string? body, bool retry, bool stopping, ICollection<string> queuedSigs)
    {
        int first = p.Steps.FindIndex(s => s.Tool.IsLongRunning);
        var (ft, fi) = p.Steps[first];
        ToolResult? held = null;
        try
        {
            held = IsMainBody(body) || ActiveBodies is not { } dir ? Guard.Check(ft.Name, fi, retry, stopping)
                : Guard.Check(ft.Name, fi, retry, stopping, dir.GuardKey(body!), dir.Activities(body!));
        }
        catch (Exception ex) { _log.LogDebug(ex, "tool guard failed"); }
        if (held == null && queuedSigs.Contains(SigOf(body, ft.Name, fi))) held = ToolResult.Success("already queued in this turn");
        if (held == null) return;
        // The walk is already under way (a reflex, an earlier call): keep it and queue the rest behind it.
        if (held.Ok && p.Steps.Count > first + 1 && ActivityManager.MovementTools.Contains(ft.Name))
        {
            p.Prefix = held.Text;
            p.Steps.RemoveAt(first);
        }
        else p.Result = held;
    }

    private async Task<IReadOnlyList<LlmToolResult>> ExecuteToolsAsync(List<LlmToolUse> uses, ModelTier tier, bool retry, CancellationToken ct)
    {
        bool stopping = uses.Any(u => u.Name == "stop");
        if (stopping) Orders?.Snooze();
        var results = new ToolResult?[uses.Count];
        var tasks = new List<Task>();
        // Every planned call: one per use, or one per body when a use targets several (body:["67","69"] / "all").
        var slots = PlanRound(uses, tier, retry, stopping, results, tasks, ct);
        var slotResults = new ToolResult?[slots.Count];
        StartRound(slots, slotResults, tasks, ct);

        await Task.WhenAll(tasks).ConfigureAwait(false);

        CollectUseResults(uses.Count, slots, slotResults, results);
        return uses.Select((u, i) => ToLlmResult(u, results[i])).ToList();
    }

    /// <summary>Plans every use of the round in order (an early-started use just awaits its start); returns the planned calls.</summary>
    private List<(int Use, Planned P)> PlanRound(List<LlmToolUse> uses, ModelTier tier, bool retry, bool stopping, ToolResult?[] results,
        List<Task> tasks, CancellationToken ct)
    {
        var slots = new List<(int Use, Planned P)>();
        var sigs = new List<string>();
        for (int i = 0; i < uses.Count; i++)
        {
            var u = uses[i];
            int idx = i;
            NoteRoundSequence(u.Name);
            if (_early.TryRemove(u.Id, out var early))
            {
                tasks.Add(Task.Run(async () => results[idx] = await early.Result.ConfigureAwait(false), ct));
                continue;
            }
            foreach (var p in PlanAll(u, tier, retry, stopping, sigs))
            {
                slots.Add((i, p));
                if (p.Result == null && !p.AllInstant)
                    sigs.AddRange(p.Steps.Where(s => s.Tool.IsLongRunning).Select(s => SigOf(p.Body, s.Tool.Name, s.Input)));
            }
        }
        return slots;
    }

    private void NoteRoundSequence(string name)
    {
        if (!ReadOnlyTools.Contains(name) && !IsBodyTool(name) && name != DoTool.ToolName) Latency?.NoteSequence(name);
    }

    /// <summary>Starts the round: held results stand, instant calls run at once, long-running calls form one activity chain per body.</summary>
    private void StartRound(List<(int Use, Planned P)> slots, ToolResult?[] slotResults, List<Task> tasks, CancellationToken ct)
    {
        // One activity per body for every long-running call of the round, in order (unrouted calls: main's, as before).
        var chains = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < slots.Count; k++)
        {
            var p = slots[k].P;
            int kk = k;
            if (p.Result != null) { slotResults[k] = p.Result; continue; }
            if (p.AllInstant)
            {
                tasks.Add(Task.Run(async () => slotResults[kk] = await RunInstantAsync(p, ct).ConfigureAwait(false), ct));
                continue;
            }
            var key = IsMainBody(p.Body) ? "main" : p.Body!;
            if (!chains.TryGetValue(key, out var list)) chains[key] = list = new List<int>();
            list.Add(k);
        }
        if (chains.Count >= 2 && ActiveBodies is { } crew) NotifyCrewStarted(crew, chains.Keys.ToList());
        foreach (var (key, list) in chains) tasks.Add(StartChain(key, list, slots, slotResults, ct));
    }

    private void NotifyCrewStarted(global::MineClaw.Agent.Bodies.IBodyDirectory crew, List<string> bodies)
    {
        try { crew.Started(bodies); } catch (Exception ex) { _log.LogDebug(ex, "bodies: crew start hook failed"); }
    }

    private Task StartChain(string key, List<int> list, List<(int Use, Planned P)> slots, ToolResult?[] slotResults, CancellationToken ct)
    {
        var acts = key == "main" ? _activities : ActiveBodies?.Activities(key) ?? _activities;
        var parts = list.Select(k => slots[k].P).ToList();
        return Task.Run(async () =>
        {
            var r = await RunChainAsync(parts, ct, acts).ConfigureAwait(false);
            for (int n = 0; n < list.Count; n++) slotResults[list[n]] = r[n];
        }, ct);
    }

    /// <summary>Each use's result: its one planned call's, or every body's combined; an early start already has its own.</summary>
    private static void CollectUseResults(int uses, List<(int Use, Planned P)> slots, ToolResult?[] slotResults, ToolResult?[] results)
    {
        for (int i = 0; i < uses; i++)
        {
            if (results[i] != null) continue; // an early start
            var mine = Enumerable.Range(0, slots.Count).Where(k => slots[k].Use == i).ToList();
            if (mine.Count == 1) results[i] = slotResults[mine[0]];
            else if (mine.Count > 1)
                results[i] = CombineBodies(mine.Select(k => (slots[k].P.Body ?? "main", slotResults[k] ?? ToolResult.Error("no result"))).ToList());
        }
    }

    private LlmToolResult ToLlmResult(LlmToolUse u, ToolResult? result)
    {
        var r = result ?? ToolResult.Error("no result");
        _log.LogDebug("tool {Name} → {Result}", u.Name, r);
        return new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok) { Image = r.Image };
    }

    /// <summary>A capped reply cannot authorize new work. Keep real early outcomes; movement is not a rollback.</summary>
    private async Task<IReadOnlyList<LlmToolResult>> TruncatedToolsAsync(List<LlmToolUse> uses)
    {
        var results = new List<LlmToolResult>(uses.Count);
        foreach (var u in uses)
        {
            if (!_early.TryRemove(u.Id, out var early))
            {
                results.Add(new LlmToolResult(u.Id, "not executed: response truncated (max_tokens)", true));
                continue;
            }
            var r = await early.Result.ConfigureAwait(false);
            if (early.Movement)
            {
                // Await the early start before cancelling: a queued Task must not start a walk after cancellation.
                _activities.Cancel("response truncated (max_tokens)");
                Interlocked.Exchange(ref _earlyChains, 0);
                results.Add(new LlmToolResult(u.Id, r.Text + "; response truncated: movement already started; cancellation requested, any movement so far was not undone", true));
            }
            else results.Add(new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok) { Image = r.Image });
        }
        return results;
    }

    /// <summary>The in-turn dedupe signature: per body, so 67's and 69's identical jobs are two jobs.</summary>
    private static string SigOf(string? body, string tool, JsonElement input) =>
        IsMainBody(body) ? ToolCallGuard.Signature(tool, input) : body + "|" + ToolCallGuard.Signature(tool, input);

    /// <summary>"67: ok ...; 69: failed ..." (ok only when every body's part was).</summary>
    private static ToolResult CombineBodies(IReadOnlyList<(string Body, ToolResult R)> parts)
    {
        var text = string.Join("; ", parts.Select(p => $"{p.Body}: {(p.R.Text.Length == 0 ? (p.R.Ok ? "ok" : "error") : p.R.Text)}"));
        return parts.All(p => p.R.Ok) ? ToolResult.Success(text) : ToolResult.Error(text);
    }

    private async Task<ToolResult> RunInstantAsync(Planned p, CancellationToken ct)
    {
        // A routed call acts on its body (the Bot layer's BodyScope); unrouted = as before.
        using var scope = p.Body != null ? ActiveBodies?.Push(p.Body) : null;
        if (!p.Compound && p.Steps.Count == 1) return await _tools.ExecuteAsync(p.Steps[0].Tool, p.Steps[0].Input, ct).ConfigureAwait(false);
        var parts = new List<ToolResult>();
        foreach (var (tool, input) in p.Steps)
        {
            var r = await _tools.ExecuteAsync(tool, input, ct).ConfigureAwait(false);
            parts.Add(r);
            if (!r.Ok) break;
        }
        return Combine(parts, p.Prefix);
    }

    private static readonly Regex BackgroundTask = new(@"^running in background as task #(?<id>\d+)", RegexOptions.Compiled);

    /// <summary>One activity for every long-running call of the round, in order; a pure walk runs non-blocking.</summary>
    private async Task<IReadOnlyList<ToolResult>> RunChainAsync(IReadOnlyList<Planned> parts, CancellationToken ct, ActivityManager? on = null)
    {
        var activities = on ?? _activities;
        var steps = parts.SelectMany(p => p.Steps).ToList();
        var bs = _options.Brainstem;
        bool walk = bs is { Enabled: true, NonBlockingMoves: true } && steps.All(s => s.Tool.Name.Equals("goto", StringComparison.OrdinalIgnoreCase));
        var kind = walk ? ActivityKind.Move : ActivityKind.Job;
        bool queue = parts.Any(p => p.Queue);
        // Walking, queued behind a walk, or queued behind a busy script: answer quickly; the brain keeps planning.
        bool behind = bs.Enabled && QueuedBehind(queue, activities, steps);
        // Long jobs (chop, harvest, delegate, script...) answer "running in background" at once instead of holding the turn
        // (and every chat line after them) for the old 20 s; short jobs (craft, withdraw) get a few seconds to finish inline.
        bool longJob = steps.Any(s => bs.LongJobTools.Contains(s.Tool.Name, StringComparer.OrdinalIgnoreCase));
        var wait = InlineWait(bs, walk || behind || longJob);
        var r = await activities.RunAsync(steps, wait, ct, kind, queue).ConfigureAwait(false);
        return ResultsByPart(parts, r, walk);
    }

    /// <summary>The chain's step results handed back per planned call (a compound or prefixed call's steps combined).</summary>
    private static List<ToolResult> ResultsByPart(IReadOnlyList<Planned> parts, IReadOnlyList<ToolResult> r, bool walk)
    {
        var output = new List<ToolResult>(parts.Count);
        int k = 0;
        foreach (var p in parts)
        {
            var slice = r.Skip(k).Take(p.Steps.Count).Select(x => walk ? MoveText(x) : x).ToList();
            k += p.Steps.Count;
            output.Add(p.Compound || slice.Count > 1 || p.Prefix != null ? Combine(slice, p.Prefix) : slice[0]);
        }
        return output;
    }

    /// <summary>Queued, behind a walk (or speculative step), or a "walk there, then work" chain.</summary>
    private static bool QueuedBehind(bool queue, ActivityManager activities, List<(IAgentTool Tool, JsonElement Input)> steps) =>
        queue || activities.CurrentKind is ActivityKind.Move or ActivityKind.Speculative
        || steps[0].Tool.Name.Equals("goto", StringComparison.OrdinalIgnoreCase);

    /// <summary>How long a chain may run inline: the legacy wait with the brainstem off, else short for quick answers, else the job wait.</summary>
    private TimeSpan InlineWait(Brainstem.BrainstemOptions bs, bool quick) =>
        !bs.Enabled ? TimeSpan.FromSeconds(_options.InlineToolWaitSeconds)
        : quick ? TimeSpan.FromMilliseconds(Math.Max(0, bs.MoveInlineWaitMs))
        : TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(bs.JobInlineWaitMs, _options.InlineToolWaitSeconds * 1000)));

    /// <summary>A walk that outlived the short inline wait: say so, and that the brain may keep planning.</summary>
    private static ToolResult MoveText(ToolResult r) =>
        BackgroundTask.Match(r.Text) is { Success: true } m
            ? ToolResult.Success($"walking there (task #{m.Groups["id"].Value}): keep planning; job actions you call now run on arrival; a failure wakes you")
            : r;

    private static ToolResult Combine(IReadOnlyList<ToolResult> parts, string? prefix)
    {
        var texts = parts.Select(x => x.Text).Where(t => t.Length > 0).ToList();
        if (prefix != null) texts.Insert(0, prefix);
        var text = string.Join("; then ", texts);
        return parts.All(x => x.Ok) ? ToolResult.Success(text) : ToolResult.Error(text);
    }

    /// <summary>
    /// Streaming execution: the hook handed to the provider for this call. Read-only lookups start at once; the first
    /// body call of the message starts too when it is only cancellable movement (<c>goto</c>/<c>follow</c>):
    /// walking begins while the model is still writing. Effectful tails wait for the message (order is kept).
    /// </summary>
    private Action<LlmToolUse>? EarlyStarter(ModelTier tier, bool retry, string model, CancellationToken ct)
    {
        var bs = _options.Brainstem;
        if (bs is not { Enabled: true, StreamingExecution: true } || tier != ModelTier.Main) return null;
        bool blocked = false;
        return use => blocked = EarlyStart(use, blocked, tier, retry, model, ct);
    }

    /// <summary>One streamed tool call: starts it early when it may; returns whether later calls of the message must wait.</summary>
    private bool EarlyStart(LlmToolUse use, bool blocked, ModelTier tier, bool retry, string model, CancellationToken ct)
    {
        try
        {
            if (blocked || use.Name is EscalateToolName or "stop") return true;
            // Several bodies: a call for another body (or a turn focused on one) waits for the message (routed planning).
            if (ActiveBodies != null && (TurnFocus != null || BodySpec(use.Input) != null)) return true;
            using (ToolCaller.Push(tier, model))
                return StartPlannedEarly(use, Plan(use, tier, retry, stopping: false, Array.Empty<string>()), ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "early tool start failed"); return true; }
    }

    /// <summary>Read-only lookups start at once; a pure walk starts and blocks the rest; anything else just blocks.</summary>
    private bool StartPlannedEarly(LlmToolUse use, Planned p, CancellationToken ct)
    {
        if (p.Result != null) return !ReadOnlyTools.Contains(use.Name);
        if (p.AllInstant && p.Steps.All(s => ReadOnlyTools.Contains(s.Tool.Name)))
        {
            _early[use.Id] = (Task.Run(() => RunInstantAsync(p, ct), ct), false);
            return false;
        }
        // From here on nothing starts early: tool order is the model's order.
        if (IsPlainWalk(p))
        {
            Interlocked.Increment(ref _earlyChains);
            _log.LogDebug("streaming: started {Tool} before the message ended", use.Name);
            _early[use.Id] = (Task.Run(async () => (await RunChainAsync([p], ct).ConfigureAwait(false))[0], ct), true);
        }
        return true;
    }

    /// <summary>Only cancellable movement (no build, no void, no parkour into the void), not queued.</summary>
    private static bool IsPlainWalk(Planned p) =>
        !p.Queue && p.Steps.Count > 0 && p.Steps.All(s => s.Tool.IsLongRunning && ActivityManager.MovementTools.Contains(s.Tool.Name)
            && s.Input.Bool("build") != true && s.Input.Str("into_void") == null && s.Input.Str("allow_parkour") != "void");

    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);

    /// <summary>The exact event instance (its queue time included): a player re-typing the same line is a new event.</summary>
    private static string HandledKey(Events.GameEvent e) => $"{(int)e.Kind}|{e.Player}|{e.Text}|{e.At.UtcTicks}";

    /// <summary>Every waking event of the batch was already in the previous turn (e.g. injected mid-turn): nothing new.</summary>
    private bool AlreadyHandled(Events.EventBatch batch)
    {
        var waking = batch.Waking.ToList();
        if (waking.Count == 0 || batch.HasDanger) return false;
        lock (_handled) return _handled.Count > 0 && waking.All(e => _handled.Contains(HandledKey(e)));
    }

    /// <summary>A cancelled (restarted) call may have started a walk early: stop it; the restarted turn decides again.</summary>
    private void DropEarlyStarts(string reason)
    {
        if (Interlocked.Exchange(ref _earlyChains, 0) > 0) _activities.Cancel(reason);
        _early.Clear();
    }
}
