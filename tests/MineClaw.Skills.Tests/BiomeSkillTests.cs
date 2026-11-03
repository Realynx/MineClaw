using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>find_biome and scout_biome: a far desert found from the biome memory, then by walking out.</summary>
public class BiomeSkillTests
{
    private const int Plains = 1, Desert = 2;

    private static byte[] Chunk(int biome) { var b = new byte[256]; Array.Fill(b, (byte)biome); return b; }

    private static void Desert4x4(WorldState w, int cx0, int cz0)
    {
        for (int cx = cx0; cx < cx0 + 4; cx++)
        for (int cz = cz0; cz < cz0 + 4; cz++) w.SetBiomes(cx, cz, Chunk(Desert));
    }

    [Fact]
    public async Task Find_biome_names_the_nearest_patch_by_distance_direction_and_size()
    {
        var g = LiquidTests.Island(4);                       // me at 0.5,64,0.5
        for (int cx = -3; cx <= 3; cx++) for (int cz = -3; cz <= 3; cz++) g.World.SetBiomes(cx, cz, Chunk(Plains));
        Desert4x4(g.World, 40, -2);                         // x 640..703, z -32..31: east, ~650 m
        Desert4x4(g.World, -90, -90);                       // far north-west
        var r = await g.Run("find_biome", new { biome = "desert" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("nearest 6", r.Text);                // 640+ m
        Assert.Contains("east", r.Text);
        Assert.Contains("16 chunks", r.Text);
        Assert.Contains("~4096 columns", r.Text);
        Assert.Contains("x 640..703 z -32..31", r.Text);
        Assert.Contains("1 other patch", r.Text);
    }

    [Fact]
    public async Task Find_biome_can_ask_for_big_patches_and_reports_when_there_is_none()
    {
        var g = LiquidTests.Island(4);
        g.World.SetBiomes(20, 0, Chunk(Desert));             // a one-chunk speck is no sand source
        Desert4x4(g.World, -60, 0);
        var big = await g.Run("find_biome", new { biome = "desert", min_chunks = 8 });
        Assert.True(big.Ok, big.Text);
        Assert.Contains("west", big.Text);
        Assert.Contains("16 chunks", big.Text);
        var none = await g.Run("find_biome", new { biome = "mesa" });
        Assert.False(none.Ok);
        Assert.Contains("no mesa seen", none.Text);
        var far = await g.Run("find_biome", new { biome = "desert", max = 100 });
        Assert.False(far.Ok, "the speck is 330 m away");
        Assert.False((await g.Run("find_biome", new { biome = "atlantis" })).Ok);
    }

    [Fact]
    public async Task Find_biome_without_a_name_surveys_what_was_seen_and_says_so_when_nothing_was()
    {
        var g = LiquidTests.Island(4);
        Assert.Contains("no biome data", (await g.Run("find_biome", new { biome = "desert" })).Text);
        for (int i = 0; i < 5; i++) g.World.SetBiomes(i, 0, Chunk(Plains));
        g.World.SetBiomes(9, 0, Chunk(Desert));
        var r = await g.Run("find_biome");
        Assert.True(r.Ok, r.Text);
        Assert.Contains("seen 6 chunks: plains 5, desert 1", r.Text);
    }

    /// <summary>A flat 64×64 test world; the biome arrives as the body walks, like chunks inside the view distance.</summary>
    private static FakeGame Flat()
    {
        var g = new FakeGame(terrain: (x, y, z) => y == 63 ? Blocks.Stone : 0);
        g.Stand(0.5, 64, 0.5);
        return g;
    }

    [Fact]
    public async Task Scout_biome_walks_out_and_stops_when_the_desert_shows()
    {
        var g = Flat();
        // Chunks near the origin are all plains; a desert patch at chunks (2..5, -1..2) shows once the body is at x >= 16.
        for (int cx = -4; cx <= 1; cx++) for (int cz = -4; cz <= 4; cz++) g.World.SetBiomes(cx, cz, Chunk(Plains));
        g.Arrived = pos => { if (pos.X >= 16) for (int cx = 2; cx <= 5; cx++) for (int cz = -1; cz <= 2; cz++) g.World.SetBiomes(cx, cz, Chunk(Desert)); };
        var r = await g.Run("scout_biome", new { biome = "desert", dir = 90, leg = 16, max = 200 });   // 90 = east
        Assert.True(r.Ok, r.Text);
        Assert.Contains("found after", r.Text);
        Assert.Contains("desert", r.Text);
        Assert.True(g.World.Player.Position.X is >= 16 and < 32, $"stopped at x={g.World.Player.Position.X}: it keeps walking past a find");
        Assert.True(g.Visited.Count > 0);
    }

    [Fact]
    public async Task Scout_biome_does_not_walk_when_the_patch_is_already_known_nearby()
    {
        var g = Flat();
        Desert4x4(g.World, 2, 0);
        var r = await g.Run("scout_biome", new { biome = "desert", leg = 16 });
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("already seen", r.Text);
        Assert.Empty(g.Visited);
    }

    [Fact]
    public async Task Scout_biome_gives_up_at_the_distance_limit_and_says_how_far_it_got()
    {
        var g = Flat();
        var r = await g.Run("scout_biome", new { biome = "desert", dir = 90, leg = 16, max = 24 });
        Assert.False(r.Ok);
        Assert.Contains("no desert patch", r.Text);
        Assert.InRange(g.World.Player.Position.X, 16, 40);
    }

    [Fact]
    public async Task Scout_biome_heads_for_the_side_nothing_has_been_seen_when_given_no_direction()
    {
        var g = Flat();
        // Everything within 400 m is known except the west.
        for (int cx = -30; cx <= 30; cx++) for (int cz = -30; cz <= 30; cz++) if (cx > -3) g.World.SetBiomes(cx, cz, Chunk(Plains));
        var r = await g.Run("scout_biome", new { biome = "desert", leg = 16, max = 20 });
        Assert.False(r.Ok);
        Assert.True(g.World.Player.Position.X < 0, $"went to x={g.World.Player.Position.X}, east/north/south were already explored");
    }

    [Fact]
    public async Task Scout_biome_does_not_walk_out_at_night_unless_told_to()
    {
        var g = Flat();
        g.World.Time = 15000;
        var r = await g.Run("scout_biome", new { biome = "desert", dir = 90, leg = 16, max = 20 });
        Assert.False(r.Ok);
        Assert.Contains("night", r.Text);
        Assert.Empty(g.Visited);
        var ok = await g.Run("scout_biome", new { biome = "desert", dir = 90, leg = 16, max = 20, night_ok = true });
        Assert.Contains("no desert patch", ok.Text);   // it walked, and found nothing
        Assert.NotEmpty(g.Visited);
    }
}
