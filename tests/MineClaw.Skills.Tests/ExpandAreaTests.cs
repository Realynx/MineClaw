using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Live 10-28 14:40: Fox said "expand the floor so you can build it" for design #4 (16×16 at -33,64,7, hanging over the void
/// beyond his 8×8 floor); the worker found no matching skill and went straight to the build. expand_island area=x0,z0,x1,z1
/// fills the rectangle: from the existing floor outward (BFS), every block against a solid neighbour, void-safe, resumable.
/// </summary>
public class ExpandAreaTests(Xunit.Abstractions.ITestOutputHelper log)
{
    /// <summary>A 5×5 dirt island (x/z -2..2, y61..63), the bot in the middle; every block change recorded with what touched it.</summary>
    private static (FakeGame G, List<(BlockPos Pos, bool SideSupport, bool AnySupport)> Placed) Island()
    {
        var g = LiquidTests.Island(2);
        var placed = new List<(BlockPos, bool, bool)>();
        g.Changed = (p, id, _) =>
        {
            if (id == 0) return;
            bool side = SkillWorld.Horizontal.Any(o => SkillWorld.Solid(g.World, p + o));
            bool any = side || SkillWorld.Solid(g.World, p.Down()) || SkillWorld.Solid(g.World, p.Up());
            placed.Add((p, side, any));
        };
        return (g, placed);
    }

    [Fact]
    public async Task Area_fill_grows_from_the_edges_and_finishes_the_rectangle()
    {
        var (g, placed) = Island();
        g.Give(Blocks.Cobblestone, 64);
        // The island plus 4 columns east of it: 20 empty cells, the island's own 25 already solid.
        var r = await g.Run("expand_island", new { area = "-2,-2,6,2" });
        log.WriteLine(r.Text);
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("filled 20/20 cells of -2..6,-2..2 at y63; the floor is complete", r.Text);
        for (int x = 3; x <= 6; x++)
            for (int z = -2; z <= 2; z++) Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(x, 63, z));
        Assert.Equal(0, g.World.GetBlock(-3, 63, 0)); // nothing outside the rectangle
        Assert.Equal(20, placed.Count);
        // Every block went against a solid side neighbour at the floor's level (never mid-air), ring by ring from the edge.
        Assert.All(placed, p => Assert.True(p.SideSupport, $"{p.Pos} placed without a side neighbour"));
        for (int i = 1; i < placed.Count; i++) Assert.True(placed[i].Pos.X >= placed[i - 1].Pos.X, $"{placed[i].Pos} before {placed[i - 1].Pos}");
        // The void guard: every stand it walked to had floor under it, nothing placed while over the void.
        Assert.Empty(g.Violations);
        Assert.NotEmpty(g.Visited);
        Assert.All(g.Visited, v => Assert.False(Safety.IsPlayerOverVoid(g.World, v), $"stood over the void at {v}"));
        Assert.Contains("44 cobblestone on me", r.Text);
    }

    [Fact]
    public async Task A_second_run_picks_up_the_cells_still_empty()
    {
        var (g, placed) = Island();
        g.Give(Blocks.Cobblestone, 7);
        var first = await g.Run("expand_island", new { area = "3,-2,6,2", y = 63 });
        log.WriteLine(first.Text);
        Assert.True(first.Ok, first.Text);
        Assert.StartsWith("filled 7/20 cells of 3..6,-2..2 at y63; 13 left (out of cobblestone)", first.Text);
        Assert.All(placed, p => Assert.True(p.SideSupport, p.Pos.ToString()));

        g.Give(Blocks.Cobblestone, 30);
        var second = await g.Run("expand_island", new { area = "3,-2,6,2", y = 63 });
        log.WriteLine(second.Text);
        Assert.StartsWith("filled 13/13 cells of 3..6,-2..2 at y63; the floor is complete", second.Text);
        Assert.Equal(20, placed.Count);
        Assert.Equal(20, placed.Select(p => p.Pos).Distinct().Count());
        Assert.All(placed, p => Assert.True(p.SideSupport, p.Pos.ToString()));
        Assert.Empty(g.Violations);
        Assert.Contains("17 cobblestone on me", second.Text);
        Assert.StartsWith("filled 0/0", (await g.Run("expand_island", new { area = "3,-2,6,2", y = 63 })).Text);
    }

    [Fact]
    public async Task An_area_not_touching_any_floor_or_with_a_mob_close_is_refused()
    {
        var (g, placed) = Island();
        g.Give(Blocks.Cobblestone, 64);
        var far = await g.Run("expand_island", new { area = "10,10,12,12", y = 63 });
        Assert.False(far.Ok);
        Assert.Contains("nothing solid at y63 in or beside 10..12,10..12: no edge to grow the floor from", far.Text);
        Assert.Contains("area must be x0,z0,x1,z1", (await g.Run("expand_island", new { area = "1,2,3" })).Text);
        Assert.Contains("at most 1024", (await g.Run("expand_island", new { area = "0,0,40,40" })).Text);
        g.World.Entities.SpawnMob(7, MobTypes.Zombie, new Vec3d(2.5, 64, 0.5), 0, 0);
        var mob = await g.Run("expand_island", new { area = "3,-2,6,2" });
        Assert.False(mob.Ok);
        Assert.Contains("zombie", mob.Text);
        Assert.Empty(placed);
    }

    [Fact]
    public async Task An_area_covering_a_known_design_only_partly_names_the_uncovered_strip()
    {
        // Live 10-28: the brain read design #4's footprint off the preview image 2 blocks off (x -31..-16 for -33..-18).
        var (g, _) = Island();
        g.Give(Blocks.Cobblestone, 64);
        var reg = SkillRegistry.CreateDefault();
        reg.Footprints = () => [new DesignFootprint("design #4", 3, -2, 8, 2, 64, 79)];
        var r = await g.Run("expand_island", new { area = "3,-2,6,2" }, reg);
        log.WriteLine(r.Text);
        Assert.True(r.Ok, r.Text);
        Assert.Contains("; x 7..8 of design #4 still uncovered (its area=3,-2,8,2)", r.Text);
        // Covering it whole: no note; another level or no overlap: none either.
        Assert.DoesNotContain("uncovered", (await g.Run("expand_island", new { area = "3,-2,8,2" }, reg)).Text);
        reg.Footprints = () => [new DesignFootprint("design #4", 3, -2, 8, 2, 90, 99)];
        Assert.DoesNotContain("uncovered", (await g.Run("expand_island", new { area = "3,-3,9,3" }, reg)).Text);
    }

    [Fact]
    public async Task Materials_come_through_the_obtain_hook()
    {
        var (g, _) = Island();
        var reg = SkillRegistry.CreateDefault();
        var asked = new List<(int Id, int Count)>();
        reg.Obtain = (ctx, id, n, _) => { asked.Add((id, n)); g.Give(id, n); return Task.FromResult<string?>($"took {n} from the chest"); };
        var r = await g.Run("expand_island", new { area = "3,-2,6,2", y = 63 }, reg);
        Assert.True(r.Ok, r.Text);
        Assert.Equal([(Blocks.Cobblestone, 20)], asked);
        Assert.StartsWith("filled 20/20", r.Text);
    }

    /// <summary>The real client (reach, aim, sneak, EdgeGuard, physics) against the in-memory server on the remembered island:
    /// a 3×4 rectangle west of its jagged edge, every block placed by bridging out from the floor, the body never over the void.</summary>
    [Fact]
    public async Task On_the_real_client_the_area_fill_never_stands_over_the_void()
    {
        var server = new FakeServer();
        foreach (var line in File.ReadLines(ExpandIslandFixture("island.txt")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(' ');
            var key = (int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]));
            server.Blocks[key] = int.Parse(f[3]);
            if (int.Parse(f[4]) is var m and not 0) server.Metas[key] = m;
        }
        server.SpawnAt = (-3.5, 64, 2.5);
        server.ConsumeOnPlace = true;
        server.Inventory[36] = new ItemStack((short)Blocks.Cobblestone, 64, 0);
        await using var c = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
            Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } },
        });
        using var _s = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!c.World.Player.OnGround && DateTime.UtcNow < until) await Task.Delay(5);

        bool overVoid = false;
        double minY = 64;
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var pos = c.World.Player.Position;
                minY = Math.Min(minY, pos.Y);
                if (c.World.Player.OnGround && Safety.IsPlayerOverVoid(c.World, pos)) overVoid = true;
                await Task.Delay(2);
            }
        });
        var reg = SkillRegistry.CreateDefault();
        var r = await reg.RunAsync(new SkillContext(new ClientGameActions(c), reg), "expand_island", FakeGame.J(new { area = "-8,3,-6,6", y = 63 }), CancellationToken.None);
        stop.Cancel();
        await watch;
        log.WriteLine(r.Text);
        Assert.True(r.Ok, r.Text);
        Assert.Contains("the floor is complete", r.Text);
        for (int x = -8; x <= -6; x++)
            for (int z = 3; z <= 6; z++) Assert.True(c.World.GetBlock(x, 63, z) != 0, $"{x},63,{z}");
        Assert.False(overVoid, "stood over the void");
        Assert.True(minY > 63.9, $"fell to y {minY}");
    }

    private static string ExpandIslandFixture(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "house-exit", file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tests/fixtures/house-exit/" + file);
    }
}
