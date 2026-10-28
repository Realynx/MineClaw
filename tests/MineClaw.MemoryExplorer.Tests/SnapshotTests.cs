using System.Formats.Tar;
using MineClaw.MemoryExplorer.Services;
using Microsoft.Data.Sqlite;

namespace MineClaw.MemoryExplorer.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-snap-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 10, 28, 14, 30, 5, TimeSpan.Zero);

    public SnapshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Snapshot_files_are_named_by_utc_time_and_never_clobber()
    {
        var svc = new SnapshotService(new FakeRunner(), _dir);
        var p = svc.PathFor(At);
        Assert.Equal(Path.Combine(_dir, "memory-20261028-143005.db"), p);
        File.WriteAllText(p, "x");
        var p2 = svc.PathFor(At);
        Assert.Equal(Path.Combine(_dir, "memory-20261028-143005-2.db"), p2);
        Assert.True(SnapshotService.TryParseStamp("memory-20261028-143005.db", out var t));
        Assert.Equal(At, t);
        Assert.False(SnapshotService.TryParseStamp("mineclaw.db", out _));
        Assert.False(SnapshotService.TryParseStamp("memory-2026-10-28.db", out _));
        Assert.Equal(At, MemoryBank.SnapshotTime(p));
    }

    [Fact]
    public void Snapshot_dir_is_under_the_gitignored_data_folder()
    {
        var root = ExplorerPaths.FindRoot(AppContext.BaseDirectory);
        Assert.True(File.Exists(Path.Combine(root, "MineClaw.sln")));
        var snap = Path.Combine(root, "data", "alice-snap", "memory-20261028-143005.db");
        Assert.True(SnapshotService.IsGitIgnoredDataPath(snap, root));
        Assert.False(SnapshotService.IsGitIgnoredDataPath(Path.Combine(root, "data", "seed", "x.db"), root)); // the seed is committed
        Assert.False(SnapshotService.IsGitIgnoredDataPath(Path.Combine(root, "src", "x.db"), root));
        Assert.True(SnapshotService.IsUnder(snap, Path.Combine(root, "data")));
        Assert.False(SnapshotService.IsUnder(Path.Combine(root, "data", "..", "src", "x.db"), Path.Combine(root, "data")));
    }

    [Fact]
    public void Remote_commands_only_read_and_target_ct_114()
    {
        var args = SnapshotService.Ssh("command -v sqlite3 || true");
        Assert.Contains("BatchMode=yes", args);
        Assert.Equal("alice", args[^2]);
        Assert.StartsWith("pct exec 114 -- sh -c '", args[^1]);
    }

    [Fact]
    public async Task Fetch_uses_sqlite_backup_when_the_ct_has_sqlite3()
    {
        var runner = new FakeRunner { HasSqlite = true, Payload = MakeDb };
        var svc = new SnapshotService(runner, _dir, new FixedTime(At));
        var r = await svc.FetchAsync();
        Assert.Equal("sqlite3 .backup", r.Method);
        Assert.Equal(Path.Combine(_dir, "memory-20261028-143005.db"), r.Path);
        Assert.Contains(runner.Commands, c => c.Contains(".backup /tmp/") && c.Contains("-readonly " + SnapshotService.RemoteDb) && c.Contains("rm -f"));
        Assert.DoesNotContain(runner.Commands, c => c.Contains("tar "));
        Assert.Single(Directory.GetFiles(_dir)); // no .part left behind
        using var bank = await MemoryBank.OpenAsync(r.Path, FixtureDb.Config());
        Assert.Equal(1, bank.Db.ScalarLong("SELECT count(*) FROM docs"));
    }

    [Fact]
    public async Task Fetch_falls_back_to_a_db_plus_wal_copy_and_consolidates_it()
    {
        var runner = new FakeRunner { HasSqlite = false, Payload = MakeTar };
        var svc = new SnapshotService(runner, _dir, new FixedTime(At));
        var r = await svc.FetchAsync();
        Assert.Equal("db + wal copy", r.Method);
        Assert.Contains(runner.Commands, c => c.Contains("tar -cf -"));
        Assert.Single(Directory.GetFiles(_dir));
        using var c = new SqliteConnection($"Data Source={r.Path};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM docs";
        Assert.Equal(2L, cmd.ExecuteScalar()); // the row only in the -wal was recovered
    }

    [Fact]
    public async Task A_failed_fetch_leaves_nothing_behind()
    {
        var runner = new FakeRunner { HasSqlite = true, Payload = p => File.WriteAllText(p, "not a database"), ExitCode = 0 };
        var svc = new SnapshotService(runner, _dir, new FixedTime(At));
        await Assert.ThrowsAnyAsync<Exception>(() => svc.FetchAsync());
        Assert.Empty(Directory.GetFiles(_dir));
        var failing = new SnapshotService(new FakeRunner { HasSqlite = true, Payload = MakeDb, ExitCode = 1 }, _dir, new FixedTime(At));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.FetchAsync());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private static void MakeDb(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT); CREATE TABLE docs(id INTEGER PRIMARY KEY, ref TEXT, kind TEXT, title TEXT, body TEXT); INSERT INTO docs VALUES (1,'a','note','t','b');";
        cmd.ExecuteNonQuery();
    }

    /// <summary>A tar of mineclaw.db + its -wal (one row committed only to the WAL), like the CT copy.</summary>
    private void MakeTar(string tarPath)
    {
        var src = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        var db = Path.Combine(src, "mineclaw.db");
        var keep = new SqliteConnection($"Data Source={db};Pooling=False");
        keep.Open();
        using (var cmd = keep.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE docs(id INTEGER PRIMARY KEY, ref TEXT); INSERT INTO docs VALUES (1,'a'); INSERT INTO docs VALUES (2,'b');";
            cmd.ExecuteNonQuery();
        }
        // Copy while the writer still holds the WAL open (that's the live situation), then let it go.
        var copy = Path.Combine(src, "copy");
        Directory.CreateDirectory(copy);
        foreach (var f in new[] { "mineclaw.db", "mineclaw.db-wal", "mineclaw.db-shm" })
            if (File.Exists(Path.Combine(src, f)))
                using (var a = new FileStream(Path.Combine(src, f), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var b = File.Create(Path.Combine(copy, f)))
                    a.CopyTo(b);
        Assert.True(File.Exists(Path.Combine(copy, "mineclaw.db-wal")));
        TarFile.CreateFromDirectory(copy, tarPath, includeBaseDirectory: false);
        keep.Dispose();
        try { Directory.Delete(src, true); } catch (IOException) { }
    }

    private sealed class FakeRunner : ICommandRunner
    {
        public bool HasSqlite { get; init; }
        public Action<string> Payload { get; init; } = _ => { };
        public int ExitCode { get; init; }
        public List<string> Commands { get; } = [];

        public Task<(int, string, string)> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
        {
            Assert.Equal("ssh", fileName);
            Commands.Add(args[^1]);
            return Task.FromResult((0, HasSqlite ? "/usr/bin/sqlite3\n" : "", ""));
        }

        public Task<(int, string)> RunToFileAsync(string fileName, IReadOnlyList<string> args, string outputPath, CancellationToken ct)
        {
            Commands.Add(args[^1]);
            Payload(outputPath);
            return Task.FromResult((ExitCode, ExitCode == 0 ? "" : "boom"));
        }
    }
}
