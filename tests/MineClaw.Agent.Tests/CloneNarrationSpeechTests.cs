using MineClaw.Agent.Chat;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 02:11-02:14, clone 67 as Fox's builder said its own asking/waiting narration in public chat. Main and clones both
/// strip outgoing text through <see cref="MetaSpeech.Strip"/>; these lines must go, its real questions to the player stay.
/// </summary>
public class CloneNarrationSpeechTests
{
    [Theory]
    [InlineData("Asked Fox what he wants built at the bridge edge, and I'm waiting for his answer.")]
    [InlineData("Fox is offering 64 cobble, so I'll wait for him to hand it over and ask what the plan is for.")]
    [InlineData("Asked Fox for the size and type of the build, and waiting on his reply.")]
    [InlineData("Waiting on Fox's answer before placing or removing anything else.")]
    [InlineData("Sent the worker the fix, so it will build the branch at Fox's spot instead.")]
    [InlineData("I'll walk to the west end while waiting for his answer.")]
    public void Narration_of_its_own_asking_and_waiting_is_dropped(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var dropped));
        Assert.NotEmpty(dropped);
    }

    [Theory]
    [InlineData("ooh ty Fox uwu, what's the plan tho? a wall, a platform, or a spawner pad? tell me the size and i'll get building")]
    [InlineData("i see the void drop off the bridge edge, what do u want me to build there Fox?")]
    [InlineData("PoofimaFox, Which end of the bridge should I extend 12 blocks?")]
    [InlineData("I'm waiting for you by the chest!")]
    [InlineData("I asked you twice already, silly")]
    public void Real_questions_to_the_player_stay(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));

    [Fact]
    public void A_mixed_reply_keeps_the_question_and_drops_the_narration()
    {
        var text = "ooh ty Fox uwu, what's the plan tho? a wall, a platform, or a spawner pad? Asked Fox for the size and type of the build, and waiting on his reply.";
        var kept = MetaSpeech.Strip(text, out _);
        Assert.Contains("what's the plan tho?", kept);
        Assert.DoesNotContain("waiting on his reply", kept);
    }
}
