using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.Perception;
using MineClaw.Bot.WorldIndex;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the id/name lookups the WP03 refactor turns into tables, over every input: <see cref="Protection.IsNaturalFill"/> (what a
/// structure box never protects), <see cref="Protection.IsFarmPlant"/>, <see cref="Gestures.IsUseItem"/> and
/// <see cref="IndexKinds.FromAlias"/>. One hash per function.
/// </summary>
public class LookupGoldenTests
{
    private static string Ids(Func<int, bool> f)
    {
        var sb = new StringBuilder();
        for (int id = -5; id <= 33000; id++) if (f(id)) sb.Append(id).Append(',');
        return sb.ToString();
    }

    private static readonly string[] Intents = ["thanks", "come", "banter", "greeting", "made.up"];

    private static readonly string[] Aliases =
    [
        "gen", "generator", "Cobble Gen", "cobblegen", "cobblestone_generator", "stone_generator", "generators", "workbench", "crafting",
        "table", "crafting_table", "crafting tables", "chests", "chest", "storage", "furnaces", "furnace", "trees", "tree", "sapling",
        "saplings", "farms", "farm", "wheat", "sugar_cane", "sugar cane", "sugarcane", "cactus", "portals", "portal", "nether_portal",
        "nether portals", "beds", "bed", "jukebox", "note_block", "note blocks", "spawner", "spawners", "sign", "signs", "dispenser",
        "island", "islands", "hangout", "hazard", "unknown_structure", "unknown structures", "floor", "activity", "ss", "s", "", "  Chests  ",
        "cobble_generator", "cobble_generators", "mob", "glass",
    ];

    [Fact]
    public void Lookups_match_the_golden_hashes()
    {
        var dumps = new Dictionary<string, string>
        {
            ["IsNaturalFill"] = Ids(Protection.IsNaturalFill),
            ["IsFarmPlant"] = Ids(Protection.IsFarmPlant),
            ["IsUseItem"] = Ids(Gestures.IsUseItem),
            ["FromAlias"] = string.Join("\n", Aliases.Select(a => a + "=" + (IndexKinds.FromAlias(a) ?? "null"))),
            ["BindingExample"] = string.Join("\n", MineClaw.Scripting.Events.GameEventCatalog.Names.Concat(Intents)
                .SelectMany(ev => new[] { null, "Robin" }.Select(pl => ev + "/" + (pl ?? "-") + "=" + MineClaw.Bot.GameTools.ScriptBridge.Example(ev, pl).ToJsonString()))),
        };
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        var actual = dumps.Select(kv => $"{kv.Key} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kv.Value)))[..24]}").ToList();
        if (outDir is { Length: > 0 })
        {
            foreach (var (k, v) in dumps) File.WriteAllText(Path.Combine(outDir, $"lookup-{k}.txt"), v);
            File.WriteAllLines(Path.Combine(outDir, "lookup-hashes.txt"), actual);
            return;
        }
        Assert.Equal(Expected, actual);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private static readonly string[] Expected =
    [
        "IsNaturalFill 7D3B446BC4B2882CA200E0AE",
        "IsFarmPlant 4A9C29AC841190E739143E0F",
        "IsUseItem 063210FD905CB06BF0116B13",
        "FromAlias 99F69159EEC47D10227DE465",
        "BindingExample 1B01993246505D77C5498DA2",
    ];
}
