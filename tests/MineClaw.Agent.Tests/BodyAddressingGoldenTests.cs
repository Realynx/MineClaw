using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Bodies;

namespace MineClaw.Agent.Tests;

/// <summary>Pins <see cref="BodyAddressing.Addressed"/> (WP06 refactor of <c>Addressed</c> / <c>IsAddress</c>) over a corpus of lines, plain and as emotes.</summary>
public class BodyAddressingGoldenTests
{
    private const string Expected = "FB59B2DC3AD074EBADA31ACB9D4EC3230B7954132E82A80FDB0822338A315595";

    private static readonly string[] Lines =
    [
        "67 go mine", "hey 67, chop trees", "67 and 69 come here", "67, 69 & 420 follow me", "tell 67 to stop", "give it to 69",
        "maid chan 69 dig", "Clawd spin", "main spin", "clawd and 67 come", "go to 67,64,-3", "the chest at 10, 67, 3", "1337 maid",
        "ok 420 thanks", "so 69, what now", "alright 69: go", "yes 67, do it", "67.5 blocks away", "x67 is a seed", "we need 67 logs",
        "@67 hi", "#69 stop", "  67   ", "hey maids, both of you", "everyone stop", "nice job 69!", "69!", "hi 67. 69 too",
        "maid_chan_67 come here", "maid chan 67 and clawd", "67 or 69, whoever", "thanks clawd", "where is 420", "is 67 done?",
        "67 n 69 go", "67+69 help", "about 67 of them", "Clawd_bot follow", "clawd, 67, 69: regroup", "", "   ",
        "the 69 bus", "ty 1337", "pls 420 craft", "sorry 69 my bad", "69 chan come", "1337 sis", "67 bot go",
    ];

    [Fact]
    public void Lines_address_the_same_bodies()
    {
        var a = new BodyAddressing("Clawd_bot", ["Clawd"], ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"]);
        var sb = new StringBuilder();
        foreach (var line in Lines)
            foreach (var emote in new[] { false, true })
                sb.Append(line).Append(" | ").Append(emote).Append(" => ").Append(string.Join(",", a.Addressed(line, emote)))
                  .Append(" / ").Append(string.Join(",", a.AddressedClones(line, emote))).Append(" / ").Append(BodyAddressing.IsGroupAddress(line)).Append('\n');
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "body-addressing.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
