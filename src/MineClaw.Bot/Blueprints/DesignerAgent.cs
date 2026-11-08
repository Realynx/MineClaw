using System.Globalization;
using System.Text;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// One Design Studio message: the user's words + the canvas summary (+ the selection and their own edits since the last turn)
/// go to the chosen model with the canvas tools; tool calls are applied to the sandbox canvas until the model answers in text.
/// The prefix (tools + system) is cached; history is plain text of the last few exchanges (<see cref="DesignerConversation"/>).
/// Spend is booked on the bot's <see cref="UsageTracker"/> (the shared daily/monthly caps) and refused while the budget is paused.
/// <para>A message never loses work: before each call the request is fitted to the context budget (older tool results cut);
/// a reply cut at the output limit runs its complete calls and tells the model to resend the cut one smaller; a model stuck on
/// failing calls, the round or cost limit, and a call failing after the first all end the message with the edits made so far
/// and say why (<see cref="DesignerTurnResult.Stopped"/>). Only a first call that fails throws (nothing ran).</para>
/// </summary>
public sealed class DesignerAgent(ILlmClient llm, LlmOptions llmOptions, UsageTracker? usage, DesignerOptions? options = null)
{
    private readonly DesignerOptions _o = options ?? new DesignerOptions();
    private readonly PriceTable _prices = usage?.Prices ?? new PriceTable();

    /// <summary>Why a model call failed, in a line ("the model call failed: …").</summary>
    public static string FailureReason(Exception e) => "the model call failed: " + DesignerText.Clip(e.Message, 160);

    /// <summary>The transcript line a failed model call leaves when nothing ran (the route stores it).</summary>
    public static string FailureText(Exception e) => "(" + FailureReason(e) + ")";

    /// <summary>Null = allowed; else why the budget refuses this model now.</summary>
    public string? BudgetRefusal(DesignerModel m)
    {
        if (usage == null) return null;
        var mode = usage.CheckMode();
        if (mode == BudgetMode.Paused) return "the bot's model budget is paused (" + usage.Reason + ")";
        if (mode == BudgetMode.CheapOnly && m.Tier != ModelTier.Cheap && m.Provider == LlmProviders.Anthropic)
            return "the bot's budget is on the cheap tier only; pick the memory model";
        return null;
    }

    /// <summary>
    /// The context budget of a call on <paramref name="m"/>: <see cref="DesignerOptions.MaxContextTokens"/>, lowered to an Ollama
    /// model's <c>num_ctx</c> (Ollama silently cuts a longer prompt's head: the system prompt and tools would go first).
    /// </summary>
    public int ContextBudget(DesignerModel m)
    {
        int budget = Math.Max(1024, _o.MaxContextTokens);
        int numCtx = llmOptions.Ollama.DefaultNumCtx;
        return m.Provider == LlmProviders.Ollama && numCtx > 0 ? Math.Min(budget, numCtx) : budget;
    }

    public async Task<DesignerTurnResult> RunAsync(DesignerSessionRecord s, IReadOnlyList<DesignerMessageRecord> history, DesignerModel m, string text,
        (int[] Min, int[] Max)? selection, string? player, CancellationToken ct)
    {
        if (BudgetRefusal(m) is { } refusal) return new DesignerTurnResult("", [], 0, LlmUsage.Zero, m.Model, 0, refusal);
        // The session's version for the whole turn (names, rules, the summary): it flows into every await.
        using var scope = DesignVersions.Use(s.Canvas.Version);
        // A mine canvas gets its own compact prompt and the mine templates (only then: a build turn costs nothing more).
        var system = s.Canvas.Mine ? DesignerTools.MineSystem(player, s.Canvas) : DesignerTools.System(player, s.Canvas.Version);
        var call = new Call(m, [new LlmSystemBlock(system, Cache: true)], DesignerTools.For(s.Canvas),
            DesignerConversation.Build(history, _o.HistoryTurns, Brief(s, text, selection)));
        var turn = new DesignerTurn(m.Model, _o.MaxFailedRounds);
        int rounds = Math.Max(1, _o.MaxRounds);
        bool more = true;
        for (int round = 0; round < rounds && more; round++)
            more = await RoundAsync(s, call, turn, ct).ConfigureAwait(false);
        if (more) turn.Stop($"I used all {rounds} steps of this message; say \"go on\" to continue");
        return turn.Result();
    }

    /// <summary>What every call of one turn sends: the model, the cached prefix and the growing messages.</summary>
    private sealed record Call(DesignerModel Model, IReadOnlyList<LlmSystemBlock> System, IReadOnlyList<LlmToolDefinition> Tools, List<LlmMessage> Messages);

    /// <summary>One model call and its tool calls run on the canvas; false when the turn ends (the model answered, or a limit).</summary>
    private async Task<bool> RoundAsync(DesignerSessionRecord s, Call c, DesignerTurn turn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int maxOut = Math.Max(256, _o.MaxTokens);
        if (!ContextManager.FitMessages(c.Messages, c.System, c.Tools, maxOut, ContextBudget(c.Model)))
        {
            turn.Stop($"this conversation no longer fits the model's context ({ContextBudget(c.Model)} tokens); " +
                      "start a new session or raise Supervisor:Viewer:Designer:MaxContextTokens");
            return false;
        }
        if (await CallAsync(c, maxOut, turn, ct).ConfigureAwait(false) is not { } resp) return false;
        var uses = resp.ToolUses.ToList();
        if (resp.StopReason == StopReasons.Refusal)
        {
            turn.Stop("the model declined this request");
            return false;
        }
        if (uses.Count == 0)
        {
            if (resp.StopReason == StopReasons.MaxTokens) turn.Stop("the reply was cut off at the output limit");
            return false;
        }
        var content = resp.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
        c.Messages.Add(new LlmMessage(LlmRole.Assistant, content));
        var results = RunTools(s, uses, CutOff(resp, content), turn.Ops, maxOut);
        c.Messages.Add(new LlmMessage(LlmRole.User, results));
        if (turn.Stuck(results)) return false;
        if (turn.Cost < _o.MaxUsdPerMessage) return true;
        turn.Stop(string.Create(CultureInfo.InvariantCulture, $"I stopped at the per-message cost cap (${_o.MaxUsdPerMessage:0.00}); say \"go on\" to continue"));
        return false;
    }

    /// <summary>
    /// The call, booked on the turn; null when the turn ends without a reply: the cost cap aborted it, or a call after the first
    /// failed (the edits so far stand). A failing first call throws: nothing ran, so the route answers it as a failure.
    /// </summary>
    private async Task<LlmResponse?> CallAsync(Call c, int maxOut, DesignerTurn turn, CancellationToken ct)
    {
        try
        {
            var resp = await llm.CompleteAsync(Request(c, maxOut), ct).ConfigureAwait(false);
            turn.Record(resp, Book(resp.Model, resp.Usage, c.Model.Tier));
            return resp;
        }
        catch (LlmCostCapException cc)
        {
            turn.Spend(cc.PartialUsage, Book(cc.Model, cc.PartialUsage, c.Model.Tier));
            turn.Stop("the model call hit its cost cap");
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException && turn.Calls > 0)
        {
            turn.Stop(FailureReason(e));
            return null;
        }
    }

    private LlmRequest Request(Call c, int maxOut) => new()
    {
        Tier = c.Model.Tier, Model = c.Model.Model, Profile = c.Model.Profile, MaxTokens = maxOut, Purpose = "designer",
        System = c.System, Tools = c.Tools, Messages = c.Messages.ToList(),
        CacheHistory = true, Effort = string.IsNullOrWhiteSpace(_o.Effort) ? null : _o.Effort,
    };

    /// <summary>The id of the call a reply cut at the output limit ended inside (its input is incomplete), else null.</summary>
    private static string? CutOff(LlmResponse resp, List<LlmBlock> content) =>
        resp.StopReason == StopReasons.MaxTokens && content.Count > 0 && content[^1] is LlmToolUse last ? last.Id : null;

    /// <summary>Runs the model's tool calls on the canvas (each change an op): the tool results, in order. The call cut at the
    /// output limit isn't run: the model is told to resend it smaller.</summary>
    private static List<LlmToolResult> RunTools(DesignerSessionRecord s, List<LlmToolUse> uses, string? cutId, List<DesignerOp> ops, int maxOut)
    {
        var results = new List<LlmToolResult>();
        foreach (var u in uses)
        {
            if (u.Id == cutId)
            {
                results.Add(new LlmToolResult(u.Id, $"error: not run: your reply hit the output limit ({maxOut} tokens) inside this call. " +
                    "Send it again in smaller pieces: box, line or replace for runs of blocks, fewer cells per place, fewer calls per reply.", true));
                continue;
            }
            var (res, op, err) = DesignerTools.Run(s.Canvas, u.Name, u.Input);
            if (op != null) ops.Add(op);
            results.Add(new LlmToolResult(u.Id, res, err));
        }
        return results;
    }

    private decimal Book(string model, LlmUsage u, ModelTier tier)
    {
        try
        {
            if (usage == null) return _prices.Cost(model, u);
            decimal usd = usage.Record(model, u, tier);
            usage.AddRollup("designer", usd, u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens, u.OutputTokens, 0, null);
            return usd;
        }
        catch (Exception) { return _prices.Cost(model, u); }
    }

    /// <summary>The user's turn: the canvas state, the selection, their own edits since the AI's last turn, then their words.</summary>
    public static string Brief(DesignerSessionRecord s, string text, (int[] Min, int[] Max)? selection)
    {
        var sb = new StringBuilder();
        using (DesignVersions.Use(s.Canvas.Version)) sb.Append("[canvas] ").Append(s.Canvas.Describe()).Append('\n');
        if (selection is { } sel && sel.Min.Length == 3 && sel.Max.Length == 3)
            sb.Append(CultureInfo.InvariantCulture, $"[selection] from [{sel.Min[0]},{sel.Min[1]},{sel.Min[2]}] to [{sel.Max[0]},{sel.Max[1]},{sel.Max[2]}]\n");
        if (s.PendingEdits.Length > 0) sb.Append("[user edits since your last turn] ").Append(s.PendingEdits).Append('\n');
        sb.Append(text.Trim());
        return sb.ToString();
    }
}
