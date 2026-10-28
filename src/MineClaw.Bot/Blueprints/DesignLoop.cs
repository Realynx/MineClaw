using System.Diagnostics;
using MineClaw.Agent.Llm;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>What a design run gets: the wish, the site (the ground block the build stands on) and the constraint text.</summary>
public sealed record DesignRequest(string Description, BlockPos Site, string SiteText, string Constraints)
{
    /// <summary>Revision: the blueprint to change and the change asked for.</summary>
    public string? PriorDsl { get; init; }
    public string? Change { get; init; }
}

/// <summary>One evaluated version of a design.</summary>
public sealed class DesignVersion
{
    public int N { get; init; }
    public required string Dsl { get; init; }
    public VoxelPlan? Plan { get; init; }
    public List<BlueprintIssue> Structural { get; init; } = new();
    public DesignEvaluation? Eval { get; init; }
    public Critique? Critique { get; set; }
    public DesignPicture? Picture { get; set; }
    public bool Passing => Plan != null && Eval is { Pass: true };
    public int Score => Plan == null || Eval == null ? 100_000 : Eval.Score(Critique is { Ok: false } c ? Math.Max(1, c.Issues.Count) : 0);
    public string EvalText => Eval?.Compact(N, Critique is { Skipped: false, Ok: false } c ? c.Text : null)
        ?? $"v{N} FAIL | " + string.Join("; ", Structural.Where(i => i.Level == IssueLevel.Error).Take(6));
}

/// <summary>The result: the best version (passing ones first), every version, spend and why it stopped.</summary>
public sealed class DesignOutcome
{
    public DesignVersion? Best { get; set; }
    public List<DesignVersion> Versions { get; } = new();
    public decimal AuthorUsd { get; set; }
    public decimal PerceptionUsd { get; set; }
    public decimal Usd => AuthorUsd + PerceptionUsd;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long Ms { get; set; }
    public string Stop { get; set; } = "";
    public bool ImageShown { get; set; }
}

/// <summary>
/// The design loop (zero brain tokens): the Opus author writes v1 from the brief → parse + normalize + deterministic checks →
/// a preview rendered in the real site → the perception tier's critique (passing versions only) → the author gets the ≈100-token
/// evaluation text (never the image unless it answers SHOW) and revises; at most <see cref="BlueprintOptions.MaxIterations"/>
/// versions and <see cref="BlueprintOptions.MaxUsdPerDesign"/> spend. The best passing version wins.
/// </summary>
public sealed class DesignLoop(IBlueprintAuthor author, IDesignCritic? critic, BlueprintPreview preview, BlueprintOptions o)
{
    /// <summary>The world, roles and light the checks and the preview see.</summary>
    public required Func<SiteContext> Site { get; init; }
    public Func<RoleMap?> Roles { get; init; } = () => null;
    public Func<IVoxelWorld?> Light { get; init; } = () => null;
    public Func<TextureAtlas?> Atlas { get; init; } = () => null;
    public Func<long> Time { get; init; } = () => 6000;
    /// <summary>Per version: (version, outcome so far) for telemetry/logs.</summary>
    public Action<DesignVersion, DesignOutcome>? Evaluated { get; init; }

    public async Task<DesignOutcome> RunAsync(DesignRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var outcome = new DesignOutcome();
        var conv = new List<LlmMessage> { LlmMessage.User(BlueprintPrompts.Brief(req.Description, req.SiteText, req.Constraints, req.PriorDsl, req.Change)) };
        int max = Math.Clamp(o.MaxIterations, 1, 8);
        for (int n = 1; n <= max; n++)
        {
            if (outcome.Usd >= o.MaxUsdPerDesign) { outcome.Stop = $"cost cap ${o.MaxUsdPerDesign:0.00}"; break; }
            AuthorTurn turn;
            try { turn = await author.WriteAsync(conv, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { outcome.Stop = "author failed: " + ex.Message; break; }
            outcome.AuthorUsd += turn.Usd;
            outcome.InputTokens += turn.Usage.InputTokens + turn.Usage.CacheReadTokens + turn.Usage.CacheWriteTokens;
            outcome.OutputTokens += turn.Usage.OutputTokens;
            if (BlueprintPrompts.WantsImage(turn.Text))
            {
                var last = outcome.Versions.LastOrDefault(v => v.Picture != null)?.Picture;
                conv.Add(LlmMessage.Assistant(string.IsNullOrWhiteSpace(turn.Text) ? "SHOW" : turn.Text.Trim()));
                if (last != null && !outcome.ImageShown)
                {
                    outcome.ImageShown = true;
                    conv.Add(LlmMessage.User(new LlmImageBlock(new LlmImage(last.Png, last.Width, last.Height, "[design preview]")),
                        new LlmText(last.Legend + "\nNow send the whole blueprint.")));
                }
                else conv.Add(LlmMessage.User("No image available now. Send the whole blueprint."));
                continue; // counts against the iterations: an image round is a round
            }
            var dsl = BlueprintPrompts.ExtractDsl(turn.Text);
            conv.Add(LlmMessage.Assistant("```\n" + dsl + "\n```")); // history keeps the DSL only (prose dropped)
            var v = Evaluate(dsl, n, req);
            if (v.Passing && critic != null && o.Critique && v.Plan != null)
            {
                try
                {
                    v.Picture ??= preview.Render(v.Plan, Site().World, Roles(), Light(), Atlas(), Time());
                    v.Critique = await critic.CritiqueAsync(v.Picture, v.Plan, req.Description, ct).ConfigureAwait(false);
                    outcome.PerceptionUsd += v.Critique.Usd;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { v.Critique = Critique.Skip("critique failed: " + ex.Message); }
            }
            outcome.Versions.Add(v);
            Evaluated?.Invoke(v, outcome);
            if (Better(v, outcome.Best)) outcome.Best = v;
            if (v.Passing && v.Critique is null or { Ok: true } or { Skipped: true }) { outcome.Stop = "passed"; break; }
            if (n == max) { outcome.Stop = outcome.Best is { Passing: true } ? "iterations used (best passing kept)" : "iterations used, no passing version"; break; }
            conv.Add(LlmMessage.User(v.EvalText + "\nRevise: send the whole blueprint."));
        }
        if (outcome.Stop.Length == 0) outcome.Stop = "iterations used";
        // The winner gets its picture for the players (renders are cached: usually free).
        if (outcome.Best is { Plan: { } bp } best && best.Picture == null)
            try { best.Picture = preview.Render(bp, Site().World, Roles(), Light(), Atlas(), Time()); } catch (Exception) { }
        outcome.Ms = sw.ElapsedMilliseconds;
        return outcome;
    }

    private static bool Better(DesignVersion v, DesignVersion? best) =>
        best == null || (v.Passing && !best.Passing) || (v.Passing == best.Passing && v.Score <= best.Score);

    /// <summary>Parse, normalize at the site and check one version (no model calls).</summary>
    public DesignVersion Evaluate(string dsl, int n, DesignRequest req)
    {
        var (bp, issues) = BlueprintParser.Parse(dsl);
        if (issues.Any(i => i.Level == IssueLevel.Error && i.Code == "size")) return new DesignVersion { N = n, Dsl = dsl, Structural = issues };
        var (fx, fz) = bp.Footprint;
        var origin = new BlockPos(req.Site.X - fx / 2, req.Site.Y + 1, req.Site.Z - fz / 2);
        var (plan, more) = BlueprintNormalizer.Normalize(bp, origin);
        issues.AddRange(more);
        if (plan == null) return new DesignVersion { N = n, Dsl = dsl, Structural = issues };
        var s = Site();
        if (!s.WantsLight && System.Text.RegularExpressions.Regex.IsMatch(req.Description, @"\b(lit|light|lighting|torch|torches|bright|spawn-?proof)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            s = new SiteContext
            {
                World = s.World, Verdict = s.Verdict, Inventory = s.Inventory, Stored = s.Stored, GeneratorKnown = s.GeneratorKnown,
                ChoppableTrees = s.ChoppableTrees, WantsLight = true,
            };
        var eval = DesignChecks.Evaluate(plan, s, issues);
        return new DesignVersion { N = n, Dsl = dsl, Plan = plan, Structural = issues, Eval = eval };
    }
}
