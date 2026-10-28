namespace MineClaw.Protocol.Packets;

/// <summary>0x04 Time Update (S→C). Fields: long Time (world ticks; time of day = Time % 24000).</summary>
public sealed class TimeUpdatePacket : IPacket
{
    public const byte PacketId = 0x04;
    public byte Id => PacketId;
    public long Time { get; set; }

    public void Read(MinecraftReader r, PacketDirection d) => Time = r.ReadLong();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteLong(Time);
}

/// <summary>0x06 Spawn Position (S→C). Fields: int X, int Y, int Z (block coords of the world spawn / compass target).</summary>
public sealed class SpawnPositionPacket : IPacket
{
    public const byte PacketId = 0x06;
    public byte Id => PacketId;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
    }
}

/// <summary>
/// 0x32 Pre-Chunk (S→C). Fields: int ChunkX, int ChunkZ (chunk coords = block / 16), bool Load.
/// Load = true: allocate an empty column (data follows in 0x33). Load = false: unload the column.
/// </summary>
public sealed class PreChunkPacket : IPacket
{
    public const byte PacketId = 0x32;
    public byte Id => PacketId;
    public int ChunkX { get; set; }
    public int ChunkZ { get; set; }
    public bool Load { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        ChunkX = r.ReadInt();
        ChunkZ = r.ReadInt();
        Load = r.ReadBool();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(ChunkX);
        w.WriteInt(ChunkZ);
        w.WriteBool(Load);
    }
}

/// <summary>
/// 0x33 Map Chunk (S→C). Fields: int X (block), short Y (block), int Z (block), ubyte SizeX−1, ubyte SizeY−1,
/// ubyte SizeZ−1, int CompressedSize, byte[CompressedSize] zlib data.
/// The data inflates to SizeX·SizeY·SizeZ·5/2 bytes; decode with <see cref="ChunkDataDecoder"/>.
/// A full column is X = cx·16, Y = 0, Z = cz·16, size 16×128×16 (81920 bytes inflated).
/// </summary>
public sealed class MapChunkPacket : IPacket
{
    public const byte PacketId = 0x33;
    public byte Id => PacketId;
    public int X { get; set; }
    public short Y { get; set; }
    public int Z { get; set; }
    /// <summary>Actual size (1..256); the wire carries size − 1.</summary>
    public int SizeX { get; set; } = 16;
    public int SizeY { get; set; } = 128;
    public int SizeZ { get; set; } = 16;
    public byte[] CompressedData { get; set; } = [];

    /// <summary>Length of the inflated payload: SizeX·SizeY·SizeZ·5/2.</summary>
    public int UncompressedLength => SizeX * SizeY * SizeZ * 5 / 2;

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadInt();
        Y = r.ReadShort();
        Z = r.ReadInt();
        SizeX = r.ReadUByte() + 1;
        SizeY = r.ReadUByte() + 1;
        SizeZ = r.ReadUByte() + 1;
        int len = r.ReadInt();
        if (len < 0 || len > UncompressedLength + 4096)
            throw new ProtocolException($"Map chunk compressed size {len} implausible for region {SizeX}x{SizeY}x{SizeZ}");
        CompressedData = r.ReadBytes(len);
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        if (SizeX is < 1 or > 256 || SizeY is < 1 or > 256 || SizeZ is < 1 or > 256)
            throw new InvalidOperationException("Map chunk sizes must be 1..256");
        w.WriteInt(X);
        w.WriteShort(Y);
        w.WriteInt(Z);
        w.WriteUByte((byte)(SizeX - 1));
        w.WriteUByte((byte)(SizeY - 1));
        w.WriteUByte((byte)(SizeZ - 1));
        w.WriteInt(CompressedData.Length);
        w.WriteBytes(CompressedData);
    }
}

/// <summary>
/// 0x34 Multi Block Change (S→C). Fields: int ChunkX, int ChunkZ, short Count (read unsigned),
/// short[Count] Coordinates, byte[Count] BlockIds, byte[Count] Metadata.
/// Each coordinate is <c>(x &lt;&lt; 12) | (z &lt;&lt; 8) | y</c> with x, z local (0..15) and y 0..255.
/// Decode with <see cref="Records"/> / <see cref="ChunkDataDecoder.DecodeMultiBlockChange"/>.
/// </summary>
public sealed class MultiBlockChangePacket : IPacket
{
    public const byte PacketId = 0x34;
    public byte Id => PacketId;
    public int ChunkX { get; set; }
    public int ChunkZ { get; set; }
    public short[] Coordinates { get; set; } = [];
    public byte[] BlockIds { get; set; } = [];
    public byte[] Metadata { get; set; } = [];

    /// <summary>World-coordinate block changes.</summary>
    public IReadOnlyList<BlockChangeRecord> Records => ChunkDataDecoder.DecodeMultiBlockChange(this);

    public void Read(MinecraftReader r, PacketDirection d)
    {
        ChunkX = r.ReadInt();
        ChunkZ = r.ReadInt();
        int count = r.ReadUShort();
        Coordinates = new short[count];
        for (int i = 0; i < count; i++) Coordinates[i] = r.ReadShort();
        BlockIds = r.ReadBytes(count);
        Metadata = r.ReadBytes(count);
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        int count = Coordinates.Length;
        if (BlockIds.Length != count || Metadata.Length != count)
            throw new InvalidOperationException("MultiBlockChange arrays must have equal length");
        if (count > ushort.MaxValue) throw new InvalidOperationException("Too many block changes");
        w.WriteInt(ChunkX);
        w.WriteInt(ChunkZ);
        w.WriteUShort((ushort)count);
        foreach (var c in Coordinates) w.WriteShort(c);
        w.WriteBytes(BlockIds);
        w.WriteBytes(Metadata);
    }
}

/// <summary>0x35 Block Change (S→C). Fields: int X, ubyte Y, int Z, ubyte BlockId, ubyte Metadata.</summary>
public sealed class BlockChangePacket : IPacket
{
    public const byte PacketId = 0x35;
    public byte Id => PacketId;
    public int X { get; set; }
    /// <summary>Block Y, sent as an unsigned byte.</summary>
    public int Y { get; set; }
    public int Z { get; set; }
    public byte BlockId { get; set; }
    public byte Metadata { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadInt();
        Y = r.ReadUByte();
        Z = r.ReadInt();
        BlockId = r.ReadUByte();
        Metadata = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(X);
        w.WriteUByte((byte)Y);
        w.WriteInt(Z);
        w.WriteUByte(BlockId);
        w.WriteUByte(Metadata);
    }
}

/// <summary>
/// 0x36 Block Action (S→C) — note blocks and pistons. Fields: int X, short Y, int Z, ubyte Data1, ubyte Data2.
/// Note block: Data1 = instrument, Data2 = pitch. Piston: Data1 = 0 push / 1 pull, Data2 = direction.
/// </summary>
public sealed class BlockActionPacket : IPacket
{
    public const byte PacketId = 0x36;
    public byte Id => PacketId;
    public int X { get; set; }
    public short Y { get; set; }
    public int Z { get; set; }
    public byte Data1 { get; set; }
    public byte Data2 { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadInt();
        Y = r.ReadShort();
        Z = r.ReadInt();
        Data1 = r.ReadUByte();
        Data2 = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(X);
        w.WriteShort(Y);
        w.WriteInt(Z);
        w.WriteUByte(Data1);
        w.WriteUByte(Data2);
    }
}

/// <summary>One destroyed-block offset in an <see cref="ExplosionPacket"/> (signed bytes relative to the truncated centre).</summary>
public readonly record struct ExplosionRecord(sbyte DX, sbyte DY, sbyte DZ);

/// <summary>
/// 0x3C Explosion (S→C). Fields: double X, double Y, double Z, float Radius, int RecordCount,
/// RecordCount × (byte DX, byte DY, byte DZ). Block = ((int)X + DX, (int)Y + DY, (int)Z + DZ) where (int) is
/// Java truncation toward zero, not floor. The listed blocks become air.
/// </summary>
public sealed class ExplosionPacket : IPacket
{
    public const byte PacketId = 0x3C;
    public byte Id => PacketId;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Radius { get; set; }
    public ExplosionRecord[] Records { get; set; } = [];

    /// <summary>Absolute block coordinates of the destroyed blocks.</summary>
    public IEnumerable<(int X, int Y, int Z)> AffectedBlocks
    {
        get
        {
            int bx = (int)X, by = (int)Y, bz = (int)Z;
            foreach (var rec in Records) yield return (bx + rec.DX, by + rec.DY, bz + rec.DZ);
        }
    }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadDouble();
        Y = r.ReadDouble();
        Z = r.ReadDouble();
        Radius = r.ReadFloat();
        int count = r.ReadInt();
        if (count < 0 || count > MinecraftReader.MaxArrayLength / 3)
            throw new ProtocolException($"Explosion record count {count} out of range");
        Records = new ExplosionRecord[count];
        for (int i = 0; i < count; i++) Records[i] = new ExplosionRecord(r.ReadSByte(), r.ReadSByte(), r.ReadSByte());
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteDouble(X);
        w.WriteDouble(Y);
        w.WriteDouble(Z);
        w.WriteFloat(Radius);
        w.WriteInt(Records.Length);
        foreach (var rec in Records)
        {
            w.WriteSByte(rec.DX);
            w.WriteSByte(rec.DY);
            w.WriteSByte(rec.DZ);
        }
    }
}

/// <summary>
/// 0x3D Sound / Particle Effect (S→C). Fields: int EffectId, int X, byte Y, int Z, int Data.
/// Effects: 1000 click, 1001 click2, 1002 bow, 1003 door, 1004 fizz, 1005 record (Data = record item id),
/// 1007/1008 ghast sounds, 2000 smoke particles (Data = direction), 2001 block break (Data = block id).
/// </summary>
public sealed class SoundEffectPacket : IPacket
{
    public const byte PacketId = 0x3D;
    public byte Id => PacketId;
    public int EffectId { get; set; }
    public int X { get; set; }
    /// <summary>Block Y, sent as a signed byte.</summary>
    public int Y { get; set; }
    public int Z { get; set; }
    public int Data { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EffectId = r.ReadInt();
        X = r.ReadInt();
        Y = r.ReadSByte();
        Z = r.ReadInt();
        Data = r.ReadInt();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EffectId);
        w.WriteInt(X);
        w.WriteSByte((sbyte)Y);
        w.WriteInt(Z);
        w.WriteInt(Data);
    }
}

/// <summary>
/// 0x46 New / Invalid State (S→C). Fields: byte Reason, byte GameMode.
/// Reason: 0 invalid bed ("tile.bed.notValid"), 1 begin raining, 2 end raining, 3 game mode changed (GameMode = new mode).
/// </summary>
public sealed class NewStatePacket : IPacket
{
    public const byte PacketId = 0x46;
    public const sbyte ReasonInvalidBed = 0, ReasonBeginRain = 1, ReasonEndRain = 2, ReasonGameModeChanged = 3;
    public byte Id => PacketId;
    public sbyte Reason { get; set; }
    public sbyte GameMode { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Reason = r.ReadSByte();
        GameMode = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(Reason);
        w.WriteSByte(GameMode);
    }
}

/// <summary>
/// 0x82 Update Sign (both directions). Fields: int X, short Y, int Z, 4 × string16 (max 15 chars each).
/// C→S after placing a sign and editing it; S→C when a sign comes into view.
/// </summary>
public sealed class UpdateSignPacket : IPacket
{
    public const byte PacketId = 0x82;
    public const int MaxLineLength = 15;
    public byte Id => PacketId;
    public int X { get; set; }
    public short Y { get; set; }
    public int Z { get; set; }
    public string[] Lines { get; set; } = ["", "", "", ""];

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadInt();
        Y = r.ReadShort();
        Z = r.ReadInt();
        var lines = new string[4];
        for (int i = 0; i < 4; i++) lines[i] = r.ReadString16();
        Lines = lines;
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        if (Lines.Length != 4) throw new InvalidOperationException("A sign has exactly 4 lines");
        w.WriteInt(X);
        w.WriteShort(Y);
        w.WriteInt(Z);
        int max = d == PacketDirection.ServerBound ? MaxLineLength : short.MaxValue;
        foreach (var line in Lines) w.WriteString16(line, max);
    }
}

/// <summary>
/// 0x83 Item Data (S→C) — map contents. Fields: short ItemType (358 = map), short ItemId (map damage value),
/// ubyte Length, byte[Length] Data.
/// </summary>
public sealed class ItemDataPacket : IPacket
{
    public const byte PacketId = 0x83;
    public byte Id => PacketId;
    public short ItemType { get; set; }
    public short ItemId { get; set; }
    public byte[] Data { get; set; } = [];

    public void Read(MinecraftReader r, PacketDirection d)
    {
        ItemType = r.ReadShort();
        ItemId = r.ReadShort();
        int len = r.ReadUByte();
        Data = r.ReadBytes(len);
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        if (Data.Length > 255) throw new InvalidOperationException("Item data is at most 255 bytes");
        w.WriteShort(ItemType);
        w.WriteShort(ItemId);
        w.WriteUByte((byte)Data.Length);
        w.WriteBytes(Data);
    }
}

/// <summary>
/// Version-neutral chunk data (multi-version P1; no b1.8.1 wire form): what a newer protocol's chunk packet decodes to, ready
/// for <c>WorldState.ApplyChunkRegion</c>. A full (ground-up) column is one region from y 0 to <see cref="Height"/> with the
/// sections the server left out as air (sky light 15, block light 0); a partial update is one region per sent section.
/// 1.12.2: Chunk Data 0x20 (decompiled <c>SPacketChunkData</c> + <c>Chunk.read</c>).
/// </summary>
public sealed class ChunkSectionsMessage : IPacket
{
    /// <summary>Shares the b1.8.1 map-chunk id so logs read "0x33" for chunk data.</summary>
    public byte Id => MapChunkPacket.PacketId;
    public int ChunkX { get; init; }
    public int ChunkZ { get; init; }
    /// <summary>Ground-up continuous: the whole column (sections not in <see cref="SectionMask"/> are air) plus biomes.</summary>
    public bool FullChunk { get; init; }
    /// <summary>Bit n = section n (y 16n..16n+15) was sent.</summary>
    public int SectionMask { get; init; }
    /// <summary>Blocks per column of the sending version (256 for 1.12.2).</summary>
    public int Height { get; init; }
    /// <summary>Whether the packet carried sky light (overworld); false = the regions' sky light is null.</summary>
    public bool HasSkyLight { get; init; }
    public IReadOnlyList<ChunkRegion> Regions { get; init; } = [];
    /// <summary>256 biome ids (index z·16 + x) of a full chunk, else null.</summary>
    public byte[]? Biomes { get; init; }
    /// <summary>Block entity NBT compounds (signs, chests, spawners…) as sent.</summary>
    public IReadOnlyList<Release1122.NbtCompound> BlockEntities { get; init; } = [];

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("ChunkSectionsMessage is version-neutral: it has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("ChunkSectionsMessage is version-neutral: it has no b1.8.1 wire form");

    public override string ToString() => $"ChunkSections({ChunkX},{ChunkZ} {(FullChunk ? "full" : "partial")} mask 0x{SectionMask:X4}, {BlockEntities.Count} block entities)";
}
