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

    /// <summary>
    /// The pinned trace exactly, except for the one step tick timing still decides: the pillar's dirt is gathered hole by hole until
    /// the bag holds what the pillar needs (GatherPillarDirtAsync), and each hole's pickup runs right after its dig. When the client
    /// lags (a full test run), the last hole's drop can reach it only after that pickup looked, so it digs one more hole, from the
    /// same candidate order, picks both up and fills every hole again at the end (live: the same on a laggy server). Allowed: extra
    /// "dug 2|3 at C" lines straight after the pinned holes, each with its "place 3:0 at C" refill, and nothing else; with them taken
    /// out the trace must be the pinned one line for line (the result line, every log dug, the pillar, the pinned holes and their refills, in order).
    /// </summary>
    internal static bool SameChop(string expected, string actual)
    {
        if (expected == actual) return true;
        var exp = expected.Split('\n');
        var act = actual.Split('\n');
        var extra = new List<int>();
        int j = 0;
        for (int i = 0; i < act.Length; i++)
            if (j < exp.Length && act[i] == exp[j]) j++;
            else extra.Add(i);
        return j == exp.Length && ExtraHolesOnly(exp, act, extra);
    }

    private static readonly System.Text.RegularExpressions.Regex HoleDig = new(@"^dug [23] at (-?\d+,-?\d+,-?\d+)$");

    private static bool ExtraHolesOnly(string[] exp, string[] act, List<int> extra)
    {
        var digs = extra.Where(i => HoleDig.IsMatch(act[i])).ToList();
        if (digs.Count == 0 || digs.Count * 2 != extra.Count) return false;
        // The extra digs come as one run straight after a pinned hole (a dug cell the pinned trace fills again)…
        if (digs[^1] - digs[0] != digs.Count - 1 || digs[0] == 0 || !IsPinnedHole(exp, act[digs[0] - 1])) return false;
        // …and every one of them is filled again (the other extra lines are exactly their refills).
        var cells = digs.Select(i => HoleDig.Match(act[i]).Groups[1].Value).ToHashSet();
        var refills = extra.Except(digs).Select(i => act[i]).ToList();
        return refills.Count == cells.Count && refills.All(l => l.StartsWith("place 3:0 at ", StringComparison.Ordinal) && cells.Remove(l["place 3:0 at ".Length..]));
    }

    private static bool IsPinnedHole(string[] exp, string line) =>
        HoleDig.Match(line) is { Success: true } m && exp.Contains("place 3:0 at " + m.Groups[1].Value);

    [Fact]
    public void A_lagging_pickup_may_add_refilled_holes_and_nothing_else()
    {
        const string pinned = "ok: chopped\n----\ndug 2 at 3,63,7\ndug 2 at 5,63,7\ndug 17 at 8,72,8\nplace 3:0 at 5,63,7\nplace 3:0 at 3,63,7\nplace 6:0 at 8,64,8";
        Assert.True(SameChop(pinned, pinned));
        // One more hole, filled again: the lagging pickup.
        Assert.True(SameChop(pinned, "ok: chopped\n----\ndug 2 at 3,63,7\ndug 2 at 5,63,7\ndug 2 at 5,63,9\ndug 17 at 8,72,8\nplace 3:0 at 5,63,7\nplace 3:0 at 5,63,9\nplace 3:0 at 3,63,7\nplace 6:0 at 8,64,8"));
        // A hole left open, a hole dug somewhere else in the order, a missing or moved line, another result: all drift.
        Assert.False(SameChop(pinned, "ok: chopped\n----\ndug 2 at 3,63,7\ndug 2 at 5,63,7\ndug 2 at 5,63,9\ndug 17 at 8,72,8\nplace 3:0 at 5,63,7\nplace 3:0 at 3,63,7\nplace 6:0 at 8,64,8"));
        Assert.False(SameChop(pinned, "ok: chopped\n----\ndug 2 at 3,63,7\ndug 2 at 5,63,7\ndug 17 at 8,72,8\ndug 2 at 5,63,9\nplace 3:0 at 5,63,7\nplace 3:0 at 5,63,9\nplace 3:0 at 3,63,7\nplace 6:0 at 8,64,8"));
        Assert.False(SameChop(pinned, "ok: chopped\n----\ndug 2 at 3,63,7\ndug 17 at 8,72,8\nplace 3:0 at 5,63,7\nplace 3:0 at 3,63,7\nplace 6:0 at 8,64,8"));
        Assert.False(SameChop(pinned, "fail: chopped\n----\ndug 2 at 3,63,7\ndug 2 at 5,63,7\ndug 17 at 8,72,8\nplace 3:0 at 5,63,7\nplace 3:0 at 3,63,7\nplace 6:0 at 8,64,8"));
        Assert.False(SameChop(pinned, "ok: chopped\n----\ndug 2 at 3,63,7\ndug 2 at 5,63,7\ndug 17 at 8,72,8\ndug 17 at 8,73,8\nplace 3:0 at 5,63,7\nplace 3:0 at 3,63,7\nplace 6:0 at 8,64,8"));
    }

    /// <summary>The drift a full Client.Tests run on main produced (11-04 19:28, remnant-dirt): a 12th hole at 5,63,9 after the pinned 11,
    /// filled again in its nearest-first place among the refills. Everything else was the pinned trace.</summary>
    [Fact]
    public void The_remnant_dirt_drift_seen_under_load_is_the_lagging_pickup()
    {
        var pinned = ClientChopTraceGoldenData.Traces["remnant-dirt"].ReplaceLineEndings("\n");
        var lagged = pinned.Replace("dug 2 at 5,63,7\n", "dug 2 at 5,63,7\ndug 2 at 5,63,9\n")
            .Replace("place 3:0 at 5,63,7\n", "place 3:0 at 5,63,7\nplace 3:0 at 5,63,9\n");
        Assert.NotEqual(pinned, lagged);
        Assert.True(SameChop(pinned, lagged));
        Assert.False(SameChop(pinned, lagged.Replace("place 3:0 at 5,63,9\n", ""))); // that hole left open would be drift
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
        if (expected is null || !SameChop(expected.ReplaceLineEndings("\n"), trace))
        {
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-chop-{name}-{DateTime.UtcNow:HHmmssfff}.txt");
            File.WriteAllText(path, trace);
            Assert.Fail($"chop trace drifted (actual: {path})");
        }
    }
}
