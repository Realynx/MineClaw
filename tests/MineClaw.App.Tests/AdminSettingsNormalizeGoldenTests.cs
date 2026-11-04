using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.App.Supervisor.Admin;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="AdminSettingsService.Normalize"/> (WP07): every registry setting against a list of request values (null,
/// bools, numbers, ranges, strings with spaces and control characters, lists, objects). The dump is long, so the test keeps its
/// line count and SHA-256 (WP07_GOLDEN_OUT writes it for a diff).
/// </summary>
public class AdminSettingsNormalizeGoldenTests
{
    private static readonly string[] Values =
    [
        "null", "true", "false", "\"true\"", "\"False\"", "\"yes\"", "0", "1", "-1", "2.5", "1e3", "3000000000", "\"42\"", "\"4.25\"", "\"x\"",
        "\"\"", "\"  \"", "\" claude-opus-5-5 \"", "\"claude-sonnet-5-5\"", "\"gpt-4o\"", "\"bad id!\"", "\"a\\u0001b\"", "\"line\\nbreak\"",
        "\"tab\\there\"", "\"sk-ant 123\"", "\"http://10.0.0.5:11434\"", "\"ftp://x\"", "\"https://api.anthropic.com\"", "\"auto\"", "\"AUTO\"",
        "\"json\"", "\"anthropic\"", "\"ollama\"", "\"\"", "[]", "[\"Fox\",\" Robin \"]", "[\"\"]", "[1]", "[\"a\\u0002\"]", "{}",
        "\"" + new string('k', 300) + "\"", "\"" + new string('t', 5000) + "\"", "[" + string.Join(",", Enumerable.Range(0, 300).Select(i => $"\"p{i}\"")) + "]",
    ];

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var d in SettingsRegistry.All)
            foreach (var raw in Values)
            {
                using var doc = JsonDocument.Parse(raw);
                var err = AdminSettingsService.Normalize(d, doc.RootElement, out var node);
                sb.Append(d.Key).Append(' ').Append(raw.Length > 40 ? raw[..40] + "…" : raw).Append(" -> ")
                  .Append(err ?? "ok").Append(' ').Append(node?.ToJsonString() ?? "-").Append('\n');
            }
        return sb.ToString();
    }

    [Fact]
    public void Normalize_matches_the_golden_hash()
    {
        var dump = Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "settings-normalize.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(AdminSettingsNormalizeGoldenData.Expected, $"{lines} {hash}");
    }
}
