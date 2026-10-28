using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 06:45/06:49: "[task ok] #2 harvest_generator: generator: dug 64/64 at 5,64,7, stored 4 cobblestone" and the brain
/// said "I mined all 64, but again only 4 cobble ended up in the chest" / "I finished the 24 Fox was owed" (it got 3). A count
/// larger than the task's own result is corrected before it is said or logged.
/// </summary>
public class TaskCountHonestyTests
{
    private static IReadOnlyList<SpeechFacts.TaskCount> Facts(string result) =>
        SpeechFacts.TaskCounts([GameEvent.TaskDone("#2 harvest_generator: " + result)]);

    [Fact]
    public void The_task_result_count_is_read_with_its_target_and_digs()
    {
        var f = Assert.Single(Facts("generator: 4/64 cobblestone in 4m15s (dug 64 at 5,64,7), stored 4 cobblestone in chest 5,64,4"));
        Assert.Equal(new SpeechFacts.TaskCount("cobble", 4, 64, 64), f);
    }

    [Theory]
    [InlineData("I mined all 64, but again only 4 cobble ended up in the chest.", "I mined only 4 of the 64, but again only 4 cobble ended up in the chest.")]
    [InlineData("I mined another 64 cobble at the gen.", "I mined only 4 of the 64 cobble at the gen.")]
    [InlineData("I got 64 cobblestone for Fox.", "I got only 4 of the 64 cobblestone for Fox.")]
    public void A_claim_above_the_ledger_count_is_corrected(string said, string expected)
    {
        var facts = Facts("generator: 4/64 cobblestone in 4m15s (dug 64 at 5,64,7)");
        var log = new List<string>();
        Assert.Equal(expected, SpeechFacts.CorrectCounts(said, facts, log));
        Assert.Single(log);
    }

    [Theory]
    [InlineData("I dug 64 times but only 4 made it.")]   // it did dig 64
    [InlineData("I mined 4 cobble, the rest washed away.")] // the true count
    [InlineData("I crafted 64 slabs earlier.")]          // another item, another job
    public void True_or_unrelated_counts_stay(string said) =>
        Assert.Equal(said, SpeechFacts.CorrectCounts(said, Facts("generator: 4/64 cobblestone in 4m15s (dug 64 at 5,64,7)")));

    [Fact]
    public void Finishing_the_target_names_the_real_count()
    {
        var facts = Facts("generator: 3/24 cobblestone in 1m20s (dug 24 at 5,64,7)");
        Assert.Equal("I finished only 3 of the 24 Fox was owed, but only 3 made it into the chest.",
            SpeechFacts.CorrectCounts("I finished the 24 Fox was owed, but only 3 made it into the chest.", facts));
    }

    [Fact]
    public void No_task_result_means_no_change() =>
        Assert.Equal("I mined all 64.", SpeechFacts.CorrectCounts("I mined all 64.", SpeechFacts.TaskCounts([GameEvent.TaskDone("#1 chop_tree: chopped 5 logs")])));
}
