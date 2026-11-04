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
    /// <summary>This job's spend cap (USD); null = <see cref="LlmOptions.AuthorMaxUsd"/>. The tighter of the two applies.</summary>
    public decimal? MaxUsd { get; init; }
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
        var run = new AuthorRun(job, model, JobCap(job), usage?.Prices ?? new PriceTable());
        int maxVersions = Math.Max(1, options.AuthorMaxVersions);
        for (int i = 0; i < maxVersions; i++)
        {
            var resp = await CallAsync(run, ct).ConfigureAwait(false);
            if (resp == null) break; // the call hit the cost cap
            var (artifact, check) = Draft(run, resp);
            if (check.Ok)
            {
                log?.LogInformation("author {Kind}: ok after {Versions} version(s), {Out} output tokens, ${Usd:0.00}", job.Kind, run.Versions, run.Total.OutputTokens, run.Spent);
                return new AuthorOutcome(true, artifact, run.Versions, run.Total, run.Spent, model, null);
            }
            run.Blocker = BlockerOf(artifact, resp.StopReason, check);
            if (run.Spent >= run.Cap) { run.Blocker = $"cost cap ${run.Cap:0.00} reached"; break; }
            // The draft and its errors stay in THIS context only (the brain never sees them).
            run.Conversation.Add(LlmMessage.Assistant(resp.Text.Length > 0 ? resp.Text : "(no reply)"));
            run.Conversation.Add(LlmMessage.User(check.Feedback + "\nAnswer with the WHOLE corrected artifact in one ``` block, nothing else."));
        }
        log?.LogInformation("author {Kind}: gave up after {Versions} version(s): {Blocker} (${Usd:0.00})", job.Kind, run.Versions, run.Blocker, run.Spent);
        return new AuthorOutcome(false, null, run.Versions, run.Total, run.Spent, model, run.Blocker ?? "no passing version");
    }

    /// <summary>One job's running state: its own conversation (drafts and feedback never leave it), usage, spend, last blocker.</summary>
    private sealed class AuthorRun(AuthorJob job, string model, decimal cap, PriceTable prices)
    {
        public AuthorJob Job => job;
        public string Model => model;
        public decimal Cap => cap;
        public PriceTable Prices => prices;
        public List<LlmMessage> Conversation { get; } = [LlmMessage.User(job.Brief)];
        public LlmUsage Total { get; set; } = LlmUsage.Zero;
        public decimal Spent { get; set; }
        public string? Blocker { get; set; }
        public int Versions { get; set; }
    }

    /// <summary>The job's spend cap: the configured one (default $3), tightened by the job's own.</summary>
    private decimal JobCap(AuthorJob job)
    {
        decimal cap = options.AuthorMaxUsd > 0 ? options.AuthorMaxUsd : 3.00m;
        if (job.MaxUsd is { } jobCap && jobCap > 0) cap = Math.Min(cap, jobCap);
        return cap;
    }

    private LlmRequest Request(AuthorRun run) => new()
    {
        Tier = ModelTier.Main, Model = run.Model, MaxTokens = options.AuthorMaxTokensClamped, Purpose = "author-" + run.Job.Kind,
        System = [new LlmSystemBlock(run.Job.System, Cache: true)], Messages = run.Conversation, CacheHistory = true,
        Effort = string.IsNullOrWhiteSpace(options.AuthorEffort) ? null : options.AuthorEffort.Trim(),
        MaxUsd = run.Cap - run.Spent, CostOf = u => run.Prices.Cost(run.Model, u),
    };

    /// <summary>One author call; null when it hit the cost cap (its partial usage booked). An interrupt books and rethrows.</summary>
    private async Task<LlmResponse?> CallAsync(AuthorRun run, CancellationToken ct)
    {
        try { return await llm.CompleteAsync(Request(run), ct).ConfigureAwait(false); }
        catch (LlmCostCapException cc)
        {
            run.Total = run.Total.Add(cc.PartialUsage);
            run.Spent += Book(cc.Model, cc.PartialUsage, run.Prices);
            log?.LogWarning("author {Kind}: call aborted at ${Usd:0.00} (job cap ${Cap:0.00}, {Out} output tokens)", run.Job.Kind, run.Spent, run.Cap, cc.PartialUsage.OutputTokens);
            run.Blocker = $"cost cap ${run.Cap:0.00} reached";
            return null;
        }
        catch (LlmInterruptedException ie) when (ie.PartialUsage is { } pu)
        {
            run.Total = run.Total.Add(pu);
            run.Spent += Book(ie.Model, pu, run.Prices);
            throw;
        }
    }

    /// <summary>Books a version and checks its artifact (none: ask for the block alone, sooner when it was cut off).</summary>
    private (string? Artifact, AuthorCheck Check) Draft(AuthorRun run, LlmResponse resp)
    {
        run.Versions++;
        run.Total = run.Total.Add(resp.Usage);
        run.Spent += Book(resp.Model, resp.Usage, run.Prices);
        var artifact = run.Job.Extract(resp.Text);
        var check = artifact == null
            ? AuthorCheck.Fail(resp.StopReason == StopReasons.MaxTokens
                ? "Your reply hit the output limit before the artifact. Answer at once with ONLY the artifact in one ``` block."
                : "Reply with ONLY the artifact in one ``` block.")
            : SafeCheck(run.Job, artifact);
        return (artifact, check);
    }

    private static string BlockerOf(string? artifact, string? stopReason, AuthorCheck check) =>
        artifact == null ? (stopReason == StopReasons.MaxTokens ? "cut off at the output limit" : "no artifact in the reply")
            : "checks still failing: " + FirstLine(check.Feedback);

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
