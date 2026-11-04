using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>One column of an MCWC payload (api.md §7).</summary>
public sealed record McwcColumn(int Cx, int Cz, long Hash, long SeenMs, bool Live, string By, byte[] Data, byte[]? Light);

/// <summary>The MCWC column payload (little-endian): "MCWC" u16 version(1|2) u16 flags i32 dim i32 count, then the columns.</summary>
public static class Mcwc
{
    public const int MaxColumns = 1024;
    public const int MaxBlob = 256 * 1024;

    public static (int Version, int Dim, List<McwcColumn> Columns) Decode(ReadOnlySpan<byte> b)
    {
        int p = 0;
        ReadOnlySpan<byte> Take(ref int at, int n, ReadOnlySpan<byte> src)
        {
            if (n < 0 || at + n > src.Length) throw new FormatException("MCWC truncated");
            var s = src.Slice(at, n);
            at += n;
            return s;
        }
        if (!Take(ref p, 4, b).SequenceEqual("MCWC"u8)) throw new FormatException("not MCWC");
        int version = BinaryPrimitives.ReadUInt16LittleEndian(Take(ref p, 2, b));
        if (version is not (1 or 2)) throw new FormatException("MCWC version");
        _ = BinaryPrimitives.ReadUInt16LittleEndian(Take(ref p, 2, b));
        int dim = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
        int count = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
        if (count is < 0 or > MaxColumns) throw new FormatException("MCWC count");
        var cols = new List<McwcColumn>(count);
        for (int i = 0; i < count; i++)
        {
            int cx = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
            int cz = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
            long hash = BinaryPrimitives.ReadInt64LittleEndian(Take(ref p, 8, b));
            long seen = BinaryPrimitives.ReadInt64LittleEndian(Take(ref p, 8, b));
            bool live = Take(ref p, 1, b)[0] != 0;
            int byLen = Take(ref p, 1, b)[0];
            string by = Encoding.UTF8.GetString(Take(ref p, byLen, b));
            int dataLen = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
            if (dataLen > MaxBlob) throw new FormatException("MCWC blob too large");
            var data = Take(ref p, dataLen, b).ToArray();
            byte[]? light = null;
            if (version == 2)
            {
                int lightLen = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
                if (lightLen > MaxBlob) throw new FormatException("MCWC blob too large");
                light = Take(ref p, lightLen, b).ToArray();
            }
            cols.Add(new McwcColumn(cx, cz, hash, seen, live, by, data, light));
        }
        if (p != b.Length) throw new FormatException("MCWC trailing bytes");
        return (version, dim, cols);
    }

    public static byte[] Encode(int version, int dim, IReadOnlyList<McwcColumn> cols)
    {
        using var ms = new MemoryStream();
        var tmp = new byte[8];
        void I16(int v) { BinaryPrimitives.WriteUInt16LittleEndian(tmp, (ushort)v); ms.Write(tmp, 0, 2); }
        void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(tmp, v); ms.Write(tmp, 0, 4); }
        void I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(tmp, v); ms.Write(tmp, 0, 8); }
        ms.Write("MCWC"u8);
        I16(version); I16(0); I32(dim); I32(cols.Count);
        foreach (var c in cols)
        {
            I32(c.Cx); I32(c.Cz); I64(c.Hash); I64(c.SeenMs);
            ms.WriteByte(c.Live ? (byte)1 : (byte)0);
            var by = Encoding.UTF8.GetBytes(c.By);
            if (by.Length > 255) by = by[..255];
            ms.WriteByte((byte)by.Length);
            ms.Write(by);
            I32(c.Data.Length); ms.Write(c.Data);
            if (version == 2) { var l = c.Light ?? []; I32(l.Length); ms.Write(l); }
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Hash-keyed cache for <c>/v1/sup/world/chunks</c>: every <c>/world/index</c> reply passing through records each column's current
/// hash (and seen/by/live); a chunks request is answered from cached columns whose stored hash still equals the latest indexed
/// hash, and only the rest is fetched from the supervisor (one smaller request). So several browsers asking for the same
/// unchanged column cost the bot one read. Keys carry the light flag (with light=1 the hashes combine block and light).
/// Bounded (LRU by bytes); anything unparsable falls back to the plain pass-through.
/// </summary>
public sealed class ChunkCache(long maxBytes = 96L * 1024 * 1024)
{
    private readonly record struct Key(bool Light, int Dim, int Cx, int Cz);
    private sealed record Indexed(long Hash, long Seen, string By, bool Live, long At);
    /// <summary>A cached column is served only while an index (or chunks) reply confirmed its hash this recently.</summary>
    public static readonly TimeSpan Confirm = TimeSpan.FromSeconds(120);
    private static long Now => Environment.TickCount64;

    private readonly object _lock = new();
    private readonly Dictionary<Key, Indexed> _index = new();
    private readonly Dictionary<Key, LinkedListNode<(Key Key, McwcColumn Col)>> _cols = new();
    private readonly LinkedList<(Key Key, McwcColumn Col)> _lru = new();
    private long _bytes;

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Bytes { get { lock (_lock) return _bytes; } }

    public static bool LightOf(string query) => ParseQuery(query).TryGetValue("light", out var l) && l is "1" or "true";

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString(eq >= 0 ? part[..eq] : part);
            var v = eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')) : "";
            d[k] = v;
        }
        return d;
    }

    /// <summary>Records the hashes of a <c>/world/index</c> reply (the bytes pass on unchanged).</summary>
    public void ObserveIndex(string query, byte[] body)
    {
        bool light = LightOf(query);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("dim", out var dimEl) || !root.TryGetProperty("columns", out var cols)) return;
            int dim = dimEl.GetInt32();
            lock (_lock)
                foreach (var c in cols.EnumerateArray())
                    _index[new Key(light, dim, c.GetProperty("cx").GetInt32(), c.GetProperty("cz").GetInt32())] = IndexedOf(c);
            lock (_lock) if (_index.Count > 200_000) _index.Clear();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
    }

    /// <summary>One index column's hash, when and by whom it was seen, and whether it is live.</summary>
    private Indexed IndexedOf(JsonElement c)
    {
        long hash = c.GetProperty("hash").GetInt64();
        long seen = c.TryGetProperty("seen", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
        string by = c.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : "";
        bool live = c.TryGetProperty("live", out var lv) && lv.ValueKind == JsonValueKind.True;
        return new Indexed(hash, seen, by, live, Now);
    }

    /// <summary>Requested columns split into cache hits (with index metadata applied) and the ones to fetch.</summary>
    public (Dictionary<(int, int), McwcColumn> Hits, List<(int Cx, int Cz)> Missing) Lookup(bool light, int dim, IReadOnlyList<(int Cx, int Cz)> wanted)
    {
        var hits = new Dictionary<(int, int), McwcColumn>();
        var missing = new List<(int, int)>();
        lock (_lock)
            foreach (var (cx, cz) in wanted)
            {
                var key = new Key(light, dim, cx, cz);
                if (_index.TryGetValue(key, out var idx) && _cols.TryGetValue(key, out var node) && node.Value.Col.Hash == idx.Hash && node.Value.Col.Data.Length > 0
                    && Now - idx.At <= (long)Confirm.TotalMilliseconds)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    hits[(cx, cz)] = node.Value.Col with { SeenMs = idx.Seen, By = idx.By, Live = idx.Live };
                    Hits++;
                }
                else { missing.Add((cx, cz)); Misses++; }
            }
        return (hits, missing);
    }

    public void Store(bool light, int dim, IEnumerable<McwcColumn> cols)
    {
        lock (_lock)
        {
            foreach (var c in cols)
            {
                if (c.Data.Length == 0) continue;
                var key = new Key(light, dim, c.Cx, c.Cz);
                if (_cols.Remove(key, out var old)) { _lru.Remove(old); _bytes -= Size(old.Value.Col); }
                var node = _lru.AddFirst((key, c));
                _cols[key] = node;
                _bytes += Size(c);
                // The chunks reply is the newest word on this column.
                _index[key] = new Indexed(c.Hash, c.SeenMs, c.By, c.Live, Now);
            }
            while (_bytes > maxBytes && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _cols.Remove(last.Value.Key);
                _bytes -= Size(last.Value.Col);
            }
        }
    }

    private static long Size(McwcColumn c) => c.Data.Length + (c.Light?.Length ?? 0) + 64;

    /// <summary>"cx,cz;cx,cz" → pairs (null = malformed).</summary>
    public static List<(int Cx, int Cz)>? ParseCols(string? cols)
    {
        if (string.IsNullOrEmpty(cols)) return null;
        var list = new List<(int, int)>();
        foreach (var pair in cols.Split(';'))
        {
            var xy = pair.Split(',');
            if (xy.Length != 2 || !int.TryParse(xy[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(xy[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var z)) return null;
            list.Add((x, z));
        }
        return list.Count is > 0 and <= 64 ? list : null;
    }
}
