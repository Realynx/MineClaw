using MineClaw.Data;
using MineClaw.World;
using Xunit;

namespace MineClaw.Skills.Tests;

/// <summary>
/// light_area / spawn_map over the real light model: a fixture island ends with 0 cells a hostile can spawn on after dark,
/// no torch over the void, a design's spawn deck left dark, torches through the Obtain hook, and the plan near the minimum.
/// </summary>
public class LightAreaTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>A 15×15 dirt island (x/z -7..7, top y63) away from any world spawn, with a notch of void on its east side.</summary>
    private static FakeGame Island()
    {
        var g = new FakeGame();
        g.Fill(-7, 61, -7, 7, 63, 7, Blocks.Dirt);
        g.Fill(6, 61, -2, 7, 63, 2, 0); // the notch
        g.Stand(0.5, 64, 0.5);
        g.World.Time = 18000;
        return g;
    }

    private static List<BlockPos> Spawnable(WorldState w, int y = 64, int r = 9) =>
        Enumerable.Range(-r, 2 * r + 1).SelectMany(x => Enumerable.Range(-r, 2 * r + 1).Select(z => new BlockPos(x, y, z)))
            .Where(p => w.CanHostileSpawn(p)).ToList();

    private static List<BlockPos> Torches(WorldState w) =>
        w.FindBlocks(new Vec3d(0, 64, 0), 30, Blocks.Torch, 500).Select(f => f.Pos).ToList();

    [Fact]
    public async Task Lights_a_fixture_island_to_zero_spawnable_cells_never_over_the_void()
    {
        var g = Island();
        int before = Spawnable(g.World).Count;
        Assert.True(before > 150, $"{before} dark cells on the island at night");
        g.Give(Blocks.Torch, 64);
        var r = await g.Run("light_area", new { area = "-9,-9,9,9", y = "64" });
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        Assert.Empty(Spawnable(g.World));
        var torches = Torches(g.World);
        Assert.NotEmpty(torches);
        Assert.All(torches, t => Assert.False(Safety.IsOverVoid(g.World, t.X, t.Y, t.Z), $"torch over the void at {t}"));
        // A torch lights Manhattan ≤ 6 to ≥ 8: 85 cells on a flat floor; 211 floor cells need at least 3, greedy stays near it.
        Assert.InRange(torches.Count, 3, 8);
        Assert.Contains("0 spawnable cells left", r.Text);
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Spawn_map_counts_and_plans_without_placing()
    {
        var g = Island();
        var r = await g.Run("spawn_map", new { area = "-9,-9,9,9", y = "64" });
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        int n = Spawnable(g.World).Count;
        Assert.StartsWith($"area (x -9..9, z -9..9, y 64): {n} spawnable cells after dark", r.Text);
        Assert.Contains("e.g. ", r.Text);
        Assert.Contains("would light all", r.Text);
        Assert.Empty(Torches(g.World));
        // light_area report=true is the same map.
        var rep = await g.Run("light_area", new { area = "-9,-9,9,9", y = "64", report = true });
        Assert.Equal(r.Text, rep.Text);
        // By day the open island is lit by the sky: "(0 now)".
        g.World.Time = 6000;
        Assert.Contains("(0 now)", (await g.Run("spawn_map", new { area = "-9,-9,9,9", y = "64" })).Text);
    }

    [Fact]
    public async Task A_design_target_never_lights_its_own_spawn_deck()
    {
        var g = Island();
        // A mob farm: an 8×8 stone deck at y70 on four pillars over the island, a 2-high wall ring on it.
        var solid = new HashSet<BlockPos>();
        void Put(int x, int y, int z) { g.Set(x, y, z, Blocks.Cobblestone); solid.Add(new BlockPos(x, y, z)); }
        foreach (var (px, pz) in new[] { (-4, -4), (3, -4), (-4, 3), (3, 3) })
            for (int y = 64; y < 70; y++) Put(px, y, pz);
        for (int x = -4; x <= 3; x++) for (int z = -4; z <= 3; z++) Put(x, 70, z);
        for (int x = -4; x <= 3; x++) for (int z = -4; z <= 3; z++)
            if (x is -4 or 3 || z is -4 or 3) { Put(x, 71, z); Put(x, 72, z); }
        var reg = SkillRegistry.CreateDefault();
        reg.Design = id => id == "4"
            ? new DesignRegion("design #4", new BlockPos(-4, 64, -4), new BlockPos(3, 72, 3), solid, null)
            : null;
        var deck = new BlockPos(0, 71, 0);
        Assert.True(g.World.CanHostileSpawn(deck), "the deck is a spawn floor");
        g.Give(Blocks.Torch, 64);
        var r = await g.Run("light_area", new { design = "#4" }, reg);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        Assert.Contains("excluded design #4 decks", r.Text);
        Assert.Contains("no DSL roles", r.Text);
        Assert.DoesNotContain(Torches(g.World), t => t.Y >= 70);
        Assert.True(g.World.CanHostileSpawn(deck), "the deck stays dark");
        Assert.Empty(Spawnable(g.World)); // the floor of the build (y64, under and around it) is lit
        // exclude=none would light it too; spawn_map names the deck.
        var map = await g.Run("spawn_map", new { design = "4", y = "71" }, reg);
        output.WriteLine(map.ToString());
        Assert.Contains("0 spawnable cells after dark", map.Text);
        Assert.Contains("dark there", map.Text);
    }

    [Fact]
    public async Task Torches_come_through_the_obtain_hook()
    {
        var g = Island();
        var reg = SkillRegistry.CreateDefault();
        int asked = 0;
        reg.Obtain = (ctx, id, count, _) =>
        {
            asked = count;
            if (id == Blocks.Torch) g.Give(Blocks.Torch, count);
            return Task.FromResult<string?>($"crafted {count} torches from charcoal");
        };
        var r = await g.Run("light_area", new { area = "-9,-9,9,9", y = "64" }, reg);
        Assert.True(r.Ok, r.Text);
        Assert.InRange(asked, 3, 8);
        Assert.Empty(Spawnable(g.World));

        // Nothing to obtain: an honest failure with what is still needed.
        var bare = Island();
        var none = await bare.Run("light_area", new { area = "-9,-9,9,9", y = "64" });
        output.WriteLine(none.ToString());
        Assert.False(none.Ok, none.Text);
        Assert.Contains("out of torches", none.Text);
    }

    [Fact]
    public void Planner_respects_exclusions_and_reports_unlit_cells()
    {
        var g = Island();
        var region = new LightPlan.Region(-9, 64, -9, 9, 64, 9, "area");
        var ex = new LightPlan.Exclusion();
        ex.Boxes.Add(new LightPlan.Region(-7, 64, -7, -1, 64, 7, "west half"));
        var dark = LightPlan.Dark(g.World, region, ex, 8, out int excluded);
        Assert.True(excluded > 80, $"{excluded}");
        Assert.DoesNotContain(dark, d => d.X < 0);
        var plan = LightPlan.Cover(g.World, region, dark, ex, 8);
        Assert.Empty(plan.Unlit);
        Assert.DoesNotContain(plan.Torches, t => ex.Contains(t.Pos));
        Assert.All(plan.Torches, t => Assert.False(Safety.IsOverVoid(g.World, t.Pos.X, t.Pos.Y, t.Pos.Z)));
    }
}
