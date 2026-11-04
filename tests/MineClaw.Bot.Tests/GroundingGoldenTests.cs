using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Perception;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="PerceptionService.Grounding"/> (WP03 refactor) word for word over every eval world, foci around each case's
/// focus (air and solid), three radii, and with/without facts that already name some cells.
/// </summary>
public class GroundingGoldenTests
{
    [Fact]
    public void Grounding_texts_are_unchanged()
    {
        var sb = new StringBuilder();
        foreach (var c in VisionEval.Cases(null))
        {
            var roles = VisionEval.Roles(c).Map;
            var w = c.World.PlanningView;
            for (int dx = -2; dx <= 2; dx += 2)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -2; dz <= 2; dz += 2)
            foreach (int r in new[] { 3, 6, 12 })
            {
                var f = (c.Focus.Item1 + dx, c.Focus.Item2 + dy, c.Focus.Item3 + dz);
                var none = PerceptionService.Grounding(w, roles, f, r, []);
                sb.Append($"{c.Id} {f} r{r}: {none ?? "-"}\n");
                if (dx == 0 && dz == 0)
                {
                    var named = new VerifiedFact("x", [(f.Item1, f.Item2, f.Item3), (f.Item1 + 1, f.Item2, f.Item3), (f.Item1, f.Item2 - 1, f.Item3)], null, 0.9, FactStatus.Verified);
                    sb.Append($"  named: {PerceptionService.Grounding(w, roles, f, r, [named]) ?? "-"}\n");
                }
            }
        }
        var dump = sb.ToString();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "grounding.txt"), dump);
        Assert.Equal(Expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "62A5B7F4CBB8C08956EA21CC";
}
