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
    /// <summary>Learned mechanisms (buttons/levers/plates → doors, piston doors), persisted as observations.</summary>
    public MechanismWatcher? Mechanisms { get; init; }
    /// <summary>The brainstem: head turn, reflexes, speculative first steps, social idle (zero tokens).</summary>
    public Brainstem.BrainstemController? Brainstem { get; init; }
    /// <summary>Hive claim board this bot honours (<see cref="NullClaimBoard"/> without a hive).</summary>
    public IClaimBoard Claims { get; init; } = NullClaimBoard.Instance;
    /// <summary>Hive: keeps the client's learned spawn-protection region in step with the shared protected_region fact.</summary>
    internal System.Threading.Timer? ProtectionSync { get; init; }

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
        var mechanisms = StartMechanisms(client, rt, time, loggers);
        recorder.Activity = activityTracker; // teaching sessions reuse the tracker's attribution
        // Generator dig cells (taught or regen-confirmed) are never protected, whatever box/wall protection covers them.
        var digCells = new Perception.DigCells(rt.Memory, indexer == null ? null : indexer.IsConfirmedDigCell) { Dimension = () => client.World.Dimension };
        // A player's correction ("wrong blocks", "not that one") makes the cells they teach next REPLACE the gen's old dig keys.
        var corrections = new Perception.DigCellCorrections(digCells, rt.Memory, time) { Dimension = () => client.World.Dimension };
        corrections.Changed += line => log?.LogInformation("dig cells corrected: {Line}", line);
        rt.Chat.Accepted += m => corrections.OnChat(m.Player, m.Text);
        digCells.TaughtCell += (p, by) => corrections.OnTaught(p, by);
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
        GameToolSet.RegisterAll(rt.Tools, toolClient, memory, botOptions, landmark: landmark, generatorCells: genCells,
            generator: new GameTools.GeneratorSupport(rt.Observations, (label, p) => rt.Memory.SetWaypoint(label, p.X, p.Y, p.Z, "storage chest placed by the generator loop"),
                BadCell: (p, why) => corrections.RetractBad(p, why),
                TaughtStand: cell => GameTools.GeneratorStand.Find(rt.Memory, cell))); // B12: stand where the demo stood
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
        // The speaker's facing rides along for a while after they talk to me ("that way" = where they face).
        rt.Chat.Accepted += m => { if (m.Player is { Length: > 0 } p) gestures.NoteSpoke(p, m.Text); };
        rt.Agent.TurnPerception = gestures.Line;
        // Idle check-ins: hourly at most while no human is online (the tab list, hive bots and this bot excluded).
        rt.Agent.HumansOnline = () => client.World.Entities.OnlinePlayers.Any(n => !n.Equals(client.Username, StringComparison.OrdinalIgnoreCase)
            && hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) != true);
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
        System.Threading.Timer? protectionSync = null;
        if (client is MinecraftGameClient mgp)
        {
            WireProtectedRegion(mgp.Inner, rt, log, claims, hiveBots);
            // Hive: the protected_region fact is shared (one db); a success in ANY process retracts it, so every process
            // drops its in-memory copy, and a region learned elsewhere is adopted (it already needed two undone edits).
            if (persist && (claims != null || hiveBots != null))
                protectionSync = new System.Threading.Timer(_ => SyncProtectedRegion(mgp.Inner, rt, log), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
        Beliefs.BeliefBot.Attach(rt, toolClient, claims, time, log, indexer); // belief system: claim checks when idle (zero tokens)
        var skillHub = SkillTools.Register(rt, client, claims); // worker: skill + find_skill over the whole catalog (zero prefix growth)
        // Inventory ledger: every toss/drop/use/consume/correction is recorded; inventory/where_is {item} answer "where did X go".
        if (client is MinecraftGameClient mgl) Ledger.InventoryLedgerTools.Attach(rt, mgl.Inner, skillHub.Registry, log, time);
        // Brain gets the cheap single-shot tools + delegate/ask_memory; the worker gets the game macro tools.
        rt.ApplyBrainSplit();

        var bridge = new GameBridge(client, rt, log, time)
        {
            Hunger = new HungerLatch(persist ? Path.Combine(agentOptions.DataDirectory, $"hunger-latch-{client.Username}.json") : null, time),
        };
        bridge.Attach();
        ScriptTools.Register(rt, client, skillHub, botOptions.Scripts, time, log); // script tool (brain+worker), after split + header
        VisionTools.Register(rt, client, memory, botOptions.Vision, gestures, hiveBots, time, log); // see (brain+worker) + look map marks
        // do {action, args, then?, queue?}: the brain's one compact door to the single-step macros (after every macro exists).
        Agent.Tools.DoTool.Register(rt.Tools);

        // Brainstem: deterministic controller under the brain (head turn, reflexes, speculative first step, social idle,
        // the [here] pre-observation). Attached last so its event gate sees what the script gate kept.
        var here = new Brainstem.HereBlock(client, rt.Memory, rt.Observations, landmark);
        rt.Agent.HereBlock = here.Build;
        Func<string, bool> isBot = n => hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true || rt.Chat.IsHiveBot?.Invoke(n) == true;
        var brainstem = new Brainstem.BrainstemController(rt, client, memory, isBot, time, loggers?.CreateLogger("MineClaw.Brainstem"));
        brainstem.Attach(gestures, timer: persist); // in-memory (test) hosts drive SocialTick by hand
        return new BotHost
        {
            Runtime = rt, Client = client, Bridge = bridge, Protection = protection, Recorder = recorder, MemoryAdapter = memory,
            Indexer = indexer, Activity = activityTracker, WorldMemory = worldMemory, Claims = claims ?? NullClaimBoard.Instance,
            Gestures = gestures, DigCells = digCells, Mechanisms = mechanisms, Brainstem = brainstem, ProtectionSync = protectionSync,
        };
    }

    /// <summary>
    /// Spawn protection learned by the client (logs that won't break near spawn) → one <c>protected_region</c> observation
    /// per dimension, so memory/where_is/the worker know about it; cleared when the client clears it.
    /// </summary>
    private static void WireProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log,
        IClaimBoard? claims = null, Func<IEnumerable<string>>? hiveBots = null)
    {
        int lastDim = 0;
        // An undone edit on a block another hive bot holds a lease on, or right next to another hive bot, is a race with it
        // (two bots on one tree crown), never evidence of spawn protection.
        if (claims != null || hiveBots != null)
            c.EditContested = p => IsContested(c, p, claims, hiveBots);
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

    /// <summary>Another hive bot holds a lease on <paramref name="p"/>, or stands within 4 blocks of it.</summary>
    internal static bool IsContested(MineClaw.Client.MinecraftClient c, MineClaw.World.BlockPos p, IClaimBoard? claims, Func<IEnumerable<string>>? hiveBots)
    {
        if (claims != null && !claims.IsFree(ClaimKey.Block(p, c.World.Dimension))) return true;
        foreach (var name in hiveBots?.Invoke() ?? [])
        {
            if (string.IsNullOrEmpty(name) || name.Equals(c.Options.Username, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.World.Entities.FindPlayer(name) is { } e
                && Math.Abs(e.Position.X - (p.X + 0.5)) <= 4.5 && Math.Abs(e.Position.Z - (p.Z + 0.5)) <= 4.5 && Math.Abs(e.Position.Y - p.Y) <= 6)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The client's learned region vs the shared protected_region fact: gone from the db (a success in another process retracted
    /// it) → forget it here too; present but not held here (learned by another hive process) → adopt it, keeping its time.
    /// </summary>
    internal static void SyncProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log)
    {
        try
        {
            var row = rt.Observations.Get("protected_region", "spawn", dimension: c.World.Dimension);
            if (c.ProtectedRegion is { } mine)
            {
                // Grace: my own fresh region may not be written yet (the event dispatcher upserts it).
                if (row == null && DateTime.UtcNow - mine.LearnedAt > TimeSpan.FromSeconds(30) && c.ForgetProtectedRegion("retracted in the shared memory"))
                    log?.LogInformation("spawn protection retracted elsewhere in the hive: forgot {Region}", mine);
                return;
            }
            if (row is not { Bounds: { } b }) return;
            int radius = Math.Max(1, (int)Math.Round((b.MaxX - b.MinX) / 2.0));
            var center = new MineClaw.World.BlockPos((int)Math.Round((b.MinX + b.MaxX) / 2.0), c.World.Spawn.Y, (int)Math.Round((b.MinZ + b.MaxZ) / 2.0));
            var r = new MineClaw.Client.LearnedProtectedRegion(center, radius, c.World.Dimension, 2, "shared: " + row.Text, row.LastSeen.UtcDateTime);
            if (c.AdoptProtectedRegion(r)) log?.LogInformation("adopted the hive's spawn protection fact: {Region}", r);
        }
        catch (Exception ex) { log?.LogDebug(ex, "protected_region sync failed"); }
    }

    /// <summary>Forgets notes claiming spawn protection blocks the bots (count removed).</summary>
    internal static int RetractSpawnProtectionNotes(AgentRuntime rt)
    {
        int n = 0;
        foreach (var phrase in new[] { "spawn protection", "spawn-protection", "spawn protected" }) n += rt.Memory.Forget("note", phrase);
        return n;
    }

    /// <summary>Mechanism learning (cause → effect from block changes), persisted links loaded into the world's graph,
    /// demonstrations and "what opens this?" answers from chat. The client's goto plans with the graph.</summary>
    private static MechanismWatcher StartMechanisms(IGameClient client, AgentRuntime rt, TimeProvider? time, ILoggerFactory? loggers)
    {
        var log = loggers?.CreateLogger("MineClaw.Mechanisms");
        var w = new MechanismWatcher(client.World, rt.Observations, time, log, client.Username);
        try { w.Load(); }
        catch (Exception ex) { log?.LogWarning(ex, "loading mechanisms failed"); }
        w.Attach(client).Start();
        rt.Chat.Accepted += m => w.NoteChat(m.Player, m.Text);
        w.Learned += o =>
        {
            if (o.IsNew || o.Demonstrated)
                rt.Events.Post(Agent.Events.GameEvent.Notice("learned mechanism: " + o.Link.Describe(), Agent.Events.EventPriority.Background));
        };
        return w;
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
        ProtectionSync?.Dispose();
        Bridge.Dispose();
        Brainstem?.Dispose();
        Gestures?.Dispose();
        Activity?.Dispose();
        Mechanisms?.Dispose();
        WorldMemory?.Dispose(); // final flush: the next join compares against exactly this
        Indexer?.Dispose();
    }
}
