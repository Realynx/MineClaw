using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Chat;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="MetaSpeech.Strip"/> (WP06 refactor) over the speech tests' lines plus multi-sentence mixes, with and without
/// status asked and addressees: the kept text and every suppressed piece.
/// </summary>
public class MetaSpeechGoldenTests
{
    private const string Expected = "FFB122FF92B3F66E4EC1B4DEC1BA8AFD7F4CAE44EFDD633833BDA69AD7EC1230";

    private static readonly string[] Corpus =
    [
        "", "  ", "/me waves", "4,64,6 cobblestone", "hi fox!\n/me spins\n\nok",
        "I've started the job in the background: grab a stone axe and some torches from the chest, then chop the trees by the house.",
        "I've started the job in the background: ok.",
        "Fox is asking for wood, so I'll chop the big oak. Got it fox!",
        "The fourth run worked: another 32 cobble mined and stored. Want more?",
        "I didn't say anything in chat. hi robin",
        "Eating isn't something I can trigger with my current tools, so I'll stop fiddling and just thank Fox.",
        "kiwi wants sand. On it kiwi! Fox seems busy.",
        "Another 32-cobble run for Fox has started.",
        "As an AI I can't eat.",
        "Asked Fox for the size and type of the build, and waiting on his reply.",
        "Asked Fox what he wants built at the bridge edge, and I'm waiting for his answer.",
        "Better tools would help a lot!",
        "Robin is taking damage near the tree, but she's asked me to stay quiet unless spoken to, and I don't know what's hurting her, so I didn't step in.",
        "Robin was slain by a zombie at the door.",
        "Checking my notes.",
        "Clone 69 was killed by a zombie at its tree, the same one that was attacking it.",
        "Kiwi is wondering where the iron went, so let me look through my ledger.",
        "Fox is asking about the mob chamber, so I'll check memory for it.",
        "Fox is offering 64 cobble, so I'll wait for him to hand it over and ask what the plan is for.",
        "I asked robin earlier",
        "I asked robin earlier, she said the farm is north",
        "I asked you twice already, silly",
        "I can't mine obsidian with my current tools, I need a diamond pickaxe.",
        "I can't trigger that with my tools.",
        "I haven't said anything yet.",
        "I need a tool to break this faster.",
        "I picked it back up, ty!",
        "I resumed the tower since Fox already approved it.",
        "I said nothing.",
        "I started a fifth 32-cobble run.",
        "I started another 32-cobble run for Fox's standing order.",
        "I thanked robin for the bread.",
        "I told you so!! the creeper was right there",
        "I told you the cactus hurts!",
        "I warned Fox that the cactus farm is hurting him.",
        "I warned u foxy~",
        "I'll check my memory for that.",
        "I'll know when that finishes.",
        "I'll look at the map you drew on the sign!",
        "I'll run over to the gen and mine some cobble!",
        "I'll wait for the result.",
        "I'll wait for you by the chest uwu",
        "I'll walk to the west end while waiting for his answer.",
        "I'm also back on the cobble generator now: I'm mining the 24 cobble he's still owed…",
        "I'm back! got the wood",
        "I'm staying quiet.",
        "I'm waiting for you by the chest!",
        "I've delegated the craft.",
        "I've got a stone pickaxe now",
        "Let me check my map.",
        "Let me check the chest by the gen.",
        "My system prompt says no.",
        "My tools broke, can you make me a new pickaxe?",
        "My toolset doesn't cover eating.",
        "Nothing for me to do right now.",
        "Nothing woke me for a reason this turn, so I'll stay quiet.",
        "PoofimaFox, Which end of the bridge should I extend 12 blocks?",
        "Sent the worker the fix, so it will build the branch at Fox's spot instead.",
        "Since nothing is waking me up for a reason, I'll keep it short and just ask for food.",
        "That line wasn't addressed to me.",
        "That's not something I can call.",
        "The floor job is running in the background, I'll wait for its result.",
        "The last run worked: 32 more cobble mined and stored.",
        "The seeds I said I'd plant are still in my bag, so I haven't planted them yet.",
        "There's no function for that.",
        "There's nothing else for me to do.",
        "Waiting on Fox's answer before placing or removing anything else.",
        "and told him that I'd keep mining.",
        "robin run, a zombie is at the door!",
        "robin! something's hurting you by the tree",
        "can u say it in chat? i missed it",
        "fence is 6 sticks, two full rows of 3 on the crafting table, makes 2 fences uwu",
        "foxy, u ok? that creeper got u good",
        "i see the void drop off the bridge edge, what do u want me to build there Fox?",
        "maid_chan_420 finished its chop job and logged out.",
        "master saved me uwu",
        "nothing to see here uwu",
        "oki, i'll stay quiet~",
        "ooh ty Fox uwu, what's the plan tho? a wall, a platform, or a spawner pad? tell me the size and i'll get building",
        "two lil maids are chopping now~ foxy, stand on the chest spot and say here pls uwu",
        "you were killed by a zombie, ur stuff is by the door",
        "you woke me up, foxy sama",
    ];

    [Fact]
    public void The_corpus_strips_the_same()
    {
        var sb = new StringBuilder();
        foreach (var line in Corpus)
            foreach (var (asked, who) in new[] { (false, (string[]?)null), (true, null), (false, ["fox", "kiwi", "Fox", "robin"]) })
            {
                var kept = MetaSpeech.Strip(line, out var suppressed, asked, who);
                sb.Append(line.Replace("\n", "\\n")).Append(" | ").Append(asked).Append(who != null).Append(" => ").Append(kept.Replace("\n", "\\n"))
                  .Append(" -- ").Append(string.Join(" ## ", suppressed)).Append('\n');
            }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "meta-speech.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
