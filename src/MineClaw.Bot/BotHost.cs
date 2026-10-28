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
        AttachHandoffIntents(client, rt, hiveBots, log);
        digCells.TaughtCell += (p, by) => corrections.OnTaught(p, by);
        // Stray "cells" off the gen's dig layer (the block over a cell, taught by a punch while a player mined) go, logged.
        try
        {
            foreach (var (cell, why) in digCells.PurgeOffLayer((p, why) => corrections.RetractBad(p, why)))
                log?.LogInformation("dig cell {Cell} purged: {Why}", cell, why);
        }
        catch (Exception ex) { log?.LogDebug("dig cell purge skipped: {Err}", ex.Message); }
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
        if (botOptions.Protection.ServerOp) ForgetSpawnProtection(rt, log);
        client.ReservedItems = protection.ReservedItems();

        // Hive: container/block actions of the game tools take a lease first (ClaimAwareGameClient).
        IGameClient toolClient = claims == null ? client : new ClaimAwareGameClient(client, claims);
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
        var acquisition = AttachAcquisition(client, rt, genCells, digCells, indexer, log);
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
        IntentionalReset.Attach(rt, client, log); // into_void: announce once, intentional-reset outcome, the void-reset claim's evidence
        // Shared chest memory vs what an open shows: "bread x5 gone since maid_chan opened it 1m ago; Fox may have taken it".
        if (indexer != null)
            WorldIndex.ChestChanges.Attach(rt, client, indexer,
                n => n.Equals(client.Username, StringComparison.OrdinalIgnoreCase) || hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true
                     || rt.Chat.IsHiveBot?.Invoke(n) == true, time, log);
        // Brain gets the cheap single-shot tools + delegate/ask_memory; the worker gets the game macro tools.
        rt.ApplyBrainSplit();

        var bridge = new GameBridge(client, rt, log, time)
        {
            Hunger = new HungerLatch(persist ? Path.Combine(agentOptions.DataDirectory, $"hunger-latch-{client.Username}.json") : null, time),
            PortalPairText = (dim, pos) => PortalPairText(rt.Observations.Portals, dim, pos),
        };
        bridge.Attach();
        var scripts = ScriptTools.Register(rt, client, skillHub, botOptions.Scripts, time, log); // script tool (brain+worker), after split + header
        // Vision v2: see = contact sheet + role layer; with a perception model (Agent:Llm:Perception) the brain gets verified text.
        var vision = Perception.Vision.VisionWiring.Attach(rt, client, memory, botOptions, protection,
            p => digCells.IsDigCell(p) || indexer?.IsConfirmedDigCell(p) == true, indexer, gestures, genCells, hiveBots, time, log, persist);
        // do {action, args, then?, queue?}: the brain's one compact door to the single-step macros (after every macro exists).
        Agent.Tools.DoTool.Register(rt.Tools);

        // Brainstem: deterministic controller under the brain (head turn, reflexes, speculative first step, social idle,
        // the [here] pre-observation). Attached last so its event gate sees what the script gate kept.
        var here = new Brainstem.HereBlock(client, rt.Memory, rt.Observations, landmark);
        rt.Agent.HereBlock = here.Build;
        Func<string, bool> isBot = n => hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true || rt.Chat.IsHiveBot?.Invoke(n) == true;
        var brainstem = new Brainstem.BrainstemController(rt, client, memory, isBot, time, loggers?.CreateLogger("MineClaw.Brainstem"));
        brainstem.Attach(gestures, timer: persist); // in-memory (test) hosts drive SocialTick by hand
        // Body language from static text matching (nod/shake/wave/bow/bob/hop/look-at; zero tokens, social priority).
        var bodyGestures = new Brainstem.Gestures(rt, client, agentOptions.Gestures, isBot, time, loggers?.CreateLogger("MineClaw.Gestures"));
        bodyGestures.Attach();
        var watchdog = AttachWatchdog(rt, client, scripts, time);
        // A standing-order pass that found nothing to do backs off in C#; the world model says when there's work again.
        if (rt.Agent.Orders is { } standing) standing.WorkReady = o => MineClaw.Bot.Brainstem.WorkProbe.Ready(o, client, genCells);
        if (persist) watchdog.Start(); // in-memory (test) hosts drive Tick by hand
        var (handovers, handoverPump) = AttachHandovers(client, rt, isBot, time, log, timer: persist);
        return new BotHost
        {
            Watchdog = watchdog,
            Handovers = handovers, HandoverPump = handoverPump,
            Runtime = rt, Client = client, Bridge = bridge, Protection = protection, Recorder = recorder, MemoryAdapter = memory,
            Indexer = indexer, Activity = activityTracker, WorldMemory = worldMemory, Claims = claims ?? NullClaimBoard.Instance,
            Gestures = gestures, DigCells = digCells, Mechanisms = mechanisms, Brainstem = brainstem, BodyGestures = bodyGestures, ProtectionSync = protectionSync,
            Acquisition = acquisition,
            PlacedBlocks = placedStore, ProtectionMarks = marks, ProtectionCommands = protectCmds, ProtectionTelemetry = telemetry,
            Vision = vision,
        };
    }

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
    /// The bot is op (Bot:Protection:ServerOp): spawn protection doesn't apply, so a remembered spawn-protection region (and the
    /// notes about it) from earlier sessions is dropped. Only real server reverts this session re-learn it.
    /// </summary>
    public static void ForgetSpawnProtection(AgentRuntime rt, ILogger? log)
    {
        try
        {
            int n = 0;
            foreach (int dim in new[] { 0, -1 })
                if (rt.Observations.Get("protected_region", "spawn", dimension: dim) != null)
                {
                    rt.Observations.Remove("protected_region", "spawn", tombstone: false, dimension: dim);
                    n++;
                }
            int notes = RetractSpawnProtectionNotes(rt);
            if (n + notes > 0) log?.LogInformation("op: forgot the remembered spawn protection ({Rows} region(s), {Notes} note(s))", n, notes);
        }
        catch (Exception ex) { log?.LogWarning(ex, "forgetting spawn protection failed"); }
    }

    /// <summary>Stall watchdog: a job/script/order that claims to work while the body hasn't changed for 45 s wakes the brain.</summary>
    public Brainstem.StallWatchdog? Watchdog { get; init; }

    private static readonly string[] PassiveTools = ["follow", "guard", "watch_demo", "ask_player", "ask_stand", "wait", "sit", "look", "see"];

    /// <summary>The recorded pair whose end in <paramref name="dim"/> is where the bot came out (within 4 blocks), as text.</summary>
    public static string? PortalPairText(MineClaw.Agent.Rag.PortalLinks links, int dim, MineClaw.World.Vec3d pos)
    {
        foreach (var l in links.All())
            if (l.From(dim) is { } p && p.From.Distance(pos.X, pos.Y, pos.Z) <= 4)
                return $"portal pair recorded: {MineClaw.Agent.Rag.WorldDim.Name(p.To.Dim)} {p.To.Pos} <-> {MineClaw.Agent.Rag.WorldDim.Name(p.From.Dim)} {p.From.Pos}";
        return null;
    }

    private static Brainstem.StallWatchdog AttachWatchdog(AgentRuntime rt, IGameClient client, ScriptBridge scripts, TimeProvider? time)
    {
        string? last = null;
        rt.Activities.StepFinished += (tool, _, r, _) => last = $"{tool}: {r.Text}";
        Brainstem.ClaimedWork? Work()
        {
            if (!client.IsPlaying) return null;
            if (scripts.Controller.Current is { } s && s.Status == MineClaw.Scripting.ScriptStatus.Running)
            {
                var state = s.State();
                bool waiting = state.Contains("wait", StringComparison.OrdinalIgnoreCase) || state.Contains("sleep", StringComparison.OrdinalIgnoreCase);
                return new($"script {s.Name}", waiting);
            }
            if (rt.Activities.Current is { } a && a.Kind is Agent.Tools.ActivityKind.Job or Agent.Tools.ActivityKind.Move)
            {
                bool passive = a.Steps.Count > 0 && a.Steps.All(st => PassiveTools.Contains(st.Tool, StringComparer.OrdinalIgnoreCase));
                return new($"{a.Label} #{a.Id}", passive);
            }
            return null;
        }
        string Body()
        {
            var p = client.World.Player.Position;
            var inv = client.World.Inventory.PlayerSlots();
            int h = 17;
            foreach (var st in inv) h = h * 31 + (st.IsEmpty ? 0 : st.Id * 1000 + st.Count);
            return $"{Math.Round(p.X * 2)},{Math.Round(p.Y * 2)},{Math.Round(p.Z * 2)}|{h}|{client.CurrentAction}";
        }
        string? LastResult() => rt.Worker?.IsRunning == true && rt.Worker.Trail is { } trail ? trail : last;
        return new Brainstem.StallWatchdog(Work, Body, LastResult, e => rt.Events.Post(e), time);
    }

    /// <summary>
    /// Self-reliant prerequisite planning (ensure, loadouts, skill preconditions, mine/harvest): what the acquisition planner
    /// may draw on. Asking players only while a human is online and this bot may speak; the hive fills
    /// <see cref="AcquisitionSources.RequestHandoff"/> on <see cref="Acquisition"/>.
    /// </summary>
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
                return Perception.BlockDescriber.GenCellsNear(client.World, me, 16)
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

    /// <summary>A toss within this many blocks of the bot counts as a hand-over to it.</summary>
    public const double HandoverReach = 10;

    /// <summary>Hand-overs and spoken promises (zero tokens): "Fox gave you 21 wheat; you said you'd make bread when ...".</summary>
    public Brainstem.HandoverWatch? Handovers { get; init; }
    private System.Threading.Timer? HandoverPump { get; init; }

    /// <summary>Posts settled hand-overs / kept promises as wake events (the timer does this live; tests call it by hand).</summary>
    public int PollHandovers() => Handovers is { } h ? PostHandovers(h, Runtime, Client) : 0;

    private static int PostHandovers(Brainstem.HandoverWatch h, AgentRuntime rt, IGameClient client)
    {
        int n = 0;
        foreach (var (text, player) in h.Poll(id => client.World.Inventory.CountItem(id)))
        {
            // A kept promise wakes the brain even while a script has the body (it said it would act on this).
            bool promise = text.Contains("you said you'd", StringComparison.Ordinal);
            rt.Events.Post(new Agent.Events.GameEvent(Agent.Events.EventKind.Notice, text, player, Agent.Events.EventPriority.Normal) { MustWake = promise });
            n++;
        }
        return n;
    }

    /// <summary>
    /// Live 10-27 21:04: Fox tossed 21 wheat after "come here I'll give it to you, make bread"; the bot had said "I'll make bread
    /// the second you hand it over" and then idled 40 s. Item spawns at a player's eye height / in front of them are attributed,
    /// the bot's pickups of them become one debounced event, and the bot's own "I'll X when Y" lines become pending promises.
    /// </summary>
    private static (Brainstem.HandoverWatch?, System.Threading.Timer?) AttachHandovers(IGameClient client, AgentRuntime rt, Func<string, bool> isBot,
        TimeProvider? time, ILogger? log, bool timer)
    {
        var watch = new Brainstem.HandoverWatch(time);
        var w = client.World;
        string self = client.Username;
        IEnumerable<Brainstem.HandoverWatch.PlayerView> Others() => w.Entities.All()
            .Where(e => e.Kind == MineClaw.World.EntityKind.Player && e.Name is { Length: > 0 } n && !n.Equals(self, StringComparison.OrdinalIgnoreCase) && !isBot(n))
            .Select(e => new Brainstem.HandoverWatch.PlayerView(e.Name!, e.Position, e.Yaw, e.Pitch));
        w.Entities.Spawned += e =>
        {
            try
            {
                if (e.Kind != MineClaw.World.EntityKind.Item || e.Item.IsEmpty) return;
                // A toss near the bot is a hand-over (counted at once, debounced); one far away is someone else's business.
                bool near = e.Position.DistanceTo(w.Player.Position) <= HandoverReach;
                if (watch.OnItemSpawn(e.Id, e.Position, Others(), near ? e.Item.Id : 0, near ? e.Item.Count : 0) is { } who)
                    log?.LogDebug("hand-over: {Item} x{Count} thrown by {Who}{Far}", e.Item.Id, e.Item.Count, who, near ? "" : " (far: counted only on pickup)");
            }
            catch (Exception ex) { log?.LogDebug(ex, "hand-over spawn failed"); }
        };
        w.BlockChanged += c => { if (c.NewId == 0 && c.OldId != 0) watch.OnBlockGone(c.Pos); };
        if (client is MinecraftGameClient mg)
            mg.Inner.ItemCollected += (collector, entity, stack) =>
            {
                if (collector != w.Player.EntityId || stack is not { IsEmpty: false } s) return;
                watch.OnPickup(entity, s.Id, s.Count);
            };
        rt.Chat.Accepted += m => { if (m.Player is { Length: > 0 } p && !isBot(p)) watch.OnPlayerLine(p, m.Text); };
        rt.Chat.Said += t =>
        {
            try
            {
                if (watch.OnBotLine(t, id => w.Inventory.CountItem(id)) is { } p)
                    log?.LogInformation("promise: \"{Intent}\" when {Cond}{Item}", p.Intent, p.Condition,
                        p.ItemId is { } id ? $" (watching for {MineClaw.Data.Items.DisplayName(id, 0)}{(p.From is { } f ? " from " + f : "")})" : "");
            }
            catch (Exception ex) { log?.LogDebug(ex, "promise parse failed"); }
        };
        var pump = timer
            ? new System.Threading.Timer(_ => { try { PostHandovers(watch, rt, client); } catch (Exception) { /* next tick */ } }, null, 500, 250)
            : null;
        return (watch, pump);
    }

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
