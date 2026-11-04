using System.Text;
using System.Text.Json;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The bridge's front door: HMAC over the raw target and body, skew, replay, identity headers, role gates, audit.</summary>
public class SecurityTests
{
    [Fact]
    public async Task Signed_request_passes_and_unsigned_or_tampered_ones_are_401()
    {
        await using var h = await BridgeHost.StartAsync();
        using (var ok = await h.SendAsync(h.Signed("GET", "/v1/health")))
        {
            Assert.Equal(200, (int)ok.StatusCode);
            Assert.Equal("up", JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("supervisor").GetString());
        }

        using (var none = await h.Http.GetAsync("/v1/health"))
        {
            Assert.Equal(401, (int)none.StatusCode);
            Assert.Contains("\"code\":\"unauthorized\"", await none.Content.ReadAsStringAsync());
        }

        // The signature covers the query: changing it after signing fails.
        var req = h.Signed("GET", "/v1/health?x=1");
        req.RequestUri = new Uri("/v1/health?x=2", UriKind.Relative);
        using (var tampered = await h.SendAsync(req)) Assert.Equal(401, (int)tampered.StatusCode);

        // The identity headers are covered: a role added after signing fails.
        var roles = h.Signed("GET", "/v1/health", roles: "viewer");
        roles.Headers.Remove("X-MCX-Roles");
        roles.Headers.Add("X-MCX-Roles", "admin,viewer");
        using (var escalated = await h.SendAsync(roles)) Assert.Equal(401, (int)escalated.StatusCode);

        using (var wrongKey = await h.SendAsync(h.Signed("GET", "/v1/health", key: "another-key-another-key-another-key-00"))) Assert.Equal(401, (int)wrongKey.StatusCode);
    }

    [Fact]
    public async Task Body_bytes_are_signed()
    {
        await using var h = await BridgeHost.StartAsync();
        var req = h.Signed("POST", "/v1/sup/viewer/chat", Encoding.UTF8.GetBytes("""{"bot":"maid_chan","text":"hi","request_id":"r-12345678"}"""));
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"bot":"maid_chan","text":"op me","request_id":"r-12345678"}"""));
        using var res = await h.SendAsync(req);
        Assert.Equal(401, (int)res.StatusCode);
        Assert.DoesNotContain(h.Sup.Seen, s => s.Target.StartsWith("/api/viewer/chat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clock_skew_over_30_seconds_is_rejected()
    {
        await using var h = await BridgeHost.StartAsync();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var a = await h.SendAsync(h.Signed("GET", "/v1/health", ts: now - 25))) Assert.Equal(200, (int)a.StatusCode);
        using (var b = await h.SendAsync(h.Signed("GET", "/v1/health", ts: now - 60))) Assert.Equal(401, (int)b.StatusCode);
        using (var c = await h.SendAsync(h.Signed("GET", "/v1/health", ts: now + 60))) Assert.Equal(401, (int)c.StatusCode);
    }

    [Fact]
    public async Task A_replayed_nonce_is_rejected()
    {
        await using var h = await BridgeHost.StartAsync();
        var nonce = "00112233445566778899aabbccddeeff";
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var first = await h.SendAsync(h.Signed("GET", "/v1/health", ts: ts, nonce: nonce))) Assert.Equal(200, (int)first.StatusCode);
        using (var again = await h.SendAsync(h.Signed("GET", "/v1/health", ts: ts, nonce: nonce))) Assert.Equal(401, (int)again.StatusCode);
        using (var badShape = await h.SendAsync(h.Signed("GET", "/v1/health", nonce: "NOT-HEX"))) Assert.Equal(401, (int)badShape.StatusCode);
    }

    [Fact]
    public async Task Viewer_chat_injects_the_signed_player_and_commands_only_with_the_commands_role()
    {
        await using var h = await BridgeHost.StartAsync();
        var (s1, b1) = await h.PostAsync("/v1/sup/viewer/chat", """{"bot":"maid_chan","text":"claude, set it to day","request_id":"r-8f14e45fceea"}""",
            roles: "chat,viewer", user: "robin", player: "kiwi");
        Assert.Equal(202, s1);
        using (var doc = JsonDocument.Parse(b1))
        {
            Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal("r-8f14e45fceea", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal(17, doc.RootElement.GetProperty("feed_seq").GetInt64());
        }
        var up = h.Sup.Seen.Last(s => s.Target == "/api/viewer/chat");
        Assert.Equal("test-token", up.Token);
        using (var doc = JsonDocument.Parse(up.Body))
        {
            Assert.Equal("kiwi", doc.RootElement.GetProperty("player").GetString());
            Assert.False(doc.RootElement.GetProperty("allow_commands").GetBoolean());
            Assert.Equal("maid_chan", doc.RootElement.GetProperty("bot").GetString());
        }

        var (s2, _) = await h.PostAsync("/v1/sup/viewer/chat", """{"bot":"maid_chan","text":"set it to day","request_id":"r-8f14e45fceeb"}""", roles: "chat,commands,viewer");
        Assert.Equal(202, s2);
        using (var doc = JsonDocument.Parse(h.Sup.Seen.Last(s => s.Target == "/api/viewer/chat").Body))
        {
            Assert.True(doc.RootElement.GetProperty("allow_commands").GetBoolean());
            Assert.Equal("poofimafox", doc.RootElement.GetProperty("player").GetString());
        }
    }

    [Fact]
    public async Task Viewer_chat_refuses_without_chat_role_without_player_or_with_smuggled_fields()
    {
        await using var h = await BridgeHost.StartAsync();
        const string ok = """{"bot":"maid_chan","text":"hi","request_id":"r-12345678"}""";
        Assert.Equal(403, (await h.PostAsync("/v1/sup/viewer/chat", ok, roles: "viewer")).Status);
        Assert.Equal(403, (await h.PostAsync("/v1/sup/viewer/chat", ok, roles: "commands,viewer")).Status);
        Assert.Equal(403, (await h.PostAsync("/v1/sup/viewer/chat", ok, roles: "chat", player: "")).Status);
        Assert.Equal(400, (await h.PostAsync("/v1/sup/viewer/chat", """{"bot":"maid_chan","text":"hi","request_id":"r-12345678","player":"poofimafox"}""", roles: "chat")).Status);
        Assert.Equal(400, (await h.PostAsync("/v1/sup/viewer/chat", """{"bot":"maid_chan","text":"hi","request_id":"r-12345678","allow_commands":true}""", roles: "chat")).Status);
        Assert.Equal(400, (await h.PostAsync("/v1/sup/viewer/chat", """{"bot":"maid_chan","text":"","request_id":"r-12345678"}""", roles: "chat")).Status);
        Assert.DoesNotContain(h.Sup.Seen, s => s.Target == "/api/viewer/chat");
    }

    [Fact]
    public async Task Admin_passthrough_needs_admin_and_sets_who_from_the_signed_user()
    {
        await using var h = await BridgeHost.StartAsync();
        const string edit = """{"op":"add","kind":"fact","fields":{"subject":"fox","text":"likes cobble"},"reason":"test"}""";
        Assert.Equal(403, (await h.PostAsync("/v1/sup/admin/memory", edit, roles: "chat,commands,viewer")).Status);
        Assert.Equal(403, (await h.GetAsync("/v1/sup/admin/audit", roles: "viewer")).Status);
        Assert.DoesNotContain(h.Sup.Seen, s => s.Target.StartsWith("/api/admin", StringComparison.Ordinal));

        var (status, _) = await h.PostAsync("/v1/sup/admin/memory", edit, roles: "admin,viewer", user: "fox");
        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(h.Sup.Seen.Last(s => s.Target == "/api/admin/memory").Body);
        Assert.Equal("fox (web)", doc.RootElement.GetProperty("who").GetString());
        // The opaque fields map passes untouched.
        Assert.Equal("likes cobble", doc.RootElement.GetProperty("fields").GetProperty("text").GetString());

        Assert.Equal(400, (await h.PostAsync("/v1/sup/admin/memory", """{"op":"add","kind":"fact","fields":{},"who":"someone else"}""", roles: "admin")).Status);
        Assert.Equal(403, (await h.GetAsync("/v1/admin/schema", roles: "viewer")).Status);
        Assert.Equal(200, (await h.GetAsync("/v1/admin/schema", roles: "admin")).Status);
    }

    [Fact]
    public async Task Writes_are_audited_without_bodies()
    {
        await using var h = await BridgeHost.StartAsync();
        await h.PostAsync("/v1/sup/viewer/chat", """{"bot":"maid_chan","text":"a secret phrase","request_id":"r-12345678"}""", roles: "chat", user: "robin", player: "kiwi");
        var lines = await File.ReadAllLinesAsync(h.AuditLog);
        var line = Assert.Single(lines);
        Assert.Contains("\"user\":\"robin\"", line);
        Assert.Contains("\"path\":\"/v1/sup/viewer/chat\"", line);
        Assert.Contains("\"status\":202", line);
        Assert.DoesNotContain("secret phrase", line);
    }

    [Fact]
    public async Task Oversized_json_bodies_are_413()
    {
        await using var h = await BridgeHost.StartAsync();
        var big = "{\"text\":\"" + new string('x', 300 * 1024) + "\"}";
        Assert.Equal(413, (await h.PostAsync("/v1/sup/admin/memory", big)).Status);
    }
}

/// <summary>The supervisor pass-through: the contract's allow-list, path safety, token, reply adapters, error mapping, chunk cache.</summary>
public class PassthroughTests
{
    [Theory]
    [InlineData("GET", "bots", true)]
    [InlineData("GET", "hive/tree", true)]
    [InlineData("GET", "world/chunks", true)]
    [InlineData("GET", "world/blocks", true)]
    [InlineData("GET", "world/containers", true)]
    [InlineData("POST", "world/containers", false)]
    [InlineData("GET", "world", false)]
    [InlineData("GET", "bodies/maid_chan/script", true)]
    [InlineData("GET", "bodies", false)]
    [InlineData("GET", "blueprints/3", true)]
    [InlineData("GET", "botsx", false)]
    [InlineData("GET", "viewer/now", true)]
    [InlineData("GET", "viewer/chat", false)]
    [InlineData("GET", "viewer/bodies/maid_chan/inventory", true)]
    [InlineData("GET", "viewer/bodies", false)]
    [InlineData("POST", "viewer/bodies/maid_chan/inventory", false)]
    [InlineData("GET", "admin/audit", true)]
    [InlineData("GET", "admin/rebuild/j1", true)]
    [InlineData("GET", "admin/schema", false)]
    [InlineData("POST", "viewer/chat", true)]
    [InlineData("POST", "admin/backup", true)]
    [InlineData("POST", "status", false)]
    [InlineData("POST", "bots", false)]
    [InlineData("GET", "story", true)]
    [InlineData("GET", "story/colonies/story-1", true)]
    [InlineData("GET", "story/recipes", true)]
    [InlineData("GET", "storyx", false)]
    [InlineData("POST", "story/colonies/story-1/orders", true)]
    [InlineData("POST", "story/directives", true)]
    [InlineData("POST", "story", false)]
    public void Allow_list_matches_the_contract(string method, string path, bool allowed) =>
        Assert.Equal(allowed, SupervisorProxy.Allowed(method, path));

    [Fact]
    public async Task Story_reads_pass_for_viewers_and_writes_need_the_story_or_admin_role()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/story"] = _ => FakeSupervisor.Json("""{"colonies":[{"id":"story-1","name":"Hollow","paused":false,"pawns":["maid_s1_ash"]}]}""");
        h.Sup.Routes["/api/story/colonies/story-1/orders"] = _ => FakeSupervisor.Json("""{"ok":true,"rev":12}""");
        var (s, body) = await h.GetAsync("/v1/sup/story", roles: "viewer");
        Assert.Equal(200, s);
        Assert.Contains("story-1", body);
        const string order = """{"pawns":["maid_s1_ash"],"order":{"kind":"chop","target":{"x":1,"y":64,"z":2}},"queue":true}""";
        Assert.Equal(403, (await h.PostAsync("/v1/sup/story/colonies/story-1/orders", order, roles: "chat,commands,viewer")).Status);
        Assert.DoesNotContain(h.Sup.Seen, r => r.Target.StartsWith("/api/story/colonies/story-1/orders", StringComparison.Ordinal));
        var (ok, _) = await h.PostAsync("/v1/sup/story/colonies/story-1/orders", order, roles: "story,viewer", user: "sky");
        Assert.Equal(200, ok);
        var seen = h.Sup.Seen.Last(r => r.Target == "/api/story/colonies/story-1/orders");
        Assert.Equal("test-token", seen.Token);
        using (var doc = JsonDocument.Parse(seen.Body))
        {
            Assert.Equal("sky (web)", doc.RootElement.GetProperty("who").GetString());
            Assert.Equal("chop", doc.RootElement.GetProperty("order").GetProperty("kind").GetString());
        }
        Assert.Equal(200, (await h.PostAsync("/v1/sup/story/colonies/story-1/orders", order, roles: "admin,viewer")).Status);
        // `who` comes from the signature, never the body.
        Assert.Equal(400, (await h.PostAsync("/v1/sup/story/colonies/story-1/orders", """{"who":"mallory"}""", roles: "story")).Status);
    }

    [Fact]
    public async Task Story_directives_carry_the_signed_player_and_commands_only_with_the_role()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/story/directives"] = _ => FakeSupervisor.Json("""{"id":"dir3","delivered":["maid_chan"],"line":"poofimafox (story map): come here [dir3]"}""");
        const string dir = """{"bots":["maid_chan"],"directive":{"kind":"text","text":"come here"},"request_id":"r-12345678"}""";
        Assert.Equal(200, (await h.PostAsync("/v1/sup/story/directives", dir, roles: "story,viewer")).Status);
        using (var doc = JsonDocument.Parse(h.Sup.Seen.Last(r => r.Target == "/api/story/directives").Body))
        {
            Assert.Equal("poofimafox", doc.RootElement.GetProperty("player").GetString());
            Assert.False(doc.RootElement.GetProperty("allow_commands").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("who", out _));
        }
        Assert.Equal(200, (await h.PostAsync("/v1/sup/story/directives", dir, roles: "commands,story,viewer")).Status);
        using (var doc = JsonDocument.Parse(h.Sup.Seen.Last(r => r.Target == "/api/story/directives").Body))
            Assert.True(doc.RootElement.GetProperty("allow_commands").GetBoolean());
        // Never from the body; never without a linked player.
        Assert.Equal(400, (await h.PostAsync("/v1/sup/story/directives", """{"bots":["maid_chan"],"player":"mallory"}""", roles: "story")).Status);
        Assert.Equal(400, (await h.PostAsync("/v1/sup/story/directives", """{"bots":["maid_chan"],"allow_commands":true}""", roles: "story")).Status);
        Assert.Equal(403, (await h.PostAsync("/v1/sup/story/directives", dir, roles: "story", player: "")).Status);
    }

    [Theory]
    [InlineData("/v1/sup/bodies/..%2Fadmin/x")]
    [InlineData("/v1/sup/bodies/%2e%2e/admin")]
    [InlineData("/v1/sup/bodies/a%5Cb")]
    [InlineData("/v1/sup/bodies//x")]
    public void Unsafe_paths_are_refused(string target) => Assert.Null(SupervisorProxy.Split(target));

    [Fact]
    public void Paths_are_decoded_and_re_escaped()
    {
        var s = SupervisorProxy.Split("/v1/sup/scripts/cobble%20loop?v=2")!.Value;
        Assert.Equal("scripts/cobble loop", s.Path);
        Assert.Equal("v=2", s.Query);
        Assert.Equal("api/scripts/cobble%20loop?v=2", SupervisorProxy.Upstream(s.Path, s.Query));
    }

    [Fact]
    public async Task Allowed_gets_forward_with_the_token_and_others_are_404()
    {
        await using var h = await BridgeHost.StartAsync();
        var (s, body) = await h.GetAsync("/v1/sup/bots");
        Assert.Equal(200, s);
        Assert.Contains("\"bots\":[]", body);
        Assert.Equal("test-token", h.Sup.Seen.Last(r => r.Target == "/api/bots").Token);
        Assert.Equal(404, (await h.GetAsync("/v1/sup/shutdown")).Status);
        Assert.Equal(404, (await h.PostAsync("/v1/sup/status", "{}")).Status);
        Assert.DoesNotContain(h.Sup.Seen, r => r.Target.StartsWith("/api/shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Container_contents_pass_through_for_viewers_with_the_query_and_the_token_and_errors_map_to_ApiError()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/world/containers"] = req => req.Query.ContainsKey("x0")
            ? FakeSupervisor.Json("""{"dim":0,"containers":[{"pos":[2,74,-4],"dim":0,"kind":"double_chest","size":54,"used":1,"total":64,"top":[{"id":17,"meta":0,"name":"log","count":64}],"seen_at":1762077420000,"seen_by":"maid_chan_102","source":"memory","cells":[[2,74,-4],[3,74,-4]]}],"capped":false}""")
            : req.Query["x"] == "9"
                ? FakeSupervisor.Json("""{"error":"no container at 9,64,9"}""", 404)
                : FakeSupervisor.Json("""{"pos":[2,74,-4],"dim":0,"kind":"double_chest","size":54,"slots":[{"slot":53,"id":264,"meta":0,"count":2,"name":"diamond","display":"Diamond","enchanted":false}],"seen_at":1762077420000,"seen_by":"maid_chan_102","source":"memory","version":"b1.8.1","exact":true,"cells":[[2,74,-4],[3,74,-4]]}""");
        // Signed with the viewer role only: the query passes unchanged (snake_case), the supervisor token added by the bridge.
        var (s, body) = await h.GetAsync("/v1/sup/world/containers?dim=0&x=3&y=74&z=-4", roles: "viewer");
        Assert.Equal(200, s);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.Equal("double_chest", doc.RootElement.GetProperty("kind").GetString());
            Assert.Equal(53, doc.RootElement.GetProperty("slots")[0].GetProperty("slot").GetInt32());
            Assert.Equal("maid_chan_102", doc.RootElement.GetProperty("seen_by").GetString());
        }
        var seen = h.Sup.Seen.Last(r => r.Target.StartsWith("/api/world/containers", StringComparison.Ordinal));
        Assert.Equal("/api/world/containers?dim=0&x=3&y=74&z=-4", seen.Target);
        Assert.Equal("test-token", seen.Token);
        var (bs, bb) = await h.GetAsync("/v1/sup/world/containers?dim=0&x0=-16&z0=-40&x1=32&z1=8&limit=50", roles: "viewer");
        Assert.Equal(200, bs);
        Assert.Contains("\"capped\":false", bb);
        // Unsigned: 401 before the supervisor is asked.
        int before = h.Sup.Seen.Count(r => r.Target.Contains("/world/containers", StringComparison.Ordinal));
        using (var none = await h.Http.GetAsync("/v1/sup/world/containers?x=3&y=74&z=-4")) Assert.Equal(401, (int)none.StatusCode);
        Assert.Equal(before, h.Sup.Seen.Count(r => r.Target.Contains("/world/containers", StringComparison.Ordinal)));
        // Not a container: the bot's 404 becomes the contract's ApiError; never a write.
        var (s2, b2) = await h.GetAsync("/v1/sup/world/containers?dim=0&x=9&y=64&z=9", roles: "viewer");
        Assert.Equal(404, s2);
        Assert.Contains("\"code\":\"not_found\"", b2);
        Assert.Equal(404, (await h.PostAsync("/v1/sup/world/containers", "{}")).Status);
    }

    [Fact]
    public async Task A_body_inventory_passes_through_for_viewers_with_the_token_and_errors_map_to_ApiError()
    {
        await using var h = await BridgeHost.StartAsync();
        // Signed with the viewer role only: a read, unchanged (snake_case), the supervisor token added by the bridge.
        var (s, body) = await h.GetAsync("/v1/sup/viewer/bodies/maid_chan/inventory", roles: "viewer");
        Assert.Equal(200, s);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("held_slot").GetInt32());
            Assert.Equal(1441, doc.RootElement.GetProperty("slots")[0].GetProperty("durability").GetProperty("left").GetInt32());
            Assert.Equal(4, doc.RootElement.GetProperty("armor").GetArrayLength());
        }
        Assert.Equal("test-token", h.Sup.Seen.Last(r => r.Target == "/api/viewer/bodies/maid_chan/inventory").Token);
        // Unsigned: 401 before the supervisor is asked.
        int before = h.Sup.Seen.Count(r => r.Target.Contains("/inventory", StringComparison.Ordinal));
        using (var none = await h.Http.GetAsync("/v1/sup/viewer/bodies/maid_chan/inventory")) Assert.Equal(401, (int)none.StatusCode);
        Assert.Equal(before, h.Sup.Seen.Count(r => r.Target.Contains("/inventory", StringComparison.Ordinal)));
        // An unknown body: the bot's 404 becomes the contract's ApiError.
        h.Sup.Routes["/api/viewer/bodies/nobody/inventory"] = _ => FakeSupervisor.Json("""{"error":"no such body","online":false}""", 404);
        var (s2, b2) = await h.GetAsync("/v1/sup/viewer/bodies/nobody/inventory", roles: "viewer");
        Assert.Equal(404, s2);
        Assert.Contains("\"code\":\"not_found\"", b2);
        // Not a write route, and no path tricks.
        Assert.Equal(404, (await h.PostAsync("/v1/sup/viewer/bodies/maid_chan/inventory", "{}")).Status);
        Assert.Equal(404, (await h.GetAsync("/v1/sup/viewer/bodies/..%2Fadmin/inventory")).Status);
    }

    [Fact]
    public async Task Now_pos_becomes_an_array_and_memory_ids_become_strings()
    {
        await using var h = await BridgeHost.StartAsync();
        var (s, body) = await h.GetAsync("/v1/sup/viewer/now?bot=maid_chan");
        Assert.Equal(200, s);
        using (var doc = JsonDocument.Parse(body))
        {
            var pos = doc.RootElement.GetProperty("body").GetProperty("pos");
            Assert.Equal(JsonValueKind.Array, pos.ValueKind);
            Assert.Equal(-2, pos[2].GetDouble());
        }
        h.Sup.Routes["/api/viewer/memory-activity"] = _ => FakeSupervisor.Json(
            """{"events":[{"seq":3,"at":"2026-11-02T10:00:00+00:00","brain":"main","body":null,"memory_kind":"note","memory_id":42,"raptor_node_id":7,"source":"rag","score":0.5,"turn":null}],"in_context":[],"last":3}""");
        var (s2, b2) = await h.GetAsync("/v1/sup/viewer/memory-activity?since=0");
        Assert.Equal(200, s2);
        using var d2 = JsonDocument.Parse(b2);
        Assert.Equal("42", d2.RootElement.GetProperty("events")[0].GetProperty("memory_id").GetString());
    }

    [Fact]
    public async Task Upstream_errors_map_to_ApiError_and_a_store_refusal_passes_through()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/locate"] = _ => FakeSupervisor.Json("""{"error":"no place called moon"}""", 404);
        var (s, body) = await h.GetAsync("/v1/sup/locate?q=moon");
        Assert.Equal(404, s);
        Assert.Contains("\"code\":\"not_found\"", body);
        h.Sup.Routes["/api/world/index"] = _ => FakeSupervisor.Json("""{"error":"slow down","retry_after_ms":1500}""", 429);
        var (s2, b2) = await h.GetAsync("/v1/sup/world/index?dim=0&x=0&z=0&r=32");
        Assert.Equal(429, s2);
        Assert.Contains("\"retry_after_ms\":1500", b2);
        h.Sup.Routes["/api/bots"] = _ => FakeSupervisor.Json("""{"error":"token required"}""", 401);
        Assert.Equal(502, (await h.GetAsync("/v1/sup/bots")).Status);
        h.Sup.Routes["/api/admin/memory"] = _ => FakeSupervisor.Json("""{"ok":false,"message":"no such fact","reembedded":false}""", 400);
        var (s3, b3) = await h.PostAsync("/v1/sup/admin/memory", """{"op":"delete","kind":"fact","id":"99"}""");
        Assert.Equal(400, s3);
        Assert.Contains("\"message\":\"no such fact\"", b3);
    }

    [Fact]
    public async Task Supervisor_down_is_503()
    {
        await using var h = await BridgeHost.StartAsync();
        await h.Sup.DisposeAsync();
        Assert.Equal("down", JsonDocument.Parse((await h.GetAsync("/v1/health")).Body).RootElement.GetProperty("supervisor").GetString());
        var (s, body) = await h.GetAsync("/v1/sup/bots");
        Assert.Equal(503, s);
        Assert.Contains("upstream_unavailable", body);
        Assert.DoesNotContain("127.0.0.1", body);
    }

    [Fact]
    public async Task Binary_replies_stream_with_their_headers()
    {
        await using var h = await BridgeHost.StartAsync();
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 };
        h.Sup.Routes["/api/bodies/maid_chan/vision/4.png"] = _ => (200, "image/png", png);
        using var res = await h.SendAsync(h.Signed("GET", "/v1/sup/bodies/maid_chan/vision/4.png"));
        Assert.Equal(200, (int)res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Chunks_are_served_from_the_cache_while_the_index_hash_is_unchanged()
    {
        await using var h = await BridgeHost.StartAsync();
        var col = new McwcColumn(0, 0, 1234567890123456789, 1762077600000, true, "maid_chan", [1, 2, 3], null);
        var col2 = new McwcColumn(1, 0, 42, 1762077600000, true, "maid_chan", [4, 5], null);
        h.Sup.Routes["/api/world/index"] = _ => FakeSupervisor.Json(
            """{"dim":0,"at":1,"columns":[{"cx":0,"cz":0,"hash":1234567890123456789,"seen":1762077600000,"by":"maid_chan","live":true},{"cx":1,"cz":0,"hash":42,"seen":1,"by":"x","live":false}],"version":"b1.8.1","time":0,"rain":0,"thunder":0}""");
        h.Sup.Routes["/api/world/chunks"] = req =>
        {
            var cols = req.Query["cols"].ToString().Split(';').Select(c => c == "0,0" ? col : col2).ToList();
            return (200, "application/octet-stream", Mcwc.Encode(1, 0, cols));
        };
        var (s, idx) = await h.GetAsync("/v1/sup/world/index?dim=0&x=0&z=0&r=32");
        Assert.Equal(200, s);
        Assert.Contains("1234567890123456789", idx); // the i64 hash passes through as the exact number
        using (var r1 = await h.SendAsync(h.Signed("GET", "/v1/sup/world/chunks?dim=0&cols=0,0;1,0")))
        {
            var (_, _, cols) = Mcwc.Decode(await r1.Content.ReadAsByteArrayAsync());
            Assert.Equal(2, cols.Count);
        }
        int before = h.Sup.Seen.Count(r => r.Target.StartsWith("/api/world/chunks", StringComparison.Ordinal));
        using var r2 = await h.SendAsync(h.Signed("GET", "/v1/sup/world/chunks?dim=0&cols=1,0;0,0"));
        var (v, dim, again) = Mcwc.Decode(await r2.Content.ReadAsByteArrayAsync());
        Assert.Equal(before, h.Sup.Seen.Count(r => r.Target.StartsWith("/api/world/chunks", StringComparison.Ordinal)));
        Assert.Equal([1, 0], again.Select(c => c.Cx).ToArray());
        Assert.Equal(1234567890123456789, again[1].Hash);
        Assert.NotNull(r2.Headers.ETag);
        // A new hash in the index: that column is fetched again, the other still comes from the cache.
        h.Sup.Routes["/api/world/index"] = _ => FakeSupervisor.Json(
            """{"dim":0,"at":2,"columns":[{"cx":0,"cz":0,"hash":1234567890123456789,"seen":1,"by":"maid_chan","live":true},{"cx":1,"cz":0,"hash":43,"seen":2,"by":"x","live":true}],"version":"b1.8.1","time":0,"rain":0,"thunder":0}""");
        await h.GetAsync("/v1/sup/world/index?dim=0&x=0&z=0&r=32");
        using var r3 = await h.SendAsync(h.Signed("GET", "/v1/sup/world/chunks?dim=0&cols=0,0;1,0"));
        Assert.Equal(200, (int)r3.StatusCode);
        Assert.Equal("/api/world/chunks?dim=0&cols=1,0", h.Sup.Seen.Last(r => r.Target.StartsWith("/api/world/chunks", StringComparison.Ordinal)).Target);
    }

    [Fact]
    public void Mcwc_round_trips_v1_and_v2_and_rejects_truncation()
    {
        var cols = new List<McwcColumn> { new(3, -4, -5, 6, true, "b", [9, 9], [7]), new(0, 0, 0, 0, false, "", [], []) };
        var bytes = Mcwc.Encode(2, -1, cols);
        var (v, dim, back) = Mcwc.Decode(bytes);
        Assert.Equal((2, -1), (v, dim));
        Assert.Equal(cols[0].Light, back[0].Light);
        Assert.Throws<FormatException>(() => Mcwc.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<FormatException>(() => Mcwc.Decode([.. bytes, 0]));
    }
}
