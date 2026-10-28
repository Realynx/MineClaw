using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 05:27 (alice CT 114): a turn woken by Fox's hand-over (stone pickaxe + 25 cobble) and his creeper death said its
/// thank-you with the say tool, then its final text went to public chat too: "I picked the drop tower build back up since Fox
/// already approved it. The creeper fight ended with no damage to me. Fox blew up on the main island, then gave me 1 stone
/// pickaxe and 25 cobble. I thanked him in chat and asked if he's ok." A turn says at most its in-persona lines.
/// </summary>
public class RecapSpeechTests
{
    private const string Thanks = "ty master for the pick n cobble uwu, rip that creeper got you tho, u ok??";
    private const string Picked = "I picked the drop tower build back up since Fox already approved it.";
    private const string Fight = "The creeper fight ended with no damage to me.";
    private const string AboutFox = "Fox blew up on the main island, then gave me 1 stone pickaxe and 25 cobble.";
    private const string Thanked = "I thanked him in chat and asked if he's ok.";
    private const string Leak = Picked + " " + Fight + " " + AboutFox + " " + Thanked;

    private static readonly string[] Fox = ["PoofimaFox", "fox", "foxy", "master"];

    [Fact]
    public void The_live_recap_is_stripped_sentence_by_sentence()
    {
        Assert.Equal("", MetaSpeech.Strip(Leak, out var dropped, addressees: Fox));
        Assert.Equal([Picked, Fight, AboutFox, Thanked], dropped);
    }

    [Theory]
    [InlineData(Picked)]
    [InlineData(Thanked)]
    [InlineData(Fight)]
    [InlineData("I thanked robin for the bread.")]
    [InlineData("and told him that I'd keep mining.")]
    [InlineData("I resumed the tower since Fox already approved it.")]
    public void Recaps_of_its_own_speech_and_choices_are_meta(string line) =>
        Assert.Equal("", MetaSpeech.Strip(line, out _));

    [Fact]
    public void Talking_about_the_addressee_in_the_third_person_is_a_report()
    {
        Assert.True(MetaSpeech.TalksAboutAddressee(AboutFox, Fox));
        Assert.Equal("", MetaSpeech.Strip(AboutFox, out _, addressees: Fox));
        // About someone else it's ordinary talk ("robin gave me ..." said to Fox).
        Assert.Equal(AboutFox.Replace("Fox", "Robin"), MetaSpeech.Strip(AboutFox.Replace("Fox", "Robin"), out _, addressees: Fox));
    }

    [Theory]
    [InlineData(Thanks)]
    [InlineData("I told you so")]
    [InlineData("I told you so!! the creeper was right there")]
    [InlineData("I asked robin earlier")]
    [InlineData("I asked robin earlier, she said the farm is north")]
    [InlineData("thank u master!!")]
    [InlineData("master saved me uwu")]
    [InlineData("fox look at this!")]
    [InlineData("foxy, u ok? that creeper got u good")]
    [InlineData("I picked it back up, ty!")]
    [InlineData("can u say it in chat? i missed it")]
    [InlineData("that fight was crazy lol")]
    public void Normal_speech_stays(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _, addressees: Fox));

    private static GameEvent HandOver() =>
        new GameEvent(EventKind.Notice, "PoofimaFox gave you 1 stone pickaxe and 25 cobblestone (thank them in one short line)", "PoofimaFox", EventPriority.Normal)
            with { MustWake = true };

    private static GameEvent FoxBlewUp() =>
        GameEvent.Activity("PoofimaFox blew up", "PoofimaFox", EventPriority.Normal) with { MustWake = true };

    [Fact]
    public async Task The_0527_turn_says_its_thank_you_and_keeps_the_recap_internal()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        var internalLines = new List<string>();
        rt.Agent.InternalSpeech += internalLines.Add;
        llm.ThenTool("list_memory", new { }, id: "tu_a")
            .ThenTool(BotAgent.SayToolName, new { text = Thanks }, id: "tu_b")
            .ThenText(Leak);
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([HandOver(), FoxBlewUp()]), default);

        Assert.Equal(Thanks, string.Join(" ", sent));
        Assert.Equal(Thanks, string.Join(" ", o.Said));
        Assert.Contains(Leak, internalLines);
    }

    [Fact]
    public async Task After_the_say_tool_spoke_even_a_chat_turns_final_text_is_internal()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        var internalLines = new List<string>();
        rt.Agent.InternalSpeech += internalLines.Add;
        llm.ThenTool(BotAgent.SayToolName, new { text = "on it master~" }, id: "tu_a")
            .ThenText("Fox asked for cobble, so I said I'm on it and started the gen run.");
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd get me some cobble")]), default);
        Assert.Equal("on it master~", string.Join(" ", sent));
        Assert.Single(o.Said);
        Assert.Contains(internalLines, l => l.Contains("started the gen run"));
    }

    [Fact]
    public async Task A_chat_turns_answer_after_a_tool_still_goes_out()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenTool("list_memory", new { }, id: "tu_a").ThenText("nothing saved yet master, wanna mark the gen?");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd what do u remember")]), default);
        Assert.Equal("nothing saved yet master, wanna mark the gen?", string.Join(" ", sent));
    }
}
