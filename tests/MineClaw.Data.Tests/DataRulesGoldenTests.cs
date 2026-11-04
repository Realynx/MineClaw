using System.Text;
using MineClaw.Data.Versioning;

namespace MineClaw.Data.Tests;

/// <summary>
/// Equivalence goldens for the data rules the complexity refactor (WP05) reshaped after the lookup goldens: the best tool for every
/// block against seeded inventories, 1.12.2 block records, name lookups (aliases, plurals, amounts, numbers), the bookshelf walk over
/// seeded layouts, potion chains and labels, recipe matching over every recipe placed at each offset (mirrored, perturbed), and the
/// recipe planner for every craftable item against seeded inventories; both versions where the rule is versioned. The digests were
/// taken from the implementations before they were reshaped.
/// </summary>
public class DataRulesGoldenTests
{
    private static readonly IGameData[] Versions = [Beta181GameData.Instance, Release1122GameData.Instance];

    private static void Check(string name, string expected, Action<StringBuilder> dump)
    {
        var sb = new StringBuilder();
        dump(sb);
        var text = sb.ToString();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (hash != expected)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-golden-{name}.txt");
            File.WriteAllText(path, text);
            Assert.Fail($"{name}: digest {hash} != {expected} (dump: {path})");
        }
    }

    private static IEnumerable<int> BlockIds(IGameData d) => Enumerable.Range(0, 260).Concat(d.BlockIds).Distinct().Order();

    private static int[] ToolIds(IGameData d) =>
        (d == Versions[0] ? Items.All.Select(i => i.Id) : Enumerable.Range(256, 2100).Where(Release1122GameData.Instance.ItemExists))
            .Where(id => id >= 256 && (d.Item(id).Tool != ToolType.None || id % 37 == 0)).ToArray();

    [Fact]
    public void Best_tool_matches_the_golden() => Check("data-best-tool", "736D8BC3330FD6DE82D13FCDD2E07A8A35C65CB31D14793F38DFD09872D36E72", sb =>
    {
        foreach (var d in Versions)
            using (GameData.Override(d))
            {
                var tools = ToolIds(d);
                var r = new Random(7);
                var inventories = Enumerable.Range(0, 24).Select(_ => Enumerable.Range(0, 12)
                    .Select(_ => r.Next(4) == 0 ? ItemStack.Empty : new ItemStack((short)tools[r.Next(tools.Length)], 1, (short)r.Next(3)))
                    .ToArray()).ToList();
                inventories.Add([]);
                inventories.Add(tools.Select(t => new ItemStack((short)t, 1, 0)).ToArray());
                foreach (int b in BlockIds(d))
                {
                    sb.Append(d.VersionId).Append(' ').Append(b).Append(':');
                    foreach (var inv in inventories) sb.Append(' ').Append(Tools.BestToolIndex(b, inv));
                    sb.Append('\n');
                }
            }
    });

    [Fact]
    public void Release1122_block_records_match_the_golden() => Check("data-1122-blocks", "203FE5C46BEFE5211CDAD0B7B3163EF57F349E3F9F19E38F0177424F7EB632FA", sb =>
    {
        foreach (int b in BlockIds(Release1122GameData.Instance)) sb.Append(Release1122GameData.Instance.Block(b)).Append('\n');
    });

    [Fact]
    public void Beta_name_lookup_matches_the_golden() => Check("data-beta-names", "3266AE35F0B3A8E38308425FC49B6B423A60D57F74C23F241C19A2F13B2F9CFE", sb =>
    {
        var names = new List<string> { "", " ", "minecraft:stone", "cobble", "workbench", "20 cobble slabs", "cobble slab x20", "a red wool",
            "torch x16", "16x torch", "a bucket", "charcoal", "bone meal", "bone_meal", "red wool", "red wools", "oak planks", "birch planks",
            "glasses", "torches", "bucketes", "4", "17", "300", "9999", "-1", "nothing at all", "sugar canes", "redstone", "bed", "cake" };
        foreach (var i in Items.All) { names.Add(i.Name); names.Add(i.DisplayName); names.Add(i.Name + "s"); names.Add(i.DisplayName + "es"); names.Add("3 " + i.DisplayName); }
        foreach (var n in names)
            sb.Append('[').Append(n).Append("] ").Append(Items.ByNameWithDamage(n) is { } hit ? $"{hit.Item.Id}/{hit.Damage}" : "-").Append('\n');
    });

    [Fact]
    public void Bookshelf_walk_matches_the_golden() => Check("data-bookshelves", "B056DB499EAB411838BF9C6255805D340C227AE91DA48BE9CFF0BF67ECAB5802", sb =>
    {
        var r = new Random(11);
        for (int layout = 0; layout < 3000; layout++)
        {
            var cells = new Dictionary<(int, int, int), int>();
            int density = r.Next(1, 6);
            for (int x = -2; x <= 2; x++)
            for (int y = 0; y <= 1; y++)
            for (int z = -2; z <= 2; z++)
            {
                int k = r.Next(10);
                if (k < density) cells[(x, y, z)] = Enchanting.Bookshelf;
                else if (k == 9 && Math.Abs(x) <= 1 && Math.Abs(z) <= 1) cells[(x, y, z)] = 1;
            }
            sb.Append(Enchanting.Bookshelves((x, y, z) => cells.GetValueOrDefault((x, y, z)), 0, 0, 0)).Append(layout % 40 == 39 ? '\n' : ',');
        }
    });

    [Fact]
    public void Potion_chains_and_labels_match_the_golden() => Check("data-potions", "2D1B340DFC82A9D20E7FE3AB0EABF1BDE1CADE5788CC9E9E6E163D8420B3787C", sb =>
    {
        int[] forms = [Brewing.Potion, Brewing.SplashPotion, Brewing.LingeringPotion, Brewing.GlassBottle, 1, 0];
        var types = Brewing.Types.Concat(["", "nope", "strong_nope", "long_x_y"]).ToList();
        foreach (int form in forms)
            foreach (var t in types)
            {
                sb.Append(form).Append(' ').Append(t).Append(": ");
                try { sb.Append(Brewing.Label(form, t)); } catch (Exception e) { sb.Append('!').Append(e.GetType().Name); }
                var chain = Brewing.Chain(form, t);
                sb.Append(" | ").Append(chain is null ? "-" : string.Join(" ; ", chain)).Append('\n');
            }
    });

    [Fact]
    public void Recipe_matching_matches_the_golden() => Check("data-recipe-match", "FE3DD0591FE7EE8733D18BAF1444A4D12BB27130EDFB92EDE076E14DF7AF53E3", sb =>
    {
        foreach (var d in Versions)
            using (GameData.Override(d))
            {
                var r = new Random(5);
                foreach (var recipe in Recipes.All.Where(x => x.Shaped).Take(400))
                    foreach (int n in new[] { 2, 3 })
                    {
                        if (recipe.Width > n || recipe.Height > n) continue;
                        for (int ox = 0; ox <= n - recipe.Width; ox++)
                        for (int oy = 0; oy <= n - recipe.Height; oy++)
                            foreach (bool mirror in new[] { false, true })
                                foreach (bool perturb in new[] { false, true })
                                {
                                    var grid = Enumerable.Repeat(ItemStack.Empty, n * n).ToArray();
                                    for (int x = 0; x < recipe.Width; x++)
                                    for (int y = 0; y < recipe.Height; y++)
                                        if (recipe.Pattern[y * recipe.Width + (mirror ? recipe.Width - x - 1 : x)] is { } ing)
                                            grid[(y + oy) * n + x + ox] = new ItemStack((short)ing.Id, 1, (short)Math.Max(0, ing.Damage));
                                    if (perturb) grid[r.Next(n * n)] = r.Next(2) == 0 ? ItemStack.Empty : new ItemStack(4, 1, 0);
                                    var m = Recipes.Match(grid, n);
                                    sb.Append(m is null ? "-" : $"{m.OutputId}:{m.OutputDamage}x{m.OutputCount}").Append(' ');
                                }
                        sb.Append('\n');
                    }
            }
    });

    [Fact]
    public void Recipe_planner_matches_the_golden() => Check("data-planner", "8DBDA04F2CA8C559FC200D18B3146DFB072BB0DD931BC2CDE355C5A520B83B23", sb =>
    {
        foreach (var d in Versions)
            using (GameData.Override(d))
            {
                var r = new Random(3);
                var raw = new[] { Blocks.Log, Blocks.Cobblestone, Blocks.Planks, Items.Stick, Items.IronIngot, Items.Coal, Blocks.Sand, Items.String, Items.Diamond };
                var outputs = Recipes.All.Select(x => (x.OutputId, x.OutputDamage)).Distinct().Take(250).ToList();
                foreach (var (id, dmg) in outputs)
                    for (int k = 0; k < 3; k++)
                    {
                        var inv = Enumerable.Range(0, r.Next(0, 6)).Select(_ => new ItemStack((short)raw[r.Next(raw.Length)], (byte)r.Next(1, 65), 0)).ToList();
                        var opts = new PlannerOptions { AllowSmelting = k == 1, HasCraftingTable = k != 2 };
                        var plan = RecipePlanner.Plan(id, 1 + k * 3, inv, dmg, opts);
                        sb.Append(id).Append(':').Append(dmg).Append(' ').Append(k).Append(' ').Append(plan.Complete ? "ok" : "no")
                            .Append(" | ").Append(string.Join(" ; ", plan.Steps)).Append(" | ").Append(string.Join(", ", plan.Missing))
                            .Append(" | ").Append(plan.NeedsCraftingTable).Append(plan.NeedsFurnace).Append(plan.ItemsToSmelt).Append('\n');
                    }
            }
    });
}
