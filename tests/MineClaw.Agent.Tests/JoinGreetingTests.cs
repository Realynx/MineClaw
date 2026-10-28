using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 00:20-00:25: robinchan (Robin's own bot) reconnected every few minutes; main spent an Opus turn on each join
/// ("wb robinchan~ | maid-chan missed u fr uwu"). A human regular gets one templated hi (no model call), debounced across
/// reconnects within 5 min; bots get nothing; news for the player still wakes the brain.
/// </summary>
public class JoinGreetingTests
{
    private static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent, ManualTime Time) Bot()
    {
        var time = new ManualTime();
        var (rt, llm, sent) = TestRuntime.Create(o =>
        {
            o.Self.TrustedPlayers = ["poofimafox", "kiwi"];
            o.Chat.OtherBots = ["robinchan"];
        }, time);
        return (rt, llm, sent, time);
    }

    private static Task<EpisodeOutcome> Join(AgentRuntime rt, string who)
    {
        rt.Chat.OnServerChat($"{who} joined the game.");
        return rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Joined(who)]), default);
    }

    private static Task Leave(AgentRuntime rt, string who)
    {
        rt.Chat.OnServerChat($"{who} left the game.");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_reconnecting_bot_is_never_greeted_and_costs_no_model_call()
    {
        var (rt, llm, sent, time) = Bot();
        foreach (var gap in new[] { 0, 3, 2, 5 })
        {
            time.Advance(TimeSpan.FromMinutes(gap));
            await Leave(rt, "robinchan");
            time.Advance(TimeSpan.FromSeconds(6));
            var o = await Join(rt, "robinchan");
            Assert.Equal(0, o.Calls);
        }
        Assert.Empty(llm.Requests);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_regular_gets_one_templated_hi_debounced_across_reconnects()
    {
        var (rt, llm, sent, time) = Bot();
        var o = await Join(rt, "kiwi");
        Assert.Equal(0, o.Calls);
        Assert.Empty(llm.Requests);
        Assert.Single(sent);
        Assert.StartsWith("hii robin~", sent[0]); // her nickname, a hello line (the wave gesture plays on it)

        // Drops and comes back 2 min later: no second hi.
        time.Advance(TimeSpan.FromMinutes(2));
        await Leave(rt, "kiwi");
        time.Advance(TimeSpan.FromSeconds(10));
        await Join(rt, "kiwi");
        Assert.Single(sent);

        // Away for 20 min: a fresh hi (the next template).
        time.Advance(TimeSpan.FromMinutes(1));
        await Leave(rt, "kiwi");
        time.Advance(TimeSpan.FromMinutes(20));
        await Join(rt, "kiwi");
        Assert.Equal(2, sent.Count);
        Assert.Contains("robin", sent[1]);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task Pending_news_for_the_player_still_wakes_the_brain()
    {
        var (rt, llm, sent, _) = Bot();
        rt.Agent.HasNewsFor = p => p == "PoofimaFox";
        llm.ThenText("foxy! the 300 cobble are in the chest~");
        var o = await Join(rt, "PoofimaFox");
        Assert.True(o.Calls > 0);
        Assert.Single(llm.Requests);
    }
}
