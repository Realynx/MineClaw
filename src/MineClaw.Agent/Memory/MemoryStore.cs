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

    // ---- standing orders (hive-shared; always read from the db) ----
    private bool _ordersTable;

    private void EnsureOrdersTable()
    {
        if (_ordersTable) return;
        _db.Exec("CREATE TABLE IF NOT EXISTS standing_orders(bot TEXT PRIMARY KEY COLLATE NOCASE, data TEXT NOT NULL, updated INTEGER NOT NULL)");
        _ordersTable = true;
    }

    /// <summary>
    /// The active standing order of <paramref name="bot"/> (a login: main or a clone), or null. Read from the shared db every
    /// time, so a clone sees an order main set for it, and the hive supervisor can re-send a respawned clone its job.
    /// </summary>
    public StandingOrder? GetStandingOrder(string bot)
    {
        lock (_lock)
        {
            EnsureOrdersTable();
            return _db.Query("SELECT data FROM standing_orders WHERE bot = $b", r => JsonSerializer.Deserialize<StandingOrder>(r.GetString(0), AtomicFile.Json),
                ("$b", bot.Trim())).FirstOrDefault() is { Active: true } o ? o : null;
        }
    }

    /// <summary>Every stored order (active and ended), newest first: the hive supervisor's view.</summary>
    public IReadOnlyList<StandingOrder> StandingOrders(bool activeOnly = true)
    {
        lock (_lock)
        {
            EnsureOrdersTable();
            return _db.Query("SELECT data FROM standing_orders ORDER BY updated DESC", r => JsonSerializer.Deserialize<StandingOrder>(r.GetString(0), AtomicFile.Json)!)
                .Where(o => o != null && (!activeOnly || o.Active)).ToList();
        }
    }

    /// <summary>Sets (or replaces) a bot's single standing order.</summary>
    public StandingOrder SetStandingOrder(StandingOrder order)
    {
        lock (_lock)
        {
            EnsureOrdersTable();
            var now = Now;
            order.Bot = order.Bot.Trim();
            if (order.Created == default) order.Created = now;
            order.Updated = now;
            SaveOrderRow(order);
            return order;
        }
    }

    /// <summary>Ends a bot's standing order (kept as an inactive row with who ended it). False when it had none.</summary>
    public bool ClearStandingOrder(string bot, string? endedBy = null)
    {
        lock (_lock)
        {
            var o = GetStandingOrder(bot);
            if (o == null) return false;
            o.Active = false;
            o.EndedBy = endedBy ?? "cleared";
            o.Updated = Now;
            SaveOrderRow(o);
            return true;
        }
    }

    /// <summary>Updates resume bookkeeping of the active order (no-op when it changed or ended meanwhile).</summary>
    public void UpdateStandingOrder(string bot, Action<StandingOrder> mutate)
    {
        lock (_lock)
        {
            var o = GetStandingOrder(bot);
            if (o == null) return;
            mutate(o);
            o.Updated = Now;
            SaveOrderRow(o);
        }
    }

    private void SaveOrderRow(StandingOrder o) =>
        _db.Exec("""
            INSERT INTO standing_orders(bot, data, updated) VALUES ($b, $d, $u)
            ON CONFLICT(bot) DO UPDATE SET data = excluded.data, updated = excluded.updated
            """, ("$b", o.Bot), ("$d", JsonSerializer.Serialize(o, AtomicFile.Json)), ("$u", AgentDb.ToUnixMs(o.Updated)));

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

    // ---- admin edits (one row + its doc, one transaction; a rename moves both). The edit runs on a copy that replaces the
    // mirrored entry only once the transaction committed, so a failed or rejected edit leaves memory exactly as it was. ----

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, AtomicFile.Json), AtomicFile.Json)!;

    /// <summary>Edits an existing waypoint (rename allowed). Null when there is none; throws when the new name is taken.</summary>
    public Waypoint? EditWaypoint(string name, Action<Waypoint> edit)
    {
        lock (_lock)
        {
            int i = _data.Waypoints.FindIndex(p => Same(p.Name, name));
            if (i < 0) return null;
            var old = _data.Waypoints[i];
            var w = Copy(old);
            edit(w);
            w.Name = w.Name.Trim();
            if (_data.Waypoints.Any(p => p != old && Same(p.Name, w.Name))) throw new InvalidOperationException($"a place named '{w.Name}' already exists");
            w.Updated = Now;
            _db.Tx(() =>
            {
                if (Ref(w) != Ref(old)) { DeleteRow(Ref(old)); _index.Delete(Ref(old)); }
                SaveWaypoint(w);
                _index.Upsert(Doc(w));
            });
            _data.Waypoints[i] = w;
            return w;
        }
    }

    /// <summary>Edits an existing structure (rename allowed). Null when there is none; throws when the new name is taken.</summary>
    public Structure? EditStructure(string name, Action<Structure> edit)
    {
        lock (_lock)
        {
            int i = _data.Structures.FindIndex(p => Same(p.Name, name));
            if (i < 0) return null;
            var old = _data.Structures[i];
            var s = Copy(old);
            edit(s);
            s.Name = s.Name.Trim();
            if (_data.Structures.Any(p => p != old && Same(p.Name, s.Name))) throw new InvalidOperationException($"a structure named '{s.Name}' already exists");
            s.Updated = Now;
            _db.Tx(() =>
            {
                if (Ref(s) != Ref(old)) { DeleteRow(Ref(old)); _index.Delete(Ref(old)); }
                SaveStructure(s);
                _index.Upsert(Doc(s));
            });
            _data.Structures[i] = s;
            return s;
        }
    }

    /// <summary>Edits a note/fact by id (the id never changes). Null when there is none.</summary>
    public Note? EditNote(int id, Action<Note> edit)
    {
        lock (_lock)
        {
            int i = _data.Notes.FindIndex(x => x.Id == id);
            if (i < 0) return null;
            var n = Copy(_data.Notes[i]);
            edit(n);
            n.Id = id;
            n.Subject = n.Subject.Trim();
            n.Text = n.Text.Trim();
            _db.Tx(() => { SaveNote(n); _index.Upsert(Doc(n)); });
            _data.Notes[i] = n;
            return n;
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
            if (Wants(kind, "waypoint")) n += RemoveMatching(_data.Waypoints, w => Same(w.Name, name), w => Ref(w), refs);
            if (Wants(kind, "structure")) n += RemoveMatching(_data.Structures, s => Same(s.Name, name), s => Ref(s), refs);
            if (Wants(kind, "routine")) n += RemoveMatching(_data.Routines, r => Same(r.Name, name), r => Ref(r), refs);
            if (kind is null or "" or "note" or "fact" or "player") n += RemoveMatching(_data.Notes, NoteMatch(name), x => Ref(x), refs);
            if (n > 0) DeleteRefs(refs);
            return n;
        }
    }

    /// <summary>Any kind (null/""), or this one.</summary>
    private static bool Wants(string? kind, string one) => kind is null or "" || kind == one;

    private static bool WantsNotes(string? kind) => Wants(kind, "note") || kind == "fact";

    private static int RemoveMatching<T>(List<T> list, Func<T, bool> match, Func<T, string> refOf, List<string> refs)
    {
        int n = 0;
        foreach (var x in list.Where(match).ToList()) { list.Remove(x); refs.Add(refOf(x)); n++; }
        return n;
    }

    /// <summary>A note by id ("#3", "3"), else by text or subject.</summary>
    private Func<Note, bool> NoteMatch(string name) => int.TryParse(name.TrimStart('#'), out var id)
        ? x => x.Id == id
        : x => x.Text.Contains(name, StringComparison.OrdinalIgnoreCase) || Same(x.Subject, name);

    private void DeleteRefs(List<string> refs) => _db.Tx(() =>
    {
        foreach (var r in refs)
        {
            DeleteRow(r);
            _index.Delete(r);
        }
    });

    /// <summary>Compact list of names for a kind (bounded).</summary>
    public string List(string? kind, int maxChars = 500)
    {
        lock (_lock)
        {
            kind = kind?.Trim().ToLowerInvariant().TrimEnd('s');
            var parts = new List<string>();
            int cur = CurrentDimension;
            if (Wants(kind, "waypoint") && _data.Waypoints.Count > 0)
                parts.Add("waypoints: " + string.Join(", ", _data.Waypoints.Select(w => $"{w.Name}@{w.X},{w.Y},{w.Z}{WorldDim.Tag(w.Dimension, cur)}")));
            if (Wants(kind, "structure") && _data.Structures.Count > 0)
                parts.Add("structures: " + string.Join(", ", _data.Structures.Select(s => $"{s.Name}({s.Kind})")));
            if (Wants(kind, "routine") && _data.Routines.Count > 0)
                parts.Add("routines: " + string.Join(", ", _data.Routines.Select(r => r.Name)));
            if (WantsNotes(kind) && _data.Notes.Count > 0)
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
        // Corrected cells: shown as NOT to use, so recall never resurrects a stale "mine_here".
        if (s.RetiredKeys.Count > 0)
            sb.Append(" retracted (don't use): " + string.Join(", ", s.RetiredKeys.TakeLast(4).Select(k => $"{k.X},{k.Y},{k.Z}").Distinct()));
        if (s.TaughtBy != null) sb.Append($" (by {s.TaughtBy}");
        sb.Append(s.TaughtBy != null ? $", conf {s.Confidence:0.#})" : $" (conf {s.Confidence:0.#})");
        return sb.ToString();
    }

    public static string FormatRoutine(Routine r) => r.Script is { Migrated: false } s
        ? $"script {r.Name} v{s.Version}" + (r.Note != null ? $": {r.Note}" : "") + (s.Runs > 0 ? $" ({s.Successes}/{s.Runs} ok, avg {s.AvgSeconds:0}s)" : "") +
          (s.PromotedAs != null ? $" skill {s.PromotedAs}" : "")
        : $"routine {r.Name}: {string.Join(">", r.Steps.Select(x => x.Tool))}" + (r.Repeat != 1 ? $" x{(r.Repeat == 0 ? "inf" : r.Repeat)}" : "") +
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
        if (legacy != null) MergeLegacy(legacy);
        _db.SetMeta("memory_json_imported", DateTimeOffset.UtcNow.ToString("O"));
        try { File.Move(path, path + ".imported", overwrite: true); } catch (IOException) { }
    }

    /// <summary>Merges a legacy memory.json by name (notes by subject + text, renumbered on an id clash) and syncs it to the db.</summary>
    private void MergeLegacy(MemoryData legacy)
    {
        foreach (var w in legacy.Waypoints) { _data.Waypoints.RemoveAll(x => Same(x.Name, w.Name)); _data.Waypoints.Add(w); }
        foreach (var s in legacy.Structures) { _data.Structures.RemoveAll(x => Same(x.Name, s.Name)); _data.Structures.Add(s); }
        foreach (var r in legacy.Routines) { _data.Routines.RemoveAll(x => Same(x.Name, r.Name)); _data.Routines.Add(r); }
        foreach (var n in legacy.Notes) MergeLegacyNote(n);
        _data.Identity = legacy.Identity ?? _data.Identity;
        _data.NextNoteId = _data.Notes.Count == 0 ? 1 : _data.Notes.Max(n => n.Id) + 1;
        _db.Tx(SyncAll);
    }

    private void MergeLegacyNote(Note n)
    {
        if (_data.Notes.Any(x => Same(x.Subject, n.Subject) && Same(x.Text, n.Text))) return;
        if (_data.Notes.Any(x => x.Id == n.Id)) n.Id = _data.Notes.Max(x => x.Id) + 1;
        _data.Notes.Add(n);
    }

    public void Dispose()
    {
        if (_ownsIndex) _index.Dispose();
        if (_ownsDb) _db.Dispose();
    }
}
