using System.Diagnostics;
using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Authoring;

/// <summary>
/// The patch turn's answer: the js and what the script does after (resume | restart_step | stop | escalate), a short note, the
/// call's usage, cost, model and latency. <see cref="Then"/> "escalate" (or no tool call at all) = the full brain turn.
/// </summary>
public sealed record PatchReply(string? Js, string Then, string? Note, LlmUsage Usage, decimal Usd, string Model, long Ms, string? Blocker = null)
{
    public bool Escalates => Then == "escalate" || Blocker != null;
}

/// <summary>
/// One small, fast Opus 5.5 call that fixes a paused script inline instead of a full brain turn: a tiny cached system prompt (rules
/// + one-screen SDK cheat-sheet + this bot's game functions), the per-interrupt context as the only uncached text, ONE tool
/// (<c>patch{js, then, note?}</c>), low effort and ≈ 600 output tokens. Only Opus 5.5 on Anthropic writes JS (the same rule as
/// <see cref="AuthorAgent"/>): a non-Opus brain profile, another provider or a paused budget refuses, and saved handlers still run.
/// The patch tool lives only in this request, never in the brain's tool list.
/// </summary>
public sealed class PatchAgent(ILlmClient llm, LlmOptions options, UsageTracker? usage = null, ILogger? log = null)
{
    public const string Purpose = "patch";

    public const string ToolName = "patch";

    /// <summary>The patch tool: one call, js + then (+ a short note). Only in the patch request, never in the brain's tool list.</summary>
    public static readonly LlmToolDefinition Tool = new(ToolName,
        "Fix the paused script inline. js runs inside it (scope: ev, P, m, bb, carry, SDK/game functions), then the script does `then`. " +
        "then=escalate (js empty) when it needs the full brain.",
        JsonDocument.Parse("""
            {"type":"object","properties":{"js":{"type":"string","description":"a few lines of JS (no fences)"},"then":{"type":"string","enum":["resume","restart_step","stop","escalate"]},"note":{"type":"string","description":"≤12 words: what you did / why"}},"required":["then"]}
            """).RootElement.Clone());

    /// <summary>"resume" | "restart_step" | "stop" | "escalate" (unknown → escalate; empty → resume).</summary>
    public static string NormalizeThen(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('-', '_') switch
    {
        "" or "resume" or "continue" or "ok" => "resume",
        "restart_step" or "restart" or "retry" or "redo" => "restart_step",
        "stop" or "cancel" or "end" => "stop",
        _ => "escalate",
    };

    /// <summary>The model patches run on: the brain's model when it is Opus 5.5, else Opus 5.5 itself.</summary>
    public string Model => LlmOptions.IsOpus55(options.MainModel) ? options.MainModel : LlmOptions.ScriptAuthorModel;

    /// <summary>Null when a patch turn may run; else why not (only the Opus brain authors JS).</summary>
    public string? Refusal()
    {
        if (!options.BrainAuthorsScripts) return "only the Opus brain writes patches (this brain runs a non-Opus profile); saved handlers still run";
        if (!LlmOptions.IsOpus55(Model)) return $"only {LlmOptions.ScriptAuthorModel} writes patches";
        if (options.ProviderOfModel(Model) != LlmProviders.Anthropic) return $"only {LlmOptions.ScriptAuthorModel} on Anthropic writes patches";
        if (usage?.Mode is BudgetMode.Paused or BudgetMode.CheapOnly) return "budget low";
        return null;
    }

    public async Task<PatchReply> RunAsync(string system, string context, CancellationToken ct)
    {
        var model = Model;
        if (Refusal() is { } no) return new PatchReply(null, "escalate", null, LlmUsage.Zero, 0, model, 0, no);
        var sw = Stopwatch.StartNew();
        var resp = await llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Main, Model = model, MaxTokens = Math.Clamp(options.PatchMaxTokens <= 0 ? 600 : options.PatchMaxTokens, 256, 4096),
            Purpose = Purpose, System = [new LlmSystemBlock(system, Cache: true)], Tools = [Tool],
            Messages = [LlmMessage.User(context)], CacheHistory = false,
            Effort = string.IsNullOrWhiteSpace(options.PatchEffort) ? null : options.PatchEffort.Trim(),
        }, ct).ConfigureAwait(false);
        long ms = sw.ElapsedMilliseconds;
        decimal usd;
        try { usd = usage?.Record(resp.Model, resp.Usage, ModelTier.Main) ?? 0; } catch (Exception) { usd = 0; }
        var call = resp.ToolUses.FirstOrDefault(t => t.Name == ToolName);
        if (call == null)
        {
            var text = resp.Text.Trim();
            log?.LogInformation("patch turn: no tool call ({Stop}) in {Ms} ms: {Text}", resp.StopReason, ms, text.Length > 80 ? text[..80] : text);
            return new PatchReply(null, "escalate", text.Length > 0 ? text : "no patch", resp.Usage, usd, resp.Model, ms);
        }
        string? Str(string key) => call.Input.ValueKind == JsonValueKind.Object && call.Input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var js = StripFences(Str("js"));
        var then = NormalizeThen(Str("then") ?? (js is { Length: > 0 } ? "resume" : "escalate"));
        var note = Str("note");
        log?.LogInformation("patch turn: {Then} ({Chars} chars js) in {Ms} ms, out {Out} tok, ${Usd:0.000}: {Note}", then, js?.Length ?? 0, ms,
            resp.Usage.OutputTokens, usd, note ?? "");
        return new PatchReply(js, then, note, resp.Usage, usd, resp.Model, ms);
    }

    /// <summary>A model that wrapped its js in ``` anyway.</summary>
    internal static string? StripFences(string? js)
    {
        if (js == null) return null;
        var t = js.Trim();
        return t.StartsWith("```", StringComparison.Ordinal) ? AuthorAgent.FencedBlock(t) ?? t.Trim('`') : t;
    }
}
