using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Pins chop_tree end to end, one scenario per tree type and climbing strategy (rim oak on the L island, birch by type, a tall oak
/// jumped from inside the trunk, a cobblestone pillar, a dirt pillar in a normal world, a hanging remnant that needs dirt dug for
/// its pillar, a 2x2 jungle trunk on 1.12.2): the result line and every non-movement packet in order (see
/// <see cref="ClientActionTraceGoldenTests.Trace"/>), taken before the WP05 refactor reshaped felling and finishing.
/// </summary>
public class ClientChopTraceGoldenTests
{
    private static void Field(FakeServer s)
    {
        s.Blocks.Clear();
        for (int x = -6; x <= 22; x++)
        for (int z = -6; z <= 22; z++)
        {
            for (int y = 50; y <= 53; y++) s.Blocks[(x, y, z)] = Blocks.Stone;
            for (int y = 54; y <= 62; y++) s.Blocks[(x, y, z)] = Blocks.Dirt;
            s.Blocks[(x, 63, z)] = Blocks.Grass;
        }
    }

    private static void FieldDrops(FakeServer s, int saplingEvery)
    {
        int leaves = 0;
        s.DropOnDig = true;
        s.ConsumeOnPlace = true;
        s.DigDrop = (_, id, meta) => id switch
        {
            Blocks.Log => new ItemStack(Blocks.Log, 1, (short)(meta & 3)),
            Blocks.Leaves => Interlocked.Increment(ref leaves) % saplingEvery == 0 ? new ItemStack(Blocks.Sapling, 1, (short)(meta & 3)) : null,
            Blocks.Grass => new ItemStack(Blocks.Dirt, 1, 0),
            _ => new ItemStack((short)id, 1, 0),
        };
    }

    private static (Action<FakeServer> Setup, bool Normal, bool Modern, BlockPos? At, int? Wood) Scenario(string name) => name switch
    {
        "rim-oak" => (srv =>
        {
            TreeWorld.LIsland(srv);
            TreeWorld.Tree(srv, 5, 1, 5);
            TreeWorld.Drops(srv, saplingEvery: 4);
            srv.SpawnAt = (1.5, 64, 4.5);
            srv.Inventory[36] = Harness.Stack(Items.StoneAxe, 1);
        }, false, false, null, null),
        "birch" => (srv =>
        {
            TreeWorld.Square(srv, 4, 4, 5);
            TreeWorld.Tree(srv, 7, 4, 5, wood: 2);
            TreeWorld.Tree(srv, 3, 4, 4);
            TreeWorld.Drops(srv, saplingEvery: 3);
            srv.SpawnAt = (4.5, 64, 8.5);
        }, false, false, null, 2),
        "tall-jump" => (srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 8);
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
        }, false, false, null, null),
        "cobble-pillar" => (srv =>
        {
            TreeWorld.Square(srv, 8, 8, 4);
            TreeWorld.Tree(srv, 8, 8, 10);
            TreeWorld.Drops(srv, saplingEvery: 2);
            srv.SpawnAt = (5.5, 64, 5.5);
            srv.Inventory[37] = Harness.Stack(Blocks.Dirt, 10);
            srv.Inventory[38] = Harness.Stack(Blocks.Cobblestone, 4);
        }, false, false, null, null),
        "normal-oak-12" => (srv =>
        {
            Field(srv);
            TreeWorld.Tree(srv, 8, 8, 12);
            FieldDrops(srv, saplingEvery: 3);
            srv.SpawnAt = (3.5, 64, 8.5);
        }, true, false, null, null),
        "normal-birch-8" => (srv =>
        {
            Field(srv);
            TreeWorld.Tree(srv, 8, 8, 8, 2);
            FieldDrops(srv, saplingEvery: 3);
            srv.SpawnAt = (3.5, 64, 8.5);
        }, true, false, null, null),
        "remnant-dirt" => (srv =>
        {
            Field(srv);
            TreeWorld.Tree(srv, 8, 8, 15);
            for (int y = 64; y <= 71; y++) srv.Blocks.Remove((8, y, 8));
            FieldDrops(srv, saplingEvery: 3);
            srv.SpawnAt = (3.5, 64, 8.5);
        }, true, false, new BlockPos(8, 72, 8), null),
        "jungle-2x2" => (srv =>
        {
            Field(srv);
            for (int y = 64; y <= 75; y++)
                foreach (var (x, z) in new[] { (8, 8), (9, 8), (8, 9), (9, 9) }) { srv.Blocks[(x, y, z)] = Blocks.Log; srv.Metas[(x, y, z)] = 3; }
            for (int y = 74; y <= 77; y++)
                for (int x = 6; x <= 11; x++)
                    for (int z = 6; z <= 11; z++)
                        if (!srv.Blocks.ContainsKey((x, y, z))) { srv.Blocks[(x, y, z)] = Blocks.Leaves; srv.Metas[(x, y, z)] = 3; }
            FieldDrops(srv, saplingEvery: 3);
            srv.SpawnAt = (3.5, 64, 8.5);
        }, true, true, null, null),
        _ => throw new ArgumentException(name),
    };

    internal static async Task<string> RunAsync(string name)
    {
        var sc = Scenario(name);
        using var mode = sc.Normal ? WorldMode.Override(WorldMode.Normal) : null;
        var (c, s) = await Harness.StartAsync(sc.Setup, release1122: sc.Modern);
        await using var _ = c;
        using var __ = s;
        var r = await c.ChopTreeAsync(at: sc.At, wood: sc.Wood, options: TreeWorld.Fast with { Timeout = TimeSpan.FromSeconds(90) });
        await Task.Delay(100);
        // Every n-th leaf dug drops a sapling, and which leaves get dug is timing (see ChopTrace): the count is left out.
        var result = System.Text.RegularExpressions.Regex.Replace(r.ToString(), @"\b\d+ saplings?\b", "N sapling(s)");
        return result + "\n----\n" + ChopTrace(s);
    }

    /// <summary>The timing-independent part of a chop: every non-leaf block dug (logs, the pillar, dirt for it) and every block
    /// placed (pillar steps, holes filled, the sapling) with the cell it fills, each in order. Which leaves get cleared, and which
    /// support face a placement clicks, follow the body's exact position at the tick a decision is made, so tick timing would decide
    /// the trace: they are left out.</summary>
    internal static string ChopTrace(FakeServer s)
    {
        var lines = new List<string>();
        foreach (var d in s.Dug)
            if (d.Id != Blocks.Leaves) lines.Add($"dug {d.Id} at {d.X},{d.Y},{d.Z}");
        foreach (var p in s.Got<MineClaw.Protocol.Packets.PlayerBlockPlacementPacket>())
        {
            if (p.Direction > 5) continue;
            var n = ((BlockFace)p.Direction).Normal();
            lines.Add($"place {p.HeldItem.Id}:{p.HeldItem.Damage} at {p.X + n.X},{p.Y + n.Y},{p.Z + n.Z}");
        }
        return string.Join("\n", lines);
    }

    [Theory]
    [InlineData("rim-oak")]
    [InlineData("birch")]
    [InlineData("tall-jump")]
    [InlineData("cobble-pillar")]
    [InlineData("normal-oak-12")]
    [InlineData("normal-birch-8")]
    [InlineData("remnant-dirt")]
    [InlineData("jungle-2x2")]
    public async Task Chop_sends_the_pinned_packets(string name)
    {
        var trace = await RunAsync(name);
        ClientChopTraceGoldenData.Traces.TryGetValue(name, out var expected);
        if (expected?.ReplaceLineEndings("\n") != trace)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-chop-{name}-{DateTime.UtcNow:HHmmssfff}.txt");
            File.WriteAllText(path, trace);
            Assert.Fail($"chop trace drifted (actual: {path})");
        }
    }
}
