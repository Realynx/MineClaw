using System.Reflection;

namespace MineClaw.World.Tests;

/// <summary>
/// Id tables that replaced pattern ladders (complexity refactor WP04, checkpoint 3) agree with the pattern they replaced over
/// every id.
/// </summary>
public class TableEquivalenceTests
{
    [Fact]
    public void Scaffold_veto_matches_the_crop_pattern_it_replaced()
    {
        var table = typeof(Pathfinder).GetField("ScaffoldVeto", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var contains = table.GetType().GetMethod("Contains")!;
        for (int id = -5; id < 4200; id++)
        {
            bool old = id is BlockShapes.Farmland or 59 or 81 or 83 or 86 or 103 or 104 or 105 or 115 or 127 or 141 or 142 or 207;
            Assert.True(old == (bool)contains.Invoke(table, [id])!, $"id {id}");
        }
    }
}
