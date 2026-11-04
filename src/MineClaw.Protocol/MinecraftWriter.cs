using System.Buffers.Binary;
using MineClaw.Data;

namespace MineClaw.Protocol;

/// <summary>Big-endian writer for the b1.8.1 wire format (Java DataOutputStream semantics). Not thread-safe.</summary>
public sealed class MinecraftWriter
{
    private readonly Stream _stream;
    private readonly byte[] _scratch = new byte[8];

    public MinecraftWriter(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public Stream BaseStream => _stream;

    public void WriteUByte(byte v) => _stream.WriteByte(v);
    public void WriteSByte(sbyte v) => _stream.WriteByte((byte)v);
    public void WriteBool(bool v) => _stream.WriteByte(v ? (byte)1 : (byte)0);

    public void WriteShort(short v) { BinaryPrimitives.WriteInt16BigEndian(_scratch, v); _stream.Write(_scratch, 0, 2); }
    public void WriteUShort(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(_scratch, v); _stream.Write(_scratch, 0, 2); }
    public void WriteInt(int v) { BinaryPrimitives.WriteInt32BigEndian(_scratch, v); _stream.Write(_scratch, 0, 4); }
    public void WriteLong(long v) { BinaryPrimitives.WriteInt64BigEndian(_scratch, v); _stream.Write(_scratch, 0, 8); }
    public void WriteFloat(float v) { BinaryPrimitives.WriteSingleBigEndian(_scratch, v); _stream.Write(_scratch, 0, 4); }
    public void WriteDouble(double v) { BinaryPrimitives.WriteDoubleBigEndian(_scratch, v); _stream.Write(_scratch, 0, 8); }

    public void WriteBytes(ReadOnlySpan<byte> data) => _stream.Write(data);

    /// <summary>string16: short UTF-16 code-unit count, then big-endian UTF-16 code units.</summary>
    public void WriteString16(string value, int maxLength = short.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > maxLength)
            throw new ArgumentException($"string16 of length {value.Length} exceeds maximum {maxLength}", nameof(value));
        WriteShort((short)value.Length);
        var raw = new byte[value.Length * 2];
        for (int i = 0; i < value.Length; i++)
        {
            raw[2 * i] = (byte)(value[i] >> 8);
            raw[2 * i + 1] = (byte)value[i];
        }
        _stream.Write(raw, 0, raw.Length);
    }

    /// <summary>Slot data: short id (-1 if <see cref="ItemStack.Id"/> &lt; 0), otherwise id, byte count, short damage.</summary>
    public void WriteSlot(ItemStack stack)
    {
        if (stack.Id < 0)
        {
            WriteShort(-1);
            return;
        }
        WriteShort(stack.Id);
        WriteUByte(stack.Count);
        WriteShort(stack.Damage);
    }

    /// <summary>Entity metadata entries followed by the 0x7F terminator.</summary>
    public void WriteMetadata(EntityMetadata? metadata)
    {
        if (metadata != null)
        {
            foreach (var e in metadata)
            {
                if (e.Index > 31) throw new ArgumentException($"metadata index {e.Index} > 31");
                WriteUByte((byte)(((int)e.Type << 5) | (e.Index & 0x1F)));
                WriteMetadataValue(e);
            }
        }
        WriteUByte(EntityMetadata.Terminator);
    }

    private void WriteMetadataValue(MetadataEntry e)
    {
        switch (e.Type)
        {
            case MetadataType.Byte: WriteSByte(Convert.ToSByte(e.Value)); break;
            case MetadataType.Short: WriteShort(Convert.ToInt16(e.Value)); break;
            case MetadataType.Int: WriteInt(Convert.ToInt32(e.Value)); break;
            case MetadataType.Float: WriteFloat(Convert.ToSingle(e.Value)); break;
            case MetadataType.String: WriteString16((string)e.Value); break;
            case MetadataType.Item:
                var s = (ItemStack)e.Value;
                WriteShort(s.Id); WriteUByte(s.Count); WriteShort(s.Damage);
                break;
            case MetadataType.Vector:
                var v = (MetadataVector)e.Value;
                WriteInt(v.X); WriteInt(v.Y); WriteInt(v.Z);
                break;
            default: throw new ArgumentException($"Unknown metadata type {e.Type}");
        }
    }
}
