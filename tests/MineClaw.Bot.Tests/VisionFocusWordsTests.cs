using MineClaw.Bot.GameTools;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The focus word tables in <see cref="VisionHub"/> (WP03 refactor) answer exactly like the pattern ladders they replaced,
/// over every word they name plus case variants, near misses and junk.
/// </summary>
public class VisionFocusWordsTests
{
    private static IEnumerable<string> Candidates()
    {
        string[] words =
        [
            "you", "me", "self", "player", "pointed", "this", "that", "this one", "that one", "punched",
            "hive", "the hive", "hive view", "overview", "hive overview", "everyone", "all bots", "bots",
            "gen", "the gen", "cobble gen", "cobble generator", "generator", "the generator", "cobblestone generator",
            "portal", "the portal", "nether portal", "farm", "the farm", "wheat farm",
            "", " ", "YOU", "Me", "Self", "PLAYER", "Pointed", "THIS ONE", "Hive", "GEN", "Portal", "farms", "the  gen", "this  one",
            "main house", "67", "fox", "1,2,3", "bot", "all", "nether", "punch", "overviews",
        ];
        foreach (var w in words)
        {
            yield return w;
            yield return w + " ";
            yield return w.ToUpperInvariant();
        }
    }

    [Fact]
    public void Word_tables_match_the_old_pattern_ladders()
    {
        foreach (var s in Candidates())
        {
            Assert.Equal(s is "you" or "me" or "self", VisionHub.SelfWords.Contains(s));
            Assert.Equal(s is "pointed" or "this" or "that" or "this one" or "that one" or "punched", VisionHub.PointedWords.Contains(s));
            Assert.Equal(s is "hive" or "the hive" or "hive view" or "overview" or "hive overview" or "everyone" or "all bots" or "bots", VisionHub.HiveWords.Contains(s));
            Assert.Equal(s is "you" or "me" or "self" or "player" or "pointed" or "this" or "that" or "this one" or "that one" or "punched", VisionHub.Deictic(s));
            string? kind = s switch
            {
                "gen" or "the gen" or "cobble gen" or "cobble generator" or "generator" or "the generator" or "cobblestone generator" => "cobble_generator",
                "portal" or "the portal" or "nether portal" => "portal",
                "farm" or "the farm" or "wheat farm" => "farm",
                _ => null,
            };
            Assert.Equal(kind, VisionHub.LandmarkKinds.TryGetValue(s, out var k) ? k : null);
        }
    }
}
