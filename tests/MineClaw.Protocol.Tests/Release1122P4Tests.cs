using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using static MineClaw.Protocol.Tests.TestUtil;

namespace MineClaw.Protocol.Tests;

/// <summary>
/// Multi-version P4 on the 1.12.2 wire: mob/object/orb/lightning/painting spawns, typed metadata per class
/// (<c>EntityDataManager.createKey</c> order) onto the canonical indices, exact moves, velocity, head look, effects, attributes,
/// passengers, the held-item change, experience, the death message, cooldowns, and the fake server's encoders. Wire layouts from
/// the decompiled <c>SPacket*.readPacketData</c> / protocol.json.
/// </summary>
public class Release1122P4Tests
{
    private static (Release1122Connection Client, Wire1122Peer Server) InPlay()
    {
        var (a, b) = DuplexStream.CreatePair();
        var client = new Release1122Connection(a) { Lang = LangTable.Builtin };
        var server = new Wire1122Peer(b);
        client.Send(new HandshakePacket("bot"));
        server.Read(); server.Read();
        server.Send(0x02, w => w.WriteString(Wire.OfflineUuid("bot").ToString()).WriteString("bot"));
        server.Send(0x23, w => w.WriteInt(7).WriteUByte(0).WriteInt(0).WriteUByte(2).WriteUByte(20).WriteString("default").WriteBool(false));
        Assert.IsType<LoginRequestPacket>(client.ReadPacket());
        server.Read(); server.Read(); // settings, brand
        return (client, server);
    }

    /// <summary>Spawn Mob: VarInt id, UUID, VarInt type, xyz doubles, yaw, pitch, head pitch, 3 velocity shorts, metadata.</summary>
    private static void SpawnMob(Wire1122Peer server, int eid, int type, double x, double y, double z, Action<WireWriter> meta, short vy = 0) =>
        server.Send(0x03, w =>
        {
            w.WriteVarInt(eid).WriteUuid(Guid.NewGuid()).WriteVarInt(type).WriteDouble(x).WriteDouble(y).WriteDouble(z)
                .WriteUByte(64).WriteUByte(0).WriteUByte(32).WriteShort(0).WriteShort(vy).WriteShort(0);
            meta(w);
            w.WriteUByte(0xFF);
        });

    private static T Next<T>(Release1122Connection c) where T : IPacket => Assert.IsType<T>(c.ReadPacket());

    [Fact]
    public void Creeper_metadata_state_powered_ignited_health_and_flags()
    {
        var (client, server) = InPlay();
        // Entity 0 flags 0x01 (burning), EntityLivingBase 7 health 18.5, EntityCreeper 12 state −1 (VarInt), 13 powered, 14 ignited.
        SpawnMob(server, 50, 50, 1.25, 64, -2.75, w => w
            .WriteUByte(0).WriteVarInt(0).WriteUByte(0x01)
            .WriteUByte(7).WriteVarInt(2).WriteFloat(18.5f)
            .WriteUByte(12).WriteVarInt(1).WriteVarInt(-1)
            .WriteUByte(13).WriteVarInt(6).WriteBool(true)
            .WriteUByte(14).WriteVarInt(6).WriteBool(false), vy: -628);
        var ms = Next<MobSpawnPacket>(client);
        Assert.Equal((50, (byte)50), (ms.EntityId, ms.Type));
        Assert.Equal((1.25, 64.0, -2.75), ms.Exact!.Value);
        var md = ms.Metadata;
        Assert.Equal((sbyte)0x01, md.Get(EntityMeta.Flags)!.Value.Value);
        Assert.Equal(18.5f, md.Get(EntityMeta.Health)!.Value.Value);
        Assert.Equal(-1, md.Get(EntityMeta.Mob16)!.Value.AsInt());   // b1.8.1's fuse index
        Assert.Equal(1, md.Get(EntityMeta.Mob17)!.Value.AsInt());    // charged
        Assert.Equal(0, md.Get(EntityMeta.CreeperIgnited)!.Value.AsInt());
        Assert.Equal(-628, Next<EntityVelocityPacket>(client).VelocityY);
        Assert.Equal(32, Next<EntityHeadLookMessage>(client).Yaw);
        // It starts fusing: state 1.
        server.Send(0x3C, w => w.WriteVarInt(50).WriteUByte(12).WriteVarInt(1).WriteVarInt(1).WriteUByte(0xFF));
        Assert.Equal(1, Next<EntityMetadataPacket>(client).Metadata.Get(EntityMeta.Mob16)!.Value.AsInt());
    }

    [Fact]
    public void Metadata_per_class_zombie_slime_wolf_enderman_skeleton_witch_guardian()
    {
        var (client, server) = InPlay();
        // Baby zombie with raised arms: EntityZombie 12 IS_CHILD, 13 VILLAGER_TYPE, 14 ARMS_RAISED.
        SpawnMob(server, 1, 54, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(6).WriteBool(true).WriteUByte(13).WriteVarInt(1).WriteVarInt(0)
            .WriteUByte(14).WriteVarInt(6).WriteBool(true));
        var z = Next<MobSpawnPacket>(client).Metadata;
        Assert.Equal(1, z.Get(EntityMeta.Baby)!.Value.AsInt());
        Assert.Equal(1, z.Get(EntityMeta.Aggressive)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        // Husk and zombie villager share the zombie layout; the villager adds 15 CONVERTING.
        SpawnMob(server, 2, 27, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(6).WriteBool(false).WriteUByte(15).WriteVarInt(6).WriteBool(true));
        var zv = Next<MobSpawnPacket>(client).Metadata;
        Assert.Equal((0, 1), (zv.Get(EntityMeta.Baby)!.Value.AsInt(), zv.Get(EntityMeta.Converting)!.Value.AsInt()));
        Next<EntityHeadLookMessage>(client);
        // Slime 12 SLIME_SIZE (VarInt) → b1.8.1's 16.
        SpawnMob(server, 3, 55, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(1).WriteVarInt(4));
        Assert.Equal(4, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Mob16)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        // Wolf: EntityAgeable 12 baby, EntityTameable 13 TAMED (2 = angry) → 16.
        SpawnMob(server, 4, 95, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(6).WriteBool(false).WriteUByte(13).WriteVarInt(0).WriteUByte(2));
        Assert.Equal(2, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Mob16)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        // Enderman 12 carried block (opt block state), 13 SCREAMING.
        SpawnMob(server, 5, 58, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(12).WriteVarInt(0).WriteUByte(13).WriteVarInt(6).WriteBool(true));
        Assert.Equal(1, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Screaming)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        // Stray (AbstractSkeleton 12 SWINGING_ARMS), witch (12 IS_DRINKING), guardian (13 TARGET_ENTITY).
        SpawnMob(server, 6, 6, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(6).WriteBool(true));
        Assert.Equal(1, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Aggressive)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        SpawnMob(server, 7, 66, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(6).WriteBool(true));
        Assert.Equal(1, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Drinking)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        SpawnMob(server, 8, 68, 0, 64, 0, w => w.WriteUByte(12).WriteVarInt(6).WriteBool(true).WriteUByte(13).WriteVarInt(1).WriteVarInt(7));
        Assert.Equal(7, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.TargetEntity)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        // Every serializer is skipped correctly: string, chat, rotation, position, opt pos, facing, opt UUID, NBT, then a known key.
        SpawnMob(server, 9, 92, 0, 64, 0, w =>
        {
            w.WriteUByte(2).WriteVarInt(3).WriteString("Bessie").WriteUByte(3).WriteVarInt(6).WriteBool(true);
            w.WriteUByte(20).WriteVarInt(4).WriteString("{\"text\":\"x\"}");
            w.WriteUByte(21).WriteVarInt(7).WriteFloat(1).WriteFloat(2).WriteFloat(3);
            w.WriteUByte(22).WriteVarInt(8).WritePosition(1, 2, 3).WriteUByte(23).WriteVarInt(9).WriteBool(true).WritePosition(4, 5, 6);
            w.WriteUByte(24).WriteVarInt(10).WriteVarInt(3).WriteUByte(25).WriteVarInt(11).WriteBool(true).WriteUuid(Guid.NewGuid());
            w.WriteUByte(26).WriteVarInt(13);
            Nbt.WriteRoot(w, new NbtCompound { ["a"] = 1 });
            w.WriteUByte(12).WriteVarInt(6).WriteBool(true);
        });
        Assert.Equal(1, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Baby)!.Value.AsInt()); // a calf
    }

    [Fact]
    public void Hand_states_compose_the_using_flag_and_players_get_absorption()
    {
        var (client, server) = InPlay();
        var fox = Wire.OfflineUuid("PoofImaFox");
        server.Send(0x2E, w => w.WriteVarInt(0).WriteVarInt(1).WriteUuid(fox).WriteString("PoofImaFox").WriteVarInt(0).WriteVarInt(0).WriteVarInt(20).WriteBool(false));
        Next<PlayerListItemPacket>(client);
        server.Send(0x05, w => w.WriteVarInt(77).WriteUuid(fox).WriteDouble(1.5).WriteDouble(65).WriteDouble(2.5).WriteUByte(0).WriteUByte(0).WriteUByte(0xFF));
        Next<NamedEntitySpawnPacket>(client);
        // Flags 0x08 sprinting, then hand states 0x03 (an item in use in the off hand: a raised shield), absorption 4.
        server.Send(0x3C, w => w.WriteVarInt(77).WriteUByte(0).WriteVarInt(0).WriteUByte(0x08).WriteUByte(0xFF));
        Assert.Equal((sbyte)0x08, Next<EntityMetadataPacket>(client).Metadata.Get(EntityMeta.Flags)!.Value.Value);
        server.Send(0x3C, w => w.WriteVarInt(77).WriteUByte(6).WriteVarInt(0).WriteUByte(0x03).WriteUByte(11).WriteVarInt(2).WriteFloat(4f).WriteUByte(0xFF));
        var md = Next<EntityMetadataPacket>(client).Metadata;
        Assert.Equal((sbyte)(0x08 | EntityMeta.FlagUsingItem), md.Get(EntityMeta.Flags)!.Value.Value);
        Assert.Equal(3, md.Get(EntityMeta.HandStates)!.Value.AsInt());
        Assert.Equal(4f, md.Get(EntityMeta.Absorption)!.Value.Value);
        // Our own metadata surfaces too (absorption hearts of the bot).
        server.Send(0x3C, w => w.WriteVarInt(7).WriteUByte(11).WriteVarInt(2).WriteFloat(2f).WriteUByte(0xFF));
        var self = Next<EntityMetadataPacket>(client);
        Assert.Equal((7, 2f), (self.EntityId, (float)self.Metadata.Get(EntityMeta.Absorption)!.Value.Value));
    }

    [Fact]
    public void Objects_orbs_lightning_paintings_and_item_frames()
    {
        var (client, server) = InPlay();
        // An arrow (type 60), data = shooter + 1, with velocity; critical (metadata 6 bit 0x01).
        server.Send(0x00, w => w.WriteVarInt(30).WriteUuid(Guid.NewGuid()).WriteUByte(60).WriteDouble(3.5).WriteDouble(66.1).WriteDouble(4.5)
            .WriteUByte(0).WriteUByte(0).WriteInt(8).WriteShort(8000).WriteShort(0).WriteShort(-8000));
        var arrow = Next<AddObjectVehiclePacket>(client);
        Assert.Equal((30, (byte)60, 8), (arrow.EntityId, arrow.Type, arrow.ThrowerEntityId));
        Assert.Equal((3.5, 66.1, 4.5), arrow.Exact!.Value);
        Assert.Equal((short)8000, Next<EntityVelocityPacket>(client).VelocityX);
        server.Send(0x3C, w => w.WriteVarInt(30).WriteUByte(6).WriteVarInt(0).WriteUByte(1).WriteUByte(0xFF));
        Assert.Equal(1, Next<EntityMetadataPacket>(client).Metadata.Get(EntityMeta.ArrowCritical)!.Value.AsInt());
        // An item frame (71) shows its item (metadata 6 slot) as the canonical Item.
        server.Send(0x00, w => w.WriteVarInt(31).WriteUuid(Guid.NewGuid()).WriteUByte(71).WriteDouble(0).WriteDouble(65).WriteDouble(0)
            .WriteUByte(0).WriteUByte(0).WriteInt(2).WriteShort(0).WriteShort(0).WriteShort(0));
        Next<AddObjectVehiclePacket>(client);
        server.Send(0x3C, w => w.WriteVarInt(31).WriteUByte(6).WriteVarInt(5).WriteSlot(new ItemStack(Items.Diamond, 1, 0)).WriteUByte(0xFF));
        Assert.Equal(Items.Diamond, ((ItemStack)Next<EntityMetadataPacket>(client).Metadata.Get(EntityMeta.Item)!.Value.Value).Id);
        // Experience orb: VarInt id, xyz doubles, short count.
        server.Send(0x01, w => w.WriteVarInt(32).WriteDouble(1).WriteDouble(64.5).WriteDouble(1).WriteShort(7));
        var orb = Next<ExperienceOrbPacket>(client);
        Assert.Equal((32, (short)7, 64.5), (orb.EntityId, orb.Count, orb.Exact!.Value.Y));
        server.Send(0x26, w => w.WriteVarInt(32).WriteShort(1024).WriteShort(0).WriteShort(0).WriteBool(true));
        Assert.Equal(1.25, Next<EntityTeleportPacket>(client).Exact!.Value.X);
        // Lightning (Spawn Global Entity type 1) and a painting (title, position, facing 2 = north → b1.8.1's 0).
        server.Send(0x02, w => w.WriteVarInt(33).WriteUByte(1).WriteDouble(10).WriteDouble(70).WriteDouble(-10));
        Assert.Equal(FixedPoint.ToAbsolute(10), Next<ThunderboltPacket>(client).X);
        server.Send(0x04, w => w.WriteVarInt(34).WriteUuid(Guid.NewGuid()).WriteString("Kebab").WritePosition(5, 66, 5).WriteUByte(2));
        var art = Next<EntityPaintingPacket>(client);
        Assert.Equal(("Kebab", 5, 66, 5, 0), (art.Title, art.X, art.Y, art.Z, art.Direction));
    }

    [Fact]
    public void Relative_moves_stay_exact_and_teleports_are_exact()
    {
        var (client, server) = InPlay();
        SpawnMob(server, 40, 54, 0.3, 64, 0.7, _ => { });
        Next<MobSpawnPacket>(client);
        Next<EntityHeadLookMessage>(client);
        // 100 moves of 1/4096 east and −3/4096 down: the canonical 1/32 grid would round each to 0; the exact sum survives.
        for (int i = 0; i < 100; i++) server.Send(0x27, w => w.WriteVarInt(40).WriteShort(1).WriteShort(-3).WriteShort(0).WriteUByte(128).WriteUByte(0).WriteBool(true));
        EntityTeleportPacket t = null!;
        for (int i = 0; i < 100; i++) t = Next<EntityTeleportPacket>(client);
        Assert.Equal(0.3 + 100 / 4096.0, t.Exact!.Value.X, 12);
        Assert.Equal(64 - 300 / 4096.0, t.Exact!.Value.Y, 12);
        Assert.Equal(128, t.Yaw);
        server.Send(0x4C, w => w.WriteVarInt(40).WriteDouble(-7.123456).WriteDouble(70.000001).WriteDouble(3.999999).WriteUByte(0).WriteUByte(0).WriteBool(true));
        Assert.Equal((-7.123456, 70.000001, 3.999999), Next<EntityTeleportPacket>(client).Exact!.Value);
        // Look only, head look, velocity, a no-op Entity packet; then destroy.
        server.Send(0x28, w => w.WriteVarInt(40).WriteUByte(10).WriteUByte(20).WriteBool(true));
        Assert.Equal((10, 20), ((int)Next<EntityLookPacket>(client).Yaw, 20));
        server.Send(0x25, w => w.WriteVarInt(40));
        server.Send(0x36, w => w.WriteVarInt(40).WriteUByte(200));
        Assert.Equal(200, Next<EntityHeadLookMessage>(client).Yaw);
        server.Send(0x3E, w => w.WriteVarInt(7).WriteShort(0).WriteShort(3200).WriteShort(-1600)); // our own knockback
        var v = Next<EntityVelocityPacket>(client);
        Assert.Equal((7, (short)3200, (short)-1600), (v.EntityId, v.VelocityY, v.VelocityZ));
        server.Send(0x32, w => w.WriteVarInt(1).WriteVarInt(40));
        Assert.Equal(40, Next<DestroyEntityPacket>(client).EntityId);
    }

    [Fact]
    public void Status_equipment_effects_attributes_passengers_experience_death_cooldown_held_item()
    {
        var (client, server) = InPlay();
        // Entity Status: 37 (burning hurt) → canonical 2; 35 totem and 30 shield disabled pass through.
        server.Send(0x1B, w => w.WriteInt(7).WriteSByte(37));
        Assert.Equal(2, Next<EntityStatusPacket>(client).Status);
        server.Send(0x1B, w => w.WriteInt(7).WriteSByte(35));
        Assert.Equal(35, Next<EntityStatusPacket>(client).Status);
        // Entity Effect: VarInt id, byte effect, byte amplifier, VarInt duration, byte flags; Remove: VarInt id, byte effect.
        server.Send(0x4F, w => w.WriteVarInt(7).WriteSByte(3).WriteSByte(1).WriteVarInt(600).WriteUByte(0));
        var ef = Next<EntityEffectPacket>(client);
        Assert.Equal((7, (sbyte)3, (sbyte)1, (short)600), (ef.EntityId, ef.EffectId, ef.Amplifier, ef.Duration));
        server.Send(0x4F, w => w.WriteVarInt(7).WriteSByte(16).WriteSByte(0).WriteVarInt(1_000_000).WriteUByte(0));
        Assert.Equal(short.MaxValue, Next<EntityEffectPacket>(client).Duration);
        server.Send(0x33, w => w.WriteVarInt(7).WriteUByte(3));
        Assert.Equal(3, Next<RemoveEntityEffectPacket>(client).EffectId);
        // Entity Properties: int count; key, base double, VarInt modifiers (UUID, double, byte op). A sprinting player: 0.1 × 1.3.
        server.Send(0x4E, w => w.WriteVarInt(7).WriteInt(2)
            .WriteString("generic.movementSpeed").WriteDouble(0.1).WriteVarInt(1).WriteUuid(Guid.NewGuid()).WriteDouble(0.30000001192092896).WriteUByte(2)
            .WriteString("generic.attackSpeed").WriteDouble(4.0).WriteVarInt(1).WriteUuid(Guid.NewGuid()).WriteDouble(-2.4000000953674316).WriteUByte(0));
        var pm = Next<EntityPropertiesMessage>(client);
        Assert.Equal(0.13, pm.Attributes[0].Value, 7);
        Assert.Equal(1.6, pm.Attributes[1].Value, 6);
        // Set Passengers: the vehicle and all riders; a new list without a rider dismounts it.
        server.Send(0x43, w => w.WriteVarInt(100).WriteVarInt(2).WriteVarInt(7).WriteVarInt(8));
        var m1 = Next<AttachEntityPacket>(client);
        var m2 = Next<AttachEntityPacket>(client);
        Assert.Equal([(7, 100), (8, 100)], new[] { (m1.EntityId, m1.VehicleId), (m2.EntityId, m2.VehicleId) });
        server.Send(0x43, w => w.WriteVarInt(100).WriteVarInt(1).WriteVarInt(8));
        var off = Next<AttachEntityPacket>(client);
        Assert.Equal((7, -1), (off.EntityId, off.VehicleId));
        // Set Experience (float bar, VarInt level, VarInt total), Combat Event ENTITY_DIED, Set Cooldown, Held Item Change.
        server.Send(0x40, w => w.WriteFloat(0.5f).WriteVarInt(3).WriteVarInt(27));
        var xp = Next<ExperienceMessage>(client);
        Assert.Equal((0.5f, 3, 27), (xp.Bar, xp.Level, xp.Total));
        server.Send(0x2D, w => w.WriteVarInt(0)); // enter combat: nothing
        server.Send(0x2D, w => w.WriteVarInt(2).WriteVarInt(7).WriteInt(40).WriteString("{\"translate\":\"death.attack.mob\",\"with\":[\"bot\",\"Zombie\"]}"));
        var death = Next<DeathMessage>(client);
        Assert.Equal((7, 40, "bot was slain by Zombie"), (death.PlayerId, death.KillerId, death.Message));
        server.Send(0x17, w => w.WriteVarInt(442).WriteVarInt(100));
        var cd = Next<ItemCooldownMessage>(client);
        Assert.Equal((442, 100), (cd.ItemId, cd.Ticks));
        server.Send(0x3A, w => w.WriteSByte(4));
        Assert.Equal(4, Next<HoldingChangePacket>(client).Slot);
    }

    [Fact]
    public void Server_connection_round_trips_the_p4_messages_with_class_layouts()
    {
        var (a, b) = DuplexStream.CreatePair();
        var client = new Release1122Connection(a) { Lang = LangTable.Builtin };
        var server = new Release1122ServerConnection(b);
        var serverRead = Task.Run(() => (server.ReadPacket(), server.ReadPacket()));
        client.Send(new HandshakePacket("maid_chan"));
        serverRead.Wait();
        server.Send(new LoginRequestPacket { EntityId = 42, Dimension = 0, Difficulty = 2, MaxPlayers = 8 });
        Next<LoginRequestPacket>(client);

        // A creeper: canonical 16/17 + health → its class keys 12/13 + 7 and back.
        var meta = new EntityMetadata([MetadataEntry.FromByte(0, 0), MetadataEntry.FromInt(EntityMeta.Mob16, 1), MetadataEntry.FromByte(EntityMeta.Mob17, 1),
            MetadataEntry.FromFloat(EntityMeta.Health, 12.5f)]);
        server.Send(new MobSpawnPacket { EntityId = 9, Type = 50, Exact = (2.5, 64, -1.5), Metadata = meta, Yaw = 10 });
        var ms = Next<MobSpawnPacket>(client);
        Assert.Equal(((byte)50, (2.5, 64.0, -1.5)), (ms.Type, ms.Exact!.Value));
        Assert.Equal((1, 1, 12.5f), (ms.Metadata.Get(EntityMeta.Mob16)!.Value.AsInt(), ms.Metadata.Get(EntityMeta.Mob17)!.Value.AsInt(),
            (float)ms.Metadata.Get(EntityMeta.Health)!.Value.Value));
        Next<EntityHeadLookMessage>(client);
        // A later baby flag for a zombie and a slime size go to their own classes' keys.
        server.Send(new MobSpawnPacket { EntityId = 10, Type = 54, Metadata = new EntityMetadata([MetadataEntry.FromByte(EntityMeta.Baby, 1)]) });
        Assert.Equal(1, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Baby)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        server.Send(new EntityMetadataPacket { EntityId = 10, Metadata = new EntityMetadata([MetadataEntry.FromByte(EntityMeta.Aggressive, 1)]) });
        Assert.Equal(1, Next<EntityMetadataPacket>(client).Metadata.Get(EntityMeta.Aggressive)!.Value.AsInt());

        server.Send(new EntityEffectPacket { EntityId = 42, EffectId = 19, Amplifier = 0, Duration = 140 });
        Assert.Equal(((sbyte)19, (short)140), (Next<EntityEffectPacket>(client).EffectId, (short)140));
        server.Send(new RemoveEntityEffectPacket { EntityId = 42, EffectId = 19 });
        Assert.Equal(19, Next<RemoveEntityEffectPacket>(client).EffectId);
        server.Send(new ExperienceOrbPacket { EntityId = 11, Count = 3, Exact = (2.5, 64.2, -1.5) });
        Assert.Equal(3, Next<ExperienceOrbPacket>(client).Count);
        server.Send(new ExperienceMessage { Bar = 0.25f, Level = 1, Total = 10 });
        Assert.Equal(10, Next<ExperienceMessage>(client).Total);
        server.Send(new AttachEntityPacket { EntityId = 42, VehicleId = 12 });
        Assert.Equal(12, Next<AttachEntityPacket>(client).VehicleId);
        server.Send(new AttachEntityPacket { EntityId = 42, VehicleId = -1 });
        Assert.Equal((42, -1), Next<AttachEntityPacket>(client) is var d ? (d.EntityId, d.VehicleId) : default);
        server.Send(new HoldingChangePacket { Slot = 3 });
        Assert.Equal(3, Next<HoldingChangePacket>(client).Slot);
        server.Send(new EntityEquipmentPacket { EntityId = 9, Slot = 5, ItemId = 442 });
        Assert.Equal(5, Next<EntityEquipmentPacket>(client).Slot);
        server.Send(new DeathMessage { PlayerId = 42, KillerId = 9, Message = "maid_chan was blown up by Creeper" });
        Assert.Equal("maid_chan was blown up by Creeper", Next<DeathMessage>(client).Message);
        server.Send(new ItemCooldownMessage { ItemId = 442, Ticks = 100 });
        Assert.Equal(100, Next<ItemCooldownMessage>(client).Ticks);
        server.Send(new EntityPropertiesMessage { EntityId = 9, Attributes = [new EntityAttribute("generic.movementSpeed", 0.25, [])] });
        Assert.Equal(0.25, Next<EntityPropertiesMessage>(client).Attributes[0].Value);
    }

    [Fact]
    public void Entity_effect_golden_bytes()
    {
        // SPacketEntityEffect.writePacketData: VarInt 300 (AC 02), byte 1 (speed), byte 1, VarInt 200 (C8 01), byte flags 0.
        var (a, b) = DuplexStream.CreatePair();
        var server = new Release1122ServerConnection(b) { CompressionThreshold = -1 };
        var peer = new Wire1122Peer(a);
        var read = Task.Run(() => server.ReadPacket());
        peer.Send(0x00, w => w.WriteVarInt(340).WriteString("x").WriteUShort(25565).WriteVarInt(2));
        peer.Send(0x00, w => w.WriteString("bot"));
        read.Wait();
        peer.Read(); // login success
        server.Send(new EntityEffectPacket { EntityId = 300, EffectId = 1, Amplifier = 1, Duration = 200 });
        var (id, body) = peer.Read();
        Assert.Equal((0x4F, "AC 02 01 01 C8 01 00"), (id, ToHex(body)));
    }
}
