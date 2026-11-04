using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Brainstem;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="IntentMatcher.Reflex"/> (WP06 refactor) over a corpus of chat lines: every command shape (stop, trailing
/// stop, come, follow, look, gestures, give with every count form) and the near-misses that must go to the brain, each with
/// no item checker, an always-yes checker and a picky one.
/// </summary>
public class IntentReflexGoldenTests
{
    private const string Expected = "5C3A1077490051DCE9C0B4D6702F0D7E6BE4ABE9FDAC28879A6FCCA94454E4A1";

    private static readonly string[] Names = ["Clawd_bot", "clawd", "maid chan"];

    private static readonly string[] Corpus =
    [
        "", "   ", "stop?", "stop", "STOP!", "clawd stop", "stop clawd", "@clawd stop please", "ok stop now", "hold on", "wait for me",
        "stop following me", "cancel that", "everyone stop", "oh my god both of you stop", "ok guys stop it", "don't stop", "why stop",
        "the bus stop", "pls just stop", "come", "come here", "clawd, come over here!", "get over here", "come back here pls",
        "follow me", "follow", "come with me", "follow along", "look at me", "eyes on me", "face me", "jump", "jump again", "spin twice",
        "sneak", "nod", "dance", "do a dance", "do a wave", "wave hi", "wave at me", "crouch around", "give me cobblestone",
        "give me 5 cobblestone", "give me cobblestone 12", "give me 999 dirt", "give me 0 dirt", "toss me a stack of logs",
        "hand me an apple", "pass me one torch", "drop me some sand", "give me a few seeds", "give me your sword", "give me the pickaxe",
        "give me all of your dirt", "give me all dirt", "give all the cobble", "drop dirt", "throw me it", "give me a hand",
        "give me some space", "give me everything", "give me stuff", "give me those", "drop it", "give me diamond_pickaxe",
        "give me oak log", "give me bread please", "give me bread thanks", "give me two breads", "clawd give me 3 bread",
        "give me 12 stone and then build a house", "this line is definitely far too long to be any kind of reflex command",
        "jump in the hole", "can I come?", "wait that's the bridge", "stop mining", "stop digging", "halt", "freeze", "hey stop",
        "yo come here", "real quick come here", "come here right now", "now follow me", "thanks", "ok", "maid chan look here",
    ];

    private static readonly (string Name, Func<string, bool>? IsItem)[] Checkers =
    [
        ("none", null),
        ("any", _ => true),
        ("picky", item => item is "cobblestone" or "dirt" or "logs" or "apple" or "torch" or "sand" or "seeds" or "bread" or "oak log"),
    ];

    [Fact]
    public void The_corpus_matches_the_same_reflexes()
    {
        var sb = new StringBuilder();
        foreach (var line in Corpus)
            foreach (var (name, isItem) in Checkers)
            {
                var i = IntentMatcher.Reflex(line, Names, isItem);
                var j = IntentMatcher.Reflex(line, null, isItem);
                sb.Append(line).Append(" | ").Append(name).Append(" => ").Append(i).Append(" / ").Append(j).Append('\n');
            }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "intent-reflex.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
