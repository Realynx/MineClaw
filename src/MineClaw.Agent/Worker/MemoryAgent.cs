using MineClaw.Agent.Budget;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Worker;

/// <summary>
/// <c>ask_memory</c>: the brain asks one question; the cheap ("memory") tier runs a tiny retrieval loop over the RAG index
/// (recall / where_is / recall_detail, several queries if needed) and returns a distilled answer of at most
/// <see cref="AskMemoryOptions.AnswerChars"/> citing record ids. The lookups never enter the brain's context — only the answer.
/// </summary>
public sealed class MemoryAgent
{
    public const string ToolName = "ask_memory";

    private readonly AskMemoryOptions _o;
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly UsageTracker _usage;
    private readonly ILogger _log;
    private readonly IReadOnlyList<LlmSystemBlock> System;

    public MemoryAgent(AskMemoryOptions options, ILlmClient llm, ToolRegistry tools, UsageTracker usage, ILogger? log = null,
        TerseLevel terse = TerseLevel.Off)
    {
        System = [new LlmSystemBlock(Prompts.MemorySystemFor(terse), Cache: true, Ttl: "5m")];
        _o = options;
        _llm = llm;
        _tools = tools;
        _usage = usage;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Registers <c>ask_memory</c> (brain-only, instant).</summary>
    public void Register()
    {
        _tools.Register(ToolName,
            "Ask your memory a question (places, builds, chest contents, players, past events, routines, how-tos). " +
            "Returns a short answer with #ids.",
            ToolSchema.Create().Str("question").Build(),
            async (input, ct) => await AskAsync(input.ReqStr("question"), ct).ConfigureAwait(false),
            tiers: ToolTiers.Main, maxResultChars: _o.AnswerChars + 20);
    }

    public IReadOnlyList<LlmSystemBlock> SystemPrompt => System;

    public async Task<ToolResult> AskAsync(string question, CancellationToken ct)
    {
        if (_usage.CheckMode() == BudgetMode.Paused) return ToolResult.Error("memory is paused (budget)");
        var tools = Lookups();
        if (tools.Count == 0) return ToolResult.Error("no memory index");
        var defs = tools.Select(t => new LlmToolDefinition(t.Name, t.Description, t.InputSchema)).ToList();
        var messages = new List<LlmMessage> { LlmMessage.User("question: " + question.Trim()) };
        string? answer = null;
        var raw = new List<string>();
        int rounds = Math.Max(1, _o.MaxRounds);

        for (int round = 0; round < rounds; round++)
        {
            if (round == rounds - 1 && round > 0) NoteLastRound(messages);
            ContextManager.Shrink(messages, 8_000);
            var response = await RoundAsync(defs, messages, ct).ConfigureAwait(false);
            var uses = response.ToolUses.ToList();
            if (uses.Count == 0)
            {
                answer = response.Text.Trim();
                break;
            }
            messages.Add(new LlmMessage(LlmRole.User, await LookupsAsync(tools, uses, response.Model, raw, ct).ConfigureAwait(false)));
        }

        answer = FinalAnswer(answer, raw);
        _log.LogInformation("ask_memory \"{Q}\" → {A}", TextUtil.Truncate(question, 60), answer);
        return ToolResult.Success(answer);
    }

    /// <summary>The last round's question (a user message) says to answer now.</summary>
    private static void NoteLastRound(List<LlmMessage> messages)
    {
        if (messages[^1].Role != LlmRole.User) return;
        var last = messages[^1];
        messages[^1] = last with { Content = [.. last.Content, new LlmText("(last round: answer now, no more lookups)")] };
    }

    /// <summary>One memory-tier call; its reply (empty text blocks dropped, "(silent)" when nothing is left) joins the conversation.</summary>
    private async Task<LlmResponse> RoundAsync(List<LlmToolDefinition> defs, List<LlmMessage> messages, CancellationToken ct)
    {
        var response = await _llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Cheap,
            System = System,
            Tools = defs,
            Messages = messages.ToArray(),
            MaxTokens = _o.MaxTokens,
            Purpose = "ask_memory",
        }, ct).ConfigureAwait(false);
        _usage.Record(response.Model, response.Usage, ModelTier.Cheap);

        var content = response.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
        if (content.Count == 0) content.Add(new LlmText("(silent)"));
        messages.Add(new LlmMessage(LlmRole.Assistant, content));
        return response;
    }

    /// <summary>Runs up to 4 lookups of the round (the rest are skipped); successful results also feed the raw fallback.</summary>
    private async Task<List<LlmBlock>> LookupsAsync(List<IAgentTool> tools, List<LlmToolUse> uses, string model, List<string> raw, CancellationToken ct)
    {
        var results = new List<LlmBlock>();
        foreach (var u in uses.Take(4)) results.Add(await LookupAsync(tools, u, model, raw, ct).ConfigureAwait(false));
        foreach (var u in uses.Skip(4)) results.Add(new LlmToolResult(u.Id, "skipped (max 4 lookups per round)", true));
        return results;
    }

    private async Task<LlmBlock> LookupAsync(List<IAgentTool> tools, LlmToolUse u, string model, List<string> raw, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => t.Name.Equals(u.Name, StringComparison.OrdinalIgnoreCase));
        ToolResult r;
        using (ToolCaller.Push(ModelTier.Cheap, model))
            r = tool != null ? await _tools.ExecuteAsync(tool, u.Input, ct).ConfigureAwait(false) : ToolResult.Error($"no tool '{u.Name}'");
        if (r.Ok) raw.Add(TextUtil.OneLine(r.Text));
        return new LlmToolResult(u.Id, r.Text.Length == 0 ? "(empty)" : r.Text, !r.Ok);
    }

    /// <summary>The answer in one line (capped); none: the last raw lookup result as inconclusive, else "not in memory".</summary>
    private string FinalAnswer(string? answer, List<string> raw)
    {
        if (string.IsNullOrWhiteSpace(answer))
            answer = raw.Count > 0 ? "inconclusive; raw: " + raw[^1] : "not in memory";
        return TextUtil.Truncate(TextUtil.OneLine(answer), _o.AnswerChars);
    }

    private List<IAgentTool> Lookups()
    {
        var list = new List<IAgentTool>();
        foreach (var n in _o.Tools)
            if (_tools.TryGet(n, out var t)) list.Add(t);
        return list;
    }
}
