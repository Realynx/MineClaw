using System.Text.Json;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Hierarchy;

/// <summary>One account main created (or adopted from the old fixed roster).</summary>
public sealed record ManagedAccount
{
    public string Name { get; init; } = "";
    /// <summary>Main sent <c>/whitelist add</c> and no remove since: the reconcile removes it when it isn't running.</summary>
    public bool Whitelisted { get; init; }
    /// <summary>Spawned and not released yet.</summary>
    public bool Active { get; init; }
    public string Kind { get; init; } = "hive";
    public string? Brain { get; init; }
    /// <summary>The hive that owns it ("main" or another hive's account).</summary>
    public string Owner { get; init; } = "main";
    public bool Adopted { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset LastUsed { get; init; }
}

/// <summary>
/// The persisted list of accounts main manages (<c>hive-accounts.json</c> in the data directory; atomic replace on every change).
/// The single source of truth for the whitelist exception: only names in here are ever added or removed.
/// </summary>
public sealed class ManagedAccounts
{
    private sealed class FileShape { public List<ManagedAccount> Accounts { get; set; } = []; }

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, ManagedAccount> _rows = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="path">Null = in memory only (tests).</param>
    public ManagedAccounts(string? path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        if (path != null && File.Exists(path))
        {
            try
            {
                var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), HiveJson.Options);
                foreach (var a in shape?.Accounts ?? []) if (!string.IsNullOrWhiteSpace(a.Name)) _rows[a.Name] = a;
            }
            catch (JsonException) { /* a corrupt file = nothing known; the reconcile then can't remove what it doesn't know */ }
        }
    }

    public IReadOnlyList<ManagedAccount> All() { lock (_lock) return _rows.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    public ManagedAccount? Get(string name) { lock (_lock) return _rows.GetValueOrDefault(name); }
    public bool IsManaged(string? name) { if (name == null) return false; lock (_lock) return _rows.ContainsKey(name.Trim()); }
    public int ActiveCount { get { lock (_lock) return _rows.Values.Count(a => a.Active); } }

    public ManagedAccount Upsert(string name, Func<ManagedAccount, ManagedAccount> change)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var cur = _rows.GetValueOrDefault(name) ?? new ManagedAccount { Name = name, Created = now, LastUsed = now };
            var next = change(cur) with { Name = cur.Name };
            _rows[cur.Name] = next;
            Save();
            return next;
        }
    }

    public void SetWhitelisted(string name, bool on) => Upsert(name, a => a with { Whitelisted = on });

    public void MarkActive(string name, string kind, string? brain, string owner) =>
        Upsert(name, a => a with { Active = true, Kind = kind, Brain = brain, Owner = owner, LastUsed = _time.GetUtcNow() });

    public void MarkReleased(string name)
    {
        lock (_lock) if (!_rows.ContainsKey(name)) return;
        Upsert(name, a => a with { Active = false, LastUsed = _time.GetUtcNow() });
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Accounts = _rows.Values.ToList() }, HiveJson.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
