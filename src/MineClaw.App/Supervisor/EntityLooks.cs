using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>An item on the wire as <c>[id, meta]</c> (equipment, a falling block, an enderman's block), <c>[id, meta, rgb]</c> when
/// the item is dyed (1.12.2 leather armour's display.color; b1.8.1 has no dyed items, so never there), and a trailing flags
/// object when it glints: <c>[id, meta, {"glint": true}]</c> / <c>[id, meta, rgb, {"glint": true}]</c> (1.12.2 enchanted items and
/// potions with effects; b1.8.1 items carry no NBT). Readers that take the numbers ignore the object.</summary>
[JsonConverter(typeof(WireItemConverter))]
public readonly record struct WireItem(int Id, int Meta, int? Color = null, bool Glint = false);

/// <summary>The parrots on a player's shoulders (1.12.2), by colour name (<c>red_blue</c>, <c>blue</c>, <c>green</c>, <c>yellow_blue</c>,
/// <c>grey</c>); a free shoulder is left out.</summary>
public sealed record WorldShoulders(string? Left = null, string? Right = null);

/// <summary>Three ints on the wire as <c>[x, y, z]</c> (an armour stand's pose angles in whole degrees).</summary>
[JsonConverter(typeof(WireVecConverter))]
public readonly record struct WireVec(int X, int Y, int Z);

/// <summary>What an entity wears and holds; a slot that is empty is left out (null).</summary>
public sealed record WorldEquipment(WireItem? Main = null, WireItem? Off = null, WireItem? Head = null, WireItem? Chest = null, WireItem? Legs = null,
    WireItem? Feet = null);

/// <summary>An armour stand's pose (whole degrees per part; null = never sent, the default pose).</summary>
public sealed record WorldPose(WireVec? Head = null, WireVec? Body = null, WireVec? LeftArm = null, WireVec? RightArm = null, WireVec? LeftLeg = null,
    WireVec? RightLeg = null);

/// <summary>A one-shot entity event on the entity delta feed (<c>events</c>): kind swing | swing_off | hurt | death | crit |
/// magic_crit | eat | teleport | lightning (with its position); <c>at</c> unix ms.</summary>
public sealed record WorldEntityEvent(int Id, string Kind, long At, int Dim, double? X = null, double? Y = null, double? Z = null,
    string? Sound = null, float? Volume = null, float? Pitch = null);

internal sealed class WireItemConverter : JsonConverter<WireItem>
{
    public override WireItem Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        using var doc = JsonDocument.ParseValue(ref r);
        var ints = new List<int>();
        bool glint = false;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number) ints.Add(e.GetInt32());
                else if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("glint", out var g)) glint = g.ValueKind == JsonValueKind.True;
        return new WireItem(ints.Count > 0 ? ints[0] : 0, ints.Count > 1 ? ints[1] : 0, ints.Count > 2 ? ints[2] : null, glint);
    }

    public override void Write(Utf8JsonWriter w, WireItem v, JsonSerializerOptions o)
    {
        w.WriteStartArray(); w.WriteNumberValue(v.Id); w.WriteNumberValue(v.Meta);
        if (v.Color is { } c) w.WriteNumberValue(c);
        if (v.Glint) { w.WriteStartObject(); w.WriteBoolean("glint", true); w.WriteEndObject(); }
        w.WriteEndArray();
    }
}

internal sealed class WireVecConverter : JsonConverter<WireVec>
{
    public override WireVec Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var a = JsonSerializer.Deserialize<int[]>(ref r, o) ?? [];
        return new WireVec(a.Length > 0 ? a[0] : 0, a.Length > 1 ? a[1] : 0, a.Length > 2 ? a[2] : 0);
    }

    public override void Write(Utf8JsonWriter w, WireVec v, JsonSerializerOptions o)
    {
        w.WriteStartArray(); w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteNumberValue(v.Z); w.WriteEndArray();
    }
}

/// <summary>
/// The browser viewer's looks of an entity, from what the tracker already keeps (equipment, head look, canonical metadata, the
/// spawn's data word): its wire kind, the compact <c>look</c> string per kind, a number <c>variant</c>, equipment, an armour
/// stand's pose, a falling block's block, a hanging entity's facing. Both versions: b1.8.1's metadata 16/17 and spawn data,
/// 1.12.2's translated metadata (canonical 32+ and 50+). Pure.
/// </summary>
public static class EntityLooks
{
    public static readonly string[] Dyes =
        ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "silver", "cyan", "purple", "blue", "brown", "green", "red", "black"];
    private static readonly string[] HorseColors = ["white", "creamy", "chestnut", "brown", "black", "gray", "dark_brown"];
    private static readonly string[] HorseMarkings = ["", "white", "white_field", "white_dots", "black_dots"];
    private static readonly string[] HorseArmors = ["", "iron", "gold", "diamond"];
    private static readonly string[] LlamaColors = ["creamy", "white", "brown", "gray"];
    private static readonly string[] Professions = ["farmer", "librarian", "priest", "blacksmith", "butcher", "nitwit"];
    private static readonly string[] OcelotTypes = ["wild", "black", "red", "siamese"];
    // Names after the 1.12.2 renderers' textures (RenderRabbit, RenderParrot), so the viewer can pick the texture by name.
    private static readonly string[] RabbitTypes = ["brown", "white", "black", "white_splotched", "gold", "salt"];
    private static readonly string[] ParrotColors = ["red_blue", "blue", "green", "yellow_blue", "grey"];
    private static readonly string[] BoatWoods = ["oak", "spruce", "birch", "jungle", "acacia", "dark_oak"];
    private static readonly string[] CartTypes = ["", "chest", "furnace", "tnt", "spawner", "hopper", "command_block"];
    private static readonly string[] Facings = ["north", "west", "south", "east"]; // b1.8.1 painting direction 0 −Z, 1 −X, 2 +Z, 3 +X

    private static string? At(string[] names, int? i) => i is { } n && n >= 0 && n < names.Length && names[n].Length > 0 ? names[n] : null;

    /// <summary>A world's one-shot entity events of the last few seconds, on the wire (lightning with its position, 2 decimals).</summary>
    public static IEnumerable<WorldEntityEvent> EventsOf(WorldState w, DateTime nowUtc)
    {
        int dim = w.Dimension;
        foreach (var e in w.Entities.Events.Recent(nowUtc))
            yield return new WorldEntityEvent(e.EntityId, e.Kind, new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), e.Dimension ?? dim,
                e.Position is { } p ? Math.Round(p.X, 2) : null, e.Position is { } q ? Math.Round(q.Y, 2) : null, e.Position is { } r ? Math.Round(r.Z, 2) : null,
                e.Sound, e.Volume, e.Pitch);
    }

    /// <summary>The mob's or object's name (the version's, else the b1.8.1 tables).</summary>
    public static string NameOf(Entity e) => e.Kind == EntityKind.Mob ? e.TypeName ?? MobTypes.Name(e.TypeId) : e.TypeName ?? ObjectTypes.Name(e.TypeId);

    /// <summary>The wire kind of an object: painting, item_frame, armor_stand, arrow, boat, minecart, falling_block, tnt, xp_orb,
    /// ender_crystal, firework, or "object" (its <c>name</c> says which).</summary>
    public static string ObjectKind(Entity e) => e.Kind switch
    {
        EntityKind.Painting => "painting",
        EntityKind.XpOrb => "xp_orb",
        _ => ObjectKinds.GetValueOrDefault(NameOf(e), "object"),
    };

    private static readonly Dictionary<string, string> ObjectKinds = new(StringComparer.Ordinal)
    {
        ["item_frame"] = "item_frame", ["armor_stand"] = "armor_stand", ["arrow"] = "arrow", ["spectral_arrow"] = "arrow", ["boat"] = "boat",
        ["minecart"] = "minecart", ["storage_minecart"] = "minecart", ["powered_minecart"] = "minecart",
        ["falling_block"] = "falling_block", ["falling_sand"] = "falling_block", ["falling_gravel"] = "falling_block",
        ["primed_tnt"] = "tnt", ["ender_crystal"] = "ender_crystal", ["firework"] = "firework",
    };

    /// <summary>Equipment in wire slots (canonical 0 held, 1 feet, 2 legs, 3 chest, 4 head, 5 off hand); null when nothing is worn.
    /// A dyed item (1.12.2 display.color) carries its colour as the third element, a glinting one its flags object.</summary>
    public static WorldEquipment? Equipment(Entity e)
    {
        if (e.Equipment is null) return null;
        WireItem? S(int slot) => e.EquipmentAt(slot) is { IsEmpty: false } st ? new WireItem(st.Id, st.Damage, st.Tag?.DisplayColor, Glint(st)) : null;
        var eq = new WorldEquipment(S(0), S(5), S(4), S(3), S(2), S(1));
        return eq == new WorldEquipment() ? null : eq;
    }

    /// <summary>Whether the stack's NBT makes vanilla draw the enchantment glint on it (<c>ItemStack.hasEffect</c>): enchantments
    /// (an "ench" list, an enchanted book's stored ones) or a potion (373 potion, 438 splash, 441 lingering) with effects. Items
    /// that always glint whatever their NBT (enchanted book, nether star, bottle o' enchanting, written book, end crystal, the
    /// enchanted golden apple) the viewer knows by id. b1.8.1 stacks have no NBT: never.</summary>
    public static bool Glint(ItemStack s) =>
        !s.IsEmpty && s.Tag is { } t && (t.Enchanted || (t.PotionEffects && s.Id is 373 or 438 or 441));

    /// <summary>An enderman's carried block (<c>LayerHeldBlock</c>): 1.12.2's <c>CARRIED_BLOCK</c> state, b1.8.1's metadata 16 (id)
    /// and 17 (data); null when it carries nothing.</summary>
    public static WireItem? Carried(Entity e)
    {
        if (e.Kind != EntityKind.Mob || NameOf(e) != "enderman") return null;
        if (e.MetaInt(EntityMeta.CarriedBlock) is { } state)
            return (state & 0xFFF) > 0 ? new WireItem(state & 0xFFF, (state >> 12) & 15) : null;
        if (e.TypeName is null && e.MetaInt(EntityMeta.Mob16) is { } id && (id & 0xFF) > 0)
            return new WireItem(id & 0xFF, (e.MetaInt(EntityMeta.Mob17) ?? 0) & 15);
        return null;
    }

    /// <summary>The parrots on a player's shoulders (1.12.2 metadata); null when both are free.</summary>
    public static WorldShoulders? Shoulders(Entity e)
    {
        if (e.Kind != EntityKind.Player) return null;
        var s = new WorldShoulders(At(ParrotColors, e.MetaInt(EntityMeta.ShoulderLeft)), At(ParrotColors, e.MetaInt(EntityMeta.ShoulderRight)));
        return s == new WorldShoulders() ? null : s;
    }

    /// <summary>Arrows stuck in a living entity (1.12.2 <c>EntityLivingBase.ARROW_COUNT_IN_ENTITY</c>; b1.8.1 has none); null for 0.</summary>
    public static int? Arrows(Entity e) => e.Kind is EntityKind.Mob or EntityKind.Player && e.MetaInt(EntityMeta.ArrowsInEntity) is int n && n > 0 ? n : null;

    /// <summary>The number <c>variant</c>: b1.8.1's metadata 16 where it is set (sheep fleece, slime size, wolf flags… as before),
    /// else the kind's main number (horse colour+markings, llama colour, rabbit coat, profession, ocelot/parrot type, boat wood,
    /// minecart type); null = none.</summary>
    public static int? Variant(Entity e)
    {
        if (e.MetaInt(EntityMeta.Mob16) is { } m16) return m16;
        return e.Kind == EntityKind.Mob ? MobVariant(e) : ObjectVariant(e);
    }

    private static int? MobVariant(Entity e) =>
        e.MetaInt(EntityMeta.HorseVariant) ?? e.MetaInt(EntityMeta.LlamaVariant) ?? e.MetaInt(EntityMeta.RabbitType)
        ?? e.MetaInt(EntityMeta.Profession) ?? e.MetaInt(EntityMeta.OcelotType) ?? e.MetaInt(EntityMeta.ParrotVariant);

    private static int? ObjectVariant(Entity e) => NameOf(e) switch
    {
        "boat" => e.MetaInt(EntityMeta.BoatType),
        "minecart" when e.TypeName != null => e.Data is > 0 and < 7 ? e.Data : null, // 1.12.2: the spawn data is the cart type
        _ => null,
    };

    /// <summary>The compact <c>look</c> string per kind (comma-separated tokens: "red,sheared", "chestnut,white_dots,saddle,armor=iron",
    /// "tame,collar=red", "profession=librarian", "size=4", "powered"); null when there's nothing to say.</summary>
    public static string? Look(Entity e, DateTime? nowUtc = null)
    {
        var t = new List<string>();
        if (LookByName.TryGetValue(NameOf(e), out var add)) add(e, e.MetaInt(EntityMeta.Mob16), t, nowUtc);
        return t.Count == 0 ? null : string.Join(",", t);
    }

    /// <summary>Adds an entity's look tokens: the entity, its metadata 16 (b1.8.1 flags/sizes), the tokens, the time (golem roses).</summary>
    private delegate void LookTokens(Entity e, int? m16, List<string> t, DateTime? nowUtc);

    /// <summary>The look per kind name (the version's names and b1.8.1's alike); a kind not listed has none.</summary>
    private static readonly Dictionary<string, LookTokens> LookByName = BuildLooks();

    private static Dictionary<string, LookTokens> BuildLooks()
    {
        var d = new Dictionary<string, LookTokens>(StringComparer.Ordinal)
        {
            ["sheep"] = SheepLook,
            ["wolf"] = WolfLook,
            ["pig"] = (_, m16, t, _) => { if (m16 == 1) t.Add("saddle"); },
            ["creeper"] = CreeperLook,
            ["ghast"] = (_, m16, t, _) => { if (m16 == 1) t.Add("attacking"); },
            ["blaze"] = (_, m16, t, _) => { if (m16 == 1) t.Add("burning"); },
            ["enderman"] = (e, _, t, _) => { if (e.MetaFlag(EntityMeta.Screaming)) t.Add("screaming"); },
            ["horse"] = HorseLook,
            ["llama"] = LlamaLook,
            ["rabbit"] = RabbitLook,
            ["parrot"] = (e, m16, t, _) => { AddAt(t, ParrotColors, e.MetaInt(EntityMeta.ParrotVariant)); TameFlags(m16, t); },
            ["shulker"] = (e, _, t, _) => AddAt(t, Dyes, e.MetaInt(EntityMeta.ShulkerColor)),
            ["boat"] = (e, _, t, _) => AddAt(t, BoatWoods, e.MetaInt(EntityMeta.BoatType)),
            ["storage_minecart"] = (_, _, t, _) => t.Add("chest"),
            ["powered_minecart"] = (_, _, t, _) => t.Add("furnace"),
            ["minecart"] = (e, _, t, _) => { if (e.TypeName != null) AddAt(t, CartTypes, e.Data); },
            ["armor_stand"] = ArmorStandLook,
        };
        foreach (var slime in new[] { "slime", "magma_cube", "lava_slime" }) d[slime] = (_, m16, t, _) => { if (m16 is { } size) t.Add("size=" + size); };
        foreach (var golem in new[] { "villager_golem", "iron_golem" }) d[golem] = GolemLook;
        foreach (var horse in new[] { "donkey", "mule", "skeleton_horse", "zombie_horse" }) d[horse] = (e, _, t, _) => HorseGear(e, t);
        foreach (var villager in new[] { "villager", "zombie_villager" }) d[villager] = VillagerLook;
        foreach (var cat in new[] { "ozelot", "ocelot" }) d[cat] = (e, m16, t, _) => { AddAt(t, OcelotTypes, e.MetaInt(EntityMeta.OcelotType)); TameFlags(m16, t); };
        return d;
    }

    private static void AddAt(List<string> t, string[] table, int? i)
    {
        if (At(table, i) is { } s) t.Add(s);
    }

    /// <summary>Tameable flags (wolf, ocelot, parrot): 4 tame, 1 sitting, 2 angry.</summary>
    private static void TameFlags(int? flags, List<string> t)
    {
        if (flags is not { } f) return;
        if ((f & 4) != 0) t.Add("tame");
        if ((f & 1) != 0) t.Add("sitting");
        if ((f & 2) != 0) t.Add("angry");
    }

    /// <summary>A horse-like's status (2 tame, 4 saddle) and chest.</summary>
    private static void HorseGear(Entity e, List<string> t)
    {
        if (e.MetaInt(EntityMeta.HorseStatus) is { } s)
        {
            if ((s & 2) != 0) t.Add("tame");
            if ((s & 4) != 0) t.Add("saddle");
        }
        if (e.MetaFlag(EntityMeta.HasChest)) t.Add("chest");
    }

    private static void SheepLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        if (m16 is not { } w) return;
        t.Add(Dyes[w & 15]);
        if ((w & 0x10) != 0) t.Add("sheared");
    }

    private static void WolfLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        TameFlags(m16, t);
        // COLLAR_COLOR is a dye damage (0 black..15 white), the reverse of the wool order of Dyes.
        if ((m16 & 4) != 0 && e.MetaInt(EntityMeta.CollarColor) is { } collar && At(Dyes, 15 - (collar & 15)) is { } cn) t.Add("collar=" + cn);
    }

    private static void CreeperLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        if (e.MetaFlag(EntityMeta.Mob17)) t.Add("powered");
        if (m16 == 1 || e.MetaFlag(EntityMeta.CreeperIgnited)) t.Add("fusing");
    }

    private static void GolemLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        if (e.RoseUntil > (nowUtc ?? DateTime.UtcNow)) t.Add("holding_rose"); // EntityIronGolem.holdRoseTick (Entity Status 11, 400 ticks; 34 ends it)
    }

    private static void HorseLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        if (e.MetaInt(EntityMeta.HorseVariant) is { } hv)
        {
            AddAt(t, HorseColors, hv & 0xFF);
            AddAt(t, HorseMarkings, (hv >> 8) & 0xFF);
        }
        HorseGear(e, t);
        if (At(HorseArmors, e.MetaInt(EntityMeta.HorseArmor)) is { } armor) t.Add("armor=" + armor);
    }

    private static void LlamaLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        AddAt(t, LlamaColors, e.MetaInt(EntityMeta.LlamaVariant));
        HorseGear(e, t);
        if (At(Dyes, e.MetaInt(EntityMeta.CarpetColor)) is { } carpet) t.Add("carpet=" + carpet);
    }

    private static void VillagerLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        if (At(Professions, e.MetaInt(EntityMeta.Profession)) is { } prof) t.Add("profession=" + prof);
        if (e.MetaFlag(EntityMeta.Converting)) t.Add("converting");
    }

    private static void RabbitLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        if (e.MetaInt(EntityMeta.RabbitType) is 99) t.Add("caerbannog");
        else AddAt(t, RabbitTypes, e.MetaInt(EntityMeta.RabbitType));
    }

    private static void ArmorStandLook(Entity e, int? m16, List<string> t, DateTime? nowUtc)
    {
        if (e.MetaInt(EntityMeta.StandFlags) is not { } sf) return;
        if ((sf & 0x01) != 0) t.Add("small");
        if ((sf & 0x04) != 0) t.Add("arms");
        if ((sf & 0x08) != 0) t.Add("no_base");
        if ((sf & 0x10) != 0) t.Add("marker");
    }

    /// <summary>An armour stand's pose (null when no part was ever sent).</summary>
    public static WorldPose? Pose(Entity e)
    {
        WireVec? V(int i) => e.Metadata is not null && e.Metadata.TryGetValue(i, out var v) && v is MineClaw.Protocol.MetadataVector m ? new WireVec(m.X, m.Y, m.Z) : null;
        var p = new WorldPose(V(EntityMeta.StandHead), V(EntityMeta.StandBody), V(EntityMeta.StandLeftArm), V(EntityMeta.StandRightArm),
            V(EntityMeta.StandLeftLeg), V(EntityMeta.StandRightLeg));
        return p == new WorldPose() ? null : p;
    }

    /// <summary>A falling block's block: 1.12.2's spawn data (state id = id | meta &lt;&lt; 12), b1.8.1's falling sand/gravel types.</summary>
    public static WireItem? FallingBlock(Entity e) => NameOf(e) switch
    {
        "falling_block" when e.Data > 0 => new WireItem(e.Data & 0xFFF, (e.Data >> 12) & 15),
        "falling_sand" => new WireItem(Blocks.Sand, 0),
        "falling_gravel" => new WireItem(Blocks.Gravel, 0),
        _ => null,
    };

    /// <summary>Which way a painting (b1.8.1 direction) or an item frame (1.12.2 spawn data: horizontal facing 0 south, 1 west, 2
    /// north, 3 east) faces.</summary>
    public static string? Facing(Entity e) => e.Kind switch
    {
        EntityKind.Painting => At(Facings, e.Data),
        _ when NameOf(e) == "item_frame" => e.Data switch { 0 => "south", 1 => "west", 2 => "north", 3 => "east", _ => null },
        _ => null,
    };
}
