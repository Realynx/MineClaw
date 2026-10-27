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
    private static readonly IReadOnlyList<LlmSystemBlock> System = [new LlmSystemBlock(Prompts.MemorySystem, Cache: true, Ttl: "5m")];

    public MemoryAgent(AskMemoryOptions options, ILlmClient llm, ToolRegistry tools, UsageTracker usage, ILogger? log = null)
    {
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
            if (round == rounds - 1 && messages[^1].Role == LlmRole.User && round > 0)
            {
                var last = messages[^1];
                messages[^1] = last with { Content = [.. last.Content, new LlmText("(last round: answer now, no more lookups)")] };
            }
            ContextManager.Shrink(messages, 8_000);
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
            var uses = response.ToolUses.ToList();
            if (uses.Count == 0)
            {
                answer = response.Text.Trim();
                break;
            }
            var results = new List<LlmBlock>();
            foreach (var u in uses.Take(4))
            {
                var tool = tools.FirstOrDefault(t => t.Name.Equals(u.Name, StringComparison.OrdinalIgnoreCase));
                var r = tool != null ? await _tools.ExecuteAsync(tool, u.Input, ct).ConfigureAwait(false) : ToolResult.Error($"no tool '{u.Name}'");
                if (r.Ok) raw.Add(TextUtil.OneLine(r.Text));
                results.Add(new LlmToolResult(u.Id, r.Text.Length == 0 ? "(empty)" : r.Text, !r.Ok));
            }
            foreach (var u in uses.Skip(4)) results.Add(new LlmToolResult(u.Id, "skipped (max 4 lookups per round)", true));
            messages.Add(new LlmMessage(LlmRole.User, results));
        }

        if (string.IsNullOrWhiteSpace(answer))
            answer = raw.Count > 0 ? "inconclusive; raw: " + raw[^1] : "not in memory";
        answer = TextUtil.Truncate(TextUtil.OneLine(answer), _o.AnswerChars);
        _log.LogInformation("ask_memory \"{Q}\" → {A}", TextUtil.Truncate(question, 60), answer);
        return ToolResult.Success(answer);
    }

    private List<IAgentTool> Lookups()
    {
        var list = new List<IAgentTool>();
        foreach (var n in _o.Tools)
            if (_tools.TryGet(n, out var t)) list.Add(t);
        return list;
    }
}
