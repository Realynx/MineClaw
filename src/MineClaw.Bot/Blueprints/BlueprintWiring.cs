using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Client.Trees;
using MineClaw.Render;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// Hooks the blueprint designer into a bot host with minimal touch points: the brain's <c>design</c> tool (Opus authoring only,
/// like scripts), the hidden <c>build</c>/<c>blueprint</c> tools (also Lua functions: <c>build{bp=3, part=1, of=2, layer=0}</c>,
/// <c>blueprint(3)</c>), the chat review (accept/change, zero tokens, the consumed line doesn't wake the brain), resume after a
/// restart, and recording a finished build as a soft-protected structure.
/// </summary>
public sealed partial class BlueprintWiring
{
    public const string DesignTool = "design", BuildTool = "build", InfoTool = "blueprint";

    /// <summary>The brain's design tool (≈ the same tokens as before revisions: the op enum moved into the text, ToolBudgets.Brain).</summary>
    public const string DesignDescription =
        "Builds from a wish (drafted, checked, previewed in the background; summary+bill to chat); building needs a trusted go, resuming " +
        "an approved one doesn't. op new{what,at?} revise{id,what} build{id,at?,bodies?|crew?} show{id,image?} list cancel{id} " +
        "undo{id}: take down its placed blocks; delete{id}: and its plan; capture{name,at?|corners?}: save a standing build (corners " +
        "\"x,y,z x,y,z\"). Saves are revisions: log{id} status{id} diff{id,what?=\"r2\"|\"r1 r3\"} (vs world) commit{id,what?=msg}: " +
        "world edits as a revision; revert{id,what?=rev}: rebuild it (default the one before)";

    /// <summary>Every op the design tool takes (the schema's op is free text; unknown ops answer with this list).</summary>
    internal static readonly string[] Ops = ["new", "revise", "build", "show", "list", "cancel", "undo", "delete", "capture", "log", "status", "diff", "commit", "revert"];

    public required BlueprintService Service { get; init; }
    /// <summary>Set after vision is wired (roles, atlas, perception for the critique).</summary>
    public VisionWiring.Result? Vision { get; set; }
    /// <summary>The bodies a build is split across. Default: this bot's own body (the multi-body host replaces it).</summary>
    public IBuildCrew Crew { get; set; } = null!;
    /// <summary>design capture (<see cref="DesignCapture.Op"/>): (input, speaker) → the one-line result.</summary>
    internal Func<JsonElement, string?, ToolResult>? CaptureOp { get; set; }
    /// <summary>"my house" (speaker) → the captured design it names, or null (<see cref="DesignCapture.Resolve"/>).</summary>
    internal Func<string, string?, BlueprintRecord?>? Owned { get; set; }
    /// <summary>Revisions and the world diff (null = no history kept: no db).</summary>
    public DesignHistory? History => Service.History;
    /// <summary>The drift watcher on the main body's world (the [design] line).</summary>
    public DesignDrift? Drift { get; private set; }
    /// <summary>The editing context (a trusted touch opens a design for requested edits).</summary>
    public EditContext? Edits => Service.Edits;

    private readonly List<(string Player, string Text, DateTimeOffset At)> _consumed = new();

    public static BlueprintWiring Attach(AgentRuntime rt, IGameClient client, IGameClient toolClient, BlueprintOptions o, Protection protection,
        AcquisitionSources? acquisition, Func<BlockPos, IReadOnlyList<BlockPos>> genCells, IClaimBoard? claims, TimeProvider? time, ILogger? log, bool persist)
    {
        var clock = time ?? TimeProvider.System;
        var store = new BlueprintStore(rt.Db, clock);
        BlueprintWiring? wiring = null;
        var body = new GameBuildBody(() => toolClient);
        var gates = new Building.BuildGates();
        Func<BlockPos, string?> landmark = p => Agent.Memory.Landmarks.Near(rt.Memory, rt.Observations, p.X + 0.5, p.Y, p.Z + 0.5);
        Func<BlockPos, Building.NamedPlace?> places = p => NearestPlace(rt, p);
        var service = new BlueprintService(o, store, new BlueprintPreview(), clock, log)
        {
            Gates = gates,
            Places = places,
            Telemetry = new DesignTelemetry(persist ? rt.Options.DataDirectory : null, log, clock),
            Site = () => Site(client, protection, rt, acquisition, genCells),
            Roles = () => wiring?.Vision?.Roles.Map,
            Light = () => new WorldVoxels(client.World),
            Atlas = () => wiring?.Vision?.Hub?.Atlas(),
            WorldTime = () => client.World.Time,
            Dimension = () => client.World.Dimension,
            Crew = () => wiring!.Crew,
            Executor = new ConstructionExecutor(o, store, claims is NullClaimBoard ? null : claims, clock, log)
            {
                Gates = gates,
                Ask = line => rt.Chat.SayAsync(line),
                GateQuestion = (bp, v) => Building.BuildGate.Question(bp, v, wiring?.Service.Store.Get(bp.BlueprintId)?.Name ?? v.Blueprint.Name,
                    landmark, places, BlueprintService.PlanUrl(bp.BlueprintId)),
            },
            ResolveSite = (at, who) => ResolveSite(client, wiring?.Vision?.Hub, protection, at, who, clock, rt),
            Trusted = () => rt.Options.Self.TrustedPlayers,
            Say = line => rt.Chat.SayAsync(line),
            Notice = line => rt.Events.Post(GameEvent.Notice(line, EventPriority.Background)),
            AuthorRefusal = () => !rt.Options.Llm.BrainAuthorsScripts ? "only the Opus brain designs builds (this brain runs a non-Opus profile); saved designs can still be built"
                : rt.Usage.Mode == BudgetMode.Paused ? "budget paused" : null,
            Built = (rec, plan) => Record(rt, client, rec, plan),
            Landmark = p => Agent.Memory.Landmarks.Near(rt.Memory, rt.Observations, p.X + 0.5, p.Y, p.Z + 0.5),
        };
        service.Author = new LlmBlueprintAuthor(rt.Llm, rt.Options.Llm, rt.Usage, o);
        service.Critic = new PerceptionCritic(() => wiring?.Vision?.Perception, rt.Usage);
        wiring = new BlueprintWiring { Service = service, Crew = new SingleBodyCrew(body) };
        service.StartBuild = (id, part) => wiring.StartBuild(rt, id, part);
        service.StartUndo = id => wiring.StartBuild(rt, id, null, undo: true);
        service.StartDelete = (id, by) => { wiring._deleteBy[id] = by; return wiring.StartBuild(rt, id, null, delete: true); };
        // An undo/delete takes down only the build's own placements and never one a player marked never-break or a generator's
        // walls/fluids (self-placed or not: digging one can spill lava/water).
        service.KeepOnUndo = (p, id) => protection.Marks?.NeverAt(p, client.World.Dimension) != null
            || protection.IsDigCell?.Invoke(p) != true && protection.IndexerKind?.Invoke(p) == WorldIndex.IndexKinds.Generator;
        wiring.CaptureOp = (input, who) => DesignCapture.Op(input, who, service, client, rt.Memory, () => wiring.Vision?.Hub,
            exclude: p => protection.IndexerKind?.Invoke(p) == WorldIndex.IndexKinds.Generator, protectionChanged: protection.Invalidate, time: clock);
        wiring.Owned = (text, who) => DesignCapture.Resolve(store, rt.Memory, text, who);
        // Git-style revisions (every save/capture a revision; log/diff/status/commit/revert) and the editing context: saving is a
        // record, not a lock (Fox 10-30). The drift watcher compares standing designs with HEAD as this body sees them.
        var edits = protection.Edits;
        service.Edits = edits;
        service.History = new DesignHistory(service, new DesignRevisions(rt.Db, clock), clock)
        {
            World = () => client.World.PlanningView, Edits = edits, Memory = rt.Memory, BotName = () => client.Username,
        };
        if (edits != null)
            edits.HiveDesign ??= id => store.Get(id) is { } r && r.Status != DesignStatus.Captured && (string.IsNullOrWhiteSpace(r.Requester) || edits.IsBot(r.Requester));
        wiring.Drift = new DesignDrift(service, service.History, client.World, clock, log, timer: persist)
        {
            Notice = line => rt.Events.Post(GameEvent.Notice(line, EventPriority.Background)),
            Self = () => client.Username,
            IsBot = n => edits?.IsBot(n) == true,
            Busy = service.Busy,
        };
        // Migration: every design saved before revisions becomes r1 (its current DSL), once, in the background.
        if (persist)
            _ = Task.Run(() =>
            {
                try { foreach (var r in store.List(200)) service.History.Ensure(r); }
                catch (Exception ex) { log?.LogDebug(ex, "design revisions migration skipped"); }
            });
        wiring.RegisterTools(rt);
        wiring.WrapDelegate(rt);
        wiring._playerAt = name => client.World.Entities.FindPlayer(name) is { } pe ? pe.Position.ToBlockPos() : null;
        wiring.AttachChat(rt, log);
        wiring.WireRulesAndResume(rt, client, o, clock, log, persist);
        return wiring;
    }

    /// <summary>Standing rules on designs, and a build paused by a restart told (or resumed) once the body spawns.</summary>
    private void WireRulesAndResume(AgentRuntime rt, IGameClient client, BlueprintOptions o, TimeProvider clock, ILogger? log, bool persist)
    {
        var service = Service;
        // Trusted players' standing rules ride along with resume/build results, and a pending build puts its design in play for
        // the turn header's [standing rules] (text only: the brain decides, e.g. "only build in daytime" at night).
        var rules = Agent.Rules.DirectiveStore.For(rt.Db, rt.Rag);
        rules.TimeOfDay ??= () => client.IsPlaying ? (client.World.IsNight ? "night" : "day") : null;
        rules.AddInPlay(() => service.InPlay(client.Username));
        service.Rules = id => rules.ForDesign(id);
        // The one coded check: a rule that limits the build to a time of day stops it at dusk and walks the body home.
        service.Executor.Hold = id => client.IsPlaying ? rules.TimeHold(id, client.World.TimeOfDay) : null;
        if (!persist) return;
        // A build paused by the restart rides in main's one startup wake (and the idle watchdog), not a Background notice that
        // never wakes the brain (live 10-28 07:37-08:45).
        if (!o.AutoResume) rt.Agent.AddPendingWork(() => service.NotifyPending(client.Username, notice: false));
        int spawns = 0;
        client.Spawned += () => _ = Task.Run(async () =>
        {
            try
            {
                // First spawn of a process: the startup wake already tells a paused build (AutoResume off).
                if (Interlocked.Increment(ref spawns) == 1 && !o.AutoResume && rt.Agent.WakeOnStart && rt.Options.Idle.StartupWake) return;
                await Task.Delay(TimeSpan.FromSeconds(15), clock).ConfigureAwait(false);
                if (rt.Agent.ActivitiesFor(client.Username).IsBusy) return;
                var line = o.AutoResume ? service.ResumePending(client.Username) : service.NotifyPending(client.Username);
                if (line != null) log?.LogInformation("blueprints: {Line}", line);
            }
            catch (Exception ex) { log?.LogDebug(ex, "blueprint resume failed"); }
        });
    }

    // ------------------------------------------------------------------ tools

    private void RegisterTools(AgentRuntime rt)
    {
        rt.Tools.Register(DesignTool, DesignDescription,
            ToolSchema.Create().Str("op", required: false)
                // bodies/crew (op build) are read by name, documented in the description only (brain budget, ToolBudgets.Brain).
                .Str("what", required: false).Str("at", required: false).Str("id", required: false).Bool("image", required: false).Build(),
            (input, ct) => Task.FromResult(Design(input)), tiers: ToolTiers.Main, maxResultChars: 600);
        // Only the Opus brain authors designs (like Lua scripts); every other path may build or inspect saved ones.
        rt.Tools.AddPolicy(DesignTool, (input, caller) =>
            (input.Str("op") ?? "new").Trim().ToLowerInvariant() is "new" or "revise" && caller?.IsBrainModel(rt.Options.Llm) != true
                ? "only the Opus brain designs builds; build or show a saved design instead" : null);
        // Hidden (no prompt bytes): the activity behind a build, and the Lua build{}/blueprint() functions.
        // The worker's (and a hive clone's worker's) way to run an approved design: a thin wrapper over BlueprintService
        // (BuildAsync / BuildLaneAsync), the same method the script bindings call. Schema = bp + lane only (worker budget);
        // scripts also pass part/of/layer/by_layer/undo/bodies/crew by name.
        rt.Tools.SetInputAdapter(BuildTool, NormalizeBlueprintId);
        rt.Tools.Register(BuildTool, "Build/resume an approved saved design by id (its saved plan); lane = crew lane; undo/delete:true (trusted word) remove only its placed blocks.",
            ToolSchema.Create().Str("bp").Int("lane", required: false).Build(),
            RunBuildToolAsync, longRunning: true, tiers: ToolTiers.Worker, maxResultChars: 600);
        rt.Tools.SetInputAdapter(InfoTool, NormalizeBlueprintId);
        rt.Tools.Register(InfoTool, "A saved blueprint: size, layers, bill, status.",
            ToolSchema.Create().Str("bp").Build(),
            (input, _) => Task.FromResult(Service.Store.Find(Id(input, "bp") ?? Id(input, "id") ?? "") is { } r ? ToolResult.Success(Service.Describe(r)) : ToolResult.Error("no such blueprint")),
            tiers: ToolTiers.None);
    }

    /// <summary>The hidden build tool: delete / undo / a crew lane / a build (or resume) of a saved design.</summary>
    private async Task<ToolResult> RunBuildToolAsync(JsonElement input, CancellationToken ct)
    {
        var bp = Id(input, "bp") ?? Id(input, "id") ?? "";
        if (input.Bool("delete") == true) return await DeleteFromToolAsync(bp, ct).ConfigureAwait(false);
        if (input.Bool("undo") == true)
        {
            var (uok, utext) = await Service.UndoAsync(bp, ct).ConfigureAwait(false);
            return Outcome(uok, utext);
        }
        // A hive clone's lane of a crew build (main started it as a hive job: build{bp, lane}).
        if (input.Int("lane") is { } lane)
        {
            var (lok, ltext) = await Service.BuildLaneAsync(bp, lane, ct).ConfigureAwait(false);
            return Outcome(lok, ltext);
        }
        if (CrewOf(input) is { } crew && Service.Store.Find(bp) is { } brec) Service.RequestCrew(brec.Id, crew);
        var (ok, text) = await Service.BuildAsync(bp, input.Int("part"), input.Int("of"), input.Int("layer"), ct, input.Bool("by_layer") == true).ConfigureAwait(false);
        return Outcome(ok, text);
    }

    /// <summary>The brain's design delete hands its trusted speaker over; a worker's own call needs a trusted speaker this turn.</summary>
    private async Task<ToolResult> DeleteFromToolAsync(string bp, CancellationToken ct)
    {
        long.TryParse(bp.TrimStart('#'), out var did);
        var by = (did != 0 && _deleteBy.TryRemove(did, out var auth) ? auth : null) ?? TurnContext.Current?.Speaker;
        var (dok, dtext) = await Service.DeleteAsync(bp, by, ct).ConfigureAwait(false);
        return Outcome(dok, dtext);
    }

    // ------------------------------------------------------------------ delegated design builds

    private static readonly System.Text.RegularExpressions.Regex DesignRef = new(
        @"\b(?:design|blueprint|build)\w*\s*(?:#|no\.?\s*|id\s*)?(\d+)\b|#(\d+)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // A build verb heading the reference in the same clause, at most 4 words before it ("continue building design #4",
    // "finish the mob drop tower design"). "Torch the dark side by the mob drop tower (design #8)" is not a design build.
    private static readonly System.Text.RegularExpressions.Regex BuildLead = new(@"\b(?:build|building|construct|continue|resume|finish)\w*[^\w.;!?]+(?:[\w'#-]+[^\w.;!?]+){0,4}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool BuildsAt(string task, int at) => BuildLead.IsMatch(task[..at]);

    /// <summary>
    /// A delegated task that is about building a saved design ("Continue building design #4 'mob drop tower'"): the design it
    /// names, and either the line the worker gets (run <c>build{bp:N}</c>: its saved plan) or why no worker can do it (not
    /// approved: the brain's own <c>design build{id}</c>). (null, null) = not a design build.
    /// </summary>
    internal (string? Hint, string? Refusal) DesignTask(string task)
    {
        if (string.IsNullOrWhiteSpace(task)) return (null, null);
        var rec = DesignByRef(task) ?? DesignByName(task);
        if (rec == null) return (null, null);
        if (IsApproved(rec))
            return ($"[design #{rec.Id} \"{rec.Name}\" is approved: call build{{bp:\"{rec.Id}\"}} (it resumes the saved plan; never place its blocks by hand)]", null);
        return (null, $"design #{rec.Id} \"{rec.Name}\" is {rec.Status}, not approved: a worker can't build it. A trusted player's 'build it', then design build{{id={rec.Id}}}");
    }

    private static bool IsApproved(BlueprintRecord r) => r.Status is DesignStatus.Accepted or DesignStatus.Building or DesignStatus.Built;

    /// <summary>"design #4" (or "#4") headed by a build verb.</summary>
    private BlueprintRecord? DesignByRef(string task)
    {
        foreach (System.Text.RegularExpressions.Match m in DesignRef.Matches(task))
            if ((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) is { Length: > 0 } id && BuildsAt(task, m.Index)
                && Service.Store.Find(id) is { } r) return r;
        return null;
    }

    /// <summary>By name: approved designs first, since two can share one (live: #4 "mob drop tower" built, #8 a draft of it).</summary>
    private BlueprintRecord? DesignByName(string task)
    {
        if (!task.Contains("design", StringComparison.OrdinalIgnoreCase)) return null;
        return Service.Store.List(10).Where(r => r.Name.Length >= 4)
            .OrderBy(r => IsApproved(r) ? 0 : 1)
            .FirstOrDefault(r => task.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) is >= 0 and var at && BuildsAt(task, at));
    }

    /// <summary>The delegate tool, same definition: a design-build task carries its build{bp} line, or is refused up front.</summary>
    private void WrapDelegate(AgentRuntime rt)
    {
        if (!rt.Tools.TryGet(Agent.Worker.WorkerAgent.ToolName, out var inner)) return;
        rt.Tools.Replace(new DesignTaskTool(inner, this));
    }

    private sealed class DesignTaskTool(IAgentTool inner, BlueprintWiring wiring) : IAgentTool
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public JsonElement InputSchema => inner.InputSchema;
        public bool IsLongRunning => inner.IsLongRunning;
        public bool AvailableToCheapTier => inner.AvailableToCheapTier;
        public ToolTiers Tiers => inner.Tiers;
        public int? MaxResultChars => inner.MaxResultChars;

        public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("task", out var t) || t.ValueKind != JsonValueKind.String)
                return inner.ExecuteAsync(input, ct);
            (string? Hint, string? Refusal) d;
            try { d = wiring.DesignTask(t.GetString() ?? ""); } catch (Exception) { return inner.ExecuteAsync(input, ct); }
            if (d.Refusal != null) return Task.FromResult(ToolResult.Error(d.Refusal));
            if (d.Hint == null) return inner.ExecuteAsync(input, ct);
            var o = System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText())!.AsObject();
            o["task"] = t.GetString() + "\n" + d.Hint;
            return inner.ExecuteAsync(JsonSerializer.SerializeToElement(o), ct);
        }
    }

    /// <summary>Keep the script bindings' numeric saved ids and the handler's id alias within the canonical bp schema.</summary>
    private static JsonElement NormalizeBlueprintId(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return input;
        var o = System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText())!.AsObject();
        if (!input.TryGetProperty("bp", out var bp) && input.TryGetProperty("id", out var alias))
            bp = alias;
        if (bp.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            o["bp"] = bp.ValueKind == JsonValueKind.Number ? bp.GetRawText() : bp.GetString();
        return JsonSerializer.SerializeToElement(o);
    }

    /// <summary>An id given as a string ("3", "hut") or a number (Lua <c>build{bp=3}</c>).</summary>
    private static string? Id(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.Number => v.GetRawText(), JsonValueKind.String => v.GetString(), _ => null } : null;

    /// <summary>bodies:["67","69"] (or "67,69", or numbers) and/or crew:N → who builds; null = the default crew.</summary>
    internal static CrewRequest? CrewOf(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        List<string>? names = input.TryGetProperty("bodies", out var b) ? BodyNames(b) : null;
        int? count = input.TryGetProperty("crew", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? Math.Max(1, n) : null;
        if (names is { Count: 0 }) names = null;
        return names == null && count == null ? null : new CrewRequest(names, count);
    }

    private static List<string>? BodyNames(JsonElement b) => b.ValueKind switch
    {
        JsonValueKind.Array => b.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.GetString() ?? "")
            .Where(n => n.Trim().Length > 0).ToList(),
        JsonValueKind.String => (b.GetString() ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        JsonValueKind.Number => [b.GetRawText()],
        _ => null,
    };

    private Func<IReadOnlyList<string>> _trusted = () => [];
    private IReadOnlyList<string> rt_Trusted() => _trusted();

    private static (int W, int H)? Png(byte[] png)
    {
        try { var d = MineClaw.Render.Png.Decode(png); return (d.Width, d.Height); } catch (Exception) { return null; }
    }

    /// <summary>Runs the build as the body's job (an activity: stop cancels it, its end wakes the brain with [task ok/failed]).</summary>
    /// <summary>The trusted player behind a pending design delete (the brain's op → the body's build{delete} job).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _deleteBy = new();

    private string? StartBuild(AgentRuntime rt, long id, int? part, bool undo = false, bool delete = false)
    {
        if (!rt.Tools.TryGet(BuildTool, out var tool)) return "build tool missing";
        var bpId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var input = JsonSerializer.SerializeToElement(delete ? new { bp = bpId, delete = true } : undo ? new { bp = bpId, undo = true } : part is { } p ? new { bp = bpId, part = p } : (object)new { bp = bpId });
        // The body in scope builds (a review line has none: the main body), never a captured rt.Activities.
        var activities = rt.Agent.ActivitiesFor(Bodies.BodyScope.Current?.Name);
        _ = activities.RunAsync([(tool, input)], TimeSpan.Zero, activities.Shutdown);
        return null;
    }

    // ------------------------------------------------------------------ chat review

    private Func<string, BlockPos?> _playerAt = _ => null;

    /// <summary>
    /// A trusted player's chat line opens the designs it is about for editing: one it names ("the spiral staircase", "the stairs"
    /// for "spiral_staircase": a 5-letter stem of a name word), or the standing design they are in or right next to (≤ 3).
    /// Only opens: the edit itself still has to be an explicit, requested dig (<see cref="Protection.EditYields"/>).
    /// </summary>
    internal void TouchFromChat(AgentRuntime rt, string? player, string text)
    {
        if (Edits is not { } e || !e.IsTrusted(player) || string.IsNullOrWhiteSpace(text)) return;
        foreach (var rec in Service.Store.List(30))
            if (IsLive(rec) && Names(text, rec.Name))
                e.Touch(rec.Id, $"{player} named it");
        if (Drift != null && _playerAt(player!) is { } at && Drift.At(at, pad: 3) is { } d) e.Touch(d.Id, $"{player} asked while at it");
    }

    private static bool IsLive(BlueprintRecord rec) => rec.Status is not (DesignStatus.Deleted or DesignStatus.Cancelled or DesignStatus.Failed);

    /// <summary>"keep digging the stairs" names "spiral_staircase": the whole name, or a word sharing a 5-letter stem with one of its words.</summary>
    internal static bool Names(string text, string name)
    {
        static string Norm(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        var t = Norm(text);
        var n = Norm(name).Trim();
        if (n.Length >= 4 && $" {t} ".Contains($" {n} ", StringComparison.Ordinal)) return true;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 5).Select(w => w[..5]).ToHashSet();
        return n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Length >= 5 && words.Contains(w[..5]));
    }

    private void AttachChat(AgentRuntime rt, ILogger? log)
    {
        _trusted = () => rt.Options.Self.TrustedPlayers;
        // A trusted player naming a saved design, or talking to me while standing at one, opens it for editing (their requested
        // digs may change it: "keep digging the staircase", said in the stair shaft).
        rt.Chat.Accepted += m =>
        {
            try { TouchFromChat(rt, m.Player, m.Text); }
            catch (Exception ex) { log?.LogDebug(ex, "design touch from chat failed"); }
        };
        rt.Chat.Accepted += m =>
        {
            try
            {
                if (DesignReview.OnChat(Service, m.Player, m.Text) is not { } did) return;
                log?.LogInformation("blueprints: {Line}", did);
                lock (_consumed)
                {
                    _consumed.RemoveAll(c => DateTimeOffset.UtcNow - c.At > TimeSpan.FromSeconds(30));
                    _consumed.Add((m.Player!, m.Text.Trim(), DateTimeOffset.UtcNow));
                }
                rt.Events.Post(GameEvent.Notice("[design] " + did, EventPriority.Background));
            }
            catch (Exception ex) { log?.LogWarning(ex, "design review failed"); }
        };
        // A line the review handled is context for the brain, not a wake (zero brain tokens for "build it" / "make it taller").
        var prev = rt.Events.Gate;
        Func<GameEvent, GameEvent?> gate = e =>
        {
            if (e.Kind is not (EventKind.Chat or EventKind.Whisper) || e.Player == null) return e;
            lock (_consumed)
                if (_consumed.Any(c => c.Player.Equals(e.Player, StringComparison.OrdinalIgnoreCase) && c.Text == e.Text.Trim()))
                    return e with { Wakes = false, Priority = EventPriority.Background };
            return e;
        };
        rt.Events.Gate = prev == null ? gate : e => prev(e) is { } kept ? gate(kept) : null;
    }

    // ------------------------------------------------------------------ site, materials, recording

    private static SiteContext Site(IGameClient client, Protection protection, AgentRuntime rt, AcquisitionSources? acquisition,
        Func<BlockPos, IReadOnlyList<BlockPos>> genCells)
    {
        var w = client.World;
        bool gen = false;
        int trees = 0;
        try { gen = acquisition?.GeneratorCells?.Invoke() is { Count: > 0 } || genCells(w.Player.Position.ToBlockPos()).Count > 0; } catch (Exception) { }
        try
        {
            var census = TreeStewardship.Census(w, w.Player.Position);
            trees = Math.Max(0, census.Trees.Count(TreeStewardship.IsLiving) - (acquisition?.MinTrees ?? TreeStewardship.DefaultMinTrees));
        }
        catch (Exception) { }
        var stock = new Lazy<ChestStock>(() =>
        {
            try { return ChestStock.Read(client, rt.Observations, acquisition?.SkipChest); } catch (Exception) { return ChestStock.Empty; }
        });
        return new SiteContext
        {
            World = w.PlanningView,
            // A revision of a design is checked against everything but itself (saving is a record, not a lock).
            Verdict = protection.CheckFor,
            Inventory = (id, dmg) => w.Inventory.CountItem(id, dmg >= 0 ? dmg : null),
            // The chests the obtainer would take from, read once per site; the exact variant (44:0 never counts for 44:3).
            Stored = (id, dmg) => stock.Value.Count(id, dmg),
            GeneratorKnown = gen,
            ChoppableTrees = trees,
        };
    }

    private static (SitePick?, string?) ResolveSite(IGameClient client, VisionHub? hub, Protection protection, string? at, string? who, TimeProvider clock,
        AgentRuntime? rt = null)
    {
        var w = client.World;
        var requester = who != null ? w.Entities.FindPlayer(who) : null;
        // What the player referenced, never the bot's own spot or a nearby landmark by default (live 10-28: "wrong spot").
        bool pointedNow = PointedBy(hub, who, clock) != null;
        if (Building.SiteAnchor.IsHere(at) || string.IsNullOrWhiteSpace(at) && !pointedNow) return StandingSite(w, requester, who);
        var near = requester?.Position.ToBlockPos() ?? w.Player.Position.ToBlockPos();
        if (StructureSite(w, rt, at, near, clock) is { } on) return (on, null);
        if (at is { Length: > 0 } a) return NamedSite(w, hub, a);
        // Pointed recently by the requester: build there.
        if (PointedBy(hub, who, clock) is { } g)
            return (new SitePick(BlueprintService.Ground(w.PlanningView, g.Pos), $"where {g.Player} pointed", null, Inferred: true), null);
        return (null, "where should it go? (say 'here' where you stand, point at a block, or name a place)");
    }

    /// <summary>The requester's (or anyone's, without one) point gesture of the last 10 minutes.</summary>
    private static Perception.Gesture? PointedBy(VisionHub? hub, string? who, TimeProvider clock) =>
        hub?.LastPoint is { } lp && (who == null || lp.Player.Equals(who, StringComparison.OrdinalIgnoreCase)) && clock.GetUtcNow() - lp.At < TimeSpan.FromMinutes(10)
            ? lp : null;

    /// <summary>"here" (or nothing said and nothing pointed): where the requester stands, facing their way.</summary>
    private static (SitePick?, string?) StandingSite(WorldState w, Entity? requester, string? who)
    {
        if (requester == null) return (null, $"where should it go? I can't see {who ?? "you"} (stand there and say 'here', point at a block, or name a place)");
        var feet = requester.Position.ToBlockPos();
        var facing = Building.SiteAnchor.Facing(requester.Yaw);
        return (new SitePick(BlueprintService.Ground(w.PlanningView, feet.Down()), $"where {who} is standing, facing {facing.Name()}", null, Inferred: true), null);
    }

    /// <summary>"on the floor I just built", or "on the &lt;remembered structure&gt;": its top.</summary>
    private static SitePick? StructureSite(WorldState w, AgentRuntime? rt, string? at, BlockPos near, TimeProvider clock)
    {
        if (Building.SiteAnchor.IsRecentBuild(at) && Building.SiteAnchor.RecentBuild(w.Placed.All(), w.Dimension, near, clock.GetUtcNow(), TimeSpan.FromHours(2)) is { } floor)
            return new SitePick(Building.SiteAnchor.Ground(floor), $"on the {floor.Name}", floor);
        if (rt == null || Building.SiteAnchor.OnWhat(at) is not { } what || rt.Memory.GetStructure(what) is not { Min: { } mn } st) return null;
        var mx = st.Max ?? mn;
        var ext = new Building.SiteExtent(new BlockPos(Math.Min(mn.X, mx.X), Math.Max(mn.Y, mx.Y), Math.Min(mn.Z, mx.Z)),
            new BlockPos(Math.Max(mn.X, mx.X), Math.Max(mn.Y, mx.Y), Math.Max(mn.Z, mx.Z)), st.Name);
        return new SitePick(Building.SiteAnchor.Ground(ext), $"on the {st.Name}", ext);
    }

    /// <summary>A named site: what vision resolves (a player, a saved place, pointed), else bare coordinates.</summary>
    private static (SitePick?, string?) NamedSite(WorldState w, VisionHub? hub, string a)
    {
        if (hub != null)
        {
            var (focus, player, error) = hub.ResolveFocus(a);
            if (error != null) return (null, error);
            return (new SitePick(BlueprintService.Ground(w.PlanningView, player != null ? focus.Down() : focus), $"at {a}"), null);
        }
        if (Locations.ParseCoords(a) is { } p) return (new SitePick(BlueprintService.Ground(w.PlanningView, p), $"at {a}"), null);
        return (null, $"unknown site '{a}' (x,y,z, pointed, a player or a saved place)");
    }

    /// <summary>
    /// A player-requested build becomes a soft-protected structure: its blocks leave the placed-block log (which would otherwise
    /// let any bot take them back) and are remembered as "&lt;name&gt;" (protection: "part of the hut build"). Scaffolding was removed
    /// and anything left of it stays self-placed.
    /// </summary>
    /// <summary>The nearest remembered waypoint or structure (within 24 blocks) to a cell, for relative wording.</summary>
    private static Building.NamedPlace? NearestPlace(AgentRuntime rt, BlockPos p)
    {
        try
        {
            int dim = rt.Memory.CurrentDimension;
            var (wps, sts) = rt.Memory.Read(d => (d.Waypoints.Where(x => x.Dimension == dim).ToList(), d.Structures.Where(x => x.Dimension == dim && x.Min != null).ToList()));
            var all = wps.Select(x => (x.Name, At: new BlockPos(x.X, x.Y, x.Z)))
                .Concat(sts.Select(x => (x.Name, At: new BlockPos((x.Min!.X + (x.Max ?? x.Min).X) / 2, x.Min.Y, (x.Min.Z + (x.Max ?? x.Min).Z) / 2))));
            var best = all.Select(x => (x.Name, x.At, D: x.At.DistanceSquaredTo(p))).Where(x => x.D <= 24 * 24).OrderBy(x => x.D).FirstOrDefault();
            return best.Name != null ? new Building.NamedPlace(best.Name, best.At) : null;
        }
        catch (Exception) { return null; }
    }

    private static void Record(AgentRuntime rt, IGameClient client, BlueprintRecord rec, VoxelPlan plan)
    {
        var w = client.World;
        foreach (var b in plan.Blocks.Keys) w.Placed.Forget(b, w.Dimension);
        // A design that already has its structure (a captured build reverted in place, a revision built over it): that one moves.
        var linked = rt.Memory.Read(d => d.Structures.FirstOrDefault(s => Memory.DesignLinks.Of(s.Note) == rec.Id)?.Name);
        if (linked != null)
        {
            DesignHistory.MoveLink(rt.Memory, rec, plan.Min, plan.Max, null);
            return;
        }
        var name = rt.Memory.GetStructure(rec.Name) is { Min: { } m } existing && (m.X != plan.Min.X || m.Y != plan.Min.Y || m.Z != plan.Min.Z) ? $"{rec.Name} #{rec.Id}" : rec.Name;
        rt.Memory.UpsertStructure(name, s =>
        {
            s.Kind = "build";
            s.Min = new MPos(plan.Min.X, plan.Min.Y, plan.Min.Z);
            s.Max = new MPos(plan.Max.X, plan.Max.Y, plan.Max.Z);
            s.Dimension = w.Dimension;
            s.TaughtBy = rec.AcceptedBy ?? rec.Requester;
            s.Confidence = 1.0;
            s.Note = $"built from design #{rec.Id} for {rec.Requester ?? "a player"}: {rec.Description}";
        });
    }
}
