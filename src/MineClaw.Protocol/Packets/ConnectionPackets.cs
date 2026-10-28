namespace MineClaw.Protocol.Packets;

/// <summary>
/// 0x00 Keep Alive (both directions). Fields: int KeepAliveId.
/// The server sends one every 20 ticks (1 s) with a random id; the client must echo the same id back
/// (the server uses it for the tab-list ping). The server drops connections that send nothing for 1200 ticks.
/// </summary>
public sealed class KeepAlivePacket : IPacket
{
    public const byte PacketId = 0x00;
    public byte Id => PacketId;
    public int KeepAliveId { get; set; }

    public KeepAlivePacket() { }
    public KeepAlivePacket(int keepAliveId) => KeepAliveId = keepAliveId;

    public void Read(MinecraftReader r, PacketDirection d) => KeepAliveId = r.ReadInt();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteInt(KeepAliveId);
}

/// <summary>
/// 0x01 Login Request (both directions, identical layout).
/// Fields: int ProtocolVersion/EntityId, string16 Username (max 16), long MapSeed, int ServerMode,
/// byte Dimension, byte Difficulty, ubyte WorldHeight, ubyte MaxPlayers.
/// <para>C→S: ProtocolVersion = 17, Username = player name; every other field is ignored by the server and the
/// client sends 0 for all of them (see <see cref="ForClient"/>).</para>
/// <para>S→C: first int is the player's entity id, Username is "" (unused), MapSeed is the world seed,
/// ServerMode 0 = survival / 1 = creative, Dimension 0 = overworld / -1 = nether, Difficulty 0..3 (vanilla sends
/// the spawnMonsters value), WorldHeight = 128 (sent as byte -128, read unsigned), MaxPlayers = server's max players.</para>
/// <para>Full offline-mode login sequence: see <see cref="ProtocolInfo"/>.</para>
/// </summary>
public sealed class LoginRequestPacket : IPacket
{
    public const byte PacketId = 0x01;
    public byte Id => PacketId;

    /// <summary>The first int: protocol version (C→S) or entity id (S→C). See <see cref="ProtocolVersion"/> / <see cref="EntityId"/>.</summary>
    public int ProtocolVersionOrEntityId { get; set; }
    public int ProtocolVersion { get => ProtocolVersionOrEntityId; set => ProtocolVersionOrEntityId = value; }
    public int EntityId { get => ProtocolVersionOrEntityId; set => ProtocolVersionOrEntityId = value; }
    public string Username { get; set; } = string.Empty;
    public long MapSeed { get; set; }
    public int ServerMode { get; set; }
    public sbyte Dimension { get; set; }
    public sbyte Difficulty { get; set; }
    public byte WorldHeight { get; set; }
    public byte MaxPlayers { get; set; }

    /// <summary>The login request a vanilla b1.8.1 client sends: protocol 17, the username, all other fields zero.</summary>
    public static LoginRequestPacket ForClient(string username) =>
        new() { ProtocolVersion = ProtocolInfo.ProtocolVersion, Username = username };

    public void Read(MinecraftReader r, PacketDirection d)
    {
        ProtocolVersionOrEntityId = r.ReadInt();
        Username = r.ReadString16();
        MapSeed = r.ReadLong();
        ServerMode = r.ReadInt();
        Dimension = r.ReadSByte();
        Difficulty = r.ReadSByte();
        WorldHeight = r.ReadUByte();
        MaxPlayers = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteInt(ProtocolVersionOrEntityId);
        w.WriteString16(Username, d == PacketDirection.ServerBound ? 16 : short.MaxValue);
        w.WriteLong(MapSeed);
        w.WriteInt(ServerMode);
        w.WriteSByte(Dimension);
        w.WriteSByte(Difficulty);
        w.WriteUByte(WorldHeight);
        w.WriteUByte(MaxPlayers);
    }
}

/// <summary>
/// 0x02 Handshake (both directions). Fields: string16 (vanilla max 32).
/// C→S: the username. S→C: connection hash — "-" for an offline-mode server (no authentication), otherwise a hex
/// string the client must submit to the session server before sending 0x01.
/// </summary>
public sealed class HandshakePacket : IPacket
{
    public const byte PacketId = 0x02;
    public byte Id => PacketId;

    /// <summary>Username (C→S) or connection hash (S→C).</summary>
    public string Text { get; set; } = string.Empty;
    public string Username { get => Text; set => Text = value; }
    public string ConnectionHash { get => Text; set => Text = value; }

    /// <summary>True if the server's hash says no authentication is needed.</summary>
    public bool IsOfflineMode => Text == "-";

    public HandshakePacket() { }
    public HandshakePacket(string text) => Text = text;

    public void Read(MinecraftReader r, PacketDirection d) => Text = r.ReadString16();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteString16(Text, 32);
}

/// <summary>
/// 0x03 Chat Message (both directions). Fields: string16 Message.
/// C→S: max 100 chars (server kicks for longer: "Chat message too long"); a leading '/' is a command.
/// S→C: vanilla max 119 chars, may contain '§' colour codes.
/// </summary>
public sealed class ChatPacket : IPacket
{
    public const byte PacketId = 0x03;
    public const int MaxServerBoundLength = 100;
    public const int MaxClientBoundLength = 119;
    public byte Id => PacketId;
    public string Message { get; set; } = string.Empty;

    /// <summary>Where a 1.12.2 server shows the line (SPacketChat type): 0 chat box, 1 system message, 2 game info (above the
    /// hotbar: not chat). b1.8.1 lines are always <see cref="PositionChat"/>.</summary>
    public sbyte Position { get; set; }
    public const sbyte PositionChat = 0, PositionSystem = 1, PositionGameInfo = 2;

    /// <summary>The JSON component a 1.12.2 server sent (null on b1.8.1); <see cref="Message"/> is its legacy text.</summary>
    public string? Json { get; set; }

    public ChatPacket() { }
    public ChatPacket(string message) => Message = message;

    public void Read(MinecraftReader r, PacketDirection d) => Message = r.ReadString16();

    public void Write(MinecraftWriter w, PacketDirection d) =>
        w.WriteString16(Message, d == PacketDirection.ServerBound ? MaxServerBoundLength : short.MaxValue);
}

/// <summary>
/// 0x09 Respawn (both directions, identical layout).
/// Fields: byte Dimension, byte Difficulty, byte GameMode (0 survival, 1 creative), short WorldHeight (128), long MapSeed.
/// C→S: sent by the client after death to request a respawn (server ignores the field values; send the values from login).
/// S→C: sent on respawn / dimension change; the client must clear its world.
/// </summary>
public sealed class RespawnPacket : IPacket
{
    public const byte PacketId = 0x09;
    public byte Id => PacketId;
    public sbyte Dimension { get; set; }
    public sbyte Difficulty { get; set; }
    public sbyte GameMode { get; set; }
    public short WorldHeight { get; set; } = 128;
    public long MapSeed { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Dimension = r.ReadSByte();
        Difficulty = r.ReadSByte();
        GameMode = r.ReadSByte();
        WorldHeight = r.ReadShort();
        MapSeed = r.ReadLong();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(Dimension);
        w.WriteSByte(Difficulty);
        w.WriteSByte(GameMode);
        w.WriteShort(WorldHeight);
        w.WriteLong(MapSeed);
    }
}

/// <summary>
/// 0xFE Server List Ping (C→S only, no fields). The server answers with 0xFF whose reason is
/// "motd§onlinePlayers§maxPlayers" and closes the connection.
/// </summary>
public sealed class ServerListPingPacket : IPacket
{
    public const byte PacketId = 0xFE;
    public byte Id => PacketId;
    public void Read(MinecraftReader r, PacketDirection d) { }
    public void Write(MinecraftWriter w, PacketDirection d) { }
}

/// <summary>0xFF Disconnect / Kick (both directions). Fields: string16 Reason (vanilla max 100).</summary>
public sealed class KickPacket : IPacket
{
    public const byte PacketId = 0xFF;
    public byte Id => PacketId;
    public string Reason { get; set; } = string.Empty;

    public KickPacket() { }
    public KickPacket(string reason) => Reason = reason;

    public void Read(MinecraftReader r, PacketDirection d) => Reason = r.ReadString16();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteString16(Reason);
}
