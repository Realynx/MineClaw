using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Versioning;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// The server side of a protocol 340 (1.12.2) connection over the canonical message model (docs/design/multi-version.md,
/// "How tests run both versions"): a fake server keeps its game logic on canonical messages and gets this as its wire codec
/// instead of <see cref="PacketConnection"/>. It is the inverse of <see cref="Release1122Connection"/>, written from the
/// decompiled server-side packet classes (<c>SPacket*.writePacketData</c>, <c>CPacket*.readPacketData</c>), not from the client
/// translator, so a client test over it checks both against the source.
/// <list type="bullet">
/// <item>Login: Handshake + Login Start are answered here (Set Compression with <see cref="CompressionThreshold"/>, Login
/// Success with the offline UUID) and surface as the canonical <see cref="HandshakePacket"/> (the username) followed by the
/// client's <see cref="LoginRequestPacket"/>, so b1.8.1-shaped server logic answers with its 0x01 login (→ Join Game).</item>
/// <item>Swallowed C→S: teleport confirms, client settings, plugin messages, keep-alives, the recipe book.</item>
/// <item>S→C <see cref="MapChunkPacket"/> (a b1.8.1 zlib region) is re-encoded as a paletted Chunk Data column; window 0's items
/// gain the off-hand slot 45; a <see cref="PickupSpawnPacket"/> becomes Spawn Object 2 + its Entity Metadata; a
/// <see cref="NamedEntitySpawnPacket"/> is preceded by the Player List entry that carries the name.</item>
/// <item>C→S placements carry no held item on 1.12.2: <see cref="PlayerBlockPlacementPacket.HeldItem"/> is empty and the
/// server logic must use its own copy of the hotbar.</item>
/// </list>
/// </summary>
public sealed partial class Release1122ServerConnection : IGameConnection
{
    private enum State { Handshake, Login, Play }

    private readonly Stream _stream;
    private readonly BufferedStream _in;
    private readonly bool _ownsStream;
    private readonly object _sendLock = new();
    private readonly Framing _framing = new();
    private readonly Queue<IPacket> _pending = new();
    private readonly Dictionary<int, byte> _windowTypes = new();
    private volatile State _state = State.Handshake;
    private volatile bool _disposed;
    private int _teleportId;
    // Entity classes of what was spawned (the metadata layout) and riders per vehicle (Set Passengers lists them all).
    private readonly Dictionary<int, (EntityWireKind Kind, int Type)> _kinds = new();
    private readonly Dictionary<int, List<int>> _riders = new();
    private int _dimension;

    public Release1122ServerConnection(Stream stream, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
        _in = new BufferedStream(stream, 64 * 1024);
    }

    /// <summary>Set Compression threshold sent before Login Success (vanilla: 256); −1 = no compression.</summary>
    public int CompressionThreshold { get; init; } = 256;

    public Action<PacketTraffic>? TrafficLog { get; set; }
    public long PacketsReceived { get; private set; }
    public long PacketsSent { get; private set; }
    public string? Username { get; private set; }
    /// <summary>Teleport ids the client confirmed (Teleport Confirm 0x00).</summary>
    public int LastConfirmedTeleport { get; private set; }

    // ------------------------------------------------------------------ reading (C→S)

    public IPacket? ReadPacket()
    {
        while (true)
        {
            if (_pending.TryDequeue(out var queued)) return queued;
            byte[]? payload;
            try { payload = _framing.Read(_in); }
            catch (EndOfStreamException ex) { throw new ProtocolException("stream ended mid-frame (1.12.2 server)", ex); }
            if (payload is null) return null;
            PacketsReceived++;
            var r = new WireReader(payload);
            int id = r.ReadVarInt();
            var body = payload.AsSpan(r.Position);
            var p = _state == State.Play ? ReadPlay(id, body) : ReadLogin(id, body);
            TrafficLog?.Invoke(new PacketTraffic(PacketDirection.ServerBound, p ?? new WirePacket1122(id, body.ToArray()), payload));
            if (p is not null) return p;
        }
    }

    private IPacket? ReadLogin(int id, ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        if (_state == State.Handshake)
        {
            if (id != Ids1122.Handshake) throw new ProtocolException($"expected a handshake, got 0x{id:X2}");
            int protocol = r.ReadVarInt();
            if (protocol != Release1122Connection.ProtocolVersion) throw new ProtocolException($"client speaks protocol {protocol}");
            r.ReadString(255); r.ReadUShort();
            if (r.ReadVarInt() != 2) throw new ProtocolException("handshake for status, not login");
            _state = State.Login;
            return null;
        }
        if (id != Ids1122.LoginStart) throw new ProtocolException($"expected login start, got 0x{id:X2}");
        Username = r.ReadString(16);
        lock (_sendLock)
        {
            if (CompressionThreshold >= 0)
            {
                WriteLocked(WireWriter.ForPacket(Ids1122.SetCompression).WriteVarInt(CompressionThreshold));
                _framing.Threshold = CompressionThreshold;
            }
            WriteLocked(WireWriter.ForPacket(Ids1122.LoginSuccess).WriteString(Wire.OfflineUuid(Username).ToString(), 36).WriteString(Username, 16));
            _state = State.Play;
        }
        _pending.Enqueue(LoginRequestPacket.ForClient(Username));
        return new HandshakePacket(Username);
    }

    private delegate IPacket? PlayReader(Release1122ServerConnection s, ReadOnlySpan<byte> body);

    /// <summary>Serverbound play id → its reader (<c>CPacket*.readPacketData</c>); ids with none (settings, brand, keep-alive,
    /// abilities, recipe book…) are swallowed.</summary>
    private static readonly PlayReader?[] PlayReaders = BuildPlayReaders();

    private static PlayReader?[] BuildPlayReaders()
    {
        var t = new PlayReader?[Ids1122.UseItem + 1];
        t[Ids1122.TeleportConfirm] = static (s, b) => { s.LastConfirmedTeleport = new WireReader(b).ReadVarInt(); return null; };
        t[Ids1122.ChatOut] = static (_, b) => new ChatPacket(new WireReader(b).ReadString(256));
        t[Ids1122.ClientStatus] = static (s, b) => new WireReader(b).ReadVarInt() == 0 ? new RespawnPacket { Dimension = (sbyte)s._dimension, WorldHeight = 128 } : null;
        t[Ids1122.ConfirmTransactionOut] = static (_, b) => ReadTransaction(b);
        t[Ids1122.ClickWindow] = static (_, b) => ReadClickWindow(b);
        t[Ids1122.CloseWindowOut] = static (_, b) => new CloseWindowPacket { WindowId = (sbyte)new WireReader(b).ReadUByte() };
        t[Ids1122.EnchantItem] = static (_, b) => ReadEnchantItem(b);
        t[Ids1122.UseEntity] = static (_, b) => ReadUseEntity(b);
        t[Ids1122.Player] = static (_, b) => new PlayerOnGroundPacket { OnGround = new WireReader(b).ReadBool() };
        t[Ids1122.PlayerPosition] = static (_, b) => ReadPlayerPosition(b);
        t[Ids1122.PlayerPositionRotation] = static (_, b) => ReadPlayerPositionRotation(b);
        t[Ids1122.PlayerRotation] = static (_, b) => ReadPlayerRotation(b);
        t[Ids1122.PlayerDigging] = static (_, b) => ReadDigging(b);
        t[Ids1122.EntityAction] = static (_, b) => ReadEntityAction(b);
        t[Ids1122.HeldItemChange] = static (_, b) => new HoldingChangePacket { Slot = new WireReader(b).ReadShort() };
        t[Ids1122.UpdateSign] = static (_, b) => ReadUpdateSign(b);
        t[Ids1122.Animation] = static (_, b) => new AnimationPacket { Animation = 1, Hand = (byte)new WireReader(b).ReadVarInt() };
        t[Ids1122.PlaceBlock] = static (_, b) => ReadPlaceBlock(b);
        t[Ids1122.UseItem] = static (_, b) => PlayerBlockPlacementPacket.UseItem(ItemStack.Empty, (byte)new WireReader(b).ReadVarInt());
        return t;
    }

    private IPacket? ReadPlay(int id, ReadOnlySpan<byte> body) =>
        (uint)id < (uint)PlayReaders.Length && PlayReaders[id] is { } read ? read(this, body) : null;

    private static IPacket ReadTransaction(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new TransactionPacket { WindowId = r.ReadSByte(), ActionNumber = r.ReadShort(), Accepted = r.ReadBool() };
    }

    /// <summary>CPacketClickWindow: ubyte window, short slot, byte button, short action, VarInt ClickType, slot.</summary>
    private static IPacket ReadClickWindow(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int wid = r.ReadUByte();
        short slot = r.ReadShort();
        sbyte button = r.ReadSByte();
        short action = r.ReadShort();
        byte mode = (byte)r.ReadVarInt();
        var item = Codec1122.ReadSlot(ref r);
        return new WindowClickPacket
        {
            WindowId = (sbyte)wid, Slot = slot, ActionNumber = action, Mode = mode, Button = button, ClickedItem = item,
            RightClick = mode <= 1 && button == 1, Shift = mode == WindowClickPacket.ModeQuickMove,
        };
    }

    private static IPacket ReadEnchantItem(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new EnchantItemPacket { WindowId = (sbyte)r.ReadUByte(), Enchantment = r.ReadSByte() };
    }

    /// <summary>CPacketUseEntity: 0 interact (hand), 1 attack, 2 interact at (hit vector, hand).</summary>
    private static IPacket ReadUseEntity(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int target = r.ReadVarInt();
        int type = r.ReadVarInt();
        var u = new UseEntityPacket { TargetEntityId = target, LeftClick = type == 1 };
        if (type == 2) u.InteractAt = (r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
        if (type != 1) u.Hand = (byte)r.ReadVarInt();
        return u;
    }

    private static IPacket ReadPlayerPosition(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
        return new PlayerPositionPacket { X = x, Y = y, Stance = y + PlayerPositionLookPacket.EyeHeight, Z = z, OnGround = r.ReadBool() };
    }

    private static IPacket ReadPlayerPositionRotation(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
        float yaw = r.ReadFloat(), pitch = r.ReadFloat();
        return new PlayerPositionLookPacket { X = x, Y = y, Stance = y + PlayerPositionLookPacket.EyeHeight, Z = z, Yaw = yaw, Pitch = pitch, OnGround = r.ReadBool() };
    }

    private static IPacket ReadPlayerRotation(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new PlayerLookPacket { Yaw = r.ReadFloat(), Pitch = r.ReadFloat(), OnGround = r.ReadBool() };
    }

    private static IPacket ReadDigging(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int status = r.ReadVarInt();
        var (x, y, z) = r.ReadPosition();
        return new PlayerDiggingPacket { Status = (byte)status, X = x, Y = y, Z = z, Face = r.ReadUByte() };
    }

    /// <summary>CPacketEntityAction.Action ordinals 0..4 → b1.8.1 1 crouch, 2 uncrouch, 3 leave bed, 4/5 sprint; others aren't surfaced.</summary>
    private static IPacket? ReadEntityAction(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        int eid = r.ReadVarInt();
        int action = r.ReadVarInt();
        r.ReadVarInt(); // jump boost
        return action is >= 0 and <= 4 ? new EntityActionPacket { EntityId = eid, Action = (sbyte)(action + 1) } : null;
    }

    private static IPacket ReadUpdateSign(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        var (x, y, z) = r.ReadPosition();
        var lines = new string[4];
        for (int i = 0; i < 4; i++) lines[i] = r.ReadString(384);
        return new UpdateSignPacket { X = x, Y = (short)y, Z = z, Lines = lines };
    }

    /// <summary>CPacketPlayerTryUseItemOnBlock: position, VarInt facing, VarInt hand, float cursor x/y/z.</summary>
    private static IPacket ReadPlaceBlock(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        var (x, y, z) = r.ReadPosition();
        int face = r.ReadVarInt();
        int hand = r.ReadVarInt();
        var cursor = (r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
        return new PlayerBlockPlacementPacket { X = x, Y = y, Z = z, Direction = (byte)face, Hand = (byte)hand, Cursor = cursor };
    }

    // ------------------------------------------------------------------ sending (S→C)

    public bool CanSend(IPacket packet) => true;

    /// <summary>Encodes and writes the message's frames (<c>Release1122ServerConnection.Encoders.cs</c>); one with no encoder sends nothing.</summary>
    public void Send(IPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_disposed) throw new ObjectDisposedException(nameof(Release1122ServerConnection));
        if (Encoders.TryGetValue(packet.GetType(), out var encode)) encode(this, packet);
    }

    /// <summary>Writes one frame of <paramref name="packet"/>'s encoding.</summary>
    private void Emit(WireWriter w, IPacket packet)
    {
        lock (_sendLock) WriteLocked(w, packet);
    }

    /// <summary>A b1.8.1 chunk region (whole 16-wide columns from y 0) as a full 1.12.2 Chunk Data column.</summary>
    private WireWriter ChunkFrom(ChunkRegion reg)
    {
        int cx = reg.X >> 4, cz = reg.Z >> 4;
        var sections = new int[]?[ChunkData.Sections];
        var bits = new int?[ChunkData.Sections];
        for (int s = 0; s < ChunkData.Sections && s * 16 < reg.Y + reg.SizeY; s++)
        {
            var distinct = new HashSet<int>();
            var st = SectionStates(reg, s, distinct);
            if (st is null) continue;
            distinct.Add(0);
            sections[s] = st;
            int n = distinct.Count;
            bits[s] = n <= 16 ? 4 : n <= 256 ? (int)Math.Ceiling(Math.Log2(n)) : ChunkData.GlobalBits;
        }
        var body = ChunkData.EncodeBody(cx, cz, full: true, bits, sections, hasSky: _dimension == 0, blockLight: 0, skyLight: 15);
        return WireWriter.ForPacket(Ids1122.ChunkData).WriteBytes(body);
    }

    /// <summary>Section <paramref name="s"/>'s 4096 states (index y·256 + z·16 + x, id &lt;&lt; 4 | meta) from the region, collecting
    /// the distinct ones; null when it is all air.</summary>
    private static int[]? SectionStates(ChunkRegion reg, int s, HashSet<int> distinct)
    {
        var st = new int[ChunkData.SectionCells];
        bool any = false;
        for (int y = 0; y < 16; y++)
        {
            int wy = s * 16 + y - reg.Y;
            if (wy < 0 || wy >= reg.SizeY) continue;
            for (int z = 0; z < 16 && z < reg.SizeZ; z++)
            for (int x = 0; x < 16 && x < reg.SizeX; x++)
            {
                int i = reg.Index(x, wy, z);
                int state = reg.BlockIds[i] << 4 | reg.Metadata[i] & 15;
                st[y * 256 + z * 16 + x] = state;
                any |= state != 0;
                distinct.Add(state);
            }
        }
        return any ? st : null;
    }

    private void WriteLocked(WireWriter w, IPacket? canonical = null)
    {
        if (_disposed) return;
        var payload = w.ToArray();
        var frame = _framing.Encode(payload);
        TrafficLog?.Invoke(new PacketTraffic(PacketDirection.ClientBound, canonical ?? new WirePacket1122(new WireReader(payload).ReadVarInt(), payload), payload));
        _stream.Write(frame);
        _stream.Flush();
        PacketsSent++;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsStream) try { _stream.Dispose(); } catch { /* ignore */ }
    }
}
