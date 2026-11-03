using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MineClaw.Render;
using MineClaw.ViewerBridge.Data;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>Shards, groom, script diff.</summary>
public class ToolsTests
{
    [Fact]
    public async Task Shard_export_streams_a_zip_and_import_preview_diffs_it_read_only()
    {
        var fx = await FixtureDb.SharedAsync();
        await using var h = await BridgeHost.StartAsync(fx.Path, new HashEmbeddings());
        var req = h.Signed("POST", "/v1/shards/export", Encoding.UTF8.GetBytes("""{"name":"Fox facts","selection":{"mode":"filter","kinds":["fact","place"],"include_world":true}}"""));
        using var res = await h.SendAsync(req);
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        Assert.Matches("^attachment; filename=\"fox-facts-\\d{8}-\\d{4}\\.mcshard\"$", res.Content.Headers.ContentDisposition?.ToString() ?? "");
        var shard = await res.Content.ReadAsByteArrayAsync();
        var manifest = MineClaw.Agent.Admin.Shards.ShardFile.Read(shard).Manifest;
        Assert.Equal("fox (web)", manifest.CreatedBy);

        var boundary = "----mcx" + Guid.NewGuid().ToString("N");
        // Nest re-encodes the options part in snake_case.
        var body = Multipart(boundary, shard, """{"mode":"merge","conflict":"keep-newer","include_rules":false,"include_world":true,"max_usd":0.25}""");
        var viewer = h.Signed("POST", "/v1/shards/import/preview", body, roles: "viewer", contentType: $"multipart/form-data; boundary={boundary}");
        using (var forbidden = await h.SendAsync(viewer)) Assert.Equal(403, (int)forbidden.StatusCode);
        var admin = h.Signed("POST", "/v1/shards/import/preview", body, roles: "admin,viewer", contentType: $"multipart/form-data; boundary={boundary}");
        using var preview = await h.SendAsync(admin);
        var text = await preview.Content.ReadAsStringAsync();
        Assert.True(preview.IsSuccessStatusCode, text);
        var diff = JsonDocument.Parse(text).RootElement.GetProperty("diff");
        Assert.Equal("merge", diff.GetProperty("mode").GetString());
        Assert.True(diff.GetProperty("unchanged").GetInt32() > 0); // the shard came from this db

        var bad = Multipart(boundary, shard, """{"mode":"merge","who":"mallory"}""");
        using var smuggled = await h.SendAsync(h.Signed("POST", "/v1/shards/import/preview", bad, roles: "admin", contentType: $"multipart/form-data; boundary={boundary}"));
        Assert.Equal(400, (int)smuggled.StatusCode);
    }

    private static byte[] Multipart(string boundary, byte[] shard, string options)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        W($"--{boundary}\r\nContent-Disposition: form-data; name=\"options\"\r\nContent-Type: application/json\r\n\r\n{options}\r\n");
        W($"--{boundary}\r\nContent-Disposition: form-data; name=\"shard\"; filename=\"x.mcshard\"\r\nContent-Type: application/zip\r\n\r\n");
        ms.Write(shard);
        W($"\r\n--{boundary}--\r\n");
        return ms.ToArray();
    }

    [Fact]
    public async Task Groom_preview_runs_the_dry_run_chain_for_admins()
    {
        var fx = await FixtureDb.SharedAsync();
        await using var h = await BridgeHost.StartAsync(fx.Path, new HashEmbeddings());
        var (s, body) = await h.PostAsync("/v1/groom/preview", """{"source":"paste","text":"Fox placed 12 cobblestone at the gen"}""", roles: "admin");
        Assert.True(s == 200, body);
        var r = JsonDocument.Parse(body).RootElement;
        Assert.True(r.GetProperty("in_count").GetInt32() >= 0);
        Assert.Equal(403, (await h.PostAsync("/v1/groom/preview", """{"source":"paste","text":"x"}""", roles: "viewer")).Status);
        Assert.Equal(400, (await h.PostAsync("/v1/groom/preview", """{"source":"paste","text":"x","extra":1}""", roles: "admin")).Status);
    }

    [Fact]
    public void Script_diff_folds_hunks_with_context()
    {
        var a = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var b = a.Replace("line 10", "line ten") + "\nline 21";
        var d = ScriptDiff.Build("loop", 1, 2, a, b);
        Assert.Equal((2, 1), (d.Added, d.Removed));
        Assert.Equal(2, d.Hunks.Count);
        Assert.Equal(7, d.Hunks[0].ALines); // 3 context + the change + 3 context
        Assert.StartsWith("--- v1\n+++ v2\n@@ -7,7 +7,7 @@", d.Unified);
    }
}
