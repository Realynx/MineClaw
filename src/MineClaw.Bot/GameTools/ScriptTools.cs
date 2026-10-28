using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Authoring;
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
        AcquireTool.ToolName, // hidden tool: ensure{} / acquire{"pickaxe"} through the acquisition planner
        Blueprints.BlueprintWiring.BuildTool, Blueprints.BlueprintWiring.InfoTool, // hidden: build{bp, part, of, layer} / blueprint(id)
    ];

    /// <summary>Never cut short by a pause/cancel: click sequences and single block actions finish, then the script stops.</summary>
    private static readonly HashSet<string> AtomicTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "craft", "smelt", "collect_furnace", "deposit", "withdraw", "organize_chest", "place", "dig", "use_block", "eat",
    };

    private static readonly AsyncLocal<bool> InWorker = new();
    private static readonly Regex StopWord = new(@"^\W*(?:stop|halt|freeze|wait|hold on|hold up)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        Library = new ScriptLibrary(rt.Memory)
        {
            Now = time.GetUtcNow, MinRuns = options.PromoteMinRuns, MinRate = options.PromoteMinSuccessRate,
            AutoRevertMinRuns = options.AutoRevertMinRuns,
        };
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
            Muted = text => Muted(text) == null,
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
        // Same last line of defence as the brain's speech: plumbing and pure data dumps ("4,64,6 cobblestone") never reach chat.
        if (!t.StartsWith("/me ", StringComparison.Ordinal))
        {
            var kept = Agent.Chat.MetaSpeech.Strip(t, out var meta);
            foreach (var m in meta) _log?.LogInformation("[meta suppressed] {Line}", m);
            if (string.IsNullOrWhiteSpace(kept)) return ActResult.Success("logged, not said: data isn't chat (use log() for debugging)");
            t = kept;
        }
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
        // A hive clone on a profile says so ("asleep (script cobble_loop 42%, profile haiku)").
        var prof = _rt.Options.Llm.MainProfile is { } mp ? ", profile " + mp : "";
        if (Controller.Current is not { } s) return IdleState?.Invoke() is { } idle ? $"{idle}{(prof.Length > 0 ? $" ({prof[2..]})" : "")}" : "awake";
        if (s.Status == ScriptStatus.Paused) return $"awake (script {s.Name} paused{prof})";
        return $"asleep (script {s.Name}{(s.ProgressPct is { } p ? $" {p:0}%" : "")}{prof})";
    }

    /// <summary>Brain state while no script runs (a hive clone sleeping between scripts: "asleep"); null = awake.</summary>
    public Func<string?>? IdleState { get; set; }

    /// <summary>
    /// Main's side of <c>hive spawn</c> with a script: a saved name must exist; inline code (already authorised as the Opus
    /// brain's by the hive tool's policy) is linted here — errors block the spawn and come back as the findings — and saved
    /// under <paramref name="name"/> with the caller as author, so the clone runs it by name (zero tokens, standing order).
    /// </summary>
    public (string? Name, string? Error, string? Code) PrepareForHive(string? name, string? code, string? author)
    {
        // Unnamed inline code: a name from its hash, so two clones' different code never share one script (same code = same name).
        var reference = CleanRef(!string.IsNullOrWhiteSpace(name) ? name
            : "hive_" + (string.IsNullOrWhiteSpace(code) ? "script" : Agent.Rag.VectorMath.Hash(code)[..6].ToLowerInvariant()));
        var clean = ScriptLibrary.ParseRef(reference).Name;
        if (string.IsNullOrWhiteSpace(code))
        {
            var v = Library.Resolve(reference, IsScriptable, out var why);
            return v == null ? (null, why ?? $"no saved script '{clean}'", null) : (clean, null, v.Code);
        }
        var lint = Lint(code);
        if (lint.HasErrors) return (null, lint.Format(LintSeverity.Error, 300), null); // "lint errors (fix and resubmit): ..."
        var res = Library.SaveVersion(clean, code, author: author, purpose: ScriptLint.Purpose(code), argNames: ScriptLint.ArgNames(code));
        _log?.LogInformation("hive script {Name} saved v{Version} by {Author}", clean, res.Routine?.Script?.Version, author ?? "c#");
        return (clean, null, code);
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
        if (e.MustWake) return e with { Wakes = true }; // a background failure/stall: the sleeping brain must hear it
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
            // "claudechan stop" / "wait!" / "both of you stop": the bot's name and filler don't hide a trusted stop.
            string[] names = [_rt.Chat.Username, _rt.Chat.DisplayName, .. _rt.Chat.Aliases];
            bool stop = trusted && (StopWord.IsMatch(e.Text) || StopWord.IsMatch(Agent.Brainstem.IntentMatcher.Normalize(e.Text, names))
                || Agent.Brainstem.IntentMatcher.Reflex(e.Text, names).Kind == Agent.Brainstem.IntentKind.Stop);
            return new ScriptEvent(stop ? ScriptEventKind.Stop : ScriptEventKind.Chat, e.Text, e.Player);
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
        _log?.LogInformation("script {Name}{Version} by {Author} {Status}: {Actions} actions in {Seconds:0}s — {Outcome}", s.Name,
            s.Request.Version is { } v ? " v" + v : "", s.Request.Author ?? "c#", s.Status, s.Actions, s.Elapsed.TotalSeconds, s.Outcome);
        try
        {
            var saved = Library.Get(s.Name);
            if (saved?.Script is { } sc && s.Status != ScriptStatus.Cancelled)
            {
                ScriptLibrary.Active(sc);
                // Stats go to the version that ran (by number, else by identical code); ad-hoc code under a saved name doesn't count.
                var ran = s.Request.Version is { } rv ? sc.Versions.FirstOrDefault(x => x.V == rv) : sc.Versions.LastOrDefault(x => x.Code == s.Code);
                if (ran != null && ran.Code == s.Code)
                {
                    var stats = Library.RecordRun(s.Name, ran.V, s.Ok, s.Elapsed, s.Actions, s.Outcome ?? "", s.ItemsGained, s.Reason, out var revert);
                    if (revert != null)
                    {
                        _log?.LogWarning("{Note}", revert);
                        PostOwn(GameEvent.Notice("script " + revert, EventPriority.Background));
                    }
                    if (stats is { PromotedAs: null } && Library.Proven(ScriptLibrary.Active(stats))) Promote(s.Name);
                }
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "script stats for {Name} failed", s.Name); }
        MaybeHousekeep();
        // Detached from a worker and never adopted as an activity: nobody else will report it.
        if (l.DetachedFromWorker && !l.Attached && !l.PlannerEnded)
            PostOwn(s.Ok ? GameEvent.TaskDone(s.Outcome ?? "") : GameEvent.TaskFail(s.Outcome ?? "", wakes: s.Status != ScriptStatus.Cancelled));
    }

    // ------------------------------------------------------------------ the tool

    internal async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var op = DefaultOp(input);
        switch (op)
        {
            case "show":
            {
                var n = ToolInput.Str(input, "name");
                if (string.IsNullOrWhiteSpace(n)) return ToolResult.Error("show needs name");
                var v = Library.Resolve(CleanRef(n), IsScriptable, out var why);
                if (v == null) return ToolResult.Error(why ?? $"no saved script '{n}'");
                return ToolResult.Success($"{ScriptLibrary.ParseRef(CleanRef(n)).Name} v{v.V}: " + Clip(v.Code, ShowChars));
            }
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
            case "save":
                return await SaveOpAsync(input, ct).ConfigureAwait(false);
            case "rollback":
            {
                var n = ToolInput.Str(input, "name");
                if (string.IsNullOrWhiteSpace(n)) return ToolResult.Error("rollback needs name (x, or x@v2 for a version)");
                var (r, msg) = Library.Rollback(CleanRef(n));
                return r != null ? ToolResult.Success(msg) : ToolResult.Error(msg);
            }
            case "list":
                return ToolResult.Success(Library.Catalog());
            default:
                return ToolResult.Error("op is " + string.Join("|", ScriptPrompts.Ops));
        }
    }

    /// <summary>No op: code -> run, goal -> save (author + save, no run), else status.</summary>
    internal static string DefaultOp(JsonElement input) =>
        (ToolInput.Str(input, "op") ?? (ToolInput.Str(input, "code") != null ? "run" : ToolInput.Str(input, "goal") != null ? "save" : "status"))
        .Trim().ToLowerInvariant();

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";

    /// <summary>op=show: at most this much of a saved script's code reaches the brain.</summary>
    internal const int ShowChars = 600;

    private async Task<ToolResult> RunOrReplaceAsync(bool replace, JsonElement input, CancellationToken ct)
    {
        var code = ToolInput.Str(input, "code");
        var rawName = ToolInput.Str(input, "name");
        var reference = CleanRef(rawName ?? (replace ? Controller.Current?.Name : null) ?? "script");
        var name = ScriptLibrary.ParseRef(reference).Name;
        string? author = ToolCaller.Current?.Tag;
        int? version = null;
        var notes = new List<string>();
        string? warnings = null;
        if (string.IsNullOrWhiteSpace(code) && ToolInput.Str(input, "goal") is { } goal && !string.IsNullOrWhiteSpace(goal))
        {
            // The separate Opus author writes, lints, revises and saves it; the brain gets only the summary (then it runs).
            var w = await AuthorScriptAsync(name, goal, input, ct).ConfigureAwait(false);
            if (!w.Ok) return ToolResult.Error(w.Summary);
            code = w.Code!;
            version = w.Version;
            author = w.Author;
            notes.Add(w.Summary);
        }
        else if (string.IsNullOrWhiteSpace(code))
        {
            // A saved script (or name@v2) runs as saved: its own author and version, args from the call.
            var v = Library.Resolve(reference, IsScriptable, out var why);
            if (v == null) return ToolResult.Error(rawName == null ? "need code (or the name of a saved script)" : why ?? $"no saved script '{name}'");
            code = v.Code;
            version = Library.Get(name)?.Script is { Migrated: true } ? null : v.V;
            author = v.Author;
        }
        else
        {
            var lint = Lint(code);
            if (lint.HasErrors) return ToolResult.Error(lint.Format(LintSeverity.Error, 300));
            warnings = lint.Format(LintSeverity.Warning);
            if (rawName != null && ToolInput.Bool(input, "save") != false)
            {
                // A named run with code is saved (versioned; same code = same version). save:false opts out; a near-duplicate
                // of another saved script runs unsaved and says so.
                var (saved, note) = SaveChecked(name, code, author, ToolInput.Str(input, "force"));
                if (note != null) notes.Add(note);
                if (saved?.Script is { } sc) version = sc.Version;
            }
        }
        string? argsJson = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;
        var req = new ScriptRequest
        {
            Name = name, Code = code, ArgsJson = argsJson, ResumeFrom = ToolInput.Str(input, "resume_from"), Author = author, Version = version,
            LintWarnings = string.IsNullOrEmpty(warnings) ? null : warnings,
        };
        if (replace && Controller.Current is { } old) L(old).PlannerEnded = true;
        var (s, err) = replace ? await Controller.ReplaceAsync(req, ct).ConfigureAwait(false) : Controller.Start(req);
        if (s == null) return ToolResult.Error(err ?? "can't start");
        var result = await FollowAsync(s, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(warnings)) notes.Add(warnings);
        return notes.Count == 0 ? result : result with { Text = result.Text + " | " + string.Join(" | ", notes) };
    }

    /// <summary>"Cobble Loop@v2" → "cobble_loop@v2" (CleanName would turn '@' into '_').</summary>
    private static string CleanRef(string raw)
    {
        var (n, v) = ScriptLibrary.ParseRef(raw);
        return ScriptController.CleanName(n) + (v is { } x ? "@v" + x : "");
    }

    /// <summary>Lint with this bot's action/skill names (so unchecked-result warnings know what an action is).</summary>
    internal LintReport Lint(string code)
    {
        var names = new HashSet<string>(_actions, StringComparer.Ordinal);
        foreach (var (alias, target) in ScriptSession.Aliases) if (_actions.Contains(target) || (_skills?.Registry.TryGet(target, out _) ?? false)) names.Add(alias);
        if (_skills != null) foreach (var sk in _skills.Registry.Specs) names.Add(sk.Name);
        // Required args from the real tool schemas: "gen{} needs at=" before any action runs (live 10-27 21:12).
        var sigs = new Dictionary<string, BindingSignature>(StringComparer.Ordinal);
        foreach (var a in _actions)
            if (_rt.Tools.TryGet(a, out var tool) && ScriptLint.SignatureOf(tool.InputSchema) is { } sig) sigs[a] = sig;
        return ScriptLint.Check(code, names, Options.LintLongScriptActions, sigs);
    }

    /// <summary>
    /// Saves a version unless it's a near-duplicate of ANOTHER saved script (≥ DuplicateThreshold) and no force reason was
    /// given: then nothing is saved and the note says which script to update instead.
    /// </summary>
    private (Agent.Memory.Routine? Saved, string? Note) SaveChecked(string name, string code, string? author, string? force)
    {
        if (string.IsNullOrWhiteSpace(force) && Library.Similar(name, code, Options.DuplicateThreshold, Similarity) is { } sim)
            return (null, $"not saved: similar to {sim.Name} ({sim.Score:0.00}) - update it instead? (use op=save name={sim.Name}; force=reason to keep both)");
        var res = Library.SaveVersion(name, code, author: author, purpose: ScriptLint.Purpose(code), argNames: ScriptLint.ArgNames(code),
            forcedReason: string.IsNullOrWhiteSpace(force) ? null : force.Trim());
        var sc = res.Routine!.Script!;
        if (res.NewVersion) _log?.LogInformation("script {Name} saved v{Version} by {Author}", name, sc.Version, author ?? "c#");
        return (res.Routine, res.NewVersion && sc.Version > 1 ? $"saved {name} v{sc.Version}" : null);
    }

    private static double Similarity(string code, RoutineScript other) =>
        ScriptLint.Similarity(code, other.Code, Words(ScriptLint.Purpose(code)), Words(other.Purpose));

    private static IEnumerable<string> Words(string? s) =>
        Regex.Matches(s ?? "", "[a-z]{3,}", RegexOptions.IgnoreCase).Select(m => m.Value.ToLowerInvariant());

    /// <summary>Writes scripts for a goal in its own Opus context (set by <see cref="ScriptTools.Register"/>; null = goal refused).</summary>
    public AuthorAgent? Author { get; set; }

    /// <summary>One authored script: Ok + the saved code (for running) and the brain-facing summary (no code, no drafts).</summary>
    internal sealed record AuthoredScript(bool Ok, string Summary, string? Code = null, int? Version = null, string? Author = null);

    /// <summary>
    /// goal=: the <see cref="AuthorAgent"/> writes the script in its own context (cheat-sheet + brief + the facts pulled here, up to
    /// <see cref="Agent.Llm.LlmOptions.AuthorMaxTokens"/>), lint errors are revised inside that loop, the passing version is
    /// saved, and only the final facts come back: name/version, purpose, args, lint status, size, cost, or the blocker.
    /// </summary>
    internal async Task<AuthoredScript> AuthorScriptAsync(string name, string goal, JsonElement input, CancellationToken ct)
    {
        if (Author == null) return new(false, "no script author configured; send code instead");
        if (Author.Refusal() is { } no) return new(false, "can't author: " + no);
        string? argsJson = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;
        var job = new AuthorJob
        {
            Kind = "script",
            System = ScriptPrompts.AuthorSystem,
            Brief = ScriptPrompts.AuthorBrief(name, goal.Trim(), null, argsJson, AuthorFacts(name), Options.MaxCodeChars),
            Check = code =>
            {
                if (code.Length > Options.MaxCodeChars) return AuthorCheck.Fail($"too long: {code.Length} chars (max {Options.MaxCodeChars}); shorten it");
                var lint = Lint(code);
                return lint.HasErrors ? AuthorCheck.Fail("lint errors: " + lint.Format(LintSeverity.Error, 800)) : AuthorCheck.Pass;
            },
        };
        var outcome = await Author.RunAsync(job, ct).ConfigureAwait(false);
        if (!outcome.Ok || outcome.Artifact is not { } code)
            return new(false, $"script {name} not written: {outcome.Blocker} ({outcome.Cost})");
        var author = $"main/{outcome.Model}";
        var (saved, note) = SaveChecked(name, code, author, ToolInput.Str(input, "force"));
        if (saved?.Script is not { } sc) return new(false, $"script {name} written but {note ?? "not saved"} ({outcome.Cost})");
        var warnings = Lint(code).Warnings.Count();
        var args = sc.ArgNames.Count > 0 ? $" | args({string.Join(",", sc.ArgNames)})" : "";
        var purpose = string.IsNullOrWhiteSpace(sc.Purpose) ? "" : " | " + Clip(sc.Purpose!, 100);
        var lintTag = warnings > 0 ? $"lint ok, {warnings} warning(s)" : "lint ok";
        var summary = $"wrote {name} v{sc.Version}{purpose}{args} | {lintTag} | {code.Length} chars | {outcome.Cost}";
        return new(true, summary, code, sc.Version, author);
    }

    /// <summary>What the author may build on: action names, skills, saved scripts, the current version when improving one.</summary>
    private string AuthorFacts(string name)
    {
        var skills = _skills?.Registry.Specs.Where(sp => sp.Status != SkillStatus.Stub).Select(sp => sp.Name).Take(40).ToList() ?? [];
        string saved;
        try { saved = Library.Catalog(); } catch (Exception) { saved = ""; }
        return "Actions: " + string.Join(" ", _actions.Order(StringComparer.Ordinal)) +
            (skills.Count > 0 ? "\nSkills: " + string.Join(" ", skills) : "") +
            (saved.Length > 0 ? "\nSaved scripts (call or update, don't fork): " + Clip(saved, 500) : "") +
            (Library.Get(name)?.Script is { } cur ? $"\nCurrent {name} v{cur.Version} (improve it):\n" + cur.Code : "");
    }

    /// <summary>op=save: lint, near-duplicate check, new version (no run). With goal: authored apart, summary only.</summary>
    private async Task<ToolResult> SaveOpAsync(JsonElement input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ToolInput.Str(input, "code")) && ToolInput.Str(input, "goal") is { } goal && !string.IsNullOrWhiteSpace(goal))
        {
            var rn = ToolInput.Str(input, "name");
            if (string.IsNullOrWhiteSpace(rn)) return ToolResult.Error("goal needs name");
            var w = await AuthorScriptAsync(ScriptLibrary.ParseRef(CleanRef(rn)).Name, goal, input, ct).ConfigureAwait(false);
            return w.Ok ? ToolResult.Success("ok: " + w.Summary) : ToolResult.Error(w.Summary);
        }
        return SaveOp(input);
    }

    /// <summary>op=save with code: lint, near-duplicate check, new version (no run).</summary>
    private ToolResult SaveOp(JsonElement input)
    {
        var code = ToolInput.Str(input, "code");
        var rawName = ToolInput.Str(input, "name");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(rawName)) return ToolResult.Error("save needs name and code");
        var name = ScriptLibrary.ParseRef(CleanRef(rawName)).Name;
        var lint = Lint(code);
        if (lint.HasErrors) return ToolResult.Error(lint.Format(LintSeverity.Error, 300));
        int before = Library.Get(name)?.Script?.Version ?? 0;
        var (saved, note) = SaveChecked(name, code, ToolCaller.Current?.Tag, ToolInput.Str(input, "force"));
        if (saved?.Script is not { } sc) return ToolResult.Error(note ?? "not saved");
        var args = sc.ArgNames.Count > 0 ? $" args({string.Join(",", sc.ArgNames)})" : "";
        var warn = lint.Format(LintSeverity.Warning);
        var head = sc.Version == before ? $"ok: {name} unchanged (v{sc.Version})" : $"ok: saved {name} v{sc.Version}{args}" +
            (sc.Version > 1 ? $"; v{sc.Version - 1} stays callable as {name}@v{sc.Version - 1} until v{sc.Version} proves reliable" : "");
        return ToolResult.Success(warn.Length > 0 ? head + " | " + warn : head);
    }

    private DateTimeOffset _lastHousekeep = DateTimeOffset.MinValue;

    /// <summary>At most every 6 hours (C#, zero tokens): merge exact duplicates' stats, flag stale scripts for archive.</summary>
    internal string MaybeHousekeep(bool force = false)
    {
        var now = _time.GetUtcNow();
        if (!force && now - _lastHousekeep < TimeSpan.FromHours(6)) return "";
        _lastHousekeep = now;
        try
        {
            var note = Library.Housekeep(Options.StaleDays, ScriptLint.ExactKey);
            if (note.Length > 0) _log?.LogInformation("script housekeeping: {Note}", note);
            return note;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "script housekeeping failed"); return ""; }
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
        var summary = (sc.Purpose ?? r.Note ?? $"saved script {name} ({ScriptLibrary.Active(sc).Reliability} ok)").Trim();
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

    /// <summary>
    /// "script cobble_loop 40%" while a running (not paused) script holds the body, else null: the brain's body lock
    /// (<see cref="BotAgent.BodyHolder"/>, shown as "[body: ...]").
    /// </summary>
    internal string? BodyState() =>
        Controller.Current is { Status: ScriptStatus.Starting or ScriptStatus.Running } s
            ? $"script {s.Name}{(s.ProgressPct is { } p ? $" {p:0}%" : "")}" : null;

    /// <summary>"script x L12 3m" for the state header while a script exists (≈ 8 tokens).</summary>
    internal string? HeaderTag() =>
        Controller.Current is { } s ? $"script {s.Name} {(s.Status == ScriptStatus.Paused ? "PAUSED" : "L" + s.Line)} {ScriptText.Dur(s.Elapsed)}" : null;
}

public static class ScriptTools
{
    public const string ToolName = ScriptPrompts.ToolName;

    /// <summary>True when a script call writes code: op run/replace/save with code (or no op and code), or rollback.</summary>
    public static bool WritesCode(JsonElement input)
    {
        var code = ToolInput.Str(input, "code");
        var op = ScriptBridge.DefaultOp(input);
        if (op == "rollback") return true;
        // goal= commissions the Opus author: authorship too.
        return op is "run" or "replace" or "save" && (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(ToolInput.Str(input, "goal")));
    }

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
                .Raw("args", """{"type":"object"}""", required: false).Str("resume_from", required: false).Str("force", required: false).Build(),
            bridge.ExecuteAsync, tiers: ToolTiers.Main | ToolTiers.Worker);
        // Only the Opus brain writes scripts: code (run/replace/save) and rollback from any other tier — or from no model at all —
        // is refused in the registry, whatever path the call takes. Saved scripts by name, status and cancel stay open to all.
        rt.Tools.AddPolicy(ToolName, (input, caller) => WritesCode(input) && caller?.IsBrainModel(rt.Options.Llm) != true ? ScriptPrompts.BrainOnly : null);
        rt.Agent.ExtendSystem(ScriptPrompts.CheatSheet);
        rt.Agent.ExtendSystem(ScriptPrompts.AuthorLine); // goal= is brain-only: documented here, not in the shared schema
        bridge.Author = new AuthorAgent(rt.Llm, rt.Options.Llm, rt.Usage, log);
        rt.Worker?.ExtendSystem(ScriptPrompts.WorkerLine);
        bridge.MaybeHousekeep(force: true);

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
        rt.Agent.BodyHolder = bridge.BodyState; // body lock: brain body tools answer "body busy" while a script runs
        var header = rt.Agent.StateHeader;
        rt.Agent.StateHeader = () =>
        {
            var h = header?.Invoke() ?? "";
            return bridge.HeaderTag() is { } tag ? (h.Length > 0 ? h + " | " + tag : tag) : h;
        };
        return bridge;
    }
}
