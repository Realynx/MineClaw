using System.Text;
using System.Text.Json;
using MineClaw.App.Hosting;
using MineClaw.App.Supervisor;
using MineClaw.App.Supervisor.Admin;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="AdminSettingsRoutes.HandleAsync"/> (WP07): the gate (flag, token, role, user, size), every route's methods,
/// the save/test/restart validation texts, the Ollama model list, the rate limits (fixed clock) and the error mapping. Bodies the
/// service builds (the registry view, node, audit, safety) print their keys; the temp directory prints as &lt;dir&gt;.
/// </summary>
public sealed class AdminSettingsRouteGoldenTests : IDisposable
{
    private const string Token = "sup-token-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-admin-golden-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (AdminSettingsRoutes Routes, FakeProbe Probe, List<string> Restarts, ManualClock Clock) Make(bool restartable = true, int writes = 12, int probes = 30)
    {
        Directory.CreateDirectory(_dir);
        var basePath = Path.Combine(_dir, "appsettings.json");
        var overridePath = Path.Combine(_dir, "state", "settings.override.json");
        File.WriteAllText(basePath, JsonSerializer.Serialize(new
        {
            Minecraft = new { Host = "10.1.2.69", Port = 25565, Username = "maid_chan" },
            Agent = new
            {
                DataDirectory = Path.Combine(_dir, "state"),
                Llm = new { ApiKey = "sk-ant-base-secret-AAAA1111", MainModel = "claude-opus-5-5", Ollama = new { Endpoint = "http://10.0.0.5:11434", ApiKey = "olk-1" } },
            },
            Supervisor = new { Enabled = true, Token, AllowAdminWrites = true },
        }));
        var env = new Dictionary<string, string?> { [SettingsOverrideFile.EnvVar] = overridePath };
        var config = AppConfig.Build(AppCli.Parse(["--config", basePath]), _dir, env);
        var live = AppSettings.Bind(config, _dir);
        var probe = new FakeProbe();
        var restarts = new List<string>();
        var roots = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = live.Agent, ["Supervisor"] = live.Supervisor, ["Host"] = live.Host, ["Hive"] = live.Hive, ["Telemetry"] = live.Telemetry,
            ["Minecraft"] = live.Mc, ["Bodies"] = live.Bodies,
        };
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var svc = new AdminSettingsService(config, new SettingsOverrideStore(overridePath, keep: 3), roots, new AdminNodeInfo("local", "maid_chan", "1.0", null, "b1.8.1"),
            probe, new FakeTester(), restartable ? why => { lock (restarts) restarts.Add(why); } : null, new ListLogger(), time: clock, env: _ => null);
        return (new AdminSettingsRoutes(live.Supervisor, svc, new ListLogger(), clock, writes, probes) { RestartDelay = TimeSpan.FromHours(1) }, probe, restarts, clock);
    }

    private static SupervisorRequest Req(string method, string path, string? body = null, string? roles = "viewer,admin", string? user = "fox",
        string? token = Token, string? query = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (user != null) headers["X-MCX-User"] = user;
        if (roles != null) headers["X-MCX-Roles"] = roles;
        return new SupervisorRequest(method, path, RouteGolden.Query(query), token, body, headers);
    }

    private static readonly (string Method, string Path, string? Body, string? Query)[] Cases =
    [
        ("GET", "/api/admin/settings", null, null), ("HEAD", "/api/admin/settings/", null, null), ("POST", "/api/admin/settings", "{}", null),
        ("DELETE", "/api/admin/settings", null, null), ("PUT", "/api/admin/settings", null, null), ("PUT", "/api/admin/settings", "[1]", null),
        ("PUT", "/api/admin/settings", "{bad", null), ("PUT", "/api/admin/settings", """{"changes":5}""", null),
        ("PUT", "/api/admin/settings", """{"changes":[],"reason":"ok"}""", null), ("PUT", "/api/admin/settings", """{"changes":[],"reason":5}""", null),
        ("PUT", "/api/admin/settings", """{"changes":[5],"reason":"because"}""", null), ("PUT", "/api/admin/settings", """{"changes":[{"value":1}],"reason":"because"}""", null),
        ("PUT", "/api/admin/settings", """{"changes":[{"key":"Nope:Nothing","value":1}],"reason":"because"}""", null),
        ("PUT", "/api/admin/settings", """{"changes":[{"key":"Nope:A","value":1},{"key":"Nope:B","value":2}],"reason":"because"}""", null),
        ("PUT", "/api/admin/settings", """{"changes":[{"key":"Agent:Llm:MainModel","value":"gpt-4o"}],"reason":"because"}""", null),
        ("PUT", "/api/admin/settings", """{"changes":[{"key":"Agent:Llm:MainModel","value":"claude-sonnet-5-5"}],"reason":"because"}""", null),
        ("PUT", "/api/admin/settings", "{\"changes\":[" + string.Join(",", Enumerable.Range(0, 101).Select(i => $"{{\"key\":\"K{i}\"}}")) + "],\"reason\":\"because\"}", null),
        ("GET", "/api/admin/settings/node", null, null), ("POST", "/api/admin/settings/node", null, null),
        ("GET", "/api/admin/settings/audit", null, null), ("GET", "/api/admin/settings/audit", null, "max=0"), ("PUT", "/api/admin/settings/audit", null, null),
        ("GET", "/api/admin/settings/test", null, null), ("POST", "/api/admin/settings/test", null, null), ("POST", "/api/admin/settings/test", """{"kind":"x"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"anthropic"}""", null), ("POST", "/api/admin/settings/test", """{"kind":"anthropic","api_key":"sk-good-1"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"anthropic","api_key":"sk-good-1","model":"gpt-4o"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"anthropic","api_key":"sk-bad","base_url":"ftp://x"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"anthropic","api_key":"sk-good-1","base_url":null}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"ollama"}""", null), ("POST", "/api/admin/settings/test", """{"kind":"ollama","endpoint":"ftp://x"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"ollama","endpoint":"http://down:11434"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"ollama","model":"qwen3:8b"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"ollama","model":"mistral"}""", null),
        ("POST", "/api/admin/settings/test", """{"kind":"ollama","endpoint":"http://other:11434","ollama_api_key":" k2 "}""", null),
        ("POST", "/api/admin/ollama/models", null, null), ("GET", "/api/admin/ollama/models", null, null), ("GET", "/api/admin/ollama/models", null, "endpoint=ftp://x"),
        ("GET", "/api/admin/ollama/models", null, "endpoint= http://down:1 "),
        ("POST", "/api/admin/restart/safety", null, null), ("GET", "/api/admin/restart/safety", null, null),
        ("GET", "/api/admin/restart", null, null), ("POST", "/api/admin/restart", "{}", null), ("POST", "/api/admin/restart", """{"reason":"no"}""", null),
        ("GET", "/api/admin/ollama", null, null), ("GET", "/api/admin/restart/x", null, null),
    ];

    internal async Task<string> Dump()
    {
        var sb = new StringBuilder();
        void Add(string label, SupervisorReply r, bool keysOnly = true) =>
            RouteGolden.Append(sb, label, new SupervisorReply(r.Status, r.ContentType, Encoding.UTF8.GetBytes(r.Text.Replace(_dir.Replace("\\", "\\\\"), "<dir>").Replace(_dir, "<dir>")), r.Headers),
                maxBody: 2000, keysOnly: keysOnly);
        // The gate.
        var (routes, probe, restarts, clock) = Make();
        Add("no token header", await routes.HandleAsync(Req("GET", "/api/admin/settings", token: null), default));
        Add("wrong token", await routes.HandleAsync(Req("GET", "/api/admin/settings", token: "x"), default));
        Add("no role", await routes.HandleAsync(Req("GET", "/api/admin/settings", roles: "viewer"), default));
        Add("no roles header", await routes.HandleAsync(Req("GET", "/api/admin/settings", roles: null), default));
        Add("no user", await routes.HandleAsync(Req("GET", "/api/admin/settings", user: null), default));
        Add("empty user", await routes.HandleAsync(Req("GET", "/api/admin/settings", user: ""), default));
        Add("ADMIN role", await routes.HandleAsync(Req("GET", "/api/admin/settings/node", roles: " ADMIN , x"), default));
        Add("too large", await routes.HandleAsync(Req("PUT", "/api/admin/settings", SupervisorAdminRoutes.TooLarge), default));
        foreach (var (m, p, body, q) in Cases)
        {
            // The probes and writes have their own per-minute limits: a fresh minute for each case (the limits are pinned below).
            clock.Now = clock.Now.AddMinutes(2);
            Add($"{m} {p}{(q != null ? "?" + q : "")} {(body is { Length: > 80 } ? body[..80] + "…" : body ?? "-")}", await routes.HandleAsync(Req(m, p, body, query: q), default),
                keysOnly: !p.Contains("/test", StringComparison.Ordinal) && !p.Contains("/ollama/", StringComparison.Ordinal));
        }
        // Restart: unsafe (409 with the reasons), forced, already under way; not restartable.
        probe.Safe = false;
        clock.Now = clock.Now.AddMinutes(2);
        Add("restart unsafe", await routes.HandleAsync(Req("POST", "/api/admin/restart", """{"reason":"deploy"}"""), default));
        Add("restart forced", await routes.HandleAsync(Req("POST", "/api/admin/restart", """{"reason":"deploy","force":true}"""), default));
        Add("restart again", await routes.HandleAsync(Req("POST", "/api/admin/restart", """{"reason":"deploy","force":true}"""), default));
        sb.Append("restarts: ").Append(restarts.Count).Append('\n');
        var (fixedRoutes, _, _, clock2) = Make(restartable: false, writes: 2, probes: 1);
        Add("not restartable", await fixedRoutes.HandleAsync(Req("POST", "/api/admin/restart", """{"reason":"deploy"}"""), default));
        // Rate limits: 2 writes, 1 probe a minute.
        Add("write 2", await fixedRoutes.HandleAsync(Req("PUT", "/api/admin/settings", """{"changes":[],"reason":"because"}"""), default));
        clock2.Now = clock2.Now.AddSeconds(20);
        Add("write 3", await fixedRoutes.HandleAsync(Req("PUT", "/api/admin/settings", """{"changes":[],"reason":"because"}"""), default));
        Add("restart limited", await fixedRoutes.HandleAsync(Req("POST", "/api/admin/restart", """{"reason":"deploy"}"""), default));
        Add("probe 1", await fixedRoutes.HandleAsync(Req("GET", "/api/admin/ollama/models"), default));
        Add("probe 2", await fixedRoutes.HandleAsync(Req("POST", "/api/admin/settings/test", """{"kind":"ollama"}"""), default));
        clock2.Now = clock2.Now.AddSeconds(59);
        Add("probe 3", await fixedRoutes.HandleAsync(Req("GET", "/api/admin/ollama/models"), default));
        return sb.ToString();
    }

    [Fact]
    public async Task Settings_routes_match_the_golden_dump() =>
        RouteGolden.Check("settings-routes", AdminSettingsRouteGoldenData.Expected, await Dump());
}
