using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Bodies;
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
    /// <summary>Vision v2: the see hub, the perception tier (telemetry) and auto-vision (null parts when off).</summary>
    public Perception.Vision.VisionWiring.Result? Vision { get; init; }
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
    /// <summary>Body-language gestures from the bot's own chat/emotes and addressed player chat (zero tokens).</summary>
    public Brainstem.Gestures? BodyGestures { get; init; }
    /// <summary>Hive claim board this bot honours (<see cref="NullClaimBoard"/> without a hive).</summary>
    public IClaimBoard Claims { get; init; } = NullClaimBoard.Instance;
    /// <summary>The acquisition planner's sources for this bot. Seam for the hive: set <c>RequestHandoff</c> (ask main / a clone
    /// holding a spare to hand it over).</summary>
    public AcquisitionSources? Acquisition { get; init; }
    /// <summary>Hive: keeps the client's learned spawn-protection region in step with the shared protected_region fact.</summary>
    internal System.Threading.Timer? ProtectionSync { get; init; }

    /// <summary>
    /// One brain, one body (step 1 of docs/design/one-brain-many-bodies.md): <see cref="CreateBrain"/> (runtime, shared stores,
    /// tools against the body routers), then <see cref="BodyFactory"/> wires the main body. The order is the old single-body
    /// order: the brain split before the script tool, see/do after every macro and the script tool, the brainstem gate last.
    /// </summary>
    public static BotHost Create(IGameClient client, AgentOptions agentOptions, BotOptions? botOptions = null, ILlmClient? llm = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true,
        WorldIndexOptions? worldIndex = null, ActivityOptions? activity = null,
        IClaimBoard? claims = null, Func<IEnumerable<string>>? hiveBots = null)
    {
        var brain = CreateBrain(client, agentOptions, botOptions, llm, loggers, time, persist, worldIndex, activity, claims, hiveBots);
        var main = brain.Main;
        BodyFactory.AttachJobs(brain, main);      // bridge (+ hunger latch, state header), the script lane (+ the script tool)
        FinishTools(brain);                       // see, then do (its door covers every macro that exists now)
        BodyFactory.AttachBehaviour(brain, main); // [here], brainstem (gate last), gestures, watchdog, standing orders, hand-overs
        return new BotHost
        {
            Brain = brain, Body = main,
            Watchdog = main.Watchdog,
            Handovers = main.Handovers, HandoverPump = main.HandoverPump,
            Runtime = brain.Runtime, Client = client, Bridge = main.Bridge!, Protection = brain.Protection, Recorder = brain.Recorder,
            MemoryAdapter = brain.Memory,
            Indexer = brain.Indexer, Activity = brain.Activity, WorldMemory = brain.WorldMemory, Claims = claims ?? NullClaimBoard.Instance,
            Gestures = brain.Gestures, DigCells = brain.DigCells, Mechanisms = brain.Mechanisms, Brainstem = main.Brainstem,
            BodyGestures = main.Gestures, ProtectionSync = brain.ProtectionSync,
            Acquisition = brain.Acquisition,
            PlacedBlocks = brain.PlacedBlocks, ProtectionMarks = brain.ProtectionMarks, ProtectionCommands = brain.ProtectionCommands,
            ProtectionTelemetry = brain.ProtectionTelemetry,
            Vision = brain.Vision,
            Blueprints = brain.Blueprints,
        };
    }

    /// <summary>The brain this host was built from (runtime, shared stores, body routers).</summary>
    public BrainContext? Brain { get; init; }
    /// <summary>The main body (step 1: the registry's only one).</summary>
    public Body? Body { get; init; }
    /// <summary>The bodies (step 1: exactly main).</summary>
    public BodyRegistry? Bodies => Brain?.Bodies;

    /// <summary>
    /// The brain: the agent runtime with chat sent through the main body's account, the shared stores and protection, the main
    /// body's perception, and every tool, registered once against the body routers (they act on <see cref="BodyScope.Current"/>,
    /// else the focus) before the first turn so the tool list is a stable cached prefix. Ends with the brain split and the
    /// blueprint tools; the per-body wiring follows (<see cref="BodyFactory"/>).
    /// </summary>
    public static BrainContext CreateBrain(IGameClient client, AgentOptions agentOptions, BotOptions? botOptions = null, ILlmClient? llm = null,
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

        // One brain, many bodies: the main body (the runtime's activity slot and standing orders) and the two routers every
        // tool registers against. Hive: container/block actions of the game tools take a lease first (ClaimAwareGameClient).
        IGameClient mainGame = claims == null ? client : new ClaimAwareGameClient(client, claims);
        var main = new Body(client.Username, isMain: true, client, mainGame, rt.Activities, rt.Agent.Orders);
        var bodies = new BodyRegistry(main);
        var bodyLog = loggers?.CreateLogger("MineClaw.Bodies");
        var toolClient = new BodyRoutingGameClient(bodies, b => b.Game, bodyLog);
        var adapter = new BodyRoutingGameClient(bodies, b => b.Adapter, bodyLog);
        rt.Agent.BodyActivities = name => bodies.Find(name)?.Activities;

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
        AttachHandoffIntents(client, rt, hiveBots, log);
        digCells.TaughtCell += (p, by) => corrections.OnTaught(p, by);
        digCells.FluidsAround = p => Perception.GenLayout.FluidsAround(client.World, p);
        // Stray "cells" off the gen's dig layer (the block over a cell, taught by a punch while a player mined) go, logged.
        try
        {
            foreach (var (cell, why) in digCells.PurgeOffLayer((p, why) => corrections.RetractBad(p, why)))
                log?.LogInformation("dig cell {Cell} purged: {Why}", cell, why);
        }
        catch (Exception ex) { log?.LogDebug("dig cell purge skipped: {Err}", ex.Message); }
        // The world check needs the gen's chunks: every minute for the first 15, once they're loaded.
        if (persist) _ = PurgeNonCellsAsync(client, digCells, corrections, time ?? TimeProvider.System, log);
        // The protection policy (ARCHITECTURE.md "Protection policy"): every source in one place, soft vs hard, overrides.
        var placedStore = new PlacedBlockStore(rt.Db, client.World.Placed, log); // rule 1: own/hive placements, shared + persistent
        if (persist) placedStore.Start();
        var marks = new ProtectionMarks(rt.Db, time); // rule 3/4: trusted permits, never-break marks (shared db)
        // Gen regen cells take a never-break mark only on that exact cell; vague old ones on confirmed regen cells go (one-time, idempotent).
        marks.IsGenCell = digCells.IsDigCell;
        corrections.Marks = marks;
        corrections.UnmarkConfirmedGenCells(rt.Observations, rt.Options.Self.TrustedPlayers.FirstOrDefault() ?? "a trusted player");
        var telemetry = new ProtectionTelemetry(log, persist ? agentOptions.DataDirectory : null, time); // rule 6
        protection.IndexerKind = indexer == null ? null : indexer.ProtectionKind;
        protection.BlockMeta = p => client.World.GetMeta(p); // crop growth stage: ripe crops are harvestable anywhere
        protection.IsDigCell = digCells.IsDigCell;
        protection.Claims = claims; // hive: a block another bot holds a lease on is soft-protected while it works there
        protection.Placed = client.World.Placed;
        protection.Marks = marks;
        protection.Dimension = () => client.World.Dimension;
        protection.Telemetry = telemetry;
        client.IsProtected = protection.IsProtected;
        if (client is MinecraftGameClient mgx)
        {
            mgx.Inner.ExplainProtection = protection.Explain;
            mgx.Inner.SoftOverride = protection.OverridesSoft;
            mgx.Inner.CountRefusal = r => telemetry.Count(r);
            mgx.Inner.NeverBreak = p => marks.HasNever && marks.NeverAt(p, client.World.Dimension) != null;
            // Autonomous mine: taught / regen-confirmed generator cells are resource sources too.
            mgx.Inner.MineSource = (p, _) => digCells.IsDigCell(p) || indexer?.IsConfirmedDigCell(p) == true;
        }
        if (botOptions.Protection.ServerOp) ForgetSpawnProtection(rt, log, client.Username);
        client.ReservedItems = protection.ReservedItems();

        Func<double, double, double, string?> landmark = (x, y, z) => Agent.Memory.Landmarks.Near(rt.Memory, rt.Observations, x, y, z);
        // Relational block names for questions/needs_input ("the west cell of the gen, right next to the lava").
        Func<int, int, int, string?> blockLandmark = (x, y, z) => Perception.BlockDescriber.Landmark(client.World, new MineClaw.World.BlockPos(x, y, z),
            (a, b, c2) => landmark(a, b, c2));
        rt.Chat.Landmark = blockLandmark;
        // Addressing classifier: only while the bot can actually see the speaker nearby.
        rt.Chat.IsNear = name => client.IsPlaying && client.World.Entities.FindPlayer(name) is { } pe &&
            pe.Position.DistanceTo(client.World.Player.Position) <= rt.Options.Chat.Classifier.NearBlocks;
        // Addressee by gaze/proximity: whom the speaker looks at or stands right next to (us = our username).
        rt.Chat.LookingAt = name =>
        {
            if (!client.IsPlaying || client.World.Entities.FindPlayer(name) is not { } sp) return null;
            var others = client.World.Entities.All()
                .Where(e => e.Kind == MineClaw.World.EntityKind.Player && e.Name is { Length: > 0 } n && !n.Equals(name, StringComparison.OrdinalIgnoreCase)
                            && !n.Equals(client.Username, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Name!, e.Position))
                .Append((client.Username, client.World.Player.Position));
            return MineClaw.Bot.Brainstem.GazeAddress.Target(new MineClaw.Bot.Brainstem.HandoverWatch.PlayerView(name, sp.Position, sp.Yaw, sp.Pitch), others);
        };
        if (rt.Worker != null) rt.Worker.Landmark = blockLandmark;
        // All dig cells of the generator around a block: geometry + the indexer + taught cells (claim-aware harvest picks a free one).
        Func<MineClaw.World.BlockPos, IReadOnlyList<MineClaw.World.BlockPos>> genCells = p =>
            Perception.BlockDescriber.GenCellsNear(client.World, p, 4)
                .Concat(indexer?.GeneratorCellsNear(p, 4) ?? [])
                .Concat(digCells.Taught().Where(t => Math.Abs(t.X - p.X) <= 4 && Math.Abs(t.Y - p.Y) <= 2 && Math.Abs(t.Z - p.Z) <= 4))
                .Distinct().ToList();
        // The main body's acquisition sources (attached to its plain adapter; the router finds them for the body in scope).
        var acquisition = AttachAcquisition(client, rt, genCells, digCells, indexer, log);
        // A never-touch mark on a chest keeps every obtain/craft withdrawal out of it (clone bodies read this through).
        acquisition.SkipChest = p => marks.HasNever && marks.NeverAt(p, client.World.Dimension) != null ? "marked never-touch" : null;
        GameToolSet.RegisterAll(rt.Tools, toolClient, memory, botOptions, landmark: landmark, generatorCells: genCells,
            generator: new GameTools.GeneratorSupport(rt.Observations, (label, p) => rt.Memory.SetWaypoint(label, p.X, p.Y, p.Z, "storage chest placed by the generator loop"),
                BadCell: (p, why) => corrections.RetractBad(p, why),
                Diagnose: cell => Perception.GenLayout.Diagnose(client.World, rt.Memory, client.World.Dimension, cell),
                TaughtStand: cell => GameTools.GeneratorStand.Find(rt.Memory, cell))); // B12: stand where the demo stood
        HumanGameTools.RegisterAskStand(rt.Tools, rt.Chat, adapter, botOptions.AskStandTimeoutSeconds, digCells: digCells);
        HumanGameTools.RegisterWatchDemo(rt.Tools, rt.Chat, recorder, memory);
        // Gesture perception: a punch while looking at a block is pointing (outside demos too); a punched gen cell is taught.
        var gestures = new Perception.GestureTracker(client, isBot: n => hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true
                || n.Equals(rt.Chat.Username, StringComparison.OrdinalIgnoreCase), time,
            describe: (w, p, _) => Perception.BlockDescriber.Describe(w, p, (a, b, c2) => landmark(a, b, c2)));
        var punched = new Dictionary<string, List<(MineClaw.World.BlockPos Pos, DateTimeOffset At)>>(StringComparer.OrdinalIgnoreCase);
        gestures.Pointed += g =>
        {
            // Live 10-27 21:46: Fox punched 3 cells, the log showed 2 — the third was already known, so nothing was logged.
            if (Perception.BlockDescriber.GenCellRejection(client.World, g.Pos) is { } why)
            {
                bool nearGen = genCells(g.Pos).Any(c => Math.Abs(c.X - g.Pos.X) <= 3 && Math.Abs(c.Y - g.Pos.Y) <= 1 && Math.Abs(c.Z - g.Pos.Z) <= 3);
                if (nearGen) log?.LogInformation("punch by {Player} at {Pos} next to the gen isn't a dig cell: {Why}", g.Player, g.Pos, why);
                return;
            }
            // Players mining their own gen punch the cobble that forms over a cell too: off the known dig layer = not a cell.
            if (digCells.OffLayer(g.Pos) is { } off)
            {
                log?.LogInformation("punch by {Player} at {Pos} isn't a dig cell: {Why}", g.Player, g.Pos, off);
                return;
            }
            bool added = digCells.Register(g.Pos, g.Player);
            if (added) log?.LogInformation("dig cell taught by {Player}'s punch: {Pos}", g.Player, g.Pos);
            else log?.LogInformation("dig cell confirmed by {Player}'s punch (already known): {Pos}", g.Player, g.Pos);
            var now = (time ?? TimeProvider.System).GetUtcNow();
            List<MineClaw.World.BlockPos> recent;
            lock (punched)
            {
                if (!punched.TryGetValue(g.Player, out var list)) punched[g.Player] = list = new();
                list.RemoveAll(x => now - x.At > TimeSpan.FromSeconds(90) || x.Pos == g.Pos);
                list.Add((g.Pos, now));
                recent = list.Select(x => x.Pos).ToList();
            }
            // The brain's confirmation counts every punched cell, known ones included ("got 3 of 3").
            rt.Events.Post(Agent.Events.GameEvent.Notice(
                $"{g.Player} punched gen cell {g.Pos.X},{g.Pos.Y},{g.Pos.Z}{(added ? " (new)" : " (already known)")}; " +
                $"{recent.Count} gen cell(s) punched in the last 90s: {string.Join(" ", recent.Select(p => $"{p.X},{p.Y},{p.Z}"))}",
                Agent.Events.EventPriority.Background));
        };
        // The speaker's facing rides along for a while after they talk to me ("that way" = where they face).
        rt.Chat.Accepted += m => { if (m.Player is { Length: > 0 } p) gestures.NoteSpoke(p, m.Text); };
        // Protection from chat: trusted "break that" (+ punch) / "you can break it" permits, anyone's "never break X" (zero tokens).
        var protectCmds = new ProtectionCommands(marks, protection, client.World, botOptions.Protection, time)
        {
            Trusted = () => rt.Options.Self.TrustedPlayers,
            PointedBy = name => gestures.Recent().LastOrDefault(g => g.Player.Equals(name, StringComparison.OrdinalIgnoreCase))?.Pos,
            NamedArea = name => NamedStructure(rt, client, name),
            GenCells = () => acquisition.GeneratorCells?.Invoke() ?? [],
            Say = line => _ = rt.Chat.SayAsync(line),
            Notice = line => rt.Events.Post(Agent.Events.GameEvent.Notice(line, Agent.Events.EventPriority.Low)),
            GenCellAllowed = (cell, by, middle) => corrections.UnmarkGenCell(cell, by, middle),
        };
        rt.Chat.Accepted += m =>
        {
            try { if (protectCmds.OnChat(m.Player, m.Text) is { } did) log?.LogInformation("protection: {Line}", did); }
            catch (Exception ex) { log?.LogWarning(ex, "protection command failed"); }
        };
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
        if (client is MinecraftGameClient mg)
        {
            // The fight tools act on the body in scope; the main body's fight reports are wired here (per body in step 2).
            CombatTools.Register(rt.Tools, () => GameClients.Minecraft(adapter)?.Inner ?? mg.Inner);
            CombatTools.AttachEvents(mg.Inner, rt.Events);
        }
        if (client is MinecraftGameClient mgc) TreeTools.AttachReports(mgc.Inner, indexer, rt.Events);
        BodyLanguageTools.Register(rt.Tools, adapter); // act {move, target?, times?}: brain-only body language
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
        var skillHub = SkillTools.Register(rt, adapter, claims); // worker: skill + find_skill over the whole catalog (zero prefix growth)
        // Inventory ledger: every toss/drop/use/consume/correction is recorded; inventory/where_is {item} answer "where did X go".
        // The main body's ledger (a ledger per body comes with in-process clone bodies, step 2).
        if (client is MinecraftGameClient mgl)
        {
            var ledger = Ledger.InventoryLedgerTools.Attach(rt, mgl.Inner, skillHub.Registry, log, time);
            main.Ledger = ledger; // a clone body's own ledger: BodyFactory.CreateClone
            // A worker's time slice reports what it gathered and where it went ("ok: time slice done (5 min): 32 cobblestone; ...").
            if (rt.Worker != null)
                rt.Worker.GainsTracker = () =>
                {
                    var t0 = (time ?? TimeProvider.System).GetUtcNow();
                    return () => ledger.GainsSince(t0);
                };
        }
        IntentionalReset.Attach(rt, client, log); // into_void: announce once, intentional-reset outcome, the void-reset claim's evidence
        // Shared chest memory vs what an open shows: "bread x5 gone since maid_chan opened it 1m ago; Fox may have taken it".
        if (indexer != null)
            WorldIndex.ChestChanges.Attach(rt, client, indexer,
                n => n.Equals(client.Username, StringComparison.OrdinalIgnoreCase) || hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true
                     || rt.Chat.IsHiveBot?.Invoke(n) == true, time, log,
                current: () => BodyScope.Current is Body { Indexer: { } own } ? own : indexer); // a clone's withdraw: its own eyes
        // Brain gets the cheap single-shot tools + delegate/ask_memory; the worker gets the game macro tools.
        rt.ApplyBrainSplit();

        // Build from a description: design (brain, Opus authoring) + hidden build/blueprint tools (Lua build{}/blueprint()), chat review.
        // The builder resolves its body per call through the routers (never a captured client).
        var blueprints = botOptions.Blueprints.Enabled ? MineClaw.Bot.Blueprints.BlueprintWiring.Attach(rt, adapter, toolClient, botOptions.Blueprints, protection, acquisition, genCells, claims, time, log, persist) : null;
        if (blueprints != null) skillHub.Registry.Footprints ??= blueprints.Service.Footprints; // expand_island area: does the floor cover the design?
        if (blueprints != null) skillHub.Registry.Design ??= blueprints.Service.RegionOf; // light_area/spawn_map design= and its deck

        Func<string, bool> isBot = n => hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true || rt.Chat.IsHiveBot?.Invoke(n) == true;
        return new BrainContext
        {
            Runtime = rt, AgentOptions = agentOptions, BotOptions = botOptions,
            Bodies = bodies, Tools = toolClient, Adapter = adapter,
            Protection = protection, Memory = memory, Recorder = recorder, DigCells = digCells, Gestures = gestures,
            Acquisition = acquisition, Skills = skillHub, Landmark = landmark, GenCells = genCells, IsBot = isBot,
            Indexer = indexer, Activity = activityTracker, WorldMemory = worldMemory, Mechanisms = mechanisms, Blueprints = blueprints,
            PlacedBlocks = placedStore, ProtectionMarks = marks, ProtectionCommands = protectCmds, ProtectionTelemetry = telemetry,
            ProtectionSync = protectionSync, Claims = claims, HiveBots = hiveBots,
            Loggers = loggers, Log = log, Time = time, Persist = persist,
        };
    }

    /// <summary>
    /// The brain's last tools, after every macro and the script tool exist: vision v2 (see = contact sheet + role layer; with a
    /// perception model the brain gets verified text) and <c>do</c> (the brain's door to the single-step macros). Vision is the
    /// brain's eyes through the main body (other bodies are seen through the shared world memory: <c>see focus=67</c>).
    /// </summary>
    public static void FinishTools(BrainContext brain)
    {
        if (brain.ToolsFinished) return;
        var rt = brain.Runtime;
        var client = brain.Main.Adapter;
        var indexer = brain.Indexer;
        var digCells = brain.DigCells;
        var vision = Perception.Vision.VisionWiring.Attach(rt, client, brain.Memory, brain.BotOptions, brain.Protection,
            p => digCells.IsDigCell(p) || indexer?.IsConfirmedDigCell(p) == true, indexer, brain.Gestures, brain.GenCells, brain.HiveBots,
            brain.Time, brain.Log, brain.Persist);
        if (brain.Blueprints != null) brain.Blueprints.Vision = vision; // the preview's roles/atlas and the perception critic
        Agent.Tools.DoTool.Register(rt.Tools);
        brain.Vision = vision;
        brain.ToolsFinished = true;
    }

    /// <summary>Build from a description: the design service, its tools and the build crew seam (null when disabled).</summary>
    public MineClaw.Bot.Blueprints.BlueprintWiring? Blueprints { get; init; }
    /// <summary>Rule 1 persistence: own/hive placed blocks in the shared db.</summary>
    public PlacedBlockStore? PlacedBlocks { get; init; }
    /// <summary>Trusted permits and never-break marks (shared db).</summary>
    public ProtectionMarks? ProtectionMarks { get; init; }
    /// <summary>Chat → permits/marks.</summary>
    public ProtectionCommands? ProtectionCommands { get; init; }
    /// <summary>Refusals counted by reason.</summary>
    public ProtectionTelemetry? ProtectionTelemetry { get; init; }

    /// <summary>A remembered structure by name ("main house", "the house" → the one whose name contains it), as a box.</summary>
    /// <summary>
    /// Hand-off intents from chat (live 10-27 15:48: bread dropped "for 67" was auto-eaten the tick it was picked up): the bot's
    /// own "I'll pass it to 67" or a player's "pass this to 69" / "bread for 67" opens a short intent on the client, so what the
    /// bot picks up next is held for that target (auto-eat skips it unless hp is critical) until delivered or timed out.
    /// </summary>
    private static void AttachHandoffIntents(IGameClient client, AgentRuntime rt, Func<IEnumerable<string>>? hiveBots, ILogger? log)
    {
        if (client is not MinecraftGameClient mg) return;
        IEnumerable<string> Names() => (hiveBots?.Invoke() ?? []).Concat(client.World.Entities.OnlinePlayers)
            .Where(n => !n.Equals(rt.Chat.Username, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Self() => [rt.Chat.Username, rt.Chat.DisplayName, .. rt.Chat.Aliases];
        void On(string? text, string who)
        {
            try
            {
                if (text is null || Perception.HandoffIntent.Detect(text, Names(), Self()) is not { } hit) return;
                mg.Inner.Handoffs.Open(hit.Target, hit.ItemId);
                log?.LogInformation("hand-off intent ({Who}): pickups{Item} are for {Target}", who,
                    hit.ItemId is { } id ? " of " + MineClaw.Data.Items.DisplayName(id, 0) : "", hit.Target);
            }
            catch (Exception ex) { log?.LogWarning(ex, "hand-off intent failed"); }
        }
        rt.Chat.Accepted += m => On(m.Text, m.Player ?? "?");
        rt.Chat.Said += t => On(t, "me");
    }

    private static (MineClaw.World.BlockPos Min, MineClaw.World.BlockPos Max)? NamedStructure(AgentRuntime rt, IGameClient client, string name)
    {
        int dim = client.World.Dimension;
        var n = name.Trim();
        var s = rt.Memory.Read(d => d.Structures.Where(x => x.Min != null && x.Dimension == dim)
            .OrderBy(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(n, StringComparison.OrdinalIgnoreCase)
                                 || n.Contains(x.Name, StringComparison.OrdinalIgnoreCase)));
        if (s?.Min is not { } a) return null;
        var b = s.Max ?? a;
        return (new MineClaw.World.BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
            new MineClaw.World.BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    /// <summary>
    /// This account is op (Bot:Protection:ServerOp, main's account): spawn protection doesn't bind IT, so it is exempt from the
    /// remembered fact. The fact itself stays in the shared memory for every other hive account (clones and bodies aren't op);
    /// a fact that never named the accounts it binds is dropped, as before (nothing says anyone else is bound).
    /// </summary>
    public static void ForgetSpawnProtection(AgentRuntime rt, ILogger? log, string? account = null)
    {
        try
        {
            int dropped = 0, exempt = 0;
            foreach (int dim in new[] { 0, -1 })
            {
                if (SpawnProtectionFact.Read(rt.Observations, dim) is not { } fact) continue;
                if (account != null && fact.Affects.Any(a => !a.Equals(account, StringComparison.OrdinalIgnoreCase)))
                {
                    fact.WithExempt(account).Write(rt.Observations, MineClaw.World.ChunkColumn.MaxHeight);
                    exempt++;
                    continue;
                }
                SpawnProtectionFact.Retract(rt.Observations, dim);
                dropped++;
            }
            int notes = dropped > 0 ? RetractSpawnProtectionNotes(rt) : 0;
            if (dropped + notes > 0) log?.LogInformation("op: forgot the remembered spawn protection ({Rows} region(s), {Notes} note(s))", dropped, notes);
            if (exempt > 0) log?.LogInformation("op: {Account} is exempt from the remembered spawn protection; it still binds the other hive accounts", account);
        }
        catch (Exception ex) { log?.LogWarning(ex, "forgetting spawn protection failed"); }
    }

    /// <summary>Stall watchdog: a job/script/order that claims to work while the body hasn't changed for 45 s wakes the brain.</summary>
    public Brainstem.StallWatchdog? Watchdog { get; init; }

    /// <summary>The recorded pair whose end in <paramref name="dim"/> is where the bot came out (within 4 blocks), as text.</summary>
    public static string? PortalPairText(MineClaw.Agent.Rag.PortalLinks links, int dim, MineClaw.World.Vec3d pos)
    {
        foreach (var l in links.All())
            if (l.From(dim) is { } p && p.From.Distance(pos.X, pos.Y, pos.Z) <= 4)
                return $"portal pair recorded: {MineClaw.Agent.Rag.WorldDim.Name(p.To.Dim)} {p.To.Pos} <-> {MineClaw.Agent.Rag.WorldDim.Name(p.From.Dim)} {p.From.Pos}";
        return null;
    }

    /// <summary>
    /// Self-reliant prerequisite planning (ensure, loadouts, skill preconditions, mine/harvest): what the acquisition planner
    /// may draw on. Asking players only while a human is online and this bot may speak; the hive fills
    /// <see cref="AcquisitionSources.RequestHandoff"/> on <see cref="Acquisition"/>.
    /// </summary>
    /// <summary>Purges taught dig cells the loaded world shows are lava/water/other blocks (logged), a few times after startup.</summary>
    private static async Task PurgeNonCellsAsync(IGameClient client, Perception.DigCells cells, Perception.DigCellCorrections corrections,
        TimeProvider time, ILogger? log)
    {
        for (int i = 0; i < 15; i++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), time).ConfigureAwait(false);
                if (!client.IsPlaying) continue;
                var w = client.World;
                foreach (var (cell, why) in cells.PurgeNonCells(p => w.IsLoaded(p.X, p.Z) ? (w.GetBlock(p), w.GetMeta(p)) : null,
                             (p, why) => corrections.RetractBad(p, why)))
                    log?.LogInformation("dig cell {Cell} purged: {Why}", cell, why);
            }
            catch (Exception ex) { log?.LogDebug("dig cell world purge skipped: {Err}", ex.Message); }
        }
    }

    private static AcquisitionSources AttachAcquisition(IGameClient client, AgentRuntime rt,
        Func<MineClaw.World.BlockPos, IReadOnlyList<MineClaw.World.BlockPos>> genCells, Perception.DigCells digCells, WorldIndexer? indexer, ILogger? log) =>
        GameTools.Acquisition.Attach(client, new AcquisitionSources
        {
            Observations = rt.Observations,
            CellsNear = genCells,
            KnownCell = digCells.IsDigCell,
            ConfirmedCell = p => indexer?.IsConfirmedDigCell(p) == true,
            GeneratorCells = () =>
            {
                var me = client.World.Player.Position.ToBlockPos();
                return // Geometry (lava + water beside it) is a generator only on skyblock; a normal world knows its gens by regen/teaching.
                    (MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone ? Perception.BlockDescriber.GenCellsNear(client.World, me, 16) : [])
                    .Concat(indexer?.GeneratorCellsNear(me, 32) ?? [])
                    .Concat(digCells.Taught().Where(t => t.DistanceSquaredTo(me) <= 48 * 48))
                    .Distinct().ToList();
            },
            MinTrees = client is MinecraftGameClient mg ? mg.Inner.MinTrees : MineClaw.Client.Trees.TreeStewardship.DefaultMinTrees,
            Announce = plan => log?.LogInformation("acquire: {Plan}", plan),
            AskPlayer = async (question, timeout, ct) =>
            {
                if (rt.Agent.HumansOnline?.Invoke() != true) return null;
                if (rt.Agent.SpeechGate is { } gate && gate(new Agent.Events.EventBatch([]), question, false) == null) return null; // a muted clone
                // Another hive bot asked the players this a few minutes ago: no second ask (its answer is in chat already).
                if (Agent.Human.AskedBefore.For(rt.Db, rt.Chat, TimeProvider.System)(question, null) is { } prior)
                {
                    log?.LogInformation("acquire: not asking again: {Prior}", prior);
                    return null;
                }
                var wait = rt.Chat.WaitForReplyAsync(null, timeout, ct);
                await rt.Chat.SayAsync(question, ct: ct).ConfigureAwait(false);
                return await wait.ConfigureAwait(false);
            },
        });

    /// <summary>
    /// Spawn protection learned by the client (logs that won't break near spawn) → one <c>protected_region</c> observation
    /// per dimension, so memory/where_is/the worker know about it; cleared when the client clears it.
    /// </summary>
    /// <remarks>Public for in-process bodies (each body's client feeds the same shared fact under its own account).</remarks>
    public static void WireProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log,
        IClaimBoard? claims = null, Func<IEnumerable<string>>? hiveBots = null)
    {
        var account = c.Options.Username;
        // An undone edit on a block another hive bot holds a lease on, or right next to another hive bot, is a race with it
        // (two bots on one tree crown), never evidence of spawn protection.
        if (claims != null || hiveBots != null)
            c.EditContested = p => IsContested(c, p, claims, hiveBots);
        // An edit that worked inside the remembered region: evidence for THIS account only. An account the fact binds is free of
        // it now (the fact goes once it binds nobody: then the brain's notes about it go too); any other account is exempt (op).
        c.EditSucceededNearSpawn += (p, dim) =>
        {
            try { NoteEditWorked(rt, account, p, dim, c.World.WorldHeight, log); }
            catch (Exception ex) { log?.LogWarning(ex, "protected_region update failed"); }
        };
        c.ProtectedRegionChanged += r =>
        {
            try
            {
                if (r == null) return; // a success inside the client's region: EditSucceededNearSpawn above already updated the fact
                var fact = SpawnProtectionFact.Learned(SpawnProtectionFact.Read(rt.Observations, r.Dimension), r, account);
                fact.Write(rt.Observations, c.World.WorldHeight);
                log?.LogInformation("spawn protection fact: {Text}", fact.Text());
            }
            catch (Exception ex) { log?.LogWarning(ex, "protected_region observation failed"); }
        };
    }

    /// <summary><paramref name="account"/>'s edit at <paramref name="p"/> worked: the shared fact learns from it (see <see cref="SpawnProtectionFact.AfterSuccess"/>).</summary>
    internal static void NoteEditWorked(AgentRuntime rt, string account, MineClaw.World.BlockPos p, int dim, int worldHeight, ILogger? log)
    {
        if (SpawnProtectionFact.Read(rt.Observations, dim) is not { } fact) return;
        var (next, changed) = fact.AfterSuccess(account, p, dim);
        if (!changed) return;
        if (next == null)
        {
            SpawnProtectionFact.Retract(rt.Observations, dim);
            int n = RetractSpawnProtectionNotes(rt);
            log?.LogInformation("edit by {Account} at {Pos} worked inside the remembered spawn protection, which bound only it: retracted ({Notes} note(s))",
                account, p, n);
            return;
        }
        next.Write(rt.Observations, worldHeight);
        log?.LogInformation("edit by {Account} at {Pos} worked inside the spawn protection: {Account} is exempt (op); it still binds {Others}",
            account, p, account, string.Join(", ", next.Affects));
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
        var store = new SqliteWorldMemoryStore(rt.Db, client.Username); // seen_by: whose eyes (main or a clone) last saw a column
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
    public static void SyncProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log)
    {
        try
        {
            var account = c.Options.Username;
            var fact = SpawnProtectionFact.Read(rt.Observations, c.World.Dimension);
            if (c.ProtectedRegion is { } mine)
            {
                // Grace: my own fresh region may not be written yet (the event dispatcher upserts it).
                if (fact == null && DateTime.UtcNow - mine.LearnedAt > TimeSpan.FromSeconds(30) && c.ForgetProtectedRegion("retracted in the shared memory"))
                    log?.LogInformation("spawn protection retracted elsewhere in the hive: forgot {Region}", mine);
                else if (fact != null && !fact.AppliesTo(account) && c.ForgetProtectedRegion("this account is exempt (op)"))
                    log?.LogInformation("spawn protection doesn't bind {Account} (exempt): forgot {Region}", account, mine);
                return;
            }
            if (fact == null || !fact.AppliesTo(account)) return;
            var row = rt.Observations.Get(SpawnProtectionFact.Kind, SpawnProtectionFact.Key, dimension: c.World.Dimension);
            var center = fact.Center with { Y = fact.Center.Y != 0 ? fact.Center.Y : c.World.Spawn.Y };
            var r = (fact with { Center = center }).ToRegion(2, row?.LastSeen.UtcDateTime ?? DateTime.UtcNow);
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
        tracker.Danger.IsOtherBot = rt.Chat.IsOtherBot;
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

    /// <summary>A toss within this many blocks of the bot counts as a hand-over to it.</summary>
    public const double HandoverReach = 10;

    /// <summary>Hand-overs and spoken promises (zero tokens): "Fox gave you 21 wheat; you said you'd make bread when ...".</summary>
    public Brainstem.HandoverWatch? Handovers { get; init; }
    private System.Threading.Timer? HandoverPump { get; init; }

    /// <summary>Posts settled hand-overs / kept promises as wake events (the timer does this live; tests call it by hand).</summary>
    public int PollHandovers() => Handovers is { } h ? BodyFactory.PostHandovers(h, Runtime, Client) : 0;

    public void Dispose()
    {
        HandoverPump?.Dispose();
        ProtectionSync?.Dispose();
        PlacedBlocks?.Dispose();
        Watchdog?.Dispose();
        Bridge.Dispose();
        Brainstem?.Dispose();
        Gestures?.Dispose();
        Activity?.Dispose();
        Mechanisms?.Dispose();
        WorldMemory?.Dispose(); // final flush: the next join compares against exactly this
        Indexer?.Dispose();
    }
}
