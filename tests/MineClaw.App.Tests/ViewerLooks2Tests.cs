using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// The last viewer looks, packet → wire JSON on both fake servers (b1.8.1 and 1.12.2): the enchantment glint on equipment, framed
/// and dropped items; an enderman's carried block; an iron golem's offered flower; parrots on shoulders; arrows stuck in living
/// entities; styled 1.12.2 sign text as rich runs.
/// </summary>
public class ViewerLooks2Tests
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

    private static int F(double v) => (int)Math.Floor(v * 32);

    private static JsonElement Wire(MinecraftClient c)
    {
        var list = HiveEntityMerge.Merge([new BodyEntities("maid_chan", 0, c.World.Entities.All())], new HashSet<string>(), DateTime.UtcNow);
        return JsonDocument.Parse(SupervisorJson.Bytes(new { entities = list })).RootElement.GetProperty("entities");
    }

    private static JsonElement ById(JsonElement list, int id) => list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == id);

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static ItemTag Enchanted() => Codec1122.TagOf(new NbtCompound
    {
        ["ench"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)16, ["lvl"] = (short)5 }]),
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enchanted_equipment_and_framed_items_glint(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var cc = c;
        using var ss = s;
        // A zombie with an enchanted sword, enchanted dyed leather boots and a plain helmet. The b1.8.1 wire has no item NBT.
        var boots = Codec1122.TagOf(new NbtCompound
        {
            ["ench"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)0, ["lvl"] = (short)1 }]),
            ["display"] = new NbtCompound { ["color"] = 0x3366CC },
        });
        s.Send(new MobSpawnPacket { EntityId = 401, Type = 54, X = F(10.5), Y = F(64), Z = F(10.5) });
        s.Send(new EntityEquipmentPacket { EntityId = 401, Slot = 0, ItemId = (short)Items.DiamondSword, Damage = 0, Tag = Enchanted() });
        s.Send(new EntityEquipmentPacket { EntityId = 401, Slot = 1, ItemId = (short)Items.LeatherBoots, Damage = 0, Tag = boots });
        s.Send(new EntityEquipmentPacket { EntityId = 401, Slot = 4, ItemId = (short)Items.IronHelmet, Damage = 0 });
        Assert.True(await Wait(() => c.World.Entities.Get(401) is { } z && !z.EquipmentAt(0).IsEmpty && !z.EquipmentAt(1).IsEmpty && !z.EquipmentAt(4).IsEmpty));

        var eq = ById(Wire(c), 401).GetProperty("equipment");
        if (modern)
        {
            Assert.Equal($"[{Items.DiamondSword},0,{{\"glint\":true}}]", eq.GetProperty("main").GetRawText());
            Assert.Equal($"[{Items.LeatherBoots},0,{0x3366CC},{{\"glint\":true}}]", eq.GetProperty("feet").GetRawText());
        }
        else Assert.Equal($"[{Items.DiamondSword},0]", eq.GetProperty("main").GetRawText());
        Assert.Equal($"[{Items.IronHelmet},0]", eq.GetProperty("head").GetRawText()); // no NBT, no glint

        if (!modern) return;
        // 1.12.2: an item frame holding an enchanted book's twin (an enchanted stick) and one holding a plain diamond.
        s.Send(new AddObjectVehiclePacket { EntityId = 402, Type = 71, X = F(9.5), Y = F(65), Z = F(12.03), ThrowerEntityId = 2 });
        s.Send(new EntityMetadataPacket { EntityId = 402, Metadata = new EntityMetadata([MetadataEntry.FromItem(EntityMeta.Item, new ItemStack(Items.Stick, 1, 0) { Tag = Enchanted() })]) });
        s.Send(new AddObjectVehiclePacket { EntityId = 403, Type = 71, X = F(11.5), Y = F(65), Z = F(12.03), ThrowerEntityId = 2 });
        s.Send(new EntityMetadataPacket { EntityId = 403, Metadata = new EntityMetadata([MetadataEntry.FromItem(EntityMeta.Item, new ItemStack(Items.Diamond, 1, 0))]) });
        Assert.True(await Wait(() => c.World.Entities.Get(402) is { FramedItem.IsEmpty: false } && c.World.Entities.Get(403) is { FramedItem.IsEmpty: false }));
        var list = Wire(c);
        Assert.True(ById(list, 402).GetProperty("glint").GetBoolean());
        Assert.False(ById(list, 403).TryGetProperty("glint", out _));
    }

    [Fact]
    public void Dropped_items_glint_from_their_nbt_and_potions_by_their_effects()
    {
        var now = DateTime.UtcNow;
        JsonElement W(ItemStack st) => JsonDocument.Parse(SupervisorJson.Bytes(HiveEntityMerge.ToWire(new Entity(7, EntityKind.Item, new Vec3d(0, 64, 0), Item: st), 0, "maid_chan", now)))
            .RootElement;
        Assert.True(W(new ItemStack(Items.DiamondPickaxe, 1, 0) { Tag = Enchanted() }).GetProperty("glint").GetBoolean());
        Assert.False(W(new ItemStack(Items.DiamondPickaxe, 1, 0)).TryGetProperty("glint", out _));
        // ItemPotion.hasEffect: a potion type with effects glints, water does not; a tipped arrow's potion never does.
        ItemTag Potion(string type) => Codec1122.TagOf(new NbtCompound { ["Potion"] = "minecraft:" + type });
        Assert.True(W(new ItemStack(373, 1, 0) { Tag = Potion("swiftness") }).GetProperty("glint").GetBoolean());
        Assert.False(W(new ItemStack(373, 1, 0) { Tag = Potion("water") }).TryGetProperty("glint", out _));
        Assert.False(W(new ItemStack(440, 1, 0) { Tag = Potion("swiftness") }).TryGetProperty("glint", out _));
        // An enchanted book's stored enchantments.
        var book = Codec1122.TagOf(new NbtCompound { ["StoredEnchantments"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)70, ["lvl"] = (short)1 }]) });
        Assert.True(book.Enchanted);
    }

    [Fact]
    public void Wire_items_round_trip_their_colour_and_glint()
    {
        var o = SupervisorJson.Options;
        foreach (var item in new[] { new WireItem(1, 2), new WireItem(1, 2, 0x112233), new WireItem(1, 2, null, true), new WireItem(1, 2, 0x112233, true) })
            Assert.Equal(item, JsonSerializer.Deserialize<WireItem>(JsonSerializer.Serialize(item, o), o));
        Assert.Equal("[1,2,{\"glint\":true}]", JsonSerializer.Serialize(new WireItem(1, 2, null, true), o));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_enderman_carries_its_block(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var cc = c;
        using var ss = s;
        // 1.12.2 CARRIED_BLOCK (state id = id | meta << 12: red sand), b1.8.1 metadata 16 (id) and 17 (data).
        var md = modern
            ? new EntityMetadata([MetadataEntry.FromInt(EntityMeta.CarriedBlock, Blocks.Sand | 1 << 12)])
            : new EntityMetadata([MetadataEntry.FromByte(EntityMeta.Mob16, (sbyte)Blocks.Grass), MetadataEntry.FromByte(EntityMeta.Mob17, 0)]);
        s.Send(new MobSpawnPacket { EntityId = 501, Type = 58, X = F(10.5), Y = F(64), Z = F(10.5), Metadata = md });
        s.Send(new MobSpawnPacket { EntityId = 502, Type = 58, X = F(12.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata() });
        Assert.True(await Wait(() => c.World.Entities.Get(501) is { Metadata: not null } && c.World.Entities.Get(502) != null));
        var list = Wire(c);
        Assert.Equal(modern ? $"[{Blocks.Sand},1]" : $"[{Blocks.Grass},0]", ById(list, 501).GetProperty("carried").GetRawText());
        Assert.False(ById(list, 502).TryGetProperty("carried", out _));

        if (!modern) return;
        // Putting it down: the optional state goes absent (0).
        s.Send(new EntityMetadataPacket { EntityId = 501, Metadata = new EntityMetadata([MetadataEntry.FromInt(EntityMeta.CarriedBlock, 0)]) });
        Assert.True(await Wait(() => c.World.Entities.Get(501)?.MetaInt(EntityMeta.CarriedBlock) == 0));
        Assert.False(ById(Wire(c), 501).TryGetProperty("carried", out _));
    }

    [Fact]
    public async Task An_iron_golem_offers_a_flower_until_status_34()
    {
        var (c, s) = await Start(true);
        await using var cc = c;
        using var ss = s;
        s.Send(new MobSpawnPacket { EntityId = 601, Type = 99, X = F(10.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata() });
        Assert.True(await Wait(() => c.World.Entities.Get(601) != null));
        Assert.Null(Str(ById(Wire(c), 601), "look"));
        s.Send(new EntityStatusPacket { EntityId = 601, Status = 11 });
        Assert.True(await Wait(() => c.World.Entities.Get(601)!.RoseUntil > DateTime.UtcNow));
        Assert.Equal("holding_rose", Str(ById(Wire(c), 601), "look"));
        // 400 ticks later it is gone by itself; status 34 takes it back at once.
        var e = c.World.Entities.Get(601)!;
        Assert.Null(EntityLooks.Look(e, DateTime.UtcNow.AddSeconds(20.5)));
        s.Send(new EntityStatusPacket { EntityId = 601, Status = 34 });
        Assert.True(await Wait(() => c.World.Entities.Get(601)!.RoseUntil == DateTime.MinValue));
        Assert.Null(Str(ById(Wire(c), 601), "look"));
        // Status 4 (its attack) swings its arms on the event feed.
        s.Send(new EntityStatusPacket { EntityId = 601, Status = 4 });
        Assert.True(await Wait(() => EntityLooks.EventsOf(c.World, DateTime.UtcNow).Any(x => x.Id == 601 && x.Kind == "swing")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Players_carry_shoulder_parrots_and_stuck_arrows(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var cc = c;
        using var ss = s;
        s.Send(new NamedEntitySpawnPacket { EntityId = 701, PlayerName = "poofimafox", X = F(10.5), Y = F(64), Z = F(10.5) });
        s.Send(new MobSpawnPacket { EntityId = 702, Type = 54, X = F(12.5), Y = F(64), Z = F(10.5), Metadata = new EntityMetadata() });
        Assert.True(await Wait(() => c.World.Entities.Get(701) != null && c.World.Entities.Get(702) != null));
        if (modern)
        {
            s.Send(new EntityMetadataPacket { EntityId = 701, Metadata = new EntityMetadata([MetadataEntry.FromInt(EntityMeta.ShoulderLeft, 2),
                MetadataEntry.FromInt(EntityMeta.ShoulderRight, -1), MetadataEntry.FromInt(EntityMeta.ArrowsInEntity, 3)]) });
            s.Send(new EntityMetadataPacket { EntityId = 702, Metadata = new EntityMetadata([MetadataEntry.FromInt(EntityMeta.ArrowsInEntity, 1)]) });
            Assert.True(await Wait(() => c.World.Entities.Get(701)?.MetaInt(EntityMeta.ArrowsInEntity) == 3 && c.World.Entities.Get(702)?.MetaInt(EntityMeta.ArrowsInEntity) == 1));
        }
        var list = Wire(c);
        var fox = ById(list, 701);
        if (!modern)
        {
            // b1.8.1 has neither parrots nor an arrow count.
            Assert.False(fox.TryGetProperty("shoulders", out _));
            Assert.False(fox.TryGetProperty("arrows", out _));
            return;
        }
        Assert.Equal("{\"left\":\"green\"}", fox.GetProperty("shoulders").GetRawText());
        Assert.Equal(3, fox.GetProperty("arrows").GetInt32());
        Assert.Equal(1, ById(list, 702).GetProperty("arrows").GetInt32());
        // The parrot flies off: an empty compound.
        s.Send(new EntityMetadataPacket { EntityId = 701, Metadata = new EntityMetadata([MetadataEntry.FromInt(EntityMeta.ShoulderLeft, -1)]) });
        Assert.True(await Wait(() => c.World.Entities.Get(701)?.MetaInt(EntityMeta.ShoulderLeft) == -1));
        Assert.False(ById(Wire(c), 701).TryGetProperty("shoulders", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Styled_sign_text_comes_as_rich_runs(bool modern)
    {
        var (c, s) = await Start(modern, srv => srv.Blocks[(10, 66, 9)] = Blocks.SignPost);
        await using var cc = c;
        using var ss = s;
        string[] json =
        [
            """{"text":"Fox","color":"red","bold":true,"extra":[{"text":"ie","color":"gold","bold":false}]}""",
            """{"text":"","extra":["house ",{"text":"!","italic":true,"underlined":true}]}""",
            """{"text":"§kab§r cd"}""",
            """{"text":"keep out","strikethrough":true,"color":"dark_blue"}""",
        ];
        s.Send(new UpdateSignPacket { X = 10, Y = 66, Z = 9, Lines = ["Foxie", "house !", "ab cd", "keep out"], Json = modern ? json : null });
        Assert.True(await Wait(() => c.World.BlockEntities.Get(new BlockPos(10, 66, 9)) != null));
        var col = BlockEntityColumns.Build(0, 0, [c.World]);
        var sign = JsonDocument.Parse(SupervisorJson.Bytes(col)).RootElement.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("x").GetInt32() == 10);
        Assert.Equal(["Foxie", "house !", "ab cd", "keep out"], sign.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        if (!modern)
        {
            Assert.False(sign.TryGetProperty("rich", out _)); // b1.8.1 signs are plain strings
            return;
        }
        var rich = sign.GetProperty("rich");
        Assert.Equal(4, rich.GetArrayLength());
        Assert.Equal("""[{"text":"Fox","color":"red","bold":true},{"text":"ie","color":"gold"}]""", rich[0].GetRawText());
        Assert.Equal("""[{"text":"house "},{"text":"!","italic":true,"underlined":true}]""", rich[1].GetRawText());
        Assert.Equal("""[{"text":"ab","obfuscated":true},{"text":" cd"}]""", rich[2].GetRawText());
        Assert.Equal("""[{"text":"keep out","color":"dark_blue","strikethrough":true}]""", rich[3].GetRawText());

        // A plain sign carries no rich text.
        s.Send(new UpdateSignPacket { X = 10, Y = 66, Z = 9, Lines = ["plain", "", "", ""] });
        Assert.True(await Wait(() => c.World.BlockEntities.Get(new BlockPos(10, 66, 9))?.Lines?[0] == "plain"));
        var plain = JsonDocument.Parse(SupervisorJson.Bytes(BlockEntityColumns.Build(0, 0, [c.World]))).RootElement.GetProperty("entities")
            .EnumerateArray().Single(e => e.GetProperty("x").GetInt32() == 10);
        Assert.False(plain.TryGetProperty("rich", out _));
    }

    [Fact]
    public void Legacy_codes_read_like_the_font_renderer()
    {
        // A colour resets the styles; an unknown code is white; §r goes back to the default.
        var runs = SignText.Runs("§lA§cB§zC§rD§o§nE");
        Assert.Equal([new SignRun("A", null, true), new SignRun("B", "red"), new SignRun("C", "white"), new SignRun("D"), new SignRun("E", null, null, true, true)], runs);
        Assert.Empty(SignText.Runs(""));
        Assert.Null(SignText.Rich(["§rplain", "", "", ""]));
    }
}
