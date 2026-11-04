using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Brainstem;

namespace MineClaw.Agent.Tests;

/// <summary>Pins <see cref="ChatIntent.Classify"/> (WP06 refactor) over a corpus of chat lines, as chat and as emotes.</summary>
public class ChatIntentGoldenTests
{
    private const string Expected = "8F1509B55ACF3BA739B356AD87584B846D65C3132E8CAD86CF5EBBECDA065493";

    private static readonly string[] Corpus =
    [
        "", "  ", "help!", "maid help me a zombie is on me", "zombies chasing me", "stop", "come here", "follow me", "give me 5 dirt",
        "look at me", "jump", "hi", "hiii maid", "hello everyone", "gn everyone", "bye bye", "cya later", "brb", "thanks!", "ty maid",
        "good job maid", "ily <3", "you're so dumb", "stfu", "what are you doing", "wyd", "how are you?", "where are you",
        "grab some cobble and come here", "mine the cobble gen", "chop some wood", "bring me 10 sand", "go to the farm",
        "can you grab cobble?", "could you please build a wall", "where did you put the wheat?", "is it night?", "any diamonds?",
        "lol", "lmao", "ok", "yes", "nice", "you should build a farm", "you need to sleep", "the farm looks great",
        "maid craft a furnace then smelt the iron", "clawd, sort the chest", "I think we need more torches", "@maid hi",
        "maid", "MAID CHAN", "o/", "welcome back", "i'm back", "hate you", "wave hi", "please help me", "help me build", "hello maid chan!!",
    ];

    [Fact]
    public void The_corpus_classifies_the_same()
    {
        var sb = new StringBuilder();
        string[] names = ["maid", "maid chan", "clawd"];
        foreach (var line in Corpus)
        {
            sb.Append(line).Append(" => ").Append(ChatIntent.Classify(line, names)).Append(" / ").Append(ChatIntent.Classify(line, names, emote: true))
              .Append(" / ").Append(ChatIntent.Classify(line)).Append('\n');
        }
        sb.Append(ChatIntent.Classify(null)).Append('\n');
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "chat-intent.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
