using System.Globalization;
using System.Text;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Perception;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Perception.Vision;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="PerceptionCritic.CritiqueAsync"/> over scripted perception answers: ok words, an empty answer, a problem with
/// and without facts (coords on and off the build, empty claims, more than three), an error, a throwing model, an over-long text,
/// a cache hit, no service, direct mode and a spent budget. A refactor must leave <see cref="DesignCriticGoldenData.Expected"/>
/// unchanged. Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class DesignCriticGoldenTests
{
    private sealed class ScriptedModel(Func<PerceptionResult> answer) : IPerceptionModel
    {
        public string Provider => "anthropic";
        public string Model => "claude-sonnet-5-5";
        public Task<PerceptionResult> AskAsync(PerceptionQuery query, CancellationToken ct) => Task.FromResult(answer());
    }

    private static PerceptionResult R(string? answer, string? error = null, params (string Claim, (int X, int Y, int Z)[] At)[] facts) =>
        new(facts.Select(f => new PerceptionFact(f.Claim, f.At, null, 0.9)).ToList(), answer, "{}", LlmUsage.Zero, "claude-sonnet-5-5", "anthropic", 5, error);

    private static IEnumerable<(string Name, Func<PerceptionResult>? Answer, PerceptionOptions O)> Cases()
    {
        var o = new PerceptionOptions();
        yield return ("ok", () => R("ok"), o);
        yield return ("Looks good.", () => R("Looks good."), o);
        yield return ("empty", () => R(""), o);
        yield return ("null answer", () => R(null), o);
        yield return ("problem", () => R("the roof is flat"), o);
        yield return ("ok with facts", () => R("ok", null, ("door faces a wall", [(0, 64, 0), (1, 65, 1)]), ("odd glass", [(50, 64, 50)])), o);
        yield return ("problem with facts", () => R("roof too flat", null, ("  ", [(0, 64, 0)]), ("door faces a wall", [(-2, 64, -2), (0, 65, 0), (1, 66, 1)]),
            ("windows uneven", []), ("odd block", [(2, 64, 2)]), ("fourth", [(0, 64, 0)])), o);
        yield return ("empty answer with facts", () => R("", null, ("no roof", [(0, 67, 0)])), o);
        yield return ("long", () => R(string.Concat(Enumerable.Repeat("the walls are much too tall for a hut ", 10))), o);
        yield return ("error", () => R(null, "http 529"), o);
        yield return ("throws", () => throw new InvalidOperationException("boom"), o);
        yield return ("no service", null, o);
        yield return ("direct", () => R("ok"), new PerceptionOptions { Mode = PerceptionModes.Direct });
        yield return ("no budget", () => R("ok"), new PerceptionOptions { PerHour = 0 });
    }

    internal static async Task<string> Dump()
    {
        var (plan, _) = BlueprintNormalizer.FromText(BlueprintTests.Hut, new BlockPos(-2, 64, -2));
        var sb = new StringBuilder();
        ulong key = 1;
        foreach (var (name, answer, o) in Cases())
        {
            var svc = answer == null ? null : new PerceptionService(new ScriptedModel(answer), o);
            var critic = new PerceptionCritic(() => svc, null);
            var pic = new DesignPicture([1, 2, 3], 8, 8, "legend", key++, null!);
            sb.Append("== ").Append(name).Append('\n');
            foreach (var desc in new[] { "a small cobble hut", " A small cobble HUT " })
                Append(sb, await critic.CritiqueAsync(pic, plan!, desc, CancellationToken.None));
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Critique c) =>
        sb.Append(CultureInfo.InvariantCulture, $"  ok {c.Ok} skipped {c.Skipped} cached {c.Cached} usd {c.Usd} why {c.Why ?? "-"} text [{c.Text}] issues [{string.Join(" | ", c.Issues)}]\n");

    [Fact]
    public async Task Critiques_match_the_golden_dump()
    {
        var actual = (await Dump()).ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(DesignCriticGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
