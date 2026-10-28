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

    /// <summary>Landmark describer for coordinates in needs_input results ("4,64,6 (the west gen cell)"); null = none.</summary>
    public Func<int, int, int, string?>? Landmark { get; set; }

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
                var o = await RunAsync(input.ReqStr("task"), input.Str("done_when"), input.Int("max_minutes"), ct, profile).ConfigureAwait(false);
                // needs_input usually ends in a question for the players: give the brain landmarks next to the coordinates.
                var summary = o.NeedsInput ? Human.Questions.Annotate(o.Summary, Landmark) : o.Summary;
                return o.Ok ? ToolResult.Success(summary) : ToolResult.Error(summary);
            },
            longRunning: true, tiers: ToolTiers.Main, maxResultChars: _o.SummaryChars + 140);

        _tools.Register(AmendToolName,
            "Add a correction/detail to the running delegate task (no restart); the worker sees it next step.",
            ToolSchema.Create().Str("note").Build(),
            (input, _) => Task.FromResult(Amend(input.ReqStr("note"))
                ? ToolResult.Success("ok: the worker gets it at its next step")
                : ToolResult.Error("no delegate task running (a script? use script op=replace)")),
            tiers: ToolTiers.Main);
    }

    /// <summary>Runs one task to completion. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.</summary>
    /// <param name="profile">Named model profile the worker loop runs on (validated; null = the worker tier's model).</param>
    public async Task<WorkerOutcome> RunAsync(string task, string? doneWhen, int? maxMinutes, CancellationToken ct, string? profile = null)
    {
        if (profile != null && _llmOptions.CheckProfile(profile) is { } bad) return Done(new(false, "failed: " + bad, 0, 0, 0, "profile"));
        // Workers run on WorkerModel unless the profile explicitly says otherwise (an Opus profile alone never makes an Opus worker).
        var effective = _llmOptions.WorkerProfileFor(profile);
        if (profile != null && !string.Equals(effective, profile, StringComparison.OrdinalIgnoreCase))
            _log.LogInformation("delegate profile {Asked} → worker runs on {Effective}", profile, effective ?? _llmOptions.WorkerModel);
        profile = effective;
        var prof = profile != null ? _llmOptions.Profile(profile) : null;
        int minutes = Math.Clamp(maxMinutes ?? _o.DefaultMinutes, 1, Math.Max(1, _o.MaxMinutes));
        var mode = _usage.CheckMode();
        if (mode is BudgetMode.Paused or BudgetMode.CheapOnly)
            return Done(new(false, "failed: budget low, no worker tasks right now", 0, 0, 0, "budget"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(minutes), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = linked.Token;

        var tools = ToolDefinitions;
        var messages = new List<LlmMessage> { LlmMessage.User(FirstMessage(task, doneWhen, minutes)) };
        while (_amendments.TryDequeue(out _)) { } // notes for an earlier task never leak into this one
        LastLiveAmendment = null;
        Interlocked.Increment(ref _running);
        int calls = 0, toolCalls = 0, continued = 0;
        decimal cost = 0;
        string? summary = null, stop = "done";
        var log = new List<string>();
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        _trail = null;

        try
        {
            for (int round = 0; ; round++)
            {
                if (round >= _o.MaxRounds) { stop = "max rounds"; break; }
                if (cost >= _o.MaxCostUsd) { stop = "cost cap"; break; }
                var m = _usage.CheckMode();
                if (m is BudgetMode.Paused or BudgetMode.CheapOnly) { stop = "budget low"; break; }
                ApplyAmendments(messages);
                if (round > 0) await ApplyRoundNoteAsync(messages, token).ConfigureAwait(false);
                if (!FitContext(messages, tools)) { stop = "context full"; break; }

                var response = await CallAsync(messages, tools, token, profile, prof).ConfigureAwait(false);
                calls++;
                cost += _usage.Record(response.Model, response.Usage, ModelTier.Worker);
                _log.LogInformation("worker call {Round}: {Model} {Usage} cache {Hit:P0}", round, response.Model, response.Usage, UsageTracker.CacheHit(response.Usage));
                try { CallCompleted?.Invoke(); } catch (Exception) { /* telemetry only */ }

                var content = response.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
                if (content.Count == 0) content.Add(new LlmText("(silent)"));
                messages.Add(new LlmMessage(LlmRole.Assistant, content));

                var uses = response.ToolUses.ToList();
                if (uses.Count == 0 || response.StopReason == StopReasons.Refusal)
                {
                    summary = response.Text.Trim();
                    // needs_input with a recommended answer: don't park the bot. Carry on with the default; the brain asks.
                    if (Questions is { } qo && continued < MaxDefaultContinues && response.StopReason != StopReasons.Refusal &&
                        NeedsDefault.Match(ResultLine(summary)) is { Success: true } nd)
                    {
                        var question = nd.Groups["q"].Value.Trim();
                        var dflt = nd.Groups["d"].Value.Trim().TrimEnd('.');
                        bool risky = qo.IsRisky(question + " " + dflt);
                        if (risky) dflt = qo.SafeDefault;
                        continued++;
                        try { Asked?.Invoke(question, dflt); } catch (Exception) { }
                        AppendText(messages, $"[planner] go ahead with \"{dflt}\" for now" + (risky ? " (do the rest, not the risky part)" : "") +
                            "; an amend comes if the player wants otherwise.");
                        summary = null;
                        continue;
                    }
                    break;
                }

                var results = new List<LlmBlock>();
                foreach (var u in uses)
                {
                    ToolResult r;
                    if (!_tools.TryGet(u.Name, out var tool) || !_tools.Allows(tool, ModelTier.Worker))
                        r = ToolResult.Error($"no tool '{u.Name}' here");
                    else
                    {
                        // Sequential: the bot has one body. Each game tool honours the task token.
                        _trail = TrailOf(log, $"{u.Name} {TextUtil.Truncate(TextUtil.OneLine(u.Input.GetRawText()), 50)} (running)");
                        using (ToolCaller.Push(ModelTier.Worker, response.Model))
                            r = await _tools.ExecuteAsync(tool, u.Input, token).ConfigureAwait(false);
                        toolCalls++;
                    }
                    token.ThrowIfCancellationRequested();
                    log.Add($"{u.Name}{(r.Ok ? "" : "!")} {TextUtil.Truncate(TextUtil.OneLine(r.Text), 60)}");
                    _trail = TrailOf(log, null);
                    results.Add(new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok) { Image = r.Image });
                    // Protection rule 5: the first protected refusal ends the task and goes back as-is (why + unlock phrase),
                    // never retried a second way (live 10-27: two workers and a script hit the same protected log).
                    if (!r.Ok && summary == null && IsProtectionRefusal(r.Text))
                    {
                        stop = "repeated failure";
                        summary = $"failed: {u.Name}: {TextUtil.Truncate(TextUtil.OneLine(r.Text), 260)}";
                    }
                    if (!r.Ok && _o.MaxIdenticalFailures > 0 && !ToolCallGuard.IsTransientCombat(r.Text))
                    {
                        var key = FailureKey(u.Name, r.Text);
                        int n = failures[key] = failures.GetValueOrDefault(key) + 1;
                        if (n >= _o.MaxIdenticalFailures && summary == null)
                        {
                            stop = "repeated failure";
                            summary = $"failed: {u.Name} failed {n}x the same way: {TextUtil.Truncate(TextUtil.OneLine(r.Text), 160)}";
                        }
                    }
                }
                messages.Add(new LlmMessage(LlmRole.User, results));
                if (stop == "repeated failure") break;
            }

            // Out of rounds/cost/context: one wrap-up call (same prefix, so it's mostly cache) for an honest summary.
            if (summary == null && stop is "max rounds" or "cost cap" or "context full" && cost < _o.MaxCostUsd * 1.5m)
            {
                AppendText(messages, $"[{stop}] Stop now. Reply with the one-line summary (ok:/failed:/needs_input:), no tools.");
                if (FitContext(messages, tools))
                {
                    var response = await CallAsync(messages, tools, token, profile, prof).ConfigureAwait(false);
                    calls++;
                    cost += _usage.Record(response.Model, response.Usage, ModelTier.Worker);
                    if (!response.ToolUses.Any() && response.Text.Trim() is { Length: > 0 } t) summary = t;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            stop = "time";
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }

        summary ??= stop switch
        {
            "time" => $"failed: out of time ({minutes} min)",
            "budget low" => "failed: budget low, stopped",
            _ => $"failed: stopped ({stop})",
        } + (log.Count > 0 ? "; last: " + log[^1] : "") + $"; {toolCalls} actions";
        var outcome = Classify(summary, calls, toolCalls, cost, stop);
        _log.LogInformation("worker task \"{Task}\": {Summary} ({Calls} calls, {Tools} tools, ${Cost:0.0000}, {Stop})",
            TextUtil.Truncate(task, 60), outcome.Summary, calls, toolCalls, cost, stop);
        return Done(outcome);
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
    private bool FitContext(List<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools)
    {
        int fixedTokens = _system.Sum(s => TextUtil.EstimateTokens(s.Text)) + ToolTokens(tools) + _llmOptions.MaxTokensFor(ModelTier.Worker);
        int budget = _o.MaxContextTokens - fixedTokens;
        if (budget <= 0) return false;
        return ContextManager.Shrink(messages, budget) <= budget;
    }

    internal static int ToolTokens(IEnumerable<LlmToolDefinition> tools) =>
        tools.Sum(t => TextUtil.EstimateTokens(t.Name + t.Description + t.InputSchema.GetRawText()) + 6);

    private static void AppendText(List<LlmMessage> messages, string text)
    {
        var last = messages[^1];
        if (last.Role == LlmRole.User) messages[^1] = last with { Content = [.. last.Content, new LlmText(text)] };
        else messages.Add(LlmMessage.User(text));
    }
}
