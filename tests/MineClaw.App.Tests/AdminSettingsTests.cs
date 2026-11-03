using System.Text;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;
using MineClaw.App.Supervisor;
using MineClaw.App.Supervisor.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Tests;

/// <summary>Collects every formatted log line (the masking tests search them for secrets).</summary>
internal sealed class ListLogger : ILogger
{
    public readonly List<string> Lines = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add(formatter(state, exception) + (exception != null ? " " + exception : ""));
    }
}

internal sealed class FakeProbe : IRestartSafetyProbe
{
    public bool Safe = true;
    public RestartSafetyResult Check() => new(Safe, DateTimeOffset.UnixEpoch, Safe ? false : true, Safe ? 6000 : 18000,
        Safe ? [] : ["maid_chan_101 is outside at night under open sky"],
        [new RestartBodyCheck("maid_chan_101", 0, 70, !Safe, false, 0, Safe ? null : "maid_chan_101 is outside at night under open sky")]);
}

internal sealed class FakeTester : IProviderTester
{
    public readonly List<(string Key, string? BaseUrl, string Model)> Anthropic = [];
    public readonly List<(string Endpoint, string? Key)> Ollama = [];

    public Task<ProviderTestResult> AnthropicAsync(string apiKey, string? baseUrl, string model, CancellationToken ct)
    {
        Anthropic.Add((apiKey, baseUrl, model));
        return Task.FromResult(apiKey.StartsWith("sk-good", StringComparison.Ordinal)
            ? new ProviderTestResult(true, $"key accepted; {model} is available", 12, [model])
            : new ProviderTestResult(false, "the key was rejected (401)", 9));
    }

    public Task<OllamaListResult> OllamaAsync(string endpoint, string? apiKey, bool details, CancellationToken ct)
    {
        Ollama.Add((endpoint, apiKey));
        return Task.FromResult(endpoint.Contains("down", StringComparison.Ordinal)
            ? new OllamaListResult(false, "connection refused or host unreachable", [], 3)
            : new OllamaListResult(true, null, [new OllamaModelInfoView("qwen3:8b", details ? ["completion", "tools"] : null), new OllamaModelInfoView("llava:7b", details ? ["vision"] : null)], 4));
    }
}

public sealed class AdminSettingsTests : IDisposable
{
    private const string Token = "sup-token-0123456789";
    private const string BaseKey = "sk-ant-base-secret-AAAA1111";
    private const string NewKey = "sk-ant-new-secret-ZZZZ9876";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-admin-" + Guid.NewGuid().ToString("N"));
    private readonly string _base;
    private readonly string _override;
    private readonly string _baseText;

    public AdminSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _base = Path.Combine(_dir, "appsettings.json");
        _override = Path.Combine(_dir, "state", "settings.override.json");
        _baseText = JsonSerializer.Serialize(new
        {
            Minecraft = new { Host = "10.1.2.69", Port = 25565, Username = "maid_chan" },
            Agent = new
            {
                DataDirectory = Path.Combine(_dir, "state"),
                Llm = new { ApiKey = BaseKey, MainModel = "claude-opus-5-5", WorkerModel = "claude-sonnet-5-5", CheapModel = "claude-haiku-5-5", MainMaxTokens = 1200 },
                Chat = new { Username = "maid_chan", DisplayName = "claudechan", Aliases = new[] { "claude chan", "claude", "cc" } },
            },
            Supervisor = new { Enabled = true, Token, AllowAdminWrites = true, Viewer = new { Enabled = true, PerMinute = 12 } },
            Hive = new { MaxClones = 0 },
        });
        File.WriteAllText(_base, _baseText);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IConfigurationRoot Config(Dictionary<string, string?>? env = null)
    {
        env ??= new Dictionary<string, string?>();
        env[SettingsOverrideFile.EnvVar] = _override;
        return AppConfig.Build(AppCli.Parse(["--config", _base]), _dir, env);
    }

    private sealed record Rig(IConfigurationRoot Config, AdminSettingsService Svc, AdminSettingsRoutes Routes, SupervisorServer Server, AppSettings Live,
        FakeProbe Probe, FakeTester Tester, ListLogger Log, List<string> Restarts);

    private Rig Make(Dictionary<string, string?>? env = null)
    {
        var config = Config(env);
        var live = AppSettings.Bind(config, _dir);
        var log = new ListLogger();
        var probe = new FakeProbe();
        var tester = new FakeTester();
        var restarts = new List<string>();
        var roots = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = live.Agent, ["Supervisor"] = live.Supervisor, ["Host"] = live.Host, ["Hive"] = live.Hive, ["Telemetry"] = live.Telemetry,
            ["Minecraft"] = live.Mc, ["Bodies"] = live.Bodies,
        };
        var svc = new AdminSettingsService(config, new SettingsOverrideStore(_override, keep: 3), roots, new AdminNodeInfo("local", "maid_chan", "1.0", null, "b1.8.1"),
            probe, tester, why => { lock (restarts) restarts.Add(why); }, log, env: _ => null);
        var routes = new AdminSettingsRoutes(live.Supervisor, svc, log) { RestartDelay = TimeSpan.Zero };
        var server = new SupervisorServer(live.Supervisor, new FakeSupervisorData()) { Settings = routes };
        return new Rig(config, svc, routes, server, live, probe, tester, log, restarts);
    }

    private static SupervisorRequest Req(string method, string path, object? body = null, string roles = "viewer,admin", string? token = Token, string? query = null)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query != null)
            foreach (var kv in query.Split('&')) { var i = kv.IndexOf('='); q[kv[..i]] = Uri.UnescapeDataString(kv[(i + 1)..]); }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-MCX-User"] = "fox", ["X-MCX-Roles"] = roles };
        return new SupervisorRequest(method, path, q, token, body == null ? null : JsonSerializer.Serialize(body), headers);
    }

    private static object Save(params (string Key, object? Value)[] changes) =>
        new { changes = changes.Select(c => new { key = c.Key, value = c.Value }).ToArray(), reason = "test change" };

    private static JsonElement Parse(SupervisorReply r) => JsonDocument.Parse(r.Text).RootElement.Clone();

    private static JsonElement Setting(JsonElement view, string key) =>
        view.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    // ------------------------------------------------------------------ registry

    [Fact]
    public void Every_registry_key_binds_to_a_real_option_of_a_fitting_type()
    {
        Assert.NotEmpty(SettingsRegistry.All);
        Assert.Equal(SettingsRegistry.All.Count, SettingsRegistry.All.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var groups = SettingsRegistry.Groups.Select(g => g.Id).ToHashSet();
        foreach (var d in SettingsRegistry.All)
        {
            var path = SettingsRegistry.PathOf(d.Key);
            Assert.True(path != null, $"{d.Key} binds to no option");
            Assert.True(SettingsRegistry.Fits(d.Kind, path![^1].PropertyType), $"{d.Key}: {d.Kind} does not fit {path[^1].PropertyType.Name}");
            Assert.True(path[^1].CanWrite, $"{d.Key} can't be bound (no setter)");
            Assert.Contains(d.Group, groups);
            if (d.Kind == SettingKind.Enum) Assert.NotEmpty(d.Options!);
            if (d.Kind == SettingKind.Model && d.ProviderKey != null) Assert.NotNull(SettingsRegistry.Find(d.ProviderKey));
            // A live setting must really be live: its section has a running object the service writes to.
            if (d.Live) Assert.Contains(d.Key.Split(':')[0], new[] { "Agent", "Supervisor", "Host" });
        }
        // The lock-out keys are never editable.
        foreach (var k in new[] { "Supervisor:Token", "Supervisor:AllowAdminWrites", "Supervisor:Enabled", "Supervisor:Bind", "Supervisor:Port" })
            Assert.NotNull(SettingsRegistry.Find(k)!.ReadOnly);
        Assert.True(SettingsRegistry.Find("Agent:Llm:ApiKey")!.Secret);
        Assert.True(SettingsRegistry.Find("Supervisor:Token")!.Secret);
        // The binder really reads every editable key back from config.
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(SettingsRegistry.All.Where(d => d.Kind is SettingKind.String or SettingKind.Url or SettingKind.Text or SettingKind.Secret)
            .Select(d => new KeyValuePair<string, string?>(d.Key, "probe-" + d.Key.Length))).Build();
        var bound = AdminSettingsService.Bind(cfg);
        foreach (var d in SettingsRegistry.All.Where(d => d.Kind is SettingKind.String or SettingKind.Url or SettingKind.Text or SettingKind.Secret))
            Assert.Equal("probe-" + d.Key.Length, SettingsRegistry.Read(bound[d.Key.Split(':')[0]], SettingsRegistry.PathOf(d.Key)!));
    }

    [Fact]
    public async Task View_lists_values_sources_and_masks_secrets()
    {
        var rig = Make(new Dictionary<string, string?> { ["MINECLAW_Agent__Llm__WorkerMaxTokens"] = "900" });
        var reply = await rig.Server.HandleAsync(Req("GET", "/api/admin/settings"));
        Assert.Equal(200, reply.Status);
        Assert.DoesNotContain(BaseKey, reply.Text);
        Assert.DoesNotContain(Token, reply.Text);
        var view = Parse(reply);
        var key = Setting(view, "Agent:Llm:ApiKey");
        Assert.False(key.TryGetProperty("value", out _));
        Assert.True(key.GetProperty("secret_state").GetProperty("set").GetBoolean());
        Assert.Equal("…1111", key.GetProperty("secret_state").GetProperty("hint").GetString());
        Assert.Equal("config", key.GetProperty("source").GetString());
        Assert.Equal("env", Setting(view, "Agent:Llm:WorkerMaxTokens").GetProperty("source").GetString());
        Assert.Equal("default", Setting(view, "Agent:Llm:CheapMaxTokens").GetProperty("source").GetString());
        Assert.Equal(1200, Setting(view, "Agent:Llm:MainMaxTokens").GetProperty("value").GetInt32());
        Assert.Equal(ModelProfiles.CurrentGeneration, view.GetProperty("allowed_claude_models").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("live", Setting(view, "Supervisor:Viewer:PerMinute").GetProperty("apply").GetString());
        Assert.False(Setting(view, "Supervisor:Token").GetProperty("editable").GetBoolean());
        Assert.Equal("maid_chan", view.GetProperty("node").GetProperty("name").GetString());
    }

    // ------------------------------------------------------------------ gates

    [Fact]
    public async Task Routes_need_admin_writes_the_token_and_the_admin_identity()
    {
        var rig = Make();
        Assert.Equal(403, (await rig.Server.HandleAsync(Req("GET", "/api/admin/settings", roles: "viewer,chat"))).Status);
        Assert.Equal(403, (await rig.Server.HandleAsync(Req("GET", "/api/admin/settings", token: null))).Status);
        Assert.Equal(403, (await rig.Server.HandleAsync(Req("GET", "/api/admin/settings", token: "wrong-token-0123456789"))).Status);
        var noHeaders = new SupervisorRequest("GET", "/api/admin/settings", new Dictionary<string, string>(), Token);
        Assert.Equal(403, (await rig.Server.HandleAsync(noHeaders)).Status);
        rig.Live.Supervisor.AllowAdminWrites = false;
        Assert.Equal(403, (await rig.Server.HandleAsync(Req("GET", "/api/admin/settings"))).Status);
        // Without the routes wired the paths are 404, never the memory-admin routes.
        var bare = new SupervisorServer(new SupervisorOptions { Token = Token, AllowAdminWrites = true }, new FakeSupervisorData());
        Assert.Equal(404, (await bare.HandleAsync(Req("GET", "/api/admin/settings"))).Status);
    }

    // ------------------------------------------------------------------ saving

    [Fact]
    public async Task Save_is_all_or_nothing()
    {
        var rig = Make();
        var reply = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings",
            Save(("Supervisor:Viewer:PerMinute", 30), ("Agent:Llm:MainMaxTokens", 999_999), ("Supervisor:Token", "x"))));
        Assert.Equal(400, reply.Status);
        var errors = Parse(reply).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        Assert.Contains("Agent:Llm:MainMaxTokens", errors);
        Assert.Contains("Supervisor:Token", errors);
        Assert.False(File.Exists(_override));
        Assert.Equal(12, rig.Live.Supervisor.Viewer.PerMinute); // the good change was not applied either
        Assert.Contains(rig.Svc.AuditTail(5), l => l.Action == "update" && !l.Ok);
    }

    [Fact]
    public async Task Only_5_5_generation_claude_models_are_accepted()
    {
        // No Ollama endpoint, whatever the machine's own appsettings.json beside the binaries says.
        var rig = Make(new Dictionary<string, string?> { ["MINECLAW_Agent__Llm__Ollama__Endpoint"] = "" });
        var bad = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Agent:Llm:MainModel", "claude-3-opus-20240229"))));
        Assert.Equal(400, bad.Status);
        Assert.Contains("Agent:Llm:MainModel", bad.Text);
        Assert.Equal(400, (await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Agent:Llm:Perception:Model", "claude-sonnet-4-5"))))).Status);
        // An Ollama tier needs an endpoint (the startup rule), and then any tag is fine.
        var noEndpoint = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Agent:Llm:CheapProvider", "ollama"), ("Agent:Llm:CheapModel", "qwen3:8b"))));
        Assert.Equal(400, noEndpoint.Status);
        Assert.Contains("Ollama:Endpoint", noEndpoint.Text);
        var ok = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings",
            Save(("Agent:Llm:CheapProvider", "ollama"), ("Agent:Llm:CheapModel", "qwen3:8b"), ("Agent:Llm:Ollama:Endpoint", "http://10.1.2.5:11434"))));
        Assert.Equal(200, ok.Status);
        Assert.Equal(200, (await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Agent:Llm:MainModel", "claude-sonnet-5-5"))))).Status);
        // Every allowed id is the startup set, nothing else.
        foreach (var id in ModelProfiles.CurrentGeneration)
            Assert.Null(AdminSettingsService.Normalize(SettingsRegistry.Find("Agent:Llm:WorkerModel")!, JsonDocument.Parse($"\"{id}\"").RootElement, out _));
    }

    [Fact]
    public async Task Override_wins_and_the_base_file_stays_untouched()
    {
        var rig = Make(new Dictionary<string, string?> { ["MINECLAW_Hive__MaxClones"] = "3" });
        var reply = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings",
            Save(("Hive:MaxClones", 7), ("Agent:Chat:Aliases", new[] { "maid" }), ("Agent:Llm:ApiKey", NewKey))));
        Assert.Equal(200, reply.Status);
        Assert.Equal(_baseText, File.ReadAllText(_base));
        Assert.True(File.Exists(_override));
        // A fresh config (what the bot reads at its next start) layers the override above the base file and env.
        var next = AppSettings.Bind(Config(new Dictionary<string, string?> { ["MINECLAW_Hive__MaxClones"] = "3" }), _dir);
        Assert.Equal(7, next.Hive.MaxClones);
        // The list is replaced, not merged index by index with the base file's longer list.
        Assert.Equal(["maid"], Config(new Dictionary<string, string?>()).GetSection("Agent:Chat:Aliases").Get<string[]>());
        Assert.Equal(NewKey, next.Agent.Llm.ApiKey);
        var view = Parse(await rig.Server.HandleAsync(Req("GET", "/api/admin/settings")));
        Assert.Equal("override", Setting(view, "Hive:MaxClones").GetProperty("source").GetString());
        Assert.True(Setting(view, "Hive:MaxClones").GetProperty("overridden").GetBoolean());
        // Reset drops the key: back to env's value.
        Assert.Equal(200, (await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Hive:MaxClones", null))))).Status);
        Assert.Equal(3, AppSettings.Bind(Config(new Dictionary<string, string?> { ["MINECLAW_Hive__MaxClones"] = "3" }), _dir).Hive.MaxClones);
    }

    [Fact]
    public async Task Writes_are_atomic_with_rotated_backups()
    {
        var rig = Make();
        for (int i = 1; i <= 5; i++)
        {
            var r = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Supervisor:Viewer:PerMinute", 10 + i))));
            Assert.Equal(200, r.Status);
            var backup = Parse(r).TryGetProperty("backup", out var b) ? b.GetString() : null;
            if (i == 1) Assert.Null(backup); else Assert.NotNull(backup);
        }
        var store = new SettingsOverrideStore(_override, keep: 3);
        Assert.Equal(3, store.Backups().Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_override)!, "*.tmp"));
        Assert.Equal(15, SettingsOverrideStore.Get(store.Read(), "Supervisor:Viewer:PerMinute")!.GetValue<long>());
        // The newest backup holds the previous version.
        Assert.Equal(14, SettingsOverrideStore.Get(JsonNodeOf(store.Backups()[0]), "Supervisor:Viewer:PerMinute")!.GetValue<long>());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_override));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Backups()[0]));
        }
        // Keys the registry doesn't know (a hand edit) survive a write.
        var root = store.Read();
        SettingsOverrideStore.Set(root, "Agent:Custom:Thing", System.Text.Json.Nodes.JsonValue.Create("kept"));
        File.WriteAllText(_override, root.ToJsonString());
        await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Supervisor:Viewer:PerMinute", 20))));
        Assert.Equal("kept", SettingsOverrideStore.Get(store.Read(), "Agent:Custom:Thing")!.GetValue<string>());
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeOf(string file) => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;

    [Fact]
    public async Task Live_settings_apply_now_restart_settings_wait()
    {
        var rig = Make();
        var reply = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings",
            Save(("Supervisor:Viewer:PerMinute", 40), ("Agent:Llm:MainMaxTokens", 2048), ("Host:ConsoleLogLevel", "warning"), ("Hive:MaxClones", 9))));
        Assert.Equal(200, reply.Status);
        var r = Parse(reply);
        var applied = r.GetProperty("applied").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Supervisor:Viewer:PerMinute", applied);
        Assert.Contains("Agent:Llm:MainMaxTokens", applied);
        Assert.Equal(["Hive:MaxClones"], r.GetProperty("restart_required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(40, rig.Live.Supervisor.Viewer.PerMinute);       // the viewer's rate limit reads this object per request
        Assert.Equal(2048, rig.Live.Agent.Llm.MainMaxTokens);           // the brain reads it per call
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, AppLogging.ConsoleLevel);
        Assert.Equal("Warning", rig.Live.Host.ConsoleLogLevel);
        Assert.Equal(0, rig.Live.Hive.MaxClones);                      // restart setting: the running value is unchanged
        var view = Parse(await rig.Server.HandleAsync(Req("GET", "/api/admin/settings")));
        Assert.True(Setting(view, "Hive:MaxClones").GetProperty("pending_restart").GetBoolean());
        Assert.False(Setting(view, "Supervisor:Viewer:PerMinute").GetProperty("pending_restart").GetBoolean());
        Assert.Contains("Hive:MaxClones", view.GetProperty("restart_pending").EnumerateArray().Select(e => e.GetString()));
        AppLogging.SetConsoleLevel("Information");
    }

    [Fact]
    public async Task Secrets_never_reach_responses_logs_or_the_audit()
    {
        var rig = Make();
        var bad = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Agent:Llm:ApiKey", "sk-ant has spaces SECRETX"))));
        Assert.Equal(400, bad.Status);
        Assert.DoesNotContain("SECRETX", bad.Text);
        var ok = await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Agent:Llm:ApiKey", NewKey), ("Agent:Llm:Ollama:ApiKey", "ollama-bearer-QQQQ5555"))));
        Assert.Equal(200, ok.Status);
        Assert.DoesNotContain(NewKey, ok.Text);
        var view = await rig.Server.HandleAsync(Req("GET", "/api/admin/settings"));
        Assert.DoesNotContain(NewKey, view.Text);
        Assert.DoesNotContain("QQQQ5555\"", view.Text);
        Assert.Equal("…9876", Setting(Parse(view), "Agent:Llm:ApiKey").GetProperty("secret_state").GetProperty("hint").GetString());
        var test = await rig.Server.HandleAsync(Req("POST", "/api/admin/settings/test", new { kind = "anthropic" }));
        Assert.Equal(200, test.Status);
        Assert.Equal(NewKey, rig.Tester.Anthropic.Single().Key); // the running key is used, never sent back
        Assert.DoesNotContain(NewKey, test.Text);
        var audit = await rig.Server.HandleAsync(Req("GET", "/api/admin/settings/audit"));
        Assert.Contains("Agent:Llm:ApiKey", audit.Text);
        foreach (var text in new[] { audit.Text, File.ReadAllText(rig.Svc.AuditPath), string.Join("\n", rig.Log.Lines) })
        {
            Assert.DoesNotContain(NewKey, text);
            Assert.DoesNotContain(BaseKey, text);
            Assert.DoesNotContain("QQQQ5555", text);
            Assert.DoesNotContain(Token, text);
        }
    }

    // ------------------------------------------------------------------ probes

    [Fact]
    public async Task Provider_tests_save_nothing_and_check_their_input()
    {
        var rig = Make();
        var good = Parse(await rig.Server.HandleAsync(Req("POST", "/api/admin/settings/test", new { kind = "anthropic", api_key = "sk-good-candidate-key", model = "claude-opus-5-5" })));
        Assert.True(good.GetProperty("ok").GetBoolean());
        Assert.Equal(400, (await rig.Server.HandleAsync(Req("POST", "/api/admin/settings/test", new { kind = "anthropic", api_key = "sk-good-x", model = "claude-3-haiku" }))).Status);
        var ollama = Parse(await rig.Server.HandleAsync(Req("POST", "/api/admin/settings/test", new { kind = "ollama", endpoint = "http://10.1.2.5:11434", model = "qwen3:8b" })));
        Assert.True(ollama.GetProperty("ok").GetBoolean());
        var missing = Parse(await rig.Server.HandleAsync(Req("POST", "/api/admin/settings/test", new { kind = "ollama", endpoint = "http://10.1.2.5:11434", model = "nope:1b" })));
        Assert.False(missing.GetProperty("ok").GetBoolean());
        Assert.Equal(400, (await rig.Server.HandleAsync(Req("POST", "/api/admin/settings/test", new { kind = "ollama", endpoint = "http://169.254.169.254/" }))).Status);
        Assert.Equal(400, (await rig.Server.HandleAsync(Req("POST", "/api/admin/settings/test", new { kind = "ollama", endpoint = "file:///etc/passwd" }))).Status);
        var models = Parse(await rig.Server.HandleAsync(Req("GET", "/api/admin/ollama/models", query: "endpoint=http%3A%2F%2F10.1.2.5%3A11434")));
        Assert.True(models.GetProperty("ok").GetBoolean());
        Assert.Equal("tools", models.GetProperty("models")[0].GetProperty("capabilities")[1].GetString());
        Assert.False(File.Exists(_override));
    }

    [Fact]
    public async Task Writes_and_probes_are_rate_limited()
    {
        var rig = Make();
        int limited = 0;
        for (int i = 0; i < 14; i++)
            if ((await rig.Server.HandleAsync(Req("PUT", "/api/admin/settings", Save(("Supervisor:Viewer:PerMinute", 10 + i))))).Status == 429) limited++;
        Assert.Equal(2, limited);
    }

    // ------------------------------------------------------------------ restart

    [Fact]
    public async Task Restart_is_refused_while_unsafe_unless_forced()
    {
        var rig = Make();
        rig.Probe.Safe = false;
        var refused = await rig.Server.HandleAsync(Req("POST", "/api/admin/restart", new { reason = "apply models" }));
        Assert.Equal(409, refused.Status);
        Assert.Contains("outside at night", refused.Text);
        await Task.Delay(50);
        Assert.Empty(rig.Restarts);
        Assert.Contains(rig.Svc.AuditTail(5), l => l.Action == "restart_refused");
        Assert.Equal(400, (await rig.Server.HandleAsync(Req("POST", "/api/admin/restart", new { reason = "", force = true }))).Status);
        var forced = await rig.Server.HandleAsync(Req("POST", "/api/admin/restart", new { reason = "she is in a hole, it's fine", force = true }));
        Assert.Equal(202, forced.Status);
        Assert.True(Parse(forced).GetProperty("forced").GetBoolean());
        for (int i = 0; i < 50 && rig.Restarts.Count == 0; i++) await Task.Delay(20);
        Assert.Single(rig.Restarts);
        Assert.Contains("fox (web)", rig.Restarts[0]);
        Assert.Equal(409, (await rig.Server.HandleAsync(Req("POST", "/api/admin/restart", new { reason = "again please", force = true }))).Status);
    }

    [Fact]
    public void Safety_judges_night_sky_and_ledges()
    {
        // A 3x3 platform at y=64 over the void, a roofed hut at x=10..12 (roof y=68).
        int Block(int x, int y, int z) =>
            y == 64 && x is >= 0 and <= 12 && z is >= 0 and <= 2 ? 1
            : y == 68 && x is >= 10 and <= 12 && z is >= 0 and <= 2 ? 5
            : 0;
        bool Loaded(int x, int z) => true;
        var bodies = new[]
        {
            new SafetyBody("hut", true, 0, 11.5, 65, 1.5),     // roofed, platform all around
            new SafetyBody("open", true, 0, 5.5, 65, 1.5),     // open sky, platform all around (z 0..2)
            new SafetyBody("edge", true, 0, 0.5, 65, 1.5),     // at the platform's west edge: the void beside
            new SafetyBody("away", false, 0, 0, 0, 0),
            new SafetyBody("nether", true, -1, 5, 65, 1),
        };
        var day = RestartSafety.Evaluate(DateTimeOffset.UnixEpoch, 6000, 0, bodies, Block, Loaded, 128);
        Assert.False(day.Safe); // the edge body, even by day
        Assert.Equal(["edge is at a ledge over a long (void) drop"], day.Reasons);
        Assert.False(day.Night);
        var night = RestartSafety.Evaluate(DateTimeOffset.UnixEpoch, 18000, 0, bodies, Block, Loaded, 128);
        Assert.True(night.Night);
        Assert.Contains("open is outside at night under open sky", night.Reasons);
        Assert.DoesNotContain(night.Reasons, r => r.StartsWith("hut", StringComparison.Ordinal));
        Assert.Contains(night.Bodies, b => b.Name == "edge" && b.Exposed && b.HighBuild);
        Assert.Contains(night.Bodies, b => b.Name == "nether" && !b.Exposed && b.Reason == null);
        var safe = RestartSafety.Evaluate(DateTimeOffset.UnixEpoch, 18000, 0, [bodies[0]], Block, Loaded, 128);
        Assert.True(safe.Safe);
        var offline = new LiveRestartProbe(() => null, () => false, () => bodies).Check();
        Assert.True(offline.Safe);
        Assert.Null(offline.Night);
    }

    [Fact]
    public void Override_provider_replaces_lists_and_survives_a_broken_file()
    {
        File.WriteAllText(_base, """{"Agent":{"Chat":{"Aliases":["a","b","c"]}}}""");
        Directory.CreateDirectory(Path.GetDirectoryName(_override)!);
        File.WriteAllText(_override, """{"Agent":{"Chat":{"Aliases":["x"]}}}""");
        var cfg = Config();
        Assert.Equal(["x"], cfg.GetSection("Agent:Chat:Aliases").Get<string[]>());
        File.WriteAllText(_override, "{ not json");
        var broken = Config();
        // A broken override is ignored: the result equals the config without any override layer (the machine's own
        // appsettings.json beside the binaries may add aliases, so compare against that rather than a literal list).
        var none = AppConfig.Build(AppCli.Parse(["--config", _base]), _dir, new Dictionary<string, string?> { [SettingsOverrideFile.EnvVar] = "off" });
        Assert.Equal(none.GetSection("Agent:Chat:Aliases").Get<string[]>(), broken.GetSection("Agent:Chat:Aliases").Get<string[]>());
        Assert.Contains("a", broken.GetSection("Agent:Chat:Aliases").Get<string[]>()!);
        Assert.NotNull(SettingsOverrideFile.Of(broken)!.LoadError);
        // A fake environment without MINECLAW_SETTINGS_OVERRIDE gets no override layer (tests stay deterministic).
        Assert.Null(SettingsOverrideFile.Of(AppConfig.Build(AppCli.Parse(["--config", _base]), _dir, new Dictionary<string, string?>())));
    }
}
