using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 01:3x: a turn woken by Fox's danger at the cactus farm warned him, then the same turn's narration went to public
/// chat ("I warned Fox that the cactus farm is hurting him. I'm also back on the cobble generator now: ..."). A danger turn's
/// public text is the warning line; third-person self-reports never reach chat.
/// </summary>
public class DangerTurnSpeechTests
{
    private const string Live =
        "foxy!! u getting poked at the cactus farm, careful uwu\n" +
        "I warned Fox that the cactus farm is hurting him. I'm also back on the cobble generator now: I'm mining the 24 cobble he's still owed… " +
        "The seeds I said I'd plant are still in my bag, so I haven't planted them yet.";

    [Fact]
    public async Task A_danger_turn_says_the_warning_and_keeps_its_narration_internal()
    {
        var (rt, llm, sent) = TestRuntime.Create(o => o.Self.TrustedPlayers = ["poofimafox"]);
        rt.Chat.OnServerChat("PoofimaFox joined the game.");
        llm.ThenText(Live);
        await rt.Agent.HandleBatchAsync(new EventBatch([
            GameEvent.Activity("PoofimaFox is taking damage near the cactus farm (hp 14)", "PoofimaFox", EventPriority.Normal)]), default);
        Assert.Equal(["foxy!! u getting poked at the cactus farm, careful uwu"], sent);
    }

    [Fact]
    public async Task One_paragraph_danger_reply_keeps_only_its_first_sentence()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenText("Careful foxy, the cactus is poking u! I'll know when the gen run finishes. It's going fine.");
        await rt.Agent.HandleBatchAsync(new EventBatch([
            GameEvent.Activity("PoofimaFox is taking damage near the cactus farm (hp 12)", "PoofimaFox", EventPriority.Normal)]), default);
        Assert.Equal(["Careful foxy, the cactus is poking u!"], sent);
    }

    [Theory]
    [InlineData("I warned Fox that the cactus farm is hurting him.", true)]
    [InlineData("I'm also back on the cobble generator now: I'm mining the 24 cobble he's still owed…", true)]
    [InlineData("I'll know when that finishes.", true)]
    [InlineData("The seeds I said I'd plant are still in my bag, so I haven't planted them yet.", true)]
    [InlineData("I told you the cactus hurts!", false)]
    [InlineData("I warned u foxy~", false)]
    [InlineData("I'm back! got the wood", false)]
    public void Self_reports_are_recognised(string line, bool report) => Assert.Equal(report, MetaSpeech.IsSelfReport(line));

    [Fact]
    public void MetaSpeech_strips_the_live_narration()
    {
        var kept = MetaSpeech.Strip(Live, out var dropped);
        Assert.Equal("foxy!! u getting poked at the cactus farm, careful uwu", kept);
        Assert.Equal(3, dropped.Count);
    }
}
