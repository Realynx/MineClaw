using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive.Tests;

/// <summary>Pins <c>HiveSupervisor.Verb</c> (WP07: the switch became a table): every listed word, the "-ing" fallbacks and the empty task.</summary>
public class HiveVerbTests
{
    [Theory]
    [InlineData("mine 10 cobble at the gen", "mining")] [InlineData("dig a hole", "mining")] [InlineData("harvest wheat", "mining")] [InlineData("grind xp", "mining")]
    [InlineData("chop trees", "chopping")] [InlineData("cut grass", "chopping")]
    [InlineData("craft a pick", "crafting")] [InlineData("make sticks", "crafting")]
    [InlineData("build a hut", "building")] [InlineData("place torches", "building")]
    [InlineData("smelt iron", "smelting")] [InlineData("cook fish", "smelting")]
    [InlineData("organize the chests", "sorting")] [InlineData("organise it", "sorting")] [InlineData("sort", "sorting")]
    [InlineData("deposit all", "storing")] [InlineData("store it", "storing")] [InlineData("stash loot", "storing")]
    [InlineData("fetch wood", "fetching")] [InlineData("get food", "fetching")] [InlineData("bring me a pick", "fetching")]
    [InlineData("collect eggs", "fetching")] [InlineData("gather seeds", "fetching")]
    [InlineData("farm wheat", "farming")] [InlineData("plant saplings", "farming")]
    [InlineData("MINE cobble", "mining")] [InlineData("  42 chop", "chopping")]
    [InlineData("explore the cave", "exploring")] [InlineData("trade", "trading")] [InlineData("fight zombies", "fighting")] [InlineData("e", "ing")]
    [InlineData("", "working")] [InlineData("   123 !!", "working")] [InlineData(null, "working")]
    public void Verb_names_what_the_task_does(string? task, string verb) => Assert.Equal(verb, HiveSupervisor.Verb(task!));
}
