using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Live bug: "expand_island placed only 3 cobble per run (6 total, 58 left), skipped 19 each time" extending the floor 2 rows
/// west by the player (-4,64,2), east of the west door row (x -9). The island is the remembered world from the live db
/// (tests/fixtures/house-exit/island.txt); the skill runs on the REAL client (reach, aim, sneak, EdgeGuard, physics) against
/// the in-memory server. The west edge there is jagged (y 63):
/// <code>
///   x: -9 -8 -7 -6 -5 -4 -3
/// z-2   .  .  .  .  .  P  P     edge x=-4 (z -14..-2)
/// z-1   C  C  C  C  C  C  C     bridge to the portal (z -1..1), doors at x=-9 y64-65
/// z 1   C  C  C  C  C  C  C
/// z 2   .  .  .  P  C  C  C     one plank sticks out at x=-6
/// z 3   .  .  .  .  C  C  C     edge x=-5 (z 3..12)
/// </code>
/// Only cells with a side-face neighbour (the bridge, the plank) could be placed from a cell centre; every other edge cell's
/// only support is the inward floor block's outer face, visible only when leaning out over the edge.
/// </summary>
public class ExpandIslandEdgeTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static string Fixture(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "house-exit", file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tests/fixtures/house-exit/" + file);
    }

    private static void LoadIsland(FakeServer s)
    {
        foreach (var line in File.ReadLines(Fixture("island.txt")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(' ');
            var key = (int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]));
            s.Blocks[key] = int.Parse(f[3]);
            if (int.Parse(f[4]) is var m and not 0) s.Metas[key] = m;
        }
    }

    private async Task<(MinecraftClient C, FakeServer S)> Start(double x, double z, int cobble)
    {
        var server = new FakeServer();
        LoadIsland(server);
        server.SpawnAt = (x, 64, z);
        server.ConsumeOnPlace = true;
        server.Inventory[36] = new ItemStack((short)Blocks.Cobblestone, (byte)cobble, 0);
        var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
            Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } },
        });
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await client.ConnectAsync(cts.Token);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!client.World.Player.OnGround && DateTime.UtcNow < until) await Task.Delay(5);
        return (client, server);
    }

    [Fact]
    public async Task Fills_two_rows_west_along_a_jagged_edge_on_the_real_client()
    {
        var (c, s) = await Start(-3.5, 2.5, 64);
        await using var _c = c;
        using var _s = s;
        var before = s.Blocks.Keys.Where(k => k.Item2 == 63).Select(k => (k.Item1, k.Item3)).ToHashSet();
        // Watch the body every few ms: never on the ground over the void, never falling.
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
        var game = new ClientGameActions(c);
        var reg = SkillRegistry.CreateDefault();
        var r = await reg.RunAsync(new SkillContext(game, reg), "expand_island", FakeGame.J(new { side = "west", rows = 2, max = 64 }), CancellationToken.None);
        stop.Cancel();
        await watch;
        log.WriteLine(r.Text);
        for (int z = -15; z <= 13; z++)
            log.WriteLine($"{z,4} " + string.Concat(Enumerable.Range(-12, 16).Select(x => c.World.GetBlock(x, 63, z) switch
            {
                0 => '.', _ when !before.Contains((x, z)) => 'n', Blocks.Planks => 'P', _ => 'C',
            })));
        Assert.True(r.Ok, r.Text);
        Assert.DoesNotContain("skipped", r.Text);
        // Both rows west of the jagged edge near me are floor now: z 3..6 (edge x -5 → -6, -7), z -2..-5 (edge -4 → -5, -6),
        // and the plank's row z 2 (-6 → -7, -8).
        foreach (int z in new[] { 3, 4, 5, 6 })
            for (int x = -7; x <= -6; x++) Assert.True(c.World.GetBlock(x, 63, z) == Blocks.Cobblestone, $"{x},63,{z}");
        foreach (int z in new[] { -2, -3, -4, -5 })
            for (int x = -6; x <= -5; x++) Assert.True(c.World.GetBlock(x, 63, z) == Blocks.Cobblestone, $"{x},63,{z}");
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(-7, 63, 2));
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(-8, 63, 2));
        Assert.False(overVoid, "stood over the void");
        Assert.True(minY > 63.9, $"fell to y {minY}");
    }
}
