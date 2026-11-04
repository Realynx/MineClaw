using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;
using MineClaw.Render;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="VisionRouter.Answer"/> (WP03 refactor) word for word: every intent over every eval world, foci around each
/// case's focus, two radii, with drops and mobs (some burning) around and without entities.
/// </summary>
public class VisionRouterGoldenTests
{
    private static IReadOnlyList<VisionEntity> Around((int X, int Y, int Z) f) =>
    [
        new("item", "cobblestone", f.X + 1.3, f.Y, f.Z + 0.4) { ItemId = 4 },
        new("item", "sapling", f.X - 2.6, f.Y + 1, f.Z + 3.1) { ItemId = 6 },
        new("item", "log", f.X + 30, f.Y, f.Z) { ItemId = 17 },
        new("mob", "zombie", f.X + 4, f.Y, f.Z - 2) { Mob = "zombie", Burning = true },
        new("mob", "zombie", f.X + 5, f.Y, f.Z - 3) { Mob = "zombie", Burning = true },
        new("mob", "skeleton", f.X - 6, f.Y, f.Z + 1) { Mob = "skeleton" },
        new("mob", "cave_spider", f.X - 3, f.Y + 2, f.Z) { Mob = "cave_spider" },
    ];

    [Fact]
    public void Answers_are_unchanged()
    {
        var sb = new StringBuilder();
        var questions = new Dictionary<VisionIntent, string>
        {
            [VisionIntent.Void] = "how far is the edge to the east?",
            [VisionIntent.Things] = "any trees within 20 of here?",
        };
        foreach (var c in VisionEval.Cases(null))
        {
            var roles = VisionEval.Roles(c).Map;
            var w = c.World.PlanningView;
            for (int dx = -2; dx <= 2; dx += 2)
            for (int dy = -1; dy <= 1; dy++)
            foreach (int r in new[] { 4, 10 })
            foreach (var intent in Enum.GetValues<VisionIntent>())
            {
                if (intent == VisionIntent.Model) continue;
                var f = (c.Focus.Item1 + dx, c.Focus.Item2 + dy, c.Focus.Item3);
                var q = questions.GetValueOrDefault(intent, "what is here?");
                sb.Append($"{c.Id} {f} r{r} {intent}: {VisionRouter.Answer(intent, q, w, roles, f, r)}\n");
                sb.Append($"  ents: {VisionRouter.Answer(intent, q, w, roles, f, r, Around(f))}\n");
            }
        }
        var dump = sb.ToString();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "vision-router.txt"), dump);
        Assert.Equal(Expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "A5AB75CEF62D3ED6FC77B96F";
}
