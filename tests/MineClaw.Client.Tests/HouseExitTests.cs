using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live bug: "no path to within 3 of (-4.125, 64, 2.625) (no route); unreachable even by pillaring/bridging" from inside the
/// plank house (x 7..16, z -12..-4, door pair at 12/13,64,-4). The fixture is the remembered world from the live db
/// (tests/fixtures/house-exit/island.txt); the goto runs the tool's exact path (GotoOptions.Default, goal near the player).
/// </summary>
public class HouseExitTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private Func<ClientOptions, ClientOptions> Logged => o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } };

    internal static string Fixture(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "house-exit", file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tests/fixtures/house-exit/" + file);
    }

    internal static void LoadIsland(FakeServer s)
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

    /// <summary>What the live bot had: the WorldIndexer's unknown_structure #8 (-4,62,-24..16,71,2, "unknown player
    /// build") protects every one of its cells, the doors included; the old CanOpen vetoed protected doors.</summary>
    internal static bool HouseProtected(BlockPos p, int id) =>
        id != 0 && p.X is >= -4 and <= 16 && p.Y is >= 62 and <= 71 && p.Z is >= -24 and <= 2;

    [Theory]
    [InlineData(8.5, -4.5)]   // where `look` put it after the failure
    [InlineData(13.5, -4.64)] // where it spawned, by the chest
    public async Task Walks_out_of_the_house_through_the_door_pair_to_the_player(double x, double z)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            LoadIsland(srv);
            srv.SpawnAt = (x, 64, z);
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
            srv.Inventory[37] = Harness.Stack(Blocks.Cobblestone, 64);
        }, o => Logged(o) with { IsProtected = HouseProtected });
        await using var _ = c;
        using var __ = s;
        // The goto tool for a player: GoalNearPosition(player, dist) with GotoOptions.Default (+ build off, parkour ground).
        var opts = GotoOptions.Default with { AllowBreak = false, AllowPlace = false, Parkour = ParkourMode.Ground };
        var r = await c.GotoAsync(new GoalNearPosition(new Vec3d(-4.125, 64, 2.625), 3), opts);
        Assert.True(r.Ok, r.ToString());
        var p = c.World.Player.Position;
        Assert.True(Math.Sqrt(Math.Pow(p.X + 4.125, 2) + Math.Pow(p.Z - 2.625, 2)) <= 3.6, $"at {p}");
        // Out through the door pair (right-clicked, then put back), not over/through a wall.
        Assert.Contains(s.Got<MineClaw.Protocol.Packets.PlayerBlockPlacementPacket>(), u => u.Z == -4 && u.X is 12 or 13 && u.Y is 64 or 65);
        Assert.Equal(3, s.Metas[(12, 64, -4)]);
        Assert.Equal(6, s.Metas[(13, 64, -4)]);
        Assert.Empty(s.Dug);
    }
}
