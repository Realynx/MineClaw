using System.Text.RegularExpressions;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Authoring;

/// <summary>The checker's verdict on one draft. <see cref="Feedback"/> goes back to the AUTHOR only (never to the brain).</summary>
public sealed record AuthorCheck(bool Ok, string Feedback = "")
{
    public static AuthorCheck Pass { get; } = new(true);
    public static AuthorCheck Fail(string feedback) => new(false, feedback);
}

/// <summary>One authoring job: what to write, the author's reference prompt, and how a draft is checked.</summary>
public sealed class AuthorJob
{
    /// <summary>"script" | "blueprint" | "plan" (telemetry purpose "author-&lt;kind&gt;").</summary>
    public required string Kind { get; init; }
    /// <summary>The author's cached reference (Lua cheat-sheet, DSL...). Its own prefix: never the brain's.</summary>
    public required string System { get; init; }
    /// <summary>The spec the brain gave plus the facts the caller pulled (world, memory, saved artifacts).</summary>
    public required string Brief { get; init; }
    /// <summary>Lint/test of one extracted draft. A failure's feedback becomes the next author turn.</summary>
    public required Func<string, AuthorCheck> Check { get; init; }
    /// <summary>The artifact inside a reply (default: the first fenced block, else null).</summary>
    public Func<string, string?> Extract { get; init; } = AuthorAgent.FencedBlock;
}

/// <summary>
/// The finished job. <see cref="Artifact"/> is the full text for the caller to SAVE; only the caller's compact summary of it
/// reaches the brain. Drafts, thinking and fixed errors never leave the author context.
/// </summary>
public sealed record AuthorOutcome(bool Ok, string? Artifact, int Versions, LlmUsage Usage, decimal Usd, string Model, string? Blocker)
{
    public string Cost => $"${Usd:0.00}";
}

/// <summary>
/// The Opus 5.5 author: a separate context with its own reference prompt, up to <see cref="LlmOptions.AuthorMaxTokens"/> (100K)
/// output per call (streamed, cost-capped by <see cref="LlmOptions.AuthorMaxUsd"/> per job), and a write → check → revise loop
/// of at most <see cref="LlmOptions.AuthorMaxVersions"/>. The brain gives a short spec and gets ONLY the final artifact's facts
/// back from the caller; its own context limit and compaction are untouched. Only Opus 5.5 on Anthropic authors.
/// </summary>
public sealed class AuthorAgent(ILlmClient llm, LlmOptions options, UsageTracker? usage = null, ILogger? log = null)
{
    /// <summary>The model the author runs on: the brain's model when it is Opus 5.5, else Opus 5.5 itself.</summary>
    public string Model => LlmOptions.IsOpus55(options.MainModel) ? options.MainModel : LlmOptions.ScriptAuthorModel;

    /// <summary>Null when authoring may run; else why not (non-Opus brain profile, provider, paused budget).</summary>
    public string? Refusal()
    {
        if (!options.BrainAuthorsScripts) return "only the Opus brain authors (this brain runs a non-Opus profile); saved ones still run";
        var model = Model;
        if (!LlmOptions.IsOpus55(model)) return $"only {LlmOptions.ScriptAuthorModel} authors";
        if (options.ProviderOfModel(model) != LlmProviders.Anthropic) return $"only {LlmOptions.ScriptAuthorModel} on Anthropic authors";
        if (usage?.Mode == BudgetMode.Paused) return "budget paused";
        return null;
    }

    public async Task<AuthorOutcome> RunAsync(AuthorJob job, CancellationToken ct)
    {
        var model = Model;
        if (Refusal() is { } no) return new AuthorOutcome(false, null, 0, LlmUsage.Zero, 0, model, no);
        decimal cap = options.AuthorMaxUsd > 0 ? options.AuthorMaxUsd : 3.00m;
        var prices = usage?.Prices ?? new PriceTable();
        var conversation = new List<LlmMessage> { LlmMessage.User(job.Brief) };
        var total = LlmUsage.Zero;
        decimal spent = 0;
        string? blocker = null;
        int versions = 0, maxVersions = Math.Max(1, options.AuthorMaxVersions);
        for (int i = 0; i < maxVersions; i++)
        {
            LlmResponse resp;
            try
            {
                resp = await llm.CompleteAsync(new LlmRequest
                {
                    Tier = ModelTier.Main, Model = model, MaxTokens = options.AuthorMaxTokensClamped, Purpose = "author-" + job.Kind,
                    System = [new LlmSystemBlock(job.System, Cache: true)], Messages = conversation, CacheHistory = true,
                    Effort = string.IsNullOrWhiteSpace(options.AuthorEffort) ? null : options.AuthorEffort.Trim(),
                    MaxUsd = cap - spent, CostOf = u => prices.Cost(model, u),
                }, ct).ConfigureAwait(false);
            }
            catch (LlmCostCapException cc)
            {
                total = total.Add(cc.PartialUsage);
                spent += Book(cc.Model, cc.PartialUsage, prices);
                log?.LogWarning("author {Kind}: call aborted at ${Usd:0.00} (job cap ${Cap:0.00}, {Out} output tokens)", job.Kind, spent, cap, cc.PartialUsage.OutputTokens);
                blocker = $"cost cap ${cap:0.00} reached";
                break;
            }
            catch (LlmInterruptedException ie) when (ie.PartialUsage is { } pu)
            {
                total = total.Add(pu);
                spent += Book(ie.Model, pu, prices);
                throw;
            }
            versions++;
            total = total.Add(resp.Usage);
            spent += Book(resp.Model, resp.Usage, prices);

            var text = resp.Text;
            var artifact = job.Extract(text);
            AuthorCheck check = artifact == null
                ? AuthorCheck.Fail(resp.StopReason == StopReasons.MaxTokens
                    ? "Your reply hit the output limit before the artifact. Answer at once with ONLY the artifact in one ``` block."
                    : "Reply with ONLY the artifact in one ``` block.")
                : SafeCheck(job, artifact);
            if (check.Ok)
            {
                log?.LogInformation("author {Kind}: ok after {Versions} version(s), {Out} output tokens, ${Usd:0.00}", job.Kind, versions, total.OutputTokens, spent);
                return new AuthorOutcome(true, artifact, versions, total, spent, model, null);
            }
            blocker = artifact == null ? (resp.StopReason == StopReasons.MaxTokens ? "cut off at the output limit" : "no artifact in the reply")
                : "checks still failing: " + FirstLine(check.Feedback);
            if (spent >= cap) { blocker = $"cost cap ${cap:0.00} reached"; break; }
            // The draft and its errors stay in THIS context only (the brain never sees them).
            if (text.Length > 0) conversation.Add(LlmMessage.Assistant(text));
            else conversation.Add(LlmMessage.Assistant("(no reply)"));
            conversation.Add(LlmMessage.User(check.Feedback + "\nAnswer with the WHOLE corrected artifact in one ``` block, nothing else."));
        }
        log?.LogInformation("author {Kind}: gave up after {Versions} version(s): {Blocker} (${Usd:0.00})", job.Kind, versions, blocker, spent);
        return new AuthorOutcome(false, null, versions, total, spent, model, blocker ?? "no passing version");
    }

    private static AuthorCheck SafeCheck(AuthorJob job, string artifact)
    {
        try { return job.Check(artifact); }
        catch (Exception ex) { return AuthorCheck.Fail("check failed: " + ex.Message); }
    }

    private decimal Book(string model, LlmUsage u, PriceTable prices)
    {
        try { return usage?.Record(model, u, ModelTier.Main) ?? prices.Cost(model, u); }
        catch (Exception) { return prices.Cost(model, u); }
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    private static readonly Regex Fence = new(@"```[A-Za-z0-9_-]*[ \t]*\r?\n(.*?)\r?\n?```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The first ``` fenced block's body (language tag dropped), or null.</summary>
    public static string? FencedBlock(string text)
    {
        var m = Fence.Match(text ?? "");
        if (!m.Success) return null;
        var body = m.Groups[1].Value.Trim();
        return body.Length == 0 ? null : body;
    }
}
