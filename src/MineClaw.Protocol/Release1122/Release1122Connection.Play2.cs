using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// Multi-version P2 of the 1.12.2 client connection: inventory windows, digging, placing and using, item entities and the
/// entity traffic the pickup logic and the observers read. Wire formats from the decompiled packet classes
/// (<c>readPacketData</c>/<c>writePacketData</c>), cross-checked with minecraft-data's protocol.json.
/// </summary>
public sealed partial class Release1122Connection
{
    /// <summary>An entity the connection surfaced (players, item entities): its exact position, so 1.12.2's 1/4096 relative
    /// moves become absolute canonical teleports without drift.</summary>
    private sealed class Tracked
    {
        public double X, Y, Z;
        public byte Yaw, Pitch;
        public bool IsItem;
        /// <summary>An item entity waits for its metadata (the stack) before it is surfaced as a pickup spawn.</summary>
        public bool Surfaced;
        public short VX, VY, VZ;
    }

    private readonly Dictionary<int, Tracked> _entities = new();
    // Open container windows' canonical types (furnace properties are renumbered).
    private readonly Dictionary<int, byte> _windowTypes = new();

    private void ResetEntities()
    {
        _entities.Clear();
        _windowTypes.Clear();
    }

    private IPacket? TranslatePlay2(int id, ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        switch (id)
        {
            // ---- windows ----
            case Ids1122.OpenWindow:
            {
                int wid = r.ReadUByte();
                var type = r.ReadString(32);
                var title = r.ReadString();
                int slots = r.ReadUByte();
                var (ct, n) = Codec1122.WindowType(type, slots);
                _windowTypes[wid] = ct;
                return new OpenWindowPacket { WindowId = (sbyte)wid, InventoryType = ct, Title = ChatComponent.ToPlain(title, Lang), SlotCount = (byte)Math.Min(n, 255) };
            }
            case Ids1122.CloseWindow:
            {
                int wid = r.ReadUByte();
                _windowTypes.Remove(wid);
                return new CloseWindowPacket { WindowId = (sbyte)wid };
            }
            case Ids1122.WindowItems:
            {
                int wid = r.ReadUByte();
                int n = r.ReadShort();
                if (n < 0 || n > r.Remaining) throw new ProtocolException($"window items count {n} out of range");
                var items = new ItemStack[n];
                for (int i = 0; i < n; i++) items[i] = Codec1122.ReadSlot(ref r);
                return new WindowItemsPacket { WindowId = (sbyte)wid, Items = items };
            }
            case Ids1122.SetSlot:
            {
                int wid = r.ReadSByte();
                int slot = r.ReadShort();
                var item = Codec1122.ReadSlot(ref r);
                if (wid == -2)
                {
                    // An InventoryPlayer index (handleSetSlot): the player window's slot.
                    wid = 0;
                    slot = Codec1122.InventoryIndexToWindowSlot(slot);
                    if (slot < 0) return null;
                }
                return new SetSlotPacket { WindowId = (sbyte)wid, Slot = (short)slot, Item = item };
            }
            case Ids1122.WindowProperty:
            {
                int wid = r.ReadUByte();
                int prop = r.ReadShort();
                short value = r.ReadShort();
                if (_windowTypes.TryGetValue(wid, out byte t) && t == 2)
                {
                    prop = Codec1122.FurnacePropertyToCanonical(prop);
                    if (prop < 0) return null;
                }
                return new UpdateProgressBarPacket { WindowId = (sbyte)wid, ProgressBar = (short)prop, Value = value };
            }
            case Ids1122.ConfirmTransaction:
                return new TransactionPacket { WindowId = r.ReadSByte(), ActionNumber = r.ReadShort(), Accepted = r.ReadBool() };

            // ---- blocks ----
            case Ids1122.BlockBreakAnim:
            {
                int eid = r.ReadVarInt();
                var (x, y, z) = r.ReadPosition();
                int stage = r.ReadSByte();
                return new BlockBreakAnimationMessage { EntityId = eid, X = x, Y = y, Z = z, Stage = stage };
            }
            case Ids1122.UpdateBlockEntity:
            {
                var (x, y, z) = r.ReadPosition();
                int action = r.ReadUByte();
                var tag = Nbt.ReadRoot(ref r);
                if (action != Codec1122.SignAction || tag is null || Codec1122.SignLines(tag, Lang) is not { } lines) return null;
                return new UpdateSignPacket { X = x, Y = (short)y, Z = z, Lines = lines };
            }

            // ---- entities ----
            case Ids1122.SpawnObject:
            {
                int eid = r.ReadVarInt();
                r.ReadUuid();
                int type = r.ReadSByte();
                if (type != Ids1122.ObjectItem) return null; // other objects (arrows, boats, falling blocks): P4
                var e = new Tracked { X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(), IsItem = true };
                e.Pitch = r.ReadUByte();
                e.Yaw = r.ReadUByte();
                r.ReadInt(); // object data
                (e.VX, e.VY, e.VZ) = (r.ReadShort(), r.ReadShort(), r.ReadShort());
                _entities[eid] = e;
                return null; // surfaced when its stack arrives (the Entity Metadata right after)
            }
            case Ids1122.SpawnPlayer:
            {
                int eid = r.ReadVarInt();
                var uuid = r.ReadUuid();
                var e = new Tracked { X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(), Surfaced = true };
                e.Yaw = r.ReadUByte();
                e.Pitch = r.ReadUByte();
                _entities[eid] = e;
                string name = _names.TryGetValue(uuid, out var n) ? n : uuid.ToString("N")[..16];
                return new NamedEntitySpawnPacket
                {
                    EntityId = eid, PlayerName = name, X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z),
                    Yaw = e.Yaw, Pitch = e.Pitch,
                };
            }
            case Ids1122.EntityMetadata:
            {
                int eid = r.ReadVarInt();
                if (!_entities.TryGetValue(eid, out var e) || !e.IsItem) return null;
                int pos = r.Position;
                if (Codec1122.ReadMetadataSlot(body, ref pos) is not { } stack) return null;
                if (!e.Surfaced)
                {
                    if (stack.IsEmpty) return null;
                    e.Surfaced = true;
                    return new PickupSpawnPacket
                    {
                        EntityId = eid, ItemId = stack.Id, Count = stack.Count, Damage = stack.Damage,
                        X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z),
                        MotionX = Motion(e.VX), MotionY = Motion(e.VY), MotionZ = Motion(e.VZ),
                    };
                }
                return new ItemEntityStackMessage { EntityId = eid, Item = stack };
            }
            case Ids1122.EntityRelMove:
            case Ids1122.EntityLookRelMove:
            {
                int eid = r.ReadVarInt();
                short dx = r.ReadShort(), dy = r.ReadShort(), dz = r.ReadShort();
                if (!_entities.TryGetValue(eid, out var e)) return null;
                // handleEntityMovement: serverPos += delta; position = serverPos / 4096.
                e.X += dx / 4096.0; e.Y += dy / 4096.0; e.Z += dz / 4096.0;
                if (id == Ids1122.EntityLookRelMove) { e.Yaw = r.ReadUByte(); e.Pitch = r.ReadUByte(); }
                return e.Surfaced ? Teleport(eid, e) : null;
            }
            case Ids1122.EntityLook:
            {
                int eid = r.ReadVarInt();
                if (!_entities.TryGetValue(eid, out var e)) return null;
                e.Yaw = r.ReadUByte(); e.Pitch = r.ReadUByte();
                return e.Surfaced ? new EntityLookPacket { EntityId = eid, Yaw = e.Yaw, Pitch = e.Pitch } : null;
            }
            case Ids1122.EntityTeleport:
            {
                int eid = r.ReadVarInt();
                if (!_entities.TryGetValue(eid, out var e)) return null;
                (e.X, e.Y, e.Z) = (r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                e.Yaw = r.ReadUByte(); e.Pitch = r.ReadUByte();
                return e.Surfaced ? Teleport(eid, e) : null;
            }
            case Ids1122.DestroyEntities:
            {
                int n = r.ReadVarInt();
                if (n < 0 || n > r.Remaining) throw new ProtocolException($"destroy entities count {n} out of range");
                var gone = new List<DestroyEntityPacket>(n);
                for (int i = 0; i < n; i++)
                {
                    int eid = r.ReadVarInt();
                    if (_entities.Remove(eid, out var e) && e.Surfaced) gone.Add(new DestroyEntityPacket { EntityId = eid });
                }
                return First(gone);
            }
            case Ids1122.CollectItem:
                return new CollectItemPacket { CollectedEntityId = r.ReadVarInt(), CollectorEntityId = r.ReadVarInt() }; // + pickup count
            case Ids1122.EntityStatus:
                return new EntityStatusPacket { EntityId = r.ReadInt(), Status = r.ReadSByte() };
            case Ids1122.EntityAnimation:
            {
                int eid = r.ReadVarInt();
                // SPacketAnimation: 0 swing main arm, 1 hurt, 2 wake up, 3 swing off hand, 4 crit, 5 magic crit → b1.8.1's 1/2/3.
                sbyte anim = (sbyte)(r.ReadUByte() switch { 0 or 3 => 1, 1 => 2, 2 => 3, _ => 0 });
                return anim == 0 ? null : new AnimationPacket { EntityId = eid, Animation = anim };
            }
            case Ids1122.EntityEquipment:
            {
                int eid = r.ReadVarInt();
                int slot = r.ReadVarInt();
                var item = Codec1122.ReadSlot(ref r);
                // EntityEquipmentSlot: 0 main hand, 1 off hand, 2 feet, 3 legs, 4 chest, 5 head → b1.8.1 0 held, 1..4 boots..helmet.
                short cslot = slot switch { 0 => 0, 2 => 1, 3 => 2, 4 => 3, 5 => 4, _ => -1 };
                if (cslot < 0) return null;
                return new EntityEquipmentPacket { EntityId = eid, Slot = cslot, ItemId = item.IsEmpty ? (short)-1 : item.Id, Damage = item.Damage };
            }
            default:
                return null; // sounds, particles, mobs, effects, advancements, recipe book…: recorded, not surfaced
        }
    }

    private static EntityTeleportPacket Teleport(int eid, Tracked e) => new()
    {
        EntityId = eid, X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z), Yaw = e.Yaw, Pitch = e.Pitch,
    };

    /// <summary>Velocity (blocks/tick × 8000) → b1.8.1's pickup motion byte (× 128).</summary>
    private static sbyte Motion(short v) => (sbyte)Math.Clamp((int)Math.Round(v / 8000.0 * 128), sbyte.MinValue, sbyte.MaxValue);

    // ------------------------------------------------------------------ sending (P2)

    private bool SendPlay2(IPacket packet)
    {
        switch (packet)
        {
            case WindowClickPacket c:
                SendWire(WireWriter.ForPacket(Ids1122.ClickWindow).WriteUByte((byte)c.WindowId).WriteShort(c.Slot).WriteSByte(c.EffectiveButton)
                    .WriteShort(c.ActionNumber).WriteVarInt(c.EffectiveMode).WriteSlot(c.ClickedItem), packet);
                return true;
            case TransactionPacket t:
                SendWire(WireWriter.ForPacket(Ids1122.ConfirmTransactionOut).WriteSByte(t.WindowId).WriteShort(t.ActionNumber).WriteBool(t.Accepted), packet);
                return true;
            case CloseWindowPacket cw:
                _windowTypes.Remove((byte)cw.WindowId);
                SendWire(WireWriter.ForPacket(Ids1122.CloseWindowOut).WriteUByte((byte)cw.WindowId), packet);
                return true;
            case PlayerDiggingPacket d:
            {
                // Drop / release / swap carry BlockPos.ORIGIN and DOWN (PlayerControllerMP, EntityPlayerSP.dropItem).
                bool atBlock = d.Status is PlayerDiggingPacket.StatusStarted or PlayerDiggingPacket.StatusCancelled or PlayerDiggingPacket.StatusFinished;
                var w = WireWriter.ForPacket(Ids1122.PlayerDigging).WriteVarInt(d.Status);
                if (atBlock) w.WritePosition(d.X, d.Y, d.Z).WriteUByte(d.Face > 5 ? (byte)0 : d.Face);
                else w.WritePosition(0, 0, 0).WriteUByte(0);
                SendWire(w, packet);
                return true;
            }
            case PlayerBlockPlacementPacket p when p.Direction == PlayerBlockPlacementPacket.NoDirection:
                SendWire(WireWriter.ForPacket(Ids1122.UseItem).WriteVarInt(p.Hand), packet); // CPacketPlayerTryUseItem
                return true;
            case PlayerBlockPlacementPacket p:
            {
                var (cx, cy, cz) = p.EffectiveCursor;
                SendWire(WireWriter.ForPacket(Ids1122.PlaceBlock).WritePosition(p.X, p.Y, p.Z).WriteVarInt(p.Direction).WriteVarInt(p.Hand)
                    .WriteFloat(cx).WriteFloat(cy).WriteFloat(cz), packet);
                return true;
            }
            case UseEntityPacket u:
            {
                // CPacketUseEntity.Action: 0 interact, 1 attack, 2 interact at (+ hit vector); interact and interact-at name a hand.
                var w = WireWriter.ForPacket(Ids1122.UseEntity).WriteVarInt(u.TargetEntityId);
                if (u.LeftClick) w.WriteVarInt(1);
                else if (u.InteractAt is { } at) w.WriteVarInt(2).WriteFloat(at.X).WriteFloat(at.Y).WriteFloat(at.Z).WriteVarInt(u.Hand);
                else w.WriteVarInt(0).WriteVarInt(u.Hand);
                SendWire(w, packet);
                return true;
            }
            case UpdateSignPacket sign:
            {
                var w = WireWriter.ForPacket(Ids1122.UpdateSign).WritePosition(sign.X, sign.Y, sign.Z);
                for (int i = 0; i < 4; i++) w.WriteString(i < sign.Lines.Length ? sign.Lines[i] : "", 384);
                SendWire(w, packet);
                return true;
            }
            default:
                return false;
        }
    }
}
