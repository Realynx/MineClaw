using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MineClaw.Agent;
using MineClaw.ViewerBridge.Data;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>A request the fake supervisor saw.</summary>
/// <param name="User">The identity headers the node proxy forwards (<c>X-MCX-User</c>, <c>X-MCX-Roles</c>); null when absent.</param>
public sealed record SeenRequest(string Method, string Target, string? Token, string Body, string? User = null, string? Roles = null);

/// <summary>
/// A stand-in for the bot's Supervisor on a free loopback port: canned replies per path (status, viewer routes, admin, world) and
/// a log of every request (method, target, token header, body).
/// </summary>
public sealed class FakeSupervisor : IAsyncDisposable
{
    private readonly WebApplication _app;
    public ConcurrentQueue<SeenRequest> Seen { get; } = new();
    /// <summary>path (no query) → (status, content type, body); a func of the query for cursor routes.</summary>
    public ConcurrentDictionary<string, Func<HttpRequest, (int Status, string Type, byte[] Body)>> Routes { get; } = new(StringComparer.Ordinal);
    public string Url { get; }

    private FakeSupervisor(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public static (int, string, byte[]) Json(string json, int status = 200) => (status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

    public static async Task<FakeSupervisor> StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = b.Build();
        FakeSupervisor? self = null;
        app.Run(async ctx =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var body = await sr.ReadToEndAsync();
            var target = ctx.Request.Path.Value + ctx.Request.QueryString.Value;
            self!.Seen.Enqueue(new SeenRequest(ctx.Request.Method, target, ctx.Request.Headers["X-Supervisor-Token"].FirstOrDefault(), body,
                ctx.Request.Headers["X-MCX-User"].FirstOrDefault(), ctx.Request.Headers["X-MCX-Roles"].FirstOrDefault()));
            if (self.Routes.TryGetValue(ctx.Request.Path.Value ?? "", out var h))
            {
                var (status, type, bytes) = h(ctx.Request);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = type;
                await ctx.Response.Body.WriteAsync(bytes);
                return;
            }
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("""{"error":"not found"}""");
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeSupervisor(app, url.Replace("[::1]", "127.0.0.1"));
        self.Defaults();
        return self;
    }

    private void Defaults()
    {
        Routes["/api/status"] = _ => Json("""{"at":"2026-11-02T10:00:00+00:00","main":"maid_chan","bots":[{"name":"maid_chan","status":"online","at":"2026-11-02T10:00:00+00:00","pos":"1,64,2","dim":0,"hp":20,"food":20,"budget_mode":"normal","chat":[]}],"claims":[]}""");
        Routes["/api/bots"] = _ => Json("""{"at":"2026-11-02T10:00:00+00:00","bots":[]}""");
        Routes["/api/viewer/now"] = _ => Json("""{"bot":"maid_chan","at":"2026-11-02T10:00:00+00:00","brain_state":"awake","in_flight":null,"thought":null,"tool":null,"task":"mining","script":null,"body":{"hp":20,"food":19,"pos":"1,64,-2","dim":0,"held":"stone_pickaxe"}}""");
        Routes["/api/viewer/chat"] = _ => Json("""{"accepted":true,"id":17}""");
        Routes["/api/admin/memory"] = _ => Json("""{"ok":true,"message":"added","id":"5","reembedded":true}""");
        Routes["/api/hive/tree"] = _ => Json("""{"id":"main","name":"maid_chan","kind":"main","state":"running","children":[]}""");
        Routes["/api/events"] = _ => Json("""{"events":[],"last":0}""");
        Routes["/api/viewer/feed"] = _ => Json("""{"lines":[],"last":0}""");
        Routes["/api/world/entities"] = _ => Json("""{"at":1762077600000,"rev":1,"full":true,"bots":[],"entities":[],"removed":[]}""");
        Routes["/api/world/blocks"] = _ => Json("""{"at":1762077600000,"rev":0,"full":true,"more":false,"blocks":{},"columns":[]}""");
        Routes["/api/viewer/memory-activity"] = _ => Json("""{"events":[],"in_context":[],"last":0}""");
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>The bridge on a free loopback port in front of a <see cref="FakeSupervisor"/>, plus a signing client.</summary>
public sealed class BridgeHost : IAsyncDisposable
{
    public const string Key = SigningTests.Key;
    private readonly WebApplication _app;
    public HttpClient Http { get; }
    public FakeSupervisor Sup { get; }
    public string BaseUrl { get; }
    public IServiceProvider Services => _app.Services;

    private BridgeHost(WebApplication app, FakeSupervisor sup, string url)
    {
        _app = app;
        Sup = sup;
        BaseUrl = url;
        Http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
    }

    public static async Task<BridgeHost> StartAsync(string? dbPath = null, MineClaw.Agent.Rag.IEmbeddingProvider? embeddings = null)
    {
        var sup = await FakeSupervisor.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = BridgeConfig.ForTests(new BridgeOptions
        {
            Key = Key, SupervisorUrl = sup.Url, DbPath = dbPath ?? Path.Combine(dir, "missing.db"), AuditLog = Path.Combine(dir, "audit.jsonl"),
            PacksDir = Path.Combine(dir, "packs"), ServerVersion = "b1.8.1",
        });
        var app = BridgeApp.Build([], config, s =>
        {
            if (embeddings != null)
            {
                s.AddSingleton(sp => new BankHost(sp.GetRequiredService<BridgeConfig>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BankHost>>()) { Embeddings = embeddings });
            }
        }, port: 0);
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new BridgeHost(app, sup, url);
    }

    public string AuditLog => Services.GetRequiredService<BridgeConfig>().AuditLog;

    /// <summary>A signed request exactly as Nest sends it (the raw target as written).</summary>
    public HttpRequestMessage Signed(string method, string target, byte[]? body = null, string user = "fox", string player = "poofimafox",
        string roles = "admin,chat,commands,viewer", long? ts = null, string? nonce = null, string? contentType = "application/json", string key = Key)
    {
        body ??= [];
        ts ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        nonce ??= Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var tsText = ts.Value.ToString(CultureInfo.InvariantCulture);
        var sig = BridgeSigning.Sign(key, BridgeSigning.SigningString(method, target, tsText, nonce, BridgeSigning.Sha256Hex(body), user, player, roles));
        var req = new HttpRequestMessage(new HttpMethod(method), target);
        req.Headers.Add(BridgeSigning.TsHeader, tsText);
        req.Headers.Add(BridgeSigning.NonceHeader, nonce);
        req.Headers.Add(BridgeSigning.SigHeader, sig);
        req.Headers.Add(BridgeSigning.UserHeader, user);
        req.Headers.Add(BridgeSigning.PlayerHeader, player);
        req.Headers.Add(BridgeSigning.RolesHeader, roles);
        if (body.Length > 0 || method == "POST")
        {
            req.Content = new ByteArrayContent(body);
            if (contentType != null) req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }
        return req;
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage req) => Http.SendAsync(req);

    public async Task<(int Status, string Body)> GetAsync(string target, string roles = "admin,chat,commands,viewer")
    {
        using var res = await Http.SendAsync(Signed("GET", target, roles: roles));
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    public async Task<(int Status, string Body)> PostAsync(string target, string json, string roles = "admin,chat,commands,viewer", string user = "fox", string player = "poofimafox")
    {
        using var res = await Http.SendAsync(Signed("POST", target, Encoding.UTF8.GetBytes(json), user: user, player: player, roles: roles));
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
        await Sup.DisposeAsync();
    }
}
