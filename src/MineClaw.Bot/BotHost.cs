using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using Microsoft.Extensions.Logging;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot;

/// <summary>
/// Composition root for the game bot: creates the agent runtime with chat sent through the client, registers the
/// game tools (before the first turn, so the tool list is a stable cached prefix), applies protection/reserved items
/// and attaches the event bridge.
/// </summary>
public sealed class BotHost : IDisposable
{
    public required AgentRuntime Runtime { get; init; }
    public required IGameClient Client { get; init; }
    public required GameBridge Bridge { get; init; }
    public required Protection Protection { get; init; }
    public required DemonstrationRecorder Recorder { get; init; }
    public required MemoryStoreAdapter MemoryAdapter { get; init; }

    public static BotHost Create(IGameClient client, AgentOptions agentOptions, BotOptions? botOptions = null, ILlmClient? llm = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true)
    {
        botOptions ??= new BotOptions();
        var log = loggers?.CreateLogger("MineClaw.Bot");
        var recorder = new DemonstrationRecorder(client, botOptions, time);

        var rt = AgentRuntime.Create(agentOptions,
            async (line, ct) =>
            {
                var r = await client.SendChatAsync(line, ct).ConfigureAwait(false);
                if (!r.Ok) log?.LogWarning("chat not sent ({Reason}): {Line}", r.Message, line);
            },
            llm,
            position: () => client.IsPlaying && client.World.Player.Position.ToBlockPos() is var p ? new MPos(p.X, p.Y, p.Z) : null,
            demonstrations: recorder, loggers: loggers, time: time, persist: persist);

        // TODO(rag-rework): rewire these adapters to the Agent's new memory API when it lands.
        var memory = new MemoryStoreAdapter(rt.Memory);
        var protection = new Protection(botOptions, memory, memory, time);
        foreach (var u in protection.Unknown) log?.LogWarning("Bot config: unknown {Name}", u);
        client.IsProtected = protection.IsProtected;
        client.ReservedItems = protection.ReservedItems();

        GameToolSet.RegisterAll(rt.Tools, client, memory, botOptions);
        HumanGameTools.RegisterAskStand(rt.Tools, rt.Chat, client, botOptions.AskStandTimeoutSeconds);
        HumanGameTools.RegisterWatchDemo(rt.Tools, rt.Chat, recorder, memory);

        var bridge = new GameBridge(client, rt, log, time);
        bridge.Attach();
        return new BotHost
        {
            Runtime = rt, Client = client, Bridge = bridge, Protection = protection, Recorder = recorder, MemoryAdapter = memory,
        };
    }

    public void Dispose() => Bridge.Dispose();
}
