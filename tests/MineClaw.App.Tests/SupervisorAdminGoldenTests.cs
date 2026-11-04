using System.Text;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="SupervisorAdminRoutes.HandleAsync"/> (WP07): the gate, every route's method check, body parsing errors, the
/// shard and compaction routes and the error mapping. A refactor must leave <see cref="SupervisorAdminGoldenData.Expected"/> unchanged.
/// </summary>
public class SupervisorAdminGoldenTests
{
    private const string Token = "s3cret-token";

    /// <summary>The fake admin with shards and compaction: a shard of "bad" bytes is refused, "boom" crashes.</summary>
    private sealed class ShardAdmin(FakeAdmin inner) : ISupervisorAdmin
    {
        public readonly List<string> Calls = [];
        public string Target => inner.Target;
        public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct) => inner.ApplyAsync(edit, ct);
        public RebuildEstimate Estimate(RebuildRequest request) { Calls.Add($"estimate {request}"); return inner.Estimate(request); }
        public string StartRebuild(RebuildRequest request) { Calls.Add($"start {request}"); return inner.StartRebuild(request); }
        public AdminJobView? Job(string jobId) => inner.Job(jobId);
        public bool CancelJob(string jobId) => inner.CancelJob(jobId);
        public string Backup(string who) { Calls.Add("backup " + who); return inner.Backup(who); }
        public IReadOnlyList<AuditEntry> Audit(int max) { Calls.Add($"audit {max}"); return inner.Audit(max); }
        public bool SupportsShards => true;

        public ShardDiff PreviewShard(byte[] shard, ShardImportOptions options)
        {
            Calls.Add($"preview {Encoding.UTF8.GetString(shard)} {options.Mode} {options.Conflict} {options.IncludeRules} {options.IncludeWorld} {options.Who} {options.RebuildMaxUsd}");
            throw Encoding.UTF8.GetString(shard) == "boom" ? new InvalidOperationException("crash") : new ShardFormatException("bad magic");
        }

        public Task<ShardImportResult> ImportShardAsync(byte[] shard, ShardImportOptions options, CancellationToken ct)
        {
            Calls.Add($"import {Encoding.UTF8.GetString(shard)}");
            throw new ShardFormatException("not a shard");
        }

        public Task<ActivityCompactionReport> CompactActivityAsync(bool dryRun, string who, CancellationToken ct)
        {
            Calls.Add($"compact {dryRun} {who}");
            if (who == "cancel") throw new OperationCanceledException();
            return Task.FromResult(new ActivityCompactionReport(who != "fail", dryRun, 10, 8, 5, 3, 5, 2, 1, 1, 0, "done", ["a"]));
        }
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static readonly (string Method, string Path, string? Body, string? Query)[] Cases =
    [
        ("GET", "/api/admin", null, null), ("GET", "/api/admin/nothing", null, null),
        ("GET", "/api/admin/memory", null, null), ("POST", "/api/admin/memory", null, null), ("POST", "/api/admin/memory", "[1]", null),
        ("POST", "/api/admin/memory", "{bad", null), ("POST", "/api/admin/memory", """{"op":"update","kind":"fact","id":1,"fields":{"text":"b","n":5,"t":true,"f":false,"z":null},"who":"Fox"}""", null),
        ("POST", "/api/admin/memory", """{"op":"update","kind":"note"}""", null), ("POST", "/api/admin/memory", """{"kind":"fact","fields":{"x":[1]}}""", null),
        ("POST", "/api/admin/MEMORY/", """{"kind":"fact"}""", null),
        ("GET", "/api/admin/rebuild/estimate", null, null), ("POST", "/api/admin/rebuild/estimate", """{"scope":"node","node":7}""", null),
        ("POST", "/api/admin/rebuild/estimate", """{"scope":"kind","kind":"fact","max_usd":"1.5","who":"Fox"}""", null),
        ("POST", "/api/admin/rebuild/estimate", """{"scope":"node","node":"x"}""", null),
        ("GET", "/api/admin/rebuild", null, null), ("POST", "/api/admin/rebuild", """{"scope":"node","node":7}""", null),
        ("GET", "/api/admin/backup", null, null), ("POST", "/api/admin/backup", null, null), ("POST", "/api/admin/backup", """{"who":"Fox"}""", null),
        ("POST", "/api/admin/backup", "{bad", null), ("POST", "/api/admin/backup", """{"who":""}""", null),
        ("POST", "/api/admin/audit", null, null), ("GET", "/api/admin/audit", null, null), ("HEAD", "/api/admin/audit", null, "max=9999"),
        ("GET", "/api/admin/audit", null, "max=0"), ("GET", "/api/admin/audit", null, "max=x"),
        ("GET", "/api/admin/shard/import", null, null), ("POST", "/api/admin/shard/import/preview", null, null),
        ("POST", "/api/admin/shard/import/preview", """{"mode":"merge"}""", null), ("POST", "/api/admin/shard/import/preview", """{"shard":"!!"}""", null),
        ("POST", "/api/admin/shard/import/preview", $$"""{"shard":"{{B64("abc")}}","mode":"replace","conflict":"mine","include_rules":true,"include_world":"true","who":"Fox","max_usd":2}""", null),
        ("POST", "/api/admin/shard/import/preview", $$"""{"shard":"{{B64("boom")}}"}""", null),
        ("POST", "/api/admin/shard/import/preview", $$"""{"shard":"{{B64("abc")}}","mode":"sideways"}""", null),
        ("POST", "/api/admin/shard/import", $$"""{"shard":"{{B64("abc")}}"}""", null),
        ("GET", "/api/admin/activity/compact", null, null), ("POST", "/api/admin/activity/compact", null, null),
        ("POST", "/api/admin/activity/compact", """{"dry_run":true,"who":"Fox"}""", null), ("POST", "/api/admin/activity/compact", """{"dry_run":"yes","who":"fail"}""", null),
        ("POST", "/api/admin/activity/compact", "[1]", null), ("POST", "/api/admin/activity/compact", """{"who":"cancel"}""", null),
        ("POST", "/api/admin/schema", null, null), ("GET", "/api/admin/schema", null, null),
        ("GET", "/api/admin/rebuild/j1", null, null), ("GET", "/api/admin/rebuild/j2", null, null), ("POST", "/api/admin/rebuild/j1", null, null),
        ("POST", "/api/admin/rebuild/j1/cancel", null, null), ("POST", "/api/admin/rebuild/j2/CANCEL", null, null), ("GET", "/api/admin/rebuild/j1/cancel", null, null),
        ("PUT", "/api/admin/memory", "{}", null),
    ];

    internal static async Task<string> Dump()
    {
        var sb = new StringBuilder();
        // The gate, in order.
        foreach (var (label, o, admin, token, body) in new (string, SupervisorOptions, ISupervisorAdmin?, string?, string?)[]
        {
            ("off", new SupervisorOptions { Token = Token }, new FakeAdmin(), Token, null),
            ("no token", new SupervisorOptions { AllowAdminWrites = true }, new FakeAdmin(), Token, null),
            ("wrong token", new SupervisorOptions { Token = Token, AllowAdminWrites = true }, new FakeAdmin(), "x", null),
            ("no header", new SupervisorOptions { Token = Token, AllowAdminWrites = true }, new FakeAdmin(), null, null),
            ("no admin", new SupervisorOptions { Token = Token, AllowAdminWrites = true }, null, Token, null),
            ("too large", new SupervisorOptions { Token = Token, AllowAdminWrites = true }, new FakeAdmin(), Token, SupervisorAdminRoutes.TooLarge),
        })
            RouteGolden.Append(sb, label, await SupervisorAdminRoutes.HandleAsync(new SupervisorRequest("POST", "/api/admin/memory", RouteGolden.Query(null), token, body), o, admin, default));
        var on = new SupervisorOptions { Token = Token, AllowAdminWrites = true };
        RouteGolden.Append(sb, "too large shard", await SupervisorAdminRoutes.HandleAsync(
            new SupervisorRequest("POST", "/api/admin/shard/import", RouteGolden.Query(null), Token, SupervisorAdminRoutes.TooLarge), on, new FakeAdmin(), default));
        RouteGolden.Append(sb, "no shards", await SupervisorAdminRoutes.HandleAsync(
            new SupervisorRequest("POST", "/api/admin/shard/import", RouteGolden.Query(null), Token, "{}"), on, new FakeAdmin(), default));
        RouteGolden.Append(sb, "no compaction", await SupervisorAdminRoutes.HandleAsync(
            new SupervisorRequest("POST", "/api/admin/activity/compact", RouteGolden.Query(null), Token, "{}"), on, new FakeAdmin(), default));
        RouteGolden.Append(sb, "busy", await SupervisorAdminRoutes.HandleAsync(
            new SupervisorRequest("POST", "/api/admin/rebuild", RouteGolden.Query(null), Token, """{"scope":"node","node":7}"""), on, new FakeAdmin { Busy = true }, default));
        var a = new ShardAdmin(new FakeAdmin());
        foreach (var (m, p, body, q) in Cases)
            RouteGolden.Append(sb, $"{m} {p}{(q != null ? "?" + q : "")} {body ?? "-"}",
                await SupervisorAdminRoutes.HandleAsync(new SupervisorRequest(m, p, RouteGolden.Query(q), Token, body), on, a, default), maxBody: 3000);
        sb.Append("calls: ").Append(string.Join(" | ", a.Calls)).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public async Task Admin_routes_match_the_golden_dump() =>
        RouteGolden.Check("admin-routes", SupervisorAdminGoldenData.Expected, await Dump());
}
