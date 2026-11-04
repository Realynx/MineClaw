using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins the bridge's two upstream-error mappers (WP07): <see cref="SettingsProxy.WriteErrorAsync"/> and
/// <see cref="SupervisorProxy.MapErrorAsync"/>, over every status they name and some they don't, bodies with an error, per-key
/// errors, a retry hint, URL- and token-like messages, long and multi-line messages, non-JSON and non-object bodies, and a
/// Retry-After header. Each reply's status, content type, cache and retry headers and body are dumped; the test keeps the dump's
/// line count and SHA-256 (WP07_GOLDEN_OUT writes the dump for a diff).
/// </summary>
public class ProxyErrorGoldenTests
{
    private static readonly int[] Statuses = [400, 401, 403, 404, 405, 409, 413, 418, 429, 500, 502, 503];

    private static readonly string[] Bodies =
    [
        "",
        "not json",
        "[1,2]",
        "{}",
        "{\"error\":\"bad value\"}",
        "{\"error\":42}",
        "{\"error\":\"see https://example.org/x\"}",
        "{\"error\":\"oops token=abc\"}",
        "{\"error\":\"line one\\nline two\"}",
        "{\"error\":\"" + new string('e', 350) + "\"}",
        "{\"error\":\"the bot is busy\",\"retry_after_ms\":2500}",
        "{\"error\":\"slow down\",\"retry_after_ms\":\"soon\"}",
        "{\"error\":\"invalid\",\"errors\":[{\"key\":\"Agent:Llm:MainModel\",\"message\":\"unknown model\"},{\"key\":7,\"message\":null},{\"message\":\"http://x\"},3]}",
        "{\"ok\":false,\"error\":\"store refused\"}",
    ];

    private static async Task<string> Reply(Func<HttpContext, Task> write)
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        try { await write(ctx); }
        catch (Exception ex) { return "throws " + ex.GetType().Name; } // e.g. a non-numeric retry_after_ms upstream (kept as it is)
        var h = ctx.Response.Headers;
        return $"{ctx.Response.StatusCode} [{ctx.Response.ContentType}] cache[{h.CacheControl}] retry[{h.RetryAfter}] {Encoding.UTF8.GetString(body.ToArray())}";
    }

    private static HttpResponseMessage Upstream(int status, string body, TimeSpan? retryAfter)
    {
        var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        if (retryAfter is { } d) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(d);
        return res;
    }

    internal static async Task<string> Dump()
    {
        var sb = new StringBuilder();
        foreach (var status in Statuses)
            foreach (var body in Bodies)
                foreach (var retry in new TimeSpan?[] { null, TimeSpan.FromSeconds(3.2) })
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var label = $"{status} {(body.Length > 40 ? body[..40] + "…" : body)} retry={retry?.TotalSeconds}";
                    sb.Append("settings ").Append(label).Append(" -> ").Append(await Reply(ctx => SettingsProxy.WriteErrorAsync(ctx, status, bytes, retry))).Append('\n');
                    foreach (var post in new[] { false, true })
                        sb.Append("proxy post=").Append(post).Append(' ').Append(label).Append(" -> ")
                            .Append(await Reply(ctx => SupervisorProxy.MapErrorAsync(ctx, Upstream(status, body, retry), "x", post))).Append('\n');
                }
        return sb.ToString();
    }

    [Fact]
    public async Task Error_mappers_match_the_golden_hash()
    {
        var dump = await Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "proxy-errors.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(ProxyErrorGoldenData.Expected, $"{lines} {hash}");
    }
}
