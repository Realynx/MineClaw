using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-28 02:05-03:06: main and both clones stood at 7.5,64.5,6.3 by the cobble gen ("can't reach chest at 14,64,-5: no
/// walkable spot that sees it (stuck (after 4 re-plans) at 7.5,64.5,6.3)", "goto: stuck, re-planning" x5, then "I need a pick
/// and can't get one" with spare picks in that chest). World (tests/fixtures/gen-slab-stuck/blocks.txt, from the live db's
/// world memory): the bot stands on the cobble slab 7,64,6 (feet 64.5, head 66.3); north of it 7,64,5 is open ground (floor
/// 64), but the oak canopy has a leaf block at 7,66,5. The "same level" walk checked the body only in the source column at the
/// higher floor, never the target column while the feet are still at 64.5, so the plan stepped north off the slab and the
/// head hit the leaves at z = 6.0 (center 6.3, half-width 0.3) every time. Re-plans chose the same move.
/// </summary>
public class GenSlabStuckTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private Func<ClientOptions, ClientOptions> Logged => o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } };

    internal static void Load(FakeServer s)
    {
        string? path = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && path is null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "gen-slab-stuck", "blocks.txt");
            if (File.Exists(p)) path = p;
        }
        foreach (var line in File.ReadLines(path ?? throw new FileNotFoundException("tests/fixtures/gen-slab-stuck/blocks.txt")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(' ');
            var key = (int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]));
            s.Blocks[key] = int.Parse(f[3]);
            if (int.Parse(f[4]) is var m and not 0) s.Metas[key] = m;
        }
    }

    private static WorldState World()
    {
        var w = new WorldState(Blocks.Get);
        var srv = new FakeServer();
        Load(srv);
        w.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        foreach (var ((x, y, z), id) in srv.Blocks) w.SetBlock(x, y, z, id, srv.Metas.GetValueOrDefault((x, y, z)));
        return w;
    }

    [Fact]
    public void Planner_never_steps_off_the_slab_into_the_leaves()
    {
        var w = World();
        Assert.Equal(Blocks.Get(44).Id, w.GetBlock(7, 64, 6));
        // Straight north is the short way (7,64,5 → 7,64,3 are open ground under the canopy at y66), but not off the slab.
        var path = Pathfinder.FindPath(w, new Vec3d(7.5, 64.5, 6.3), new GoalBlock(7, 64, 3), PathOptions.Default);
        Assert.Equal(PathStatus.Found, path.Status);
        Assert.NotEqual(new BlockPos(7, 64, 5), path.Steps[0].Pos);
        // The probe names the leaf at head height for that step, and east (8,64,6: clear above) as a free way off.
        var hits = StuckProbe.Obstructions(w, new Vec3d(7.5, 64.5, 6.3), new Vec3d(7.5, 64, 5.5));
        Assert.Equal([new StuckProbe.Obstruction(new BlockPos(7, 66, 5), true)], hits);
        Assert.Contains((1, 0), StuckProbe.FreeDirections(w, new Vec3d(7.5, 64.5, 6.3)));
        Assert.DoesNotContain((0, -1), StuckProbe.FreeDirections(w, new Vec3d(7.5, 64.5, 6.3)));
    }

    /// <summary>The generic escape: whatever collision the planner misses (here a leaf it doesn't know about), the stuck move
    /// goes into <see cref="PathOptions.AvoidMoves"/> and the re-plan leaves another way instead of repeating it.</summary>
    [Fact]
    public void A_move_stuck_on_is_avoided_by_the_re_plan()
    {
        var real = World();
        var planned = World();
        planned.SetBlock(7, 66, 5, 0, 0); // the planner's view lacks the leaf; the body still hits it
        var s = new PhysicsState();
        s.Teleport(new Vec3d(7.5, 64.5, 6.3), 0, 0);
        s.OnGround = true;
        var goal = new GoalBlock(7, 64, 3);
        var avoid = new HashSet<(BlockPos, BlockPos)>();
        string? first = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var start = Pathfinder.StartCellFor(real, s.Position) ?? s.Position.ToBlockPos();
            if (goal.IsReached(start)) break;
            var path = Pathfinder.FindPath(planned, s.Position, goal, PathOptions.Default with { AvoidMoves = avoid });
            Assert.Equal(PathStatus.Found, path.Status);
            var f = new PathFollower(path.Steps);
            FollowerOutput o;
            for (int t = 0; ; t++)
            {
                Assert.True(t < 2000, "tick budget");
                o = f.Tick(real, s);
                if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
                PlayerPhysics.Tick(real, s, o.Input);
            }
            if (o.Status == FollowerStatus.Finished) continue;
            first ??= o.Reason;
            Assert.Equal("stuck", o.Reason);
            Assert.Equal(new BlockPos(7, 64, 5), f.CurrentStep!.Pos);
            avoid.Add((start, f.CurrentStep.Pos));
        }
        Assert.Equal("stuck", first);
        Assert.True(goal.IsReached(Pathfinder.StartCellFor(real, s.Position) ?? s.Position.ToBlockPos()), $"at {s.Position}");
    }

    [Fact]
    public async Task Walks_from_the_gen_slab_to_the_house_chest_and_withdraws()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Load(srv);
            srv.SpawnAt = (7.5, 64.5, 6.3);
            var chest = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            chest[0] = Harness.Stack(Items.StonePickaxe, 2);
            srv.Chests[(14, 64, -5)] = chest;
            srv.Chests[(15, 64, -5)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        }, Logged);
        await using var _ = c;
        using var __ = s;
        Assert.Equal(64.5, c.World.Player.Position.Y, 2);
        var r = await c.WithdrawAsync(Items.StonePickaxe, 1, new BlockPos(14, 64, -5));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(1, c.World.Inventory.CountItem(Items.StonePickaxe));
        Assert.Empty(s.Dug);
    }
}
