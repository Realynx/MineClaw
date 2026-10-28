using System.Buffers.Binary;
using System.Text;

namespace MineClaw.Protocol.Release1122;

/// <summary>NBT tag ids (decompiled 1.12.2 <c>NBTBase</c>; 12 = long array, new in 1.12).</summary>
public enum NbtType : byte
{
    End = 0, Byte = 1, Short = 2, Int = 3, Long = 4, Float = 5, Double = 6, ByteArray = 7, String = 8, List = 9, Compound = 10,
    IntArray = 11, LongArray = 12,
}

/// <summary>An NBT list: its element type and the values (same .NET types as <see cref="NbtCompound"/>'s).</summary>
public sealed class NbtList(NbtType elementType, List<object> items)
{
    public NbtType ElementType { get; } = elementType;
    public List<object> Items { get; } = items;
    public int Count => Items.Count;
    public object this[int i] => Items[i];
}

/// <summary>
/// An NBT compound. Values are plain .NET objects: <c>sbyte</c>, <c>short</c>, <c>int</c>, <c>long</c>, <c>float</c>,
/// <c>double</c>, <c>byte[]</c>, <c>string</c>, <see cref="NbtList"/>, <see cref="NbtCompound"/>, <c>int[]</c>, <c>long[]</c>.
/// </summary>
public sealed class NbtCompound : Dictionary<string, object>
{
    public NbtCompound() : base(StringComparer.Ordinal) { }

    /// <summary>The name the root tag carried on the wire ("" for a block entity).</summary>
    public string RootName { get; set; } = "";

    public string? GetString(string key) => TryGetValue(key, out var v) ? v as string : null;

    /// <summary>A numeric value of any integer width (byte/short/int/long), or null.</summary>
    public long? GetLong(string key) => TryGetValue(key, out var v) ? v switch
    {
        sbyte b => b, short s => s, int i => i, long l => l, _ => null,
    } : null;

    public int? GetInt(string key) => GetLong(key) is { } l ? (int)l : null;
    public NbtCompound? GetCompound(string key) => TryGetValue(key, out var v) ? v as NbtCompound : null;
    public NbtList? GetList(string key) => TryGetValue(key, out var v) ? v as NbtList : null;
}

/// <summary>
/// A small NBT reader/writer for the uncompressed network form (decompiled 1.12.2 <c>CompressedStreamTools.read(DataInput)</c>:
/// root = type byte, name (Java modified UTF-8, u16 length), payload; TAG_End as root = no tag). Depth is capped at 512 like
/// <c>NBTTagCompound.read</c>; the payload size is capped by the caller's buffer (the packet).
/// </summary>
public static class Nbt
{
    public const int MaxDepth = 512;

    /// <summary>Reads one root tag. Returns null for a TAG_End root (PacketBuffer.readCompoundTag: "no tag").</summary>
    public static NbtCompound? ReadRoot(ref WireReader r)
    {
        var type = (NbtType)r.ReadUByte();
        if (type == NbtType.End) return null;
        if (type != NbtType.Compound) throw new ProtocolException($"NBT root is {type}, expected a compound");
        string name = ReadModifiedUtf8(ref r);
        var c = (NbtCompound)ReadPayload(ref r, type, 0);
        c.RootName = name;
        return c;
    }

    public static NbtCompound? ReadRoot(ReadOnlySpan<byte> data, out int consumed)
    {
        var r = new WireReader(data);
        var c = ReadRoot(ref r);
        consumed = r.Position;
        return c;
    }

    private static object ReadPayload(ref WireReader r, NbtType type, int depth)
    {
        if (depth > MaxDepth) throw new ProtocolException("NBT nested deeper than 512");
        switch (type)
        {
            case NbtType.Byte: return r.ReadSByte();
            case NbtType.Short: return r.ReadShort();
            case NbtType.Int: return r.ReadInt();
            case NbtType.Long: return r.ReadLong();
            case NbtType.Float: return r.ReadFloat();
            case NbtType.Double: return r.ReadDouble();
            case NbtType.ByteArray:
                return r.ReadBytes(Length(ref r, 1)).ToArray();
            case NbtType.String: return ReadModifiedUtf8(ref r);
            case NbtType.List:
            {
                var et = (NbtType)r.ReadUByte();
                int n = r.ReadInt();
                if (n < 0 || n > r.Remaining) throw new ProtocolException($"NBT list length {n} out of range");
                if (n > 0 && et == NbtType.End) throw new ProtocolException("NBT list of TAG_End with elements");
                var items = new List<object>(n);
                for (int i = 0; i < n; i++) items.Add(ReadPayload(ref r, et, depth + 1));
                return new NbtList(et, items);
            }
            case NbtType.Compound:
            {
                var c = new NbtCompound();
                while (true)
                {
                    var t = (NbtType)r.ReadUByte();
                    if (t == NbtType.End) return c;
                    string key = ReadModifiedUtf8(ref r);
                    c[key] = ReadPayload(ref r, t, depth + 1);
                }
            }
            case NbtType.IntArray:
            {
                var a = new int[Length(ref r, 4)];
                for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt();
                return a;
            }
            case NbtType.LongArray:
            {
                var a = new long[Length(ref r, 8)];
                for (int i = 0; i < a.Length; i++) a[i] = r.ReadLong();
                return a;
            }
            default: throw new ProtocolException($"unknown NBT tag type {(byte)type}");
        }
    }

    private static int Length(ref WireReader r, int elementSize)
    {
        int n = r.ReadInt();
        if (n < 0 || (long)n * elementSize > r.Remaining) throw new ProtocolException($"NBT array length {n} out of range");
        return n;
    }

    /// <summary>DataInput.readUTF: u16 byte length, Java modified UTF-8 (NUL as C0 80, supplementary chars as surrogate pairs).</summary>
    public static string ReadModifiedUtf8(ref WireReader r)
    {
        int len = r.ReadUShort();
        var b = r.ReadBytes(len);
        var sb = new StringBuilder(len);
        for (int i = 0; i < b.Length;)
        {
            int c = b[i];
            if (c < 0x80) { sb.Append((char)c); i++; }
            else if ((c & 0xE0) == 0xC0 && i + 1 < b.Length) { sb.Append((char)((c & 0x1F) << 6 | b[i + 1] & 0x3F)); i += 2; }
            else if ((c & 0xF0) == 0xE0 && i + 2 < b.Length) { sb.Append((char)((c & 0x0F) << 12 | (b[i + 1] & 0x3F) << 6 | b[i + 2] & 0x3F)); i += 3; }
            else throw new ProtocolException("malformed modified UTF-8 in NBT");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ writing (fixtures, the fake server)

    public static void WriteRoot(WireWriter w, NbtCompound? c)
    {
        if (c is null) { w.WriteUByte(0); return; }
        w.WriteUByte((byte)NbtType.Compound);
        WriteModifiedUtf8(w, c.RootName);
        WritePayload(w, c);
    }

    public static byte[] ToBytes(NbtCompound? c)
    {
        var w = new WireWriter();
        WriteRoot(w, c);
        return w.ToArray();
    }

    public static NbtType TypeOf(object v) => v switch
    {
        sbyte => NbtType.Byte, short => NbtType.Short, int => NbtType.Int, long => NbtType.Long, float => NbtType.Float,
        double => NbtType.Double, byte[] => NbtType.ByteArray, string => NbtType.String, NbtList => NbtType.List,
        NbtCompound => NbtType.Compound, int[] => NbtType.IntArray, long[] => NbtType.LongArray,
        _ => throw new ArgumentException($"{v.GetType().Name} is not an NBT value"),
    };

    private static void WritePayload(WireWriter w, object v)
    {
        switch (v)
        {
            case sbyte b: w.WriteSByte(b); break;
            case short s: w.WriteShort(s); break;
            case int i: w.WriteInt(i); break;
            case long l: w.WriteLong(l); break;
            case float f: w.WriteFloat(f); break;
            case double d: w.WriteDouble(d); break;
            case byte[] a: w.WriteInt(a.Length).WriteBytes(a); break;
            case string s: WriteModifiedUtf8(w, s); break;
            case NbtList list:
                w.WriteUByte((byte)list.ElementType).WriteInt(list.Count);
                foreach (var item in list.Items) WritePayload(w, item);
                break;
            case NbtCompound c:
                foreach (var (k, val) in c)
                {
                    w.WriteUByte((byte)TypeOf(val));
                    WriteModifiedUtf8(w, k);
                    WritePayload(w, val);
                }
                w.WriteUByte(0);
                break;
            case int[] ia: w.WriteInt(ia.Length); foreach (var x in ia) w.WriteInt(x); break;
            case long[] la: w.WriteInt(la.Length); foreach (var x in la) w.WriteLong(x); break;
            default: throw new ArgumentException($"{v.GetType().Name} is not an NBT value");
        }
    }

    public static void WriteModifiedUtf8(WireWriter w, string s)
    {
        var bytes = new List<byte>(s.Length);
        foreach (char ch in s)
        {
            if (ch is >= '\u0001' and <= '\u007F') bytes.Add((byte)ch);
            else if (ch <= '߿') { bytes.Add((byte)(0xC0 | ch >> 6)); bytes.Add((byte)(0x80 | ch & 0x3F)); }
            else { bytes.Add((byte)(0xE0 | ch >> 12)); bytes.Add((byte)(0x80 | ch >> 6 & 0x3F)); bytes.Add((byte)(0x80 | ch & 0x3F)); }
        }
        if (bytes.Count > ushort.MaxValue) throw new ArgumentException("NBT string too long");
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)bytes.Count);
        w.WriteBytes(len).WriteBytes(bytes.ToArray());
    }
}
