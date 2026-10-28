using System.Diagnostics;
using MineClaw.Agent;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.App.Hosting;
using MineClaw.Bot.Tests;

namespace MineClaw.App.Tests;

/// <summary>
/// <c>MineClaw.App --reset-memory</c> (the first deploy into a new world) on a fixture data dir: a complete, verified backup first; a fresh
/// db with only the 1.12.2 pack + sdk-js (no mode); usage.json kept; refusal without --yes; refusal when the backup can't be made; the
/// fresh db boots with the persona and the 1.12.2 core only. Plus the deploy script's dry run (-NoStart / -ResetMemory never enable or
/// start the unit) and its PowerShell parse.
/// </summary>
public sealed class MemoryResetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mineclaw-reset-" + Guid.NewGuid().ToString("N")[..8]);
    private string Data => Path.Combine(_root, "mineclaw");
    private static readonly DateTimeOffset At = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
    private string DefaultBackup => Path.Combine(_root, "mineclaw-backup-20261028T200000Z");

    public MemoryResetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Pack => Path.Combine(Root(), "shards", "defaults", "1.12.2-gamedata.mcshard");

    /// <summary>A lived-in b1.8.1 skyblock data dir: a db with memories (+ its WAL), usage.json, state files, a staged b1.8.1 seed.</summary>
    private void LivedIn()
    {
        Directory.CreateDirectory(Path.Combine(Data, "seed"));
        Directory.CreateDirectory(Path.Combine(Data, "assets"));
        using (var db = AgentDb.Open(Path.Combine(Data, "mineclaw.db")))
        using (var rag = new RagIndex(db, new RagOptions(), NullEmbeddingProvider.Instance, backgroundEmbedding: false))
        using (var mem = new MineClaw.Agent.Memory.MemoryStore(db, rag))
        {
            mem.SetWaypoint("cobble_gen", 3, 64, -2);
            mem.AddNote("kiwi", "likes the cobble gen tidy");
            rag.Upsert(new RagDoc("gd:smelt:12", "gamedata", "smelt sand -> glass", "Smelting in beta 1.8.1: sand smelts into glass.") { Source = "seed" });
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(Data, "usage.json"), """{"day":"2026-10-28","usd":1.23}""");
        File.WriteAllText(Path.Combine(Data, "hive-ledger.json"), """{"clones":0.5}""");
        File.WriteAllText(Path.Combine(Data, "hunger-latch-maid_chan.json"), "{}");
        File.WriteAllText(Path.Combine(Data, "memory.json"), """{"Notes":[{"Id":1,"Subject":"x","Text":"old world"}]}""");
        File.WriteAllText(Path.Combine(Data, "server-version.txt"), "b1.8.1\n");
        File.WriteAllBytes(Path.Combine(Data, "seed", "mineclaw-seed.db"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(Data, "assets", "terrain.png"), [9, 9, 9]);
    }

    private MemoryReset.Request Req(bool yes = true, string? backup = null) => new()
    {
        DataDirectory = Data, PackPath = Pack, Yes = yes, BackupDirectory = backup,
        Options = new AgentOptions { DataDirectory = Data, KnowledgeDirectory = Path.Combine(Root(), "docs", "knowledge") },
    };

    private static Dictionary<string, byte[]> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes);

    [Fact]
    public async Task Reset_backs_up_everything_then_leaves_only_the_packs_and_keeps_usage()
    {
        LivedIn();
        var before = Snapshot(Data);
        var log = new StringWriter();
        var outcome = await MemoryReset.RunAsync(Req(), log, At);
        Assert.True(outcome.Exit == ExitCodes.Ok, log.ToString());

        // The backup: every file, byte for byte, at the timestamped sibling.
        Assert.Equal(DefaultBackup, outcome.Backup);
        var backup = Snapshot(DefaultBackup);
        Assert.Equal(before.Keys.Order(), backup.Keys.Order());
        foreach (var (k, v) in before) Assert.Equal(v, backup[k]);
        Assert.Contains("mineclaw.db", backup.Keys);

        // Kept: budget accounting and everything that isn't world state.
        Assert.Equal(before["usage.json"], File.ReadAllBytes(Path.Combine(Data, "usage.json")));
        Assert.Equal(before["hive-ledger.json"], File.ReadAllBytes(Path.Combine(Data, "hive-ledger.json")));
        Assert.True(File.Exists(Path.Combine(Data, "assets", "terrain.png")));
        // Gone: world state outside the db, the b1.8.1 seed a lost db would bootstrap from.
        Assert.False(File.Exists(Path.Combine(Data, "hunger-latch-maid_chan.json")));
        Assert.False(File.Exists(Path.Combine(Data, "memory.json")));
        Assert.False(File.Exists(Path.Combine(Data, "seed", "mineclaw-seed.db")));
        Assert.Equal("1.12.2", File.ReadAllText(Path.Combine(Data, "server-version.txt")).Trim());

        // The fresh db: only the 1.12.2 pack + sdk-js, no mode, no lived-in rows.
        using (var db = AgentDb.Open(Path.Combine(Data, "mineclaw.db")))
        {
            Assert.Equal(AgentDb.LatestSchemaVersion, db.SchemaVersion);
            var kinds = db.Query("SELECT DISTINCT kind FROM docs", r => r.GetString(0)).Order().ToList();
            Assert.Equal(["gamedata", "knowledge"], kinds);
            Assert.Equal(0, db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'wp:%' OR ref LIKE 'note:%' OR body LIKE '%beta 1.8.1%'"));
            Assert.Equal(0, db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'kb:%' AND ref NOT LIKE 'kb:mechanics_1122.md:%' AND ref NOT LIKE 'kb:sdk-js.md:%'"));
            Assert.True(db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'kb:sdk-js.md:%'") > 20);
            foreach (var t in MemoryReset.LivedInTables)
                if (db.TableExists(t)) Assert.True(db.ScalarLong($"SELECT count(*) FROM {t}") == 0, $"{t} has rows");
            var active = KnowledgePack.Active(db);
            Assert.Equal("1.12.2", active.Version);
            Assert.Equal("1.12.2-gamedata", active.Name);
            Assert.Equal(["sdk-js"], active.Modes!.Select(m => m.Mode));
            Assert.Null(db.GetMeta("memory_json_imported"));
        }
        Assert.Equal(outcome.Counts!["docs.gamedata"], ShardFile.Read(Pack).Records.Count(r => r.Kind == ShardKinds.GameData));
        Assert.Contains("counts:", log.ToString());
    }

    [Fact]
    public async Task The_fresh_db_boots_with_the_persona_defaults_and_only_the_1122_core()
    {
        LivedIn();
        Assert.Equal(ExitCodes.Ok, (await MemoryReset.RunAsync(Req(), new StringWriter(), At)).Exit);
        var o = TestBot.Options();
        o.DataDirectory = Data;
        o.KnowledgeDirectory = Path.Combine(Root(), "docs", "knowledge");
        o.GameVersion = "1.12.2";
        o.Rag.SeedFile = "";
        using (var rt = AgentRuntime.Create(o, (_, _) => Task.CompletedTask, new NoLlm(), persist: true, embeddings: NullEmbeddingProvider.Instance))
        {
            var system = string.Join("\n", rt.Agent.SystemPrompt.Select(b => b.Text));
            Assert.Contains("Core rules (1.12.2)", system);
            Assert.Contains("Minecraft 1.12.2", system); // the pack's prompt line
            Assert.DoesNotContain("The void is below", system);
            Assert.DoesNotContain("Core rules (b1.8.1)", system);
            Assert.Contains(o.Chat.Username, system); // persona from config
            Assert.NotNull(rt.Self);
            Assert.Equal(0, rt.Self!.Version);       // the self-profile at its defaults: no growth
            Assert.Empty(rt.Memory.Read(m => m.Notes.ToList()));
            Assert.Empty(rt.Memory.Read(m => m.Waypoints.ToList()));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Without_yes_it_prints_the_plan_and_changes_nothing()
    {
        LivedIn();
        var before = Snapshot(Data);
        var log = new StringWriter();
        var outcome = await MemoryReset.RunAsync(Req(yes: false), log, At);
        Assert.Equal(ExitCodes.Usage, outcome.Exit);
        Assert.Contains("refusing without --yes", log.ToString());
        Assert.Contains("reset-memory plan", log.ToString());
        Assert.False(Directory.Exists(DefaultBackup));
        var after = Snapshot(Data);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (k, v) in before) Assert.Equal(v, after[k]);
    }

    [Fact]
    public async Task A_failed_backup_refuses_and_changes_nothing()
    {
        LivedIn();
        var before = Snapshot(Data);
        var blocker = Path.Combine(_root, "not-a-dir");
        File.WriteAllText(blocker, "a file where the backup's parent should be");
        var log = new StringWriter();
        var outcome = await MemoryReset.RunAsync(Req(backup: Path.Combine(blocker, "backup")), log, At);
        Assert.Equal(ExitCodes.Error, outcome.Exit);
        Assert.Contains("backup to", log.ToString());
        Assert.Contains("nothing was changed", log.ToString());
        var after = Snapshot(Data);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (k, v) in before) Assert.Equal(v, after[k]);

        // A backup dir that already holds something is refused too.
        var used = Path.Combine(_root, "used");
        Directory.CreateDirectory(used);
        File.WriteAllText(Path.Combine(used, "x"), "x");
        Assert.Equal(ExitCodes.Error, (await MemoryReset.RunAsync(Req(backup: used), new StringWriter(), At)).Exit);
        Assert.True(File.Exists(Path.Combine(Data, "mineclaw.db")));
    }

    [Fact]
    public async Task A_mode_pack_is_refused_as_the_version_pack()
    {
        LivedIn();
        var r = Req() with { PackPath = Path.Combine(Root(), "shards", "defaults", "skyblock.mcshard") };
        var log = new StringWriter();
        Assert.Equal(ExitCodes.Usage, (await MemoryReset.RunAsync(r, log, At)).Exit);
        Assert.Contains("not a version pack", log.ToString());
        Assert.False(Directory.Exists(DefaultBackup));
    }

    [Fact]
    public void The_cli_parses_the_reset_and_guards_its_options()
    {
        var c = AppCli.Parse(["--reset-memory", "--pack", "p.mcshard", "--modes", "none", "--yes", "--data-dir", "/var/lib/mineclaw"]);
        Assert.Empty(c.Errors);
        Assert.True(c.ResetMemory && c.Yes);
        Assert.Equal("p.mcshard", c.ResetPack);
        Assert.Empty(c.ResetModes);
        Assert.Equal(["a.mcshard", "b.mcshard"], AppCli.Parse(["--reset-memory", "--pack", "p", "--modes", "a.mcshard,b.mcshard"]).ResetModes);
        Assert.Contains(AppCli.Parse(["--reset-memory"]).Errors, e => e.Contains("--pack"));
        Assert.Contains(AppCli.Parse(["--pack", "p"]).Errors, e => e.Contains("--reset-memory"));
        Assert.Contains("--reset-memory", AppCli.Usage);
    }

    // ======================= deploy/deploy-alice.ps1 (dry run: no ssh, no publish) =======================

    private static string? Pwsh()
    {
        foreach (var p in new[] { @"C:\Program Files\PowerShell\7\pwsh.exe", "/usr/bin/pwsh", "/usr/local/bin/pwsh", "/opt/microsoft/powershell/7/pwsh" })
            if (File.Exists(p)) return p;
        return null;
    }

    private static (int Exit, string Out) Deploy(params string[] args)
    {
        var psi = new ProcessStartInfo(Pwsh()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(Root(), "deploy", "deploy-alice.ps1") }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "deploy script hung");
        return (p.ExitCode, o.Result + e.Result);
    }

    [Fact]
    public void Deploy_script_parses_and_NoStart_never_enables_or_starts_the_unit()
    {
        if (Pwsh() is null) return;
        var (exit, output) = Deploy("-NoStart", "-DryRun");
        Assert.True(exit == 0, output);
        Assert.Contains("--no-start", output);
        Assert.Contains("enabled state unchanged", output);
        Assert.DoesNotContain("systemctl enable", output);
        Assert.DoesNotContain("reset-memory", output);

        // ct-release.sh install honours --no-start: no `systemctl enable` on that path.
        var script = File.ReadAllText(Path.Combine(Root(), "deploy", "ct-release.sh")).Replace("\r\n", "\n");
        var install = script[script.IndexOf("install_release() {", StringComparison.Ordinal)..script.IndexOf("rollback() {", StringComparison.Ordinal)];
        Assert.Matches(@"if \(\(NO_START\)\); then[^\n]*\n[^\n]*enabled state left as it is[^\n]*\n\s*else\n\s*systemctl enable --quiet mineclaw", install);
    }

    [Fact]
    public void Deploy_script_reset_plan_stops_installs_without_start_and_resets_as_the_service_user()
    {
        if (Pwsh() is null) return;
        var (exit, output) = Deploy("-ResetMemory", "-Pack", Pack, "-DryRun");
        Assert.True(exit == 0, output);
        int stop = output.IndexOf("systemctl stop mineclaw", StringComparison.Ordinal);
        int install = output.IndexOf("mineclaw-release install", StringComparison.Ordinal);
        int reset = output.IndexOf("mineclaw-release reset-memory /root/mineclaw-deploy/1.12.2-gamedata.mcshard", StringComparison.Ordinal);
        Assert.True(stop >= 0 && install > stop && reset > install, output);
        Assert.Contains("--no-start", output[install..reset]);
        Assert.DoesNotContain("--start", output[reset..]);
        Assert.Contains("pct push $v $d/sdk-js.mcshard /root/mineclaw-deploy/sdk-js.mcshard", output);
        Assert.Contains("stopped", output);

        var (exit2, started) = Deploy("-ResetMemory", "-Pack", Pack, "-Start", "-DryRun");
        Assert.True(exit2 == 0, started);
        Assert.Contains("reset-memory /root/mineclaw-deploy/1.12.2-gamedata.mcshard --timeout 180 --start", started);
        Assert.NotEqual(0, Deploy("-Start", "-DryRun").Exit); // -Start only goes with a reset
        Assert.NotEqual(0, Deploy("-ResetMemory", "-DryRun").Exit); // a reset needs its pack
    }
}
