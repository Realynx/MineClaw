using MineClaw.Data;
using MineClaw.Protocol.Packets;
using static MineClaw.Protocol.Tests.TestUtil;

namespace MineClaw.Protocol.Tests;

/// <summary>Hand-built wire bytes for layouts that are easy to get wrong.</summary>
public class FixtureTests
{
    private const string D1 = "3F F0 00 00 00 00 00 00";     // 1.0
    private const string D64 = "40 50 00 00 00 00 00 00";    // 64.0
    private const string D65_5 = "40 50 60 00 00 00 00 00";  // 65.5
    private const string DM2 = "C0 00 00 00 00 00 00 00";    // -2.0
    private const string F90 = "42 B4 00 00";                 // 90f
    private const string F0 = "00 00 00 00";

    [Fact]
    public void PositionLook_ServerBound_IsXYStanceZ()
    {
        var bytes = Hex($"0D {D1} {D64} {D65_5} {DM2} {F90} {F0} 01");
        var p = Assert.IsType<PlayerPositionLookPacket>(ReadExact(bytes, PacketDirection.ServerBound));
        Assert.Equal(1.0, p.X);
        Assert.Equal(64.0, p.Y);
        Assert.Equal(65.5, p.Stance);
        Assert.Equal(-2.0, p.Z);
        Assert.Equal(90f, p.Yaw);
        Assert.Equal(0f, p.Pitch);
        Assert.True(p.OnGround);

        var built = new PlayerPositionLookPacket { X = 1, Y = 64, Stance = 65.5, Z = -2, Yaw = 90, Pitch = 0, OnGround = true };
        Assert.Equal(ToHex(bytes), ToHex(built.ToBytes(PacketDirection.ServerBound)));
    }

    [Fact]
    public void PositionLook_ClientBound_IsXStanceYZ_AndEchoSwapsBack()
    {
        var s2c = Hex($"0D {D1} {D65_5} {D64} {DM2} {F90} {F0} 00");
        var p = Assert.IsType<PlayerPositionLookPacket>(ReadExact(s2c, PacketDirection.ClientBound));
        Assert.Equal(64.0, p.Y);
        Assert.Equal(65.5, p.Stance);

        var echo = p.EchoForServer(onGround: true);
        Assert.Equal(Hex($"0D {D1} {D64} {D65_5} {DM2} {F90} {F0} 01"), echo.ToBytes(PacketDirection.ServerBound));
    }

    [Fact]
    public void Position_0x0B_FollowsSameDirectionRule()
    {
        var c2s = Hex($"0B {D1} {D64} {D65_5} {DM2} 01");
        var p = Assert.IsType<PlayerPositionPacket>(ReadExact(c2s, PacketDirection.ServerBound));
        Assert.Equal(64.0, p.Y);
        Assert.Equal(65.5, p.Stance);
        var s2c = Assert.IsType<PlayerPositionPacket>(ReadExact(Hex($"0B {D1} {D65_5} {D64} {DM2} 01"), PacketDirection.ClientBound));
        Assert.Equal(64.0, s2c.Y);
    }

    [Fact]
    public void EntityMetadata_AllSevenTypes()
    {
        var bytes = Hex(
            "28 00 00 00 2A " +
            "00 02 " +                       // byte  idx0 = 2
            "21 01 2C " +                    // short idx1 = 300
            "50 FF FF FF FB " +              // int   idx16 = -5
            "71 3F C0 00 00 " +              // float idx17 = 1.5
            "92 00 02 00 48 00 69 " +        // str   idx18 = "Hi"
            "B3 01 01 05 00 0C " +           // item  idx19 = 257 x5 dmg 12
            "D4 00 00 00 01 FF FF FF FE 00 00 00 03 " + // vec idx20 = (1,-2,3)
            "7F");
        var p = Assert.IsType<EntityMetadataPacket>(ReadExact(bytes, PacketDirection.ClientBound));
        Assert.Equal(42, p.EntityId);
        var m = p.Metadata;
        Assert.Equal(7, m.Count);
        Assert.Equal((sbyte)2, m.Flags);
        Assert.Equal(new MetadataEntry(1, MetadataType.Short, (short)300), m.Get(1));
        Assert.Equal(-5, m.Get(16)!.Value.AsInt());
        Assert.Equal(1.5f, (float)m.Get(17)!.Value.Value);
        Assert.Equal("Hi", (string)m.Get(18)!.Value.Value);
        Assert.Equal(new ItemStack(257, 5, 12), (ItemStack)m.Get(19)!.Value.Value);
        Assert.Equal(new MetadataVector(1, -2, 3), (MetadataVector)m.Get(20)!.Value.Value);
        Assert.Equal(ToHex(bytes), ToHex(p.ToBytes(PacketDirection.ClientBound)));
    }

    [Fact]
    public void MobSpawn_MetadataEmptyListIsJustTerminator()
    {
        var bytes = Hex("18 00 00 00 05 32 00 00 00 20 00 00 08 00 FF FF FF E0 40 00 7F");
        var p = Assert.IsType<MobSpawnPacket>(ReadExact(bytes, PacketDirection.ClientBound));
        Assert.Equal(50, p.Type);
        Assert.Equal(1.0, FixedPoint.FromAbsolute(p.X));
        Assert.Equal(64.0, FixedPoint.FromAbsolute(p.Y));
        Assert.Equal(-1.0, FixedPoint.FromAbsolute(p.Z));
        Assert.Equal(90f, FixedPoint.FromAngle(p.Yaw));
        Assert.Empty(p.Metadata);
    }

    [Fact]
    public void Metadata_UnknownType_Throws()
    {
        var bytes = Hex("28 00 00 00 01 E0 00 7F");
        var ex = Assert.Throws<ProtocolException>(() => ReadExact(bytes, PacketDirection.ClientBound));
        Assert.Contains("metadata type 7", ex.Message);
    }

    [Fact]
    public void Slots_EmptyAndFilled()
    {
        var r = new MinecraftReader(Hex("FF FF 01 15 40 00 03"));
        Assert.True(r.ReadSlot().IsEmpty);
        Assert.Equal(new ItemStack(277, 64, 3), r.ReadSlot());

        var ms = new MemoryStream();
        var w = new MinecraftWriter(ms);
        w.WriteSlot(ItemStack.Empty);
        w.WriteSlot(new ItemStack(277, 64, 3));
        Assert.Equal("FF FF 01 15 40 00 03", ToHex(ms.ToArray()));
    }

    [Fact]
    public void WindowClick_Layout()
    {
        var bytes = Hex("66 00 00 24 01 00 07 00 00 11 40 00 02");
        var p = Assert.IsType<WindowClickPacket>(ReadExact(bytes, PacketDirection.ServerBound));
        Assert.Equal(0, p.WindowId);
        Assert.Equal(36, p.Slot);
        Assert.True(p.RightClick);
        Assert.Equal(7, p.ActionNumber);
        Assert.False(p.Shift);
        Assert.Equal(new ItemStack(17, 64, 2), p.ClickedItem);
        Assert.Equal(ToHex(bytes), ToHex(p.ToBytes()));

        var empty = new WindowClickPacket { WindowId = 1, Slot = WindowClickPacket.OutsideWindowSlot, ActionNumber = 1, Shift = true };
        Assert.Equal("66 01 FC 19 00 00 01 01 FF FF", ToHex(empty.ToBytes()));
    }

    [Fact]
    public void WindowClick_IsNotValidClientBound()
    {
        Assert.Throws<ProtocolException>(() => ReadExact(Hex("66 00 00 24 01 00 07 00 FF FF"), PacketDirection.ClientBound));
    }

    [Fact]
    public void String16_NonAscii_IsUtf16BigEndianWithCodeUnitCount()
    {
        var p = new ChatPacket("é✓😀");
        var bytes = p.ToBytes();
        Assert.Equal("03 00 04 00 E9 27 13 D8 3D DE 00", ToHex(bytes));
        Assert.Equal("é✓😀", Assert.IsType<ChatPacket>(ReadExact(bytes, PacketDirection.ClientBound)).Message);
    }

    [Fact]
    public void String16_NegativeLength_Throws()
    {
        Assert.Throws<ProtocolException>(() => ReadExact(Hex("03 FF FF"), PacketDirection.ClientBound));
    }

    [Fact]
    public void Chat_ServerBound_Over100Chars_RefusesToSerialize()
    {
        Assert.Throws<ArgumentException>(() => new ChatPacket(new string('a', 101)).ToBytes(PacketDirection.ServerBound));
        Assert.Equal(1 + 2 + 200, new ChatPacket(new string('a', 100)).ToBytes(PacketDirection.ServerBound).Length);
    }

    [Fact]
    public void Login_ClientRequest_ExactBytes()
    {
        var bytes = LoginRequestPacket.ForClient("Abc").ToBytes();
        Assert.Equal("01 00 00 00 11 00 03 00 41 00 62 00 63 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", ToHex(bytes));
    }

    [Fact]
    public void Login_ServerResponse_UnsignedHeightAndMaxPlayers()
    {
        var bytes = Hex("01 00 00 00 2A 00 00 00 00 00 00 00 00 00 7B 00 00 00 01 FF 02 80 14");
        var p = Assert.IsType<LoginRequestPacket>(ReadExact(bytes, PacketDirection.ClientBound));
        Assert.Equal(42, p.EntityId);
        Assert.Equal("", p.Username);
        Assert.Equal(123, p.MapSeed);
        Assert.Equal(1, p.ServerMode);
        Assert.Equal(-1, p.Dimension);
        Assert.Equal(2, p.Difficulty);
        Assert.Equal(128, p.WorldHeight);
        Assert.Equal(20, p.MaxPlayers);
    }

    [Fact]
    public void Respawn_Layout()
    {
        var p = Assert.IsType<RespawnPacket>(ReadExact(Hex("09 FF 01 00 00 80 00 00 00 00 00 00 00 05"), PacketDirection.ClientBound));
        Assert.Equal(-1, p.Dimension);
        Assert.Equal(1, p.Difficulty);
        Assert.Equal(0, p.GameMode);
        Assert.Equal(128, p.WorldHeight);
        Assert.Equal(5, p.MapSeed);
    }

    [Fact]
    public void Experience_IsByteByteShort()
    {
        var p = Assert.IsType<ExperiencePacket>(ReadExact(Hex("2B 05 03 00 28"), PacketDirection.ClientBound));
        Assert.Equal(5, p.Experience);
        Assert.Equal(3, p.Level);
        Assert.Equal(40, p.TotalExperience);
    }

    [Fact]
    public void UpdateHealth_HasFoodAndSaturation()
    {
        var p = Assert.IsType<UpdateHealthPacket>(ReadExact(Hex("08 00 14 00 12 40 A0 00 00"), PacketDirection.ClientBound));
        Assert.Equal(20, p.Health);
        Assert.Equal(18, p.Food);
        Assert.Equal(5f, p.FoodSaturation);
    }

    [Fact]
    public void OpenWindow_TitleIsString16()
    {
        var p = Assert.IsType<OpenWindowPacket>(ReadExact(Hex("64 01 00 00 05 00 43 00 68 00 65 00 73 00 74 1B"), PacketDirection.ClientBound));
        Assert.Equal("Chest", p.Title);
        Assert.Equal(27, p.SlotCount);
    }

    [Fact]
    public void CreativeAction_IsFourShorts()
    {
        var p = Assert.IsType<CreativeInventoryActionPacket>(ReadExact(Hex("6B 00 24 00 01 00 40 00 00"), PacketDirection.ServerBound));
        Assert.Equal(36, p.Slot);
        Assert.Equal(1, p.ItemId);
        Assert.Equal(64, p.Count);
    }

    [Fact]
    public void AddObject_SpeedOnlyWhenThrowerPositive()
    {
        Assert.Equal(22, new AddObjectVehiclePacket { ThrowerEntityId = 0, SpeedX = 5 }.ToBytes(PacketDirection.ClientBound).Length);
        Assert.Equal(22, new AddObjectVehiclePacket { ThrowerEntityId = -4 }.ToBytes(PacketDirection.ClientBound).Length);
        Assert.Equal(28, new AddObjectVehiclePacket { ThrowerEntityId = 1 }.ToBytes(PacketDirection.ClientBound).Length);
    }

    [Fact]
    public void BlockPlacement_UseItemForm()
    {
        var bytes = PlayerBlockPlacementPacket.UseItem(new ItemStack(260, 1, 0)).ToBytes();
        Assert.Equal("0F FF FF FF FF FF FF FF FF FF FF 01 04 01 00 00", ToHex(bytes));
    }

    [Fact]
    public void Explosion_RecordsUseTruncatedCentre()
    {
        var p = new ExplosionPacket { X = -1.5, Y = 64.9, Z = 2.2, Radius = 4, Records = [new(1, -1, 0)] };
        var read = Assert.IsType<ExplosionPacket>(ReadExact(p.ToBytes(PacketDirection.ClientBound), PacketDirection.ClientBound));
        Assert.Equal((0, 63, 2), read.AffectedBlocks.Single()); // (int)-1.5 = -1, +1 = 0
    }

    [Fact]
    public void MultiBlockChange_DecodesToWorldCoordinates()
    {
        var bytes = Hex("34 00 00 00 02 FF FF FF FF 00 02 F0 7F 13 02 01 04 00 0F");
        var p = Assert.IsType<MultiBlockChangePacket>(ReadExact(bytes, PacketDirection.ClientBound));
        var recs = ChunkDataDecoder.DecodeMultiBlockChange(p);
        Assert.Equal(new BlockChangeRecord(47, 127, -16, 1, 0), recs[0]);
        Assert.Equal(new BlockChangeRecord(33, 2, -13, 4, 15), recs[1]);
    }

    [Fact]
    public void UnknownId_ReportsRecentIds()
    {
        var r = new MinecraftReader(Hex("00 00 00 00 01 04 00 00 00 00 00 00 00 05 23 00"));
        PacketRegistry.Read(r, PacketDirection.ClientBound);
        PacketRegistry.Read(r, PacketDirection.ClientBound);
        var ex = Assert.Throws<ProtocolException>(() => PacketRegistry.Read(r, PacketDirection.ClientBound));
        Assert.Contains("0x23", ex.Message);
        Assert.Contains("0x00, 0x04, 0x23", ex.Message);
    }

    [Fact]
    public void Truncated_Packet_Throws()
    {
        Assert.Throws<ProtocolException>(() => PacketRegistry.Read(new MinecraftReader(Hex("04 00 00")), PacketDirection.ClientBound));
        Assert.Throws<EndOfStreamException>(() => PacketRegistry.Read(new MinecraftReader([]), PacketDirection.ClientBound));
    }

    [Fact]
    public void FixedPoint_Conversions()
    {
        Assert.Equal(-33, FixedPoint.ToAbsolute(-1.01));
        Assert.Equal(1.5, FixedPoint.FromAbsolute(48));
        Assert.Equal(-0.125, FixedPoint.FromRelative(-4));
        Assert.Equal(64, FixedPoint.ToAngle(90f));
        Assert.Equal(192, FixedPoint.ToAngle(-90f));
        Assert.Equal(270f, FixedPoint.FromAngle(192));
        Assert.Equal(-90f, FixedPoint.FromAngleSigned(192));
        Assert.Equal(0.5, FixedPoint.FromVelocity(4000));
        Assert.Equal(31200, FixedPoint.ToVelocity(10));
    }
}
