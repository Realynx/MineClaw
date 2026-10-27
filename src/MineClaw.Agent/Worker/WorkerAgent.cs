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

    /// <summary>One-line game state for the task's first message (the bot agent's state header).</summary>
    public Func<string>? StateHeader { get; set; }

    /// <summary>Raised after every finished task (logs, tests).</summary>
    public event Action<WorkerOutcome>? Finished;

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

    /// <summary>The worker's tool definitions (its cached prefix, stable for the run).</summary>
    public IReadOnlyList<LlmToolDefinition> ToolDefinitions => _tools.Definitions(ModelTier.Worker);

    /// <summary>Registers <c>delegate</c> (brain-only, long-running: it runs as the bot's activity).</summary>
    public void Register()
    {
        _tools.Register(ToolName,
            "Hand a multi-step game task (mine, craft, smelt, build, chests, farm...) to your worker; it runs in the " +
            "background and reports ok/failed/needs_input.",
            ToolSchema.Create().Str("task", "concrete goal with items/places").Str("done_when", "checkable finish condition")
                .Int("max_minutes", $"default {_o.DefaultMinutes}, max {_o.MaxMinutes}", required: false).Build(),
            async (input, ct) =>
            {
                var o = await RunAsync(input.ReqStr("task"), input.Str("done_when"), input.Int("max_minutes"), ct).ConfigureAwait(false);
                return o.Ok ? ToolResult.Success(o.Summary) : ToolResult.Error(o.Summary);
            },
            longRunning: true, tiers: ToolTiers.Main, maxResultChars: _o.SummaryChars + 20);
    }

    /// <summary>Runs one task to completion. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.</summary>
    public async Task<WorkerOutcome> RunAsync(string task, string? doneWhen, int? maxMinutes, CancellationToken ct)
    {
        int minutes = Math.Clamp(maxMinutes ?? _o.DefaultMinutes, 1, Math.Max(1, _o.MaxMinutes));
        var mode = _usage.CheckMode();
        if (mode is BudgetMode.Paused or BudgetMode.CheapOnly)
            return Done(new(false, "failed: budget low, no worker tasks right now", 0, 0, 0, "budget"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(minutes), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = linked.Token;

        var tools = ToolDefinitions;
        var messages = new List<LlmMessage> { LlmMessage.User(FirstMessage(task, doneWhen, minutes)) };
        int calls = 0, toolCalls = 0;
        decimal cost = 0;
        string? summary = null, stop = "done";
        var log = new List<string>();

        try
        {
            for (int round = 0; ; round++)
            {
                if (round >= _o.MaxRounds) { stop = "max rounds"; break; }
                if (cost >= _o.MaxCostUsd) { stop = "cost cap"; break; }
                var m = _usage.CheckMode();
                if (m is BudgetMode.Paused or BudgetMode.CheapOnly) { stop = "budget low"; break; }
                if (!FitContext(messages, tools)) { stop = "context full"; break; }

                var response = await CallAsync(messages, tools, token).ConfigureAwait(false);
                calls++;
                cost += _usage.Record(response.Model, response.Usage, ModelTier.Worker);
                _log.LogInformation("worker call {Round}: {Model} {Usage} cache {Hit:P0}", round, response.Model, response.Usage, UsageTracker.CacheHit(response.Usage));

                var content = response.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
                if (content.Count == 0) content.Add(new LlmText("(silent)"));
                messages.Add(new LlmMessage(LlmRole.Assistant, content));

                var uses = response.ToolUses.ToList();
                if (uses.Count == 0 || response.StopReason == StopReasons.Refusal)
                {
                    summary = response.Text.Trim();
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
                        r = await _tools.ExecuteAsync(tool, u.Input, token).ConfigureAwait(false);
                        toolCalls++;
                    }
                    token.ThrowIfCancellationRequested();
                    log.Add($"{u.Name}{(r.Ok ? "" : "!")} {TextUtil.Truncate(TextUtil.OneLine(r.Text), 60)}");
                    results.Add(new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok));
                }
                messages.Add(new LlmMessage(LlmRole.User, results));
            }

            // Out of rounds/cost/context: one wrap-up call (same prefix, so it's mostly cache) for an honest summary.
            if (summary == null && stop is "max rounds" or "cost cap" or "context full" && cost < _o.MaxCostUsd * 1.5m)
            {
                AppendText(messages, $"[{stop}] Stop now. Reply with the one-line summary (ok:/failed:/needs_input:), no tools.");
                if (FitContext(messages, tools))
                {
                    var response = await CallAsync(messages, tools, token).ConfigureAwait(false);
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

    private Task<LlmResponse> CallAsync(List<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, CancellationToken ct) =>
        _llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Worker,
            System = _system,
            Tools = tools,
            Messages = messages.ToArray(),
            Purpose = "worker",
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
