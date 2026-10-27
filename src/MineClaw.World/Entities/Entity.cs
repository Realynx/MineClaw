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
    /// <summary>Metadata value at <paramref name="index"/> widened to int (byte/short/int), or null.</summary>
    public int? MetaInt(int index) =>
        Metadata is not null && Metadata.TryGetValue(index, out var v) && v is sbyte or byte or short or int ? Convert.ToInt32(v) : null;

    /// <summary>0x05 equipment slot (0 held, 1 boots, 2 leggings, 3 chestplate, 4 helmet); empty when never sent.</summary>
    public ItemStack EquipmentAt(int slot) =>
        Equipment is not null && slot >= 0 && slot < Equipment.Count ? Equipment[slot] : ItemStack.Empty;

    public DateTime SpawnedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Entity flags from metadata index 0 (bit 0 on fire, bit 1 crouched, bit 2 riding, bit 3 sprinting, bit 4 eating).</summary>
    public int Flags => Metadata is not null && Metadata.TryGetValue(0, out var v) && v is IConvertible c ? c.ToInt32(null) : 0;
    public bool IsCrouched => (Flags & 2) != 0;

    public bool IsHostileMob => Kind == EntityKind.Mob && MobTypes.IsHostile(TypeId);

    public string DisplayName => Kind switch
    {
        EntityKind.Player => Name ?? "player",
        EntityKind.Mob => MobTypes.Name(TypeId),
        EntityKind.Item => $"item {Item}",
        EntityKind.Object => ObjectTypes.Name(TypeId),
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

    public static bool IsHostile(int t) => t is Creeper or Skeleton or Spider or Giant or Zombie or Slime or Ghast
        or Enderman or CaveSpider or Silverfish;

    public static string Name(int t) => t switch
    {
        Creeper => "creeper", Skeleton => "skeleton", Spider => "spider", Giant => "giant", Zombie => "zombie",
        Slime => "slime", Ghast => "ghast", PigZombie => "zombie_pigman", Enderman => "enderman",
        CaveSpider => "cave_spider", Silverfish => "silverfish", Pig => "pig", Sheep => "sheep", Cow => "cow",
        Chicken => "chicken", Squid => "squid", Wolf => "wolf", _ => $"mob_{t}",
    };

    public static int? ByName(string name)
    {
        foreach (var t in new[] { 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 90, 91, 92, 93, 94, 95 })
            if (string.Equals(Name(t), name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }
}

/// <summary>b1.8.1 object/vehicle type ids (0x17 Add Object/Vehicle).</summary>
public static class ObjectTypes
{
    public static string Name(int t) => t switch
    {
        1 => "boat", 10 => "minecart", 11 => "storage_minecart", 12 => "powered_minecart", 50 => "primed_tnt",
        60 => "arrow", 61 => "snowball", 62 => "egg", 63 => "fireball", 70 => "falling_sand", 71 => "falling_gravel",
        90 => "fishing_float", _ => $"object_{t}",
    };
}
