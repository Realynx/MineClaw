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
    /// <summary>Remembered world (chunk baselines, "since last time", stale planning data) and the scenery ledger.</summary>
    public WorldMemory? WorldMemory { get; init; }
    /// <summary>Players' pointing gestures (punches at blocks), for the turn context.</summary>
    public Perception.GestureTracker? Gestures { get; init; }
    /// <summary>Generator dig cells (taught or regen-confirmed): never protected.</summary>
    public Perception.DigCells? DigCells { get; init; }
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
        var worldMemory = StartWorldMemory(client, rt, indexer, worldIndex, time, loggers);
        recorder.Activity = activityTracker; // teaching sessions reuse the tracker's attribution
        // Generator dig cells (taught or regen-confirmed) are never protected, whatever box/wall protection covers them.
        var digCells = new Perception.DigCells(rt.Memory, indexer == null ? null : indexer.IsConfirmedDigCell) { Dimension = () => client.World.Dimension };
        Func<MineClaw.World.BlockPos, int, bool> boxed = indexer == null ? protection.IsProtected : (p, id) => protection.IsProtected(p, id) || indexer.IsProtected(p, id);
        Func<MineClaw.World.BlockPos, int, bool> isProtected = (p, id) => boxed(p, id) && !digCells.IsDigCell(p);
        // Hive: a block another bot holds a lease on is off limits too (mining/harvest skip it).
        client.IsProtected = claims == null ? isProtected : (p, id) => isProtected(p, id) || !claims.IsFree(ClaimKey.Block(p, client.World.Dimension));
        client.ReservedItems = protection.ReservedItems();

        // Hive: container/block actions of the game tools take a lease first (ClaimAwareGameClient).
        IGameClient toolClient = claims == null ? client : new ClaimAwareGameClient(client, claims);
        Func<double, double, double, string?> landmark = (x, y, z) => Agent.Memory.Landmarks.Near(rt.Memory, rt.Observations, x, y, z);
        // Relational block names for questions/needs_input ("the west cell of the gen, right next to the lava").
        Func<int, int, int, string?> blockLandmark = (x, y, z) => Perception.BlockDescriber.Landmark(client.World, new MineClaw.World.BlockPos(x, y, z),
            (a, b, c2) => landmark(a, b, c2));
        rt.Chat.Landmark = blockLandmark;
        if (rt.Worker != null) rt.Worker.Landmark = blockLandmark;
        // All dig cells of the generator around a block: geometry + the indexer + taught cells (claim-aware harvest picks a free one).
        Func<MineClaw.World.BlockPos, IReadOnlyList<MineClaw.World.BlockPos>> genCells = p =>
            Perception.BlockDescriber.GenCellsNear(client.World, p, 4)
                .Concat(indexer?.GeneratorCellsNear(p, 4) ?? [])
                .Concat(digCells.Taught().Where(t => Math.Abs(t.X - p.X) <= 4 && Math.Abs(t.Y - p.Y) <= 2 && Math.Abs(t.Z - p.Z) <= 4))
                .Distinct().ToList();
        GameToolSet.RegisterAll(rt.Tools, toolClient, memory, botOptions, landmark: landmark, generatorCells: genCells);
        HumanGameTools.RegisterAskStand(rt.Tools, rt.Chat, client, botOptions.AskStandTimeoutSeconds, digCells: digCells);
        HumanGameTools.RegisterWatchDemo(rt.Tools, rt.Chat, recorder, memory);
        // Gesture perception: a punch while looking at a block is pointing (outside demos too); a punched gen cell is taught.
        var gestures = new Perception.GestureTracker(client, isBot: n => hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true
                || n.Equals(rt.Chat.Username, StringComparison.OrdinalIgnoreCase), time,
            describe: (w, p, _) => Perception.BlockDescriber.Describe(w, p, (a, b, c2) => landmark(a, b, c2)));
        gestures.Pointed += g =>
        {
            if (!Perception.BlockDescriber.IsGenCell(client.World, g.Pos)) return;
            if (digCells.Register(g.Pos, g.Player)) log?.LogInformation("dig cell taught by {Player}'s punch: {Pos}", g.Player, g.Pos);
        };
        rt.Agent.TurnPerception = gestures.Line;
        if (rt.Agent.TurnDigest is { } digest)
            rt.Agent.TurnDigest = since =>
            {
                var d = digest(since);
                var older = gestures.Older(since);
                return older.Length == 0 ? d : string.IsNullOrWhiteSpace(d) ? older : d + "; " + older;
            };
        if (client is MinecraftGameClient mg) CombatTools.Register(rt.Tools, mg.Inner, rt.Events);
        if (client is MinecraftGameClient mgc) TreeTools.AttachReports(mgc.Inner, indexer, rt.Events);
        BodyLanguageTools.Register(rt.Tools, client); // act {move, target?, times?}: brain-only body language
        if (client is MinecraftGameClient mgp) WireProtectedRegion(mgp.Inner, rt, log);
        Beliefs.BeliefBot.Attach(rt, toolClient, claims, time, log, indexer); // belief system: claim checks when idle (zero tokens)
        var skillHub = SkillTools.Register(rt, client, claims); // worker: skill + find_skill over the whole catalog (zero prefix growth)
        // Brain gets the cheap single-shot tools + delegate/ask_memory; the worker gets the game macro tools.
        rt.ApplyBrainSplit();

        var bridge = new GameBridge(client, rt, log, time);
        bridge.Attach();
        ScriptTools.Register(rt, client, skillHub, botOptions.Scripts, time, log); // script tool (brain+worker), after split + header
        return new BotHost
        {
            Runtime = rt, Client = client, Bridge = bridge, Protection = protection, Recorder = recorder, MemoryAdapter = memory,
            Indexer = indexer, Activity = activityTracker, WorldMemory = worldMemory, Claims = claims ?? NullClaimBoard.Instance,
            Gestures = gestures, DigCells = digCells,
        };
    }

    /// <summary>
    /// Spawn protection learned by the client (logs that won't break near spawn) → one <c>protected_region</c> observation
    /// per dimension, so memory/where_is/the worker know about it; cleared when the client clears it.
    /// </summary>
    private static void WireProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log)
    {
        int lastDim = 0;
        // A stale "spawn protection" belief (accounts got op, or a past session's guess) is retracted by any edit that works
        // inside it: the observation AND the brain's notes about it.
        c.EditSucceededNearSpawn += (p, dim) =>
        {
            try
            {
                if (rt.Observations.Get("protected_region", "spawn", dimension: dim) is not { Bounds: { } b }) return;
                if (p.X < b.MinX || p.X > b.MaxX || p.Z < b.MinZ || p.Z > b.MaxZ) return;
                rt.Observations.Remove("protected_region", "spawn", tombstone: false, dimension: dim);
                int n = RetractSpawnProtectionNotes(rt);
                log?.LogInformation("edit at {Pos} worked inside the remembered spawn protection: retracted it ({Notes} note(s))", p, n);
            }
            catch (Exception ex) { log?.LogWarning(ex, "protected_region retraction failed"); }
        };
        c.ProtectedRegionChanged += r =>
        {
            try
            {
                if (r == null)
                {
                    rt.Observations.Remove("protected_region", "spawn", tombstone: false, dimension: lastDim);
                    RetractSpawnProtectionNotes(rt);
                    return;
                }
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

    /// <summary>
    /// Remembered world + scenery ledger: unknown builds already announced/asked (or there, unchanged, last time) stay quiet
    /// across restarts; real changes while away become one "since last time" activity line.
    /// </summary>
    private static WorldMemory? StartWorldMemory(IGameClient client, AgentRuntime rt, WorldIndexer? indexer, WorldIndexOptions? o,
        TimeProvider? time, ILoggerFactory? loggers)
    {
        var opts = (o ?? new WorldIndexOptions()).Remembered;
        if (indexer == null || !opts.Enabled) return null;
        var store = new SqliteWorldMemoryStore(rt.Db);
        var wm = new WorldMemory(client.World, store, opts, time, loggers?.CreateLogger("MineClaw.WorldMemory"));
        WorldMemory.GateAnnouncements(indexer, wm, new SceneryLedger(store, time));
        wm.SinceLastTime += line => rt.Events.Post(Agent.Events.GameEvent.Activity(line)); // clones drop Activity (main's job)
        wm.Start();
        return wm;
    }

    /// <summary>Forgets notes claiming spawn protection blocks the bots (count removed).</summary>
    internal static int RetractSpawnProtectionNotes(AgentRuntime rt)
    {
        int n = 0;
        foreach (var phrase in new[] { "spawn protection", "spawn-protection", "spawn protected" }) n += rt.Memory.Forget("note", phrase);
        return n;
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
        Gestures?.Dispose();
        Activity?.Dispose();
        WorldMemory?.Dispose(); // final flush: the next join compares against exactly this
        Indexer?.Dispose();
    }
}
