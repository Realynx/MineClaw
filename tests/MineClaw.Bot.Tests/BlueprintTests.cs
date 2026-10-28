using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Tests;

/// <summary>Blueprint designer: DSL, checks, the revision loop, chat review, construction order/scaffolding, split, resume.</summary>
public class BlueprintTests
{
    public static readonly string Hut = """
        blueprint small cobble hut
        size 5x4x5
        facing south
        palette C=cobblestone P=planks G=glass_pane D=door:south
        layer 0
        CCCCC
        C...C
        C...C
        C...C
        CCDCC
        layer 1
        CCCCC
        G...G
        C...C
        C...C
        CC_CC
        layer 2
        CCCCC
        C...C
        C...C
        C...C
        CCCCC
        layer 3
        PPPPP
        PPPPP
        PPPPP
        PPPPP
        PPPPP
        """.Replace("\r\n", "\n");

    /// <summary>An in-memory site: chunks -16..15 loaded as air, a stone island (y 63) of radius <paramref name="r"/>.</summary>
    internal static WorldState Island(int r = 6, int y = 63)
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++) w.SetBlock(x, y, z, Blocks.Stone, 0);
        w.Player.ServerTeleport(new Vec3d(0.5, y + 1, r + 0.5 - 1), y + 2.62, 0, 0, true);
        return w;
    }

    internal static SiteContext Site(WorldState w, Func<BlockPos, int, ProtectionVerdict>? verdict = null, Dictionary<int, int>? inv = null,
        Dictionary<int, int>? stored = null, bool gen = true, int trees = 2) => new()
    {
        World = w.PlanningView, Verdict = verdict, Inventory = (id, _) => inv?.GetValueOrDefault(id) ?? 0, Stored = id => stored?.GetValueOrDefault(id) ?? 0,
        GeneratorKnown = gen, ChoppableTrees = trees,
    };

    private static VoxelPlan PlanOf(string dsl, BlockPos? origin = null)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl, origin ?? new BlockPos(-2, 64, -2));
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return plan!;
    }

    // ------------------------------------------------------------------ DSL

    [Fact]
    public void Hut_parses_into_voxels_with_valid_door_halves()
    {
        var plan = PlanOf(Hut);
        Assert.Equal("small cobble hut", plan.Blueprint.Name);
        Assert.Equal(new BlockPos(2, 67, 2), plan.Max);
        var lower = plan.Blocks[new BlockPos(0, 64, 2)];
        var upper = plan.Blocks[new BlockPos(0, 65, 2)];
        Assert.Equal(Blocks.WoodenDoor, lower.Id);
        Assert.Equal(BuildBlocks.DoorMeta(Dir.South), lower.Meta);     // placed from outside, facing north: meta 3 (mc-dev ItemDoor)
        Assert.Equal(3, lower.Meta);
        Assert.Equal(lower.Meta | 8, upper.Meta);                        // upper half = lower | 8
        Assert.Contains(new BlockPos(0, 64, 0), plan.Air);
        Assert.Equal(Blocks.GlassPane, plan.Blocks[new BlockPos(-2, 65, -1)].Id);
        Assert.Equal(25, plan.Blocks.Values.Count(b => b.Id == Blocks.Planks));
    }

    [Fact]
    public void Facing_rotates_the_build_and_its_metas()
    {
        var east = Hut.Replace("facing south", "facing east");
        var plan = PlanOf(east);
        // The front (door) now faces east: the door sits on the east wall's middle, and its meta turned with it.
        var door = plan.Blocks.Values.Single(b => b.Id == Blocks.WoodenDoor && !b.UpperDoor);
        Assert.Equal(new BlockPos(2, 64, 0), door.Pos);
        Assert.Equal(BuildBlocks.DoorMeta(Dir.East), door.Meta);
        Assert.Equal((2, 0, 4), plan.ToLocal(door.Pos)); // findings point at what the author drew
        var stairs = PlanOf("size 1x1x1\nfacing west\npalette S=cobblestone_stairs:north\nlayer 0\nS");
        Assert.Equal(BuildBlocks.StairsMeta(Dir.East), stairs.Blocks.Values.Single().Meta); // north turned 90° clockwise = east
    }

    [Fact]
    public void Primitives_build_walls_roofs_doors_and_stairs()
    {
        var plan = PlanOf("""
            blueprint shed
            size 5x6x5
            palette C=cobblestone S=cobblestone_stairs P=planks D=wooden_door
            walls C 0,0,0 4,2,4
            door D 2,0,4 south
            roof S gable 0,3,0 4,4 P
            """.Replace("\r\n", "\n"));
        Assert.Equal(BuildBlocks.StairsMeta(Dir.South), plan.Blocks[new BlockPos(-2 + 0, 64 + 3, -2 + 0)].Meta); // north slope rises south
        Assert.Equal(BuildBlocks.StairsMeta(Dir.North), plan.Blocks[new BlockPos(-2, 67, 2)].Meta);
        Assert.Equal(Blocks.Planks, plan.Blocks[new BlockPos(0, 69, 0)].Id); // ridge row
        Assert.Contains(new BlockPos(0, 65, 0), plan.Air); // interior
        Assert.True(plan.Blocks[new BlockPos(0, 65, 2)].UpperDoor);
    }

    [Theory]
    [InlineData("size 2x1x1\npalette X=unobtainium\nlayer 0\nXX", "unknown b1.8.1 block")]
    [InlineData("size 2x1x1\npalette L=leaves\nlayer 0\nLL", "decay")]
    [InlineData("size 3x1x1\npalette C=cobblestone\nlayer 0\nCC", "is 2 wide")]
    [InlineData("size 2x1x1\npalette C=cobblestone\nlayer 0\nCQ", "'Q' is not in the palette")]
    [InlineData("size 1x2x1\npalette H=slab:cobble\nlayer 0-1\nH", "merge into a double slab")]
    [InlineData("size 1x3x1\npalette D=door\nlayer 0-2\nD", "three high")]
    [InlineData("size 1x1x1\npalette S=wooden_stairs:5\nlayer 0\nS", "stairs are 0..3")]
    [InlineData("size 1x2x1\npalette D=door C=cobblestone\nlayer 0\nD\nlayer 1\nC", "upper half free")]
    [InlineData("size 1x1x1\npalette W=water\nlayer 0\nW", "liquids")]
    [InlineData("palette C=cobblestone\nbox C 0,0,0 1,1,1", "missing 'size")] // no size and no layers to infer it from
    public void Invalid_blueprints_are_rejected_with_the_reason(string dsl, string expected)
    {
        var (_, issues) = BlueprintNormalizer.FromText(dsl, new BlockPos(0, 64, 0));
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.Text.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Metadata_rules_follow_mc_dev()
    {
        Assert.Equal(0, BuildBlocks.StairsMeta(Dir.East));
        Assert.Equal(3, BuildBlocks.StairsMeta(Dir.North));
        Assert.Equal(1, BuildBlocks.DoorFacingMeta(Dir.South)); // ItemDoor: yaw 0 (facing south) → 1
        Assert.Equal(2, BuildBlocks.LadderMeta(Dir.South));      // BlockLadder: solid block at z+1 → 2
        Assert.Equal(2, BuildBlocks.FrontMeta(Dir.North));
        Assert.Null(BuildBlocks.MetaProblem(Blocks.Slab, 5));    // 6 slab types in 1.8 (stone brick = 5)
        Assert.NotNull(BuildBlocks.MetaProblem(Blocks.Slab, 6));
        Assert.NotNull(BuildBlocks.Refusal(Blocks.Bed));
        Assert.Equal((Items.WoodenDoor, 0), BuildBlocks.ItemFor(Blocks.WoodenDoor, 3));
        Assert.Equal((Blocks.Wool, 14), BuildBlocks.ItemFor(Blocks.Wool, 14));
        // Rotation keeps the door's upper bit and hinge.
        Assert.Equal(BuildBlocks.DoorFacingMeta(Dir.West) | 8 | 4, BuildBlocks.Rotate(Blocks.WoodenDoor, BuildBlocks.DoorFacingMeta(Dir.South) | 8 | 4, 1));
    }

    // ------------------------------------------------------------------ checks

    [Fact]
    public void Bill_counts_inventory_storage_and_the_acquisition_route()
    {
        var w = Island();
        var plan = PlanOf(Hut);
        var e = DesignChecks.Evaluate(plan, Site(w, inv: new() { [Blocks.Cobblestone] = 20 }, stored: new() { [Blocks.Cobblestone] = 10, [Blocks.Log] = 2 }));
        var cobble = e.Bill.Single(b => b.Item == Blocks.Cobblestone);
        Assert.Equal(plan.Blocks.Values.Count(b => b.Id == Blocks.Cobblestone), cobble.Need);
        Assert.Equal(cobble.Need - 30, cobble.Missing);
        Assert.StartsWith("gen", cobble.Source);
        var planks = e.Bill.Single(b => b.Item == Blocks.Planks);
        Assert.Equal(25, planks.Missing);
        Assert.Contains("logs", planks.Source); // 2 stored logs cover 8 planks, the rest from trees
        Assert.Equal(1, e.Bill.Single(b => b.Item == Items.WoodenDoor).Need); // a door is one item for both halves
        Assert.True(e.Pass, string.Join("; ", e.Issues));
        Assert.Equal(1, e.Doors);
        Assert.Equal(9, e.Interior);
        Assert.Contains("south", e.Compact(1));
    }

    [Fact]
    public void Floating_falling_and_void_blocks_are_found()
    {
        var w = Island(2);
        var floating = PlanOf("size 3x3x1\npalette C=cobblestone\nlayer 0\nC__\nlayer 2\n__C", new BlockPos(0, 64, 0));
        var e = DesignChecks.Evaluate(floating, Site(w));
        Assert.Contains(e.Issues, i => i.Code == "floating" && i.Text.Contains("2,2,0"));
        var sand = PlanOf("size 2x2x1\npalette C=cobblestone S=sand\nlayer 0\nC_\nlayer 1\nCS", new BlockPos(0, 64, 0));
        Assert.Contains(DesignChecks.Evaluate(sand, Site(w)).Issues, i => i.Code == "gravity");
        var bridge = PlanOf("size 5x1x1\npalette C=cobblestone\nlayer 0\nCCCCC", new BlockPos(1, 63, 0)); // runs off the island's east edge
        var be = DesignChecks.Evaluate(bridge, Site(w));
        Assert.Contains(be.Issues, i => i.Code == "void" && i.Level == IssueLevel.Warning);
        Assert.DoesNotContain(be.Issues, i => i.Code == "floating");
    }

    [Fact]
    public void Overlap_with_protected_and_never_break_blocks_is_an_error()
    {
        var w = Island();
        w.SetBlock(-1, 65, -2, Blocks.Planks, 0);  // part of someone's build
        w.SetBlock(1, 66, -2, Blocks.Bookshelf, 0); // marked never-break
        ProtectionVerdict V(BlockPos p, int id) => p == new BlockPos(-1, 65, -2) ? new(ProtectionLevel.Soft, "structure", "part of the main house build")
            : p == new BlockPos(1, 66, -2) ? new(ProtectionLevel.Hard, "never_break", "Fox marked it never-break") : ProtectionVerdict.None;
        var e = DesignChecks.Evaluate(PlanOf(Hut), Site(w, V));
        Assert.False(e.Pass);
        Assert.Contains(e.Issues, i => i.Code == "protected" && i.Text.Contains("main house"));
        Assert.Contains(e.Issues, i => i.Code == "never_break");
    }

    [Fact]
    public void Entrance_headroom_and_light_are_checked()
    {
        var w = Island();
        var sealedHut = Hut.Replace("CCDCC", "CCCCC").Replace("CC_CC", "CCCCC").Replace("palette C=cobblestone P=planks G=glass_pane D=door:south", "palette C=cobblestone P=planks G=glass_pane");
        Assert.Contains(DesignChecks.Evaluate(PlanOf(sealedHut), Site(w)).Issues, i => i.Code == "entrance" && i.Level == IssueLevel.Error);
        var low = PlanOf("size 3x2x3\npalette C=cobblestone\nlayer 0\nCCC\nC.C\nC.C\nlayer 1\nCCC\nCCC\nCCC");
        Assert.Contains(DesignChecks.Evaluate(low, Site(w)).Issues, i => i.Code == "headroom");
        var lit = PlanOf(Hut.Replace("facing south", "facing south\nlight"));
        Assert.Contains(DesignChecks.Evaluate(lit, Site(w)).Issues, i => i.Code == "light" && i.Level == IssueLevel.Error);
        var torch = PlanOf(Hut.Replace("facing south", "facing south\nlight").Replace("palette C", "palette T=torch C").Replace("C...C\nC...C\nCCCCC\nlayer 3", "C...C\nC.T.C\nCCCCC\nlayer 3"));
        Assert.DoesNotContain(DesignChecks.Evaluate(torch, Site(w)).Issues, i => i.Code == "light");
    }

    // ------------------------------------------------------------------ the revision loop

    private sealed class ScriptedAuthor(params string[] answers) : IBlueprintAuthor
    {
        public List<IReadOnlyList<LlmMessage>> Seen { get; } = new();
        private int _i;
        public Task<AuthorTurn> WriteAsync(IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
        {
            Seen.Add(conversation.ToList());
            var a = answers[Math.Min(_i++, answers.Length - 1)];
            return Task.FromResult(new AuthorTurn("```\n" + a + "\n```", new LlmUsage(500, 300, 0, 0), "claude-opus-5-5", 0.05m));
        }
    }

    private sealed class ScriptedCritic(params Critique[] answers) : IDesignCritic
    {
        public int Calls;
        public Task<Critique> CritiqueAsync(DesignPicture picture, VoxelPlan plan, string description, CancellationToken ct)
        {
            Assert.True(picture.Png.Length > 100);
            Assert.Contains("planned", picture.Legend);
            return Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]);
        }
    }

    private const string Floating = "size 3x3x1\npalette C=cobblestone\nlayer 0\nC__\nlayer 2\n__C";

    [Fact]
    public async Task Revision_loop_fixes_errors_then_the_critique_and_keeps_the_best()
    {
        var w = Island();
        var author = new ScriptedAuthor(Floating, Hut.Replace("G=glass_pane", "G=glass"), Hut);
        var critic = new ScriptedCritic(new Critique(false, "no windows on the front", ["no windows on the front @2,1,4"], 0.01m), new Critique(true, "ok", [], 0.01m));
        var loop = new DesignLoop(author, critic, new BlueprintPreview(), new BlueprintOptions { MaxIterations = 3 }) { Site = () => Site(w, inv: new() { [Blocks.Cobblestone] = 99, [Blocks.Planks] = 40 }) };
        var o = await loop.RunAsync(new DesignRequest("small cobble hut with windows", new BlockPos(0, 63, 0), "ground 0,63,0", "materials: cobble 99"), CancellationToken.None);
        Assert.Equal(3, o.Versions.Count);
        Assert.False(o.Versions[0].Passing);
        Assert.Equal("passed", o.Stop);
        Assert.Same(o.Versions[2], o.Best);
        Assert.Equal(2, critic.Calls);                     // only passing versions are critiqued
        // The author sees ≈100 tokens of evaluation, never an image.
        var second = author.Seen[1];
        var eval = second[^1].Text;
        Assert.Contains("v1 FAIL", eval);
        Assert.Contains("float", eval);
        Assert.True(eval.Length < 700, eval);
        Assert.DoesNotContain(author.Seen.SelectMany(c => c).SelectMany(m => m.Content), b => b is LlmImageBlock);
        Assert.Contains("no windows on the front", author.Seen[2][^1].Text);
        Assert.Equal(0.17m, o.Usd);
    }

    [Fact]
    public async Task Loop_stops_at_the_cost_cap_and_the_author_can_ask_for_the_image_once()
    {
        var w = Island();
        var author = new ScriptedAuthor(Floating, "SHOW", Floating, Floating);
        var loop = new DesignLoop(author, null, new BlueprintPreview(), new BlueprintOptions { MaxIterations = 4, MaxUsdPerDesign = 0.12m })
        { Site = () => Site(w) };
        var o = await loop.RunAsync(new DesignRequest("a tower", new BlockPos(0, 63, 0), "ground", "none"), CancellationToken.None);
        Assert.StartsWith("cost cap", o.Stop);
        Assert.False(o.Best!.Passing);
        // v1 had no picture (it failed), so SHOW got "no image": the image goes out only when there is a preview.
        Assert.DoesNotContain(author.Seen.SelectMany(c => c).SelectMany(m => m.Content), b => b is LlmImageBlock);
    }

    // ------------------------------------------------------------------ chat review

    internal static (BlueprintService S, List<string> Said, List<(long Id, int? Part)> Started, WorldState W) Service(IBlueprintAuthor author, AgentDb? db = null)
    {
        var w = Island();
        var said = new List<string>();
        var started = new List<(long, int?)>();
        var body = new FakeBody(w);
        var store = new BlueprintStore(db ?? AgentDb.Open(null));
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => Site(w, inv: new() { [Blocks.Cobblestone] = 99, [Blocks.Planks] = 40 }), Crew = () => new SingleBodyCrew(body),
            ResolveSite = (at, _) => (new SitePick(new BlockPos(0, 63, 0), at ?? "here"), null), Trusted = () => ["PoofImaFox"],
            Say = l => { lock (said) said.Add(l); return Task.CompletedTask; },
            Executor = new ConstructionExecutor(new BlueprintOptions(), store),
        };
        s.Author = author;
        s.StartBuild = (id, part) => { started.Add((id, part)); return null; };
        return (s, said, started, w);
    }

    [Fact]
    public async Task Summary_goes_to_chat_change_requests_revise_and_only_trusted_acceptance_builds()
    {
        var author = new ScriptedAuthor(Hut, Hut.Replace("size 5x4x5", "size 5x5x5").Replace("layer 3", "layer 4").Replace("layer 2", "layer 2-3"));
        var (s, said, started, _) = Service(author);
        var r = s.Design("small cobble hut", null, "kiwi");
        Assert.StartsWith("designing #1", r);
        await s.LastRun!;
        var rec = s.Store.Get(1)!;
        Assert.Equal(DesignStatus.Review, rec.Status);
        Assert.Contains("small cobble hut 5×5×4", said.Last());
        Assert.Contains("door south", said.Last());
        Assert.Contains("2 windows", said.Last());
        Assert.Contains("say 'build it'", said.Last());
        Assert.NotNull(s.Store.Picture(1));

        // An untrusted "build it" starts nothing.
        Assert.NotNull(DesignReview.OnChat(s, "kiwi", "build it"));
        Assert.Empty(started);
        Assert.Contains("only PoofImaFox", said.Last());
        // The requester asks for a change: another revision.
        Assert.NotNull(DesignReview.OnChat(s, "kiwi", "make it taller"));
        await s.LastRun!;
        rec = s.Store.Get(1)!;
        Assert.Equal(2, rec.Rev);
        Assert.Contains("5×5×5", rec.Summary);
        Assert.Contains("Change requested: make it taller", author.Seen[1][0].Text);
        // Chit-chat isn't a review line; a trusted "looks good, build" is.
        Assert.Null(DesignReview.OnChat(s, "PoofImaFox", "what are you up to?"));
        Assert.NotNull(DesignReview.OnChat(s, "PoofImaFox", "looks good, build it"));
        Assert.Equal([(1L, (int?)null)], started);
        Assert.Equal(DesignStatus.Accepted, s.Store.Get(1)!.Status);
        Assert.Equal("PoofImaFox", s.Store.Get(1)!.AcceptedBy);
    }

    [Theory]
    [InlineData("build it", true)]
    [InlineData("looks good, build", true)]
    [InlineData("ok build it now", true)]
    [InlineData("yes please build that", true)]
    [InlineData("build another one like the hut", false)]
    [InlineData("I'll build it myself later today", false)]
    public void Acceptance_must_be_explicit(string line, bool accepted) => Assert.Equal(accepted, DesignReview.IsAcceptance(line));

    [Fact]
    public async Task A_saved_design_is_reused_at_a_new_site()
    {
        var (s, _, started, _) = Service(new ScriptedAuthor(Hut));
        s.Design("hut", null, "PoofImaFox");
        await s.LastRun!;
        var rec = s.Store.Find("hut")!;
        rec.Status = DesignStatus.Built;
        s.Store.Update(rec);
        var (copy, text) = s.Reuse(rec, "3,63,0", "PoofImaFox");
        Assert.NotNull(copy);
        Assert.Equal(DesignStatus.Accepted, copy!.Status);
        Assert.StartsWith("building another", text);
        Assert.Equal(copy.Id, started.Single().Id);
        Assert.Null(s.Reuse(rec, "3,63,0", "kiwi").Copy); // untrusted: no
    }

    // ------------------------------------------------------------------ construction

    /// <summary>A body over an in-memory world: placements need a solid neighbour (like the client), metas come out right.</summary>
    internal sealed class FakeBody(WorldState w, Dictionary<int, int>? inv = null) : IBuildBody
    {
        public Dictionary<int, int> Inv { get; } = inv ?? new() { [Blocks.Cobblestone] = 256, [Blocks.Planks] = 128, [Items.WoodenDoor] = 4, [Blocks.GlassPane] = 16 };
        public List<string> Log { get; } = new();
        public int PlaceLimit = int.MaxValue;
        public CancellationTokenSource? Stop;
        public string Name => "maid_chan";
        public WorldState World => w;
        public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct)
        {
            Log.Add($"goto {stand.X},{stand.Y},{stand.Z}");
            w.Player.ServerTeleport(stand.BottomCenter, stand.Y + 1.62, 0, 0, true);
            return Task.FromResult(ActionResult.Success("ok"));
        }
        public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (PlaceLimit-- <= 0) { Stop?.Cancel(); ct.ThrowIfCancellationRequested(); }
            if (!BlockFaceExtensions.All.Any(f => BuildBlocks.Supports(w.GetBlock(step.Pos.Offset(f)))))
                return Task.FromResult(ActionResult.Fail("nothing solid on any side"));
            if (Inv.GetValueOrDefault(itemId) <= 0) return Task.FromResult(ActionResult.Fail("none left"));
            Inv[itemId]--;
            w.SetBlock(step.Pos.X, step.Pos.Y, step.Pos.Z, step.Id, step.Meta);
            w.Placed.Note(step.Pos, w.Dimension, step.Id, Name, DateTimeOffset.UtcNow);
            if (BuildBlocks.KindOf(step.Id) == BuildKind.Door) w.SetBlock(step.Pos.X, step.Pos.Y + 1, step.Pos.Z, step.Id, step.Meta | 8);
            Log.Add($"{(step.Kind == StepKind.Scaffold ? "scaffold" : "place")} {step.Pos.X},{step.Pos.Y},{step.Pos.Z}");
            return Task.FromResult(ActionResult.Success("placed"));
        }
        public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct)
        {
            Log.Add($"dig {pos.X},{pos.Y},{pos.Z}");
            int id = w.GetBlock(pos);
            w.SetBlock(pos.X, pos.Y, pos.Z, 0, 0);
            Inv[id] = Inv.GetValueOrDefault(id) + 1;
            return Task.FromResult(ActionResult.Success("dug"));
        }
        public int Count(int itemId, int damage) => Inv.GetValueOrDefault(itemId);
        public Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct) =>
            Task.FromResult(Count(itemId, damage) >= count ? null : $"missing {Items.Get(itemId).Name} (need {count}, have {Count(itemId, damage)})");
        public Task<string?> ClutchAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public bool IsProtected(BlockPos p, int id) => false;
    }

    [Fact]
    public async Task Builds_bottom_up_support_first_with_scaffolding_that_comes_down()
    {
        var w = Island(1);
        // A pillar with an arm reaching out over the void at the top: the arm's far end needs scaffold to reach it from below? No:
        // it touches the arm. The lone block at the end of the second layer touches nothing until scaffold joins it.
        var plan = PlanOf("size 4x3x1\npalette C=cobblestone\nlayer 0\nC___\nlayer 2\nC__C", new BlockPos(0, 64, 0));
        var e = DesignChecks.Evaluate(plan, Site(w));
        Assert.Contains(e.Issues, i => i.Code == "floating"); // the checker flags it; the planner can still build it with scaffold
        var steps = ConstructionPlanner.Plan(plan, w.PlanningView);
        Assert.True(steps.Scaffolds > 0);
        var places = steps.Steps.Where(s => s.Kind == StepKind.Place).ToList();
        Assert.True(places.Select(s => s.Pos.Y).SequenceEqual(places.Select(s => s.Pos.Y).OrderBy(y => y))); // bottom-up
        var body = new FakeBody(w);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("x"));
        Assert.Equal(steps.Scaffolds, report.Scaffolded);
        Assert.Equal(report.Scaffolded, report.ScaffoldRemoved);
        foreach (var b in plan.Blocks.Values) Assert.Equal(b.Id, w.GetBlock(b.Pos));
        // Only the plan's blocks are left: every scaffold block came down, the last ones dug top first.
        var digs = body.Log.Where(l => l.StartsWith("dig")).Select(l => int.Parse(l.Split(' ')[1].Split(',')[1])).ToList();
        Assert.Equal(digs.OrderByDescending(y => y), digs);
    }

    [Fact]
    public async Task Hut_is_built_with_doors_and_stairs_facing_right()
    {
        var w = Island();
        var plan = PlanOf(Hut.Replace("palette C=cobblestone", "palette S=cobblestone_stairs C=cobblestone") + "\nstairs S 1,0,1 north", new BlockPos(-2, 64, -3));
        var body = new FakeBody(w) { Inv = { [Blocks.CobblestoneStairs] = 4 } };
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("hut"));
        Assert.Empty(report.Deviations);
        // The door was walked up to from outside (facing north), then placed on the floor's top face.
        Assert.Contains(body.Log, l => l.StartsWith("goto 0,64,"));
    }

    [Fact]
    public void Split_gives_disjoint_claims_covering_every_cell()
    {
        var plan = PlanOf("size 12x3x5\npalette C=cobblestone\nhollow C 0,0,0 11,2,4", new BlockPos(0, 64, 0));
        foreach (var byLayer in new[] { false, true })
        {
            var parts = plan.Partition(3, byLayer);
            Assert.Equal(3, parts.Count);
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                {
                    Assert.False(parts[i].Claim.ConflictsWith(parts[j].Claim), $"{parts[i].Claim} vs {parts[j].Claim}");
                    Assert.Empty(parts[i].Cells.Intersect(parts[j].Cells));
                }
            Assert.Equal(plan.Blocks.Count + plan.Air.Count, parts.Sum(p => p.Cells.Count));
            Assert.All(parts, p => Assert.All(p.Cells, c => Assert.True(p.Claim.Contains(c))));
            // Balanced: no part holds more than half the blocks.
            Assert.All(parts, p => Assert.True(p.Cells.Count(plan.Blocks.ContainsKey) <= plan.Blocks.Count / 2));
        }
    }

    [Fact]
    public async Task Two_bodies_build_their_strips_at_once_under_claims()
    {
        var w = Island(8);
        var plan = PlanOf("size 12x2x3\npalette C=cobblestone\nwalls C 0,0,0 11,1,2", new BlockPos(-6, 64, -1));
        var board = new RecordingBoard();
        var ex = new ConstructionExecutor(new BlueprintOptions(), claims: board);
        var parts = plan.Partition(2);
        var a = new FakeBody(w);
        var b = new FakeBody(w);
        var reports = await Task.WhenAll(ex.RunAsync(1, plan, parts[0], a, CancellationToken.None), ex.RunAsync(1, plan, parts[1], b, CancellationToken.None));
        Assert.All(reports, r => Assert.True(r.Complete, r.Text("part")));
        Assert.Equal(2, board.Claimed.Distinct().Count());
        Assert.True(a.Log.Count(l => l.StartsWith("place")) > 0 && b.Log.Count(l => l.StartsWith("place")) > 0);
        Assert.Empty(board.Held); // released when done
    }

    private sealed class RecordingBoard : IClaimBoard
    {
        public List<ClaimKey> Claimed { get; } = new();
        public HashSet<ClaimKey> Held { get; } = new();
        public string Owner => "main";
        public bool IsFree(ClaimKey key) => true;
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
        {
            lock (Held)
            {
                if (Held.Any(h => h != key && h.ConflictsWith(key))) return Task.FromResult(ClaimResult.Refused("other", DateTimeOffset.MaxValue));
                Claimed.Add(key);
                Held.Add(key);
            }
            return Task.FromResult(ClaimResult.Granted("main", DateTimeOffset.MaxValue));
        }
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) { lock (Held) Held.Remove(key); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Missing_materials_stop_with_a_missing_item_line()
    {
        var w = Island();
        var plan = PlanOf(Hut);
        var body = new FakeBody(w, new() { [Blocks.Cobblestone] = 0 });
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.StartsWith("missing cobblestone", report.Stopped);
    }

    [Fact]
    public async Task An_interrupted_build_resumes_after_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-bp-{Guid.NewGuid():N}.db");
        try
        {
            var w = Island();
            var plan = PlanOf(Hut);
            using (var db = AgentDb.Open(path))
            {
                var store = new BlueprintStore(db);
                using var cts = new CancellationTokenSource();
                var body = new FakeBody(w) { PlaceLimit = 20, Stop = cts };
                var r1 = await new ConstructionExecutor(new BlueprintOptions { CheckpointEvery = 4 }, store).RunAsync(7, plan, BuildPart.Whole(plan), body, cts.Token);
                Assert.Equal("cancelled", r1.Stopped);
                var saved = store.GetBuild(7, 0)!;
                Assert.Equal("building", saved.State);
                Assert.Single(store.Unfinished("maid_chan"));
            }
            // A new process: same db, same world state (the server kept the blocks).
            using (var db = AgentDb.Open(path))
            {
                var store = new BlueprintStore(db);
                var body = new FakeBody(w);
                var r2 = await new ConstructionExecutor(new BlueprintOptions(), store).RunAsync(7, plan, BuildPart.Whole(plan), body, CancellationToken.None);
                Assert.True(r2.Complete, r2.Text("hut"));
                Assert.True(r2.Placed < plan.Blocks.Count - 20, $"re-placed {r2.Placed}");
                Assert.Equal("done", store.GetBuild(7, 0)!.State);
                Assert.Empty(store.Unfinished("maid_chan"));
            }
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public async Task Design_tool_is_brain_only_authoring_and_builds_need_a_trusted_go()
    {
        var (h, _) = TestBot.Create();
        var brain = h.Runtime.Tools.Definitions(ModelTier.Main).Select(d => d.Name).ToList();
        Assert.Contains("design", brain);
        Assert.DoesNotContain("build", brain);      // hidden: activity + Lua build{}
        Assert.DoesNotContain("blueprint", brain);
        Assert.DoesNotContain("design", h.Runtime.Tools.Definitions(ModelTier.Worker).Select(d => d.Name));
        var worker = await h.RunAs(ModelTier.Worker, "design", new { op = "new", what = "a hut" });
        Assert.False(worker.Ok);
        Assert.Contains("only the Opus brain", worker.Text);
        Assert.Equal("no designs yet", (await h.Run("design", new { op = "list" })).Text);
        // A saved, reviewed design: the brain can't start it for an untrusted speaker (or with nobody asking).
        var s = h.Blueprints!.Service;
        var rec = s.Store.Insert(new BlueprintRecord { Name = "hut", Description = "hut", Bot = "maid_chan", Dsl = Hut, Status = DesignStatus.Review });
        Agent.TurnContext.Current = new Agent.TurnInfo("stranger", ["stranger"], false);
        try { Assert.Contains("trusted", (await h.Run("design", new { op = "build", id = rec.Id.ToString() })).Text); }
        finally { Agent.TurnContext.Current = null; }
        Assert.Contains("waits for a trusted player's 'build it'", (await h.Runtime.Tools.ExecuteAsync("build", TestBot.J(new { bp = rec.Id.ToString() }), CancellationToken.None)).Text);
        Assert.Contains("layers", (await h.Runtime.Tools.ExecuteAsync("blueprint", TestBot.J(new { bp = "hut" }), CancellationToken.None)).Text);
    }

    [Fact]
    public async Task Resume_pending_restarts_the_build_job()
    {
        var db = AgentDb.Open(null);
        var (s, _, started, _) = Service(new ScriptedAuthor(Hut), db);
        s.Design("hut", null, "PoofImaFox");
        await s.LastRun!;
        var rec = s.Store.Get(1)!;
        rec.Status = DesignStatus.Building;
        s.Store.Update(rec);
        s.Store.SaveBuild(new BuildRecord { BlueprintId = 1, Part = 0, Owner = "maid_chan", State = "building", Placed = 12, Total = 70 });
        Assert.Contains("12/70", s.ResumePending("maid_chan"));
        Assert.Equal(1, started.Single().Id);
    }
}
