using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;
using MineClaw.Agent.Worker;
using MineClaw.Bot.Game;
using MineClaw.Scripting;
using MineClaw.Skills;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Wires the script engine (<see cref="ScriptController"/>) into the bot: ONE <c>script</c> tool for brain and worker, script
/// functions over the same gated game tools/skills the models use, the interrupt policy (danger/stop/addressed chat pause
/// the script and wake the brain with its state), long-running-tool semantics through the <see cref="ActivityManager"/>
/// (one body: a new activity supersedes the script, <c>stop</c> cancels it, background end → <c>[task ok/failed]</c>),
/// saved/versioned scripts on routines with run stats, promotion to catalog skills, and run_routine on the engine.
/// </summary>
public sealed class ScriptBridge
{
    /// <summary>Game tools exposed as script functions (positional params = the tool's schema order).</summary>
    public static readonly string[] ActionTools =
    [
        "goto", "follow", "mine", "harvest_generator", "collect_drops", "craft", "smelt", "collect_furnace", "deposit", "withdraw",
        "organize_chest", "place", "dig", "use_block", "eat", "chop_tree", "act",
    ];

    /// <summary>Never cut short by a pause/cancel: click sequences and single block actions finish, then the script stops.</summary>
    private static readonly HashSet<string> AtomicTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "craft", "smelt", "collect_furnace", "deposit", "withdraw", "organize_chest", "place", "dig", "use_block", "eat",
    };

    private static readonly AsyncLocal<bool> InWorker = new();
    private static readonly Regex StopWord = new(@"^\W*(?:stop|halt|freeze)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Link
    {
        public bool Attached;
        public int ActivityId = -1;
        public bool DetachedFromWorker;
        public bool PlannerEnded;
        public int BrainWaiters;
    }

    private readonly ConditionalWeakTable<ScriptSession, Link> _links = new();
    private readonly AgentRuntime _rt;
    private readonly IGameClient _client;
    private readonly SkillHub? _skills;
    private readonly ILogger? _log;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _actions;

    public ScriptController Controller { get; }
    public ScriptLibrary Library { get; }
    public ScriptOptions Options => Controller.Options;

    internal ScriptBridge(AgentRuntime rt, IGameClient client, SkillHub? skills, ScriptOptions options, TimeProvider time, ILogger? log)
    {
        _rt = rt;
        _client = client;
        _skills = skills;
        _log = log;
        _time = time;
        Library = new ScriptLibrary(rt.Memory);
        _actions = ActionTools.Where(rt.Tools.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Controller = new ScriptController(BuildHost(), options, time);
        Controller.Paused += OnPaused;
        Controller.Ended += OnEnded;
    }

    private Link L(ScriptSession s) => _links.GetValue(s, _ => new Link());

    // ------------------------------------------------------------------ host port

    private ScriptHost BuildHost()
    {
        var actions = new List<ScriptAction>();
        foreach (var name in ActionTools)
        {
            if (!_rt.Tools.TryGet(name, out var tool)) continue;
            var ps = new List<string>();
            if (tool.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject()) ps.Add(p.Name);
            actions.Add(new ScriptAction(name, ps, AtomicTools.Contains(name)));
        }
        return new ScriptHost
        {
            World = _client.World,
            Username = _client.Username,
            Actions = actions,
            Act = async (name, args, ct) =>
            {
                var r = await _rt.Tools.ExecuteAsync(name, args, ct).ConfigureAwait(false);
                return new ActResult(r.Ok, r.Text);
            },
            Skills = () => _skills?.Registry.Specs.Where(s => s.Status != SkillStatus.Stub && s.Name != "promote_routine").Select(s => s.Name).ToArray() ?? [],
            Skill = async (name, args, ct) =>
            {
                if (_skills == null) return ActResult.Fail("no skill catalog");
                var r = await _skills.RunAsync(name, MoveDefaults(name, args), ct).ConfigureAwait(false);
                return new ActResult(r.Ok, r.Text);
            },
            SkillSchema = name => _skills != null && _skills.Registry.TryGet(name, out var sk) ? sk.Spec.Schema : null,
            Wake = (s, reason) => PostOwn(GameEvent.Notice($"script wake: {reason} | {s.State()}", EventPriority.Normal)),
            SkillIsAtomic = name => _skills != null && _skills.Registry.TryGet(name, out var sk)
                && sk.Spec.Category is SkillCategory.Storage or SkillCategory.Items or SkillCategory.Smelting,
            Say = (text, ct) => ChatAsync(text, ct),
            Emote = (text, ct) => ChatAsync("/me " + text, ct),
            Ask = async (player, question, timeout, ct) =>
            {
                var text = player != null && !question.Contains(player, StringComparison.OrdinalIgnoreCase) ? $"{player}, {question}" : question;
                if (Muted(text) == null) return null;
                var wait = _rt.Chat.WaitForReplyAsync(player, timeout, ct);
                await _rt.Chat.SayAsync(text, audience: player != null ? [player] : null, ct: ct).ConfigureAwait(false);
                return await wait.ConfigureAwait(false);
            },
            WhereIs = async (item, ct) =>
            {
                var r = await _rt.Tools.ExecuteAsync("where_is", JsonSerializer.SerializeToElement(new { item }), ct).ConfigureAwait(false);
                return r.Ok ? r.Text : null;
            },
            NearestKnown = kind =>
            {
                if (_rt.Memory.GetWaypoint(kind) is { } w) return new BlockPos(w.X, w.Y, w.Z);
                var p = _client.World.Player.Position;
                foreach (var (row, _) in _rt.Observations.Nearest(kind, p.X, p.Y, p.Z, limit: 1, maxDistance: 128))
                    if (row.Bounds is { } b) return new BlockPos((int)b.MinX, (int)b.MinY, (int)b.MinZ);
                return null;
            },
            IsProtected = (p, id) => _client.IsProtected?.Invoke(p, id) ?? false,
            OnlinePlayers = () => _client.World.Entities.OnlinePlayers,
            // Scripts cost no tokens, but without a brain to supervise them they don't play either (same as worker tasks).
            ActionVeto = () => _rt.Usage.CheckMode() is BudgetMode.Paused or BudgetMode.CheapOnly ? "budget low" : null,
            Log = line => _log?.LogInformation("{Line}", line),
        };
    }

    /// <summary>A hive clone speaks only when addressed: its speech gate mutes script chat the same way.</summary>
    private string? Muted(string text) =>
        _rt.Agent.SpeechGate is { } gate ? gate(new EventBatch([]), text, false) : text;

    private async Task<ActResult> ChatAsync(string text, CancellationToken ct)
    {
        if (Muted(text) is not { } t) return ActResult.Fail("muted (hive clone speaks only when addressed)");
        var lines = await _rt.Chat.SayAsync(t, ct: ct).ConfigureAwait(false);
        return lines.Count > 0 ? ActResult.Success("said") : ActResult.Fail("not sent");
    }

    internal bool IsScriptable(string tool) => _actions.Contains(tool);

    /// <summary>move{{item="log",n="all",to="wood"}}: moves without a source come from the inventory.</summary>
    private static JsonElement MoveDefaults(string skill, JsonElement args)
    {
        if (skill != "move_items" || args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("moves", out var moves) || moves.ValueKind != JsonValueKind.Array)
            return args;
        var o = System.Text.Json.Nodes.JsonNode.Parse(args.GetRawText())!.AsObject();
        foreach (var m in o["moves"]!.AsArray())
            if (m is System.Text.Json.Nodes.JsonObject mo && !mo.ContainsKey("from")) mo["from"] = "inv";
        return JsonSerializer.SerializeToElement(o);
    }

    [ThreadStatic] private static bool _posting;

    /// <summary>Posts one of our own events (script wake/pause/end): the sleep gate lets it through untouched.</summary>
    private void PostOwn(GameEvent e)
    {
        _posting = true;
        try { _rt.Events.Post(e); }
        finally { _posting = false; }
    }

    // ------------------------------------------------------------------ the brain sleeps while a script runs

    /// <summary>"asleep (script cobble_loop 34%)" while a script runs, else "awake" (TUI header, BotSnapshot.Brain).</summary>
    public string BrainState()
    {
        if (Controller.Current is not { } s) return "awake";
        if (s.Status == ScriptStatus.Paused) return $"awake (script {s.Name} paused)";
        return $"asleep (script {s.Name}{(s.ProgressPct is { } p ? $" {p:0}%" : "")})";
    }

    /// <summary>
    /// <see cref="EventQueue.Gate"/>: while a script runs, only real wakes wake the brain — task ends, danger and other
    /// interrupt-priority events, chat addressed to this bot (a trusted "stop" too), script wake/needs_input/pause notices.
    /// Events the script's own on()/waitfor/ask takes don't wake it either. Everything else stays queued as non-waking
    /// context for the next real wake; idle check-ins are dropped.
    /// </summary>
    internal GameEvent? Gate(GameEvent e)
    {
        var s = Controller.Current;
        if (s == null || _posting) return e;
        var se = Map(e);
        if (se != null)
        {
            if (se.Kind is not ScriptEventKind.Stop and not ScriptEventKind.Join && s.Status != ScriptStatus.Paused && s.HasHandler(se))
            {
                s.Deliver(se);
                return e with { Wakes = false }; // the script handles it; brain stays asleep (it's context next time)
            }
            s.Deliver(se);
            if (se.Kind != ScriptEventKind.Join) return e;
        }
        if (s.Status == ScriptStatus.Paused) return e; // a paused script means the brain is awake
        if (e.Kind == EventKind.IdleTick) return null;
        if (e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed) return e;
        if (e.Wakes && e.Priority >= _rt.Options.InterruptPriority) return e;
        return e.Wakes ? e with { Wakes = false } : e;
    }

    // ------------------------------------------------------------------ events → interrupt policy

    private ScriptEvent? Map(GameEvent e)
    {
        if (e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed or EventKind.TaskProgress) return null;
        if (e.Kind == EventKind.PlayerJoined && e.Player != null) return new ScriptEvent(ScriptEventKind.Join, e.Text, e.Player);
        bool chat = e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote;
        if (chat)
        {
            if (!e.Wakes || e.Priority < EventPriority.Normal || e.Player == null) return null;
            if (string.Equals(e.Player, _client.Username, StringComparison.OrdinalIgnoreCase) || _rt.Chat.IsHiveBot?.Invoke(e.Player) == true) return null;
            bool trusted = _rt.Options.Self.TrustedPlayers.Any(p => string.Equals(p, e.Player, StringComparison.OrdinalIgnoreCase));
            return new ScriptEvent(trusted && StopWord.IsMatch(e.Text) ? ScriptEventKind.Stop : ScriptEventKind.Chat, e.Text, e.Player);
        }
        bool danger = e.Kind is EventKind.LowHealth or EventKind.VoidFall or EventKind.Death
            || (e.Wakes && e.Priority >= _rt.Options.InterruptPriority);
        return danger ? new ScriptEvent(ScriptEventKind.Danger, e.Format(), e.Player) : null;
    }

    private bool InterruptPending() =>
        _rt.Events.Snapshot().Any(e => e.Wakes && e.Priority >= _rt.Options.InterruptPriority && Map(e)?.Kind is ScriptEventKind.Danger);

    private void OnPaused(ScriptSession s)
    {
        var l = L(s);
        if (Volatile.Read(ref l.BrainWaiters) > 0 || s.PausedFor == PauseKind.Brain) return; // the brain's own call reports it
        bool wakes = s.PausedFor is PauseKind.Danger or PauseKind.Stop or PauseKind.Script;
        var text = "script paused: " + s.State() + (s.PausedFor == PauseKind.Chat ? $" (auto-resume {Options.ChatAutoResumeSeconds}s)"
            : s.PauseReason?.StartsWith("needs_input", StringComparison.Ordinal) == true ? " (answer: script{op:resume,code:<answer>})" : "");
        PostOwn(GameEvent.Notice(text, wakes ? EventPriority.Normal : EventPriority.Background));
    }

    private void OnEnded(ScriptSession s)
    {
        var l = L(s);
        _log?.LogInformation("script {Name} {Status}: {Actions} actions in {Seconds:0}s — {Outcome}", s.Name, s.Status, s.Actions, s.Elapsed.TotalSeconds, s.Outcome);
        try
        {
            var saved = Library.Get(s.Name);
            if (saved?.Script is { } code && code.Code == s.Code && s.Status != ScriptStatus.Cancelled)
            {
                var stats = Library.RecordRun(s.Name, s.Ok, s.Elapsed, s.Actions, s.Outcome ?? "");
                if (stats is { PromotedAs: null } && stats.Successes >= Options.PromoteAfterSuccesses) Promote(s.Name);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "script stats for {Name} failed", s.Name); }
        // Detached from a worker and never adopted as an activity: nobody else will report it.
        if (l.DetachedFromWorker && !l.Attached && !l.PlannerEnded)
            PostOwn(s.Ok ? GameEvent.TaskDone(s.Outcome ?? "") : GameEvent.TaskFail(s.Outcome ?? "", wakes: s.Status != ScriptStatus.Cancelled));
    }

    // ------------------------------------------------------------------ the tool

    internal async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var op = (ToolInput.Str(input, "op") ?? (ToolInput.Str(input, "code") != null ? "run" : "status")).Trim().ToLowerInvariant();
        switch (op)
        {
            case "status":
                return ToolResult.Success(Controller.Status());
            case "pause":
            {
                var s = Controller.Current;
                if (s == null) return ToolResult.Error("no script running");
                Controller.Pause();
                try { await s.WhenSettled(ct).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { }
                return ToolResult.Success((s.Status == ScriptStatus.Paused ? "paused: " : "pausing after current action: ") + s.State());
            }
            case "resume":
            {
                var s = Controller.Current;
                if (s == null) return ToolResult.Error("no script to resume");
                if (s.Status != ScriptStatus.Paused) return ToolResult.Success("not paused: " + s.State());
                // The answer to a script's ask("brain", q) rides in code (no extra schema field).
                s.Resume(ToolInput.Str(input, "code"));
                return await FollowAsync(s, ct).ConfigureAwait(false);
            }
            case "cancel":
            {
                var s = Controller.Current;
                if (s == null) return ToolResult.Error("no script running");
                L(s).PlannerEnded = true;
                s.Stop("cancelled by planner");
                try { await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
                catch (TimeoutException) { return ToolResult.Success($"cancelling script {s.Name} (an atomic action is finishing)"); }
                return ToolResult.Success(s.Outcome ?? "cancelled");
            }
            case "run" or "replace":
                return await RunOrReplaceAsync(op == "replace", input, ct).ConfigureAwait(false);
            default:
                return ToolResult.Error("op is run|status|pause|resume|cancel|replace");
        }
    }

    private async Task<ToolResult> RunOrReplaceAsync(bool replace, JsonElement input, CancellationToken ct)
    {
        var code = ToolInput.Str(input, "code");
        var name = ScriptController.CleanName(ToolInput.Str(input, "name") ?? (replace ? Controller.Current?.Name : null) ?? "script");
        if (string.IsNullOrWhiteSpace(code))
        {
            code = Library.CodeOf(name, IsScriptable);
            if (code == null) return ToolResult.Error(ToolInput.Str(input, "name") == null ? "need code (or the name of a saved script)" : $"no saved script '{name}'");
        }
        else if (ToolInput.Str(input, "name") != null && ToolInput.Bool(input, "save") != false)
        {
            // A named run with code is saved (versioned; same code = same version). save:false opts out.
            var r = Library.Save(name, code);
            _log?.LogInformation("script {Name} saved v{Version}", name, r.Script?.Version);
        }
        string? argsJson = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;
        var req = new ScriptRequest { Name = name, Code = code, ArgsJson = argsJson, ResumeFrom = ToolInput.Str(input, "resume_from") };
        if (replace && Controller.Current is { } old) L(old).PlannerEnded = true;
        var (s, err) = replace ? await Controller.ReplaceAsync(req, ct).ConfigureAwait(false) : Controller.Start(req);
        if (s == null) return ToolResult.Error(err ?? "can't start");
        return await FollowAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>Brain: the script becomes the bot's activity (inline wait, then background). Worker: inline, then detaches.</summary>
    private Task<ToolResult> FollowAsync(ScriptSession s, CancellationToken ct) =>
        InWorker.Value ? FollowInlineAsync(s, TimeSpan.FromSeconds(Options.WorkerInlineSeconds), ct) : FollowAsActivityAsync(s, TimeSpan.FromSeconds(_rt.Options.InlineToolWaitSeconds), ct);

    private async Task<ToolResult> FollowAsActivityAsync(ScriptSession s, TimeSpan inlineWait, CancellationToken ct)
    {
        var l = L(s);
        Interlocked.Increment(ref l.BrainWaiters);
        try
        {
            var r = await _rt.Activities.RunAsync([(new ScriptActivity(this, s), ToolSchema.Empty)], inlineWait, ct).ConfigureAwait(false);
            return r[0];
        }
        finally { Interlocked.Decrement(ref l.BrainWaiters); }
    }

    /// <summary>
    /// Waits for the script on the caller (worker task, skill, run_routine). Caller cancelled → pause if an interrupt is
    /// pending (the brain gets the state), else stop. <paramref name="detachAfter"/> elapsed → it keeps running and becomes
    /// the bot's activity when the worker's task ends.
    /// </summary>
    internal async Task<ToolResult> FollowInlineAsync(ScriptSession s, TimeSpan? detachAfter, CancellationToken ct)
    {
        using var reg = ct.Register(() => { if (!s.PausePending && !InterruptPending()) s.Stop("stopped"); });
        var settled = s.WhenSettled();
        var gate = detachAfter is { } d ? Task.Delay(d, _time) : Task.Delay(Timeout.Infinite, CancellationToken.None);
        await Task.WhenAny(settled, gate).ConfigureAwait(false);
        if (!settled.IsCompleted)
        {
            L(s).DetachedFromWorker = true;
            return ToolResult.Success($"running: script {s.Name} continues as the bot's task when your task ends ([task] event reports). " +
                $"Finish now: ok: started script {s.Name}");
        }
        if (s.Status == ScriptStatus.Paused) return ToolResult.Success("paused: " + s.State());
        return s.Ok ? ToolResult.Success(s.Outcome ?? "ok") : ToolResult.Error(s.Outcome ?? "failed");
    }

    /// <summary>Worker task ended: a detached script becomes the bot's activity (or is cancelled if something else took the body).</summary>
    internal void AdoptOrphan()
    {
        var s = Controller.Current;
        if (s == null) return;
        var l = L(s);
        if (!l.DetachedFromWorker || l.Attached) return;
        var cur = _rt.Activities.Current;
        if (cur != null && cur.Label != WorkerAgent.ToolName) { s.Stop("superseded by " + cur.Label); return; }
        if (s.Status == ScriptStatus.Paused) return; // stays paused for the brain
        _ = FollowAsActivityAsync(s, TimeSpan.Zero, CancellationToken.None);
    }

    /// <summary>
    /// Our activity's token fired: an interrupt (the gate already asked for a pause), a <c>stop</c>, or a new activity taking
    /// the body. The ActivityManager cancels before it swaps the current activity, so decide a moment later.
    /// </summary>
    internal void OnActivityCancelled(ScriptSession s, int myId)
    {
        if (s.PausePending || InterruptPending()) return;
        // stop already ended it (OnStopTool); shutdown cancels every activity; anything else is a new activity taking over.
        // The swap happens under the manager's lock right after this callback, so read the new label off-thread.
        _ = Task.Run(() =>
        {
            if (s.PausePending || InterruptPending()) return;
            var label = _rt.Activities.Current is { } c && c.Id != myId ? c.Label : "another task";
            s.Stop(_rt.Activities.Shutdown.IsCancellationRequested ? "shutdown" : "superseded by " + label);
        });
    }

    /// <summary><c>stop</c> also ends a paused or detached script.</summary>
    internal void OnStopTool()
    {
        if (Controller.Current is not { } s) return;
        L(s).PlannerEnded = true;
        s.Stop("stop");
    }

    /// <summary>The script as the ActivityManager's current activity.</summary>
    private sealed class ScriptActivity(ScriptBridge b, ScriptSession s) : IAgentTool
    {
        public string Name => ScriptPrompts.ToolName;
        public string Description => "";
        public JsonElement InputSchema => ToolSchema.Empty;
        public bool IsLongRunning => true;
        public ToolTiers Tiers => ToolTiers.None;

        public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            var l = b.L(s);
            int myId = b._rt.Activities.Current?.Id ?? -1;
            l.Attached = true;
            l.ActivityId = myId;
            l.DetachedFromWorker = false;
            s.OwnerLost = () => b._rt.Activities.Current is { } c && c.Id != myId ? "superseded by " + c.Label : null;
            try
            {
                using (ct.Register(() => b.OnActivityCancelled(s, myId)))
                    await s.WhenSettled().ConfigureAwait(false);
            }
            finally
            {
                s.OwnerLost = null;
                l.Attached = false;
            }
            if (s.Status == ScriptStatus.Paused)
            {
                if (Volatile.Read(ref l.BrainWaiters) > 0) return ToolResult.Success("paused: " + s.State());
                Silence(myId); // the pause notice (or the brain's own pause call) reports it
                return ToolResult.Error("paused");
            }
            if (l.PlannerEnded) Silence(myId);
            return s.Ok ? ToolResult.Success(s.Outcome ?? "ok") : ToolResult.Error(s.Outcome ?? "failed");
        }

        /// <summary>Marks our own activity as model-stopped so the ActivityManager posts no task event for it.</summary>
        private void Silence(int myId)
        {
            if (b._rt.Activities.Current?.Id == myId) b._rt.Activities.Cancel("stop");
        }
    }

    /// <summary>Same definition and position as the wrapped tool; runs code around it.</summary>
    internal sealed class Scope(IAgentTool inner, Func<JsonElement, CancellationToken, Func<Task<ToolResult>>, Task<ToolResult>> around) : IAgentTool
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public JsonElement InputSchema => inner.InputSchema;
        public bool IsLongRunning => inner.IsLongRunning;
        public bool AvailableToCheapTier => inner.AvailableToCheapTier;
        public ToolTiers Tiers => inner.Tiers;
        public int? MaxResultChars => inner.MaxResultChars;
        public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct) => around(input, ct, () => inner.ExecuteAsync(input, ct));
    }

    internal static async Task<ToolResult> InWorkerScope(Func<Task<ToolResult>> run, ScriptBridge b)
    {
        InWorker.Value = true;
        try { return await run().ConfigureAwait(false); }
        finally
        {
            InWorker.Value = false;
            b.AdoptOrphan();
        }
    }

    // ------------------------------------------------------------------ learned skills, routines

    /// <summary>A saved script with enough successful runs joins the skill catalog (zero prefix tokens; find_skill finds it).</summary>
    internal string? Promote(string name)
    {
        if (_skills == null) return null;
        var r = Library.Get(name);
        if (r?.Script is not { } sc) return null;
        var skill = PromotedSkill.SkillName(name);
        if (_skills.Registry.TryGet(skill, out var existing) && existing.Spec.Source != "script") skill = "script_" + skill;
        var summary = (r.Note ?? $"saved script {name} ({sc.Successes} ok runs)").Trim();
        if (summary.Length > 60) summary = summary[..57] + "...";
        _skills.Registry.Add(new PromotedSkill(new SkillSpec
        {
            Name = skill, Category = SkillCategory.Misc, Source = "script", Args = "args?", Summary = summary,
            Estimate = TimeSpan.FromSeconds(Math.Max(10, sc.AvgSeconds)), Keywords = name.Replace('_', ' ') + " script routine " + r.Note,
        }, async (_, args, ct) =>
        {
            var code = Library.CodeOf(name);
            if (code == null) return SkillResult.Fail($"script {name} was forgotten");
            var (s, err) = Controller.Start(new ScriptRequest { Name = name, Code = code, ArgsJson = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : null });
            if (s == null) return SkillResult.Fail(err ?? "can't start");
            var res = await FollowInlineAsync(s, null, ct).ConfigureAwait(false);
            return new SkillResult(res.Ok, res.Text);
        }));
        Library.MarkPromoted(name, skill);
        return skill;
    }

    internal void SyncPromoted()
    {
        if (_skills == null) return;
        foreach (var r in Library.Scripts())
            if (r.Script?.PromotedAs is { } sk && !(_skills.Registry.TryGet(sk, out var ex) && ex.Spec.Source == "script")) Promote(r.Name);
    }

    /// <summary>run_routine (and promoted routine skills) on the engine: saved scripts directly, step routines migrated.</summary>
    internal async Task<ToolResult?> RunRoutineAsync(Agent.Memory.Routine routine, int? repeat, CancellationToken ct)
    {
        var code = routine.Script is { Code.Length: > 0 } sc ? sc.Code : Library.Migrate(routine, IsScriptable)?.Script?.Code;
        if (code == null) return null; // a step the script API can't express: legacy replay
        var (s, err) = Controller.Start(new ScriptRequest
        {
            Name = routine.Name, Code = code, ArgsJson = repeat is { } n ? $"{{\"repeat\":{n}}}" : null,
        });
        if (s == null) return ToolResult.Error(err ?? "can't start");
        return await FollowInlineAsync(s, null, ct).ConfigureAwait(false);
    }

    /// <summary>"script x L12 3m" for the state header while a script exists (≈ 8 tokens).</summary>
    internal string? HeaderTag() =>
        Controller.Current is { } s ? $"script {s.Name} {(s.Status == ScriptStatus.Paused ? "PAUSED" : "L" + s.Line)} {ScriptText.Dur(s.Elapsed)}" : null;
}

public static class ScriptTools
{
    public const string ToolName = ScriptPrompts.ToolName;

    private static readonly ConditionalWeakTable<AgentRuntime, ScriptBridge> Bridges = new();

    /// <summary>The script bridge of a runtime (hosts, telemetry, TUI), or null.</summary>
    public static ScriptBridge? Of(AgentRuntime rt) => Bridges.TryGetValue(rt, out var b) ? b : null;

    /// <summary>"asleep (script x 34%)" / "awake" — the brain's sleep state for telemetry and the TUI header.</summary>
    public static string BrainState(AgentRuntime rt) => Of(rt)?.BrainState() ?? "awake";

    /// <summary>
    /// The single registration call (BotHost, after the brain split and the bridge so tiers and the state header stick):
    /// the <c>script</c> tool (brain + worker), the worker cheat-sheet, stop/delegate scopes, events, routines, learned skills.
    /// </summary>
    public static ScriptBridge Register(AgentRuntime rt, IGameClient client, SkillHub? skills = null, ScriptOptions? options = null,
        TimeProvider? time = null, ILogger? log = null)
    {
        var bridge = new ScriptBridge(rt, client, skills, options ?? new ScriptOptions(), time ?? TimeProvider.System, log);

        rt.Tools.Register(ToolName, ScriptPrompts.ToolDescription,
            ToolSchema.Create().Str("op", enumValues: ScriptPrompts.Ops).Str("code", required: false).Str("name", required: false)
                .Str("resume_from", required: false).Build(),
            bridge.ExecuteAsync, tiers: ToolTiers.Main | ToolTiers.Worker);
        rt.Worker?.ExtendSystem(ScriptPrompts.CheatSheet);

        if (rt.Tools.TryGet("stop", out var stop))
            rt.Tools.Replace(new ScriptBridge.Scope(stop, (_, _, run) => { bridge.OnStopTool(); return run(); }));
        if (rt.Tools.TryGet(WorkerAgent.ToolName, out var del))
            rt.Tools.Replace(new ScriptBridge.Scope(del, (_, _, run) => ScriptBridge.InWorkerScope(run, bridge)));

        var prevGate = rt.Events.Gate;
        rt.Events.Gate = prevGate == null ? bridge.Gate : e => prevGate(e) is { } kept ? bridge.Gate(kept) : null;
        Bridges.AddOrUpdate(rt, bridge);
        rt.Routines.ScriptRunner = bridge.RunRoutineAsync;
        if (skills != null)
        {
            bridge.SyncPromoted();
            var prev = skills.SyncLearned;
            skills.SyncLearned = () => { prev?.Invoke(); bridge.SyncPromoted(); };
        }
        var header = rt.Agent.StateHeader;
        rt.Agent.StateHeader = () =>
        {
            var h = header?.Invoke() ?? "";
            return bridge.HeaderTag() is { } tag ? (h.Length > 0 ? h + " | " + tag : tag) : h;
        };
        return bridge;
    }
}
