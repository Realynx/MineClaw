using System.Security.Cryptography;
using System.Text;
using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="EntityLooks.Look"/> (WP07) over every kind it knows and then some: each name with every metadata index it
/// reads set to one value (all together, and each alone), each value of a list, with and without a version name, before and
/// after a golem's rose. The dump is long, so the test keeps its line count and SHA-256 (WP07_GOLDEN_OUT writes it for a diff).
/// </summary>
public class EntityLooksGoldenTests
{
    private static readonly string[] Names =
    [
        "sheep", "wolf", "pig", "creeper", "slime", "magma_cube", "lava_slime", "ghast", "blaze", "enderman", "villager_golem", "iron_golem",
        "horse", "donkey", "mule", "skeleton_horse", "zombie_horse", "llama", "villager", "zombie_villager", "ozelot", "ocelot", "rabbit",
        "parrot", "shulker", "boat", "storage_minecart", "powered_minecart", "minecart", "armor_stand", "cow", "zombie", "item_frame",
    ];

    private static readonly int[] Indices =
    [
        EntityMeta.Mob16, EntityMeta.Mob17, EntityMeta.CollarColor, EntityMeta.CreeperIgnited, EntityMeta.Screaming, EntityMeta.HorseVariant,
        EntityMeta.HorseStatus, EntityMeta.HasChest, EntityMeta.HorseArmor, EntityMeta.LlamaVariant, EntityMeta.CarpetColor, EntityMeta.Profession,
        EntityMeta.Converting, EntityMeta.OcelotType, EntityMeta.RabbitType, EntityMeta.ParrotVariant, EntityMeta.ShulkerColor, EntityMeta.BoatType,
        EntityMeta.StandFlags,
    ];

    private static readonly int[] Values = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 15, 16, 17, 0x1D, 0x1F, 99, 0x0302, 0x0405, -1];

    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static Entity Make(string name, Dictionary<int, object> meta, bool versionName, int data, DateTime rose) =>
        new(1, EntityKind.Mob, default, TypeId: 0, Metadata: meta) { TypeName = versionName ? name : null, Data = data, RoseUntil = rose };

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var name in Names)
            foreach (var v in Values)
            {
                var sets = new List<(string Label, Dictionary<int, object> Meta)> { ("all", Indices.ToDictionary(i => i, _ => (object)v)), ("none", new()) };
                foreach (var i in Indices) sets.Add(($"@{i}", new Dictionary<int, object> { [i] = v }));
                foreach (var (label, meta) in sets)
                    foreach (var rose in new[] { Now.AddSeconds(5), Now.AddSeconds(-5) })
                    {
                        // A null TypeName falls back to the b1.8.1 table, which names nothing here: only the named entity is varied.
                        var look = EntityLooks.Look(Make(name, meta, versionName: true, data: v, rose), Now);
                        sb.Append(name).Append(' ').Append(v).Append(' ').Append(label).Append(rose > Now ? " rose" : "").Append(" -> ").Append(look ?? "-").Append('\n');
                    }
            }
        return sb.ToString();
    }

    [Fact]
    public void Looks_match_the_golden_hash()
    {
        var dump = Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "entity-looks.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(EntityLooksGoldenData.Expected, $"{lines} {hash}");
    }
}
