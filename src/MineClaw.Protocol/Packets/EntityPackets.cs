using MineClaw.Data;

namespace MineClaw.Protocol.Packets;

// Units used below (see FixedPoint): absolute positions are int = floor(blocks × 32); relative moves are
// signed byte = Δblocks × 32; angles are a byte = degrees × 256 / 360; velocities are short = blocks/tick × 8000.

/// <summary>
/// 0x05 Entity Equipment (S→C). Fields: int EntityId, short Slot (0 held, 1–4 armour boots..helmet),
/// short ItemId (−1 = empty), short Damage. <see cref="Tag"/> is the item's NBT on 1.12.2 (dyed leather's display.color);
/// b1.8.1's wire has none, so it is never read or written here.
/// </summary>
public sealed class EntityEquipmentPacket : IPacket
{
    public const byte PacketId = 0x05;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public short Slot { get; set; }
    public short ItemId { get; set; }
    public short Damage { get; set; }
    public ItemTag? Tag { get; set; }

    /// <summary>The worn item (empty when <see cref="ItemId"/> is −1), its tag included.</summary>
    public ItemStack Item => ItemId < 0 ? ItemStack.Empty : new ItemStack(ItemId, 1, Damage) { Tag = Tag };

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Slot = r.ReadShort();
        ItemId = r.ReadShort();
        Damage = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteShort(Slot);
        w.WriteShort(ItemId);
        w.WriteShort(Damage);
    }
}

/// <summary>
/// 0x11 Use Bed (S→C). Fields: int EntityId, byte InBed (always 0), int X, byte Y, int Z (head-of-bed block).
/// </summary>
public sealed class UseBedPacket : IPacket
{
    public const byte PacketId = 0x11;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte InBed { get; set; }
    public int X { get; set; }
    /// <summary>Block Y, sent as a signed byte.</summary>
    public int Y { get; set; }
    public int Z { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        InBed = r.ReadSByte();
        X = r.ReadInt();
        Y = r.ReadSByte();
        Z = r.ReadInt();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(InBed);
        w.WriteInt(X);
        w.WriteSByte((sbyte)Y);
        w.WriteInt(Z);
    }
}

/// <summary>
/// 0x14 Named Entity Spawn (S→C) — another player. Fields: int EntityId, string16 PlayerName (max 16),
/// int X, int Y, int Z (absolute ×32), byte Yaw, byte Pitch (angles), short CurrentItem (held item id, 0 = none).
/// </summary>
public sealed class NamedEntitySpawnPacket : IPacket
{
    public const byte PacketId = 0x14;
    public byte Id => PacketId;
    /// <summary>The exact position in blocks when the version sends doubles (1.12.2); null = the fixed-point fields (b1.8.1).</summary>
    public (double X, double Y, double Z)? Exact { get; set; }
    public int EntityId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public byte Yaw { get; set; }
    public byte Pitch { get; set; }
    public short CurrentItem { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        PlayerName = r.ReadString16();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
        Yaw = r.ReadUByte();
        Pitch = r.ReadUByte();
        CurrentItem = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteString16(PlayerName);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
        w.WriteUByte(Yaw);
        w.WriteUByte(Pitch);
        w.WriteShort(CurrentItem);
    }
}

/// <summary>
/// 0x15 Pickup Spawn (S→C) — a dropped item entity. Fields: int EntityId, short ItemId, byte Count, short Damage,
/// int X, int Y, int Z (absolute ×32), byte MotionX, byte MotionY, byte MotionZ (velocity × 128; wiki.vg calls
/// these rotation/pitch/roll).
/// </summary>
public sealed class PickupSpawnPacket : IPacket
{
    public const byte PacketId = 0x15;
    public byte Id => PacketId;
    /// <summary>The exact position in blocks when the version sends doubles (1.12.2); null = the fixed-point fields (b1.8.1).</summary>
    public (double X, double Y, double Z)? Exact { get; set; }
    public int EntityId { get; set; }
    public short ItemId { get; set; }
    public byte Count { get; set; }
    public short Damage { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public sbyte MotionX { get; set; }
    public sbyte MotionY { get; set; }
    public sbyte MotionZ { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        ItemId = r.ReadShort();
        Count = r.ReadUByte();
        Damage = r.ReadShort();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
        MotionX = r.ReadSByte();
        MotionY = r.ReadSByte();
        MotionZ = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteShort(ItemId);
        w.WriteUByte(Count);
        w.WriteShort(Damage);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
        w.WriteSByte(MotionX);
        w.WriteSByte(MotionY);
        w.WriteSByte(MotionZ);
    }
}

/// <summary>0x16 Collect Item (S→C). Fields: int CollectedEntityId, int CollectorEntityId.</summary>
public sealed class CollectItemPacket : IPacket
{
    public const byte PacketId = 0x16;
    public byte Id => PacketId;
    public int CollectedEntityId { get; set; }
    public int CollectorEntityId { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        CollectedEntityId = r.ReadInt();
        CollectorEntityId = r.ReadInt();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(CollectedEntityId);
        w.WriteInt(CollectorEntityId);
    }
}

/// <summary>
/// 0x17 Add Object / Vehicle (S→C). Fields: int EntityId, byte Type, int X, int Y, int Z (absolute ×32),
/// int ThrowerEntityId; <b>only if ThrowerEntityId &gt; 0</b>: short SpeedX, short SpeedY, short SpeedZ (×8000).
/// Types: 1 boat, 10 minecart, 11 storage cart, 12 powered cart, 50 TNT, 60 arrow, 61 snowball, 62 egg,
/// 63 fireball, 70 falling sand, 71 falling gravel, 90 fishing float.
/// </summary>
public sealed class AddObjectVehiclePacket : IPacket
{
    public const byte PacketId = 0x17;
    public byte Id => PacketId;
    /// <summary>The exact position in blocks when the version sends doubles (1.12.2); null = the fixed-point fields (b1.8.1).</summary>
    public (double X, double Y, double Z)? Exact { get; set; }
    public int EntityId { get; set; }
    public byte Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int ThrowerEntityId { get; set; }
    /// <summary>Only on the wire when <see cref="ThrowerEntityId"/> &gt; 0.</summary>
    public short SpeedX { get; set; }
    public short SpeedY { get; set; }
    public short SpeedZ { get; set; }

    public bool HasSpeed => ThrowerEntityId > 0;

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Type = r.ReadUByte();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
        ThrowerEntityId = r.ReadInt();
        if (ThrowerEntityId > 0)
        {
            SpeedX = r.ReadShort();
            SpeedY = r.ReadShort();
            SpeedZ = r.ReadShort();
        }
        else
        {
            SpeedX = SpeedY = SpeedZ = 0;
        }
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteUByte(Type);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
        w.WriteInt(ThrowerEntityId);
        if (ThrowerEntityId > 0)
        {
            w.WriteShort(SpeedX);
            w.WriteShort(SpeedY);
            w.WriteShort(SpeedZ);
        }
    }
}

/// <summary>
/// 0x18 Mob Spawn (S→C). Fields: int EntityId, byte Type, int X, int Y, int Z (absolute ×32), byte Yaw,
/// byte Pitch, metadata. Types: 50 creeper, 51 skeleton, 52 spider, 53 giant, 54 zombie, 55 slime, 56 ghast,
/// 57 zombie pigman, 58 enderman, 59 cave spider, 60 silverfish, 90 pig, 91 sheep, 92 cow, 93 chicken,
/// 94 squid, 95 wolf.
/// </summary>
public sealed class MobSpawnPacket : IPacket
{
    public const byte PacketId = 0x18;
    public byte Id => PacketId;
    /// <summary>The exact position in blocks when the version sends doubles (1.12.2); null = the fixed-point fields (b1.8.1).</summary>
    public (double X, double Y, double Z)? Exact { get; set; }
    public int EntityId { get; set; }
    public byte Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public byte Yaw { get; set; }
    public byte Pitch { get; set; }
    public EntityMetadata Metadata { get; set; } = new();

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Type = r.ReadUByte();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
        Yaw = r.ReadUByte();
        Pitch = r.ReadUByte();
        Metadata = r.ReadMetadata();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteUByte(Type);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
        w.WriteUByte(Yaw);
        w.WriteUByte(Pitch);
        w.WriteMetadata(Metadata);
    }
}

/// <summary>
/// 0x19 Entity Painting (S→C). Fields: int EntityId, string16 Title (max 13), int X, int Y, int Z (block coords
/// of the painting centre), int Direction (0 −Z, 1 −X, 2 +Z, 3 +X).
/// </summary>
public sealed class EntityPaintingPacket : IPacket
{
    public const byte PacketId = 0x19;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Direction { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Title = r.ReadString16();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
        Direction = r.ReadInt();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteString16(Title);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
        w.WriteInt(Direction);
    }
}

/// <summary>0x1A Experience Orb (S→C). Fields: int EntityId, int X, int Y, int Z (absolute ×32), short Count (xp value).</summary>
public sealed class ExperienceOrbPacket : IPacket
{
    public const byte PacketId = 0x1A;
    public byte Id => PacketId;
    /// <summary>The exact position in blocks when the version sends doubles (1.12.2); null = the fixed-point fields (b1.8.1).</summary>
    public (double X, double Y, double Z)? Exact { get; set; }
    public int EntityId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public short Count { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
        Count = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
        w.WriteShort(Count);
    }
}

/// <summary>0x1C Entity Velocity (S→C). Fields: int EntityId, short VelocityX, VelocityY, VelocityZ (blocks/tick × 8000).</summary>
public sealed class EntityVelocityPacket : IPacket
{
    public const byte PacketId = 0x1C;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public short VelocityX { get; set; }
    public short VelocityY { get; set; }
    public short VelocityZ { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        VelocityX = r.ReadShort();
        VelocityY = r.ReadShort();
        VelocityZ = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteShort(VelocityX);
        w.WriteShort(VelocityY);
        w.WriteShort(VelocityZ);
    }
}

/// <summary>0x1D Destroy Entity (S→C). Fields: int EntityId.</summary>
public sealed class DestroyEntityPacket : IPacket
{
    public const byte PacketId = 0x1D;
    public byte Id => PacketId;
    public int EntityId { get; set; }

    public void Read(MinecraftReader r, PacketDirection d) => EntityId = r.ReadInt();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteInt(EntityId);
}

/// <summary>0x1E Entity (S→C) — "entity still exists" with no movement. Fields: int EntityId.</summary>
public sealed class EntityPacket : IPacket
{
    public const byte PacketId = 0x1E;
    public byte Id => PacketId;
    public int EntityId { get; set; }

    public void Read(MinecraftReader r, PacketDirection d) => EntityId = r.ReadInt();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteInt(EntityId);
}

/// <summary>0x1F Entity Relative Move (S→C). Fields: int EntityId, byte DX, byte DY, byte DZ (signed, Δblocks × 32).</summary>
public sealed class EntityRelativeMovePacket : IPacket
{
    public const byte PacketId = 0x1F;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte DX { get; set; }
    public sbyte DY { get; set; }
    public sbyte DZ { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        DX = r.ReadSByte();
        DY = r.ReadSByte();
        DZ = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(DX);
        w.WriteSByte(DY);
        w.WriteSByte(DZ);
    }
}

/// <summary>0x20 Entity Look (S→C). Fields: int EntityId, byte Yaw, byte Pitch (angles).</summary>
public sealed class EntityLookPacket : IPacket
{
    public const byte PacketId = 0x20;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public byte Yaw { get; set; }
    public byte Pitch { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Yaw = r.ReadUByte();
        Pitch = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteUByte(Yaw);
        w.WriteUByte(Pitch);
    }
}

/// <summary>
/// 0x21 Entity Look and Relative Move (S→C). Fields: int EntityId, byte DX, DY, DZ (signed ×32), byte Yaw, byte Pitch.
/// </summary>
public sealed class EntityLookRelativeMovePacket : IPacket
{
    public const byte PacketId = 0x21;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte DX { get; set; }
    public sbyte DY { get; set; }
    public sbyte DZ { get; set; }
    public byte Yaw { get; set; }
    public byte Pitch { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        DX = r.ReadSByte();
        DY = r.ReadSByte();
        DZ = r.ReadSByte();
        Yaw = r.ReadUByte();
        Pitch = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(DX);
        w.WriteSByte(DY);
        w.WriteSByte(DZ);
        w.WriteUByte(Yaw);
        w.WriteUByte(Pitch);
    }
}

/// <summary>0x22 Entity Teleport (S→C). Fields: int EntityId, int X, int Y, int Z (absolute ×32), byte Yaw, byte Pitch.</summary>
public sealed class EntityTeleportPacket : IPacket
{
    public const byte PacketId = 0x22;
    public byte Id => PacketId;
    /// <summary>The exact position in blocks when the version sends doubles (1.12.2); null = the fixed-point fields (b1.8.1).</summary>
    public (double X, double Y, double Z)? Exact { get; set; }
    public int EntityId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public byte Yaw { get; set; }
    public byte Pitch { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
        Yaw = r.ReadUByte();
        Pitch = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
        w.WriteUByte(Yaw);
        w.WriteUByte(Pitch);
    }
}

/// <summary>
/// 0x26 Entity Status (S→C). Fields: int EntityId, byte Status.
/// Status: 2 hurt, 3 dead, 6 wolf taming, 7 wolf tamed, 8 wolf shaking water, 9 eating accepted (own player).
/// </summary>
public sealed class EntityStatusPacket : IPacket
{
    public const byte PacketId = 0x26;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte Status { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Status = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(Status);
    }
}

/// <summary>0x27 Attach Entity (S→C). Fields: int EntityId (rider), int VehicleId (−1 = dismount).</summary>
public sealed class AttachEntityPacket : IPacket
{
    public const byte PacketId = 0x27;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public int VehicleId { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        VehicleId = r.ReadInt();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteInt(VehicleId);
    }
}

/// <summary>0x28 Entity Metadata (S→C). Fields: int EntityId, metadata (terminated by 0x7F).</summary>
public sealed class EntityMetadataPacket : IPacket
{
    public const byte PacketId = 0x28;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public EntityMetadata Metadata { get; set; } = new();

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Metadata = r.ReadMetadata();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteMetadata(Metadata);
    }
}

/// <summary>0x29 Entity Effect (S→C). Fields: int EntityId, byte EffectId, byte Amplifier, short Duration (ticks).</summary>
public sealed class EntityEffectPacket : IPacket
{
    public const byte PacketId = 0x29;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte EffectId { get; set; }
    public sbyte Amplifier { get; set; }
    public short Duration { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        EffectId = r.ReadSByte();
        Amplifier = r.ReadSByte();
        Duration = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(EffectId);
        w.WriteSByte(Amplifier);
        w.WriteShort(Duration);
    }
}

/// <summary>0x2A Remove Entity Effect (S→C). Fields: int EntityId, byte EffectId.</summary>
public sealed class RemoveEntityEffectPacket : IPacket
{
    public const byte PacketId = 0x2A;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte EffectId { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        EffectId = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(EffectId);
    }
}

/// <summary>
/// 0x47 Thunderbolt / global entity (S→C). Fields: int EntityId, byte Type (1 = lightning bolt; wiki.vg: "always true"),
/// int X, int Y, int Z (absolute ×32).
/// </summary>
public sealed class ThunderboltPacket : IPacket
{
    public const byte PacketId = 0x47;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte Type { get; set; } = 1;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Type = r.ReadSByte();
        X = r.ReadInt();
        Y = r.ReadInt();
        Z = r.ReadInt();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(Type);
        w.WriteInt(X);
        w.WriteInt(Y);
        w.WriteInt(Z);
    }
}
