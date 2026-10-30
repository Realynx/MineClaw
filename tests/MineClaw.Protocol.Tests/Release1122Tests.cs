using System.IO.Compression;
using System.Text.Json;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using static MineClaw.Protocol.Tests.TestUtil;

namespace MineClaw.Protocol.Tests;

/// <summary>The server end of a 1.12.2 wire, for tests: raw frames through the same codec rules (decompiled 1.12.2).</summary>
internal sealed class Wire1122Peer(Stream stream)
{
    public Framing Framing { get; } = new();
    public Stream Stream { get; } = stream;

    public (int Id, byte[] Body) Read()
    {
        var payload = Framing.Read(Stream) ?? throw new EndOfStreamException("client closed");
        var r = new WireReader(payload);
        int id = r.ReadVarInt();
        return (id, payload[r.Position..]);
    }

    /// <summary>Reads frames until one with <paramref name="id"/> (skipping e.g. movement), returning its body.</summary>
    public byte[] ReadUntil(int id, int max = 50)
    {
        for (int i = 0; i < max; i++)
        {
            var (got, body) = Read();
            if (got == id) return body;
        }
        throw new InvalidOperationException($"no 0x{id:X2} within {max} frames");
    }

    public void Send(int id, Action<WireWriter> body)
    {
        var w = WireWriter.ForPacket(id);
        body(w);
        Stream.Write(Framing.Encode(w.Span));
        Stream.Flush();
    }
}

/// <summary>
/// Multi-version P1: the 1.12.2 wire. Golden bytes follow protocol.json (third_party/minecraft-data/pc/1.12.2) and the
/// decompiled 1.12.2 classes named in each test; the packed position follows BlockPos.toLong (x ≪ 38 | y ≪ 26 | z), which
/// protocol.json's bitfield (x 26, y 12, z 26) agrees with.
/// </summary>
public class Release1122Tests
{
    // ------------------------------------------------------------------ primitives

    [Fact]
    public void Position_packs_like_BlockPos_toLong()
    {
        Assert.Equal(0x0000_0040_0800_0003L, Wire.PackPosition(1, 2, 3));
        Assert.Equal(-1L, Wire.PackPosition(-1, -1, -1));
        Assert.Equal((1, 2, 3), Wire.UnpackPosition(Wire.PackPosition(1, 2, 3)));
        Assert.Equal((-30_000_000, 255, 29_999_999), Wire.UnpackPosition(Wire.PackPosition(-30_000_000, 255, 29_999_999)));
        Assert.Equal((-5, 0, -7), Wire.UnpackPosition(Wire.PackPosition(-5, 0, -7)));
    }

    [Fact]
    public void VarInt_and_strings_round_trip()
    {
        var w = new WireWriter().WriteVarInt(0).WriteVarInt(127).WriteVarInt(128).WriteVarInt(340).WriteVarInt(-1).WriteString("héllo").WriteVarLong(long.MinValue);
        Assert.Equal("00 7F 80 01 D4 02 FF FF FF FF 0F 06 68 C3 A9 6C 6C 6F", ToHex(w.ToArray()[..18]));
        var r = new WireReader(w.ToArray());
        Assert.Equal(0, r.ReadVarInt());
        Assert.Equal(127, r.ReadVarInt());
        Assert.Equal(128, r.ReadVarInt());
        Assert.Equal(340, r.ReadVarInt());
        Assert.Equal(-1, r.ReadVarInt());
        Assert.Equal("héllo", r.ReadString());
        Assert.Equal(long.MinValue, r.ReadVarLong());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Offline_uuid_is_the_vanilla_name_based_one()
    {
        // EntityPlayer.getOfflineUUID = UUID.nameUUIDFromBytes("OfflinePlayer:" + name): version 3, IETF variant.
        var g = Wire.OfflineUuid("Notch");
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", g.ToString());
        var (msb, lsb) = Wire.FromGuid(g);
        Assert.Equal(g, Wire.ToGuid(msb, lsb));
    }

    // ------------------------------------------------------------------ framing + compression (NettyCompressionEncoder/Decoder)

    [Fact]
    public void Uncompressed_frames_are_length_id_data()
    {
        var f = new Framing();
        var frame = f.Encode(WireWriter.ForPacket(0x0B).WriteLong(0x0102030405060708L).Span);
        Assert.Equal("09 0B 01 02 03 04 05 06 07 08", ToHex(frame));
        Assert.Equal("0B 01 02 03 04 05 06 07 08", ToHex(f.Read(new MemoryStream(frame))!));
        Assert.Null(f.Read(new MemoryStream()));
    }

    [Fact]
    public void Compression_round_trips_below_and_above_the_threshold()
    {
        var f = new Framing { Threshold = 64 };
        var small = WireWriter.ForPacket(0x02).WriteString("hi").ToArray();
        var frameSmall = f.Encode(small);
        // Below the threshold: dataLength 0, raw payload.
        Assert.Equal($"{small.Length + 1:X2} 00 " + ToHex(small), ToHex(frameSmall));
        var big = WireWriter.ForPacket(0x02).WriteString(new string('a', 200)).ToArray();
        var frameBig = f.Encode(big);
        var r = new WireReader(frameBig);
        int packetLen = r.ReadVarInt();
        Assert.Equal(frameBig.Length - r.Position, packetLen);
        Assert.Equal(big.Length, r.ReadVarInt()); // dataLength = uncompressed size
        var ms = new MemoryStream();
        ms.Write(frameSmall);
        ms.Write(frameBig);
        ms.Position = 0;
        Assert.Equal(small, f.Read(ms));
        Assert.Equal(big, f.Read(ms));
        Assert.Null(f.Read(ms));
        Assert.True(frameBig.Length < big.Length);
    }

    [Fact]
    public void A_compressed_payload_below_the_threshold_is_rejected()
    {
        var payload = WireWriter.ForPacket(0x02).WriteString("hi").ToArray();
        var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.Optimal, true)) d.Write(payload);
        var body = new WireWriter().WriteVarInt(payload.Length).WriteBytes(z.ToArray());
        var frame = new WireWriter().WriteVarInt(body.Length).WriteBytes(body.Span).ToArray();
        var ex = Assert.Throws<ProtocolException>(() => new Framing { Threshold = 256 }.Read(new MemoryStream(frame)));
        Assert.Contains("below the threshold", ex.Message);
        Assert.Equal(payload, new Framing { Threshold = 4 }.Read(new MemoryStream(frame)));
    }

    // ------------------------------------------------------------------ NBT (CompressedStreamTools / NBTTagCompound)

    [Fact]
    public void Nbt_reads_a_sign_block_entity_fixture()
    {
        // TileEntitySign.writeToNBT: id, x, y, z, Text1..Text4 (JSON). Hand-assembled big-endian bytes.
        var bytes = Hex("""
            0A 00 00
              08 00 02 69 64  00 0E 6D 69 6E 65 63 72 61 66 74 3A 73 69 67 6E
              03 00 01 78  FF FF FF FE
              03 00 01 79  00 00 00 C8
              03 00 01 7A  00 00 00 05
              08 00 05 54 65 78 74 31  00 0D 7B 22 74 65 78 74 22 3A 22 68 69 22 7D
              09 00 01 4C  03 00 00 00 02  00 00 00 07  FF FF FF FF
              0C 00 01 41  00 00 00 01  00 00 00 00 00 00 00 2A
              0A 00 01 43  01 00 01 62 7F  00
            00
            """);
        var c = Nbt.ReadRoot(bytes, out int used);
        Assert.Equal(bytes.Length, used);
        Assert.NotNull(c);
        Assert.Equal("minecraft:sign", c!.GetString("id"));
        Assert.Equal(-2, c.GetInt("x"));
        Assert.Equal(200, c.GetInt("y"));
        Assert.Equal("""{"text":"hi"}""", c.GetString("Text1"));
        Assert.Equal(NbtType.Int, c.GetList("L")!.ElementType);
        Assert.Equal([7, -1], c.GetList("L")!.Items.Cast<int>());
        Assert.Equal([42L], (long[])c["A"]);
        Assert.Equal((sbyte)127, c.GetCompound("C")!["b"]);
        Assert.Equal(bytes, Nbt.ToBytes(c));
        Assert.Null(Nbt.ReadRoot([0], out used));
        Assert.Equal(1, used);
    }

    [Fact]
    public void Nbt_strings_are_modified_utf8()
    {
        var c = new NbtCompound { ["s"] = "a\0é€" };
        var bytes = Nbt.ToBytes(c);
        // NUL is C0 80 in Java's modified UTF-8.
        Assert.Contains("61 C0 80 C3 A9 E2 82 AC", ToHex(bytes));
        Assert.Equal("a\0é€", Nbt.ReadRoot(bytes, out _)!.GetString("s"));
        Assert.Throws<ProtocolException>(() => Nbt.ReadRoot(Hex("0A 00 00 07 00 01 62 7F FF FF FF"), out _));
    }

    // ------------------------------------------------------------------ chat (ITextComponent.Serializer → getFormattedText)

    [Fact]
    public void Player_chat_flattens_to_the_b181_line_shape()
    {
        // What PlayerList/NetHandlerPlayServer.processChatMessage sends: chat.type.text with the name component (click/hover
        // events and insertion) and the raw message.
        var json = """{"translate":"chat.type.text","with":[{"insertion":"poofimafox","clickEvent":{"action":"suggest_command","value":"/msg poofimafox "},"hoverEvent":{"action":"show_entity","value":{"text":"{name:\"poofimafox\"}"}},"text":"poofimafox"},"hello there"]}""";
        Assert.Equal("<poofimafox> hello there", ChatComponent.ToLegacy(json));
        Assert.Equal("* kiwi waves", ChatComponent.ToLegacy("""{"translate":"chat.type.emote","with":["kiwi","waves"]}"""));
        Assert.Equal("§epoofimafox§r§e joined the game§r", ChatComponent.ToLegacy("""{"translate":"multiplayer.player.joined","color":"yellow","with":[{"text":"poofimafox"}]}"""));
        Assert.Equal("Fox was slain by Zombie", ChatComponent.ToLegacy("""{"translate":"death.attack.mob","with":["Fox",{"translate":"entity.Zombie.name"}]}""",
            new LangTable(new() { ["death.attack.mob"] = "%1$s was slain by %2$s", ["entity.Zombie.name"] = "Zombie" })));
    }

    [Fact]
    public void Styles_extras_arrays_and_fallbacks()
    {
        Assert.Equal("plain", ChatComponent.ToLegacy("\"plain\""));
        Assert.Equal("a§c§lb§r§cc§r", ChatComponent.ToLegacy("""{"text":"a","extra":[{"text":"b","color":"red","bold":true,"extra":[{"text":"c","bold":false}]}]}"""));
        Assert.Equal("xyz", ChatComponent.ToLegacy("""["x",{"text":"y"},"z"]"""));
        Assert.Equal("§a5§r", ChatComponent.ToLegacy("""{"score":{"name":"Fox","objective":"kills","value":"5"},"color":"green"}"""));
        Assert.Equal("100% done", ChatComponent.ToLegacy("""{"translate":"x.pct","with":["100"]}""", new LangTable(new() { ["x.pct"] = "%s%% done" })));
        // An unknown key reads as words, not a raw key (live 10-29: "commands.generic.permission" in chat).
        Assert.Equal("some unknown key Fox", ChatComponent.ToLegacy("""{"translate":"some.unknown.key","with":["Fox"]}"""));
        Assert.Equal("not json {", ChatComponent.ToLegacy("not json {"));
        Assert.Equal("", ChatComponent.ToLegacy(null));
        Assert.Equal("bold", ChatComponent.ToPlain("""{"text":"bold","bold":true}"""));
    }

    [Fact]
    public void The_client_jar_lang_table_has_the_builtin_keys()
    {
        var jar = LangTable.CandidateJars().FirstOrDefault(File.Exists);
        if (jar is null) return; // no 1.12.2 client jar on this machine: the built-in table is what runs
        var t = LangTable.FromJar(jar);
        Assert.True(t.Count > 3000);
        Assert.True(t.TryGet("chat.type.text", out var v) && v == "<%s> %s");
        Assert.True(t.TryGet("death.attack.lava", out var lava) && lava == "%1$s tried to swim in lava");
        Assert.True(t.TryGet("entity.Creeper.name", out var creeper) && creeper == "Creeper");
    }

    // ------------------------------------------------------------------ chunk sections (BlockStateContainer, BitArray, NibbleArray)

    private static int[] Uniform(int state) => Enumerable.Repeat(state, ChunkData.SectionCells).ToArray();

    [Fact]
    public void Five_bit_entries_span_longs_like_BitArray()
    {
        // 17 distinct states → 5 bits (BlockStatePaletteHashMap). Entry 12 occupies bits 60..64: 4 bits in long 0, 1 in long 1.
        var states = new int[ChunkData.SectionCells];
        for (int i = 0; i < states.Length; i++) states[i] = (i % 17 + 1) << 4; // ids 1..17, meta 0
        var w = new WireWriter();
        ChunkData.WriteStates(w, states, 5);
        var r = new WireReader(w.ToArray());
        Assert.Equal(5, r.ReadUByte());
        Assert.Equal(17, r.ReadVarInt());
        for (int i = 0; i < 17; i++) Assert.Equal((i + 1) << 4, r.ReadVarInt());
        Assert.Equal(4096 * 5 / 64, r.ReadVarInt());
        long l0 = r.ReadLong(), l1 = r.ReadLong();
        long expect0 = 0;
        for (int i = 0; i < 12; i++) expect0 |= (long)(i % 17) << (5 * i);
        expect0 |= (long)(12 & 0xF) << 60;
        Assert.Equal(expect0, l0);
        Assert.Equal(12 >> 4, l1 & 1); // the spilled top bit of entry 12
        Assert.Equal(13L, l1 >> 1 & 31); // entry 13 starts right after
        var back = new int[ChunkData.SectionCells];
        var rr = new WireReader(w.ToArray());
        ChunkData.ReadStates(ref rr, back);
        Assert.Equal(states, back);
        Assert.Equal(0, rr.Remaining);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(13)]
    public void Sections_decode_with_indirect_and_direct_palettes(int bits)
    {
        var states = new int[ChunkData.SectionCells];
        for (int i = 0; i < states.Length; i++)
        {
            int x = i & 15, z = i >> 4 & 15, y = i >> 8;
            states[i] = y == 0 ? (7 << 4) : (x == z ? (35 << 4 | (y & 7)) : 0); // bedrock floor, a wool diagonal by meta (10 states)
        }
        var body = ChunkData.EncodeBody(3, -2, full: true, [null, null, null, null, null, null, null, null, null, (int?)bits],
            [Uniform(1 << 4), null, null, null, null, null, null, null, null, states], hasSky: true, blockLight: 3, skyLight: 9, biome: 4);
        var m = ChunkData.Decode(body, hasSky: true);
        Assert.Equal((3, -2, true, 0x0201), (m.ChunkX, m.ChunkZ, m.FullChunk, m.SectionMask));
        var region = Assert.Single(m.Regions);
        Assert.Equal((48, 0, -32, 16, 256, 16), (region.X, region.Y, region.Z, region.SizeX, region.SizeY, region.SizeZ));
        Assert.Equal(1, region.GetBlockId(48, 0, -32)); // section 0: stone
        Assert.Equal(7, region.GetBlockId(50, 144, -30)); // section 9, y 0 of it: bedrock
        Assert.Equal(35, region.GetBlockId(53, 150, -27)); // x == z == 5, y 150 → meta 6
        Assert.Equal(6, region.GetMetadata(53, 150, -27));
        Assert.Equal(0, region.GetBlockId(53, 150, -28));
        Assert.Equal(0, region.GetBlockId(50, 40, -30)); // an absent section of a full chunk: air
        int at = region.Index(5, 150, 5);
        Assert.Equal((3, 9), (region.BlockLight[at], region.SkyLight[at]));
        int air = region.Index(5, 40, 5);
        Assert.Equal((0, 15), (region.BlockLight[air], region.SkyLight[air]));
        Assert.Equal(Enumerable.Repeat((byte)4, 256), m.Biomes!);
    }

    [Fact]
    public void Light_nibbles_take_the_low_half_first()
    {
        var body = ChunkData.EncodeBody(0, 0, full: false, null, [Uniform(1 << 4)], hasSky: true);
        // Rewrite the block light of cells 0 and 1: byte 0 = cell 1 << 4 | cell 0 (NibbleArray).
        var m0 = ChunkData.Decode(body, true);
        var r = new WireReader(body);
        r.ReadInt(); r.ReadInt(); r.ReadBool(); r.ReadVarInt(); r.ReadVarInt();
        int dataStart = r.Position;
        // bits (1) + palette count (1) + palette entry VarInt 16 (1) + long count VarInt 256 (2) + 256 longs: then the block light.
        int lightAt = dataStart + 1 + 1 + 1 + 2 + 256 * 8;
        body[lightAt] = 0xA5;
        var m = ChunkData.Decode(body, true);
        var reg = Assert.Single(m.Regions);
        Assert.Equal((0, 0, 0, 16, 16, 16), (reg.X, reg.Y, reg.Z, reg.SizeX, reg.SizeY, reg.SizeZ));
        Assert.Equal(5, reg.BlockLight[reg.Index(0, 0, 0)]);
        Assert.Equal(10, reg.BlockLight[reg.Index(1, 0, 0)]);
        Assert.Null(m.Biomes);
        Assert.Equal(0, m0.Regions[0].BlockLight[0]);
    }

    [Fact]
    public void Nether_chunks_carry_no_sky_light()
    {
        var body = ChunkData.EncodeBody(0, 0, full: true, null, [Uniform(87 << 4)], hasSky: false, blockLight: 2);
        var m = ChunkData.Decode(body, hasSky: false);
        Assert.False(m.HasSkyLight);
        Assert.Equal(87, m.Regions[0].GetBlockId(0, 5, 0));
        Assert.Throws<ProtocolException>(() => ChunkData.Decode(body, hasSky: true)); // reads past the data: the dimension matters
    }

    [Fact]
    public void Block_entities_come_with_the_chunk()
    {
        var sign = new NbtCompound { ["id"] = "minecraft:sign", ["x"] = 1, ["y"] = 70, ["z"] = 2, ["Text1"] = ChatComponent.Text("hi") };
        var body = ChunkData.EncodeBody(0, 0, full: true, null, [Uniform(1 << 4)], hasSky: true, blockEntities: [sign]);
        var m = ChunkData.Decode(body, true);
        var be = Assert.Single(m.BlockEntities);
        Assert.Equal("minecraft:sign", be.GetString("id"));
        Assert.Equal("hi", ChatComponent.ToLegacy(be.GetString("Text1")));
    }

    // ------------------------------------------------------------------ the connection against a fake server

    private static (Release1122Connection Client, Wire1122Peer Server) Pair()
    {
        var (a, b) = DuplexStream.CreatePair();
        return (new Release1122Connection(a) { Host = "mc.example", Port = 25570, ViewDistance = 6 }, new Wire1122Peer(b));
    }

    [Fact]
    public void Login_golden_bytes_and_compression()
    {
        var (client, server) = Pair();
        client.Send(new HandshakePacket("maid_chan"));
        // Handshake: 00, VarInt 340 (D4 02), "mc.example", port 25570 (63 E2), next state 2.
        var hs = server.Read();
        Assert.Equal(0x00, hs.Id);
        Assert.Equal("D4 02 0A 6D 63 2E 65 78 61 6D 70 6C 65 63 E2 02", ToHex(hs.Body));
        var ls = server.Read();
        Assert.Equal(0x00, ls.Id);
        Assert.Equal("09 6D 61 69 64 5F 63 68 61 6E", ToHex(ls.Body));

        server.Send(0x03, w => w.WriteVarInt(64)); // Set Compression (still uncompressed itself)
        server.Framing.Threshold = 64;
        server.Send(0x02, w => w.WriteString(Wire.OfflineUuid("maid_chan").ToString()).WriteString("maid_chan"));
        server.Send(0x23, w => w.WriteInt(42).WriteUByte(0 | 8).WriteInt(0).WriteUByte(2).WriteUByte(20).WriteString("default").WriteBool(false));
        var login = Assert.IsType<LoginRequestPacket>(client.ReadPacket());
        Assert.Equal((42, 0, (sbyte)0, (sbyte)2, (byte)20), (login.EntityId, login.ServerMode, login.Dimension, login.Difficulty, login.MaxPlayers));
        Assert.Equal(64, client.CompressionThreshold);
        Assert.Equal(Wire.OfflineUuid("maid_chan"), client.PlayerUuid);
        // handleJoinGame → Client Settings (en_us, view 6, chat full, colours, all skin parts, right hand) + MC|Brand.
        var settings = server.Read();
        Assert.Equal(0x04, settings.Id);
        Assert.Equal("05 65 6E 5F 75 73 06 00 01 7F 01", ToHex(settings.Body));
        var brand = server.Read();
        Assert.Equal(0x09, brand.Id);
        Assert.Equal("08 4D 43 7C 42 72 61 6E 64 07 76 61 6E 69 6C 6C 61", ToHex(brand.Body));
    }

    private static (Release1122Connection, Wire1122Peer) InPlay()
    {
        var (client, server) = Pair();
        client.Send(new HandshakePacket("bot"));
        server.Read(); server.Read();
        server.Send(0x02, w => w.WriteString(Wire.OfflineUuid("bot").ToString()).WriteString("bot"));
        server.Send(0x23, w => w.WriteInt(7).WriteUByte(0).WriteInt(0).WriteUByte(1).WriteUByte(20).WriteString("default").WriteBool(false));
        Assert.IsType<LoginRequestPacket>(client.ReadPacket());
        server.Read(); server.Read(); // settings, brand
        return (client, server);
    }

    [Fact]
    public void Keepalive_is_echoed_inside_the_connection()
    {
        var (client, server) = InPlay();
        server.Send(0x1F, w => w.WriteLong(-1234567890123L));
        server.Send(0x47, w => w.WriteLong(100).WriteLong(-6000)); // doDaylightCycle off: negative time of day
        var t = Assert.IsType<TimeUpdatePacket>(client.ReadPacket()); // the keep-alive never surfaces
        Assert.Equal(6000, t.Time);
        var ka = server.Read();
        Assert.Equal(0x0B, ka.Id);
        Assert.Equal(-1234567890123L, new WireReader(ka.Body).ReadLong());
    }

    [Fact]
    public void Teleport_flags_resolve_against_the_last_position_and_are_confirmed()
    {
        var (client, server) = InPlay();
        server.Send(0x2F, w => w.WriteDouble(10.5).WriteDouble(70).WriteDouble(-3.5).WriteFloat(90).WriteFloat(10).WriteUByte(0).WriteVarInt(1));
        var a = Assert.IsType<PlayerPositionLookPacket>(client.ReadPacket());
        Assert.Equal((10.5, 70.0, 71.62, -3.5, 90f, 10f), (a.X, a.Y, Math.Round(a.Stance, 2), a.Z, a.Yaw, a.Pitch));
        var confirm = server.Read();
        Assert.Equal((0x00, "01"), (confirm.Id, ToHex(confirm.Body)));
        // The client moves (C→S 0x0E golden), then a relative teleport: X/Y/Z and yaw relative (0x01|0x02|0x04|0x08), pitch absolute.
        client.Send(new PlayerPositionLookPacket { X = 11, Y = 71, Stance = 72.62, Z = -4, Yaw = 45, Pitch = 0, OnGround = true });
        var move = server.Read();
        Assert.Equal(0x0E, move.Id);
        Assert.Equal("40 26 00 00 00 00 00 00  40 51 C0 00 00 00 00 00  C0 10 00 00 00 00 00 00  42 34 00 00  00 00 00 00  01".Replace("  ", " "), ToHex(move.Body));
        server.Send(0x2F, w => w.WriteDouble(1).WriteDouble(-0.5).WriteDouble(2).WriteFloat(10).WriteFloat(-20).WriteUByte(0x0F).WriteVarInt(2));
        var b = Assert.IsType<PlayerPositionLookPacket>(client.ReadPacket());
        Assert.Equal((12.0, 70.5, -2.0, 55f, -20f), (b.X, b.Y, b.Z, b.Yaw, b.Pitch));
        var confirm2 = server.Read();
        Assert.Equal((0x00, "02"), (confirm2.Id, ToHex(confirm2.Body)));
    }

    [Fact]
    public void Movement_and_actions_golden_bytes()
    {
        var (client, server) = InPlay();
        client.Send(new PlayerPositionPacket { X = 0.5, Y = 64, Stance = 65.62, Z = 0.5, OnGround = true });
        Assert.Equal((0x0D, "3F E0 00 00 00 00 00 00 40 50 00 00 00 00 00 00 3F E0 00 00 00 00 00 00 01"), Frame(server));
        client.Send(new PlayerLookPacket { Yaw = 0, Pitch = 90, OnGround = true });
        Assert.Equal((0x0F, "00 00 00 00 42 B4 00 00 01"), Frame(server));
        // A bare on-ground packet goes out only when on-ground changed (EntityPlayerSP.onUpdateWalkingPlayer).
        client.Send(new PlayerOnGroundPacket { OnGround = true });
        client.Send(new PlayerOnGroundPacket { OnGround = false });
        Assert.Equal((0x0C, "00"), Frame(server));
        // b1.8.1 actions 4 sprint / 1 crouch → START_SPRINTING 3 / START_SNEAKING 0 (VarInt entity id, action, jump boost 0).
        client.Send(new EntityActionPacket { EntityId = 300, Action = 4 });
        Assert.Equal((0x15, "AC 02 03 00"), Frame(server));
        client.Send(new EntityActionPacket { EntityId = 7, Action = 1 });
        Assert.Equal((0x15, "07 00 00"), Frame(server));
        client.Send(new ChatPacket(new string('x', 256)));
        var chat = server.Read();
        Assert.Equal(0x02, chat.Id);
        Assert.Equal(256, new WireReader(chat.Body).ReadString(256).Length);
        Assert.Throws<ArgumentException>(() => client.Send(new ChatPacket(new string('x', 257))));
        client.Send(new RespawnPacket());
        Assert.Equal((0x03, "00"), Frame(server)); // Client Status PERFORM_RESPAWN
        client.Send(new HoldingChangePacket { Slot = 4 });
        Assert.Equal((0x1A, "00 04"), Frame(server));
        client.Send(new AnimationPacket { EntityId = 7, Animation = 1 });
        Assert.Equal((0x1D, "00"), Frame(server));
        // P2 covers digging and windows (Release1122P2Tests); the creative inventory action stays untranslated.
        Assert.True(client.CanSend(new PlayerDiggingPacket()));
        Assert.True(client.CanSend(new WindowClickPacket()));
        Assert.False(client.CanSend(new CreativeInventoryActionPacket()));
        Assert.Throws<NotSupportedException>(() => client.Send(new CreativeInventoryActionPacket()));
    }

    private static (int, string) Frame(Wire1122Peer server)
    {
        var (id, body) = server.Read();
        return (id, ToHex(body));
    }

    [Fact]
    public void Play_packets_translate_to_canonical_messages()
    {
        var (client, server) = InPlay();
        server.Send(0x41, w => w.WriteFloat(0.5f).WriteVarInt(17).WriteFloat(2.5f));
        var h = Assert.IsType<UpdateHealthPacket>(client.ReadPacket());
        Assert.Equal(((short)1, (short)17, 2.5f, 0.5f), (h.Health, h.Food, h.FoodSaturation, h.HealthExact!.Value)); // 0.5 hp isn't dead
        server.Send(0x46, w => w.WritePosition(-10, 200, 33));
        var sp = Assert.IsType<SpawnPositionPacket>(client.ReadPacket());
        Assert.Equal((-10, 200, 33), (sp.X, sp.Y, sp.Z));
        server.Send(0x0F, w => w.WriteString(ChatComponent.Translate("chat.type.text", "kiwi", "hi bot")).WriteSByte(0));
        var chat = Assert.IsType<ChatPacket>(client.ReadPacket());
        Assert.Equal(("<kiwi> hi bot", (sbyte)0), (chat.Message, chat.Position));
        Assert.NotNull(chat.Json);
        server.Send(0x1E, w => w.WriteUByte(1).WriteFloat(0));
        Assert.Equal(NewStatePacket.ReasonBeginRain, Assert.IsType<NewStatePacket>(client.ReadPacket()).Reason);
        server.Send(0x1E, w => w.WriteUByte(3).WriteFloat(1));
        var gm = Assert.IsType<NewStatePacket>(client.ReadPacket());
        Assert.Equal((NewStatePacket.ReasonGameModeChanged, (sbyte)1), (gm.Reason, gm.GameMode));
        // Reasons 7/8: the rain and thunder strengths (were dropped: the viewer never saw the weather).
        server.Send(0x1E, w => w.WriteUByte(7).WriteFloat(0.25f));
        var rain = Assert.IsType<NewStatePacket>(client.ReadPacket());
        Assert.Equal((NewStatePacket.ReasonRainLevel, 0.25f), (rain.Reason, rain.Value));
        server.Send(0x1E, w => w.WriteUByte(8).WriteFloat(0.75f));
        var thunder = Assert.IsType<NewStatePacket>(client.ReadPacket());
        Assert.Equal((NewStatePacket.ReasonThunderLevel, 0.75f), (thunder.Reason, thunder.Value));
        server.Send(0x0B, w => w.WritePosition(5, 130, -9).WriteVarInt(35 << 4 | 14));
        var bc = Assert.IsType<BlockChangePacket>(client.ReadPacket());
        Assert.Equal((5, 130, -9, (byte)35, (byte)14), (bc.X, bc.Y, bc.Z, bc.BlockId, bc.Metadata));
        server.Send(0x10, w => w.WriteInt(-1).WriteInt(2).WriteVarInt(2).WriteUByte(0x3F).WriteUByte(200).WriteVarInt(1 << 4).WriteUByte(0x00).WriteUByte(0).WriteVarInt(0));
        var mb = Assert.IsType<MultiBlockChangePacket>(client.ReadPacket());
        Assert.Equal([(-13, 200, 47, (byte)1, (byte)0), (-16, 0, 32, (byte)0, (byte)0)], mb.Records.Select(r => (r.X, r.Y, r.Z, r.BlockId, r.Metadata)));
        server.Send(0x1D, w => w.WriteInt(4).WriteInt(-5));
        var un = Assert.IsType<PreChunkPacket>(client.ReadPacket());
        Assert.Equal((4, -5, false), (un.ChunkX, un.ChunkZ, un.Load));
        server.Send(0x35, w => w.WriteInt(-1).WriteUByte(2).WriteUByte(0).WriteString("default"));
        var rp = Assert.IsType<RespawnPacket>(client.ReadPacket());
        Assert.Equal(((sbyte)-1, (sbyte)2, (sbyte)0), (rp.Dimension, rp.Difficulty, rp.GameMode));
        // In the nether now: chunk data without sky light.
        var nether = ChunkData.EncodeBody(0, 0, true, null, [Uniform(87 << 4)], hasSky: false);
        server.Send(0x20, w => w.WriteBytes(nether));
        Assert.False(Assert.IsType<ChunkSectionsMessage>(client.ReadPacket()).HasSkyLight);
        // Unknown packets (sounds, entities) are skipped, not surfaced.
        server.Send(0x49, w => w.WriteVarInt(1).WriteVarInt(0).WriteInt(0).WriteInt(0).WriteInt(0).WriteFloat(1).WriteFloat(1));
        server.Send(0x1A, w => w.WriteString(ChatComponent.Translate("multiplayer.disconnect.kicked")));
        Assert.Equal("Kicked by an operator.", Assert.IsType<KickPacket>(client.ReadPacket()).Reason);
    }

    [Fact]
    public void Player_list_names_come_from_the_uuid_map()
    {
        var (client, server) = InPlay();
        var fox = Guid.NewGuid();
        var robin = Guid.NewGuid();
        server.Send(0x2E, w =>
        {
            w.WriteVarInt(0).WriteVarInt(2);
            foreach (var (id, name) in new[] { (fox, "poofimafox"), (robin, "kiwi") })
                w.WriteUuid(id).WriteString(name).WriteVarInt(1).WriteString("textures").WriteString("abc").WriteBool(false).WriteVarInt(0).WriteVarInt(35).WriteBool(false);
        });
        var a = Assert.IsType<PlayerListItemPacket>(client.ReadPacket());
        var b = Assert.IsType<PlayerListItemPacket>(client.ReadPacket());
        Assert.Equal(("poofimafox", true, "kiwi", true), (a.PlayerName, a.Online, b.PlayerName, b.Online));
        server.Send(0x2E, w => w.WriteVarInt(4).WriteVarInt(1).WriteUuid(robin));
        var gone = Assert.IsType<PlayerListItemPacket>(client.ReadPacket());
        Assert.Equal(("kiwi", false), (gone.PlayerName, gone.Online));
    }

    [Fact]
    public void Online_mode_and_login_kicks_surface_as_kicks()
    {
        var (client, server) = Pair();
        client.Send(new HandshakePacket("bot"));
        server.Read(); server.Read();
        server.Send(0x00, w => w.WriteString("""{"text":"You are not white-listed on this server!"}"""));
        Assert.Equal("You are not white-listed on this server!", Assert.IsType<KickPacket>(client.ReadPacket()).Reason);
        var (c2, s2) = Pair();
        c2.Send(new HandshakePacket("bot"));
        s2.Read(); s2.Read();
        s2.Send(0x01, w => w.WriteString("").WriteVarInt(0).WriteVarInt(0));
        Assert.Contains("online-mode", Assert.IsType<KickPacket>(c2.ReadPacket()).Reason);
    }

    [Fact]
    public void The_profile_json_fixture_ids_match_the_translator()
    {
        // protocol.json (minecraft-data pc/1.12.2) mappings for every packet P1 reads or writes.
        var path = Path.Combine(FindRepoRoot(), "third_party", "minecraft-data", "pc", "1.12.2", "protocol.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int Id(string state, string dir, string name)
        {
            var m = doc.RootElement.GetProperty(state).GetProperty(dir).GetProperty("types").GetProperty("packet")[1][0].GetProperty("type")[1].GetProperty("mappings");
            foreach (var p in m.EnumerateObject()) if (p.Value.GetString() == name) return Convert.ToInt32(p.Name, 16);
            throw new KeyNotFoundException(name);
        }
        Assert.Equal(Ids1122.SetCompression, Id("login", "toClient", "compress"));
        Assert.Equal(Ids1122.LoginSuccess, Id("login", "toClient", "success"));
        Assert.Equal(Ids1122.BlockChange, Id("play", "toClient", "block_change"));
        Assert.Equal(Ids1122.Chat, Id("play", "toClient", "chat"));
        Assert.Equal(Ids1122.MultiBlockChange, Id("play", "toClient", "multi_block_change"));
        Assert.Equal(Ids1122.Disconnect, Id("play", "toClient", "kick_disconnect"));
        Assert.Equal(Ids1122.UnloadChunk, Id("play", "toClient", "unload_chunk"));
        Assert.Equal(Ids1122.ChangeGameState, Id("play", "toClient", "game_state_change"));
        Assert.Equal(Ids1122.KeepAlive, Id("play", "toClient", "keep_alive"));
        Assert.Equal(Ids1122.ChunkData, Id("play", "toClient", "map_chunk"));
        Assert.Equal(Ids1122.JoinGame, Id("play", "toClient", "login"));
        Assert.Equal(Ids1122.PlayerListItem, Id("play", "toClient", "player_info"));
        Assert.Equal(Ids1122.PlayerPosLook, Id("play", "toClient", "position"));
        Assert.Equal(Ids1122.Respawn, Id("play", "toClient", "respawn"));
        Assert.Equal(Ids1122.UpdateHealth, Id("play", "toClient", "update_health"));
        Assert.Equal(Ids1122.SpawnPosition, Id("play", "toClient", "spawn_position"));
        Assert.Equal(Ids1122.TimeUpdate, Id("play", "toClient", "update_time"));
        Assert.Equal(Ids1122.TeleportConfirm, Id("play", "toServer", "teleport_confirm"));
        Assert.Equal(Ids1122.ChatOut, Id("play", "toServer", "chat"));
        Assert.Equal(Ids1122.ClientStatus, Id("play", "toServer", "client_command"));
        Assert.Equal(Ids1122.ClientSettings, Id("play", "toServer", "settings"));
        Assert.Equal(Ids1122.CustomPayload, Id("play", "toServer", "custom_payload"));
        Assert.Equal(Ids1122.KeepAliveOut, Id("play", "toServer", "keep_alive"));
        Assert.Equal(Ids1122.Player, Id("play", "toServer", "flying"));
        Assert.Equal(Ids1122.PlayerPosition, Id("play", "toServer", "position"));
        Assert.Equal(Ids1122.PlayerPositionRotation, Id("play", "toServer", "position_look"));
        Assert.Equal(Ids1122.PlayerRotation, Id("play", "toServer", "look"));
        Assert.Equal(Ids1122.EntityAction, Id("play", "toServer", "entity_action"));
        Assert.Equal(Ids1122.HeldItemChange, Id("play", "toServer", "held_item_slot"));
        Assert.Equal(Ids1122.Animation, Id("play", "toServer", "arm_animation"));
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
