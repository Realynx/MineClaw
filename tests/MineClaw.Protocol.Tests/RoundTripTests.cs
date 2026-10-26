using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Tests;

public class RoundTripTests
{
    private static EntityMetadata SampleMetadata() => new(
    [
        MetadataEntry.FromByte(0, 0x02),
        MetadataEntry.FromShort(1, 300),
        MetadataEntry.FromInt(16, -5),
        MetadataEntry.FromFloat(17, 1.5f),
        MetadataEntry.FromString(18, "Steve"),
        MetadataEntry.FromItem(19, new ItemStack(257, 1, 12)),
        MetadataEntry.FromVector(20, new MetadataVector(1, -2, 3)),
    ]);

    /// <summary>One populated instance of every packet class.</summary>
    public static IEnumerable<IPacket> Samples()
    {
        yield return new KeepAlivePacket(-123456);
        yield return LoginRequestPacket.ForClient("MineClaw");
        yield return new LoginRequestPacket { EntityId = 42, Username = "", MapSeed = -9876543210L, ServerMode = 1, Dimension = -1, Difficulty = 2, WorldHeight = 128, MaxPlayers = 20 };
        yield return new HandshakePacket("MineClaw");
        yield return new ChatPacket("<Bob> héllo ✓ §cred");
        yield return new TimeUpdatePacket { Time = 123456789L };
        yield return new EntityEquipmentPacket { EntityId = 7, Slot = 4, ItemId = -1, Damage = 0 };
        yield return new SpawnPositionPacket { X = -100, Y = 64, Z = 2000 };
        yield return new UseEntityPacket { UserEntityId = 1, TargetEntityId = 99, LeftClick = true };
        yield return new UpdateHealthPacket { Health = 17, Food = 12, FoodSaturation = 3.5f };
        yield return new RespawnPacket { Dimension = -1, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 555 };
        yield return new PlayerOnGroundPacket { OnGround = true };
        yield return new PlayerPositionPacket { X = 1.5, Y = 64, Stance = 65.62, Z = -3.25, OnGround = true };
        yield return new PlayerLookPacket { Yaw = 270.5f, Pitch = -45f, OnGround = false };
        yield return new PlayerPositionLookPacket { X = 8.5, Y = 70, Stance = 71.62, Z = 8.5, Yaw = 90, Pitch = 10, OnGround = true };
        yield return new PlayerDiggingPacket { Status = 2, X = -5, Y = 63, Z = 12, Face = 1 };
        yield return new PlayerBlockPlacementPacket { X = 10, Y = 64, Z = -10, Direction = 1, HeldItem = new ItemStack(4, 32, 0) };
        yield return PlayerBlockPlacementPacket.UseItem(ItemStack.Empty);
        yield return new HoldingChangePacket { Slot = 8 };
        yield return new UseBedPacket { EntityId = 3, InBed = 0, X = 1, Y = 65, Z = 2 };
        yield return new AnimationPacket { EntityId = 3, Animation = 1 };
        yield return new EntityActionPacket { EntityId = 3, Action = 4 };
        yield return new NamedEntitySpawnPacket { EntityId = 11, PlayerName = "Notch", X = 320, Y = 2048, Z = -64, Yaw = 200, Pitch = 10, CurrentItem = 278 };
        yield return new PickupSpawnPacket { EntityId = 12, ItemId = 4, Count = 3, Damage = 0, X = 1, Y = 2, Z = 3, MotionX = -5, MotionY = 10, MotionZ = 0 };
        yield return new CollectItemPacket { CollectedEntityId = 12, CollectorEntityId = 3 };
        yield return new AddObjectVehiclePacket { EntityId = 13, Type = 1, X = 32, Y = 64, Z = 96, ThrowerEntityId = 0 };
        yield return new AddObjectVehiclePacket { EntityId = 14, Type = 60, X = 32, Y = 64, Z = 96, ThrowerEntityId = 3, SpeedX = 100, SpeedY = -200, SpeedZ = 300 };
        yield return new MobSpawnPacket { EntityId = 15, Type = 50, X = 1, Y = 2, Z = 3, Yaw = 4, Pitch = 5, Metadata = SampleMetadata() };
        yield return new EntityPaintingPacket { EntityId = 16, Title = "Kebab", X = 1, Y = 70, Z = 3, Direction = 2 };
        yield return new ExperienceOrbPacket { EntityId = 17, X = 1, Y = 2, Z = 3, Count = 7 };
        yield return new StanceUpdatePacket { Strafe = 1, Forward = 2, Pitch = 3, Yaw = 4, Flag1 = true, Flag2 = false };
        yield return new EntityVelocityPacket { EntityId = 18, VelocityX = -8000, VelocityY = 100, VelocityZ = 0 };
        yield return new DestroyEntityPacket { EntityId = 19 };
        yield return new EntityPacket { EntityId = 20 };
        yield return new EntityRelativeMovePacket { EntityId = 21, DX = -128, DY = 127, DZ = 0 };
        yield return new EntityLookPacket { EntityId = 22, Yaw = 255, Pitch = 128 };
        yield return new EntityLookRelativeMovePacket { EntityId = 23, DX = 1, DY = -1, DZ = 2, Yaw = 3, Pitch = 4 };
        yield return new EntityTeleportPacket { EntityId = 24, X = -1, Y = 2, Z = -3, Yaw = 64, Pitch = 192 };
        yield return new EntityStatusPacket { EntityId = 25, Status = 2 };
        yield return new AttachEntityPacket { EntityId = 26, VehicleId = -1 };
        yield return new EntityMetadataPacket { EntityId = 27, Metadata = SampleMetadata() };
        yield return new EntityEffectPacket { EntityId = 28, EffectId = 1, Amplifier = 0, Duration = 600 };
        yield return new RemoveEntityEffectPacket { EntityId = 29, EffectId = 1 };
        yield return new ExperiencePacket { Experience = 5, Level = 3, TotalExperience = 40 };
        yield return new PreChunkPacket { ChunkX = -3, ChunkZ = 4, Load = true };
        yield return new MapChunkPacket { X = 16, Y = 0, Z = -32, SizeX = 16, SizeY = 128, SizeZ = 16, CompressedData = [1, 2, 3, 4, 5] };
        yield return new MultiBlockChangePacket
        {
            ChunkX = 2, ChunkZ = -1,
            Coordinates = [ChunkDataDecoder.EncodeMultiBlockCoordinate(15, 127, 0), ChunkDataDecoder.EncodeMultiBlockCoordinate(1, 2, 3)],
            BlockIds = [1, 4], Metadata = [0, 15],
        };
        yield return new BlockChangePacket { X = -7, Y = 200, Z = 9, BlockId = 54, Metadata = 3 };
        yield return new BlockActionPacket { X = 1, Y = 64, Z = 2, Data1 = 0, Data2 = 5 };
        yield return new ExplosionPacket { X = 1.5, Y = 64, Z = -2.5, Radius = 3f, Records = [new(1, 0, -1), new(-3, 2, 0)] };
        yield return new SoundEffectPacket { EffectId = 2001, X = 1, Y = 64, Z = 3, Data = 4 };
        yield return new NewStatePacket { Reason = 3, GameMode = 1 };
        yield return new ThunderboltPacket { EntityId = 30, Type = 1, X = 1, Y = 2, Z = 3 };
        yield return new OpenWindowPacket { WindowId = 1, InventoryType = 0, Title = "Large chest", SlotCount = 54 };
        yield return new CloseWindowPacket { WindowId = 1 };
        yield return new WindowClickPacket { WindowId = 0, Slot = 36, RightClick = true, ActionNumber = 7, Shift = true, ClickedItem = new ItemStack(17, 64, 2) };
        yield return new SetSlotPacket { WindowId = -1, Slot = -1, Item = new ItemStack(3, 1, 0) };
        yield return new WindowItemsPacket { WindowId = 0, Items = [ItemStack.Empty, new ItemStack(1, 64, 0), ItemStack.Empty, new ItemStack(276, 1, 100)] };
        yield return new UpdateProgressBarPacket { WindowId = 2, ProgressBar = 0, Value = 150 };
        yield return new TransactionPacket { WindowId = 1, ActionNumber = 12, Accepted = false };
        yield return new CreativeInventoryActionPacket { Slot = 36, ItemId = 1, Count = 64, Damage = 0 };
        yield return new UpdateSignPacket { X = 1, Y = 65, Z = 2, Lines = ["Hello", "", "MineClaw", "é"] };
        yield return new ItemDataPacket { ItemType = 358, ItemId = 0, Data = [0, 1, 2, 3, 255] };
        yield return new IncrementStatisticPacket { StatisticId = 1000, Amount = 1 };
        yield return new PlayerListItemPacket { PlayerName = "Alex", Online = true, Ping = 42 };
        yield return new ServerListPingPacket();
        yield return new KickPacket("Server closed");
    }

    public static TheoryData<int> SampleIndices()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < Samples().Count(); i++) data.Add(i);
        return data;
    }

    [Fact]
    public void EveryRegisteredPacketHasASample()
    {
        var sampled = Samples().Select(p => p.Id).ToHashSet();
        var missing = PacketRegistry.All.Where(e => !sampled.Contains(e.Id)).Select(e => e.Name).ToList();
        Assert.Empty(missing);
        Assert.Equal(64, PacketRegistry.All.Count());
    }

    [Theory]
    [MemberData(nameof(SampleIndices))]
    public void RoundTripsInEveryAllowedDirection(int index)
    {
        var packet = Samples().ElementAt(index);
        var entry = PacketRegistry.Get(packet.Id)!;
        Assert.Equal(entry.Type, packet.GetType());
        foreach (var dir in new[] { PacketDirection.ClientBound, PacketDirection.ServerBound })
        {
            if (!PacketRegistry.IsAllowed(packet.Id, dir)) continue;
            var bytes = packet.ToBytes(dir);
            Assert.Equal(packet.Id, bytes[0]);
            var read = TestUtil.ReadExact(bytes, dir);
            Assert.IsType(packet.GetType(), read);
            Assert.Equal(TestUtil.ToHex(bytes), TestUtil.ToHex(read.ToBytes(dir)));
        }
    }

    [Fact]
    public void BackToBackPacketsStayAligned()
    {
        var ms = new MemoryStream();
        var w = new MinecraftWriter(ms);
        var samples = Samples().Where(p => PacketRegistry.IsAllowed(p.Id, PacketDirection.ClientBound)).ToList();
        foreach (var p in samples) PacketRegistry.Write(w, p, PacketDirection.ClientBound);
        ms.Position = 0;
        var r = new MinecraftReader(ms);
        foreach (var p in samples) Assert.Equal(p.Id, PacketRegistry.Read(r, PacketDirection.ClientBound).Id);
        Assert.False(PacketRegistry.TryRead(r, PacketDirection.ClientBound, out _));
    }
}
