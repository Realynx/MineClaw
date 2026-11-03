using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Protocol.Packets;
using MineClaw.Sim;

namespace MineClaw.Bot.Tests;

public sealed class OfflineSandboxTests
{
    [Fact]
    public async Task ScriptedBrainRepliesAndWalksTowardSimulatedPlayerOverTcp()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new SimServer(0);
        var serving = server.RunAsync(stop.Token);
        await using var client = new MinecraftClient(new ClientOptions
        {
            Host = "127.0.0.1", Port = server.Port, Username = "MineClaw", AutoReconnect = false,
        });
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        options.Llm.MainProvider = options.Llm.WorkerProvider = options.Llm.CheapProvider = LlmProviders.Mock;
        options.Llm.MainModel = "mock-main";
        options.Llm.WorkerModel = "mock-worker";
        options.Llm.CheapModel = "mock-memory";
        options.Llm.MockScriptPath = Path.Combine(AppContext.BaseDirectory, "offline-brain.json");
        options.Chat.Username = options.Chat.DisplayName = "MineClaw";
        options.Chat.LineDelayMs = 0;
        options.Rag.Enabled = false;
        options.Idle.IdleDefault = options.Idle.ResumeStandingOrders = false;
        // Otherwise the built-in "come here" reflex could make a broken mock brain appear to work.
        options.Brainstem.Enabled = false;
        options.Brainstem.SpeculativeStart = false;
        options.Brainstem.Social.Enabled = false;
        options.Brainstem.Reply.QuickAck = false;
        var bot = BotHost.Create(new MinecraftGameClient(client), options, persist: false,
            worldIndex: new() { Enabled = false });
        var runtime = bot.Runtime;
        Task brain = Task.CompletedTask;
        try
        {
            await client.ConnectAsync(stop.Token);
            var session = await server.WaitForSessionAsync(stop.Token);
            await UntilAsync(() => client.World.Entities.FindPlayer(SkyblockStart.PlayerName) != null, stop.Token);
            double startX = client.World.Player.Position.X;
            Assert.InRange(startX, 8.4, 8.6);
            brain = runtime.Agent.RunAsync(stop.Token);
            server.Say("come here");
            await UntilAsync(() => session.Got<ChatPacket>().Any(p => p.Message.Contains("On my way."))
                && (session.Got<PlayerPositionLookPacket>().Any(p => p.X > startX + 1)
                    || session.Got<PlayerPositionPacket>().Any(p => p.X > startX + 1)), stop.Token);
            Assert.Contains(session.Got<ChatPacket>(), p => p.Message.Contains("On my way."));
            Assert.True(client.World.Player.Position.X > startX + 1, "The real physics client must walk toward tester, not just say it will");
            Assert.InRange(client.World.Player.Position.Y, 63.5, 64.5);
            Assert.True(Math.Abs(SkyblockStart.PlayerX - client.World.Player.Position.X) < Math.Abs(SkyblockStart.PlayerX - startX) - 1);
        }
        finally
        {
            stop.Cancel();
            runtime.Activities.Cancel("test finished");
            try { await brain; } catch (OperationCanceledException) { }
            await client.DisconnectAsync("test finished");
            await serving;
            bot.Dispose();
            runtime.Dispose();
        }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate()) await Task.Delay(20, ct);
    }
}
