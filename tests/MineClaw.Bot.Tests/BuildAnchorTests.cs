using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28: "You're building it in the wrong spot." The design went "21 S of mob chamber" while Fox meant the 8×8 drop floor
/// at the end of the 2×2 bridge branch clone 67 had just built, where both stood. The anchor now comes from what the player
/// referenced, the footprint snaps to a referenced floor, a size mismatch is a question, and the summary says where.
/// </summary>
public class BuildAnchorTests
{
    [Theory]
    [InlineData("here", true)]
    [InlineData("where we are", true)]
    [InlineData("where I'm standing", true)]
    [InlineData("right here", true)]
    [InlineData("on the 8x8 floor", false)]
    [InlineData("-39,64,1", false)]
    public void Here_means_the_requester(string at, bool here) => Assert.Equal(here, SiteAnchor.IsHere(at));

    /// <summary>A 2-wide bridge from the island out to an 8×8 floor, all placed by clone 67 a few minutes ago.</summary>
    private static List<PlacedBlock> BridgeAndFloor(DateTimeOffset at)
    {
        var list = new List<PlacedBlock>();
        for (int x = 7; x <= 12; x++) for (int z = 0; z <= 1; z++) list.Add(new PlacedBlock(new BlockPos(x, 63, z), 0, Blocks.Cobblestone, "maid_chan_67", at));
        for (int x = 13; x <= 20; x++) for (int z = -3; z <= 4; z++) list.Add(new PlacedBlock(new BlockPos(x, 63, z), 0, Blocks.Cobblestone, "maid_chan_67", at));
        return list;
    }

    [Fact]
    public void The_floor_at_the_end_of_the_bridge_is_found_with_its_extent()
    {
        var now = DateTimeOffset.UtcNow;
        var e = SiteAnchor.RecentBuild(BridgeAndFloor(now.AddMinutes(-5)), 0, new BlockPos(16, 64, 0), now, TimeSpan.FromHours(2));
        Assert.NotNull(e);
        Assert.Equal((8, 8), (e!.W, e.D));
        Assert.Equal(new BlockPos(13, 63, -3), e.Min);
        Assert.Contains("8×8 floor at the end of the bridge", e.Name);
        Assert.True(SiteAnchor.IsRecentBuild("on the 8x8 floor"));
        Assert.True(SiteAnchor.IsRecentBuild("at the end of the bridge"));
    }

    private static readonly SiteExtent Floor8 = new(new BlockPos(-3, 63, -3), new BlockPos(4, 63, 4), "8×8 floor at the end of the bridge");

    [Fact]
    public void A_matching_footprint_snaps_onto_the_floor_corner_and_a_mismatch_is_flagged()
    {
        var w = BlueprintTests.Island(8);
        var loop = new DesignLoop(new Author(""), null, new BlueprintPreview(), new BlueprintOptions()) { Site = () => BlueprintTests.Site(w) };
        var req = new DesignRequest("a shed", SiteAnchor.Ground(Floor8), "on the floor", "cobble") { Extent = Floor8 };
        var fit = loop.Evaluate("blueprint shed\nsize 8x2x8\npalette C=cobblestone\nwalls C 0,0,0 7,1,7", 1, req);
        Assert.Equal(new BlockPos(-3, 64, -3), fit.Plan!.Origin);
        Assert.DoesNotContain(fit.Structural, i => i.Code == "size-mismatch");
        var big = loop.Evaluate("blueprint tower\nsize 16x2x16\npalette C=cobblestone\nwalls C 0,0,0 15,1,15", 2, req);
        Assert.Contains(big.Structural, i => i.Code == "size-mismatch" && i.Text.Contains("16×16") && i.Text.Contains("8×8"));
    }

    private sealed class Author(string dsl) : IBlueprintAuthor
    {
        public Task<AuthorTurn> WriteAsync(IReadOnlyList<LlmMessage> conversation, CancellationToken ct) =>
            Task.FromResult(new AuthorTurn("```\n" + dsl + "\n```", new LlmUsage(500, 300, 0, 0), "claude-opus-5-5", 0.05m));
    }

    [Fact]
    public async Task The_summary_says_where_by_the_floor_and_asks_about_a_size_mismatch_before_building()
    {
        var w = BlueprintTests.Island(10);
        var store = new BlueprintStore(AgentDb.Open(null));
        var said = new List<string>();
        var s = new BlueprintService(new BlueprintOptions { Critique = false }, store, new BlueprintPreview())
        {
            Site = () => BlueprintTests.Site(w, inv: new() { [Blocks.Cobblestone] = 999 }),
            Crew = () => new SingleBodyCrew(new SimBuildBody(w)),
            ResolveSite = (at, who) => (new SitePick(SiteAnchor.Ground(Floor8), "on the " + Floor8.Name, Floor8), null),
            Trusted = () => ["PoofImaFox"],
            Say = l => { lock (said) said.Add(l); return Task.CompletedTask; },
            Executor = new ConstructionExecutor(new BlueprintOptions(), store),
            Landmark = _ => "21 S of mob chamber",
        };
        s.Author = new Author("blueprint mob tower\nsize 16x2x16\npalette C=cobblestone\nwalls C 0,0,0 15,1,15");
        s.StartBuild = (_, _) => null;
        s.Design("mob tower on the 8x8 floor", "on the 8x8 floor", "PoofImaFox");
        await s.LastRun!;
        var line = Assert.Single(said);
        Assert.Contains("on the 8×8 floor at the end of the bridge", line);
        Assert.DoesNotContain("mob chamber", line);                   // the anchor, not a nearby landmark
        Assert.Contains("it's 16×16 but the 8×8 floor", line);
        Assert.Contains("shrink it to fit", line);
    }
}
