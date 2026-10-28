using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>A fake admin that serves the shard routes and records what reached it.</summary>
internal sealed class FakeShardAdmin : ISupervisorAdmin
{
    public int Previews, Imports;
    public string Target => "live:test";
    public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct) => Task.FromResult(new MemoryEditResult(false, "n/a"));
    public RebuildEstimate Estimate(RebuildRequest request) => new(0, 0, 0, 0, 0, "m", "");
    public string StartRebuild(RebuildRequest request) => "j";
    public AdminJobView? Job(string jobId) => null;
    public bool CancelJob(string jobId) => false;
    public string Backup(string who) => "b";
    public IReadOnlyList<AuditEntry> Audit(int max) => [];
    public bool SupportsShards => true;
    public ShardDiff PreviewShard(byte[] shard, ShardImportOptions options) { Previews++; return new ShardDiff { Shard = "x" }; }
    public Task<ShardImportResult> ImportShardAsync(byte[] shard, ShardImportOptions options, CancellationToken ct)
    {
        Imports++;
        return Task.FromResult(new ShardImportResult { Ok = true, Message = "ok" });
    }
}

public class SupervisorShardRouteTests
{
    private const string Token = "s3cret-token";

    private static SupervisorServer Server(ISupervisorAdmin admin, bool allow = true, string? token = Token) =>
        new(new SupervisorOptions { Token = token, AllowAdminWrites = allow }, new FakeSupervisorData()) { Admin = admin };

    private static SupervisorRequest Req(string path, string? body, string? token = Token, string method = "POST") =>
        new(method, path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), token, body);

    private static string Body(byte[] shard, string mode = "merge") =>
        JsonSerializer.Serialize(new { shard = Convert.ToBase64String(shard), mode, conflict = "newer", who = "Fox" });

    [Fact]
    public async Task Shard_routes_are_403_without_the_token_or_with_writes_off()
    {
        var admin = new FakeShardAdmin();
        var body = Body([1, 2, 3]);
        await using (var s = Server(admin))
        {
            foreach (var path in new[] { "/api/admin/shard/import/preview", "/api/admin/shard/import" })
            {
                Assert.Equal(403, (await s.HandleAsync(Req(path, body, token: null))).Status);
                Assert.Equal(403, (await s.HandleAsync(Req(path, body, token: "wrong"))).Status);
                Assert.Equal(405, (await s.HandleAsync(Req(path, null, method: "GET"))).Status);
            }
        }
        await using (var off = Server(admin, allow: false))
        {
            var r = await off.HandleAsync(Req("/api/admin/shard/import", body));
            Assert.Equal(403, r.Status);
            Assert.Contains("AllowAdminWrites", r.Text);
            Assert.Equal(403, (await off.HandleAsync(Req("/api/admin/shard/import/preview", body))).Status);
        }
        await using (var noToken = Server(admin, token: null))
            Assert.Equal(403, (await noToken.HandleAsync(Req("/api/admin/shard/import", body, token: null))).Status);
        Assert.Equal(0, admin.Previews + admin.Imports);

        // A service without shard support refuses too; one with it is reached only with flag + token.
        await using (var plain = Server(new FakeAdmin()))
            Assert.Equal(403, (await plain.HandleAsync(Req("/api/admin/shard/import", body))).Status);
        await using (var ok = Server(admin))
        {
            Assert.Equal(200, (await ok.HandleAsync(Req("/api/admin/shard/import/preview", body))).Status);
            Assert.Equal(200, (await ok.HandleAsync(Req("/api/admin/shard/import", body))).Status);
            Assert.Equal(400, (await ok.HandleAsync(Req("/api/admin/shard/import", """{"shard":"%%%"}"""))).Status);
            Assert.Equal(413, (await ok.HandleAsync(Req("/api/admin/shard/import", SupervisorAdminRoutes.TooLarge))).Status);
        }
        Assert.Equal((1, 1), (admin.Previews, admin.Imports));
    }

    [Fact]
    public async Task The_live_service_previews_and_applies_a_shard_with_its_own_stores()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-supshard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] shard;
            using (var src = MemoryAdminContext.OpenLocal(Path.Combine(dir, "src.db"), new AgentOptions { KnowledgeDirectory = null }, NullEmbeddingProvider.Instance, llm: null))
            {
                src.Memory.AddNote("PoofImaFox", "likes the cobblestone generator tidy");
                src.Memory.AddNote("kiwi", "Robin prefers the wool sheep pen");
                shard = ShardFile.ToBytes(new ShardExporter(src.Db).Build(new ShardExportRequest("fox",
                    new ShardSelection { Mode = "person", People = ["PoofImaFox"] })));
            }
            var db = Path.Combine(dir, "memory.db");
            var ctx = MemoryAdminContext.OpenLocal(db, new AgentOptions { KnowledgeDirectory = null }, NullEmbeddingProvider.Instance, llm: null);
            var audit = new AdminAudit(Path.Combine(dir, SupervisorAdminService.AuditFile));
            await using var s = Server(new SupervisorAdminService(ctx, audit, "live:test", Path.Combine(dir, "backups")));

            var preview = await s.HandleAsync(Req("/api/admin/shard/import/preview", Body(shard)));
            Assert.Equal(200, preview.Status);
            using (var j = JsonDocument.Parse(preview.Text))
            {
                Assert.Equal(1, j.RootElement.GetProperty("added").GetInt32());
                Assert.Equal("merge", j.RootElement.GetProperty("mode").GetString());
                Assert.Equal("keep-newer", j.RootElement.GetProperty("conflict").GetString());
            }
            Assert.Equal(0, ctx.Db.ScalarLong("SELECT count(*) FROM notes")); // preview writes nothing

            var import = await s.HandleAsync(Req("/api/admin/shard/import", Body(shard)));
            Assert.Equal(200, import.Status);
            Assert.True(JsonDocument.Parse(import.Text).RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, ctx.Db.ScalarLong("SELECT count(*) FROM notes WHERE subject = 'PoofImaFox'"));
            Assert.Equal(0, ctx.Db.ScalarLong("SELECT count(*) FROM notes WHERE subject = 'kiwi'"));
            Assert.Contains(audit.Read(), e => e.Op == "import" && e.Ok);
            Assert.Contains(audit.Read(), e => e.Op == "backup" && e.Ok); // a backup before the write

            var broken = await s.HandleAsync(Req("/api/admin/shard/import", Body("not a shard"u8.ToArray())));
            Assert.Equal(400, broken.Status);
            Assert.Contains("shard refused", broken.Text);
            var swap = await s.HandleAsync(Req("/api/admin/shard/import", Body(shard, "swap")));
            Assert.Equal(400, swap.Status);
            Assert.Contains("knowledge-pack", swap.Text);
            Assert.DoesNotContain(Token, File.ReadAllText(audit.Path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
