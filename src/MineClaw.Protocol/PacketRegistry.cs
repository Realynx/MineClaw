using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol;

/// <summary>
/// Maps packet ids to packet classes for protocol 17 and reads/writes complete packets (id byte + body).
/// Direction rules mirror the vanilla b1.8 registration table: reading an id in a direction vanilla never
/// uses it is reported as an error, because it almost always means the stream is already desynchronised.
/// </summary>
public static class PacketRegistry
{
    /// <summary>Registration info for one packet id.</summary>
    public sealed record Entry(byte Id, string Name, Type Type, bool ClientBound, bool ServerBound, Func<IPacket> Factory);

    private static readonly Entry?[] Entries = new Entry?[256];

    static PacketRegistry()
    {
        // (S->C allowed, C->S allowed) exactly as in vanilla b1.8 Packet.java.
        Add<KeepAlivePacket>(0x00, true, true);
        Add<LoginRequestPacket>(0x01, true, true);
        Add<HandshakePacket>(0x02, true, true);
        Add<ChatPacket>(0x03, true, true);
        Add<TimeUpdatePacket>(0x04, true, false);
        Add<EntityEquipmentPacket>(0x05, true, false);
        Add<SpawnPositionPacket>(0x06, true, false);
        Add<UseEntityPacket>(0x07, false, true);
        Add<UpdateHealthPacket>(0x08, true, false);
        Add<RespawnPacket>(0x09, true, true);
        Add<PlayerOnGroundPacket>(0x0A, true, true);
        Add<PlayerPositionPacket>(0x0B, true, true);
        Add<PlayerLookPacket>(0x0C, true, true);
        Add<PlayerPositionLookPacket>(0x0D, true, true);
        Add<PlayerDiggingPacket>(0x0E, false, true);
        Add<PlayerBlockPlacementPacket>(0x0F, false, true);
        Add<HoldingChangePacket>(0x10, false, true);
        Add<UseBedPacket>(0x11, true, false);
        Add<AnimationPacket>(0x12, true, true);
        Add<EntityActionPacket>(0x13, false, true);
        Add<NamedEntitySpawnPacket>(0x14, true, false);
        Add<PickupSpawnPacket>(0x15, true, false);
        Add<CollectItemPacket>(0x16, true, false);
        Add<AddObjectVehiclePacket>(0x17, true, false);
        Add<MobSpawnPacket>(0x18, true, false);
        Add<EntityPaintingPacket>(0x19, true, false);
        Add<ExperienceOrbPacket>(0x1A, true, false);
        Add<StanceUpdatePacket>(0x1B, false, true);
        Add<EntityVelocityPacket>(0x1C, true, false);
        Add<DestroyEntityPacket>(0x1D, true, false);
        Add<EntityPacket>(0x1E, true, false);
        Add<EntityRelativeMovePacket>(0x1F, true, false);
        Add<EntityLookPacket>(0x20, true, false);
        Add<EntityLookRelativeMovePacket>(0x21, true, false);
        Add<EntityTeleportPacket>(0x22, true, false);
        Add<EntityStatusPacket>(0x26, true, false);
        Add<AttachEntityPacket>(0x27, true, false);
        Add<EntityMetadataPacket>(0x28, true, false);
        Add<EntityEffectPacket>(0x29, true, false);
        Add<RemoveEntityEffectPacket>(0x2A, true, false);
        Add<ExperiencePacket>(0x2B, true, false);
        Add<PreChunkPacket>(0x32, true, false);
        Add<MapChunkPacket>(0x33, true, false);
        Add<MultiBlockChangePacket>(0x34, true, false);
        Add<BlockChangePacket>(0x35, true, false);
        Add<BlockActionPacket>(0x36, true, false);
        Add<ExplosionPacket>(0x3C, true, false);
        Add<SoundEffectPacket>(0x3D, true, false);
        Add<NewStatePacket>(0x46, true, false);
        Add<ThunderboltPacket>(0x47, true, false);
        Add<OpenWindowPacket>(0x64, true, false);
        Add<CloseWindowPacket>(0x65, true, true);
        Add<WindowClickPacket>(0x66, false, true);
        Add<SetSlotPacket>(0x67, true, false);
        Add<WindowItemsPacket>(0x68, true, false);
        Add<UpdateProgressBarPacket>(0x69, true, false);
        Add<TransactionPacket>(0x6A, true, true);
        Add<CreativeInventoryActionPacket>(0x6B, true, true);
        Add<UpdateSignPacket>(0x82, true, true);
        Add<ItemDataPacket>(0x83, true, false);
        Add<IncrementStatisticPacket>(0xC8, true, false);
        Add<PlayerListItemPacket>(0xC9, true, false);
        Add<ServerListPingPacket>(0xFE, false, true);
        Add<KickPacket>(0xFF, true, true);
    }

    private static void Add<T>(byte id, bool clientBound, bool serverBound) where T : IPacket, new()
    {
        var probe = new T();
        if (probe.Id != id) throw new InvalidOperationException($"{typeof(T).Name}.Id is 0x{probe.Id:X2}, registered as 0x{id:X2}");
        if (Entries[id] != null) throw new InvalidOperationException($"Duplicate packet id 0x{id:X2}");
        Entries[id] = new Entry(id, typeof(T).Name, typeof(T), clientBound, serverBound, static () => new T());
    }

    /// <summary>All registered packets in id order.</summary>
    public static IEnumerable<Entry> All => Entries.Where(e => e != null)!;

    public static Entry? Get(byte id) => Entries[id];

    public static bool IsAllowed(byte id, PacketDirection direction) =>
        Entries[id] is { } e && (direction == PacketDirection.ClientBound ? e.ClientBound : e.ServerBound);

    public static string NameOf(byte id) => Entries[id]?.Name ?? $"Unknown(0x{id:X2})";

    /// <summary>Creates an empty packet instance for the id.</summary>
    public static IPacket Create(byte id) =>
        (Entries[id] ?? throw new ProtocolException($"Unknown packet id 0x{id:X2}")).Factory();

    /// <summary>
    /// Reads one complete packet travelling in <paramref name="direction"/>. Throws <see cref="EndOfStreamException"/>
    /// if the stream ends exactly at a packet boundary, and <see cref="ProtocolException"/> (with the last packet ids
    /// read) for unknown ids, ids not valid in this direction, malformed bodies or truncation mid-packet.
    /// </summary>
    public static IPacket Read(MinecraftReader reader, PacketDirection direction)
    {
        if (!TryRead(reader, direction, out var packet))
            throw new EndOfStreamException("Stream ended at a packet boundary");
        return packet;
    }

    /// <summary>Like <see cref="Read"/> but returns false on a clean end of stream at a packet boundary.</summary>
    public static bool TryRead(MinecraftReader reader, PacketDirection direction, out IPacket packet)
    {
        int raw = reader.ReadByteOrEof();
        if (raw < 0)
        {
            packet = null!;
            return false;
        }
        byte id = (byte)raw;
        reader.NotePacketId(id);
        var entry = Entries[id];
        if (entry == null)
            throw new ProtocolException(
                $"Unknown packet id 0x{id:X2} ({direction.Arrow()}) after {reader.PacketsRead - 1} packets; recent ids: {reader.RecentPacketIdsText}");
        if (!(direction == PacketDirection.ClientBound ? entry.ClientBound : entry.ServerBound))
            throw new ProtocolException(
                $"Packet 0x{id:X2} {entry.Name} is not valid {direction.Arrow()} (desync?); recent ids: {reader.RecentPacketIdsText}");

        packet = entry.Factory();
        try
        {
            packet.Read(reader, direction);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ProtocolException(
                $"Failed reading packet 0x{id:X2} {entry.Name} ({direction.Arrow()}): {ex.Message}; recent ids: {reader.RecentPacketIdsText}", ex);
        }
        return true;
    }

    /// <summary>Writes id byte + body.</summary>
    public static void Write(MinecraftWriter writer, IPacket packet, PacketDirection direction)
    {
        writer.WriteUByte(packet.Id);
        packet.Write(writer, direction);
    }
}
