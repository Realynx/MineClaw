namespace MineClaw.Protocol.Packets;

/// <summary>0xC8 Increment Statistic (S→C). Fields: int StatisticId, byte Amount.</summary>
public sealed class IncrementStatisticPacket : IPacket
{
    public const byte PacketId = 0xC8;
    public byte Id => PacketId;
    public int StatisticId { get; set; }
    public sbyte Amount { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        StatisticId = r.ReadInt();
        Amount = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(StatisticId);
        w.WriteSByte(Amount);
    }
}

/// <summary>
/// 0xC9 Player List Item (S→C) — tab list. Fields: string16 PlayerName (max 16), bool Online, short Ping (ms).
/// Online = false removes the name. Sent for every player on join and periodically with updated pings.
/// </summary>
public sealed class PlayerListItemPacket : IPacket
{
    public const byte PacketId = 0xC9;
    public byte Id => PacketId;
    public string PlayerName { get; set; } = string.Empty;
    public bool Online { get; set; }
    public short Ping { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        PlayerName = r.ReadString16();
        Online = r.ReadSByte() != 0;
        Ping = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteString16(PlayerName);
        w.WriteBool(Online);
        w.WriteShort(Ping);
    }
}
