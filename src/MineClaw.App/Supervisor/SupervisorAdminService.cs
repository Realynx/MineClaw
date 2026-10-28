using System.Collections.Concurrent;
using MineClaw.Agent;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Supervisor;

/// <summary>A background RAPTOR rebuild as the admin endpoints report it.</summary>
/// <param name="State">queued | running | done | failed | cancelled</param>
public sealed record AdminJobView(string JobId, string State, DateTimeOffset Started, DateTimeOffset? Finished, RebuildRequest Request,
    RebuildProgress? Progress, RebuildResult? Result);

/// <summary>What the supervisor's <c>/api/admin/*</c> routes call (the live bot's service, or a fake in tests).</summary>
public interface ISupervisorAdmin
{
    /// <summary>"live:{bot}" or "local:{path}".</summary>
    string Target { get; }
    Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct);
    RebuildEstimate Estimate(RebuildRequest request);
    /// <summary>Starts a rebuild in the background and returns its job id. Throws <see cref="InvalidOperationException"/> while
    /// another one runs.</summary>
    string StartRebuild(RebuildRequest request);
    AdminJobView? Job(string jobId);
    bool CancelJob(string jobId);
    /// <summary>A consistent db copy (VACUUM INTO) in the bot's data dir; returns its path.</summary>
    string Backup(string who);
    IReadOnlyList<AuditEntry> Audit(int max);

    /// <summary>True when the memory shard routes (<c>/api/admin/shard/import[/preview]</c>) are served.</summary>
    bool SupportsShards => false;
    /// <summary>The import diff of a <c>.mcshard</c> (nothing is written). Throws <see cref="ShardFormatException"/> on a bad shard.</summary>
    ShardDiff PreviewShard(byte[] shard, ShardImportOptions options) => throw new NotSupportedException("memory shards are not available in this process");
    /// <summary>Applies the shard inside the bot with its own stores, then starts the scoped RAPTOR rebuild in the background.</summary>
    Task<ShardImportResult> ImportShardAsync(byte[] shard, ShardImportOptions options, CancellationToken ct) =>
        throw new NotSupportedException("memory shards are not available in this process");

    /// <summary>The one-off activity compaction inside the bot (dry = report only; a real run backs up first).</summary>
    Task<ActivityCompactionReport> CompactActivityAsync(bool dryRun, string who, CancellationToken ct) =>
        throw new NotSupportedException("the activity compaction is not available in this process");
}

/// <summary>
/// The admin-write service of a running bot: edits and RAPTOR rebuilds through the bot's own stores
/// (<see cref="MemoryAdminContext.FromRuntime"/>), under its own budget caps, with the audit trail next to its db
/// (<c>admin-audit.jsonl</c>) and in the app log. One rebuild job at a time; the last 20 jobs are kept for polling.
/// </summary>
public sealed class SupervisorAdminService : ISupervisorAdmin, IDisposable
{
    public const string AuditFile = "admin-audit.jsonl";
    private const int KeepJobs = 20;

    private readonly MemoryAdminContext _ctx;
    private readonly MemoryAdmin _admin;
    private readonly RaptorSectionRebuilder _rebuilder;
    private readonly ShardImporter _shards;
    private readonly AdminAudit _audit;
    private readonly string _backupDir;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, RebuildJob> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();
    private readonly object _startLock = new();

    private sealed class RebuildJob(string id, RebuildRequest request)
    {
        public string Id { get; } = id;
        public RebuildRequest Request { get; } = request;
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? Finished { get; set; }
        public volatile string State = "queued";
        public RebuildProgress? Progress { get; set; }
        public RebuildResult? Result { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public AdminJobView View() => new(Id, State, Started, Finished, Request, Progress, Result);
    }

    public SupervisorAdminService(MemoryAdminContext ctx, AdminAudit audit, string target, string backupDirectory, ILogger? log = null)
    {
        _ctx = ctx;
        _audit = audit;
        Target = target;
        _backupDir = backupDirectory;
        _log = log ?? NullLogger.Instance;
        _admin = new MemoryAdmin(ctx, audit, target);
        _rebuilder = new RaptorSectionRebuilder(ctx, audit, target);
        _shards = new ShardImporter(ctx, audit, target);
    }

    /// <summary>
    /// The running bot's service, or null when <see cref="SupervisorOptions.AllowAdminWrites"/> is off (nothing is built then).
    /// With the flag on but no token, it is not built either (the routes stay 403) and a warning is logged.
    /// </summary>
    public static SupervisorAdminService? Create(SupervisorOptions o, AgentRuntime rt, string bot, ILogger log)
    {
        if (!o.AllowAdminWrites) return null;
        if (string.IsNullOrEmpty(o.Token))
        {
            log.LogWarning("supervisor: AllowAdminWrites needs Supervisor:Token; admin writes stay off");
            return null;
        }
        var ctx = MemoryAdminContext.FromRuntime(rt, log: log);
        var dir = Path.GetDirectoryName(Path.GetFullPath(rt.Db.Path ?? Path.Combine(rt.Options.DataDirectory, "x"))) ?? rt.Options.DataDirectory;
        var audit = new AdminAudit(Path.Combine(dir, AuditFile), log);
        log.LogInformation("supervisor: admin writes ON (token required; audit {Audit})", audit.Path);
        return new SupervisorAdminService(ctx, audit, "live:" + bot, Path.Combine(dir, "backups"), log);
    }

    public string Target { get; }
    public AdminAudit AuditTrail => _audit;

    public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct) => _admin.ApplyAsync(edit, ct);

    public RebuildEstimate Estimate(RebuildRequest request) => _rebuilder.Estimate(request);

    public async Task<ActivityCompactionReport> CompactActivityAsync(bool dryRun, string who, CancellationToken ct)
    {
        var compaction = ActivityCompaction.For(_ctx);
        if (dryRun) return await compaction.RunAsync(true, ct).ConfigureAwait(false);
        var preview = await compaction.RunAsync(true, ct).ConfigureAwait(false);
        if (preview.Processed == 0) return preview;
        var backup = Backup(who);
        var report = await compaction.RunAsync(false, ct, backup).ConfigureAwait(false);
        _audit.Write(new AuditEntry(DateTimeOffset.UtcNow, who, Target, "compact", "activity", null, $"{report.ActivityBefore} activity docs",
            $"{report.ActivityAfter} activity docs", null, report.Ok, report.Message));
        return report;
    }

    public string StartRebuild(RebuildRequest request) => StartJob([request], null);

    /// <summary>One background job running <paramref name="requests"/> in order (a shard import's scoped rebuilds), results merged.</summary>
    private string StartJob(IReadOnlyList<RebuildRequest> requests, IReadOnlyDictionary<string, string>? reuse)
    {
        RebuildJob job;
        lock (_startLock)
        {
            if (_jobs.Values.FirstOrDefault(j => j.State is "queued" or "running") is { } busy)
                throw new InvalidOperationException($"a rebuild is already running (job {busy.Id})");
            job = new RebuildJob(Guid.NewGuid().ToString("N")[..12], requests[0]);
            _jobs[job.Id] = job;
            foreach (var old in _jobs.Values.Where(j => j.Finished != null).OrderByDescending(j => j.Started).Skip(KeepJobs).ToList())
                _jobs.TryRemove(old.Id, out _);
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, job.Cts.Token);
        var rebuilder = reuse == null ? _rebuilder : new RaptorSectionRebuilder(_ctx, _audit, Target) { ReuseSummaries = reuse };
        _ = Task.Run(async () =>
        {
            try
            {
                job.State = "running";
                var progress = new Progress(p => job.Progress = p);
                RebuildResult? result = null;
                foreach (var request in requests)
                {
                    var r = await rebuilder.RunAsync(request, progress, linked.Token).ConfigureAwait(false);
                    result = result == null ? r : result with
                    {
                        Ok = result.Ok && r.Ok, Message = result.Message + " | " + r.Message, Diffs = [.. result.Diffs, .. r.Diffs],
                        SpentUsd = result.SpentUsd + r.SpentUsd, Elapsed = result.Elapsed + r.Elapsed,
                    };
                    if (linked.IsCancellationRequested) break;
                }
                job.Result = result;
                job.State = result!.Ok ? "done" : linked.IsCancellationRequested ? "cancelled" : "failed";
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "admin rebuild job {Job} failed", job.Id);
                job.Result = new RebuildResult(false, ex.Message, [], 0, DateTimeOffset.UtcNow - job.Started);
                job.State = linked.IsCancellationRequested ? "cancelled" : "failed";
            }
            finally
            {
                job.Finished = DateTimeOffset.UtcNow;
                linked.Dispose();
            }
        });
        return job.Id;
    }

    public AdminJobView? Job(string jobId) => _jobs.TryGetValue(jobId, out var j) ? j.View() : null;

    public bool CancelJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var j) || j.Finished != null) return false;
        j.Cts.Cancel();
        return true;
    }

    public string Backup(string who)
    {
        try
        {
            var path = AdminBackup.Create(_ctx.Db, _backupDir);
            _audit.Write(new AuditEntry(DateTimeOffset.UtcNow, who, Target, "backup", "db", null, null, path, null, true, "backup written"));
            return path;
        }
        catch (Exception ex)
        {
            _audit.Write(new AuditEntry(DateTimeOffset.UtcNow, who, Target, "backup", "db", null, null, null, null, false, ex.Message));
            throw;
        }
    }

    public IReadOnlyList<AuditEntry> Audit(int max) => _audit.Read(max);

    // ------------------------------------------------------------------ memory shards

    public bool SupportsShards => true;

    public ShardDiff PreviewShard(byte[] shard, ShardImportOptions options) => _shards.Preview(ShardFile.Read(shard), options);

    /// <summary>
    /// Applies the shard with the bot's own stores (under the edit gate), signals RAPTOR maintenance (the new leaves go in at zero
    /// tokens), then runs the import's scoped section rebuilds as one background job capped per section (a section over its cap
    /// stays dirty for the regular maintenance budget). A backup is taken first.
    /// </summary>
    public async Task<ShardImportResult> ImportShardAsync(byte[] shard, ShardImportOptions options, CancellationToken ct)
    {
        var s = ShardFile.Read(shard);
        Backup(options.Who);
        var cap = Math.Min(options.RebuildMaxUsd, Math.Max(0.01m, _ctx.Options.Rag.Raptor.RebuildMaxUsd));
        var result = await _shards.ApplyAsync(s, options with { RebuildMaxUsd = cap }, ct).ConfigureAwait(false);
        if (!result.Ok || result.Rebuild.Count == 0) return result;
        try { return result with { JobId = StartJob(result.Rebuild.Select(r => r with { MaxUsd = cap }).ToList(), ShardImporter.ReuseMap(s)) }; }
        catch (InvalidOperationException ex) { return result with { Message = result.Message + "; rebuild not started: " + ex.Message + " (maintenance refreshes it)" }; }
    }

    public void Dispose()
    {
        _life.Cancel();
        _ctx.Dispose(); // borrows the runtime's stores: owns nothing, disposes nothing
    }

    private sealed class Progress(Action<RebuildProgress> on) : IProgress<RebuildProgress>
    {
        public void Report(RebuildProgress value) => on(value);
    }
}
