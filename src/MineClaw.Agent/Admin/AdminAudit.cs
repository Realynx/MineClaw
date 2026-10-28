using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Admin;

/// <summary>
/// Append-only audit trail of admin writes: one JSON object per line (snake_case, UTF-8) plus one log line per entry. Thread-safe
/// (also across <see cref="AdminAudit"/> instances on the same file in one process). Never contains secrets: entries hold only
/// the record before/after, who, target and the outcome.
/// </summary>
public sealed class AdminAudit
{
    private static readonly object FileLock = new();

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly ILogger _log;

    public AdminAudit(string jsonlPath, ILogger? log = null)
    {
        Path = System.IO.Path.GetFullPath(jsonlPath);
        _log = log ?? NullLogger.Instance;
    }

    public string Path { get; }

    public void Write(AuditEntry e)
    {
        var line = JsonSerializer.Serialize(e, Json);
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + "\n");
            }
        }
        catch (IOException ex) { _log.LogWarning("admin audit: can't write {Path}: {Error}", Path, ex.Message); }
        catch (UnauthorizedAccessException ex) { _log.LogWarning("admin audit: can't write {Path}: {Error}", Path, ex.Message); }
        var level = e.Ok ? LogLevel.Information : LogLevel.Warning;
        _log.Log(level, "admin {Op} {Kind} {Id} by {Who} on {Target}: {Outcome} {Message} | before {Before} | after {After}",
            e.Op, e.Kind, e.Id ?? "-", e.Who, e.Target, e.Ok ? "ok" : "FAILED", e.Message,
            TextUtil.Truncate(e.Before ?? "-", 300), TextUtil.Truncate(e.After ?? "-", 300));
    }

    /// <summary>The newest <paramref name="max"/> entries, oldest first (unreadable lines are skipped).</summary>
    public IReadOnlyList<AuditEntry> Read(int max = 500)
    {
        string[] lines;
        lock (FileLock)
        {
            if (!File.Exists(Path)) return [];
            try { lines = File.ReadAllLines(Path); }
            catch (IOException) { return []; }
        }
        var list = new List<AuditEntry>();
        foreach (var l in lines.Skip(Math.Max(0, lines.Length - Math.Max(0, max))))
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            try { if (JsonSerializer.Deserialize<AuditEntry>(l, Json) is { } e) list.Add(e); }
            catch (JsonException) { }
        }
        return list;
    }
}

/// <summary>Consistent copies of an <see cref="AgentDb"/>.</summary>
public static class AdminBackup
{
    /// <summary>
    /// <c>VACUUM INTO</c> on the db's own connection (a consistent, compacted snapshot even while the bot writes, WAL included)
    /// into <paramref name="directory"/>/memory-backup-{utc yyyyMMdd-HHmmss-fff}.db. Returns the full path.
    /// </summary>
    public static string Create(AgentDb db, string directory)
    {
        if (db.Path == null) throw new InvalidOperationException("an in-memory database has no file to back up");
        var dir = System.IO.Path.GetFullPath(directory);
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"memory-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.db");
        if (File.Exists(path)) throw new IOException($"backup already exists: {path}");
        lock (db.Gate) db.Exec("VACUUM INTO $p", ("$p", path));
        return path;
    }
}
