using System.Text;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>A fake admin service: records calls, answers canned results.</summary>
internal sealed class FakeAdmin : ISupervisorAdmin
{
    public readonly List<MemoryEdit> Edits = [];
    public readonly List<RebuildRequest> Rebuilds = [];
    public bool Busy;
    public string Target => "live:test";

    public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct)
    {
        Edits.Add(edit);
        return Task.FromResult(edit.Kind == "fact" ? new MemoryEditResult(true, "updated fact #1", "1", "{\"text\":\"a\"}", "{\"text\":\"b\"}", true)
            : new MemoryEditResult(false, "unknown kind"));
    }

    public RebuildEstimate Estimate(RebuildRequest request) { Rebuilds.Add(request); return new RebuildEstimate(3, 900, 750, 750, 0.0042m, "claude-haiku-5-5", "ok"); }

    public string StartRebuild(RebuildRequest request)
    {
        if (Busy) throw new InvalidOperationException("a rebuild is already running (job j0)");
        Rebuilds.Add(request);
        return "j1";
    }

    public AdminJobView? Job(string jobId) => jobId == "j1"
        ? new AdminJobView("j1", "done", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new RebuildRequest("node", 7), new RebuildProgress("done", 1, 1),
            new RebuildResult(true, "rebuilt", [new SummaryDiff(7, 2, "old", "new")], 0.001m, TimeSpan.FromSeconds(2)))
        : null;

    public bool CancelJob(string jobId) => jobId == "j1";
    public string Backup(string who) => "/data/backups/memory-backup-x.db";
    public IReadOnlyList<AuditEntry> Audit(int max) => [new AuditEntry(DateTimeOffset.UnixEpoch, "Fox", Target, "update", "fact", "1", "a", "b", null, true, "ok")];
}

public class SupervisorAdminTests
{
    private const string Token = "s3cret-token";

    private static SupervisorServer Server(bool allow = true, string? token = Token, ISupervisorAdmin? admin = null) =>
        new(new SupervisorOptions { Token = token, AllowAdminWrites = allow }, new FakeSupervisorData()) { Admin = admin ?? new FakeAdmin() };

    private static SupervisorRequest Req(string method, string path, string? token = Token, string? body = null, string? query = null)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query != null)
            foreach (var kv in query.Split('&'))
            {
                var i = kv.IndexOf('=');
                q[kv[..i]] = kv[(i + 1)..];
            }
        return new SupervisorRequest(method, path, q, token, body);
    }

    private const string FactEdit = """{"op":"update","kind":"fact","id":1,"fields":{"text":"b","x":5},"who":"Fox","reason":"typo"}""";

    [Fact]
    public async Task Admin_routes_are_403_without_the_right_token_header()
    {
        await using var s = Server();
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/admin/memory", token: null, body: FactEdit))).Status);
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/admin/memory", token: "wrong", body: FactEdit))).Status);
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/admin/memory", token: Token + "x", body: FactEdit))).Status);
        // ?token= is not accepted for admin writes (header only).
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/admin/memory", token: null, body: FactEdit, query: "token=" + Token))).Status);
        Assert.Equal(403, (await s.HandleAsync(Req("GET", "/api/admin/audit", token: null))).Status);
    }

    [Fact]
    public async Task Admin_routes_are_403_with_the_right_token_when_the_flag_is_off()
    {
        var admin = new FakeAdmin();
        await using var s = Server(allow: false, admin: admin);
        var r = await s.HandleAsync(Req("POST", "/api/admin/memory", body: FactEdit));
        Assert.Equal(403, r.Status);
        Assert.Contains("AllowAdminWrites", r.Text);
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/admin/backup"))).Status);
        Assert.Empty(admin.Edits);
    }

    [Fact]
    public async Task Admin_routes_are_403_when_no_token_is_configured()
    {
        var admin = new FakeAdmin();
        await using var s = Server(token: null, admin: admin);
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/admin/memory", token: null, body: FactEdit))).Status);
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/admin/memory", token: "", body: FactEdit))).Status);
        Assert.Empty(admin.Edits);
        await using var s2 = Server(token: "");
        Assert.Equal(403, (await s2.HandleAsync(Req("POST", "/api/admin/memory", token: "", body: FactEdit))).Status);
        // No admin service at all (flag on, token right): still 403.
        await using var s3 = new SupervisorServer(new SupervisorOptions { Token = Token, AllowAdminWrites = true }, new FakeSupervisorData());
        Assert.Equal(403, (await s3.HandleAsync(Req("POST", "/api/admin/memory", body: FactEdit))).Status);
    }

    [Fact]
    public async Task Admin_routes_work_with_flag_and_token()
    {
        var admin = new FakeAdmin();
        await using var s = Server(admin: admin);

        var r = await s.HandleAsync(Req("POST", "/api/admin/memory", body: FactEdit));
        Assert.Equal(200, r.Status);
        using (var j = JsonDocument.Parse(r.Text))
        {
            Assert.True(j.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(j.RootElement.GetProperty("reembedded").GetBoolean());
            Assert.Equal("{\"text\":\"a\"}", j.RootElement.GetProperty("before").GetString());
        }
        var e = Assert.Single(admin.Edits);
        Assert.Equal(("update", "fact", "1", "Fox", "typo"), (e.Op, e.Kind, e.Id, e.Who, e.Reason));
        Assert.Equal("b", e.Fields["text"]);
        Assert.Equal("5", e.Fields["x"]); // numbers become strings

        Assert.Equal(400, (await s.HandleAsync(Req("POST", "/api/admin/memory", body: """{"op":"add","kind":"nope","fields":{}}"""))).Status);
        Assert.Equal(400, (await s.HandleAsync(Req("POST", "/api/admin/memory", body: "{not json"))).Status);
        Assert.Equal(400, (await s.HandleAsync(Req("POST", "/api/admin/memory", body: null))).Status);
        Assert.Equal(413, (await s.HandleAsync(Req("POST", "/api/admin/memory", body: SupervisorAdminRoutes.TooLarge))).Status);
        Assert.Equal(405, (await s.HandleAsync(Req("GET", "/api/admin/memory"))).Status);

        var est = await s.HandleAsync(Req("POST", "/api/admin/rebuild/estimate", body: """{"scope":"kind","kind":"fact"}"""));
        Assert.Equal(200, est.Status);
        Assert.Contains("\"summaries\":3", est.Text);
        Assert.Contains("\"model\":\"claude-haiku-5-5\"", est.Text);
        Assert.Equal(0.50m, admin.Rebuilds[^1].MaxUsd); // default cap

        var start = await s.HandleAsync(Req("POST", "/api/admin/rebuild", body: """{"scope":"node","node":7,"max_usd":0.1,"who":"Fox"}"""));
        Assert.Equal(202, start.Status);
        Assert.Contains("\"job_id\":\"j1\"", start.Text);
        Assert.Equal((7, 0.1m, "Fox"), (admin.Rebuilds[^1].Node!.Value, admin.Rebuilds[^1].MaxUsd, admin.Rebuilds[^1].Who));
        admin.Busy = true;
        Assert.Equal(409, (await s.HandleAsync(Req("POST", "/api/admin/rebuild", body: """{"scope":"node","node":7}"""))).Status);

        var job = await s.HandleAsync(Req("GET", "/api/admin/rebuild/j1"));
        Assert.Equal(200, job.Status);
        Assert.Contains("\"state\":\"done\"", job.Text);
        Assert.Contains("\"diffs\":[{\"index\":7,\"layer\":2,\"old\":\"old\",\"new\":\"new\"}]", job.Text);
        Assert.Equal(404, (await s.HandleAsync(Req("GET", "/api/admin/rebuild/nope"))).Status);
        Assert.Equal(200, (await s.HandleAsync(Req("POST", "/api/admin/rebuild/j1/cancel"))).Status);

        var backup = await s.HandleAsync(Req("POST", "/api/admin/backup"));
        Assert.Equal(200, backup.Status);
        Assert.Contains("memory-backup-x.db", backup.Text);
        var audit = await s.HandleAsync(Req("GET", "/api/admin/audit", query: "max=10"));
        Assert.Equal(200, audit.Status);
        Assert.Contains("\"entries\":[", audit.Text);
        var schema = await s.HandleAsync(Req("GET", "/api/admin/schema"));
        Assert.Equal(200, schema.Status);
        Assert.Contains("\"fact\":{\"ops\":[\"add\",\"update\",\"delete\"]", schema.Text);
        Assert.Equal(404, (await s.HandleAsync(Req("GET", "/api/admin/nope"))).Status);
    }

    [Fact]
    public async Task Other_routes_keep_their_405_and_404_behaviour()
    {
        await using var s = Server();
        var post = await s.HandleAsync(Req("POST", "/api/bots", body: "{}"));
        Assert.Equal(405, post.Status);
        Assert.Equal("GET, HEAD", post.Headers!["Allow"]);
        Assert.Equal(405, (await s.HandleAsync(Req("DELETE", "/api/status"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Req("GET", "/api/nothing"))).Status);
        Assert.Equal(200, (await s.HandleAsync(Req("GET", "/api/bots"))).Status);
        Assert.Equal(401, (await s.HandleAsync(Req("GET", "/api/bots", token: null))).Status);
        // A path that only starts like the admin prefix is an ordinary route.
        Assert.Equal(404, (await s.HandleAsync(Req("GET", "/api/administrator"))).Status);
    }

    [Fact]
    public async Task Live_service_applies_edits_through_real_stores_and_audits_next_to_the_db()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-supadmin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = Path.Combine(dir, "memory.db");
            var ctx = MemoryAdminContext.OpenLocal(db, new AgentOptions { KnowledgeDirectory = null }, NullEmbeddingProvider.Instance, llm: null);
            var audit = new AdminAudit(Path.Combine(dir, SupervisorAdminService.AuditFile));
            await using var s = Server(admin: new SupervisorAdminService(ctx, audit, "local:" + db, Path.Combine(dir, "backups")));

            var add = await s.HandleAsync(Req("POST", "/api/admin/memory",
                body: """{"op":"add","kind":"place","fields":{"name":"melon patch","x":10,"y":64,"z":-3},"who":"Fox"}"""));
            Assert.Equal(200, add.Status);
            Assert.Equal(1, ctx.Db.ScalarLong("SELECT count(*) FROM waypoints WHERE name = 'melon patch'"));
            var bad = await s.HandleAsync(Req("POST", "/api/admin/memory", body: """{"op":"update","kind":"place","id":"nowhere","fields":{"x":1},"who":"Fox"}"""));
            Assert.Equal(400, bad.Status);
            Assert.Contains("no place", bad.Text);

            var est = await s.HandleAsync(Req("POST", "/api/admin/rebuild/estimate", body: """{"scope":"kind","kind":"fact"}"""));
            Assert.Equal(200, est.Status);
            Assert.Contains("no RAPTOR tree", est.Text);
            var job = await s.HandleAsync(Req("POST", "/api/admin/rebuild", body: """{"scope":"kind","kind":"fact"}"""));
            Assert.Equal(202, job.Status);
            var id = JsonDocument.Parse(job.Text).RootElement.GetProperty("job_id").GetString()!;
            string state = "";
            for (int i = 0; i < 100 && state is not ("done" or "failed" or "cancelled"); i++)
            {
                await Task.Delay(20);
                state = JsonDocument.Parse((await s.HandleAsync(Req("GET", "/api/admin/rebuild/" + id))).Text).RootElement.GetProperty("state").GetString()!;
            }
            Assert.Equal("failed", state); // no tree: refused, recorded

            var backup = await s.HandleAsync(Req("POST", "/api/admin/backup", body: """{"who":"Fox"}"""));
            Assert.Equal(200, backup.Status);
            Assert.True(File.Exists(JsonDocument.Parse(backup.Text).RootElement.GetProperty("path").GetString()));

            var lines = File.ReadAllLines(audit.Path);
            Assert.Equal(4, lines.Length); // add, failed update, failed rebuild, backup
            Assert.Contains("\"op\":\"backup\"", lines[^1]);
            Assert.DoesNotContain(Token, File.ReadAllText(audit.Path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
