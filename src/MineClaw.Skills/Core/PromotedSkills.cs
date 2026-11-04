using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// A skill made at run time from something the bot learned: a saved routine (replayed in C#, zero tokens per step)
/// or a taught structure's usage steps. Adding one costs zero prompt tokens — it only joins the searchable catalog.
/// </summary>
public sealed class PromotedSkill(SkillSpec spec, Func<SkillContext, JsonElement, CancellationToken, Task<SkillResult>> run) : ISkill
{
    public SkillSpec Spec { get; } = spec;
    public Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) => run(ctx, args, ct);

    public static string SkillName(string raw) =>
        Regex.Replace(raw.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_') is { Length: > 0 } s ? s : "unnamed";

    /// <summary>Wraps a routine runner (the Agent's RoutineExecutor) as skill <c>name</c>.</summary>
    public static PromotedSkill FromRoutine(string routine, string? summary, Func<int?, CancellationToken, Task<(bool Ok, string Text)>> runRoutine) =>
        new(new SkillSpec
        {
            Name = SkillName(routine), Category = SkillCategory.Misc, Source = "routine", Args = "repeat?",
            Summary = Trim(summary ?? $"replay routine {routine}"), Schema = SkillJson.Schema("repeat:int?"),
            Estimate = TimeSpan.FromMinutes(1), Keywords = routine.Replace('_', ' ') + " routine " + summary,
        }, async (_, args, ct) =>
        {
            var (ok, text) = await runRoutine(args.Int("repeat"), ct);
            return new SkillResult(ok, text);
        });

    /// <summary>
    /// "use_&lt;structure&gt;" from a demonstration's usage steps. Recognised steps run directly: "stand at x,y,z",
    /// "mine/break &lt;block&gt; at x,y,z [repeatedly|N times]", "open/use/press/right-click &lt;thing&gt; at x,y,z",
    /// "wait N s". Anything else stops the skill with needs_input naming that step.
    /// </summary>
    public static PromotedSkill FromStructure(string name, string kind, IReadOnlyList<string> steps, BlockPos? at)
    {
        var spec = new SkillSpec
        {
            Name = "use_" + SkillName(name), Category = SkillCategory.Misc, Source = "structure", Args = "times?",
            Summary = Trim($"use the {kind.Replace('_', ' ')} '{name}' as taught"), Schema = SkillJson.Schema("times:int?"),
            Estimate = TimeSpan.FromSeconds(30 + 10 * steps.Count), Keywords = $"{name} {kind.Replace('_', ' ')} use operate run taught farm",
        };
        return new PromotedSkill(spec, async (ctx, args, ct) =>
        {
            int times = Math.Clamp(args.Int("times") ?? 1, 1, 50), done = 0;
            for (int t = 0; t < times; t++)
            {
                foreach (var step in steps)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await RunStepAsync(ctx, step, ct);
                    if (r is null) return SkillResult.Fail($"needs_input: don't know how to do step \"{step}\" of {name}");
                    if (!r.Ok) return SkillResult.Fail($"{name} step \"{step}\": {r.Text} ({done} rounds done)");
                }
                done++;
            }
            return SkillResult.Success($"used {name} {done}x");
        });
    }

    private static readonly Regex Pos = new(@"(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)", RegexOptions.Compiled);

    private enum StepKind { None, Go, Dig, Use, Collect }

    // A step's verb (its first word) -> what it does.
    private static readonly (string[] Verbs, StepKind Kind)[] StepVerbs =
    [
        (["stand", "go", "walk"], StepKind.Go),
        (["mine", "break", "dig"], StepKind.Dig),
        (["open", "use", "press", "right", "pull", "flip"], StepKind.Use),
        (["collect", "pick up"], StepKind.Collect),
    ];

    private static StepKind KindOf(string s)
    {
        foreach (var (verbs, kind) in StepVerbs)
            if (verbs.Any(v => s.StartsWith(v))) return kind;
        return StepKind.None;
    }

    private static BlockPos? StepPos(string s)
    {
        var m = Pos.Match(s);
        return m.Success ? new BlockPos(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    internal static async Task<SkillResult?> RunStepAsync(SkillContext ctx, string step, CancellationToken ct)
    {
        var s = step.Trim().ToLowerInvariant();
        BlockPos? p = StepPos(s);
        if (Regex.Match(s, @"wait\s+(\d+)") is { Success: true } w)
        {
            await ctx.Game.WaitTicksAsync(Math.Clamp(int.Parse(w.Groups[1].Value), 1, 60) * 20, ct);
            return SkillResult.Success("waited");
        }
        if (p is not { } pos) return null;
        return KindOf(s) switch
        {
            StepKind.Go => SkillResult.From(await ctx.Game.GotoAsync(new GoalBlock(pos), null, ct)),
            StepKind.Dig => await DigStepAsync(ctx, s, pos, ct),
            StepKind.Use => SkillResult.From(await ctx.Game.ActivateAsync(pos, ct)),
            StepKind.Collect => SkillResult.From(await ctx.Game.CollectDropsAsync(8, ct)),
            _ => null,
        };
    }

    // "mine/break/dig <block> at x,y,z [repeatedly|N times]": a cell already empty is waited on once (a generator refilling).
    private static async Task<SkillResult> DigStepAsync(SkillContext ctx, string s, BlockPos pos, CancellationToken ct)
    {
        int n = Regex.Match(s, @"(\d+)\s*(x|times)") is { Success: true } c ? int.Parse(c.Groups[1].Value) : s.Contains("repeat") ? 16 : 1;
        var last = SkillResult.Success("nothing to dig");
        for (int i = 0; i < Math.Clamp(n, 1, 64); i++)
        {
            if (SkillWorld.Replaceable(ctx.World, pos)) { await ctx.Game.WaitTicksAsync(10, ct); if (SkillWorld.Replaceable(ctx.World, pos)) continue; }
            last = SkillResult.From(await ctx.Game.DigAsync(pos, ct));
            if (!last.Ok) return last;
        }
        await ctx.Game.CollectDropsAsync(6, ct);
        return last;
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
