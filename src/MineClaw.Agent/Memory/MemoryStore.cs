using System.Text;
using MineClaw.Agent.Runtime;

namespace MineClaw.Agent.Memory;

/// <summary>
/// Long-term memory on disk (one JSON file, atomic writes, saved on every change). Waypoints, structures,
/// notes/facts and routines. Everything the model gets from here is bounded and compact.
/// </summary>
public sealed class MemoryStore
{
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private MemoryData _data;

    /// <param name="path">JSON file; null = in-memory only.</param>
    public MemoryStore(string? path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _data = (path != null ? AtomicFile.ReadJson<MemoryData>(path) : null) ?? new MemoryData();
    }

    public string? Path => _path;

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Read under the lock (don't keep references to mutable objects after returning).</summary>
    public T Read<T>(Func<MemoryData, T> read) { lock (_lock) return read(_data); }

    /// <summary>Mutate under the lock and persist.</summary>
    public void Update(Action<MemoryData> mutate)
    {
        lock (_lock)
        {
            mutate(_data);
            Save();
        }
    }

    private void Save()
    {
        if (_path != null) AtomicFile.WriteJson(_path, _data);
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ---- waypoints ----
    public Waypoint SetWaypoint(string name, int x, int y, int z, string? note = null)
    {
        lock (_lock)
        {
            var w = _data.Waypoints.FirstOrDefault(p => Same(p.Name, name));
            if (w == null) _data.Waypoints.Add(w = new Waypoint { Name = name.Trim() });
            w.X = x; w.Y = y; w.Z = z;
            if (note != null) w.Note = note;
            w.Updated = Now;
            Save();
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
            if (s == null) _data.Structures.Add(s = new Structure { Name = name.Trim() });
            update(s);
            s.Updated = Now;
            Save();
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
            var n = new Note { Id = _data.NextNoteId++, Subject = subject.Trim(), Text = text.Trim(), Created = Now };
            _data.Notes.Add(n);
            Save();
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
            Save();
            return routine;
        }
    }

    public Routine? GetRoutine(string name) { lock (_lock) return _data.Routines.FirstOrDefault(r => Same(r.Name, name)); }

    // ---- generic ----

    /// <summary>Removes an entry by kind (waypoint|structure|note|routine, or null = any) and name (note: id or text match).</summary>
    public int Forget(string? kind, string name)
    {
        lock (_lock)
        {
            int n = 0;
            kind = kind?.Trim().ToLowerInvariant().TrimEnd('s');
            if (kind is null or "" or "waypoint") n += _data.Waypoints.RemoveAll(w => Same(w.Name, name));
            if (kind is null or "" or "structure") n += _data.Structures.RemoveAll(w => Same(w.Name, name));
            if (kind is null or "" or "routine") n += _data.Routines.RemoveAll(w => Same(w.Name, name));
            if (kind is null or "" or "note" or "fact")
            {
                if (int.TryParse(name.TrimStart('#'), out var id)) n += _data.Notes.RemoveAll(x => x.Id == id);
                else n += _data.Notes.RemoveAll(x => x.Text.Contains(name, StringComparison.OrdinalIgnoreCase) || Same(x.Subject, name));
            }
            if (n > 0) Save();
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
            if (kind is null or "" or "waypoint" && _data.Waypoints.Count > 0)
                parts.Add("waypoints: " + string.Join(", ", _data.Waypoints.Select(w => $"{w.Name}@{w.X},{w.Y},{w.Z}")));
            if (kind is null or "" or "structure" && _data.Structures.Count > 0)
                parts.Add("structures: " + string.Join(", ", _data.Structures.Select(s => $"{s.Name}({s.Kind})")));
            if (kind is null or "" or "routine" && _data.Routines.Count > 0)
                parts.Add("routines: " + string.Join(", ", _data.Routines.Select(r => r.Name)));
            if (kind is null or "" or "note" or "fact" && _data.Notes.Count > 0)
                parts.Add($"notes: {_data.Notes.Count} (subjects: " + string.Join(", ", _data.Notes.Select(n => n.Subject).Distinct(StringComparer.OrdinalIgnoreCase).Take(12)) + ")");
            var s = parts.Count == 0 ? "nothing saved" : string.Join(" | ", parts);
            return Tools.TextUtil.Truncate(s, maxChars);
        }
    }

    /// <summary>
    /// Keyword search over everything; each hit is one compact line. Bounded by <paramref name="maxHits"/> and
    /// <paramref name="maxChars"/>.
    /// </summary>
    public string Recall(string query, int maxHits = 6, int maxChars = 700)
    {
        var terms = query.ToLowerInvariant().Split([' ', ',', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1).Distinct().ToArray();
        lock (_lock)
        {
            var hits = new List<(int Score, string Line)>();
            void Consider(string haystack, Func<string> line)
            {
                var h = haystack.ToLowerInvariant();
                int score = terms.Length == 0 ? 1 : terms.Count(t => h.Contains(t));
                if (score > 0) hits.Add((score, line()));
            }

            foreach (var w in _data.Waypoints)
                Consider($"waypoint {w.Name} {w.Note}", () => $"wp {w.Name} {w.X},{w.Y},{w.Z}" + (w.Note != null ? $" ({w.Note})" : ""));
            foreach (var s in _data.Structures)
                Consider($"structure {s.Name} {s.Kind} {s.Note} {string.Join(' ', s.Steps)} {s.TaughtBy}", () => FormatStructure(s));
            foreach (var n in _data.Notes)
                Consider($"note {n.Subject} {n.Text}", () => $"note#{n.Id} {n.Subject}: {n.Text}");
            foreach (var r in _data.Routines)
                Consider($"routine {r.Name} {r.Note} {string.Join(' ', r.Steps.Select(x => x.Tool))}",
                    () => $"routine {r.Name}: {string.Join(">", r.Steps.Select(x => x.Tool))}" + (r.Repeat != 1 ? $" x{(r.Repeat == 0 ? "∞" : r.Repeat)}" : "") +
                          (r.StopWhen != null ? $" until '{r.StopWhen}'" : ""));

            if (hits.Count == 0) return "no memories match";
            var sb = new StringBuilder();
            foreach (var (_, line) in hits.OrderByDescending(h => h.Score).Take(maxHits))
            {
                if (sb.Length + line.Length + 1 > maxChars) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            if (sb.Length == 0) sb.Append(Tools.TextUtil.Truncate(hits.OrderByDescending(h => h.Score).First().Line, maxChars));
            return sb.ToString();
        }
    }

    public static string FormatStructure(Structure s)
    {
        var sb = new StringBuilder($"struct {s.Name} [{s.Kind}]");
        if (s.Min != null) sb.Append($" {s.Min}" + (s.Max != null && s.Max != s.Min ? $"..{s.Max}" : ""));
        if (s.KeyBlocks.Count > 0) sb.Append(" keys: " + string.Join("; ", s.KeyBlocks.Select(k => $"{k.Label}@{k.X},{k.Y},{k.Z}")));
        if (s.Steps.Count > 0) sb.Append(" how: " + string.Join("; ", s.Steps.Select((x, i) => $"{i + 1}) {x}")));
        if (s.TaughtBy != null) sb.Append($" (by {s.TaughtBy}");
        sb.Append(s.TaughtBy != null ? $", conf {s.Confidence:0.#})" : $" (conf {s.Confidence:0.#})");
        return sb.ToString();
    }
}
