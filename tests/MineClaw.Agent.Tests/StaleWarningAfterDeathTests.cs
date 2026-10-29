using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 2026-10-29: right after "kiwi was slain by Zombie", main said "robin run to the river chest, maid-chan will bonk it there".
/// Advice or a warning for a player who just died is stale: every sender goes through <see cref="ChatRouter.SayAsync"/>, which
/// drops it (what else the line says stays).
/// </summary>
public class StaleWarningAfterDeathTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 29, 2, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ChatRouter R, List<string> Sent, Clock T) Rig()
    {
        var t = new Clock();
        var sent = new List<string>();
        var o = new ChatOptions { Username = "maid_chan", LineDelayMs = 0 };
        o.Classifier.Enabled = false;
        var r = new ChatRouter(o, new EventQueue(new EventQueueOptions(), t), (l, _) => { lock (sent) sent.Add(l); return Task.CompletedTask; }, t);
        r.SetOnlinePlayers(["PoofimaFox", "kiwi"]);
        return (r, sent, t);
    }

    [Fact]
    public async Task The_live_line_to_robin_is_dropped_after_her_death()
    {
        var (r, sent, _) = Rig();
        r.OnServerChat("kiwi was slain by Zombie", test: false);
        var said = await r.SayAsync("robin run to the river chest, maid-chan will bonk it there");
        Assert.Empty(said);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Condolences_stay_and_the_same_advice_is_fine_later_or_to_someone_alive()
    {
        var (r, sent, t) = Rig();
        r.OnServerChat("kiwi was slain by Zombie", test: false);
        Assert.NotEmpty(await r.SayAsync("robin noo, ur stuff is by the door, i'll guard it~"));
        Assert.NotEmpty(await r.SayAsync("foxy run to the river chest!"));
        t.Now = t.Now.AddSeconds(45);
        Assert.NotEmpty(await r.SayAsync("robin run to the river chest, a zombie is at the door!"));
        Assert.Equal(3, sent.Count);
    }

    [Theory]
    [InlineData("kiwi drowned", "kiwi")]
    [InlineData("PoofimaFox was shot by Skeleton", "PoofimaFox")]
    [InlineData("maid_chan_69 was slain by Zombie Villager", "maid_chan_69")]
    [InlineData("kiwi hit the ground too hard", "kiwi")]
    [InlineData("<kiwi> i almost died lol", null)]
    public void Death_lines_are_recognised(string line, string? who)
    {
        var d = new RecentDeaths(() => DateTimeOffset.UnixEpoch);
        Assert.Equal(who, d.Observe(line));
    }
}
