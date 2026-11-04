using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Bot.GameTools;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the see tool's <c>views</c> parsing (WP03 refactor): <see cref="VisionHub.ParseViews"/> over string and object forms of
/// every view kind with bodies, spots, compass sides, distances, orbit counts and camera keys, plus the errors.
/// </summary>
public class SeeViewsGoldenTests
{
    private static readonly string[] Views =
    [
        "eye", "eye 67", "eye:67", "EYE maid_chan_69", "follow", "follow robinchan", "cam", "cam tower", "cam tower NE", "cam tower NE 14",
        "cam 4,64,6 above", "cam 4,64,6 12", "camera gen sw 8.5", "freecam spot", "cam NE", "cam 14", "cam tower NE 14 pitch=60",
        "orbit", "orbit 3", "orbit gen", "orbit gen 3", "orbit gen x", "orbit 1", "orbit 4,64,6 2 yaw=10", "high", "iso yaw=200 pitch=50",
        "top zoom=2 dx=3", "side", "layers", "shoulder", "iso", "  ", "bogus", "bogus 3", "high above", "top:4,64,6",
        """{"cam":{"at":"tower","from":"NE","dist":14}}""", """{"camera":{"at":"tower","dist":"x"}}""", """{"cam":{"from":"NE"}}""",
        """{"orbit":{"at":"gen","n":3,"yaw":20}}""", """{"orbit":{}}""", """{"eye":{"body":"67"}}""", """{"eye":{"at":"69"}}""",
        """{"follow":{"at":"robin"}}""", """{"follow":{}}""", """{"iso":{"yaw":200,"pitch":50,"zoom":1.3}}""", """{"top":{"zoom":2,"dx":3}}""",
        """{"bogus":{"at":"x"}}""", """{"eye":"67"}""", """{"cam":"tower NE 14"}""", """{"high":true}""", """{"bogus":true}""",
        """{"high":false}""", """{"cam":{"at":"t","dist":5,"pitch":40}}""", "{}", "3",
    ];

    private static string Parse(string view)
    {
        var json = view.TrimStart().StartsWith('{') || view == "3" ? $$"""{"views":[{{view}}]}""" : JsonSerializer.Serialize(new { views = new[] { view } });
        var list = VisionHub.ParseViews(JsonDocument.Parse(json).RootElement, out var error);
        return list == null ? $"null err={error ?? "-"}" : string.Join(" ; ", list.Select(p => p + " fmt=" + p.Format()));
    }

    [Fact]
    public void Views_parse_to_the_golden_requests()
    {
        var sb = new StringBuilder();
        foreach (var v in Views) sb.Append(v).Append(" => ").Append(Parse(v)).Append('\n');
        var many = VisionHub.ParseViews(JsonDocument.Parse("""{"views":["orbit gen 5","orbit gen 5","orbit gen 5","high"]}""").RootElement, out var err);
        sb.Append("many => ").Append(many == null ? "null" : many.Count.ToString()).Append(' ').Append(err ?? "-").Append('\n');
        var single = VisionHub.ParseViews(JsonDocument.Parse("""{"views":"iso"}""").RootElement, out var err2);
        sb.Append("single => ").Append(single?.Count.ToString() ?? "null").Append(' ').Append(err2 ?? "-").Append('\n');
        var dump = sb.ToString();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "see-views.txt"), dump);
        Assert.Equal(Expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "C0B9563DDC46AC643E19816F";
}
