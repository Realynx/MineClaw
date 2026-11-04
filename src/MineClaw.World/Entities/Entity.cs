using MineClaw.Data;

namespace MineClaw.World;

public enum EntityKind
{
    Player,
    Mob,
    Item,
    /// <summary>Objects/vehicles from 0x17 (boats, minecarts, arrows, primed TNT, falling sand...).</summary>
    Object,
    XpOrb,
    Painting,
    /// <summary>Lightning bolt (0x47 thunderbolt).</summary>
    Weather,
}

/// <summary>
/// Immutable snapshot of a tracked entity. The tracker replaces the record on every update, so instances handed
/// to other threads never change underneath them.
/// </summary>
/// <param name="TypeId">Mob type (0x18) or object type (0x17); -1 otherwise.</param>
/// <param name="Name">Player name (0x14) or painting title.</param>
/// <param name="Item">Dropped item stack (0x15 pickup spawn).</param>
/// <param name="Count">Experience orb amount (0x1A).</param>
/// <param name="Metadata">Raw entity metadata values by index (0x18/0x28), as decoded by the Client.</param>
public sealed record Entity(
    int Id,
    EntityKind Kind,
    Vec3d Position,
    Vec3d Velocity = default,
    float Yaw = 0,
    float Pitch = 0,
    int TypeId = -1,
    string? Name = null,
    ItemStack Item = default,
    int Count = 0,
    int HeldItem = 0,
    int VehicleId = -1,
    IReadOnlyDictionary<int, object>? Metadata = null,
    IReadOnlyList<ItemStack>? Equipment = null)
{
    /// <summary>The version's facts for a mob (stamped by the tracker from its <see cref="Versioning.IEntityRules"/>); null =
    /// the b1.8.1 tables.</summary>
    public MobProfile? Profile { get; init; }
    /// <summary>The version's name of a mob or object type; null = <see cref="MobTypes.Name"/> / <see cref="ObjectTypes.Name"/>.</summary>
    public string? TypeName { get; init; }
    /// <summary>Head yaw (1.12.2 Entity Head Look); NaN = never sent (the body yaw).</summary>
    public float HeadYaw { get; init; } = float.NaN;
    /// <summary>Potion effects by id (1.12.2 Entity Effect); null = none seen.</summary>
    public IReadOnlyDictionary<int, StatusEffect>? Effects { get; init; }
    /// <summary>The spawn's data word: an object's (b1.8.1 0x17 thrower id; 1.12.2 Spawn Object data: a falling block's state
    /// id | meta &lt;&lt; 12, an item frame's facing, a minecart's type, an arrow's shooter + 1), a painting's direction (b1.8.1
    /// 0 −Z, 1 −X, 2 +Z, 3 +X). 0 = none. The viewer's looks only.</summary>
    public int Data { get; init; }
    /// <summary>An iron golem offers a poppy until then (1.12.2 Entity Status 11 sets <c>holdRoseTick</c> to 400 ticks, 34 clears
    /// it); <see cref="DateTime.MinValue"/> = not holding one. The viewer's looks only.</summary>
    public DateTime RoseUntil { get; init; }
    /// <summary>Synced attribute values by key ("generic.movementSpeed"…; 1.12.2 Entity Properties); null = none seen.</summary>
    public IReadOnlyDictionary<string, double>? Attributes { get; init; }

    /// <summary>An arrow (object 60; 1.12.2 also the spectral arrow 91).</summary>
    public bool IsArrow => Kind == EntityKind.Object && (TypeId == 60 || (TypeId == 91 && TypeName == "spectral_arrow"));
    /// <summary>An arrow that stopped in a block (the tracker: its critical flag cleared, as 1.12.2 <c>EntityArrow.onHit</c> does
    /// on a block, or a move/teleport that left it where it was): no longer a projectile, motion zero.</summary>
    public bool InGround { get; init; }

    /// <summary>Metadata value at <paramref name="index"/> as a float (1.12.2 health, absorption), or null.</summary>
    public float? MetaFloat(int index) =>
        Metadata is not null && Metadata.TryGetValue(index, out var v) && v is float f ? f : null;

    /// <summary>Metadata flag (sbyte 0/1 and friends) at <paramref name="index"/>.</summary>
    public bool MetaFlag(int index) => (MetaInt(index) ?? 0) != 0;

    /// <summary>Health from 1.12.2 metadata (<see cref="EntityMeta.Health"/>); null when the version doesn't send it.</summary>
    public float? Health => MetaFloat(EntityMeta.Health);
    /// <summary>A baby (zombie, husk, animals): half-size hitbox.</summary>
    public bool IsBaby => MetaFlag(EntityMeta.Baby);
    /// <summary>Using an item: eating, drawing a bow, raising a shield (canonical flags 0x10, from 1.12.2's hand states).</summary>
    public bool IsUsingItem => (Flags & EntityMeta.FlagUsingItem) != 0;
    /// <summary>The off-hand item (1.12.2 equipment slot 5); empty when never sent.</summary>
    public ItemStack Offhand => EquipmentAt(5);
    /// <summary>The item an item frame shows (1.12.2 metadata).</summary>
    public ItemStack FramedItem => Metadata is not null && Metadata.TryGetValue(EntityMeta.Item, out var v) && v is ItemStack s ? s : ItemStack.Empty;

    /// <summary>The effect's amplifier if active at <paramref name="now"/> (default: now), else −1.</summary>
    public int EffectAmplifier(int effectId, DateTime? now = null) =>
        Effects is not null && Effects.TryGetValue(effectId, out var fx) && fx.Active(now ?? DateTime.UtcNow) ? fx.Amplifier : -1;

    /// <summary>Metadata value at <paramref name="index"/> widened to int (byte/short/int), or null.</summary>
    public int? MetaInt(int index) =>
        Metadata is not null && Metadata.TryGetValue(index, out var v) && v is sbyte or byte or short or int ? Convert.ToInt32(v) : null;

    /// <summary>0x05 equipment slot (0 held, 1 boots, 2 leggings, 3 chestplate, 4 helmet; 1.12.2: 5 off hand); empty when never sent.</summary>
    public ItemStack EquipmentAt(int slot) =>
        Equipment is not null && slot >= 0 && slot < Equipment.Count ? Equipment[slot] : ItemStack.Empty;

    public DateTime SpawnedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Entity flags from metadata index 0 (bit 0 on fire, bit 1 crouched, bit 2 riding, bit 3 sprinting, bit 4 eating).</summary>
    public int Flags => Metadata is not null && Metadata.TryGetValue(0, out var v) && v is IConvertible c ? c.ToInt32(null) : 0;
    public bool IsCrouched => (Flags & 2) != 0;
    /// <summary>On fire (Entity.java data bit 0): drawn burning; undead in daylight die soon.</summary>
    public bool IsBurning => (Flags & 1) != 0;
    /// <summary>Sprint flag (Entity.at() = data bit 3, Entity.java:1001-1007). A sprinting player's hit adds 1.0 b/t knockback.</summary>
    public bool IsSprinting => (Flags & 8) != 0;

    public bool IsHostileMob => Kind == EntityKind.Mob && (Profile?.Hostile ?? MobTypes.IsHostile(TypeId));

    public string DisplayName => Kind switch
    {
        EntityKind.Player => Name ?? "player",
        EntityKind.Mob => TypeName ?? MobTypes.Name(TypeId),
        EntityKind.Item => $"item {Item}",
        EntityKind.Object => TypeName ?? ObjectTypes.Name(TypeId),
        EntityKind.XpOrb => $"xp orb ({Count})",
        EntityKind.Painting => $"painting {Name}",
        _ => Kind.ToString(),
    };
}

/// <summary>b1.8.1 mob type ids (0x18 Mob Spawn).</summary>
public static class MobTypes
{
    public const int Creeper = 50, Skeleton = 51, Spider = 52, Giant = 53, Zombie = 54, Slime = 55, Ghast = 56,
        PigZombie = 57, Enderman = 58, CaveSpider = 59, Silverfish = 60,
        Pig = 90, Sheep = 91, Cow = 92, Chicken = 93, Squid = 94, Wolf = 95;

    /// <summary>1.12.2 types (Spawn Mob; entities.json). b1.8.1 never sends them; their facts come from the 1.12.2 entity rules.</summary>
    public const int ElderGuardian = 4, WitherSkeleton = 5, Stray = 6, Husk = 23, ZombieVillager = 27, SkeletonHorse = 28, ZombieHorse = 29,
        Donkey = 31, Mule = 32, Evoker = 34, Vex = 35, Vindicator = 36, Illusioner = 37, Blaze = 61, MagmaCube = 62, EnderDragon = 63,
        Wither = 64, Bat = 65, Witch = 66, Endermite = 67, Guardian = 68, Shulker = 69, Mooshroom = 96, SnowGolem = 97, Ocelot = 98,
        IronGolem = 99, Horse = 100, Rabbit = 101, PolarBear = 102, Llama = 103, Parrot = 105, Villager = 120;

    public static bool IsHostile(int t) => t is Creeper or Skeleton or Spider or Giant or Zombie or Slime or Ghast
        or Enderman or CaveSpider or Silverfish;

    public static string Name(int t) => BetaNames.TryGetValue(t, out var name) ? name
        : Versioning.Release1122EntityRules.Known(t) is { Name: { } mobName } ? mobName : $"mob_{t}"; // 1.12.2 types (b1.8.1 never sends them)

    /// <summary>The b1.8.1 mobs, in type order.</summary>
    private static readonly Dictionary<int, string> BetaNames = new()
    {
        [Creeper] = "creeper", [Skeleton] = "skeleton", [Spider] = "spider", [Giant] = "giant", [Zombie] = "zombie",
        [Slime] = "slime", [Ghast] = "ghast", [PigZombie] = "zombie_pigman", [Enderman] = "enderman",
        [CaveSpider] = "cave_spider", [Silverfish] = "silverfish", [Pig] = "pig", [Sheep] = "sheep", [Cow] = "cow",
        [Chicken] = "chicken", [Squid] = "squid", [Wolf] = "wolf",
    };

    /// <summary>The everyday names of 1.12.2 registry names ("vindicator" for vindication_illager).</summary>
    private static readonly Dictionary<string, string> RegistryAliases = new(StringComparer.Ordinal)
    {
        ["vindicator"] = "vindication_illager", ["evoker"] = "evocation_illager", ["illusioner"] = "illusion_illager",
        ["iron_golem"] = "villager_golem", ["golem"] = "villager_golem", ["snow_golem"] = "snowman", ["magma"] = "magma_cube",
    };

    public static int? ByName(string name)
    {
        foreach (var t in BetaNames.Keys)
            if (string.Equals(Name(t), name, StringComparison.OrdinalIgnoreCase)) return t;
        // 1.12.2 names, and the everyday ones of its registry names.
        string lower = name.ToLowerInvariant();
        string n = RegistryAliases.TryGetValue(lower, out var registry) ? registry : lower;
        foreach (var p in Versioning.Release1122EntityRules.All)
            if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) return p.Type;
        return null;
    }
}

/// <summary>b1.8.1 object/vehicle type ids (0x17 Add Object/Vehicle).</summary>
public static class ObjectTypes
{
    public static string Name(int t) => Names.TryGetValue(t, out var name) ? name : $"object_{t}";

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "boat", [10] = "minecart", [11] = "storage_minecart", [12] = "powered_minecart", [50] = "primed_tnt",
        [60] = "arrow", [61] = "snowball", [62] = "egg", [63] = "fireball", [70] = "falling_sand", [71] = "falling_gravel",
        [90] = "fishing_float",
    };
}
