using MineClaw.Agent.Chat;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 2026-10-29 (1.12.2): main said its own status aloud in public chat, "Nothing for me to do right now." and, after clone 69
/// died, "Clone 69 was killed by a zombie at its tree, the same one that was attacking it." (the turn's final text after a task /
/// hive event, through <see cref="MetaSpeech.Strip"/>). The user wants very low verbosity: third-person status narration goes
/// unless a player asked for status; warnings and answers to the listener stay.
/// </summary>
public class StatusNarrationSpeechTests
{
    [Theory]
    [InlineData("Nothing for me to do right now.")]
    [InlineData("Clone 69 was killed by a zombie at its tree, the same one that was attacking it.")]
    [InlineData("There's nothing else for me to do.")]
    [InlineData("maid_chan_420 finished its chop job and logged out.")]
    [InlineData("Robin was slain by a zombie at the door.")]
    public void Status_narration_is_dropped(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var dropped));
        Assert.NotEmpty(dropped);
    }

    [Theory]
    [InlineData("Nothing for me to do right now.")]
    [InlineData("Clone 69 was killed by a zombie at its tree, the same one that was attacking it.")]
    public void It_stays_when_a_player_asked_for_status(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _, statusAsked: true));

    [Theory]
    [InlineData("robin run, a zombie is at the door!")]
    [InlineData("you were killed by a zombie, ur stuff is by the door")]
    [InlineData("two lil maids are chopping now~ foxy, stand on the chest spot and say here pls uwu")]
    [InlineData("nothing to see here uwu")]
    public void Warnings_and_talk_to_the_listener_stay(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));
}
