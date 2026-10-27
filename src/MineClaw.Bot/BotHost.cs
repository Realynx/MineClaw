using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.World.Claims;
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
    /// <summary>Hive claim board this bot honours (<see cref="NullClaimBoard"/> without a hive).</summary>
    public IClaimBoard Claims { get; init; } = NullClaimBoard.Instance;

    public static BotHost Create(IGameClient client, AgentOptions agentOptions, BotOptions? botOptions = null, ILlmClient? llm = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true,
        WorldIndexOptions? worldIndex = null, ActivityOptions? activity = null,
        IClaimBoard? claims = null, Func<IEnumerable<string>>? hiveBots = null)
    {
        botOptions ??= new BotOptions();
        var log = loggers?.CreateLogger("MineClaw.Bot");
        var recorder = new DemonstrationRecorder(client, botOptions, time);

        var rt = AgentRuntime.Create(agentOptions,
            async (line, ct) =>
            {
                // The runtime already passed this line through its command gate (an armed run_command line is consumed
                // there), so the real client's adapter filter is bypassed here; every other sender goes through it.
                var r = client is MinecraftGameClient raw ? await raw.Inner.SendChatAsync(line, ct).ConfigureAwait(false)
                    : await client.SendChatAsync(line, ct).ConfigureAwait(false);
                if (!r.Ok) log?.LogWarning("chat not sent ({Reason}): {Line}", r.Message, line);
            },
            llm,
            position: () => client.IsPlaying && client.World.Player.Position.ToBlockPos() is var p ? new MPos(p.X, p.Y, p.Z) : null,
            demonstrations: recorder, loggers: loggers, time: time, persist: persist);
        // Memory is dimension-aware: writers stamp, readers ("where", recall, auto-retrieval, digest) default to this one.
        rt.Rag.CurrentDimension = () => client.World.Dimension;

        // Outgoing choke point for skills/tools/console chat: no unauthorised /command leaves through the adapter either.
        if (client is MinecraftGameClient gated) gated.OutgoingFilter = rt.Commands.AllowOutgoing;

        var memory = new MemoryStoreAdapter(rt.Memory, rt.Observations,
            () => (MineClaw.World.BlockPos?)client.World.Player.Position.ToBlockPos());
        var protection = new Protection(botOptions, memory, memory, time);
        foreach (var u in protection.Unknown) log?.LogWarning("Bot config: unknown {Name}", u);
        var (indexer, activityTracker) = StartWorldIndex(client, rt, memory, worldIndex, activity, time, loggers, hiveBots);
        recorder.Activity = activityTracker; // teaching sessions reuse the tracker's attribution
        Func<MineClaw.World.BlockPos, int, bool> isProtected = indexer == null ? protection.IsProtected : (p, id) => protection.IsProtected(p, id) || indexer.IsProtected(p, id);
        // Hive: a block another bot holds a lease on is off limits too (mining/harvest skip it).
        client.IsProtected = claims == null ? isProtected : (p, id) => isProtected(p, id) || !claims.IsFree(ClaimKey.Block(p, client.World.Dimension));
        client.ReservedItems = protection.ReservedItems();

        // Hive: container/block actions of the game tools take a lease first (ClaimAwareGameClient).
        IGameClient toolClient = claims == null ? client : new ClaimAwareGameClient(client, claims);
        GameToolSet.RegisterAll(rt.Tools, toolClient, memory, botOptions,
            landmark: (x, y, z) => Agent.Memory.Landmarks.Near(rt.Memory, rt.Observations, x, y, z));
        HumanGameTools.RegisterAskStand(rt.Tools, rt.Chat, client, botOptions.AskStandTimeoutSeconds);
        HumanGameTools.RegisterWatchDemo(rt.Tools, rt.Chat, recorder, memory);
        if (client is MinecraftGameClient mg) CombatTools.Register(rt.Tools, mg.Inner, rt.Events);
        if (client is MinecraftGameClient mgc) TreeTools.AttachReports(mgc.Inner, indexer, rt.Events);
        BodyLanguageTools.Register(rt.Tools, client); // act {move, target?, times?}: brain-only body language
        if (client is MinecraftGameClient mgp) WireProtectedRegion(mgp.Inner, rt, log);
        Beliefs.BeliefBot.Attach(rt, toolClient, claims, time, log, indexer); // belief system: claim checks when idle (zero tokens)
        SkillTools.Register(rt, client, claims); // worker: skill + find_skill over the whole catalog (zero prefix growth)
        // Brain gets the cheap single-shot tools + delegate/ask_memory; the worker gets the game macro tools.
        rt.ApplyBrainSplit();

        var bridge = new GameBridge(client, rt, log, time);
        bridge.Attach();
        return new BotHost
        {
            Runtime = rt, Client = client, Bridge = bridge, Protection = protection, Recorder = recorder, MemoryAdapter = memory,
            Indexer = indexer, Activity = activityTracker, Claims = claims ?? NullClaimBoard.Instance,
        };
    }

    /// <summary>
    /// Spawn protection learned by the client (logs that won't break near spawn) → one <c>protected_region</c> observation
    /// per dimension, so memory/where_is/the worker know about it; cleared when the client clears it.
    /// </summary>
    private static void WireProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log)
    {
        int lastDim = 0;
        c.ProtectedRegionChanged += r =>
        {
            try
            {
                if (r == null) { rt.Observations.Remove("protected_region", "spawn", tombstone: false, dimension: lastDim); return; }
                lastDim = r.Dimension;
                rt.Observations.Upsert(new Agent.Rag.Observation("protected_region", "spawn",
                    $"spawn protection: can't break blocks within {r.Radius} of {r.Center.X},{r.Center.Y},{r.Center.Z} ({r.Reason})")
                {
                    Bounds = Agent.Rag.Box.Of(r.Center.X - r.Radius, 0, r.Center.Z - r.Radius, r.Center.X + r.Radius, 127, r.Center.Z + r.Radius),
                    Dimension = r.Dimension,
                    Confidence = 0.9,
                });
            }
            catch (Exception ex) { log?.LogWarning(ex, "protected_region observation failed"); }
        };
    }

    /// <summary>World indexer + activity tracker (background passes; never on the network thread, never the LLM).</summary>
    private static (WorldIndexer?, PlayerActivityTracker?) StartWorldIndex(IGameClient client, AgentRuntime rt, MemoryStoreAdapter memory,
        WorldIndexOptions? o, ActivityOptions? a, TimeProvider? time, ILoggerFactory? loggers, Func<IEnumerable<string>>? hiveBots)
    {
        o ??= new WorldIndexOptions();
        a ??= new ActivityOptions();
        if (!o.Enabled) return (null, null);
        IWorldIndexFeed feed = client is MinecraftGameClient mg ? new ClientIndexFeed(mg.Inner) : new ManualIndexFeed();
        IDimensionSource dims = client is MinecraftGameClient mgd ? new LiveDimensionSource(mgd.Inner) : new WorldDimensionSource(client.World);
        var indexer = new WorldIndexer(client.World, new ObservationStoreSink(rt.Observations), feed, o, client.Username, time,
            loggers?.CreateLogger("MineClaw.WorldIndex"), dims) { IsKnownArea = memory.IsKnownArea };
        indexer.Start();
        if (!a.Enabled) return (indexer, null);
        var tracker = new PlayerActivityTracker(client.World, new GameClientPlayerEvents(client), new ActivityLog(rt.Db, rt.Rag, a, time),
            indexer, a, client.Username,
            botNames: () => new[] { client.Username, rt.Chat.DisplayName }.Concat(rt.Chat.Aliases).Concat(hiveBots?.Invoke() ?? []),
            botBusy: () => client.CurrentAction != null, time: time, logger: loggers?.CreateLogger("MineClaw.Activity"), dimensions: dims)
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
