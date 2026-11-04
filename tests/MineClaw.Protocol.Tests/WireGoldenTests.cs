using System.Text;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;

namespace MineClaw.Protocol.Tests;

/// <summary>
/// Wire goldens (complexity refactor WP04): a client/server pair of each version over an in-memory duplex, every frame's payload
/// bytes and the canonical message each side decodes (by reflection, <see cref="WireDump"/>), for every canonical packet type with
/// representative fields, plus raw frames for what only a vanilla server sends (relative teleports, raw moves, hand-made metadata,
/// player list actions, …). The digests were taken on the codecs before they were reshaped; they must not move.
/// </summary>
public class WireGoldenTests
{
    [Fact]
    public void Release1122_wire_matches_the_golden() =>
        Golden.Check("wire-1122", "DBFC52FA48F7CA48313F8B83C78ED5D3D3FFD02038BFC2026CC41DE4409FD999", sb => new Wire1122Script(sb).Run());

    [Fact]
    public void Beta181_wire_matches_the_golden() =>
        Golden.Check("wire-b181", "37DC9FDD514247E69743836ADD1031A64A6143B99B64C1929F54962EC4E05085", BetaScript.Run);
}

/// <summary>Both ends of a 1.12.2 connection on one thread: each step sends, then both ends read until nothing is left.</summary>
internal class Wire1122Harness
{
    private readonly Queue<byte> _s2c = new(), _c2s = new();
    private readonly Framing _down = new(), _up = new();
    private readonly bool _withServer;
    protected readonly StringBuilder Sb;
    protected readonly Release1122Connection Client;
    protected readonly Release1122ServerConnection Server;
    private bool _play;

    protected Wire1122Harness(StringBuilder sb, int threshold = 256, bool withServer = true)
    {
        Sb = sb;
        _withServer = withServer;
        Client = new Release1122Connection(new QueueEnd(_s2c, _c2s), ownsStream: false) { Host = "mc.example", Port = 25570, ViewDistance = 6 };
        Server = new Release1122ServerConnection(new QueueEnd(_c2s, _s2c), ownsStream: false) { CompressionThreshold = threshold };
        Client.TrafficLog = t => Traffic(t.Direction == PacketDirection.ServerBound ? "c>" : "c<", t);
        Server.TrafficLog = t => Traffic(t.Direction == PacketDirection.ClientBound ? "s>" : "s<", t);
    }

    /// <summary>After login: frames use the compression threshold, and the random UUIDs of spawns are masked.</summary>
    protected void EnterPlay(int threshold)
    {
        _play = true;
        _down.Threshold = _up.Threshold = threshold;
    }

    private void Traffic(string tag, PacketTraffic t)
    {
        var raw = t.Raw.ToArray();
        var packet = t.Packet;
        if (_play && t.Direction == PacketDirection.ClientBound)
        {
            var r = new WireReader(raw);
            MaskSpawnUuid(r.ReadVarInt(), raw, r.Position);
            if (packet is WirePacket1122 wp)
            {
                var body = wp.Body.ToArray();
                MaskSpawnUuid(wp.WireId, body, 0);
                packet = new WirePacket1122(wp.WireId, body);
            }
        }
        Sb.Append("  ").Append(tag).Append(' ').Append(WireDump.Hex(raw)).Append(" = ").Append(Show(packet)).Append('\n');
    }

    /// <summary>Spawn Object / Mob / Painting carry a fresh random UUID after the entity id: zeroed in the dump.</summary>
    private static void MaskSpawnUuid(int id, byte[] buf, int bodyStart)
    {
        if (id is not (Ids1122.SpawnObject or EntityIds1122.SpawnMob or EntityIds1122.SpawnPainting)) return;
        var r = new WireReader(buf.AsSpan(bodyStart));
        r.ReadVarInt();
        int at = bodyStart + r.Position;
        if (at + 16 <= buf.Length) Array.Clear(buf, at, 16);
    }

    /// <summary>A b1.8.1 map chunk's zlib bytes depend on the runtime's zlib: shown by its region only.</summary>
    private static string Show(IPacket p) =>
        p is MapChunkPacket mc ? $"MapChunkPacket({mc.X},{mc.Y},{mc.Z} {mc.SizeX}x{mc.SizeY}x{mc.SizeZ})" : WireDump.Of(p);

    protected void Step(string header, Action act)
    {
        Sb.Append("## ").Append(header).Append('\n');
        try { act(); }
        catch (Exception ex) { Sb.Append("  ! ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n'); }
        Drain();
    }

    protected void Down(IPacket p) => Step("down " + Show(p), () => Server.Send(p));
    protected void Up(IPacket p) => Step("up " + Show(p), () => Client.Send(p));

    protected void RawDown(string label, WireWriter w) => Step("raw down " + label, () => Enqueue(_s2c, _down.Encode(w.Span)));
    protected void RawUp(string label, WireWriter w) => Step("raw up " + label, () => Enqueue(_c2s, _up.Encode(w.Span)));

    private static void Enqueue(Queue<byte> q, byte[] frame)
    {
        foreach (var b in frame) q.Enqueue(b);
    }

    private void Drain()
    {
        for (int round = 0; round < 8; round++)
        {
            bool any = DrainOne("c", Client);
            if (_withServer) any |= DrainOne("s", Server);
            if (!any && _s2c.Count == 0 && (!_withServer || _c2s.Count == 0)) return;
        }
        Sb.Append("  (still pending)\n");
    }

    private bool DrainOne(string tag, Versioning.IGameConnection conn)
    {
        bool any = false;
        for (int i = 0; i < 200; i++)
        {
            IPacket? p;
            try { p = conn.ReadPacket(); }
            catch (Exception ex)
            {
                Sb.Append("  ").Append(tag).Append("! ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
                any = true;
                continue;
            }
            if (p is null) return any;
            any = true;
            Sb.Append("  ").Append(tag).Append(" read ").Append(Show(p)).Append('\n');
        }
        return any;
    }
}

/// <summary>The 1.12.2 corpus.</summary>
internal sealed class Wire1122Script(StringBuilder sb) : Wire1122Harness(sb)
{
    private const int Self = 100;

    public void Run()
    {
        Login();
        Basics();
        Chunks();
        Windows();
        BlockEntities();
        Players();
        Mobs();
        Objects();
        EntityState();
        RawEntities();
        RawMisc();
        Upstream();
        Sb.Append("# can send\n");
        foreach (var p in CanSendSamples()) Sb.Append("  ").Append(Client.CanSend(p) ? "yes " : "no  ").Append(WireDump.Of(p)).Append('\n');
        Down(new KickPacket("Bye now"));

        Sb.Append("# login variants\n");
        new LoginVariants(Sb).Run();
    }

    private void Login()
    {
        Up(new HandshakePacket("Bot"));
        Down(new LoginRequestPacket { EntityId = Self, ServerMode = 1, Dimension = 0, Difficulty = 2, MaxPlayers = 20 });
        EnterPlay(256);
    }

    private void Basics()
    {
        Down(new KeepAlivePacket(77));
        Down(new ChatPacket("<Bob> hi §cred"));
        Down(new ChatPacket("x") { Json = "{\"translate\":\"chat.type.text\",\"with\":[\"Bob\",{\"text\":\"hello\",\"color\":\"red\",\"bold\":true}]}", Position = 1 });
        Down(new ChatPacket("") { Json = "{\"text\":\"\",\"extra\":[{\"text\":\"a\",\"italic\":true,\"underlined\":true,\"strikethrough\":true,\"obfuscated\":true},{\"translate\":\"death.attack.mob\",\"with\":[\"Bot\",{\"translate\":\"entity.Zombie.name\"}]},{\"text\":\"b\",\"color\":\"gold\"}]}", Position = 2 });
        Down(new ChatPacket("") { Json = "[\"\",{\"text\":\"list\",\"color\":\"dark_aqua\"},{\"translate\":\"%s and %s\",\"with\":[{\"text\":\"one\"},\"two\"]},{\"score\":{\"name\":\"x\",\"objective\":\"y\",\"value\":\"5\"}},{\"selector\":\"@p\"},{\"keybind\":\"key.jump\"}]", Position = 0 });
        Down(new ChatPacket("") { Json = "\"plain string\"" });
        Down(new TimeUpdatePacket { Time = 6000 });
        Down(new TimeUpdatePacket { Time = 18000, Frozen = true });
        Down(new SpawnPositionPacket { X = -100, Y = 64, Z = 2000 });
        Down(new UpdateHealthPacket { Health = 17, Food = 12, FoodSaturation = 3.5f });
        Down(new UpdateHealthPacket { Health = 17, Food = 20, FoodSaturation = 5f, HealthExact = 16.25f });
        Down(new PlayerPositionLookPacket { X = 8.5, Y = 70, Stance = 71.62, Z = -3.25, Yaw = 90, Pitch = 10, OnGround = true });
        foreach (sbyte reason in new sbyte[] { 0, 1, 2, 3, 7, 8 })
            Down(new NewStatePacket { Reason = reason, GameMode = 1, Value = 0.75f });
        Down(new PlayerListItemPacket { PlayerName = "Alex", Online = true, Ping = 42 });
        Down(new PlayerListItemPacket { PlayerName = "Alex", Online = false });
        Down(new PlayerListItemPacket { PlayerName = "Nobody", Online = false });
        Down(new PreChunkPacket { ChunkX = 1, ChunkZ = 2, Load = true });
        Down(new PreChunkPacket { ChunkX = 1, ChunkZ = 2, Load = false });
        Down(new BlockChangePacket { X = -7, Y = 200, Z = 9, BlockId = 54, Metadata = 3 });
        Down(new BlockChangePacket { X = 1, Y = 2, Z = 3, BlockId = 35, Metadata = 31 });
        Down(new MultiBlockChangePacket
        {
            ChunkX = 2, ChunkZ = -1,
            Coordinates = [ChunkDataDecoder.EncodeMultiBlockCoordinate(15, 127, 0), ChunkDataDecoder.EncodeMultiBlockCoordinate(1, 2, 3)],
            BlockIds = [1, 4], Metadata = [0, 15],
        });
        Down(new ExplosionPacket { X = 1.5, Y = 64, Z = -2.5, Radius = 3f, Records = [new(1, 0, -1), new(-3, 2, 0)] });
        Down(new SoundEffectPacket { EffectId = 2001, X = 1, Y = 64, Z = 3, Data = 4 }); // no 1.12.2 counterpart
        Down(new KeepAlivePacket(-5));
    }

    private static ChunkRegion Column(int cx, int cz, int distinct)
    {
        var reg = new ChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16);
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        for (int y = 0; y < 70; y++)
        {
            int i = reg.Index(x, y, z);
            int k = (x * 7 + z * 3 + y) % distinct;
            reg.BlockIds[i] = (byte)(y < 3 ? 7 : 1 + k % 90);
            reg.Metadata[i] = (byte)(k / 90 % 16);
            reg.BlockLight[i] = (byte)(y & 15);
            reg.SkyLight[i] = 15;
        }
        return reg;
    }

    private void Chunks()
    {
        Down(ChunkDataDecoder.Encode(Column(0, 0, 5)));
        Down(ChunkDataDecoder.Encode(Column(1, 0, 40)));
        Down(ChunkDataDecoder.Encode(Column(-1, 2, 300)));
        Down(new RespawnPacket { Dimension = -1, Difficulty = 1, GameMode = 1, WorldHeight = 128 });
        Down(ChunkDataDecoder.Encode(Column(0, 0, 3)));
        Down(new RespawnPacket { Dimension = 0, Difficulty = 2, GameMode = 0, WorldHeight = 128 });
    }

    private static ItemStack Tagged(short id)
    {
        var tag = new NbtCompound
        {
            ["ench"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)16, ["lvl"] = (short)3 }, new NbtCompound { ["id"] = (short)34 }]),
            ["display"] = new NbtCompound { ["Name"] = "Blade", ["color"] = 0x12345678 },
            ["RepairCost"] = 4,
        };
        return new ItemStack(id, 1, 7) { Tag = Codec1122.TagOf(tag) };
    }

    private static ItemStack Potion() => new ItemStack(373, 1, 0)
    {
        Tag = Codec1122.TagOf(new NbtCompound
        {
            ["Potion"] = "minecraft:strong_healing",
            ["StoredEnchantments"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)1, ["lvl"] = (short)2 }]),
            ["CustomPotionEffects"] = new NbtList(NbtType.Compound, [new NbtCompound { ["Id"] = (sbyte)1 }]),
        }),
    };

    private void Windows()
    {
        Down(new OpenWindowPacket { WindowId = 1, InventoryType = 0, Title = "Chest", SlotCount = 27 });
        Down(new OpenWindowPacket { WindowId = 2, InventoryType = 0, Title = "Large", SlotCount = 54, Kind = "chest" });
        Down(new OpenWindowPacket { WindowId = 3, InventoryType = 1, Title = "Crafting", SlotCount = 9 });
        Down(new OpenWindowPacket { WindowId = 4, InventoryType = 2, Title = "Furnace", SlotCount = 3 });
        for (short prop = 0; prop < 4; prop++) Down(new UpdateProgressBarPacket { WindowId = 4, ProgressBar = prop, Value = (short)(100 + prop) });
        Down(new UpdateProgressBarPacket { WindowId = 1, ProgressBar = 0, Value = 9 });
        Down(new OpenWindowPacket { WindowId = 5, InventoryType = 3, Title = "Trap", SlotCount = 9 });
        Down(new OpenWindowPacket { WindowId = 6, InventoryType = 0, Title = "Brew", SlotCount = 5, Kind = "brewing_stand" });
        Down(new OpenWindowPacket { WindowId = 7, InventoryType = 0, Title = "Enchant", SlotCount = 2, Kind = "enchanting_table" });
        Down(new OpenWindowPacket { WindowId = 8, InventoryType = 0, Title = "Anvil", SlotCount = 3, Kind = "anvil" });
        var inv = new ItemStack[45];
        for (int i = 0; i < inv.Length; i++) inv[i] = i % 3 == 0 ? ItemStack.Empty : new ItemStack((short)(i + 1), (byte)(i % 64 + 1), (short)(i % 4));
        inv[36] = Tagged(276);
        inv[37] = Potion();
        Down(new WindowItemsPacket { WindowId = 0, Items = inv });
        Down(new WindowItemsPacket { WindowId = 1, Items = [ItemStack.Empty, new ItemStack(1, 64, 0), new ItemStack(4, 200, 0)] });
        Down(new SetSlotPacket { WindowId = 0, Slot = 36, Item = Tagged(278) });
        Down(new SetSlotPacket { WindowId = -1, Slot = -1, Item = new ItemStack(3, 1, 0) });
        Down(new TransactionPacket { WindowId = 1, ActionNumber = 12, Accepted = false });
        Down(new CloseWindowPacket { WindowId = 4 });
        Down(new UpdateProgressBarPacket { WindowId = 4, ProgressBar = 0, Value = 1 });
    }

    private void BlockEntities()
    {
        Down(new UpdateSignPacket { X = 1, Y = 65, Z = 2, Lines = ["Hello", "", "MineClaw", "é"] });
        Down(new UpdateSignPacket { X = 1, Y = 66, Z = 2, Lines = ["a", "b", "c", "d"], Json = ["{\"text\":\"a\",\"color\":\"red\"}", "\"b\"", "{\"text\":\"c\",\"bold\":true}", "{\"text\":\"\"}"] });
        BlockEntityInfo[] infos =
        [
            new("bed") { Color = 3 }, new("bed"),
            new("banner") { Color = 4, Patterns = [new BannerPattern("bs", 1), new BannerPattern("cr", 15)] }, new("banner"),
            new("skull") { Skull = 3, Rotation = 9, Owner = "Notch" }, new("skull"),
            new("flower_pot") { Item = "red_flower", Data = 2 }, new("flower_pot"),
            new("mob_spawner") { Mob = "zombie" }, new("mob_spawner"),
            new("note_block") { Note = 12 }, new("jukebox"), new("chest"), new("ender_chest"),
        ];
        int x = 0;
        foreach (var info in infos) Down(new BlockEntityMessage { X = x++, Y = 70, Z = -4, Info = info });
        Down(new BlockEntityMessage { X = 1, Y = 1, Z = 1, Info = null });
        Down(new BlockBreakAnimationMessage { EntityId = 77, X = 3, Y = 64, Z = -9, Stage = 6 });
    }

    private void Players()
    {
        Down(new PickupSpawnPacket { EntityId = 200, ItemId = 4, Count = 3, Damage = 0, X = 32, Y = 2048, Z = -64, MotionX = -5, MotionY = 10, MotionZ = 127 });
        Down(new PickupSpawnPacket { EntityId = 201, ItemId = 276, Count = 1, Damage = 9, X = 0, Y = 0, Z = 0, Exact = (1.25, 64.5, -7.75) });
        Down(new NamedEntitySpawnPacket { EntityId = 210, PlayerName = "Alex", X = 320, Y = 2048, Z = -64, Yaw = 200, Pitch = 10, CurrentItem = 278 });
        Down(new NamedEntitySpawnPacket { EntityId = 211, PlayerName = "Steve", X = 0, Y = 0, Z = 0, Exact = (0.5, 70.0, 0.5) });
        Down(new EntityTeleportPacket { EntityId = 210, X = -1, Y = 2, Z = -3, Yaw = 64, Pitch = 192 });
        Down(new EntityTeleportPacket { EntityId = 210, X = 0, Y = 0, Z = 0, Yaw = 1, Pitch = 2, Exact = (10.1, 64.0, -5.9) });
        Down(new EntityLookRelativeMovePacket { EntityId = 210, DX = 1, DY = -1, DZ = 2, Yaw = 3, Pitch = 4 });
        Down(new EntityRelativeMovePacket { EntityId = 210, DX = -128, DY = 127, DZ = 0 });
        Down(new EntityLookPacket { EntityId = 210, Yaw = 255, Pitch = 128 });
        Down(new EntityMetadataPacket { EntityId = 210, Metadata = SinkA() });
        Down(new EntityMetadataPacket { EntityId = 210, Metadata = SinkB() });
        Down(new EntityMetadataPacket { EntityId = Self, Metadata = new EntityMetadata([MetadataEntry.FromByte(0, 0x10), MetadataEntry.FromFloat(32, 7.5f)]) });
        Down(new CollectItemPacket { CollectedEntityId = 200, CollectorEntityId = Self });
        Down(new EntityStatusPacket { EntityId = 210, Status = 2 });
        Down(new EntityStatusPacket { EntityId = 210, Status = 3 });
        foreach (var (anim, hand) in new (sbyte, byte)[] { (1, 0), (1, 1), (2, 0), (3, 0), (5, 0), (6, 0), (7, 0) })
            Down(new AnimationPacket { EntityId = 210, Animation = anim, Hand = hand });
        for (short slot = 0; slot <= 6; slot++)
            Down(new EntityEquipmentPacket { EntityId = 210, Slot = slot, ItemId = (short)(slot == 2 ? -1 : 300 + slot), Damage = slot });
    }

    /// <summary>Every canonical index with its natural value type.</summary>
    private static EntityMetadata SinkA()
    {
        var list = new List<MetadataEntry> { MetadataEntry.FromByte(0, 0x1B), MetadataEntry.FromInt(1, 280), MetadataEntry.FromByte(16, 5), MetadataEntry.FromByte(17, 1) };
        for (int i = 32; i <= 73; i++)
        {
            byte b = (byte)i;
            list.Add(i switch
            {
                32 or 38 => MetadataEntry.FromFloat(b, 13.5f),
                35 => MetadataEntry.FromByte(b, 3),
                37 => MetadataEntry.FromItem(b, new ItemStack(276, 1, 5)),
                >= 64 and <= 69 => MetadataEntry.FromVector(b, new MetadataVector(10 + i, -20, 370)),
                72 => MetadataEntry.FromInt(b, -1),
                73 => MetadataEntry.FromInt(b, 2),
                _ => MetadataEntry.FromInt(b, i % 5 == 0 ? 0 : i * 7),
            });
        }
        return new EntityMetadata(list);
    }

    /// <summary>Every canonical index with an off type (bytes, shorts, strings), flags without hand states.</summary>
    private static EntityMetadata SinkB()
    {
        var list = new List<MetadataEntry> { MetadataEntry.FromByte(0, 0x12), MetadataEntry.FromShort(1, 300), MetadataEntry.FromShort(16, -1), MetadataEntry.FromString(17, "x") };
        for (int i = 32; i <= 73; i++)
        {
            if (i == 35) continue;
            byte b = (byte)i;
            list.Add(i % 3 == 0 ? MetadataEntry.FromShort(b, (short)(i * 11)) : i % 3 == 1 ? MetadataEntry.FromByte(b, (sbyte)-i) : MetadataEntry.FromString(b, "s"));
        }
        return new EntityMetadata(list);
    }

    private static readonly int[] MobTypes =
        [90, 91, 92, 93, 96, 102, 100, 28, 29, 31, 32, 103, 101, 120, 95, 98, 105, 54, 23, 57, 27, 51, 6, 5, 50, 52, 59, 58, 61, 55, 62, 56, 66, 68, 4, 69, 36, 34, 37, 99, 63, 300];

    private void Mobs()
    {
        for (int i = 0; i < MobTypes.Length; i++)
        {
            int eid = 300 + i;
            Down(new MobSpawnPacket { EntityId = eid, Type = (byte)Math.Min(MobTypes[i], 255), X = 32 * i, Y = 2048, Z = -32 * i, Yaw = (byte)(i * 5), Pitch = (byte)(200 + i), Metadata = SinkA() });
            Down(new EntityMetadataPacket { EntityId = eid, Metadata = SinkB() });
        }
        Down(new MobSpawnPacket { EntityId = 399, Type = 50, X = 1, Y = 2, Z = 3, Exact = (0.03125, 64.0, 2.5), Metadata = new EntityMetadata() });
    }

    private static readonly int[] ObjectTypes = [1, 2, 10, 50, 60, 61, 62, 70, 71, 73, 76, 78, 90, 91];

    private void Objects()
    {
        for (int i = 0; i < ObjectTypes.Length; i++)
        {
            int eid = 500 + i;
            Down(new AddObjectVehiclePacket { EntityId = eid, Type = (byte)ObjectTypes[i], X = 32 * i, Y = 2100, Z = 64, ThrowerEntityId = i % 2 == 0 ? 0 : Self + 1, SpeedX = (short)(100 * i), SpeedY = -200, SpeedZ = 0 });
            Down(new EntityMetadataPacket { EntityId = eid, Metadata = SinkA() });
            Down(new EntityMetadataPacket { EntityId = eid, Metadata = SinkB() });
        }
        Down(new AddObjectVehiclePacket { EntityId = 520, Type = 60, X = 0, Y = 0, Z = 0, Exact = (3.5, 65.25, -1.0) });
        Down(new ExperienceOrbPacket { EntityId = 530, X = 32, Y = 2048, Z = 96, Count = 7 });
        Down(new ExperienceOrbPacket { EntityId = 531, X = 0, Y = 0, Z = 0, Count = 1, Exact = (0.1, 0.2, 0.3) });
        for (int d = 0; d < 4; d++) Down(new EntityPaintingPacket { EntityId = 540 + d, Title = "Kebab", X = d, Y = 70, Z = -d, Direction = d });
        Down(new ThunderboltPacket { EntityId = 550, Type = 1, X = 32, Y = 2048, Z = 64 });
        Down(new EntityMetadataPacket { EntityId = 530, Metadata = SinkA() });
        Down(new EntityMetadataPacket { EntityId = 540, Metadata = SinkA() });
        Down(new EntityMetadataPacket { EntityId = 998, Metadata = SinkA() });
    }

    private void EntityState()
    {
        Down(new EntityVelocityPacket { EntityId = 300, VelocityX = -8000, VelocityY = 100, VelocityZ = 0 });
        Down(new EntityVelocityPacket { EntityId = Self, VelocityX = 1, VelocityY = 2, VelocityZ = 3 });
        Down(new EntityHeadLookMessage { EntityId = 300, Yaw = 99 });
        Down(new EntityHeadLookMessage { EntityId = 999, Yaw = 99 });
        Down(new EntityEffectPacket { EntityId = 300, EffectId = 1, Amplifier = 2, Duration = 600 });
        Down(new EntityEffectPacket { EntityId = Self, EffectId = 14, Amplifier = 0, Duration = 32767 });
        Down(new RemoveEntityEffectPacket { EntityId = 300, EffectId = 1 });
        Down(new EntityPropertiesMessage
        {
            EntityId = Self,
            Attributes =
            [
                new EntityAttribute("generic.movementSpeed", 0.1, [new AttributeModifierValue(new Guid("662a6b8d-da3e-4c1c-8813-96ea6097278d"), 0.3, 2)]),
                new EntityAttribute("generic.attackSpeed", 4.0, []),
                new EntityAttribute("generic.maxHealth", 20.0, [new AttributeModifierValue(Guid.Empty, 4, 0), new AttributeModifierValue(new Guid("00000000-0000-0000-0000-000000000001"), -0.5, 1)]),
            ],
        });
        Down(new AttachEntityPacket { EntityId = Self, VehicleId = 301 });
        Down(new AttachEntityPacket { EntityId = 210, VehicleId = 301 });
        Down(new AttachEntityPacket { EntityId = Self, VehicleId = 302 });
        Down(new AttachEntityPacket { EntityId = 210, VehicleId = -1 });
        Down(new AttachEntityPacket { EntityId = 555, VehicleId = -1 });
        Down(new AttachEntityPacket { EntityId = Self, VehicleId = -1 });
        Down(new HoldingChangePacket { Slot = 3 });
        Down(new ExperienceMessage { Bar = 0.25f, Level = 7, Total = 120 });
        Down(new DeathMessage { PlayerId = Self, KillerId = 300, Message = "Bot was slain by Zombie" });
        Down(new ItemCooldownMessage { ItemId = 368, Ticks = 20 });
        Down(new UseBedPacket { EntityId = 210, InBed = 0, X = 1, Y = 65, Z = 2 });
        Down(new DestroyEntityPacket { EntityId = 300 });
        Down(new DestroyEntityPacket { EntityId = 200 });
        Down(new DestroyEntityPacket { EntityId = 12345 });
    }

    // ------------------------------------------------------------------ raw frames (what only a vanilla server sends)

    private static WireWriter W(int id) => WireWriter.ForPacket(id);

    /// <summary>One entry of every 1.12.2 metadata serializer (<c>DataSerializers</c> 0..13), then the end.</summary>
    private static WireWriter AllSerializers(WireWriter w)
    {
        w.WriteUByte(0).WriteVarInt(0).WriteUByte(0x21);                       // flags: burning + invisible
        w.WriteUByte(1).WriteVarInt(1).WriteVarInt(250);                       // air
        w.WriteUByte(2).WriteVarInt(3).WriteString("Named");                   // custom name
        w.WriteUByte(3).WriteVarInt(6).WriteBool(true);                        // name visible
        w.WriteUByte(6).WriteVarInt(0).WriteUByte(3);                          // hand states: active, off hand
        w.WriteUByte(7).WriteVarInt(2).WriteFloat(11.5f);                      // health
        w.WriteUByte(10).WriteVarInt(1).WriteVarInt(2);                        // arrows
        w.WriteUByte(12).WriteVarInt(6).WriteBool(true);                       // baby / aggressive
        w.WriteUByte(13).WriteVarInt(1).WriteVarInt(4);
        w.WriteUByte(14).WriteVarInt(0).WriteUByte(1);
        w.WriteUByte(15).WriteVarInt(13);
        Nbt.WriteRoot(w, new NbtCompound { ["id"] = "minecraft:parrot", ["Variant"] = 9 });
        w.WriteUByte(16).WriteVarInt(13);
        Nbt.WriteRoot(w, new NbtCompound());
        w.WriteUByte(17).WriteVarInt(5).WriteSlot(Tagged(261));
        w.WriteUByte(18).WriteVarInt(4).WriteString("{\"text\":\"chat\"}");
        w.WriteUByte(19).WriteVarInt(7).WriteFloat(1.4f).WriteFloat(-90.6f).WriteFloat(float.NaN);
        w.WriteUByte(20).WriteVarInt(8).WritePosition(1, 2, 3);
        w.WriteUByte(21).WriteVarInt(9).WriteBool(true).WritePosition(-1, 5, 7);
        w.WriteUByte(22).WriteVarInt(9).WriteBool(false);
        w.WriteUByte(23).WriteVarInt(10).WriteVarInt(4);
        w.WriteUByte(24).WriteVarInt(11).WriteBool(true).WriteUuid(new Guid("11111111-2222-3333-4444-555555555555"));
        w.WriteUByte(25).WriteVarInt(11).WriteBool(false);
        w.WriteUByte(26).WriteVarInt(12).WriteVarInt(4096 + 35);
        return w.WriteUByte(0xFF);
    }

    private static WireWriter SpawnObject(int eid, int type, short vx, int data = 0) =>
        W(Ids1122.SpawnObject).WriteVarInt(eid).WriteUuid(Guid.Empty).WriteUByte((byte)type).WriteDouble(1.5).WriteDouble(70.25).WriteDouble(-3.75)
            .WriteUByte(10).WriteUByte(20).WriteInt(data).WriteShort(vx).WriteShort(-400).WriteShort(0);

    private void RawEntities()
    {
        RawDown("teleport, every flag relative", W(Ids1122.PlayerPosLook).WriteDouble(1).WriteDouble(-0.5).WriteDouble(2).WriteFloat(15).WriteFloat(-5).WriteUByte(0x1F).WriteVarInt(9));
        RawDown("teleport, x and yaw relative", W(Ids1122.PlayerPosLook).WriteDouble(0.25).WriteDouble(80).WriteDouble(4).WriteFloat(-30).WriteFloat(45).WriteUByte(0x09).WriteVarInt(10));
        RawDown("item spawn", SpawnObject(400, Ids1122.ObjectItem, 800));
        RawDown("move of an unsurfaced item", W(Ids1122.EntityRelMove).WriteVarInt(400).WriteShort(4096).WriteShort(0).WriteShort(-2048).WriteBool(true));
        RawDown("look of an unsurfaced item", W(Ids1122.EntityLook).WriteVarInt(400).WriteUByte(1).WriteUByte(2).WriteBool(true));
        RawDown("teleport of an unsurfaced item", W(Ids1122.EntityTeleport).WriteVarInt(400).WriteDouble(2).WriteDouble(71).WriteDouble(-3).WriteUByte(3).WriteUByte(4).WriteBool(true));
        RawDown("item metadata without a slot", W(Ids1122.EntityMetadata).WriteVarInt(400).WriteUByte(0).WriteVarInt(0).WriteUByte(0).WriteUByte(0xFF));
        RawDown("item metadata, empty stack", W(Ids1122.EntityMetadata).WriteVarInt(400).WriteItemMetadata(ItemStack.Empty));
        RawDown("item metadata, the stack", W(Ids1122.EntityMetadata).WriteVarInt(400).WriteItemMetadata(new ItemStack(264, 5, 0)));
        RawDown("item metadata, a new stack", W(Ids1122.EntityMetadata).WriteVarInt(400).WriteItemMetadata(Tagged(310)));
        RawDown("look+move of a surfaced item", W(Ids1122.EntityLookRelMove).WriteVarInt(400).WriteShort(-1).WriteShort(1).WriteShort(4095).WriteUByte(9).WriteUByte(8).WriteBool(false));
        RawDown("arrow spawn with velocity", SpawnObject(401, 60, 1200, Self + 1));
        RawDown("boat spawn at rest", SpawnObject(402, 1, 0));
        RawDown("mob spawn with velocity and every serializer",
            AllSerializers(W(EntityIds1122.SpawnMob).WriteVarInt(410).WriteUuid(Guid.Empty).WriteVarInt(54).WriteDouble(10).WriteDouble(64).WriteDouble(10)
                .WriteUByte(64).WriteUByte(32).WriteUByte(70).WriteShort(10).WriteShort(0).WriteShort(-10)));
        RawDown("mob spawn of a type over 255", W(EntityIds1122.SpawnMob).WriteVarInt(411).WriteUuid(Guid.Empty).WriteVarInt(300).WriteDouble(0).WriteDouble(0).WriteDouble(0)
            .WriteUByte(0).WriteUByte(0).WriteUByte(0).WriteShort(0).WriteShort(0).WriteShort(0).WriteUByte(0xFF));
        RawDown("metadata, every serializer, on a mob", AllSerializers(W(Ids1122.EntityMetadata).WriteVarInt(410)));
        RawDown("metadata, every serializer, on an arrow", AllSerializers(W(Ids1122.EntityMetadata).WriteVarInt(401)));
        RawDown("metadata of an unknown entity", AllSerializers(W(Ids1122.EntityMetadata).WriteVarInt(9999)));
        RawDown("metadata of the player itself", AllSerializers(W(Ids1122.EntityMetadata).WriteVarInt(Self)));
        RawDown("metadata, flags only", W(Ids1122.EntityMetadata).WriteVarInt(410).WriteUByte(0).WriteVarInt(0).WriteUByte(0x02).WriteUByte(0xFF));
        RawDown("metadata, hand states only", W(Ids1122.EntityMetadata).WriteVarInt(410).WriteUByte(6).WriteVarInt(0).WriteUByte(0).WriteUByte(0xFF));
        RawDown("metadata, nothing canonical", W(Ids1122.EntityMetadata).WriteVarInt(410).WriteUByte(4).WriteVarInt(6).WriteBool(true).WriteUByte(0xFF));
        RawDown("metadata, unknown serializer", W(Ids1122.EntityMetadata).WriteVarInt(410).WriteUByte(4).WriteVarInt(14).WriteUByte(0xFF));
        RawDown("metadata ran past the packet", W(Ids1122.EntityMetadata).WriteVarInt(410).WriteUByte(4).WriteVarInt(6).WriteBool(true));
        var alex = Wire.OfflineUuid("Alex2");
        RawDown("player list add with properties and a display name",
            W(Ids1122.PlayerListItem).WriteVarInt(0).WriteVarInt(2)
                .WriteUuid(alex).WriteString("Alex2").WriteVarInt(2).WriteString("textures").WriteString("abc").WriteBool(true).WriteString("sig").WriteString("other").WriteString("v").WriteBool(false)
                .WriteVarInt(1).WriteVarInt(40000).WriteBool(true).WriteString("{\"text\":\"Alex the 2nd\"}")
                .WriteUuid(Guid.Empty).WriteString("Zed").WriteVarInt(0).WriteVarInt(3).WriteVarInt(5).WriteBool(false));
        RawDown("player spawn with metadata", AllSerializers(W(Ids1122.SpawnPlayer).WriteVarInt(420).WriteUuid(alex).WriteDouble(5).WriteDouble(64).WriteDouble(5).WriteUByte(1).WriteUByte(2)));
        RawDown("player spawn, unknown uuid, no metadata", W(Ids1122.SpawnPlayer).WriteVarInt(421).WriteUuid(new Guid("abcdefab-cdef-abcd-efab-cdefabcdefab")).WriteDouble(5).WriteDouble(64).WriteDouble(5).WriteUByte(1).WriteUByte(2));
        RawDown("player spawn, empty metadata", W(Ids1122.SpawnPlayer).WriteVarInt(422).WriteUuid(alex).WriteDouble(5).WriteDouble(64).WriteDouble(5).WriteUByte(1).WriteUByte(2).WriteUByte(0xFF));
        RawDown("player list game mode", W(Ids1122.PlayerListItem).WriteVarInt(1).WriteVarInt(1).WriteUuid(alex).WriteVarInt(1));
        RawDown("player list latency", W(Ids1122.PlayerListItem).WriteVarInt(2).WriteVarInt(1).WriteUuid(alex).WriteVarInt(99));
        RawDown("player list display names", W(Ids1122.PlayerListItem).WriteVarInt(3).WriteVarInt(2).WriteUuid(alex).WriteBool(true).WriteString("{\"text\":\"x\"}").WriteUuid(alex).WriteBool(false));
        RawDown("player list remove", W(Ids1122.PlayerListItem).WriteVarInt(4).WriteVarInt(3).WriteUuid(alex).WriteUuid(Guid.Empty).WriteUuid(new Guid("abcdefab-cdef-abcd-efab-cdefabcdefab")));
        RawDown("player list unknown action", W(Ids1122.PlayerListItem).WriteVarInt(7).WriteVarInt(1).WriteUuid(alex));
        RawDown("player list count out of range", W(Ids1122.PlayerListItem).WriteVarInt(0).WriteVarInt(500));
        RawDown("lightning? no, another global", W(EntityIds1122.SpawnGlobal).WriteVarInt(430).WriteUByte(2).WriteDouble(1).WriteDouble(2).WriteDouble(3));
        foreach (int status in new[] { 2, 3, 9, 33, 36, 37 })
            RawDown($"status {status}", W(Ids1122.EntityStatus).WriteInt(410).WriteSByte((sbyte)status));
        foreach (int anim in new[] { 0, 1, 2, 3, 4, 5, 9 })
            RawDown($"animation {anim}", W(Ids1122.EntityAnimation).WriteVarInt(410).WriteUByte((byte)anim));
        foreach (int slot in new[] { 0, 1, 2, 3, 4, 5, 7 })
            RawDown($"equipment slot {slot}", W(Ids1122.EntityEquipment).WriteVarInt(410).WriteVarInt(slot).WriteSlot(slot == 3 ? ItemStack.Empty : Tagged(306)));
        RawDown("entity keep-alive", W(EntityIds1122.Entity).WriteVarInt(410));
        RawDown("moves of an unknown entity", W(Ids1122.EntityRelMove).WriteVarInt(4444).WriteShort(1).WriteShort(1).WriteShort(1).WriteBool(true));
        RawDown("look of an unknown entity", W(Ids1122.EntityLook).WriteVarInt(4444).WriteUByte(1).WriteUByte(1).WriteBool(true));
        RawDown("teleport of an unknown entity", W(Ids1122.EntityTeleport).WriteVarInt(4444).WriteDouble(1).WriteDouble(1).WriteDouble(1).WriteUByte(1).WriteUByte(1).WriteBool(true));
        RawDown("head look of an unknown entity", W(EntityIds1122.HeadLook).WriteVarInt(4444).WriteUByte(5));
        RawDown("passengers: two riders", W(EntityIds1122.SetPassengers).WriteVarInt(402).WriteVarInt(2).WriteVarInt(410).WriteVarInt(Self));
        RawDown("passengers: one leaves, one joins", W(EntityIds1122.SetPassengers).WriteVarInt(402).WriteVarInt(2).WriteVarInt(Self).WriteVarInt(420));
        RawDown("passengers: all leave", W(EntityIds1122.SetPassengers).WriteVarInt(402).WriteVarInt(0));
        RawDown("passengers: count out of range", W(EntityIds1122.SetPassengers).WriteVarInt(402).WriteVarInt(99));
        RawDown("leash", W(EntityIds1122.AttachEntity).WriteInt(410).WriteInt(Self));
        RawDown("use bed", W(EntityIds1122.UseBed).WriteVarInt(420).WriteLong(Wire.PackPosition(-5, 64, 7)));
        RawDown("combat enter", W(EntityIds1122.CombatEvent).WriteVarInt(0));
        RawDown("combat end", W(EntityIds1122.CombatEvent).WriteVarInt(1).WriteVarInt(20).WriteInt(410));
        RawDown("combat death", W(EntityIds1122.CombatEvent).WriteVarInt(2).WriteVarInt(Self).WriteInt(410).WriteString("{\"translate\":\"death.attack.mob\",\"with\":[\"Bot\",\"Zombie\"]}"));
        RawDown("properties count out of range", W(EntityIds1122.EntityProperties).WriteVarInt(410).WriteInt(1000));
        RawDown("modifier count out of range", W(EntityIds1122.EntityProperties).WriteVarInt(410).WriteInt(1).WriteString("generic.armor").WriteDouble(2).WriteVarInt(1000));
        RawDown("destroy three", W(Ids1122.DestroyEntities).WriteVarInt(3).WriteVarInt(410).WriteVarInt(7777).WriteVarInt(402));
        RawDown("destroy count out of range", W(Ids1122.DestroyEntities).WriteVarInt(600));
        RawDown("held item change", W(Ids1122.HeldItemChangeIn).WriteSByte(8));
        RawDown("experience", W(EntityIds1122.SetExperience).WriteFloat(0.5f).WriteVarInt(30).WriteVarInt(1395));
        RawDown("cooldown", W(EntityIds1122.SetCooldown).WriteVarInt(432).WriteVarInt(100));
        RawDown("remove effect", W(EntityIds1122.RemoveEffect).WriteVarInt(Self).WriteUByte(200));
        RawDown("effect with a long duration", W(EntityIds1122.EntityEffect).WriteVarInt(Self).WriteSByte(5).WriteSByte(-1).WriteVarInt(100000).WriteUByte(3));
        RawDown("collect", W(Ids1122.CollectItem).WriteVarInt(400).WriteVarInt(Self).WriteVarInt(5));
        RawDown("velocity", W(EntityIds1122.Velocity).WriteVarInt(Self).WriteShort(-1).WriteShort(2).WriteShort(-3));
        RawDown("painting", W(EntityIds1122.SpawnPainting).WriteVarInt(440).WriteUuid(Guid.Empty).WriteString("Alban").WriteLong(Wire.PackPosition(1, 2, 3)).WriteUByte(3));
        RawDown("orb", W(EntityIds1122.SpawnXpOrb).WriteVarInt(441).WriteDouble(0.5).WriteDouble(1.5).WriteDouble(2.5).WriteShort(17));
    }

    private void RawMisc()
    {
        RawDown("game state 4", W(Ids1122.ChangeGameState).WriteUByte(4).WriteFloat(0));
        RawDown("game state 5", W(Ids1122.ChangeGameState).WriteUByte(5).WriteFloat(101));
        RawDown("game state 3, survival", W(Ids1122.ChangeGameState).WriteUByte(3).WriteFloat(0.4f));
        RawDown("time, negative", W(Ids1122.TimeUpdate).WriteLong(5).WriteLong(-6000));
        RawDown("health, fractional", W(Ids1122.UpdateHealth).WriteFloat(0.01f).WriteVarInt(3).WriteFloat(0));
        RawDown("join game again, hardcore flag", W(Ids1122.JoinGame).WriteInt(Self).WriteUByte(8 | 2).WriteInt(-1).WriteUByte(3).WriteUByte(8).WriteString("flat").WriteBool(true));
        RawDown("respawn into the overworld", W(Ids1122.Respawn).WriteInt(0).WriteUByte(1).WriteUByte(9).WriteString("default"));
        RawDown("block state over 255", W(Ids1122.BlockChange).WriteLong(Wire.PackPosition(1, 2, 3)).WriteVarInt(300 << 4 | 2));
        RawDown("multi block change, out of range", W(Ids1122.MultiBlockChange).WriteInt(0).WriteInt(0).WriteVarInt(1000));
        RawDown("multi block change, state over 255", W(Ids1122.MultiBlockChange).WriteInt(0).WriteInt(0).WriteVarInt(2).WriteShort(0x1234).WriteVarInt(4096 << 4).WriteShort(0x0F7F).WriteVarInt(17));
        RawDown("explosion, out of range", W(Ids1122.Explosion).WriteFloat(0).WriteFloat(0).WriteFloat(0).WriteFloat(1).WriteInt(1000));
        RawDown("open window, hopper", W(Ids1122.OpenWindow).WriteUByte(11).WriteString("minecraft:hopper").WriteString("{\"text\":\"Hopper\"}").WriteUByte(5));
        RawDown("open window, villager", W(Ids1122.OpenWindow).WriteUByte(12).WriteString("minecraft:villager").WriteString("{\"translate\":\"entity.Villager.name\"}").WriteUByte(3));
        RawDown("open window, beacon", W(Ids1122.OpenWindow).WriteUByte(13).WriteString("minecraft:beacon").WriteString("\"Beacon\"").WriteUByte(1));
        RawDown("open window, shulker box", W(Ids1122.OpenWindow).WriteUByte(14).WriteString("minecraft:shulker_box").WriteString("\"Box\"").WriteUByte(27));
        RawDown("open window, dropper", W(Ids1122.OpenWindow).WriteUByte(15).WriteString("minecraft:dropper").WriteString("\"Dropper\"").WriteUByte(9));
        RawDown("open window, horse", W(Ids1122.OpenWindow).WriteUByte(16).WriteString("EntityHorse").WriteString("\"Horse\"").WriteUByte(2).WriteInt(500));
        RawDown("open window, container", W(Ids1122.OpenWindow).WriteUByte(17).WriteString("minecraft:container").WriteString("\"Big\"").WriteUByte(200));
        RawDown("open window, furnace", W(Ids1122.OpenWindow).WriteUByte(18).WriteString("minecraft:furnace").WriteString("\"F\"").WriteUByte(3));
        for (int prop = 0; prop < 5; prop++)
            RawDown($"furnace property {prop}", W(Ids1122.WindowProperty).WriteUByte(18).WriteShort((short)prop).WriteShort(77));
        RawDown("close the furnace", W(Ids1122.CloseWindow).WriteUByte(18));
        RawDown("furnace property after close", W(Ids1122.WindowProperty).WriteUByte(18).WriteShort(3).WriteShort(77));
        foreach (int index in new[] { 0, 8, 9, 35, 36, 39, 40, 41, -3 })
            RawDown($"set slot -2 index {index}", W(Ids1122.SetSlot).WriteSByte(-2).WriteShort((short)index).WriteSlot(new ItemStack(1, 2, 3)));
        RawDown("window items count out of range", W(Ids1122.WindowItems).WriteUByte(1).WriteShort(5000));
        RawDown("confirm transaction", W(Ids1122.ConfirmTransaction).WriteSByte(1).WriteShort(-4).WriteBool(true));
        RawDown("block break animation", W(Ids1122.BlockBreakAnim).WriteVarInt(410).WriteLong(Wire.PackPosition(4, 5, 6)).WriteSByte(-1));
        RawDown("block entity, spawner", BlockEntity(1, new NbtCompound { ["id"] = "minecraft:mob_spawner", ["SpawnData"] = new NbtCompound { ["id"] = "minecraft:skeleton" } }));
        RawDown("block entity, sign action with a non-sign", BlockEntity(Codec1122.SignAction, new NbtCompound { ["id"] = "minecraft:banner", ["Base"] = 2 }));
        RawDown("block entity, sign", BlockEntity(Codec1122.SignAction, Codec1122.SignTag(4, 5, 6, ["one", "two", "", "four"], ["{\"text\":\"one\",\"color\":\"blue\"}", "\"two\"", "\"\"", "{\"text\":\"four\",\"italic\":true}"])));
        RawDown("block entity, empty root", W(Ids1122.UpdateBlockEntity).WriteLong(Wire.PackPosition(4, 5, 6)).WriteUByte(5).WriteUByte(0));
        RawDown("block entity, no id", BlockEntity(0, new NbtCompound { ["x"] = 4 }));
        RawDown("block entity, music", BlockEntity(0, new NbtCompound { ["id"] = "Music", ["note"] = (sbyte)4 }));
        RawDown("block entity, jukebox", BlockEntity(0, new NbtCompound { ["id"] = "minecraft:jukebox", ["RecordItem"] = new NbtCompound { ["id"] = "minecraft:record_cat" } }));
        RawDown("block entity, flower pot with no item", BlockEntity(5, new NbtCompound { ["id"] = "minecraft:flower_pot", ["Item"] = "minecraft:air", ["Data"] = 0 }));
        RawDown("chunk with block entities", ChunkWithEntities());
        RawDown("unload", W(Ids1122.UnloadChunk).WriteInt(-4).WriteInt(9));
        RawDown("sound (unsurfaced)", W(0x49).WriteVarInt(3).WriteVarInt(0).WriteInt(8).WriteInt(64).WriteInt(8).WriteFloat(1).WriteFloat(1));
        RawDown("difficulty (unsurfaced)", W(Ids1122.Difficulty).WriteUByte(2));
        RawDown("disconnect", W(Ids1122.Disconnect).WriteString("{\"translate\":\"multiplayer.disconnect.kicked\"}"));
        RawDown("keep alive", W(Ids1122.KeepAlive).WriteLong(long.MinValue));
        RawDown("chat, game info", W(Ids1122.Chat).WriteString("{\"text\":\"bar\"}").WriteSByte(2));
    }

    private static WireWriter BlockEntity(int action, NbtCompound tag)
    {
        var w = W(Ids1122.UpdateBlockEntity).WriteLong(Wire.PackPosition(4, 5, 6)).WriteUByte((byte)action);
        Nbt.WriteRoot(w, tag);
        return w;
    }

    private static WireWriter ChunkWithEntities()
    {
        var sections = new int[]?[ChunkData.Sections];
        var st = new int[ChunkData.SectionCells];
        for (int i = 0; i < st.Length; i++) st[i] = i % 7 == 0 ? 63 << 4 | (i % 8) : 1 << 4;
        sections[4] = st;
        var bes = new List<NbtCompound>
        {
            Codec1122.SignTag(3, 66, 5, ["a", "b", "c", "d"]),
            Codec1122.SignTag(4, 66, 5, ["x", "", "", ""], ["{\"text\":\"x\",\"color\":\"green\"}", "\"\"", "\"\"", "\"\""]),
            new() { ["id"] = "minecraft:sign", ["x"] = 1 }, // no lines, no coordinates
            new() { ["id"] = "minecraft:chest", ["x"] = 2, ["y"] = 65, ["z"] = 3, ["Items"] = new NbtList(NbtType.Compound, []) },
            new() { ["id"] = "minecraft:bed", ["x"] = 6, ["y"] = 65, ["z"] = 3, ["color"] = 11 },
            new() { ["id"] = "minecraft:skull", ["SkullType"] = (sbyte)1 }, // no position
        };
        var body = ChunkData.EncodeBody(0, 0, full: true, null, sections, hasSky: true, blockLight: 3, skyLight: 12, biome: 4, blockEntities: bes);
        return W(Ids1122.ChunkData).WriteBytes(body);
    }

    // ------------------------------------------------------------------ client → server

    private void Upstream()
    {
        Up(new ChatPacket("hello there"));
        Up(new PlayerOnGroundPacket { OnGround = true });
        Up(new PlayerOnGroundPacket { OnGround = true });
        Up(new PlayerOnGroundPacket { OnGround = false });
        Up(new PlayerPositionPacket { X = 1.5, Y = 64, Stance = 65.62, Z = -3.25, OnGround = true });
        Up(new PlayerOnGroundPacket { OnGround = true });
        Up(new PlayerPositionLookPacket { X = 8.5, Y = 70, Stance = 71.62, Z = 8.5, Yaw = 90, Pitch = 10, OnGround = false });
        Up(new PlayerLookPacket { Yaw = 270.5f, Pitch = -45f, OnGround = true });
        Up(new PlayerOnGroundPacket { OnGround = true });
        RawDown("teleport relative to what was sent", W(Ids1122.PlayerPosLook).WriteDouble(1).WriteDouble(1).WriteDouble(1).WriteFloat(1).WriteFloat(1).WriteUByte(0x1F).WriteVarInt(11));
        for (sbyte a = 0; a <= 6; a++) Up(new EntityActionPacket { EntityId = Self, Action = a });
        Up(new RespawnPacket());
        Up(new HoldingChangePacket { Slot = 4 });
        Up(new AnimationPacket { EntityId = Self, Animation = 1 });
        Up(new AnimationPacket { EntityId = Self, Animation = 1, Hand = 1 });
        Up(new AnimationPacket { EntityId = Self, Animation = 2 });
        Up(new KeepAlivePacket(5));
        Up(new KickPacket("quit"));
        Up(new WindowClickPacket { WindowId = 1, Slot = 3, ActionNumber = 1, ClickedItem = new ItemStack(1, 64, 0) });
        Up(new WindowClickPacket { WindowId = 1, Slot = 4, ActionNumber = 2, RightClick = true });
        Up(new WindowClickPacket { WindowId = 0, Slot = 36, ActionNumber = 3, Shift = true, ClickedItem = Tagged(276) });
        Up(new WindowClickPacket { WindowId = 0, Slot = 36, ActionNumber = 4, Mode = WindowClickPacket.ModeSwap, Button = 3 });
        Up(new WindowClickPacket { WindowId = 0, Slot = 9, ActionNumber = 5, Mode = WindowClickPacket.ModeThrow, Button = 1 });
        Up(new WindowClickPacket { WindowId = 0, Slot = -999, ActionNumber = 6, Mode = WindowClickPacket.ModeQuickCraft, Button = 4 });
        Up(new WindowClickPacket { WindowId = 0, Slot = 10, ActionNumber = 7, Mode = WindowClickPacket.ModePickupAll });
        Up(new WindowClickPacket { WindowId = 0, Slot = 11, ActionNumber = 8, Mode = WindowClickPacket.ModeClone, Button = 2 });
        Up(new EnchantItemPacket { WindowId = 7, Enchantment = 2 });
        Up(new TransactionPacket { WindowId = 1, ActionNumber = -3, Accepted = true });
        Up(new CloseWindowPacket { WindowId = 1 });
        for (byte s = 0; s <= 6; s++) Up(new PlayerDiggingPacket { Status = s, X = -5, Y = 63, Z = 12, Face = s == 2 ? (byte)7 : (byte)1 });
        Up(PlayerBlockPlacementPacket.UseItem(new ItemStack(261, 1, 0), 1));
        Up(PlayerBlockPlacementPacket.UseItem(ItemStack.Empty));
        Up(new PlayerBlockPlacementPacket { X = 10, Y = 64, Z = -10, Direction = 1, HeldItem = new ItemStack(4, 32, 0), Cursor = (0.25f, 1f, 0.75f) });
        for (byte face = 0; face <= 5; face++) Up(new PlayerBlockPlacementPacket { X = 10, Y = 64, Z = -10, Direction = face, Hand = (byte)(face % 2) });
        Up(new UseEntityPacket { TargetEntityId = 410, LeftClick = true });
        Up(new UseEntityPacket { TargetEntityId = 410, Hand = 1 });
        Up(new UseEntityPacket { TargetEntityId = 410, InteractAt = (0.5f, 1.25f, -0.5f) });
        Up(new UpdateSignPacket { X = 1, Y = 65, Z = 2, Lines = ["a", "b", "c", "d"] });
        Up(new UpdateSignPacket { X = 1, Y = 65, Z = 2, Lines = ["only", "two"] });
        Up(new CreativeInventoryActionPacket { Slot = 36, ItemId = 1, Count = 64, Damage = 0 });
        RawUp("client status 1", W(Ids1122.ClientStatus).WriteVarInt(1));
        RawUp("entity action 7", W(Ids1122.EntityAction).WriteVarInt(Self).WriteVarInt(7).WriteVarInt(0));
        RawUp("player abilities (ignored)", W(0x13).WriteUByte(2).WriteFloat(0.05f).WriteFloat(0.1f));
        RawUp("use entity, interact at", W(Ids1122.UseEntity).WriteVarInt(9).WriteVarInt(2).WriteFloat(1).WriteFloat(2).WriteFloat(3).WriteVarInt(0));
    }

    private static IEnumerable<IPacket> CanSendSamples()
    {
        foreach (var p in RoundTripTests.Samples()) yield return p;
        yield return new AnimationPacket { Animation = 1, Hand = 1 };
        yield return new AnimationPacket { Animation = 2 };
        yield return new EnchantItemPacket();
        yield return new BlockEntityMessage();
    }
}

/// <summary>The login answers a 1.12.2 server can give that the fake server never sends.</summary>
internal sealed class LoginVariants(StringBuilder sb) : Wire1122Harness(sb, withServer: false)
{
    public void Run()
    {
        Up(new HandshakePacket("Bot"));
        RawDown("login disconnect", WireWriter.ForPacket(Ids1122.LoginDisconnect).WriteString("{\"text\":\"Banned\",\"color\":\"red\"}"));
        RawDown("encryption request", WireWriter.ForPacket(Ids1122.EncryptionRequest).WriteString("").WriteVarInt(1).WriteUByte(1).WriteVarInt(1).WriteUByte(2));
        RawDown("unexpected login packet", WireWriter.ForPacket(0x07));
        RawDown("login success without compression", WireWriter.ForPacket(Ids1122.LoginSuccess).WriteString("not-a-uuid").WriteString("Bot"));
        RawDown("chat in play", WireWriter.ForPacket(Ids1122.Chat).WriteString("{\"text\":\"hi\"}").WriteSByte(0));
        new Uncompressed(Sb).Run();
    }

    /// <summary>A server that never sets compression.</summary>
    private sealed class Uncompressed(StringBuilder sb) : Wire1122Harness(sb, threshold: -1)
    {
        public void Run()
        {
            Up(new HandshakePacket("Bot2"));
            Down(new LoginRequestPacket { EntityId = 5, ServerMode = 0, Dimension = 1, Difficulty = 0, MaxPlayers = 8 });
            EnterPlay(-1);
            Down(new ChatPacket("no compression"));
            Up(new ChatPacket("fine"));
            Up(new RespawnPacket());
        }
    }
}

/// <summary>The b1.8.1 corpus: every packet class (<see cref="RoundTripTests.Samples"/>) through a <see cref="PacketConnection"/> pair
/// in each direction it is allowed.</summary>
internal static class BetaScript
{
    public static void Run(StringBuilder sb)
    {
        var s2c = new Queue<byte>();
        var c2s = new Queue<byte>();
        using var client = new PacketConnection(new QueueEnd(s2c, c2s), PacketDirection.ClientBound, ownsStream: false);
        using var server = new PacketConnection(new QueueEnd(c2s, s2c), PacketDirection.ServerBound, ownsStream: false);
        client.TrafficLog = t => sb.Append("  c").Append(t.Direction == PacketDirection.ServerBound ? '>' : '<').Append(' ').Append(WireDump.Hex(t.Raw.Span)).Append(" = ").Append(WireDump.Of(t.Packet)).Append('\n');
        server.TrafficLog = t => sb.Append("  s").Append(t.Direction == PacketDirection.ClientBound ? '>' : '<').Append(' ').Append(WireDump.Hex(t.Raw.Span)).Append(" = ").Append(WireDump.Of(t.Packet)).Append('\n');
        foreach (var p in Samples())
        {
            sb.Append("## ").Append(WireDump.Of(p)).Append('\n');
            if (PacketRegistry.IsAllowed(p.Id, PacketDirection.ClientBound))
            {
                server.Send(p);
                while (client.ReadPacket() is { } got) sb.Append("  c read ").Append(WireDump.Of(got)).Append('\n');
            }
            if (PacketRegistry.IsAllowed(p.Id, PacketDirection.ServerBound))
            {
                sb.Append("  can send: ").Append(client.CanSend(p)).Append('\n');
                client.Send(p);
                while (server.ReadPacket() is { } got) sb.Append("  s read ").Append(WireDump.Of(got)).Append('\n');
            }
        }
    }

    private static IEnumerable<IPacket> Samples()
    {
        foreach (var p in RoundTripTests.Samples()) yield return p;
        // Metadata of every type and index range, more slot shapes, the 1.12.2-only click / dig / use forms.
        yield return new MobSpawnPacket
        {
            EntityId = 1, Type = 90, X = -1, Y = -2, Z = -3, Yaw = 252, Pitch = 127,
            Metadata = new EntityMetadata([MetadataEntry.FromByte(31, -1), MetadataEntry.FromShort(0, short.MinValue), MetadataEntry.FromInt(5, int.MaxValue),
                MetadataEntry.FromFloat(6, float.NegativeInfinity), MetadataEntry.FromString(7, ""), MetadataEntry.FromItem(8, ItemStack.Empty),
                MetadataEntry.FromVector(9, new MetadataVector(int.MinValue, 0, -1))]),
        };
        yield return new EntityMetadataPacket { EntityId = 2, Metadata = new EntityMetadata() };
        yield return new WindowItemsPacket { WindowId = 5, Items = [] };
        yield return new SetSlotPacket { WindowId = 1, Slot = 0, Item = ItemStack.Empty };
        yield return new WindowClickPacket { WindowId = 0, Slot = 1, Mode = WindowClickPacket.ModeSwap, Button = 2 };
        yield return new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusSwapHands };
        yield return new PlayerBlockPlacementPacket { X = 1, Y = 2, Z = 3, Direction = 4, Hand = 1 };
        yield return new AnimationPacket { EntityId = 3, Animation = 1, Hand = 1 };
        yield return new UseEntityPacket { TargetEntityId = 4, InteractAt = (1, 2, 3) };
    }
}
