using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Client.Tests;

/// <summary>
/// Pins mine and collect_drops (WP05 refactor): the result line and the timing-independent trace (every block dug and placed, in
/// order; see <see cref="ClientChopTraceGoldenTests.ChopTrace"/>) for mining sand on an island, mining what isn't there, leaving placed
/// cobblestone alone, and collecting drops of which one lies over the void.
/// </summary>
public class ClientGatherTraceGoldenTests
{
    private static void Island(FakeServer s)
    {
        TreeWorld.Square(s, 4, 4, 5);
        s.DropOnDig = true;
        s.DigDrop = (_, id, meta) => new ItemStack((short)id, 1, 0);
        foreach (var (x, z) in new[] { (2, 2), (6, 3), (3, 7), (7, 7) }) s.Blocks[(x, 64, z)] = Blocks.Sand;
        s.SpawnAt = (4.5, 64, 4.5);
    }

    private static readonly Dictionary<string, (Action<FakeServer> Setup, bool Normal, Func<MinecraftClient, FakeServer, Task<ActionResult>> Act)> Scenarios = new()
    {
        // No drops here: where the body stands after walking to a drop is tick timing, and it decides the next pick under load.
        ["mine-sand"] = (s => { Island(s); s.DropOnDig = false; }, false, (c, _) => c.MineAsync("sand", 3)),
        ["mine-none"] = (Island, false, (c, _) => c.MineAsync("diamond_ore", 2)),
        ["mine-placed-cobble"] = (s =>
        {
            Island(s);
            s.Blocks[(5, 64, 5)] = Blocks.Cobblestone;
            s.Blocks[(5, 65, 5)] = Blocks.Cobblestone;
        }, false, (c, _) => c.MineAsync("cobblestone", 2)),
        ["mine-dirt-normal"] = (s =>
        {
            Island(s);
            s.DigDrop = (_, id, meta) => new ItemStack((short)(id == Blocks.Grass ? Blocks.Dirt : id), 1, 0);
        }, true, (c, _) => c.MineAsync("dirt", 2)),
        ["collect"] = (Island, false, async (c, s) =>
        {
            s.SpawnDrop(6.5, 64.1, 4.5, Harness.Stack(Blocks.Dirt, 2));
            s.SpawnDrop(2.5, 64.1, 6.5, Harness.Stack(Items.Seeds, 3));
            s.SpawnDrop(10.5, 64.1, 4.5, Harness.Stack(Blocks.Sapling, 1)); // on the rim
            s.SpawnDrop(12.5, 64.1, 4.5, Harness.Stack(Blocks.Sand, 1));    // over the void
            Assert.True(await Harness.WaitAsync(() => c.World.Entities.All().Count(e => e.Kind == MineClaw.World.EntityKind.Item) == 4));
            return await c.CollectDropsAsync(10);
        }),
    };

    public static IEnumerable<object[]> Cases() => Scenarios.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Gathering_digs_the_pinned_blocks(string name)
    {
        var sc = Scenarios[name];
        using var mode = sc.Normal ? WorldMode.Override(WorldMode.Normal) : null;
        var (c, s) = await Harness.StartAsync(sc.Setup);
        await using var _ = c;
        using var __ = s;
        var r = await sc.Act(c, s);
        await Task.Delay(100);
        var trace = r + "\n----\n" + ClientChopTraceGoldenTests.ChopTrace(s);
        ClientGatherTraceGoldenData.Traces.TryGetValue(name, out var expected);
        if (expected?.ReplaceLineEndings("\n") != trace)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-gather-{name}-{DateTime.UtcNow:HHmmssfff}.txt");
            File.WriteAllText(path, trace);
            Assert.Fail($"gather trace drifted (actual: {path})");
        }
    }
}
