using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using MineClaw.World.Versioning;
using Xunit;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Underground work as one deterministic job (live 10-30 CT 114: Fox's spiral stair shaft to diamond level, 4×4 rooms off it,
/// strip mines with signs): rooms dug from where the body stands (never a walk to a buried block), the stair pattern learned
/// from the dug cells and extended identically, must-be-air cells never filled, a cave under a column stopping the dig-down,
/// seals that fill cave holes but not doorways, rooms that meet a landing, torches that keep the dig lit and strip mines that
/// take the ores they expose. Both versions: b1.8.1 (128 high) and 1.12.2 (256 high, its block table, diamonds at y ≤ 16).
/// </summary>
public class ExcavationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Fox's rule: a 3-tall column, one down, a quarter turn about the 2×2 core's centre (counter-clockwise from above,
    /// the way his shaft winds: 153,275 → 153,276 → 154,276 → 154,275).</summary>
    private const string FoxRule = "column 3 step 0,-1,0 turn -90 pivot 0.5,0.5";

    private static DigPattern Fox(int top = 60) => DigPattern.Parse(FoxRule, new BlockPos(0, top, 0), out _)!;

    private static IDisposable? Version(bool release1122) => release1122 ? GameData.Override(Release1122GameData.Instance) : null;

    /// <summary>Solid stone up to <paramref name="top"/> over bedrock, air above; dark (night) so the light check bites.</summary>
    private static FakeGame Rock(bool release1122, int top = 70) =>
        new(release1122 ? 256 : 128, (_, y, _) => y == 0 ? Blocks.Bedrock : y <= top ? Blocks.Stone : 0,
            release1122 ? Release1122PhysicsRules.Instance : null)
        { StrictReach = true };

    private static void Carve(FakeGame g, IEnumerable<BlockPos> cells) { foreach (var c in cells) g.Set(c, 0); }

    private static bool Open(WorldState w, BlockPos c) => w.GetBlock(c) is 0 or Blocks.Torch;

    private static void Night(FakeGame g) => g.World.Time = 18000;

    private static IEnumerable<BlockPos> Cuboid(BlockPos a, BlockPos b)
    {
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
            yield return new BlockPos(x, y, z);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Excavates_a_room_in_solid_rock_from_where_it_stands_never_pathing_to_a_buried_block(bool release1122)
    {
        using var _ = Version(release1122);
        int y = release1122 ? 11 : 30; // 1.12.2: diamond level
        var g = Rock(release1122);
        Carve(g, [new(0, y, 0), new(0, y + 1, 0)]); // a 1×2 pocket in the rock: where the body stands
        g.Stand(0.5, y, 0.5);
        Night(g);
        g.Give(Blocks.Torch, 16);
        g.Give(Blocks.Cobblestone, 16);
        // A 4×4×3 room two blocks east: rock between (live 02:31 "can't reach stone at 147,12,275").
        var r = await g.Run("excavate", new { box = $"3,{y},-1,6,{y + 2},2" });
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        var room = Cuboid(new(3, y, -1), new(6, y + 2, 2)).ToList();
        Assert.All(room, c => Assert.True(Open(g.World, c), $"{c} still {g.World.GetBlock(c)}"));
        Assert.Empty(g.Violations); // no goto into rock, no dig out of reach (StrictReach), nothing over the void
        Assert.Contains("access", r.Text); // the 1×2 passage it tunnelled in through
        Assert.Contains("left 0", r.Text);
        // Torches keep it lit: no cell a hostile can spawn in after dark.
        Assert.DoesNotContain(room, c => g.World.CanHostileSpawn(c));
        Assert.True(g.World.FindBlocks(new Vec3d(4, y, 0), 8, Blocks.Torch, 50).Count > 0);
        // A plain box is released: no must-be-air cells stay behind.
        Assert.Equal(0, g.World.KeepAir.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_spiral_stair_is_learned_from_its_dug_cells_and_extended_20_levels_identically(bool release1122)
    {
        using var _ = Version(release1122);
        int top = release1122 ? 40 : 60;
        var g = Rock(release1122, top: top + 4);
        var fox = Fox(top);
        Carve(g, fox.Cells(0, 8)); // Fox's demonstration: 8 repeats
        var bottom = fox.Repeat(7).MinBy(c => c.Y);
        g.Stand(bottom.X + 0.5, bottom.Y, bottom.Z + 0.5);
        Night(g);
        g.Give(Blocks.Torch, 32);
        g.Give(Blocks.Cobblestone, 16);

        var reg = SkillRegistry.CreateDefault();
        var learn = await g.Run("excavate", new { op = "learn", name = "spiral stair", box = $"-1,{top - 20},-1,2,{top + 2},2" }, reg);
        output.WriteLine(learn.ToString());
        Assert.True(learn.Ok, learn.Text);
        Assert.Contains(FoxRule, learn.Text);
        Assert.Contains("8 repeats", learn.Text);
        Assert.Contains($"from 0,{top},0", learn.Text);

        // The learned rule, re-applied by the executor 20 repeats further down.
        var r = await g.Run("excavate", new { site = "spiral stair", repeats = 20 }, reg);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        Assert.Contains("repeats 8→28", r.Text);
        // Exactly the pattern's cells are open in the shaft; every tread and the ring wall stays rock.
        var want = fox.Cells(0, 28);
        for (int x = -1; x <= 2; x++)
        for (int z = -1; z <= 2; z++)
        for (int yy = top - 32; yy <= top + 2; yy++)
        {
            var c = new BlockPos(x, yy, z);
            Assert.True(want.Contains(c) == Open(g.World, c), $"{c}: pattern {want.Contains(c)}, world {g.World.GetBlock(c)}");
        }
        // Identical: every new repeat is the one four before it, one full turn and four blocks lower.
        for (int k = 8; k < 28; k++)
            Assert.Equal(fox.Repeat(k - 4).Select(c => c.Down(4)).ToHashSet(), fox.Repeat(k).ToHashSet());
        var stored = reg.Sites.Find("Spiral_Stair");
        Assert.NotNull(stored);
        Assert.Equal(28, stored!.Repeats);
        Assert.Equal(FoxRule, stored.Rule);
        // The stair's air is must-be-air from now on, and lit.
        Assert.All(want, c => Assert.True(g.World.KeepAir.Contains(c, 0)));
        Assert.DoesNotContain(fox.Cells(8, 20), c => g.World.CanHostileSpawn(c));
        Assert.Empty(g.Violations);
    }

    [Fact]
    public void Learn_finds_the_rule_for_a_straight_stair_and_a_corridor_too()
    {
        var straight = DigPattern.Parse("column 3 step 1,-1,0", new BlockPos(0, 50, 0), out _)!;
        var l1 = DigPattern.Learn(straight.Cells(0, 10));
        Assert.NotNull(l1);
        Assert.Equal("column 3 step 1,-1,0", l1!.Pattern.Rule);
        Assert.Equal(10, l1.Repeats);
        var corridor = DigPattern.Parse("unit 0,0,0 0,1,0 step 0,0,1", new BlockPos(5, 20, 5), out _)!;
        var l2 = DigPattern.Learn(corridor.Cells(0, 12));
        Assert.NotNull(l2);
        Assert.Equal(1, Math.Abs(l2!.Pattern.Step.Z)); // either end of a level corridor is its start
        Assert.Equal(0, l2.Pattern.Step.X + l2.Pattern.Step.Y);
        Assert.Equal(2, l2.Pattern.Unit.Count);
        // Fox's spiral with a stray wall hole: still the 3-cell unit, the hole reported.
        var cells = Fox().Cells(0, 12);
        cells.Add(new BlockPos(-1, 55, 0));
        var l3 = DigPattern.Learn(cells)!;
        Assert.Equal(FoxRule, l3.Pattern.Rule);
        Assert.Single(l3.Outliers);
        // Re-applied elsewhere, turned a quarter: the same shape.
        var moved = Fox().At(new BlockPos(10, 40, 10), 90);
        Assert.Equal(Fox().Cells(0, 8).Count, moved.Cells(0, 8).Count);
        Assert.Equal(moved.Rule.Split(" turn")[0], "column 3 step 0,-1,0");
        Assert.Equal(8, moved.RepeatsTo(40 - 7 - 2));
    }

    [Fact]
    public async Task Must_be_air_cells_are_never_filled_by_goto_bridging_or_by_sealing()
    {
        // Goto bridging (the client's CanPlace rule): a 1-wide gap whose only bridge cell is a stair step.
        var g = new FakeGame();
        g.Fill(-5, 63, -1, -1, 63, 1, Blocks.Cobblestone);
        g.Fill(1, 63, -1, 5, 63, 1, Blocks.Cobblestone);
        g.Stand(-3.5, 64, 0.5);
        g.Give(Blocks.Cobblestone, 16);
        // The whole x=0 slice is a stair shaft's air (so no bridge or pillar gets round it a block higher).
        var shaft = Enumerable.Range(-12, 25).SelectMany(z => Enumerable.Range(60, 10).Select(y => new BlockPos(0, y, z))).ToList();
        g.World.KeepAir.Set("spiral stair", 0, shaft);
        var go = await g.GotoAsync(new GoalBlock(3, 64, 0), GotoOptions.Default with { AllowPlace = true }, CancellationToken.None);
        Assert.False(go.Ok, go.Message);
        Assert.All(shaft, c => Assert.Equal(0, g.World.GetBlock(c)));
        g.World.KeepAir.Remove("spiral stair");
        Assert.True((await g.GotoAsync(new GoalBlock(3, 64, 0), GotoOptions.Default with { AllowPlace = true }, CancellationToken.None)).Ok);

        // Sealing: a room dug beside an open stair never fills the stair cells it opens onto.
        var r = Rock(false);
        var fox = Fox();
        Carve(r, fox.Cells(0, 8));
        r.World.KeepAir.Set("spiral stair", 0, fox.Cells(0, 8));
        var land = fox.Repeat(6).MinBy(c => c.Y); // 1,52,1 (south-east column)
        r.Stand(land.X + 0.5, land.Y, land.Z + 0.5);
        r.Give(Blocks.Cobblestone, 32);
        r.Give(Blocks.Torch, 8);
        var dig = await r.Run("excavate", new { box = $"2,{land.Y},0,4,{land.Y + 2},2" }); // touches the stair's east side
        output.WriteLine(dig.ToString());
        Assert.True(dig.Ok, dig.Text);
        Assert.All(fox.Cells(0, 8), c => Assert.True(Open(r.World, c), $"stair cell {c} filled"));
        Assert.DoesNotContain(r.Violations, v => v.Contains("must-be-air"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cave_under_a_column_stops_the_dig_down_without_a_block_to_seal_it(bool release1122)
    {
        using var _ = Version(release1122);
        var g = Rock(release1122);
        var fox = Fox();
        Carve(g, fox.Cells(0, 4));
        var col5 = fox.Repeat(5).MinBy(c => c.Y); // repeat 5's bottom cell: 0,53,0
        Carve(g, Enumerable.Range(0, 5).Select(i => col5.Down(1 + i))); // a dungeon shaft right under it: drop 5
        var feet = fox.Repeat(3).MinBy(c => c.Y);
        g.Stand(feet.X + 0.5, feet.Y, feet.Z + 0.5);
        var reg = SkillRegistry.CreateDefault();
        Assert.True((await g.Run("excavate", new { op = "learn", name = "spiral stair", rule = FoxRule, at = "0,60,0" }, reg)).Ok);

        var r = await g.Run("excavate", new { site = "spiral stair", repeats = 6, torches = false }, reg);
        output.WriteLine(r.ToString());
        Assert.Contains("cave under it (drop 5)", r.Text);
        Assert.Equal(Blocks.Stone, g.World.GetBlock(col5)); // never opened onto the drop
        Assert.All(fox.Repeat(6).Concat(fox.Repeat(7)), c => Assert.Equal(Blocks.Stone, g.World.GetBlock(c))); // stopped descending
        Assert.Equal(5, reg.Sites.Find("spiral stair")!.Repeats);
        Assert.DoesNotContain(g.Visited, v => v.Y < feet.Y - 2);

        // With cobble on hand the floor is sealed first and the stair goes on.
        g.Give(Blocks.Cobblestone, 16);
        var r2 = await g.Run("excavate", new { site = "spiral stair", repeats = 3, torches = false }, reg);
        output.WriteLine(r2.ToString());
        Assert.True(r2.Ok, r2.Text);
        Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(col5.Down()));
        Assert.All(fox.Repeat(7), c => Assert.True(Open(g.World, c)));
        Assert.Equal(8, reg.Sites.Find("spiral stair")!.Repeats);
    }

    [Fact]
    public async Task Sealing_fills_the_cave_holes_and_missing_treads_but_not_the_rooms_doorway()
    {
        var g = Rock(false);
        var fox = Fox();
        Carve(g, fox.Cells(0, 12));
        var reg = SkillRegistry.CreateDefault();
        Assert.True((await g.Run("excavate", new { op = "learn", name = "spiral stair", rule = FoxRule, at = "0,60,0" }, reg)).Ok);
        // A cave west of the shaft, opened into the stair at -1,51,0 (column 0,0 is open 52..50 in repeat 8).
        Carve(g, Cuboid(new(-4, 50, 0), new(-2, 52, 1)));
        g.Set(-1, 51, 0, 0);
        // A missing tread under repeat 8's column (0,49,0), rock under it.
        g.Set(0, 49, 0, 0);
        // A room east of the stair through a doorway in the ring (2,53..54,1), remembered as its own dig.
        var room = Cuboid(new(2, 53, 1), new(2, 54, 1)).Concat(Cuboid(new(3, 53, 0), new(6, 55, 3))).ToList();
        Carve(g, room);
        reg.Sites.Save(new DigSite { Name = "stair room", Kind = "room", Parent = "spiral stair", Cells = room });
        g.World.KeepAir.Set("stair room", 0, room);
        var feet = fox.Repeat(5).MinBy(c => c.Y);
        g.Stand(feet.X + 0.5, feet.Y, feet.Z + 0.5);
        g.Give(Blocks.Cobblestone, 16);

        var r = await g.Run("seal_openings", new { site = "spiral stair" }, reg);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(-1, 51, 0)); // the cave hole
        Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(0, 49, 0));  // the tread
        Assert.All(room, c => Assert.Equal(0, g.World.GetBlock(c)));   // the doorway and the room stay open
        Assert.All(fox.Cells(0, 12), c => Assert.Equal(0, g.World.GetBlock(c)));
        Assert.Contains("2 openings filled", r.Text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task A_room_off_the_stair_connects_to_a_landing_without_cutting_a_stair_cell(int gap)
    {
        var g = Rock(false);
        var fox = Fox();
        Carve(g, fox.Cells(0, 12));
        var reg = SkillRegistry.CreateDefault();
        Assert.True((await g.Run("excavate", new { op = "learn", name = "spiral stair", rule = FoxRule, at = "0,60,0" }, reg)).Ok);
        var landing = fox.Repeat(6).MinBy(c => c.Y); // 1,52,1
        g.Stand(landing.X + 0.5, landing.Y, landing.Z + 0.5);
        g.Give(Blocks.Torch, 8);
        g.Give(Blocks.Cobblestone, 8);
        var treads = DigPattern.Floors(fox.Cells(0, 12));

        var r = await g.Run("dig_room", new { site = "spiral stair", y = landing.Y, gap }, reg);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        var site = reg.Sites.Find($"spiral stair room y{landing.Y}");
        Assert.NotNull(site);
        Assert.Equal(4 * 4 * 3 + 2 * gap, site!.Cells.Count);
        Assert.All(site.Cells, c => Assert.True(Open(g.World, c), $"{c} not dug"));
        Assert.All(fox.Cells(0, 12), c => Assert.True(Open(g.World, c), $"stair {c} filled"));
        Assert.All(treads.Where(t => t.Y < 60), t => Assert.Equal(Blocks.Stone, g.World.GetBlock(t)));
        Assert.Equal(landing.Y, site.Cells.Min(c => c.Y)); // floor = the landing's level
        // Walk off the step into the room: a plain path, no digging or placing.
        var far = site.Cells.Where(c => c.Y == landing.Y).MaxBy(c => c.DistanceSquaredTo(landing));
        var path = Pathfinder.FindPath(g.World, landing, new GoalBlock(far), PathOptions.Default);
        Assert.True(path.Success, $"no walk from the landing {landing} into the room at {far}: {path.Reason}");
        if (gap > 0) // a wall stays between them except the doorway
            Assert.Equal(2, site.Cells.Count(c => BlockFaceExtensions.All.Any(f => fox.Cells(0, 12).Contains(c.Offset(f)))));
        Assert.Contains("of the landing", r.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_strip_mine_branch_mines_the_ores_it_exposes_and_signs_them(bool release1122)
    {
        using var _ = Version(release1122);
        int y = release1122 ? 12 : 20;
        var g = Rock(release1122);
        var room = Cuboid(new(0, y, 0), new(3, y + 2, 3)).ToList();
        Carve(g, room);
        var reg = SkillRegistry.CreateDefault();
        reg.Sites.Save(new DigSite { Name = "v1 room y" + y, Kind = "room", Cells = room });
        g.World.KeepAir.Set("v1 room y" + y, 0, room);
        g.Stand(1.5, y, 1.5);
        Night(g);
        // Ores in the corridor walls east of the room: iron beside the main corridor (a diamond behind it: the vein), coal
        // beside the first branch.
        int iron = Blocks.ByName("iron_ore")!.Id, diamond = Blocks.ByName("diamond_ore")!.Id, coal = Blocks.ByName("coal_ore")!.Id;
        g.Set(5, y, 2, iron);      // beside the main corridor (x 4..11, z 1), between the mouth and the first branch
        g.Set(5, y, 3, diamond);   // behind the iron: only the vein leads to it
        g.Set(8, y + 1, 4, coal);  // beside the branch at x 7
        g.Give(Blocks.Torch, 32);
        g.Give(Blocks.Cobblestone, 32);
        g.Give(Items.Sign, 4);

        var r = await g.Run("strip_mine", new { site = "v1 room y" + y, dir = "east", length = 8, spacing = 3, branch = 4, signs = true }, reg);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        foreach (var (x, yy, z) in new[] { (5, y, 2), (5, y, 3), (8, y + 1, 4) })
            Assert.True(Open(g.World, new BlockPos(x, yy, z)), $"ore at {x},{yy},{z} left");
        Assert.Contains("iron ore 1", r.Text);
        Assert.Contains("diamond ore 1", r.Text);
        Assert.Contains("coal ore 1", r.Text);
        Assert.Contains(g.SignText.Values, l => l[0] == "iron ore" && l[2] == $"y{y}");
        Assert.Equal(3, g.SignText.Count);
        var strip = reg.Sites.Find($"v1 room y{y} strip east");
        Assert.NotNull(strip);
        Assert.DoesNotContain(strip!.Cells, c => g.World.CanHostileSpawn(c));
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Falling_gravel_is_dug_again_and_its_hole_sealed_and_lava_behind_a_wall_is_left_with_why()
    {
        var g = Rock(false);
        g.Gravity = true;
        Carve(g, [new(0, 30, 0), new(0, 31, 0)]);
        g.Stand(0.5, 30, 0.5);
        g.Give(Blocks.Cobblestone, 16);
        g.Give(Blocks.Torch, 8);
        // Two gravel above the corridor's ceiling at x 3; a lava pocket behind its far wall at x 5.
        g.Set(3, 32, 0, Blocks.Gravel);
        g.Set(3, 33, 0, Blocks.Gravel);
        g.Set(5, 30, 1, Blocks.Lava);
        var r = await g.Run("excavate", new { box = "1,30,0,5,31,0" });
        output.WriteLine(r.ToString());
        Assert.All(Cuboid(new(1, 30, 0), new(4, 31, 0)), c => Assert.True(Open(g.World, c), $"{c} not dug"));
        Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(3, 32, 0)); // the hole the gravel left in the ceiling, sealed
        Assert.Equal(Blocks.Stone, g.World.GetBlock(5, 30, 0));       // never opened onto the lava
        Assert.Contains("lava behind it", r.Text);
        Assert.Equal(Blocks.Lava, g.World.GetBlock(5, 30, 1));
    }

    [Fact]
    public void Excavate_signature_fits_the_catalog_and_is_found_for_underground_requests()
    {
        var full = SkillRegistry.CreateDefault();
        foreach (var name in new[] { "excavate", "seal_openings", "dig_room", "strip_mine" })
        {
            var sig = full.Specs.Single(s => s.Name == name).Signature;
            output.WriteLine(sig);
            Assert.True(sig.Length <= SkillCatalog.MaxSignatureChars, sig);
        }
        foreach (var (q, want) in new[]
                 {
                     ("dig the stairs down to diamond level", "excavate"), ("seal the holes in the stair shaft walls", "seal_openings"),
                     ("dig a 4x4 room off the stairs", "dig_room"), ("strip mine from the room", "strip_mine"),
                 })
        {
            var hits = SkillCatalog.Search(full.Specs, q, 3).Select(h => h.Name).ToList();
            output.WriteLine($"{q} -> {string.Join(", ", hits)}");
            Assert.Contains(want, hits);
        }
    }
}
