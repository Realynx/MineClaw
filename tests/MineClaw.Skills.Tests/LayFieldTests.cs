using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// lay_field builds an irrigated field in one call without improvised trenches or leaking water.
/// The fake runs the 1.12 water model (<see cref="FakeGame.Fluids"/>:
/// flowing levels, falling, gap cells beside 2 sources becoming sources, scooped infinite cells coming back).
/// </summary>
public class LayFieldTests
{
    private const int Y = 64;
    private static readonly BlockPos Corner = new(-10, Y, -10);

    /// <summary>Ground top at <paramref name="h"/>(x,z): dirt below, grass on top, air above, stone far below.</summary>
    private static FakeGame Land(Func<int, int, int>? h = null)
    {
        h ??= (_, _) => Y;
        var g = new FakeGame(128, (x, yy, z) => yy > h(x, z) ? 0 : yy == h(x, z) ? Blocks.Grass : yy >= h(x, z) - 3 ? Blocks.Dirt : Blocks.Stone) { Fluids = true };
        g.Stand(3.5, Y + 1, 3.5);
        return g;
    }

    private static void Kit(FakeGame g, int waterBuckets = 2, bool fence = true)
    {
        g.Give(Items.StoneHoe, 1);
        if (waterBuckets > 0) g.Give(Items.WaterBucket, waterBuckets);
        if (fence) { g.Give(Blocks.Fence, 64); g.Give(Blocks.Fence, 40); g.Give(Blocks.FenceGate, 2); g.Give(Blocks.Torch, 16); }
    }

    private static object Args(int width = 9, int length = 10, string axis = "x", bool level = false, bool fence = true, string corner = "-10,64,-10") =>
        new { corner, width, length, axis, level, fence };

    /// <summary>The independently computed layout: cell (u along the length, v across) of a field with this corner and axis.</summary>
    private static BlockPos At(int u, int v, bool alongX = true) => alongX ? new(Corner.X + u, Y, Corner.Z + v) : new(Corner.X + v, Y, Corner.Z + u);

    private static List<BlockPos> AllWater(FakeGame g) =>
        g.World.FindBlocks(new Vec3d(-6, Y, -6), 40, (id, _) => BlockShapes.IsWater(id), 8192).Select(f => f.Pos).ToList();

    private static void AssertStandardField(FakeGame g, int width, int length, int[] trenchCols, bool alongX = true, bool fence = true)
    {
        var w = g.World;
        var trench = new HashSet<BlockPos>();
        foreach (int v in trenchCols) for (int u = 0; u < length; u++) trench.Add(At(u, v, alongX));
        foreach (var c in trench) Assert.True(FluidRules.IsSource(w, c, false), $"trench {c} is {w.GetBlock(c)}/{w.GetMeta(c)}, not a source");
        // Every other cell is farmland, within 4 of water, same level, and nothing but crops/air above.
        for (int u = 0; u < length; u++)
        for (int v = 0; v < width; v++)
        {
            var c = At(u, v, alongX);
            if (trench.Contains(c)) continue;
            Assert.Equal(Blocks.Farmland, w.GetBlock(c));
            Assert.True(trenchCols.Min(t => Math.Abs(t - v)) <= 4, $"{c} is {trenchCols.Min(t => Math.Abs(t - v))} from water");
            Assert.Equal(0, w.GetBlock(c.Up()));
        }
        // The only water in the world is the trench: nothing leaked, nothing flows (all sources).
        Assert.Empty(AllWater(g).Where(p => !trench.Contains(p)));
        if (!fence) return;
        int posts = 0, gates = 0, torches = 0;
        for (int u = -1; u <= length; u++)
        for (int v = -1; v <= width; v++)
        {
            if (u >= 0 && u < length && v >= 0 && v < width) continue;
            var p = At(u, v, alongX).Up();
            int id = w.GetBlock(p);
            Assert.True(id is Blocks.Fence or Blocks.FenceGate, $"no fence at {p} ({id})");
            if (id == Blocks.FenceGate) gates++; else posts++;
            if (w.GetBlock(p.Up()) == Blocks.Torch) torches++;
        }
        Assert.Equal(1, gates);
        Assert.Equal(2 * (width + length) + 4, posts + gates);
        Assert.InRange(torches, (2 * (width + length) + 4) / 7, (2 * (width + length) + 4) / 5 + 1); // about one every 6 posts
    }

    [Fact]
    public async Task Lays_the_standard_field_on_flat_grass_x_axis()
    {
        var g = Land();
        Kit(g);
        var r = await g.Run("lay_field", Args());
        Assert.True(r.Ok, r.Text);
        AssertStandardField(g, 9, 10, [4]);
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Lays_the_field_along_z_too()
    {
        var g = Land();
        Kit(g);
        var r = await g.Run("lay_field", Args(width: 9, length: 8, axis: "z"));
        Assert.True(r.Ok, r.Text);
        AssertStandardField(g, 9, 8, [4], alongX: false);
    }

    [Fact]
    public async Task A_17_wide_field_gets_two_trenches_so_every_row_is_within_4_of_water()
    {
        var g = Land();
        Kit(g);
        var r = await g.Run("lay_field", Args(width: 17, length: 6));
        Assert.True(r.Ok, r.Text);
        AssertStandardField(g, 17, 6, [4, 12]);
    }

    [Fact]
    public async Task Without_level_a_bumpy_rectangle_is_refused_up_front_naming_the_worst_cells_and_nothing_is_touched()
    {
        var g = Land((x, z) => x is >= -6 and <= -4 && z is >= -8 and <= -6 ? Y + 2 : Y);
        Kit(g);
        var r = await g.Run("lay_field", Args());
        Assert.False(r.Ok);
        Assert.Contains("aren't at y64", r.Text);
        Assert.Contains("2 above", r.Text);
        Assert.Contains("level:true", r.Text);
        Assert.DoesNotContain(g.Log, l => l.StartsWith("dig") || l.StartsWith("place") || l.StartsWith("use"));
    }

    [Fact]
    public async Task Level_true_digs_the_high_cells_down_and_fills_the_low_ones_up_then_lays_the_field()
    {
        var g = Land((x, z) => x is >= -6 and <= -4 && z is >= -8 and <= -6 ? Y + 2 : x is >= -3 and <= -2 && z is >= -4 and <= -3 ? Y - 2 : Y);
        Kit(g);
        g.Give(Blocks.Dirt, 30);
        var r = await g.Run("lay_field", Args(level: true));
        Assert.True(r.Ok, r.Text);
        AssertStandardField(g, 9, 10, [4]);
        Assert.Contains("levelled", r.Text);
        // The dip was filled with dirt up to the farm layer (it is farmland now), the mound is gone above it.
        Assert.Equal(Blocks.Farmland, g.World.GetBlock(-3, Y, -3));
        Assert.Equal(Blocks.Dirt, g.World.GetBlock(-3, Y - 1, -3));
        Assert.Equal(0, g.World.GetBlock(-5, Y + 1, -7));
        Assert.Equal(0, g.World.GetBlock(-5, Y + 2, -7));
    }

    [Fact]
    public async Task Level_true_on_flat_grass_never_digs_or_replaces_a_cell_already_at_the_target_height()
    {
        // Grass already at the target height is tillable ground and must be left alone.
        var g = Land();
        Kit(g);
        var r = await g.Run("lay_field", Args(level: true));
        Assert.True(r.Ok, r.Text);
        var trenchCells = Enumerable.Range(0, 10).Select(u => At(u, 4)).Select(p => $"dig {p}").ToHashSet();
        Assert.All(g.Log.Where(l => l.StartsWith("dig")), l => Assert.Contains(l, trenchCells));              // only the trench is dug
        var placedAt = g.Log.Where(l => l.StartsWith("place")).Select(l => System.Text.RegularExpressions.Regex.Matches(l, "-?\\d+").Select(m => int.Parse(m.Value)).ToArray()).ToList();
        Assert.NotEmpty(placedAt);                                                                              // the fence went up ...
        Assert.All(placedAt, n => Assert.True(n[1] > Y, $"placed at y{n[1]}"));                                 // ... nothing was laid at or under the farm layer
        Assert.DoesNotContain("levelled", r.Text);
    }

    [Fact]
    public async Task Level_true_refuses_a_drop_over_the_limit_and_water_in_the_field_without_changes()
    {
        var steep = Land((x, z) => x == -5 && z == -5 ? Y + 6 : Y);
        Kit(steep);
        var r1 = await steep.Run("lay_field", Args(level: true));
        Assert.False(r1.Ok);
        Assert.Contains("6 above y64 (more than 4)", r1.Text);
        Assert.DoesNotContain(steep.Log, l => l.StartsWith("dig") || l.StartsWith("place"));

        var wet = Land();
        Kit(wet);
        wet.Set(-5, Y, -8, Blocks.Water);                   // a pond inside the rectangle, off the trench column
        var r2 = await wet.Run("lay_field", Args(level: true));
        Assert.False(r2.Ok);
        Assert.Contains("water at -5,64,-8", r2.Text);

        var lava = Land();
        Kit(lava);
        lava.Set(-5, Y, -8, Blocks.Lava);
        var r3 = await lava.Run("lay_field", Args());
        Assert.False(r3.Ok);
        Assert.Contains("lava at -5,64,-8", r3.Text);
    }

    [Fact]
    public async Task Protected_cells_refuse_the_levelling_before_anything_is_dug()
    {
        var g = Land((x, z) => x == -5 && z == -5 ? Y + 2 : Y);
        Kit(g);
        g.ProtectedBlocks.Add(new BlockPos(-5, Y + 2, -5));
        var r = await g.Run("lay_field", Args(level: true));
        Assert.False(r.Ok);
        Assert.Contains("-5,66,-5", r.Text);
        Assert.DoesNotContain(g.Log, l => l.StartsWith("dig"));
    }

    [Fact]
    public async Task A_dip_under_the_trench_end_is_filled_before_any_water_goes_in_so_nothing_leaks()
    {
        // The ring cell beyond the trench's first cell is a 3-deep hole: poured water would run out of the trench and down into it.
        var g = Land((x, z) => x == -11 && z == -6 ? Y - 3 : Y);
        Kit(g);
        g.Give(Blocks.Dirt, 10);
        var r = await g.Run("lay_field", Args());
        Assert.True(r.Ok, r.Text);
        AssertStandardField(g, 9, 10, [4]);                              // no water anywhere but the trench
        Assert.True(BlockShapes.HasCollision(g.World, -11, Y, -6));      // the hole is capped at the farm layer
    }

    [Fact]
    public async Task A_hole_under_a_trench_cell_is_closed_before_it_is_poured()
    {
        var g = Land();
        Kit(g);
        g.Fill(-7, Y - 1, -6, -7, Y - 3, -6, 0);                         // a shaft under trench cell u=3
        g.Give(Blocks.Dirt, 10);
        var r = await g.Run("lay_field", Args());
        Assert.True(r.Ok, r.Text);
        AssertStandardField(g, 9, 10, [4]);
        Assert.True(BlockShapes.HasCollision(g.World, -7, Y - 1, -6));
    }

    [Fact]
    public async Task Water_that_turns_up_outside_the_trench_stops_the_job_and_is_named()
    {
        var g = Land();
        Kit(g);
        // A surprise the closed trench should make impossible (a server-side quirk): the first drop also wets a cell beyond the fence line.
        g.Changed = (p, id, _) => { if (id == Blocks.Water && p == At(0, 4)) g.World.SetBlock(-12, Y, -6, Blocks.Water, 0); };
        var r = await g.Run("lay_field", Args());
        Assert.False(r.Ok);
        Assert.Contains("stray water outside the trench at -12,64,-6", r.Text);
        Assert.DoesNotContain(g.Log, l => l.StartsWith("use ") && !int.TryParse(l[4..], out _)); // it stopped before hoeing anything (a bare "use <id>" is a bucket)
    }

    [Fact]
    public async Task One_water_bucket_and_no_infinite_water_is_refused_up_front()
    {
        var g = Land();
        Kit(g, waterBuckets: 1);
        var r = await g.Run("lay_field", Args());
        Assert.False(r.Ok);
        Assert.Contains("2 water buckets", r.Text);
        Assert.DoesNotContain(g.Log, l => l.StartsWith("dig") || l.StartsWith("place") || l.StartsWith("use"));
    }

    [Fact]
    public async Task One_water_bucket_plus_a_pond_is_enough_the_pond_stays_and_refills_come_from_it_then_the_trench()
    {
        var g = Land();
        Kit(g, waterBuckets: 1);
        g.Give(Items.Bucket, 1);
        foreach (var (x, z) in new[] { (10, 10), (11, 10), (10, 11), (11, 11) }) g.Set(x, Y, z, Blocks.Water);   // a 2x2 pond: infinite
        var r = await g.Run("lay_field", Args());
        Assert.True(r.Ok, r.Text);
        var trench = Enumerable.Range(0, 10).Select(u => At(u, 4)).ToHashSet();
        foreach (var c in trench) Assert.True(FluidRules.IsSource(g.World, c, false), $"{c}");
        Assert.Empty(AllWater(g).Where(p => !trench.Contains(p) && !(p.X is 10 or 11 && p.Z is 10 or 11)));
        foreach (var (x, z) in new[] { (10, 10), (11, 10), (10, 11), (11, 11) }) Assert.True(FluidRules.IsSource(g.World, new BlockPos(x, Y, z), false), $"pond {x},{z}: {g.World.GetBlock(x, Y, z)}");
    }

    [Fact]
    public async Task Too_few_fences_is_refused_before_anything_changes_and_fence_false_lays_the_field_without()
    {
        var g = Land();
        Kit(g, fence: false);
        g.Give(Blocks.Fence, 5);
        var r = await g.Run("lay_field", Args());
        Assert.False(r.Ok);
        Assert.Contains("short of", r.Text);
        Assert.Contains("fence", r.Text);
        Assert.DoesNotContain(g.Log, l => l.StartsWith("dig") || l.StartsWith("place") || l.StartsWith("use"));

        var r2 = await g.Run("lay_field", Args(fence: false));
        Assert.True(r2.Ok, r2.Text);
        AssertStandardField(g, 9, 10, [4], fence: false);
        Assert.Equal(5, g.World.Inventory.CountItem(Blocks.Fence));
    }

    [Fact]
    public async Task Rerunning_a_finished_field_does_nothing_and_a_half_built_one_continues_without_rehoeing_or_repouring()
    {
        var done = Land();
        Kit(done);
        Assert.True((await done.Run("lay_field", Args())).Ok);
        int logged = done.Log.Count;
        var again = await done.Run("lay_field", Args());
        Assert.True(again.Ok, again.Text);
        Assert.Contains("nothing new", again.Text);
        Assert.DoesNotContain(done.Log.Skip(logged), l => l.StartsWith("dig") || l.StartsWith("place") || l.StartsWith("use"));

        // Half built: the first 4 trench cells dug and the first two drops in, rows v=0..2 tilled.
        var g = Land();
        Kit(g);
        for (int u = 0; u < 4; u++) g.Set(At(u, 4), 0);
        g.Set(At(0, 4), Blocks.Water);
        g.Set(At(2, 4), Blocks.Water);
        var pretilled = new List<BlockPos>();
        for (int v = 0; v <= 2; v++) for (int u = 0; u < 10; u++) { g.Set(At(u, v), Blocks.Farmland); pretilled.Add(At(u, v)); }
        var r = await g.Run("lay_field", Args());
        Assert.True(r.Ok, r.Text);
        AssertStandardField(g, 9, 10, [4]);
        foreach (var p in pretilled) Assert.DoesNotContain(g.Log, l => l.StartsWith($"use {p} "));       // farmland never re-hoed
        Assert.DoesNotContain(g.Log, l => l.StartsWith($"dig {At(0, 4)}") || l.StartsWith($"dig {At(2, 4)}"));
        // The pre-poured sources were never poured again: water buckets used = pours still needed (u = 4, 6, 8, 9).
        Assert.Equal(4, g.Log.Count(l => l == $"use {Items.WaterBucket}"));
    }

    [Fact]
    public void The_brain_finds_it_by_the_phrases_it_will_use()
    {
        var specs = SkillRegistry.CreateDefault().Specs;
        foreach (var q in new[] { "a big wheat field", "standard field", "farm 17 by 40", "lay a wheat field with a water trench", "build me a 17 wide field" })
            Assert.Equal("lay_field", SkillCatalog.Search(specs, q).First().Name);
        Assert.True(SkillRegistry.CreateDefault().TryGet("standard_field", out _));
    }
}
