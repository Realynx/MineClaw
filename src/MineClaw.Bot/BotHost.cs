using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
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
    /// <summary>Zero-token map memory (chests, generators, farms, islands...) built from the client's world copy.</summary>
    public WorldIndexer? Indexer { get; init; }
    /// <summary>Other players' activity: attributed events, rollups, digest, wake rules.</summary>
    public PlayerActivityTracker? Activity { get; init; }

    public static BotHost Create(IGameClient client, AgentOptions agentOptions, BotOptions? botOptions = null, ILlmClient? llm = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true,
        WorldIndexOptions? worldIndex = null, ActivityOptions? activity = null)
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

        var memory = new MemoryStoreAdapter(rt.Memory, rt.Observations,
            () => (MineClaw.World.BlockPos?)client.World.Player.Position.ToBlockPos());
        var protection = new Protection(botOptions, memory, memory, time);
        foreach (var u in protection.Unknown) log?.LogWarning("Bot config: unknown {Name}", u);
        var (indexer, activityTracker) = StartWorldIndex(client, rt, memory, worldIndex, activity, time, loggers);
        recorder.Activity = activityTracker; // teaching sessions reuse the tracker's attribution
        client.IsProtected = indexer == null ? protection.IsProtected : (p, id) => protection.IsProtected(p, id) || indexer.IsProtected(p, id);
        client.ReservedItems = protection.ReservedItems();

        GameToolSet.RegisterAll(rt.Tools, client, memory, botOptions,
            landmark: (x, y, z) => Agent.Memory.Landmarks.Near(rt.Memory, rt.Observations, x, y, z));
        HumanGameTools.RegisterAskStand(rt.Tools, rt.Chat, client, botOptions.AskStandTimeoutSeconds);
        HumanGameTools.RegisterWatchDemo(rt.Tools, rt.Chat, recorder, memory);
        if (client is MinecraftGameClient mg) CombatTools.Register(rt.Tools, mg.Inner, rt.Events);
        // Brain gets the cheap single-shot tools + delegate/ask_memory; the worker gets the game macro tools.
        rt.ApplyBrainSplit();

        var bridge = new GameBridge(client, rt, log, time);
        bridge.Attach();
        return new BotHost
        {
            Runtime = rt, Client = client, Bridge = bridge, Protection = protection, Recorder = recorder, MemoryAdapter = memory,
            Indexer = indexer, Activity = activityTracker,
        };
    }

    /// <summary>World indexer + activity tracker (background passes; never on the network thread, never the LLM).</summary>
    private static (WorldIndexer?, PlayerActivityTracker?) StartWorldIndex(IGameClient client, AgentRuntime rt, MemoryStoreAdapter memory,
        WorldIndexOptions? o, ActivityOptions? a, TimeProvider? time, ILoggerFactory? loggers)
    {
        o ??= new WorldIndexOptions();
        a ??= new ActivityOptions();
        if (!o.Enabled) return (null, null);
        IWorldIndexFeed feed = client is MinecraftGameClient mg ? new ClientIndexFeed(mg.Inner) : new ManualIndexFeed();
        var indexer = new WorldIndexer(client.World, new ObservationStoreSink(rt.Observations), feed, o, client.Username, time,
            loggers?.CreateLogger("MineClaw.WorldIndex")) { IsKnownArea = memory.IsKnownArea };
        indexer.Start();
        if (!a.Enabled) return (indexer, null);
        var tracker = new PlayerActivityTracker(client.World, new GameClientPlayerEvents(client), new ActivityLog(rt.Db, rt.Rag, a, time),
            indexer, a, client.Username,
            botNames: () => new[] { client.Username, rt.Chat.DisplayName }.Concat(rt.Chat.Aliases),
            botBusy: () => client.CurrentAction != null, time: time, logger: loggers?.CreateLogger("MineClaw.Activity"))
        { TaughtStructureAt = memory.TaughtStructureAt };
        tracker.Wake += e => rt.Events.Post(e);
        var lastEvidence = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        tracker.StructureEvidence += (name, r) =>
        {
            lock (lastEvidence)
            {
                if (lastEvidence.TryGetValue(name, out var t) && r.End - t < TimeSpan.FromHours(1)) return;
                lastEvidence[name] = r.End;
            }
            rt.Memory.AddNote(name, "seen: " + r.Text);
        };
        rt.Agent.TurnDigest = since => tracker.Digest.Since(since, rt.Options.TurnDigestMaxChars, prefix: "");
        ActivityTools.Register(rt.Tools, tracker.Log!, time);
        if (!rt.Options.AskMemory.Tools.Contains(ActivityTools.ToolName)) rt.Options.AskMemory.Tools = [.. rt.Options.AskMemory.Tools, ActivityTools.ToolName];
        tracker.Start();
        return (indexer, tracker);
    }

    public void Dispose()
    {
        Bridge.Dispose();
        Activity?.Dispose();
        Indexer?.Dispose();
    }
}
