using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.App.Hosting;
using MineClaw.App.Supervisor;
using MineClaw.App.Supervisor.Admin;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Tests;

/// <summary>Setup mode: a fresh install waits for the web panel (Administration) instead of exiting with the config error.</summary>
public sealed class SetupModeTests : IDisposable
{
    private const string Token = "sup-token-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-setup-" + Guid.NewGuid().ToString("N"));
    private readonly string _base;
    private readonly string _override;

    public SetupModeTests()
    {
        Directory.CreateDirectory(_dir);
        _base = Path.Combine(_dir, "appsettings.json");
        _override = Path.Combine(_dir, "state", "settings.override.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A fresh install's config: no Minecraft host, no key (a key in the environment can't fill the host).</summary>
    private void WriteBase(bool supervisor = true, string host = "", string? apiKey = "")
    {
        File.WriteAllText(_base, JsonSerializer.Serialize(new
        {
            Minecraft = new { Host = host, Port = 25565, Username = "maid_chan" },
            Agent = new
            {
                DataDirectory = Path.Combine(_dir, "state"),
                Llm = new { ApiKey = apiKey, MainModel = "claude-opus-5-5", WorkerModel = "claude-sonnet-5-5", CheapModel = "claude-haiku-5-5" },
                Chat = new { Username = "maid_chan" },
            },
            Supervisor = new { Enabled = supervisor, Token, AllowAdminWrites = true, Viewer = new { Enabled = true } },
            Hive = new { MaxClones = 0 },
        }));
    }

    private (IConfigurationRoot Config, AppSettings Settings) Load(AppCli cli, string? overridePath = null)
    {
        var env = new Dictionary<string, string?> { [SettingsOverrideFile.EnvVar] = overridePath ?? _override };
        var config = AppConfig.Build(cli, _dir, env);
        return (config, AppSettings.Bind(config, _dir));
    }

    private AppCli Cli(params string[] extra) => AppCli.Parse(["--config", _base, .. extra]);

    private (bool Setup, IReadOnlyList<string> Problems) Decide(AppCli cli, string? overridePath = null)
    {
        var (config, settings) = Load(cli, overridePath);
        var (problems, setup) = SetupMode.Check(cli, config, settings);
        return (setup, problems);
    }

    private SupervisorServer Server(Action<string> restart)
    {
        var (config, settings) = Load(Cli());
        return SetupSupervisor.Create(config, settings, restart, new ListLogger(), new FakeTester()) ?? throw new InvalidOperationException("no override layer");
    }

    private static SupervisorRequest Req(string method, string path, object? body = null, string? token = Token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-MCX-User"] = "fox", ["X-MCX-Roles"] = "viewer,admin" };
        return new SupervisorRequest(method, path, new Dictionary<string, string>(), token, body == null ? null : JsonSerializer.Serialize(body), headers);
    }

    private static JsonElement Parse(SupervisorReply r) => JsonDocument.Parse(r.Text).RootElement.Clone();

    // ------------------------------------------------------------------ the decision

    [Fact]
    public void A_fresh_install_with_the_supervisor_waits_in_setup_mode()
    {
        WriteBase();
        var (setup, problems) = Decide(Cli());
        Assert.True(setup);
        Assert.Contains("Minecraft:Host is empty", problems);
        var line = SetupMode.Line(problems);
        Assert.StartsWith("setup mode: waiting for configuration from the web panel (", line);
        Assert.Contains("Minecraft:Host is empty", line);
    }

    [Fact]
    public void Without_the_supervisor_a_clone_or_the_override_layer_it_exits_as_before()
    {
        WriteBase(supervisor: false);
        Assert.False(Decide(Cli()).Setup); // exit 2 with the console message
        Assert.NotEmpty(Decide(Cli()).Problems);
        WriteBase();
        Assert.False(Decide(Cli("--clone", "maid_chan_67", "--parent-pipe", "p")).Setup); // hive clones never wait
        Assert.False(Decide(Cli(), overridePath: "off").Setup); // nothing the page could write to
    }

    [Fact]
    public void A_valid_config_never_enters_setup_mode()
    {
        WriteBase(host: "localhost", apiKey: "sk-ant-test-0000000000000000");
        var (setup, problems) = Decide(Cli());
        Assert.Empty(problems);
        Assert.False(setup);
    }

    [Fact]
    public void A_rejected_key_waits_only_where_setup_mode_applies()
    {
        WriteBase(host: "localhost", apiKey: "sk-ant-test-0000000000000000");
        var (config, settings) = Load(Cli());
        Assert.True(SetupMode.Applies(Cli(), config, settings.Supervisor));
        Assert.False(SetupMode.Applies(Cli("--clone", "maid_chan_67", "--parent-pipe", "p"), config, settings.Supervisor));
        Assert.StartsWith("Agent:Llm:ApiKey", SetupMode.KeyRejected);
    }

    // ------------------------------------------------------------------ the setup supervisor

    [Fact]
    public async Task Settings_read_and_save_against_the_override_file()
    {
        WriteBase();
        var server = Server(_ => { });
        var get = await server.HandleAsync(Req("GET", "/api/admin/settings"));
        Assert.Equal(200, get.Status);
        var view = Parse(get);
        Assert.Contains(view.GetProperty("settings").EnumerateArray(), s => s.GetProperty("key").GetString() == "Minecraft:Host");
        Assert.Equal(AppVersion.Informational, view.GetProperty("node").GetProperty("version").GetString());
        Assert.False(view.GetProperty("node").TryGetProperty("game_version", out _)); // no game version before the bot runs

        var put = await server.HandleAsync(Req("PUT", "/api/admin/settings", new
        {
            changes = new object[] { new { key = "Minecraft:Host", value = "mc.example.invalid" }, new { key = "Agent:Llm:ApiKey", value = "sk-ant-new-secret-ZZZZ9876" } },
            reason = "first run",
        }));
        Assert.Equal(200, put.Status);
        Assert.Contains("Minecraft:Host", Parse(put).GetProperty("restart_required").EnumerateArray().Select(e => e.GetString()));
        var saved = JsonNode.Parse(File.ReadAllText(_override))!;
        Assert.Equal("mc.example.invalid", saved["Minecraft"]!["Host"]!.GetValue<string>());
        Assert.Equal("sk-ant-new-secret-ZZZZ9876", saved["Agent"]!["Llm"]!["ApiKey"]!.GetValue<string>());

        // The next start reads them: the config is complete and runs normally.
        var (setup, problems) = Decide(Cli());
        Assert.Empty(problems);
        Assert.False(setup);

        Assert.Equal(403, (await server.HandleAsync(Req("GET", "/api/admin/settings", token: null))).Status); // same gate as normal mode
    }

    [Fact]
    public async Task Live_only_routes_answer_503_and_the_health_reads_answer()
    {
        WriteBase();
        var server = Server(_ => { });
        foreach (var path in new[] { "/api/viewer/now", "/api/story", "/view/maid_chan", "/camera", "/api/world/columns", "/api/bodies/maid_chan/vision" })
        {
            var r = await server.HandleAsync(Req("GET", path));
            Assert.Equal(503, r.Status);
            Assert.Equal("setup", Parse(r).GetProperty("error").GetString());
            Assert.Equal(SetupSupervisor.Message, Parse(r).GetProperty("message").GetString());
        }
        Assert.Equal(503, (await server.HandleAsync(Req("POST", "/api/viewer/chat", new { text = "hi" }))).Status);
        Assert.Equal(401, (await server.HandleAsync(Req("GET", "/api/viewer/now", token: null))).Status);

        var bots = await server.HandleAsync(Req("GET", "/api/bots")); // the bridge's health probe
        Assert.Equal(200, bots.Status);
        Assert.Equal(0, Parse(bots).GetProperty("bots").GetArrayLength());
        Assert.Equal(401, (await server.HandleAsync(Req("GET", "/api/bots", token: null))).Status);
        Assert.Equal("setup", Parse(await server.HandleAsync(Req("GET", "/api/hive/tree"))).GetProperty("state").GetString());
        Assert.True(Parse(await server.HandleAsync(Req("GET", "/api/status"))).GetProperty("setup").GetBoolean());
        Assert.Equal(200, (await server.HandleAsync(Req("GET", "/api/admin/restart/safety"))).Status);
    }

    [Fact]
    public async Task Restart_requests_the_shutdown()
    {
        WriteBase();
        using var signals = new ShutdownSignals();
        var server = Server(signals.Request);
        var safety = Parse(await server.HandleAsync(Req("GET", "/api/admin/restart/safety")));
        Assert.True(safety.GetProperty("safe").GetBoolean());
        var r = await server.HandleAsync(Req("POST", "/api/admin/restart", new { reason = "settings saved" }));
        Assert.Equal(202, r.Status);
        await Task.Delay(TimeSpan.FromSeconds(10), signals.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        Assert.True(signals.Token.IsCancellationRequested);
        Assert.Contains("restart requested from the web", signals.Reason);
    }

    // ------------------------------------------------------------------ --version

    [Fact]
    public void Version_flag_parses_and_the_version_is_stamped()
    {
        var c = AppCli.Parse(["--version"]);
        Assert.True(c.PrintVersion);
        Assert.Empty(c.Errors);
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Informational));
        Assert.Contains("--version", AppCli.Usage);
    }
}
