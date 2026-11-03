using System.Text.Json;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The Administration node proxy: admin only, the signed identity forwarded to the bot as headers, errors mapped.</summary>
public class SettingsProxyTests
{
    private const string Settings = """{"node":{"id":"local","name":"maid_chan","version":"1.0"},"groups":[],"settings":[],"allowed_claude_models":["claude-opus-5-5"],"restart_pending":[],"override":{"path":"/var/lib/mineclaw/settings.override.json","exists":false,"backups":0}}""";

    [Fact]
    public async Task Admin_routes_need_the_admin_role_and_forward_the_identity()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/admin/settings"] = req => req.Method == "PUT"
            ? FakeSupervisor.Json("""{"applied":["Supervisor:Viewer:PerMinute"],"restart_required":[],"backup":null}""")
            : FakeSupervisor.Json(Settings);
        h.Sup.Routes["/api/admin/settings/node"] = _ => FakeSupervisor.Json("""{"id":"local","name":"maid_chan","version":"1.0"}""");
        h.Sup.Routes["/api/admin/ollama/models"] = req => FakeSupervisor.Json($$"""{"endpoint":"{{req.Query["endpoint"]}}","ok":true,"models":[{"name":"qwen3:8b"}]}""");

        // Not admin: refused at the bridge, nothing reaches the bot.
        Assert.Equal(403, (await h.GetAsync("/v1/admin/nodes/local/settings", roles: "viewer,chat,commands,story")).Status);
        Assert.Equal(403, (await h.PostAsync("/v1/admin/nodes/local/settings", """{"changes":[],"reason":"x"}""", roles: "design,story")).Status);
        Assert.Equal(403, (await h.GetAsync("/v1/admin/nodes", roles: "viewer")).Status);
        Assert.DoesNotContain(h.Sup.Seen, s => s.Target.StartsWith("/api/admin/", StringComparison.Ordinal));

        var (status, body) = await h.GetAsync("/v1/admin/nodes/local/settings", roles: "admin,viewer");
        Assert.Equal(200, status);
        using (var doc = JsonDocument.Parse(body))
        {
            var node = doc.RootElement.GetProperty("node");
            Assert.Equal("local", node.GetProperty("id").GetString());
            Assert.Equal(h.Sup.Url, node.GetProperty("supervisor_url").GetString());
            Assert.True(node.GetProperty("reachable").GetBoolean());
        }
        var seen = h.Sup.Seen.Last(s => s.Target == "/api/admin/settings");
        Assert.Equal("fox", seen.User);
        Assert.Equal("admin,viewer", seen.Roles);
        Assert.NotNull(seen.Token);

        // Save: POST at the bridge, PUT at the bot; the body passes unchanged, the identity rides in headers only.
        var save = """{"changes":[{"key":"Agent:Llm:ApiKey","value":"sk-ant-secret-1234567890"}],"reason":"rotate"}""";
        Assert.Equal(200, (await h.PostAsync("/v1/admin/nodes/local/settings", save, roles: "admin", user: "fox")).Status);
        var put = h.Sup.Seen.Last(s => s.Method == "PUT");
        Assert.Equal("/api/admin/settings", put.Target);
        Assert.Equal("fox", put.User);
        Assert.DoesNotContain("who", put.Body);
        Assert.Equal(400, (await h.PostAsync("/v1/admin/nodes/local/settings", """{"changes":[],"reason":"x","who":"mallory"}""", roles: "admin")).Status);
        // The bridge's own audit never holds the body (the key above).
        Assert.DoesNotContain("sk-ant-secret", await File.ReadAllTextAsync(h.AuditLog));

        // Queries pass through; unknown nodes and paths are 404.
        Assert.Equal(200, (await h.GetAsync("/v1/admin/nodes/local/ollama/models?endpoint=http%3A%2F%2F10.1.2.5%3A11434", roles: "admin")).Status);
        Assert.Contains(h.Sup.Seen, s => s.Target == "/api/admin/ollama/models?endpoint=http%3A%2F%2F10.1.2.5%3A11434");
        Assert.Equal(404, (await h.GetAsync("/v1/admin/nodes/other/settings", roles: "admin")).Status);
        Assert.Equal(404, (await h.GetAsync("/v1/admin/nodes/local/secrets", roles: "admin")).Status);

        var nodes = await h.GetAsync("/v1/admin/nodes", roles: "admin");
        Assert.Equal(200, nodes.Status);
        Assert.Contains("\"maid_chan\"", nodes.Body);
    }

    [Fact]
    public async Task Bot_errors_become_api_errors_with_per_key_details()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/admin/settings"] = _ => FakeSupervisor.Json(
            """{"error":"2 settings need attention; nothing was saved","errors":[{"key":"Agent:Llm:MainModel","message":"not allowed"},{"key":"Hive:MaxClones","message":"at most 500"}]}""", 400);
        h.Sup.Routes["/api/admin/restart"] = _ => FakeSupervisor.Json("""{"error":"not safe to restart now: maid_chan_101 is outside at night under open sky","safety":{"safe":false}}""", 409);
        var (status, body) = await h.PostAsync("/v1/admin/nodes/local/settings", """{"changes":[{"key":"Agent:Llm:MainModel","value":"x"}],"reason":"test"}""", roles: "admin");
        Assert.Equal(400, status);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.Equal("bad_request", doc.RootElement.GetProperty("code").GetString());
            var details = doc.RootElement.GetProperty("details").EnumerateArray().ToList();
            Assert.Equal("Agent:Llm:MainModel", details[0].GetProperty("path").GetString());
            Assert.Equal("at most 500", details[1].GetProperty("message").GetString());
        }
        var restart = await h.PostAsync("/v1/admin/nodes/local/restart", """{"reason":"apply"}""", roles: "admin");
        Assert.Equal(409, restart.Status);
        Assert.Contains("outside at night", restart.Body);
        Assert.Contains("conflict", restart.Body);
    }

    [Fact]
    public void The_route_table_is_closed()
    {
        Assert.Equal(HttpMethod.Put, SettingsProxy.Route("POST", "settings")!.Value.Method);
        Assert.Null(SettingsProxy.Route("GET", "settings/test"));
        Assert.Null(SettingsProxy.Route("POST", "audit"));
        Assert.Null(SettingsProxy.Route("DELETE", "settings"));
        // The generic pass-through never forwards the identity headers, so the bot refuses settings routes reached through it.
        Assert.True(SupervisorProxy.NeedsAdmin("admin/settings"));
    }
}
