using System.Security.Cryptography;
using System.Text;
using MineClaw.Data;
using MineClaw.Skills.Inventory;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Pins the pure helpers the WP09 refactor reshapes (storage categories, slot layouts and presets, click permutations,
/// positions, catalog stems) over a broad, seeded input set: each section's text is hashed. WP09_PURE_OUT=&lt;dir&gt; writes
/// the section texts (pure-&lt;section&gt;.txt) for diffing.
/// </summary>
public class PureFunctionGoldenTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> Pinned = new()
    {
        ["category"] = "E4FFD11DC5062AF3405A07C965FEBAF2",
        ["layout"] = "798090E8FE91A3B28791E884EF3BF315",
        ["permute"] = "C0758FE07E0A1AAFD84328917E4C4571",
        ["pos"] = "610E436069243C770E9ADCC7A7547648",
        ["stem"] = "9B912FF3E0B7AC36F0B03CCECC5B491C",
    };

    private void Check(string section, StringBuilder sb)
    {
        var text = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        if (Environment.GetEnvironmentVariable("WP09_PURE_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "pure-" + section + ".txt"), text);
        }
        output.WriteLine($"{section} {hash} ({text.Length} chars)");
        Assert.Equal(Pinned[section], hash);
    }

    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (Exception ex) { return "throws " + ex.GetType().Name + ": " + ex.Message; }
    }

    [Fact]
    public void Storage_categories_are_unchanged()
    {
        var sb = new StringBuilder();
        for (int id = 0; id < 2400; id++)
            sb.Append(id).Append(':').Append(StorageCategory.Of(id)).Append(',').Append(StorageCategory.Of(id, 4)).Append(',')
                .Append(StorageCategory.Of(id, 15)).Append(',').Append(StorageCategory.Of(id, 1)).Append('\n');
        string?[] words =
        [
            null, "", "  ", "ores", "Ore chest", "storage:ores", "ingots", "minerals", "wood", "logs", "planks", "stone", "cobble", "blocks",
            "building", "farming", "crops", "seeds", "farm", "food", "tools", "armor", "weapons", "mob", "mob drops", "misc", "junk",
            "STORAGE:WOOD", "storage:", "tool chest", "cobblestone storage", "food and seeds", "ore", "misc stuff", "stones", "toolbox",
        ];
        foreach (var w in words) sb.Append(w ?? "<null>").Append(" -> ").Append(StorageCategory.Parse(w) ?? "<null>").Append('\n');
        foreach (var c in StorageCategory.Order.Append("bogus")) sb.Append(c).Append(" rank ").Append(StorageCategory.Rank(c)).Append('\n');
        Check("category", sb);
    }

    private static List<ItemStack> RandomStacks(Random rng, int n)
    {
        int[] ids = [Blocks.Cobblestone, Blocks.Dirt, Blocks.Log, Blocks.Planks, Items.Coal, Items.Diamond, Items.Bread, Items.Seeds, Items.Dye,
            Items.IronIngot, Items.String, Items.Bow, Blocks.Torch, Items.Stick, Items.Bone, Blocks.Sand];
        var l = new List<ItemStack>();
        for (int i = 0; i < n; i++)
        {
            if (rng.Next(3) == 0) { l.Add(ItemStack.Empty); continue; }
            int id = ids[rng.Next(ids.Length)];
            int dmg = id == Items.Dye ? rng.Next(3) * 15 / 2 : 0;
            l.Add(new ItemStack((short)id, (byte)(1 + rng.Next(64)), (short)dmg));
        }
        return l;
    }

    [Fact]
    public void Layouts_and_presets_are_unchanged()
    {
        var sb = new StringBuilder();
        string[] templates =
        [
            "cobble*3,dirt*2,_*4 / log*9", "", "_", "log", "torch*9/torch*9/torch*9", "torch*10", "stick*0,coal*99", "bogus_item*2",
            "empty,_,,dirt", "log*9/log*9/log*9/log*9", "planks*2 / / dirt", "bone meal*3,ink sac", "water bucket",
        ];
        foreach (var t in templates)
            foreach (var slots in new[] { 27, 9 })
                sb.Append(t).Append(" @").Append(slots).Append(" => ")
                    .Append(Try(() => string.Join(",", SlotLayout.Parse(t, slots).Slots.Select(s => s is { } v ? $"{v.Id}:{v.Damage}" : "_")))).Append('\n');
        var rng = new Random(9);
        for (int round = 0; round < 40; round++)
        {
            int n = (round % 4) switch { 0 => 27, 1 => 54, 2 => 9, _ => 5 };
            var stacks = RandomStacks(rng, n);
            sb.Append("# round ").Append(round).Append(' ').Append(string.Join(",", stacks.Select(s => s.IsEmpty ? "_" : $"{s.Id}:{s.Damage}x{s.Count}"))).Append('\n');
            sb.Append("describe ").Append(SlotLayout.Describe(stacks)).Append('\n');
            foreach (var p in LayoutPresets.Names.Append("other"))
                sb.Append(p).Append(' ').Append(string.Join(",", LayoutPresets.Target(p, stacks))).Append('\n');
            var layout = new SlotLayout(Enumerable.Range(0, Math.Max(1, n - round % 7)).Select(i =>
                rng.Next(3) == 0 ? ((int, int)?)null : stacks[rng.Next(n)] is { IsEmpty: false } s ? (s.Id, s.Damage) : (Blocks.Glass, 0)).ToList());
            var (target, unplaced) = LayoutPresets.Target(layout, stacks);
            sb.Append("layout ").Append(string.Join(",", target)).Append(" unplaced ").Append(unplaced).Append('\n');
        }
        Check("layout", sb);
    }

    [Fact]
    public void Permutations_are_unchanged()
    {
        var sb = new StringBuilder();
        var rng = new Random(4);
        for (int round = 0; round < 60; round++)
        {
            int n = 1 + rng.Next(30);
            var perm = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).ToArray();
            var source = perm.Select(p => rng.Next(4) == 0 ? -1 : p).ToArray();
            // a valid source never maps two slots from one stack: drop duplicates after the first
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++) if (source[i] >= 0 && !seen.Add(source[i])) source[i] = -1;
            sb.Append(string.Join(",", source)).Append(" => ").Append(string.Join(" ", ClickPlanner.Permute(source).Select(c => c.ToString()))).Append('\n');
        }
        Check("permute", sb);
    }

    [Fact]
    public void Positions_are_unchanged()
    {
        var sb = new StringBuilder();
        string[] docs =
        [
            "{}", "{\"pos\":\"1,2,3\"}", "{\"pos\":\"1 2 3\"}", "{\"pos\":\"1;2;3\"}", "{\"pos\":\"1,2\"}", "{\"pos\":\"1,2,3,4\"}", "{\"pos\":\"a,b,c\"}",
            "{\"pos\":[1,2,3]}", "{\"pos\":[1,2]}", "{\"pos\":[1,2.5,3]}", "{\"pos\":[1,\"2\",3]}", "{\"pos\":{\"x\":1,\"y\":2,\"z\":3}}",
            "{\"pos\":{\"x\":1,\"y\":2}}", "{\"pos\":{\"x\":\"1\",\"y\":\"2\",\"z\":\"3\"}}", "{\"pos\":5}", "{\"pos\":null}", "{\"pos\":true}",
            "{\"at\":\"-4,64,9\"}", "{\"pos\":\" -1 , 70 , -2 \"}", "{\"pos\":{\"x\":1.5,\"y\":2,\"z\":3}}",
        ];
        foreach (var d in docs)
        {
            var e = SkillJson.Parse(d);
            sb.Append(d).Append(" pos=").Append(Try(() => e.Pos()?.ToString() ?? "null")).Append(" at=").Append(Try(() => e.Pos("at")?.ToString() ?? "null")).Append('\n');
        }
        Check("pos", sb);
    }

    [Fact]
    public void Catalog_stems_are_unchanged()
    {
        var sb = new StringBuilder();
        string[] words =
        [
            "mining", "mined", "mines", "mine", "boxes", "glasses", "grass", "chests", "dig", "digging", "sing", "ring", "bed", "red",
            "ses", "goes", "us", "is", "bus", "torches", "planted", "chopping", "logs", "trees", "cut", "fetch", "get", "make", "craft",
            "building", "built", "fishing", "fished", "potatoes", "a", "", "seeds", "ores",
        ];
        foreach (var w in words) sb.Append(w).Append(" -> ").Append(SkillCatalog.Stem(w)).Append('\n');
        Check("stem", sb);
    }
}
