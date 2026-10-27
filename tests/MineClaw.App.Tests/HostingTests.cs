using MineClaw.App.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Tests;

public class HostingTests
{
    [Fact]
    public void Cli_ParsesHeadlessServiceLine()
    {
        var c = AppCli.Parse(["--no-tui", "--data-dir", "/var/lib/mineclaw", "--log-dir=/var/log/mineclaw", "--config", "/etc/mineclaw/appsettings.json",
            "--shutdown-timeout", "45", "--log-format", "systemd", "--stdin-commands"]);
        Assert.Empty(c.Errors);
        Assert.Equal(TuiChoice.Off, c.Tui);
        Assert.Equal("/var/lib/mineclaw", c.DataDir);
        Assert.Equal("/var/log/mineclaw", c.LogDir);
        Assert.Equal("/etc/mineclaw/appsettings.json", c.ConfigPath);
        Assert.Equal(45, c.ShutdownTimeoutSeconds);
        Assert.Equal("systemd", c.LogFormat);
        Assert.True(c.StdinCommands);
    }

    [Fact]
    public void Cli_DemoFrameBotView_AndDefaults()
    {
        var c = AppCli.Parse(["--demo", "--demo-bots", "2", "--frame", "100x30", "--bot", "3", "--view", "side", "--minutes", "1.5"]);
        Assert.Empty(c.Errors);
        Assert.True(c.Demo);
        Assert.Equal(2, c.DemoBots);
        Assert.Equal((100, 30), c.Frame);
        Assert.Equal(3, c.Bot);
        Assert.Equal("side", c.ViewMode);
        Assert.Equal(1.5, c.Minutes);

        var d = AppCli.Parse([]);
        Assert.Equal(TuiChoice.Auto, d.Tui);
        Assert.False(d.StdinCommands);
        Assert.Null(d.Frame);
        Assert.Equal(1, d.Bot);
        Assert.Equal((120, 40), AppCli.Parse(["--frame"]).Frame);
    }

    [Fact]
    public void Cli_ReportsErrors()
    {
        var c = AppCli.Parse(["--bogus", "--minutes", "abc", "--data-dir", "--log-format", "xml", "--frame", "1x1"]);
        Assert.Contains(c.Errors, e => e.Contains("--bogus"));
        Assert.Contains(c.Errors, e => e.Contains("--minutes"));
        Assert.Contains(c.Errors, e => e.Contains("--data-dir needs a value"));
        Assert.Contains(c.Errors, e => e.Contains("--log-format"));
        Assert.Contains(c.Errors, e => e.Contains("--frame"));
        Assert.True(AppCli.Parse(["-h"]).Help);
    }

    [Fact]
    public void Config_LayersFileThenPrefixedEnvThenCli()
    {
        var file = Path.Combine(Path.GetTempPath(), "mineclaw-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, """{ "Agent": { "DataDirectory": "from-file", "Llm": { "MainModel": "file-model" } }, "Host": { "LogDirectory": "file-logs" } }""");
        try
        {
            var env = new Dictionary<string, string?>
            {
                ["MINECLAW_Agent__Llm__MainModel"] = "env-model",
                ["MINECLAW_Host__LogDirectory"] = "env-logs",
                ["PATH"] = "/usr/bin",
            };
            var cfg = AppConfig.Build(AppCli.Parse(["--config", file, "--log-dir", "/var/log/mineclaw"]), Path.GetTempPath(), env);
            Assert.Equal("from-file", cfg["Agent:DataDirectory"]);
            Assert.Equal("env-model", cfg["Agent:Llm:MainModel"]);
            Assert.Equal("/var/log/mineclaw", cfg["Host:LogDirectory"]); // CLI beats env
            Assert.Null(cfg["PATH"]);

            var cfg2 = AppConfig.Build(AppCli.Parse(["--data-dir", "/var/lib/mineclaw"]), Path.GetTempPath(),
                new Dictionary<string, string?> { ["MINECLAW_CONFIG"] = file });
            Assert.Equal("/var/lib/mineclaw", cfg2["Agent:DataDirectory"]);
            Assert.Equal("file-logs", cfg2.GetSection("Host").Get<HostSettings>()!.LogDirectory);
        }
        finally { File.Delete(file); }

        Assert.Throws<FileNotFoundException>(() => AppConfig.Build(AppCli.Parse(["--config", file]), Path.GetTempPath(), new Dictionary<string, string?>()));
    }

    [Theory]
    [InlineData(TuiChoice.Auto, false, false, "xterm-256color", false, true, true)]
    [InlineData(TuiChoice.Auto, true, false, "xterm", false, true, false)]   // stdout piped (journald, file)
    [InlineData(TuiChoice.Auto, false, true, "xterm", false, true, false)]   // stdin piped
    [InlineData(TuiChoice.Auto, false, false, "xterm", true, true, false)]   // under systemd
    [InlineData(TuiChoice.Auto, false, false, "dumb", false, true, false)]
    [InlineData(TuiChoice.Off, false, false, "xterm", false, true, false)]
    [InlineData(TuiChoice.On, true, true, "dumb", true, false, true)]       // forced
    public void TerminalDetect_FallsBackToHeadless(TuiChoice choice, bool outRedir, bool inRedir, string term, bool systemd, bool interactive, bool expectTui)
    {
        var (tui, why) = TerminalDetect.Decide(choice, new TerminalDetect.Facts(outRedir, inRedir, term, systemd, interactive));
        Assert.Equal(expectTui, tui);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void LogFormat_AutoPicksSystemdUnderJournal()
    {
        Assert.Equal("systemd", AppLogging.ResolveFormat("auto", underJournal: true));
        Assert.Equal("simple", AppLogging.ResolveFormat(null, underJournal: false));
        Assert.Equal("json", AppLogging.ResolveFormat("JSON", underJournal: true));
    }

    [Fact]
    public async Task ShutdownSequence_TimesOutSlowSteps_AndKeepsGoing()
    {
        var seq = new ShutdownSequence(TimeSpan.FromSeconds(2), NullLogger.Instance);
        var ran = new List<string>();
        await seq.StepAsync("slow", TimeSpan.FromMilliseconds(200), ct => Task.Delay(Timeout.Infinite, CancellationToken.None));
        await seq.StepAsync("ok", TimeSpan.FromSeconds(1), _ => { ran.Add("ok"); return Task.CompletedTask; });
        await seq.StepAsync("throws", TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("x"));
        seq.Final("flush", () => ran.Add("flush"));
        Assert.Equal(["slow"], seq.TimedOut);
        Assert.Equal(["ok", "flush"], ran);
        Assert.True(seq.Remaining > TimeSpan.FromSeconds(1));
    }
}
