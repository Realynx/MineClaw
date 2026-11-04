using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Rag.Beliefs;

namespace MineClaw.Agent.Tests;

/// <summary>Pins <see cref="SeedOracle.Check"/> (WP06 refactor) over recipe, smelting, drop, count and liquid claims.</summary>
public class SeedOracleGoldenTests
{
    private const string Expected = "2666C639CCB8C219BFE3B620CE00B254BFA08A1023F4C0C7FC021FD3F1CBAE4B";

    [Fact]
    public void Seed_verdicts_stay_the_same()
    {
        string[] texts =
        [
            "glass from gravel", "glass is made by smelting sand", "torches from coal and sticks", "gravel drops flint", "a furnace from 8 cobblestone",
            "a furnace from 7 cobblestone", "4 torches from coal and sticks", "5 torches from coal and sticks", "a crafting table from 4 planks",
            "a crafting table from 3 planks", "iron ingot from iron ore", "iron ingots from smelting gold ore", "obsidian from lava and water",
            "cobblestone from water bucket and lava bucket", "bread from 3 wheat", "bread from 2 wheat", "a bed from wool and planks",
            "sticks from cobblestone", "a chest from 8 planks", "a chest from 8 cobblestone", "diamond from coal", "string from spiders",
            "charcoal from smelting logs", "paper from 3 sugar cane", "paper from sugar cane and leather", "slime ball from slimes",
            "gold block from 9 gold ingots", "2 gold blocks from 9 gold ingots", "clay from gravel", "flint from sand",
            "nonsense from gibberish", "a stone pickaxe from 3 cobblestone and 2 sticks", "a stone pickaxe from 3 cobblestone and 3 sticks",
        ];
        var sb = new StringBuilder();
        foreach (var t in texts)
        {
            var r = ClaimParser.TryRecipe(t);
            var v = r == null ? null : SeedOracle.Check(r);
            sb.Append(t).Append(" => ").Append(r == null ? "no recipe" : $"{r.Output} <- {string.Join("+", r.Inputs)} smelt={r.Smelt}").Append(" | ")
              .Append(v == null ? "null" : $"{v.Polarity} {v.Ref} {v.Reason}").Append('\n');
        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "seed-oracle.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
