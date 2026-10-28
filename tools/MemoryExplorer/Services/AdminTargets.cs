using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Llm;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>A rebuild job's state as the UI shows it (local runs report through the same shape).</summary>
public sealed record RebuildStatus(string State, RebuildProgress? Progress, RebuildResult? Result, string? Error = null)
{
    public bool Done => State is "done" or "failed" or "cancelled";
}

/// <summary>
/// Where edits and rebuilds go. Both targets take the same requests and go through the bot's own stores (see
/// <see cref="MemoryAdmin"/> / <see cref="RaptorSectionRebuilder"/>): locally in this process on a db file, or remotely inside the
/// running bot through the Supervisor's token-protected admin endpoints.
/// </summary>
public interface IAdminTarget : IDisposable
{
    string Name { get; }
    /// <summary>"local: data/alice-snap/memory-….db" / "live: http://10.1.2.73:8765".</summary>
    string Describe { get; }
    Task<string> BackupAsync(CancellationToken ct);
    Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct);
    Task<RebuildEstimate> EstimateAsync(RebuildRequest r, CancellationToken ct);
    /// <summary>Runs (local) or starts and polls (live) a rebuild, reporting status until it is done.</summary>
    Task<RebuildStatus> RebuildAsync(RebuildRequest r, IProgress<RebuildStatus> progress, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> AuditAsync(int max, CancellationToken ct);
}

/// <summary>
/// Edits a LOCAL db file directly (the default for snapshots): the bot's stores opened read-write in this process
/// (<see cref="MemoryAdminContext.OpenLocal"/>), audit JSONL next to the db, backups under <c>backups/</c> beside it.
/// Summaries for a rebuild come from the cheap tier (5.5 generation only, enforced) with the configured key.
/// </summary>
public sealed class LocalAdminTarget : IAdminTarget
{
    private readonly MemoryAdminContext _ctx;
    private readonly MemoryAdmin _admin;
    private readonly RaptorSectionRebuilder _rebuilder;

    public LocalAdminTarget(string dbPath, ExplorerConfig config, Agent.Rag.IEmbeddingProvider? embeddings = null, ILlmClient? llm = null)
    {
        DbPath = Path.GetFullPath(dbPath);
        var options = config.Agent;
        llm ??= config.LlmConfigured && LlmProblems(config).Count == 0 ? LlmClientFactory.Create(options.Llm) : null;
        _ctx = MemoryAdminContext.OpenLocal(DbPath, options, embeddings ?? config.CreateEmbeddings(), llm);
        Audit = new AdminAudit(AuditPathFor(DbPath));
        var target = "local:" + DbPath;
        _admin = new MemoryAdmin(_ctx, Audit, target);
        _rebuilder = new RaptorSectionRebuilder(_ctx, Audit, target);
    }

    public string DbPath { get; }
    public AdminAudit Audit { get; }
    public MemoryAdminContext Context => _ctx;
    public string Name => "Local file";
    public string Describe => "local: " + ExplorerPaths.Display(DbPath);

    public static string AuditPathFor(string dbPath) => Path.ChangeExtension(Path.GetFullPath(dbPath), null) + ".admin-audit.jsonl";
    public static string BackupDirFor(string dbPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "backups");

    /// <summary>Config problems that block summary rebuilds (the 5.5-generation rule, providers).</summary>
    public static IReadOnlyList<string> LlmProblems(ExplorerConfig config) =>
        config.Agent.Llm.ValidateProviders().Where(p => p.Contains("CheapModel", StringComparison.Ordinal) || p.Contains("CheapProvider", StringComparison.Ordinal)).ToList();

    public Task<string> BackupAsync(CancellationToken ct) => Task.Run(() => AdminBackup.Create(_ctx.Db, BackupDirFor(DbPath)), ct);

    public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct) => _admin.ApplyAsync(edit, ct);

    public Task<RebuildEstimate> EstimateAsync(RebuildRequest r, CancellationToken ct) => Task.Run(() => _rebuilder.Estimate(r), ct);

    public async Task<RebuildStatus> RebuildAsync(RebuildRequest r, IProgress<RebuildStatus> progress, CancellationToken ct)
    {
        progress.Report(new RebuildStatus("running", new RebuildProgress("starting", 0, 1), null));
        var p = new Progress<RebuildProgress>(x => progress.Report(new RebuildStatus("running", x, null)));
        try
        {
            var result = await Task.Run(() => _rebuilder.RunAsync(r, p, ct), ct);
            var s = new RebuildStatus(result.Ok ? "done" : "failed", null, result);
            progress.Report(s);
            return s;
        }
        catch (OperationCanceledException)
        {
            var s = new RebuildStatus("cancelled", null, null, "cancelled");
            progress.Report(s);
            return s;
        }
    }

    public Task<IReadOnlyList<AuditEntry>> AuditAsync(int max, CancellationToken ct) => Task.FromResult(Audit.Read(max));

    public void Dispose() => _ctx.Dispose();
}

/// <summary>
/// Edits the LIVE bot on alice through the Supervisor's admin endpoints (POST, <c>X-Supervisor-Token</c>, refused unless the bot
/// runs with <c>Supervisor:AllowAdminWrites=true</c>). The running bot applies everything itself with its own stores, budget caps
/// and audit trail; this process never touches the bot's db file.
/// </summary>
public sealed class LiveAdminTarget(SupervisorEndpoint endpoint) : IAdminTarget
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = endpoint.CreateClient(TimeSpan.FromSeconds(60));

    public string Name => "Live alice";
    public string Describe => "live: " + endpoint.BaseUrl + " (the bot applies the edit)";

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("the Supervisor refused (403): admin writes are off on the bot (Supervisor:AllowAdminWrites) or the token is wrong");
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("the Supervisor wants a token (~/.mineclaw-supervisor-token)");
        // A rejected edit answers 400 with the same result body (ok=false, message).
        if (res.StatusCode == HttpStatusCode.BadRequest && typeof(T) == typeof(MemoryEditResult) && text.Contains("\"ok\"", StringComparison.Ordinal))
            return JsonSerializer.Deserialize<T>(text, Json)!;
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Supervisor {(int)res.StatusCode}: {ErrorOf(text)}");
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new InvalidDataException("empty answer from the Supervisor");
    }

    private static string ErrorOf(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e)) return e.ToString();
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }

    private sealed record BackupReply(string Path);
    private sealed record JobReply(string JobId);
    private sealed record JobStatus(string? State, RebuildProgress? Progress, RebuildResult? Result, string? Error);
    private sealed record AuditReply(IReadOnlyList<AuditEntry>? Entries);

    public async Task<string> BackupAsync(CancellationToken ct) => (await SendAsync<BackupReply>(HttpMethod.Post, "api/admin/backup", new { }, ct)).Path;

    public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct) => SendAsync<MemoryEditResult>(HttpMethod.Post, "api/admin/memory", edit, ct);

    public Task<RebuildEstimate> EstimateAsync(RebuildRequest r, CancellationToken ct) => SendAsync<RebuildEstimate>(HttpMethod.Post, "api/admin/rebuild/estimate", r, ct);

    public async Task<RebuildStatus> RebuildAsync(RebuildRequest r, IProgress<RebuildStatus> progress, CancellationToken ct)
    {
        var job = await SendAsync<JobReply>(HttpMethod.Post, "api/admin/rebuild", r, ct);
        progress.Report(new RebuildStatus("running", new RebuildProgress("started in the bot", 0, 1), null));
        using var onCancel = ct.Register(() =>
        {
            // Ask the bot to cancel the job (best effort; the job also stops at its own cost cap).
            try { _ = _http.PostAsync("api/admin/rebuild/" + Uri.EscapeDataString(job.JobId) + "/cancel", JsonContent.Create(new { }), CancellationToken.None); }
            catch (Exception) { }
        });
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            var s = await SendAsync<JobStatus>(HttpMethod.Get, "api/admin/rebuild/" + Uri.EscapeDataString(job.JobId), null, ct);
            var status = new RebuildStatus(s.State ?? "running", s.Progress, s.Result, s.Error);
            progress.Report(status);
            if (status.Done) return status;
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> AuditAsync(int max, CancellationToken ct) =>
        (await SendAsync<AuditReply>(HttpMethod.Get, $"api/admin/audit?max={max}", null, ct)).Entries ?? [];

    public void Dispose() => _http.Dispose();
}
