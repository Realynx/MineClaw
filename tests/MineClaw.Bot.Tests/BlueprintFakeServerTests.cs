using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>End to end over the in-process fake server: a real MinecraftClient builds a 3×3×3 hut from its blueprint.</summary>
public class BlueprintFakeServerTests
{
    private const string Hut3 = "blueprint test hut\nsize 3x3x3\npalette C=cobblestone P=planks\n" +
                                "layer 0\nCCC\nC.C\nC.C\nlayer 1\nCCC\nC.C\nC.C\nlayer 2\nPPP\nPPP\nPPP";

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

    [Fact]
    public async Task A_3x3x3_hut_is_placed_correctly_on_the_fake_server()
    {
        var server = new FakeServer { ConsumeOnPlace = true };
        server.SpawnAt = (8.5, 64, 10.5); // south edge of the 5×5 island (6..10, y 63), outside the hut
        server.Inventory[36] = new ItemStack(Blocks.Cobblestone, 32, 0);
        server.Inventory[37] = new ItemStack(Blocks.Planks, 16, 0);
        var c = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        });
        await using var _ = c;
        using var __ = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
        Assert.True(await Until(() => c.World.Player.OnGround && c.World.Inventory.CountItem(Blocks.Cobblestone) == 32, 4000));

        var (plan, issues) = BlueprintNormalizer.FromText(Hut3, new BlockPos(7, 64, 6));
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        var site = new SiteContext { World = c.World.PlanningView, Inventory = (id, _) => c.World.Inventory.CountItem(id), GeneratorKnown = false };
        var eval = DesignChecks.Evaluate(plan!, site);
        Assert.True(eval.Pass, string.Join("; ", eval.Issues));
        Assert.All(eval.Bill, b => Assert.Equal(0, b.Missing));

        var body = new GameBuildBody(() => new MinecraftGameClient(c));
        using var run = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan!, BuildPart.Whole(plan!), body, run.Token);
        Assert.True(report.Complete, report.Text("hut"));
        Assert.Equal(23, report.Placed); // 7 + 7 cobble walls (doorway open), 9 planks roof
        foreach (var b in plan!.Blocks.Values)
        {
            Assert.True(server.Blocks.TryGetValue((b.Pos.X, b.Pos.Y, b.Pos.Z), out var id), $"nothing at {b.Pos}");
            Assert.Equal(b.Id, id);
        }
        // The doorway and the inside stay open, the island is untouched, nothing extra was placed.
        foreach (var a in plan.Air) Assert.False(server.Blocks.TryGetValue((a.X, a.Y, a.Z), out var id) && id != 0, $"{a} should be air");
        Assert.Equal(25 + 23, server.Blocks.Count(kv => kv.Value != 0));
        Assert.Equal(32 - 14, c.World.Inventory.CountItem(Blocks.Cobblestone));
    }
}
