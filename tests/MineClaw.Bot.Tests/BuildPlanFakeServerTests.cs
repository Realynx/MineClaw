using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>End to end over the in-process fake server: a real MinecraftClient runs a sliced plan (planned stands, clicked
/// faces via PlaceAgainstAsync), pauses at the confirmation gate, carries on with the yes, and the plan view renders it.</summary>
public class BuildPlanFakeServerTests
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

    [Fact]
    public async Task A_gated_floor_and_wall_build_runs_on_the_fake_server()
    {
        var server = new FakeServer { ConsumeOnPlace = true };
        server.SpawnAt = (8.5, 64, 10.5);
        server.Inventory[36] = new ItemStack(Blocks.Cobblestone, 40, 0);
        var c = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        });
        await using var client = c;
        using var __ = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
        Assert.True(await Until(() => c.World.Player.OnGround && c.World.Inventory.CountItem(Blocks.Cobblestone) == 40, 4000));

        // A 3×1 wall two high on the 5×5 island (6..10, y 63).
        var (plan, issues) = BlueprintNormalizer.FromText("blueprint wall\nsize 3x2x1\npalette C=cobblestone\nlayer 0-1\nCCC", new BlockPos(7, 64, 7));
        Assert.NotNull(plan);
        var gates = new BuildGates();
        var asked = new List<string>();
        var ex = new ConstructionExecutor(new BlueprintOptions { GateBlocks = 3 })
        {
            Gates = gates,
            Ask = q =>
            {
                asked.Add(q);
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 400 && !gates.IsWaiting(1); i++) await Task.Delay(5);
                    gates.Answer(1, new GateReply(GateVerdict.Approve, "PoofImaFox", "keep going"));
                });
                return Task.CompletedTask;
            },
        };
        var body = new GameBuildBody(() => new MinecraftGameClient(c));
        using var run = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var report = await ex.RunAsync(1, plan!, BuildPart.Whole(plan!), body, run.Token);
        Assert.True(report.Complete, report.Text("wall"));
        Assert.Single(asked);
        Assert.Equal(GateState.Approved, report.Plan!.Gate.State);
        foreach (var b in plan!.Blocks.Values)
            Assert.True(server.Blocks.TryGetValue((b.Pos.X, b.Pos.Y, b.Pos.Z), out var id) && id == b.Id, $"nothing at {b.Pos}");
        Assert.All(report.Plan.Steps.Where(s => s.Action == StepAction.Place), s => Assert.Equal(StepStatus.Verified, s.Status));
        var html = BuildPlanView.Html([report.Plan], "#1 wall");
        Assert.Contains("<svg", html);
        Assert.Contains("6/6 steps done", html);
        Assert.Contains("gate: approved", html);
    }
}
