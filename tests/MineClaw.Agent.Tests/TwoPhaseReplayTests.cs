using System.Text.RegularExpressions;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Telemetry;
using Xunit.Abstractions;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Before/after replay of the live session of 2026-10-27 (mineclaw-20261027.log from 14:05: "chat: &lt;Player&gt; ..." →
/// "call opus ... round N" → "chat: &lt;maid_chan&gt; ..."), all times relative to the player's first line. "before" = what the
/// log shows (whole message sent after each round). "after" = two-phase replies on the same turns with simulated latencies:
/// Opus round = 1.2 s to first token + out/70 tok/s, text written first at ~250 chars/s, a streamed line out when its first
/// sentence is complete; Haiku (ack / cheap-only reply) 0.9 s; ack due at 2.0 s; cheap-only decided by the real
/// <see cref="QuickReply.Classify"/>; reflex lines get no ack.
/// </summary>
public class TwoPhaseReplayTests(ITestOutputHelper output)
{
    private sealed record Turn(string[] Lines, (double End, int Out)[] Rounds, (double At, int Round, string Text)[] Said);

    private const double Ttft = 1.2, TokPerSec = 70, CharsPerSec = 250, Haiku = 0.9, AckDue = 2.0, Debounce = 1.2;
    private static readonly string[] Names = ["maid_chan", "maid chan", "maidchan", "maid"];

    private static readonly Turn[] Turns =
    [
        new(["come here"], [(4.399, 84), (5.757, 25)], [(5.793, 1, "On my way master fox uwu, also i'm starving lol, got any food?")]),
        new(["go work the tree farm, get a pickaxe"], [(6.81, 535), (10.454, 37)], [(10.513, 1, "hai master, chopping the tree farm now uwu, i already got 2 stone pickaxes so i'm good")]),
        new(["MAID CHAN"], [(3.629, 43)], [(3.651, 0, "ROBIN hiii uwu, maid-chan reporting for tree duty, also lowkey starving rn, you got any food?")]),
        new(["DOES IT LOOK LIKE WE HAVE FOOD?"], [(2.29, 45)], [(2.341, 0, "lmaooo fair, no cap we're all broke, i'll hunt for apples while chopping and we can farm some wheat for bread later")]),
        new(["good girls"], [(3.495, 15)], [(3.535, 0, "/me curtsies with the axe uwu")]),
        new(["maidchan here"], [(4.621, 105)], [(4.636, 0, "made it robin uwu, glad master fox survived, ngl fox that rotten flesh lowkey looks like dinner to me rn, could i have one? i'm at 0 food lo")]),
        new(["come here follow"], [(3.598, 75), (5.852, 15)], [(5.886, 1, "following you robin, lead the way uwu")]),
        new(["you know you can shift off stairways", "so that doesn't help"], [(6.557, 82)], [(6.588, 0, "oh true, sneak just stops you falling off edges, it doesn't help on stairs lol, noted robin")]),
        new(["go back up, there's not a way up here in the house"], [(22.806, 30), (24.902, 129), (28.23, 40)], [(28.28, 2, "I'm following robin around again. I stopped for a second by mistake and started right back up. Still at 0 food and half health.")]),
        new(["69?", "hello? maid 69?!"], [(21.945, 130), (23.169, 25), (24.805, 52)], [(24.847, 2, "haii master, i don't have a bucket and none of the chests i know have one, where's the bucket at? otherwise i'm heading back to chopping tre")]),
        new(["okay maid go bring me a bucket"], [(6.091, 318), (8.835, 19)], [(8.881, 1, "on it master, checking the chests for a bucket uwu")]),
        new(["and have clone 69 harvest and replant trees"], [(5.258, 291), (6.905, 31)], [(6.937, 1, "got the bucket, bringing it to you master, and clone 69 is on tree duty uwu")]),
        new(["spawn 67 to be on tool duty, whenever u or i need tools it crafts"], [(6.395, 387), (7.709, 33)], [(7.742, 1, "got it master, 67 is spawning in for tool duty and will hang out by the crafting table uwu")]),
        new(["maidchan check your inv", "see what I gave you?", "maid watch me put water in this wehat field here"], [(4.93, 26), (6.843, 137), (22.255, 45)], [(6.914, 1, "PoofimaFox, show me! tell me when you're done"), (22.264, 2, "I'm watching, master. And thanks for the wooden hoe, robin! Want me to till the dirt for the wheat field with it?")]),
        new(["never, ever, till grass, any of you"], [(4.608, 151), (6.159, 34)], [(6.187, 1, "Got it, robin, no tilling grass ever, for me or my clones. I'll remember that one.")]),
        new(["maidchan wyd", "go listen to foxy"], [(3.708, 58)], [(3.735, 0, "Just chillin by the trees, robin. 69's on tree duty and I'm holding a bunch of logs and saplings. Still starving lol, want me to help with t")]),
        new(["I want you to finish filling the water in the farm like I showed you"], [(6.439, 151), (15.588, 761), (18.534, 23)], [(18.547, 2, "On it master, filling the rest of the farm with water like you showed me.")]),
        new(["GIV ME THE FREAKIN BUCKET", "MAID!"], [(18.614, 110), (20.666, 55)], [(20.697, 1, "I gave you the bucket back, master, sorry I took so long. I lost track of where the infinite pool you showed me is. Point at it again and I'")]),
        new(["maid why are you not farming trees, arer you confused who I meant?"], [(2.979, 266), (5.746, 38)], [(5.786, 1, "Sorry master, I got mixed up, I thought you meant clone 69. Heading out to chop the trees now.")]),
        new(["You shoul do it like this, cut wood, replant, then cut leaves, then next tree, repeat"], [(5.15, 279), (6.463, 39)], [(6.487, 1, "Got it master: cut the wood, replant, clear the leaves, then on to the next tree. Doing it that way now.")]),
        new(["mine down on the wood"], [(1.855, 92)], [(1.897, 0, "robin you ok? i saw you taking damage up there. also foxy told me to clear the leaves too, should i still skip them?")]),
        new(["maid bring me 64 cobbl and 20 cobble slabs"], [(7.493, 578), (12.855, 438), (15.739, 28)], [(15.779, 2, "Yes master, getting you 64 cobble and 20 cobble slabs now.")]),

    ];

    private static double Start(Turn t, int k)
    {
        double prev = k == 0 ? Debounce : t.Rounds[k - 1].End;
        return Math.Max(prev, t.Rounds[k].End - (Ttft + t.Rounds[k].Out / TokPerSec));
    }

    private static int FirstSentence(string text)
    {
        var m = Regex.Match(text, @"[.!?~]+(?=\s)");
        return m.Success ? m.Index + m.Length : text.Length;
    }

    /// <summary>When a line written first in round k is complete on the stream (never later than the log shows it).</summary>
    private static double Streamed(Turn t, (double At, int Round, string Text) u) =>
        Math.Min(u.At, Start(t, u.Round) + Ttft + FirstSentence(u.Text) / CharsPerSec);

    [Fact]
    public void Replay_first_chat_and_conclusive_before_vs_after()
    {
        var before = new List<(long? First, long? Conclusive)>();
        var after = new List<(long? First, long? Conclusive)>();
        int cheap = 0, acks = 0;
        output.WriteLine($"{"line",-44} {"kind",-9} {"first b→a",-13} {"conclusive b→a",-15}");
        foreach (var t in Turns)
        {
            static long Ms(double s) => (long)Math.Round(s * 1000);
            long? bFirst = t.Said.Length > 0 ? Ms(t.Said[0].At) : null, bConc = t.Said.Length > 0 ? Ms(t.Said[^1].At) : null;
            bool reflex = t.Lines.All(l => IntentMatcher.Reflex(l, Names, _ => true).IsReflex);
            var kind = QuickReply.Classify(t.Lines, Names);
            long? aFirst, aConc;
            string tag;
            if (kind != QuickKind.Brain && !reflex)
            {
                cheap++;
                aFirst = aConc = Ms(Start(t, 0) + Haiku);
                tag = "cheap";
            }
            else
            {
                double? first = t.Said.Length > 0 ? Streamed(t, t.Said[0]) : null;
                bool ack = !reflex && (first ?? double.MaxValue) > AckDue;
                if (ack) { acks++; first = Math.Min(first ?? double.MaxValue, Math.Max(AckDue, Start(t, 0) + Haiku)); }
                aFirst = first is { } f ? Ms(f) : null;
                aConc = t.Said.Length > 0 ? Ms(Streamed(t, t.Said[^1])) : null;
                tag = reflex ? "reflex" : ack ? "ack+brain" : "brain";
            }
            before.Add((bFirst, bConc));
            after.Add((aFirst, aConc));
            static string S(long? ms) => ms is { } v ? $"{v / 1000.0:0.0}s" : "-";
            var line = string.Join(" / ", t.Lines);
            output.WriteLine($"{(line.Length > 43 ? line[..43] : line),-44} {tag,-9} {S(bFirst) + "→" + S(aFirst),-13} {S(bConc) + "→" + S(aConc),-15}");
        }
        long? P(IEnumerable<long?> v) => LatencyTracker.P50(v);
        var (bf, bc, af, ac) = (P(before.Select(x => x.First)), P(before.Select(x => x.Conclusive)), P(after.Select(x => x.First)), P(after.Select(x => x.Conclusive)));
        output.WriteLine($"p50 first_chat_ms: {bf} → {af}; p50 conclusive_ms: {bc} → {ac}; turns {Turns.Length}, cheap-only {cheap}, acks {acks}");

        Assert.True(af < bf);
        Assert.True(ac <= bc);
        // A turn that starts late (the brain was still busy with the previous one) acks only once it starts: the ack lives in
        // the turn, so those rows stay slow (19.8 s, 22.1 s) — the queue wait, not the model.
        Assert.True(after.Count(a => a.First <= 3000) >= after.Count / 2);
        Assert.InRange(cheap, 1, Turns.Length / 3); // small talk only
    }
}
