using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

public class ChatTests
{
    private static (ChatRouter Router, EventQueue Queue, List<string> Sent) Make(Action<ChatOptions>? cfg = null, TimeProvider? time = null)
    {
        var o = new ChatOptions { Username = "Clawd", DisplayName = "Clawd", Aliases = ["claw"], LineDelayMs = 0 };
        cfg?.Invoke(o);
        var q = new EventQueue();
        var sent = new List<string>();
        var r = new ChatRouter(o, q, (l, _) => { sent.Add(l); return Task.CompletedTask; }, time);
        return (r, q, sent);
    }

    [Theory]
    [InlineData("<Alice> hello there", ChatKind.Public, "Alice", "hello there")]
    [InlineData("§f<§cAlice§f> §ehello", ChatKind.Public, "Alice", "hello")]
    [InlineData("<[Admin] Bob> hi all", ChatKind.Public, "Bob", "hi all")]
    [InlineData("§7Alice whispers come here", ChatKind.Whisper, "Alice", "come here")]
    [InlineData("Alice whispers to you: secret", ChatKind.Whisper, "Alice", "secret")]
    [InlineData("[Bob -> me] hey", ChatKind.Whisper, "Bob", "hey")]
    [InlineData("§eAlice joined the game.", ChatKind.Join, "Alice", null)]
    [InlineData("Bob left the game.", ChatKind.Leave, "Bob", null)]
    public void Parse_Formats(string raw, ChatKind kind, string player, string? text)
    {
        var (r, _, _) = Make();
        var m = r.Parse(raw);
        Assert.Equal(kind, m.Kind);
        Assert.Equal(player, m.Player);
        if (text != null) Assert.Equal(text, m.Text);
    }

    [Fact]
    public void Parse_BukkitStyle_RequiresKnownPlayerWhenListKnown()
    {
        var (r, _, _) = Make();
        Assert.Equal(ChatKind.Public, r.Parse("[Member] Alice: yo").Kind); // nobody known yet → accept
        r.SetOnlinePlayers(["Alice"]);
        var m = r.Parse("[Member] Alice: yo");
        Assert.Equal(("Alice", "yo"), (m.Player, m.Text));
        Assert.Equal(ChatKind.System, r.Parse("Usage: /tell <player> <message>").Kind);
    }

    [Fact]
    public void OwnMessages_AreIgnored()
    {
        var (r, q, _) = Make();
        r.OnServerChat("<Clawd> hi everyone");
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Addressing_ByNameAliasWhisperAndGroup()
    {
        var (r, q, _) = Make();
        r.SetOnlinePlayers(["Alice", "Bob"]);
        bool A(string line) => r.IsAddressed(r.Parse(line));

        Assert.True(A("<Alice> hey clawd"));
        Assert.True(A("<Alice> CLAW, come here"));
        Assert.True(A("<Alice> @Clawd hi"));
        Assert.False(A("<Alice> clawdia is a name"));
        Assert.False(A("<Alice> bob look at this"));
        Assert.True(A("<Alice> ok everyone lets go"));
        Assert.True(A("Bob whispers hi"));
    }

    [Fact]
    public void Addressing_SoloPlayer_AndReplyWindow()
    {
        var time = new ManualTime();
        var (r, q, _) = Make(time: time);
        r.SetOnlinePlayers(["Alice"]);
        Assert.True(r.IsAddressed(r.Parse("<Alice> nice day")));

        r.SetOnlinePlayers(["Alice", "Bob"]);
        Assert.False(r.IsAddressed(r.Parse("<Alice> nice day")));
        r.OnServerChat("<Alice> clawd hi");                // starts an exchange
        Assert.True(r.IsAddressed(r.Parse("<Alice> nice day")));
        Assert.False(r.IsAddressed(r.Parse("<Alice> bob you there?"))); // talking to Bob
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(r.IsAddressed(r.Parse("<Alice> nice day")));
    }

    [Fact]
    public void NonAddressed_BecomesBackgroundContext_AndSpamIsThrottled()
    {
        var (r, q, _) = Make(o => o.PerPlayerMaxMessages = 2);
        r.SetOnlinePlayers(["Alice", "Bob"]);
        r.OnServerChat("<Alice> bob lol");
        Assert.False(q.HasWaking);
        r.OnServerChat("<Bob> clawd 1");
        r.OnServerChat("<Bob> clawd 2");
        r.OnServerChat("<Bob> clawd 3");
        var snap = q.Snapshot();
        Assert.Equal(2, snap.Count(e => e.Wakes && e.Player == "Bob"));
        Assert.Contains(snap, e => e.Text == "clawd 3" && !e.Wakes);
    }

    [Fact]
    public void Join_PostsEvent_AndUpdatesOnline()
    {
        var (r, q, _) = Make();
        r.OnServerChat("§eAlice joined the game.");
        Assert.Contains("Alice", r.OnlinePlayers);
        Assert.Equal(EventKind.PlayerJoined, q.Snapshot().Single().Kind);
    }

    [Theory]
    [InlineData("hi §cthere", "hi there")]
    [InlineData("“quoted” — dash…", "\"quoted\" - dash...")]
    [InlineData("café ő naïve", "café o naïve")]
    [InlineData("love you ❤️ \U0001F600!", "love you <3 !")]
    [InlineData("/op Alice", "op Alice")]
    [InlineData("use `code`\tand\nnewline", "use 'code' and newline")]
    [InlineData("**bold** text", "bold text")]
    public void Sanitize(string input, string expected)
    {
        var s = ChatText.Sanitize(input);
        Assert.Equal(expected, s);
        Assert.All(s, c => Assert.True(ChatText.IsAllowed(c), $"char {(int)c}"));
    }

    [Fact]
    public void Split_OnWordBoundaries_Max100()
    {
        var text = string.Join(" ", Enumerable.Range(0, 60).Select(i => "word" + i));
        var lines = ChatText.Split(text);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 100));
        Assert.Equal(text, string.Join(" ", lines));

        var huge = new string('a', 250);
        var parts = ChatText.Split("x " + huge);
        Assert.Equal(["x", new string('a', 100), new string('a', 100), new string('a', 50)], parts);
    }

    [Fact]
    public async Task Say_CapsLines_WhispersWithinLimit()
    {
        var (r, _, sent) = Make(o => o.MaxLinesPerReply = 2);
        var text = string.Join(" ", Enumerable.Range(0, 80).Select(i => "blah" + i));
        await r.SayAsync(text, whisperTo: "Alice");
        Assert.Equal(2, sent.Count);
        Assert.All(sent, l => { Assert.StartsWith("/tell Alice ", l); Assert.True(l.Length <= 100); });
        Assert.EndsWith("...", sent[^1]);
    }

    [Fact]
    public async Task Say_EchoIsIgnored()
    {
        var (r, q, _) = Make();
        await r.SayAsync("hello friends");
        r.OnServerChat("hello friends"); // server echoes without a name
        r.OnServerChat("<Somebody> hello friends");
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task WaitForReply_ConsumesPlayersNextMessage()
    {
        var (r, q, _) = Make();
        r.SetOnlinePlayers(["Alice", "Bob"]);
        var wait = r.WaitForReplyAsync("Alice", TimeSpan.FromSeconds(5), default);
        r.OnServerChat("<Bob> unrelated");
        r.OnServerChat("<Alice> it's a cobble gen");
        Assert.Equal("Alice: it's a cobble gen", await wait);
        Assert.DoesNotContain(q.Snapshot(), e => e.Player == "Alice");

        Assert.Null(await r.WaitForReplyAsync("Alice", TimeSpan.FromMilliseconds(50), default));
    }
}
