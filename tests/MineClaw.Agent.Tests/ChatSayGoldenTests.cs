using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="ChatRouter.SayAsync"/> and the router's inbound delivery (WP06 refactor): model text × whisper/audience ×
/// configs (line cap, the startup cap, a command gate) → the lines returned, the raw lines sent, blocked commands, and the
/// follow-up state (who we talked with, open questions) seen through a later unnamed line. Hashed against <see cref="ExpectedHash"/>.
/// </summary>
public class ChatSayGoldenTests
{
    private const string ExpectedHash = "0120CA0A3ADF782D490D06259EC97D1D86F7A491A3C11549B064CA2425391FD0";

    private static readonly string[] Texts =
    [
        "", "   ", "hi fox", "hi fox\nand robin", "/me waves", "ok /me hops around", "/tp fox 1 2 3", "/tell kiwi psst",
        "line one\r\n/me jumps\nline two", new string('a', 250), "word " + string.Join(" ", Enumerable.Repeat("cobblestone", 40)),
        "§cred §ftext", "fox, can you grab the wheat?", "kiwi want some bread? i have food", "hi kiwi! welcome back",
        "robin run to the chest", "watch out fox, a creeper is behind you!", "/me " + new string('b', 120), "a\n\n\nb\nc\nd\ne",
        "please hop off the blocks", "Did you mean the west gen?",
    ];

    private static readonly (string Name, string? Whisper, string[]? Audience)[] Targets =
    [
        ("public", null, null), ("whisper", "kiwi", null), ("audience", null, ["PoofimaFox"]), ("both", "kiwi", ["PoofimaFox", "kiwi"]),
        ("empty-audience", null, []),
    ];

    private static readonly (string Name, Action<ChatOptions> Configure, bool Session, bool Gate)[] Configs =
    [
        ("default", _ => { }, false, false),
        ("cap1-gate", o => o.MaxLinesPerReply = 1, false, true),
        ("startup", o => o.StartupMaxLines = 2, true, true),
    ];

    private static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (cname, configure, session, gate) in Configs)
            foreach (var (tname, whisper, audience) in Targets)
            {
                sb.Append("#### ").Append(cname).Append(' ').Append(tname).Append('\n');
                var time = new ManualTime { Now = new DateTimeOffset(2026, 10, 27, 23, 35, 0, TimeSpan.Zero) };
                var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid-chan", Aliases = ["maid", "maidchan"], LineDelayMs = 0 };
                o.Classifier.Enabled = false;
                configure(o);
                var queue = new EventQueue(new EventQueueOptions(), time);
                var sent = new List<string>();
                var router = new ChatRouter(o, queue, (l, _) => { lock (sent) sent.Add(l); return Task.CompletedTask; }, time);
                router.SetOnlinePlayers(["PoofimaFox", "kiwi"]);
                if (gate) router.Commands = new CommandGate(new CommandOptions(), () => ["PoofimaFox"], time);
                if (session) router.BeginSession();
                router.CommandBlocked += l => sb.Append("  blocked ").Append(l).Append('\n');
                router.Said += l => sb.Append("  said-event ").Append(l.Length).Append('\n');
                router.SpokeTo += p => sb.Append("  spoke-to ").Append(p).Append('\n');
                foreach (var text in Texts)
                {
                    time.Advance(TimeSpan.FromSeconds(3));
                    sb.Append("> [").Append(text.Replace("\r", "\\r").Replace("\n", "\\n")).Append("]\n");
                    var lines = router.SayAsync(text, whisper, audience).GetAwaiter().GetResult();
                    foreach (var l in lines) sb.Append("  = ").Append(l).Append('\n');
                    lock (sent) { foreach (var s in sent) sb.Append("  raw ").Append(s).Append('\n'); sent.Clear(); }
                }
                // What the lines left behind: an unnamed follow-up from each player.
                foreach (var p in new[] { "PoofimaFox", "kiwi" })
                {
                    time.Advance(TimeSpan.FromSeconds(5));
                    var d = router.Decide(new ChatMessage(ChatKind.Public, p, "yeah ok", $"<{p}> yeah ok"));
                    sb.Append("  follow-up ").Append(p).Append(": ").Append(d.Reason).Append(" | ").Append(d.Why).Append('\n');
                }
            }
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void Say_output_is_pinned()
    {
        var dump = Dump();
        Assert.Equal(dump, Dump());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != ExpectedHash)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "chat-say.txt"), dump);
        }
        Assert.True(hash == ExpectedHash, $"say output changed: {hash}");
    }
}
