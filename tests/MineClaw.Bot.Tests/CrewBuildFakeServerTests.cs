using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Two real MinecraftClients build one design at once over the in-process fake server (two fake servers standing in for one
/// world: they share a lock and mirror every placed/dug block to each other), each running its lane of the crew plan.
/// </summary>
public class CrewBuildFakeServerTests
{
    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    private static MinecraftClient Client(FakeServer server, string name) => new(new ClientOptions
    {
        Username = name, TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
        RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
    });

    [Fact]
    public async Task Two_bodies_build_a_wall_together_on_the_fake_server()
    {
        var a = new FakeServer { ConsumeOnPlace = true, SpawnAt = (6.5, 64, 10.5) };
        var b = new FakeServer { ConsumeOnPlace = true, SpawnAt = (10.5, 64, 10.5), BlockLock = a.BlockLock };
        a.Mirror = (x, y, z, id, m) => b.SetBlock(x, y, z, id, m);
        b.Mirror = (x, y, z, id, m) => a.SetBlock(x, y, z, id, m);
        a.Inventory[36] = new ItemStack(Blocks.Cobblestone, 40, 0);
        b.Inventory[36] = new ItemStack(Blocks.Cobblestone, 40, 0);
        using var _a = a;
        using var _b = b;
        await using var ca = Client(a, "maid_chan");
        await using var cb = Client(b, "maid_chan_67");
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await ca.ConnectAsync(cts.Token);
            await cb.ConnectAsync(cts.Token);
        }
        Assert.True(await Until(() => ca.World.Player.OnGround && cb.World.Player.OnGround
                                      && ca.World.Inventory.CountItem(Blocks.Cobblestone) == 40 && cb.World.Inventory.CountItem(Blocks.Cobblestone) == 40, 4000));

        // A 5×2 wall across the 5×5 island (6..10, y 63), two blocks in from its north edge.
        var (plan, issues) = BlueprintNormalizer.FromText("blueprint wall\nsize 5x2x1\npalette C=cobblestone\nlayer 0-1\nCCCCC", new BlockPos(6, 64, 7));
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        using var db = AgentDb.Open(null);
        var crew = new CrewExecutor(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)))
        {
            Poll = TimeSpan.FromMilliseconds(5), ParkAfter = TimeSpan.FromMilliseconds(200), WaitLimit = TimeSpan.FromSeconds(15),
        };
        IBuildBody[] bodies = [new GameBuildBody(() => new MinecraftGameClient(ca)), new GameBuildBody(() => new MinecraftGameClient(cb))];
        using var run = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var report = await crew.RunAsync(1, plan!, bodies, run.Token);
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        Assert.Equal(2, report.Rounds[0].Lanes.Count);
        foreach (var blk in plan!.Blocks.Values)
        {
            Assert.True(a.Blocks.TryGetValue((blk.Pos.X, blk.Pos.Y, blk.Pos.Z), out var id) && id == blk.Id, $"nothing at {blk.Pos}");
            Assert.True(b.Blocks.TryGetValue((blk.Pos.X, blk.Pos.Y, blk.Pos.Z), out var id2) && id2 == blk.Id, $"the other view lacks {blk.Pos}");
        }
        // Both bodies placed some of it (each server consumed from its own client's stack).
        Assert.True(ca.World.Inventory.CountItem(Blocks.Cobblestone) < 40, CrewBuildTests.Why(report));
        Assert.True(cb.World.Inventory.CountItem(Blocks.Cobblestone) < 40, CrewBuildTests.Why(report));
        Assert.Equal(10, 80 - ca.World.Inventory.CountItem(Blocks.Cobblestone) - cb.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Equal("done", new BuildPlanStore(db).Get(1, 0)!.State);
    }
}
