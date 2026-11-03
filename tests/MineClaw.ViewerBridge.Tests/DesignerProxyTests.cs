using System.Text.Json;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The Design Studio passthrough: reads with any role, writes with design or admin, the designer's identity from the signed headers.</summary>
public class DesignerProxyTests
{
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
