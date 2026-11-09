using System.Collections.Frozen;
using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// S→C: one encoder per canonical message type (every message class is sealed, so the exact type is the key), written from the
/// decompiled <c>SPacket*.writePacketData</c>. An encoder emits zero or more frames; a type with no encoder (the handshake, which
/// is answered on read, and anything with no 1.12.2 counterpart in the fake servers' vocabulary) sends nothing.
/// </summary>
public sealed partial class Release1122ServerConnection
{
    private delegate void Encoder(Release1122ServerConnection s, IPacket packet);

    private static readonly FrozenDictionary<Type, Encoder> Encoders = BuildEncoders();

    private static void On<T>(Dictionary<Type, Encoder> table, Action<Release1122ServerConnection, T> encode) where T : IPacket =>
        table[typeof(T)] = (s, p) => encode(s, (T)p);

    private static FrozenDictionary<Type, Encoder> BuildEncoders()
    {
        var t = new Dictionary<Type, Encoder>();
        // the session and the player
        On<LoginRequestPacket>(t, static (s, p) => s.EncodeJoinGame(p));
        On<KeepAlivePacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.KeepAlive).WriteLong(p.KeepAliveId), p));
        On<KickPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.Disconnect).WriteString(ChatComponent.Text(p.Reason)), p));
        On<ChatPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.Chat).WriteString(p.Json ?? ChatComponent.Text(p.Message)).WriteSByte(p.Position), p));
        On<TimeUpdatePacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.TimeUpdate).WriteLong(p.Time).WriteLong(p.Frozen ? -p.Time : p.Time), p));
        On<SpawnPositionPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.SpawnPosition).WritePosition(p.X, p.Y, p.Z), p));
        On<UpdateHealthPacket>(t, static (s, p) => s.EncodeHealth(p));
        On<PlayerPositionLookPacket>(t, static (s, p) => s.EncodePlayerPosLook(p));
        On<RespawnPacket>(t, static (s, p) => s.EncodeRespawn(p));
        On<NewStatePacket>(t, static (s, p) => s.EncodeGameState(p));
        On<PlayerListItemPacket>(t, static (s, p) => s.EncodePlayerList(p));
        On<HoldingChangePacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.HeldItemChangeIn).WriteSByte((sbyte)p.Slot), p));
        On<ExperienceMessage>(t, static (s, p) => s.Emit(WireWriter.ForPacket(EntityIds1122.SetExperience).WriteFloat(p.Bar).WriteVarInt(p.Level).WriteVarInt(p.Total), p));
        On<DeathMessage>(t, static (s, p) => s.EncodeDeath(p));
        On<SoundMessage>(t, static (s, p) => s.EncodeSound(p));
        On<ItemCooldownMessage>(t, static (s, p) => s.Emit(WireWriter.ForPacket(EntityIds1122.SetCooldown).WriteVarInt(p.ItemId).WriteVarInt(p.Ticks), p));
        // the world
        On<PreChunkPacket>(t, static (s, p) => s.EncodePreChunk(p));
        On<MapChunkPacket>(t, static (s, p) => s.Emit(s.ChunkFrom(ChunkDataDecoder.Decode(p)), p));
        On<BlockChangePacket>(t, static (s, p) => s.EncodeBlockChange(p));
        On<MultiBlockChangePacket>(t, static (s, p) => s.EncodeMultiBlockChange(p));
        On<ExplosionPacket>(t, static (s, p) => s.EncodeExplosion(p));
        On<UpdateSignPacket>(t, static (s, p) => s.EncodeSign(p));
        On<BlockEntityMessage>(t, static (s, p) => s.EncodeBlockEntity(p));
        On<BlockBreakAnimationMessage>(t, static (s, p) => s.EncodeBlockBreak(p));
        On<BlockActionPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.BlockAction).WritePosition(p.X, p.Y, p.Z)
            .WriteUByte(p.Data1).WriteUByte(p.Data2).WriteVarInt(p.BlockId ?? 0), p));
        // windows
        On<OpenWindowPacket>(t, static (s, p) => s.EncodeOpenWindow(p));
        On<CloseWindowPacket>(t, static (s, p) => s.EncodeCloseWindow(p));
        On<WindowItemsPacket>(t, static (s, p) => s.EncodeWindowItems(p));
        On<SetSlotPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.SetSlot).WriteSByte(p.WindowId).WriteShort(p.Slot).WriteSlot(p.Item), p));
        On<UpdateProgressBarPacket>(t, static (s, p) => s.EncodeWindowProperty(p));
        On<TransactionPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.ConfirmTransaction).WriteSByte(p.WindowId).WriteShort(p.ActionNumber).WriteBool(p.Accepted), p));
        // entity spawns
        On<PickupSpawnPacket>(t, static (s, p) => s.EncodePickupSpawn(p));
        On<NamedEntitySpawnPacket>(t, static (s, p) => s.EncodePlayerSpawn(p));
        On<MobSpawnPacket>(t, static (s, p) => s.EncodeMobSpawn(p));
        On<AddObjectVehiclePacket>(t, static (s, p) => s.EncodeObjectSpawn(p));
        On<ExperienceOrbPacket>(t, static (s, p) => s.EncodeOrbSpawn(p));
        On<EntityPaintingPacket>(t, static (s, p) => s.EncodePaintingSpawn(p));
        On<ThunderboltPacket>(t, static (s, p) => s.EncodeLightning(p));
        // entity moves and state
        On<EntityTeleportPacket>(t, static (s, p) => s.EncodeTeleport(p));
        On<EntityLookRelativeMovePacket>(t, static (s, p) => s.EncodeLookRelativeMove(p));
        On<EntityRelativeMovePacket>(t, static (s, p) => s.EncodeRelativeMove(p));
        On<EntityLookPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.EntityLook).WriteVarInt(p.EntityId).WriteUByte(p.Yaw).WriteUByte(p.Pitch).WriteBool(true), p));
        On<DestroyEntityPacket>(t, static (s, p) => s.EncodeDestroy(p));
        On<CollectItemPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.CollectItem).WriteVarInt(p.CollectedEntityId).WriteVarInt(p.CollectorEntityId).WriteVarInt(1), p));
        On<EntityStatusPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(Ids1122.EntityStatus).WriteInt(p.EntityId).WriteSByte(p.Status), p));
        On<AnimationPacket>(t, static (s, p) => s.EncodeAnimation(p));
        On<EntityEquipmentPacket>(t, static (s, p) => s.EncodeEquipment(p));
        On<EntityMetadataPacket>(t, static (s, p) => s.EncodeMetadata(p));
        On<EntityVelocityPacket>(t, static (s, p) => s.EncodeVelocity(p));
        On<EntityHeadLookMessage>(t, static (s, p) => s.Emit(WireWriter.ForPacket(EntityIds1122.HeadLook).WriteVarInt(p.EntityId).WriteUByte(p.Yaw), p));
        On<EntityEffectPacket>(t, static (s, p) => s.EncodeEffect(p));
        On<RemoveEntityEffectPacket>(t, static (s, p) => s.Emit(WireWriter.ForPacket(EntityIds1122.RemoveEffect).WriteVarInt(p.EntityId).WriteUByte((byte)p.EffectId), p));
        On<EntityPropertiesMessage>(t, static (s, p) => s.EncodeProperties(p));
        On<AttachEntityPacket>(t, static (s, p) => s.EncodeAttach(p));
        return t.ToFrozenDictionary();
    }

    // ---- the session and the player ----

    private void EncodeJoinGame(LoginRequestPacket l)
    {
        _dimension = l.Dimension;
        Emit(WireWriter.ForPacket(Ids1122.JoinGame).WriteInt(l.EntityId).WriteUByte((byte)l.ServerMode).WriteInt(l.Dimension)
            .WriteUByte((byte)l.Difficulty).WriteUByte(l.MaxPlayers).WriteString("default", 16).WriteBool(false), l);
    }

    private void EncodeHealth(UpdateHealthPacket h) =>
        Emit(WireWriter.ForPacket(Ids1122.UpdateHealth).WriteFloat(h.HealthExact ?? h.Health).WriteVarInt(h.Food).WriteFloat(h.FoodSaturation), h);

    private void EncodePlayerPosLook(PlayerPositionLookPacket p) =>
        Emit(WireWriter.ForPacket(Ids1122.PlayerPosLook).WriteDouble(p.X).WriteDouble(p.Y).WriteDouble(p.Z).WriteFloat(p.Yaw)
            .WriteFloat(p.Pitch).WriteUByte(0).WriteVarInt(++_teleportId), p);

    private void EncodeRespawn(RespawnPacket rp)
    {
        _dimension = rp.Dimension;
        Emit(WireWriter.ForPacket(Ids1122.Respawn).WriteInt(rp.Dimension).WriteUByte((byte)rp.Difficulty).WriteUByte((byte)rp.GameMode)
            .WriteString("default", 16), rp);
    }

    private void EncodeGameState(NewStatePacket ns) =>
        Emit(WireWriter.ForPacket(Ids1122.ChangeGameState).WriteUByte((byte)ns.Reason)
            .WriteFloat(ns.Reason is NewStatePacket.ReasonRainLevel or NewStatePacket.ReasonThunderLevel ? ns.Value : ns.GameMode), ns);

    private void EncodePlayerList(PlayerListItemPacket pl)
    {
        var w = WireWriter.ForPacket(Ids1122.PlayerListItem).WriteVarInt(pl.Online ? 0 : 4).WriteVarInt(1).WriteUuid(Wire.OfflineUuid(pl.PlayerName));
        if (pl.Online) w.WriteString(pl.PlayerName, 16).WriteVarInt(0).WriteVarInt(0).WriteVarInt(pl.Ping).WriteBool(false);
        Emit(w, pl);
    }

    private void EncodeDeath(DeathMessage dm) =>
        Emit(WireWriter.ForPacket(EntityIds1122.CombatEvent).WriteVarInt(2).WriteVarInt(dm.PlayerId).WriteInt(dm.KillerId)
            .WriteString(ChatComponent.Text(dm.Message)), dm);

    // ---- the world ----

    private void EncodePreChunk(PreChunkPacket pc)
    {
        if (!pc.Load) Emit(WireWriter.ForPacket(Ids1122.UnloadChunk).WriteInt(pc.ChunkX).WriteInt(pc.ChunkZ), pc);
    }

    private void EncodeBlockChange(BlockChangePacket bc) =>
        Emit(WireWriter.ForPacket(Ids1122.BlockChange).WritePosition(bc.X, bc.Y, bc.Z).WriteVarInt(bc.BlockId << 4 | bc.Metadata & 15), bc);

    private void EncodeMultiBlockChange(MultiBlockChangePacket mb)
    {
        var w = WireWriter.ForPacket(Ids1122.MultiBlockChange).WriteInt(mb.ChunkX).WriteInt(mb.ChunkZ).WriteVarInt(mb.Coordinates.Length);
        for (int i = 0; i < mb.Coordinates.Length; i++) w.WriteShort(mb.Coordinates[i]).WriteVarInt(mb.BlockIds[i] << 4 | mb.Metadata[i] & 15);
        Emit(w, mb);
    }

    private void EncodeExplosion(ExplosionPacket ex)
    {
        var w = WireWriter.ForPacket(Ids1122.Explosion).WriteFloat((float)ex.X).WriteFloat((float)ex.Y).WriteFloat((float)ex.Z).WriteFloat(ex.Radius)
            .WriteInt(ex.Records.Length);
        foreach (var rec in ex.Records) w.WriteSByte(rec.DX).WriteSByte(rec.DY).WriteSByte(rec.DZ);
        Emit(w.WriteFloat(0).WriteFloat(0).WriteFloat(0), ex); // the player's knockback
    }

    private void EncodeSign(UpdateSignPacket sign)
    {
        var w = WireWriter.ForPacket(Ids1122.UpdateBlockEntity).WritePosition(sign.X, sign.Y, sign.Z).WriteUByte(Codec1122.SignAction);
        Nbt.WriteRoot(w, Codec1122.SignTag(sign.X, sign.Y, sign.Z, sign.Lines, sign.Json));
        Emit(w, sign);
    }

    /// <summary>A block entity's looks (nothing without them).</summary>
    private void EncodeBlockEntity(BlockEntityMessage be)
    {
        if (be.Info is not { } info) return;
        var w = WireWriter.ForPacket(Ids1122.UpdateBlockEntity).WritePosition(be.X, be.Y, be.Z).WriteUByte((byte)BlockEntities1122.ActionOf(info.Kind));
        Nbt.WriteRoot(w, BlockEntities1122.Tag(be.X, be.Y, be.Z, info));
        Emit(w, be);
    }

    private void EncodeBlockBreak(BlockBreakAnimationMessage bb) =>
        Emit(WireWriter.ForPacket(Ids1122.BlockBreakAnim).WriteVarInt(bb.EntityId).WritePosition(bb.X, bb.Y, bb.Z).WriteSByte((sbyte)bb.Stage), bb);

    // ---- windows ----

    private void EncodeOpenWindow(OpenWindowPacket ow)
    {
        var (type, slots) = Codec1122.WindowTypeName(ow.InventoryType, ow.SlotCount);
        // A window kind of its own (brewing_stand, enchanting_table, anvil...): its own type string; the table and the anvil
        // are interaction objects (slot count 0), the rest send their size.
        if (ow.Kind is { Length: > 0 } kind && ow.InventoryType == 0 && kind != "chest")
            (type, slots) = ("minecraft:" + kind, kind is "enchanting_table" or "anvil" ? 0 : ow.SlotCount);
        _windowTypes[(byte)ow.WindowId] = ow.InventoryType;
        Emit(WireWriter.ForPacket(Ids1122.OpenWindow).WriteUByte((byte)ow.WindowId).WriteString(type, 32)
            .WriteString(ChatComponent.Text(ow.Title)).WriteUByte((byte)slots), ow);
    }

    private void EncodeCloseWindow(CloseWindowPacket cw)
    {
        _windowTypes.Remove((byte)cw.WindowId);
        Emit(WireWriter.ForPacket(Ids1122.CloseWindow).WriteUByte((byte)cw.WindowId), cw);
    }

    private void EncodeWindowItems(WindowItemsPacket wi)
    {
        var items = wi.Items;
        if (wi.WindowId == 0 && items.Length == 45) items = [.. items, ItemStack.Empty]; // + the off hand (ContainerPlayer slot 45)
        var w = WireWriter.ForPacket(Ids1122.WindowItems).WriteUByte((byte)wi.WindowId).WriteShort((short)items.Length);
        foreach (var it in items) w.WriteSlot(it);
        Emit(w, wi);
    }

    private void EncodeWindowProperty(UpdateProgressBarPacket pb)
    {
        int prop = _windowTypes.TryGetValue((byte)pb.WindowId, out byte t) && t == 2 ? Codec1122.FurnacePropertyFromCanonical(pb.ProgressBar) : pb.ProgressBar;
        Emit(WireWriter.ForPacket(Ids1122.WindowProperty).WriteUByte((byte)pb.WindowId).WriteShort((short)prop).WriteShort(pb.Value), pb);
    }

    // ---- entity spawns ----

    /// <summary>A canonical spawn's exact position, or its fixed-point one.</summary>
    private static (double X, double Y, double Z) Position((double, double, double)? exact, int x, int y, int z) =>
        exact ?? (FixedPoint.FromAbsolute(x), FixedPoint.FromAbsolute(y), FixedPoint.FromAbsolute(z));

    private void EncodePickupSpawn(PickupSpawnPacket ps)
    {
        _kinds[ps.EntityId] = (EntityWireKind.Object, Ids1122.ObjectItem);
        var (px, py, pz) = Position(ps.Exact, ps.X, ps.Y, ps.Z);
        // EntityTrackerEntry.createSpawnPacket: SPacketSpawnObject(item, 2, 1), then the tracked data (the stack).
        Emit(WireWriter.ForPacket(Ids1122.SpawnObject).WriteVarInt(ps.EntityId).WriteUuid(Guid.NewGuid()).WriteSByte(Ids1122.ObjectItem)
            .WriteDouble(px).WriteDouble(py).WriteDouble(pz)
            .WriteUByte(0).WriteUByte(0).WriteInt(1)
            .WriteShort(Velocity(ps.MotionX)).WriteShort(Velocity(ps.MotionY)).WriteShort(Velocity(ps.MotionZ)), ps);
        Emit(WireWriter.ForPacket(Ids1122.EntityMetadata).WriteVarInt(ps.EntityId)
            .WriteItemMetadata(new ItemStack(ps.ItemId, ps.Count, ps.Damage)), ps);
    }

    /// <summary>The Player List entry that carries the name, then Spawn Player.</summary>
    private void EncodePlayerSpawn(NamedEntitySpawnPacket ne)
    {
        var uuid = Wire.OfflineUuid(ne.PlayerName);
        _kinds[ne.EntityId] = (EntityWireKind.Player, 0);
        var (nx, ny, nz) = Position(ne.Exact, ne.X, ne.Y, ne.Z);
        Emit(WireWriter.ForPacket(Ids1122.PlayerListItem).WriteVarInt(0).WriteVarInt(1).WriteUuid(uuid).WriteString(ne.PlayerName, 16)
            .WriteVarInt(0).WriteVarInt(0).WriteVarInt(1).WriteBool(false), ne);
        Emit(WireWriter.ForPacket(Ids1122.SpawnPlayer).WriteVarInt(ne.EntityId).WriteUuid(uuid)
            .WriteDouble(nx).WriteDouble(ny).WriteDouble(nz)
            .WriteUByte(ne.Yaw).WriteUByte(ne.Pitch).WriteUByte(0xFF), ne);
    }

    private void EncodeMobSpawn(MobSpawnPacket ms)
    {
        _kinds[ms.EntityId] = (EntityWireKind.Mob, ms.Type);
        var (x, y, z) = Position(ms.Exact, ms.X, ms.Y, ms.Z);
        Emit(WireWriter.ForPacket(EntityIds1122.SpawnMob).WriteVarInt(ms.EntityId).WriteUuid(Guid.NewGuid()).WriteVarInt(ms.Type)
            .WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteUByte(ms.Yaw).WriteUByte(ms.Pitch).WriteUByte(ms.Yaw)
            .WriteShort(0).WriteShort(0).WriteShort(0)
            .WriteMetadata(EntityMeta1122.FromCanonical(EntityWireKind.Mob, ms.Type, ms.Metadata)), ms);
    }

    private void EncodeObjectSpawn(AddObjectVehiclePacket ao)
    {
        _kinds[ao.EntityId] = (EntityWireKind.Object, ao.Type);
        var (x, y, z) = Position(ao.Exact, ao.X, ao.Y, ao.Z);
        Emit(WireWriter.ForPacket(Ids1122.SpawnObject).WriteVarInt(ao.EntityId).WriteUuid(Guid.NewGuid()).WriteUByte(ao.Type)
            .WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteUByte(0).WriteUByte(0).WriteInt(ao.ThrowerEntityId)
            .WriteShort(ao.SpeedX).WriteShort(ao.SpeedY).WriteShort(ao.SpeedZ), ao);
    }

    /// <summary>Sound Effect 0x49 in the NEUTRAL category (6, <c>Entity.getSoundCategory</c>), the position ×8.</summary>
    private void EncodeSound(SoundMessage sm) =>
        Emit(WireWriter.ForPacket(Ids1122.SoundEffect).WriteVarInt(SoundMessage.Ids1122.Single(kv => kv.Value == sm.Sound).Key).WriteVarInt(6)
            .WriteInt((int)(sm.X * 8)).WriteInt((int)(sm.Y * 8)).WriteInt((int)(sm.Z * 8)).WriteFloat(sm.Volume).WriteFloat(sm.Pitch), sm);

    private void EncodeOrbSpawn(ExperienceOrbPacket orb)
    {
        _kinds[orb.EntityId] = (EntityWireKind.Orb, 0);
        var (x, y, z) = Position(orb.Exact, orb.X, orb.Y, orb.Z);
        Emit(WireWriter.ForPacket(EntityIds1122.SpawnXpOrb).WriteVarInt(orb.EntityId).WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteShort(orb.Count), orb);
    }

    // b1.8.1's direction 0 −Z, 1 −X, 2 +Z, 3 +X → EnumFacing's horizontal index 0 south, 1 west, 2 north, 3 east (anything else east).
    private static readonly byte[] PaintingFacingFromCanonical = [2, 1, 0];

    private void EncodePaintingSpawn(EntityPaintingPacket pt)
    {
        _kinds[pt.EntityId] = (EntityWireKind.Painting, 0);
        byte facing = (uint)pt.Direction < (uint)PaintingFacingFromCanonical.Length ? PaintingFacingFromCanonical[pt.Direction] : (byte)3;
        Emit(WireWriter.ForPacket(EntityIds1122.SpawnPainting).WriteVarInt(pt.EntityId).WriteUuid(Guid.NewGuid()).WriteString(pt.Title)
            .WritePosition(pt.X, pt.Y, pt.Z).WriteUByte(facing), pt);
    }

    private void EncodeLightning(ThunderboltPacket tb) =>
        Emit(WireWriter.ForPacket(EntityIds1122.SpawnGlobal).WriteVarInt(tb.EntityId).WriteUByte(EntityIds1122.GlobalLightning)
            .WriteDouble(FixedPoint.FromAbsolute(tb.X)).WriteDouble(FixedPoint.FromAbsolute(tb.Y)).WriteDouble(FixedPoint.FromAbsolute(tb.Z)), tb);

    // ---- entity moves and state ----

    private void EncodeTeleport(EntityTeleportPacket et)
    {
        var (tx, ty, tz) = Position(et.Exact, et.X, et.Y, et.Z);
        Emit(WireWriter.ForPacket(Ids1122.EntityTeleport).WriteVarInt(et.EntityId).WriteDouble(tx).WriteDouble(ty).WriteDouble(tz)
            .WriteUByte(et.Yaw).WriteUByte(et.Pitch).WriteBool(true), et);
    }

    private void EncodeLookRelativeMove(EntityLookRelativeMovePacket lrm) =>
        Emit(WireWriter.ForPacket(Ids1122.EntityLookRelMove).WriteVarInt(lrm.EntityId)
            .WriteShort((short)(lrm.DX * 128)).WriteShort((short)(lrm.DY * 128)).WriteShort((short)(lrm.DZ * 128))
            .WriteUByte(lrm.Yaw).WriteUByte(lrm.Pitch).WriteBool(true), lrm);

    private void EncodeRelativeMove(EntityRelativeMovePacket rm) =>
        Emit(WireWriter.ForPacket(Ids1122.EntityRelMove).WriteVarInt(rm.EntityId)
            .WriteShort((short)(rm.DX * 128)).WriteShort((short)(rm.DY * 128)).WriteShort((short)(rm.DZ * 128)).WriteBool(true), rm);

    private void EncodeDestroy(DestroyEntityPacket de)
    {
        _kinds.Remove(de.EntityId);
        Emit(WireWriter.ForPacket(Ids1122.DestroyEntities).WriteVarInt(1).WriteVarInt(de.EntityId), de);
    }

    // canonical swing (1, by hand), hurt (2), wake up (3), crit (6), magic crit (7) → SPacketAnimation 0/3, 1, 2, 4, 5; −1 none.
    private static readonly sbyte[] AnimationToWire = [-1, 0, 1, 2, -1, -1, 4, 5];

    private void EncodeAnimation(AnimationPacket an)
    {
        int anim = (uint)an.Animation < (uint)AnimationToWire.Length ? AnimationToWire[an.Animation] : -1;
        if (anim == 0 && an.Hand == 1) anim = 3; // the off hand's swing
        if (anim >= 0) Emit(WireWriter.ForPacket(Ids1122.EntityAnimation).WriteVarInt(an.EntityId).WriteUByte((byte)anim), an);
    }

    // canonical 0 held, 1..4 boots..helmet, 5 off hand → EntityEquipmentSlot 0 main hand, 2 feet .. 5 head, 1 off hand.
    private static readonly int[] EquipmentSlotToWire = [0, 2, 3, 4, 5, 1];

    private void EncodeEquipment(EntityEquipmentPacket eq)
    {
        if ((uint)eq.Slot >= (uint)EquipmentSlotToWire.Length) return;
        Emit(WireWriter.ForPacket(Ids1122.EntityEquipment).WriteVarInt(eq.EntityId).WriteVarInt(EquipmentSlotToWire[eq.Slot]).WriteSlot(eq.Item), eq);
    }

    private void EncodeMetadata(EntityMetadataPacket md)
    {
        var (kind, type) = _kinds.TryGetValue(md.EntityId, out var k) ? k : (EntityWireKind.Player, 0);
        var entries = EntityMeta1122.FromCanonical(kind, type, md.Metadata);
        if (entries.Count > 0) Emit(WireWriter.ForPacket(Ids1122.EntityMetadata).WriteVarInt(md.EntityId).WriteMetadata(entries), md);
    }

    private void EncodeVelocity(EntityVelocityPacket v) =>
        Emit(WireWriter.ForPacket(EntityIds1122.Velocity).WriteVarInt(v.EntityId).WriteShort(v.VelocityX).WriteShort(v.VelocityY).WriteShort(v.VelocityZ), v);

    private void EncodeEffect(EntityEffectPacket ef) =>
        Emit(WireWriter.ForPacket(EntityIds1122.EntityEffect).WriteVarInt(ef.EntityId).WriteSByte(ef.EffectId).WriteSByte(ef.Amplifier)
            .WriteVarInt(ef.Duration).WriteUByte(0), ef);

    private void EncodeProperties(EntityPropertiesMessage pm)
    {
        var w = WireWriter.ForPacket(EntityIds1122.EntityProperties).WriteVarInt(pm.EntityId).WriteInt(pm.Attributes.Count);
        foreach (var a in pm.Attributes)
        {
            w.WriteString(a.Key, 64).WriteDouble(a.Base).WriteVarInt(a.Modifiers.Count);
            foreach (var m in a.Modifiers) w.WriteUuid(m.Uuid).WriteDouble(m.Amount).WriteUByte((byte)m.Operation);
        }
        Emit(w, pm);
    }

    /// <summary>Set Passengers names the vehicle and all its riders: a dismount re-sends the old vehicle's list without the rider.</summary>
    private void EncodeAttach(AttachEntityPacket at)
    {
        int vehicle = at.VehicleId < 0 ? Dismount(at.EntityId) : Mount(at.EntityId, at.VehicleId);
        if (vehicle < 0) return;
        var now = _riders.TryGetValue(vehicle, out var cur) ? cur : [];
        var pw = WireWriter.ForPacket(EntityIds1122.SetPassengers).WriteVarInt(vehicle).WriteVarInt(now.Count);
        foreach (int p in now) pw.WriteVarInt(p);
        Emit(pw, at);
    }

    /// <summary>Takes the rider off the vehicle it was on: that vehicle, or −1 when it rode nothing.</summary>
    private int Dismount(int rider)
    {
        foreach (var (veh, riders) in _riders)
            if (riders.Remove(rider)) return veh;
        return -1;
    }

    /// <summary>Moves the rider onto <paramref name="vehicle"/> (off any other).</summary>
    private int Mount(int rider, int vehicle)
    {
        foreach (var riders in _riders.Values) riders.Remove(rider);
        if (!_riders.TryGetValue(vehicle, out var list)) _riders[vehicle] = list = new List<int>();
        list.Add(rider);
        return vehicle;
    }

    private static short Velocity(sbyte pickupMotion) => (short)Math.Clamp(pickupMotion / 128.0 * 8000, short.MinValue, short.MaxValue);
}
