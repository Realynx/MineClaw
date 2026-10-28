using System.Buffers.Binary;
using System.Text;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// Reader over one decoded 1.12.2 packet body (netty <c>PacketBuffer</c> semantics: big-endian, VarInt/VarLong, VarInt-prefixed
/// UTF-8 strings, packed block positions). Throws <see cref="ProtocolException"/> when the body is shorter than a field.
/// </summary>
public ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new ProtocolException($"1.12.2 packet ended early (need {n} bytes at {_pos} of {_data.Length})");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte ReadUByte() => Take(1)[0];
    public sbyte ReadSByte() => (sbyte)Take(1)[0];
    public bool ReadBool() => Take(1)[0] != 0;
    public short ReadShort() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));
    public ReadOnlySpan<byte> ReadBytes(int n) => Take(n);
    public ReadOnlySpan<byte> ReadRest() => Take(Remaining);

    public int ReadVarInt()
    {
        int result = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = ReadUByte();
            result |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new ProtocolException("VarInt longer than 5 bytes");
    }

    public long ReadVarLong()
    {
        long result = 0;
        for (int i = 0; i < 10; i++)
        {
            byte b = ReadUByte();
            result |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new ProtocolException("VarLong longer than 10 bytes");
    }

    /// <summary>PacketBuffer.readString(max): VarInt byte length (≤ 4·max), UTF-8, at most <paramref name="max"/> chars.</summary>
    public string ReadString(int max = 32767)
    {
        int len = ReadVarInt();
        if (len < 0 || len > max * 4) throw new ProtocolException($"string of {len} bytes exceeds {max * 4}");
        var s = Encoding.UTF8.GetString(Take(len));
        if (s.Length > max) throw new ProtocolException($"string of {s.Length} chars exceeds {max}");
        return s;
    }

    /// <summary>BlockPos.fromLong: x 26 bits (≪ 38), y 12 bits (≪ 26), z 26 bits, all signed (decompiled 1.12.2 BlockPos).</summary>
    public (int X, int Y, int Z) ReadPosition() => Wire.UnpackPosition(ReadLong());

    public Guid ReadUuid()
    {
        long msb = ReadLong(), lsb = ReadLong();
        return Wire.ToGuid(msb, lsb);
    }
}

/// <summary>Writer for one 1.12.2 packet body (id included by the caller via <see cref="WriteVarInt"/>).</summary>
public sealed class WireWriter
{
    private readonly MemoryStream _ms;
    private readonly byte[] _scratch = new byte[8];

    public WireWriter(int capacity = 64) => _ms = new MemoryStream(capacity);

    /// <summary>A writer whose body starts with the packet id.</summary>
    public static WireWriter ForPacket(int id)
    {
        var w = new WireWriter();
        w.WriteVarInt(id);
        return w;
    }

    public int Length => (int)_ms.Length;
    public byte[] ToArray() => _ms.ToArray();
    public ReadOnlySpan<byte> Span => _ms.GetBuffer().AsSpan(0, (int)_ms.Length);

    public WireWriter WriteUByte(byte v) { _ms.WriteByte(v); return this; }
    public WireWriter WriteSByte(sbyte v) { _ms.WriteByte((byte)v); return this; }
    public WireWriter WriteBool(bool v) { _ms.WriteByte(v ? (byte)1 : (byte)0); return this; }
    public WireWriter WriteShort(short v) { BinaryPrimitives.WriteInt16BigEndian(_scratch, v); _ms.Write(_scratch, 0, 2); return this; }
    public WireWriter WriteUShort(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(_scratch, v); _ms.Write(_scratch, 0, 2); return this; }
    public WireWriter WriteInt(int v) { BinaryPrimitives.WriteInt32BigEndian(_scratch, v); _ms.Write(_scratch, 0, 4); return this; }
    public WireWriter WriteLong(long v) { BinaryPrimitives.WriteInt64BigEndian(_scratch, v); _ms.Write(_scratch, 0, 8); return this; }
    public WireWriter WriteFloat(float v) { BinaryPrimitives.WriteSingleBigEndian(_scratch, v); _ms.Write(_scratch, 0, 4); return this; }
    public WireWriter WriteDouble(double v) { BinaryPrimitives.WriteDoubleBigEndian(_scratch, v); _ms.Write(_scratch, 0, 8); return this; }
    public WireWriter WriteBytes(ReadOnlySpan<byte> b) { _ms.Write(b); return this; }

    public WireWriter WriteVarInt(int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            _ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        _ms.WriteByte((byte)v);
        return this;
    }

    public WireWriter WriteVarLong(long value)
    {
        ulong v = (ulong)value;
        while (v >= 0x80)
        {
            _ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        _ms.WriteByte((byte)v);
        return this;
    }

    /// <summary>VarInt UTF-8 byte length + bytes. Throws when the string is longer than <paramref name="max"/> chars.</summary>
    public WireWriter WriteString(string value, int max = 32767)
    {
        if (value.Length > max) throw new ArgumentException($"string of {value.Length} chars exceeds {max}", nameof(value));
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(bytes.Length);
        _ms.Write(bytes);
        return this;
    }

    public WireWriter WritePosition(int x, int y, int z) => WriteLong(Wire.PackPosition(x, y, z));

    public WireWriter WriteUuid(Guid id)
    {
        var (msb, lsb) = Wire.FromGuid(id);
        return WriteLong(msb).WriteLong(lsb);
    }
}

public static class Wire
{
    /// <summary>BlockPos.toLong (decompiled 1.12.2): <c>(x &amp; 0x3FFFFFF) &lt;&lt; 38 | (y &amp; 0xFFF) &lt;&lt; 26 | z &amp; 0x3FFFFFF</c>.</summary>
    public static long PackPosition(int x, int y, int z) => ((long)x & 0x3FFFFFF) << 38 | ((long)y & 0xFFF) << 26 | ((long)z & 0x3FFFFFF);

    public static (int X, int Y, int Z) UnpackPosition(long v) => ((int)(v >> 38), (int)(v << 26 >> 52), (int)(v << 38 >> 38));

    /// <summary>A Java UUID (most/least significant longs) as a <see cref="Guid"/> whose "D" string equals Java's toString.</summary>
    public static Guid ToGuid(long msb, long lsb)
    {
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(b, msb);
        BinaryPrimitives.WriteInt64BigEndian(b[8..], lsb);
        return new Guid(b, bigEndian: true);
    }

    public static (long Msb, long Lsb) FromGuid(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b, bigEndian: true, out _);
        return (BinaryPrimitives.ReadInt64BigEndian(b), BinaryPrimitives.ReadInt64BigEndian(b[8..]));
    }

    /// <summary>The offline-mode UUID the vanilla server gives a player: name-based v3 of "OfflinePlayer:" + name
    /// (EntityPlayer.getOfflineUUID).</summary>
    public static Guid OfflineUuid(string name)
    {
        var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)(hash[6] & 0x0F | 0x30);
        hash[8] = (byte)(hash[8] & 0x3F | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}
