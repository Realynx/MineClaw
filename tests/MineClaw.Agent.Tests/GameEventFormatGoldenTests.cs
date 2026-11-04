using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="GameEvent.Format"/>, <see cref="GameEvent.DefaultPriority"/> and <see cref="GameEvent.BodyTag"/> (WP06 refactor):
/// every event kind × priority × player × body × text shape × count, the model-facing line each renders to.
/// </summary>
public class GameEventFormatGoldenTests
{
    private const string Expected = "012FFFA423F43775A005C1487871180627A615E0EB8CD32B6393ADECB5BDB6E3";

    [Fact]
    public void Every_event_shape_formats_the_same()
    {
        var sb = new StringBuilder();
        string?[] players = [null, "kiwi"];
        string?[] bodies = [null, "", "main", "MAIN", "maid_chan_67", "maid_chan_", "bob", "_7", "a_b_69x"];
        string[] texts = ["joined", "[bodies] 67 and 69 are up", ""];
        int[] counts = [1, 3];
        foreach (var kind in Enum.GetValues<EventKind>())
        {
            sb.Append(kind).Append(" default ").Append(GameEvent.DefaultPriority(kind)).Append('\n');
            foreach (var pri in Enum.GetValues<EventPriority>())
                foreach (var player in players)
                    foreach (var body in bodies)
                        foreach (var text in texts)
                            foreach (var count in counts)
                            {
                                var e = new GameEvent(kind, text, player, pri) { Body = body, Count = count };
                                sb.Append(e.Format()).Append('\n');
                            }
        }
        foreach (var body in bodies) sb.Append(body ?? "null").Append(" -> ").Append(GameEvent.BodyTag(body) ?? "null").Append('\n');

        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "game-event-format.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
