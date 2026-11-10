using System.Text;
using System.Text.Json;
using MineClaw.ViewerBridge.Supervisor;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The Design Studio passthrough: reads with any role, writes with design or admin, the designer's identity from the signed headers.</summary>
public class DesignerProxyTests
{
    [Fact]
    public async Task Large_signed_design_picture_reaches_supervisor_but_other_routes_keep_the_small_body_cap()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/designer/sessions/17/save"] = _ => FakeSupervisor.Json("""{"design_id":5}""");
        string picture = "data:image/png;base64," + new string('a', 1_000_000);
        string body = JsonSerializer.Serialize(new { name = "Detailed storehouse", preview_png = picture });
        var (status, _) = await h.PostAsync("/v1/sup/designer/sessions/17/save", body, roles: "design", player: "builder");
        Assert.Equal(200, status);
        var sent = Assert.Single(h.Sup.Seen, s => s.Target == "/api/designer/sessions/17/save");
        using var doc = JsonDocument.Parse(sent.Body);
        Assert.Equal(picture, doc.RootElement.GetProperty("preview_png").GetString());
        Assert.Equal("builder", doc.RootElement.GetProperty("player").GetString());

        Assert.Equal(413, (await h.PostAsync("/v1/sup/designer/sessions/17/canvas", body, roles: "design")).Status);
        Assert.Equal(413, (await h.PostAsync("/v1/sup/admin/memory", body, roles: "admin")).Status);
        Assert.Equal(413, (await h.PostAsync("/v1/sup/designer/sessions/17/save", new string('a', (int)BridgeAuthMiddleware.DesignerSaveBodyLimit + 1), roles: "design")).Status);
    }

    /// <summary>
    /// The browser puts at most 200 000 bytes of edits into one canvas request (<c>DESIGNER_CANVAS_BATCH_BYTES</c> in
    /// web/packages/contracts/src/designer.ts, with 32 768 bytes of room demanded up to the cap): a request of that size has to
    /// pass, or the studio's edits stop reaching the bot.
    /// </summary>
    [Fact]
    public async Task A_canvas_request_as_large_as_the_browser_sends_reaches_the_supervisor()
    {
        const int batchBytes = 200_000;
        Assert.True(batchBytes + 32_768 < BridgeAuthMiddleware.JsonBodyLimit);

        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/designer/sessions/17/canvas"] = _ => FakeSupervisor.Json("""{"rev":1,"applied":9000,"stale":false,"fixed":[]}""");
        const string head = """{"rev":0,"ops":[{"kind":"dig","label":"a floor","cells":[""", cell = "[255,127,255,252,15]", tail = "]}]}";
        var sb = new StringBuilder(head);
        int cells = 0;
        for (; sb.Length + cell.Length + 1 + tail.Length <= batchBytes; cells++) sb.Append(cells > 0 ? "," : "").Append(cell);
        string body = sb.Append(' ', batchBytes - sb.Length - tail.Length).Append(tail).ToString();
        Assert.Equal(batchBytes, Encoding.UTF8.GetByteCount(body));

        var (status, _) = await h.PostAsync("/v1/sup/designer/sessions/17/canvas", body, roles: "design", player: "builder");
        Assert.Equal(200, status);
        var sent = Assert.Single(h.Sup.Seen, s => s.Target == "/api/designer/sessions/17/canvas");
        using var doc = JsonDocument.Parse(sent.Body);
        Assert.Equal(cells, doc.RootElement.GetProperty("ops")[0].GetProperty("cells").GetArrayLength());
        Assert.Equal("builder", doc.RootElement.GetProperty("player").GetString());
    }

    [Theory]
    [InlineData("/v1/sup/designer/sessions/1/save", true)]
    [InlineData("/v1/sup/designer/sessions/1/canvas", false)]
    [InlineData("/v1/sup/designer/sessions/1/messages", false)]
    [InlineData("/v1/sup/designer/sessions/0/save", false)]
    [InlineData("/v1/sup/designer/sessions/-1/save", false)]
    [InlineData("/v1/sup/designer/sessions/x/save", false)]
    [InlineData("/v1/sup/designer/sessions/1/other/save", false)]
    [InlineData("/v1/sup/designer/sessions/1/save/other", false)]
    [InlineData("/v1/sup/designer/designs/1/delete", false)]
    public void Only_the_exact_save_route_gets_the_picture_body_limit(string path, bool save) =>
        Assert.Equal(save ? BridgeAuthMiddleware.DesignerSaveBodyLimit : BridgeAuthMiddleware.JsonBodyLimit, BridgeAuthMiddleware.BodyLimitFor(path));

    [Fact]
    public async Task Designer_reads_pass_writes_need_design_or_admin_and_get_the_signed_identity()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/designer/models"] = _ => FakeSupervisor.Json("""{"models":[],"default":null,"version":"b1.8.1","budget":null}""");
        h.Sup.Routes["/api/designer/sessions"] = _ => FakeSupervisor.Json("""{"id":1}""");
        h.Sup.Routes["/api/designer/sessions/1/save"] = _ => FakeSupervisor.Json("""{"design_id":5}""");
        h.Sup.Routes["/api/designer/sessions/1/messages"] = _ => FakeSupervisor.Json("""{"error":"the session is busy"}""", 409);

        Assert.Equal(200, (await h.GetAsync("/v1/sup/designer/models", roles: "viewer")).Status);
        Assert.Equal(403, (await h.PostAsync("/v1/sup/designer/sessions", "{}", roles: "chat,commands,viewer")).Status);
        Assert.DoesNotContain(h.Sup.Seen, s => s.Method == "POST" && s.Target.StartsWith("/api/designer", StringComparison.Ordinal));
        // A client can't say who it is.
        Assert.Equal(400, (await h.PostAsync("/v1/sup/designer/sessions", """{"player":"kiwi"}""", roles: "design,viewer")).Status);
        Assert.Equal(400, (await h.PostAsync("/v1/sup/designer/sessions", """{"who":"x"}""", roles: "admin")).Status);

        var (status, _) = await h.PostAsync("/v1/sup/designer/sessions", """{"model":"main"}""", roles: "design,viewer", user: "robin", player: "kiwi");
        Assert.Equal(200, status);
        using (var doc = JsonDocument.Parse(h.Sup.Seen.Last(s => s.Target == "/api/designer/sessions").Body))
        {
            Assert.Equal("kiwi", doc.RootElement.GetProperty("player").GetString());
            Assert.Equal("robin (web)", doc.RootElement.GetProperty("who").GetString());
            Assert.Equal("main", doc.RootElement.GetProperty("model").GetString());
        }
        Assert.Equal(200, (await h.PostAsync("/v1/sup/designer/sessions/1/save", """{"name":"hut"}""", roles: "admin")).Status);
        // A busy session (a message still running) becomes a retryable 429.
        var (busy, body) = await h.PostAsync("/v1/sup/designer/sessions/1/messages", """{"text":"hi","canvas":{"rev":0}}""", roles: "design");
        Assert.Equal(429, busy);
        Assert.Contains("rate_limited", body);
        Assert.True(SupervisorProxy.Allowed("POST", "designer/sessions/1/save"));
        Assert.True(SupervisorProxy.Allowed("GET", "designer/models"));
        Assert.Equal(TimeSpan.FromSeconds(240), DesignerProxy.TimeoutFor("designer/sessions/1/messages"));
    }
}
