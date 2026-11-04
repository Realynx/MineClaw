using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.App.Supervisor.Admin;

/// <summary>
/// The override file on disk (<see cref="Hosting.SettingsOverrideFile"/>): read as a JSON object, changed by config key, written
/// atomically (a temp file in the same directory, flushed, renamed over the old one) with mode 0600, after the previous version
/// is copied to <c>settings-backups/settings.override.&lt;utc&gt;.json</c> (0600, the newest <see cref="Keep"/> kept). Keys the
/// registry doesn't know (hand edits) are kept as they are. Thread-safe; one writer at a time.
/// </summary>
public sealed class SettingsOverrideStore(string path, int keep = 10, TimeProvider? time = null)
{
    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public int Keep { get; } = Math.Max(1, keep);
    public string BackupDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "settings-backups");
    public bool Exists => File.Exists(Path);
    public DateTimeOffset? UpdatedAt => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null;

    public int BackupCount => Directory.Exists(BackupDirectory) ? Backups().Count : 0;

    /// <summary>The file as a JSON object (empty when missing; a broken file throws so it is never silently overwritten).</summary>
    public JsonObject Read()
    {
        lock (_lock) return ReadUnlocked();
    }

    private JsonObject ReadUnlocked()
    {
        if (!File.Exists(Path)) return [];
        var text = File.ReadAllText(Path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("the override file is not a JSON object");
    }

    /// <summary>Whether the file holds a value for this key (case-insensitive path).</summary>
    public bool Has(string key) => Get(Read(), key) != null;

    public static JsonNode? Get(JsonObject root, string key)
    {
        JsonNode? n = root;
        foreach (var seg in key.Split(':'))
        {
            if (n is not JsonObject o || Child(o, seg) is not { } name) return null;
            n = o[name];
        }
        return n;
    }

    private static string? Child(JsonObject o, string seg) => o.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(seg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets (value != null) or removes (null) a key in the object; empty parents are pruned on removal.</summary>
    public static void Set(JsonObject root, string key, JsonNode? value)
    {
        var segs = key.Split(':');
        var trail = new List<(JsonObject Parent, string Name)>();
        if (LeafParent(root, segs, create: value != null, trail) is not { } o) return; // nothing to remove
        var leaf = Child(o, segs[^1]);
        if (leaf != null) o.Remove(leaf);
        if (value == null) PruneEmpty(trail);
        else o[leaf ?? segs[^1]] = value;
    }

    /// <summary>
    /// The object holding the key's leaf, walking existing names case-insensitively; a missing (or non-object) parent is created
    /// when <paramref name="create"/>, else null. <paramref name="trail"/> gets each (parent, name) step.
    /// </summary>
    private static JsonObject? LeafParent(JsonObject root, string[] segs, bool create, List<(JsonObject Parent, string Name)> trail)
    {
        var o = root;
        for (int i = 0; i < segs.Length - 1; i++)
        {
            var name = Child(o, segs[i]);
            if (name == null || o[name] is not JsonObject next)
            {
                if (!create) return null;
                name ??= segs[i];
                next = [];
                o[name] = next;
            }
            trail.Add((o, name));
            o = next;
        }
        return o;
    }

    /// <summary>After a removal: empty parents go, innermost first, up to the first one that still holds something.</summary>
    private static void PruneEmpty(List<(JsonObject Parent, string Name)> trail)
    {
        for (int i = trail.Count - 1; i >= 0; i--)
        {
            var (parent, name) = trail[i];
            if (parent[name] is JsonObject child && child.Count == 0) parent.Remove(name);
            else break;
        }
    }

    /// <summary>
    /// Applies the changes (null = remove the key) and writes the file atomically, backing up the previous version first.
    /// Returns the backup's file name (null when there was no previous file).
    /// </summary>
    public string? Write(IReadOnlyList<(string Key, JsonNode? Value)> changes)
    {
        lock (_lock)
        {
            var root = ReadUnlocked();
            foreach (var (key, value) in changes) Set(root, key, value?.DeepClone());
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            string? backup = null;
            if (File.Exists(Path))
            {
                Directory.CreateDirectory(BackupDirectory);
                Restrict(BackupDirectory, directory: true);
                var stamp = _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
                backup = $"settings.override.{stamp}.json";
                var target = System.IO.Path.Combine(BackupDirectory, backup);
                for (int n = 1; File.Exists(target); n++) target = System.IO.Path.Combine(BackupDirectory, backup = $"settings.override.{stamp}-{n}.json");
                File.Copy(Path, target);
                Restrict(target, directory: false);
                Prune();
            }
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
            var tmp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var fs = new FileStream(tmp, opts))
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }
                Restrict(tmp, directory: false);
                File.Move(tmp, Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            return backup;
        }
    }

    /// <summary>The backup files, newest first.</summary>
    public IReadOnlyList<string> Backups() =>
        Directory.Exists(BackupDirectory)
            ? Directory.GetFiles(BackupDirectory, "settings.override.*.json").OrderByDescending(f => f, StringComparer.Ordinal).ToList()
            : [];

    private void Prune()
    {
        foreach (var old in Backups().Skip(Keep))
            try { File.Delete(old); } catch (IOException) { }
    }

    /// <summary>0600 for files and 0700 for the backup directory (Linux; Windows keeps its ACLs).</summary>
    private static void Restrict(string p, bool directory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(p, directory ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
