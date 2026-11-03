using System.Globalization;
using System.Text;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;

namespace MineClaw.Bot.Blueprints;

/// <summary>A model the Design Studio can talk to: one of the bot's tiers, or a configured profile (Ollama/DeepSeek, …).</summary>
/// <param name="Id">"main" / "worker" / "cheap" (the tiers) or "profile:&lt;name&gt;".</param>
public sealed record DesignerModel(string Id, string Label, string Provider, string Model, ModelTier Tier, string? Profile, string? CostHint);

/// <summary>
/// The models the bot is configured to use (Agent:Llm): each tier's model and every usable profile, de-duplicated by
/// (provider, model). A Claude model outside the 5.5 generation is never offered (startup refuses such a config anyway).
/// </summary>
public static class DesignerModels
{
    public static List<DesignerModel> List(LlmOptions o)
    {
        var list = new List<DesignerModel>();
        void Add(string id, string label, string provider, string model, ModelTier tier, string? profile, string? hint)
        {
            provider = LlmProviders.Normalize(provider);
            if (string.IsNullOrWhiteSpace(model) || !o.ProviderConfigured(provider)) return;
            if (provider == LlmProviders.Anthropic && !ModelProfiles.IsCurrentGeneration(model)) return;
            if (list.Any(m => m.Provider == provider && m.Model.Equals(model, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new DesignerModel(id, label, provider, model.Trim(), tier, profile, hint));
        }
        Add("main", $"{Pretty(o.MainModel)} (brain)", o.ProviderFor(ModelTier.Main), o.MainModel, ModelTier.Main, null, "$$$");
        Add("worker", $"{Pretty(o.WorkerModel)} (worker)", o.ProviderFor(ModelTier.Worker), o.WorkerModel, ModelTier.Worker, null, "$$");
        Add("cheap", $"{Pretty(o.CheapModel)} (memory)", o.ProviderFor(ModelTier.Cheap), o.CheapModel, ModelTier.Cheap, null, "$");
        foreach (var name in o.UsableProfiles())
            if (o.Profile(name) is { } p)
                Add("profile:" + name, $"{Pretty(p.Model)} ({name})", p.Provider, p.Model, ModelTier.Main, name, p.CostHint);
        return list;
    }

    /// <summary>The default: the brain tier when usable, else the first.</summary>
    public static DesignerModel? Default(LlmOptions o) => List(o).FirstOrDefault();

    /// <summary>An id from <see cref="List"/>, or a model id one of them names; null = not available.</summary>
    public static DesignerModel? Resolve(LlmOptions o, string? id)
    {
        var all = List(o);
        if (string.IsNullOrWhiteSpace(id)) return all.FirstOrDefault();
        id = id.Trim();
        return all.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(m => m.Model.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5"; other providers' names stay as they are.</summary>
    public static string Pretty(string model)
    {
        var m = model.Trim();
        if (!m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return m;
        var parts = m["claude-".Length..].Split('-');
        if (parts.Length < 3) return m;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[0]) + " " + parts[1] + "." + parts[2];
    }
}

/// <summary>What one Design Studio message did: the reply, the ops applied (in order), cost and usage.</summary>
public sealed record DesignerTurnResult(string Reply, IReadOnlyList<DesignerOp> Ops, decimal CostUsd, LlmUsage Usage, string Model, int Calls, string? Refusal = null);

/// <summary>Limits of one Design Studio message ("Supervisor:Viewer:Designer").</summary>
public sealed class DesignerOptions
{
    /// <summary>Off = the <c>/api/designer/*</c> routes are 404 even with the viewer on.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Model calls per message (each tool round is one).</summary>
    public int MaxRounds { get; set; } = 8;
    public int MaxTokens { get; set; } = 4000;
    /// <summary>A message stops calling the model once it has cost this much (USD).</summary>
    public decimal MaxUsdPerMessage { get; set; } = 1.50m;
    /// <summary>Earlier exchanges sent as plain text (older ones are dropped: the canvas summary carries the state).</summary>
    public int HistoryTurns { get; set; } = 6;
    /// <summary>output_config.effort of the studio's calls ("" = the tier's).</summary>
    public string Effort { get; set; } = "low";
}

/// <summary>
/// One Design Studio message: the user's words + the canvas summary (+ the selection and their own edits since the last turn)
/// go to the chosen model with the canvas tools; tool calls are applied to the sandbox canvas until the model answers in text.
/// The prefix (tools + system) is cached; history is plain text of the last few exchanges. Spend is booked on the bot's
/// <see cref="UsageTracker"/> (the shared daily/monthly caps) and refused while the budget is paused.
/// </summary>
public sealed class DesignerAgent(ILlmClient llm, LlmOptions llmOptions, UsageTracker? usage, DesignerOptions? options = null)
{
    private readonly DesignerOptions _o = options ?? new DesignerOptions();
    private readonly PriceTable _prices = usage?.Prices ?? new PriceTable();

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

    public async Task<DesignerTurnResult> RunAsync(DesignerSessionRecord s, IReadOnlyList<DesignerMessageRecord> history, DesignerModel m, string text,
        (int[] Min, int[] Max)? selection, string? player, CancellationToken ct)
    {
        if (BudgetRefusal(m) is { } refusal) return new DesignerTurnResult("", [], 0, LlmUsage.Zero, m.Model, 0, refusal);
        var messages = new List<LlmMessage>();
        foreach (var h in history.TakeLast(Math.Max(0, _o.HistoryTurns) * 2))
            messages.Add(h.Role == "assistant" ? LlmMessage.Assistant(h.Text.Length == 0 ? "(done)" : h.Text) : LlmMessage.User(h.Text));
        // The API wants alternating turns starting with the user: drop a leading assistant line, merge none (we always add pairs).
        while (messages.Count > 0 && messages[0].Role != LlmRole.User) messages.RemoveAt(0);
        messages.Add(LlmMessage.User(Brief(s, text, selection)));
        var ops = new List<DesignerOp>();
        var total = LlmUsage.Zero;
        decimal cost = 0;
        int calls = 0;
        string reply = "", model = m.Model;
        for (int round = 0; round < Math.Max(1, _o.MaxRounds); round++)
        {
            ct.ThrowIfCancellationRequested();
            LlmResponse resp;
            try
            {
                resp = await llm.CompleteAsync(new LlmRequest
                {
                    Tier = m.Tier, Model = m.Model, Profile = m.Profile, MaxTokens = Math.Max(256, _o.MaxTokens), Purpose = "designer",
                    System = [new LlmSystemBlock(DesignerTools.System(player), Cache: true)], Tools = DesignerTools.All, Messages = messages.ToList(),
                    CacheHistory = true, Effort = string.IsNullOrWhiteSpace(_o.Effort) ? null : _o.Effort,
                }, ct).ConfigureAwait(false);
            }
            catch (LlmCostCapException cc)
            {
                cost += Book(cc.Model, cc.PartialUsage, m.Tier);
                total = total.Add(cc.PartialUsage);
                break;
            }
            calls++;
            model = resp.Model;
            total = total.Add(resp.Usage);
            cost += Book(resp.Model, resp.Usage, m.Tier);
            if (resp.Text.Trim() is { Length: > 0 } t) reply = t;
            var uses = resp.ToolUses.ToList();
            if (uses.Count == 0 || resp.StopReason != StopReasons.ToolUse) break;
            messages.Add(new LlmMessage(LlmRole.Assistant, resp.Content));
            var results = new List<LlmBlock>();
            foreach (var u in uses)
            {
                var (res, op, err) = DesignerTools.Run(s.Canvas, u.Name, u.Input);
                if (op != null) ops.Add(op);
                results.Add(new LlmToolResult(u.Id, res, err));
            }
            if (cost >= _o.MaxUsdPerMessage)
            {
                reply = reply.Length > 0 ? reply : $"I stopped at the per-message cost cap (${_o.MaxUsdPerMessage:0.00}); say \"go on\" to continue.";
                break;
            }
            messages.Add(new LlmMessage(LlmRole.User, results));
        }
        if (reply.Length == 0) reply = ops.Count > 0 ? "Done: " + string.Join("; ", ops.Select(o => o.Label).Distinct().Take(4)) + "." : "(no changes)";
        return new DesignerTurnResult(reply, ops, cost, total, model, calls);
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
        sb.Append("[canvas] ").Append(s.Canvas.Describe()).Append('\n');
        if (selection is { } sel && sel.Min.Length == 3 && sel.Max.Length == 3)
            sb.Append(CultureInfo.InvariantCulture, $"[selection] from [{sel.Min[0]},{sel.Min[1]},{sel.Min[2]}] to [{sel.Max[0]},{sel.Max[1]},{sel.Max[2]}]\n");
        if (s.PendingEdits.Length > 0) sb.Append("[user edits since your last turn] ").Append(s.PendingEdits).Append('\n');
        sb.Append(text.Trim());
        return sb.ToString();
    }
}
