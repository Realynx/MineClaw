using MineClaw.Data;
using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

/// <summary>
/// Id → rule / name lookups golden (complexity refactor WP04), taken before the switch ladders became tables: item rules (stack
/// sizes, damageable, subtypes, tool/armor, armor slot, container item, equipment slot) under both versions' data, window slot kinds,
/// mob / object / potion / openable / container names, the name → mob lookup, and the stairs test, over every id.
/// </summary>
public class LookupGoldenTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lookups_match_the_golden(bool release1122) => Golden.Check($"lookups-{(release1122 ? "1122" : "b181")}",
        release1122 ? "3C477E4C97A2EE6DD8A3AE39EF41B25F2EB0F9639E89E6F09C8BCB25895B89E6" : "1DD4D461321F8B7049C773C0A24181186A5BECAE4A5F7ABEDF710467A7C24228", sb =>
    {
        using var _ = GoldenWorlds.Data(release1122);
        for (int id = -3; id < 4100; id++)
        {
            sb.Append(id).Append(':').Append(ItemRules.DefaultMaxStack(id)).Append(ItemRules.DefaultIsDamageable(id) ? 'D' : '-')
                .Append(ItemRules.DefaultHasSubtypes(id) ? 'S' : '-').Append(ItemRules.IsToolOrArmor(id) ? 'T' : '-').Append(ItemRules.ArmorType(id))
                .Append(ItemRules.ContainerItem(id)).Append(InventoryManager.EquipmentSlot(id)).Append(PlaceCursor.IsStairs(id) ? 'Z' : '-')
                .Append(PlaceCursor.IsSlab(id) ? 'L' : '-').Append(' ').Append(MobTypes.Name(id)).Append(' ').Append(ObjectTypes.Name(id)).Append(' ')
                .Append(Release1122EntityRules.Instance.ObjectName(id) ?? "-").Append(' ').Append(PotionEffects.Name(id)).Append(' ')
                .Append(Openables.Name(id)).Append(' ').Append(ContainerSightings.KindOf(id) ?? "-").Append('/').Append(ContainerSightings.KindOf(id, 54) ?? "-")
                .Append('\n');
        }
        foreach (var name in new[] { "creeper", "Zombie", "pig", "vindicator", "evoker", "illusioner", "iron_golem", "golem", "snow_golem", "magma",
                     "magma_cube", "villager", "wolf", "WOLF", "squid", "none", "", "zombie_pigman", "cave_spider", "parrot", "polar_bear" })
            sb.Append(name).Append('=').Append(MobTypes.ByName(name)?.ToString() ?? "-").Append('\n');
        var inv = new InventoryManager(model: release1122 ? InventoryModel.Release1122 : InventoryModel.Beta181);
        var windows = new[]
        {
            inv.PlayerWindow, inv.Open(1, WindowType.Chest, "c", 27), inv.Open(2, WindowType.Chest, "c", 54), inv.Open(3, WindowType.Workbench, "w", 10),
            inv.Open(4, WindowType.Furnace, "f", 3), inv.Open(5, WindowType.Dispenser, "d", 9),
        };
        foreach (var w in windows)
        {
            sb.Append(w.Type).Append(':');
            for (int s = -2; s < w.SlotCount + 2; s++) sb.Append(w.KindOf(s)).Append(',');
            sb.Append('\n');
        }
    });
}
