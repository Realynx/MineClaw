using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MineClaw.Client.Tests;

/// <summary>
/// Exhaustive equivalence goldens for the FakeServer's pure rules (complexity refactor WP05): every input of
/// <see cref="MineClaw.Sim.FakeServer.VanillaMeta"/> (version × block id × damage × clicked face × cursor height × yaw × feet) is
/// dumped and hashed; the digest was taken from the switch implementation before it became a table, so a drift in any single
/// input fails here. On a mismatch the dump is written to the temp folder for a diff.
/// </summary>
public class SimRulesGoldenTests
{
    internal static void Check(string name, string expected, Action<StringBuilder> dump)
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

    [Fact]
    public void Vanilla_placement_meta_matches_the_golden() => Check("sim-vanilla-meta", "D0498C3DB2303C29D30EB875C3726529EC8B8AED50DE657C79ADD1EBAD061649", sb =>
    {
        float[] hits = [0f, 0.25f, 0.5f, 0.5000001f, 0.75f, 1f];
        float[] yaws = [-725f, -180f, -45f, -44.9f, 0f, 44.9f, 45f, 90f, 134.9f, 135f, 180f, 225f, 270f, 315f, 359.9f, 360f, 405f, 1000f];
        (double, double, double)[] feet =
            [(8.5, 64, 8.5), (8.5, 62, 8.5), (8.5, 66.5, 8.5), (10.4, 64, 8.5), (10.6, 64, 8.5), (6.6, 64, 8.5), (8.5, 64, 6.4), (5, 64, 5), (9.9, 63.2, 9.9)];
        foreach (bool modern in new[] { false, true })
            for (int id = -1; id < 260; id++)
            {
                if (MineClaw.Sim.FakeServer.VanillaMeta(modern, id, 0, 1, 0.5f, 0f, (0.5, 64, 0.5), 0, 64, 0) is null
                    && MineClaw.Sim.FakeServer.VanillaMeta(modern, id, 0, 2, 0.75f, 90f, (8.5, 64, 8.5), 8, 64, 8) is null)
                {
                    sb.Append(id).Append(" none\n");
                    continue;
                }
                sb.Append(modern ? 'M' : 'B').Append(id);
                for (int damage = 0; damage < 16; damage += 3)
                    for (int dir = -1; dir < 7; dir++)
                        foreach (float hit in hits)
                            foreach (float yaw in yaws)
                                foreach (var f in feet)
                                {
                                    var m = MineClaw.Sim.FakeServer.VanillaMeta(modern, id, damage, dir, hit, yaw, f, 8, 64, 8);
                                    sb.Append(m is { } v ? v.ToString(CultureInfo.InvariantCulture) : "-").Append(',');
                                }
                sb.Append('\n');
            }
    });
}
