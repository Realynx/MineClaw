using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Night review 2026-10-27 (docs/reviews/2026-10-27-night-log-review.md), speech items B17, B18, B20, L3, L4. The model lines
/// are the real ones from data/alice-logs/mineclaw-20261027.log and logs/live2/.
/// </summary>
public class NightReviewSpeechTests
{
    // ---------------------------------------------------------------- B17 / L4: wake and background-job narration

    [Theory]
    [InlineData("Since nothing is waking me up for a reason, I'll keep it short and just ask for food.")]        // alice 07:52:46
    [InlineData("The floor job is running in the background, I'll wait for its result.")]                        // live2 69 01:11:52
    [InlineData("Nothing woke me for a reason this turn, so I'll stay quiet.")]
    [InlineData("I'll wait for the result.")]
    [InlineData("I've delegated the craft.")]
    public void B17_L4_turn_and_background_plumbing_never_reaches_chat(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var dropped));
        Assert.Single(dropped);
    }

    [Fact]
    public void B17_the_real_part_of_the_reply_stays()
    {
        var said = MetaSpeech.Strip("Since nothing is waking me up for a reason, I'll keep it short and just ask for food. maid-chan is " +
            "starving fr, belly at zero and hp stuck at half... foxy, robin, any apples or bread for your poor maid?", out _);
        Assert.StartsWith("maid-chan is starving fr", said);
    }

    [Fact]
    public void L4_a_job_announcement_keeps_what_the_job_is()
    {
        // live2 67 01:07:29
        var said = MetaSpeech.Strip("I've started the job in the background: grab a stone axe and some torches from the chest, then chop the trees by the house.", out var dropped);
        Assert.Equal("on it: grab a stone axe and some torches from the chest, then chop the trees by the house.", said);
        Assert.Equal(["I've started the job in the background:"], dropped);
    }

    [Theory]
    [InlineData("I'll wait for you by the chest uwu")]
    [InlineData("my turn to mine, foxy!")]
    [InlineData("you woke me up, foxy sama")]
    [InlineData("I've got a stone pickaxe now")]
    public void B17_L4_real_speech_is_kept(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));

    // ---------------------------------------------------------------- B18: inline /me

    [Fact]
    public async Task B18_an_inline_me_is_sent_as_its_own_emote_line()
    {
        var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid chan", LineDelayMs = 0, MaxLinesPerReply = 5 };
        var sent = new List<string>();
        var r = new ChatRouter(o, new EventQueue(), (l, _) => { sent.Add(l); return Task.CompletedTask; });
        // alice 07:47:57: "<maid_chan> Hai, foxy sama~ /me curtsies. Your maid has chopped…"
        await r.SayAsync("Hai, foxy sama~ /me curtsies. Your maid has chopped 3 trees and replanted them all uwu");
        Assert.Equal(["Hai, foxy sama~", "/me curtsies.", "Your maid has chopped 3 trees and replanted them all uwu"], sent);

        sent.Clear();
        await r.SayAsync("/me curtsies deeply"); // a leading /me is unchanged
        Assert.Equal(["/me curtsies deeply"], sent);
        sent.Clear();
        await r.SayAsync("ok foxy /me bows");    // no sentence end: the rest of the line is the emote
        Assert.Equal(["ok foxy", "/me bows"], sent);
    }

    // ---------------------------------------------------------------- L3: raw coordinates in the model's own sentences

    [Fact]
    public async Task L3_the_brains_own_coordinates_are_said_as_landmarks()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        rt.Chat.Landmark = (x, y, z) => (x, y, z) == (3, 64, 5) ? "the stand spot by the gen" : null;
        // live2 00:52:35 "is the clone's brick the one at 3,64,5?"
        llm.ThenText("is the clone's brick the one at 3,64,5? or the one at 9,64,9?");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd the clone's brick is the other one")]), default);
        Assert.Equal(["is the clone's brick the one at the stand spot by the gen? or the one at 9,64,9?"], sent);

        // A player who talks in coordinates gets coordinates back.
        sent.Clear();
        rt.Chat.OnServerChat("<PoofimaFox> clawd mine 3,64,5 not 9,64,9");
        llm.ThenText("mining 3,64,5 then");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd mine 3,64,5 not 9,64,9")]), default);
        Assert.Equal(["mining 3,64,5 then"], sent);
    }

    // ---------------------------------------------------------------- B20: claims the live state already contradicts

    [Fact]
    public async Task B20_a_player_who_rejoined_during_the_turn_is_not_reported_gone()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        rt.Chat.OnServerChat("PoofimaFox left the game.");
        // alice 07:45:35.04 "PoofimaFox joined the game.", then 0.85 s later the line written before it was sent.
        llm.Then(r =>
        {
            rt.Chat.OnServerChat("PoofimaFox joined the game.");
            return llm.Resp(r, StopReasons.EndTurn, new LlmText("Foxy just logged off, so I chopped one more tree and I'm going to wait by the house till morning. " +
                "A starving maid powering through the night, skibidi uwu"));
        });
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("kiwi", "clawd where did foxy go?")]), default);
        Assert.Equal(["A starving maid powering through the night, skibidi uwu"], sent);
    }

    [Fact]
    public void B20_presence_claims_stay_when_they_are_true()
    {
        var dropped = new List<string>();
        Assert.Equal("Foxy just logged off, night robin", SpeechFacts.DropStalePresence("Foxy just logged off, night robin", ["kiwi"], [], dropped));
        // Naming an online player's login is stale whatever the timing.
        Assert.Equal("ok", SpeechFacts.DropStalePresence("kiwi logged off. ok", ["kiwi"], [], dropped));
        Assert.Equal(["kiwi logged off."], dropped);
    }

    [Fact]
    public void B20_arrival_and_completion_claims_wait_for_the_running_walk_or_job()
    {
        var dropped = new List<string>();
        // alice 07:02:12, written while the walk/job of this turn had only just started
        var said = SpeechFacts.DropPrematureClaims("Got it foxy, I'm back on my block now and 67 is headed to the one by the lava, doing it properly this time uwu",
            moving: true, working: false, dropped);
        Assert.Equal("Got it foxy and 67 is headed to the one by the lava, doing it properly this time uwu", said);
        Assert.Equal(["I'm back on my block now"], dropped);

        Assert.Equal("on it foxy!", SpeechFacts.DropPrematureClaims("Done foxy! on it foxy!", moving: false, working: true, dropped));
        // Talking about the work is fine; so is a completion that is clearly in the future.
        foreach (var ok in new[] { "I'm heading back to my block now", "I'm on it", "I'll tell you when it's done", "not done yet uwu", "working on side B now" })
            Assert.Equal(ok, SpeechFacts.DropPrematureClaims(ok, moving: true, working: true, dropped));
        // Nothing running: nothing to contradict.
        Assert.Equal("Done foxy!", SpeechFacts.DropPrematureClaims("Done foxy!", moving: false, working: false, dropped));
    }
}
