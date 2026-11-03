using Xunit.Abstractions;

namespace MineClaw.Skills.Tests;

/// <summary>The brain finds the infrastructure skills by what it wants, and the older skills keep their queries.</summary>
public class InfraCatalogTests(ITestOutputHelper output)
{
    private static readonly SkillRegistry Full = SkillRegistry.CreateDefault();

    [Theory]
    [InlineData("where is a desert", "find_biome")]
    [InlineData("find a desert or beach for sand", "find_biome")]
    [InlineData("scout for a desert", "scout_biome")]
    [InlineData("walk out until we see a desert", "scout_biome")]
    [InlineData("cast a portal frame with lava and water, no diamond pickaxe", "cast_portal")]
    [InlineData("make a nether portal without a diamond pickaxe", "cast_portal")]
    [InlineData("is a portal to the far sand worth it", "plan_portals")]
    [InlineData("build a nether portal", "build_portal")]          // unchanged
    [InlineData("make some obsidian", "make_obsidian")]            // unchanged
    [InlineData("go to the nether", "enter_portal")]               // unchanged
    public void The_right_skill_comes_first(string query, string skill)
    {
        var hits = SkillCatalog.Search(Full.Specs, query, 3);
        output.WriteLine($"{query} -> {string.Join(", ", hits.Select(h => h.Name))}");
        Assert.Equal(skill, hits[0].Name);
    }
}
