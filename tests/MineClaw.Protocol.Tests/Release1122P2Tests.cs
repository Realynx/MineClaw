using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using static MineClaw.Protocol.Tests.TestUtil;

namespace MineClaw.Protocol.Tests;

/// <summary>
/// Multi-version P2 on the 1.12.2 wire: slots with NBT, windows, clicks and the confirm flow, digging, placing with hand and
/// cursor, use item/entity, signs, item entities and the entity traffic the pickup logic reads. Golden bytes follow the
/// decompiled packet classes (<c>CPacketClickWindow</c>, <c>CPacketPlayerTryUseItemOnBlock</c>, <c>CPacketPlayerDigging</c>,
/// <c>CPacketUseEntity</c>, <c>PacketBuffer.writeItemStack</c>…) and protocol.json's field tables.
/// </summary>
public class Release1122P2Tests
{
    private static (Release1122Connection Client, Wire1122Peer Server) InPlay()
    {
        var (a, b) = DuplexStream.CreatePair();
        var client = new Release1122Connection(a) { Host = "mc.example", Port = 25570, ViewDistance = 6 };
        var server = new Wire1122Peer(b);
        client.Send(new HandshakePacket("bot"));
        server.Read(); server.Read();
        server.Send(0x02, w => w.WriteString(Wire.OfflineUuid("bot").ToString()).WriteString("bot"));
        server.Send(0x23, w => w.WriteInt(7).WriteUByte(0).WriteInt(0).WriteUByte(1).WriteUByte(20).WriteString("default").WriteBool(false));
        Assert.IsType<LoginRequestPacket>(client.ReadPacket());
        server.Read(); server.Read(); // settings, brand
        return (client, server);
    }

    private static (int, string) Frame(Wire1122Peer server)
    {
        var (id, body) = server.Read();
        return (id, ToHex(body));
    }

    private static NbtCompound Efficiency(short level) => new()
    {
        ["ench"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)32, ["lvl"] = level }]),
    };

    // ------------------------------------------------------------------ slots

    [Fact]
    public void Slot_with_nbt_golden_bytes_and_round_trip()
    {
        // writeItemStack: short id, byte count, short damage, then the compound (or TAG_End when the item has none).
        Assert.Equal("FF FF", ToHex(new WireWriter().WriteSlot(ItemStack.Empty).ToArray()));
        Assert.Equal("00 04 40 00 00 00", ToHex(new WireWriter().WriteSlot(new ItemStack(4, 64, 0)).ToArray()));
        var pick = new ItemStack(Items.DiamondPickaxe, 1, 5) with { Tag = Codec1122.TagOf(Efficiency(3)) };
        var bytes = new WireWriter().WriteSlot(pick).ToArray();
        Assert.Equal(
            "01 16 01 00 05 " +
            "0A 00 00 " +                                     // root compound, empty name
            "09 00 04 65 6E 63 68 0A 00 00 00 01 " +          // list "ench" of 1 compound
            "02 00 02 69 64 00 20 02 00 03 6C 76 6C 00 03 00 " + // {id: 32s, lvl: 3s}
            "00", ToHex(bytes));                              // end of the root

        var r = new WireReader(bytes);
        var back = Codec1122.ReadSlot(ref r);
        Assert.Equal(0, r.Remaining);
        Assert.Equal(pick, back); // the tag is compared by its bytes
        Assert.Equal(3, back.Tag!.EnchantmentLevel(32));
        Assert.NotEqual(pick, pick with { Tag = null });
        Assert.NotEqual(pick, pick with { Tag = Codec1122.TagOf(Efficiency(2)) });

        // A count ≤ 0 is empty (ItemStack.isEmpty), but its bytes are still consumed.
        var zero = new WireWriter().WriteShort(4).WriteSByte(0).WriteShort(0).WriteUByte(0).WriteShort(-1).ToArray();
        var rz = new WireReader(zero);
        Assert.True(Codec1122.ReadSlot(ref rz).IsEmpty);
        Assert.True(Codec1122.ReadSlot(ref rz).IsEmpty);
        Assert.Equal(0, rz.Remaining);

        // A renamed stack: display.Name.
        var named = Codec1122.TagOf(new NbtCompound { ["display"] = new NbtCompound { ["Name"] = "Lucky" } });
        Assert.Equal("Lucky", named.DisplayName);
    }

    // ------------------------------------------------------------------ windows S→C

    [Fact]
    public void Block_actions_preserve_chest_users_and_block_identity()
    {
        var (client, server) = InPlay();
        server.Send(0x0A, w => w.WritePosition(-17, 64, 33).WriteUByte(1).WriteUByte(2).WriteVarInt(146));
        var action = Assert.IsType<BlockActionPacket>(client.ReadPacket());
        Assert.Equal((-17, 64, 33, 1, 2, (int?)146), (action.X, (int)action.Y, action.Z, (int)action.Data1, (int)action.Data2, action.BlockId));
        server.Send(0x0A, w => w.WritePosition(-17, 64, 33).WriteUByte(1).WriteUByte(0).WriteVarInt(146));
        Assert.Equal(0, Assert.IsType<BlockActionPacket>(client.ReadPacket()).Data2);
    }

    [Fact]
    public void Windows_translate_with_string_types_json_titles_offhand_and_inventory_indices()
    {
        var (client, server) = InPlay();
        // Open Window: ubyte id, string type, JSON title, ubyte slots.
        server.Send(0x13, w => w.WriteUByte(3).WriteString("minecraft:chest").WriteString("{\"translate\":\"container.chest\"}").WriteUByte(27));
        var ow = Assert.IsType<OpenWindowPacket>(client.ReadPacket());
        Assert.Equal((3, 0, 27), (ow.WindowId, ow.InventoryType, ow.SlotCount));
        server.Send(0x13, w => w.WriteUByte(4).WriteString("minecraft:crafting_table").WriteString(ChatComponent.Text("Crafting")).WriteUByte(0));
        var wb = Assert.IsType<OpenWindowPacket>(client.ReadPacket());
        Assert.Equal((4, 1, 9, "Crafting"), (wb.WindowId, wb.InventoryType, wb.SlotCount, wb.Title)); // slot count 0 on the wire
        server.Send(0x13, w => w.WriteUByte(5).WriteString("minecraft:hopper").WriteString(ChatComponent.Text("Hopper")).WriteUByte(5));
        Assert.Equal(5, Assert.IsType<OpenWindowPacket>(client.ReadPacket()).SlotCount);

        // Window Items for the player window: 46 slots, the off hand last.
        server.Send(0x14, w =>
        {
            w.WriteUByte(0).WriteShort(46);
            for (int i = 0; i < 46; i++) w.WriteSlot(i == 45 ? new ItemStack(Blocks.Torch, 12, 0) : i == 36 ? new ItemStack(Items.Stick, 3, 0) : ItemStack.Empty);
        });
        var wi = Assert.IsType<WindowItemsPacket>(client.ReadPacket());
        Assert.Equal(46, wi.Items.Length);
        Assert.Equal(new ItemStack(Blocks.Torch, 12, 0), wi.Items[45]);

        // Set Slot −2 names an InventoryPlayer index: 40 = the off hand → window-0 slot 45; 0 = hotbar 0 → 36; 39 = head → 5.
        server.Send(0x16, w => w.WriteSByte(-2).WriteShort(40).WriteSlot(new ItemStack(442, 1, 0)));
        var off = Assert.IsType<SetSlotPacket>(client.ReadPacket());
        Assert.Equal((0, 45), (off.WindowId, off.Slot));
        server.Send(0x16, w => w.WriteSByte(-2).WriteShort(39).WriteSlot(new ItemStack(Items.IronHelmet, 1, 0)));
        Assert.Equal(5, Assert.IsType<SetSlotPacket>(client.ReadPacket()).Slot);
        server.Send(0x16, w => w.WriteSByte(-1).WriteShort(-1).WriteSlot(ItemStack.Empty)); // the cursor
        Assert.True(Assert.IsType<SetSlotPacket>(client.ReadPacket()).IsCursor);

        // Furnace properties are renumbered: 1.12.2 2 cook time → canonical 0; 0 burn time → 1; 1 item burn time → 2; 3 dropped.
        server.Send(0x13, w => w.WriteUByte(6).WriteString("minecraft:furnace").WriteString(ChatComponent.Text("Furnace")).WriteUByte(3));
        Assert.Equal(2, Assert.IsType<OpenWindowPacket>(client.ReadPacket()).InventoryType);
        server.Send(0x15, w => w.WriteUByte(6).WriteShort(3).WriteShort(200));
        server.Send(0x15, w => w.WriteUByte(6).WriteShort(2).WriteShort(57));
        var pb = Assert.IsType<UpdateProgressBarPacket>(client.ReadPacket());
        Assert.Equal((6, 0, 57), (pb.WindowId, pb.ProgressBar, pb.Value));
        server.Send(0x15, w => w.WriteUByte(6).WriteShort(0).WriteShort(1600));
        Assert.Equal(1, Assert.IsType<UpdateProgressBarPacket>(client.ReadPacket()).ProgressBar);

        // Confirm Transaction (byte window, short action, bool) and Close Window.
        server.Send(0x11, w => w.WriteSByte(3).WriteShort(17).WriteBool(false));
        var tx = Assert.IsType<TransactionPacket>(client.ReadPacket());
        Assert.Equal((3, 17, false), (tx.WindowId, tx.ActionNumber, tx.Accepted));
        server.Send(0x12, w => w.WriteUByte(3));
        Assert.Equal(3, Assert.IsType<CloseWindowPacket>(client.ReadPacket()).WindowId);
    }

    // ------------------------------------------------------------------ C→S golden bytes

    [Fact]
    public void Clicks_confirm_and_close_golden_bytes()
    {
        var (client, server) = InPlay();
        // CPacketClickWindow: ubyte window, short slot, byte button, short action, VarInt ClickType, slot.
        client.Send(new WindowClickPacket { WindowId = 0, Slot = 36, ActionNumber = 1, ClickedItem = ItemStack.Empty });
        Assert.Equal((0x07, "00 00 24 00 00 01 00 FF FF"), Frame(server));
        client.Send(new WindowClickPacket { WindowId = 2, Slot = 0, Shift = true, ActionNumber = 2, ClickedItem = new ItemStack(Blocks.Planks, 4, 0) });
        Assert.Equal((0x07, "02 00 00 00 00 02 01 00 05 04 00 00 00"), Frame(server)); // shift = quick move (1)
        client.Send(new WindowClickPacket { WindowId = 0, Slot = 10, Mode = WindowClickPacket.ModeSwap, Button = 3, ActionNumber = 3 });
        Assert.Equal((0x07, "00 00 0A 03 00 03 02 FF FF"), Frame(server)); // number key 4 (hotbar 3)
        client.Send(new WindowClickPacket { WindowId = 0, Slot = 12, Mode = WindowClickPacket.ModeThrow, Button = 1, ActionNumber = 4 });
        Assert.Equal((0x07, "00 00 0C 01 00 04 04 FF FF"), Frame(server));
        client.Send(new WindowClickPacket { WindowId = 0, Slot = -999, Mode = WindowClickPacket.ModeQuickCraft, Button = 0, ActionNumber = 5 });
        Assert.Equal((0x07, "00 FC 19 00 00 05 05 FF FF"), Frame(server)); // drag start, split evenly
        client.Send(new WindowClickPacket { WindowId = 0, Slot = 20, Mode = WindowClickPacket.ModePickupAll, Button = 0, ActionNumber = 6 });
        Assert.Equal((0x07, "00 00 14 00 00 06 06 FF FF"), Frame(server));
        client.Send(new WindowClickPacket { WindowId = 0, Slot = 20, Mode = WindowClickPacket.ModeClone, Button = 2, ActionNumber = 7 });
        Assert.Equal((0x07, "00 00 14 02 00 07 03 FF FF"), Frame(server));
        // The apology for a rejected click, and Close Window.
        client.Send(new TransactionPacket { WindowId = 2, ActionNumber = 2, Accepted = true });
        Assert.Equal((0x05, "02 00 02 01"), Frame(server));
        client.Send(new CloseWindowPacket { WindowId = 2 });
        Assert.Equal((0x08, "02"), Frame(server));
    }

    [Fact]
    public void Dig_place_use_and_entity_golden_bytes()
    {
        var (client, server) = InPlay();
        const string pos = "00 00 00 41 03 FF FF FE"; // (1, 64, −2): 1 << 38 | 64 << 26 | (−2 & 0x3FFFFFF)
        // CPacketPlayerDigging: VarInt action, position, ubyte face.
        client.Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusStarted, X = 1, Y = 64, Z = -2, Face = 1 });
        Assert.Equal((0x14, "00 " + pos + " 01"), Frame(server));
        client.Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusCancelled, X = 1, Y = 64, Z = -2, Face = 1 });
        Assert.Equal((0x14, "01 " + pos + " 01"), Frame(server));
        client.Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusFinished, X = 1, Y = 64, Z = -2, Face = 5 });
        Assert.Equal((0x14, "02 " + pos + " 05"), Frame(server));
        // Drop / drop stack / release / swap hands: BlockPos.ORIGIN and DOWN, whatever the canonical message held.
        foreach (var (status, hex) in new[] { (PlayerDiggingPacket.StatusDropStack, "03"), (PlayerDiggingPacket.StatusDropItem, "04"),
                     (PlayerDiggingPacket.StatusReleaseUseItem, "05"), (PlayerDiggingPacket.StatusSwapHands, "06") })
        {
            client.Send(new PlayerDiggingPacket { Status = status, X = 0, Y = 0, Z = 0, Face = 255 });
            Assert.Equal((0x14, hex + " 00 00 00 00 00 00 00 00 00"), Frame(server));
        }

        // CPacketPlayerTryUseItemOnBlock: position, VarInt facing, VarInt hand, float cursor x/y/z. Default cursor: the face centre.
        client.Send(new PlayerBlockPlacementPacket { X = 1, Y = 64, Z = -2, Direction = 1, HeldItem = new ItemStack(4, 1, 0) });
        Assert.Equal((0x1F, pos + " 01 00 3F 00 00 00 3F 80 00 00 3F 00 00 00"), Frame(server));
        client.Send(new PlayerBlockPlacementPacket { X = 1, Y = 64, Z = -2, Direction = 2, Hand = 1, Cursor = (0.25f, 0.75f, 0f) });
        Assert.Equal((0x1F, pos + " 02 01 3E 80 00 00 3F 40 00 00 00 00 00 00"), Frame(server));
        // No target block → CPacketPlayerTryUseItem(hand).
        client.Send(PlayerBlockPlacementPacket.UseItem(new ItemStack(Items.Bread, 1, 0)));
        Assert.Equal((0x20, "00"), Frame(server));
        client.Send(PlayerBlockPlacementPacket.UseItem(ItemStack.Empty, hand: 1));
        Assert.Equal((0x20, "01"), Frame(server));

        // CPacketUseEntity: VarInt target, VarInt action (0 interact, 1 attack, 2 interact at), [x y z], [hand].
        client.Send(new UseEntityPacket { TargetEntityId = 300, LeftClick = true });
        Assert.Equal((0x0A, "AC 02 01"), Frame(server));
        client.Send(new UseEntityPacket { TargetEntityId = 300 });
        Assert.Equal((0x0A, "AC 02 00 00"), Frame(server));
        client.Send(new UseEntityPacket { TargetEntityId = 300, InteractAt = (0f, 1f, 0f), Hand = 1 });
        Assert.Equal((0x0A, "AC 02 02 00 00 00 00 3F 80 00 00 00 00 00 00 01"), Frame(server));
        // Arm swing with the hand.
        client.Send(new AnimationPacket { Animation = 1, Hand = 1 });
        Assert.Equal((0x1D, "01"), Frame(server));
        // CPacketUpdateSign: position, four strings.
        client.Send(new UpdateSignPacket { X = 1, Y = 64, Z = -2, Lines = ["hi", "", "", "x"] });
        Assert.Equal((0x1C, pos + " 02 68 69 00 00 01 78"), Frame(server));
    }

    // ------------------------------------------------------------------ entities and block entities

    [Fact]
    public void Item_entities_players_moves_and_signs_translate()
    {
        var (client, server) = InPlay();
        // Spawn Object type 2 (an item) surfaces only with its stack (the Entity Metadata index 6 that follows).
        server.Send(0x00, w => w.WriteVarInt(900).WriteUuid(Guid.NewGuid()).WriteSByte(2).WriteDouble(10.5).WriteDouble(64.25).WriteDouble(-3.5)
            .WriteUByte(0).WriteUByte(0).WriteInt(1).WriteShort(0).WriteShort(1600).WriteShort(0));
        server.Send(0x26, w => w.WriteVarInt(900).WriteShort(0).WriteShort(-1024).WriteShort(0).WriteBool(false)); // falls 0.25 first
        // Metadata: 0 flags (byte), then 6 the item (slot), then the 0xFF end.
        server.Send(0x3C, w => w.WriteVarInt(900).WriteUByte(0).WriteVarInt(0).WriteUByte(0).WriteItemMetadata(new ItemStack(Blocks.Log, 3, 0)));
        var spawn = Assert.IsType<PickupSpawnPacket>(client.ReadPacket());
        Assert.Equal((900, (short)Blocks.Log, (byte)3), (spawn.EntityId, spawn.ItemId, spawn.Count));
        Assert.Equal((FixedPoint.ToAbsolute(10.5), FixedPoint.ToAbsolute(64.0), FixedPoint.ToAbsolute(-3.5)), (spawn.X, spawn.Y, spawn.Z));
        Assert.Equal(26, spawn.MotionY); // 1600/8000 = 0.2 blocks/tick × 128

        // Relative moves (1/4096) become absolute teleports, without drift.
        for (int i = 0; i < 8; i++) server.Send(0x26, w => w.WriteVarInt(900).WriteShort(512).WriteShort(0).WriteShort(0).WriteBool(true));
        EntityTeleportPacket last = null!;
        for (int i = 0; i < 8; i++) last = Assert.IsType<EntityTeleportPacket>(client.ReadPacket());
        Assert.Equal(FixedPoint.ToAbsolute(11.5), last.X);
        // A later stack change (merging) → ItemEntityStackMessage.
        server.Send(0x3C, w => w.WriteVarInt(900).WriteItemMetadata(new ItemStack(Blocks.Log, 5, 0)));
        Assert.Equal(5, Assert.IsType<ItemEntityStackMessage>(client.ReadPacket()).Item.Count);

        // Collect Item (collected, collector, count) and Destroy Entities (several ids in one packet).
        server.Send(0x4B, w => w.WriteVarInt(900).WriteVarInt(7).WriteVarInt(5));
        var ci = Assert.IsType<CollectItemPacket>(client.ReadPacket());
        Assert.Equal((900, 7), (ci.CollectedEntityId, ci.CollectorEntityId));
        server.Send(0x00, w => w.WriteVarInt(901).WriteUuid(Guid.NewGuid()).WriteSByte(2).WriteDouble(0).WriteDouble(64).WriteDouble(0)
            .WriteUByte(0).WriteUByte(0).WriteInt(1).WriteShort(0).WriteShort(0).WriteShort(0));
        server.Send(0x3C, w => w.WriteVarInt(901).WriteItemMetadata(new ItemStack(Blocks.Dirt, 1, 0)));
        Assert.IsType<PickupSpawnPacket>(client.ReadPacket());
        server.Send(0x32, w => w.WriteVarInt(3).WriteVarInt(900).WriteVarInt(555).WriteVarInt(901)); // 555 was never surfaced
        Assert.Equal(900, Assert.IsType<DestroyEntityPacket>(client.ReadPacket()).EntityId);
        Assert.Equal(901, Assert.IsType<DestroyEntityPacket>(client.ReadPacket()).EntityId);

        // Spawn Player: the name comes from the Player List (uuid → name).
        var fox = Wire.OfflineUuid("PoofImaFox");
        server.Send(0x2E, w => w.WriteVarInt(0).WriteVarInt(1).WriteUuid(fox).WriteString("PoofImaFox").WriteVarInt(0).WriteVarInt(0).WriteVarInt(20).WriteBool(false));
        Assert.IsType<PlayerListItemPacket>(client.ReadPacket());
        server.Send(0x05, w => w.WriteVarInt(77).WriteUuid(fox).WriteDouble(1.5).WriteDouble(65).WriteDouble(2.5).WriteUByte(64).WriteUByte(0)
            .WriteUByte(0).WriteVarInt(0).WriteUByte(0).WriteUByte(0xFF));
        var ne = Assert.IsType<NamedEntitySpawnPacket>(client.ReadPacket());
        Assert.Equal(("PoofImaFox", 77, 64), (ne.PlayerName, ne.EntityId, (int)ne.Yaw));
        Assert.IsType<EntityMetadataPacket>(client.ReadPacket()); // P4: the spawn's own metadata (flags) follows it
        // Equipment: main hand (0) → canonical held slot 0; the off hand (1) → canonical slot 5 (P4).
        server.Send(0x3F, w => w.WriteVarInt(77).WriteVarInt(1).WriteSlot(new ItemStack(442, 1, 0)));
        server.Send(0x3F, w => w.WriteVarInt(77).WriteVarInt(0).WriteSlot(new ItemStack(Items.IronPickaxe, 1, 0)));
        var off = Assert.IsType<EntityEquipmentPacket>(client.ReadPacket());
        Assert.Equal((5, (short)442), (off.Slot, off.ItemId));
        var eq = Assert.IsType<EntityEquipmentPacket>(client.ReadPacket());
        Assert.Equal((0, (short)Items.IronPickaxe), (eq.Slot, eq.ItemId));
        // Animation: 0 swing main arm → b1.8.1's 1; Block Break Animation; Entity Status.
        server.Send(0x06, w => w.WriteVarInt(77).WriteUByte(0));
        Assert.Equal(1, Assert.IsType<AnimationPacket>(client.ReadPacket()).Animation);
        server.Send(0x08, w => w.WriteVarInt(77).WritePosition(2, 64, 2).WriteSByte(4));
        var bb = Assert.IsType<BlockBreakAnimationMessage>(client.ReadPacket());
        Assert.Equal((77, 2, 64, 2, 4), (bb.EntityId, bb.X, bb.Y, bb.Z, bb.Stage));
        server.Send(0x1B, w => w.WriteInt(7).WriteSByte(9));
        Assert.Equal(9, Assert.IsType<EntityStatusPacket>(client.ReadPacket()).Status);

        // Update Block Entity action 9: a sign, its lines JSON components.
        server.Send(0x09, w =>
        {
            w.WritePosition(4, 65, 4).WriteUByte(9);
            Nbt.WriteRoot(w, Codec1122.SignTag(4, 65, 4, ["Hello", "", "fox", ""]));
        });
        var sign = Assert.IsType<UpdateSignPacket>(client.ReadPacket());
        Assert.Equal((4, 65, 4), (sign.X, (int)sign.Y, sign.Z));
        Assert.Equal(["Hello", "", "fox", ""], sign.Lines);

        // Signs among a chunk's block entities come out after the chunk.
        var body = ChunkData.EncodeBody(0, 0, full: true, null, new int[]?[16], hasSky: true, blockEntities: [Codec1122.SignTag(1, 70, 1, ["a", "b", "c", "d"])]);
        server.Send(0x20, w => w.WriteBytes(body));
        Assert.IsType<ChunkSectionsMessage>(client.ReadPacket());
        Assert.Equal(["a", "b", "c", "d"], Assert.IsType<UpdateSignPacket>(client.ReadPacket()).Lines);
    }

    // ------------------------------------------------------------------ the server side (fake servers)

    [Fact]
    public void Server_connection_logs_in_and_round_trips_the_p2_messages()
    {
        var (a, b) = DuplexStream.CreatePair();
        var client = new Release1122Connection(a) { Lang = LangTable.Builtin };
        var server = new Release1122ServerConnection(b);
        var serverRead = Task.Run(() => (server.ReadPacket(), server.ReadPacket()));
        client.Send(new HandshakePacket("maid_chan"));
        var (hs, login) = serverRead.Result;
        Assert.Equal("maid_chan", Assert.IsType<HandshakePacket>(hs).Username);
        Assert.IsType<LoginRequestPacket>(login);
        server.Send(new HandshakePacket("-")); // swallowed: the login was answered on read
        server.Send(new LoginRequestPacket { EntityId = 42, ServerMode = 0, Dimension = 0, Difficulty = 1, MaxPlayers = 8 });
        Assert.Equal(42, Assert.IsType<LoginRequestPacket>(client.ReadPacket()).EntityId);
        Assert.Equal(256, client.CompressionThreshold);

        // S→C: the b1.8.1-shaped window 0 (45 slots) gains the off hand; an item spawn becomes Spawn Object + metadata.
        server.Send(new WindowItemsPacket { WindowId = 0, Items = Enumerable.Repeat(new ItemStack(4, 1, 0), 45).ToArray() });
        var wi = Assert.IsType<WindowItemsPacket>(client.ReadPacket());
        Assert.Equal(46, wi.Items.Length);
        Assert.True(wi.Items[45].IsEmpty);
        server.Send(new PickupSpawnPacket { EntityId = 5, ItemId = Blocks.Log, Count = 2, X = 320, Y = 2048, Z = 64 });
        var ps = Assert.IsType<PickupSpawnPacket>(client.ReadPacket());
        Assert.Equal((5, (short)Blocks.Log, (byte)2, 320, 2048, 64), (ps.EntityId, ps.ItemId, ps.Count, ps.X, ps.Y, ps.Z));
        server.Send(new OpenWindowPacket { WindowId = 1, InventoryType = 1, Title = "Crafting", SlotCount = 9 });
        Assert.Equal((1, 9), (Assert.IsType<OpenWindowPacket>(client.ReadPacket()).InventoryType, 9));
        server.Send(new UpdateSignPacket { X = 1, Y = 2, Z = 3, Lines = ["a", "b", "", ""] });
        Assert.Equal(["a", "b", "", ""], Assert.IsType<UpdateSignPacket>(client.ReadPacket()).Lines);

        // C→S: a swap click, a placement with its cursor, a dig.
        var read = Task.Run(() => (server.ReadPacket(), server.ReadPacket(), server.ReadPacket()));
        var tag = Codec1122.TagOf(Efficiency(1));
        client.Send(new WindowClickPacket { WindowId = 1, Slot = 12, Mode = WindowClickPacket.ModeSwap, Button = 2, ActionNumber = 9, ClickedItem = new ItemStack(278, 1, 0) with { Tag = tag } });
        client.Send(new PlayerBlockPlacementPacket { X = 1, Y = 64, Z = 1, Direction = 3, Cursor = (0.5f, 0.75f, 1f) });
        client.Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusSwapHands });
        var (c1, c2, c3) = read.Result;
        var click = Assert.IsType<WindowClickPacket>(c1);
        Assert.Equal((WindowClickPacket.ModeSwap, (sbyte)2, (short)9), (click.Mode, click.EffectiveButton, click.ActionNumber));
        Assert.Equal(tag, click.ClickedItem.Tag);
        var place = Assert.IsType<PlayerBlockPlacementPacket>(c2);
        Assert.Equal((3, (0.5f, 0.75f, 1f)), (place.Direction, place.Cursor!.Value));
        Assert.True(place.HeldItem.IsEmpty); // 1.12.2 sends no held item
        Assert.Equal(PlayerDiggingPacket.StatusSwapHands, Assert.IsType<PlayerDiggingPacket>(c3).Status);
    }

    [Fact]
    public void Beta181_connection_refuses_the_newer_forms()
    {
        var (a, _) = DuplexStream.CreatePair();
        var b181 = new PacketConnection(a);
        Assert.True(b181.CanSend(new WindowClickPacket { Shift = true }));
        Assert.False(b181.CanSend(new WindowClickPacket { Mode = WindowClickPacket.ModeSwap }));
        Assert.False(b181.CanSend(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusSwapHands }));
        Assert.False(b181.CanSend(new PlayerBlockPlacementPacket { Hand = 1 }));
        Assert.True(b181.CanSend(new PlayerBlockPlacementPacket { Cursor = (0.5f, 1f, 0.5f) })); // the cursor is just not sent
    }
}
