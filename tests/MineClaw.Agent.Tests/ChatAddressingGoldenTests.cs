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
        ["asked_and_answers@default"] = "9D214ABA6B1AF0D9EF62BADA82B9CF381F912B5B7CF9DB2D57545CE75FF10DCA",
        ["asked_and_answers@roomy"] = "0B760C0D995CAD204992866CDDDE0C8C2C56063F5970ADEE24EE69B0B3A43468",
        ["asked_and_answers@strict"] = "9D214ABA6B1AF0D9EF62BADA82B9CF381F912B5B7CF9DB2D57545CE75FF10DCA",
        ["bot_detection@default"] = "5AC3EF93228A7618FBEDC2C2FDEB48BAF635890F023C3A6E104FB63A3EDD308F",
        ["bot_detection@roomy"] = "893F8F8290D1ADBC9099CDC8EDB3F2015592802FB11F36732CA7E79BABA8FB16",
        ["bot_detection@strict"] = "5AC3EF93228A7618FBEDC2C2FDEB48BAF635890F023C3A6E104FB63A3EDD308F",
        ["bot_pingpong@default"] = "D92097450D450F1639BA196BB46115FC64AA5FE93C9701B2AD3FEEA15A5C7021",
        ["bot_pingpong@roomy"] = "797B943CD42A4AE5AD376E74BA1B468C11378789795E69435AB21EDBB6A40D91",
        ["bot_pingpong@strict"] = "D92097450D450F1639BA196BB46115FC64AA5FE93C9701B2AD3FEEA15A5C7021",
        ["robinchan_replay@default"] = "886799A091CB49A1BA4AB4BD83FF629357A3316C147F038B141499F633E6DE91",
        ["robinchan_replay@roomy"] = "534EC51BC11B3BDD32ED2C0237DB04810BAB26FA252011F23D0C0647EB6FFA6F",
        ["robinchan_replay@strict"] = "886799A091CB49A1BA4AB4BD83FF629357A3316C147F038B141499F633E6DE91",
        ["continuity_and_requests@default"] = "EA868D859E45F2DC2598026FDA307584FCD8B8B2FF70498976617F7E66881FB9",
        ["continuity_and_requests@roomy"] = "D5409AB7C7939E1E818EF66C3381972C544D2D3BA294678183F10C0A0DA7C1E7",
        ["continuity_and_requests@strict"] = "EA868D859E45F2DC2598026FDA307584FCD8B8B2FF70498976617F7E66881FB9",
        ["gaze@default"] = "726735F9CC3B0EC9F607631F4A77294968688C3E8554BB9EC545A77F397D3AD4",
        ["gaze@roomy"] = "25073E2A4146A7EE8EE3AAB5368B1BAD83BCAF44884ED97DC6429A9120FFE4C6",
        ["gaze@strict"] = "726735F9CC3B0EC9F607631F4A77294968688C3E8554BB9EC545A77F397D3AD4",
        ["name_only_links@default"] = "60BA01DA027688813FAC35F43E01987152F8C39104DB0457379A5D3B5EBE043D",
        ["name_only_links@roomy"] = "51EB5D1DF4FDF45ABEE1770DF1933BAE8BB7217ECDD8AC25A9D13DBD4AE9EB55",
        ["name_only_links@strict"] = "60BA01DA027688813FAC35F43E01987152F8C39104DB0457379A5D3B5EBE043D",
        ["prefix_threads@default"] = "75A58A4CD75841042C57CB5E7730B20ECC331096A3696045C76ED47347533C5A",
        ["prefix_threads@roomy"] = "50505EB19E9E0F4EDE3AF998428E1ECAC51EC3E9C5BC1C934554979A0E4F9C59",
        ["prefix_threads@strict"] = "75A58A4CD75841042C57CB5E7730B20ECC331096A3696045C76ED47347533C5A",
        ["rejection@default"] = "6AC0EFC62B6A162FB3678683ADD26BB15128BF4790A0606BC27CC0E449E56001",
        ["rejection@roomy"] = "C5527F54C59A17B8BB5E144A500AB10EE4C6F8042ABE33DA04764959D1758234",
        ["rejection@strict"] = "69C173C6431B6F23B1E372473FA75E19858BAF5EF2EB51AEB60EC8EE39CE7EF0",
        ["solo_and_whispers@default"] = "20D93EB1F5D4AC31B7D96C8D8F2D90758A2A5E3B55E3195BF121C042ED886FAB",
        ["solo_and_whispers@roomy"] = "65E1ABBDB2DF54D14D87CA6BDBC8002041A3C3F0EE9527FCDF174D61C36AC186",
        ["solo_and_whispers@strict"] = "9FEBB390BA51CAD66C4E703861B7E7A71FC470241853697CC55DFDA35FC9B028",
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
        var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid-chan", Aliases = ["maid", "maidchan"], LineDelayMs = 0 };
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
