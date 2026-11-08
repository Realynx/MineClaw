using System.Text;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Worker;

/// <summary>Outcome of one delegated task (also what tests inspect).</summary>
public sealed record WorkerOutcome(bool Ok, string Summary, int Calls, int ToolCalls, decimal CostUsd, string StopReason)
{
    public bool NeedsInput => Summary.StartsWith(WorkerAgent.NeedsInputPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The worker/builder tier. Runs one delegated task as a short-lived tool loop on the worker model: its own small context
/// (task + tool results only, never committed to the brain's history), its own cached prefix (worker system prompt +
/// worker tools), and a per-task budget of rounds, minutes and dollars. Game tools are executed directly with the task's
/// token — the delegate call itself is the bot's activity, so <c>stop</c>, a new activity or an interrupt cancels both.
/// Returns ONE terse summary ("ok: ...", "failed: ...", "needs_input: ...") of at most <see cref="WorkerOptions.SummaryChars"/>.
/// </summary>
public sealed class WorkerAgent
{
    public const string ToolName = "delegate";
    public const string AmendToolName = "amend_task";
    public const string NeedsInputPrefix = "needs_input";

    private readonly WorkerOptions _o;
    private readonly LlmOptions _llmOptions;
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly UsageTracker _usage;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private IReadOnlyList<LlmSystemBlock> _system;

    public WorkerAgent(WorkerOptions options, LlmOptions llmOptions, ILlmClient llm, ToolRegistry tools, UsageTracker usage,
        string coreKnowledge = "", TimeProvider? time = null, ILogger? log = null, TerseLevel terse = TerseLevel.Off)
    {
        _o = options;
        _llmOptions = llmOptions;
        _llm = llm;
        _tools = tools;
        _usage = usage;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        // Core rules only (RAG on); with RAG off the knowledge is the whole doc set, so keep the worker's share bounded.
        var k = options.IncludeCoreKnowledge ? TextUtil.Truncate(coreKnowledge ?? "", 6000) : "";
        _system = [new LlmSystemBlock(Prompts.WorkerSystem(k, terse), Cache: true, Ttl: "5m")];
    }

    /// <summary>
    /// Questions policy: a needs_input with "default: X" doesn't end the task — the worker carries on with the default (risky
    /// choices: the safe default) and <see cref="Asked"/> tells the brain, which asks the player and amends later if needed.
    /// Null = needs_input always ends the task.
    /// </summary>
    public Human.QuestionOptions? Questions { get; set; }

    /// <summary>Max default continuations per task (then needs_input ends it as before).</summary>
    public int MaxDefaultContinues { get; set; } = 2;

    /// <summary>(question, default the worker went on with) — raised when a task kept going on its recommended answer.</summary>
    public event Action<string, string>? Asked;

    private static readonly System.Text.RegularExpressions.Regex NeedsDefault = new(
        @"needs_input:\s*(?<q>.+?)\s*(?:\(|;|,|\.|-)?\s*default:\s*(?<d>[^)\n]+)\)?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>One-line game state for the task's first message (the bot agent's state header).</summary>
    public Func<string>? StateHeader { get; set; }

    /// <summary>Raised after every finished task (logs, tests).</summary>
    public event Action<WorkerOutcome>? Finished;

    /// <summary>Raised after each worker model call (latency telemetry: rounds before the first game action).</summary>
    public event Action? CallCompleted;

    public IReadOnlyList<LlmSystemBlock> SystemPrompt => _system;

    /// <summary>
    /// Appends a static block to the worker's cached system prompt (e.g. the script API cheat-sheet). Startup only, before
    /// the first task: it is part of the cached prefix.
    /// </summary>
    public void ExtendSystem(string block)
    {
        if (string.IsNullOrWhiteSpace(block) || _system[0].Text.Contains(block, StringComparison.Ordinal)) return;
        _system = [_system[0] with { Text = _system[0].Text.TrimEnd() + "\n" + block.Trim() + "\n" }, .. _system.Skip(1)];
    }

    private volatile string? _trail;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _amendments = new();
    private int _running;

    /// <summary>A delegated task is running right now.</summary>
    public bool IsRunning => Volatile.Read(ref _running) > 0;

    /// <summary>
    /// Adds a note from the brain to the running task (a player added "and bring me 10"): the worker sees it as
    /// "[brain amends task] ..." with its next tool results, instead of the task being cancelled and redone. False = idle.
    /// </summary>
    public bool Amend(string note)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(note)) return false;
        _amendments.Enqueue(TextUtil.Truncate(TextUtil.OneLine(note.Trim()), 300));
        return true;
    }

    /// <summary>
    /// A player's correction straight from chat (zero tokens, no brain round): "just like 5" while a torch task runs reaches the
    /// worker's next round as "[player amends task] ...". True when a task was running and the line was an amendment.
    /// </summary>
    public bool AmendFromChat(string player, string text)
    {
        if (!IsRunning || TaskAmendment.Detect(text) is not { } a) return false;
        _amendments.Enqueue(TextUtil.Truncate(TextUtil.OneLine("[player] " + a.Note(player)), 300));
        LastLiveAmendment = $"{player}: \"{a.Text}\"";
        return true;
    }

    /// <summary>The last amendment forwarded from chat during the running task (the brain's busy detail says it's done).</summary>
    public string? LastLiveAmendment { get; private set; }

    /// <summary>
    /// Per-round context from the body (auto-vision: "[vision] ..." after a spatial step, ≈100 tokens of verified text), appended
    /// to the newest tool results. Null/empty = nothing. Bounded by the hook itself (it never waits long).
    /// </summary>
    public Func<CancellationToken, Task<string?>>? RoundNote { get; set; }

    private async Task ApplyRoundNoteAsync(List<LlmMessage> messages, CancellationToken ct)
    {
        if (RoundNote == null || messages.Count == 0 || messages[^1].Role != LlmRole.User) return;
        string? note = null;
        try { note = await RoundNote(ct).ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* vision never breaks a task */ }
        if (!string.IsNullOrWhiteSpace(note)) AppendText(messages, TextUtil.Truncate(note.Trim(), 600));
    }

    private void ApplyAmendments(List<LlmMessage> messages)
    {
        var notes = new List<string>();
        while (_amendments.TryDequeue(out var n)) notes.Add(n);
        if (notes.Count > 0) AppendText(messages, "[brain amends task] " + string.Join(" / ", notes));
    }

    /// <summary>
    /// What the running (or last) task did, newest last: "craft! need 3 planks > chop_tree (running)". The stop tool reports
    /// it, so the brain can tell players what really happened instead of inventing a reason.
    /// </summary>
    public string? Trail => _trail;

    /// <summary>
    /// Called when a task starts; the function it returns, called at the end, says what the bot gained and where it stored it
    /// meanwhile ("32 cobblestone; stored 30 cobblestone in chest 14,64,-5"), or null for nothing. A task that ran out of its
    /// minutes with gains is a finished time slice, not a failure (live 10-27 23:13: a 5-min gen slice that mined reported
    /// "failed: out of time (5 min); 1 actions").
    /// </summary>
    public Func<Func<string?>>? GainsTracker { get; set; }

    /// <summary>Landmark describer for coordinates in needs_input results ("4,64,6 (the west gen cell)"); null = none.</summary>
    public Func<int, int, int, string?>? Landmark { get; set; }

    /// <summary>
    /// One brain, many bodies: pushes the body this worker drives around every tool call (the host's <c>BodyScope.Push</c>;
    /// the Agent layer only sees the disposable). Null = no scope (tests, no body layer).
    /// </summary>
    public Func<IDisposable?>? StepScope { get; set; }

    /// <summary>
    /// One brain, many bodies: the per-body workers behind <c>delegate</c> (null = this one worker, as before). Set on the
    /// runtime's worker only.
    /// </summary>
    public WorkerPool? Pool { get; set; }

    /// <summary>
    /// A worker for another body: the same options, model, tools, cached system prompt (byte-identical, so the prefix cache is
    /// shared) and hooks; its own task state (running, trail, amendments). Its Finished/Asked/CallCompleted are raised on this
    /// worker too, so telemetry and the brain's notices see every body's runs. The pool sets its body scope.
    /// </summary>
    public WorkerAgent Sibling()
    {
        var w = new WorkerAgent(_o, _llmOptions, _llm, _tools, _usage, "", _time, _log)
        {
            Questions = Questions, MaxDefaultContinues = MaxDefaultContinues, StateHeader = StateHeader, GainsTracker = GainsTracker,
            Landmark = Landmark, RoundNote = RoundNote, StepScope = StepScope,
        };
        w._system = _system;
        w.Finished += o => Finished?.Invoke(o);
        w.Asked += (q, d) => Asked?.Invoke(q, d);
        w.CallCompleted += () => CallCompleted?.Invoke();
        return w;
    }

    /// <summary>The worker's tool definitions (its cached prefix, stable for the run).</summary>
    public IReadOnlyList<LlmToolDefinition> ToolDefinitions => _tools.Definitions(ModelTier.Worker);

    /// <summary>Registers <c>delegate</c> (brain-only, long-running: it runs as the bot's activity).</summary>
    public void Register()
    {
        var schema = ToolSchema.Create().Str("task", "concrete goal with items/places").Str("done_when", "checkable finish condition")
            .Int("max_minutes", $"default {_o.DefaultMinutes}, max {_o.MaxMinutes}", required: false);
        // Only when some profile is usable (a provider configured): the field costs prefix tokens.
        if (_llmOptions.UsableProfiles().Any()) schema = schema.Str("profile", required: false);
        _tools.Register(ToolName,
            "Hand a multi-step game task (mine, craft, smelt, build, chests, farm...) to your worker; it runs in the " +
            "background and reports ok/failed/needs_input. Change a running one with amend_task.",
            schema.Build(),
            async (input, ct) =>
            {
                var profile = input.Str("profile") is { Length: > 0 } pr ? pr.Trim() : null;
                if (profile != null && _llmOptions.CheckProfile(profile) is { } bad) return ToolResult.Error("failed: " + bad);
                var task = input.ReqStr("task");
                WorkerOutcome o;
                if (Pool is { } pool)
                {
                    // One brain, many bodies: the worker of the body this delegate runs on, within the pool's cap.
                    var body = pool.CurrentBody();
                    var w = pool.For(body);
                    o = await pool.RunAsync(body, () => w.RunAsync(task, input.Str("done_when"), input.Int("max_minutes"), ct, profile), ct).ConfigureAwait(false);
                }
                else o = await RunAsync(task, input.Str("done_when"), input.Int("max_minutes"), ct, profile).ConfigureAwait(false);
                // needs_input usually ends in a question for the players: give the brain landmarks next to the coordinates.
                var summary = o.NeedsInput ? Human.Questions.Annotate(o.Summary, Landmark) : o.Summary;
                summary += $"; {o.ToolCalls} actions";
                return o.Ok ? ToolResult.Success(summary) : ToolResult.Error(summary);
            },
            longRunning: true, tiers: ToolTiers.Main, maxResultChars: _o.SummaryChars + 140);

        _tools.Register(AmendToolName,
            "Add a correction/detail to the running delegate task (no restart); the worker sees it next step.",
            ToolSchema.Create().Str("note").Build(),
            (input, _) => Task.FromResult((Pool?.Current() ?? this).Amend(input.ReqStr("note"))
                ? ToolResult.Success("ok: the worker gets it at its next step")
                : ToolResult.Error("no delegate task running (a script? use script op=replace)")),
            tiers: ToolTiers.Main);
    }

    /// <summary>Runs one task to completion, including a partial failed outcome when interrupted or cancelled.</summary>
    /// <param name="profile">Named model profile the worker loop runs on (validated; null = the worker tier's model).</param>
    public async Task<WorkerOutcome> RunAsync(string task, string? doneWhen, int? maxMinutes, CancellationToken ct, string? profile = null)
    {
        if (profile != null && _llmOptions.CheckProfile(profile) is { } bad) return Done(new(false, "failed: " + bad, 0, 0, 0, "profile"));
        profile = EffectiveProfile(profile);
        var prof = profile != null ? _llmOptions.Profile(profile) : null;
        int minutes = Math.Clamp(maxMinutes ?? _o.DefaultMinutes, 1, Math.Max(1, _o.MaxMinutes));
        if (BudgetTooLow()) return Done(new(false, "failed: budget low, no worker tasks right now", 0, 0, 0, "budget"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(minutes), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var run = BeginTask(task, doneWhen, minutes, profile, prof, linked.Token);
        try
        {
            await RoundsAsync(run).ConfigureAwait(false);
            await WrapUpAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Stop = ct.IsCancellationRequested ? "cancelled" : "time";
        }
        catch (Exception ex)
        {
            run.Stop = "error";
            run.Log.Add("error: " + TextUtil.OneLine(ex.Message));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
        return Done(FinishTask(run, task));
    }

    /// <summary>One delegated task's loop state (its context, counters, result so far).</summary>
    private sealed class TaskRun
    {
        public required int Minutes;
        public required string? Profile;
        public required LlmProfile? Prof;
        public required CancellationToken Token;
        public required IReadOnlyList<LlmToolDefinition> Tools;
        public required List<LlmMessage> Messages;
        public required Func<string?>? Gains;
        public int Calls, ToolCalls, Continued;
        public decimal Cost;
        public string? Summary;
        public string Stop = "done";
        public readonly List<string> Log = [];
        public readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);
    }

    /// <summary>Workers run on WorkerModel unless the profile explicitly says otherwise (an Opus profile alone never makes an Opus worker).</summary>
    private string? EffectiveProfile(string? profile)
    {
        var effective = _llmOptions.WorkerProfileFor(profile);
        if (profile != null && !string.Equals(effective, profile, StringComparison.OrdinalIgnoreCase))
            _log.LogInformation("delegate profile {Asked} → worker runs on {Effective}", profile, effective ?? _llmOptions.WorkerModel);
        return effective;
    }

    private bool BudgetTooLow() => _usage.CheckMode() is BudgetMode.Paused or BudgetMode.CheapOnly;

    /// <summary>Starts the task's state: its first message, no leftover amendments, running, a fresh trail and gains tracker.</summary>
    private TaskRun BeginTask(string task, string? doneWhen, int minutes, string? profile, LlmProfile? prof, CancellationToken token)
    {
        var tools = ToolDefinitions;
        var messages = new List<LlmMessage> { LlmMessage.User(FirstMessage(task, doneWhen, minutes)) };
        while (_amendments.TryDequeue(out _)) { } // notes for an earlier task never leak into this one
        LastLiveAmendment = null;
        Interlocked.Increment(ref _running);
        _trail = null;
        Func<string?>? gains = null;
        try { gains = GainsTracker?.Invoke(); } catch (Exception) { /* telemetry only */ }
        return new TaskRun { Minutes = minutes, Profile = profile, Prof = prof, Token = token, Tools = tools, Messages = messages, Gains = gains };
    }

    /// <summary>The tool loop: model call, then its tools, until a summary, a limit or a dead end.</summary>
    private async Task RoundsAsync(TaskRun run)
    {
        for (int round = 0; ; round++)
        {
            if (RoundLimit(run, round) is { } limit) { run.Stop = limit; return; }
            ApplyAmendments(run.Messages);
            if (round > 0) await ApplyRoundNoteAsync(run.Messages, run.Token).ConfigureAwait(false);
            if (!FitContext(run.Messages, run.Tools)) { run.Stop = "context full"; return; }
            if (!await RoundAsync(run, round).ConfigureAwait(false)) return;
        }
    }

    private string? RoundLimit(TaskRun run, int round)
    {
        if (round >= _o.MaxRounds) return "max rounds";
        if (run.Cost >= _o.MaxCostUsd) return "cost cap";
        return BudgetTooLow() ? "budget low" : null;
    }

    /// <summary>One model call and its tools. False = the loop ends (a summary, truncation, a dead end).</summary>
    private async Task<bool> RoundAsync(TaskRun run, int round)
    {
        var response = await CallAsync(run.Messages, run.Tools, run.Token, run.Profile, run.Prof).ConfigureAwait(false);
        RecordCall(run, response);
        _log.LogInformation("worker call {Round}: {Model} {Usage} cache {Hit:P0}", round, response.Model, response.Usage, UsageTracker.CacheHit(response.Usage));
        try { CallCompleted?.Invoke(); } catch (Exception) { /* telemetry only */ }

        var content = response.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
        if (content.Count == 0) content.Add(new LlmText("(silent)"));
        run.Messages.Add(new LlmMessage(LlmRole.Assistant, content));

        var uses = response.ToolUses.ToList();
        if (response.StopReason == StopReasons.MaxTokens)
        {
            MarkTruncated(run, uses);
            return false;
        }
        if (uses.Count == 0 || response.StopReason == StopReasons.Refusal) return ContinueOnDefault(run, response);

        await RunToolsAsync(run, uses, response.Model).ConfigureAwait(false);
        return run.Stop != "repeated failure";
    }

    private void RecordCall(TaskRun run, LlmResponse response)
    {
        run.Calls++;
        run.Cost += _usage.Record(response.Model, response.Usage, ModelTier.Worker);
    }

    /// <summary>A truncated reply: its tools are answered "not executed" and the task fails.</summary>
    private static void MarkTruncated(TaskRun run, List<LlmToolUse> uses)
    {
        if (uses.Count > 0)
            run.Messages.Add(new LlmMessage(LlmRole.User, uses.Select(u => (LlmBlock)new LlmToolResult(u.Id,
                "not executed: response truncated (max_tokens)", true)).ToList()));
        run.Stop = StopReasons.MaxTokens;
        run.Summary = "failed: response truncated (max_tokens); remaining tools not executed";
    }

    /// <summary>
    /// A reply without tools is the summary — except needs_input with a recommended answer: don't park the bot. Carry on with
    /// the default (true = keep looping); the brain asks.
    /// </summary>
    private bool ContinueOnDefault(TaskRun run, LlmResponse response)
    {
        run.Summary = response.Text.Trim();
        if (Questions is not { } qo || run.Continued >= MaxDefaultContinues || response.StopReason == StopReasons.Refusal) return false;
        if (NeedsDefault.Match(ResultLine(run.Summary)) is not { Success: true } nd) return false;
        var question = nd.Groups["q"].Value.Trim();
        var dflt = nd.Groups["d"].Value.Trim().TrimEnd('.');
        bool risky = qo.IsRisky(question + " " + dflt);
        if (risky) dflt = qo.SafeDefault;
        run.Continued++;
        try { Asked?.Invoke(question, dflt); } catch (Exception) { }
        AppendText(run.Messages, $"[planner] go ahead with \"{dflt}\" for now" + (risky ? " (do the rest, not the risky part)" : "") +
            "; an amend comes if the player wants otherwise.");
        run.Summary = null;
        return true;
    }

    /// <summary>Runs the reply's tools in order (the bot has one body) and adds their results as the next user message.</summary>
    private async Task RunToolsAsync(TaskRun run, List<LlmToolUse> uses, string model)
    {
        var results = new List<LlmBlock>();
        foreach (var u in uses)
        {
            var r = await RunToolAsync(run, u, model).ConfigureAwait(false);
            run.Log.Add($"{u.Name}{(r.Ok ? "" : "!")} {TextUtil.Truncate(TextUtil.OneLine(r.Text), 160)}");
            run.Token.ThrowIfCancellationRequested();
            _trail = TrailOf(run.Log, null);
            results.Add(new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok) { Image = r.Image });
            if (!r.Ok) NoteFailure(run, u.Name, r.Text);
        }
        run.Messages.Add(new LlmMessage(LlmRole.User, results));
    }

    private async Task<ToolResult> RunToolAsync(TaskRun run, LlmToolUse u, string model)
    {
        if (!_tools.TryGet(u.Name, out var tool) || !_tools.Allows(tool, ModelTier.Worker))
            return ToolResult.Error($"no tool '{u.Name}' here");
        // Sequential: the bot has one body. Each game tool honours the task token.
        _trail = TrailOf(run.Log, $"{u.Name} {TextUtil.Truncate(TextUtil.OneLine(u.Input.GetRawText()), 50)} (running)");
        ToolResult r;
        using (ToolCaller.Push(ModelTier.Worker, model))
        using (StepScope?.Invoke()) // the body this worker drives (explicit, never just inherited)
            r = await _tools.ExecuteAsync(tool, u.Input, run.Token).ConfigureAwait(false);
        run.ToolCalls++;
        return r;
    }

    /// <summary>A failed tool: a protection refusal or the same failure again ends the task (first reason wins).</summary>
    private void NoteFailure(TaskRun run, string name, string text)
    {
        // Protection rule 5: the first protected refusal ends the task and goes back as-is (why + unlock phrase),
        // never retried a second way (live 10-27: two workers and a script hit the same protected log).
        if (run.Summary == null && IsProtectionRefusal(text))
        {
            run.Stop = "repeated failure";
            run.Summary = $"failed: {name}: {TextUtil.Truncate(TextUtil.OneLine(text), 260)}";
        }
        if (_o.MaxIdenticalFailures <= 0 || ToolCallGuard.IsTransientCombat(text)) return;
        var key = FailureKey(name, text);
        int n = run.Failures[key] = run.Failures.GetValueOrDefault(key) + 1;
        if (n < _o.MaxIdenticalFailures || run.Summary != null) return;
        run.Stop = "repeated failure";
        run.Summary = $"failed: {name} failed {n}x the same way: {TextUtil.Truncate(TextUtil.OneLine(text), 160)}";
    }

    /// <summary>Out of rounds/cost/context: one wrap-up call (same prefix, so it's mostly cache) for an honest summary.</summary>
    private async Task WrapUpAsync(TaskRun run)
    {
        if (run.Summary != null || run.Stop is not ("max rounds" or "cost cap" or "context full") || run.Cost >= _o.MaxCostUsd * 1.5m) return;
        AppendText(run.Messages, $"[{run.Stop}] Stop now. Reply with the one-line summary (ok:/failed:/needs_input:), no tools.");
        if (!FitContext(run.Messages, run.Tools)) return;
        var response = await CallAsync(run.Messages, run.Tools, run.Token, run.Profile, run.Prof).ConfigureAwait(false);
        RecordCall(run, response);
        if (response.StopReason == StopReasons.MaxTokens)
        {
            run.Messages.Add(new LlmMessage(LlmRole.Assistant, response.Content));
            MarkTruncated(run, response.ToolUses.ToList());
        }
        else if (!response.ToolUses.Any() && response.Text.Trim() is { Length: > 0 } t) run.Summary = t;
    }

    /// <summary>The task's one summary (a time slice with gains is done; otherwise the stop reason) and its outcome.</summary>
    private WorkerOutcome FinishTask(TaskRun run, string task)
    {
        if (run.Summary == null && run.Stop == "time" && SafeGains(run.Gains) is { } got)
            run.Summary = $"ok: time slice done ({run.Minutes} min): {got}";
        var summary = run.Summary ?? StopSummary(run);
        var outcome = Classify(summary, run.Calls, run.ToolCalls, run.Cost, run.Stop);
        _log.LogInformation("worker task \"{Task}\": {Summary} ({Calls} calls, {Tools} tools, ${Cost:0.0000}, {Stop})",
            TextUtil.Truncate(task, 60), outcome.Summary, run.Calls, run.ToolCalls, run.Cost, run.Stop);
        return outcome;
    }

    private static string StopSummary(TaskRun run) => run.Stop switch
    {
        "time" => $"failed: out of time ({run.Minutes} min)",
        "budget low" => "failed: budget low, stopped",
        "cancelled" => "failed: cancelled; partial work only",
        _ => $"failed: stopped ({run.Stop})",
    } + (run.Log.Count > 0 ? "; last: " + run.Log[^1] : "") + $"; {run.ToolCalls} actions";

    private static string? SafeGains(Func<string?>? gains)
    {
        try { return gains?.Invoke() is { Length: > 0 } g ? g : null; } catch (Exception) { return null; }
    }

    /// <summary>"fill_bucket" + "no water source at 1,64,-9" → "fill_bucket|no water source at ,,-": coordinates/counts don't make a failure new.</summary>

    /// <summary>A refusal of the protection policy (MineClaw.World.ProtectionText: "... is protected (why); say '...'").</summary>
    public static bool IsProtectionRefusal(string? text) =>
        text is { Length: > 0 } && text.Contains(" is protected", StringComparison.Ordinal)
        && !text.Contains("spawn protection", StringComparison.OrdinalIgnoreCase);

    internal static string FailureKey(string tool, string error)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(TextUtil.OneLine(error ?? "").ToLowerInvariant(), @"[\d.]+", "");
        return tool + "|" + TextUtil.Truncate(t.Trim(), 80);
    }

    private static string TrailOf(List<string> log, string? running)
    {
        var parts = log.TakeLast(running == null ? 3 : 2).ToList();
        if (running != null) parts.Add(running);
        return string.Join(" > ", parts);
    }

    private WorkerOutcome Done(WorkerOutcome o)
    {
        Finished?.Invoke(o);
        return o;
    }

    /// <summary>
    /// The result line: the text from the last "ok:" / "failed:" / "needs_input:" that starts a line or sentence, so a
    /// short reasoning preface ("Two chests could be Fox's. needs_input: ...") never hides or mislabels the outcome.
    /// </summary>
    internal static string ResultLine(string text)
    {
        var t = text.Trim();
        if (StartsWithPrefix(t)) return t;
        var m = ResultPrefix.Matches(t);
        return m.Count > 0 ? t[m[^1].Groups[1].Index..].Trim() : t;
    }

    private static bool StartsWithPrefix(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"^(?:ok|failed|needs_input)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex ResultPrefix =
        new(@"(?:^|[\n.!?]\s*)((?:ok|failed|needs_input):)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private WorkerOutcome Classify(string text, int calls, int toolCalls, decimal cost, string stop)
    {
        var s = TextUtil.Truncate(TextUtil.OneLine(ResultLine(text)), Math.Max(40, _o.SummaryChars));
        bool ok = s.StartsWith("ok", StringComparison.OrdinalIgnoreCase) && !s.StartsWith(NeedsInputPrefix, StringComparison.OrdinalIgnoreCase);
        bool known = ok || s.StartsWith("failed", StringComparison.OrdinalIgnoreCase) || s.StartsWith(NeedsInputPrefix, StringComparison.OrdinalIgnoreCase);
        if (!known)
        {
            // No prefix: trust it only if the loop ended normally.
            ok = stop == "done";
            s = TextUtil.Truncate((ok ? "ok: " : "failed: ") + s, Math.Max(40, _o.SummaryChars));
        }
        return new WorkerOutcome(ok, s, calls, toolCalls, cost, stop);
    }

    private string FirstMessage(string task, string? doneWhen, int minutes)
    {
        string header = "";
        try { header = StateHeader?.Invoke() ?? ""; } catch (Exception) { }
        var sb = new StringBuilder();
        if (header.Length > 0) sb.Append(header.Trim()).Append('\n');
        sb.Append("task: ").Append(task.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(doneWhen)) sb.Append("done when: ").Append(doneWhen.Trim()).Append('\n');
        sb.Append($"budget: {_o.MaxRounds} rounds, {minutes} min");
        return sb.ToString();
    }

    private Task<LlmResponse> CallAsync(List<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, CancellationToken ct,
        string? profile = null, LlmProfile? prof = null) =>
        _llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Worker,
            System = _system,
            Tools = tools,
            Messages = messages.ToArray(),
            Purpose = "worker",
            // A delegate profile: that model (and provider) for this task's loop; still the worker tier (never script authorship).
            Model = prof?.Model.Trim(),
            Profile = profile,
            MaxTokens = prof?.MaxTokens is > 0 ? prof.MaxTokens : null,
            Thinking = string.IsNullOrWhiteSpace(prof?.Thinking) ? null : prof.Thinking,
        }, ct);

    /// <summary>Keeps the worker context under <see cref="WorkerOptions.MaxContextTokens"/> (older results cut first).</summary>
    private bool FitContext(List<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools) =>
        ContextManager.FitMessages(messages, _system, tools, _llmOptions.MaxTokensFor(ModelTier.Worker), _o.MaxContextTokens);

    private static void AppendText(List<LlmMessage> messages, string text)
    {
        var last = messages[^1];
        if (last.Role == LlmRole.User) messages[^1] = last with { Content = [.. last.Content, new LlmText(text)] };
        else messages.Add(LlmMessage.User(text));
    }
}
