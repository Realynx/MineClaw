using System.Collections.Frozen;
using System.Net.Sockets;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Versioning;

namespace MineClaw.Protocol.Release1122;

/// <summary>Wire packet ids of protocol 340 (decompiled 1.12.2 <c>EnumConnectionState</c>: ids are registration order).</summary>
public static class Ids1122
{
    // handshaking / login
    public const int Handshake = 0x00, LoginStart = 0x00;
    public const int LoginDisconnect = 0x00, EncryptionRequest = 0x01, LoginSuccess = 0x02, SetCompression = 0x03;
    // play, clientbound
    public const int SpawnObject = 0x00, SpawnPlayer = 0x05, EntityAnimation = 0x06, BlockBreakAnim = 0x08, UpdateBlockEntity = 0x09, BlockAction = 0x0A,
        BlockChange = 0x0B, Difficulty = 0x0D, Chat = 0x0F, MultiBlockChange = 0x10, ConfirmTransaction = 0x11, CloseWindow = 0x12,
        OpenWindow = 0x13, WindowItems = 0x14, WindowProperty = 0x15, SetSlot = 0x16, Disconnect = 0x1A, EntityStatus = 0x1B, Explosion = 0x1C,
        UnloadChunk = 0x1D, ChangeGameState = 0x1E, KeepAlive = 0x1F, ChunkData = 0x20, JoinGame = 0x23, EntityRelMove = 0x26,
        EntityLookRelMove = 0x27, EntityLook = 0x28, OpenSignEditor = 0x2A, PlayerListItem = 0x2E, PlayerPosLook = 0x2F,
        DestroyEntities = 0x32, Respawn = 0x35, HeldItemChangeIn = 0x3A, EntityMetadata = 0x3C, EntityEquipment = 0x3F,
        UpdateHealth = 0x41, SpawnPosition = 0x46, TimeUpdate = 0x47, SoundEffect = 0x49, CollectItem = 0x4B,
        EntityTeleport = 0x4C;
    // play, serverbound
    public const int TeleportConfirm = 0x00, ChatOut = 0x02, ClientStatus = 0x03, ClientSettings = 0x04, ConfirmTransactionOut = 0x05,
        EnchantItem = 0x06, ClickWindow = 0x07, CloseWindowOut = 0x08, CustomPayload = 0x09, UseEntity = 0x0A, KeepAliveOut = 0x0B, Player = 0x0C,
        PlayerPosition = 0x0D, PlayerPositionRotation = 0x0E, PlayerRotation = 0x0F, PlayerDigging = 0x14, EntityAction = 0x15,
        HeldItemChange = 0x1A, UpdateSign = 0x1C, Animation = 0x1D, PlaceBlock = 0x1F, UseItem = 0x20;
    /// <summary>Spawn Object type of an item entity (<c>NetHandlerPlayClient.handleSpawnObject</c>: 2 → EntityItem).</summary>
    public const int ObjectItem = 2;
}

/// <summary>A 1.12.2 packet the translator doesn't surface (logged to <see cref="IGameConnection.TrafficLog"/> only).</summary>
public sealed class WirePacket1122(int wireId, byte[] body) : IPacket
{
    public int WireId { get; } = wireId;
    public byte[] Body { get; } = body;
    public byte Id => (byte)WireId;
    public void Read(MinecraftReader reader, PacketDirection direction) => throw new NotSupportedException();
    public void Write(MinecraftWriter writer, PacketDirection direction) => throw new NotSupportedException();
    public override string ToString() => $"1.12.2 0x{WireId:X2} ({Body.Length} bytes)";
}

/// <summary>
/// The client side of a protocol 340 (1.12.2) connection, translating to and from the canonical (b1.8.1-shaped) messages
/// (docs/design/multi-version.md, "Canonical message model"). Behaviour ported from the decompiled 1.12.2 client
/// (<c>NetHandlerLoginClient</c>, <c>NetHandlerPlayClient</c>, <c>EntityPlayerSP.onUpdateWalkingPlayer</c>):
/// <list type="bullet">
/// <item>Login, inside the connection: the canonical C→S <see cref="HandshakePacket"/> (the username) becomes Handshake(340,
/// host, port, next state 2) + Login Start; Set Compression switches the frame codec; Login Success enters play; a login
/// Disconnect or an Encryption Request (online mode) surfaces as a <see cref="KickPacket"/>.</item>
/// <item>Answered here and never surfaced: Keep Alive (echoes the long id), the teleport confirm of every Player Position And
/// Look (sent at once, like handlePlayerPosLook), and Client Settings + the MC|Brand payload after Join Game.</item>
/// <item>Surfaced as canonical messages: Join Game → <see cref="LoginRequestPacket"/>; Respawn; Player Position And Look
/// (relative flags resolved against the last known position) → absolute <see cref="PlayerPositionLookPacket"/>; Chat (JSON
/// flattened by <see cref="ChatComponent"/>); Disconnect → <see cref="KickPacket"/>; Time; Health (float → ceiling);
/// Spawn Position; Change Game State (rain, game mode); Player List add/remove (uuid → name kept here); Chunk Data →
/// <see cref="ChunkSectionsMessage"/>; Unload Chunk → <see cref="PreChunkPacket"/> (Load false); Block Change / Multi Block
/// Change (state = id &lt;&lt; 4 | meta).</item>
/// <item>Sent: chat (≤ 256), the four movement packets (stance dropped; a bare on-ground packet only when on-ground changed,
/// as 1.12.2's onUpdateWalkingPlayer does), entity action (renumbered), respawn (Client Status 0), held item, arm swing.
/// P2 (<c>Release1122Connection.Play2.cs</c>): windows, clicks with their click type and the confirm flow, digging, placing
/// with hand and cursor, use item, use entity, signs; item entities (Spawn Object 2 + its metadata), players, their moves,
/// equipment, animations, statuses, collects and destroys; another player's Block Break Animation.</item>
/// </list>
/// Threading: one reader thread calls <see cref="ReadPacket"/>; <see cref="Send"/> is thread-safe (frames are encoded and
/// written under one lock, which the reader also takes for its automatic replies).
/// </summary>
public sealed partial class Release1122Connection : IGameConnection
{
    public const int ProtocolVersion = 340;
    public const int MaxChatLength = 256;

    private enum State { Handshake, Login, Play }

    private readonly Stream _stream;
    private readonly BufferedStream _in;
    private readonly TcpClient? _tcp;
    private readonly bool _ownsStream;
    private readonly object _sendLock = new();
    private readonly Framing _framing = new();
    private readonly Dictionary<Guid, string> _names = new();
    private volatile State _state = State.Handshake;
    private volatile bool _disposed;
    private int _dimension;
    private int _selfId = int.MinValue;
    // The player's position and look as the server last knew it (teleports in, movement out): what relative flags add to.
    private double _x, _y, _z;
    private float _yaw, _pitch;
    private bool? _lastOnGround;

    public Release1122Connection(Stream stream, bool ownsStream = true) : this(stream, null, ownsStream) { }

    private Release1122Connection(Stream stream, TcpClient? tcp, bool ownsStream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _tcp = tcp;
        _ownsStream = ownsStream;
        _in = new BufferedStream(stream, 64 * 1024);
    }

    public static Release1122Connection Connect(string host, int port, TimeSpan? connectTimeout = null, int viewDistance = 8, LangTable? lang = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(connectTimeout ?? TimeSpan.FromSeconds(10));
            tcp.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return new Release1122Connection(tcp.GetStream(), tcp, ownsStream: true) { Host = host, Port = port, ViewDistance = viewDistance, Lang = lang ?? LangTable.Builtin };
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Server address the handshake names (vanilla sends what the user typed).</summary>
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    /// <summary>Client Settings view distance in chunks (vanilla's default render distance is 12; fewer columns = less memory).</summary>
    public int ViewDistance { get; init; } = 8;
    /// <summary>Translations for chat components (<see cref="LangTable.LoadDefault"/> by default).</summary>
    public LangTable Lang { get; init; } = LangTable.Builtin;
    public Action<string>? Log { get; init; }

    public Action<PacketTraffic>? TrafficLog { get; set; }
    public long PacketsReceived { get; private set; }
    public long PacketsSent { get; private set; }

    /// <summary>The compression threshold the server set (−1 = none yet).</summary>
    public int CompressionThreshold => _framing.Threshold;
    public bool InPlay => _state == State.Play;
    /// <summary>The player's UUID and name from Login Success.</summary>
    public Guid PlayerUuid { get; private set; }
    public string? PlayerName { get; private set; }

    // ------------------------------------------------------------------ reading

    public IPacket? ReadPacket()
    {
        while (true)
        {
            if (_extra.TryDequeue(out var extra)) return extra;
            Framing.PooledPayload? frame;
            try
            {
                frame = _framing.ReadPooled(_in);
            }
            catch (EndOfStreamException ex)
            {
                throw new ProtocolException("stream ended mid-frame (1.12.2)", ex);
            }
            using var lease = frame;
            if (lease is null) return null;
            var payload = lease.Memory;
            PacketsReceived++;
            var r = new WireReader(payload.Span);
            int id = r.ReadVarInt();
            var body = payload.Span[r.Position..];
            IPacket? canonical;
            try
            {
                canonical = _state == State.Play ? TranslatePlay(id, body) : TranslateLogin(id, body);
            }
            catch (ProtocolException ex)
            {
                throw new ProtocolException($"1.12.2 {_state} packet 0x{id:X2} ({body.Length} bytes): {ex.Message}", ex);
            }
            TrafficLog?.Invoke(new PacketTraffic(PacketDirection.ClientBound, canonical ?? new WirePacket1122(id, body.ToArray()), payload.ToArray()));
            if (canonical is not null) return canonical;
        }
    }

    private IPacket? TranslateLogin(int id, ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        switch (id)
        {
            case Ids1122.LoginDisconnect:
                return new KickPacket(ChatComponent.ToPlain(r.ReadString(), Lang));
            case Ids1122.EncryptionRequest:
                return new KickPacket("server requires online-mode authentication");
            case Ids1122.SetCompression:
            {
                int threshold = r.ReadVarInt();
                lock (_sendLock) _framing.Threshold = threshold;
                return null;
            }
            case Ids1122.LoginSuccess:
            {
                var uuid = r.ReadString(36);
                PlayerName = r.ReadString(16);
                PlayerUuid = Guid.TryParse(uuid, out var g) ? g : Guid.Empty;
                _state = State.Play;
                return null;
            }
            default:
                throw new ProtocolException($"unexpected login packet 0x{id:X2}");
        }
    }

    /// <summary>A play packet → its canonical message (null = answered or recorded only): the translator registered for its id
    /// (<c>Release1122Connection.Dispatch.cs</c>).</summary>
    private IPacket? TranslatePlay(int id, ReadOnlySpan<byte> body) =>
        (uint)id < (uint)PlayTranslators.Length && PlayTranslators[id] is { } translate ? translate(this, body) : null;

    private IPacket? TranslateKeepAlive(ReadOnlySpan<byte> body)
    {
        long ka = new WireReader(body).ReadLong();
        SendWire(WireWriter.ForPacket(Ids1122.KeepAliveOut).WriteLong(ka));
        return null;
    }

    private IPacket? TranslateJoinGame(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int eid = r.ReadInt();
        _selfId = eid;
        int mode = r.ReadUByte();
        _dimension = r.ReadInt();
        int difficulty = r.ReadUByte();
        int maxPlayers = r.ReadUByte();
        r.ReadString(16); // level type
        r.ReadBool(); // reduced debug info
        // handleJoinGame → gameSettings.sendSettingsToServer, then the MC|Brand payload.
        SendWire(WireWriter.ForPacket(Ids1122.ClientSettings).WriteString("en_us", 16).WriteSByte((sbyte)ViewDistance)
            .WriteVarInt(0).WriteBool(true).WriteUByte(0x7F).WriteVarInt(1));
        var brand = new WireWriter().WriteString("vanilla");
        SendWire(WireWriter.ForPacket(Ids1122.CustomPayload).WriteString("MC|Brand", 20).WriteBytes(brand.Span));
        return new LoginRequestPacket
        {
            EntityId = eid, ServerMode = mode & ~8, Dimension = (sbyte)_dimension, Difficulty = (sbyte)difficulty,
            MaxPlayers = (byte)maxPlayers,
        };
    }

    private IPacket? TranslateRespawn(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        ResetEntities();
        _dimension = r.ReadInt();
        int difficulty = r.ReadUByte(), mode = r.ReadUByte();
        return new RespawnPacket { Dimension = (sbyte)_dimension, Difficulty = (sbyte)difficulty, GameMode = (sbyte)(mode & 7), WorldHeight = 256 };
    }

    private IPacket? TranslatePlayerPosLook(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
        float yaw = r.ReadFloat(), pitch = r.ReadFloat();
        int flags = r.ReadUByte();
        int teleportId = r.ReadVarInt();
        // handlePlayerPosLook: each set flag adds the player's current value.
        x = Relative(x, flags, 0x01, _x);
        y = Relative(y, flags, 0x02, _y);
        z = Relative(z, flags, 0x04, _z);
        pitch = Relative(pitch, flags, 0x10, _pitch);
        yaw = Relative(yaw, flags, 0x08, _yaw);
        (_x, _y, _z, _yaw, _pitch) = (x, y, z, yaw, pitch);
        SendWire(WireWriter.ForPacket(Ids1122.TeleportConfirm).WriteVarInt(teleportId));
        return new PlayerPositionLookPacket
        {
            X = x, Y = y, Stance = y + PlayerPositionLookPacket.EyeHeight, Z = z, Yaw = yaw, Pitch = pitch, OnGround = false,
        };
    }

    /// <summary>A Player Position And Look field: relative (its flag set) adds the player's current value.</summary>
    private static double Relative(double value, int flags, int flag, double current) => (flags & flag) != 0 ? value + current : value;

    private static float Relative(float value, int flags, int flag, float current) => (flags & flag) != 0 ? value + current : value;

    private IPacket? TranslateChat(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        var json = r.ReadString();
        sbyte pos = r.ReadSByte();
        return new ChatPacket(ChatComponent.ToLegacy(json, Lang)) { Json = json, Position = pos };
    }

    private static IPacket? TranslateTimeUpdate(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        r.ReadLong(); // world age
        long time = r.ReadLong();
        return new TimeUpdatePacket { Time = time < 0 ? -time : time, Frozen = time < 0 }; // negative = doDaylightCycle off
    }

    private static IPacket? TranslateUpdateHealth(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        float hp = r.ReadFloat();
        int food = r.ReadVarInt();
        float sat = r.ReadFloat();
        return new UpdateHealthPacket
        {
            Health = (short)Math.Clamp(Math.Ceiling(hp), short.MinValue, short.MaxValue), Food = (short)food, FoodSaturation = sat, HealthExact = hp,
        };
    }

    private static IPacket? TranslateSpawnPosition(ReadOnlySpan<byte> body)
    {
        var (x, y, z) = new WireReader(body).ReadPosition();
        return new SpawnPositionPacket { X = x, Y = y, Z = z };
    }

    private static IPacket? TranslateChangeGameState(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int reason = r.ReadUByte();
        float value = r.ReadFloat();
        // WorldServer.updateWeather: 1 = rain starts, 2 = rain stops (the b1.8.1 numbering); 3 = game mode; 7/8 = the
        // rain/thunder strength fading 0..1 (the viewer's sky, light and streaks follow them; live 10-29 the viewer showed no weather).
        return reason is 0 or 1 or 2 or 3 or 7 or 8
            ? new NewStatePacket { Reason = (sbyte)reason, GameMode = (sbyte)Math.Floor(value + 0.5f), Value = value }
            : null;
    }

    private IPacket? TranslateChunkData(ReadOnlySpan<byte> body)
    {
        var chunk = ChunkData.Decode(body, hasSky: _dimension == 0, compact: true);
        foreach (var be in chunk.BlockEntities) EnqueueChunkBlockEntity(be);
        return chunk;
    }

    /// <summary>A block entity sent with its chunk: a sign's lines, or the viewer's looks of any other (beds, banners, skulls,
    /// pots, spawners, chests…); one without a position is dropped.</summary>
    private void EnqueueChunkBlockEntity(NbtCompound be)
    {
        if (Codec1122.SignLines(be, Lang) is { } lines && be.GetInt("x") is { } sx && be.GetInt("y") is { } sy && be.GetInt("z") is { } sz)
            _extra.Enqueue(new UpdateSignPacket { X = sx, Y = (short)sy, Z = sz, Lines = lines, Legacy = Codec1122.SignLegacy(be, Lang) });
        else if (BlockEntities1122.Info(be) is { } info && BlockEntities1122.PositionOf(be) is { } bp)
            _extra.Enqueue(new BlockEntityMessage { X = bp.X, Y = bp.Y, Z = bp.Z, Info = info });
    }

    private static IPacket? TranslateBlockChange(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        var (x, y, z) = r.ReadPosition();
        int state = r.ReadVarInt();
        var (bid, meta) = Split(state);
        return new BlockChangePacket { X = x, Y = y, Z = z, BlockId = bid, Metadata = meta };
    }

    private static IPacket? TranslateMultiBlockChange(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int cx = r.ReadInt(), cz = r.ReadInt();
        int n = r.ReadVarInt();
        if (n < 0 || n > r.Remaining) throw new ProtocolException($"multi block change count {n} out of range");
        var coords = new short[n];
        var ids = new byte[n];
        var metas = new byte[n];
        for (int i = 0; i < n; i++)
        {
            coords[i] = r.ReadShort(); // x << 12 | z << 8 | y: the b1.8.1 layout
            (ids[i], metas[i]) = Split(r.ReadVarInt());
        }
        return new MultiBlockChangePacket { ChunkX = cx, ChunkZ = cz, Coordinates = coords, BlockIds = ids, Metadata = metas };
    }

    /// <summary>
    /// SPacketExplosion: float x, y, z, strength, int count, count × (byte dx, dy, dz) from (int)x/(int)y/(int)z, then the
    /// player's knockback (3 floats, ignored). Same records as b1.8.1's 0x3C (doubles there): the blast takes those blocks with no
    /// block change of their own, so without it a 1.12.2 crater stayed standing in the world view.
    /// </summary>
    private static IPacket? TranslateExplosion(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat(), radius = r.ReadFloat();
        int n = r.ReadInt();
        if (n < 0 || n * 3 > r.Remaining) throw new ProtocolException($"explosion record count {n} out of range");
        var recs = new ExplosionRecord[n];
        for (int i = 0; i < n; i++) recs[i] = new ExplosionRecord(r.ReadSByte(), r.ReadSByte(), r.ReadSByte());
        return new ExplosionPacket { X = x, Y = y, Z = z, Radius = radius, Records = recs };
    }

    private static (byte Id, byte Meta) Split(int state)
    {
        int id = state >> 4;
        return (uint)id > 255 ? (ChunkData.UnknownBlock, (byte)0) : ((byte)id, (byte)(state & 15));
    }

    private IPacket? TranslatePlayerList(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int action = r.ReadVarInt();
        int n = r.ReadVarInt();
        if (n < 0 || n > r.Remaining) throw new ProtocolException($"player list count {n} out of range");
        var found = new List<PlayerListItemPacket>();
        for (int i = 0; i < n; i++)
        {
            var uuid = r.ReadUuid();
            if (PlayerListEntry(ref r, action, uuid) is { } item) found.Add(item);
        }
        return First(found);
    }

    /// <summary>One Player List Item entry: an add names the player (uuid → name kept for spawns), a remove takes it off; the
    /// game mode, latency and display name updates are read past.</summary>
    private PlayerListItemPacket? PlayerListEntry(ref WireReader r, int action, Guid uuid)
    {
        switch (action)
        {
            case 0: return PlayerListAdd(ref r, uuid);
            case 1: r.ReadVarInt(); return null;
            case 2: r.ReadVarInt(); return null;
            case 3: SkipOptionalString(ref r); return null;
            case 4: return _names.Remove(uuid, out var gone) ? new PlayerListItemPacket { PlayerName = gone, Online = false } : null;
            default: throw new ProtocolException($"player list action {action}");
        }
    }

    private PlayerListItemPacket PlayerListAdd(ref WireReader r, Guid uuid)
    {
        var name = r.ReadString(16);
        int props = r.ReadVarInt();
        for (int p = 0; p < props; p++)
        {
            r.ReadString(); r.ReadString();
            SkipOptionalString(ref r); // signature
        }
        r.ReadVarInt(); // game mode
        int ping = r.ReadVarInt();
        SkipOptionalString(ref r); // display name
        _names[uuid] = name;
        return new PlayerListItemPacket { PlayerName = name, Online = true, Ping = (short)Math.Min(ping, short.MaxValue) };
    }

    private static void SkipOptionalString(ref WireReader r)
    {
        if (r.ReadBool()) r.ReadString();
    }

    // One wire packet can carry several canonical messages (players listed, entities destroyed, signs in a chunk); the first
    // is returned, the rest come out of the next reads.
    private readonly Queue<IPacket> _extra = new();

    private IPacket? First<T>(List<T> list) where T : IPacket
    {
        for (int i = 1; i < list.Count; i++) _extra.Enqueue(list[i]);
        return list.Count > 0 ? list[0] : null;
    }

    // ------------------------------------------------------------------ sending

    /// <summary>The canonical messages with a 1.12.2 form (all sealed: an exact type lookup); an arm swing is one too.</summary>
    private static readonly FrozenSet<Type> SendableTypes = new[]
    {
        typeof(HandshakePacket), typeof(KeepAlivePacket), typeof(KickPacket), typeof(ChatPacket), typeof(PlayerOnGroundPacket),
        typeof(PlayerPositionPacket), typeof(PlayerPositionLookPacket), typeof(PlayerLookPacket), typeof(EntityActionPacket),
        typeof(RespawnPacket), typeof(HoldingChangePacket), typeof(WindowClickPacket), typeof(TransactionPacket), typeof(CloseWindowPacket),
        typeof(PlayerDiggingPacket), typeof(PlayerBlockPlacementPacket), typeof(UseEntityPacket), typeof(UpdateSignPacket),
        typeof(EnchantItemPacket),
    }.ToFrozenSet();

    public bool CanSend(IPacket packet) => packet is AnimationPacket a ? a.Animation == 1 : SendableTypes.Contains(packet.GetType());

    public void Send(IPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SendSession(packet) || SendMovement(packet) || SendPlay2(packet)) return;
        throw new NotSupportedException($"{packet.GetType().Name} is not supported on 1.12.2 yet");
    }

    /// <summary>The login, chat, respawn, entity actions, the held item and the arm swing.</summary>
    private bool SendSession(IPacket packet)
    {
        switch (packet)
        {
            case HandshakePacket hs:
                SendLogin(hs);
                return true;
            case KeepAlivePacket:
                return true; // answered inside the connection
            case KickPacket:
                return true; // 1.12.2 has no client disconnect packet: closing the socket is the quit
            case ChatPacket chat:
                SendWire(WireWriter.ForPacket(Ids1122.ChatOut).WriteString(chat.Message, MaxChatLength), packet);
                return true;
            case EntityActionPacket a:
                SendEntityAction(a);
                return true;
            case RespawnPacket:
                SendWire(WireWriter.ForPacket(Ids1122.ClientStatus).WriteVarInt(0), packet); // PERFORM_RESPAWN
                return true;
            case HoldingChangePacket h:
                SendWire(WireWriter.ForPacket(Ids1122.HeldItemChange).WriteShort(h.Slot), packet);
                return true;
            case AnimationPacket { Animation: 1 } swing:
                SendWire(WireWriter.ForPacket(Ids1122.Animation).WriteVarInt(swing.Hand), packet);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Handshake(340, host, port, next state 2) + Login Start, written together.</summary>
    private void SendLogin(HandshakePacket hs)
    {
        lock (_sendLock)
        {
            WriteLocked(WireWriter.ForPacket(Ids1122.Handshake).WriteVarInt(ProtocolVersion).WriteString(Host, 255)
                .WriteUShort((ushort)Port).WriteVarInt(2), hs);
            _state = State.Login;
            WriteLocked(WireWriter.ForPacket(Ids1122.LoginStart).WriteString(hs.Username, 16), hs);
        }
    }

    /// <summary>b1.8.1 1 crouch, 2 uncrouch, 3 leave bed, 4/5 sprint → CPacketEntityAction.Action ordinals 0..4; others aren't sent.</summary>
    private void SendEntityAction(EntityActionPacket a)
    {
        int action = a.Action is >= 1 and <= 5 ? a.Action - 1 : -1;
        if (action < 0) return;
        SendWire(WireWriter.ForPacket(Ids1122.EntityAction).WriteVarInt(a.EntityId).WriteVarInt(action).WriteVarInt(0), a);
    }

    /// <summary>The four movement packets (stance dropped), tracking what the server knows for relative teleports.</summary>
    private bool SendMovement(IPacket packet)
    {
        switch (packet)
        {
            case PlayerOnGroundPacket g:
                // onUpdateWalkingPlayer: a bare CPacketPlayer only when on-ground changed (b1.8.1 sent one every tick).
                if (_lastOnGround == g.OnGround) return true;
                _lastOnGround = g.OnGround;
                SendWire(WireWriter.ForPacket(Ids1122.Player).WriteBool(g.OnGround), packet);
                return true;
            case PlayerPositionPacket p:
                (_x, _y, _z) = (p.X, p.Y, p.Z);
                _lastOnGround = p.OnGround;
                SendWire(WireWriter.ForPacket(Ids1122.PlayerPosition).WriteDouble(p.X).WriteDouble(p.Y).WriteDouble(p.Z).WriteBool(p.OnGround), packet);
                return true;
            case PlayerPositionLookPacket p:
                (_x, _y, _z, _yaw, _pitch) = (p.X, p.Y, p.Z, p.Yaw, p.Pitch);
                _lastOnGround = p.OnGround;
                SendWire(WireWriter.ForPacket(Ids1122.PlayerPositionRotation).WriteDouble(p.X).WriteDouble(p.Y).WriteDouble(p.Z)
                    .WriteFloat(p.Yaw).WriteFloat(p.Pitch).WriteBool(p.OnGround), packet);
                return true;
            case PlayerLookPacket p:
                (_yaw, _pitch) = (p.Yaw, p.Pitch);
                _lastOnGround = p.OnGround;
                SendWire(WireWriter.ForPacket(Ids1122.PlayerRotation).WriteFloat(p.Yaw).WriteFloat(p.Pitch).WriteBool(p.OnGround), packet);
                return true;
            default:
                return false;
        }
    }

    private void SendWire(WireWriter w, IPacket? canonical = null)
    {
        lock (_sendLock) WriteLocked(w, canonical);
    }

    private void WriteLocked(WireWriter w, IPacket? canonical)
    {
        if (_disposed) return;
        var payload = w.ToArray();
        var frame = _framing.Encode(payload);
        TrafficLog?.Invoke(new PacketTraffic(PacketDirection.ServerBound, canonical ?? new WirePacket1122(new WireReader(payload).ReadVarInt(), payload), payload));
        _stream.Write(frame);
        _stream.Flush();
        PacketsSent++;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsStream)
        {
            try { _stream.Dispose(); } catch { /* ignore */ }
            _tcp?.Dispose();
        }
    }
}
