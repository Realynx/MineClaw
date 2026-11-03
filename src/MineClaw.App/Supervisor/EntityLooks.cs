using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>An item on the wire as <c>[id, meta]</c> (equipment, a falling block), or <c>[id, meta, rgb]</c> when the item is
/// dyed (1.12.2 leather armour's display.color; b1.8.1 has no dyed items, so never there).</summary>
[JsonConverter(typeof(WireItemConverter))]
public readonly record struct WireItem(int Id, int Meta, int? Color = null);

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
public sealed record WorldEntityEvent(int Id, string Kind, long At, int Dim, double? X = null, double? Y = null, double? Z = null);

internal sealed class WireItemConverter : JsonConverter<WireItem>
{
    public override WireItem Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var a = JsonSerializer.Deserialize<int[]>(ref r, o) ?? [];
        return new WireItem(a.Length > 0 ? a[0] : 0, a.Length > 1 ? a[1] : 0, a.Length > 2 ? a[2] : null);
    }

    public override void Write(Utf8JsonWriter w, WireItem v, JsonSerializerOptions o)
    {
        w.WriteStartArray(); w.WriteNumberValue(v.Id); w.WriteNumberValue(v.Meta);
        if (v.Color is { } c) w.WriteNumberValue(c);
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
            yield return new WorldEntityEvent(e.EntityId, e.Kind, new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), dim,
                e.Position is { } p ? Math.Round(p.X, 2) : null, e.Position is { } q ? Math.Round(q.Y, 2) : null, e.Position is { } r ? Math.Round(r.Z, 2) : null);
    }

    /// <summary>The mob's or object's name (the version's, else the b1.8.1 tables).</summary>
    public static string NameOf(Entity e) => e.Kind == EntityKind.Mob ? e.TypeName ?? MobTypes.Name(e.TypeId) : e.TypeName ?? ObjectTypes.Name(e.TypeId);

    /// <summary>The wire kind of an object: painting, item_frame, armor_stand, arrow, boat, minecart, falling_block, tnt, xp_orb,
    /// ender_crystal, firework, or "object" (its <c>name</c> says which).</summary>
    public static string ObjectKind(Entity e) => e.Kind switch
    {
        EntityKind.Painting => "painting",
        EntityKind.XpOrb => "xp_orb",
        _ => NameOf(e) switch
        {
            "item_frame" => "item_frame", "armor_stand" => "armor_stand", "arrow" or "spectral_arrow" => "arrow", "boat" => "boat",
            "minecart" or "storage_minecart" or "powered_minecart" => "minecart", "falling_block" or "falling_sand" or "falling_gravel" => "falling_block",
            "primed_tnt" => "tnt", "ender_crystal" => "ender_crystal", "firework" => "firework", _ => "object",
        },
    };

    /// <summary>Equipment in wire slots (canonical 0 held, 1 feet, 2 legs, 3 chest, 4 head, 5 off hand); null when nothing is worn.
    /// A dyed item (1.12.2 display.color) carries its colour as the third element.</summary>
    public static WorldEquipment? Equipment(Entity e)
    {
        if (e.Equipment is null) return null;
        WireItem? S(int slot) => e.EquipmentAt(slot) is { IsEmpty: false } st ? new WireItem(st.Id, st.Damage, st.Tag?.DisplayColor) : null;
        var eq = new WorldEquipment(S(0), S(5), S(4), S(3), S(2), S(1));
        return eq == new WorldEquipment() ? null : eq;
    }

    /// <summary>The number <c>variant</c>: b1.8.1's metadata 16 where it is set (sheep fleece, slime size, wolf flags… as before),
    /// else the kind's main number (horse colour+markings, llama colour, rabbit coat, profession, ocelot/parrot type, boat wood,
    /// minecart type); null = none.</summary>
    public static int? Variant(Entity e)
    {
        if (e.MetaInt(EntityMeta.Mob16) is { } m16) return m16;
        if (e.Kind == EntityKind.Mob)
            return e.MetaInt(EntityMeta.HorseVariant) ?? e.MetaInt(EntityMeta.LlamaVariant) ?? e.MetaInt(EntityMeta.RabbitType)
                ?? e.MetaInt(EntityMeta.Profession) ?? e.MetaInt(EntityMeta.OcelotType) ?? e.MetaInt(EntityMeta.ParrotVariant);
        return NameOf(e) switch
        {
            "boat" => e.MetaInt(EntityMeta.BoatType),
            "minecart" when e.TypeName != null => e.Data is > 0 and < 7 ? e.Data : null, // 1.12.2: the spawn data is the cart type
            _ => null,
        };
    }

    /// <summary>The compact <c>look</c> string per kind (comma-separated tokens: "red,sheared", "chestnut,white_dots,saddle,armor=iron",
    /// "tame,collar=red", "profession=librarian", "size=4", "powered"); null when there's nothing to say.</summary>
    public static string? Look(Entity e)
    {
        var t = new List<string>();
        int? m16 = e.MetaInt(EntityMeta.Mob16);
        void Tame(int? flags)
        {
            if (flags is not { } f) return;
            if ((f & 4) != 0) t.Add("tame");
            if ((f & 1) != 0) t.Add("sitting");
            if ((f & 2) != 0) t.Add("angry");
        }
        void Horse()
        {
            if (e.MetaInt(EntityMeta.HorseStatus) is { } s)
            {
                if ((s & 2) != 0) t.Add("tame");
                if ((s & 4) != 0) t.Add("saddle");
            }
            if (e.MetaFlag(EntityMeta.HasChest)) t.Add("chest");
        }
        switch (NameOf(e))
        {
            case "sheep" when m16 is { } w:
                t.Add(Dyes[w & 15]);
                if ((w & 0x10) != 0) t.Add("sheared");
                break;
            case "wolf":
                Tame(m16);
                // COLLAR_COLOR is a dye damage (0 black..15 white), the reverse of the wool order of Dyes.
                if ((m16 & 4) != 0 && e.MetaInt(EntityMeta.CollarColor) is { } collar && At(Dyes, 15 - (collar & 15)) is { } cn) t.Add("collar=" + cn);
                break;
            case "pig" when m16 == 1:
                t.Add("saddle");
                break;
            case "creeper":
                if (e.MetaFlag(EntityMeta.Mob17)) t.Add("powered");
                if (m16 == 1 || e.MetaFlag(EntityMeta.CreeperIgnited)) t.Add("fusing");
                break;
            case "slime" or "magma_cube" or "lava_slime" when m16 is { } size:
                t.Add("size=" + size);
                break;
            case "ghast" when m16 == 1:
            case "blaze" when m16 == 1:
                t.Add(NameOf(e) == "ghast" ? "attacking" : "burning");
                break;
            case "enderman" when e.MetaFlag(EntityMeta.Screaming):
                t.Add("screaming");
                break;
            case "horse":
                if (e.MetaInt(EntityMeta.HorseVariant) is { } hv)
                {
                    if (At(HorseColors, hv & 0xFF) is { } c) t.Add(c);
                    if (At(HorseMarkings, (hv >> 8) & 0xFF) is { } mk) t.Add(mk);
                }
                Horse();
                if (At(HorseArmors, e.MetaInt(EntityMeta.HorseArmor)) is { } armor) t.Add("armor=" + armor);
                break;
            case "donkey" or "mule" or "skeleton_horse" or "zombie_horse":
                Horse();
                break;
            case "llama":
                if (At(LlamaColors, e.MetaInt(EntityMeta.LlamaVariant)) is { } lc) t.Add(lc);
                Horse();
                if (At(Dyes, e.MetaInt(EntityMeta.CarpetColor)) is { } carpet) t.Add("carpet=" + carpet);
                break;
            case "villager" or "zombie_villager":
                if (At(Professions, e.MetaInt(EntityMeta.Profession)) is { } prof) t.Add("profession=" + prof);
                if (e.MetaFlag(EntityMeta.Converting)) t.Add("converting");
                break;
            case "ozelot" or "ocelot":
                if (At(OcelotTypes, e.MetaInt(EntityMeta.OcelotType)) is { } ot) t.Add(ot);
                Tame(m16);
                break;
            case "rabbit":
                if (e.MetaInt(EntityMeta.RabbitType) is 99) t.Add("caerbannog");
                else if (At(RabbitTypes, e.MetaInt(EntityMeta.RabbitType)) is { } rt) t.Add(rt);
                break;
            case "parrot":
                if (At(ParrotColors, e.MetaInt(EntityMeta.ParrotVariant)) is { } pc) t.Add(pc);
                Tame(m16);
                break;
            case "shulker" when At(Dyes, e.MetaInt(EntityMeta.ShulkerColor)) is { } sc:
                t.Add(sc);
                break;
            case "boat" when At(BoatWoods, e.MetaInt(EntityMeta.BoatType)) is { } wood:
                t.Add(wood);
                break;
            case "storage_minecart":
                t.Add("chest");
                break;
            case "powered_minecart":
                t.Add("furnace");
                break;
            case "minecart" when e.TypeName != null && At(CartTypes, e.Data) is { } cart:
                t.Add(cart);
                break;
            case "armor_stand" when e.MetaInt(EntityMeta.StandFlags) is { } sf:
                if ((sf & 0x01) != 0) t.Add("small");
                if ((sf & 0x04) != 0) t.Add("arms");
                if ((sf & 0x08) != 0) t.Add("no_base");
                if ((sf & 0x10) != 0) t.Add("marker");
                break;
        }
        return t.Count == 0 ? null : string.Join(",", t);
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
