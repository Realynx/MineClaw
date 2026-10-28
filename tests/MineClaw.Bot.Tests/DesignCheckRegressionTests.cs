using MineClaw.Agent.Llm;
using MineClaw.Bot.Blueprints;
using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>Live design #8 (mob drop tower, 10-28): door side, doors in the entrance flood-fill, stairs vs headroom, best-version choice.</summary>
public class DesignCheckRegressionTests
{
    /// <summary>Verbatim from the live db.</summary>
    public static readonly string MobDropTower = """
        blueprint mob drop tower
        size 8x12x8
        facing north
        origin -25,64,15
        palette C=cobblestone D=door:north S=cobblestone_stairs:north E=cobblestone_stairs:east H=slab:cobble
        walls C 0,0,0 7,9,7
        walls C 2,0,2 5,9,5
        clear 3,0,5 3,1,5
        door D 3,0,0 north
        stairs S 1,0,5 north
        stairs S 1,1,4 north
        stairs S 1,2,3 north
        stairs S 1,3,2 north
        stairs S 1,4,1 north
        stairs E 2,5,1 east
        stairs E 3,6,1 east
        stairs E 4,7,1 east
        stairs E 5,8,1 east
        stairs E 6,9,1 east
        floor C 0,10,0 7,10,7
        clear 3,10,3 4,10,4
        clear 5,10,1 6,10,1
        walls H 0,11,0 7,11,7
        """.Replace("\r\n", "\n");

    /// <summary>
    /// The site: stone at y63 under the footprint (x -25..-18, z 15..22) and an island running off to the north; the void south
    /// of z 22. A door turned onto the south wall opens onto nothing (what the live checker saw).
    /// </summary>
    internal static WorldState TowerSite()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-48, 0, -16, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        for (int x = -30; x <= -13; x++) for (int z = 5; z <= 22; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        return w;
    }

    private static (VoxelPlan Plan, List<BlueprintIssue> Issues) Tower(string? dsl = null)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl ?? MobDropTower);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return (plan!, issues);
    }

    [Fact]
    public void Mob_drop_tower_passes_with_no_entrance_error_and_no_false_headroom()
    {
        var (plan, issues) = Tower();
        var e = DesignChecks.Evaluate(plan, BlueprintTests.Site(TowerSite()), issues);
        Assert.True(e.Pass, e.Compact(1));
        Assert.DoesNotContain(e.Issues, i => i.Code == "entrance");
        Assert.DoesNotContain(e.Issues, i => i.Code == "headroom"); // 1,0,4 sits under the step at 1,1,4: nobody stands there
        Assert.Equal(0, e.Unreachable);
        Assert.Equal(0, e.LowHeadroom);
        Assert.True(e.Interior > 0);
    }

    [Fact]
    public void Mob_drop_tower_reports_its_door_on_the_north_side_where_it_was_drawn()
    {
        var (plan, _) = Tower();
        // Every door drawn on the side `facing` names: the drawing is the build as it stands, not turned 180°.
        Assert.Equal(Dir.North, plan.Blueprint.DrawnFront);
        Assert.Equal(0, plan.Blueprint.Quarters);
        var lower = plan.Blocks.Values.Single(b => b.Id == Blocks.WoodenDoor && !b.UpperDoor);
        Assert.Equal(new BlockPos(-22, 64, 15), lower.Pos);                     // local 3,0,0: the z=0 (north) wall
        Assert.Equal(BuildBlocks.DoorMeta(Dir.North), lower.Meta);
        Assert.Equal((3, 0, 0), plan.ToLocal(lower.Pos));
        // The doorway is the door at y and y+1: the wall block there was replaced, not left solid.
        var upper = plan.Blocks[lower.Pos.Up()];
        Assert.True(upper.UpperDoor);
        Assert.Equal(Blocks.WoodenDoor, upper.Id);

        var e = DesignChecks.Evaluate(plan, BlueprintTests.Site(TowerSite()));
        Assert.Equal([Dir.North], e.DoorSides);
        Assert.Equal([Dir.North], e.DrawnDoorSides);
        Assert.Contains("1 door north,", e.Compact(1));
    }

    [Fact]
    public void A_south_front_drawing_facing_north_is_turned_180_degrees_and_says_so()
    {
        // The canonical convention still holds: front drawn on the south row, `facing north` turns it (a z-flip and an x-flip).
        var dsl = MobDropTower.Replace("D=door:north", "D=door:south").Replace("door D 3,0,0 north", "door D 4,0,7 south").Replace("clear 3,0,5 3,1,5", "clear 4,0,2 4,1,2");
        var (plan, _) = Tower(dsl);
        Assert.Equal(Dir.South, plan.Blueprint.DrawnFront);
        Assert.Equal(2, plan.Blueprint.Quarters);
        var lower = plan.Blocks.Values.Single(b => b.Id == Blocks.WoodenDoor && !b.UpperDoor);
        Assert.Equal(new BlockPos(-25 + 7 - 4, 64, 15), lower.Pos);               // local z=7 (south row) → world z=15 (north)
        Assert.Equal((4, 0, 7), plan.ToLocal(lower.Pos));
        Assert.Equal(BuildBlocks.DoorMeta(Dir.North), lower.Meta);
        var e = DesignChecks.Evaluate(plan, BlueprintTests.Site(TowerSite()));
        Assert.Equal([Dir.North], e.DoorSides);
        Assert.Equal([Dir.South], e.DrawnDoorSides);
        Assert.Contains("1 door south (faces north once turned)", e.Compact(1));
    }

    [Fact]
    public void A_sealed_box_still_fails_with_no_entrance()
    {
        var dsl = MobDropTower.Replace(" D=door:north", "").Replace("door D 3,0,0 north\n", "");
        var (plan, issues) = Tower(dsl);
        var e = DesignChecks.Evaluate(plan, BlueprintTests.Site(TowerSite()), issues);
        Assert.False(e.Pass);
        Assert.Contains(e.Issues, i => i.Code == "entrance" && i.Level == IssueLevel.Error && i.Text.StartsWith("no entrance", StringComparison.Ordinal));
    }

    [Fact]
    public void A_door_facing_the_void_leaves_the_interior_unreachable()
    {
        // The live failure: the drawing turned 180° put the door on the south wall, over the void. Same tower, door drawn south.
        var dsl = MobDropTower.Replace("door D 3,0,0 north", "door D 3,0,7 south").Replace("facing north", "facing south");
        var (plan, issues) = Tower(dsl);
        var e = DesignChecks.Evaluate(plan, BlueprintTests.Site(TowerSite()), issues);
        Assert.Contains(e.Issues, i => i.Code == "entrance" && i.Level == IssueLevel.Error);
    }

    // ------------------------------------------------------------------ best version

    private static DesignVersion Version(int n, VoxelPlan plan, int blocks, int unreachable, int headroom, Critique? critique)
    {
        var e = new DesignEvaluation { Blocks = blocks, Unreachable = unreachable, LowHeadroom = headroom };
        if (headroom > 0) e.Issues.Add(new(IssueLevel.Warning, "headroom", $"headroom 1 at {headroom} interior cell(s)"));
        if (unreachable > 0) e.Issues.Add(new(IssueLevel.Warning, "entrance", $"{unreachable} interior cell(s) can't be reached"));
        return new DesignVersion { N = n, Dsl = "", Plan = plan, Eval = e, Critique = critique };
    }

    [Fact]
    public void The_best_passing_version_is_ranked_by_quality_not_kept_because_it_came_last()
    {
        var (plan, _) = Tower();
        var v1 = Version(1, plan, 491, 0, 1, new Critique(true, "ok", [], 0));
        var v3 = Version(3, plan, 544, 8, 4, new Critique(false, "stray blocks", ["stray blocks @5,3,6"], 0));
        Assert.True(v1.Passing && v3.Passing);
        Assert.True(v1.Score < v3.Score, $"v1 {v1.Score} vs v3 {v3.Score}");
        Assert.False(DesignLoop.Better(v3, v1));
        Assert.True(DesignLoop.Better(v1, v3));
        Assert.False(DesignLoop.Better(Version(2, plan, 491, 0, 1, null), v1)); // a tie keeps the earlier one
    }

    private sealed class Scripted(params string[] answers) : IBlueprintAuthor
    {
        public int Calls;
        public Task<AuthorTurn> WriteAsync(IReadOnlyList<LlmMessage> conversation, CancellationToken ct) =>
            Task.FromResult(new AuthorTurn("```\n" + answers[Math.Min(Calls++, answers.Length - 1)] + "\n```", new LlmUsage(100, 100, 0, 0), "claude-opus-5-5", 0.01m));
    }

    private sealed class Picky(params Critique[] answers) : IDesignCritic
    {
        private int _i;
        public Task<Critique> CritiqueAsync(DesignPicture picture, VoxelPlan plan, string description, CancellationToken ct) =>
            Task.FromResult(answers[Math.Min(_i++, answers.Length - 1)]);
    }

    [Fact]
    public async Task A_clean_pass_gets_one_look_revision_then_the_better_version_is_kept()
    {
        var w = TowerSite();
        // v2 lowers a ceiling in the south corridor: still passes, but with 1-high cells. v1 must win, and v3 is never asked for.
        var worse = MobDropTower.Replace("floor C 0,10,0 7,10,7", "floor C 0,10,0 7,10,7\nfloor C 2,1,6 6,1,6");
        var author = new Scripted(MobDropTower, worse, worse);
        var critic = new Picky(new Critique(false, "plain walls", ["plain walls"], 0), new Critique(false, "stray blocks", ["stray blocks"], 0));
        var loop = new DesignLoop(author, critic, new BlueprintPreview(), new BlueprintOptions { MaxIterations = 3 }) { Site = () => BlueprintTests.Site(w) };
        var o = await loop.RunAsync(new DesignRequest("mob drop tower", new BlockPos(-22, 63, 18), "ground", "none"), CancellationToken.None);
        Assert.Equal(2, o.Versions.Count);
        Assert.Equal(2, author.Calls);
        Assert.True(o.Versions[1].Passing);
        Assert.True(o.Versions[1].Eval!.LowHeadroom > 0);
        Assert.Same(o.Versions[0], o.Best);
        Assert.StartsWith("passed", o.Stop);
        Assert.False(o.SuspectChecker);
    }

    [Fact]
    public async Task Three_identical_check_failures_are_flagged_as_a_possible_checker_issue()
    {
        var w = TowerSite();
        var sealedBox = MobDropTower.Replace(" D=door:north", "").Replace("door D 3,0,0 north\n", "");
        var loop = new DesignLoop(new Scripted(sealedBox), null, new BlueprintPreview(), new BlueprintOptions { MaxIterations = 3 }) { Site = () => BlueprintTests.Site(w) };
        var o = await loop.RunAsync(new DesignRequest("mob drop tower", new BlockPos(-22, 63, 18), "ground", "none"), CancellationToken.None);
        Assert.Equal(3, o.Versions.Count);
        Assert.True(o.SuspectChecker);
        Assert.EndsWith("(possible checker issue)", o.Stop);
        Assert.Equal(" (possible checker issue)", o.CheckerNote);
    }

    // ------------------------------------------------------------------ summary wording

    [Fact]
    public void The_summary_names_a_player_build_as_the_requesters_floor_never_a_unknown_structure()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++) w.SetBlock(x, 63, z, Blocks.Cobblestone, 0);
        BlueprintService Service(Func<BlockPos, string?> landmark) => new(new BlueprintOptions(), new BlueprintStore(AgentDb.Open(null)), new BlueprintPreview())
        {
            Site = () => BlueprintTests.Site(w), Crew = () => throw new InvalidOperationException(), ResolveSite = (_, _) => (null, "none"), Landmark = landmark,
        };
        var rec = new BlueprintRecord { Name = "mob drop tower", Requester = "Fox", Site = new BlockPos(3, 63, 4), Anchor = "at 3,63,4" };
        Assert.Equal("on Fox's 8×8 floor", Service(_ => "by a player build").AnchorText(rec));
        Assert.Equal("2 E of cobble_gen", Service(_ => "2 E of cobble_gen").AnchorText(rec));
        Assert.Equal("near 3,4", Service(_ => null).AnchorText(rec));
        Assert.Equal((8, 8), BlueprintService.FloorSize(w.PlanningView, new BlockPos(3, 63, 4)));
    }
}
