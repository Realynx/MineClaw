using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// A stepped pyramid of a falling block (sand). The DSL's
/// <c>pyramid</c> primitive lays the layers (a hand-drawn 21-wide pyramid is 11 layers of 21 rows); the design limit now holds
/// one of up to 31 wide; and crews of bodies build it from a chest without a grain of sand hanging in the air.
/// </summary>
public class PyramidTests
{
    private static string Dsl(int baseSide, string palette = "S=sand", string statement = "pyramid S 0,0,0 {0}") =>
        $"blueprint pyramid\nsize {baseSide}x{(baseSide + 1) / 2}x{baseSide}\npalette {palette}\n{string.Format(statement, baseSide)}";

    private static VoxelPlan PlanOf(string dsl, BlockPos origin)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl, origin);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return plan!;
    }

    /// <summary>Sum of the odd squares up to <paramref name="b"/> (b odd): the blocks of a solid stepped pyramid.</summary>
    private static int OddSquares(int b) => Enumerable.Range(0, (b + 1) / 2).Sum(k => (b - 2 * k) * (b - 2 * k));

    // ------------------------------------------------------------------ the primitive

    [Theory]
    [InlineData(5, 35)]
    [InlineData(7, 84)]
    [InlineData(21, 1771)]
    [InlineData(31, 5456)]
    public void A_pyramid_of_odd_base_holds_the_sum_of_odd_squares_and_each_layer_is_inset_by_one(int b, int blocks)
    {
        Assert.Equal(blocks, OddSquares(b));
        var plan = PlanOf(Dsl(b), new BlockPos(100, 64, 200));
        Assert.Equal(blocks, plan.Blocks.Count);
        Assert.All(plan.Blocks.Values, p => Assert.Equal(Blocks.Sand, p.Id));
        for (int k = 0; k < (b + 1) / 2; k++)
        {
            var layer = plan.Blocks.Keys.Where(p => p.Y == 64 + k).ToList();
            Assert.Equal((b - 2 * k) * (b - 2 * k), layer.Count);
            Assert.Equal((100 + k, 100 + b - 1 - k), (layer.Min(p => p.X), layer.Max(p => p.X)));
            Assert.Equal((200 + k, 200 + b - 1 - k), (layer.Min(p => p.Z), layer.Max(p => p.Z)));
        }
        Assert.Single(plan.Blocks.Keys.Where(p => p.Y == 64 + (b - 1) / 2)); // an odd base ends in one block
    }

    [Fact]
    public void On_release_1_12_2_data_sand_and_sandstone_pyramids_parse_and_pass_the_design_check()
    {
        // With 1.12.2 data (world height 256), sand and sandstone resolve; a sand pyramid on solid ground has no
        // gravity error, and its bill is counted in sand items.
        using var data = MineClaw.Data.Versioning.GameData.Override(MineClaw.Data.Versioning.Release1122GameData.Instance);
        foreach (var (block, id) in new[] { ("sand", Blocks.Sand), ("sandstone", Blocks.Sandstone) })
        {
            var (plan, issues) = BlueprintNormalizer.FromText(Dsl(11, $"S={block}"), new BlockPos(-5, 64, -5), worldHeight: 256);
            Assert.NotNull(plan);
            Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
            Assert.Equal(OddSquares(11), plan!.Blocks.Count);
            Assert.All(plan.Blocks.Values, p => Assert.Equal((id, 0), (p.Id, p.Meta)));
        }
        var w = BlueprintTests.Island(8);
        var sand = PlanOf(Dsl(11), new BlockPos(-5, 64, -5));
        var eval = DesignChecks.Evaluate(sand, BlueprintTests.Site(w, inv: new() { [Blocks.Sand] = 400 }));
        Assert.DoesNotContain(eval.Issues, i => i.Level == IssueLevel.Error);
        Assert.Equal(OddSquares(11), Assert.Single(eval.Bill).Need);
    }

    [Fact]
    public void An_even_base_ends_in_a_two_by_two_top()
    {
        var plan = PlanOf(Dsl(6), new BlockPos(0, 64, 0));
        Assert.Equal(36 + 16 + 4, plan.Blocks.Count);
        Assert.Equal(4, plan.Blocks.Keys.Count(p => p.Y == 66));
    }

    [Fact]
    public void Every_sand_block_of_a_pyramid_rests_on_a_block_of_the_layer_below()
    {
        // Sand and gravel fall when nothing is under them: the layer above must lie wholly on the one below, so the design checker's
        // "gravity" error never fires on the pyramid itself (on solid ground).
        var plan = PlanOf(Dsl(15), new BlockPos(-7, 64, -7));
        Assert.All(plan.Blocks.Keys.Where(p => p.Y > 64), p => Assert.True(plan.Blocks.ContainsKey(p.Down()), $"{p} hangs"));
        var w = BlueprintTests.Island(12);
        var eval = DesignChecks.Evaluate(plan, BlueprintTests.Site(w, inv: new() { [Blocks.Sand] = 5000 }));
        Assert.DoesNotContain(eval.Issues, i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void A_core_fills_everything_deeper_than_two_rows_and_the_skin_still_rests_on_something()
    {
        // base 9, sand skin + dirt core: layers 9,7,5,3,1 → cores 5², 3², 1², none, none = 25 + 9 + 1 = 35 dirt of 165.
        var plan = PlanOf(Dsl(9, "S=sand D=dirt", "pyramid S 0,0,0 {0} core:D"), new BlockPos(0, 64, 0));
        Assert.Equal(OddSquares(9), plan.Blocks.Count);
        Assert.Equal(35, plan.Blocks.Values.Count(b => b.Id == Blocks.Dirt));
        Assert.Equal(130, plan.Blocks.Values.Count(b => b.Id == Blocks.Sand));
        // The core never shows: every dirt block is two rows inside its layer, so the outside is sand all round.
        Assert.All(plan.Blocks.Values.Where(b => b.Id == Blocks.Dirt), b =>
        {
            int k = b.Pos.Y - 64;
            Assert.True(b.Pos.X >= k + 2 && b.Pos.X <= 8 - k - 2 && b.Pos.Z >= k + 2 && b.Pos.Z <= 8 - k - 2);
        });
        // Nothing hangs: every block above the ground has a block (sand or dirt) under it.
        Assert.All(plan.Blocks.Keys.Where(p => p.Y > 64), p => Assert.True(plan.Blocks.ContainsKey(p.Down()), $"{p} hangs"));
        // A sand block resting on core: the second row of each layer sits on the core ring of the layer below.
        Assert.Contains(plan.Blocks.Values.Where(b => b.Pos.Y > 64), b => b.Id == Blocks.Sand && plan.Blocks[b.Pos.Down()].Id == Blocks.Dirt);
    }

    [Fact]
    public void A_pyramid_that_does_not_fit_its_size_line_says_so()
    {
        var (plan, issues) = BlueprintNormalizer.FromText("blueprint p\nsize 9x3x9\npalette S=sand\npyramid S 0,0,0 9", new BlockPos(0, 64, 0));
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.Code == "bounds" && i.Text.Contains("pyramid"));
        Assert.True(plan == null || plan.Blocks.Count == 0);
        var (_, bad) = BlueprintParser.Parse("size 3x2x3\npalette S=sand\npyramid S 0,0,0 zero");
        Assert.Contains(bad, i => i.Code == "op" && i.Text.StartsWith("pyramid:"));
        var (_, noCore) = BlueprintParser.Parse("size 3x2x3\npalette S=sand\npyramid S 0,0,0 3 core:SS");
        Assert.Contains(noCore, i => i.Code == "op" && i.Text.Contains("core:Y"));
        var (_, undefined) = BlueprintNormalizer.FromText("size 5x3x5\npalette S=sand\npyramid S 0,0,0 5 core:Q", new BlockPos(0, 64, 0));
        Assert.Contains(undefined, i => i.Code == "palette" && i.Text.Contains("'Q'"));
    }

    [Fact]
    public void The_author_is_told_about_the_pyramid_primitive()
    {
        Assert.Contains("pyramid X x,y,z base [core:Y]", BlueprintPrompts.System);
    }

    // ------------------------------------------------------------------ the size a design may have

    [Fact]
    public void A_design_may_hold_the_giant_pyramid_a_thirty_one_wide_stepped_pyramid_is_5456_blocks()
    {
        Assert.True(BlueprintNormalizer.MaxBlocks >= 5456);
        var (plan, issues) = BlueprintNormalizer.FromText(Dsl(31), new BlockPos(0, 64, 0));
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Code == "size");
        Assert.Equal(5456, plan!.Blocks.Count);
        Assert.Equal("31×31×16", plan.Blueprint.SizeText);
        // Past the parser's side limit (64 a side, 32 high since the massive pyramid) it is still refused.
        var (_, big) = BlueprintParser.Parse(Dsl(65));
        Assert.Contains(big, i => i.Code == "size");
    }

    [Fact]
    public void The_design_bill_for_a_sand_pyramid_is_all_sand()
    {
        var w = BlueprintTests.Island(12);
        var plan = PlanOf(Dsl(21), new BlockPos(-10, 64, -10));
        var eval = DesignChecks.Evaluate(plan, BlueprintTests.Site(w, inv: new() { [Blocks.Sand] = 300 }, stored: new() { [Blocks.Sand] = 1000 }));
        var line = Assert.Single(eval.Bill);
        Assert.Equal((1771, 300, 1000, 471), (line.Need, line.Have, line.Stored, line.Missing));
    }

    // ------------------------------------------------------------------ built by a crew

    private static readonly BlockPos At7 = new(-3, 64, -3);

    private static (SimCrewLink Link, List<SimBuildBody> Bodies, SimChest Chest) SandCrew(int bodies, int chestSand, int bag)
    {
        var link = new SimCrewLink();
        var chest = new SimChest(new() { [(Blocks.Sand, 0)] = chestSand }, bag);
        var starts = new[] { (0, 5), (-7, 6), (7, 6) };
        var names = new[] { "maid_chan", "maid_chan_67", "maid_chan_69" };
        var list = new List<SimBuildBody>();
        for (int i = 0; i < bodies; i++)
        {
            var w = BlueprintTests.Island(8);
            w.Player.ServerTeleport(new Vec3d(starts[i].Item1 + 0.5, 64, starts[i].Item2 + 0.5), 65.62, 0, 0, true);
            list.Add(new SimBuildBody(w, new(), names[i], link) { Chest = chest });
        }
        return (link, list, chest);
    }

    [Fact]
    public async Task Two_bodies_build_a_sand_pyramid_layer_by_layer_from_a_chest_and_no_sand_falls()
    {
        var (link, bodies, chest) = SandCrew(2, chestSand: OddSquares(7), bag: 30); // 84 sand in the chest, 30 fit in a bag
        var plan = PlanOf(Dsl(7), At7);
        using var db = AgentDb.Open(null);
        var report = await CrewBuildTests.Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db))).RunAsync(1, plan, bodies, CrewBuildTests.Soon());
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        var w = bodies[0].World;
        foreach (var b in plan.Blocks.Values) Assert.Equal(Blocks.Sand, w.GetBlock(b.Pos));
        Assert.Equal(84, plan.Blocks.Count(b => w.GetBlock(b.Key) == Blocks.Sand));
        Assert.Empty(bodies.SelectMany(b => b.Falls));                                  // not one grain placed over air
        Assert.Empty(link.Violations);
        Assert.All(bodies, b => Assert.NotEmpty(b.Placed));                             // both bodies built
        Assert.Equal(0, report.Rebalances);                                             // in parallel: no lane gave up to the other
        Assert.True(chest.Withdrawals >= 4, $"84 sand in 30-block bags: several trips to the chest, got {chest.Withdrawals}");
        Assert.Equal(0, chest.Stock[(Blocks.Sand, 0)]);
        Assert.Equal(84, bodies.Sum(b => b.Placed.Count));
        // Layer by layer: for each column, the block below a sand block was put there first (global event order).
        var first = link.FirstPlaced();
        foreach (var (pos, at) in first.Where(kv => plan.Blocks.ContainsKey(kv.Key) && kv.Key.Y > 64))
            Assert.True(first[pos.Down()] < at, $"{pos} placed before the sand under it");
    }

    [Fact]
    public async Task One_body_builds_a_sand_pyramid_with_a_dirt_core_dirt_inside_sand_outside()
    {
        var (link, bodies, _) = SandCrew(1, 0, 100);
        foreach (var b in bodies) { b.Inv[(Blocks.Sand, 0)] = 200; b.Inv[(Blocks.Dirt, 0)] = 200; }
        var plan = PlanOf(Dsl(9, "S=sand D=dirt", "pyramid S 0,0,0 {0} core:D"), new BlockPos(-4, 64, -4));
        using var db = AgentDb.Open(null);
        var report = await CrewBuildTests.Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db))).RunAsync(2, plan, bodies, CrewBuildTests.Soon());
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        foreach (var b in plan.Blocks.Values) Assert.Equal(b.Id, bodies[0].World.GetBlock(b.Pos));
        Assert.Empty(bodies.SelectMany(b => b.Falls));
        Assert.Equal(35, 200 - bodies[0].Inv[(Blocks.Dirt, 0)]);   // the core
        Assert.Equal(130, 200 - bodies[0].Inv[(Blocks.Sand, 0)]);  // the skin
        Assert.Empty(link.Violations);
    }
}

/// <summary>
/// Two real MinecraftClients build a base-5 sand pyramid at once over the in-process fake server, which makes sand fall when it is
/// placed over air (BlockFalling): the pyramid only stands if every grain went on top of something.
/// </summary>
public class PyramidFakeServerTests
{
    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    private static MinecraftClient Client(FakeServer server, string name) => new(new ClientOptions
    {
        Username = name, TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
        RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
    });

    [Fact]
    public async Task Two_clients_build_a_base_5_sand_pyramid_and_every_grain_stays_where_it_was_put()
    {
        var a = new FakeServer { ConsumeOnPlace = true, FallingBlocks = true, SpawnAt = (2.5, 64, 8.5) };
        var b = new FakeServer { ConsumeOnPlace = true, FallingBlocks = true, SpawnAt = (14.5, 64, 8.5), BlockLock = a.BlockLock };
        a.Mirror = (x, y, z, id, m) => b.SetBlock(x, y, z, id, m);
        b.Mirror = (x, y, z, id, m) => a.SetBlock(x, y, z, id, m);
        foreach (var s in new[] { a, b })
        {
            for (int x = 0; x <= 16; x++) for (int z = 0; z <= 16; z++) s.Blocks[(x, 63, z)] = Blocks.Stone; // a 17×17 stone platform
            s.Inventory[36] = new ItemStack(Blocks.Sand, 20, 0);
        }
        using var _a = a;
        using var _b = b;
        await using var ca = Client(a, "maid_chan");
        await using var cb = Client(b, "maid_chan_67");
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await ca.ConnectAsync(cts.Token);
            await cb.ConnectAsync(cts.Token);
        }
        Assert.True(await Until(() => ca.World.Player.OnGround && cb.World.Player.OnGround
                                      && ca.World.Inventory.CountItem(Blocks.Sand) == 20 && cb.World.Inventory.CountItem(Blocks.Sand) == 20, 4000));

        var (plan, issues) = BlueprintNormalizer.FromText("blueprint pyramid\nsize 5x3x5\npalette S=sand\npyramid S 0,0,0 5", new BlockPos(6, 64, 6));
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal(35, plan!.Blocks.Count);
        using var db = AgentDb.Open(null);
        var crew = new CrewExecutor(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)))
        {
            Poll = TimeSpan.FromMilliseconds(5), ParkAfter = TimeSpan.FromMilliseconds(200), WaitLimit = TimeSpan.FromSeconds(15),
        };
        IBuildBody[] bodies = [new GameBuildBody(() => new MinecraftGameClient(ca)), new GameBuildBody(() => new MinecraftGameClient(cb))];
        using var run = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var report = await crew.RunAsync(1, plan, bodies, run.Token);
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        Assert.Equal(2, report.Rounds[0].Lanes.Count);
        Assert.Equal(0, report.Rebalances);
        foreach (var blk in plan.Blocks.Values)
        {
            Assert.True(a.Blocks.TryGetValue((blk.Pos.X, blk.Pos.Y, blk.Pos.Z), out var id) && id == Blocks.Sand, $"no sand at {blk.Pos} (it fell?)");
            Assert.True(b.Blocks.TryGetValue((blk.Pos.X, blk.Pos.Y, blk.Pos.Z), out var id2) && id2 == Blocks.Sand, $"the other view lacks {blk.Pos}");
        }
        Assert.Equal(35, a.Blocks.Count(kv => kv.Value == Blocks.Sand));   // none fell elsewhere, none stray
        Assert.Equal(35, 40 - ca.World.Inventory.CountItem(Blocks.Sand) - cb.World.Inventory.CountItem(Blocks.Sand));
        Assert.True(ca.World.Inventory.CountItem(Blocks.Sand) < 20 && cb.World.Inventory.CountItem(Blocks.Sand) < 20, "both bodies placed some");
    }
}
