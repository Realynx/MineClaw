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
    /// <summary>The floor/structure the build sits on: the footprint snaps to it, a size mismatch is flagged.</summary>
    public Building.SiteExtent? Extent { get; init; }
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
    public int Score => Plan == null || Eval == null ? 100_000 : Eval.Score(Critique);
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
    /// <summary>Every version (2+) failed the checks with the identical errors: likely a checker false positive, not the author.</summary>
    public bool SuspectChecker { get; set; }
    /// <summary>" (possible checker issue)" when <see cref="SuspectChecker"/>, for the notices.</summary>
    public string CheckerNote => SuspectChecker ? " (possible checker issue)" : "";
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
    /// <summary>The site world's height (bounds for normalizing); 128 when no site world is available.</summary>
    private int WorldHeight() { try { return Site().World.WorldHeight; } catch (Exception) { return ChunkColumn.SizeY; } }
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
        string? lastFailure = null;
        int cleanAt = 0; // first version that passed with no real warnings
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
            DesignVersion v;
            if (dsl.Length == 0 || turn.CutOff && BlueprintParser.Parse(dsl).Blueprint.W == 0 && !dsl.Contains("layer", StringComparison.OrdinalIgnoreCase))
            {
                // Nothing to parse: the reply was cut off before the blueprint (Opus 5.5 thinks even at "off"; the budget went there).
                var why = turn.CutOff ? $"reply cut off at the output limit ({turn.Usage.OutputTokens} tokens) before any blueprint" : "empty reply (no blueprint)";
                v = new DesignVersion { N = n, Dsl = dsl, Structural = [new BlueprintIssue(IssueLevel.Error, "cutoff", why)] };
                outcome.Versions.Add(v);
                Evaluated?.Invoke(v, outcome);
                var sig = Failure(v);
                if (sig == lastFailure) { outcome.Stop = $"stopped: the same failure twice ({sig})"; break; }
                lastFailure = sig;
                if (n == max) { outcome.Stop = outcome.Best is { Passing: true } ? "iterations used (best passing kept)" : $"iterations used, no passing version ({why})"; break; }
                conv.Add(LlmMessage.User(BlueprintPrompts.CutOffHint));
                continue;
            }
            v = Evaluate(dsl, n, req);
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
            // A clean pass (no real warnings) gets one look revision for the reviewer, then the best is kept: no more versions
            // chasing taste (live #8: v1 clean but for a false positive, v2/v3 only got worse).
            if (cleanAt > 0) { outcome.Stop = "passed (clean version kept unless the look revision beat it)"; break; }
            if (v.Passing && v.Eval!.RealWarnings == 0) cleanAt = n;
            // The same failure twice in a row: a third try won't differ (live 10-28: 3 × "missing size" per design, $0.10 each).
            var signature = Failure(v);
            if (signature != null && signature == lastFailure)
            {
                outcome.Stop = $"stopped: the same failure twice ({signature})";
                break;
            }
            lastFailure = signature;
            if (n == max) { outcome.Stop = outcome.Best is { Passing: true } ? "iterations used (best passing kept)" : "iterations used, no passing version"; break; }
            var ask = v.EvalText;
            if (v.Structural.Any(i => i.Level == IssueLevel.Error && i.Code == "size")) ask += "\n" + BlueprintPrompts.SizeHint;
            if (turn.CutOff) ask += "\n(your reply hit the output limit: keep it shorter with layer ranges and primitives)";
            conv.Add(LlmMessage.User(ask + "\nRevise: send the whole blueprint."));
        }
        if (outcome.Stop.Length == 0) outcome.Stop = "iterations used";
        // Every version failing with the very same check errors: the author can't fix what may be a checker false positive.
        var failed = outcome.Versions;
        if (failed.Count >= 2 && failed.All(x => x.Eval != null && !x.Passing)
            && failed.Select(ErrorSet).Distinct().Count() == 1 && ErrorSet(failed[0]).Length > 0)
        {
            outcome.SuspectChecker = true;
            outcome.Stop += outcome.CheckerNote;
        }
        // The winner gets its picture for the players (renders are cached: usually free).
        if (outcome.Best is { Plan: { } bp } best && best.Picture == null)
            try { best.Picture = preview.Render(bp, Site().World, Roles(), Light(), Atlas(), Time()); } catch (Exception) { }
        outcome.Ms = sw.ElapsedMilliseconds;
        return outcome;
    }

    /// <summary>A parse-level failure's signature (same errors = same failure), or null for versions that parsed.</summary>
    private static string? Failure(DesignVersion v) =>
        v.Plan != null ? null : v.Structural.Where(i => i.Level == IssueLevel.Error).Select(i => i.Text).DefaultIfEmpty("unparsable").Aggregate((a, b) => a + "; " + b);

    /// <summary>The check errors of a failed version (same set = same failure).</summary>
    private static string ErrorSet(DesignVersion v) =>
        string.Join("; ", (v.Eval?.Issues ?? v.Structural).Where(i => i.Level == IssueLevel.Error).Select(i => i.Code + ":" + i.Text).Order(StringComparer.Ordinal));

    /// <summary>Passing beats failing; among equals the better quality score wins, and a tie keeps the earlier version.</summary>
    public static bool Better(DesignVersion v, DesignVersion? best) =>
        best == null || (v.Passing && !best.Passing) || (v.Passing == best.Passing && v.Score < best.Score);

    /// <summary>Parse, normalize at the site and check one version (no model calls).</summary>
    public DesignVersion Evaluate(string dsl, int n, DesignRequest req)
    {
        var (bp, issues) = BlueprintParser.Parse(dsl);
        if (issues.Any(i => i.Level == IssueLevel.Error && i.Code == "size")) return new DesignVersion { N = n, Dsl = dsl, Structural = issues };
        var (fx, fz) = bp.Footprint;
        var origin = new BlockPos(req.Site.X - fx / 2, req.Site.Y + 1, req.Site.Z - fz / 2);
        if (req.Extent is { } ext)
        {
            // Snap onto the referenced floor: its corner when the footprint matches, else centred on it (and flagged).
            bp.Origin = null;
            origin = Building.SiteAnchor.Mismatch(fx, fz, ext) is { } mismatch
                ? new BlockPos((ext.Min.X + ext.Max.X + 1) / 2 - fx / 2, ext.Max.Y + 1, (ext.Min.Z + ext.Max.Z + 1) / 2 - fz / 2)
                : new BlockPos(ext.Min.X, ext.Max.Y + 1, ext.Min.Z);
            if (Building.SiteAnchor.Mismatch(fx, fz, ext) is { } m2) issues.Add(new BlueprintIssue(IssueLevel.Warning, "size-mismatch", m2));
        }
        var (plan, more) = BlueprintNormalizer.Normalize(bp, origin, WorldHeight());
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
