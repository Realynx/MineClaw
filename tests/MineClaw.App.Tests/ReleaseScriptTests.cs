using System.Diagnostics;
using MineClaw.App.Hosting;

namespace MineClaw.App.Tests;

/// <summary>
/// deploy/ct-release.sh rollback guard (live 10-27: <c>-Rollback</c> crash-looped, the previous build couldn't open db schema
/// v6). Runs the real script under bash against a temp release tree (MINECLAW_RELEASE_TEST seam; systemctl/journalctl are
/// stubbed through BASH_ENV). Skipped quietly where no bash is installed.
/// </summary>
public class ReleaseScriptTests : IDisposable
{
    private const string Old = "20261027-120000", New = "20261027-200000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mineclaw-rel-" + Guid.NewGuid().ToString("N")[..8]);
    private string App => Path.Combine(_root, "app");
    private string Data => Path.Combine(_root, "data");
    private string Db => Path.Combine(Data, "mineclaw.db");
    private string Backups => Path.Combine(Data, "backups");

    public ReleaseScriptTests()
    {
        foreach (var id in new[] { Old, New }) Directory.CreateDirectory(Path.Combine(App, "releases", id));
        Directory.CreateDirectory(Backups);
        File.WriteAllText(Path.Combine(App, "current"), New + "\n");
        File.WriteAllText(Path.Combine(App, "releases", Old, "SCHEMA_VERSION"), "5\n");
        File.WriteAllText(Path.Combine(App, "releases", New, "SCHEMA_VERSION"), "6\n");
        File.WriteAllText(Path.Combine(_root, "stubs.sh"),
            "systemctl() { echo \"systemctl $*\" >> \"$MINECLAW_DATA/../calls.log\"; return 0; }\n" +
            "journalctl() { echo '2026-10-27T20:00:00 mineclaw: joined 10.1.2.69 as maid_chan'; }\n" +
            "sleep() { :; }\n");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>A SQLite file header (100 bytes + padding) with PRAGMA user_version = <paramref name="v"/> at offset 60.</summary>
    private static byte[] DbBytes(int v, byte fill = 0)
    {
        var b = new byte[512];
        Array.Fill(b, fill, 100, 412);
        "SQLite format 3\0"u8.ToArray().CopyTo(b, 0);
        b[60] = (byte)(v >> 24); b[61] = (byte)(v >> 16); b[62] = (byte)(v >> 8); b[63] = (byte)v;
        return b;
    }

    private static string? Bash()
    {
        foreach (var p in new[] { @"C:\Program Files\Git\bin\bash.exe", "/bin/bash", "/usr/bin/bash" })
            if (File.Exists(p)) return p;
        return null;
    }

    private (int Exit, string Out) Run(params string[] args)
    {
        var bash = Bash()!;
        var src = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "ct-release.sh");
        var script = Path.Combine(_root, "ct-release.sh");
        File.WriteAllText(script, File.ReadAllText(src).Replace("\r\n", "\n")); // bash wants LF (deploy-alice.ps1 converts too)
        var psi = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(script.Replace('\\', '/'));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["MINECLAW_RELEASE_TEST"] = "1";
        psi.Environment["MINECLAW_APP"] = App.Replace('\\', '/');
        psi.Environment["MINECLAW_DATA"] = Data.Replace('\\', '/');
        psi.Environment["BASH_ENV"] = Path.Combine(_root, "stubs.sh").Replace('\\', '/');
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "script hung");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private string Current() => File.ReadAllText(Path.Combine(App, "current")).Trim();

    [Fact]
    public void Rollback_to_an_older_schema_without_a_backup_refuses_and_changes_nothing()
    {
        if (Bash() is null) return;
        File.WriteAllBytes(Db, DbBytes(6));
        var (exit, output) = Run("rollback");
        Assert.NotEqual(0, exit);
        Assert.Contains("refusing to roll back: the live db is schema v6 but releases/" + Old + " opens schema v5", output);
        Assert.Equal(New, Current());
        Assert.Equal(DbBytes(6), File.ReadAllBytes(Db));
    }

    [Fact]
    public void Dry_run_prints_the_restore_plan_and_changes_nothing()
    {
        if (Bash() is null) return;
        File.WriteAllBytes(Db, DbBytes(6));
        var backup = $"mineclaw-20261027T200000Z-rel{Old}-v5.db";
        File.WriteAllBytes(Path.Combine(Backups, backup), DbBytes(5, fill: 7));
        var (exit, output) = Run("rollback", "--dry-run");
        Assert.Equal(0, exit);
        Assert.Contains("live db: schema v6", output);
        Assert.Contains("restoring", output);
        Assert.Contains(backup, output);
        Assert.Contains("dry run: nothing changed", output);
        Assert.Equal(New, Current());
        Assert.Equal(DbBytes(6), File.ReadAllBytes(Db));
    }

    [Fact]
    public void Rollback_restores_the_matching_backup_and_keeps_the_newer_db_aside()
    {
        if (Bash() is null) return;
        File.WriteAllBytes(Db, DbBytes(6));
        File.WriteAllBytes(Path.Combine(Backups, $"mineclaw-20261027T190000Z-rel{Old}-v4.db"), DbBytes(4, fill: 3));
        var backup = $"mineclaw-20261027T200000Z-rel{Old}-v5.db";
        File.WriteAllBytes(Path.Combine(Backups, backup), DbBytes(5, fill: 7));
        var (exit, output) = Run("rollback", "--timeout", "4");
        Assert.True(exit == 0, output);
        Assert.Equal(Old, Current());
        Assert.Equal(DbBytes(5, fill: 7), File.ReadAllBytes(Db)); // the newest v5 backup taken under the old release
        Assert.Contains("db: restored " + backup, output);
        var aside = Directory.GetFiles(Backups, $"*-rolledback-from-rel{New}-v6.db");
        Assert.Single(aside);
        Assert.Equal(DbBytes(6), File.ReadAllBytes(aside[0]));
        Assert.Contains("HEALTHY", output);
    }

    [Fact]
    public void A_db_the_older_build_can_open_is_kept()
    {
        if (Bash() is null) return;
        File.WriteAllBytes(Db, DbBytes(5));
        var (exit, output) = Run("rollback", "--dry-run");
        Assert.Equal(0, exit);
        Assert.Contains("db: schema v5 opens with releases/" + Old + " (v5): kept as it is", output);
    }

    [Fact]
    public void An_uncheckpointed_migration_in_the_wal_counts_as_the_live_schema()
    {
        if (Bash() is null) return;
        File.WriteAllBytes(Db, DbBytes(5));
        // WAL: 32-byte header (page size 512 at offset 8), one frame: 24-byte header (page 1) + the migrated page 1 (v6).
        var wal = new byte[32 + 24 + 512];
        wal[8] = 0; wal[9] = 0; wal[10] = 2; wal[11] = 0;
        wal[32 + 3] = 1;
        DbBytes(6).CopyTo(wal, 32 + 24);
        File.WriteAllBytes(Db + "-wal", wal);
        var (exit, output) = Run("rollback", "--dry-run");
        Assert.NotEqual(0, exit);
        Assert.Contains("live db is schema v6", output);
    }

    [Fact]
    public void The_app_reports_the_schema_version_it_opens()
    {
        var cli = AppCli.Parse(["--schema-version"]);
        Assert.True(cli.PrintSchemaVersion);
        Assert.Empty(cli.Errors);
        Assert.True(MineClaw.Agent.Rag.AgentDb.LatestSchemaVersion >= 6);
    }
}
