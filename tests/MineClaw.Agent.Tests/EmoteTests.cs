using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

public class EmoteTests
{
    private static (ChatRouter Chat, EventQueue Events, List<string> Sent) Router()
    {
        var events = new EventQueue(new EventQueueOptions { CoalesceWindowMs = 5, MaxBatchDelayMs = 20 });
        var sent = new List<string>();
        var chat = new ChatRouter(new ChatOptions { Username = "maid_chan", DisplayName = "claudechan", Aliases = ["claude"], LineDelayMs = 0 },
            events, (l, _) => { lock (sent) sent.Add(l); return Task.CompletedTask; });
        chat.SetOnlinePlayers(["kiwi", "poofimafox"]);
        return (chat, events, sent);
    }

    private static async Task<EventBatch> Next(EventQueue q) => await q.WaitBatchAsync(new CancellationTokenSource(30000).Token);

    [Fact]
    public async Task Emote_FromPlayer_AddressedWhenItMentionsTheBot()
    {
        var (chat, events, _) = Router();
        var msg = chat.OnServerChat("* kiwi pats claudechan");
        Assert.Equal(ChatKind.Emote, msg.Kind);
        Assert.Equal("kiwi", msg.Player);
        Assert.Equal("pats claudechan", msg.Text);

        var ev = Assert.Single((await Next(events)).Events);
        Assert.Equal(EventKind.Emote, ev.Kind);
        Assert.True(ev.Wakes);
        Assert.Equal("[emote] kiwi pats claudechan", ev.Format());
    }

    [Fact]
    public void Emote_NotMentioningBot_IsContextOnly_AndNeverASystemEvent()
    {
        var (chat, events, _) = Router();
        var msg = chat.OnServerChat("* kiwi fell out of the world");
        Assert.Equal(ChatKind.Emote, msg.Kind);         // kiwi emoting, not a death message
        Assert.Null(events.TryTakeUrgent(EventPriority.Low));
        var e = GameEvent.Emote("kiwi", "fell out of the world", addressed: false);
        Assert.False(e.Wakes);
        Assert.Equal("[emote] kiwi fell out of the world", e.Format());
    }

    [Fact]
    public void OwnEmoteEcho_IsIgnored()
    {
        var (chat, events, _) = Router();
        chat.OnServerChat("* maid_chan curtsies at claudechan");
        Assert.Null(events.TryTakeUrgent(EventPriority.Background));
    }

    [Fact]
    public async Task Outgoing_MeLinesAreEmotes_OtherCommandsAreBlocked()
    {
        var (chat, _, sent) = Router();
        var blocked = new List<string>();
        chat.CommandBlocked += blocked.Add;

        var said = await chat.SayAsync("hii robin\n/me does a happy little spin\n/op kiwi\n/give maid_chan diamond 64\nok bye");
        Assert.Equal(["hii robin", "/me does a happy little spin", "ok bye"], sent);
        Assert.Equal(sent, said);
        Assert.Equal(["/op kiwi", "/give maid_chan diamond 64"], blocked);

        // Emotes are always public, even when replying to a whisper.
        sent.Clear();
        await chat.SayAsync("/me curtsies\nsure thing", whisperTo: "kiwi");
        Assert.Equal(["/me curtsies", "/tell kiwi sure thing"], sent);

        // A lone command produces nothing at all.
        sent.Clear();
        Assert.Empty(await chat.SayAsync("/kill @a"));
        Assert.Empty(sent);
    }

    [Fact]
    public void Emotes_FlowIntoEpisodeSummaries()
    {
        var e = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
        e.Messages.Add(LlmMessage.User("pos 1,64,1 hp 20\n[emote] kiwi pats claudechan"));
        e.Messages.Add(LlmMessage.Assistant("/me purrs"));
        var flat = ContextManager.Flatten(e, 100);
        Assert.Contains("[emote] kiwi pats claudechan", flat);
        Assert.Contains("you: /me purrs", flat);
    }
}
