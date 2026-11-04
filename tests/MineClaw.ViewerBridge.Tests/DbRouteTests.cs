using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The bridge-native db routes over a real fixture db opened read-only, server-paged; shapes checked against the contracts.</summary>
public class DbRouteTests(ITestOutputHelper output)
{
    private static async Task<BridgeHost> Host() => await BridgeHost.StartAsync((await FixtureDb.SharedAsync()).Path, new HashEmbeddings());

    private static JsonElement J(string body) => JsonDocument.Parse(body).RootElement.Clone();

    /// <summary>The graph's top-level nodes (no parent), highest layer first.</summary>
    private static List<int> TopNodes(JsonElement graph) => graph.GetProperty("nodes").EnumerateArray()
        .Where(n => !n.TryGetProperty("parent", out var p) || p.ValueKind == JsonValueKind.Null)
        .OrderByDescending(n => n.GetProperty("layer").GetInt32()).ThenBy(n => n.GetProperty("id").GetInt32()).Select(n => n.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task Memory_lists_are_paged_filtered_and_sorted_in_sql()
    {
        await using var h = await Host();
        var (s, body) = await h.GetAsync("/v1/memory/chat?limit=100&offset=0");
        Assert.Equal(200, s);
        var page = J(body);
        Assert.Equal(250, page.GetProperty("total").GetInt32());
        Assert.Equal(100, page.GetProperty("items").GetArrayLength());
        Assert.Equal("line 249 hello", page.GetProperty("items")[0].GetProperty("preview").GetString()); // newest first

        var last = J((await h.GetAsync("/v1/memory/chat?limit=100&offset=200")).Body);
        Assert.Equal(50, last.GetProperty("items").GetArrayLength());

        var filtered = J((await h.GetAsync("/v1/memory/chat?q=cobble&sort=id&dir=asc&limit=5")).Body);
        Assert.Equal(25, filtered.GetProperty("total").GetInt32());
        Assert.Equal("1", filtered.GetProperty("items")[0].GetProperty("id").GetString());

        Assert.Equal(400, (await h.GetAsync("/v1/memory/chat?limit=501")).Status);
        Assert.Equal(400, (await h.GetAsync("/v1/memory/chat?sort=bogus")).Status);
        Assert.Equal(404, (await h.GetAsync("/v1/memory/bogus")).Status);

        var places = J((await h.GetAsync("/v1/memory/places?dim=nether")).Body);
        Assert.Equal(1, places.GetProperty("total").GetInt32());
        Assert.Equal("nether hub", places.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("nether", places.GetProperty("items")[0].GetProperty("dim").GetString());
        Assert.Equal("wp:nether hub", places.GetProperty("items")[0].GetProperty("doc_ref").GetString());
    }

    [Fact]
    public async Task Kinds_count_every_table_and_details_carry_record_props_and_prefill()
    {
        await using var h = await Host();
        var kinds = J((await h.GetAsync("/v1/memory/kinds")).Body).GetProperty("kinds");
        Assert.Equal(16, kinds.GetArrayLength());
        var facts = kinds.EnumerateArray().First(k => k.GetProperty("key").GetString() == "facts");
        Assert.Equal(2, facts.GetProperty("count").GetInt32());
        Assert.Equal("fact", facts.GetProperty("edit_kind").GetString());

        var list = J((await h.GetAsync("/v1/memory/facts")).Body);
        var id = list.GetProperty("items")[0].GetProperty("id").GetString()!;
        var (s, body) = await h.GetAsync($"/v1/memory/facts/{id}");
        Assert.Equal(200, s);
        var d = J(body);
        Assert.Equal("facts", d.GetProperty("record").GetProperty("kind").GetString());
        Assert.Equal("fact", d.GetProperty("edit_kind").GetString());
        Assert.True(d.GetProperty("edit_prefill").TryGetProperty("subject", out _));
        Assert.True(d.GetProperty("props").GetArrayLength() > 0);

        var st = J((await h.GetAsync("/v1/memory/structures/cobble%20gen")).Body);
        Assert.Equal("stand at 3,65,-2", st.GetProperty("edit_prefill").GetProperty("steps").GetString());
        Assert.Equal(JsonValueKind.Object, st.GetProperty("record").GetProperty("data").ValueKind);
        Assert.Equal(404, (await h.GetAsync("/v1/memory/facts/999999")).Status);
    }

    [Fact]
    public async Task Search_tree_node_leaves_and_graph_answer_from_the_fixture()
    {
        await using var h = await Host();
        var fx = await FixtureDb.SharedAsync();
        var search = J((await h.GetAsync("/v1/search?q=cobblestone%20generator&k=10")).Body);
        Assert.Equal("hybrid", search.GetProperty("mode").GetString());
        Assert.True(search.GetProperty("embedded").GetBoolean());
        Assert.True(search.GetProperty("hits").GetArrayLength() > 0);
        var lexical = J((await h.GetAsync("/v1/search?q=cobblestone&mode=lexical")).Body);
        Assert.Equal("lexical", lexical.GetProperty("mode").GetString());

        var top = TopNodes(J((await h.GetAsync("/v1/raptor/graph")).Body));
        Assert.True(top.Count > 0);
        var root = top[0];
        var leaves = J((await h.GetAsync($"/v1/raptor/node/{root}/leaves")).Body);
        Assert.True(leaves.GetProperty("leaves").GetArrayLength() > 0);
        var node = J((await h.GetAsync($"/v1/raptor/node/{root}")).Body);
        Assert.Equal(root, node.GetProperty("node").GetProperty("index").GetInt32());

        foreach (var layout in new[] { "icicle", "radial", "nodelink" })
        {
            var g = J((await h.GetAsync($"/v1/raptor/graph?layout={layout}&color_by=cluster")).Body);
            Assert.Equal(fx.TreeNodes, g.GetProperty("nodes").GetArrayLength());
            foreach (var n in g.GetProperty("nodes").EnumerateArray())
            {
                Assert.InRange(n.GetProperty("x").GetDouble(), 0, 1);
                Assert.InRange(n.GetProperty("y").GetDouble(), 0, 1);
                Assert.Matches("^#[0-9a-f]{6}$", n.GetProperty("color").GetString()!);
            }
        }
        var focused = J((await h.GetAsync($"/v1/raptor/graph?focus={root}")).Body);
        Assert.True(focused.GetProperty("nodes").GetArrayLength() < fx.TreeNodes || top.Count == 1);
        Assert.Equal(root, focused.GetProperty("focus").GetInt32());
    }

    [Fact]
    public async Task Designs_list_detail_diff_and_picture()
    {
        await using var h = await Host();
        var list = J((await h.GetAsync("/v1/designs")).Body);
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(2, list.GetProperty("items")[0].GetProperty("revisions").GetInt32());
        var detail = J((await h.GetAsync("/v1/designs/1")).Body);
        Assert.Equal("1", detail.GetProperty("default_diff").GetProperty("a").GetString());
        var diff = J((await h.GetAsync("/v1/designs/1/diff?a=1&b=2")).Body);
        Assert.Equal(1, diff.GetProperty("added").GetInt32());
        Assert.Equal(1, diff.GetProperty("removed").GetInt32());
        using var pic = await h.SendAsync(h.Signed("GET", "/v1/designs/1/picture.png"));
        Assert.Equal("image/png", pic.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, (await h.GetAsync("/v1/designs/1/diff?a=x")).Status);
    }

    [Fact]
    public async Task The_db_is_never_written()
    {
        var fx = await FixtureDb.SharedAsync();
        long before = new FileInfo(fx.Path).Length;
        var stamp = File.GetLastWriteTimeUtc(fx.Path);
        await using (var h = await Host())
        {
            await h.GetAsync("/v1/overview");
            await h.GetAsync("/v1/search?q=wheat");
            await h.GetAsync("/v1/raptor/graph");
            await h.GetAsync("/v1/memory/facts");
        }
        Assert.Equal(before, new FileInfo(fx.Path).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(fx.Path));
    }

    [Fact]
    public async Task Missing_db_is_503_not_a_crash()
    {
        await using var h = await BridgeHost.StartAsync();
        var (s, body) = await h.GetAsync("/v1/overview");
        Assert.Equal(503, s);
        Assert.Contains("upstream_unavailable", body);
    }

    /// <summary>Every response shape is parsed by the real zod schemas (node + the built contracts package), as Nest parses it.</summary>
    [Fact]
    public async Task Responses_parse_with_the_contract_schemas()
    {
        var dist = ContractDist();
        if (dist == null || !NodeAvailable()) { output.WriteLine("skipped: node or web/packages/contracts/dist missing (npm run build -w @mineclaw/contracts)"); return; }
        await using var h = await Host();
        int root = TopNodes(J((await h.GetAsync("/v1/raptor/graph")).Body))[0];
        var cloud = h.Services.GetRequiredService<MineClaw.ViewerBridge.Graph.MemoryCloudService>();
        await h.GetAsync("/v1/memory-activity/cloud");
        await cloud.Idle();
        var factId = J((await h.GetAsync("/v1/memory/facts")).Body).GetProperty("items")[0].GetProperty("id").GetString();
        var checks = new List<(string Schema, string Target)>
        {
            ("Overview", "/v1/overview"), ("MemoryKindsResponse", "/v1/memory/kinds"), ("MemoryPage", "/v1/memory/facts"), ("MemoryPage", "/v1/memory/chat?limit=3"),
            ("MemoryPage", "/v1/memory/places"), ("MemoryPage", "/v1/memory/episodes?limit=5"), ("MemoryPage", "/v1/memory/structures"),
            ("MemoryDetail", $"/v1/memory/facts/{factId}"), ("MemoryDetail", "/v1/memory/structures/cobble%20gen"), ("MemoryDetail", "/v1/memory/places/spawn"),
            ("MemoryDetail", "/v1/memory/episodes/ep:fixture:3"), ("MemoryDetail", "/v1/memory/chat/5"),
            ("SearchResponse", "/v1/search?q=cobblestone%20generator"), ("SearchResponse", "/v1/search?q=wheat&mode=lexical&kind=episode"),
            ("MemoryCloudResponse", "/v1/memory-activity/cloud"), ("RaptorNodeDetail", $"/v1/raptor/node/{root}"), ("RaptorLeavesResponse", $"/v1/raptor/node/{root}/leaves"),
            ("RaptorGraph", "/v1/raptor/graph"), ("RaptorGraph", "/v1/raptor/graph?layout=radial&color_by=time"), ("RaptorGraph", $"/v1/raptor/graph?layout=nodelink&focus={root}"),
            ("PersonalityResponse", "/v1/personality"), ("ShardPacksResponse", "/v1/shards/packs"), ("ScriptLibraryResponse", "/v1/scripts?source=db"),
            ("DesignListResponse", "/v1/designs"), ("DesignDetail", "/v1/designs/1"), ("DesignDiff", "/v1/designs/1/diff?a=1&b=2"),
            ("EditSchemaResponse", "/v1/admin/schema"), ("MemoryHeatResponse", "/v1/memory-activity/heat"), ("BotNow", "/v1/sup/viewer/now?bot=maid_chan"),
            ("BodyInventory", "/v1/sup/viewer/bodies/maid_chan/inventory"),
            ("ContainerView", "/v1/sup/world/containers?dim=0&x=6&y=74&z=-4"),
        };
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-bridge-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var args = new List<string> { Path.Combine(AppContext.BaseDirectory, "contract-check.mjs"), dist };
        int i = 0;
        foreach (var (schema, target) in checks)
        {
            var (s, body) = await h.GetAsync(target);
            Assert.True(s == 200, $"{target} answered {s}: {body}");
            var f = Path.Combine(dir, $"{i++:00}-{schema}.json");
            await File.WriteAllTextAsync(f, body);
            args.Add(schema);
            args.Add(f);
        }
        var (ps, pb) = await h.PostAsync("/v1/shards/export/preview", """{"name":"facts","selection":{"mode":"filter","kinds":["fact"]}}""");
        Assert.True(ps == 200, pb);
        var pf = Path.Combine(dir, "preview.json");
        await File.WriteAllTextAsync(pf, pb);
        args.AddRange(["ShardPreview", pf]);
        var (cs, cb) = await h.PostAsync("/v1/sup/viewer/chat", """{"bot":"maid_chan","text":"hi","request_id":"r-12345678"}""");
        Assert.Equal(202, cs);
        var cf = Path.Combine(dir, "chat.json");
        await File.WriteAllTextAsync(cf, cb);
        args.AddRange(["ChatAccepted", cf]);

        var (code, text) = Run("node", args);
        output.WriteLine(text);
        Assert.True(code == 0, text);
    }

    internal static string? ContractDist()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MineClaw.sln")))
            {
                var f = Path.Combine(d.FullName, "web", "packages", "contracts", "dist", "index.js");
                return File.Exists(f) ? f : null;
            }
        return null;
    }

    internal static bool NodeAvailable()
    {
        try { return Run("node", ["--version"]).Code == 0; }
        catch (Exception) { return false; }
    }

    internal static (int Code, string Output) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60_000);
        return (p.ExitCode, o.Result + e.Result);
    }
}
