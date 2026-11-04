using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed partial class ScriptBridge
{
    /// <summary>Game tools exposed as script functions (positional params = the tool's schema order).</summary>
    public static readonly string[] ActionTools =
    [
        "goto", "follow", "mine", "harvest_generator", "collect_drops", "craft", "smelt", "collect_furnace", "deposit", "withdraw",
        "organize_chest", "place", "dig", "use_block", "eat", "chop_tree", "act", "attack",
        AcquireTool.ToolName, // hidden tool: ensure{} / acquire{"pickaxe"} through the acquisition planner
        EquipTool.ToolName, // hidden tool: equip(item, mode, from, ench) — hold / wear / unwear / take one exact stack
        FaceTool.ToolName, // hidden: face{at} (turn the head; Story pawns' recreation)
        EscapeDigTool.ToolName, // hidden: escape_dig{at} (a Story pawn getting out: any tool, the drop may be lost)
        FishingTool.ToolName, // hidden tool: fish(at, seconds) with a rod
        TurnedPlaceTool.ToolName, // hidden: place_turned(block, at, meta) (a design's facing, axis or half)
        Blueprints.BlueprintWiring.BuildTool, Blueprints.BlueprintWiring.InfoTool, // hidden: build{bp, part, of, layer} / blueprint(id)
    ];

    /// <summary>Never cut short by a pause/cancel: click sequences and single block actions finish, then the script stops.</summary>
    private static readonly HashSet<string> AtomicTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "craft", "smelt", "collect_furnace", "deposit", "withdraw", "organize_chest", "place", "dig", "use_block", "eat", EquipTool.ToolName, EscapeDigTool.ToolName,
        TurnedPlaceTool.ToolName,
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
        /// <summary>Wakes held back while a stall/chat interrupt is fixed inline (re-posted if it escalates).</summary>
        public readonly List<(string Kind, GameEvent E)> Held = new();
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
        Controller.Resumed += OnResumed;
        Controller.Started += OnStarted;
        // Inline interrupt fixing: the script's onInterrupt → a learned handler → one small Opus patch call → the full turn.
        Patcher = new ScriptPatcher(Library, options, rt.Interrupts,
            () => PatchPrompt.System(MineClaw.Scripting.Sdk.SdkReference.GameFunctions(GameFunctions(), 900), options.PatchMaxActions, options.PatchMaxSeconds), log)
        {
            Agent = new PatchAgent(rt.Llm, rt.Options.Llm, rt.Usage, log),
            OnEscalated = ReleaseHeld,
        };
        Controller.Host.Patcher = Patcher;
        Controller.Host.SafetyBusy = () => _client.SafetyEngaged;
        rt.Agent.TurnEnded += OnTurnEnded;
    }

    /// <summary>Learned handlers and the patch turn for this lane's scripts.</summary>
    public ScriptPatcher Patcher { get; }

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
            // Names resolve through this version's game data (the SDK's item/find/gather), read-only.
            Data = _client is MinecraftGameClient mgc ? mgc.Inner.Options.Profile.Data : MineClaw.Data.Versioning.Beta181GameData.Instance,
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
            // The body in scope's lease view (resolved per call: the client may be the body router).
            Claims = () => MineClaw.Bot.Game.GameClients.Claimed(_client)?.Claims,
            OnlinePlayers = () => _client.World.Entities.OnlinePlayers,
            // Scripts cost no tokens, but without a brain to supervise them they don't play either (same as worker tasks).
            ActionVeto = () => _rt.Usage.CheckMode() is BudgetMode.Paused or BudgetMode.CheapOnly ? "budget low" : null,
            Log = line => _log?.LogInformation("{Line}", line),
            // Saved scripts keep their checkpoints in their routine row (shared db): a restart or respawn resumes in place.
            Checkpoints = new LibraryCheckpoints(Library),
            StoredParams = name => Library.ParamsOf(name),
        };
    }

    /// <summary>Durable checkpoints on the saved script's routine row (unsaved ad-hoc runs keep them in memory only).</summary>
    private sealed class LibraryCheckpoints(ScriptLibrary lib) : IScriptCheckpointStore
    {
        public void Save(string script, string? lane, string label, string snapshot) => lib.SaveCheckpoint(script, lane, label, snapshot);
        public string? Load(string script, string? lane, string label) => lib.LoadCheckpoint(script, lane, label);
    }

    /// <summary>Several bodies (in-process lanes or hive clones): set by the host once the bodies exist; null = one body.</summary>
    public MineClaw.Scripting.Crew.ICrewPort? Crew { get => Controller.Host.Crew; set => Controller.Host.Crew = value; }

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
        try { _rt.Events.Post(Body is { Length: > 0 } b && e.Body == null ? e with { Body = b } : e); }
        finally { _posting = false; }
    }

    // ------------------------------------------------------------------ one brain, many bodies

    /// <summary>
    /// The body this lane drives (short name, "67"); null = the main body (hive mode and main: as before). A clone lane's
    /// events carry it, and its interrupt policy hears only its own body's dangers and chat addressed to it.
    /// </summary>
    public string? Body { get => _body; init { _body = value; Controller.Host.Body = value; Patcher.Body = value; } }
    private readonly string? _body;

    /// <summary>
    /// The activity slot a brain-run script occupies (the body's own); default the runtime's (main's). A clone lane must set
    /// it: on main's slot its script would block (and be superseded by) main's jobs.
    /// </summary>
    public ActivityManager Activities { get => _activities ?? _rt.Activities; init => _activities = value; }
    private readonly ActivityManager? _activities;

    /// <summary>
    /// Which bodies a player line addresses (short names; main = "main"), for the interrupt policy across bodies. Null = one
    /// body (every line is this lane's, as before).
    /// </summary>
    public Func<string, IReadOnlyList<string>>? Addressed { get; set; }

    /// <summary>The event concerns this lane's body (null/"main" bodies are main's).</summary>
    private bool OwnBody(GameEvent e)
    {
        var b = GameEvent.BodyTag(e.Body);
        return Body is { Length: > 0 } mine ? string.Equals(b, mine, StringComparison.OrdinalIgnoreCase) : b == null;
    }

    /// <summary>
    /// Whose a player line is, for this lane: <c>Named</c> = it addresses this lane's body; <c>Unnamed</c> = it names no body
    /// (main's lane takes it as before; a clone lane only takes a trusted "stop" from it: a plain stop stops every lane);
    /// <c>Other</c> = it names other bodies only. One body (no <see cref="Addressed"/>): every line is <c>Named</c>.
    /// </summary>
    private ChatOwner OwnChat(GameEvent e)
    {
        if (Addressed == null) return ChatOwner.Named;
        if (e.Kind == EventKind.Whisper && GameEvent.BodyTag(e.Body) is { } whisperedTo)
            return Body != null && whisperedTo.Equals(Body, StringComparison.OrdinalIgnoreCase) ? ChatOwner.Named : ChatOwner.Other;
        IReadOnlyList<string> named;
        try { named = Addressed(e.Text); } catch (Exception) { return Body == null ? ChatOwner.Named : ChatOwner.Other; }
        if (named.Count == 0) return Body == null ? ChatOwner.Named : ChatOwner.Unnamed;
        return named.Contains(Body ?? "main", StringComparer.OrdinalIgnoreCase) ? ChatOwner.Named : ChatOwner.Other;
    }

    private enum ChatOwner { Named, Unnamed, Other }

    /// <summary>What players call a clone lane's body besides its number and login ("clone 67", "maid chan 67").</summary>
    private static string[] LaneNames(string shortName) => ["maid chan " + shortName, "maid " + shortName, "clone " + shortName, "#" + shortName];

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
        var lang = LanguageOf(code);
        if (lang == ScriptLanguage.Lua && !Options.AcceptLuaCode) return (null, ScriptPrompts.JsOnly, null);
        var lint = Lint(code, lang);
        if (lint.HasErrors) return (null, lint.Format(LintSeverity.Error, 300), null); // "lint errors (fix and resubmit): ..."
        var res = Library.SaveVersion(clean, code, author: author, purpose: ScriptLint.Purpose(code), argNames: ScriptLint.ArgNames(code),
            lang: lang == ScriptLanguage.Js ? "js" : null);
        _log?.LogInformation("hive script {Name} saved v{Version} by {Author}", clean, res.Routine?.Script?.Version, author ?? "c#");
        return (clean, null, code);
    }

    /// <summary>
    /// A hive script's coordinator (<c>hive spawn kind=script bodies=N</c>): its own controller with no body of its own — the game
    /// functions answer that it works through its roles (<c>everyBody</c>/<c>parallel</c>/<c>spawn</c>), chat goes to the log — over
    /// exactly <paramref name="crew"/>'s bodies, with this bot's library, data, leases and checkpoints. Its runs are unattended (no
    /// brain) and get <see cref="ScriptOptions.HiveScriptMinutes"/>.
    /// </summary>
    public ScriptController Coordinator(MineClaw.Scripting.Crew.ICrewPort crew, string label)
    {
        var h = Controller.Host;
        const string NoBody = "a hive script's coordinator has no body: act in a role (everyBody/parallel/spawn)";
        var host = new ScriptHost
        {
            World = h.World, Username = h.Username, Data = h.Data, Actions = h.Actions,
            Act = (_, _, _) => Task.FromResult(ActResult.Fail(NoBody)),
            Skills = h.Skills, Skill = (_, _, _) => Task.FromResult(ActResult.Fail(NoBody)), SkillSchema = h.SkillSchema, SkillIsAtomic = h.SkillIsAtomic,
            Say = (t, _) => { _log?.LogInformation("hive script {Label} (not said): {Text}", label, t); return Task.FromResult(ActResult.Success("logged (no body to speak)")); },
            Muted = _ => true,
            Emote = (_, _) => Task.FromResult(ActResult.Success("logged (no body)")),
            WhereIs = h.WhereIs, NearestKnown = h.NearestKnown, IsProtected = h.IsProtected, OnlinePlayers = h.OnlinePlayers,
            Wake = (s, reason) => _log?.LogInformation("hive script {Label} wake (no brain): {Reason}", label, reason),
            ActionVeto = h.ActionVeto,
            Log = line => _log?.LogInformation("hive script {Label}: {Line}", label, line),
            Body = "hive", Crew = crew, Claims = h.Claims, Checkpoints = h.Checkpoints, StoredParams = h.StoredParams,
        };
        var o = Options.Copy();
        o.MaxMinutes = Math.Max(o.MaxMinutes, o.HiveScriptMinutes);
        return new ScriptController(host, o, _time);
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
        // A crew this brain coordinates bodiless (script{name, body:"all"}, a hive spawned onto a script): its idle check-ins are
        // dropped as for its own running script; everything else wakes as usual (it isn't asleep: it may talk and work meanwhile).
        if (s == null && !_posting && Body == null && e.Kind == EventKind.IdleTick && !_crewRuns.IsEmpty) return null;
        if (s == null || _posting) return e;
        var se = Map(e);
        // Several bodies: another body's event, or chat that isn't this lane's, is not this lane's to sleep on.
        if (se == null && (Body != null || Addressed != null))
        {
            bool chat = e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote;
            if (chat ? OwnChat(e) != ChatOwner.Named || Body != null : !OwnBody(e)) return e;
        }
        if (se != null)
        {
            if (se.Kind is not ScriptEventKind.Stop and not ScriptEventKind.Join && s.Status != ScriptStatus.Paused && s.HasHandler(se))
            {
                s.Deliver(se);
                return e with { Wakes = false }; // the script handles it; brain stays asleep (it's context next time)
            }
            // A danger the script will try to fix inline (onInterrupt / learned handler / patch turn): the brain's wake waits for
            // the outcome; an escalation pauses as before and its "script paused" notice wakes the brain with this event as context.
            if (se.Kind == ScriptEventKind.Danger && s.Status != ScriptStatus.Paused
                && (s.PatchingKind is { } fixing ? string.Equals(fixing, se.Topic ?? "danger", StringComparison.OrdinalIgnoreCase) && se.Player == null
                    : s.WouldPatch(new ScriptInterrupt(se.Topic ?? "danger", se.Text, se.Player))))
            {
                s.Deliver(se);
                return e with { Wakes = false };
            }
            // A trusted player's line that tweaks the running job ("make it 32", "use the other chest"): a patch retunes it.
            if (se.Kind == ScriptEventKind.Chat && s.Status != ScriptStatus.Paused && Trusted(se.Player)
                && ScriptPatcher.LooksLikeTweak(se.Text, ParamNames(s)) && s.WouldPatch(new ScriptInterrupt("chat", se.Text, se.Player))
                && Hold(s, "chat", e) && s.RequestInterrupt(new ScriptInterrupt("chat", se.Text, se.Player), cut: false))
                return e with { Wakes = false };
            s.Deliver(se);
            // An unattended run (a crew lane) pauses on its body's danger and carries on by itself once the reflexes let go: the
            // coordinator's business, not a brain wake (20 bodies fighting zombies were 20 wakes).
            if (se.Kind == ScriptEventKind.Danger && s.Unattended) return e with { Wakes = false };
            if (se.Kind != ScriptEventKind.Join) return e;
        }
        // The stall watchdog on a running script: a patch may unstick it (restart the step, step aside) before the brain hears it.
        if (e.Kind == EventKind.Notice && e.Text.StartsWith("stalled: ", StringComparison.Ordinal) && s.Status != ScriptStatus.Paused
            && s.Patching == null && s.WouldPatch(new ScriptInterrupt("stuck", e.Text))
            && Hold(s, "stuck", e with { Wakes = true }) && s.RequestInterrupt(new ScriptInterrupt("stuck", e.Text), cut: true))
            return e with { Wakes = false };
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
            // Several bodies: "67 stop" is lane 67's only; a line for 69 never pauses 67; a plain trusted "stop" stops every lane.
            var owner = OwnChat(e);
            if (owner == ChatOwner.Other) return null;
            bool trusted = _rt.Options.Self.TrustedPlayers.Any(p => string.Equals(p, e.Player, StringComparison.OrdinalIgnoreCase));
            // "claudechan stop" / "wait!" / "both of you stop": the bot's name and filler don't hide a trusted stop.
            string[] names = Body is { Length: > 0 } me ? [me, _client.Username, .. LaneNames(me)] : [_rt.Chat.Username, _rt.Chat.DisplayName, .. _rt.Chat.Aliases];
            bool stop = trusted && (StopWord.IsMatch(e.Text) || StopWord.IsMatch(Agent.Brainstem.IntentMatcher.Normalize(e.Text, names))
                || Agent.Brainstem.IntentMatcher.Reflex(e.Text, names).Kind == Agent.Brainstem.IntentKind.Stop);
            if (owner == ChatOwner.Unnamed && !stop) return null; // chat for the brain: a clone lane keeps working
            return new ScriptEvent(stop ? ScriptEventKind.Stop : ScriptEventKind.Chat, e.Text, e.Player);
        }
        // Several bodies: only this lane's own body's danger interrupts it (69's danger never pauses 67's script).
        if ((Body != null || Addressed != null) && !OwnBody(e)) return null;
        bool danger = e.Kind is EventKind.LowHealth or EventKind.VoidFall or EventKind.Death
            || (e.Wakes && e.Priority >= _rt.Options.InterruptPriority);
        // The topic lets a machine or policy react to the kind of danger (on: {lowhp: 'retreat', void: 'flee', died: 'regroup'}).
        var topic = e.Kind switch { EventKind.LowHealth => "lowhp", EventKind.VoidFall => "void", EventKind.Death => "died", _ => null };
        return danger ? new ScriptEvent(ScriptEventKind.Danger, e.Format(), e.Player, topic) : null;
    }

    private bool Trusted(string? player) =>
        player != null && _rt.Options.Self.TrustedPlayers.Any(p => string.Equals(p, player, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ParamNames(ScriptSession s)
    {
        try { return ScriptLint.Params(s.Code).Select(p => p.Name).ToList(); } catch (Exception) { return []; }
    }

    private bool Hold(ScriptSession s, string kind, GameEvent e)
    {
        var l = L(s);
        lock (l.Held) { l.Held.RemoveAll(h => h.Kind == kind); l.Held.Add((kind, e)); }
        return true;
    }

    /// <summary>A stall/chat interrupt escalated: the brain hears the event it was spared (the script keeps running).</summary>
    private void ReleaseHeld(ScriptSession s, ScriptInterrupt i)
    {
        var l = L(s);
        List<GameEvent> due;
        lock (l.Held) { due = l.Held.Where(h => h.Kind == i.Kind).Select(h => h.E).ToList(); l.Held.RemoveAll(h => h.Kind == i.Kind); }
        foreach (var e in due) PostOwn(e with { Wakes = true });
    }

    private bool InterruptPending() =>
        _rt.Events.Snapshot().Any(e => e.Wakes && e.Priority >= _rt.Options.InterruptPriority && Map(e)?.Kind is ScriptEventKind.Danger);

    private void OnPaused(ScriptSession s)
    {
        var l = L(s);
        if (Volatile.Read(ref l.BrainWaiters) > 0 || s.PausedFor == PauseKind.Brain) return; // the brain's own call reports it
        // An unattended run (crew lane, hive script) resumes by itself once its body is safe: nobody to wake, nothing to report.
        if (s.AutoResumes)
        {
            _log?.LogInformation("script {Name}{Body}: paused for {Why}; resumes once safe", s.Name, Body is { } bn ? " on " + bn : "", s.PauseReason);
            return;
        }
        bool wakes = s.PausedFor is PauseKind.Danger or PauseKind.Stop or PauseKind.Script;
        // The full turn's interrupt → resume latency (a patch that escalated keeps its open measurement, now "escalated").
        if (wakes)
        {
            var key = ScriptPatcher.Key(s);
            if (!_rt.Interrupts.IsOpen(key)) _rt.Interrupts.Open(key, s.Name, PauseKindTag(s), "full", Body);
        }
        var text = "script paused: " + s.State() + (s.PausedFor == PauseKind.Chat ? $" (auto-resume {Options.ChatAutoResumeSeconds}s)"
            : s.PauseReason?.StartsWith("needs_input", StringComparison.Ordinal) == true ? " (answer: script{op:resume,code:<answer>})" : "");
        PostOwn(GameEvent.Notice(text, wakes ? EventPriority.Normal : EventPriority.Background));
    }

    private static string PauseKindTag(ScriptSession s) =>
        s.PauseReason?.StartsWith("needs_input", StringComparison.Ordinal) == true ? "needs_input"
        : s.PausedFor switch { PauseKind.Danger => "danger", PauseKind.Stop => "stop", _ => "pause" };

    /// <summary>
    /// A resume ends the interrupt; one the brain's turn made ends with that turn (its last rounds count: the full path's cost is the
    /// whole turn, as the baseline measured it).
    /// </summary>
    private void OnResumed(ScriptSession s, bool auto)
    {
        // A lane that carried on by itself after a danger (or a chat pause) is its body's activity again: its slot, "body busy" for
        // brain tools, its interrupts (the pause ended the activity that followed it).
        if (auto && s.Request.Role != null && !L(s).Attached)
        {
            var scope = Controller.StepScope;
            _ = Task.Run(async () =>
            {
                using (scope?.Invoke()) await FollowAsActivityAsync(s, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
            });
        }
        var key = ScriptPatcher.Key(s);
        if (!auto && _rt.Agent.TurnStartedAt != null && _rt.Interrupts.IsOpen(key))
        {
            lock (_closeAtTurnEnd) _closeAtTurnEnd.Add(key);
            return;
        }
        _rt.Interrupts.Close(key, auto ? "auto_resume" : "resume", true);
    }

    private readonly List<string> _closeAtTurnEnd = new();

    private void OnTurnEnded()
    {
        List<string> keys;
        lock (_closeAtTurnEnd) { keys = _closeAtTurnEnd.ToList(); _closeAtTurnEnd.Clear(); }
        foreach (var k in keys) _rt.Interrupts.Close(k, "resume", true);
    }

    /// <summary>A failure that went to the brain is measured until the brain starts the next script on this lane.</summary>
    private string? _failedKey;

    private void OnStarted(ScriptSession s)
    {
        if (Interlocked.Exchange(ref _failedKey, null) is { } k) _rt.Interrupts.Close(k, "restarted", true, "next script " + s.Name);
    }

    private void OnEnded(ScriptSession s)
    {
        // An open interrupt ends with the run: replaced/cancelled by the brain, or failed (then measured until the next start).
        var ikey = ScriptPatcher.Key(s);
        if (_rt.Interrupts.IsOpen(ikey))
        {
            if (s.Status == ScriptStatus.Failed) Interlocked.Exchange(ref _failedKey, ikey);
            else _rt.Interrupts.Close(ikey, s.Reason == "replaced" ? "replaced" : s.Status.ToString().ToLowerInvariant(), s.Ok);
        }
        else if (s.Status == ScriptStatus.Failed && s.Request.Role == null)
        {
            // A plain failure that wakes the brain (no patch tried): the full path, until the brain starts something again.
            _rt.Interrupts.Open(ikey, s.Name, "failed", "full", Body);
            Interlocked.Exchange(ref _failedKey, ikey);
        }
        var l = L(s);
        _log?.LogInformation("script {Name}{Version} ({Lang}) by {Author} {Status}: {Actions} actions in {Seconds:0}s — {Outcome} | params {Params} | timings {Timings}{State}", s.Name,
            s.Request.Version is { } v ? " v" + v : "", ScriptLanguages.Tag(s.Language), s.Request.Author ?? "c#", s.Status, s.Actions, s.Elapsed.TotalSeconds, s.Outcome,
            s.ParamsJson ?? "-", ScriptController.TimingText(s) ?? "-", s.MachineState is { } ms ? $" | state {ms} ({s.Transitions} moves)" : "");
        try
        {
            var saved = Library.Get(s.Name);
            // A crew lane's run is part of its coordinator's run: it doesn't count as a run of the saved script.
            if (saved?.Script is { } sc && s.Status != ScriptStatus.Cancelled && s.Request.Role == null)
            {
                ScriptLibrary.Active(sc);
                // Stats go to the version that ran (by number, else by identical code); ad-hoc code under a saved name doesn't count.
                var ran = s.Request.Version is { } rv ? sc.Versions.FirstOrDefault(x => x.V == rv) : sc.Versions.LastOrDefault(x => x.Code == s.Code);
                if (ran != null && ran.Code == s.Code)
                {
                    var stats = Library.RecordRun(s.Name, ran.V, s.Ok, s.Elapsed, s.Actions, s.Outcome ?? "", s.ItemsGained, s.Reason,
                        s.ParamsJson, ScriptController.TimingText(s), out var revert);
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
                return ToolResult.Success(StatusWithCrews());
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
                if (await CancelCrewAsync(input, ct).ConfigureAwait(false) is { } crewCancelled) return crewCancelled;
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
            case "bind" or "unbind" or "bindings":
                return await BindingOpAsync(op, input, ct).ConfigureAwait(false); // ScriptTools.Bindings.cs
            default:
                return ToolResult.Error("op is " + string.Join("|", ScriptPrompts.Ops.Concat(ScriptPrompts.BindOps)));
        }
    }

    /// <summary>No op: code -> run, goal -> save (author + save, no run), a name -> run it, else status.</summary>
    internal static string DefaultOp(JsonElement input) =>
        (ToolInput.Str(input, "op") ?? (ToolInput.Str(input, "code") != null ? "run" : ToolInput.Str(input, "goal") != null ? "save"
            : ToolInput.Str(input, "name") != null ? "run" : "status"))
        .Trim().ToLowerInvariant();

    /// <summary>The tool's params object (this run's tunables) as JSON, or null.</summary>
    internal static string? ParamsOf(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p.GetRawText() : null;

    private static string? ArgsOf(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;

    /// <summary>A crew lane started as a hive job carries its role and board in its args (__role, __crew).</summary>
    private static (string? Role, string? Crew) LaneOf(string? argsJson)
    {
        if (argsJson == null || !argsJson.Contains("__role", StringComparison.Ordinal)) return (null, null);
        try
        {
            using var d = JsonDocument.Parse(argsJson);
            return (d.RootElement.TryGetProperty("__role", out var r) ? r.GetString() : null, d.RootElement.TryGetProperty("__crew", out var c) ? c.GetString() : null);
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>JS unless the code is clearly legacy Lua.</summary>
    internal static ScriptLanguage LanguageOf(string code) => ScriptLanguages.Detect(code);

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
        var lang = ScriptLanguage.Js;
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
            if (v == null) return ToolResult.Error(rawName == null ? "need a goal (or the name of a saved script)" : why ?? $"no saved script '{name}'");
            code = v.Code;
            version = Library.Get(name)?.Script is { Migrated: true } ? null : v.V;
            author = v.Author;
            lang = v.IsLua && !LooksJs(name) ? ScriptLanguage.Lua : ScriptLanguage.Js;
            if (lang == ScriptLanguage.Lua && !Options.LegacyLua)
                return ToolResult.Error($"{name} v{v.V} is legacy Lua and not migrated to JS yet (Scripting:LegacyLua=false); rewrite it: script{{name:{name}, goal:...}}");
        }
        else
        {
            lang = LanguageOf(code);
            if (lang == ScriptLanguage.Lua && !Options.AcceptLuaCode) return ToolResult.Error(ScriptPrompts.JsOnly);
            var lint = Lint(code, lang);
            if (lint.HasErrors) return ToolResult.Error(lint.Format(LintSeverity.Error, 300));
            warnings = lint.Format(LintSeverity.Warning);
            if (rawName != null && ToolInput.Bool(input, "save") != false)
            {
                // A named run with code is saved (versioned; same code = same version). save:false opts out; a near-duplicate
                // of another saved script runs unsaved and says so.
                var (saved, note) = SaveChecked(name, code, author, ToolInput.Str(input, "force"), lang);
                if (note != null) notes.Add(note);
                if (saved?.Script is { } sc) version = sc.Version;
            }
        }
        string? argsJson = ArgsOf(input);
        var (role, crewId) = LaneOf(argsJson);
        var req = new ScriptRequest
        {
            Name = name, Code = code, Language = lang, ArgsJson = argsJson, ParamsJson = ParamsOf(input), Role = role, CrewId = crewId,
            ResumeFrom = ToolInput.Str(input, "resume_from"), Author = author, Version = version,
            LintWarnings = string.IsNullOrEmpty(warnings) ? null : warnings,
        };
        // A crew script over a body list or "all" (the planner resolved them): ONE bodiless coordinator, this body stays free.
        if (CrewBodiesOf(input) is { } crewBodies)
        {
            var crew = await RunCrewAsync(req, crewBodies, replace, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(warnings)) notes.Add(warnings);
            return notes.Count == 0 || !crew.Ok ? crew : crew with { Text = crew.Text + " | " + string.Join(" | ", notes) };
        }
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

    /// <summary>A step routine migrated before JS existed is Lua; one migrated now is JS (its row says so).</summary>
    private bool LooksJs(string name) => Library.Get(name)?.Script is { Lang: "js" };

    /// <summary>
    /// <see cref="Agent.Human.StandingOrders.Prepare"/> for script orders: the stored call must still run from C# after the
    /// Lua→JS migration. A pinned version that is Lua or gone re-resolves to the saved script's newest (active) version; inline
    /// code is dropped when a saved script of that name exists; a checkpoint label the new code lacks is dropped. Anything that
    /// can't run (no such script, Lua that never migrated, inline code, a goal) is a problem the brain hears once.
    /// </summary>
    internal Agent.Human.OrderCheck CheckOrder(Agent.Memory.StandingOrder o)
    {
        if (!string.Equals(o.Tool, ScriptTools.ToolName, StringComparison.OrdinalIgnoreCase) || o.Input.ValueKind != JsonValueKind.Object)
            return default;
        if (DefaultOp(o.Input) is not ("run" or "replace")) return default;
        if (System.Text.Json.Nodes.JsonNode.Parse(o.Input.GetRawText()) is not System.Text.Json.Nodes.JsonObject node) return default;
        var notes = new List<string>();
        var rawName = ToolInput.Str(o.Input, "name");
        var code = ToolInput.Str(o.Input, "code");
        bool named = !string.IsNullOrWhiteSpace(rawName);
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (named && Library.Get(ScriptLibrary.ParseRef(CleanRef(rawName!)).Name)?.Script is { Code.Length: > 0 })
            {
                node.Remove("code");
                node.Remove("save");
                notes.Add("inline code dropped, runs the saved script");
            }
            else
                return Agent.Human.OrderCheck.Invalid(LanguageOf(code!) == ScriptLanguage.Lua
                    ? "its call carries inline legacy Lua code (scripts are JS now) and no saved script by that name"
                    : "its call carries inline code, which C# can't re-run (only the brain writes code); name a saved script");
        }
        if (!named)
            return Agent.Human.OrderCheck.Invalid(ToolInput.Str(o.Input, "goal") is { Length: > 0 }
                ? "its call commissions a new script (goal) instead of naming a saved one" : "its call names no saved script");
        var reference = CleanRef(rawName!);
        var (name, ver) = ScriptLibrary.ParseRef(reference);
        if (Library.Get(name) == null) return Agent.Human.OrderCheck.Invalid($"no saved script '{name}' any more");
        // A pinned older version is Lua by its own row (the script's "js" marks only the active one, e.g. the migrated v2).
        if (ver != null && (Library.Resolve(reference, IsScriptable, out _) is not { } pinned
                            || (pinned.V != Library.Get(name)?.Script?.Version ? pinned.IsLua : pinned.IsLua && !LooksJs(name))))
        {
            node["name"] = name;
            notes.Add($"{reference} re-resolved to {name}'s newest version");
        }
        var v = Library.Resolve(CleanRef(node["name"]?.GetValue<string>() ?? reference), IsScriptable, out var why);
        if (v == null) return Agent.Human.OrderCheck.Invalid(why ?? $"no saved script '{name}'");
        if (v.IsLua && !LooksJs(name) && !Options.LegacyLua)
            return Agent.Human.OrderCheck.Invalid($"{name} v{v.V} is legacy Lua and wasn't migrated to JS ({Library.Get(name)?.Script?.Migration ?? "migration pending"}); " +
                $"rewrite it: script{{name:{name}, goal:...}}");
        if (ToolInput.Str(o.Input, "resume_from") is { Length: > 0 } label && !ScriptSession.HasCheckpoint(v.Code, label.Trim()))
        {
            node.Remove("resume_from");
            notes.Add($"checkpoint '{label}' isn't in {name} v{v.V}, starts from the top");
        }
        return notes.Count == 0 ? default : Agent.Human.OrderCheck.Rewrite(JsonSerializer.SerializeToElement(node), string.Join("; ", notes));
    }

    /// <summary>Lint with this bot's action/skill names (so unchecked-result warnings know what an action is).</summary>
    internal LintReport Lint(string code) => Lint(code, LanguageOf(code));

    internal LintReport Lint(string code, ScriptLanguage lang)
    {
        var names = new HashSet<string>(_actions, StringComparer.Ordinal);
        foreach (var (alias, target) in ScriptSession.Aliases) if (_actions.Contains(target) || (_skills?.Registry.TryGet(target, out _) ?? false)) names.Add(alias);
        if (_skills != null) foreach (var sk in _skills.Registry.Specs) names.Add(sk.Name);
        // Required args from the real tool schemas: "gen{} needs at=" before any action runs (live 10-27 21:12).
        var sigs = new Dictionary<string, BindingSignature>(StringComparer.Ordinal);
        foreach (var a in _actions)
            if (_rt.Tools.TryGet(a, out var tool) && ScriptLint.SignatureOf(tool.InputSchema) is { } sig) sigs[a] = sig;
        return ScriptLint.Check(code, names, Options.LintLongScriptActions, sigs, lang);
    }

    /// <summary>The game functions a JS script can call here, with their positional params (the author's facts).</summary>
    internal IEnumerable<(string Name, IReadOnlyList<string> Params)> GameFunctions()
    {
        foreach (var (alias, target) in ScriptSession.Aliases)
            if (_actions.Contains(target) && _rt.Tools.TryGet(target, out var t)) yield return (alias, ParamNames(t.InputSchema));
        foreach (var a in _actions.Order(StringComparer.Ordinal))
            if (_rt.Tools.TryGet(a, out var t)) yield return (a, ParamNames(t.InputSchema));
    }

    private static IReadOnlyList<string> ParamNames(JsonElement schema) =>
        schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object ? props.EnumerateObject().Select(p => p.Name).ToList() : [];

    /// <summary>
    /// Saves a version unless it's a near-duplicate of ANOTHER saved script (≥ DuplicateThreshold) and no force reason was
    /// given: then nothing is saved and the note says which script to update instead.
    /// </summary>
    private (Agent.Memory.Routine? Saved, string? Note) SaveChecked(string name, string code, string? author, string? force, ScriptLanguage lang = ScriptLanguage.Js)
    {
        if (string.IsNullOrWhiteSpace(force) && Library.Similar(name, code, Options.DuplicateThreshold, Similarity) is { } sim)
            return (null, $"not saved: similar to {sim.Name} ({sim.Score:0.00}) - update it instead? (use op=save name={sim.Name}; force=reason to keep both)");
        var res = Library.SaveVersion(name, code, author: author, purpose: ScriptLint.Purpose(code), argNames: ScriptLint.ArgNames(code),
            forcedReason: string.IsNullOrWhiteSpace(force) ? null : force.Trim(), lang: lang == ScriptLanguage.Js ? "js" : null);
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
        string? argsJson = ParamsOf(input) ?? ArgsOf(input);
        var job = new AuthorJob
        {
            Kind = "script",
            System = ScriptPrompts.AuthorSystem,
            Brief = ScriptPrompts.AuthorBrief(name, goal.Trim(), null, argsJson, AuthorFacts(name), Options.MaxCodeChars),
            Check = CheckDraft,
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

    /// <summary>
    /// A draft passes when it fits, lints clean (JS) and survives a dry run on a fake world (all actions succeed, waits fast):
    /// a ReferenceError/TypeError or a binding misuse the lint can't see comes back to the author, never to a live run.
    /// </summary>
    internal AuthorCheck CheckDraft(string code)
    {
        if (code.Length > Options.MaxCodeChars) return AuthorCheck.Fail($"too long: {code.Length} chars (max {Options.MaxCodeChars}); shorten it");
        if (LanguageOf(code) == ScriptLanguage.Lua) return AuthorCheck.Fail("that is Lua: write JavaScript (see the SDK reference)");
        var lint = Lint(code, ScriptLanguage.Js);
        if (lint.HasErrors) return AuthorCheck.Fail("lint errors: " + lint.Format(LintSeverity.Error, 800));
        // Migration off the old strategy helpers: saved versions still run on them (99-compat.js), but no new version may call one.
        if (lint.Warnings.Where(w => w.Rule == ScriptLint.CompatRule).Select(w => w.ToString()).ToList() is { Count: > 0 } compat)
            return AuthorCheck.Fail("deprecated helpers (compose the primitives instead, see the examples): " + string.Join("; ", compat));
        var dry = ScriptDryRun.Run(code, Controller.Host);
        return dry == null ? AuthorCheck.Pass : AuthorCheck.Fail("dry run (fake world, every action succeeds): " + dry);
    }

    /// <summary>" (calls deprecated helpers: mineGen → use gather(...); ...; rewrite with the primitives)" for code on the compat module, else "".</summary>
    internal static string CompatNote(string code) =>
        ScriptLint.CompatCalls(code) is { Count: > 0 } calls
            ? $" (calls deprecated helpers: {string.Join("; ", calls.Select(c => $"{c.Name} → {c.Use}"))}; rewrite with the primitives)" : "";

    /// <summary>What the author may build on: game functions with params, skills, saved scripts, the current version when improving one.</summary>
    internal string AuthorFacts(string name)
    {
        var skills = _skills?.Registry.Specs.Where(sp => sp.Status != SkillStatus.Stub).Select(sp => sp.Name).Take(40).ToList() ?? [];
        string saved;
        try { saved = Library.Catalog(); } catch (Exception) { saved = ""; }
        return MineClaw.Scripting.Sdk.SdkReference.GameFunctions(GameFunctions()) +
            (skills.Count > 0 ? "\nSkills (call by name, camelCase or snake_case; or skill(name, args)): " + string.Join(" ", skills) : "") +
            (saved.Length > 0 ? "\nSaved scripts (call or update, don't fork): " + Clip(saved, 500) : "") +
            (Library.Get(name)?.Script is { } cur ? $"\nCurrent {name} v{cur.Version}{(ScriptLibrary.Active(cur).IsLua ? " (legacy Lua: rewrite it in JS)" : CompatNote(cur.Code))} (improve it):\n" + cur.Code : "");
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
        // script{op:save, name, params}: retune a saved script without new code (merged over its saved tunables).
        if (string.IsNullOrWhiteSpace(code) && ParamsOf(input) is { } tune && !string.IsNullOrWhiteSpace(rawName))
        {
            var tn = ScriptLibrary.ParseRef(CleanRef(rawName)).Name;
            if (!Library.SetParams(tn, tune)) return ToolResult.Error($"no saved script '{tn}'");
            var declared = Library.Get(tn)?.Script is { } tsc ? ScriptLint.Params(tsc.Code).Select(p => p.Name).ToHashSet(StringComparer.Ordinal) : [];
            var unknown = JsonDocument.Parse(tune).RootElement.EnumerateObject().Select(p => p.Name).Where(k => declared.Count > 0 && !declared.Contains(k)).ToList();
            return ToolResult.Success($"ok: {tn} tuned {Library.ParamsOf(tn) ?? "{}"}" + (unknown.Count > 0 ? $" (not declared by its params(): {string.Join(",", unknown)})" : ""));
        }
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(rawName)) return ToolResult.Error("save needs name and code");
        var name = ScriptLibrary.ParseRef(CleanRef(rawName)).Name;
        var lang = LanguageOf(code);
        if (lang == ScriptLanguage.Lua && !Options.AcceptLuaCode) return ToolResult.Error(ScriptPrompts.JsOnly);
        var lint = Lint(code, lang);
        if (lint.HasErrors) return ToolResult.Error(lint.Format(LintSeverity.Error, 300));
        int before = Library.Get(name)?.Script?.Version ?? 0;
        var (saved, note) = SaveChecked(name, code, ToolCaller.Current?.Tag, ToolInput.Str(input, "force"), lang);
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
            var r = await Activities.RunAsync([(new ScriptActivity(this, s), ToolSchema.Empty)], inlineWait, ct).ConfigureAwait(false);
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

    /// <summary>
    /// A crew lane on this lane's body (in-process crews): starts the role run and makes it the body's activity at once (its
    /// slot, its interrupts, "body busy" for brain tools), so the coordinator only waits for its completion.
    /// </summary>
    public (ScriptSession? Session, string? Error) StartLane(ScriptRequest request, Bodies.IBody body)
    {
        var (s, err) = Controller.Start(request);
        if (s == null) return (null, err);
        L(s).DetachedFromWorker = true; // nobody waits inline: report the end like a detached run if no activity adopts it
        _ = Task.Run(async () =>
        {
            using (Bodies.BodyScope.Push(body))
                await FollowAsActivityAsync(s, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        });
        return (s, null);
    }

    /// <summary>
    /// The one-time Lua → JS migration (Scripting:MigrateLua, main process only, background): every saved script whose active
    /// version is still Lua is translated by the Opus author, linted, dry-run, and saved as its next version (Lua kept as history).
    /// </summary>
    public Task<IReadOnlyList<LuaMigration.Item>> StartLuaMigrationAsync(CancellationToken ct) =>
        Options.MigrateLua && Author != null ? Task.Run(() => new LuaMigration(this, _log).RunAsync(ct), ct) : Task.FromResult<IReadOnlyList<LuaMigration.Item>>([]);

    /// <summary>Worker task ended: a detached script becomes the bot's activity (or is cancelled if something else took the body).</summary>
    internal void AdoptOrphan()
    {
        var s = Controller.Current;
        if (s == null) return;
        var l = L(s);
        if (!l.DetachedFromWorker || l.Attached) return;
        var cur = Activities.Current;
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
            var label = Activities.Current is { } c && c.Id != myId ? c.Label : "another task";
            s.Stop(Activities.Shutdown.IsCancellationRequested ? "shutdown" : "superseded by " + label);
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
            int myId = b.Activities.Current?.Id ?? -1;
            l.Attached = true;
            l.ActivityId = myId;
            l.DetachedFromWorker = false;
            s.OwnerLost = () => b.Activities.Current is { } c && c.Id != myId ? "superseded by " + c.Label : null;
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
            // A crew lane reports to its coordinator (parallel/everyBody), never to the brain: N lanes ending must not be N wakes.
            if (l.PlannerEnded || s.Request.Role != null) Silence(myId);
            return s.Ok ? ToolResult.Success(s.Outcome ?? "ok") : ToolResult.Error(s.Outcome ?? "failed");
        }

        /// <summary>Marks our own activity as model-stopped so the ActivityManager posts no task event for it.</summary>
        private void Silence(int myId)
        {
            if (b.Activities.Current?.Id == myId) b.Activities.Cancel("stop");
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
            var ver = Library.Resolve(name, IsScriptable, out string? _why);
            if (ver == null) return SkillResult.Fail($"script {name} was forgotten");
            var (s, err) = Controller.Start(new ScriptRequest
            {
                Name = name, Code = ver.Code, Language = ver.IsLua && !LooksJs(name) ? ScriptLanguage.Lua : ScriptLanguage.Js,
                ArgsJson = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : null,
            });
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
        var sc = routine.Script is { Code.Length: > 0 } own ? own : Library.Migrate(routine, IsScriptable)?.Script;
        if (sc == null) return null; // a step the script API can't express: legacy replay
        var lua = ScriptLibrary.Active(sc).IsLua && sc.Lang != "js";
        if (lua && !Options.LegacyLua) return ToolResult.Error($"{routine.Name} is legacy Lua and not migrated yet");
        var (s, err) = Controller.Start(new ScriptRequest
        {
            Name = routine.Name, Code = sc.Code, Language = lua ? ScriptLanguage.Lua : ScriptLanguage.Js,
            ArgsJson = repeat is { } n ? $"{{\"repeat\":{n}}}" : null,
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

    /// <summary>
    /// One brain, many bodies: a clone body's lane joins the event gate chain, so its script hears its own body's dangers and
    /// the chat addressed to it (and the brain sleeps on that body's minor events while its script runs). Other bodies'
    /// events pass it untouched.
    /// </summary>
    public static void AttachLaneGate(AgentRuntime rt, ScriptBridge lane)
    {
        var prevGate = rt.Events.Gate;
        rt.Events.Gate = prevGate == null ? lane.Gate : e => prevGate(e) is { } kept ? lane.Gate(kept) : null;
    }

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

        // The brain's whole view: script{name|goal, params?, op}. code/args/resume_from/force still work (tools, hive, tests)
        // but cost no schema bytes; only the separate author writes code.
        JsonElement Schema(IEnumerable<string> ops) => ToolSchema.Create().Str("op", enumValues: ops)
            .Str("name", required: false).Str("goal", required: false).Raw("params", """{"type":"object"}""", required: false).Build();
        // Binding ops are documented in the system hint, not the budget-pinned tool definition. Validate them too.
        rt.Tools.SetInputAdapter(ToolName, input =>
        {
            if (input.ValueKind != JsonValueKind.Object) return input;
            if (input.TryGetProperty("op", out var op) && op.ValueKind != JsonValueKind.String) return input;
            var o = JsonNode.Parse(input.GetRawText())!.AsObject();
            o["op"] = ScriptBridge.DefaultOp(input);
            return JsonSerializer.SerializeToElement(o);
        }, Schema(ScriptPrompts.Ops.Concat(ScriptPrompts.BindOps)));
        rt.Tools.Register(ToolName, ScriptPrompts.ToolDescription,
            Schema(ScriptPrompts.Ops),
            // One brain, many bodies: a call for a clone body (its slot, its worker, the brain's body=67) runs on that body's lane.
            (input, ct) => (Bodies.BodyScope.Current is Bodies.Body { IsMain: false, ScriptBridge: { } lane } && !ReferenceEquals(lane, bridge) ? lane : bridge)
                .ExecuteAsync(input, ct),
            tiers: ToolTiers.Main | ToolTiers.Worker);
        // Only the Opus brain writes scripts: code (run/replace/save) and rollback from any other tier — or from no model at all —
        // is refused in the registry, whatever path the call takes. Saved scripts by name, status and cancel stay open to all.
        rt.Tools.AddPolicy(ToolName, (input, caller) => WritesCode(input) && caller?.IsBrainModel(rt.Options.Llm) != true ? ScriptPrompts.BrainOnly : null);
        rt.Agent.ExtendSystem(ScriptPrompts.AuthorLine); // the brain never writes code: goal= goes to the separate Opus author
        rt.Agent.ExtendSystem(ScriptPrompts.BindLine); // instant triggers (standing bindings, learned reactions): one cached line
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
        rt.Agent.CrewRun = bridge.IsCrewCall; // a crew script with a body list or "all" = ONE crew over those bodies
        if (rt.Agent.Orders is { } orders)
        {
            // A live script holds the body: the order waits instead of a run that answers "already running" (live 10-28 03:48:
            // three of those paused auto-resume in the db, and every restart after stayed idle). Stored calls are checked
            // (Lua→JS migration: newest version by name) before C# re-issues them.
            orders.BodyBusy = () => bridge.Controller.Current is { IsLive: true };
            orders.Prepare = bridge.CheckOrder;
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
