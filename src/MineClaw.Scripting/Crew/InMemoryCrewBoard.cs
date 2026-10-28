using System.Globalization;
using System.Text.Json;

namespace MineClaw.Scripting.Crew;

/// <summary>The crew board for one process (in-process bodies, tests). Thread-safe; values capped at <see cref="MaxBytes"/> per crew.</summary>
public sealed class InMemoryCrewBoard(TimeProvider? time = null) : ICrewBoard
{
    public const int MaxBytes = 4096;

    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, Dictionary<string, string>> _kv = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Crew, string To), Queue<string>> _inbox = new();
    private readonly Dictionary<(string Crew, string Key), (string Owner, DateTimeOffset Until)> _locks = new();

    public string? Get(string crew, string key)
    {
        lock (_lock) return _kv.TryGetValue(crew, out var d) && d.TryGetValue(key, out var v) ? v : null;
    }

    public bool Set(string crew, string key, string? json)
    {
        lock (_lock)
        {
            if (!_kv.TryGetValue(crew, out var d)) _kv[crew] = d = new(StringComparer.Ordinal);
            if (json == null) { d.Remove(key); return true; }
            int size = d.Where(kv => kv.Key != key).Sum(kv => kv.Key.Length + kv.Value.Length) + key.Length + json.Length;
            if (size > MaxBytes) return false;
            d[key] = json;
            return true;
        }
    }

    public double Inc(string crew, string key, double by)
    {
        lock (_lock)
        {
            double cur = 0;
            if (Get(crew, key) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) cur = d;
            cur += by;
            Set(crew, key, JsonSerializer.Serialize(cur));
            return cur;
        }
    }

    public void Send(string crew, string to, string json)
    {
        lock (_lock)
        {
            var k = (crew, to.ToLowerInvariant());
            if (!_inbox.TryGetValue(k, out var q)) _inbox[k] = q = new Queue<string>();
            if (q.Count < 64) q.Enqueue(json);
        }
    }

    public string? TryReceive(string crew, string me)
    {
        lock (_lock) return _inbox.TryGetValue((crew, me.ToLowerInvariant()), out var q) && q.TryDequeue(out var m) ? m : null;
    }

    public bool TryLock(string crew, string key, string owner, TimeSpan ttl)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_locks.TryGetValue((crew, key), out var l) && l.Until > now && !string.Equals(l.Owner, owner, StringComparison.OrdinalIgnoreCase)) return false;
            _locks[(crew, key)] = (owner, now + ttl);
            return true;
        }
    }

    public void Unlock(string crew, string key, string owner)
    {
        lock (_lock)
            if (_locks.TryGetValue((crew, key), out var l) && string.Equals(l.Owner, owner, StringComparison.OrdinalIgnoreCase)) _locks.Remove((crew, key));
    }
}
