using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using static MineClaw.Protocol.Tests.TestUtil;

namespace MineClaw.Protocol.Tests;

/// <summary>
/// The browser viewer's render facts on the 1.12.2 wire: off-hand swings and crits (SPacketAnimation 3/4/5), the metadata of
/// looks (horse colour/markings/armour, llama, villager profession, wolf collar, item frame rotation, boat wood, armour stand
/// flags and pose) onto canonical 50+, and block entities (beds, banners, skulls, flower pots, spawners) from Update Block Entity
/// and chunk data, both ways through the fake server's connection.
/// </summary>
public class Release1122LooksTests
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
        server.Read(); server.Read();
        return (client, server);
    }

    private static T Next<T>(Release1122Connection c) where T : IPacket => Assert.IsType<T>(c.ReadPacket());

    private static void SpawnMob(Wire1122Peer server, int eid, int type, Action<WireWriter> meta) =>
        server.Send(0x03, w =>
        {
            w.WriteVarInt(eid).WriteUuid(Guid.NewGuid()).WriteVarInt(type).WriteDouble(0.5).WriteDouble(64).WriteDouble(0.5)
                .WriteUByte(0).WriteUByte(0).WriteUByte(0).WriteShort(0).WriteShort(0).WriteShort(0);
            meta(w);
            w.WriteUByte(0xFF);
        });

    [Fact]
    public void Animations_keep_the_off_hand_and_the_crits()
    {
        var (client, server) = InPlay();
        (sbyte, byte) Anim(byte raw)
        {
            server.Send(0x06, w => w.WriteVarInt(9).WriteUByte(raw));
            var a = Next<AnimationPacket>(client);
            return (a.Animation, a.Hand);
        }
        Assert.Equal(((sbyte)1, (byte)0), Anim(0)); // main-hand swing: the b1.8.1 swing, as before
        Assert.Equal(((sbyte)1, (byte)1), Anim(3)); // off-hand swing: still a swing for the bot, the hand says which
        Assert.Equal(((sbyte)2, (byte)0), Anim(1));
        Assert.Equal(((sbyte)6, (byte)0), Anim(4)); // crit: canonical 6 (b1.8.1's wire has no crit; its 5 is start eating)
        Assert.Equal(((sbyte)7, (byte)0), Anim(5)); // magic crit
    }

    [Fact]
    public void Looks_metadata_lands_on_the_canonical_50_plus_indices()
    {
        var (client, server) = InPlay();
        // Horse: 12 baby, 13 status (tame 0x02 | saddled 0x04), 15 variant chestnut (2) + white dots (3 << 8), 16 armour gold (2).
        SpawnMob(server, 1, 100, w => w.WriteUByte(12).WriteVarInt(6).WriteBool(true).WriteUByte(13).WriteVarInt(0).WriteUByte(0x06)
            .WriteUByte(15).WriteVarInt(1).WriteVarInt(2 | 3 << 8).WriteUByte(16).WriteVarInt(1).WriteVarInt(2));
        var h = Next<MobSpawnPacket>(client).Metadata;
        Assert.Equal((1, 6, 2 | 3 << 8, 2), (h.Get(EntityMeta.Baby)!.Value.AsInt(), h.Get(EntityMeta.HorseStatus)!.Value.AsInt(),
            h.Get(EntityMeta.HorseVariant)!.Value.AsInt(), h.Get(EntityMeta.HorseArmor)!.Value.AsInt()));
        Next<EntityHeadLookMessage>(client);
        // Llama: 15 chest, 17 carpet red (14), 18 brown (2).
        SpawnMob(server, 2, 103, w => w.WriteUByte(15).WriteVarInt(6).WriteBool(true).WriteUByte(17).WriteVarInt(1).WriteVarInt(14)
            .WriteUByte(18).WriteVarInt(1).WriteVarInt(2));
        var l = Next<MobSpawnPacket>(client).Metadata;
        Assert.Equal((1, 14, 2), (l.Get(EntityMeta.HasChest)!.Value.AsInt(), l.Get(EntityMeta.CarpetColor)!.Value.AsInt(), l.Get(EntityMeta.LlamaVariant)!.Value.AsInt()));
        Next<EntityHeadLookMessage>(client);
        // Villager 13 profession librarian; wolf 13 tame flags + 17 collar blue.
        SpawnMob(server, 3, 120, w => w.WriteUByte(13).WriteVarInt(1).WriteVarInt(1));
        Assert.Equal(1, Next<MobSpawnPacket>(client).Metadata.Get(EntityMeta.Profession)!.Value.AsInt());
        Next<EntityHeadLookMessage>(client);
        SpawnMob(server, 4, 95, w => w.WriteUByte(13).WriteVarInt(0).WriteUByte(4).WriteUByte(17).WriteVarInt(1).WriteVarInt(11));
        var wolf = Next<MobSpawnPacket>(client).Metadata;
        Assert.Equal((4, 11), (wolf.Get(EntityMeta.Mob16)!.Value.AsInt(), wolf.Get(EntityMeta.CollarColor)!.Value.AsInt()));
        Next<EntityHeadLookMessage>(client);

        // Armour stand (object 78): 11 flags (small | arms), 12 head rotation (rotations serializer 7).
        server.Send(0x00, w => w.WriteVarInt(5).WriteUuid(Guid.NewGuid()).WriteUByte(78).WriteDouble(2.5).WriteDouble(64).WriteDouble(2.5)
            .WriteUByte(0).WriteUByte(0).WriteInt(0).WriteShort(0).WriteShort(0).WriteShort(0));
        Assert.Equal(78, Next<AddObjectVehiclePacket>(client).Type);
        server.Send(0x3C, w => w.WriteVarInt(5).WriteUByte(11).WriteVarInt(0).WriteUByte(0x05)
            .WriteUByte(12).WriteVarInt(7).WriteFloat(10.4f).WriteFloat(-30f).WriteFloat(0f).WriteUByte(0xFF));
        var stand = Next<EntityMetadataPacket>(client).Metadata;
        Assert.Equal(5, stand.Get(EntityMeta.StandFlags)!.Value.AsInt());
        Assert.Equal(new MetadataVector(10, -30, 0), stand.Get(EntityMeta.StandHead)!.Value.Value);
        // Item frame (71, data 2 = facing north) with rotation 3; a spruce boat.
        server.Send(0x00, w => w.WriteVarInt(6).WriteUuid(Guid.NewGuid()).WriteUByte(71).WriteDouble(3.5).WriteDouble(65).WriteDouble(3.03)
            .WriteUByte(0).WriteUByte(0).WriteInt(2).WriteShort(0).WriteShort(0).WriteShort(0));
        Assert.Equal(2, Next<AddObjectVehiclePacket>(client).ThrowerEntityId); // the facing travels as the spawn's data
        server.Send(0x3C, w => w.WriteVarInt(6).WriteUByte(7).WriteVarInt(1).WriteVarInt(3).WriteUByte(0xFF));
        Assert.Equal(3, Next<EntityMetadataPacket>(client).Metadata.Get(EntityMeta.FrameRotation)!.Value.AsInt());
        server.Send(0x00, w => w.WriteVarInt(7).WriteUuid(Guid.NewGuid()).WriteUByte(1).WriteDouble(0).WriteDouble(62).WriteDouble(0)
            .WriteUByte(0).WriteUByte(0).WriteInt(0).WriteShort(0).WriteShort(0).WriteShort(0));
        Next<AddObjectVehiclePacket>(client);
        server.Send(0x3C, w => w.WriteVarInt(7).WriteUByte(9).WriteVarInt(1).WriteVarInt(1).WriteUByte(0xFF));
        Assert.Equal(1, Next<EntityMetadataPacket>(client).Metadata.Get(EntityMeta.BoatType)!.Value.AsInt());
    }

    [Fact]
    public void Block_entities_come_from_update_block_entity_and_chunk_data()
    {
        var (client, server) = InPlay();
        void Update(int action, NbtCompound tag) => server.Send(0x09, w => { w.WritePosition(tag.GetInt("x")!.Value, tag.GetInt("y")!.Value, tag.GetInt("z")!.Value).WriteUByte((byte)action); Nbt.WriteRoot(w, tag); });

        Update(11, new NbtCompound { ["id"] = "minecraft:bed", ["x"] = 1, ["y"] = 64, ["z"] = 1, ["color"] = 11 });
        var bed = Next<BlockEntityMessage>(client);
        Assert.Equal((1, 64, 1, "bed", 11), (bed.X, bed.Y, bed.Z, bed.Info!.Kind, bed.Info.Color));

        Update(6, new NbtCompound
        {
            ["id"] = "minecraft:banner", ["x"] = 2, ["y"] = 64, ["z"] = 1, ["Base"] = 15,
            ["Patterns"] = new NbtList(NbtType.Compound, [new NbtCompound { ["Pattern"] = "cr", ["Color"] = 1 }, new NbtCompound { ["Pattern"] = "bo", ["Color"] = 0 }]),
        });
        var banner = Next<BlockEntityMessage>(client).Info!;
        Assert.Equal(15, banner.Color);
        Assert.Equal(new[] { new BannerPattern("cr", 1), new BannerPattern("bo", 0) }, banner.Patterns);

        Update(4, new NbtCompound { ["id"] = "minecraft:skull", ["x"] = 3, ["y"] = 64, ["z"] = 1, ["SkullType"] = (sbyte)3, ["Rot"] = (sbyte)12,
            ["Owner"] = new NbtCompound { ["Name"] = "PoofimaFox" } });
        var skull = Next<BlockEntityMessage>(client).Info!;
        Assert.Equal((3, 12, "PoofimaFox"), (skull.Skull, skull.Rotation, skull.Owner));

        Update(5, new NbtCompound { ["id"] = "minecraft:flower_pot", ["x"] = 4, ["y"] = 64, ["z"] = 1, ["Item"] = "minecraft:red_flower", ["Data"] = 3 });
        var pot = Next<BlockEntityMessage>(client).Info!;
        Assert.Equal(("red_flower", 3), (pot.Item, pot.Data));

        Update(1, new NbtCompound { ["id"] = "minecraft:mob_spawner", ["x"] = 5, ["y"] = 20, ["z"] = 1, ["SpawnData"] = new NbtCompound { ["id"] = "minecraft:zombie" } });
        Assert.Equal("zombie", Next<BlockEntityMessage>(client).Info!.Mob);

        // A chunk's block entities: the sign as before, every other kind as a block entity message after the chunk.
        var body = ChunkData.EncodeBody(0, 0, full: true, null, new int[]?[16], hasSky: true, blockEntities:
            [Codec1122.SignTag(1, 70, 1, ["a", "b", "c", "d"]), new NbtCompound { ["id"] = "minecraft:chest", ["x"] = 2, ["y"] = 70, ["z"] = 2 },
             new NbtCompound { ["id"] = "minecraft:bed", ["x"] = 3, ["y"] = 70, ["z"] = 3, ["color"] = 0 }]);
        server.Send(0x20, w => w.WriteBytes(body));
        Assert.IsType<ChunkSectionsMessage>(client.ReadPacket());
        Assert.Equal(["a", "b", "c", "d"], Next<UpdateSignPacket>(client).Lines);
        Assert.Equal("chest", Next<BlockEntityMessage>(client).Info!.Kind);
        Assert.Equal(0, Next<BlockEntityMessage>(client).Info!.Color);
    }

    [Fact]
    public void The_fake_server_writes_block_entities_paintings_and_crits_back()
    {
        var (a, b) = DuplexStream.CreatePair();
        var client = new Release1122Connection(a) { Lang = LangTable.Builtin };
        var server = new Release1122ServerConnection(b);
        var serverRead = Task.Run(() => (server.ReadPacket(), server.ReadPacket()));
        client.Send(new HandshakePacket("bot"));
        Assert.True(serverRead.Wait(TimeSpan.FromSeconds(5)));
        server.Send(new HandshakePacket("-"));
        server.Send(new LoginRequestPacket { EntityId = 7, ServerMode = 0, Dimension = 0, Difficulty = 1, MaxPlayers = 8 });
        Assert.IsType<LoginRequestPacket>(client.ReadPacket());

        var info = new BlockEntityInfo("banner") { Color = 4, Patterns = [new BannerPattern("gra", 15)] };
        server.Send(new BlockEntityMessage { X = 9, Y = 65, Z = -3, Info = info });
        var got = Next<BlockEntityMessage>(client);
        Assert.Equal((9, 65, -3, 4), (got.X, got.Y, got.Z, got.Info!.Color));
        Assert.Equal(info.Patterns, got.Info.Patterns);

        server.Send(new EntityPaintingPacket { EntityId = 40, Title = "Kebab", X = 4, Y = 66, Z = 7, Direction = 1 });
        var p = Next<EntityPaintingPacket>(client);
        Assert.Equal(("Kebab", 4, 66, 7, 1), (p.Title, p.X, p.Y, p.Z, p.Direction));

        server.Send(new AnimationPacket { EntityId = 40, Animation = 6 });
        Assert.Equal(6, Next<AnimationPacket>(client).Animation);
        server.Send(new AnimationPacket { EntityId = 40, Animation = 1, Hand = 1 });
        Assert.Equal(1, Next<AnimationPacket>(client).Hand);
    }
}
