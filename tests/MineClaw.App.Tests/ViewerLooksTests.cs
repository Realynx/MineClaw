using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// Render parity for the browser viewer, packet → wire JSON on both versions (a real client against the fake server, b1.8.1 and
/// 1.12.2): entity equipment, head yaw, baby, variant/look, flags and vehicle; one-shot events (swing, hurt, crit, death,
/// lightning) on the entity delta feed; paintings, minecarts, falling blocks, xp orbs, item frames, armour stands, primed TNT;
/// block entities (signs, chests, beds, banners) on <c>/api/world/blockentities</c> with a <c>block_entity</c> column event.
/// </summary>
public class ViewerLooksTests
{
    private static async Task<(MinecraftClient C, FakeServer S)> Start(bool modern, Action<FakeServer>? setup = null)
    {
        var server = new FakeServer(modern);
        setup?.Invoke(server);
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
            ConnectionFactory = modern ? server.CreateConnection : server.CreateClientConnection, HostileAlertRadius = 0, AutoRespawn = false,
        };
        if (modern) o = o with { Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122 };
        var client = new MinecraftClient(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        await Wait(() => client.World.Player.OnGround);
        return (client, server);
    }

    private static async Task<bool> Wait(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    private static int F(double v) => (int)Math.Floor(v * 32); // b1.8.1 fixed point

    private static JsonElement Wire(MinecraftClient c)
    {
        var list = HiveEntityMerge.Merge([new BodyEntities("maid_chan", 0, c.World.Entities.All())], new HashSet<string>(), DateTime.UtcNow);
        return JsonDocument.Parse(SupervisorJson.Bytes(new { entities = list })).RootElement.GetProperty("entities");
    }

    private static JsonElement ById(JsonElement list, int id) => list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == id);

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entities_carry_equipment_looks_flags_and_vehicle(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var cc = c;
        using var ss = s;
        // A burning zombie in an iron helmet holding an iron sword, riding a pig; a red sheared sheep.
        s.Send(new MobSpawnPacket { EntityId = 101, Type = 54, X = F(10.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata([MetadataEntry.FromByte(0, 0x01)]) });
        s.Send(new MobSpawnPacket { EntityId = 102, Type = 90, X = F(10.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata([MetadataEntry.FromByte(EntityMeta.Mob16, 1)]) });
        s.Send(new MobSpawnPacket { EntityId = 103, Type = 91, X = F(12.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata([MetadataEntry.FromByte(EntityMeta.Mob16, 14 | 0x10)]) });
        s.Send(new EntityEquipmentPacket { EntityId = 101, Slot = 4, ItemId = (short)Items.IronHelmet, Damage = 0 });
        s.Send(new EntityEquipmentPacket { EntityId = 101, Slot = 0, ItemId = (short)Items.IronSword, Damage = 3 });
        s.Send(new AttachEntityPacket { EntityId = 101, VehicleId = 102 });
        Assert.True(await Wait(() => c.World.Entities.Get(101) is { VehicleId: 102 } z && !z.EquipmentAt(0).IsEmpty && c.World.Entities.Get(103) is { Metadata: not null }));

        var list = Wire(c);
        var zombie = ById(list, 101);
        var eq = zombie.GetProperty("equipment");
        Assert.Equal(new[] { Items.IronHelmet, 0 }, Ints(eq.GetProperty("head")));
        Assert.Equal(new[] { Items.IronSword, 3 }, Ints(eq.GetProperty("main")));
        Assert.False(eq.TryGetProperty("off", out _)); // an empty slot is left out
        Assert.Equal(1, zombie.GetProperty("flags").GetInt32() & 0x01); // on fire
        Assert.Equal(102, zombie.GetProperty("vehicle").GetInt32());
        Assert.Equal("saddle", Str(ById(list, 102), "look"));
        var sheep = ById(list, 103);
        Assert.Equal("red,sheared", Str(sheep, "look"));
        Assert.Equal(14 | 0x10, sheep.GetProperty("variant").GetInt32());
        Assert.False(sheep.TryGetProperty("baby", out _));
        Assert.False(sheep.TryGetProperty("equipment", out _));

        if (!modern) return;
        // 1.12.2 only: head look, a baby, a horse's coat, markings, saddle and armour.
        s.Send(new EntityHeadLookMessage { EntityId = 103, Yaw = 64 }); // 90°
        s.Send(new MobSpawnPacket { EntityId = 104, Type = 100, X = F(14.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata([
            MetadataEntry.FromByte(EntityMeta.Baby, 1), MetadataEntry.FromByte(EntityMeta.HorseStatus, 0x06), MetadataEntry.FromInt(EntityMeta.HorseVariant, 2 | 3 << 8),
            MetadataEntry.FromInt(EntityMeta.HorseArmor, 2)]) });
        Assert.True(await Wait(() => c.World.Entities.Get(104) is { IsBaby: true } && !float.IsNaN(c.World.Entities.Get(103)!.HeadYaw)));
        list = Wire(c);
        Assert.Equal(90, ById(list, 103).GetProperty("head_yaw").GetDouble(), 1);
        var horse = ById(list, 104);
        Assert.True(horse.GetProperty("baby").GetBoolean());
        Assert.Equal("chestnut,white_dots,tame,saddle,armor=gold", Str(horse, "look"));
        Assert.Equal(2 | 3 << 8, horse.GetProperty("variant").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_shot_events_reach_the_delta_feed_once(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var cc = c;
        using var ss = s;
        s.Send(new MobSpawnPacket { EntityId = 201, Type = 54, X = F(10.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata() });
        Assert.True(await Wait(() => c.World.Entities.Get(201) != null));
        var log = new EntityDeltaLog { MinInterval = TimeSpan.Zero };
        List<WorldEntityEvent> Events() => EntityLooks.EventsOf(c.World, DateTime.UtcNow).ToList();
        var first = log.Since(0, () => Wire2(c), Events);

        // A snowball breaking (status 3 on an object) is no death.
        s.Send(new AddObjectVehiclePacket { EntityId = 202, Type = 61, X = F(11.5), Y = F(66), Z = F(10.5) });
        Assert.True(await Wait(() => c.World.Entities.Get(202) != null));
        s.Send(new EntityStatusPacket { EntityId = 202, Status = 3 });
        s.Send(new AnimationPacket { EntityId = 201, Animation = 1 });
        s.Send(new EntityStatusPacket { EntityId = 201, Status = 2 });
        // b1.8.1's server sends Packet18 5 when a player starts eating (mc-dev EntityPlayer); 1.12.2 sends crits (SPacketAnimation 4).
        s.Send(new AnimationPacket { EntityId = 201, Animation = (sbyte)(modern ? 6 : 5) });
        s.Send(new EntityStatusPacket { EntityId = 201, Status = 3 });
        s.Send(new ThunderboltPacket { EntityId = 900, Type = 1, X = 9 * 32, Y = 64 * 32, Z = 8 * 32 });
        Assert.True(await Wait(() => Events().Count >= 5));
        var d = log.Since(first.Rev, () => Wire2(c), Events);
        Assert.Equal(new[] { "swing", "hurt", modern ? "crit" : "eat", "death", "lightning" }, d.Events.Select(e => e.Kind));
        var bolt = d.Events.Single(e => e.Kind == "lightning");
        Assert.Equal((900, 9.0, 64.0, 8.0), (bolt.Id, bolt.X!.Value, bolt.Y!.Value, bolt.Z!.Value));
        var json = JsonDocument.Parse(SupervisorJson.Bytes(new { events = d.Events })).RootElement.GetProperty("events")[0];
        Assert.Equal(new[] { "id", "kind", "at", "dim" }, json.EnumerateObject().Select(p => p.Name)); // x/y/z only for lightning
        Assert.Empty(log.Since(d.Rev, () => Wire2(c), Events).Events); // seen once, though still in the 5 s ring
        Assert.DoesNotContain(Wire2(c), e => e.Id == 900); // the bolt is an event, not an entity to draw
    }

    private static IReadOnlyList<WorldEntity> Wire2(MinecraftClient c) =>
        HiveEntityMerge.Merge([new BodyEntities("maid_chan", 0, c.World.Entities.All())], new HashSet<string>(), DateTime.UtcNow);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paintings_minecarts_falling_blocks_and_orbs_are_drawn(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var cc = c;
        using var ss = s;
        s.Send(new EntityPaintingPacket { EntityId = 301, Title = "Kebab", X = 5, Y = 65, Z = 12, Direction = 2 });
        // b1.8.1 names its carts and falling blocks by type; 1.12.2 says it in the spawn's data word.
        s.Send(modern
            ? new AddObjectVehiclePacket { EntityId = 302, Type = 10, X = F(6.5), Y = F(64), Z = F(12.5), ThrowerEntityId = 1 }
            : new AddObjectVehiclePacket { EntityId = 302, Type = 11, X = F(6.5), Y = F(64), Z = F(12.5) });
        s.Send(modern
            ? new AddObjectVehiclePacket { EntityId = 303, Type = 70, X = F(7.5), Y = F(70), Z = F(12.5), ThrowerEntityId = Blocks.Gravel }
            : new AddObjectVehiclePacket { EntityId = 303, Type = 71, X = F(7.5), Y = F(70), Z = F(12.5) });
        s.Send(new ExperienceOrbPacket { EntityId = 304, X = F(8.5), Y = F(64), Z = F(12.5), Count = 7 });
        Assert.True(await Wait(() => new[] { 301, 302, 303, 304 }.All(id => c.World.Entities.Get(id) != null)));
        var list = Wire(c);
        var painting = ById(list, 301);
        Assert.Equal(("painting", "Kebab", "south"), (Str(painting, "kind"), Str(painting, "name"), Str(painting, "facing")));
        var cart = ById(list, 302);
        Assert.Equal(("minecart", "chest"), (Str(cart, "kind"), Str(cart, "look")));
        var falling = ById(list, 303);
        Assert.Equal("falling_block", Str(falling, "kind"));
        Assert.Equal(new[] { Blocks.Gravel, 0 }, Ints(falling.GetProperty("block")));
        var orb = ById(list, 304);
        Assert.Equal(("xp_orb", 7), (Str(orb, "kind"), orb.GetProperty("count").GetInt32()));

        if (!modern) return;
        // 1.12.2: an item frame (facing north, a diamond turned 3), an armour stand (small, arms, posed head) and primed TNT.
        s.Send(new AddObjectVehiclePacket { EntityId = 305, Type = 71, X = F(9.5), Y = F(65), Z = F(12.03), ThrowerEntityId = 2 });
        s.Send(new EntityMetadataPacket { EntityId = 305, Metadata = new EntityMetadata([MetadataEntry.FromItem(EntityMeta.Item, new ItemStack(Items.Diamond, 1, 0)),
            MetadataEntry.FromInt(EntityMeta.FrameRotation, 3)]) });
        s.Send(new AddObjectVehiclePacket { EntityId = 306, Type = 78, X = F(10.5), Y = F(64), Z = F(12.5) });
        s.Send(new EntityMetadataPacket { EntityId = 306, Metadata = new EntityMetadata([MetadataEntry.FromByte(EntityMeta.StandFlags, 0x05),
            MetadataEntry.FromVector(EntityMeta.StandHead, new MetadataVector(10, -30, 0))]) });
        s.Send(new EntityEquipmentPacket { EntityId = 306, Slot = 3, ItemId = (short)Items.DiamondChestplate, Damage = 0 });
        s.Send(new AddObjectVehiclePacket { EntityId = 307, Type = 50, X = F(11.5), Y = F(64), Z = F(12.5) });
        s.Send(new EntityMetadataPacket { EntityId = 307, Metadata = new EntityMetadata([MetadataEntry.FromInt(EntityMeta.Fuse, 63)]) });
        Assert.True(await Wait(() => c.World.Entities.Get(307)?.MetaInt(EntityMeta.Fuse) == 63 && c.World.Entities.Get(306) is { Equipment: not null }
            && c.World.Entities.Get(305)?.MetaInt(EntityMeta.FrameRotation) == 3));
        list = Wire(c);
        var frame = ById(list, 305);
        Assert.Equal(("item_frame", "north", Items.Diamond, 3), (Str(frame, "kind"), Str(frame, "facing"), frame.GetProperty("item").GetInt32(), frame.GetProperty("rotation").GetInt32()));
        var stand = ById(list, 306);
        Assert.Equal(("armor_stand", "small,arms"), (Str(stand, "kind"), Str(stand, "look")));
        Assert.Equal(new[] { 10, -30, 0 }, Ints(stand.GetProperty("pose").GetProperty("head")));
        Assert.Equal(new[] { Items.DiamondChestplate, 0 }, Ints(stand.GetProperty("equipment").GetProperty("chest")));
        var tnt = ById(list, 307);
        Assert.Equal(("tnt", 63), (Str(tnt, "kind"), tnt.GetProperty("fuse").GetInt32()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Block_entities_are_served_per_column_with_a_column_event(bool modern)
    {
        var (c, s) = await Start(modern, srv =>
        {
            srv.Blocks[(9, 66, 9)] = Blocks.Chest;
            srv.Blocks[(10, 66, 9)] = Blocks.SignPost;
            srv.Blocks[(11, 66, 9)] = 26;  // bed
            srv.Blocks[(12, 66, 9)] = 176; // standing banner (1.12.2; b1.8.1 has none and never sends one)
        });
        await using var cc = c;
        using var ss = s;
        var log = new BlockDeltaLog();
        log.Watch(c.World);
        long r0 = log.Since(0, null, 100).Rev;
        s.Send(new UpdateSignPacket { X = 10, Y = 66, Z = 9, Lines = ["Fox's", "house", "", "keep out"] });
        Assert.True(await Wait(() => c.World.BlockEntities.Get(new BlockPos(10, 66, 9)) != null));
        if (modern)
        {
            s.Send(new BlockEntityMessage { X = 11, Y = 66, Z = 9, Info = new BlockEntityInfo("bed") { Color = 11 } });
            s.Send(new BlockEntityMessage { X = 12, Y = 66, Z = 9, Info = new BlockEntityInfo("banner") { Color = 15, Patterns = [new BannerPattern("cr", 1)] } });
            Assert.True(await Wait(() => c.World.BlockEntities.Get(new BlockPos(12, 66, 9)) != null));
        }
        var col = BlockEntityColumns.Build(0, 0, [c.World]);
        var json = JsonDocument.Parse(SupervisorJson.Bytes(new { columns = new[] { col } })).RootElement.GetProperty("columns")[0];
        var entities = json.GetProperty("entities").EnumerateArray().ToDictionary(e => e.GetProperty("x").GetInt32());
        Assert.Equal("chest", Str(entities[9], "kind"));                       // a chest's kind from its block (b1.8.1 sends nothing)
        Assert.Equal(["Fox's", "house", "", "keep out"], entities[10].GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("bed", Str(entities[11], "kind"));
        if (modern)
        {
            Assert.Equal(11, entities[11].GetProperty("color").GetInt32());
            var pattern = entities[12].GetProperty("patterns")[0];
            Assert.Equal(("cr", 1), (pattern.GetProperty("pattern").GetString(), pattern.GetProperty("color").GetInt32()));
        }
        else Assert.False(entities[11].TryGetProperty("color", out _)); // b1.8.1 beds are red, unsaid
        Assert.NotEqual(0, json.GetProperty("hash").GetInt64());

        var ev = log.Since(r0, null, 100).Columns.Where(x => x.Kind == BlockDeltaLog.BlockEntity).ToList();
        Assert.Contains(ev, x => (x.Cx, x.Cz) == (0, 0));

        // The sign is edited: the hash changes.
        long before = col.Hash;
        s.Send(new UpdateSignPacket { X = 10, Y = 66, Z = 9, Lines = ["Fox's", "house", "", "welcome"] });
        Assert.True(await Wait(() => c.World.BlockEntities.Get(new BlockPos(10, 66, 9))?.Lines?[3] == "welcome"));
        Assert.NotEqual(before, BlockEntityColumns.Build(0, 0, [c.World]).Hash);
    }
}
