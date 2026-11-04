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
public sealed class Release1122ServerConnection : IGameConnection
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

    private IPacket? ReadPlay(int id, ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        switch (id)
        {
            case Ids1122.TeleportConfirm:
                LastConfirmedTeleport = r.ReadVarInt();
                return null;
            case Ids1122.ChatOut:
                return new ChatPacket(r.ReadString(256));
            case Ids1122.ClientStatus:
                return r.ReadVarInt() == 0 ? new RespawnPacket { Dimension = (sbyte)_dimension, WorldHeight = 128 } : null;
            case Ids1122.ConfirmTransactionOut:
                return new TransactionPacket { WindowId = r.ReadSByte(), ActionNumber = r.ReadShort(), Accepted = r.ReadBool() };
            case Ids1122.ClickWindow:
            {
                // CPacketClickWindow: ubyte window, short slot, byte button, short action, VarInt ClickType, slot.
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
            case Ids1122.CloseWindowOut:
                return new CloseWindowPacket { WindowId = (sbyte)r.ReadUByte() };
            case Ids1122.UseEntity:
            {
                int target = r.ReadVarInt();
                int type = r.ReadVarInt();
                var u = new UseEntityPacket { TargetEntityId = target, LeftClick = type == 1 };
                if (type == 2) u.InteractAt = (r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
                if (type != 1) u.Hand = (byte)r.ReadVarInt();
                return u;
            }
            case Ids1122.Player:
                return new PlayerOnGroundPacket { OnGround = r.ReadBool() };
            case Ids1122.PlayerPosition:
            {
                double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
                return new PlayerPositionPacket { X = x, Y = y, Stance = y + PlayerPositionLookPacket.EyeHeight, Z = z, OnGround = r.ReadBool() };
            }
            case Ids1122.PlayerPositionRotation:
            {
                double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
                float yaw = r.ReadFloat(), pitch = r.ReadFloat();
                return new PlayerPositionLookPacket { X = x, Y = y, Stance = y + PlayerPositionLookPacket.EyeHeight, Z = z, Yaw = yaw, Pitch = pitch, OnGround = r.ReadBool() };
            }
            case Ids1122.PlayerRotation:
                return new PlayerLookPacket { Yaw = r.ReadFloat(), Pitch = r.ReadFloat(), OnGround = r.ReadBool() };
            case Ids1122.PlayerDigging:
            {
                int status = r.ReadVarInt();
                var (x, y, z) = r.ReadPosition();
                return new PlayerDiggingPacket { Status = (byte)status, X = x, Y = y, Z = z, Face = r.ReadUByte() };
            }
            case Ids1122.EntityAction:
            {
                int eid = r.ReadVarInt();
                int action = r.ReadVarInt();
                r.ReadVarInt(); // jump boost
                sbyte canonical = action switch { 0 => 1, 1 => 2, 2 => 3, 3 => 4, 4 => 5, _ => 0 };
                return canonical == 0 ? null : new EntityActionPacket { EntityId = eid, Action = canonical };
            }
            case Ids1122.HeldItemChange:
                return new HoldingChangePacket { Slot = r.ReadShort() };
            case Ids1122.UpdateSign:
            {
                var (x, y, z) = r.ReadPosition();
                var lines = new string[4];
                for (int i = 0; i < 4; i++) lines[i] = r.ReadString(384);
                return new UpdateSignPacket { X = x, Y = (short)y, Z = z, Lines = lines };
            }
            case Ids1122.Animation:
                return new AnimationPacket { Animation = 1, Hand = (byte)r.ReadVarInt() };
            case Ids1122.PlaceBlock:
            {
                // CPacketPlayerTryUseItemOnBlock: position, VarInt facing, VarInt hand, float cursor x/y/z.
                var (x, y, z) = r.ReadPosition();
                int face = r.ReadVarInt();
                int hand = r.ReadVarInt();
                var cursor = (r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
                return new PlayerBlockPlacementPacket { X = x, Y = y, Z = z, Direction = (byte)face, Hand = (byte)hand, Cursor = cursor };
            }
            case Ids1122.UseItem:
                return PlayerBlockPlacementPacket.UseItem(ItemStack.Empty, (byte)r.ReadVarInt());
            default:
                return null; // settings, brand, keep-alive, abilities, recipe book…
        }
    }

    // ------------------------------------------------------------------ sending (S→C)

    public bool CanSend(IPacket packet) => true;

    public void Send(IPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_disposed) throw new ObjectDisposedException(nameof(Release1122ServerConnection));
        foreach (var w in Encode(packet))
            lock (_sendLock) WriteLocked(w, packet);
    }

    private IEnumerable<WireWriter> Encode(IPacket packet)
    {
        switch (packet)
        {
            case HandshakePacket:
                yield break; // the login is answered on read
            case LoginRequestPacket l:
                _dimension = l.Dimension;
                yield return WireWriter.ForPacket(Ids1122.JoinGame).WriteInt(l.EntityId).WriteUByte((byte)l.ServerMode).WriteInt(l.Dimension)
                    .WriteUByte((byte)l.Difficulty).WriteUByte(l.MaxPlayers).WriteString("default", 16).WriteBool(false);
                break;
            case KeepAlivePacket ka:
                yield return WireWriter.ForPacket(Ids1122.KeepAlive).WriteLong(ka.KeepAliveId);
                break;
            case KickPacket k:
                yield return WireWriter.ForPacket(Ids1122.Disconnect).WriteString(ChatComponent.Text(k.Reason));
                break;
            case ChatPacket c:
                yield return WireWriter.ForPacket(Ids1122.Chat).WriteString(c.Json ?? ChatComponent.Text(c.Message)).WriteSByte(c.Position);
                break;
            case TimeUpdatePacket t:
                yield return WireWriter.ForPacket(Ids1122.TimeUpdate).WriteLong(t.Time).WriteLong(t.Frozen ? -t.Time : t.Time);
                break;
            case SpawnPositionPacket sp:
                yield return WireWriter.ForPacket(Ids1122.SpawnPosition).WritePosition(sp.X, sp.Y, sp.Z);
                break;
            case UpdateHealthPacket h:
                yield return WireWriter.ForPacket(Ids1122.UpdateHealth).WriteFloat(h.HealthExact ?? h.Health).WriteVarInt(h.Food).WriteFloat(h.FoodSaturation);
                break;
            case PlayerPositionLookPacket p:
                yield return WireWriter.ForPacket(Ids1122.PlayerPosLook).WriteDouble(p.X).WriteDouble(p.Y).WriteDouble(p.Z).WriteFloat(p.Yaw)
                    .WriteFloat(p.Pitch).WriteUByte(0).WriteVarInt(++_teleportId);
                break;
            case RespawnPacket rp:
                _dimension = rp.Dimension;
                yield return WireWriter.ForPacket(Ids1122.Respawn).WriteInt(rp.Dimension).WriteUByte((byte)rp.Difficulty).WriteUByte((byte)rp.GameMode)
                    .WriteString("default", 16);
                break;
            case NewStatePacket ns:
                yield return WireWriter.ForPacket(Ids1122.ChangeGameState).WriteUByte((byte)ns.Reason)
                    .WriteFloat(ns.Reason is NewStatePacket.ReasonRainLevel or NewStatePacket.ReasonThunderLevel ? ns.Value : ns.GameMode);
                break;
            case PlayerListItemPacket pl:
            {
                var w = WireWriter.ForPacket(Ids1122.PlayerListItem).WriteVarInt(pl.Online ? 0 : 4).WriteVarInt(1).WriteUuid(Wire.OfflineUuid(pl.PlayerName));
                if (pl.Online) w.WriteString(pl.PlayerName, 16).WriteVarInt(0).WriteVarInt(0).WriteVarInt(pl.Ping).WriteBool(false);
                yield return w;
                break;
            }
            case PreChunkPacket pc:
                if (!pc.Load) yield return WireWriter.ForPacket(Ids1122.UnloadChunk).WriteInt(pc.ChunkX).WriteInt(pc.ChunkZ);
                break;
            case MapChunkPacket mc:
                yield return ChunkFrom(ChunkDataDecoder.Decode(mc));
                break;
            case BlockChangePacket bc:
                yield return WireWriter.ForPacket(Ids1122.BlockChange).WritePosition(bc.X, bc.Y, bc.Z).WriteVarInt(bc.BlockId << 4 | bc.Metadata & 15);
                break;
            case MultiBlockChangePacket mb:
            {
                var w = WireWriter.ForPacket(Ids1122.MultiBlockChange).WriteInt(mb.ChunkX).WriteInt(mb.ChunkZ).WriteVarInt(mb.Coordinates.Length);
                for (int i = 0; i < mb.Coordinates.Length; i++) w.WriteShort(mb.Coordinates[i]).WriteVarInt(mb.BlockIds[i] << 4 | mb.Metadata[i] & 15);
                yield return w;
                break;
            }
            case ExplosionPacket ex:
            {
                var w = WireWriter.ForPacket(Ids1122.Explosion).WriteFloat((float)ex.X).WriteFloat((float)ex.Y).WriteFloat((float)ex.Z).WriteFloat(ex.Radius)
                    .WriteInt(ex.Records.Length);
                foreach (var rec in ex.Records) w.WriteSByte(rec.DX).WriteSByte(rec.DY).WriteSByte(rec.DZ);
                yield return w.WriteFloat(0).WriteFloat(0).WriteFloat(0); // the player's knockback
                break;
            }
            case OpenWindowPacket ow:
            {
                var (type, slots) = Codec1122.WindowTypeName(ow.InventoryType, ow.SlotCount);
                _windowTypes[(byte)ow.WindowId] = ow.InventoryType;
                yield return WireWriter.ForPacket(Ids1122.OpenWindow).WriteUByte((byte)ow.WindowId).WriteString(type, 32)
                    .WriteString(ChatComponent.Text(ow.Title)).WriteUByte((byte)slots);
                break;
            }
            case CloseWindowPacket cw:
                _windowTypes.Remove((byte)cw.WindowId);
                yield return WireWriter.ForPacket(Ids1122.CloseWindow).WriteUByte((byte)cw.WindowId);
                break;
            case WindowItemsPacket wi:
            {
                var items = wi.Items;
                if (wi.WindowId == 0 && items.Length == 45) items = [.. items, ItemStack.Empty]; // + the off hand (ContainerPlayer slot 45)
                var w = WireWriter.ForPacket(Ids1122.WindowItems).WriteUByte((byte)wi.WindowId).WriteShort((short)items.Length);
                foreach (var it in items) w.WriteSlot(it);
                yield return w;
                break;
            }
            case SetSlotPacket ss:
                yield return WireWriter.ForPacket(Ids1122.SetSlot).WriteSByte(ss.WindowId).WriteShort(ss.Slot).WriteSlot(ss.Item);
                break;
            case UpdateProgressBarPacket pb:
            {
                int prop = _windowTypes.TryGetValue((byte)pb.WindowId, out byte t) && t == 2 ? Codec1122.FurnacePropertyFromCanonical(pb.ProgressBar) : pb.ProgressBar;
                yield return WireWriter.ForPacket(Ids1122.WindowProperty).WriteUByte((byte)pb.WindowId).WriteShort((short)prop).WriteShort(pb.Value);
                break;
            }
            case TransactionPacket tx:
                yield return WireWriter.ForPacket(Ids1122.ConfirmTransaction).WriteSByte(tx.WindowId).WriteShort(tx.ActionNumber).WriteBool(tx.Accepted);
                break;
            case UpdateSignPacket sign:
            {
                var w = WireWriter.ForPacket(Ids1122.UpdateBlockEntity).WritePosition(sign.X, sign.Y, sign.Z).WriteUByte(Codec1122.SignAction);
                Nbt.WriteRoot(w, Codec1122.SignTag(sign.X, sign.Y, sign.Z, sign.Lines, sign.Json));
                yield return w;
                break;
            }
            case BlockEntityMessage be when be.Info is { } info:
            {
                var w = WireWriter.ForPacket(Ids1122.UpdateBlockEntity).WritePosition(be.X, be.Y, be.Z).WriteUByte((byte)BlockEntities1122.ActionOf(info.Kind));
                Nbt.WriteRoot(w, BlockEntities1122.Tag(be.X, be.Y, be.Z, info));
                yield return w;
                break;
            }
            case BlockBreakAnimationMessage bb:
                yield return WireWriter.ForPacket(Ids1122.BlockBreakAnim).WriteVarInt(bb.EntityId).WritePosition(bb.X, bb.Y, bb.Z).WriteSByte((sbyte)bb.Stage);
                break;
            case PickupSpawnPacket ps:
            {
                _kinds[ps.EntityId] = (EntityWireKind.Object, Ids1122.ObjectItem);
                var (px, py, pz) = ps.Exact ?? (FixedPoint.FromAbsolute(ps.X), FixedPoint.FromAbsolute(ps.Y), FixedPoint.FromAbsolute(ps.Z));
                // EntityTrackerEntry.createSpawnPacket: SPacketSpawnObject(item, 2, 1), then the tracked data (the stack).
                yield return WireWriter.ForPacket(Ids1122.SpawnObject).WriteVarInt(ps.EntityId).WriteUuid(Guid.NewGuid()).WriteSByte(Ids1122.ObjectItem)
                    .WriteDouble(px).WriteDouble(py).WriteDouble(pz)
                    .WriteUByte(0).WriteUByte(0).WriteInt(1)
                    .WriteShort(Velocity(ps.MotionX)).WriteShort(Velocity(ps.MotionY)).WriteShort(Velocity(ps.MotionZ));
                yield return WireWriter.ForPacket(Ids1122.EntityMetadata).WriteVarInt(ps.EntityId)
                    .WriteItemMetadata(new ItemStack(ps.ItemId, ps.Count, ps.Damage));
                break;
            }
            case NamedEntitySpawnPacket ne:
            {
                var uuid = Wire.OfflineUuid(ne.PlayerName);
                _kinds[ne.EntityId] = (EntityWireKind.Player, 0);
                var (nx, ny, nz) = ne.Exact ?? (FixedPoint.FromAbsolute(ne.X), FixedPoint.FromAbsolute(ne.Y), FixedPoint.FromAbsolute(ne.Z));
                yield return WireWriter.ForPacket(Ids1122.PlayerListItem).WriteVarInt(0).WriteVarInt(1).WriteUuid(uuid).WriteString(ne.PlayerName, 16)
                    .WriteVarInt(0).WriteVarInt(0).WriteVarInt(1).WriteBool(false);
                yield return WireWriter.ForPacket(Ids1122.SpawnPlayer).WriteVarInt(ne.EntityId).WriteUuid(uuid)
                    .WriteDouble(nx).WriteDouble(ny).WriteDouble(nz)
                    .WriteUByte(ne.Yaw).WriteUByte(ne.Pitch).WriteUByte(0xFF);
                break;
            }
            case EntityTeleportPacket et:
            {
                var (tx, ty, tz) = et.Exact ?? (FixedPoint.FromAbsolute(et.X), FixedPoint.FromAbsolute(et.Y), FixedPoint.FromAbsolute(et.Z));
                yield return WireWriter.ForPacket(Ids1122.EntityTeleport).WriteVarInt(et.EntityId).WriteDouble(tx).WriteDouble(ty).WriteDouble(tz)
                    .WriteUByte(et.Yaw).WriteUByte(et.Pitch).WriteBool(true);
                break;
            }
            case EntityLookRelativeMovePacket lrm:
                yield return WireWriter.ForPacket(Ids1122.EntityLookRelMove).WriteVarInt(lrm.EntityId)
                    .WriteShort((short)(lrm.DX * 128)).WriteShort((short)(lrm.DY * 128)).WriteShort((short)(lrm.DZ * 128))
                    .WriteUByte(lrm.Yaw).WriteUByte(lrm.Pitch).WriteBool(true);
                break;
            case EntityRelativeMovePacket rm:
                yield return WireWriter.ForPacket(Ids1122.EntityRelMove).WriteVarInt(rm.EntityId)
                    .WriteShort((short)(rm.DX * 128)).WriteShort((short)(rm.DY * 128)).WriteShort((short)(rm.DZ * 128)).WriteBool(true);
                break;
            case EntityLookPacket el:
                yield return WireWriter.ForPacket(Ids1122.EntityLook).WriteVarInt(el.EntityId).WriteUByte(el.Yaw).WriteUByte(el.Pitch).WriteBool(true);
                break;
            case DestroyEntityPacket de:
                _kinds.Remove(de.EntityId);
                yield return WireWriter.ForPacket(Ids1122.DestroyEntities).WriteVarInt(1).WriteVarInt(de.EntityId);
                break;
            case CollectItemPacket ci:
                yield return WireWriter.ForPacket(Ids1122.CollectItem).WriteVarInt(ci.CollectedEntityId).WriteVarInt(ci.CollectorEntityId).WriteVarInt(1);
                break;
            case EntityStatusPacket st:
                yield return WireWriter.ForPacket(Ids1122.EntityStatus).WriteInt(st.EntityId).WriteSByte(st.Status);
                break;
            case AnimationPacket an:
            {
                int anim = an.Animation switch { 1 => an.Hand == 1 ? 3 : 0, 2 => 1, 3 => 2, 6 => 4, 7 => 5, _ => -1 };
                if (anim >= 0) yield return WireWriter.ForPacket(Ids1122.EntityAnimation).WriteVarInt(an.EntityId).WriteUByte((byte)anim);
                break;
            }
            case EntityEquipmentPacket eq:
            {
                int slot = eq.Slot switch { 0 => 0, 1 => 2, 2 => 3, 3 => 4, 4 => 5, 5 => 1, _ => -1 };
                if (slot >= 0)
                    yield return WireWriter.ForPacket(Ids1122.EntityEquipment).WriteVarInt(eq.EntityId).WriteVarInt(slot)
                        .WriteSlot(eq.Item);
                break;
            }
            default:
                foreach (var w in EncodeEntities(packet)) yield return w;
                break;
        }
    }

    /// <summary>P4: mobs, objects, orbs, lightning, typed metadata per class, velocity, head look, effects, attributes, passengers,
    /// the held-item change, experience, the death message and item cooldowns (<c>SPacket*.writePacketData</c>).</summary>
    private IEnumerable<WireWriter> EncodeEntities(IPacket packet)
    {
        switch (packet)
        {
            case MobSpawnPacket ms:
            {
                _kinds[ms.EntityId] = (EntityWireKind.Mob, ms.Type);
                var (x, y, z) = ms.Exact ?? (FixedPoint.FromAbsolute(ms.X), FixedPoint.FromAbsolute(ms.Y), FixedPoint.FromAbsolute(ms.Z));
                yield return WireWriter.ForPacket(EntityIds1122.SpawnMob).WriteVarInt(ms.EntityId).WriteUuid(Guid.NewGuid()).WriteVarInt(ms.Type)
                    .WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteUByte(ms.Yaw).WriteUByte(ms.Pitch).WriteUByte(ms.Yaw)
                    .WriteShort(0).WriteShort(0).WriteShort(0)
                    .WriteMetadata(EntityMeta1122.FromCanonical(EntityWireKind.Mob, ms.Type, ms.Metadata));
                break;
            }
            case AddObjectVehiclePacket ao:
            {
                _kinds[ao.EntityId] = (EntityWireKind.Object, ao.Type);
                var (x, y, z) = ao.Exact ?? (FixedPoint.FromAbsolute(ao.X), FixedPoint.FromAbsolute(ao.Y), FixedPoint.FromAbsolute(ao.Z));
                yield return WireWriter.ForPacket(Ids1122.SpawnObject).WriteVarInt(ao.EntityId).WriteUuid(Guid.NewGuid()).WriteUByte(ao.Type)
                    .WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteUByte(0).WriteUByte(0).WriteInt(ao.ThrowerEntityId)
                    .WriteShort(ao.SpeedX).WriteShort(ao.SpeedY).WriteShort(ao.SpeedZ);
                break;
            }
            case ExperienceOrbPacket orb:
            {
                _kinds[orb.EntityId] = (EntityWireKind.Orb, 0);
                var (x, y, z) = orb.Exact ?? (FixedPoint.FromAbsolute(orb.X), FixedPoint.FromAbsolute(orb.Y), FixedPoint.FromAbsolute(orb.Z));
                yield return WireWriter.ForPacket(EntityIds1122.SpawnXpOrb).WriteVarInt(orb.EntityId).WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteShort(orb.Count);
                break;
            }
            case EntityPaintingPacket pt:
                _kinds[pt.EntityId] = (EntityWireKind.Painting, 0);
                // b1.8.1's direction 0 −Z, 1 −X, 2 +Z, 3 +X → EnumFacing's horizontal index 0 south, 1 west, 2 north, 3 east.
                yield return WireWriter.ForPacket(EntityIds1122.SpawnPainting).WriteVarInt(pt.EntityId).WriteUuid(Guid.NewGuid()).WriteString(pt.Title)
                    .WritePosition(pt.X, pt.Y, pt.Z).WriteUByte((byte)(pt.Direction switch { 2 => 0, 1 => 1, 0 => 2, _ => 3 }));
                break;
            case ThunderboltPacket tb:
                yield return WireWriter.ForPacket(EntityIds1122.SpawnGlobal).WriteVarInt(tb.EntityId).WriteUByte(EntityIds1122.GlobalLightning)
                    .WriteDouble(FixedPoint.FromAbsolute(tb.X)).WriteDouble(FixedPoint.FromAbsolute(tb.Y)).WriteDouble(FixedPoint.FromAbsolute(tb.Z));
                break;
            case EntityMetadataPacket md:
            {
                var (kind, type) = _kinds.TryGetValue(md.EntityId, out var k) ? k : (EntityWireKind.Player, 0);
                var entries = EntityMeta1122.FromCanonical(kind, type, md.Metadata);
                if (entries.Count > 0) yield return WireWriter.ForPacket(Ids1122.EntityMetadata).WriteVarInt(md.EntityId).WriteMetadata(entries);
                break;
            }
            case EntityVelocityPacket v:
                yield return WireWriter.ForPacket(EntityIds1122.Velocity).WriteVarInt(v.EntityId).WriteShort(v.VelocityX).WriteShort(v.VelocityY).WriteShort(v.VelocityZ);
                break;
            case EntityHeadLookMessage hl:
                yield return WireWriter.ForPacket(EntityIds1122.HeadLook).WriteVarInt(hl.EntityId).WriteUByte(hl.Yaw);
                break;
            case EntityEffectPacket ef:
                yield return WireWriter.ForPacket(EntityIds1122.EntityEffect).WriteVarInt(ef.EntityId).WriteSByte(ef.EffectId).WriteSByte(ef.Amplifier)
                    .WriteVarInt(ef.Duration).WriteUByte(0);
                break;
            case RemoveEntityEffectPacket re:
                yield return WireWriter.ForPacket(EntityIds1122.RemoveEffect).WriteVarInt(re.EntityId).WriteUByte((byte)re.EffectId);
                break;
            case EntityPropertiesMessage pm:
            {
                var w = WireWriter.ForPacket(EntityIds1122.EntityProperties).WriteVarInt(pm.EntityId).WriteInt(pm.Attributes.Count);
                foreach (var a in pm.Attributes)
                {
                    w.WriteString(a.Key, 64).WriteDouble(a.Base).WriteVarInt(a.Modifiers.Count);
                    foreach (var m in a.Modifiers) w.WriteUuid(m.Uuid).WriteDouble(m.Amount).WriteUByte((byte)m.Operation);
                }
                yield return w;
                break;
            }
            case AttachEntityPacket at:
            {
                // Set Passengers names the vehicle and all its riders: a dismount re-sends the old vehicle's list without the rider.
                int vehicle = at.VehicleId;
                if (vehicle < 0)
                {
                    foreach (var (veh, riders) in _riders)
                        if (riders.Remove(at.EntityId)) { vehicle = veh; break; }
                    if (vehicle < 0) break;
                }
                else
                {
                    foreach (var riders in _riders.Values) riders.Remove(at.EntityId);
                    if (!_riders.TryGetValue(vehicle, out var list)) _riders[vehicle] = list = new List<int>();
                    list.Add(at.EntityId);
                }
                var now = _riders.TryGetValue(vehicle, out var cur) ? cur : [];
                var pw = WireWriter.ForPacket(EntityIds1122.SetPassengers).WriteVarInt(vehicle).WriteVarInt(now.Count);
                foreach (int p in now) pw.WriteVarInt(p);
                yield return pw;
                break;
            }
            case HoldingChangePacket hc:
                yield return WireWriter.ForPacket(Ids1122.HeldItemChangeIn).WriteSByte((sbyte)hc.Slot);
                break;
            case ExperienceMessage xm:
                yield return WireWriter.ForPacket(EntityIds1122.SetExperience).WriteFloat(xm.Bar).WriteVarInt(xm.Level).WriteVarInt(xm.Total);
                break;
            case DeathMessage dm:
                yield return WireWriter.ForPacket(EntityIds1122.CombatEvent).WriteVarInt(2).WriteVarInt(dm.PlayerId).WriteInt(dm.KillerId)
                    .WriteString(ChatComponent.Text(dm.Message));
                break;
            case ItemCooldownMessage cd:
                yield return WireWriter.ForPacket(EntityIds1122.SetCooldown).WriteVarInt(cd.ItemId).WriteVarInt(cd.Ticks);
                break;
            default:
                yield break; // no 1.12.2 counterpart in the fake servers' vocabulary
        }
    }

    private static short Velocity(sbyte pickupMotion) => (short)Math.Clamp(pickupMotion / 128.0 * 8000, short.MinValue, short.MaxValue);

    /// <summary>A b1.8.1 chunk region (whole 16-wide columns from y 0) as a full 1.12.2 Chunk Data column.</summary>
    private WireWriter ChunkFrom(ChunkRegion reg)
    {
        int cx = reg.X >> 4, cz = reg.Z >> 4;
        var sections = new int[]?[ChunkData.Sections];
        var bits = new int?[ChunkData.Sections];
        for (int s = 0; s < ChunkData.Sections && s * 16 < reg.Y + reg.SizeY; s++)
        {
            var st = new int[ChunkData.SectionCells];
            bool any = false;
            var distinct = new HashSet<int>();
            for (int y = 0; y < 16; y++)
            for (int z = 0; z < 16 && z < reg.SizeZ; z++)
            for (int x = 0; x < 16 && x < reg.SizeX; x++)
            {
                int wy = s * 16 + y - reg.Y;
                if (wy < 0 || wy >= reg.SizeY) continue;
                int i = reg.Index(x, wy, z);
                int state = reg.BlockIds[i] << 4 | reg.Metadata[i] & 15;
                st[y * 256 + z * 16 + x] = state;
                any |= state != 0;
                distinct.Add(state);
            }
            if (!any) continue;
            distinct.Add(0);
            sections[s] = st;
            int n = distinct.Count;
            bits[s] = n <= 16 ? 4 : n <= 256 ? (int)Math.Ceiling(Math.Log2(n)) : ChunkData.GlobalBits;
        }
        var body = ChunkData.EncodeBody(cx, cz, full: true, bits, sections, hasSky: _dimension == 0, blockLight: 0, skyLight: 15);
        return WireWriter.ForPacket(Ids1122.ChunkData).WriteBytes(body);
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
