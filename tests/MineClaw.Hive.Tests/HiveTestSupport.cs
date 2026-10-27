using System.Diagnostics;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Db;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive.Tests;

/// <summary>Finds and starts the fake clone process (CloneStub) built next to the tests.</summary>
public static class Stub
{
    public static string Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        // .../tests/MineClaw.Hive.Tests/bin/<Config>/net10.0/
        var tfm = d.Name;
        var config = d.Parent!.Name;
        var project = d.Parent.Parent!.Parent!.FullName;
        var dir = Path.Combine(project, "CloneStub", "bin", config, tfm);
        // --artifacts-path layout: <artifacts>/bin/MineClaw.Hive.Tests/<config>/ next to <artifacts>/bin/MineClaw.Hive.CloneStub/<config>/
        if (!Directory.Exists(dir)) dir = Path.Combine(d.Parent.Parent!.FullName, "MineClaw.Hive.CloneStub", d.Name);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("clone stub not built: " + dir);
        return dir;
    }

    public static (string Exe, string[] Prefix) Command()
    {
        var dir = Dir();
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "MineClaw.Hive.CloneStub.exe" : "MineClaw.Hive.CloneStub");
        if (File.Exists(apphost)) return (apphost, []);
        return ("dotnet", [Path.Combine(dir, "MineClaw.Hive.CloneStub.dll")]);
    }

    public static ProcessLauncher Launcher(params string[] extra)
    {
        var (exe, prefix) = Command();
        return new ProcessLauncher(exe, prefix, extra);
    }

    /// <summary>Starts the stub directly (no supervisor) and captures stdout lines.</summary>
    public static (Process Process, List<string> Lines) StartRaw(params string[] args)
    {
        var (exe, prefix) = Command();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var a in prefix.Concat(args)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var lines = new List<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add("ERR " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return (p, lines);
    }
}

/// <summary>A main-side hive (supervisor + board + ledger + arbiter + shared db) for process tests.</summary>
public sealed class TestHive : IAsyncDisposable
{
    public string DbPath { get; }
    public AgentDb Db { get; }
    public HiveStore Store { get; }
    public ClaimBoard Board { get; } = new(TimeSpan.FromSeconds(60));
    public BudgetLedger Ledger { get; }
    public ChatArbiter Arbiter { get; }
    public HiveSupervisor Supervisor { get; }
    public List<GameEvent> Notices { get; } = new();
    public decimal MainToday { get; set; }
    public List<string> Online { get; } = ["maid_chan", "poofimafox", "kiwi"];
    public int MaxPlayers { get; set; } = 7;
    /// <summary>The server's player list from somewhere else (a real client on the fake server) instead of <see cref="Online"/>.</summary>
    public Func<SlotFacts>? Facts { get; set; }

    public TestHive(HiveConfig? cfg = null, Func<string, ProcessLauncher>? launcher = null, string mode = "normal", params string[] stubArgs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "mineclaw.db");
        Db = AgentDb.Open(DbPath);
        Store = new HiveStore(Db, "maid_chan");
        cfg ??= Config();
        Ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (MainToday, MainToday));
        Arbiter = new ChatArbiter("maid_chan", "maid_chan", cfg.Clones());
        var l = launcher?.Invoke(DbPath) ?? Stub.Launcher(["--db", DbPath, "--mode", mode, .. stubArgs]);
        Supervisor = new HiveSupervisor(cfg, "maid_chan", l, Board, Ledger, Arbiter, Store,
            () => { if (Facts is { } f) return f(); lock (Online) return new SlotFacts(MaxPlayers, Online.ToList()); }, ["poofimafox", "kiwi"]);
        Supervisor.Notice += e => { lock (Notices) Notices.Add(e); };
    }

    public static HiveConfig Config() => new()
    {
        CloneUsernames = ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"],
        HeartbeatSeconds = 0.5, HeartbeatTimeoutSeconds = 6, StartTimeoutSeconds = 30,
        BackoffBaseSeconds = 60, MaxCrashes = 3, SnapshotSeconds = 0.3,
        LaunchGapSeconds = 0, // the join throttle has its own test
    };

    public string NoticeText() { lock (Notices) return string.Join(" || ", Notices.Select(n => n.Text)); }

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync();
        Db.Dispose();
    }
}

public static class Wait
{
    public static async Task Until(Func<bool> cond, double seconds = 30, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > seconds) throw new TimeoutException("timed out waiting for " + (what ?? "condition"));
            await Task.Delay(50);
        }
    }
}
