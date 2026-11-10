using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins who a chat line is for (WP06 refactor of <c>ChatRouter.Decide</c> / <c>Observe</c> / <c>HandleMessage</c> /
/// <c>Deliver</c>): scripted transcripts (heard lines, our lines, gaze, time) under several configs, dumping every decision
/// (reason, why, to), the queued events, what we learned (bots, nicknames) and what was sent. Hashed against
/// <see cref="Expected"/> (line-ending independent); on a mismatch the dump lands in the temp dir.
/// </summary>
public class ChatAddressingGoldenTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["asked_and_answers@default"] = "35D3D6B6C4EB59B0A0DE7274BFBDE828774FBD4B6EFEE1BB56CC5D85C1309D04",
        ["asked_and_answers@roomy"] = "E1F88076787BB5D1854CB0F60E578DD83E634215825BC67AC6EC917987166695",
        ["asked_and_answers@strict"] = "35D3D6B6C4EB59B0A0DE7274BFBDE828774FBD4B6EFEE1BB56CC5D85C1309D04",
        ["bot_detection@default"] = "0997A908FD336E6E9969C2290FCF65CF3D2183F5F26F0813D5F026394FD52C12",
        ["bot_detection@roomy"] = "A9422308CE677B4A9335E2FAA9759474E820ECE85E02345B43F12BC3CF9EBB45",
        ["bot_detection@strict"] = "0997A908FD336E6E9969C2290FCF65CF3D2183F5F26F0813D5F026394FD52C12",
        ["bot_pingpong@default"] = "A89BD08EF86E4672E9978ABAFC55DCAE93B6EC48EA60E09178CAD9740DA1B776",
        ["bot_pingpong@roomy"] = "6A0A77900606B4E6A08B3F4029F135CA45D49945AA48B0BC05E7CF2333C37EE6",
        ["bot_pingpong@strict"] = "A89BD08EF86E4672E9978ABAFC55DCAE93B6EC48EA60E09178CAD9740DA1B776",
        ["robinchan_replay@default"] = "0F7E2EC1D123664C90FE2859D9C54372FED78FD95A7ADFA1490DD83FB9985AEF",
        ["robinchan_replay@roomy"] = "4DD7572457D726E6F6F11AED62D669807FB0447812E4BB930C7736F4E569BED0",
        ["robinchan_replay@strict"] = "0F7E2EC1D123664C90FE2859D9C54372FED78FD95A7ADFA1490DD83FB9985AEF",
        ["continuity_and_requests@default"] = "65F0813EAC759C3BCCBFBCFCF17192EDD3738981B355AB46816546DAFF3987AC",
        ["continuity_and_requests@roomy"] = "0DB9771412632C3D226529DF0F4F69C8659DB8A132BAA5A0F0463849F49270A2",
        ["continuity_and_requests@strict"] = "65F0813EAC759C3BCCBFBCFCF17192EDD3738981B355AB46816546DAFF3987AC",
        ["gaze@default"] = "FD0B6F63072BE494C3738025C663B8BF831F6D46CEF2EC574D75F7493F457B28",
        ["gaze@roomy"] = "B6E40C7E476A601256D00958E5378E25935E1033114CBBD7AE09294539ADEF3D",
        ["gaze@strict"] = "FD0B6F63072BE494C3738025C663B8BF831F6D46CEF2EC574D75F7493F457B28",
        ["name_only_links@default"] = "7C305A8984957D1277D76CC5758D8A4106F0FD60DCC0DEC4D49386BA733EED2A",
        ["name_only_links@roomy"] = "C6E52FBF69AD3967D5ADCCE86343A45A74ECAC932582FC5526379C2F5D52B337",
        ["name_only_links@strict"] = "7C305A8984957D1277D76CC5758D8A4106F0FD60DCC0DEC4D49386BA733EED2A",
        ["prefix_threads@default"] = "4077F21233DF5EA1CFD1354C80C1EEEDA41E52C4F77C0C8157B1DB09E2B1D7C2",
        ["prefix_threads@roomy"] = "D924B7CC3715DF3819FF816BCE134AECBD337D08F1946404A21DEF320A6C34F6",
        ["prefix_threads@strict"] = "4077F21233DF5EA1CFD1354C80C1EEEDA41E52C4F77C0C8157B1DB09E2B1D7C2",
        ["rejection@default"] = "FFA310469B4724E19E04BC0B805BC174F3CA4A64D27A5BE6F5E38398CA472ED2",
        ["rejection@roomy"] = "8F847749EA6AB95247C3EC2368DBBC4A101FD333B58CA6B77CA5911AD44CEB97",
        ["rejection@strict"] = "AD539F9D20298CD792119D1875E42912BA2D4620B030F8A91DEBC703475D77FF",
        ["solo_and_whispers@default"] = "7B2E1900259F19BE72CA1F662952A699E8FBC6FF35083BF0F34830CF025910A4",
        ["solo_and_whispers@roomy"] = "48EDFAAC9EC06B4188E7E4B48EBC63597E3E3DF420E59320684E20B22F6CFCCA",
        ["solo_and_whispers@strict"] = "E2D26C0E0D134A88A2E399470FABB8BD695436A41024DBBDE485831BC450FE6D",
    };

    /// <summary>
    /// Transcript DSL, one step per string: "&lt;name&gt; text" or any raw server line heard 2 s later; "+N raw" heard N s
    /// later; "say p1,p2: text" our line to those players; "look p=q" / "look -" gaze; "online a,b" the player list;
    /// "drop on|off" background chat; "regular p" marks a trusted regular.
    /// </summary>
    private static readonly (string Name, string[] Steps)[] Transcripts =
    [
        ("robinchan_replay",
        [
            "<kiwi> robinchan?", "+1.1 <robinchan> uwu yes kiwi-sama!! what do u need? *hops*", "+12 <kiwi> I'm robin-sama to you",
            "+68 <kiwi> maidchan I'm not talking to you", "+13 <kiwi> robinchan, can you do anything",
            "+0.9 <robinchan> uwu yes robinsama!! i can help u and foxy get that skibidi rizz on this island!! <3",
            "+19 <kiwi> robinchan, I'm gonna call you c from now on", "+0.9 <robinchan> uwu oki!! c is such a sigma name!! i love it robinsama!! <3",
            "+3.5 <kiwi> c follow me", "+120 <kiwi> c follow me",
        ]),
        ("gaze",
        [
            "<robinchan> uwu hiii everyone!! <3", "look kiwi=maid_chan", "<kiwi> follow me", "look kiwi=robinchan", "+5 <kiwi> follow me",
            "look -", "+5 <kiwi> follow me",
        ]),
        ("asked_and_answers",
        [
            "<kiwi> maidchan hiii", "say kiwi: hiii robin~", "+8 <kiwi> robinchan?", "+1 <robinchan> uwu yes robinsama!! what do u need?",
            "+6 <kiwi> you have to respond to everything currently?", "+30 <robinchan> uwu i love this island!! <3",
            "say kiwi: robin, want me to grab the wheat for you?", "+4 <kiwi> yeah do it", "+3 <kiwi> and the seeds",
        ]),
        ("rejection",
        [
            "online kiwi", "<kiwi> maidchan I'm not talking to you", "+5 <kiwi> come here", "+5 <kiwi> maidchan come here",
            "+5 <kiwi> hello? i'm not talking to you", "+61 <kiwi> come here", "+5 <kiwi> everyone come here",
        ]),
        ("bot_pingpong",
        [
            "<PoofimaFox> maidchan say hi to robinchan", "say robinchan: hiii robinchan~", "+1 <robinchan> omg maid_chan is literally hitting the griddy uwu!! <3",
            "+3 <robinchan> maid_chan omg ur so cute!! <3", "+360 <robinchan> maid_chan come play uwu", "+1 <PoofimaFox> maidchan hi",
            "+1 <robinchan> omg maid_chan the rizz is contagious uwu", "say robinchan: omg robinchan hiii", "+1 <robinchan> maid_chan hehe ur the best!! <3",
            "+1 [robinchan -> me] psst maid_chan", "+1 * robinchan waves at maid_chan",
        ]),
        ("bot_detection",
        [
            "online PoofimaFox,robo,helperbot", "<PoofimaFox> robo is a bot btw", "+5 <PoofimaFox> helperbot what's up",
            "+1.2 <helperbot> hello there!! i am happy to help with anything", "+5 <PoofimaFox> ok cool",
            "+0.8 <helperbot> great!! let me know if you need anything else", "+3 <PoofimaFox> anyone wanna help", "+3 <robo> I am a bot",
            "+3 <PoofimaFox> grab some wood", "+60 <PoofimaFox> grab some wood",
        ]),
        ("name_only_links",
        [
            "<robinchan> uwu i love this island!! <3", "+20 <PoofimaFox> dump your stuff into a chest", "+3 <PoofimaFox> maidchan",
            "+45 <PoofimaFox> dump your stuff into a chest", "+45 <PoofimaFox> maidchan", "+2 <PoofimaFox> hey maid?",
        ]),
        ("prefix_threads",
        [
            "<kiwi> c help", "+0.3 <robinchan> i can: make me a stone pickaxe, get me 20 cobble/some wood, chop trees, mine cobble, harvest wheat,",
            "+12 <kiwi> c follow", "+0.3 <robinchan> following u kiwi!! say stop when u want me to stay", "+3000 <kiwi> maid_chan don't loose it",
            "say kiwi: I'll be careful with it robin-sama, I'll stay away from the edges uwu", "+20 <kiwi> c trees",
            "+0.7 <robinchan> uwu i got u robinsama!! chopping these trees with skibidi rizz!!", "+17 <kiwi> get an axe, cut them down like real players",
            "+15 <kiwi> maidchan go back to what you were doing", "+5 <kiwi> and some sticks", "+5 <kiwi> help im falling",
            "+30 <kiwi> c stop", "+0.4 <robinchan> ok stopping!!", "+8 <kiwi> now come back", "+4 <kiwi> say c help for its list",
        ]),
        ("continuity_and_requests",
        [
            "<kiwi> maid_chan come here", "+0.5 <robinchan> uwu hiii everyone!! skibidi!!", "say kiwi: omw robin-sama~",
            "+5 <kiwi> and bring some wood", "say PoofimaFox: fox can you hop off the blocks pls", "+30 <PoofimaFox> I hopped off it",
            "+200 <PoofimaFox> what now", "+100 <kiwi> lol", "+2 <PoofimaFox> kiwi look at this", "+3 <kiwi> wow",
            "+3 <kiwi> i'm gonna call you bun", "+3 <PoofimaFox> call me foxy", "+3 <kiwi> foxy come here", "+3 <PoofimaFox> ok",
        ]),
        ("solo_and_whispers",
        [
            "online PoofimaFox", "<PoofimaFox> what's up", "+2 [PoofimaFox -> me] get me 10 cobble", "+2 PoofimaFox whispers to you: and sand",
            "+2 * PoofimaFox waves", "+2 * PoofimaFox hugs maid_chan", "+2 <PoofimaFox> you there?", "+2 Usage: /tp <player>",
            "+2 kiwi joined the game", "+2 <kiwi> hi everyone", "+2 <PoofimaFox> brb", "+2 kiwi left the game", "+2 <PoofimaFox> ok back",
            "drop on", "+2 <PoofimaFox> nice weather", "+2 * PoofimaFox yawns", "drop off", "regular PoofimaFox",
            "+2 <PoofimaFox> robinchan answers to b", "+2 <PoofimaFox> b help",
        ]),
    ];

    private static readonly (string Name, Action<ChatOptions> Configure)[] Configs =
    [
        ("default", _ => { }),
        ("strict", o => { o.ImperativesAddressed = false; o.SoloPlayerAlwaysAddressed = false; o.SmallGroupSize = 1; }),
        ("roomy", o => { o.SmallGroupSize = 6; o.OtherBots = []; o.BotReplyCooldownMinutes = 0; o.OtherConversationSeconds = 10; o.ReplyWindowSeconds = 20; }),
    ];

    private static string Run(string[] steps, Action<ChatOptions> configure)
    {
        var time = new ManualTime { Now = new DateTimeOffset(2026, 10, 27, 23, 35, 0, TimeSpan.Zero) };
        var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid-chan", Aliases = ["maid", "maidchan"], LineDelayMs = 0 }.Regulars();
        o.Classifier.Enabled = false;
        configure(o);
        var queue = new EventQueue(new EventQueueOptions(), time);
        var sent = new List<string>();
        var router = new ChatRouter(o, queue, (l, _) => { lock (sent) sent.Add(l); return Task.CompletedTask; }, time);
        router.SetOnlinePlayers(["PoofimaFox", "kiwi", "robinchan"]);
        var regulars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        router.IsRegular = p => regulars.Contains(p);
        var sb = new StringBuilder();
        router.AddressDecided += (m, d) => sb.Append("  ").Append(m.Kind).Append(' ').Append(m.Player).Append(": ").Append(d.Reason)
            .Append(" | ").Append(d.Why).Append(" | to=").Append(d.To ?? "-").Append(" addressed=").Append(d.Addressed).Append('\n');
        router.BotDetected += (n, why) => sb.Append("  bot ").Append(n).Append(": ").Append(why).Append('\n');
        foreach (var step in steps)
        {
            sb.Append(step).Append('\n');
            if (step.StartsWith("say ", StringComparison.Ordinal))
            {
                var colon = step.IndexOf(':', StringComparison.Ordinal);
                var to = step[4..colon].Split(',');
                router.SayAsync(step[(colon + 1)..].Trim(), null, to).GetAwaiter().GetResult();
            }
            else if (step.StartsWith("look ", StringComparison.Ordinal))
            {
                var spec = step[5..];
                if (spec == "-") router.LookingAt = null;
                else
                {
                    var (p, q) = (spec.Split('=')[0], spec.Split('=')[1]);
                    router.LookingAt = x => x.Equals(p, StringComparison.OrdinalIgnoreCase) ? q : null;
                }
            }
            else if (step.StartsWith("online ", StringComparison.Ordinal)) router.SetOnlinePlayers(step[7..].Split(','));
            else if (step.StartsWith("drop ", StringComparison.Ordinal)) router.DropBackgroundChat = step == "drop on";
            else if (step.StartsWith("regular ", StringComparison.Ordinal)) regulars.Add(step[8..]);
            else
            {
                double after = 2;
                var raw = step;
                if (step.StartsWith('+'))
                {
                    var sp = step.IndexOf(' ', StringComparison.Ordinal);
                    after = double.Parse(step[1..sp], System.Globalization.CultureInfo.InvariantCulture);
                    raw = step[(sp + 1)..];
                }
                time.Advance(TimeSpan.FromSeconds(after));
                router.OnServerChat(raw);
            }
        }
        sb.Append("-- queue\n");
        foreach (var e in queue.Snapshot())
            sb.Append("  ").Append(e.Kind).Append(' ').Append(e.Player ?? "-").Append(" wakes=").Append(e.Wakes).Append(" prio=").Append(e.Priority)
              .Append(" conf=").Append(e.AddressConfidence?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append(' ').Append(e.Text).Append('\n');
        sb.Append("-- sent\n");
        lock (sent) foreach (var s in sent) sb.Append("  ").Append(s).Append('\n');
        sb.Append("-- learned\n");
        foreach (var p in new[] { "PoofimaFox", "kiwi", "robinchan", "robo", "helperbot", "maid_chan" })
            sb.Append("  ").Append(p).Append(" bot=").Append(router.IsOtherBot(p)).Append(" nicks=").Append(string.Join(",", router.NicknamesOf(p))).Append('\n');
        sb.Append("  self nicks=").Append(string.Join(",", router.LearnedSelfNicknames.Order(StringComparer.Ordinal))).Append('\n');
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> Cases() =>
        from t in Transcripts from c in Configs select new object[] { t.Name, c.Name };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Addressing_decisions_are_pinned(string transcript, string config)
    {
        var steps = Transcripts.Single(t => t.Name == transcript).Steps;
        var cfg = Configs.Single(c => c.Name == config).Configure;
        var dump = Run(steps, cfg);
        Assert.Equal(dump, Run(steps, cfg)); // deterministic
        var name = transcript + "@" + config;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden", "chat-addressing");
        if (!Expected.TryGetValue(name, out var want) || want != hash)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), dump);
        }
        Assert.True(Expected.TryGetValue(name, out want), $"no golden for {name}: [\"{name}\"] = \"{hash}\",");
        Assert.True(want == hash, $"{name} decisions changed: {hash} (dump in {dir})");
    }
}
