using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class JobScaffoldCleanupTests
{
    [Fact]
    public async Task Job_end_reclaims_its_movement_pillar_but_not_explicit_build_blocks()
    {
        using var s = new FakeServer();
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++) s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.Inventory[36] = new ItemStack(Blocks.Cobblestone, 32, 0);
        await using var c = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            ConnectionFactory = s.CreateClientConnection, HostileAlertRadius = 0,
            Survival = new() { Enabled = false },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await c.ConnectAsync(timeout.Token);
        while (!c.World.Player.OnGround || c.World.Inventory.CountItem(Blocks.Cobblestone) != 32)
            await Task.Delay(10, timeout.Token);
        using var host = BotHost.Create(new MinecraftGameClient(c), TestBot.Options(), null, new NoLlm(), persist: false);
        host.Runtime.Tools.Register("scaffold_job", "work at height", ToolSchema.Empty, async (_, ct) =>
        {
            var design = await c.PlaceBlockAsync(7, 64, 7, Blocks.Cobblestone, ct);
            Assert.True(design.Ok, design.Message);
            var up = await c.GotoAsync(new GoalBlock(8, 66, 8), MineClaw.Client.GotoOptions.Default with { AllowPlace = true }, ct);
            Assert.True(up.Ok, up.Message);
            Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(8, 64, 8));
            var down = await c.GotoAsync(new GoalBlock(9, 64, 8), null, ct);
            Assert.True(down.Ok, down.Message);
            return ToolResult.Success("work done");
        }, longRunning: true);
        host.Runtime.Tools.TryGet("scaffold_job", out var job);
        var result = await host.Runtime.Activities.RunAsync([(job!, ToolSchema.Empty)], TimeSpan.FromSeconds(30), default);
        Assert.All(result, r => Assert.True(r.Ok, r.Text));
        Assert.Equal(0, c.World.GetBlock(8, 65, 8));
        Assert.Equal(0, c.World.GetBlock(8, 64, 8));
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(7, 64, 7));
        Assert.Equal(Blocks.Stone, c.World.GetBlock(8, 63, 8));
    }
}
