using System.Text;
using System.Text.Json.Nodes;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>
/// WP07's golden-dump helpers for the supervisor routes: one line per request with the status, the headers and the body (whole,
/// or only its top-level keys when it is long or comes from state another package owns). Line endings are normalised, so a
/// CRLF checkout compares equal. Regenerate a dump (only for an intended change) by setting WP07_GOLDEN_OUT to a directory.
/// </summary>
internal static class RouteGolden
{
    public static Dictionary<string, string> Query(string? query)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query == null) return q;
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            q[i < 0 ? kv : kv[..i]] = i < 0 ? "" : Uri.UnescapeDataString(kv[(i + 1)..]);
        }
        return q;
    }

    /// <summary>One reply as a line; <paramref name="maxBody"/> longer bodies (or every 2xx body with keysOnly) print their keys.</summary>
    public static void Append(StringBuilder sb, string label, SupervisorReply? r, int maxBody = 400, bool keysOnly = false)
    {
        sb.Append(label).Append(" -> ");
        if (r == null) { sb.Append("null\n"); return; }
        sb.Append(r.Status).Append(' ').Append(r.ContentType);
        if (r.Headers != null)
            foreach (var kv in r.Headers.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(" [").Append(kv.Key).Append(": ").Append(kv.Value).Append(']');
        if (!r.ContentType.StartsWith("application/json", StringComparison.Ordinal) && !r.ContentType.StartsWith("text/", StringComparison.Ordinal))
        {
            sb.Append(" <").Append(r.Body.Length).Append(" bytes ").Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(r.Body))[..12]).Append(">\n");
            return;
        }
        var text = r.Text;
        bool ok = r.Status is >= 200 and < 300;
        if ((keysOnly && ok) || text.Length > maxBody) sb.Append(" keys ").Append(Keys(text));
        else sb.Append(' ').Append(text);
        sb.Append('\n');
    }

    public static string Keys(string json)
    {
        try
        {
            return JsonNode.Parse(json) switch
            {
                JsonObject o => "{" + string.Join(",", o.Select(kv => kv.Key)) + "}",
                JsonArray a => $"[{a.Count}]",
                var n => n?.ToJsonString() ?? "null",
            };
        }
        catch (System.Text.Json.JsonException) { return $"<{json.Length} chars>"; }
    }

    public static void Check(string name, string expected, string actual)
    {
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, name + ".txt"), actual);
        Assert.Equal(expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
