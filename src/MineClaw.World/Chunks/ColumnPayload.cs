using System.Buffers.Binary;
using System.Text;

namespace MineClaw.World;

/// <summary>
/// One column of a hive world payload: where it is, its content hash (<see cref="ChunkSnapshot.Hash"/>, the delta key), when and
/// by whom it was last seen, whether that was a live column, and its <see cref="ChunkSnapshot.Encode"/> bytes. Empty
/// <see cref="Data"/> = the column is not known (any more): the reader drops it. <see cref="Light"/> = the live column's
/// <see cref="LightSnapshot.Encode"/> bytes (payload version 2, only when the viewer asked for light; remembered columns have
/// none), and then <see cref="Hash"/> is <see cref="LightSnapshot.Combine"/> of the block and light hashes.
/// </summary>
public sealed record ColumnPayloadEntry(int Cx, int Cz, long Hash, long SeenMs, string? By, bool Live, byte[] Data, byte[]? Light = null)
{
    public bool Missing => Data.Length == 0;
}

/// <summary>
/// The supervisor's compact binary chunk payload (<c>GET /api/world/chunks</c>), little-endian:
/// <code>
/// "MCWC" u16 version(=1|2) u16 flags(=0) i32 dim i32 count
/// count × { i32 cx, i32 cz, i64 hash, i64 seen_unix_ms, u8 live, u8 by_len, by_len × utf8, i32 data_len, data_len × byte
///           [version 2: i32 light_len, light_len × byte (<see cref="LightSnapshot"/>; 0 = none)] }
/// </code>
/// Version 2 is written only when an entry carries light (the viewer asked with <c>light=1</c>), so an older viewer never sees it.
/// <c>data</c> is exactly <see cref="ChunkSnapshot.Encode"/> (the same bytes <c>world_chunks.data</c> stores), so a remembered
/// column is shipped without re-encoding. Decoding rejects a wrong magic/version, truncation, trailing bytes and oversize
/// counts with <see cref="InvalidDataException"/>.
/// </summary>
public static class ColumnPayload
{
    public static ReadOnlySpan<byte> Magic => "MCWC"u8;
    public const ushort Version = 1;
    /// <summary>The version with per-column light (<see cref="ColumnPayloadEntry.Light"/>).</summary>
    public const ushort VersionLight = 2;
    /// <summary>Most columns in one payload.</summary>
    public const int MaxColumns = 1024;
    /// <summary>Largest encoded column accepted (a raw column packs to 48 KB; deflate never grows it past this).</summary>
    public const int MaxColumnBytes = 256 * 1024;
    public const string ContentType = "application/octet-stream";

    private const int HeaderSize = 4 + 2 + 2 + 4 + 4;

    public static byte[] Encode(int dim, IReadOnlyList<ColumnPayloadEntry> columns)
    {
        if (columns.Count > MaxColumns) throw new ArgumentException($"at most {MaxColumns} columns per payload");
        bool light = columns.Any(c => c.Light != null);
        int size = HeaderSize;
        var names = new byte[columns.Count][];
        for (int i = 0; i < columns.Count; i++)
        {
            var by = columns[i].By ?? "";
            var b = Encoding.UTF8.GetBytes(by.Length > 64 ? by[..64] : by);
            names[i] = b.Length > 255 ? b[..255] : b;
            if (columns[i].Data.Length > MaxColumnBytes) throw new ArgumentException("column data too large");
            size += 4 + 4 + 8 + 8 + 1 + 1 + names[i].Length + 4 + columns[i].Data.Length;
            if (light)
            {
                if ((columns[i].Light?.Length ?? 0) > MaxColumnBytes) throw new ArgumentException("column light too large");
                size += 4 + (columns[i].Light?.Length ?? 0);
            }
        }
        var buf = new byte[size];
        var s = buf.AsSpan();
        Magic.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], light ? VersionLight : Version);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], dim);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], columns.Count);
        int o = HeaderSize;
        for (int i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            BinaryPrimitives.WriteInt32LittleEndian(s[o..], c.Cx); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(s[o..], c.Cz); o += 4;
            BinaryPrimitives.WriteInt64LittleEndian(s[o..], c.Hash); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(s[o..], c.SeenMs); o += 8;
            s[o++] = c.Live ? (byte)1 : (byte)0;
            s[o++] = (byte)names[i].Length;
            names[i].CopyTo(s[o..]); o += names[i].Length;
            BinaryPrimitives.WriteInt32LittleEndian(s[o..], c.Data.Length); o += 4;
            c.Data.CopyTo(s[o..]); o += c.Data.Length;
            if (light)
            {
                var l = c.Light ?? [];
                BinaryPrimitives.WriteInt32LittleEndian(s[o..], l.Length); o += 4;
                l.CopyTo(s[o..]); o += l.Length;
            }
        }
        return buf;
    }

    public static (int Dim, List<ColumnPayloadEntry> Columns) Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length < HeaderSize || !s[..4].SequenceEqual(Magic)) throw new InvalidDataException("not a column payload (bad magic)");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
        if (version is not (Version or VersionLight)) throw new InvalidDataException($"column payload version {version} unsupported (want {Version} or {VersionLight})");
        int dim = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(s[12..]);
        if (count < 0 || count > MaxColumns) throw new InvalidDataException($"column payload count {count} out of range");
        var list = new List<ColumnPayloadEntry>(count);
        int o = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            Need(s, o, 4 + 4 + 8 + 8 + 1 + 1);
            int cx = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4;
            int cz = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4;
            long hash = BinaryPrimitives.ReadInt64LittleEndian(s[o..]); o += 8;
            long seen = BinaryPrimitives.ReadInt64LittleEndian(s[o..]); o += 8;
            byte live = s[o++];
            if (live > 1) throw new InvalidDataException("column payload: bad live flag");
            int nameLen = s[o++];
            Need(s, o, nameLen + 4);
            string? by = nameLen == 0 ? null : Encoding.UTF8.GetString(s.Slice(o, nameLen)); o += nameLen;
            int len = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4;
            if (len < 0 || len > MaxColumnBytes) throw new InvalidDataException($"column payload: column size {len} out of range");
            Need(s, o, len);
            var data = s.Slice(o, len).ToArray(); o += len;
            byte[]? light = null;
            if (version == VersionLight)
            {
                Need(s, o, 4);
                int ll = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4;
                if (ll < 0 || ll > MaxColumnBytes) throw new InvalidDataException($"column payload: light size {ll} out of range");
                Need(s, o, ll);
                if (ll > 0) light = s.Slice(o, ll).ToArray();
                o += ll;
            }
            list.Add(new ColumnPayloadEntry(cx, cz, hash, seen, by, live == 1, data, light));
        }
        if (o != s.Length) throw new InvalidDataException("column payload: trailing bytes");
        return (dim, list);
    }

    private static void Need(ReadOnlySpan<byte> s, int at, int n)
    {
        if (n < 0 || at + n > s.Length) throw new InvalidDataException("column payload truncated");
    }
}
