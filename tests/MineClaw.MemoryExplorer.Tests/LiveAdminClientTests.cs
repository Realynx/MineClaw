using System.Net;
using System.Net.Sockets;
using System.Text;
using MineClaw.Agent.Admin;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The live edit target speaks the Supervisor's admin JSON (snake_case) with the token header, and turns 403 into a
/// clear message. A tiny HttpListener on localhost plays the Supervisor.</summary>
public sealed class LiveAdminClientTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _tokenFile = Path.GetTempFileName();
    private readonly List<(string Method, string Path, string? Token, string Body)> _seen = [];
    private readonly int _port;
    private bool _forbid;
    private int _polls;

    public LiveAdminClientTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        File.WriteAllText(_tokenFile, "s3cret\n");
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch (Exception) { return; }
            using var r = new StreamReader(ctx.Request.InputStream);
            var body = await r.ReadToEndAsync();
            var path = ctx.Request.Url!.AbsolutePath;
            lock (_seen) _seen.Add((ctx.Request.HttpMethod, path, ctx.Request.Headers["X-Supervisor-Token"], body));
            var (status, json) = _forbid ? (403, """{"error":"admin writes are off"}""") : path switch
            {
                "/api/admin/memory" when body.Contains("\"op\":\"update\"") =>
                    (200, """{"ok":true,"message":"updated","id":"2","before":"{\"text\":\"a\"}","after":"{\"text\":\"b\"}","reembedded":true}"""),
                "/api/admin/memory" => (400, """{"ok":false,"message":"text is required"}"""),
                "/api/admin/rebuild/estimate" => (200, """{"summaries":3,"input_tokens":1200,"output_tokens":750,"embedding_tokens":300,"usd":0.0042,"model":"claude-haiku-5-5","note":"node #7"}"""),
                "/api/admin/rebuild" => (202, """{"job_id":"j1"}"""),
                "/api/admin/rebuild/j1" => Interlocked.Increment(ref _polls) < 2
                    ? (200, """{"job_id":"j1","state":"running","progress":{"stage":"summarize","done":1,"total":3}}""")
                    : (200, """{"job_id":"j1","state":"done","progress":{"stage":"done","done":3,"total":3},"result":{"ok":true,"message":"rebuilt","diffs":[{"index":7,"layer":1,"old":"x","new":"y"}],"spent_usd":0.004,"elapsed":"00:00:02.500"}}"""),
                "/api/admin/backup" => (200, """{"path":"/var/lib/mineclaw/backups/memory-backup-1.db"}"""),
                "/api/admin/audit" => (200, """{"target":"live:maid_chan","entries":[{"at":"2026-10-28T10:00:00Z","who":"admin:poofi","target":"live:maid_chan","op":"update","kind":"fact","id":"2","ok":true,"message":"updated"}]}"""),
                _ => (404, """{"error":"not found"}"""),
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private LiveAdminTarget Target() => new(new SupervisorEndpoint($"http://localhost:{_port}", _tokenFile));

    [Fact]
    public async Task Edits_estimates_rebuilds_and_audit_round_trip_the_supervisor_json()
    {
        using var t = Target();
        var ok = await t.ApplyAsync(new MemoryEdit("update", MemoryKinds.Fact, "2", new Dictionary<string, string?> { ["text"] = "b" }, "admin:test"), CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.True(ok.Reembedded);
        Assert.Contains("\"b\"", ok.After);
        var rejected = await t.ApplyAsync(new MemoryEdit("add", MemoryKinds.Fact, null, new Dictionary<string, string?>(), "admin:test"), CancellationToken.None);
        Assert.False(rejected.Ok);
        Assert.Equal("text is required", rejected.Message);

        var est = await t.EstimateAsync(new RebuildRequest("node", Node: 7), CancellationToken.None);
        Assert.Equal((3, 1200L, 750L, 0.0042m), (est.Summaries, est.InputTokens, est.OutputTokens, est.Usd));

        var states = new List<string>();
        var final = await t.RebuildAsync(new RebuildRequest("node", Node: 7, MaxUsd: 0.01m), new SyncProgress(s => states.Add(s.State)), CancellationToken.None);
        Assert.Equal("done", final.State);
        Assert.Equal(TimeSpan.FromSeconds(2.5), final.Result!.Elapsed);
        Assert.Equal("y", Assert.Single(final.Result.Diffs).New);
        Assert.Contains("running", states);

        Assert.EndsWith("memory-backup-1.db", await t.BackupAsync(CancellationToken.None));
        Assert.Equal("admin:poofi", Assert.Single(await t.AuditAsync(50, CancellationToken.None)).Who);

        lock (_seen)
        {
            Assert.All(_seen, s => Assert.Equal("s3cret", s.Token)); // the header, never the query string
            Assert.All(_seen, s => Assert.DoesNotContain("token", s.Path, StringComparison.OrdinalIgnoreCase));
            var edit = _seen.First(s => s.Path == "/api/admin/memory");
            Assert.Contains("\"kind\":\"fact\"", edit.Body);
            Assert.Contains("\"who\":\"admin:test\"", edit.Body);
            var rebuild = _seen.First(s => s.Path == "/api/admin/rebuild");
            Assert.Contains("\"max_usd\":0.01", rebuild.Body);
        }
    }

    [Fact]
    public async Task A_403_says_admin_writes_are_off_or_the_token_is_wrong()
    {
        _forbid = true;
        using var t = Target();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            t.ApplyAsync(new MemoryEdit("delete", MemoryKinds.Fact, "2", new Dictionary<string, string?>(), "admin:test"), CancellationToken.None));
        Assert.Contains("AllowAdminWrites", ex.Message);
        Assert.DoesNotContain("s3cret", ex.Message);
    }

    private sealed class SyncProgress(Action<RebuildStatus> on) : IProgress<RebuildStatus>
    {
        public void Report(RebuildStatus value) => on(value);
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        File.Delete(_tokenFile);
    }
}
