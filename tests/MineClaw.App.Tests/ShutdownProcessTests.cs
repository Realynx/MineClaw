using System.Diagnostics;
using MineClaw.Hive.Ipc;

namespace MineClaw.App.Tests;

/// <summary>
/// The live hive test's slow /quit: the app itself must stop within seconds — main mode on /quit from stdin (even while it
/// is still trying to connect) and on SIGTERM (Unix), clone mode on recall from main. No real server, no API calls:
/// the game server is 127.0.0.1:1 (refused), the key is a dummy, embeddings/RAPTOR/telemetry are off.
/// </summary>
public class ShutdownProcessTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string AppDll()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = d.Name;
        var config = d.Parent!.Name;
        // --artifacts-path layout: <artifacts>/bin/MineClaw.App.Tests/<config>/ next to <artifacts>/bin/MineClaw.App/<config>/
        var artifacts = Path.Combine(d.Parent.Parent?.FullName ?? "", "MineClaw.App", d.Name, "MineClaw.App.dll");
        if (File.Exists(artifacts)) return artifacts;
        for (; d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "src", "MineClaw.App", "bin", config, tfm, "MineClaw.App.dll");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("MineClaw.App.dll not built");
    }

    private static string Config(string dir)
    {
        var path = Path.Combine(dir, "test-appsettings.json");
        File.WriteAllText(path, """
        {
          "Minecraft": { "Host": "127.0.0.1", "Port": 1, "Username": "quit_test" },
          "Agent": {
            "Chat": { "Username": "quit_test" },
            "Llm": { "ApiKey": "sk-ant-test-dummy-not-a-key" },
            "Embeddings": { "Provider": "none", "Endpoint": "", "ApiKey": "" },
            "Rag": { "Raptor": { "Enabled": false, "Maintenance": false }, "SeedFile": "" },
            "Self": { "Enabled": false }
          },
          "Hive": { "Enabled": false },
          "Telemetry": { "Enabled": false }
        }
        """);
        return path;
    }

    private static (Process P, List<string> Lines, string Dir) Start(params string[] extra)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-quit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { AppDll(), "--no-tui", "--no-key-check", "--config", Config(dir), "--data-dir", Path.Combine(dir, "data"), "--log-dir", Path.Combine(dir, "logs") }.Concat(extra))
            psi.ArgumentList.Add(a);
        psi.Environment["MINECLAW_Agent__Llm__ApiKey"] = "sk-ant-test-dummy-not-a-key";
        var p = Process.Start(psi)!;
        var lines = new List<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add("ERR " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return (p, lines, dir);
    }

    private static async Task Until(Func<bool> cond, double seconds, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > seconds) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    private string All(List<string> lines) { lock (lines) { var s = string.Join("\n", lines); output.WriteLine(s); return s; } }

    [Fact]
    public async Task Main_mode_quits_within_seconds_on_stdin_quit_while_still_connecting()
    {
        var (p, lines, _) = Start("--stdin-commands", "--shutdown-timeout", "10");
        try
        {
            await Until(() => { lock (lines) return lines.Any(l => l.Contains("connect failed")); }, 60, "first connect attempt");
            var sw = Stopwatch.StartNew();
            await p.StandardInput.WriteLineAsync("/quit");
            await p.StandardInput.FlushAsync();
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            output.WriteLine($"exited {p.ExitCode} after {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.Equal(0, p.ExitCode);
        }
        finally { if (!p.HasExited) p.Kill(true); All(lines); }
    }

    [Fact]
    public async Task Main_mode_stops_within_seconds_on_SIGTERM()
    {
        if (OperatingSystem.IsWindows()) return; // no SIGTERM to another process on Windows; Ctrl+C/console close share the same token
        var (p, lines, _) = Start("--shutdown-timeout", "10");
        try
        {
            await Until(() => { lock (lines) return lines.Any(l => l.Contains("connect failed")); }, 60, "first connect attempt");
            var sw = Stopwatch.StartNew();
            Process.Start("kill", ["-TERM", p.Id.ToString()])!.WaitForExit();
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.Equal(0, p.ExitCode);
        }
        finally { if (!p.HasExited) p.Kill(true); All(lines); }
    }

    [Fact]
    public async Task Clone_mode_logs_out_within_seconds_on_recall()
    {
        var pipe = PipeNames.For("maid_chan_67");
        await using var server = PipeNames.Server(pipe);
        var (p, lines, _) = Start("--clone", "maid_chan_67", "--parent-pipe", pipe);
        try
        {
            await server.WaitForConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
            await using var link = new HiveLink(server, "test-main");
            var hello = new TaskCompletionSource();
            link.Handler = (m, _) =>
            {
                if (m.T != Msg.Hello) return Task.FromResult<object?>(null);
                hello.TrySetResult();
                return Task.FromResult<object?>(new WelcomeMsg("quit_test", null, new RosterMsg("quit_test", ["quit_test"], [], new()), new ClaimsMsg([]), false, 0));
            };
            link.Start();
            await hello.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Until(() => { lock (lines) return lines.Any(l => l.Contains("connect failed") || l.Contains("connecting to")); }, 60, "clone connecting");
            var sw = Stopwatch.StartNew();
            await link.SendAsync(Msg.Recall, new RecallMsg("test recall", 5));
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
            output.WriteLine($"clone exited {p.ExitCode} after {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.Equal(0, p.ExitCode);
        }
        finally { if (!p.HasExited) p.Kill(true); All(lines); }
    }
}
