using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MineClaw.Render;
using MineClaw.ViewerBridge.Atlas;
using MineClaw.ViewerBridge.Data;
using Xunit.Abstractions;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The runtime atlas (no Mojang assets: with none on disk it is the procedural cells + flat colours), shards, groom, diff.</summary>
public class AtlasAndToolsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public async Task Atlas_png_and_json_describe_every_state_and_are_cacheable(string version)
    {
        await using var h = await BridgeHost.StartAsync();
        using var png = await h.SendAsync(h.Signed("GET", $"/v1/world/atlas.png?version={version}"));
        Assert.Equal(200, (int)png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Contains("max-age=86400", png.Headers.CacheControl?.ToString());
        var tag = png.Headers.ETag!.Tag;
        Assert.StartsWith($"\"{version}-", tag);
        var bytes = await png.Content.ReadAsByteArrayAsync();
        var (w, hgt, _) = Png.Decode(bytes);

        using var json = await h.SendAsync(h.Signed("GET", $"/v1/world/atlas.json?version={version}"));
        var jsonText = await json.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(jsonText).RootElement;
        Assert.Equal("mineclaw-atlas", root.GetProperty("format").GetString());
        Assert.Equal(version, root.GetProperty("version").GetString());
        Assert.Equal(w, root.GetProperty("width").GetInt32());
        Assert.Equal(hgt, root.GetProperty("height").GetInt32());
        Assert.Equal(hgt, root.GetProperty("rows").GetInt32() * root.GetProperty("tile_size").GetInt32());
        var states = root.GetProperty("states");
        Assert.Equal("cube", states.GetProperty("1").GetProperty("shape").GetString()); // stone
        Assert.Equal(6, states.GetProperty("1").GetProperty("faces").GetArrayLength());
        Assert.Equal("fluid", states.GetProperty("9").GetProperty("shape").GetString());
        Assert.Equal("quads", states.GetProperty("50").GetProperty("shape").GetString()); // torch
        Assert.True(states.GetProperty("50").GetProperty("emissive").GetBoolean());
        Assert.False(states.TryGetProperty("0", out _));
        output.WriteLine($"{version}: {w}x{hgt}, {bytes.Length / 1024} KB png, {jsonText.Length / 1024} KB json, textured {root.GetProperty("textured").GetBoolean()}, {states.EnumerateObject().Count()} state keys");

        var again = h.Signed("GET", $"/v1/world/atlas.png?version={version}");
        again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(tag));
        using var notModified = await h.SendAsync(again);
        Assert.Equal(304, (int)notModified.StatusCode);
        Assert.Equal(400, (await h.GetAsync("/v1/world/atlas.json?version=1.7.10")).Status);
    }

    [Fact]
    public async Task Atlas_json_parses_with_the_contract()
    {
        var dist = DbRouteTests.ContractDist();
        if (dist == null || !DbRouteTests.NodeAvailable()) return;
        await using var h = await BridgeHost.StartAsync();
        var f = Path.Combine(Path.GetTempPath(), $"atlas-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(f, (await h.GetAsync("/v1/world/atlas.json")).Body);
        var (code, text) = DbRouteTests.Run("node", [Path.Combine(AppContext.BaseDirectory, "contract-check.mjs"), dist, "WorldAtlasJson", f]);
        Assert.True(code == 0, text);
    }

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
        var body = Multipart(boundary, shard, """{"mode":"merge","conflict":"keep-newer"}""");
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
