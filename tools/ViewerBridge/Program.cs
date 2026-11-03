using System.Net;
using MineClaw.ViewerBridge;
using MineClaw.ViewerBridge.Atlas;
using MineClaw.ViewerBridge.Data;
using MineClaw.ViewerBridge.Endpoints;
using MineClaw.ViewerBridge.Live;
using MineClaw.ViewerBridge.Security;
using MineClaw.ViewerBridge.Supervisor;

var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
var config = BridgeConfig.Load(args, env);
var app = BridgeApp.Build(args, config);
app.Logger.LogInformation("MineClaw viewer bridge on http://{Bind}:{Port} ({Describe})", config.Bridge.Bind, config.Bridge.Port, config.Describe());
if (string.IsNullOrEmpty(config.Bridge.Key) || config.Bridge.Key.Length < 32)
{
    app.Logger.LogCritical("Bridge:Key is missing or shorter than 32 characters (set it in {File} or MINECLAW_BRIDGE_KEY); refusing to start", BridgeOptions.DefaultKeyFile);
    return 2;
}
if (!config.Bridge.Bind.Equals("127.0.0.1", StringComparison.Ordinal) && !config.Bridge.Bind.Equals("::1", StringComparison.Ordinal) && !config.Bridge.Bind.Equals("localhost", StringComparison.OrdinalIgnoreCase))
    app.Logger.LogWarning("Bridge:Bind is {Bind}: the bridge still answers loopback sources only", config.Bridge.Bind);
app.Run();
return 0;

namespace MineClaw.ViewerBridge
{
    public static class BridgeApp
    {
        /// <param name="port">Overrides Bridge:Port (tests pass 0 for a free port).</param>
        public static WebApplication Build(string[] args, BridgeConfig config, Action<IServiceCollection>? configure = null, int? port = null)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = BridgeAuthMiddleware.UploadBodyLimit;
                k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
                k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
                var ip = config.Bridge.Bind.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(config.Bridge.Bind);
                k.Listen(ip, port ?? config.Bridge.Port);
            });
            builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
            {
                o.MultipartBodyLengthLimit = BridgeAuthMiddleware.UploadBodyLimit;
                o.ValueLengthLimit = 64 * 1024;
            });
            var s = builder.Services;
            s.AddSingleton(config);
            s.AddSingleton(TimeProvider.System);
            s.AddSingleton<NonceCache>(sp => new NonceCache(sp.GetRequiredService<TimeProvider>()));
            s.AddSingleton<BridgeAudit>();
            s.AddSingleton<BankHost>();
            s.AddSingleton<SupervisorClient>(sp => new SupervisorClient(sp.GetRequiredService<BridgeConfig>()));
            s.AddSingleton<ChunkCache>();
            s.AddSingleton<MemoryActivityStore>();
            s.AddSingleton<LiveHub>();
            s.AddSingleton<AtlasService>();
            configure?.Invoke(s);

            var app = builder.Build();
            app.UseMiddleware<BridgeAuthMiddleware>();
            app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers.XContentTypeOptions = "nosniff";
                try { await next(ctx); }
                catch (Exception ex) when (!ctx.Response.HasStarted)
                {
                    var (status, code, msg) = ex switch
                    {
                        BankUnavailableException b => (503, "upstream_unavailable", b.Message),
                        SupervisorUnavailableException u => (503, "upstream_unavailable", u.Message),
                        InvalidDataException => (413, "payload_too_large", "body too large"),
                        ArgumentException a => (400, "bad_request", a.Message),
                        FormatException => (400, "bad_request", "malformed request"),
                        KeyNotFoundException k => (404, "not_found", k.Message),
                        OperationCanceledException when ctx.RequestAborted.IsCancellationRequested => (499, "internal", "cancelled"),
                        _ => (500, "internal", "internal error"),
                    };
                    if (status == 500) app.Logger.LogError(ex, "unhandled error on {Path}", ctx.Request.Path.Value);
                    if (status != 499) await ApiErrors.Write(ctx, status, code, msg);
                }
            });
            BridgeEndpoints.Map(app);
            AtlasEndpoints.Map(app);
            app.MapFallback((HttpContext ctx) => ApiErrors.NotFound());
            return app;
        }
    }
}
