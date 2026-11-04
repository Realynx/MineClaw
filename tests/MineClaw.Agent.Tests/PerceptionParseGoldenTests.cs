using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Perception;

namespace MineClaw.Agent.Tests;

/// <summary>Pins <see cref="PerceptionPrompt.Parse"/> (WP06 refactor) over clean, fenced, prose-wrapped, truncated and odd answers.</summary>
public class PerceptionParseGoldenTests
{
    private const string Expected = "A7C1482D7D74E39F199C67F44810450925E6AEDFE6BB3BE5167628A5072E6093";

    private static readonly string?[] Corpus =
    [
        null, "", "   ", "no json here", "} backwards {",
        """{"look":"x","answer":" a gen cell ","facts":[{"claim":"cobble at gen","coords":[[1,64,2]],"role":"gen cell","confidence":0.9}]}""",
        "```json\n{\"answer\":\"yes\",\"facts\":[{\"claim\":\" water \",\"coords\":\"3,64,-4\",\"role\":\"water\",\"confidence\":\"0.7\"}]}\n```",
        """Sure! {"answer":"hm","facts":[{"claim":"","coords":[]},{"claim":"void","coords":[[0,63,0],[1,63,0]],"role":"null","confidence":2}]} hope that helps""",
        """{"answer":5,"facts":[1,"x",{"claim":"wall","role":"none","confidence":-1},{"coords":[[4,64,4]],"role":""},{"claim":"door","confidence":"abc"}]}""",
        """{"answer":"cut","facts":[{"claim":"floor","coords":[[1,2,3]],"role":"floor","confidence":0.4},{"claim":"lava","coo""",
        """{"answer":"no facts"}""",
        """{"facts":{"claim":"not an array"}}""",
        """{"answer":"x","facts":[{"claim":"many","coords":[[1,2,3],"4,5,6",[7,8],[9,10,11,12],"a,b,c"],"role":"protected","confidence":0.33}]}""",
    ];

    [Fact]
    public void Answers_parse_the_same()
    {
        var sb = new StringBuilder();
        foreach (var raw in Corpus)
        {
            var (facts, answer, error) = PerceptionPrompt.Parse(raw);
            sb.Append(answer ?? "null").Append(" | ").Append(error ?? "null").Append('\n');
            foreach (var f in facts)
                sb.Append("  ").Append(f.Claim).Append(" [").Append(string.Join(";", f.Coords.Select(c => $"{c.X},{c.Y},{c.Z}"))).Append("] ")
                  .Append(f.Role ?? "null").Append(' ').Append(f.Confidence.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "perception-parse.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
