using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>Runs one external command and streams its stdout into a file (seam for tests: no ssh needed).</summary>
public interface ICommandRunner
{
    /// <returns>Exit code and stderr text.</returns>
    Task<(int ExitCode, string StdErr)> RunToFileAsync(string fileName, IReadOnlyList<string> args, string outputPath, CancellationToken ct);
    Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct);
}

public sealed class ProcessRunner : ICommandRunner
{
    public async Task<(int, string)> RunToFileAsync(string fileName, IReadOnlyList<string> args, string outputPath, CancellationToken ct)
    {
        using var p = Start(fileName, args);
        var err = p.StandardError.ReadToEndAsync(ct);
        await using (var f = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await p.StandardOutput.BaseStream.CopyToAsync(f, ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await err.ConfigureAwait(false));
    }

    public async Task<(int, string, string)> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var p = Start(fileName, args);
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }

    private static Process Start(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
        p.StandardInput.Close(); // never prompt: BatchMode below, and no stdin
        return p;
    }
}

public sealed record SnapshotResult(string Path, DateTimeOffset At, string Method, long Bytes);

/// <summary>
/// "Fetch snapshot from alice": a consistent copy of the live bot's db (CT 114, <c>/var/lib/mineclaw/mineclaw.db</c>) into
/// <c>data/alice-snap/memory-&lt;utc yyyyMMdd-HHmmss&gt;.db</c> (data/ is gitignored).
/// Preferred: <c>sqlite3 -readonly db ".backup /tmp/…"</c> inside the CT (an online-backup copy, removed right after it is
/// streamed back; the bot's data directory is never written). Fallback when the CT has no sqlite3: tar of db + -wal + -shm,
/// which is then recovered locally into a single self-contained file. The live db is never opened read-write from here.
/// </summary>
public sealed class SnapshotService(ICommandRunner runner, string? snapshotDir = null, TimeProvider? time = null)
{
    public const string Host = "alice";
    public const int Container = 114;
    public const string RemoteDb = "/var/lib/mineclaw/mineclaw.db";

    private static readonly Regex Stamp = new(@"^memory-(\d{8}-\d{6})\.db$", RegexOptions.CultureInvariant);

    public string SnapshotDir { get; } = Path.GetFullPath(snapshotDir ?? ExplorerPaths.SnapshotDir);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The file a snapshot taken at <paramref name="at"/> is saved as (unique: a numeric suffix on a clash).</summary>
    public string PathFor(DateTimeOffset at)
    {
        var name = $"memory-{at.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(SnapshotDir, name + ".db");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(SnapshotDir, $"{name}-{i}.db");
        if (!IsUnder(path, SnapshotDir)) throw new InvalidOperationException("snapshot path escapes the snapshot directory");
        return path;
    }

    public static bool TryParseStamp(string fileName, out DateTimeOffset at)
    {
        at = default;
        var m = Stamp.Match(fileName);
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) return false;
        at = new DateTimeOffset(dt, TimeSpan.Zero);
        return true;
    }

    public static bool IsUnder(string path, string dir)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> is covered by the repo's "/data/*" ignore rule (and not the committed seed).</summary>
    public static bool IsGitIgnoredDataPath(string path, string repoRoot)
    {
        var data = Path.Combine(repoRoot, "data");
        if (!IsUnder(path, data)) return false;
        if (IsUnder(path, Path.Combine(data, "seed"))) return false;
        var gi = Path.Combine(repoRoot, ".gitignore");
        return File.Exists(gi) && File.ReadAllLines(gi).Any(l => l.Trim() is "/data/*" or "/data/" or "data/");
    }

    internal static IReadOnlyList<string> Ssh(string remoteCommand) =>
        ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", Host, $"pct exec {Container} -- sh -c '{remoteCommand}'"];

    public async Task<SnapshotResult> FetchAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(SnapshotDir);
        var at = _time.GetUtcNow();
        var target = PathFor(at);
        progress?.Report("checking for sqlite3 in CT 114…");
        var probe = await runner.RunAsync("ssh", Ssh("command -v sqlite3 || true"), ct).ConfigureAwait(false);
        if (probe.ExitCode != 0) throw new InvalidOperationException("ssh alice failed: " + OneLine(probe.StdErr));
        bool hasSqlite = probe.StdOut.Trim().Length > 0;
        string method;
        var partial = target + ".part";
        try
        {
            if (hasSqlite)
            {
                method = "sqlite3 .backup";
                progress?.Report("online backup in the CT (sqlite3 .backup), streaming…");
                var tmp = $"/tmp/mineclaw-explorer-{at.ToUnixTimeMilliseconds()}.db";
                var cmd = $"sqlite3 -readonly {RemoteDb} \".timeout 10000\" \".backup {tmp}\" && cat {tmp}; rc=$?; rm -f {tmp}; exit $rc";
                var (code, err) = await runner.RunToFileAsync("ssh", Ssh(cmd), partial, ct).ConfigureAwait(false);
                if (code != 0) throw new InvalidOperationException($"backup failed (exit {code}): {OneLine(err)}");
                File.Move(partial, target);
            }
            else
            {
                method = "db + wal copy";
                progress?.Report("no sqlite3 in the CT: copying db + -wal + -shm…");
                var dir = Path.GetDirectoryName(RemoteDb)!.Replace('\\', '/');
                var name = Path.GetFileName(RemoteDb);
                var cmd = $"cd {dir} && tar -cf - {name} $(ls {name}-wal {name}-shm 2>/dev/null)";
                var (code, err) = await runner.RunToFileAsync("ssh", Ssh(cmd), partial, ct).ConfigureAwait(false);
                if (code != 0) throw new InvalidOperationException($"copy failed (exit {code}): {OneLine(err)}");
                progress?.Report("recovering the copy into one file…");
                Consolidate(partial, target);
                File.Delete(partial);
            }
            Verify(target);
        }
        catch
        {
            TryDelete(partial);
            TryDelete(target);
            throw;
        }
        var bytes = new FileInfo(target).Length;
        progress?.Report($"snapshot saved: {ExplorerPaths.Display(target)} ({MemoryBank.FormatBytes(bytes)})");
        return new SnapshotResult(target, at, method, bytes);
    }

    /// <summary>
    /// Unpacks the tar of db (+ -wal/-shm) into a temp folder, lets SQLite replay the WAL on that LOCAL copy (the stale -shm is
    /// dropped; it is only an index), and writes one self-contained file with <c>VACUUM INTO</c>.
    /// </summary>
    internal static void Consolidate(string tarPath, string target)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mineclaw-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            System.Formats.Tar.TarFile.ExtractToDirectory(tarPath, tmp, overwriteFiles: true);
            var db = Directory.EnumerateFiles(tmp, "*.db").FirstOrDefault() ?? throw new InvalidDataException("no db in the copy");
            TryDelete(db + "-shm");
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "VACUUM INTO $t";
                cmd.Parameters.AddWithValue("$t", target);
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The copy is a readable SQLite db with the bot's schema (integrity quick check).</summary>
    public static void Verify(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        var ok = cmd.ExecuteScalar() as string;
        if (ok != "ok") throw new InvalidDataException("snapshot failed the integrity check: " + ok);
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'docs'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) throw new InvalidDataException("snapshot has no docs table: not a MineClaw db");
    }

    private static string OneLine(string s)
    {
        var t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length > 240 ? t[..240] + "…" : t.Length == 0 ? "(no output)" : t;
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
