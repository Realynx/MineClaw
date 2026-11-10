using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>User requirement 10-27: clones never re-ask a player something main (or another clone) already asked.</summary>
public class AskedBeforeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 21, 50, 0, TimeSpan.Zero);
    private static bool Hive(string n) => n.StartsWith("maid_chan", StringComparison.OrdinalIgnoreCase);

    private static void Log(AgentDb db, DateTimeOffset at, string kind, string player, string text) =>
        db.Exec("INSERT INTO chat_log(at, kind, player, text) VALUES ($a, $k, $p, $t)",
            ("$a", AgentDb.ToUnixMs(at)), ("$k", kind), ("$p", player), ("$t", text));

    [Fact]
    public void Same_question_by_another_hive_bot_is_found_with_its_answer()
    {
        using var db = AgentDb.Open(null);
        Log(db, T0, "bot", "maid_chan", "PoofimaFox, which tree should I chop first, the big oak or the small one?");
        Log(db, T0.AddSeconds(20), "chat", "PoofimaFox", "the big oak");
        var hit = AskedBefore.Find(db, "maid_chan_67", Hive, "Fox, should I chop the big oak tree first or the small one?", "PoofimaFox", T0.AddMinutes(2));
        Assert.NotNull(hit);
        Assert.StartsWith("maid_chan already asked PoofimaFox", hit);
        Assert.Contains("answer: \"the big oak\"", hit);
    }

    [Fact]
    public void Own_questions_other_topics_old_asks_and_players_are_not_dedupe_hits()
    {
        using var db = AgentDb.Open(null);
        Log(db, T0, "bot", "maid_chan_67", "which tree should I chop first, the big oak or the small one?");
        Log(db, T0, "bot", "maid_chan", "do you want the torches in the chest?");
        Log(db, T0, "chat", "PoofimaFox", "which tree should I chop first, the big oak or the small one?");
        Assert.Null(AskedBefore.Find(db, "maid_chan_67", Hive, "which tree should I chop first, the big oak or the small one?", null, T0.AddMinutes(1)));
        Log(db, T0, "bot", "maid_chan", "should I chop the big oak first?");
        Assert.Null(AskedBefore.Find(db, "maid_chan_67", Hive, "should I chop the big oak first?", null, T0.AddMinutes(11))); // outside 10 min
        Assert.NotNull(AskedBefore.Find(db, "maid_chan_67", Hive, "should I chop the big oak first?", null, T0.AddMinutes(9)));
        Assert.False(AskedBefore.Same("want me to mine cobble?", "want me to chop trees?"));
    }

    [Fact]
    public async Task Ask_player_does_not_ask_twice()
    {
        var sent = new List<string>();
        var q = new EventQueue(new EventQueueOptions(), TimeProvider.System);
        var chat = new ChatRouter(new ChatOptions { Username = "maid_chan_67", LineDelayMs = 0 }.Regulars(), q, (l, _) => { lock (sent) sent.Add(l); return Task.CompletedTask; });
        var tools = new ToolRegistry();
        HumanTools.RegisterAskPlayer(tools, chat, new QuestionOptions(), q, askedBefore: (question, player) =>
            question.Contains("oak") ? "maid_chan already asked PoofimaFox \"which tree first?\" 1m ago; answer: \"the big oak\"" : null);
        var r = await tools.ExecuteAsync("ask_player", JsonSerializer.SerializeToElement(new
        {
            question = "which tree first, the big oak or the small one?", name = "PoofimaFox", @default = "the big oak",
        }), CancellationToken.None);
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("not asked again: maid_chan already asked PoofimaFox", r.Text);
        Assert.Contains("go ahead with \"the big oak\"", r.Text);
        lock (sent) Assert.Empty(sent);
    }
}
