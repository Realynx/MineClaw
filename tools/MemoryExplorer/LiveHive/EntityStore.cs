namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>One entity as a renderer draws it this frame: the interpolated position plus the wire fields.</summary>
public struct EntityView
{
    public int Id, Dim, Item, Damage, Count, Type;
    public double X, Y, Z;
    /// <summary>Ticks since spawn (advanced locally between updates: drops bob and spin smoothly).</summary>
    public double AgeTicks;
    public double Vx, Vz;
    /// <summary>Entity flags byte (0x01 burning, 0x02 sneaking, 0x08 sprinting) and mob variant (sheep fleece, slime size).</summary>
    public int Flags, Variant;
    public float Yaw;
    public string Kind;
    public string? Name;
}

/// <summary>
/// The viewer's merged entity set, fed by the delta stream (<see cref="Apply"/> on the sync worker) and read by the renderers
/// every frame (<see cref="Interpolated"/>, allocation-free into a caller-owned list). Positions are interpolated: each update
/// starts a glide from wherever the entity is drawn now to the new position over the measured update interval, so 5 Hz
/// updates show as smooth motion with no jumps. Thread-safe (one lock; the copy per frame is a few hundred structs).
/// </summary>
public sealed class EntityStore
{
    private sealed class Track
    {
        public EntityDto E = null!;
        public double FromX, FromY, FromZ, ToX, ToY, ToZ, StartMs, DurMs = 200, AgeAt, AgeMs;
    }

    private readonly object _lock = new();
    private readonly Dictionary<(int Dim, int Id), Track> _tracks = new();
    private readonly List<EntityDto> _anon = []; // full replies from an older server (no ids): drawn as-is
    private double _lastUpdateMs;
    private double _intervalMs = 200;
    private long _version;

    public Func<double> Clock { get; init; } = () => FreecamStats.NowMs;
    public long Version => Interlocked.Read(ref _version);
    public int Count { get { lock (_lock) return _tracks.Count + _anon.Count; } }

    /// <summary>Applies a reply (full or delta) and returns it as a full reply (bots + every entity's latest wire record).</summary>
    public EntitiesDto Apply(EntitiesDto d)
    {
        double now = Clock();
        lock (_lock)
        {
            if (_lastUpdateMs > 0) _intervalMs = Math.Clamp(_intervalMs * 0.7 + (now - _lastUpdateMs) * 0.3, 50, 1000);
            _lastUpdateMs = now;
            if (d.Full) { _anon.Clear(); }
            var seen = d.Full ? new HashSet<(int, int)>() : null;
            foreach (var e in d.Entities) ApplyOne(e, d.Full, seen, now);
            if (seen != null)
                foreach (var k in _tracks.Keys.Where(k => !seen.Contains(k)).ToList()) _tracks.Remove(k);
            RemoveListed(d.Removed);
            Interlocked.Increment(ref _version);
            var all = new List<EntityDto>(_tracks.Count + _anon.Count);
            foreach (var t in _tracks.Values) all.Add(t.E);
            all.AddRange(_anon);
            return d with { Entities = all, Full = true, Removed = null };
        }
    }

    /// <summary>One wire record (under the lock): a new track, a glide of a known one, or (no id, full reply) an anonymous entity.</summary>
    private void ApplyOne(EntityDto e, bool full, HashSet<(int, int)>? seen, double now)
    {
        if (e.Id is not { } id) { if (full) _anon.Add(e); return; }
        var k = (e.Dim, id);
        seen?.Add(k);
        if (_tracks.TryGetValue(k, out var t)) Glide(t, e, now);
        else _tracks[k] = new Track { E = e, FromX = e.X, FromY = e.Y, FromZ = e.Z, ToX = e.X, ToY = e.Y, ToZ = e.Z, StartMs = now, AgeAt = (e.Age ?? 0) * 20, AgeMs = now };
    }

    /// <summary>Starts a glide from where the track is drawn now to the record's position (a teleport-sized jump snaps).</summary>
    private void Glide(Track t, EntityDto e, double now)
    {
        Position(t, now, out var cx, out var cy, out var cz);
        bool snap = Math.Abs(e.X - cx) + Math.Abs(e.Y - cy) + Math.Abs(e.Z - cz) > 8;
        (t.FromX, t.FromY, t.FromZ) = snap ? (e.X, e.Y, e.Z) : (cx, cy, cz);
        (t.ToX, t.ToY, t.ToZ) = (e.X, e.Y, e.Z);
        t.StartMs = now;
        t.DurMs = _intervalMs;
        if (e.Age is { } age && Math.Abs(age * 20 - Ticks(t, now)) > 20) { t.AgeAt = age * 20; t.AgeMs = now; }
        t.E = e;
    }

    private void RemoveListed(List<int[]>? removed)
    {
        if (removed == null) return;
        foreach (var r in removed)
            if (r.Length >= 2) _tracks.Remove((r[0], r[1]));
    }

    private static double Ticks(Track t, double now) => t.AgeAt + (now - t.AgeMs) / 50.0;

    private static void Position(Track t, double now, out double x, out double y, out double z)
    {
        double k = t.DurMs <= 0 ? 1 : Math.Clamp((now - t.StartMs) / t.DurMs, 0, 1);
        x = t.FromX + (t.ToX - t.FromX) * k;
        y = t.FromY + (t.ToY - t.FromY) * k;
        z = t.FromZ + (t.ToZ - t.FromZ) * k;
    }

    /// <summary>Fills <paramref name="into"/> (cleared first) with every entity of <paramref name="dim"/> at its interpolated
    /// position now. No allocation once the list has grown to size.</summary>
    public void Interpolated(int dim, List<EntityView> into)
    {
        into.Clear();
        double now = Clock();
        lock (_lock)
        {
            foreach (var kv in _tracks)
            {
                if (kv.Key.Dim != dim) continue;
                into.Add(ViewOf(kv.Key.Id, dim, kv.Value, now));
            }
            foreach (var e in _anon)
                if (e.Dim == dim)
                    into.Add(new EntityView { Id = -1, Dim = dim, X = e.X, Y = e.Y, Z = e.Z, Kind = e.Kind, Name = e.Name, Count = 1, Type = -1 });
        }
    }

    private static EntityView ViewOf(int id, int dim, Track t, double now)
    {
        Position(t, now, out var x, out var y, out var z);
        var e = t.E;
        return new EntityView
        {
            Id = id, Dim = dim, X = x, Y = y, Z = z, AgeTicks = Ticks(t, now), Kind = e.Kind, Name = e.Name, Item = e.Item ?? 0,
            Damage = e.Damage ?? 0, Count = e.Count ?? 1, Type = e.Type ?? -1, Yaw = e.Yaw ?? 0, Vx = e.Vx ?? 0, Vz = e.Vz ?? 0, Flags = e.Flags ?? 0, Variant = e.Variant ?? 0,
        };
    }

    public void Clear()
    {
        lock (_lock) { _tracks.Clear(); _anon.Clear(); _lastUpdateMs = 0; }
        Interlocked.Increment(ref _version);
    }
}
