using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Perception;

namespace MineClaw.Agent.Tests;

/// <summary>Pins how <see cref="PerceptionPrompt.Parse"/> reads a fact's coords (WP06 refactor of <c>ReadCoords</c>): arrays, strings, objects, nesting.</summary>
public class PerceptionCoordsGoldenTests
{
    private const string Expected = "48F69C26B85B1D4F3A767044B7BB393FF51033785BFB18FBB2816FE4E827DD8B";

    private static readonly string[] Coords =
    [
        """{"x":1,"y":2,"z":3}""",
        """{"x":1.7,"y":-2.5,"z":3.99}""",
        """{"x":-1.7,"y":64,"z":-0.2}""",
        """{"x":"1","y":2,"z":3}""",
        """{"x":1,"y":2}""",
        """{"x":1,"y":null,"z":3}""",
        """{"X":1,"Y":2,"Z":3}""",
        """{"x":1e3,"y":2,"z":3,"w":4}""",
        """[{"x":4,"y":5,"z":6},[1,2,3],"7,8,9",{"x":1}]""",
        """[[1.5,2.5,-3.5],[0.49,-0.5,-0.51]]""",
        """[1.5,2.5,-3.5]""",
        """["1",2,3]""",
        """[1,2]""",
        """[1,2,3,4]""",
        """[[[1,2,3]],[[4,5,6],[7,8,9]]]""",
        """[]""",
        """ "a 1 b 2 c 3" """,
        """ "-1 -2 -3" """,
        """ "1,2" """,
        """ "1,2,3,4" """,
        """ "" """,
        """null""",
        """true""",
        """42""",
        """{}""",
        """[{"x":1,"y":2,"z":3},{"x":4,"y":5,"z":"6"},{"x":7,"y":8,"z":9}]""",
    ];

    [Fact]
    public void Coord_shapes_read_the_same()
    {
        var sb = new StringBuilder();
        foreach (var c in Coords)
        {
            var raw = "{\"answer\":\"a\",\"facts\":[{\"claim\":\"c\",\"coords\":" + c.Trim() + ",\"role\":\"r\",\"confidence\":0.5}]}";
            var (facts, answer, error) = PerceptionPrompt.Parse(raw);
            sb.Append(c.Trim()).Append(" => ").Append(answer ?? "null").Append(" | ").Append(error ?? "null").Append(" |");
            foreach (var f in facts) sb.Append(' ').Append(f.Claim).Append(" [").Append(string.Join(";", f.Coords.Select(p => $"{p.X},{p.Y},{p.Z}"))).Append(']');
            sb.Append('\n');
        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "perception-coords.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
