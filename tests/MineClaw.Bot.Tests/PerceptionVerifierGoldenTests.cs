using System.Text;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Perception;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="PerceptionVerifier.Verify"/> (WP03 refactor) over the eval cases × a spread of claims: air/solid/role/block/negated
/// claims, true, off-by-one and hallucinated coordinates, coordinate-free and unchecked facts, answers naming kept, snapped and
/// refuted coordinates. Every verdict, count, fact and text is compared word for word.
/// </summary>
public class PerceptionVerifierGoldenTests
{
    private static PerceptionResult Result(string json) => PerceptionPrompt.Result(json, new LlmUsage(500, 80, 0, 0), "claude-sonnet-5-5", "anthropic", 900);

    private static readonly string[] Claims =
    [
        "gen cell between water and lava", "gap in the floor", "hole at the edge", "solid floor", "floor block", "wall of the gen",
        "not a wall", "not a gen cell", "cobblestone block", "glowstone floor east", "water source", "lava pool", "void below",
        "chest", "the gen looks well built", "planks", "soul sand", "solid block here",
    ];

    private static IEnumerable<string> Jsons(VisionEvalCase c)
    {
        var (x, y, z) = c.Focus;
        string P(int dx, int dy, int dz) => $"[{x + dx},{y + dy},{z + dz}]";
        for (int i = 0; i < Claims.Length; i++)
        {
            var claim = Claims[i];
            var coordSets = new[]
            {
                "", P(0, 0, 0), P(0, -1, 0), P(1, 0, 0), P(0, 1, 0), P(2, -1, 1) + "," + P(-1, -1, 0), P(40, 0, 0),
                P(1, -1, 1) + "," + P(0, -1, 0) + "," + P(-2, 0, 0),
            };
            foreach (var cs in coordSets)
                foreach (var conf in new[] { 0.5, 0.9 })
                {
                    var answer = cs.Length == 0 ? "nothing to add" : $"look at {cs.Split(']')[0].TrimStart('[')} for the {claim}";
                    yield return $$"""{"answer":"{{answer}}","facts":[{"claim":"{{claim}}","coords":[{{cs}}],"confidence":{{conf}}}]}""";
                }
            yield return $$"""{"answer":"the {{claim}} at {{x}},{{y - 1}},{{z}}","facts":[{"claim":"{{claim}}","coords":[{{P(0, -1, 0)}}],"role":"floor","confidence":0.9},{"claim":"glowstone floor east","coords":[{{P(3, -1, 0)}}],"confidence":0.9},{"claim":"{{Claims[(i + 1) % Claims.Length]}}","coords":[{{P(0, 0, 1)}}],"role":"gen cell","confidence":0.8}]}""";
        }
        yield return """{"answer":"","facts":[]}""";
    }

    [Fact]
    public void Verifier_outputs_match_the_golden_hash()
    {
        var sb = new StringBuilder();
        foreach (var c in VisionEval.Cases(null))
        {
            var roles = VisionEval.Roles(c).Map;
            foreach (var json in Jsons(c))
            {
                var v = PerceptionVerifier.Verify(c.Question, Result(json), c.World.PlanningView, roles, c.Focus, c.Radius);
                sb.Append(c.Id).Append(" | ").Append(json).Append('\n');
                sb.Append($"  ans={v.Answer ?? "null"} chk={v.Checked} mis={v.Mismatched} drop={v.Dropped} cor={v.Corrected} unv={v.Unverified}\n");
                foreach (var f in v.Facts)
                    sb.Append($"  {f.Status} {f.Role?.ToString() ?? "-"} {f.Confidence} [{string.Join(" ", f.Coords)}] {f.Note ?? "-"} :: {f.Claim}\n");
                sb.Append("  text=").Append(v.Text).Append('\n');
            }
        }
        var bad = PerceptionVerifier.Verify("q", new PerceptionResult([], null, "", new LlmUsage(0, 0, 0, 0), "m", "p", 0, "down"), VisionEval.Cases(null)[0].World.PlanningView,
            VisionEval.Roles(VisionEval.Cases(null)[0]).Map, (0, 0, 0), 4);
        sb.Append("failed: ").Append(bad.Text).Append('\n');
        var dump = sb.ToString();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "verifier.txt"), dump);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24];
        Assert.Equal(Expected, hash);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "26377F2B665A4093333AA5F2";
}
