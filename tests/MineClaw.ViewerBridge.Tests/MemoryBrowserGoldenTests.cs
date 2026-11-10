using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.ViewerBridge.Data;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins <see cref="MemoryBrowser"/> (WP07) over the fixture db: every kind's grid SELECT (and an unknown kind), the kinds list, pages
/// (filters by text, dimension and state; each sort, both directions, paging) and the detail of each kind's first rows (record,
/// props, edit prefill, people). Wall-clock stamps the fixture writes are masked. The test keeps the dump's line count and SHA-256
/// (WP07_GOLDEN_OUT writes the dump for a diff).
/// </summary>
public class MemoryBrowserGoldenTests
{
    private static readonly Regex Millis = new(@"\b1[6-9]\d{11}\b", RegexOptions.Compiled);
    private static readonly Regex Iso = new(@"\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(\.\d+)?Z?", RegexOptions.Compiled);

    private static string Mask(string s) => Iso.Replace(Millis.Replace(s, "<ms>"), "<time>");

    internal static async Task<string> Dump()
    {
        var fx = await FixtureDb.SharedAsync();
        using var bank = await MemoryBank.OpenAsync(fx.Path, new AgentOptions(), new HashEmbeddings());
        var b = new MemoryBrowser(bank);
        var sb = new StringBuilder();
        var keys = MemoryBrowser.Kinds.Select(k => k.Key).Append("bogus").ToList();
        foreach (var k in keys) sb.Append("select ").Append(k).Append(": ").Append(b.Select(k) ?? "null").Append('\n');
        sb.Append("kinds: ").Append(JsonSerializer.Serialize(b.KindsInfo())).Append('\n');
        var queries = new[]
        {
            new MemoryListQuery(null, "id", "asc", "all", null, 0, 4),
            new MemoryListQuery(null, "name", "desc", "all", null, 1, 3),
            new MemoryListQuery("cobble", "type", "asc", "all", null, 0, 3),
            new MemoryListQuery("the a", "state", "asc", "overworld", null, 0, 3),
            new MemoryListQuery(null, "dim", "asc", "nether", null, 0, 3),
            new MemoryListQuery(null, "conf", "desc", "none", null, 0, 3),
            new MemoryListQuery(null, "bogus", "asc", "all", "verified", 0, 3),
        };
        foreach (var k in keys)
        {
            foreach (var q in queries)
                sb.Append("page ").Append(k).Append(' ').Append(q).Append(": ").Append(JsonSerializer.Serialize(b.Page(k, q))).Append('\n');
            var first = b.Page(k, new MemoryListQuery(null, "id", "asc", "all", null, 0, 3));
            foreach (var row in first?.Items ?? [])
                sb.Append("detail ").Append(k).Append(' ').Append(row.Id).Append(": ").Append(JsonSerializer.Serialize(b.Detail(k, row.Id))).Append('\n');
            sb.Append("detail ").Append(k).Append(" missing: ").Append(JsonSerializer.Serialize(b.Detail(k, "999999"))).Append('\n');
        }
        return Mask(sb.ToString());
    }

    [Fact]
    public async Task Browser_matches_the_golden_hash()
    {
        var dump = await Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "memory-browser.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(MemoryBrowserGoldenData.Expected, $"{lines} {hash}");
    }
}

internal static class MemoryBrowserGoldenData
{
    public const string Expected = "184 77D14ABCD354E183C19D44B86079545D3E0E4327750D506943AB43324CB5A0CD";
}
