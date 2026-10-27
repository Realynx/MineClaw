using System.Text;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Runtime;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Memory;

/// <summary>
/// Long-term memory in SQLite (<see cref="AgentDb"/>): waypoints, structures, notes/player facts, routines and the
/// bot's identity, with an in-memory mirror for cheap reads. Every record is also a searchable doc in
/// <see cref="RagIndex"/> (FTS + vectors + spatial bounds), so <c>recall</c> and auto-retrieval find it. A legacy
/// <c>memory.json</c> is imported once. Thread-safe; everything handed to the model is bounded and compact.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    public const double MemoryWeight = 1.2;
    private static readonly string[] Prefixes = ["wp:", "st:", "note:", "rt:"];
    private static readonly HashSet<string> GenericSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "world", "base", "island", "server", "spawn", "me", "self", "bot", "everyone", "team", "game", "farm", "home", "general", "us",
    };

    private readonly AgentDb _db;
    private readonly RagIndex _index;
    private readonly bool _ownsDb, _ownsIndex;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private MemoryData _data = new();

    /// <summary>
    /// Standalone store. <paramref name="path"/> is the SQLite file (null = in-memory). For compatibility a
    /// "*.json" path opens "*.db" next to it and imports the JSON once.
    /// </summary>
    public MemoryStore(string? path, TimeProvider? time = null)
        : this(OpenFor(path), null, time, path != null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : null, ownsDb: true)
    {
    }

    public MemoryStore(AgentDb db, RagIndex? index, TimeProvider? time = null, string? importJsonPath = null)
        : this(db, index, time, importJsonPath, ownsDb: false)
    {
    }

    private MemoryStore(AgentDb db, RagIndex? index, TimeProvider? time, string? importJsonPath, bool ownsDb)
    {
        _db = db;
        _ownsDb = ownsDb;
        _ownsIndex = index == null;
        _index = index ?? new RagIndex(db, time: time);
        _time = time ?? TimeProvider.System;
        lock (_lock)
        {
            _data = Load();
            if (importJsonPath != null) ImportJson(importJsonPath);
            ReindexAll();
        }
    }

    private static AgentDb OpenFor(string? path) =>
        AgentDb.Open(path != null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.ChangeExtension(path, ".db") : path);

    public string? Path => _db.Path;
    public AgentDb Db => _db;
    public RagIndex Index => _index;
    /// <summary>The bot's current dimension (new waypoints/structures are stamped with it; readers default to it).</summary>
    public int CurrentDimension => _index.CurrentDimension();

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>
    /// Hive: re-reads waypoints/structures/notes/routines/identity from the db (another process committed). The RAG docs are
    /// already shared through the db, so nothing is re-indexed.
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            var d = Load();
            d.NextNoteId = Math.Max(d.NextNoteId, _data.NextNoteId);
            _data = d;
        }
    }

    /// <summary>Read under the lock (don't keep references to mutable objects after returning).</summary>
    public T Read<T>(Func<MemoryData, T> read) { lock (_lock) return read(_data); }

    /// <summary>Mutate under the lock and persist everything (prefer the specific methods — they write one row).</summary>
    public void Update(Action<MemoryData> mutate)
    {
        lock (_lock)
        {
            mutate(_data);
            _db.Tx(SyncAll);
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ---- waypoints ----
    /// <param name="dimension">Null = the bot's current dimension.</param>
    public Waypoint SetWaypoint(string name, int x, int y, int z, string? note = null, int? dimension = null)
    {
        lock (_lock)
        {
            var w = _data.Waypoints.FirstOrDefault(p => Same(p.Name, name));
            if (w == null) _data.Waypoints.Add(w = new Waypoint { Name = name.Trim() });
            w.X = x; w.Y = y; w.Z = z;
            w.Dimension = dimension ?? CurrentDimension;
            if (note != null) w.Note = note;
            w.Updated = Now;
            _db.Tx(() => { SaveWaypoint(w); _index.Upsert(Doc(w)); });
            return w;
        }
    }

    public Waypoint? GetWaypoint(string name) { lock (_lock) return _data.Waypoints.FirstOrDefault(p => Same(p.Name, name)); }

    // ---- structures ----
    public Structure UpsertStructure(string name, Action<Structure> update)
    {
        lock (_lock)
        {
            var s = _data.Structures.FirstOrDefault(p => Same(p.Name, name));
            if (s == null) _data.Structures.Add(s = new Structure { Name = name.Trim(), Dimension = CurrentDimension });
            update(s);
            s.Updated = Now;
            _db.Tx(() => { SaveStructure(s); _index.Upsert(Doc(s)); });
            return s;
        }
    }

    public Structure? GetStructure(string name) { lock (_lock) return _data.Structures.FirstOrDefault(p => Same(p.Name, name)); }

    // ---- notes ----
    public Note AddNote(string subject, string text)
    {
        lock (_lock)
        {
            var existing = _data.Notes.FirstOrDefault(n => Same(n.Subject, subject) && Same(n.Text, text));
            if (existing != null) return existing;
            var n = new Note { Subject = subject.Trim(), Text = text.Trim(), Created = Now };
            _db.Tx(() =>
            {
                // Id from the db inside the (IMMEDIATE) transaction: several hive processes share the notes table.
                n.Id = (int)Math.Max(_data.NextNoteId, _db.ScalarLong("SELECT COALESCE(MAX(id), 0) + 1 FROM notes"));
                SaveNote(n);
                _index.Upsert(Doc(n));
            });
            _data.NextNoteId = n.Id + 1;
            _data.Notes.Add(n);
            return n;
        }
    }

    // ---- routines ----
    public Routine SaveRoutine(Routine routine)
    {
        lock (_lock)
        {
            _data.Routines.RemoveAll(r => Same(r.Name, routine.Name));
            routine.Updated = Now;
            _data.Routines.Add(routine);
            _db.Tx(() => { SaveRoutineRow(routine); _index.Upsert(Doc(routine)); });
            return routine;
        }
    }

    public Routine? GetRoutine(string name) { lock (_lock) return _data.Routines.FirstOrDefault(r => Same(r.Name, name)); }

    // ---- identity ----
    public BotIdentity? Identity { get { lock (_lock) return _data.Identity; } }

    public void SetIdentity(string displayName, IEnumerable<string> aliases, string? namedBy)
    {
        lock (_lock)
        {
            _data.Identity ??= new BotIdentity();
            _data.Identity.DisplayName = displayName;
            _data.Identity.Aliases = aliases.ToList();
            if (namedBy != null) _data.Identity.NamedBy = namedBy;
            _data.Identity.Updated = Now;
            SaveIdentity(_data.Identity);
        }
    }

    // ---- generic ----

    /// <summary>Removes an entry by kind (waypoint|structure|note|routine, or null = any) and name (note: id or text match).</summary>
    public int Forget(string? kind, string name)
    {
        lock (_lock)
        {
            kind = kind?.Trim().ToLowerInvariant().TrimEnd('s');
            var refs = new List<string>();
            int n = 0;
            if (kind is null or "" or "waypoint")
                foreach (var w in _data.Waypoints.Where(w => Same(w.Name, name)).ToList()) { _data.Waypoints.Remove(w); refs.Add(Ref(w)); n++; }
            if (kind is null or "" or "structure")
                foreach (var s in _data.Structures.Where(s => Same(s.Name, name)).ToList()) { _data.Structures.Remove(s); refs.Add(Ref(s)); n++; }
            if (kind is null or "" or "routine")
                foreach (var r in _data.Routines.Where(r => Same(r.Name, name)).ToList()) { _data.Routines.Remove(r); refs.Add(Ref(r)); n++; }
            if (kind is null or "" or "note" or "fact" or "player")
            {
                var notes = int.TryParse(name.TrimStart('#'), out var id)
                    ? _data.Notes.Where(x => x.Id == id).ToList()
                    : _data.Notes.Where(x => x.Text.Contains(name, StringComparison.OrdinalIgnoreCase) || Same(x.Subject, name)).ToList();
                foreach (var x in notes) { _data.Notes.Remove(x); refs.Add(Ref(x)); n++; }
            }
            if (n > 0)
            {
                _db.Tx(() =>
                {
                    foreach (var r in refs)
                    {
                        DeleteRow(r);
                        _index.Delete(r);
                    }
                });
            }
            return n;
        }
    }

    /// <summary>Compact list of names for a kind (bounded).</summary>
    public string List(string? kind, int maxChars = 500)
    {
        lock (_lock)
        {
            kind = kind?.Trim().ToLowerInvariant().TrimEnd('s');
            var parts = new List<string>();
            int cur = CurrentDimension;
            if (kind is null or "" or "waypoint" && _data.Waypoints.Count > 0)
                parts.Add("waypoints: " + string.Join(", ", _data.Waypoints.Select(w => $"{w.Name}@{w.X},{w.Y},{w.Z}{WorldDim.Tag(w.Dimension, cur)}")));
            if (kind is null or "" or "structure" && _data.Structures.Count > 0)
                parts.Add("structures: " + string.Join(", ", _data.Structures.Select(s => $"{s.Name}({s.Kind})")));
            if (kind is null or "" or "routine" && _data.Routines.Count > 0)
                parts.Add("routines: " + string.Join(", ", _data.Routines.Select(r => r.Name)));
            if (kind is null or "" or "note" or "fact" && _data.Notes.Count > 0)
                parts.Add($"notes: {_data.Notes.Count} (subjects: " + string.Join(", ", _data.Notes.Select(n => n.Subject).Distinct(StringComparer.OrdinalIgnoreCase).Take(12)) + ")");
            var s = parts.Count == 0 ? "nothing saved" : string.Join(" | ", parts);
            return TextUtil.Truncate(s, maxChars);
        }
    }

    /// <summary>
    /// Local search (FTS + spatial; no network) over everything indexed; one compact line per hit, prefixed with
    /// its id ("#12 wp home 1,64,2"). Bounded by <paramref name="maxHits"/> and <paramref name="maxChars"/>.
    /// </summary>
    /// <param name="dimension">Null = current dimension; <see cref="WorldDim.Any"/> = all.</param>
    public string Recall(string query, int maxHits = 6, int maxChars = 700, int? dimension = null)
    {
        var hits = _index.Search(new RagQuery { Text = query, Limit = maxHits, Dimension = dimension ?? CurrentDimension });
        return FormatHits(_index, hits, maxChars, 240);
    }

    public static string FormatHits(RagIndex index, IReadOnlyList<RagHit> hits, int maxChars, int maxLine)
    {
        if (hits.Count == 0) return "no memories match";
        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            var line = index.FormatHit(h, maxLine);
            if (sb.Length + line.Length + 1 > maxChars) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length == 0) sb.Append(index.FormatHit(hits[0], maxChars));
        return sb.ToString();
    }

    public static string FormatStructure(Structure s)
    {
        var sb = new StringBuilder($"struct {s.Name} [{s.Kind}]");
        if (s.Min != null) sb.Append($" {s.Min}" + (s.Max != null && s.Max != s.Min ? $"..{s.Max}" : ""));
        if (s.KeyBlocks.Count > 0) sb.Append(" keys: " + string.Join("; ", s.KeyBlocks.Select(k => $"{k.Label}@{k.X},{k.Y},{k.Z}")));
        if (s.Steps.Count > 0) sb.Append(" how: " + string.Join("; ", s.Steps.Select((x, i) => $"{i + 1}) {x}")));
        if (s.Note != null) sb.Append($" note: {s.Note}");
        if (s.TaughtBy != null) sb.Append($" (by {s.TaughtBy}");
        sb.Append(s.TaughtBy != null ? $", conf {s.Confidence:0.#})" : $" (conf {s.Confidence:0.#})");
        return sb.ToString();
    }

    public static string FormatRoutine(Routine r) =>
        $"routine {r.Name}: {string.Join(">", r.Steps.Select(x => x.Tool))}" + (r.Repeat != 1 ? $" x{(r.Repeat == 0 ? "inf" : r.Repeat)}" : "") +
        (r.StopWhen != null ? $" until '{r.StopWhen}'" : "") + (r.Note != null ? $" ({r.Note})" : "");

    // ---------------- index docs ----------------

    private static string Ref(Waypoint w) => "wp:" + w.Name.Trim().ToLowerInvariant();
    private static string Ref(Structure s) => "st:" + s.Name.Trim().ToLowerInvariant();
    private static string Ref(Note n) => "note:" + n.Id;
    private static string Ref(Routine r) => "rt:" + r.Name.Trim().ToLowerInvariant();

    private static RagDoc Doc(Waypoint w) => new(Ref(w), "waypoint", w.Name, $"wp {w.Name} {w.X},{w.Y},{w.Z}" + (w.Note != null ? $" ({w.Note})" : ""))
    {
        Bounds = Box.Point(w.X, w.Y, w.Z), Dimension = w.Dimension, Weight = MemoryWeight, Source = "memory",
    };

    private static RagDoc Doc(Structure s)
    {
        Box? b = null;
        if (s.Min != null)
        {
            var mx = s.Max ?? s.Min;
            b = Box.Of(s.Min.X, s.Min.Y, s.Min.Z, mx.X, mx.Y, mx.Z);
        }
        foreach (var k in s.KeyBlocks)
            b = b is { } bb ? bb.Union(Box.Point(k.X, k.Y, k.Z)) : Box.Point(k.X, k.Y, k.Z);
        return new RagDoc(Ref(s), "structure", $"{s.Name} {s.Kind.Replace('_', ' ')}", FormatStructure(s))
            { Bounds = b, Dimension = s.Dimension, Weight = MemoryWeight, Source = "memory" };
    }

    private static RagDoc Doc(Note n) =>
        new(Ref(n), GenericSubjects.Contains(n.Subject) ? "note" : "player", n.Subject, $"note#{n.Id} {n.Subject}: {n.Text}") { Weight = MemoryWeight, Source = "memory" };

    private static RagDoc Doc(Routine r) => new(Ref(r), "routine", r.Name, FormatRoutine(r)) { Weight = MemoryWeight, Source = "memory" };

    private void ReindexAll()
    {
        var docs = _data.Waypoints.Select(Doc).Concat(_data.Structures.Select(Doc)).Concat(_data.Notes.Select(Doc)).Concat(_data.Routines.Select(Doc)).ToList();
        var keep = docs.Select(d => d.Ref).ToHashSet();
        _db.Tx(() =>
        {
            _index.UpsertMany(docs);
            foreach (var p in Prefixes) _index.DeleteByPrefix(p, keep);
        });
    }

    // ---------------- rows ----------------

    private MemoryData Load()
    {
        var d = new MemoryData();
        d.Waypoints = _db.Query("SELECT name, x, y, z, note, updated, dimension FROM waypoints ORDER BY rowid",
            r => new Waypoint
            {
                Name = r.GetString(0), X = r.GetInt32(1), Y = r.GetInt32(2), Z = r.GetInt32(3), Note = AgentDb.StrOrNull(r, 4),
                Updated = AgentDb.FromUnixMs(r.GetInt64(5)), Dimension = r.GetInt32(6),
            });
        d.Structures = _db.Query("SELECT data FROM structures ORDER BY rowid", r => JsonSerializer.Deserialize<Structure>(r.GetString(0), AtomicFile.Json)!)
            .Where(s => s != null).ToList();
        d.Notes = _db.Query("SELECT id, subject, text, created FROM notes ORDER BY id",
            r => new Note { Id = r.GetInt32(0), Subject = r.GetString(1), Text = r.GetString(2), Created = AgentDb.FromUnixMs(r.GetInt64(3)) });
        d.Routines = _db.Query("SELECT data FROM routines ORDER BY rowid", r => JsonSerializer.Deserialize<Routine>(r.GetString(0), AtomicFile.Json)!)
            .Where(x => x != null).ToList();
        d.Identity = _db.Query("SELECT display_name, aliases, named_by, updated FROM identity WHERE id = 1", r => new BotIdentity
        {
            DisplayName = r.GetString(0),
            Aliases = JsonSerializer.Deserialize<List<string>>(r.GetString(1)) ?? [],
            NamedBy = AgentDb.StrOrNull(r, 2),
            Updated = AgentDb.FromUnixMs(r.GetInt64(3)),
        }).FirstOrDefault();
        d.NextNoteId = d.Notes.Count == 0 ? 1 : d.Notes.Max(n => n.Id) + 1;
        return d;
    }

    private void SaveWaypoint(Waypoint w) =>
        _db.Exec("""
            INSERT INTO waypoints(name, x, y, z, note, updated, dimension) VALUES ($n, $x, $y, $z, $note, $u, $dim)
            ON CONFLICT(name) DO UPDATE SET name = excluded.name, x = excluded.x, y = excluded.y, z = excluded.z, note = excluded.note,
                updated = excluded.updated, dimension = excluded.dimension
            """, ("$n", w.Name), ("$x", w.X), ("$y", w.Y), ("$z", w.Z), ("$note", w.Note), ("$u", AgentDb.ToUnixMs(w.Updated)), ("$dim", w.Dimension));

    private void SaveStructure(Structure s) =>
        _db.Exec("""
            INSERT INTO structures(name, kind, taught_by, confidence, data, updated, dimension) VALUES ($n, $k, $t, $c, $d, $u, $dim)
            ON CONFLICT(name) DO UPDATE SET name = excluded.name, kind = excluded.kind, taught_by = excluded.taught_by,
                confidence = excluded.confidence, data = excluded.data, updated = excluded.updated, dimension = excluded.dimension
            """, ("$n", s.Name), ("$k", s.Kind), ("$t", s.TaughtBy), ("$c", s.Confidence),
            ("$d", JsonSerializer.Serialize(s, AtomicFile.Json)), ("$u", AgentDb.ToUnixMs(s.Updated)), ("$dim", s.Dimension));

    private void SaveNote(Note n) =>
        _db.Exec("INSERT OR REPLACE INTO notes(id, subject, text, created) VALUES ($i, $s, $t, $c)",
            ("$i", n.Id), ("$s", n.Subject), ("$t", n.Text), ("$c", AgentDb.ToUnixMs(n.Created)));

    private void SaveRoutineRow(Routine r) =>
        _db.Exec("""
            INSERT INTO routines(name, data, updated) VALUES ($n, $d, $u)
            ON CONFLICT(name) DO UPDATE SET name = excluded.name, data = excluded.data, updated = excluded.updated
            """, ("$n", r.Name), ("$d", JsonSerializer.Serialize(r, AtomicFile.Json)), ("$u", AgentDb.ToUnixMs(r.Updated)));

    private void SaveIdentity(BotIdentity i) =>
        _db.Exec("""
            INSERT INTO identity(id, display_name, aliases, named_by, updated) VALUES (1, $d, $a, $b, $u)
            ON CONFLICT(id) DO UPDATE SET display_name = excluded.display_name, aliases = excluded.aliases, named_by = excluded.named_by, updated = excluded.updated
            """, ("$d", i.DisplayName), ("$a", JsonSerializer.Serialize(i.Aliases)), ("$b", i.NamedBy), ("$u", AgentDb.ToUnixMs(i.Updated)));

    private void DeleteRow(string reference)
    {
        var (prefix, key) = (reference[..(reference.IndexOf(':') + 1)], reference[(reference.IndexOf(':') + 1)..]);
        switch (prefix)
        {
            case "wp:": _db.Exec("DELETE FROM waypoints WHERE name = $n", ("$n", key)); break;
            case "st:": _db.Exec("DELETE FROM structures WHERE name = $n", ("$n", key)); break;
            case "rt:": _db.Exec("DELETE FROM routines WHERE name = $n", ("$n", key)); break;
            case "note:": _db.Exec("DELETE FROM notes WHERE id = $i", ("$i", long.Parse(key))); break;
        }
    }

    private void SyncAll()
    {
        _db.Exec("DELETE FROM waypoints; DELETE FROM structures; DELETE FROM notes; DELETE FROM routines;");
        foreach (var w in _data.Waypoints) SaveWaypoint(w);
        foreach (var s in _data.Structures) SaveStructure(s);
        foreach (var n in _data.Notes) SaveNote(n);
        foreach (var r in _data.Routines) SaveRoutineRow(r);
        if (_data.Identity != null) SaveIdentity(_data.Identity);
        else _db.Exec("DELETE FROM identity");
        ReindexAll();
    }

    /// <summary>One-time import of a legacy memory.json (merged by name; the file is renamed *.imported).</summary>
    private void ImportJson(string path)
    {
        if (_db.GetMeta("memory_json_imported") != null || !File.Exists(path)) return;
        var legacy = AtomicFile.ReadJson<MemoryData>(path);
        if (legacy != null)
        {
            foreach (var w in legacy.Waypoints) { _data.Waypoints.RemoveAll(x => Same(x.Name, w.Name)); _data.Waypoints.Add(w); }
            foreach (var s in legacy.Structures) { _data.Structures.RemoveAll(x => Same(x.Name, s.Name)); _data.Structures.Add(s); }
            foreach (var r in legacy.Routines) { _data.Routines.RemoveAll(x => Same(x.Name, r.Name)); _data.Routines.Add(r); }
            foreach (var n in legacy.Notes)
            {
                if (_data.Notes.Any(x => Same(x.Subject, n.Subject) && Same(x.Text, n.Text))) continue;
                if (_data.Notes.Any(x => x.Id == n.Id)) n.Id = _data.Notes.Max(x => x.Id) + 1;
                _data.Notes.Add(n);
            }
            _data.Identity = legacy.Identity ?? _data.Identity;
            _data.NextNoteId = _data.Notes.Count == 0 ? 1 : _data.Notes.Max(n => n.Id) + 1;
            _db.Tx(SyncAll);
        }
        _db.SetMeta("memory_json_imported", DateTimeOffset.UtcNow.ToString("O"));
        try { File.Move(path, path + ".imported", overwrite: true); } catch (IOException) { }
    }

    public void Dispose()
    {
        if (_ownsIndex) _index.Dispose();
        if (_ownsDb) _db.Dispose();
    }
}
