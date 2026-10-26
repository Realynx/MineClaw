using System.Collections;
using MineClaw.Data;

namespace MineClaw.Protocol;

/// <summary>
/// Entity metadata value types in b1.8.1 (DataWatcher). The header byte of each entry is
/// <c>(type &lt;&lt; 5) | (index &amp; 0x1F)</c>; the list ends with the byte 0x7F.
/// </summary>
public enum MetadataType : byte
{
    /// <summary>Java byte (signed), 1 byte.</summary>
    Byte = 0,
    /// <summary>short, 2 bytes.</summary>
    Short = 1,
    /// <summary>int, 4 bytes.</summary>
    Int = 2,
    /// <summary>float, 4 bytes.</summary>
    Float = 3,
    /// <summary>string16 (vanilla max 64 chars).</summary>
    String = 4,
    /// <summary>Item: short id, byte count, short damage — always all three, unlike slot data (no -1 shortcut).</summary>
    Item = 5,
    /// <summary>Three ints (ChunkCoordinates x, y, z).</summary>
    Vector = 6,
}

/// <summary>Three-int vector metadata value (type 6).</summary>
public readonly record struct MetadataVector(int X, int Y, int Z);

/// <summary>
/// One metadata entry. <see cref="Value"/> is boxed: sbyte, short, int, float, string, <see cref="ItemStack"/> or
/// <see cref="MetadataVector"/> according to <see cref="Type"/>.
/// Well-known indices: 0 = entity flags byte (0x01 on fire, 0x02 crouched, 0x04 riding, 0x08 sprinting, 0x10 eating),
/// 1 = air short. Mob-specific indices start at 16.
/// </summary>
public readonly record struct MetadataEntry(byte Index, MetadataType Type, object Value)
{
    public static MetadataEntry FromByte(byte index, sbyte v) => new(index, MetadataType.Byte, v);
    public static MetadataEntry FromShort(byte index, short v) => new(index, MetadataType.Short, v);
    public static MetadataEntry FromInt(byte index, int v) => new(index, MetadataType.Int, v);
    public static MetadataEntry FromFloat(byte index, float v) => new(index, MetadataType.Float, v);
    public static MetadataEntry FromString(byte index, string v) => new(index, MetadataType.String, v);
    public static MetadataEntry FromItem(byte index, ItemStack v) => new(index, MetadataType.Item, v);
    public static MetadataEntry FromVector(byte index, MetadataVector v) => new(index, MetadataType.Vector, v);

    /// <summary>Numeric value widened to int (Byte/Short/Int), or null.</summary>
    public int? AsInt() => Value switch
    {
        sbyte b => b,
        short s => s,
        int i => i,
        _ => null,
    };

    public override string ToString() => $"[{Index}:{Type}={Value}]";
}

/// <summary>An ordered list of metadata entries as they appeared on the wire.</summary>
public sealed class EntityMetadata : IReadOnlyList<MetadataEntry>
{
    /// <summary>End-of-metadata marker byte.</summary>
    public const byte Terminator = 0x7F;

    private readonly List<MetadataEntry> _entries;

    public EntityMetadata() => _entries = new List<MetadataEntry>();
    public EntityMetadata(IEnumerable<MetadataEntry> entries) => _entries = new List<MetadataEntry>(entries);

    public int Count => _entries.Count;
    public MetadataEntry this[int i] => _entries[i];

    public void Add(MetadataEntry e)
    {
        if (e.Index > 31) throw new ArgumentOutOfRangeException(nameof(e), "metadata index must be 0..31");
        _entries.Add(e);
    }

    /// <summary>Finds the last entry with the given index.</summary>
    public MetadataEntry? Get(int index)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Index == index) return _entries[i];
        return null;
    }

    /// <summary>Entity flags (index 0) if present.</summary>
    public sbyte? Flags => Get(0) is { Type: MetadataType.Byte, Value: sbyte b } ? b : null;

    public IEnumerator<MetadataEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => string.Join(" ", _entries);
}
