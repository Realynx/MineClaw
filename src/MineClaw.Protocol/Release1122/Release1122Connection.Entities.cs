using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Release1122;

/// <summary>Play-state packet ids of the P4 entity set (protocol.json's play.toClient mappings = <c>EnumConnectionState</c>).</summary>
public static class EntityIds1122
{
    public const int SpawnXpOrb = 0x01, SpawnGlobal = 0x02, SpawnMob = 0x03, SpawnPainting = 0x04, SetCooldown = 0x17,
        CombatEvent = 0x2D, UseBed = 0x30, RemoveEffect = 0x33, HeadLook = 0x36, AttachEntity = 0x3D, Velocity = 0x3E,
        SetExperience = 0x40, SetPassengers = 0x43, EntityProperties = 0x4E, EntityEffect = 0x4F, Entity = 0x25;
    /// <summary>Spawn Global Entity type of a lightning bolt.</summary>
    public const int GlobalLightning = 1;
    /// <summary>Entity Status values that are "hurt" in <c>EntityLivingBase.handleStatusUpdate</c> (2 hurt, 33 thorns, 36 drowning,
    /// 37 burning): all surface as the canonical 2.</summary>
    public static bool IsHurtStatus(int s) => s is 2 or 33 or 36 or 37;
}

/// <summary>
/// Multi-version P4 of the 1.12.2 client connection: every entity kind (mobs, objects, players, orbs, paintings, lightning),
/// moves resolved exactly, typed metadata translated per class (<see cref="EntityMeta1122"/>), velocity, head look, effects,
/// attributes, passengers (riding), the server's held-item change, experience, the death message and item cooldowns. Wire formats
/// from the decompiled <c>SPacket*.readPacketData</c>, cross-checked with protocol.json.
/// </summary>
public sealed partial class Release1122Connection
{
    /// <summary>An entity the connection knows: its exact position (1.12.2's 1/4096 relative moves become absolute canonical
    /// teleports without drift), its class (the metadata layout) and its last wire flags and hand states.</summary>
    private sealed class Tracked
    {
        public double X, Y, Z;
        public byte Yaw, Pitch;
        public EntityWireKind Kind;
        public int Type;
        public bool IsItem => Kind == EntityWireKind.Object && Type == Ids1122.ObjectItem;
        /// <summary>An item entity waits for its metadata (the stack) before it is surfaced as a pickup spawn.</summary>
        public bool Surfaced;
        public short VX, VY, VZ;
        public int RawFlags, HandStates;
    }

    // vehicle → its passengers (Set Passengers lists them all; the ones missing from a new list dismounted)
    private readonly Dictionary<int, int[]> _passengers = new();

    private IPacket? TranslateEntities(int id, ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        switch (id)
        {
            // ---- spawns ----
            case Ids1122.SpawnObject:
            {
                int eid = r.ReadVarInt();
                r.ReadUuid();
                int type = r.ReadUByte();
                var e = new Tracked { X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(), Kind = EntityWireKind.Object, Type = type };
                e.Pitch = r.ReadUByte();
                e.Yaw = r.ReadUByte();
                int data = r.ReadInt();
                (e.VX, e.VY, e.VZ) = (r.ReadShort(), r.ReadShort(), r.ReadShort());
                _entities[eid] = e;
                if (e.IsItem) return null; // surfaced when its stack arrives (the Entity Metadata right after)
                e.Surfaced = true;
                if (e.VX != 0 || e.VY != 0 || e.VZ != 0) _extra.Enqueue(new EntityVelocityPacket { EntityId = eid, VelocityX = e.VX, VelocityY = e.VY, VelocityZ = e.VZ });
                // handleSpawnObject: data = the shooter's id + 1 for arrows, the facing for item frames, the block state for falling blocks.
                return new AddObjectVehiclePacket
                {
                    EntityId = eid, Type = (byte)type, X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z),
                    ThrowerEntityId = data, SpeedX = e.VX, SpeedY = e.VY, SpeedZ = e.VZ, Exact = (e.X, e.Y, e.Z),
                };
            }
            case EntityIds1122.SpawnMob:
            {
                int eid = r.ReadVarInt();
                r.ReadUuid();
                int type = r.ReadVarInt();
                var e = new Tracked { X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(), Kind = EntityWireKind.Mob, Type = type, Surfaced = true };
                e.Yaw = r.ReadUByte();
                e.Pitch = r.ReadUByte();
                byte head = r.ReadUByte();
                (e.VX, e.VY, e.VZ) = (r.ReadShort(), r.ReadShort(), r.ReadShort());
                int pos = r.Position;
                var raw = EntityMeta1122.Read(body, ref pos);
                _entities[eid] = e;
                var md = EntityMeta1122.ToCanonical(EntityWireKind.Mob, type, raw, ref e.RawFlags, ref e.HandStates);
                if (e.VX != 0 || e.VY != 0 || e.VZ != 0) _extra.Enqueue(new EntityVelocityPacket { EntityId = eid, VelocityX = e.VX, VelocityY = e.VY, VelocityZ = e.VZ });
                _extra.Enqueue(new EntityHeadLookMessage { EntityId = eid, Yaw = head });
                return new MobSpawnPacket
                {
                    EntityId = eid, Type = (byte)Math.Clamp(type, 0, 255), X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y),
                    Z = FixedPoint.ToAbsolute(e.Z), Yaw = e.Yaw, Pitch = e.Pitch, Metadata = md, Exact = (e.X, e.Y, e.Z),
                };
            }
            case EntityIds1122.SpawnXpOrb:
            {
                int eid = r.ReadVarInt();
                var e = new Tracked { X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(), Kind = EntityWireKind.Orb, Surfaced = true };
                short xp = r.ReadShort();
                _entities[eid] = e;
                return new ExperienceOrbPacket
                {
                    EntityId = eid, X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z), Count = xp, Exact = (e.X, e.Y, e.Z),
                };
            }
            case EntityIds1122.SpawnGlobal:
            {
                int eid = r.ReadVarInt();
                int type = r.ReadUByte();
                double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
                if (type != EntityIds1122.GlobalLightning) return null;
                return new ThunderboltPacket { EntityId = eid, X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(y), Z = FixedPoint.ToAbsolute(z) };
            }
            case EntityIds1122.SpawnPainting:
            {
                int eid = r.ReadVarInt();
                r.ReadUuid();
                var title = r.ReadString(13);
                var (x, y, z) = r.ReadPosition();
                int facing = r.ReadUByte(); // EnumFacing horizontal index: 0 south, 1 west, 2 north, 3 east
                _entities[eid] = new Tracked { X = x + 0.5, Y = y + 0.5, Z = z + 0.5, Kind = EntityWireKind.Painting, Surfaced = true };
                // → b1.8.1's 0 −Z, 1 −X, 2 +Z, 3 +X.
                return new EntityPaintingPacket { EntityId = eid, Title = title, X = x, Y = y, Z = z, Direction = facing switch { 0 => 2, 1 => 1, 2 => 0, _ => 3 } };
            }
            case Ids1122.SpawnPlayer:
            {
                int eid = r.ReadVarInt();
                var uuid = r.ReadUuid();
                var e = new Tracked { X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(), Kind = EntityWireKind.Player, Surfaced = true };
                e.Yaw = r.ReadUByte();
                e.Pitch = r.ReadUByte();
                _entities[eid] = e;
                string name = _names.TryGetValue(uuid, out var n) ? n : uuid.ToString("N")[..16];
                int pos = r.Position;
                if (pos < body.Length)
                {
                    var md = EntityMeta1122.ToCanonical(EntityWireKind.Player, 0, EntityMeta1122.Read(body, ref pos), ref e.RawFlags, ref e.HandStates);
                    if (md.Count > 0) _extra.Enqueue(new EntityMetadataPacket { EntityId = eid, Metadata = md });
                }
                return new NamedEntitySpawnPacket
                {
                    EntityId = eid, PlayerName = name, X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z),
                    Yaw = e.Yaw, Pitch = e.Pitch, Exact = (e.X, e.Y, e.Z),
                };
            }

            // ---- metadata ----
            case Ids1122.EntityMetadata:
            {
                int eid = r.ReadVarInt();
                if (!_entities.TryGetValue(eid, out var e))
                {
                    if (eid != _selfId) return null;
                    _entities[eid] = e = new Tracked { Kind = EntityWireKind.Player, Surfaced = true }; // our own flags and health facts
                }
                int pos = r.Position;
                var raw = EntityMeta1122.Read(body, ref pos);
                if (e.IsItem)
                {
                    ItemStack? stack = null;
                    foreach (var m in raw) if (m.Index == Codec1122.ItemMetadataIndex && m.Value is ItemStack s) stack = s;
                    if (stack is not { } st) return null;
                    if (!e.Surfaced)
                    {
                        if (st.IsEmpty) return null;
                        e.Surfaced = true;
                        return new PickupSpawnPacket
                        {
                            EntityId = eid, ItemId = st.Id, Count = st.Count, Damage = st.Damage,
                            X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z),
                            MotionX = Motion(e.VX), MotionY = Motion(e.VY), MotionZ = Motion(e.VZ), Exact = (e.X, e.Y, e.Z),
                        };
                    }
                    return new ItemEntityStackMessage { EntityId = eid, Item = st };
                }
                var md = EntityMeta1122.ToCanonical(e.Kind, e.Type, raw, ref e.RawFlags, ref e.HandStates);
                return md.Count == 0 || !e.Surfaced ? null : new EntityMetadataPacket { EntityId = eid, Metadata = md };
            }

            // ---- moves ----
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
            case EntityIds1122.HeadLook:
            {
                int eid = r.ReadVarInt();
                byte yaw = r.ReadUByte();
                return _entities.TryGetValue(eid, out var e) && e.Surfaced ? new EntityHeadLookMessage { EntityId = eid, Yaw = yaw } : null;
            }
            case EntityIds1122.Velocity:
            {
                // handleEntityVelocity: motion = value / 8000 (the b1.8.1 unit); our own id is knockback.
                int eid = r.ReadVarInt();
                return new EntityVelocityPacket { EntityId = eid, VelocityX = r.ReadShort(), VelocityY = r.ReadShort(), VelocityZ = r.ReadShort() };
            }
            case EntityIds1122.Entity:
                return null; // SPacketEntity with no move: a keep-alive of the tracker
            case Ids1122.DestroyEntities:
            {
                int n = r.ReadVarInt();
                if (n < 0 || n > r.Remaining) throw new ProtocolException($"destroy entities count {n} out of range");
                var gone = new List<DestroyEntityPacket>(n);
                for (int i = 0; i < n; i++)
                {
                    int eid = r.ReadVarInt();
                    _passengers.Remove(eid);
                    if (_entities.Remove(eid, out var e) && e.Surfaced) gone.Add(new DestroyEntityPacket { EntityId = eid });
                }
                return First(gone);
            }

            // ---- state ----
            case Ids1122.CollectItem:
                return new CollectItemPacket { CollectedEntityId = r.ReadVarInt(), CollectorEntityId = r.ReadVarInt() }; // + pickup count
            case Ids1122.EntityStatus:
            {
                int eid = r.ReadInt();
                sbyte status = r.ReadSByte();
                return new EntityStatusPacket { EntityId = eid, Status = EntityIds1122.IsHurtStatus(status) ? (sbyte)2 : status };
            }
            case Ids1122.EntityAnimation:
            {
                int eid = r.ReadVarInt();
                // SPacketAnimation (NetHandlerPlayClient.handleAnimation): 0 swing main arm, 1 hurt, 2 wake up, 3 swing off hand, 4 crit,
                // 5 magic crit → b1.8.1's 1/2/3, and canonical 6/7 for the crits (b1.8.1's wire has none; its 5 is "start eating");
                // the off hand is a swing (1) with Hand = 1.
                int raw = r.ReadUByte();
                sbyte anim = (sbyte)(raw switch { 0 or 3 => 1, 1 => 2, 2 => 3, 4 => 6, 5 => 7, _ => 0 });
                return anim == 0 ? null : new AnimationPacket { EntityId = eid, Animation = anim, Hand = raw == 3 ? (byte)1 : (byte)0 };
            }
            case Ids1122.EntityEquipment:
            {
                int eid = r.ReadVarInt();
                int slot = r.ReadVarInt();
                var item = Codec1122.ReadSlot(ref r);
                // EntityEquipmentSlot: 0 main hand, 1 off hand, 2 feet, 3 legs, 4 chest, 5 head → canonical 0 held, 1..4 boots..helmet, 5 off hand.
                short cslot = slot switch { 0 => 0, 1 => 5, 2 => 1, 3 => 2, 4 => 3, 5 => 4, _ => -1 };
                if (cslot < 0) return null;
                return new EntityEquipmentPacket { EntityId = eid, Slot = cslot, ItemId = item.IsEmpty ? (short)-1 : item.Id, Damage = item.Damage, Tag = item.IsEmpty ? null : item.Tag };
            }
            case EntityIds1122.EntityEffect:
            {
                // SPacketEntityEffect: VarInt entity, byte effect, byte amplifier, VarInt duration (32767 = "max"), byte flags.
                int eid = r.ReadVarInt();
                sbyte effect = r.ReadSByte(), amp = r.ReadSByte();
                int duration = r.ReadVarInt();
                return new EntityEffectPacket { EntityId = eid, EffectId = effect, Amplifier = amp, Duration = (short)Math.Clamp(duration, 0, short.MaxValue) };
            }
            case EntityIds1122.RemoveEffect:
                return new RemoveEntityEffectPacket { EntityId = r.ReadVarInt(), EffectId = (sbyte)r.ReadUByte() };
            case EntityIds1122.EntityProperties:
            {
                int eid = r.ReadVarInt();
                int n = r.ReadInt();
                if (n < 0 || n > r.Remaining) throw new ProtocolException($"entity properties count {n} out of range");
                var attrs = new List<EntityAttribute>(n);
                for (int i = 0; i < n; i++)
                {
                    var key = r.ReadString(64);
                    double baseValue = r.ReadDouble();
                    int m = r.ReadVarInt();
                    if (m < 0 || m > r.Remaining) throw new ProtocolException($"attribute modifier count {m} out of range");
                    var mods = new AttributeModifierValue[m];
                    for (int j = 0; j < m; j++) mods[j] = new AttributeModifierValue(r.ReadUuid(), r.ReadDouble(), r.ReadUByte());
                    attrs.Add(new EntityAttribute(key, baseValue, mods));
                }
                return new EntityPropertiesMessage { EntityId = eid, Attributes = attrs };
            }
            case EntityIds1122.SetPassengers:
            {
                int vehicle = r.ReadVarInt();
                int n = r.ReadVarInt();
                if (n < 0 || n > r.Remaining) throw new ProtocolException($"passenger count {n} out of range");
                var now = new int[n];
                for (int i = 0; i < n; i++) now[i] = r.ReadVarInt();
                var changes = new List<AttachEntityPacket>();
                if (_passengers.TryGetValue(vehicle, out var before))
                    foreach (int p in before)
                        if (Array.IndexOf(now, p) < 0) changes.Add(new AttachEntityPacket { EntityId = p, VehicleId = -1 });
                foreach (int p in now)
                    if (before is null || Array.IndexOf(before, p) < 0) changes.Add(new AttachEntityPacket { EntityId = p, VehicleId = vehicle });
                if (n == 0) _passengers.Remove(vehicle); else _passengers[vehicle] = now;
                return First(changes);
            }
            case EntityIds1122.AttachEntity:
                return null; // 1.12.2 Attach Entity is a leash (holder id), not riding
            case EntityIds1122.UseBed:
            {
                int eid = r.ReadVarInt();
                var (x, y, z) = r.ReadPosition();
                return new UseBedPacket { EntityId = eid, X = x, Y = y, Z = z };
            }

            // ---- the local player ----
            case Ids1122.HeldItemChangeIn:
                return new HoldingChangePacket { Slot = r.ReadSByte() }; // handleHeldItemChange: the server picked the hotbar slot
            case EntityIds1122.SetExperience:
                return new ExperienceMessage { Bar = r.ReadFloat(), Level = r.ReadVarInt(), Total = r.ReadVarInt() };
            case EntityIds1122.CombatEvent:
            {
                int ev = r.ReadVarInt(); // 0 enter combat, 1 end combat, 2 entity died
                if (ev != 2) return null;
                int player = r.ReadVarInt();
                int killer = r.ReadInt();
                return new DeathMessage { PlayerId = player, KillerId = killer, Message = ChatComponent.ToPlain(r.ReadString(), Lang) };
            }
            case EntityIds1122.SetCooldown:
                return new ItemCooldownMessage { ItemId = r.ReadVarInt(), Ticks = r.ReadVarInt() };
            default:
                return null; // sounds, particles, advancements, recipe book, boss bars, scoreboards…: recorded, not surfaced
        }
    }

    private static EntityTeleportPacket Teleport(int eid, Tracked e) => new()
    {
        EntityId = eid, X = FixedPoint.ToAbsolute(e.X), Y = FixedPoint.ToAbsolute(e.Y), Z = FixedPoint.ToAbsolute(e.Z), Yaw = e.Yaw, Pitch = e.Pitch,
        Exact = (e.X, e.Y, e.Z),
    };

    /// <summary>Velocity (blocks/tick × 8000) → b1.8.1's pickup motion byte (× 128).</summary>
    private static sbyte Motion(short v) => (sbyte)Math.Clamp((int)Math.Round(v / 8000.0 * 128), sbyte.MinValue, sbyte.MaxValue);
}
