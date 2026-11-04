using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Data.Versioning;

namespace MineClaw.Data.Tests;

/// <summary>
/// Exhaustive equivalence goldens for the data lookups (drops, fuel, food, display names, harvest rules, dig ticks…) of both
/// versions: every (block id × meta × held item) and (item id × damage) input is dumped in a canonical text form and hashed.
/// The digests were taken from the switch/if-ladder implementations before they became tables (complexity refactor WP05), so
/// any drift in a single input fails here. On a mismatch the full dump is written to the temp folder for a diff.
/// </summary>
public class DataLookupGoldenTests
{
    private static readonly IGameData Beta = Beta181GameData.Instance;
    private static readonly Release1122GameData Modern = Release1122GameData.Instance;

    private static IEnumerable<int> BlockIdsOf(IGameData d) => Enumerable.Range(0, 260).Concat(d.BlockIds).Distinct().Order();

    private static IEnumerable<int> ItemIdsOf(IGameData d) =>
        Enumerable.Range(-1, 2).Concat(d == Beta ? Items.All.Select(i => i.Id) : Enumerable.Range(1, 2300).Where(Modern.ItemExists)).Distinct().Order();

    private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    private static void Check(string name, string expected, Action<StringBuilder> dump)
    {
        var sb = new StringBuilder();
        dump(sb);
        var text = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (hash != expected)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-golden-{name}.txt");
            File.WriteAllText(path, text);
            Assert.Fail($"{name}: digest {hash} != {expected} (dump: {path})");
        }
    }

    private static void DumpDrops(StringBuilder sb, IGameData d)
    {
        var helds = ItemIdsOf(d).ToArray();
        foreach (int b in BlockIdsOf(d))
            for (int meta = 0; meta < 16; meta++)
                foreach (int held in helds)
                {
                    var drops = d.DropsFor(b, meta, held);
                    if (drops.Count == 0) continue;
                    sb.Append(b).Append(':').Append(meta).Append(':').Append(held).Append(" ->");
                    foreach (var x in drops)
                        sb.Append(' ').Append(x.ItemId).Append('/').Append(x.Damage).Append('/').Append(x.Min).Append('-').Append(x.Max)
                            .Append('@').Append(F(x.Chance));
                    sb.Append('\n');
                }
    }

    private static void DumpItems(StringBuilder sb, IGameData d)
    {
        foreach (int id in Enumerable.Range(-2, 2400))
        {
            sb.Append(id).Append(" fuel=").Append(d.FuelTicks(id));
            for (int dmg = -1; dmg < 17; dmg++)
            {
                var food = d.Food(id, dmg);
                sb.Append(" | ").Append(dmg).Append(' ').Append(d.DisplayName(id, dmg)).Append(" food=")
                    .Append(food is { } f ? $"{f.Hunger},{F(f.Saturation)}" : "-").Append(" poison=").Append(F(d.FoodPoisonChance(id, dmg)));
                if (dmg >= 0) sb.Append(" vn=").Append(d.VariantName(id, dmg));
            }
            sb.Append(" worn=").Append(d.DisplayName(id, 100)).Append('\n');
        }
    }

    private static void DumpHarvest(StringBuilder sb, IGameData d)
    {
        var helds = ItemIdsOf(d).ToArray();
        foreach (int b in BlockIdsOf(d))
        {
            sb.Append(b);
            foreach (int held in helds)
                sb.Append(' ').Append(d.CanHarvest(b, held) ? 'H' : '-').Append(d.DigTicks(b, held)).Append(',')
                    .Append(d.DigTicks(b, held, onGround: false)).Append(',').Append(d.DigTicks(b, held, inWater: true));
            sb.Append('\n');
        }
    }

    [Fact] public void Beta_drops_match_the_golden() => Check("beta-drops", "57288697FEFBE0D753F2B2CFCED74A2B76EABF67A5D1D4443E0FBC9194857FC3", sb => DumpDrops(sb, Beta));
    [Fact] public void Release1122_drops_match_the_golden() => Check("1122-drops", "2F4588F8817229AB480BB8D443C51761B95926ADD3EB64C71F08FFBBF1FA7AF4", sb => DumpDrops(sb, Modern));
    [Fact] public void Beta_items_match_the_golden() => Check("beta-items", "36806092F5BBDD247E4BE4F4142D1B0C90690689312B6ECEAEC8C745AA8494E1", sb => DumpItems(sb, Beta));
    [Fact] public void Release1122_items_match_the_golden() => Check("1122-items", "797485FFDCF3783EEA904794D0B07A1DC8020C70C276032262D4DCC51832C3E5", sb => DumpItems(sb, Modern));
    [Fact] public void Beta_harvest_and_dig_match_the_golden() => Check("beta-harvest", "6E2ADFF25B3F11F482FE26D1B69D1A359F6C4176607DAF3865485F18D1DF55D5", sb => DumpHarvest(sb, Beta));
    [Fact] public void Release1122_harvest_and_dig_match_the_golden() => Check("1122-harvest", "AF387BA6454297DF60FE0659E147485FB29DACF27F968D752471377B0201C67A", sb => DumpHarvest(sb, Modern));

    [Fact]
    public void Release1122_dig_speed_matches_the_golden() => Check("1122-dig", "1C0B9D00F57F446F37CB28827881A0AE25FAF59EF12C3FD30B87FF3D04DFB2AA", sb =>
    {
        var helds = ItemIdsOf(Modern).ToArray();
        foreach (int b in BlockIdsOf(Modern))
        {
            sb.Append(b);
            foreach (int held in helds) sb.Append(' ').Append(F(Release1122Dig.DestroySpeed(Modern, b, held)));
            foreach (int held in new[] { -1, Items.DiamondPickaxe, Items.IronAxe, Items.StoneShovel })
                for (int haste = 0; haste < 3; haste++)
                    for (int fatigue = 0; fatigue < 5; fatigue++)
                        foreach (bool aqua in new[] { false, true })
                        {
                            var stack = held > 0 ? new ItemStack((short)held, 1, 0) : ItemStack.Empty;
                            var input = new DigInput(b, 0, stack, OnGround: fatigue % 2 == 0, InWater: haste == 1, haste, fatigue, aqua);
                            sb.Append(' ').Append(F(Release1122Dig.RelativeHardness(Modern, input)));
                        }
            sb.Append('\n');
        }
    });

    [Fact]
    public void Beta_tool_tables_match_the_golden() => Check("beta-tools", "07EB8BF5548435B3E735D1DDFF191BC36DC4FF44F9A527B09840CBD6ED7A6017", sb =>
    {
        foreach (int b in BlockIdsOf(Beta))
        {
            sb.Append(b).Append(" form=").Append(Items.ItemFormOf(b)).Append(" obt=").Append(Items.IsObtainableBlockItem(b));
            foreach (var it in Items.All) sb.Append(' ').Append(F(Tools.StrVsBlock(b, it.Id)));
            sb.Append('\n');
        }
    });

    /// <summary>Every 1.12.2 item name plus the odd ones the rules name (pumpkin, skull, enchanted book…) and malformed names.</summary>
    private static IEnumerable<string> ItemNames() =>
        Enumerable.Range(1, 2300).Select(Modern.NameOf).OfType<string>()
            .Concat(["book", "enchanted_book", "pumpkin", "skull", "shears", "elytra", "shield", "carrot_on_a_stick", "flint_and_steel",
                "chainmail_boots", "stone_helmet", "wooden_", "_axe", "foo", "", "leather_sword", "golden_hoe"])
            .Distinct();

    [Fact]
    public void Enchantment_fits_and_anvil_repair_match_the_golden() => Check("enchant-anvil", "C4C8E2989759372FDF02DB399C8F5F3BA9E89F4264972B14CD23E86773B9E9C6", sb =>
    {
        foreach (var item in ItemNames())
        {
            sb.Append(item).Append(" repair=").Append(Anvil.RepairMaterial(item) is { } m ? $"{m.Id}/{m.Meta}/{m.Name}" : "-").Append(' ');
            foreach (var e in Enchanting.All)
                sb.Append(Enchanting.Fits(e, item) ? 'T' : '-').Append(Enchanting.Fits(e, item, anvil: true) ? 'A' : '-');
            sb.Append('\n');
        }
    });
}
