using System.Buffers.Binary;
using MineClaw.Data;

namespace MineClaw.Protocol;

/// <summary>
/// Big-endian reader for the b1.8.1 wire format (Java DataInputStream semantics). Blocking: each call reads
/// exactly the bytes it needs from the underlying stream and throws <see cref="EndOfStreamException"/> if the
/// stream ends first. Not thread-safe; use one reader per reading thread.
/// </summary>
public sealed class MinecraftReader
{
    /// <summary>Upper bound for any length-prefixed byte array (protects against garbage lengths after a desync).</summary>
    public const int MaxArrayLength = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _scratch = new byte[8];
    private readonly byte[] _history = new byte[16];
    private int _historyCount;
    private int _historyNext;

    public MinecraftReader(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    public MinecraftReader(byte[] data) : this(new MemoryStream(data, writable: false)) { }

    public Stream BaseStream => _stream;

    /// <summary>Number of packets read through <see cref="PacketRegistry"/> on this reader.</summary>
    public long PacketsRead { get; private set; }

    /// <summary>Records a packet id in the recent-history ring (called by <see cref="PacketRegistry"/>).</summary>
    internal void NotePacketId(byte id)
    {
        _history[_historyNext] = id;
        _historyNext = (_historyNext + 1) % _history.Length;
        if (_historyCount < _history.Length) _historyCount++;
        PacketsRead++;
    }

    /// <summary>The most recently read packet ids, oldest first (up to 16), including the current one.</summary>
    public IReadOnlyList<byte> RecentPacketIds
    {
        get
        {
            var list = new byte[_historyCount];
            int start = (_historyNext - _historyCount + _history.Length) % _history.Length;
            for (int i = 0; i < _historyCount; i++) list[i] = _history[(start + i) % _history.Length];
            return list;
        }
    }

    public string RecentPacketIdsText => string.Join(", ", RecentPacketIds.Select(b => $"0x{b:X2}"));

    /// <summary>Reads one byte, or returns -1 at end of stream (used for the packet id at a packet boundary).</summary>
    public int ReadByteOrEof() => _stream.ReadByte();

    private ReadOnlySpan<byte> Fill(int n)
    {
        _stream.ReadExactly(_scratch, 0, n);
        return _scratch.AsSpan(0, n);
    }

    /// <summary>Unsigned byte (Java <c>read()</c>).</summary>
    public byte ReadUByte() => Fill(1)[0];

    /// <summary>Signed byte (Java <c>readByte()</c>).</summary>
    public sbyte ReadSByte() => (sbyte)Fill(1)[0];

    /// <summary>Boolean: any non-zero byte is true (Java <c>readBoolean()</c>).</summary>
    public bool ReadBool() => Fill(1)[0] != 0;

    public short ReadShort() => BinaryPrimitives.ReadInt16BigEndian(Fill(2));
    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16BigEndian(Fill(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32BigEndian(Fill(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64BigEndian(Fill(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleBigEndian(Fill(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Fill(8));

    /// <summary>Reads exactly <paramref name="count"/> raw bytes.</summary>
    public byte[] ReadBytes(int count)
    {
        if (count < 0 || count > MaxArrayLength)
            throw new ProtocolException($"Byte array length {count} out of range (0..{MaxArrayLength})");
        var data = new byte[count];
        _stream.ReadExactly(data, 0, count);
        return data;
    }

    /// <summary>
    /// string16: signed short count of UTF-16 code units, then that many big-endian UTF-16 code units
    /// (Java <c>writeChars</c>). Throws if the count is negative or exceeds <paramref name="maxLength"/>.
    /// </summary>
    public string ReadString16(int maxLength = short.MaxValue)
    {
        short len = ReadShort();
        if (len < 0) throw new ProtocolException($"string16 length {len} is negative");
        if (len > maxLength) throw new ProtocolException($"string16 length {len} exceeds maximum {maxLength}");
        if (len == 0) return string.Empty;
        var raw = new byte[len * 2];
        _stream.ReadExactly(raw, 0, raw.Length);
        return string.Create(len, raw, static (span, bytes) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = (char)((bytes[2 * i] << 8) | bytes[2 * i + 1]);
        });
    }

    /// <summary>
    /// Slot data as used by 0x0F, 0x66, 0x67, 0x68: short id; if id ≥ 0 then byte count + short damage.
    /// b1.8.1 has no NBT. id &lt; 0 yields <see cref="ItemStack.Empty"/>.
    /// </summary>
    public ItemStack ReadSlot()
    {
        short id = ReadShort();
        if (id < 0) return ItemStack.Empty;
        byte count = ReadUByte();
        short damage = ReadShort();
        return new ItemStack(id, count, damage);
    }

    /// <summary>Entity metadata list up to and including the 0x7F terminator.</summary>
    public EntityMetadata ReadMetadata()
    {
        var md = new EntityMetadata();
        for (int guard = 0; ; guard++)
        {
            if (guard > 1024) throw new ProtocolException("Entity metadata has more than 1024 entries (desync?)");
            byte header = ReadUByte();
            if (header == EntityMetadata.Terminator) return md;
            var type = (MetadataType)((header & 0xE0) >> 5);
            byte index = (byte)(header & 0x1F);
            md.Add(new MetadataEntry(index, type, ReadMetadataValue(type, header, index)));
        }
    }

    private object ReadMetadataValue(MetadataType type, byte header, byte index) => type switch
    {
        MetadataType.Byte => ReadSByte(),
        MetadataType.Short => ReadShort(),
        MetadataType.Int => ReadInt(),
        MetadataType.Float => ReadFloat(),
        MetadataType.String => ReadString16(),
        MetadataType.Item => new ItemStack(ReadShort(), ReadUByte(), ReadShort()),
        MetadataType.Vector => new MetadataVector(ReadInt(), ReadInt(), ReadInt()),
        _ => throw new ProtocolException($"Unknown entity metadata type {(int)type} (header 0x{header:X2}, index {index})"),
    };
}
