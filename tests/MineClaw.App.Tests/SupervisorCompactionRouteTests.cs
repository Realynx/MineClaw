using MineClaw.Agent.Admin;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>POST /api/admin/activity/compact: token-gated like every admin write, dry runs pass through.</summary>
public class SupervisorCompactionRouteTests
{
    private const string Token = "s3cret-token";

    private sealed class CompactAdmin : ISupervisorAdmin
    {
        public readonly List<(bool Dry, string Who)> Calls = [];
        public string Target => "live:test";
        public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct) => throw new NotSupportedException();
        public RebuildEstimate Estimate(RebuildRequest request) => throw new NotSupportedException();
        public string StartRebuild(RebuildRequest request) => throw new NotSupportedException();
        public AdminJobView? Job(string jobId) => null;
        public bool CancelJob(string jobId) => false;
        public string Backup(string who) => "/b.db";
        public IReadOnlyList<AuditEntry> Audit(int max) => [];

        public Task<ActivityCompactionReport> CompactActivityAsync(bool dryRun, string who, CancellationToken ct)
        {
            Calls.Add((dryRun, who));
            return Task.FromResult(new ActivityCompactionReport(true, dryRun, 6601, 3300, 3362, 61, 3362, 3301, 61, 61, 400, "compacted", []));
        }
    }

    private static SupervisorServer Server(ISupervisorAdmin admin) =>
        new(new SupervisorOptions { Token = Token, AllowAdminWrites = true }, new FakeSupervisorData()) { Admin = admin };

    private static SupervisorRequest Req(string body, string? token = Token) =>
        new("POST", "/api/admin/activity/compact", new Dictionary<string, string>(), token, body);

    [Fact]
    public async Task Compaction_runs_inside_the_bot_and_is_refused_without_the_token()
    {
        var admin = new CompactAdmin();
        await using var s = Server(admin);
        Assert.Equal(403, (await s.HandleAsync(Req("{}", token: null))).Status);
        Assert.Empty(admin.Calls);
        var dry = await s.HandleAsync(Req("""{"dry_run":true,"who":"Fox"}"""));
        Assert.Equal(200, dry.Status);
        var real = await s.HandleAsync(Req("""{"who":"Fox"}"""));
        Assert.Equal(200, real.Status);
        Assert.Equal([(true, "Fox"), (false, "Fox")], admin.Calls);
        Assert.Contains("\"activity_after\":61", System.Text.Encoding.UTF8.GetString(real.Body));
    }

    [Fact]
    public async Task A_process_without_the_compaction_answers_an_error_not_a_crash()
    {
        await using var s = Server(new FakeAdmin());
        Assert.Equal(500, (await s.HandleAsync(Req("{}"))).Status);
    }
}
