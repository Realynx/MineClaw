using MineClaw.Data;

namespace MineClaw.Protocol.Packets;

/// <summary>
/// 0x07 Use Entity (C→S). Fields: int UserEntityId (the player's own id; ignored by the server),
/// int TargetEntityId, bool LeftClick (wire byte: 1 = left click/attack, 0 = right click/interact).
/// </summary>
public sealed class UseEntityPacket : IPacket
{
    public const byte PacketId = 0x07;
    public byte Id => PacketId;
    public int UserEntityId { get; set; }
    public int TargetEntityId { get; set; }
    public bool LeftClick { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        UserEntityId = r.ReadInt();
        TargetEntityId = r.ReadInt();
        LeftClick = r.ReadSByte() != 0;
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(UserEntityId);
        w.WriteInt(TargetEntityId);
        w.WriteBool(LeftClick);
    }
}

/// <summary>
/// 0x08 Update Health (S→C). Fields: short Health (0..20, ≤0 = dead), short Food (0..20), float FoodSaturation (0..5).
/// </summary>
public sealed class UpdateHealthPacket : IPacket
{
    public const byte PacketId = 0x08;
    public byte Id => PacketId;
    public short Health { get; set; }
    public short Food { get; set; }
    public float FoodSaturation { get; set; }
    /// <summary>The exact health a 1.12.2 server sent (a float; <see cref="Health"/> is its ceiling, so 0.5 hp isn't dead).
    /// Null on b1.8.1.</summary>
    public float? HealthExact { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Health = r.ReadShort();
        Food = r.ReadShort();
        FoodSaturation = r.ReadFloat();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteShort(Health);
        w.WriteShort(Food);
        w.WriteFloat(FoodSaturation);
    }
}

/// <summary>
/// 0x0A Player / On Ground (both directions in the registry; in practice C→S). Fields: bool OnGround.
/// The vanilla client sends one of 0x0A–0x0D every tick (20/s).
/// </summary>
public sealed class PlayerOnGroundPacket : IPacket
{
    public const byte PacketId = 0x0A;
    public byte Id => PacketId;
    public bool OnGround { get; set; }

    public void Read(MinecraftReader r, PacketDirection d) => OnGround = r.ReadBool();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteBool(OnGround);
}

/// <summary>
/// 0x0B Player Position (both directions; in practice C→S).
/// Wire C→S: double X, double Y (feet), double Stance (eyes = Y + 1.62), double Z, bool OnGround.
/// Wire S→C: X, Stance, Y, Z, OnGround (the server stores eye height in the second field; see
/// <see cref="PlayerPositionLookPacket"/>). Properties always hold feet in <see cref="Y"/> and eyes in <see cref="Stance"/>.
/// The server kicks with "Illegal stance" unless 0.1 ≤ Stance − Y ≤ 1.65.
/// </summary>
public sealed class PlayerPositionPacket : IPacket
{
    public const byte PacketId = 0x0B;
    public byte Id => PacketId;
    public double X { get; set; }
    /// <summary>Feet Y.</summary>
    public double Y { get; set; }
    /// <summary>Eye Y (feet + 1.62).</summary>
    public double Stance { get; set; }
    public double Z { get; set; }
    public bool OnGround { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadDouble();
        double a = r.ReadDouble(), b = r.ReadDouble();
        (Y, Stance) = d == PacketDirection.ServerBound ? (a, b) : (b, a);
        Z = r.ReadDouble();
        OnGround = r.ReadBool();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteDouble(X);
        if (d == PacketDirection.ServerBound) { w.WriteDouble(Y); w.WriteDouble(Stance); }
        else { w.WriteDouble(Stance); w.WriteDouble(Y); }
        w.WriteDouble(Z);
        w.WriteBool(OnGround);
    }
}

/// <summary>
/// 0x0C Player Look (both directions; in practice C→S). Fields: float Yaw (degrees), float Pitch (degrees), bool OnGround.
/// </summary>
public sealed class PlayerLookPacket : IPacket
{
    public const byte PacketId = 0x0C;
    public byte Id => PacketId;
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool OnGround { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Yaw = r.ReadFloat();
        Pitch = r.ReadFloat();
        OnGround = r.ReadBool();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteFloat(Yaw);
        w.WriteFloat(Pitch);
        w.WriteBool(OnGround);
    }
}

/// <summary>
/// 0x0D Player Position &amp; Look (both directions). <b>Wire order of the two Y doubles differs by direction:</b>
/// <list type="bullet">
/// <item>C→S: double X, double Y (feet), double Stance (eyes), double Z, float Yaw, float Pitch, bool OnGround.</item>
/// <item>S→C: double X, double Stance (eyes = feet + 1.62), double Y (feet), double Z, float Yaw, float Pitch, bool OnGround.</item>
/// </list>
/// Properties are direction-independent: <see cref="Y"/> is always feet, <see cref="Stance"/> always eyes.
/// <para>When the server sends this (login, teleport, rejected move) it ignores all movement from the client until
/// the client echoes it back C→S with exactly the same X and Z and a feet-Y within 0.1 — the vanilla client
/// re-sends the received values unchanged (feet Y, stance = feet + 1.62). Use <see cref="EchoForServer"/>.</para>
/// </summary>
public sealed class PlayerPositionLookPacket : IPacket
{
    public const byte PacketId = 0x0D;
    public const double EyeHeight = 1.62;
    public byte Id => PacketId;
    public double X { get; set; }
    /// <summary>Feet Y.</summary>
    public double Y { get; set; }
    /// <summary>Eye Y (feet + 1.62).</summary>
    public double Stance { get; set; }
    public double Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool OnGround { get; set; }

    /// <summary>The C→S confirmation the vanilla client sends after receiving a server 0x0D.</summary>
    public PlayerPositionLookPacket EchoForServer(bool onGround = false) => new()
    {
        X = X, Y = Y, Stance = Stance, Z = Z, Yaw = Yaw, Pitch = Pitch, OnGround = onGround,
    };

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadDouble();
        double a = r.ReadDouble(), b = r.ReadDouble();
        (Y, Stance) = d == PacketDirection.ServerBound ? (a, b) : (b, a);
        Z = r.ReadDouble();
        Yaw = r.ReadFloat();
        Pitch = r.ReadFloat();
        OnGround = r.ReadBool();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteDouble(X);
        if (d == PacketDirection.ServerBound) { w.WriteDouble(Y); w.WriteDouble(Stance); }
        else { w.WriteDouble(Stance); w.WriteDouble(Y); }
        w.WriteDouble(Z);
        w.WriteFloat(Yaw);
        w.WriteFloat(Pitch);
        w.WriteBool(OnGround);
    }
}

/// <summary>
/// 0x0E Player Digging (C→S). Fields: ubyte Status, int X, ubyte Y, int Z, ubyte Face.
/// Status: 0 start digging, 2 finished digging, 4 drop held item (x/y/z/face = 0), 5 shoot arrow / finish eating
/// (release use item; x/y/z = 0, face = 255). Face: 0 −Y, 1 +Y, 2 −Z, 3 +Z, 4 −X, 5 +X.
/// </summary>
public sealed class PlayerDiggingPacket : IPacket
{
    public const byte PacketId = 0x0E;
    public const byte StatusStarted = 0, StatusFinished = 2, StatusDropItem = 4, StatusReleaseUseItem = 5;
    public byte Id => PacketId;
    public byte Status { get; set; }
    public int X { get; set; }
    /// <summary>Block Y, sent as an unsigned byte (0..255).</summary>
    public int Y { get; set; }
    public int Z { get; set; }
    public byte Face { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Status = r.ReadUByte();
        X = r.ReadInt();
        Y = r.ReadUByte();
        Z = r.ReadInt();
        Face = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteUByte(Status);
        w.WriteInt(X);
        w.WriteUByte((byte)Y);
        w.WriteInt(Z);
        w.WriteUByte(Face);
    }
}

/// <summary>
/// 0x0F Player Block Placement (C→S). Fields: int X, ubyte Y, int Z, ubyte Direction (face), slot HeldItem.
/// To use the held item without targeting a block (eat, throw, bow) send X = −1, Y = 255, Z = −1, Direction = 255.
/// HeldItem is the client's current held stack in slot form (short −1 if empty; else short id, byte count, short damage).
/// </summary>
public sealed class PlayerBlockPlacementPacket : IPacket
{
    public const byte PacketId = 0x0F;
    public const byte NoDirection = 255;
    public byte Id => PacketId;
    public int X { get; set; }
    /// <summary>Block Y, sent as an unsigned byte (255 = "no block").</summary>
    public int Y { get; set; }
    public int Z { get; set; }
    public byte Direction { get; set; }
    public ItemStack HeldItem { get; set; } = ItemStack.Empty;

    /// <summary>The "use item in hand" form (no target block).</summary>
    public static PlayerBlockPlacementPacket UseItem(ItemStack held) =>
        new() { X = -1, Y = 255, Z = -1, Direction = NoDirection, HeldItem = held };

    public void Read(MinecraftReader r, PacketDirection d)
    {
        X = r.ReadInt();
        Y = r.ReadUByte();
        Z = r.ReadInt();
        Direction = r.ReadUByte();
        HeldItem = r.ReadSlot();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(X);
        w.WriteUByte((byte)Y);
        w.WriteInt(Z);
        w.WriteUByte(Direction);
        w.WriteSlot(HeldItem);
    }
}

/// <summary>0x10 Holding Change (C→S). Fields: short Slot (hotbar index 0..8).</summary>
public sealed class HoldingChangePacket : IPacket
{
    public const byte PacketId = 0x10;
    public byte Id => PacketId;
    public short Slot { get; set; }

    public void Read(MinecraftReader r, PacketDirection d) => Slot = r.ReadShort();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteShort(Slot);
}

/// <summary>
/// 0x12 Animation (both directions). Fields: int EntityId, byte Animation.
/// Animation: 0 none, 1 swing arm, 2 damage, 3 leave bed, 104 crouch, 105 uncrouch (the client sends 1 when swinging).
/// </summary>
public sealed class AnimationPacket : IPacket
{
    public const byte PacketId = 0x12;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte Animation { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Animation = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(Animation);
    }
}

/// <summary>
/// 0x13 Entity Action (C→S). Fields: int EntityId, byte Action.
/// Action: 1 crouch, 2 uncrouch, 3 leave bed, 4 start sprinting, 5 stop sprinting.
/// </summary>
public sealed class EntityActionPacket : IPacket
{
    public const byte PacketId = 0x13;
    public byte Id => PacketId;
    public int EntityId { get; set; }
    public sbyte Action { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        EntityId = r.ReadInt();
        Action = r.ReadSByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(EntityId);
        w.WriteSByte(Action);
    }
}

/// <summary>
/// 0x1B Stance Update (C→S, unused by vanilla). Fields: float A, float B, float C, float D, bool E, bool F.
/// (Vanilla names: strafe, forward, pitch, yaw, two flags.)
/// </summary>
public sealed class StanceUpdatePacket : IPacket
{
    public const byte PacketId = 0x1B;
    public byte Id => PacketId;
    public float Strafe { get; set; }
    public float Forward { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public bool Flag1 { get; set; }
    public bool Flag2 { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Strafe = r.ReadFloat();
        Forward = r.ReadFloat();
        Pitch = r.ReadFloat();
        Yaw = r.ReadFloat();
        Flag1 = r.ReadBool();
        Flag2 = r.ReadBool();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteFloat(Strafe);
        w.WriteFloat(Forward);
        w.WriteFloat(Pitch);
        w.WriteFloat(Yaw);
        w.WriteBool(Flag1);
        w.WriteBool(Flag2);
    }
}

/// <summary>
/// 0x2B Experience (S→C). b1.8 layout (changed to float/short/short in 1.0):
/// byte Experience (progress within the current level), byte Level, short TotalExperience.
/// </summary>
public sealed class ExperiencePacket : IPacket
{
    public const byte PacketId = 0x2B;
    public byte Id => PacketId;
    public sbyte Experience { get; set; }
    public sbyte Level { get; set; }
    public short TotalExperience { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Experience = r.ReadSByte();
        Level = r.ReadSByte();
        TotalExperience = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(Experience);
        w.WriteSByte(Level);
        w.WriteShort(TotalExperience);
    }
}
