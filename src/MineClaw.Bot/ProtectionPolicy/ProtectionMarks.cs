using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.World;

namespace MineClaw.Bot;

public enum MarkKind { Permit, Never }

/// <summary>
/// A player's explicit word about blocks: a trusted <b>permit</b> ("break that", "you can break the main house": soft
/// protection yields there until it expires) or a <b>never-break</b> mark ("never break the gen": hard, a command can't
/// override it; a trusted "you can break X" removes it).
/// </summary>
/// <param name="Exact">From a punch or coordinates (not a name/area): only exact permits open containers.</param>
public sealed record ProtectionMark(long Id, MarkKind Kind, int Dimension, IReadOnlyList<(BlockPos Min, BlockPos Max)> Boxes, string Label,
    string By, DateTimeOffset At, DateTimeOffset? Expires, bool Exact)
{
    public bool Contains(BlockPos p) => Boxes.Any(b => p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z);

    public bool Overlaps(IReadOnlyList<(BlockPos Min, BlockPos Max)> other) =>
        Boxes.Any(a => other.Any(b => a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z));

    public int Volume => Boxes.Sum(b => (b.Max.X - b.Min.X + 1) * (b.Max.Y - b.Min.Y + 1) * (b.Max.Z - b.Min.Z + 1));
}

/// <summary>
/// Permits and never-break marks, persisted in the shared db (<c>protection_marks</c>) so every hive bot honours them and they
/// survive restarts. Lock-free snapshot reads (the pathfinder asks); reloaded from the db at most every
/// <see cref="Refresh"/>, at once after a local change.
/// </summary>
public sealed class ProtectionMarks
{
    private readonly AgentDb? _db;
    private readonly TimeProvider _time;
    private volatile ProtectionMark[] _marks = [];
    private long _loadedAt = long.MinValue;
    private readonly object _lock = new();

    public ProtectionMarks(AgentDb? db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        if (_db != null)
            lock (_db.Gate)
                _db.Exec("""
                    CREATE TABLE IF NOT EXISTS protection_marks(id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, dim INTEGER NOT NULL,
                        boxes TEXT NOT NULL, label TEXT NOT NULL, by TEXT NOT NULL, at INTEGER NOT NULL, expires INTEGER, exact INTEGER NOT NULL);
                    """);
        Reload();
    }

    /// <summary>How often the db is re-read for marks made by other hive processes.</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromSeconds(5);

    public bool HasNever { get { MaybeReload(); return _hasNever; } }
    public bool HasPermits { get { MaybeReload(); return _hasPermits; } }
    private volatile bool _hasNever, _hasPermits;

    public IReadOnlyList<ProtectionMark> All { get { MaybeReload(); return Live(); } }

    private ProtectionMark[] Live()
    {
        var now = _time.GetUtcNow();
        return _marks.Where(m => m.Expires is not { } e || e > now).ToArray();
    }

    /// <summary>Generator regen cells (taught / regen-confirmed): an area mark ("never break the gen") doesn't cover them; only a
    /// mark on exactly that cell does.</summary>
    public Func<BlockPos, bool>? IsGenCell { get; set; }

    public ProtectionMark? NeverAt(BlockPos p, int dim)
    {
        MaybeReload();
        bool? gen = null;
        foreach (var m in _marks)
        {
            if (m.Kind != MarkKind.Never || m.Dimension != dim || !m.Contains(p)) continue;
            bool single = m.Boxes.Count == 1 && m.Boxes[0].Min == p && m.Boxes[0].Max == p;
            if (!single && (gen ??= SafeGen(p))) continue;
            return m;
        }
        return null;
    }

    private bool SafeGen(BlockPos p)
    {
        try { return IsGenCell?.Invoke(p) == true; } catch (Exception) { return false; }
    }

    public ProtectionMark? PermitAt(BlockPos p, int dim, bool exactOnly = false)
    {
        MaybeReload();
        var now = _time.GetUtcNow();
        foreach (var m in _marks)
            if (m.Kind == MarkKind.Permit && m.Dimension == dim && (!exactOnly || m.Exact) && (m.Expires is not { } e || e > now) && m.Contains(p)) return m;
        return null;
    }

    public ProtectionMark Add(MarkKind kind, int dim, IReadOnlyList<(BlockPos Min, BlockPos Max)> boxes, string label, string by, TimeSpan? ttl, bool exact)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? expires = ttl is { } t ? now + t : null;
        long id = 0;
        if (_db != null)
            lock (_db.Gate)
            {
                _db.Exec("INSERT INTO protection_marks(kind,dim,boxes,label,by,at,expires,exact) VALUES($k,$d,$b,$l,$by,$at,$e,$x)",
                    ("$k", kind.ToString()), ("$d", dim), ("$b", Boxes(boxes)), ("$l", label), ("$by", by), ("$at", AgentDb.ToUnixMs(now)),
                    ("$e", expires is { } ex ? AgentDb.ToUnixMs(ex) : null), ("$x", exact ? 1 : 0));
                id = _db.ScalarLong("SELECT last_insert_rowid()");
            }
        var mark = new ProtectionMark(id, kind, dim, boxes, label, by, now, expires, exact);
        lock (_lock)
        {
            _marks = [.. _marks, mark];
            Flags();
        }
        return mark;
    }

    /// <summary>Removes every never-break mark overlapping <paramref name="boxes"/> (a trusted "you can break X"). Returns them.</summary>
    public IReadOnlyList<ProtectionMark> Unmark(int dim, IReadOnlyList<(BlockPos Min, BlockPos Max)> boxes)
    {
        MaybeReload();
        var gone = _marks.Where(m => m.Kind == MarkKind.Never && m.Dimension == dim && m.Overlaps(boxes)).ToList();
        if (gone.Count == 0) return gone;
        if (_db != null)
            lock (_db.Gate)
                foreach (var m in gone) _db.Exec("DELETE FROM protection_marks WHERE id = $i", ("$i", m.Id));
        lock (_lock)
        {
            _marks = _marks.Where(m => !gone.Contains(m)).ToArray();
            Flags();
        }
        return gone;
    }

    private void MaybeReload()
    {
        if (_db == null) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _loadedAt) < Refresh.TotalMilliseconds) return;
        Reload();
    }

    /// <summary>Re-reads the db now (expired permits are dropped there too).</summary>
    public void Reload()
    {
        Interlocked.Exchange(ref _loadedAt, Environment.TickCount64);
        if (_db == null) { Flags(); return; }
        try
        {
            List<ProtectionMark> rows;
            lock (_db.Gate)
            {
                _db.Exec("DELETE FROM protection_marks WHERE expires IS NOT NULL AND expires < $n", ("$n", AgentDb.ToUnixMs(_time.GetUtcNow())));
                rows = _db.Query("SELECT id,kind,dim,boxes,label,by,at,expires,exact FROM protection_marks",
                    r => new ProtectionMark(r.GetInt64(0), Enum.TryParse<MarkKind>(r.GetString(1), out var k) ? k : MarkKind.Permit, r.GetInt32(2),
                        ParseBoxes(r.GetString(3)), r.GetString(4), r.GetString(5), AgentDb.FromUnixMs(r.GetInt64(6)),
                        AgentDb.LongOrNull(r, 7) is { } e ? AgentDb.FromUnixMs(e) : null, r.GetInt64(8) != 0));
            }
            lock (_lock)
            {
                _marks = rows.ToArray();
                Flags();
            }
        }
        catch (Exception) { /* keep the last snapshot */ }
    }

    private void Flags()
    {
        var m = _marks;
        _hasNever = m.Any(x => x.Kind == MarkKind.Never);
        _hasPermits = m.Any(x => x.Kind == MarkKind.Permit);
    }

    private static string Boxes(IReadOnlyList<(BlockPos Min, BlockPos Max)> boxes) =>
        JsonSerializer.Serialize(boxes.Select(b => new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }));

    private static List<(BlockPos, BlockPos)> ParseBoxes(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<int[][]>(json) ?? []).Where(a => a.Length == 6)
                .Select(a => (new BlockPos(a[0], a[1], a[2]), new BlockPos(a[3], a[4], a[5]))).ToList();
        }
        catch (JsonException) { return []; }
    }
}
